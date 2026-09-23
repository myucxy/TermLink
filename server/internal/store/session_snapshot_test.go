package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
	"os"
	"testing"
	"time"
)

func TestSessionSnapshotValidation(t *testing.T) {
	valid := SessionSnapshotEntry{ID: uuid.New(), ShellType: "pwsh", State: "RUNNING", Columns: 80, Rows: 24}
	for _, items := range [][]SessionSnapshotEntry{{}, {valid}} {
		if err := ValidateSessionSnapshot(items); err != nil {
			t.Fatal(err)
		}
	}
	for _, items := range [][]SessionSnapshotEntry{nil, {valid, valid}, {{ID: uuid.New()}}, {{ID: uuid.New(), ShellType: "pwsh", State: "RUNNING", Columns: 80, Rows: 24, LastOutputSequence: ^uint64(0)}}} {
		if ValidateSessionSnapshot(items) == nil {
			t.Fatal("invalid snapshot accepted")
		}
	}
}

func TestSessionSnapshotReconcilesStaleRowsAndEnforcesDeviceAuthority(t *testing.T) {
	url := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if url == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, url); err != nil {
		t.Fatal(err)
	}
	s, err := Open(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	user, device, android, pc := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	digest := sha256.Sum256([]byte(user.String()))
	defer s.Pool.Exec(context.Background(), `DELETE FROM users WHERE id=$1`, user)
	if _, err = s.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, user, digest[:]); err != nil {
		t.Fatal(err)
	}
	for id, typ := range map[uuid.UUID]string{device: "WINDOWS_PC", android: "ANDROID"} {
		if _, err = s.Pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$2,$3,'catalog-test',$5,$5,$4,'TRUSTED')`, id, user, typ, id.String(), append(id[:], id[:]...)); err != nil {
			t.Fatal(err)
		}
	}
	if _, err = s.Pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,session_heartbeat_capable,session_heartbeat_enabled_at) VALUES($1,$2,$3,'catalog-test','test',true,now())`, pc, user, device); err != nil {
		t.Fatal(err)
	}
	s.AutoTrustDevices = true
	if got, err := s.SessionCatalogDevice(ctx, user, android, pc); err != nil || got != device {
		t.Fatalf("authority: %v %v", got, err)
	}
	if _, err = s.SessionCatalogDevice(ctx, uuid.New(), android, pc); !errors.Is(err, ErrDenied) {
		t.Fatal("wrong account accepted")
	}
	s.AutoTrustDevices = false
	if _, err = s.SessionCatalogDevice(ctx, user, android, pc); !errors.Is(err, ErrDenied) {
		t.Fatal("missing grant accepted")
	}
	for _, permission := range []string{"VIEW", "CONTROL", "MANAGE"} {
		if _, err = s.Pool.Exec(ctx, `INSERT INTO device_grants(pc_id,device_id,permission,approved_by) VALUES($1,$2,$3,$4)
		ON CONFLICT(pc_id,device_id) DO UPDATE SET permission=excluded.permission`, pc, android, permission, device); err != nil {
			t.Fatal(err)
		}
		_, err = s.SessionCleanupDevice(ctx, user, android, pc)
		if permission == "MANAGE" && err != nil || permission != "MANAGE" && !errors.Is(err, ErrDenied) {
			t.Fatalf("cleanup permission %s: %v", permission, err)
		}
	}
	s.AutoTrustDevices = true
	first, stale := uuid.New(), uuid.New()
	entry := func(id uuid.UUID, state string) SessionSnapshotEntry {
		return SessionSnapshotEntry{ID: id, ShellType: "pwsh", State: state, Columns: 80, Rows: 24, LastOutputSequence: 9}
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, device, pc, []SessionSnapshotEntry{entry(first, "RUNNING"), entry(stale, "RUNNING")}); err != nil {
		t.Fatal(err)
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, android, pc, []SessionSnapshotEntry{}); !errors.Is(err, ErrDenied) {
		t.Fatal("Android replaced snapshot")
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, device, pc, []SessionSnapshotEntry{entry(first, "EXITED")}); err != nil {
		t.Fatal(err)
	}
	for id, want := range map[uuid.UUID]string{first: "EXITED", stale: "INTERRUPTED"} {
		var got string
		if err = s.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, id).Scan(&got); err != nil || got != want {
			t.Fatalf("state=%s err=%v", got, err)
		}
	}
	if _, err = s.Pool.Exec(ctx, `UPDATE sessions SET state='DELETED',deleted_at=now(),deletion_reason='USER_REQUESTED' WHERE id=$1`, stale); err != nil {
		t.Fatal(err)
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, device, pc, []SessionSnapshotEntry{entry(first, "RUNNING"), entry(stale, "RUNNING")}); err != nil {
		t.Fatal(err)
	}
	for id, want := range map[uuid.UUID]string{first: "EXITED", stale: "DELETED"} {
		var got string
		if err = s.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, id).Scan(&got); err != nil || got != want {
			t.Fatalf("terminal state changed: %s %v", got, err)
		}
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, device, pc, []SessionSnapshotEntry{}); err != nil {
		t.Fatal(err)
	}
	missing := uuid.New()
	if _, err = s.Pool.Exec(ctx, `INSERT INTO control_leases(session_id,holder_device_id,lease_id,expires_at,version) VALUES($1,$2,$3,now()+interval '1 minute',1)`, first, android, uuid.New()); err != nil {
		t.Fatal(err)
	}
	for _, wrongDevice := range []uuid.UUID{android, uuid.New()} {
		if err = s.ApplyHostSessionDeletions(ctx, user, wrongDevice, pc, []uuid.UUID{first}, "cleanup-test"); !errors.Is(err, ErrDenied) {
			t.Fatal("non-owning PC deleted mirror", err)
		}
	}
	for i := 0; i < 2; i++ {
		if err = s.ApplyHostSessionDeletions(ctx, user, device, pc, []uuid.UUID{first, missing}, "cleanup-test"); err != nil {
			t.Fatal(err)
		}
	}
	if err = s.ReplaceSessionSnapshot(ctx, user, device, pc, []SessionSnapshotEntry{entry(first, "RUNNING"), entry(missing, "RUNNING")}); err != nil {
		t.Fatal(err)
	}
	for _, id := range []uuid.UUID{first, missing} {
		var state string
		if err = s.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, id).Scan(&state); err != nil || state != "DELETED" {
			t.Fatalf("deletion not durable: %s %v", state, err)
		}
	}
	otherPC, foreign, untouched := uuid.New(), uuid.New(), uuid.New()
	otherDevice := uuid.New()
	if _, err = s.Pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by)
	VALUES($1,$2,'WINDOWS_PC','other',$3,$3,$4,'TRUSTED',$5)`, otherDevice, user, append(otherDevice[:], otherDevice[:]...), otherDevice.String(), device); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version) VALUES($1,$2,$3,'other','test')`, otherPC, user, otherDevice); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Pool.Exec(ctx, `INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows) VALUES($1,$2,'pwsh','EXITED',80,24)`, foreign, otherPC); err != nil {
		t.Fatal(err)
	}
	if err = s.ApplyHostSessionDeletions(ctx, user, device, pc, []uuid.UUID{untouched, foreign}, "cleanup-test"); !errors.Is(err, ErrDenied) {
		t.Fatal("foreign session accepted", err)
	}
	var exists bool
	if err = s.Pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM sessions WHERE id=$1)`, untouched).Scan(&exists); err != nil || exists {
		t.Fatal("denied batch partially committed", err)
	}
	if err = s.Pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM control_leases WHERE session_id=$1)`, first).Scan(&exists); err != nil || exists {
		t.Fatal("deleted session lease retained", err)
	}
	if _, err = s.Pool.Exec(ctx, `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, device); err != nil {
		t.Fatal(err)
	}
	if _, err = s.SessionCatalogDevice(ctx, user, android, pc); !errors.Is(err, ErrDenied) {
		t.Fatal("revoked PC accepted")
	}
	if err = s.ApplyHostSessionDeletions(ctx, user, device, pc, []uuid.UUID{first}, "cleanup-test"); !errors.Is(err, ErrDenied) {
		t.Fatal("revoked PC deleted mirror", err)
	}
}

func TestSessionCleanupValidation(t *testing.T) {
	id := uuid.New()
	if err := ValidateSessionCleanup([]uuid.UUID{id}); err != nil {
		t.Fatal(err)
	}
	for _, ids := range [][]uuid.UUID{nil, {}, {uuid.Nil}, {id, id}, make([]uuid.UUID, 65)} {
		if ValidateSessionCleanup(ids) == nil {
			t.Fatal("invalid cleanup batch accepted")
		}
	}
}

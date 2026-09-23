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
	if _, err = s.Pool.Exec(ctx, `UPDATE devices SET trust_state='REVOKED',revoked_at=now() WHERE id=$1`, device); err != nil {
		t.Fatal(err)
	}
	if _, err = s.SessionCatalogDevice(ctx, user, android, pc); !errors.Is(err, ErrDenied) {
		t.Fatal("revoked PC accepted")
	}
}

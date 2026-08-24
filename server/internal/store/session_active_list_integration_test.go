package store

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestListSessionsUsesSessionHeartbeatForCapablePCAndPCHeartbeatForLegacy(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()

	userID := uuid.New()
	legacyDeviceID, capableDeviceID := uuid.New(), uuid.New()
	legacyPCID, capablePCID := uuid.New(), uuid.New()
	legacyRunningID := uuid.New()
	capableFreshID, capableStaleID := uuid.New(), uuid.New()
	capableInterruptedID, capableExitedID := uuid.New(), uuid.New()
	subjectHash := sha256.Sum256([]byte("session-online-list-" + userID.String()))
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, userID)
	})

	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,
			key_id,trust_state,approved_by,last_seen_at)
		VALUES
		  ($1,$2,'WINDOWS_PC','Legacy PC',decode(repeat('01',32),'hex'),
		   decode(repeat('02',32),'hex'),$3,'TRUSTED',NULL,now()),
		  ($4,$2,'WINDOWS_PC','Heartbeat-capable PC',decode(repeat('03',32),'hex'),
		   decode(repeat('04',32),'hex'),$5,'TRUSTED',$1,now())`,
		legacyDeviceID, userID, "legacy-list-"+legacyDeviceID.String(),
		capableDeviceID, "capable-list-"+capableDeviceID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(
			id,owner_user_id,device_id,name,agent_version,last_seen_at,
			session_heartbeat_capable,session_heartbeat_enabled_at)
		VALUES
		  ($1,$2,$3,'Legacy PC','integration-test',now(),false,NULL),
		  ($4,$2,$5,'Heartbeat-capable PC','integration-test',now(),true,now()-interval '1 hour')`,
		legacyPCID, userID, legacyDeviceID, capablePCID, capableDeviceID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(
			id,pc_id,shell_type,state,columns,rows,last_seen_at,ended_at)
		VALUES
		  ($1,$2,'powershell','RUNNING',120,40,now()-interval '1 day',NULL),
		  ($3,$4,'powershell','RUNNING',120,40,now(),NULL),
		  ($5,$4,'powershell','RUNNING',120,40,now()-interval '46 seconds',NULL),
		  ($6,$4,'powershell','INTERRUPTED',120,40,now(),NULL),
		  ($7,$4,'powershell','EXITED',120,40,now()-interval '1 minute',now()-interval '1 minute')`,
		legacyRunningID, legacyPCID, capableFreshID, capablePCID, capableStaleID,
		capableInterruptedID, capableExitedID); err != nil {
		t.Fatal(err)
	}

	active, err := repository.ListSessions(ctx, userID, capableDeviceID, capablePCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(active) != 1 || active[0].ID != capableFreshID || !active[0].Online {
		t.Fatalf("capable active sessions=%+v, want only online fresh session %s", active, capableFreshID)
	}

	all, err := repository.ListSessionsIncludingEnded(ctx, userID, capableDeviceID, capablePCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(all) != 4 {
		t.Fatalf("capable compatibility sessions=%+v, want four", all)
	}
	onlineByID := make(map[uuid.UUID]bool, len(all))
	for _, session := range all {
		onlineByID[session.ID] = session.Online
	}
	if !onlineByID[capableFreshID] {
		t.Fatalf("fresh capable session reported offline: %+v", all)
	}
	for _, id := range []uuid.UUID{capableStaleID, capableInterruptedID, capableExitedID} {
		if onlineByID[id] {
			t.Fatalf("non-online capable session %s reported online: %+v", id, all)
		}
	}

	// A current PC heartbeat cannot make a stale sibling session active once
	// the Agent opted in to authoritative per-session heartbeats.
	if _, err := repository.Pool.Exec(ctx, `UPDATE pcs SET last_seen_at=now() WHERE id=$1`, capablePCID); err != nil {
		t.Fatal(err)
	}
	active, err = repository.ListSessions(ctx, userID, capableDeviceID, capablePCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(active) != 1 || active[0].ID != capableFreshID || !active[0].Online {
		t.Fatalf("fresh PC presence revived stale sibling: %+v", active)
	}

	// A legacy Agent has no session heartbeat signal, so its RUNNING row falls
	// back to PC presence even when the stored session timestamp is old.
	legacyActive, err := repository.ListSessions(ctx, userID, legacyDeviceID, legacyPCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(legacyActive) != 1 || legacyActive[0].ID != legacyRunningID || !legacyActive[0].Online {
		t.Fatalf("legacy active sessions=%+v, want PC-presence fallback", legacyActive)
	}
	if _, err := repository.Pool.Exec(ctx,
		`UPDATE pcs SET last_seen_at=now()-interval '46 seconds' WHERE id=$1`, legacyPCID); err != nil {
		t.Fatal(err)
	}
	legacyActive, err = repository.ListSessions(ctx, userID, legacyDeviceID, legacyPCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(legacyActive) != 0 {
		t.Fatalf("offline legacy PC retained active sessions: %+v", legacyActive)
	}
	legacyAll, err := repository.ListSessionsIncludingEnded(ctx, userID, legacyDeviceID, legacyPCID)
	if err != nil {
		t.Fatal(err)
	}
	if len(legacyAll) != 1 || legacyAll[0].Online {
		t.Fatalf("legacy compatibility online state=%+v, want retained offline row", legacyAll)
	}
}

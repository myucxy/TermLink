package sessioncleanup

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
	storepkg "github.com/termlink/termlink/server/internal/store"
)

func TestAutomaticCleanupSafetyRetentionResurrectionAndHistory(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := storepkg.Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(repository.Close)
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	now := time.Now().UTC().Truncate(time.Microsecond)
	userID, ownerID, pcID := uuid.New(), uuid.New(), uuid.New()
	graceOwnerID, gracePCID := uuid.New(), uuid.New()
	subject := sha256.Sum256([]byte("automatic-session-cleanup:" + userID.String()))
	sessionIDs := map[string]uuid.UUID{
		"fresh": uuid.New(), "stale": uuid.New(), "interrupted": uuid.New(),
		"ended_fresh": uuid.New(), "ended_old": uuid.New(), "manual": uuid.New(),
		"manual_fresh": uuid.New(), "grace": uuid.New(),
	}
	objectPrefix := "automatic-session-cleanup/" + sessionIDs["stale"].String()
	digest := sha256.Sum256([]byte("automatic-session-cleanup-history"))

	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		ids := make([]uuid.UUID, 0, len(sessionIDs))
		for _, id := range sessionIDs {
			ids = append(ids, id)
		}
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE target_id=ANY($1::uuid[])`, ids)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, ids)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	})

	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state,approved_by)
		VALUES
		  ($1,$2,'WINDOWS_PC','Cleanup owner',decode(repeat('41',32),'hex'),
		   decode(repeat('42',32),'hex'),$3,'TRUSTED',NULL),
		  ($4,$2,'WINDOWS_PC','Grace owner',decode(repeat('43',32),'hex'),
		   decode(repeat('44',32),'hex'),$5,'TRUSTED',$1)`,
		ownerID, userID, "cleanup-owner-"+ownerID.String(),
		graceOwnerID, "cleanup-grace-"+graceOwnerID.String()); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpsertPC(ctx, userID, ownerID, storepkg.PCRecord{
		ID: pcID, Name: "Cleanup PC", AgentVersion: "integration-test", KeyEpoch: 1,
		SessionHeartbeatCapable: true,
	}); err != nil {
		t.Fatal(err)
	}
	if err := repository.UpsertPC(ctx, userID, graceOwnerID, storepkg.PCRecord{
		ID: gracePCID, Name: "Grace PC", AgentVersion: "integration-test", KeyEpoch: 1,
		SessionHeartbeatCapable: true,
	}); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		UPDATE pcs
		SET session_heartbeat_enabled_at=$2,last_seen_at=$3
		WHERE id=$1`, pcID, now.Add(-49*time.Hour), now); err != nil {
		t.Fatal(err)
	}

	insertSession := func(name, state string, lastSeen, endedAt *time.Time) {
		t.Helper()
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO sessions(
				id,pc_id,shell_type,state,columns,rows,last_seen_at,ended_at)
			VALUES($1,$2,'powershell',$3,120,40,$4,$5)`,
			sessionIDs[name], pcID, state, lastSeen, endedAt); err != nil {
			t.Fatal(err)
		}
	}
	freshSeen := now.Add(-time.Minute)
	staleSeen := now.Add(-49 * time.Hour)
	recentEnd := now.Add(-14 * time.Minute)
	oldEnd := now.Add(-16 * time.Minute)
	insertSession("fresh", "RUNNING", &freshSeen, nil)
	insertSession("stale", "RUNNING", &staleSeen, nil)
	insertSession("interrupted", "INTERRUPTED", &staleSeen, nil)
	insertSession("ended_fresh", "EXITED", &recentEnd, &recentEnd)
	insertSession("ended_old", "EXITED", &oldEnd, &oldEnd)
	insertSession("manual", "RUNNING", &freshSeen, nil)
	insertSession("manual_fresh", "RUNNING", &now, nil)
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows,last_seen_at)
		VALUES($1,$2,'powershell','RUNNING',120,40,$3)`,
		sessionIDs["grace"], gracePCID, staleSeen); err != nil {
		t.Fatal(err)
	}

	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO event_chunks(
			id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes)
		VALUES($1,$2,1,3,$3,'cleanup-key-0001',$4,128)`,
		uuid.New(), sessionIDs["stale"], objectPrefix+"/chunk.bin", digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO history_uploads(
			id,user_id,actor_device_id,session_id,kind,object_key,key_id,
			ciphertext_sha256,size_bytes,start_seq,end_seq,expires_at)
		VALUES($1,$2,$3,$4,'CHUNK',$5,'cleanup-key-0002',$6,128,4,6,now()+interval '1 hour')`,
		uuid.New(), userID, ownerID, sessionIDs["stale"], objectPrefix+"/pending.bin", digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO control_leases(session_id,holder_device_id,lease_id,expires_at,version)
		VALUES($1,$2,$3,now()+interval '1 minute',1)`,
		sessionIDs["stale"], ownerID, uuid.New()); err != nil {
		t.Fatal(err)
	}

	// On a heartbeat-capable PC, manual deletion uses session-level presence:
	// the fresh session remains protected even though a stale sibling on the
	// same currently-online PC can be deleted without terminating Windows work.
	if _, err := repository.SessionHistoryObjectKeys(ctx, userID, ownerID, sessionIDs["manual_fresh"]); !errors.Is(err, storepkg.ErrSessionActive) {
		t.Fatalf("fresh session delete preflight err=%v, want ErrSessionActive", err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, ownerID, sessionIDs["manual_fresh"], "cleanup-manual-fresh"); !errors.Is(err, storepkg.ErrSessionActive) {
		t.Fatalf("fresh session delete err=%v, want ErrSessionActive", err)
	}
	if _, err := repository.SessionHistoryObjectKeys(ctx, userID, ownerID, sessionIDs["manual"]); err != nil {
		t.Fatalf("stale session delete preflight on online PC: %v", err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, ownerID, sessionIDs["manual"], "cleanup-manual"); err != nil {
		t.Fatal(err)
	}

	cleaned, err := repository.CleanupSessions(
		ctx, now.Add(-DefaultEndedRetention), now.Add(-DefaultStaleRetention), DefaultBatchSize)
	if err != nil {
		t.Fatal(err)
	}
	if cleaned < 3 {
		t.Fatalf("cleaned=%d, want at least this test's stale RUNNING, stale INTERRUPTED, and retained EXITED", cleaned)
	}

	assertTombstone := func(name, wantState string, wantDeleted bool, wantReason *string) {
		t.Helper()
		var state string
		var deletedAt *time.Time
		var reason *string
		if err := repository.Pool.QueryRow(ctx, `
			SELECT state,deleted_at,deletion_reason FROM sessions WHERE id=$1`, sessionIDs[name]).
			Scan(&state, &deletedAt, &reason); err != nil {
			t.Fatal(err)
		}
		if state != wantState || (deletedAt != nil) != wantDeleted {
			t.Fatalf("%s state/deleted=%s/%v, want %s/%v", name, state, deletedAt != nil, wantState, wantDeleted)
		}
		switch {
		case wantReason == nil && reason != nil:
			t.Fatalf("%s reason=%q, want NULL", name, *reason)
		case wantReason != nil && (reason == nil || *reason != *wantReason):
			t.Fatalf("%s reason=%v, want %q", name, reason, *wantReason)
		}
	}
	autoStale, autoEnded, userRequested := "AUTO_STALE", "AUTO_ENDED", "USER_REQUESTED"
	assertTombstone("fresh", "RUNNING", false, nil)
	assertTombstone("ended_fresh", "EXITED", false, nil)
	assertTombstone("manual_fresh", "RUNNING", false, nil)
	assertTombstone("grace", "RUNNING", false, nil)
	assertTombstone("stale", "DELETED", true, &autoStale)
	assertTombstone("interrupted", "DELETED", true, &autoStale)
	assertTombstone("ended_old", "DELETED", true, &autoEnded)
	assertTombstone("manual", "DELETED", true, &userRequested)

	var chunks, uploads, leases, deletes int
	if err := repository.Pool.QueryRow(ctx,
		`SELECT count(*) FROM event_chunks WHERE session_id=$1 AND deleted_at IS NOT NULL`, sessionIDs["stale"]).Scan(&chunks); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx,
		`SELECT count(*) FROM history_uploads WHERE session_id=$1`, sessionIDs["stale"]).Scan(&uploads); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx,
		`SELECT count(*) FROM control_leases WHERE session_id=$1`, sessionIDs["stale"]).Scan(&leases); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM outbox
		WHERE aggregate_id=$1 AND event_type='history.object.delete'`, sessionIDs["stale"]).Scan(&deletes); err != nil {
		t.Fatal(err)
	}
	if chunks != 1 || uploads != 0 || leases != 0 || deletes != 2 {
		t.Fatalf("history chunks/uploads/leases/delete-events=%d/%d/%d/%d, want 1/0/0/2", chunks, uploads, leases, deletes)
	}
	var automaticAudits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE target_id=ANY($1::uuid[]) AND action='session.auto_delete'
		  AND actor_device_id IS NULL`,
		[]uuid.UUID{sessionIDs["stale"], sessionIDs["interrupted"], sessionIDs["ended_old"]}).Scan(&automaticAudits); err != nil {
		t.Fatal(err)
	}
	if automaticAudits != 3 {
		t.Fatalf("automatic audits=%d, want 3", automaticAudits)
	}

	upsert := func(name string) {
		t.Helper()
		if err := repository.UpsertSession(ctx, userID, ownerID, storepkg.SessionRecord{
			ID: sessionIDs[name], PCID: pcID, ShellType: "powershell", State: "RUNNING",
			Columns: 120, Rows: 40, LastOutputSequence: 9, SessionHeartbeatCapable: true,
		}); err != nil {
			t.Fatal(err)
		}
	}
	upsert("stale")
	assertTombstone("stale", "RUNNING", false, nil)
	var resurrectedLastSeen *time.Time
	if err := repository.Pool.QueryRow(ctx,
		`SELECT last_seen_at FROM sessions WHERE id=$1`, sessionIDs["stale"]).Scan(&resurrectedLastSeen); err != nil {
		t.Fatal(err)
	}
	if resurrectedLastSeen == nil || !resurrectedLastSeen.After(staleSeen) {
		t.Fatalf("resurrected last_seen_at=%v, want a fresh owning-PC heartbeat", resurrectedLastSeen)
	}
	upsert("manual")
	assertTombstone("manual", "DELETED", true, &userRequested)
	upsert("ended_old")
	assertTombstone("ended_old", "DELETED", true, &autoEnded)
}

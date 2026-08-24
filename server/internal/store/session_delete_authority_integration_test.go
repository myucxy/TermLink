package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestSessionDeletionRejectsActiveAndCleansOfflineSessionAtomically(t *testing.T) {
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
	repository.AutoTrustDevices = true
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	userID := uuid.New()
	ownerID := uuid.New()
	managerID := uuid.New()
	pcID := uuid.New()
	sessionID := uuid.New()
	crossUserID := uuid.New()
	crossManagerID := uuid.New()
	subject := sha256.Sum256([]byte("session-delete-authority:" + userID.String()))
	crossSubject := sha256.Sum256([]byte("session-delete-authority-cross:" + crossUserID.String()))
	digest := sha256.Sum256([]byte("session-delete-history"))
	objectPrefix := "session-delete-authority/" + sessionID.String()

	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE target_id=$1`, sessionID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=$1`, sessionID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id IN ($1,$2)`, userID, crossUserID)
	})

	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2),($3,$4)`,
		userID, subject[:], crossUserID, crossSubject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES
		  ($1,$2,'WINDOWS_PC','Offline owner',decode(repeat('11',32),'hex'),decode(repeat('12',32),'hex'),$3,'REVOKED'),
		  ($4,$2,'ANDROID','Trusted manager',decode(repeat('21',32),'hex'),decode(repeat('22',32),'hex'),$5,'TRUSTED'),
		  ($6,$7,'ANDROID','Cross-account manager',decode(repeat('31',32),'hex'),decode(repeat('32',32),'hex'),$8,'TRUSTED')`,
		ownerID, userID, "owner-"+ownerID.String(), managerID, "manager-"+managerID.String(),
		crossManagerID, crossUserID, "cross-"+crossManagerID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,remote_routing_enabled,last_seen_at)
		VALUES($1,$2,$3,'Deletion PC','integration-test',false,now())`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO sessions(id,pc_id,shell_type,state,columns,rows)
		VALUES($1,$2,'powershell','RUNNING',120,40)`, sessionID, pcID); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO event_chunks(id,session_id,start_seq,end_seq,object_key,key_id,ciphertext_sha256,size_bytes)
		VALUES($1,$2,1,3,$3,'delete-key-0001',$4,128)`, uuid.New(), sessionID, objectPrefix+"/chunk.bin", digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO snapshots(id,session_id,snapshot_seq,rows,columns,object_key,key_id,ciphertext_sha256,size_bytes,format_name,format_version)
		VALUES($1,$2,3,40,120,$3,'delete-key-0001',$4,128,'BOUNDED_EVENT_REPLAY',1)`,
		uuid.New(), sessionID, objectPrefix+"/snapshot.bin", digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO history_uploads(id,user_id,actor_device_id,session_id,kind,object_key,key_id,ciphertext_sha256,size_bytes,start_seq,end_seq,expires_at)
		VALUES($1,$2,$3,$4,'CHUNK',$5,'delete-key-0002',$6,128,4,6,now()+interval '1 hour')`,
		uuid.New(), userID, managerID, sessionID, objectPrefix+"/pending.bin", digest[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO control_leases(session_id,holder_device_id,lease_id,expires_at,version)
		VALUES($1,$2,$3,now()+interval '1 minute',1)`, sessionID, managerID, uuid.New()); err != nil {
		t.Fatal(err)
	}

	if _, err := repository.SessionHistoryObjectKeys(ctx, userID, managerID, sessionID); !errors.Is(err, ErrSessionActive) {
		t.Fatalf("active deletion preflight err=%v, want ErrSessionActive", err)
	}
	assertSessionDeletionRows(t, ctx, repository, sessionID, "RUNNING", 0, 0, 1, 1)

	if _, err := repository.Pool.Exec(ctx, `UPDATE pcs SET last_seen_at=now()-interval '46 seconds' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	keys, err := repository.SessionHistoryObjectKeys(ctx, userID, managerID, sessionID)
	if err != nil {
		t.Fatalf("offline deletion preflight: %v", err)
	}
	if len(keys) != 3 {
		t.Fatalf("offline deletion keys=%v, want 3 history objects", keys)
	}

	// A PC heartbeat after the object-key preflight must be caught again while
	// the session and PC rows are locked by the deletion transaction.
	if _, err := repository.Pool.Exec(ctx, `UPDATE pcs SET last_seen_at=now() WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, managerID, sessionID, "session-delete-race"); !errors.Is(err, ErrSessionActive) {
		t.Fatalf("transaction active recheck err=%v, want ErrSessionActive", err)
	}
	assertSessionDeletionRows(t, ctx, repository, sessionID, "RUNNING", 0, 0, 1, 1)

	if _, err := repository.Pool.Exec(ctx, `UPDATE pcs SET last_seen_at=now()-interval '46 seconds' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, managerID, sessionID, "session-delete-offline"); err != nil {
		t.Fatalf("delete offline session: %v", err)
	}
	assertSessionDeletionRows(t, ctx, repository, sessionID, "DELETED", 1, 1, 0, 0)
	assertHistoryDeleteOutbox(t, ctx, repository, sessionID, objectPrefix+"/chunk.bin", objectPrefix+"/snapshot.bin", objectPrefix+"/pending.bin")

	keys, err = repository.SessionHistoryObjectKeys(ctx, userID, managerID, sessionID)
	if err != nil || len(keys) != 0 {
		t.Fatalf("deleted same-account preflight keys=%v err=%v, want idempotent empty result", keys, err)
	}
	if err := repository.MarkSessionDeleted(ctx, userID, managerID, sessionID, "session-delete-repeat"); err != nil {
		t.Fatalf("repeat same-account deletion: %v", err)
	}
	var audits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events WHERE action='session.delete' AND target_id=$1`, sessionID).Scan(&audits); err != nil {
		t.Fatal(err)
	}
	if audits != 1 {
		t.Fatalf("successful deletion audits=%d, want exactly 1", audits)
	}

	if _, err := repository.SessionHistoryObjectKeys(ctx, crossUserID, crossManagerID, sessionID); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account deleted preflight err=%v, want ErrDenied", err)
	}
	if err := repository.MarkSessionDeleted(ctx, crossUserID, crossManagerID, sessionID, "session-delete-cross"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account repeat deletion err=%v, want ErrDenied", err)
	}
}

func assertHistoryDeleteOutbox(t *testing.T, ctx context.Context, repository *Postgres, sessionID uuid.UUID, expected ...string) {
	t.Helper()
	rows, err := repository.Pool.Query(ctx, `
		SELECT payload->>'object_key'
		FROM outbox
		WHERE event_type='history.object.delete' AND aggregate_id=$1
		ORDER BY payload->>'object_key'`, sessionID)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	got := make(map[string]int, len(expected))
	for rows.Next() {
		var key string
		if err := rows.Scan(&key); err != nil {
			t.Fatal(err)
		}
		got[key]++
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if len(got) != len(expected) {
		t.Fatalf("history delete outbox=%v, want %v", got, expected)
	}
	for _, key := range expected {
		if got[key] != 1 {
			t.Fatalf("history delete outbox key %q count=%d, want 1", key, got[key])
		}
	}
}

func assertSessionDeletionRows(t *testing.T, ctx context.Context, repository *Postgres, sessionID uuid.UUID, wantState string, wantChunks, wantSnapshots, wantUploads, wantLeases int) {
	t.Helper()
	var state string
	var chunks, snapshots, uploads, leases int
	if err := repository.Pool.QueryRow(ctx, `SELECT state FROM sessions WHERE id=$1`, sessionID).Scan(&state); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM event_chunks WHERE session_id=$1 AND deleted_at IS NOT NULL`, sessionID).Scan(&chunks); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM snapshots WHERE session_id=$1 AND deleted_at IS NOT NULL`, sessionID).Scan(&snapshots); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM history_uploads WHERE session_id=$1`, sessionID).Scan(&uploads); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM control_leases WHERE session_id=$1`, sessionID).Scan(&leases); err != nil {
		t.Fatal(err)
	}
	if state != wantState || chunks != wantChunks || snapshots != wantSnapshots || uploads != wantUploads || leases != wantLeases {
		t.Fatalf("session deletion rows state/chunks/snapshots/uploads/leases=%s/%d/%d/%d/%d, want %s/%d/%d/%d/%d",
			state, chunks, snapshots, uploads, leases, wantState, wantChunks, wantSnapshots, wantUploads, wantLeases)
	}
}

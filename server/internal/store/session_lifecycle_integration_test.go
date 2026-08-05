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

// TestUpsertSessionKeepsTerminalStatesAndOutputCursorMonotonic exercises the
// production PostgreSQL upsert against a real schema. A delayed Agent update
// may carry a newer output sequence, but it must not resurrect an exited or
// deleted session or clear the original terminal timestamp.
func TestUpsertSessionKeepsTerminalStatesAndOutputCursorMonotonic(t *testing.T) {
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
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	userID, deviceID, pcID, sessionID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	subjectHash := sha256.Sum256([]byte("session-lifecycle-" + userID.String()))
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
		INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$2,'WINDOWS_PC','Lifecycle PC',decode(repeat('01',32),'hex'),decode(repeat('02',32),'hex'),$3,'TRUSTED')`,
		deviceID, userID, "lifecycle-"+deviceID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version) VALUES($1,$2,$3,'Lifecycle PC','integration-test')`,
		pcID, userID, deviceID); err != nil {
		t.Fatal(err)
	}

	upsert := func(state string, sequence uint64, endedAt *time.Time) {
		t.Helper()
		if err := repository.UpsertSession(ctx, userID, deviceID, SessionRecord{
			ID:                 sessionID,
			PCID:               pcID,
			ShellType:          "powershell",
			State:              state,
			Columns:            120,
			Rows:               40,
			LastOutputSequence: sequence,
			EndedAt:            endedAt,
		}); err != nil {
			t.Fatalf("upsert %s seq=%d: %v", state, sequence, err)
		}
	}
	read := func(wantState string, wantSequence uint64, wantEndedAt *time.Time) {
		t.Helper()
		var state string
		var sequence int64
		var endedAt *time.Time
		if err := repository.Pool.QueryRow(ctx,
			`SELECT state,last_output_seq,ended_at FROM sessions WHERE id=$1`, sessionID).
			Scan(&state, &sequence, &endedAt); err != nil {
			t.Fatal(err)
		}
		if state != wantState {
			t.Fatalf("state=%q, want %q", state, wantState)
		}
		if sequence != int64(wantSequence) {
			t.Fatalf("last_output_seq=%d, want %d", sequence, wantSequence)
		}
		switch {
		case wantEndedAt == nil && endedAt != nil:
			t.Fatalf("ended_at=%s, want NULL", endedAt)
		case wantEndedAt != nil && (endedAt == nil || !endedAt.Equal(*wantEndedAt)):
			t.Fatalf("ended_at=%v, want %v", endedAt, wantEndedAt)
		}
	}

	upsert("RUNNING", 2, nil)
	read("RUNNING", 2, nil)

	endedAt := time.Now().UTC().Truncate(time.Microsecond)
	upsert("EXITED", 10, &endedAt)
	read("EXITED", 10, &endedAt)

	// A delayed RUNNING update can include output that was emitted before the
	// exit notification. It advances the cursor but cannot resurrect the row.
	upsert("RUNNING", 11, nil)
	read("EXITED", 11, &endedAt)

	// The same rule applies to a delayed interruption notification.
	upsert("INTERRUPTED", 12, nil)
	read("EXITED", 12, &endedAt)

	// Repeated EXITED notifications remain idempotent: a missing or older
	// ended_at never clears or moves the original terminal timestamp.
	upsert("EXITED", 13, nil)
	read("EXITED", 13, &endedAt)
	olderEndedAt := endedAt.Add(-time.Minute)
	upsert("EXITED", 14, &olderEndedAt)
	read("EXITED", 14, &endedAt)

	deletedAt := endedAt.Add(time.Minute)
	if _, err := repository.Pool.Exec(ctx,
		`UPDATE sessions SET state='DELETED',deleted_at=$2 WHERE id=$1`, sessionID, deletedAt); err != nil {
		t.Fatal(err)
	}

	// Deletion is a stronger terminal state than EXITED. Agent retries must
	// not repopulate metadata or advance a row that the account deleted.
	upsert("RUNNING", 15, nil)
	read("DELETED", 14, &endedAt)
	upsert("EXITED", 16, &deletedAt)
	read("DELETED", 14, &endedAt)
	var storedDeletedAt *time.Time
	if err := repository.Pool.QueryRow(ctx,
		`SELECT deleted_at FROM sessions WHERE id=$1`, sessionID).Scan(&storedDeletedAt); err != nil {
		t.Fatal(err)
	}
	if storedDeletedAt == nil || !storedDeletedAt.Equal(deletedAt) {
		t.Fatalf("deleted_at=%v, want %v", storedDeletedAt, deletedAt)
	}
}

package sessioncleanup

import (
	"context"
	"crypto/sha256"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
	storepkg "github.com/termlink/termlink/server/internal/store"
)

func TestAutomaticCleanupRetainsDisconnectedAndExitedSessions(t *testing.T) {
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
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}

	userID, ownerID, pcID := uuid.New(), uuid.New(), uuid.New()
	subject := sha256.Sum256([]byte("retired-automatic-session-cleanup:" + userID.String()))
	sessionStates := map[uuid.UUID]string{
		uuid.New(): "RUNNING",
		uuid.New(): "INTERRUPTED",
		uuid.New(): "EXITED",
	}
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	})

	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subject[:]); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$2,'WINDOWS_PC','Retained sessions owner',decode(repeat('41',32),'hex'),
		       decode(repeat('42',32),'hex'),$3,'TRUSTED')`,
		ownerID, userID, "retained-owner-"+ownerID.String()); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO pcs(
			id,owner_user_id,device_id,name,agent_version,session_heartbeat_capable,
			session_heartbeat_enabled_at,last_seen_at)
		VALUES($1,$2,$3,'Retained sessions PC','integration-test',true,
		       now()-interval '2 days',now()-interval '2 days')`, pcID, userID, ownerID); err != nil {
		t.Fatal(err)
	}
	for sessionID, state := range sessionStates {
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO sessions(
				id,pc_id,shell_type,state,columns,rows,last_seen_at,ended_at)
			VALUES($1,$2,'powershell',$3,120,40,now()-interval '2 days',
			       CASE WHEN $3='EXITED' THEN now()-interval '2 days' ELSE NULL END)`,
			sessionID, pcID, state); err != nil {
			t.Fatal(err)
		}
	}

	cleaned, err := repository.CleanupSessions(
		ctx, time.Now().Add(-DefaultEndedRetention), time.Now().Add(-DefaultStaleRetention), DefaultBatchSize)
	if err != nil {
		t.Fatal(err)
	}
	if cleaned != 0 {
		t.Fatalf("cleaned=%d, want 0 after automatic cleanup retirement", cleaned)
	}
	for sessionID, wantState := range sessionStates {
		var state string
		var deletedAt *time.Time
		var deletionReason *string
		if err := repository.Pool.QueryRow(ctx, `
			SELECT state,deleted_at,deletion_reason FROM sessions WHERE id=$1`, sessionID).
			Scan(&state, &deletedAt, &deletionReason); err != nil {
			t.Fatal(err)
		}
		if state != wantState || deletedAt != nil || deletionReason != nil {
			t.Fatalf("session %s state/deleted/reason=%s/%v/%v, want %s/nil/nil",
				sessionID, state, deletedAt, deletionReason, wantState)
		}
	}
	var automaticAudits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE target_id=ANY($1::uuid[]) AND action='session.auto_delete'`,
		uuidMapKeys(sessionStates)).Scan(&automaticAudits); err != nil {
		t.Fatal(err)
	}
	if automaticAudits != 0 {
		t.Fatalf("automatic cleanup audits=%d, want 0", automaticAudits)
	}
}

func uuidMapKeys(values map[uuid.UUID]string) []uuid.UUID {
	result := make([]uuid.UUID, 0, len(values))
	for key := range values {
		result = append(result, key)
	}
	return result
}

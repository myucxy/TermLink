package store

import (
	"context"
	"fmt"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestRegistrationRecoveryPostgresStateMachineAndIdempotency(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for registration recovery integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "registration_recovery_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop recovery schema: %v", cleanupErr)
		}
	}()
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	applyRegistrationRecoveryMigrations(t, ctx, pool)
	repository := &Postgres{Pool: pool}

	adminID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,is_admin,role) VALUES($1,$2,true,'SUPER_ADMIN')`, adminID, recoveryDigest(0x10)); err != nil {
		t.Fatal(err)
	}
	batchID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_batches(id,name,status,default_max_uses,created_by) VALUES($1,'recovery','ACTIVE',10,$2)`, batchID, adminID); err != nil {
		t.Fatal(err)
	}
	codeID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_codes(id,batch_id,code_hash,code_prefix,max_uses,used_count) VALUES($1,$2,$3,'RECOV',10,3)`, codeID, batchID, recoveryDigest(0x20)); err != nil {
		t.Fatal(err)
	}

	confirmAttempt := seedRecoveryRedemption(t, ctx, pool, codeID, "RESERVED", 0x31)
	releaseAttempt := seedRecoveryRedemption(t, ctx, pool, codeID, "RESERVED", 0x32)
	protectedAttempt := seedRecoveryRedemption(t, ctx, pool, codeID, "PROVIDER_ACCEPTED", 0x33)
	releasedAttempt := seedRecoveryRedemption(t, ctx, pool, codeID, "RELEASED", 0x34)

	candidates, err := repository.ListRegistrationRecoveryCandidates(ctx, time.Now().UTC(), 50)
	if err != nil {
		t.Fatal(err)
	}
	if got := recoveryCandidateIDs(candidates); !got[confirmAttempt] || !got[releaseAttempt] || !got[releasedAttempt] || got[protectedAttempt] {
		t.Fatalf("unexpected recovery candidates: %v", got)
	}

	resolution, err := repository.ConfirmRecoveredRegistration(ctx, confirmAttempt)
	if err != nil || resolution != RegistrationRecoveryConfirmed {
		t.Fatalf("confirm reserved=(%s,%v)", resolution, err)
	}
	resolution, err = repository.ConfirmRecoveredRegistration(ctx, confirmAttempt)
	if err != nil || resolution != RegistrationRecoveryAlreadyConfirmed {
		t.Fatalf("confirm retry=(%s,%v)", resolution, err)
	}
	assertRecoveryStateAndAudit(t, ctx, pool, confirmAttempt, "PROVIDER_ACCEPTED", "registration.recovery.provider_confirmed", 1)

	resolution, err = repository.ReleaseAbsentRegistration(ctx, releaseAttempt)
	if err != nil || resolution != RegistrationRecoveryReleased {
		t.Fatalf("release reserved=(%s,%v)", resolution, err)
	}
	resolution, err = repository.ReleaseAbsentRegistration(ctx, releaseAttempt)
	if err != nil || resolution != RegistrationRecoveryAlreadyReleased {
		t.Fatalf("release retry=(%s,%v)", resolution, err)
	}
	assertRecoveryStateAndAudit(t, ctx, pool, releaseAttempt, "RELEASED", "registration.recovery.uncreated_released", 1)
	assertRecoveryStateAndAudit(t, ctx, pool, releaseAttempt, "RELEASED", "registration.recovery.released_absent", 1)
	var usedCount int
	if err := pool.QueryRow(ctx, `SELECT used_count FROM invitation_codes WHERE id=$1`, codeID).Scan(&usedCount); err != nil || usedCount != 2 {
		t.Fatalf("used_count after idempotent release=(%d,%v), want 2", usedCount, err)
	}

	resolution, err = repository.ReleaseAbsentRegistration(ctx, protectedAttempt)
	if err != nil || resolution != RegistrationRecoveryAlreadyConfirmed {
		t.Fatalf("release provider accepted=(%s,%v)", resolution, err)
	}
	assertRecoveryStateAndAudit(t, ctx, pool, protectedAttempt, "PROVIDER_ACCEPTED", "registration.recovery.uncreated_released", 0)

	resolution, err = repository.ConfirmRecoveredRegistration(ctx, releasedAttempt)
	if err != nil || resolution != RegistrationRecoveryOrphan {
		t.Fatalf("confirm released=(%s,%v)", resolution, err)
	}
	if err := repository.RecordRegistrationOrphanDeleted(ctx, releasedAttempt); err != nil {
		t.Fatal(err)
	}
	if err := repository.RecordRegistrationOrphanDeleted(ctx, releasedAttempt); err != nil {
		t.Fatal(err)
	}
	assertRecoveryStateAndAudit(t, ctx, pool, releasedAttempt, "RELEASED", "registration.recovery.orphan_deleted", 1)

	missingAttempt := uuid.New()
	var wait sync.WaitGroup
	errorsSeen := make(chan error, 2)
	for index := 0; index < 2; index++ {
		wait.Add(1)
		go func() {
			defer wait.Done()
			errorsSeen <- repository.RecordRegistrationOrphanDeleted(ctx, missingAttempt)
		}()
	}
	wait.Wait()
	close(errorsSeen)
	for err := range errorsSeen {
		if err != nil {
			t.Fatalf("concurrent orphan marker: %v", err)
		}
	}
	assertRecoveryAudit(t, ctx, pool, missingAttempt, "registration.recovery.orphan_deleted", 1)

	knownSubject := recoveryDigest(0x55)
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, uuid.New(), knownSubject); err != nil {
		t.Fatal(err)
	}
	exists, err := repository.RegistrationSubjectExists(ctx, digestArray(knownSubject))
	if err != nil || !exists {
		t.Fatalf("known subject exists=(%t,%v)", exists, err)
	}
	exists, err = repository.RegistrationSubjectExists(ctx, digestArray(recoveryDigest(0x56)))
	if err != nil || exists {
		t.Fatalf("unknown subject exists=(%t,%v)", exists, err)
	}
}

func applyRegistrationRecoveryMigrations(t *testing.T, ctx context.Context, pool *pgxpool.Pool) {
	t.Helper()
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	for version := 1; version <= int(migrations.LatestVersion); version++ {
		prefix := fmt.Sprintf("%06d_", version)
		name := ""
		for _, entry := range entries {
			if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), ".up.sql") {
				name = entry.Name()
				break
			}
		}
		if name == "" {
			t.Fatalf("migration %d missing", version)
		}
		contents, err := migrations.Files.ReadFile(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := pool.Exec(ctx, string(contents)); err != nil {
			t.Fatalf("apply %s: %v", name, err)
		}
	}
}

func seedRecoveryRedemption(t *testing.T, ctx context.Context, pool *pgxpool.Pool, codeID uuid.UUID, state string, digestByte byte) uuid.UUID {
	t.Helper()
	attemptID := uuid.New()
	providerAccepted, released := any(nil), any(nil)
	if state == "PROVIDER_ACCEPTED" {
		providerAccepted = time.Now().UTC().Add(-time.Hour)
	}
	if state == "RELEASED" {
		released = time.Now().UTC().Add(-time.Hour)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO invitation_redemptions(
			id,code_id,registration_attempt_id,email_hash,state,reserved_until,
			provider_accepted_at,released_at,created_at,updated_at)
		VALUES($1,$2,$3,$4,$5,now()-interval '30 minutes',$6,$7,now()-interval '1 hour',now()-interval '30 minutes')`,
		uuid.New(), codeID, attemptID, recoveryDigest(digestByte), state, providerAccepted, released); err != nil {
		t.Fatal(err)
	}
	return attemptID
}

func recoveryCandidateIDs(candidates []RegistrationRecoveryCandidate) map[uuid.UUID]bool {
	result := make(map[uuid.UUID]bool, len(candidates))
	for _, candidate := range candidates {
		result[candidate.AttemptID] = true
	}
	return result
}

func assertRecoveryStateAndAudit(t *testing.T, ctx context.Context, pool *pgxpool.Pool, attemptID uuid.UUID, state, action string, count int) {
	t.Helper()
	var gotState string
	if err := pool.QueryRow(ctx, `SELECT state FROM invitation_redemptions WHERE registration_attempt_id=$1`, attemptID).Scan(&gotState); err != nil || gotState != state {
		t.Fatalf("attempt state=(%q,%v), want %s", gotState, err, state)
	}
	assertRecoveryAudit(t, ctx, pool, attemptID, action, count)
}

func assertRecoveryAudit(t *testing.T, ctx context.Context, pool *pgxpool.Pool, attemptID uuid.UUID, action string, count int) {
	t.Helper()
	var got int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM audit_events WHERE target_type='registration_attempt' AND target_id=$1 AND action=$2 AND result='SUCCEEDED' AND metadata='{}'::jsonb`, attemptID, action).Scan(&got); err != nil || got != count {
		t.Fatalf("audit %s count=(%d,%v), want %d", action, got, err, count)
	}
}

func recoveryDigest(value byte) []byte {
	result := make([]byte, 32)
	for index := range result {
		result[index] = value
	}
	return result
}

func digestArray(value []byte) [32]byte {
	var result [32]byte
	copy(result[:], value)
	return result
}

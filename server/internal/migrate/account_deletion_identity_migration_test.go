package migrate

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestAccountDeletionIdentityMigrationContract(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000027_account_deletion_identity.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000027_account_deletion_identity.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	up := strings.ToLower(strings.Join(strings.Fields(string(upBytes)), " "))
	down := strings.ToLower(strings.Join(strings.Fields(string(downBytes)), " "))
	for _, required := range []string{
		"identity_handle_cipher bytea", "identity_handle_key_version smallint",
		"octet_length(identity_handle_cipher) = 64", "identity_handle_key_version > 0",
		"status not in ('confirmed','processing','failed')", "identity handle is immutable",
		"old.status <> 'requested' or new.status <> 'requested'",
		"where status='processing'", "where status in ('confirmed','failed')",
	} {
		if !strings.Contains(up, required) {
			t.Errorf("migration 27 up lacks %q", required)
		}
	}
	for _, forbidden := range []string{
		"keycloak_subject text", "provider_subject text", "identity_handle_plaintext",
		"identity_handle_cipher text", "jsonb_build_object('identity_handle_cipher'",
	} {
		if strings.Contains(up, forbidden) {
			t.Errorf("migration 27 persists forbidden identity material %q", forbidden)
		}
	}
	for _, required := range []string{
		"where status='processing'", "where status in ('requested','confirmed','failed')",
		"drop trigger if exists account_deletion_identity_handle_immutable",
		"drop function if exists enforce_account_deletion_identity_handle",
		"drop column if exists identity_handle_key_version",
		"drop column if exists identity_handle_cipher",
	} {
		if !strings.Contains(down, required) {
			t.Errorf("migration 27 down lacks %q", required)
		}
	}
}

func TestAccountDeletionIdentityMigrationUpDownPostgres(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for migration 27 integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()

	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "migration_27_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupContext, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop isolated migration schema: %v", cleanupErr)
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

	for version := 1; version <= 26; version++ {
		execMigrationVersion(t, ctx, pool, version, "up")
	}
	fixtures := seedMigration27Fixtures(t, ctx, pool)
	execMigrationVersion(t, ctx, pool, 27, "up")
	assertMigration27UpgradeAndIntegrity(t, ctx, pool, fixtures)
	execMigrationVersion(t, ctx, pool, 27, "down")
	assertMigration27Rollback(t, ctx, pool, fixtures)
}

type migration27Fixtures struct {
	requested  uuid.UUID
	confirmed  uuid.UUID
	processing uuid.UUID
	failed     uuid.UUID
	completed  uuid.UUID
}

func seedMigration27Fixtures(t *testing.T, ctx context.Context, pool *pgxpool.Pool) migration27Fixtures {
	t.Helper()
	accounts := []uuid.UUID{uuid.New(), uuid.New(), uuid.New(), uuid.New(), uuid.New()}
	for index, accountID := range accounts {
		if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, repeatedMigrationByte(byte(0x71+index))); err != nil {
			t.Fatal(err)
		}
	}
	fixture := migration27Fixtures{
		requested: uuid.New(), confirmed: uuid.New(), processing: uuid.New(),
		failed: uuid.New(), completed: uuid.New(),
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO account_deletion_requests(id,account_id,confirmation_expires_at)
		VALUES($1,$2,now()+interval '1 day')`, fixture.requested, accounts[0]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO account_deletion_requests(
			id,account_id,status,confirmation_expires_at,confirmed_at,scheduled_for,retention_deadline
		) VALUES($1,$2,'CONFIRMED',now()+interval '1 day',now(),now(),now()+interval '30 days')`,
		fixture.confirmed, accounts[1]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO account_deletion_requests(
			id,account_id,status,confirmation_expires_at,confirmed_at,scheduled_for,retention_deadline,
			processor_id,processor_lease_until,processing_started_at,processing_fence,attempt_count
		) VALUES($1,$2,'PROCESSING',now()+interval '1 day',now(),now(),now()+interval '30 days',
		         $3,now()+interval '1 minute',now(),1,1)`,
		fixture.processing, accounts[2], uuid.New()); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO account_deletion_requests(
			id,account_id,status,confirmation_expires_at,confirmed_at,scheduled_for,retention_deadline,
			error_code,failed_at
		) VALUES($1,$2,'FAILED',now()+interval '1 day',now(),now(),now()+interval '30 days',
		         'LEGACY_FAILURE',now())`, fixture.failed, accounts[3]); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO account_deletion_requests(
			id,account_id,status,confirmation_expires_at,confirmed_at,scheduled_for,retention_deadline,
			completed_at
		) VALUES($1,$2,'COMPLETED',now()+interval '1 day',now(),now(),now()+interval '30 days',now())`,
		fixture.completed, accounts[4]); err != nil {
		t.Fatal(err)
	}
	return fixture
}

func assertMigration27UpgradeAndIntegrity(t *testing.T, ctx context.Context, pool *pgxpool.Pool, fixture migration27Fixtures) {
	t.Helper()
	for requestID, wanted := range map[uuid.UUID]string{
		fixture.requested: "REQUESTED", fixture.confirmed: "CANCELED",
		fixture.processing: "CANCELED", fixture.failed: "CANCELED", fixture.completed: "COMPLETED",
	} {
		var status string
		if err := pool.QueryRow(ctx, `SELECT status FROM account_deletion_requests WHERE id=$1`, requestID).Scan(&status); err != nil || status != wanted {
			t.Fatalf("request %s status=%q error=%v, want %s", requestID, status, err, wanted)
		}
	}

	validCipher := make([]byte, 64)
	invalidCipher := make([]byte, 63)
	for name, statement := range map[string]struct {
		sql  string
		args []any
	}{
		"cipher without version": {`UPDATE account_deletion_requests SET identity_handle_cipher=$2 WHERE id=$1`, []any{fixture.requested, validCipher}},
		"version without cipher": {`UPDATE account_deletion_requests SET identity_handle_key_version=1 WHERE id=$1`, []any{fixture.requested}},
		"invalid cipher size":    {`UPDATE account_deletion_requests SET identity_handle_cipher=$2,identity_handle_key_version=1 WHERE id=$1`, []any{fixture.requested, invalidCipher}},
		"invalid key version":    {`UPDATE account_deletion_requests SET identity_handle_cipher=$2,identity_handle_key_version=0 WHERE id=$1`, []any{fixture.requested, validCipher}},
		"confirm without handle": {`UPDATE account_deletion_requests SET status='CONFIRMED',confirmed_at=now(),scheduled_for=now(),retention_deadline=now()+interval '30 days' WHERE id=$1`, []any{fixture.requested}},
		"late completed binding": {`UPDATE account_deletion_requests SET identity_handle_cipher=$2,identity_handle_key_version=1 WHERE id=$1`, []any{fixture.completed, validCipher}},
	} {
		t.Run(name, func(t *testing.T) {
			_, err := pool.Exec(ctx, statement.sql, statement.args...)
			assertMigration25PostgresCode(t, err, "23514")
		})
	}
	if _, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET identity_handle_cipher=$2,identity_handle_key_version=7 WHERE id=$1`, fixture.requested, validCipher); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET status='CONFIRMED',confirmed_at=now(),scheduled_for=now(),retention_deadline=now()+interval '30 days' WHERE id=$1`, fixture.requested); err != nil {
		t.Fatal(err)
	}
	replacement := make([]byte, 64)
	replacement[0] = 1
	_, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET identity_handle_cipher=$2 WHERE id=$1`, fixture.requested, replacement)
	assertMigration25PostgresCode(t, err, "23514")
}

func assertMigration27Rollback(t *testing.T, ctx context.Context, pool *pgxpool.Pool, fixture migration27Fixtures) {
	t.Helper()
	var columns, functions, triggers int
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM information_schema.columns
		WHERE table_schema=current_schema() AND table_name='account_deletion_requests'
		  AND column_name IN ('identity_handle_cipher','identity_handle_key_version')`).Scan(&columns); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM pg_proc
		WHERE pronamespace=current_schema()::regnamespace
		  AND proname='enforce_account_deletion_identity_handle'`).Scan(&functions); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `
		SELECT count(*)
		FROM pg_trigger trigger
		JOIN pg_class relation ON relation.oid=trigger.tgrelid
		JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
		WHERE namespace.nspname=current_schema()
		  AND relation.relname='account_deletion_requests'
		  AND trigger.tgname='account_deletion_identity_handle_immutable'
		  AND NOT trigger.tgisinternal`).Scan(&triggers); err != nil {
		t.Fatal(err)
	}
	if columns != 0 || functions != 0 || triggers != 0 {
		t.Fatalf("migration 27 rollback left columns/functions/triggers=%d/%d/%d", columns, functions, triggers)
	}
	var downgradedStatus string
	var unsafeNonterminal int
	if err := pool.QueryRow(ctx, `SELECT status FROM account_deletion_requests WHERE id=$1`, fixture.requested).Scan(&downgradedStatus); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM account_deletion_requests
		WHERE status IN ('REQUESTED','CONFIRMED','PROCESSING','FAILED')`).Scan(&unsafeNonterminal); err != nil {
		t.Fatal(err)
	}
	if downgradedStatus != "CANCELED" || unsafeNonterminal != 0 {
		t.Fatalf("unsafe downgrade state: bound request=%s nonterminal=%d", downgradedStatus, unsafeNonterminal)
	}
}

package migrate

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestControlPrivacyReconciliationMigrationContract(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000025_control_privacy_reconciliation.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000025_control_privacy_reconciliation.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	up := strings.ToLower(strings.Join(strings.Fields(string(upBytes)), " "))
	down := strings.ToLower(strings.Join(strings.Fields(string(downBytes)), " "))
	for _, required := range []string{
		"add column updated_at", "add column control_mode", "add column local_priority", "add column policy_version",
		"create table data_export_requests", "create table account_deletion_requests", "processing_fence",
		"create function enforce_privacy_request_transition", "create table reconciliation_discrepancies",
		"external_reference_hmac bytea not null", "duplicate_local_reference", "local_currency", "provider_currency",
		"foreign key (run_id, provider)", "raw statements, external transaction text, credentials, and payment account data are prohibited",
	} {
		if !strings.Contains(up, required) {
			t.Errorf("migration 25 up lacks %q", required)
		}
	}
	for _, forbidden := range []string{
		"terminal_body", "terminal_content", "statement_payload", "raw_statement", "external_trade_no text",
		"provider_trade_no text", "certificate_pem", "private_key", "client_secret", "access_token",
	} {
		if strings.Contains(up, forbidden) {
			t.Errorf("migration 25 persists forbidden sensitive field %q", forbidden)
		}
	}
	for _, required := range []string{
		"drop table if exists reconciliation_discrepancies", "drop table if exists account_deletion_requests",
		"drop table if exists data_export_requests", "drop column if exists policy_version",
		"drop column if exists local_priority", "drop column if exists control_mode", "drop column if exists updated_at",
	} {
		if !strings.Contains(down, required) {
			t.Errorf("migration 25 down lacks %q", required)
		}
	}
}

func TestControlPrivacyReconciliationMigrationUpDownPostgres(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for migration 25 integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()

	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "migration_25_" + strings.ReplaceAll(uuid.NewString(), "-", "")
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

	for version := 1; version <= 24; version++ {
		execMigrationVersion(t, ctx, pool, version, "up")
	}
	pcID, accountID := seedMigration25Fixture(t, ctx, pool)
	execMigrationVersion(t, ctx, pool, 25, "up")
	assertMigration25DefaultsAndIntegrity(t, ctx, pool, pcID, accountID)
	execMigrationVersion(t, ctx, pool, 25, "down")
	assertMigration25Rollback(t, ctx, pool)
}

func execMigrationVersion(t *testing.T, ctx context.Context, pool *pgxpool.Pool, version int, direction string) {
	t.Helper()
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	prefix := fmt.Sprintf("%06d_", version)
	suffix := "." + direction + ".sql"
	name := ""
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), suffix) {
			name = entry.Name()
			break
		}
	}
	if name == "" {
		t.Fatalf("migration %d %s was not found", version, direction)
	}
	sql, err := migrations.Files.ReadFile(name)
	if err != nil {
		t.Fatal(err)
	}
	tx, err := pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, string(sql)); err != nil {
		t.Fatalf("execute %s: %v", name, err)
	}
	if err = tx.Commit(ctx); err != nil {
		t.Fatalf("commit %s: %v", name, err)
	}
}

func seedMigration25Fixture(t *testing.T, ctx context.Context, pool *pgxpool.Pool) (uuid.UUID, uuid.UUID) {
	t.Helper()
	accountID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, accountID, repeatedMigrationByte(0x11)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state) VALUES($1,$2,'WINDOWS_PC','PC',$3,$4,'migration-25-key','TRUSTED')`, deviceID, accountID, repeatedMigrationByte(0x21), repeatedMigrationByte(0x22)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version) VALUES($1,$2,$3,'PC','1.0.0')`, pcID, accountID, deviceID); err != nil {
		t.Fatal(err)
	}
	return pcID, accountID
}

func assertMigration25DefaultsAndIntegrity(t *testing.T, ctx context.Context, pool *pgxpool.Pool, pcID, accountID uuid.UUID) {
	t.Helper()
	var mode string
	var localPriority bool
	var policyVersion int64
	var updatedAt time.Time
	if err := pool.QueryRow(ctx, `SELECT control_mode,local_priority,policy_version,updated_at FROM pcs WHERE id=$1`, pcID).Scan(&mode, &localPriority, &policyVersion, &updatedAt); err != nil || mode != "SINGLE" || !localPriority || policyVersion != 1 || updatedAt.IsZero() {
		t.Fatalf("PC policy defaults=(%q,%t,%d,%v err=%v)", mode, localPriority, policyVersion, updatedAt, err)
	}
	_, err := pool.Exec(ctx, `UPDATE pcs SET control_mode='UNSAFE' WHERE id=$1`, pcID)
	assertMigration25PostgresCode(t, err, "23514")
	_, err = pool.Exec(ctx, `UPDATE pcs SET policy_version=0 WHERE id=$1`, pcID)
	assertMigration25PostgresCode(t, err, "23514")

	exportID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO data_export_requests(id,account_id,confirmation_expires_at) VALUES($1,$2,now()+interval '1 hour')`, exportID, accountID); err == nil {
		t.Fatal("data export accepted an account deletion-only column")
	}
	if _, err := pool.Exec(ctx, `INSERT INTO data_export_requests(id,account_id) VALUES($1,$2)`, exportID, accountID); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `UPDATE data_export_requests SET scope=ARRAY['ACCOUNT_PROFILE','TERMINAL_BODY'] WHERE id=$1`, exportID)
	assertMigration25PostgresCode(t, err, "23514")
	_, err = pool.Exec(ctx, `UPDATE data_export_requests SET status='READY',artifact_object_key='exports/x',artifact_sha256=$2,artifact_size_bytes=1,completed_at=now(),expires_at=now()+interval '1 day' WHERE id=$1`, exportID, repeatedMigrationByte(0x31))
	assertMigration25PostgresCode(t, err, "23514")
	if _, err := pool.Exec(ctx, `UPDATE data_export_requests SET status='PROCESSING',processor_id=$2,processor_lease_until=now()+interval '1 minute',processing_started_at=now(),processing_fence=1,attempt_count=1,version=2,updated_at=now() WHERE id=$1`, exportID, uuid.New()); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE data_export_requests SET status='READY',artifact_object_key='exports/redacted.zip',artifact_sha256=$2,artifact_size_bytes=128,processor_lease_until=NULL,completed_at=now(),expires_at=now()+interval '1 day',version=3,updated_at=now() WHERE id=$1`, exportID, repeatedMigrationByte(0x32)); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `UPDATE data_export_requests SET scope=ARRAY['ACCOUNT_PROFILE'] WHERE id=$1`, exportID)
	assertMigration25PostgresCode(t, err, "23514")

	deletionID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO account_deletion_requests(id,account_id,confirmation_expires_at) VALUES($1,$2,now()+interval '1 day')`, deletionID, accountID); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `UPDATE account_deletion_requests SET status='PROCESSING',processor_id=$2,processor_lease_until=now()+interval '1 minute',processing_started_at=now(),processing_fence=1,attempt_count=1 WHERE id=$1`, deletionID, uuid.New())
	assertMigration25PostgresCode(t, err, "23514")
	if _, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET status='CONFIRMED',confirmed_at=now(),scheduled_for=now()+interval '1 hour',retention_deadline=now()+interval '30 days',version=2,updated_at=now() WHERE id=$1`, deletionID); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET status='PROCESSING',processor_id=$2,processor_lease_until=now()+interval '1 minute',processing_started_at=now(),processing_fence=1,attempt_count=1,version=3,updated_at=now() WHERE id=$1`, deletionID, uuid.New()); err != nil {
		t.Fatal(err)
	}

	runID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO reconciliation_runs(id,provider,period_start,period_end,status) VALUES($1,'ALIPAY',current_date,current_date,'PENDING')`, runID); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO reconciliation_discrepancies(id,run_id,discrepancy_key,provider,category,external_reference_hmac,summary_code) VALUES($1,$2,$3,'ALIPAY','DUPLICATE_LOCAL_REFERENCE',$4,'DUPLICATE_LOCAL')`, uuid.New(), runID, repeatedMigrationByte(0x41), repeatedMigrationByte(0x42)); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `INSERT INTO reconciliation_discrepancies(id,run_id,discrepancy_key,provider,category,external_reference_hmac,summary_code) VALUES($1,$2,$3,'WECHAT_PAY','OTHER',$4,'PROVIDER_MISMATCH')`, uuid.New(), runID, repeatedMigrationByte(0x43), repeatedMigrationByte(0x44))
	assertMigration25PostgresCode(t, err, "23503")
	_, err = pool.Exec(ctx, `INSERT INTO reconciliation_discrepancies(id,run_id,discrepancy_key,provider,category,external_reference_hmac,summary_code) VALUES($1,$2,$3,'ALIPAY','OTHER',$4,'RAW_REFERENCE')`, uuid.New(), runID, []byte("raw-external-reference"), []byte("raw-external-reference"))
	assertMigration25PostgresCode(t, err, "23514")
}

func assertMigration25Rollback(t *testing.T, ctx context.Context, pool *pgxpool.Pool) {
	t.Helper()
	for _, table := range []string{"data_export_requests", "account_deletion_requests", "reconciliation_discrepancies"} {
		var relation *string
		if err := pool.QueryRow(ctx, `SELECT to_regclass($1)::text`, table).Scan(&relation); err != nil || relation != nil {
			t.Errorf("table %s after rollback=(%v,%v)", table, relation, err)
		}
	}
	var count int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='pcs' AND column_name IN ('updated_at','control_mode','local_priority','policy_version')`).Scan(&count); err != nil || count != 0 {
		t.Fatalf("PC policy columns after rollback=(%d,%v)", count, err)
	}
	var constraintExists bool
	if err := pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE connamespace=current_schema()::regnamespace AND conname='reconciliation_runs_id_provider_unique')`).Scan(&constraintExists); err != nil || constraintExists {
		t.Fatalf("run/provider constraint after rollback=(%t,%v)", constraintExists, err)
	}
}

func repeatedMigrationByte(value byte) []byte {
	result := make([]byte, 32)
	for index := range result {
		result[index] = value
	}
	return result
}

func assertMigration25PostgresCode(t *testing.T, err error, code string) {
	t.Helper()
	if err == nil {
		t.Fatalf("operation succeeded, want PostgreSQL error %s", code)
	}
	var postgresError *pgconn.PgError
	if !errors.As(err, &postgresError) || postgresError.Code != code {
		t.Fatalf("PostgreSQL error=%v, want SQLSTATE %s", err, code)
	}
}

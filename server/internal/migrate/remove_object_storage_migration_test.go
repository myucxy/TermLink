package migrate

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestRemoveObjectStorageMigrationIsCompleteAndFailClosed(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000041_remove_privacy_export_and_object_storage.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000041_remove_privacy_export_and_object_storage.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	up := strings.ToLower(strings.Join(strings.Fields(string(upBytes)), " "))
	for _, required := range []string{
		"delete from outbox", "history.object.delete", "privacy.data_export.%",
		"delete from pc_file_operations", "transfer_protocol='object_v1'",
		"drop table data_export_requests", "drop column object_key",
		"transfer_protocol='realtime_v2'",
	} {
		if !strings.Contains(up, required) {
			t.Fatalf("migration 41 lacks %q", required)
		}
	}
	down := strings.ToLower(string(downBytes))
	if !strings.Contains(down, "migration 41 is irreversible") {
		t.Fatal("migration 41 down does not fail closed")
	}
}

func TestRemoveObjectStorageMigrationUpPostgres(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	if !strings.Contains(strings.ToLower(databaseURL), "migration_test") {
		t.Fatal("migration integration test requires a database name containing migration_test")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	if _, err := pool.Exec(ctx, `DROP SCHEMA public CASCADE; CREATE SCHEMA public`); err != nil {
		t.Fatal(err)
	}
	if err := UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}

	var migrationCount int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM schema_migrations`).Scan(&migrationCount); err != nil {
		t.Fatal(err)
	}
	if migrationCount != 41 {
		t.Fatalf("applied migrations=%d, want 41", migrationCount)
	}

	var exportTable *string
	if err := pool.QueryRow(ctx, `SELECT to_regclass('public.data_export_requests')::text`).Scan(&exportTable); err != nil {
		t.Fatal(err)
	}
	if exportTable != nil {
		t.Fatalf("data_export_requests still exists as %q", *exportTable)
	}

	var objectKeyColumns int
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM information_schema.columns
		WHERE table_schema='public' AND table_name='pc_file_operations' AND column_name='object_key'`).Scan(&objectKeyColumns); err != nil {
		t.Fatal(err)
	}
	if objectKeyColumns != 0 {
		t.Fatal("pc_file_operations.object_key still exists")
	}

	var constraintDefinition string
	if err := pool.QueryRow(ctx, `
		SELECT pg_get_constraintdef(oid)
		FROM pg_constraint
		WHERE conrelid='pc_file_operations'::regclass
		  AND conname='pc_file_operations_transfer_protocol_check'`).Scan(&constraintDefinition); err != nil {
		t.Fatal(err)
	}
	normalizedConstraint := strings.ToLower(constraintDefinition)
	if !strings.Contains(normalizedConstraint, "realtime_v2") || strings.Contains(normalizedConstraint, "object_v1") {
		t.Fatalf("unexpected transfer protocol constraint: %s", constraintDefinition)
	}

	var retiredOutboxEvents int
	if err := pool.QueryRow(ctx, `
		SELECT count(*) FROM outbox
		WHERE event_type='history.object.delete' OR event_type LIKE 'privacy.data_export.%'`).Scan(&retiredOutboxEvents); err != nil {
		t.Fatal(err)
	}
	if retiredOutboxEvents != 0 {
		t.Fatalf("retired outbox events=%d, want 0", retiredOutboxEvents)
	}
}

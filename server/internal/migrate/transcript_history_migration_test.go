package migrate

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestTranscriptMigrationPreservesGeneratedHistoryConstraintNames(t *testing.T) {
	up, err := migrations.Files.ReadFile("000014_transcript_history.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000014_transcript_history.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	upSQL, downSQL := string(up), string(down)
	if !strings.Contains(upSQL, "DROP CONSTRAINT history_uploads_check1") {
		t.Fatal("migration 14 does not remove the generated legacy shape constraint")
	}
	if strings.Contains(upSQL, "DROP CONSTRAINT history_uploads_check,") {
		t.Fatal("migration 14 removes the generated end_seq >= start_seq constraint")
	}
	if !strings.Contains(downSQL, "ADD CONSTRAINT history_uploads_check1 CHECK") {
		t.Fatal("migration 14 down does not restore the legacy generated shape constraint name")
	}
	if strings.Contains(downSQL, "ADD CONSTRAINT history_uploads_check CHECK") {
		t.Fatal("migration 14 down collides with the preserved range constraint")
	}
	if strings.Contains(downSQL, "DELETE FROM history_uploads WHERE kind = 'TRANSCRIPT'") ||
		!strings.Contains(downSQL, "migration 14 cannot be reversed while transcript object references exist") {
		t.Fatal("migration 14 down does not fail closed around transcript object references")
	}
}

func TestTranscriptMigrationReplacesOnlyLegacyShapeConstraint(t *testing.T) {
	t.Skip("legacy server terminal-history tables were retired by migration 39")
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the migration integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()

	rows, err := pool.Query(ctx, `
		SELECT constraint_item.conname, pg_get_constraintdef(constraint_item.oid)
		FROM pg_constraint constraint_item
		JOIN pg_class table_item ON table_item.oid=constraint_item.conrelid
		WHERE table_item.relname='history_uploads' AND constraint_item.contype='c'`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	hasEndRange, hasTranscriptShape, hasLegacyShape := false, false, false
	for rows.Next() {
		var name, definition string
		if err := rows.Scan(&name, &definition); err != nil {
			t.Fatal(err)
		}
		normalized := strings.ToUpper(definition)
		if strings.Contains(normalized, "END_SEQ >= START_SEQ") {
			hasEndRange = true
		}
		if name == "history_uploads_shape_check" && strings.Contains(normalized, "'TRANSCRIPT'") {
			hasTranscriptShape = true
		}
		if strings.Contains(normalized, "KIND = 'CHUNK'") && strings.Contains(normalized, "KIND = 'SNAPSHOT'") &&
			!strings.Contains(normalized, "'TRANSCRIPT'") {
			hasLegacyShape = true
		}
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if !hasEndRange || !hasTranscriptShape || hasLegacyShape {
		t.Fatalf("history_uploads constraints end-range/transcript-shape/legacy-shape=%t/%t/%t, want true/true/false",
			hasEndRange, hasTranscriptShape, hasLegacyShape)
	}
}

func TestTranscriptMigrationDownFailsClosedWithPendingDeletion(t *testing.T) {
	t.Skip("legacy server terminal-history tables were retired by migration 39")
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the migration integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	tx, err := pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	objectKey := "accounts/migration-test/sessions/down-guard/transcript/pending.bin"
	if _, err := tx.Exec(ctx, `
		INSERT INTO outbox(event_type,payload)
		VALUES('history.object.delete',jsonb_build_object('object_key',$1::text))`, objectKey); err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000014_transcript_history.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	_, err = tx.Exec(ctx, string(down))
	var postgresError *pgconn.PgError
	if !errors.As(err, &postgresError) || postgresError.Code != "P0001" ||
		!strings.Contains(postgresError.Message, "migration 14 cannot be reversed while transcript object references exist") {
		t.Fatalf("migration 14 down error=%v, want fail-closed P0001", err)
	}
}

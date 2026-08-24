package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestTranscriptPublicationMigrationDefinesCompletionManifestAndSafeRollback(t *testing.T) {
	up, err := migrations.Files.ReadFile("000016_transcript_publications.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000016_transcript_publications.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	upSQL, downSQL := string(up), string(down)
	for _, contract := range []string{
		"ADD COLUMN transcript_initial_before_line_id",
		"ADD COLUMN transcript_page_count",
		"CREATE TABLE transcript_publications",
		"PRIMARY KEY (session_id, recipient_device_id, as_of_snapshot_seq)",
		"expected_page_count integer NOT NULL CHECK (expected_page_count BETWEEN 0 AND 100000)",
		"completed_revision bigint CHECK (completed_revision > 0)",
		"transcript_publications_completion_check",
	} {
		if !strings.Contains(upSQL, contract) {
			t.Errorf("migration 16 up omits %q", contract)
		}
	}
	if !strings.Contains(downSQL, "migration 16 cannot be reversed while transcript publication metadata exists") ||
		!strings.Contains(downSQL, "DROP TABLE transcript_publications") ||
		!strings.Contains(downSQL, "DROP COLUMN transcript_initial_before_line_id") ||
		!strings.Contains(downSQL, "DROP COLUMN transcript_page_count") {
		t.Fatal("migration 16 down does not fail closed and remove its schema")
	}
}

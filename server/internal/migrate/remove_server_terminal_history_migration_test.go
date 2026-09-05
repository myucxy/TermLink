package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestRemoveServerTerminalHistoryMigrationPreservesRequiredBoundaries(t *testing.T) {
	data, err := migrations.Files.ReadFile("000039_remove_server_terminal_history.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	sql := strings.ToLower(string(data))
	for _, required := range []string{
		"'history.object.delete'",
		"from event_chunks",
		"from snapshots",
		"from transcript_pages",
		"from history_uploads",
		"drop table transcript_publications",
		"drop table transcript_pages",
		"drop table history_uploads",
		"drop table snapshots",
		"drop table event_chunks",
		"drop function assign_session_history_revision()",
		"drop column history_revision",
	} {
		if !strings.Contains(sql, required) {
			t.Fatalf("migration 39 lacks %q", required)
		}
	}
	for _, retained := range []string{
		"drop column snapshot_status",
		"drop column snapshot_reason_code",
		"drop index outbox_history_object_delete_unique",
		"drop table outbox",
		"drop table pc_file_operations",
		"drop table data_export_requests",
	} {
		if strings.Contains(sql, retained) {
			t.Fatalf("migration 39 crosses retained boundary %q", retained)
		}
	}
}

package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestSessionSnapshotDiagnosticsMigrationIsBoundedAndReversible(t *testing.T) {
	up, err := migrations.Files.ReadFile("000020_session_snapshot_diagnostics.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000020_session_snapshot_diagnostics.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	text := string(up)
	for _, required := range []string{
		"snapshot_status", "snapshot_reason_code", "^[A-Z0-9_]{1,64}$", "UNSUPPORTED",
	} {
		if !strings.Contains(text, required) {
			t.Fatalf("up migration omitted %q", required)
		}
	}
	if !strings.Contains(string(down), "DROP COLUMN IF EXISTS snapshot_status") {
		t.Fatal("down migration does not remove snapshot diagnostics")
	}
}

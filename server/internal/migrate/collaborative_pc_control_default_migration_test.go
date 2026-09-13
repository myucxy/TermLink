package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestCollaborativePCControlDefaultMigrationIsReversible(t *testing.T) {
	up, err := migrations.Files.ReadFile("000045_collaborative_pc_control_default.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000045_collaborative_pc_control_default.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(up), "ALTER COLUMN control_mode SET DEFAULT 'COLLABORATIVE'") {
		t.Fatal("migration 45 must make new PC agents collaborative by default")
	}
	if !strings.Contains(string(down), "ALTER COLUMN control_mode SET DEFAULT 'SINGLE'") {
		t.Fatal("migration 45 down must restore the previous default")
	}
}

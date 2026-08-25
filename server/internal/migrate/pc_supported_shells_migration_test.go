package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestPCSupportedShellsMigrationIsFailClosed(t *testing.T) {
	up, err := migrations.Files.ReadFile("000019_pc_supported_shells.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000019_pc_supported_shells.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	upSQL := string(up)
	for _, contract := range []string{
		"supported_shells text[] NOT NULL DEFAULT ARRAY[]::text[]",
		"supported_shells <@ ARRAY['command-prompt', 'windows-powershell']::text[]",
		"cardinality(supported_shells) <= 8",
	} {
		if !strings.Contains(upSQL, contract) {
			t.Errorf("migration 19 up omits %q", contract)
		}
	}
	if !strings.Contains(string(down), "DROP COLUMN IF EXISTS supported_shells") {
		t.Fatal("migration 19 down does not remove the capability column")
	}
}

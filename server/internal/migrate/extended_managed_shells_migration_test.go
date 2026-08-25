package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestExtendedManagedShellsMigrationUpdatesCapabilitiesAndOperations(t *testing.T) {
	up, err := migrations.Files.ReadFile("000021_extended_managed_shells.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000021_extended_managed_shells.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	upSQL := string(up)
	for _, contract := range []string{
		"'command-prompt', 'windows-powershell', 'powershell-7', 'wsl'",
		"pcs_supported_shells_allowed",
		"pc_file_operations_shell_type_check",
		"cardinality(supported_shells) <= 8",
	} {
		if !strings.Contains(upSQL, contract) {
			t.Errorf("migration 21 up omits %q", contract)
		}
	}
	downSQL := string(down)
	for _, contract := range []string{
		"array_remove(array_remove(supported_shells, 'powershell-7'), 'wsl')",
		"cannot remove extended shell operations while records still exist",
		"shell_type IN ('command-prompt', 'windows-powershell')",
	} {
		if !strings.Contains(downSQL, contract) {
			t.Errorf("migration 21 down omits %q", contract)
		}
	}
}

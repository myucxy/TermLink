package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestGenericPCAgentMigrationIsExpandContractAndReversible(t *testing.T) {
	up, err := migrations.Files.ReadFile("000023_generic_pc_agent.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000023_generic_pc_agent.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	for _, contract := range []string{
		"'WINDOWS_PC', 'PC_AGENT', 'ANDROID'",
		"ADD COLUMN platform",
		"ADD COLUMN architecture",
		"ADD COLUMN platform_version",
		"ADD COLUMN capabilities",
		"cardinality(capabilities) <= 64",
		"realtime_terminal_v1",
		"file_operations_v1",
	} {
		if !strings.Contains(string(up), contract) {
			t.Errorf("migration 23 up omits %q", contract)
		}
	}
	for _, contract := range []string{
		"SET device_type = 'WINDOWS_PC'",
		"WHERE device_type = 'PC_AGENT'",
		"DROP COLUMN capabilities",
		"device_type IN ('WINDOWS_PC', 'ANDROID')",
	} {
		if !strings.Contains(string(down), contract) {
			t.Errorf("migration 23 down omits %q", contract)
		}
	}
}

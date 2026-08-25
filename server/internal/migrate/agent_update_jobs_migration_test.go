package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestAgentUpdateJobsMigrationKeepsRemoteUpdatesTransactional(t *testing.T) {
	up, err := migrations.Files.ReadFile("000022_agent_update_jobs.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000022_agent_update_jobs.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	for _, contract := range []string{
		"remote_agent_update_capable", "CREATE TABLE agent_update_jobs",
		"target_build > current_build", "artifact_sha256", "artifact_signer_fingerprint",
		"observed_agent_build = target_build", "observed_agent_build = current_build",
		"agent_update_jobs_one_active_per_pc", "ROLLBACK_FAILED", "VERIFYING_CONNECTION",
		"terminal_commands", "terminal_command_id", "jsonb_array_length(terminal_commands) <= 24",
	} {
		if !strings.Contains(string(up), contract) {
			t.Errorf("migration 22 up omits %q", contract)
		}
	}
	if !strings.Contains(string(down), "cannot be reversed while Agent update or terminal-command data exists") {
		t.Fatal("migration 22 down does not fail closed")
	}
}

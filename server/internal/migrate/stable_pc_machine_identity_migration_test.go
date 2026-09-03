package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestStablePCMachineIdentityMigrationIsScopedAndReversible(t *testing.T) {
	up, err := migrations.Files.ReadFile("000038_stable_pc_machine_identity.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000038_stable_pc_machine_identity.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	normalizedUp := strings.ToLower(string(up))
	for _, required := range []string{
		"add column machine_id_hash bytea",
		"octet_length(machine_id_hash) = 32",
		"on pcs(owner_user_id, machine_id_hash)",
		"where machine_id_hash is not null",
	} {
		if !strings.Contains(normalizedUp, required) {
			t.Fatalf("stable machine migration lacks %q", required)
		}
	}
	if strings.Contains(normalizedUp, "machineguid") {
		t.Fatal("migration must never persist the raw Windows MachineGuid")
	}
	normalizedDown := strings.ToLower(string(down))
	for _, required := range []string{
		"drop index if exists pcs_owner_machine_identity_unique",
		"drop constraint if exists pcs_machine_id_hash_length",
		"drop column if exists machine_id_hash",
	} {
		if !strings.Contains(normalizedDown, required) {
			t.Fatalf("stable machine rollback lacks %q", required)
		}
	}
}

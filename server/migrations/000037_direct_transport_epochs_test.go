package migrations

import (
	"io/fs"
	"strings"
	"testing"
)

func TestDirectTransportEpochMigrationIsAccountScopedCanonicalAndBounded(t *testing.T) {
	data, err := fs.ReadFile(Files, "000037_direct_transport_epochs.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	normalized := strings.ToLower(strings.Join(strings.Fields(string(data)), " "))
	for _, required := range []string{
		"account_id uuid not null references users(id) on delete cascade",
		"first_device_id uuid not null references devices(id) on delete cascade",
		"second_device_id uuid not null references devices(id) on delete cascade",
		"primary key (account_id, first_device_id, second_device_id)",
		"last_epoch between 1 and 9223372036854775807",
		"first_device_id <> second_device_id",
		"first_device_id::text < second_device_id::text",
		"direct_transport_pair_epochs_updated_idx",
	} {
		if !strings.Contains(normalized, required) {
			t.Fatalf("migration 37 lost constraint %q", required)
		}
	}
}

package migrate

import (
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestEmbeddedMigrationsAreOrderedAndStable(t *testing.T) {
	items, err := load(migrations.Files)
	if err != nil {
		t.Fatalf("load() error = %v", err)
	}
	if len(items) != 5 || items[0].version != 1 || items[0].name != "000001_production_core.up.sql" ||
		items[1].version != 2 || items[1].name != "000002_pairing_grants.up.sql" ||
		items[2].version != 3 || items[2].name != "000003_history_uploads.up.sql" ||
		items[3].version != 4 || items[3].name != "000004_pc_key_epoch.up.sql" ||
		items[4].version != 5 || items[4].name != "000005_billing_entitlements.up.sql" {
		t.Fatalf("unexpected migration set: %#v", items)
	}
	for _, item := range items {
		if len(item.checksum) != 64 {
			t.Fatalf("migration %d checksum length = %d", item.version, len(item.checksum))
		}
	}
}

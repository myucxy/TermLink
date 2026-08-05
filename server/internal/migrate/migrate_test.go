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
	if len(items) != 8 || items[0].version != 1 || items[0].name != "000001_production_core.up.sql" ||
		items[1].version != 2 || items[1].name != "000002_pairing_grants.up.sql" ||
		items[2].version != 3 || items[2].name != "000003_history_uploads.up.sql" ||
		items[3].version != 4 || items[3].name != "000004_pc_key_epoch.up.sql" ||
		items[4].version != 5 || items[4].name != "000005_billing_entitlements.up.sql" ||
		items[5].version != 6 || items[5].name != "000006_account_roles.up.sql" ||
		items[6].version != 7 || items[6].name != "000007_android_self_trust.up.sql" ||
		items[7].version != 8 || items[7].name != "000008_windows_bootstrap_fence.up.sql" {
		t.Fatalf("unexpected migration set: %#v", items)
	}
	for _, item := range items {
		if len(item.checksum) != 64 {
			t.Fatalf("migration %d checksum length = %d", item.version, len(item.checksum))
		}
	}
}

package migrate

import (
	"regexp"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

var postgresNamedObjectPattern = regexp.MustCompile(`(?i)\b(?:CONSTRAINT|INDEX)\s+(?:IF\s+(?:NOT\s+)?EXISTS\s+)?([a-z_][a-z0-9_]*)`)

func TestEmbeddedMigrationsAreOrderedAndStable(t *testing.T) {
	items, err := load(migrations.Files)
	if err != nil {
		t.Fatalf("load() error = %v", err)
	}
	if len(items) != 21 || items[0].version != 1 || items[0].name != "000001_production_core.up.sql" ||
		items[1].version != 2 || items[1].name != "000002_pairing_grants.up.sql" ||
		items[2].version != 3 || items[2].name != "000003_history_uploads.up.sql" ||
		items[3].version != 4 || items[3].name != "000004_pc_key_epoch.up.sql" ||
		items[4].version != 5 || items[4].name != "000005_billing_entitlements.up.sql" ||
		items[5].version != 6 || items[5].name != "000006_account_roles.up.sql" ||
		items[6].version != 7 || items[6].name != "000007_android_self_trust.up.sql" ||
		items[7].version != 8 || items[7].name != "000008_windows_bootstrap_fence.up.sql" ||
		items[8].version != 9 || items[8].name != "000009_unlimited_account_resources.up.sql" ||
		items[9].version != 10 || items[9].name != "000010_same_account_direct_access.up.sql" ||
		items[10].version != 11 || items[10].name != "000011_history_paging.up.sql" ||
		items[11].version != 12 || items[11].name != "000012_history_recipients.up.sql" ||
		items[12].version != 13 || items[12].name != "000013_history_recipient_backfill_hardening.up.sql" ||
		items[13].version != 14 || items[13].name != "000014_transcript_history.up.sql" ||
		items[14].version != 15 || items[14].name != "000015_session_automatic_cleanup.up.sql" ||
		items[15].version != 16 || items[15].name != "000016_transcript_publications.up.sql" ||
		items[16].version != 17 || items[16].name != "000017_pc_file_operations.up.sql" ||
		items[17].version != 18 || items[17].name != "000018_pc_file_operations_hardening.up.sql" ||
		items[18].version != 19 || items[18].name != "000019_pc_supported_shells.up.sql" ||
		items[19].version != 20 || items[19].name != "000020_session_snapshot_diagnostics.up.sql" ||
		items[20].version != 21 || items[20].name != "000021_extended_managed_shells.up.sql" {
		t.Fatalf("unexpected migration set: %#v", items)
	}
	if items[len(items)-1].version != migrations.LatestVersion {
		t.Fatalf("latest embedded migration = %d, runtime schema version = %d",
			items[len(items)-1].version, migrations.LatestVersion)
	}
	for _, item := range items {
		if len(item.checksum) != 64 {
			t.Fatalf("migration %d checksum length = %d", item.version, len(item.checksum))
		}
	}
}

func TestEmbeddedMigrationIdentifiersFitPostgresLimit(t *testing.T) {
	const postgresIdentifierLimit = 63
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatalf("ReadDir() error = %v", err)
	}
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		contents, err := migrations.Files.ReadFile(entry.Name())
		if err != nil {
			t.Fatalf("ReadFile(%q) error = %v", entry.Name(), err)
		}
		for _, match := range postgresNamedObjectPattern.FindAllSubmatch(contents, -1) {
			if len(match[1]) > postgresIdentifierLimit {
				t.Errorf("%s names PostgreSQL identifier %q with %d bytes; maximum is %d",
					entry.Name(), match[1], len(match[1]), postgresIdentifierLimit)
			}
		}
	}
}

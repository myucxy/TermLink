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
	if len(items) != 45 || items[0].version != 1 || items[0].name != "000001_production_core.up.sql" ||
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
		items[20].version != 21 || items[20].name != "000021_extended_managed_shells.up.sql" ||
		items[22].version != 23 || items[22].name != "000023_generic_pc_agent.up.sql" ||
		items[23].version != 24 || items[23].name != "000024_server_web_commerce.up.sql" ||
		items[24].version != 25 || items[24].name != "000025_control_privacy_reconciliation.up.sql" ||
		items[25].version != 26 || items[25].name != "000026_refund_approval.up.sql" ||
		items[26].version != 27 || items[26].name != "000027_account_deletion_identity.up.sql" ||
		items[27].version != 28 || items[27].name != "000028_terminal_command_file_operations.up.sql" ||
		items[28].version != 29 || items[28].name != "000029_agent_update_manual_reconciliation.up.sql" ||
		items[29].version != 30 || items[29].name != "000030_web_identity_and_email.up.sql" ||
		items[30].version != 31 || items[30].name != "000031_web_admin_route.up.sql" ||
		items[31].version != 32 || items[31].name != "000032_account_email_challenges.up.sql" ||
		items[32].version != 33 || items[32].name != "000033_web_announcements.up.sql" ||
		items[33].version != 34 || items[33].name != "000034_terminal_core_snapshot_v2.up.sql" ||
		items[34].version != 35 || items[34].name != "000035_invitation_identity_profiles.up.sql" ||
		items[35].version != 36 || items[35].name != "000036_human_verification_settings.up.sql" ||
		items[36].version != 37 || items[36].name != "000037_direct_transport_epochs.up.sql" ||
		items[37].version != 38 || items[37].name != "000038_stable_pc_machine_identity.up.sql" ||
		items[38].version != 39 || items[38].name != "000039_remove_server_terminal_history.up.sql" ||
		items[39].version != 40 || items[39].name != "000040_realtime_file_transfer.up.sql" ||
		items[40].version != 41 || items[40].name != "000041_remove_privacy_export_and_object_storage.up.sql" ||
		items[41].version != 42 || items[41].name != "000042_single_node_lease_fencing.up.sql" ||
		items[42].version != 43 || items[42].name != "000043_embedded_identity_provider.up.sql" ||
		items[43].version != 44 || items[43].name != "000044_file_operation_search.up.sql" ||
		items[44].version != 45 || items[44].name != "000045_collaborative_pc_control_default.up.sql" {
		t.Fatalf("unexpected migration set: %#v", items)
	}
	if items[len(items)-1].version != migrations.LatestVersion {
		t.Fatalf("latest embedded migration = %d, runtime schema version = %d",
			items[len(items)-1].version, migrations.LatestVersion)
	}
	if got := items[29].checksum; got != "f5436500adb0b8f173d029c70f5aca6a9a9b5703c454f4d2edf564c57f1d9c76" {
		t.Fatalf("immutable migration 30 checksum changed: %s", got)
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

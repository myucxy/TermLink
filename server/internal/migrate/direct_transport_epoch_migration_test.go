package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestDirectTransportEpochMigrationPersistsCanonicalPairCounter(t *testing.T) {
	up, err := migrations.Files.ReadFile("000037_direct_transport_epochs.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000037_direct_transport_epochs.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	text := string(up)
	for _, required := range []string{
		"CREATE TABLE direct_transport_pair_epochs",
		"account_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE",
		"first_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE",
		"second_device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE",
		"last_epoch bigint NOT NULL",
		"PRIMARY KEY (account_id, first_device_id, second_device_id)",
		"CHECK (first_device_id <> second_device_id)",
	} {
		if !strings.Contains(text, required) {
			t.Fatalf("direct transport epoch migration missing %q", required)
		}
	}
	if !strings.Contains(string(down), "DROP TABLE IF EXISTS direct_transport_pair_epochs") {
		t.Fatal("direct transport epoch down migration does not remove its table")
	}
}

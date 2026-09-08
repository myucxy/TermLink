package migrate

import (
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestFileOperationSearchMigrationContract(t *testing.T) {
	up, err := migrations.Files.ReadFile("000044_file_operation_search.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000044_file_operation_search.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{
		"ADD COLUMN list_query text NOT NULL DEFAULT ''",
		"octet_length(convert_to(list_query, 'UTF8')) <= 512",
		"char_length(list_query) <= 128",
		"operation = 'DOWNLOAD' AND list_offset IS NULL AND list_query = ''",
	} {
		if !strings.Contains(string(up), expected) {
			t.Errorf("migration 44 up omits %q", expected)
		}
	}
	if !strings.Contains(string(down), "DROP COLUMN list_query") ||
		strings.Contains(string(down), "list_query = ''") {
		t.Fatal("migration 44 down does not restore the pre-search request shape")
	}
}

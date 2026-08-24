package migrate

import (
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestPCFileOperationsMigrationIsDurableAndBounded(t *testing.T) {
	legacyUp, err := migrations.Files.ReadFile("000017_pc_file_operations.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	legacyDown, err := migrations.Files.ReadFile("000017_pc_file_operations.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	legacyDigest := sha256.Sum256(legacyUp)
	if got := hex.EncodeToString(legacyDigest[:]); got != "b17a3edfe32628c27011256f23e0b9192d377ec6b07164ea4f44836c3c355e5f" {
		t.Fatalf("immutable migration 17 checksum changed: %s", got)
	}
	legacySQL := string(legacyUp)
	for _, contract := range []string{
		"CREATE TABLE pc_file_operations", "requester_device_id", "lease_token_hash",
		"size_bytes BETWEEN 0 AND 8589934592", "(operation <> 'LIST' OR result IS NOT NULL)",
		"pc_file_operations_expiry_check CHECK (expires_at > created_at)",
	} {
		if !strings.Contains(legacySQL, contract) {
			t.Errorf("migration 17 up omits immutable contract %q", contract)
		}
	}
	if strings.Contains(legacySQL, "pc_file_operations_open_terminal_result_shape") {
		t.Fatal("immutable migration 17 unexpectedly contains migration 18 hardening")
	}
	if !strings.Contains(string(legacyDown), "cannot be reversed while PC file-operation metadata exists") {
		t.Fatal("migration 17 down does not fail closed")
	}

	hardeningUp, err := migrations.Files.ReadFile("000018_pc_file_operations_hardening.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	hardeningDown, err := migrations.Files.ReadFile("000018_pc_file_operations_hardening.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	upSQL := string(hardeningUp)
	for _, contract := range []string{
		"size_bytes BETWEEN 0 AND 5368709120", "pc_file_operations_terminal_shape",
		"(operation = 'DOWNLOAD' OR result IS NOT NULL)", "pc_file_operations_open_terminal_result_shape",
		"result->>'shell_type' = shell_type", "result->>'cwd' = path", "lease_expires_at <= expires_at",
	} {
		if !strings.Contains(upSQL, contract) {
			t.Errorf("migration 18 up omits %q", contract)
		}
	}
	if !strings.Contains(string(hardeningDown), "migration 18 cannot be reversed while PC file-operation metadata exists") {
		t.Fatal("migration 18 down does not fail closed")
	}
}

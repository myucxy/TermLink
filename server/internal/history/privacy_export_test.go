package history

import (
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestPrivacyExportObjectKeyIsStrictAndAccountScoped(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	key := PrivacyExportObjectKey(accountID, requestID, 7)
	if !validPrivacyExportObjectKey(key) {
		t.Fatalf("generated key rejected: %q", key)
	}
	for _, invalid := range []string{
		"accounts/../privacy-exports/" + requestID.String() + "/7.bin",
		"accounts/" + accountID.String() + "/sessions/" + requestID.String() + "/7.bin",
		"accounts/" + accountID.String() + "/privacy-exports/../" + requestID.String() + "/7.bin",
		"accounts/" + accountID.String() + "/privacy-exports/" + requestID.String() + "/0.bin",
		"accounts/" + accountID.String() + "/privacy-exports/" + requestID.String() + "/07.bin",
		"accounts/" + strings.ToUpper(accountID.String()) + "/privacy-exports/" + requestID.String() + "/7.bin",
	} {
		if validPrivacyExportObjectKey(invalid) {
			t.Fatalf("unsafe privacy export key accepted: %q", invalid)
		}
	}
}

func TestPrivacyExportLimitsAreDedicated(t *testing.T) {
	if MaxPrivacyExportBytes != 16<<20 || MaxPrivacyExportBytes >= MaxObjectBytes {
		t.Fatalf("privacy export limit = %d, want dedicated 16 MiB below history maximum", MaxPrivacyExportBytes)
	}
	key := PrivacyExportObjectKey(uuid.New(), uuid.New(), 1)
	digest := make([]byte, 32)
	if err := validatePrivacyExportObject(key, MaxPrivacyExportBytes, digest); err != nil {
		t.Fatalf("maximum privacy export rejected: %v", err)
	}
	for _, size := range []int64{0, MaxPrivacyExportBytes + 1} {
		if err := validatePrivacyExportObject(key, size, digest); err == nil {
			t.Fatalf("invalid privacy export size accepted: %d", size)
		}
	}
}

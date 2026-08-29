package store

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestUserPortalExportArtifactKeyBindsAccountRequestAndFence(t *testing.T) {
	accountID, requestID := uuid.New(), uuid.New()
	valid := "accounts/" + accountID.String() + "/privacy-exports/" + requestID.String() + "/9.bin"
	if !validUserPortalExportArtifactKey(valid, accountID, requestID, 9) {
		t.Fatal("valid artifact key was rejected")
	}
	for _, invalid := range []string{
		"accounts/" + uuid.NewString() + "/privacy-exports/" + requestID.String() + "/9.bin",
		"accounts/" + accountID.String() + "/privacy-exports/" + uuid.NewString() + "/9.bin",
		"accounts/" + accountID.String() + "/privacy-exports/" + requestID.String() + "/09.bin",
	} {
		if validUserPortalExportArtifactKey(invalid, accountID, requestID, 9) {
			t.Fatalf("mismatched artifact key accepted: %q", invalid)
		}
	}
	if validUserPortalExportArtifactKey(valid, accountID, requestID, 10) {
		t.Fatal("artifact key with stale processing fence was accepted")
	}
}

func TestUserPortalExportCleanupBatchIsBounded(t *testing.T) {
	if MaxUserPortalExportCleanupBatch < 1 || MaxUserPortalExportCleanupBatch > 32 {
		t.Fatalf("cleanup batch limit = %d", MaxUserPortalExportCleanupBatch)
	}
}

func TestUserPortalExportSnapshotQueriesExcludeSensitivePayloads(t *testing.T) {
	if len(userPortalExportSnapshotSQL) != len(userPortalExportSnapshotScopes) {
		t.Fatalf("snapshot query/scope mapping = %d/%d", len(userPortalExportSnapshotSQL), len(userPortalExportSnapshotScopes))
	}
	queries := strings.ToLower(strings.Join(userPortalExportSnapshotSQL, "\n"))
	for _, forbidden := range []string{
		"title_cipher", "object_key", "event_chunks", "snapshots", "transcript_pages",
		"provider_ref", "provider_reference", "payment_payload", "payload_ciphertext",
		"secret_refs", "signing_public_key", "exchange_public_key", "idempotency_key", "trace_id", "metadata",
	} {
		if strings.Contains(queries, forbidden) {
			t.Fatalf("snapshot query contains forbidden field/table %q", forbidden)
		}
	}
	for _, query := range userPortalExportSnapshotSQL {
		if !strings.Contains(query, "$1") {
			t.Fatalf("snapshot query is not account-scoped: %s", query)
		}
	}
}

func TestUserPortalExportSnapshotRejectsInvalidOrEmptyScopeBeforeDatabase(t *testing.T) {
	for _, scopes := range [][]string{nil, {"UNKNOWN"}} {
		if _, err := (&Postgres{}).UserPortalDataExportSnapshot(context.Background(), uuid.New(), scopes); !errors.Is(err, ErrDenied) {
			t.Fatalf("scope %v error = %v, want ErrDenied", scopes, err)
		}
	}
}

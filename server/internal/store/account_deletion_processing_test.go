package store

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestAccountDeletionProcessorLookupIsStrictlyFenced(t *testing.T) {
	t.Parallel()
	query := normalizePrivacySQL(userPortalAccountDeletionProcessingLookupSQL)
	for _, required := range []string{
		"request.id=$1", "request.processor_id=$2", "request.processing_fence=$3",
		"request.status='processing'", "request.processor_lease_until>now()", "for update",
	} {
		if !strings.Contains(query, required) {
			t.Fatalf("processor lookup lacks %q: %s", required, query)
		}
	}
}

func TestAccountDeletionClaimsRequireOpaqueIdentityHandle(t *testing.T) {
	t.Parallel()
	query := normalizePrivacySQL(userPortalAccountDeletionClaimSQL)
	for _, required := range []string{
		"identity_handle_cipher is not null", "identity_handle_key_version is not null",
		"request.identity_handle_cipher", "request.identity_handle_key_version",
	} {
		if !strings.Contains(query, required) {
			t.Fatalf("account deletion claim lacks %q: %s", required, query)
		}
	}
	encoded, err := json.Marshal(UserPortalAccountDeletionJob{
		IdentityHandle:           []byte("opaque-ciphertext"),
		IdentityHandleKeyVersion: 7,
	})
	if err != nil || string(encoded) != "{}" {
		t.Fatalf("identity handle leaked through job JSON: %s error=%v", encoded, err)
	}
}

func TestAccountDeletionFinalizationPurgesPrivateRecords(t *testing.T) {
	t.Parallel()
	purge := normalizePrivacySQL(strings.Join(userPortalAccountDeletionPurgePrivateStatements, " "))
	for _, required := range []string{
		"delete from account_identity_profiles", "delete from invitation_redemptions", "delete from idempotency_keys",
		"delete from pcs", "delete from devices",
	} {
		if !strings.Contains(purge, required) {
			t.Fatalf("private-row purge lacks %q: %s", required, purge)
		}
	}
	for _, removed := range []string{"orders", "payment_transactions", "payment_notifications", "refunds", "redemption_claims", "entitlement_grants", "subscriptions", "entitlements", "quota_selections"} {
		if strings.Contains(purge, removed) {
			t.Fatalf("retired commercial table is still referenced by %q", removed)
		}
	}
	anonymize := normalizePrivacySQL(userPortalAccountDeletionAnonymizeUserSQL)
	for _, required := range []string{"oidc_subject_hash=$2", "status='deleted'", "is_admin=false", "role='user'"} {
		if !strings.Contains(anonymize, required) {
			t.Fatalf("account anonymization lacks %q: %s", required, anonymize)
		}
	}
}

func TestDeletedIdentityDigestIsStableAndRequestBound(t *testing.T) {
	t.Parallel()
	accountID := uuid.New()
	firstRequest, secondRequest := uuid.New(), uuid.New()
	first := userPortalDeletedIdentityDigest(accountID, firstRequest)
	if first != userPortalDeletedIdentityDigest(accountID, firstRequest) {
		t.Fatal("deleted identity digest is not retry-stable")
	}
	if first == userPortalDeletedIdentityDigest(accountID, secondRequest) {
		t.Fatal("deleted identity digest is not request-bound")
	}
}

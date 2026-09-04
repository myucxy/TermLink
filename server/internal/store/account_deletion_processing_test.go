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

func TestAccountDeletionObjectDiscoveryIsAccountScoped(t *testing.T) {
	t.Parallel()
	query := normalizePrivacySQL(userPortalAccountDeletionObjectsSQL)
	for _, required := range []string{
		"pc.owner_user_id=$1",
		"upload.user_id=$1",
		"operation.account_id=$1",
		"request.account_id=$1",
		"'privacy_export'",
	} {
		if !strings.Contains(query, required) {
			t.Fatalf("object discovery lacks %q: %s", required, query)
		}
	}
	for _, forbidden := range []string{"audit_events", "orders", "payment_payload", "provider_trade_no"} {
		if strings.Contains(query, forbidden) {
			t.Fatalf("object discovery reads forbidden data %q", forbidden)
		}
	}
}

func TestAccountDeletionFinalizationRedactsLegacyCommercialAndPurgesPrivateRecords(t *testing.T) {
	t.Parallel()
	redaction := normalizePrivacySQL(strings.Join(userPortalAccountDeletionRedactLegacyCommercialStatements, " "))
	for _, required := range []string{
		"update orders", "idempotency_key='deleted-'", "update payment_transactions",
		"payment_payload='{}'::jsonb", "update payment_notifications",
		"payload_ciphertext=null", "update refunds", "reason='account_deleted'",
	} {
		if !strings.Contains(redaction, required) {
			t.Fatalf("legacy commercial redaction lacks %q: %s", required, redaction)
		}
	}
	purge := normalizePrivacySQL(strings.Join(userPortalAccountDeletionPurgePrivateStatements, " "))
	for _, required := range []string{
		"delete from redemption_claims", "delete from entitlement_grants",
		"delete from subscriptions", "delete from entitlements", "delete from quota_selections",
		"delete from invitation_redemptions", "delete from idempotency_keys",
		"delete from data_export_requests", "delete from pcs", "delete from devices",
	} {
		if !strings.Contains(purge, required) {
			t.Fatalf("private-row purge lacks %q: %s", required, purge)
		}
	}
	for _, retained := range []string{"delete from orders", "delete from payment_transactions", "delete from refunds", "delete from audit_events"} {
		if strings.Contains(purge, retained) {
			t.Fatalf("retained legal record is destructively removed by %q", retained)
		}
	}
	anonymize := normalizePrivacySQL(userPortalAccountDeletionAnonymizeUserSQL)
	for _, required := range []string{"oidc_subject_hash=$2", "status='deleted'", "is_admin=false", "role='user'"} {
		if !strings.Contains(anonymize, required) {
			t.Fatalf("account anonymization lacks %q: %s", required, anonymize)
		}
	}
}

func TestAccountDeletionObjectCapabilitiesAreScopedAndNotSerializable(t *testing.T) {
	t.Parallel()
	accountID := uuid.MustParse("10000000-0000-4000-8000-000000000001")
	sessionID := uuid.MustParse("20000000-0000-4000-8000-000000000002")
	objectID := uuid.MustParse("30000000-0000-4000-8000-000000000003")
	stored := UserPortalAccountDeletionObject{
		Kind:      UserPortalAccountDeletionStoredObject,
		ObjectKey: "accounts/" + accountID.String() + "/sessions/" + sessionID.String() + "/chunk/" + objectID.String() + ".bin",
	}
	if !validUserPortalAccountDeletionObject(accountID, objectID, 0, stored) {
		t.Fatal("valid account-scoped stored object was rejected")
	}
	exportRequestID := uuid.MustParse("40000000-0000-4000-8000-000000000004")
	privacy := UserPortalAccountDeletionObject{
		Kind:      UserPortalAccountDeletionPrivacyExport,
		ObjectKey: "accounts/" + accountID.String() + "/privacy-exports/" + exportRequestID.String() + "/7.bin",
	}
	if !validUserPortalAccountDeletionObject(accountID, exportRequestID, 7, privacy) {
		t.Fatal("valid account-scoped privacy export was rejected")
	}
	for _, invalid := range []UserPortalAccountDeletionObject{
		{Kind: UserPortalAccountDeletionStoredObject, ObjectKey: "accounts/" + uuid.NewString() + "/sessions/x/chunk/y.bin"},
		{Kind: UserPortalAccountDeletionStoredObject, ObjectKey: "accounts/" + accountID.String() + "/../other.bin"},
		{Kind: "UNKNOWN", ObjectKey: stored.ObjectKey},
	} {
		if validUserPortalAccountDeletionObject(accountID, objectID, 0, invalid) {
			t.Fatalf("unsafe object capability accepted: kind=%s", invalid.Kind)
		}
	}
	encoded, err := json.Marshal(stored)
	if err != nil || string(encoded) != "{}" {
		t.Fatalf("object capability serialized as %s error=%v", encoded, err)
	}
}

func TestAccountDeletionObjectSetMustMatchExactly(t *testing.T) {
	t.Parallel()
	accountID := uuid.New()
	first := UserPortalAccountDeletionObject{
		Kind:      UserPortalAccountDeletionStoredObject,
		ObjectKey: "accounts/" + accountID.String() + "/sessions/a/chunk/one.bin",
	}
	second := UserPortalAccountDeletionObject{
		Kind:      UserPortalAccountDeletionPrivacyExport,
		ObjectKey: "accounts/" + accountID.String() + "/privacy-exports/b/1.bin",
	}
	if !sameUserPortalAccountDeletionObjects(accountID, []UserPortalAccountDeletionObject{first, second}, []UserPortalAccountDeletionObject{second, first}) {
		t.Fatal("same object set in another order was rejected")
	}
	if sameUserPortalAccountDeletionObjects(accountID, []UserPortalAccountDeletionObject{first, second}, []UserPortalAccountDeletionObject{first}) {
		t.Fatal("incomplete deleted-object set was accepted")
	}
	if sameUserPortalAccountDeletionObjects(accountID, []UserPortalAccountDeletionObject{first, second}, []UserPortalAccountDeletionObject{first, first}) {
		t.Fatal("duplicated deleted-object capability was accepted")
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

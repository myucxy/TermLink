package store

import (
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestUserPortalPrivacyQueriesAreAccountScoped(t *testing.T) {
	t.Parallel()
	contracts := map[string]struct {
		query string
		scope string
	}{
		"export active":            {userPortalDataExportActiveSQL, "account_id=$1"},
		"export lookup":            {userPortalDataExportByAccountSQL, "account_id=$2"},
		"deletion active":          {userPortalAccountDeletionActiveSQL, "account_id=$1"},
		"deletion expiration":      {userPortalAccountDeletionExpireSQL, "account_id=$1"},
		"deletion lookup":          {userPortalAccountDeletionByAccountSQL, "account_id=$2"},
		"deletion identity lookup": {userPortalAccountDeletionIdentityLookupSQL, "account_id=$2"},
		"deletion identity bind":   {userPortalAccountDeletionIdentityBindSQL, "account_id=$2"},
		"deletion cancel":          {userPortalAccountDeletionCancelSQL, "account_id=$2"},
		"deletion confirmation":    {userPortalAccountDeletionConfirmSQL, "account_id=$2"},
		"PC policy update":         {userPortalFullPCPolicyUpdateSQL, "owner_user_id=$2"},
	}
	for name, contract := range contracts {
		contract := contract
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(normalizePrivacySQL(contract.query), strings.ToLower(contract.scope)) {
				t.Fatalf("query lacks account ownership predicate %q: %s", contract.scope, contract.query)
			}
		})
	}
}

func TestUserPortalAccountDeletionIdentityBindingFailsClosed(t *testing.T) {
	t.Parallel()
	bind := normalizePrivacySQL(userPortalAccountDeletionIdentityBindSQL)
	for _, required := range []string{
		"status='requested'", "confirmation_expires_at>now()",
		"identity_handle_cipher is null", "identity_handle_key_version is null",
	} {
		if !strings.Contains(bind, required) {
			t.Fatalf("identity binding lacks %q: %s", required, bind)
		}
	}
	for name, query := range map[string]string{
		"confirm": normalizePrivacySQL(userPortalAccountDeletionConfirmSQL),
		"claim":   normalizePrivacySQL(userPortalAccountDeletionClaimSQL),
	} {
		for _, required := range []string{"identity_handle_cipher is not null", "identity_handle_key_version is not null"} {
			if !strings.Contains(query, required) {
				t.Fatalf("%s lacks %q: %s", name, required, query)
			}
		}
	}
	audit := normalizePrivacySQL(userPortalAccountDeletionIdentityAuditSQL)
	for _, forbidden := range []string{"identity_handle_cipher", "keycloak_subject", "provider_subject"} {
		if strings.Contains(audit, forbidden) {
			t.Fatalf("identity binding audit persists forbidden %q: %s", forbidden, audit)
		}
	}
}

func TestUserPortalPCPolicyUsesOptimisticVersioning(t *testing.T) {
	t.Parallel()
	query := normalizePrivacySQL(userPortalFullPCPolicyUpdateSQL)
	for _, required := range []string{
		"policy_version=policy_version+1",
		"policy_version=$6",
		"returning name,remote_routing_enabled,control_mode,local_priority,policy_version",
	} {
		if !strings.Contains(query, required) {
			t.Fatalf("PC policy update lacks %q: %s", required, query)
		}
	}

	accountID := uuid.MustParse("10000000-0000-4000-8000-000000000001")
	pcID := uuid.MustParse("20000000-0000-4000-8000-000000000002")
	first := userPortalPrivacyActionHash("pc-control-policy-v1", accountID.String(), pcID.String(), "true", "SINGLE", "true", "1")
	modeChanged := userPortalPrivacyActionHash("pc-control-policy-v1", accountID.String(), pcID.String(), "true", "COLLABORATIVE", "true", "1")
	versionChanged := userPortalPrivacyActionHash("pc-control-policy-v1", accountID.String(), pcID.String(), "true", "SINGLE", "true", "2")
	if first == modeChanged || first == versionChanged || modeChanged == versionChanged {
		t.Fatal("PC policy request hash does not bind mode and expected version")
	}
}

func TestUserPortalPrivacyClaimsAreLeasedAndFenced(t *testing.T) {
	t.Parallel()
	for name, query := range map[string]string{
		"data export":      userPortalDataExportClaimSQL,
		"account deletion": userPortalAccountDeletionClaimSQL,
	} {
		query := normalizePrivacySQL(query)
		t.Run(name, func(t *testing.T) {
			for _, required := range []string{
				"for update skip locked",
				"processor_lease_until",
				"processing_fence=request.processing_fence+1",
				"attempt_count=request.attempt_count+1",
				"version=request.version+1",
				"limit $3",
			} {
				if !strings.Contains(query, required) {
					t.Fatalf("claim SQL lacks %q: %s", required, query)
				}
			}
		})
	}
}

func TestUserPortalPrivacyProcessorWritesRequireCurrentLeaseAndFence(t *testing.T) {
	t.Parallel()
	for name, query := range map[string]string{
		"export complete":   userPortalDataExportCompleteSQL,
		"export fail":       userPortalDataExportFailSQL,
		"deletion complete": userPortalAccountDeletionCompleteSQL,
		"deletion fail":     userPortalAccountDeletionFailSQL,
	} {
		query := normalizePrivacySQL(query)
		t.Run(name, func(t *testing.T) {
			for _, required := range []string{
				"processor_id=$2", "processing_fence=$3", "status='processing'", "processor_lease_until>now()",
			} {
				if !strings.Contains(query, required) {
					t.Fatalf("processor finalization lacks %q: %s", required, query)
				}
			}
		})
	}

	for name, query := range map[string]string{
		"export complete replay":   userPortalDataExportCompleteReplaySQL,
		"export fail replay":       userPortalDataExportFailReplaySQL,
		"deletion complete replay": userPortalAccountDeletionCompleteReplaySQL,
		"deletion fail replay":     userPortalAccountDeletionFailReplaySQL,
	} {
		query := normalizePrivacySQL(query)
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(query, "processing_fence=$2") {
				t.Fatalf("idempotent replay is not bound to the processing fence: %s", query)
			}
		})
	}
}

func TestUserPortalPrivacyValidationBounds(t *testing.T) {
	t.Parallel()
	if !validUserPortalPrivacyLease(5*time.Second) || !validUserPortalPrivacyLease(10*time.Minute) {
		t.Fatal("documented lease bounds were rejected")
	}
	for _, lease := range []time.Duration{0, 5*time.Second - time.Nanosecond, 10*time.Minute + time.Nanosecond} {
		if validUserPortalPrivacyLease(lease) {
			t.Fatalf("unsafe lease accepted: %s", lease)
		}
	}
	for _, code := range []string{"EXPORT_FAILED", "OBJECT_9_UNAVAILABLE", "A"} {
		if !validUserPortalErrorCode(code) {
			t.Fatalf("valid stable error code rejected: %q", code)
		}
	}
	for _, code := range []string{"", "lowercase", "HAS-DASH", strings.Repeat("A", 129)} {
		if validUserPortalErrorCode(code) {
			t.Fatalf("unsafe error code accepted: %q", code)
		}
	}
}

func TestUserPortalDataExportArtifactValidation(t *testing.T) {
	t.Parallel()
	valid := UserPortalDataExportCompletion{
		ArtifactObjectKey: "privacy/account/request.json",
		ArtifactSHA256:    make([]byte, userPortalSHA256DigestSize),
		ArtifactSizeBytes: 0,
		ExpiresAt:         time.Now().Add(time.Hour),
	}
	if !validUserPortalArtifact(valid) {
		t.Fatal("valid controlled object key was rejected")
	}

	invalidKeys := []string{
		"", "/privacy/export.json", ".hidden/export.json", "privacy\\export.json",
		"privacy/../export.json", "privacy//export.json", "privacy/导出.json",
	}
	for _, key := range invalidKeys {
		candidate := valid
		candidate.ArtifactObjectKey = key
		if validUserPortalArtifact(candidate) {
			t.Fatalf("unsafe object key accepted: %q", key)
		}
	}
	badDigest := valid
	badDigest.ArtifactSHA256 = make([]byte, userPortalSHA256DigestSize-1)
	if validUserPortalArtifact(badDigest) {
		t.Fatal("artifact with malformed SHA-256 digest was accepted")
	}
	pastExpiry := valid
	pastExpiry.ExpiresAt = time.Now().Add(-time.Second)
	if validUserPortalArtifact(pastExpiry) {
		t.Fatal("artifact with expired retention deadline was accepted")
	}
}

func TestUserPortalPrivacyAuditAndOutboxContracts(t *testing.T) {
	t.Parallel()
	for _, query := range []string{
		userPortalDataExportAuditSQL,
		userPortalAccountDeletionRequestAuditSQL,
		userPortalAccountDeletionCancelAuditSQL,
		userPortalAccountDeletionExpireAuditSQL,
		userPortalAccountDeletionIdentityAuditSQL,
		userPortalAccountDeletionConfirmAuditSQL,
		userPortalFullPCPolicyAuditSQL,
		userPortalDataExportProcessorAuditSQL,
		userPortalAccountDeletionProcessorAuditSQL,
	} {
		assertUserPortalActionContains(t, query, "insert into audit_events", "trace_id")
	}
	for _, query := range []string{
		userPortalDataExportOutboxSQL,
		userPortalAccountDeletionOutboxSQL,
		userPortalFullPCPolicyOutboxSQL,
		userPortalDataExportProcessorOutboxSQL,
	} {
		assertUserPortalActionContains(t, query, "insert into outbox", "aggregate_id", "payload")
	}
}

func TestUserPortalPrivacyDTOsExcludeSensitiveMaterial(t *testing.T) {
	t.Parallel()
	values := []any{
		UserPortalDataExport{},
		UserPortalAccountDeletion{},
		UserPortalPCControlPolicy{},
		UserPortalDataExportJob{},
		UserPortalAccountDeletionJob{},
	}
	for _, value := range values {
		typeOf := reflect.TypeOf(value)
		for index := 0; index < typeOf.NumField(); index++ {
			name := strings.ToLower(typeOf.Field(index).Name)
			for _, forbidden := range []string{"terminal", "cipher", "secret", "provider", "rawreference", "content"} {
				if strings.Contains(name, forbidden) {
					t.Fatalf("%s exposes sensitive field %s", typeOf.Name(), typeOf.Field(index).Name)
				}
			}
		}
	}
}

func normalizePrivacySQL(query string) string {
	return strings.ToLower(strings.Join(strings.Fields(query), " "))
}

package store

import (
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestUserPortalActionLimitIsBounded(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		input int
		want  int
	}{{0, 50}, {-1, 50}, {1, 1}, {100, 100}, {101, 100}} {
		if got := normalizeUserPortalActionLimit(test.input); got != test.want {
			t.Fatalf("normalizeUserPortalActionLimit(%d)=%d, want %d", test.input, got, test.want)
		}
	}
}

func TestUserPortalActionDTOsAreMinimal(t *testing.T) {
	t.Parallel()
	assertUserPortalActionFields(t, UserPortalActionResult{}, "Changed")
	assertUserPortalActionFields(t, UserPortalPCControlPolicy{}, "PCID", "Name", "RemoteRoutingEnabled", "Mode", "LocalPriority", "PolicyVersion")
	assertUserPortalActionFields(t, UserPortalAccountDeletionPreflight{},
		"AccountID", "Status", "ActiveDevices", "LiveSessions", "Ready", "RequestSupported")
}

func TestUserPortalActionSQLIsAccountScoped(t *testing.T) {
	t.Parallel()
	for name, contract := range map[string]struct {
		query string
		scope string
	}{
		"account":          {userPortalActionAccountSQL, "id=$1"},
		"idempotency":      {userPortalActionIdempotencyInsertSQL, "user_id"},
		"device lookup":    {userPortalRevokeDeviceLookupSQL, "user_id=$2"},
		"device revoke":    {userPortalRevokeDeviceSQL, "user_id=$2"},
		"device PCs":       {userPortalRevokeDevicePCsSQL, "owner_user_id=$2"},
		"device grants":    {userPortalDeleteDeviceGrantsSQL, "owner_user_id=$2"},
		"device leases":    {userPortalDeleteDeviceLeasesSQL, "owner_user_id=$2"},
		"policy list":      {userPortalPCPoliciesSQL, "owner_user_id=$1"},
		"policy lookup":    {userPortalPCLookupSQL, "owner_user_id=$2"},
		"policy update":    {userPortalPCPolicyUpdateSQL, "owner_user_id=$2"},
		"delete preflight": {userPortalAccountDeletionPreflightSQL, "account.id=$1"},
	} {
		contract := contract
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(strings.ToLower(contract.query), strings.ToLower(contract.scope)) {
				t.Fatalf("query lacks ownership scope %q: %s", contract.scope, contract.query)
			}
		})
	}
}

func TestUserPortalActionIdempotencyContract(t *testing.T) {
	t.Parallel()
	insert := strings.ToLower(userPortalActionIdempotencyInsertSQL)
	read := strings.ToLower(userPortalActionIdempotencyReadSQL)
	complete := strings.ToLower(userPortalActionIdempotencyCompleteSQL)
	for _, required := range []string{"on conflict(user_id,idempotency_key)", "request_hash=excluded.request_hash", "expires_at<now()"} {
		if !strings.Contains(insert, required) {
			t.Fatalf("idempotency insert lacks %q", required)
		}
	}
	for _, query := range []string{read, complete} {
		if !strings.Contains(query, "user_id=$1") || !strings.Contains(query, "idempotency_key=$2") {
			t.Fatalf("idempotency query lacks account/key scope: %s", query)
		}
	}
	first := userPortalActionHash("operation", []uuid.UUID{uuid.MustParse("00000000-0000-4000-8000-000000000001")}, true)
	second := userPortalActionHash("operation", []uuid.UUID{uuid.MustParse("00000000-0000-4000-8000-000000000001")}, false)
	if first == second {
		t.Fatal("different action bodies produced the same request hash")
	}
}

func TestUserPortalActionsAuditAndOutboxAreTransactionalContracts(t *testing.T) {
	t.Parallel()
	assertUserPortalActionContains(t, userPortalDeviceRevokedAuditSQL, "user_portal.device.revoke", "user_id", "target_id")
	assertUserPortalActionContains(t, userPortalDeviceRevokedOutboxSQL, "device.revoked", "user_id", "device_id")
	assertUserPortalActionContains(t, userPortalPCPolicyAuditSQL, "user_portal.pc.remote_routing.update", "user_id", "target_id")
	assertUserPortalActionContains(t, userPortalPCPolicyOutboxSQL, "grant.updated", "pc_id", "remote_routing_enabled")
}

func TestUserPortalActionSQLDoesNotReadSensitiveData(t *testing.T) {
	t.Parallel()
	queries := []string{
		userPortalActionAccountSQL, userPortalActionIdempotencyReadSQL,
		userPortalRevokeDeviceLookupSQL, userPortalPCPoliciesSQL,
		userPortalPCLookupSQL, userPortalAccountDeletionPreflightSQL,
	}
	for _, query := range queries {
		query = strings.ToLower(query)
		for _, forbidden := range []string{
			"title_cipher", "last_output_seq", "object_key", "ciphertext",
			"payment_payload", "provider_trade_no", "provider_ref", "secret_refs",
			"terminal_commands", "audit_events.metadata", "trace_id", "ip_hash",
		} {
			if strings.Contains(query, forbidden) {
				t.Fatalf("read query contains forbidden field %q: %s", forbidden, query)
			}
		}
	}
}

func TestUnsupportedUserPortalActionsRequireMigrations(t *testing.T) {
	t.Parallel()
	wrapped := errors.Join(ErrUserPortalActionUnsupported, errors.New("migration required"))
	if !errors.Is(wrapped, ErrUserPortalActionUnsupported) {
		t.Fatal("unsupported sentinel is not usable with errors.Is")
	}
	for _, required := range []string{"account_deletion_requests", "per-PC"} {
		if !strings.Contains(UserPortalActionMigrationGuidance, required) {
			t.Fatalf("migration guidance lacks %q", required)
		}
	}
}

func assertUserPortalActionContains(t *testing.T, query string, values ...string) {
	t.Helper()
	query = strings.ToLower(query)
	for _, value := range values {
		if !strings.Contains(query, strings.ToLower(value)) {
			t.Fatalf("query lacks %q: %s", value, query)
		}
	}
}

func assertUserPortalActionFields(t *testing.T, value any, expected ...string) {
	t.Helper()
	typeOf := reflect.TypeOf(value)
	if typeOf.NumField() != len(expected) {
		t.Fatalf("%s has %d fields, want %d", typeOf.Name(), typeOf.NumField(), len(expected))
	}
	for index, name := range expected {
		if got := typeOf.Field(index).Name; got != name {
			t.Fatalf("%s field %d=%s, want %s", typeOf.Name(), index, got, name)
		}
	}
}

package store

import (
	"reflect"
	"strings"
	"testing"
)

func TestNormalizeCommerceRecoveryLimit(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name string
		in   int
		want int
	}{
		{name: "zero uses default", in: 0, want: commerceRecoveryDefaultLimit},
		{name: "negative uses default", in: -1, want: commerceRecoveryDefaultLimit},
		{name: "one retained", in: 1, want: 1},
		{name: "maximum retained", in: commerceRecoveryMaxLimit, want: commerceRecoveryMaxLimit},
		{name: "over maximum clamps", in: commerceRecoveryMaxLimit + 1, want: commerceRecoveryMaxLimit},
	} {
		t.Run(test.name, func(t *testing.T) {
			if got := normalizeCommerceRecoveryLimit(test.in); got != test.want {
				t.Fatalf("normalizeCommerceRecoveryLimit(%d)=%d, want %d", test.in, got, test.want)
			}
		})
	}
}

func TestCommerceRecoveryCandidateDTOsAreMinimal(t *testing.T) {
	t.Parallel()
	assertCommerceRecoveryFields(t, CommercePendingPaymentCandidate{},
		"OrderID", "AccountID", "Provider", "PaymentScene", "Status", "ExpiresAt", "UpdatedAt")
	assertCommerceRecoveryFields(t, CommerceFulfillmentCandidate{}, "OrderID", "Status", "UpdatedAt")
	assertCommerceRecoveryFields(t, CommerceRefundCandidate{}, "RefundID", "OrderID", "Provider", "Status", "UpdatedAt")
	assertCommerceRecoveryFields(t, CommerceReconciliationCandidate{}, "RunID", "Provider", "PeriodStart", "PeriodEnd", "CreatedAt")
	assertCommerceRecoveryFields(t, CommerceConsistencyCounts{},
		"OrdersWithoutCompatibleTransaction", "SettledTransactionsWithUnsettledOrder",
		"CompletedOrdersWithoutGrant", "RefundedOrdersWithLiveGrant")
}

func TestCommerceRecoveryCandidateSQLIsBoundedStableAndReadOnly(t *testing.T) {
	t.Parallel()
	queries := map[string]struct {
		query   string
		orderBy string
	}{
		"pending payment": {stalePendingPaymentCandidatesSQL, "order by orders.updated_at,orders.id"},
		"fulfillment":     {pendingFulfillmentCandidatesSQL, "order by orders.updated_at,orders.id"},
		"refund":          {pendingRefundCandidatesSQL, "order by refunds.updated_at,refunds.id"},
		"reconciliation":  {pendingReconciliationCandidatesSQL, "order by reconciliation_runs.created_at,reconciliation_runs.id"},
	}
	for name, contract := range queries {
		query := strings.ToLower(contract.query)
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(query, contract.orderBy) || !strings.Contains(query, "limit $2") {
				t.Fatalf("candidate query lacks stable bounded ordering: %s", query)
			}
			if !strings.Contains(query, "<=$1") {
				t.Fatalf("candidate query lacks caller-supplied age cutoff: %s", query)
			}
			assertCommerceRecoverySQLReadOnlyAndSafe(t, query)
		})
	}
}

func TestCommerceRecoverySQLSelectsOnlyRecoverableStates(t *testing.T) {
	t.Parallel()
	assertContainsAll(t, stalePendingPaymentCandidatesSQL, "status='PENDING_PAYMENT'")
	assertContainsAll(t, pendingFulfillmentCandidatesSQL, "status IN ('PAID','FULFILLING')")
	assertContainsAll(t, pendingRefundCandidatesSQL, "status IN ('CREATED','PENDING')")
	assertContainsAll(t, pendingReconciliationCandidatesSQL, "status='PENDING'")
}

func TestCommerceConsistencySQLIsReadOnlyAndCoversTransactionsAndGrants(t *testing.T) {
	t.Parallel()
	query := strings.ToLower(commerceConsistencyCountsSQL)
	assertCommerceRecoverySQLReadOnlyAndSafe(t, query)
	for _, required := range []string{"payment_transactions", "entitlement_grants", "source_id=orders.id", "account_id=orders.account_id"} {
		if !strings.Contains(query, required) {
			t.Fatalf("consistency query lacks %q", required)
		}
	}
}

func assertCommerceRecoverySQLReadOnlyAndSafe(t *testing.T, query string) {
	t.Helper()
	query = strings.ToLower(query)
	for _, forbidden := range []string{
		" for update", "skip locked", "insert ", "update ", "delete ",
		"payment_payload", "provider_trade_no", "provider_ref", "secret",
		"ciphertext", "terminal", "title_cipher", "last_output_seq",
		"metadata", "result_summary", "refunds.reason",
	} {
		if strings.Contains(query, forbidden) {
			t.Fatalf("query contains forbidden token %q", forbidden)
		}
	}
}

func assertContainsAll(t *testing.T, query string, values ...string) {
	t.Helper()
	for _, value := range values {
		if !strings.Contains(query, value) {
			t.Fatalf("query lacks %q: %s", value, query)
		}
	}
}

func assertCommerceRecoveryFields(t *testing.T, value any, expected ...string) {
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

package store

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestAdminOrderCloseValidationAndSQLContracts(t *testing.T) {
	for _, valid := range []string{"CUSTOMER_REQUEST", "PAYMENT_ABANDONED", "risk_review_1"} {
		if !ValidAdminOrderCloseReasonCode(valid) {
			t.Errorf("valid reason code %q rejected", valid)
		}
	}
	for _, invalid := range []string{"", "AB", "contains space", "SECRET:provider-token", strings.Repeat("A", 65)} {
		if ValidAdminOrderCloseReasonCode(invalid) {
			t.Errorf("invalid reason code %q accepted", invalid)
		}
	}
	for _, valid := range []string{"12345678", "admin-close-order-01"} {
		if !validAdminCloseIdempotencyKey(valid) {
			t.Errorf("valid idempotency key %q rejected", valid)
		}
	}
	for _, invalid := range []string{"short", " leading-space", "line\nbreak", strings.Repeat("x", 129)} {
		if validAdminCloseIdempotencyKey(invalid) {
			t.Errorf("invalid idempotency key %q accepted", invalid)
		}
	}

	closeSQL := strings.ToLower(adminCloseOrderSQL)
	for _, required := range []string{
		"update orders", "status='closed'", "status in ('created','pending_payment')",
		"not exists", "payment_transactions", "status in ('succeeded','refunded')", "returning",
	} {
		if !strings.Contains(closeSQL, required) {
			t.Errorf("admin close SQL is missing %q", required)
		}
	}
	transactionSQL := strings.ToLower(adminCloseTransactionsSQL)
	for _, required := range []string{"update payment_transactions", "status='closed'", "status in ('initiated','pending')", "admin_order_closed"} {
		if !strings.Contains(transactionSQL, required) {
			t.Errorf("transaction close SQL is missing %q", required)
		}
	}
	outboxSQL := strings.ToLower(adminCloseOutboxSQL)
	for _, required := range []string{"insert into outbox", "commerce.order.closed", "order_id", "account_id", "status"} {
		if !strings.Contains(outboxSQL, required) {
			t.Errorf("admin close outbox SQL is missing %q", required)
		}
	}
	for _, forbidden := range []string{"reason", "provider", "payment_payload", "idempotency"} {
		if strings.Contains(outboxSQL, forbidden) {
			t.Errorf("admin close outbox exposes %q", forbidden)
		}
	}
}

func TestAdminCloseReceiptContainsOnlyStableState(t *testing.T) {
	receipt, err := json.Marshal(adminCloseReceipt{OrderID: uuid.New(), Status: "CLOSED"})
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"provider", "payment", "reason", "idempotency", "account"} {
		if strings.Contains(strings.ToLower(string(receipt)), forbidden) {
			t.Fatalf("idempotency receipt exposes %q: %s", forbidden, receipt)
		}
	}
}

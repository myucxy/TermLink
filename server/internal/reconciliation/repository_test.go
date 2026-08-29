package reconciliation

import (
	"bytes"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
)

func TestRepositorySQLMatchesMigration25Contract(t *testing.T) {
	compact := strings.Join(strings.Fields(strings.ToLower(localEntriesSQL)), " ")
	for _, clause := range []string{
		"select row_id,order_id,transaction_id,kind,order_no,refund_no,reference_hash,amount_minor,currency,status,occurred_at",
		"t.provider_trade_no_hash as reference_hash",
		"r.provider_ref_hash as reference_hash",
		"where not $4 or (occurred_at,kind,row_id) > ($5,$6,$7)",
		"order by occurred_at,kind,row_id",
		"limit $8",
	} {
		if !strings.Contains(compact, clause) {
			t.Errorf("local reconciliation query is missing %q", clause)
		}
	}
}

func TestValidDiscrepancyMirrorsDatabaseConstraints(t *testing.T) {
	key := [32]byte{1}
	external := bytes.Repeat([]byte{2}, 32)
	status, currency := "SUCCEEDED", "CNY"
	amount := int64(100)
	valid := Discrepancy{
		Key: key, Provider: string(payment.ProviderAlipay), Category: "AMOUNT_MISMATCH",
		InternalOrderID: pointerUUID(uuid.New()), InternalTransactionID: pointerUUID(uuid.New()),
		ExternalReferenceHMAC: external, LocalStatus: &status, ProviderStatus: &status,
		SummaryCode: "AMOUNT_MISMATCH", LocalAmountMinor: &amount, ProviderAmountMinor: &amount,
		LocalCurrency: &currency, ProviderCurrency: &currency,
	}
	if !validDiscrepancy(valid) {
		t.Fatal("valid discrepancy was rejected")
	}
	tests := []struct {
		name   string
		mutate func(*Discrepancy)
	}{
		{name: "zero key", mutate: func(value *Discrepancy) { value.Key = [32]byte{} }},
		{name: "raw short reference", mutate: func(value *Discrepancy) { value.ExternalReferenceHMAC = []byte("external-id") }},
		{name: "provider", mutate: func(value *Discrepancy) { value.Provider = "MANUAL" }},
		{name: "category", mutate: func(value *Discrepancy) { value.Category = "RAW_BILL" }},
		{name: "lower status", mutate: func(value *Discrepancy) { status := "succeeded"; value.LocalStatus = &status }},
		{name: "summary code", mutate: func(value *Discrepancy) { value.SummaryCode = "bad-code" }},
		{name: "currency", mutate: func(value *Discrepancy) { currency := "US"; value.LocalCurrency = &currency }},
		{name: "negative amount", mutate: func(value *Discrepancy) { amount := int64(-1); value.LocalAmountMinor = &amount }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			copy := valid
			test.mutate(&copy)
			if validDiscrepancy(copy) {
				t.Fatalf("invalid discrepancy accepted: %+v", copy)
			}
		})
	}
}

func TestReconciliationLockKeyIsStableAndRunScoped(t *testing.T) {
	runA, runB := uuid.New(), uuid.New()
	if reconciliationLockKey(runA) != reconciliationLockKey(runA) {
		t.Fatal("lock key is not stable")
	}
	if reconciliationLockKey(runA) == reconciliationLockKey(runB) {
		t.Fatal("different run IDs unexpectedly share lock key")
	}
}

func pointerUUID(value uuid.UUID) *uuid.UUID { return &value }

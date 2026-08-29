package store

import (
	"strings"
	"testing"
)

func TestRefundApprovalOutboxIsClaimable(t *testing.T) {
	query := strings.ToLower(claimOutboxEventsSQL)
	for _, required := range []string{"refund.approval.changed", "for update skip locked", "processed_at is null"} {
		if !strings.Contains(query, required) {
			t.Errorf("outbox claim contract is missing %q", required)
		}
	}
}

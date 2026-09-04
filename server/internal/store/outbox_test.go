package store

import (
	"strings"
	"testing"
)

func TestAuthorityOutboxClaimIsBounded(t *testing.T) {
	query := strings.ToLower(claimOutboxEventsSQL)
	for _, required := range []string{"device.revoked", "account.status.changed", "history.object.delete", "for update skip locked", "processed_at is null"} {
		if !strings.Contains(query, required) {
			t.Errorf("outbox claim contract is missing %q", required)
		}
	}
	for _, removed := range []string{"entitlement.changed", "refund.approval.changed"} {
		if strings.Contains(query, removed) {
			t.Errorf("outbox claim contract still includes removed event %q", removed)
		}
	}
}

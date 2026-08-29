package outbox

import (
	"os"
	"strings"
	"testing"
)

func TestRefundApprovalOutboxPublishesOnlyAggregateID(t *testing.T) {
	source, err := os.ReadFile("dispatcher.go")
	if err != nil {
		t.Fatal(err)
	}
	text := string(source)
	start := strings.Index(text, `case "refund.approval.changed":`)
	if start < 0 {
		t.Fatal("refund approval outbox dispatcher case is missing")
	}
	end := strings.Index(text[start:], "\n\tcase ")
	if end < 0 {
		end = len(text) - start
	}
	branch := text[start : start+end]
	if !strings.Contains(branch, `publishAggregate(ctx, "refund-approval-changed", event.AggregateID)`) {
		t.Fatalf("refund approval branch does not publish the bounded aggregate: %s", branch)
	}
	for _, forbidden := range []string{"event.Payload", "reason", "provider"} {
		if strings.Contains(branch, forbidden) {
			t.Fatalf("refund approval branch exposes %q: %s", forbidden, branch)
		}
	}
}

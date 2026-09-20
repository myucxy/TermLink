package webportal

import (
	"testing"
	"time"
)

// The portal session slides on authenticated reads (see requireAccount) and
// must follow the product-wide 20-day inactivity login lifetime.
func TestWebSessionTTLFollowsTwentyDayInactivityPolicy(t *testing.T) {
	if webSessionTTL != 20*24*time.Hour {
		t.Fatalf("webSessionTTL=%v want %v", webSessionTTL, 20*24*time.Hour)
	}
}

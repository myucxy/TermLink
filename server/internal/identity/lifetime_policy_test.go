package identity

import (
	"testing"
	"time"
)

// The product contract is a 20-day inactivity login lifetime: native clients
// stay signed in while they refresh at least once every 20 days (the fosite
// refresh grant slides the window on every use), and browser SSO sessions
// slide on authorize reuse. A shorter lifetime forces daily re-login.
func TestEmbeddedSessionLifetimesFollowTwentyDayInactivityPolicy(t *testing.T) {
	expected := 20 * 24 * time.Hour
	if embeddedRefreshTokenLifetime != expected {
		t.Fatalf("embeddedRefreshTokenLifetime=%v want %v", embeddedRefreshTokenLifetime, expected)
	}
	if embeddedBrowserSessionLifetime != expected {
		t.Fatalf("embeddedBrowserSessionLifetime=%v want %v", embeddedBrowserSessionLifetime, expected)
	}
}

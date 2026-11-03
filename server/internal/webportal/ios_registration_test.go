package webportal

import (
	"html"
	"net/http"
	"net/url"
	"strings"
	"testing"

	"github.com/termlink/termlink/server/internal/store"
)

func TestNativeRegistrationContinuationRetainsIOSPKCEAndExactClientBoundaries(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.AndroidClientID = "custom-android"
	portal.config.IOSClientID = "custom-ios"
	portal.config.Repository = &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationOpen, Version: 1},
	}
	for _, clientID := range []string{"custom-ios", "custom-android"} {
		query := url.Values{"client_id": {clientID}, "response_type": {"code"},
			"redirect_uri": {"dev.termlink.ios:/oauth2redirect"}, "state": {"pkce-state"},
			"code_challenge": {"original-challenge"}, "code_challenge_method": {"S256"}}
		continuation := "https://identity.example/realms/termlink/protocol/openid-connect/auth?" + query.Encode()
		response := performRequest(portal.Handler(), http.MethodGet, "/register?continue="+url.QueryEscape(continuation), nil)
		if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), `data-registration-continue="`+html.EscapeString(continuation)+`"`) {
			t.Fatalf("configured native client lost its authorization continuation: %s, HTTP %d", clientID, response.Code)
		}
	}
	base := "https://identity.example/realms/termlink/protocol/openid-connect/auth?client_id="
	for _, candidate := range []string{
		base + "termlink-ios", // Only the configured ID is enabled.
		base + "custom-ios&client_id=termlink-windows",
		base + "custom-ios#fragment", base + "custom-ios&client_id=custom-ios",
		"https://identity.example.evil.test/realms/termlink/protocol/openid-connect/auth?client_id=custom-ios",
		"https://user@identity.example/realms/termlink/protocol/openid-connect/auth?client_id=custom-ios",
		"http://identity.example/realms/termlink/protocol/openid-connect/auth?client_id=custom-ios",
		"https://identity.example/realms/termlink/account?client_id=custom-ios",
	} {
		if got := normalizeRegistrationContinueURL(portal.config, candidate); got != "" {
			t.Fatalf("untrusted continuation accepted: %q", candidate)
		}
	}
	portal.config.IOSClientID = ""
	if got := normalizeRegistrationContinueURL(portal.config, base+"custom-ios"); got != "" {
		t.Fatal("disabled iOS client retained an authorization continuation")
	}
}

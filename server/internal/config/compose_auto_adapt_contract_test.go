package config

import (
	"os"
	"strings"
	"testing"
)

// The compose deployment relies on the server's env defaults and PUBLIC_URL
// derivation (internal/config Load). Re-stating derived values in compose
// reintroduces the contradictory-override failure class where an explicit
// issuer or redirect drifted away from TERMLINK_PUBLIC_URL and config.Load
// rejected the deployment at start-up.
func TestProductionComposeReliesOnServerAutoAdaptation(t *testing.T) {
	raw, err := os.ReadFile("../../compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	overlay := strings.ReplaceAll(string(raw), "\r\n", "\n")

	for _, contract := range []string{
		"TERMLINK_PUBLIC_URL: ${TERMLINK_PUBLIC_URL:-",
		"TERMLINK_DATABASE_URL: postgres://termlink:${TERMLINK_POSTGRES_PASSWORD:-",
		"TERMLINK_REGISTRATION_RECOVERY_ENABLED: \"true\"",
		"TERMLINK_ACCOUNT_DELETION_ENABLED: \"true\"",
		"TERMLINK_DIRECT_TRANSPORT_ENABLED: \"true\"",
	} {
		if !strings.Contains(overlay, contract) {
			t.Fatalf("production compose omitted %q", contract)
		}
	}

	for _, autoAdapted := range []string{
		"TERMLINK_RUNTIME_MODE",
		"TERMLINK_IDENTITY_PROVIDER",
		"TERMLINK_OIDC_ISSUER",
		"TERMLINK_OIDC_CLIENT_ID",
		"TERMLINK_OIDC_ALLOWED_PARTIES",
		"TERMLINK_ANDROID_OIDC_CLIENT_ID",
		"TERMLINK_ANDROID_OIDC_REDIRECT_URI",
		"TERMLINK_WINDOWS_OIDC_CLIENT_ID",
		"TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES",
		"TERMLINK_WINDOWS_OIDC_REDIRECT_URI",
		"TERMLINK_WEB_OIDC_CLIENT_ID",
		"TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID",
		"TERMLINK_WEB_OIDC_REDIRECT_URI",
		"TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION",
		"TERMLINK_LISTEN_ADDR",
		"TERMLINK_LOG_LEVEL",
		"TERMLINK_DIRECT_ROUTING_PERMITTED",
	} {
		if strings.Contains(overlay, autoAdapted) {
			t.Fatalf("production compose must rely on the server default for %q", autoAdapted)
		}
	}
}

package config

import (
	"os"
	"strings"
	"testing"
)

func TestProductionSingleNodeComposeDisablesExternalCoordination(t *testing.T) {
	raw, err := os.ReadFile("../../compose/single-node.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	overlay := strings.ReplaceAll(string(raw), "\r\n", "\n")
	for _, contract := range []string{
		"migrate:\n    profiles: [\"distributed-runtime\"]",
		"valkey:\n    profiles: [\"distributed-runtime\"]",
		"TERMLINK_RUNTIME_MODE: single-node",
		"TERMLINK_IDENTITY_PROVIDER: embedded",
		"TERMLINK_DIRECT_TRANSPORT_ENABLED: \"true\"",
		"TERMLINK_REDIS_URL_FILE: !reset null",
		"TERMLINK_DIRECT_REDIS_URL_FILE: !reset null",
		"depends_on: !override\n      postgres:",
	} {
		if !strings.Contains(overlay, contract) {
			t.Fatalf("production single-node overlay omitted %q", contract)
		}
	}
	if strings.Contains(overlay, "direct-valkey") || strings.Contains(overlay, "keycloak:\n") {
		t.Fatal("single-node overlay must not introduce another coordination or identity service")
	}
}

package auth

import (
	"os"
	"strings"
	"testing"
)

func TestInitialAdminPasswordResetRestagesDockerSecrets(t *testing.T) {
	data, err := os.ReadFile("../../ops/Reset-InitialAdminPassword.ps1")
	if err != nil {
		t.Fatal(err)
	}
	reset := string(data)
	rotate := strings.Index(reset, "WriteAllText($paths.InitialAdminPassword")
	stage := strings.Index(reset, "run --rm --no-deps runtime-secrets-init")
	install := strings.Index(reset, "/termlink-server identity-reset-initial-admin")
	if rotate < 0 || stage < 0 || install < 0 || !(rotate < stage && stage < install) {
		t.Fatal("initial admin reset must restage the rotated password before installing it in embedded identity")
	}
	if strings.Contains(strings.ToLower(reset), "keycloak") {
		t.Fatal("embedded initial administrator reset must not depend on Keycloak")
	}
}

func TestInitialAdminPasswordClaimScrubsStagedBootstrapSecret(t *testing.T) {
	data, err := os.ReadFile("../../ops/Get-InitialAdminPassword.ps1")
	if err != nil {
		t.Fatal(err)
	}
	claim := string(data)
	rotate := strings.Index(claim, "WriteAllText($paths.InitialAdminPassword")
	scrub := strings.Index(claim, "run --rm --no-deps runtime-secrets-init")
	disclose := strings.Index(claim, "Write-Output $temporaryPassword")
	if rotate < 0 || scrub < 0 || disclose < 0 || !(rotate < scrub && scrub < disclose) {
		t.Fatal("initial administrator claim must scrub the staged bootstrap secret before disclosure")
	}
}

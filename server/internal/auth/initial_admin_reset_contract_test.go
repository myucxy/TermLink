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
	install := strings.Index(reset, "$resetCommand = @'")
	if rotate < 0 || stage < 0 || install < 0 || !(rotate < stage && stage < install) {
		t.Fatal("initial admin reset must restage the rotated password before installing it in Keycloak")
	}
}

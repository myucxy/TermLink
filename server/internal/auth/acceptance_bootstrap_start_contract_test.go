package auth

import (
	"os"
	"strings"
	"testing"
)

func TestAcceptanceStartClosesPublicIngressBeforeReinitializingIdentity(t *testing.T) {
	data, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	script := string(data)
	stopIngress := strings.Index(script, "docker compose -f $paths.ComposeFile stop lucky-upstream")
	removeInitializer := strings.Index(script, "docker compose -f $paths.ComposeFile rm --stop --force")
	startStack := strings.Index(script, "docker compose -f $paths.ComposeFile up --build --detach")
	if stopIngress < 0 || removeInitializer < 0 || startStack < 0 ||
		!(stopIngress < removeInitializer && removeInitializer < startStack) {
		t.Fatal("acceptance startup must close public ingress before rerunning identity initialization")
	}
	removeLineEnd := strings.IndexByte(script[removeInitializer:], '\n')
	if removeLineEnd < 0 || !strings.Contains(script[removeInitializer:removeInitializer+removeLineEnd], "keycloak-init") {
		t.Fatal("acceptance startup must remove the completed identity initializer before bringing the stack up")
	}
}

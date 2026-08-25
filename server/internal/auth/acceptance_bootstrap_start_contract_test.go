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
	stopIngress := strings.Index(script, "docker compose @composeFileArguments stop lucky-upstream")
	removeInitializer := strings.Index(script, "docker compose @composeFileArguments rm --stop --force")
	startStack := strings.Index(script, "docker compose @composeFileArguments up --build --detach")
	if stopIngress < 0 || removeInitializer < 0 || startStack < 0 ||
		!(stopIngress < removeInitializer && removeInitializer < startStack) {
		t.Fatal("acceptance startup must close public ingress before rerunning identity initialization")
	}
	removeLineEnd := strings.IndexByte(script[removeInitializer:], '\n')
	if removeLineEnd < 0 || !strings.Contains(script[removeInitializer:removeInitializer+removeLineEnd], "keycloak-init") {
		t.Fatal("acceptance startup must remove the completed identity initializer before bringing the stack up")
	}
}

func TestAcceptanceStartWaitsOnOneStableContainerSet(t *testing.T) {
	data, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	script := string(data)
	startStack := strings.Index(script, "docker compose @composeFileArguments up --build --detach")
	waitStack := strings.LastIndex(script, "Wait-ProductionAcceptanceStack -ComposeFile $composeFiles")
	if startStack < 0 || waitStack < 0 || startStack >= waitStack {
		t.Fatal("acceptance startup must wait after creating the production container set")
	}
	if strings.Contains(script, "up --no-build --no-recreate --detach --wait") {
		t.Fatal("acceptance startup must not restart completed initializer jobs through a second compose up")
	}
	for _, contract := range []string{
		"ps --all --format json",
		"'edge', 'lucky-upstream', 'postgres', 'valkey', 'minio', 'server', 'keycloak'",
		"'runtime-secrets-init', 'keycloak-init'",
		"'server-secrets-init', 'migrate'",
		"[string]$item.Health -eq 'healthy'",
		"[string]$item.State -eq 'exited' -and [int]$item.ExitCode -eq 0",
	} {
		if !strings.Contains(script, contract) {
			t.Fatalf("acceptance stable-container wait omitted %q", contract)
		}
	}
}

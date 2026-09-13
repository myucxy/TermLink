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
	selectExternalInitializer := strings.Index(script, "$initializerServices += 'keycloak-init'")
	removeInitializer := strings.Index(script, "docker compose @composeFileArguments rm --stop --force")
	startStack := strings.Index(script, "docker compose @composeFileArguments up --build --detach")
	if stopIngress < 0 || selectExternalInitializer < 0 || removeInitializer < 0 || startStack < 0 ||
		!(stopIngress < selectExternalInitializer && selectExternalInitializer < removeInitializer && removeInitializer < startStack) {
		t.Fatal("acceptance startup must close public ingress before rerunning identity initialization")
	}
	removeLineEnd := strings.IndexByte(script[removeInitializer:], '\n')
	if removeLineEnd < 0 || !strings.Contains(script[removeInitializer:removeInitializer+removeLineEnd], "@initializerServices") {
		t.Fatal("acceptance startup must remove the selected identity initializer before bringing the stack up")
	}
	externalIdentityGuard := strings.LastIndex(script[:selectExternalInitializer], "if (-not $EmbeddedIdentity)")
	if externalIdentityGuard < 0 || externalIdentityGuard > stopIngress {
		t.Fatal("acceptance startup must only rerun the external identity initializer outside embedded identity mode")
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
		"'lucky-upstream'; 'postgres'; 'valkey'; 'server'; 'keycloak'",
		"'runtime-secrets-init'; 'keycloak-init'; 'server-secrets-init'; 'migrate'",
		"[string]$item.Health -eq 'healthy'",
		"[string]$item.State -eq 'exited' -and [int]$item.ExitCode -eq 0",
	} {
		if !strings.Contains(script, contract) {
			t.Fatalf("acceptance stable-container wait omitted %q", contract)
		}
	}
}

func TestAcceptanceDirectShadowUsesIsolatedPrewarmedState(t *testing.T) {
	startData, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	commonData, err := os.ReadFile("../../ops/ProductionAcceptance.Common.psm1")
	if err != nil {
		t.Fatal(err)
	}
	acceptanceData, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	acceptanceDirectData, err := os.ReadFile("../../compose/acceptance.direct-transport.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	stopData, err := os.ReadFile("../../ops/Stop-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	probeData, err := os.ReadFile("../../cmd/production-e2e-probe/main.go")
	if err != nil {
		t.Fatal(err)
	}
	directData, err := os.ReadFile("../../compose/direct-transport.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	start := string(startData)
	common := string(commonData)
	acceptance := string(acceptanceData)
	acceptanceDirect := string(acceptanceDirectData)
	direct := string(directData)
	stop := string(stopData)
	probe := string(probeData)
	for _, contract := range []string{
		"[switch]$EnableDirectTransportShadow",
		"[switch]$EnableDirectDataPlane",
		"$env:TERMLINK_DIRECT_ROUTING_PERMITTED = $directRoutingPermitted",
		"$env:TERMLINK_SINGLE_NODE_DIRECT_ROUTING_PERMITTED = $directRoutingPermitted",
		"TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING",
		"compose\\direct-transport.compose.yml",
		"compose\\acceptance.direct-transport.compose.yml",
		"Set-TermLinkAcceptanceDirectComposeEnvironment",
		"up --detach --wait direct-valkey",
		"$additionalHealthyServices += 'direct-valkey'",
		"$additionalCompletedServices += 'direct-valkey-secrets-init'",
		"rm --stop --force direct-valkey-secrets-init",
		"-Service direct-valkey",
		"-Path /run/secrets/direct_valkey_password",
		"-Expected '400:999:1000'",
		"$oneShotServices += 'direct-valkey-secrets-init'",
		"docker compose -f $embeddedIdentityComposeFile down --volumes --remove-orphans",
		"label=com.docker.compose.project=$composeProject",
		"$volume.StartsWith($projectPrefix, [StringComparison]::Ordinal)",
		"$network.StartsWith($projectPrefix, [StringComparison]::Ordinal)",
		"TERMLINK_ACCEPTANCE_REQUIRE_DIRECT_TRANSPORT",
		"redis://termlink-direct:$directValkeyPassword@direct-valkey:6379/0",
	} {
		if !strings.Contains(start, contract) {
			t.Fatalf("acceptance Direct shadow startup omitted %q", contract)
		}
	}
	for _, contract := range []string{
		"DirectValkeyPassword = Join-Path $secretRoot 'direct-valkey-password'",
		"DirectRedisUrl = Join-Path $secretRoot 'direct-redis-url'",
		"TERMLINK_DIRECT_REDIS_URL_FILE",
		"TERMLINK_DIRECT_VALKEY_PASSWORD_FILE",
	} {
		if !strings.Contains(common, contract) {
			t.Fatalf("acceptance Direct shadow secret contract omitted %q", contract)
		}
	}
	for _, contract := range []string{
		"rm -f /target/direct_redis_url",
		"if [ -f /run/secrets/direct_redis_url ]; then",
		"chmod 0400 /target/direct_redis_url",
		"chown 65532:65532 /target/direct_redis_url",
	} {
		if !strings.Contains(acceptance, contract) {
			t.Fatalf("acceptance Direct secret staging omitted %q", contract)
		}
	}
	for _, contract := range []string{
		"server-secrets-init:",
		"- direct_redis_url",
		"direct-valkey-secrets-init:",
		"cp /run/secrets/direct_valkey_password /target/direct_valkey_password",
		"chown 999:1000 /target/direct_valkey_password",
		"test \"$$(stat -c '%a:%u:%g' /target/direct_valkey_password)\" = \"400:999:1000\"",
		"- acceptance-direct-valkey-secrets:/target",
		"network_mode: \"none\"",
		"- acceptance-direct-valkey-secrets:/run/secrets:ro",
		"condition: service_completed_successfully",
		"acceptance-direct-valkey-secrets:",
	} {
		if !strings.Contains(acceptanceDirect, contract) {
			t.Fatalf("acceptance Direct overlay omitted %q", contract)
		}
	}
	directValkeyOverride := strings.Index(acceptanceDirect, "\n  direct-valkey:\n")
	if directValkeyOverride < 0 || !strings.Contains(
		acceptanceDirect[directValkeyOverride:],
		"secrets: !reset []",
	) {
		t.Fatal("acceptance Direct Valkey must replace the generic file-backed secret mount")
	}
	if strings.Contains(direct, "depends_on:\n      direct-valkey:") {
		t.Fatal("Direct shadow acceptance must not make server startup depend on Direct state")
	}
	for _, contract := range []string{
		"$includeDirectCleanup = $RemoveVolumes",
		"compose\\direct-transport.compose.yml",
		"compose\\acceptance.direct-transport.compose.yml",
		"if ($RemoveVolumes) { $arguments += '--volumes' }",
	} {
		if !strings.Contains(stop, contract) {
			t.Fatalf("acceptance Direct cleanup omitted %q", contract)
		}
	}
	for _, contract := range []string{
		"TERMLINK_ACCEPTANCE_REQUIRE_DIRECT_TRANSPORT",
		"TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING",
		"/v1/transport/config",
		"/v1/transport/tickets",
		"direct_transport_shadow_multipair_v2",
		"direct_session_cutover_v1",
	} {
		if !strings.Contains(probe, contract) {
			t.Fatalf("acceptance Direct runtime probe omitted %q", contract)
		}
	}
}

func TestAcceptanceSingleNodeKeepsDurableServicesAndEmbedsCoordination(t *testing.T) {
	startData, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	overlayData, err := os.ReadFile("../../compose/acceptance.single-node.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	start := string(startData)
	overlay := strings.ReplaceAll(string(overlayData), "\r\n", "\n")
	for _, contract := range []string{
		"[switch]$SingleNode",
		"compose\\acceptance.single-node.compose.yml",
		"TERMLINK_SINGLE_NODE_DIRECT_TRANSPORT_ENABLED = 'true'",
		"$EnableDirectTransportShadow -or $SingleNode",
		"if (-not $SingleNode) { $oneShotServices += 'migrate' }",
	} {
		if !strings.Contains(start, contract) {
			t.Fatalf("single-node acceptance startup omitted %q", contract)
		}
	}
	for _, contract := range []string{
		"lucky-upstream:\n    profiles: [\"distributed-runtime\"]",
		"valkey:\n    profiles: [\"distributed-runtime\"]",
		"migrate:\n    profiles: [\"distributed-runtime\"]",
		"TERMLINK_RUNTIME_MODE: single-node",
		"TERMLINK_OIDC_BACKCHANNEL_ISSUER: http://keycloak:8080/realms/termlink",
		"TERMLINK_REDIS_URL_FILE: !reset null",
		"TERMLINK_DIRECT_REDIS_URL_FILE: !reset null",
	} {
		if !strings.Contains(overlay, contract) {
			t.Fatalf("single-node acceptance overlay omitted %q", contract)
		}
	}
}

func TestAcceptanceEmbeddedIdentityTopologyHasNoKeycloakRuntime(t *testing.T) {
	startData, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	composeData, err := os.ReadFile("../../compose/acceptance.embedded-identity.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	start := string(startData)
	compose := strings.ReplaceAll(string(composeData), "\r\n", "\n")
	if strings.Contains(strings.ToLower(compose), "keycloak") {
		t.Fatal("embedded identity Compose must not declare or reference Keycloak")
	}
	for _, contract := range []string{
		"runtime-secrets-init:\n", "postgres:\n", "server:\n",
		"TERMLINK_IDENTITY_PROVIDER: embedded",
		"TERMLINK_INITIAL_ADMIN_PASSWORD_FILE: /run/secrets/admin_initial_password",
		"TERMLINK_RUNTIME_MODE: single-node",
	} {
		if !strings.Contains(compose, contract) {
			t.Fatalf("embedded identity Compose omitted %q", contract)
		}
	}
	for _, forbidden := range []string{"acceptance-keycloak-data", "keycloak_admin_password", "oidc_client_secret", "valkey:"} {
		if strings.Contains(strings.ToLower(compose), forbidden) {
			t.Fatalf("embedded identity Compose retained %q", forbidden)
		}
	}
	if strings.Contains(start, "Reset is not allowed during an embedded identity migration") {
		t.Fatal("clean embedded identity reset must be supported")
	}
	for _, contract := range []string{
		"$env:TERMLINK_ACCEPTANCE_USER_NAME = if ($EmbeddedIdentity) { 'admin' }",
		"$paths.InitialAdminPassword } else { $paths.UserPassword }",
		"if (-not $EmbeddedIdentity) {\n    Assert-RuntimeSecretMetadata -Service keycloak",
	} {
		if !strings.Contains(strings.ReplaceAll(start, "\r\n", "\n"), contract) {
			t.Fatalf("embedded identity startup omitted %q", contract)
		}
	}
}

func TestAcceptanceEmbeddedIdentityUpgradeDoesNotReuseClaimedBootstrapPassword(t *testing.T) {
	data, err := os.ReadFile("../../ops/Start-ProductionAcceptance.ps1")
	if err != nil {
		t.Fatal(err)
	}
	script := strings.ReplaceAll(string(data), "\r\n", "\n")
	claimedGuard := strings.Index(script, "$claimedEmbeddedAdmin = $EmbeddedIdentity -and")
	claimedBranch := strings.Index(script, "if ($claimedEmbeddedAdmin) {")
	authenticatedBranch := strings.Index(script, "else {\n    $env:TERMLINK_ACCEPTANCE_USER_NAME")
	probe := strings.Index(script, "& $go run ./cmd/production-e2e-probe")
	if claimedGuard < 0 || claimedBranch < 0 || authenticatedBranch < 0 || probe < 0 ||
		!(claimedGuard < claimedBranch && claimedBranch < authenticatedBranch && authenticatedBranch < probe) {
		t.Fatal("claimed embedded administrators must bypass only the credentialed acceptance probe")
	}
	if !strings.Contains(script[claimedGuard:claimedBranch], "$paths.InitialAdminClaimed") {
		t.Fatal("embedded upgrade guard must require the claimed-password marker")
	}
	claimedBlock := script[claimedBranch:authenticatedBranch]
	for _, contract := range []string{
		"/readyz",
		"/.well-known/openid-configuration",
		"Never reset a real administrator",
	} {
		if !strings.Contains(claimedBlock, contract) {
			t.Fatalf("claimed embedded upgrade checks omitted %q", contract)
		}
	}
	if strings.Contains(claimedBlock, "TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE") ||
		strings.Contains(claimedBlock, "identity-reset-initial-admin") {
		t.Fatal("claimed embedded upgrades must not reuse or reset the one-time administrator password")
	}
}

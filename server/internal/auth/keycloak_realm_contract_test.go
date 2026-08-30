package auth

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

type acceptanceRealmClient struct {
	ClientID                           string            `json:"clientId"`
	Name                               string            `json:"name"`
	RootURL                            string            `json:"rootUrl"`
	BaseURL                            string            `json:"baseUrl"`
	PublicClient                       bool              `json:"publicClient"`
	StandardFlowEnabled                bool              `json:"standardFlowEnabled"`
	DirectAccessGrantsEnabled          bool              `json:"directAccessGrantsEnabled"`
	ImplicitFlowEnabled                bool              `json:"implicitFlowEnabled"`
	ServiceAccountsEnabled             bool              `json:"serviceAccountsEnabled"`
	FullScopeAllowed                   bool              `json:"fullScopeAllowed"`
	Secret                             string            `json:"secret"`
	RedirectURIs                       []string          `json:"redirectUris"`
	Attributes                         map[string]string `json:"attributes"`
	DefaultClientScopes                []string          `json:"defaultClientScopes"`
	OptionalClientScopes               []string          `json:"optionalClientScopes"`
	AuthenticationFlowBindingOverrides map[string]string `json:"authenticationFlowBindingOverrides"`
	ProtocolMappers                    []struct {
		Name           string            `json:"name"`
		ProtocolMapper string            `json:"protocolMapper"`
		Config         map[string]string `json:"config"`
	} `json:"protocolMappers"`
}

func TestAcceptanceRealmDisablesNativeRegistrationAndUsesNativeClientLogin(t *testing.T) {
	data, err := os.ReadFile("../../acceptance/keycloak-realm.json")
	if err != nil {
		t.Fatal(err)
	}
	var realm struct {
		LoginTheme           string                  `json:"loginTheme"`
		AccountTheme         string                  `json:"accountTheme"`
		RegistrationAllowed  bool                    `json:"registrationAllowed"`
		VerifyEmail          bool                    `json:"verifyEmail"`
		ResetPasswordAllowed bool                    `json:"resetPasswordAllowed"`
		BruteForceProtected  bool                    `json:"bruteForceProtected"`
		AccessTokenLifespan  int                     `json:"accessTokenLifespan"`
		RevokeRefreshToken   bool                    `json:"revokeRefreshToken"`
		RefreshTokenMaxReuse int                     `json:"refreshTokenMaxReuse"`
		Clients              []acceptanceRealmClient `json:"clients"`
		AuthenticationFlows  []struct {
			ID                       string `json:"id"`
			Alias                    string `json:"alias"`
			TopLevel                 bool   `json:"topLevel"`
			AuthenticationExecutions []struct {
				Authenticator string `json:"authenticator"`
				FlowAlias     string `json:"flowAlias"`
				Requirement   string `json:"requirement"`
			} `json:"authenticationExecutions"`
		} `json:"authenticationFlows"`
	}
	if err := json.Unmarshal(data, &realm); err != nil {
		t.Fatal(err)
	}
	// Realm imports and reconciliation keep provider self-registration closed;
	// TermLink's hosted registration API is the only public creation boundary.
	if realm.LoginTheme != "termlink-01b-v18" || realm.AccountTheme != "termlink-01b-v18" ||
		realm.RegistrationAllowed || realm.VerifyEmail || realm.ResetPasswordAllowed || !realm.BruteForceProtected ||
		realm.AccessTokenLifespan <= 0 || realm.AccessTokenLifespan > 300 ||
		!realm.RevokeRefreshToken || realm.RefreshTokenMaxReuse != 0 {
		t.Fatalf("realm native-login policy is incomplete")
	}
	var android *acceptanceRealmClient
	for i := range realm.Clients {
		if realm.Clients[i].ClientID == "termlink-android" {
			android = &realm.Clients[i]
			break
		}
	}
	if android == nil {
		t.Fatal("termlink-android client is missing")
	}
	if !android.PublicClient || !android.StandardFlowEnabled || !android.DirectAccessGrantsEnabled || android.ImplicitFlowEnabled ||
		android.ServiceAccountsEnabled || android.FullScopeAllowed || android.Secret != "" {
		t.Fatal("termlink-android client flow policy is unsafe")
	}
	if android.Attributes["pkce.code.challenge.method"] != "S256" {
		t.Fatal("termlink-android must require S256 PKCE")
	}
	if containsString(android.DefaultClientScopes, RegistrationAttemptScope) {
		t.Fatal("registration correlation scope must never be a default Android client scope")
	}
	foundAudience := false
	for _, mapper := range android.ProtocolMappers {
		if mapper.Name == "termlink-server-audience" {
			foundAudience = mapper.Config["included.client.audience"] == "termlink-server" &&
				mapper.Config["access.token.claim"] == "true" && mapper.Config["id.token.claim"] == "false"
		}
	}
	if !foundAudience {
		t.Fatal("Android access-only server audience mapper is missing")
	}
	var windows *acceptanceRealmClient
	for i := range realm.Clients {
		if realm.Clients[i].ClientID == "termlink-windows" {
			windows = &realm.Clients[i]
			break
		}
	}
	if windows == nil {
		t.Fatal("termlink-windows client is missing")
	}
	if !windows.PublicClient || !windows.StandardFlowEnabled || !windows.DirectAccessGrantsEnabled || windows.ImplicitFlowEnabled ||
		windows.ServiceAccountsEnabled || windows.FullScopeAllowed || windows.Secret != "" {
		t.Fatal("termlink-windows client flow policy is unsafe")
	}
	if windows.Attributes["pkce.code.challenge.method"] != "S256" ||
		windows.BaseURL != "https://termlink.kget.cc:8910" ||
		!containsString(windows.RedirectURIs, "http://127.0.0.1:43971/oauth2/callback") {
		t.Fatal("termlink-windows browser fallback policy is incomplete")
	}
	foundWindowsNickname := false
	for _, mapper := range windows.ProtocolMappers {
		if mapper.Name == "termlink-windows-nickname" {
			foundWindowsNickname = mapper.ProtocolMapper == "oidc-usermodel-attribute-mapper" &&
				mapper.Config["user.attribute"] == "nickname" && mapper.Config["claim.name"] == "nickname" &&
				mapper.Config["access.token.claim"] == "true" && mapper.Config["id.token.claim"] == "false"
		}
	}
	if !foundWindowsNickname {
		t.Fatal("termlink-windows access tokens must include the display nickname")
	}
	var web *acceptanceRealmClient
	for i := range realm.Clients {
		if realm.Clients[i].ClientID == "termlink-web" {
			web = &realm.Clients[i]
			break
		}
	}
	if web == nil || web.Name != "TermLink 个人中心" ||
		web.RootURL != "https://termlink.kget.cc:8910" ||
		web.BaseURL != "https://termlink.kget.cc:8910/account" ||
		!containsString(web.RedirectURIs, "https://termlink.kget.cc:8910/auth/callback") ||
		!containsString(web.RedirectURIs, "https://termlink.kget.cc:8910/account") {
		t.Fatal("termlink-web account-center return contract is incomplete")
	}
	foundWebAMR := false
	for _, mapper := range web.ProtocolMappers {
		if mapper.Name == "termlink-web-amr" {
			foundWebAMR = mapper.ProtocolMapper == "oidc-hardcoded-claim-mapper" &&
				mapper.Config["claim.name"] == "amr" && mapper.Config["claim.value"] == `["pwd"]` &&
				mapper.Config["jsonType.label"] == "JSON" &&
				mapper.Config["id.token.claim"] == "true" && mapper.Config["access.token.claim"] == "true"
		}
	}
	if !foundWebAMR {
		t.Fatal("termlink-web must emit the password method enforced by fresh Web authorization")
	}
	var nativeFlowID string
	for _, flow := range realm.AuthenticationFlows {
		if flow.Alias != "termlink-native-browser" {
			continue
		}
		nativeFlowID = flow.ID
		if !flow.TopLevel || nativeFlowID == "" {
			t.Fatal("native browser flow must be a stable top-level flow")
		}
		for _, execution := range flow.AuthenticationExecutions {
			if execution.Authenticator == "auth-otp-form" || strings.Contains(strings.ToLower(execution.FlowAlias), "otp") {
				t.Fatal("native browser flow must not contain Web OTP")
			}
		}
	}
	if nativeFlowID == "" || android.AuthenticationFlowBindingOverrides["browser"] != nativeFlowID ||
		windows.AuthenticationFlowBindingOverrides["browser"] != nativeFlowID ||
		web.AuthenticationFlowBindingOverrides["browser"] != "" {
		t.Fatal("Web-only MFA flow isolation is incomplete")
	}
}

func TestAcceptanceRealmUsesDedicatedMFAOnlyStepUpClient(t *testing.T) {
	data, err := os.ReadFile("../../acceptance/keycloak-realm.json")
	if err != nil {
		t.Fatal(err)
	}
	var realm struct {
		Clients             []acceptanceRealmClient `json:"clients"`
		AuthenticationFlows []struct {
			ID                       string `json:"id"`
			Alias                    string `json:"alias"`
			TopLevel                 bool   `json:"topLevel"`
			AuthenticationExecutions []struct {
				Authenticator string `json:"authenticator"`
				Requirement   string `json:"requirement"`
			} `json:"authenticationExecutions"`
		} `json:"authenticationFlows"`
	}
	if err := json.Unmarshal(data, &realm); err != nil {
		t.Fatal(err)
	}

	var flowID string
	for _, flow := range realm.AuthenticationFlows {
		if flow.Alias != "termlink-web-mfa-step-up" {
			continue
		}
		if !flow.TopLevel || flow.ID == "" || len(flow.AuthenticationExecutions) != 2 {
			t.Fatal("MFA Step-up flow must be a stable top-level two-stage flow")
		}
		requirements := make(map[string]string, len(flow.AuthenticationExecutions))
		for _, execution := range flow.AuthenticationExecutions {
			requirements[execution.Authenticator] = execution.Requirement
		}
		if requirements["auth-cookie"] != "REQUIRED" || requirements["auth-otp-form"] != "REQUIRED" {
			t.Fatalf("MFA Step-up flow must require existing SSO identity and OTP: %v", requirements)
		}
		flowID = flow.ID
	}
	if flowID == "" {
		t.Fatal("MFA Step-up flow is missing")
	}

	var client *acceptanceRealmClient
	for index := range realm.Clients {
		if realm.Clients[index].ClientID == "termlink-web-step-up" {
			client = &realm.Clients[index]
			break
		}
	}
	if client == nil || !client.PublicClient || !client.StandardFlowEnabled || client.DirectAccessGrantsEnabled ||
		client.ImplicitFlowEnabled || client.ServiceAccountsEnabled || client.FullScopeAllowed || client.Secret != "" {
		t.Fatal("MFA Step-up client flow policy is unsafe")
	}
	if client.Attributes["pkce.code.challenge.method"] != "S256" ||
		client.AuthenticationFlowBindingOverrides["browser"] != flowID ||
		!containsString(client.RedirectURIs, "https://termlink.kget.cc:8910/auth/callback") {
		t.Fatal("MFA Step-up client PKCE, callback, or flow binding is incomplete")
	}
	foundAMR := false
	for _, mapper := range client.ProtocolMappers {
		if mapper.Name == "termlink-web-step-up-amr" {
			foundAMR = mapper.ProtocolMapper == "oidc-hardcoded-claim-mapper" &&
				mapper.Config["claim.name"] == "amr" && mapper.Config["claim.value"] == `["otp"]` &&
				mapper.Config["jsonType.label"] == "JSON" &&
				mapper.Config["id.token.claim"] == "true" && mapper.Config["access.token.claim"] == "true"
		}
	}
	if !foundAMR {
		t.Fatal("MFA Step-up client must emit AMR for server-side OTP verification")
	}
}

func TestAcceptanceBootstrapReconcilesDedicatedMFAOnlyStepUpClient(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	for _, contract := range []string{
		"alias=termlink-web-mfa-step-up",
		"provider=auth-cookie",
		"provider=auth-otp-form",
		`if test "$$name" = Cookie -a "$$level" = 0; then`,
		`if test "$$name" = 'OTP Form' -a "$$level" = 0; then`,
		`-s id="$$step_up_cookie_execution" -s requirement=REQUIRED`,
		`-s id="$$step_up_otp_execution" -s requirement=REQUIRED`,
		"clientId=termlink-web-step-up",
		"-s directAccessGrantsEnabled=false",
		"-s implicitFlowEnabled=false",
		"-s serviceAccountsEnabled=false",
		`-s 'attributes={"pkce.code.challenge.method":"S256"}'`,
		`authenticationFlowBindingOverrides={\"browser\":\"$$step_up_flow_id\"}`,
		`"name": "termlink-web-step-up-amr"`,
		`"protocolMapper": "oidc-hardcoded-claim-mapper"`,
		`"claim.name": "amr"`,
		`"claim.value": "[\"otp\"]"`,
		`"jsonType.label": "JSON"`,
		`clients/$$step_up_client_id/protocol-mappers/models/$$step_up_amr_mapper_id`,
		`TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID: termlink-web-step-up`,
	} {
		if !strings.Contains(compose, contract) {
			t.Fatalf("acceptance MFA Step-up reconciliation omitted %q", contract)
		}
	}
}

func TestAcceptanceIdentityRecoveryUsesBoundedServiceAccountSecret(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	clientStart := strings.Index(compose, "recovery_client_id=$$(/opt/keycloak/bin/kcadm.sh get clients")
	secretWrite := strings.Index(compose, `printf '%s' "$$recovery_secret" > /target/server-secrets/keycloak_admin_client_secret`)
	if clientStart < 0 || secretWrite < 0 || clientStart > secretWrite {
		t.Fatal("acceptance identity service account must be provisioned before its secret is published")
	}
	if strings.Contains(compose, "-s registrationAllowed=true") {
		t.Fatal("Keycloak native self-registration must stay disabled")
	}
	for _, contract := range []string{
		"clientId=termlink-registration-recovery",
		"-s publicClient=false",
		"-s standardFlowEnabled=false",
		"-s directAccessGrantsEnabled=false",
		"-s serviceAccountsEnabled=true",
		"-s fullScopeAllowed=false",
		"for recovery_role in query-users manage-users manage-realm; do",
		`query-users|manage-users|manage-realm|'') ;;`,
		"remove-roles",
		`clients/$$recovery_client_id/scope-mappings/clients/$$realm_management_id`,
		`termlink-recovery-scope-role.json`,
		`TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE: /run/secrets/keycloak_admin_client_secret`,
		"test -s /target/keycloak_admin_client_secret",
		"chmod 0400 /target/keycloak_admin_client_secret",
	} {
		if !strings.Contains(compose, contract) {
			t.Fatalf("acceptance identity recovery contract omitted %q", contract)
		}
	}
	if strings.Contains(compose, "TERMLINK_KEYCLOAK_ADMIN_PASSWORD") || strings.Contains(compose, "TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET:") {
		t.Fatal("server identity recovery must not receive an administrator password or inline client secret")
	}
	serverSecretInit := strings.Index(compose, "\n  server-secrets-init:")
	migrateStart := strings.Index(compose, "\n  migrate:")
	if serverSecretInit < 0 || migrateStart <= serverSecretInit ||
		!strings.Contains(compose[serverSecretInit:migrateStart], "keycloak-init:\n        condition: service_completed_successfully") {
		t.Fatal("server secret finalization must wait for the Keycloak service-account secret")
	}
	serverSecretFinalization := compose[serverSecretInit:migrateStart]
	secretTemporaryChown := strings.Index(serverSecretFinalization, "chown 0:0 /target/keycloak_admin_client_secret")
	secretChmod := strings.Index(serverSecretFinalization, "chmod 0400 /target/keycloak_admin_client_secret")
	secretRuntimeChown := strings.Index(serverSecretFinalization, "chown 65532:65532 /target/keycloak_admin_client_secret")
	if secretTemporaryChown < 0 || secretChmod < 0 || secretRuntimeChown < 0 ||
		!(secretTemporaryChown < secretChmod && secretChmod < secretRuntimeChown) {
		t.Fatal("service-account secret must be temporarily owned, permission-tightened, and assigned to the runtime user in order")
	}
	if strings.Contains(compose, "-s resetPasswordAllowed=true") {
		t.Fatal("password reset must remain disabled until a tested service-email configuration enables it")
	}
}

func TestProductionRegistrationUsesOnlyBoundedKeycloakAdmin(t *testing.T) {
	data, err := os.ReadFile("../../cmd/termlink-server/main.go")
	if err != nil {
		t.Fatal(err)
	}
	mainSource := string(data)
	if strings.Contains(mainSource, "NewDirectRegistrationService") ||
		!strings.Contains(mainSource, "productionAPI.Registration = keycloakAdmin") ||
		!strings.Contains(mainSource, "productionAPI.VerifiedRegistration = keycloakAdmin") {
		t.Fatal("production registration must use only the bounded Keycloak administration client")
	}
}

func TestAcceptanceExistingWebClientReceivesPasswordAMRMapperIdempotently(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	for _, contract := range []string{
		`"name": "termlink-web-amr"`,
		`"protocolMapper": "oidc-hardcoded-claim-mapper"`,
		`"claim.value": "[\"pwd\"]"`,
		`if test "$$name" = termlink-web-amr; then`,
		`clients/$$web_client_id/protocol-mappers/models/$$web_amr_mapper_id`,
		`/opt/keycloak/bin/kcadm.sh delete`,
	} {
		if !strings.Contains(compose, contract) {
			t.Fatalf("existing termlink-web client AMR reconciliation omitted %q", contract)
		}
	}
}

func TestAcceptanceRegistrationRecoveryUserProfileIsControlled(t *testing.T) {
	data, err := os.ReadFile("../../acceptance/keycloak-user-profile.json")
	if err != nil {
		t.Fatal(err)
	}
	var profile struct {
		Attributes []struct {
			Name        string              `json:"name"`
			Validations map[string]any      `json:"validations"`
			Permissions map[string][]string `json:"permissions"`
			Selector    struct {
				Scopes []string `json:"scopes"`
			} `json:"selector"`
			Multivalued bool `json:"multivalued"`
		} `json:"attributes"`
	}
	if err := json.Unmarshal(data, &profile); err != nil {
		t.Fatal(err)
	}
	wanted := map[string]struct{}{
		RegistrationAttemptIDAttribute: {}, RegistrationAttemptProofAttribute: {},
	}
	registrationFields := map[string]struct{}{"username": {}, "email": {}, "nickname": {}}
	for _, attribute := range profile.Attributes {
		if _, ok := registrationFields[attribute.Name]; ok {
			if !equalStrings(attribute.Permissions["view"], []string{"admin", "user"}) ||
				!equalStrings(attribute.Permissions["edit"], []string{"admin", "user"}) {
				t.Fatalf("registration field %q is not visible and editable during registration", attribute.Name)
			}
			delete(registrationFields, attribute.Name)
		}
		if _, ok := wanted[attribute.Name]; !ok {
			continue
		}
		if !equalStrings(attribute.Permissions["view"], []string{"admin"}) ||
			!equalStrings(attribute.Permissions["edit"], []string{"admin"}) ||
			!equalStrings(attribute.Selector.Scopes, []string{RegistrationAttemptScope}) ||
			attribute.Multivalued || attribute.Validations["length"] == nil {
			t.Fatalf("controlled registration attribute %q has unsafe profile permissions", attribute.Name)
		}
		delete(wanted, attribute.Name)
	}
	if len(wanted) != 0 {
		t.Fatalf("controlled registration attributes are missing from User Profile: %v", wanted)
	}
	if len(registrationFields) != 0 {
		t.Fatalf("public registration fields are missing from User Profile: %v", registrationFields)
	}

	composeData, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(composeData)
	profileUpdate := strings.Index(compose, "update realms/termlink/users/profile")
	if profileUpdate < 0 || strings.Contains(compose, "-s registrationAllowed=true") ||
		!strings.Contains(compose, `"include.in.token.scope":"false"`) ||
		!strings.Contains(compose, `optional-client-scopes/$$registration_scope_id`) ||
		!strings.Contains(compose, `default-client-scopes/$$registration_scope_id`) {
		t.Fatal("acceptance bootstrap does not keep controlled registration attributes private while native registration remains closed")
	}
}

func equalStrings(left, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if left[index] != right[index] {
			return false
		}
	}
	return true
}

func containsString(values []string, target string) bool {
	for _, value := range values {
		if value == target {
			return true
		}
	}
	return false
}

func TestAcceptanceBootstrapKeepsNativeRegistrationClosed(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	disableRegistration := strings.Index(compose, "-s registrationAllowed=false")
	managedMarker := strings.Index(compose, "--rolename TERMLINK_BOOTSTRAP_MANAGED_V1")
	unmanagedAdminFailure := strings.Index(compose, "Refusing to grant SUPER_ADMIN to an unmanaged existing admin account.")
	if disableRegistration < 0 || managedMarker < 0 || unmanagedAdminFailure < 0 ||
		!(disableRegistration < managedMarker && managedMarker < unmanagedAdminFailure) ||
		strings.Contains(compose, "-s registrationAllowed=true") {
		t.Fatal("acceptance identity bootstrap must keep provider self-registration closed")
	}
	const publicIngressGate = "lucky-upstream:\n"
	luckyStart := strings.Index(compose, publicIngressGate)
	keycloakStart := strings.Index(compose, "\n  keycloak:")
	if luckyStart < 0 || keycloakStart <= luckyStart {
		t.Fatal("acceptance Compose service layout is incomplete")
	}
	luckyService := compose[luckyStart:keycloakStart]
	if !strings.Contains(luckyService, "keycloak-init:\n        condition: service_completed_successfully") {
		t.Fatal("public acceptance ingress must wait for successful identity initialization")
	}
}

func TestAcceptanceBootstrapPreservesExistingAcceptanceSessions(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	loopStart := strings.Index(compose, "for username in acceptance-user acceptance-probe; do")
	if loopStart < 0 {
		t.Fatal("acceptance identity users are missing")
	}
	loopEndOffset := strings.Index(compose[loopStart:], "\n        done")
	if loopEndOffset < 0 {
		t.Fatal("acceptance identity user loop is incomplete")
	}
	loop := compose[loopStart : loopStart+loopEndOffset]
	creationBranchEnd := strings.Index(loop, "\n          fi")
	passwordInitialization := strings.Index(loop, "kcadm.sh set-password")
	profileReconciliation := strings.Index(loop, "kcadm.sh update \"users/$$user_id\"")
	if creationBranchEnd < 0 || passwordInitialization < 0 || profileReconciliation < 0 ||
		passwordInitialization > creationBranchEnd || profileReconciliation < creationBranchEnd ||
		strings.Count(loop, "kcadm.sh set-password") != 1 {
		t.Fatal("acceptance user passwords must be initialized only when the managed user is first created")
	}
}

func TestAcceptanceBootstrapReconcilesWindowsNativeLogin(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	windowsStart := strings.Index(compose, "windows_client_id=$$(/opt/keycloak/bin/kcadm.sh get clients")
	if windowsStart < 0 {
		t.Fatal("Windows client reconciliation is missing")
	}
	windowsEndOffset := strings.Index(compose[windowsStart:], "client_scope_table=$$(/opt/keycloak/bin/kcadm.sh get client-scopes")
	if windowsEndOffset < 0 {
		t.Fatal("Windows client reconciliation is incomplete")
	}
	windowsBlock := compose[windowsStart : windowsStart+windowsEndOffset]
	for _, expected := range []string{
		`-s publicClient=true`,
		`-s standardFlowEnabled=true`,
		`-s directAccessGrantsEnabled=true`,
		`-s implicitFlowEnabled=false`,
		`-s serviceAccountsEnabled=false`,
		`-s fullScopeAllowed=false`,
		`-s baseUrl=${TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN:-https://termlink.kget.cc:8910}`,
		`-s 'redirectUris=["http://127.0.0.1:43971/oauth2/callback"]'`,
		`-s 'attributes={"pkce.code.challenge.method":"S256"}'`,
	} {
		if !strings.Contains(windowsBlock, expected) {
			t.Fatalf("Windows native-login reconciliation is missing %q", expected)
		}
	}
}

func TestAcceptanceIdentityThemesAlwaysReturnToPersonalCenter(t *testing.T) {
	read := func(path string) string {
		t.Helper()
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		return string(data)
	}

	loginTemplate := read("../../acceptance/keycloak-theme/termlink/login/template.ftl")
	loginForm := read("../../acceptance/keycloak-theme/termlink/login/login.ftl")
	loginCSS := read("../../internal/webportal/assets/identity.css")
	loginThemeProperties := read("../../acceptance/keycloak-theme/termlink/login/theme.properties")
	loginThemeInit := read("../../acceptance/keycloak-theme/termlink/login/resources/js/theme-init.js")
	zhMessages := read("../../acceptance/keycloak-theme/termlink/login/messages/messages_zh_CN.properties")
	enMessages := read("../../acceptance/keycloak-theme/termlink/login/messages/messages_en.properties")
	accountScript := read("../../acceptance/keycloak-theme/termlink/account/resources/js/theme.js")
	accountTemplate := read("../../acceptance/keycloak-theme/termlink/account/index.ftl")
	accountThemeProperties := read("../../acceptance/keycloak-theme/termlink/account/theme.properties")
	accountThemeInit := read("../../acceptance/keycloak-theme/termlink/account/resources/js/theme-init.js")
	accountCSS := read("../../acceptance/keycloak-theme/termlink/account/resources/css/account.css")
	accountZhMessages := read("../../acceptance/keycloak-theme/termlink/account/messages/messages_zh_CN.properties")
	keycloakDockerfile := read("../../acceptance/Keycloak.Dockerfile")
	acceptanceCompose := read("../../compose/acceptance.compose.yml")

	for name, content := range map[string]string{
		"login template account target":         `termlinkAccountUrl = client.baseUrl`,
		"login template hosted registration":    `client.clientId == "termlink-windows" && client.baseUrl?has_content`,
		"login form authorization continuation": `?continue=${url.loginUrl?url}`,
		"login server registration policy":      `msg("termlinkServerRegistration")`,
		"login template accessible label":       `msg('termlinkBackToPersonalCenter')`,
		"login template theme transfer":         `data-theme-transfer`,
		"login pre-paint theme script":          `src="${url.resourcesPath}/js/theme-init.js"`,
		"login pre-paint stored theme":          `const storageKey = "termlink-theme"`,
		"login pre-paint transferred theme":     `searchParams.get(transferParameter)`,
		"login theme responsive account action": ".identity-account-link",
		"login shared identity stylesheet":      "styles=css/identity.css",
		"login SVG theme control":               `class="theme-icon-moon"`,
		"login SVG account return":              `<span class="identity-account-link-icon" aria-hidden="true"><svg`,
		"Chinese personal-center copy":          "termlinkBackToPersonalCenter=返回个人中心",
		"English personal-center copy":          "termlinkBackToPersonalCenter=Back to account center",
		"account validated referrer target":     `safeAccountReturnURL(nativeReferrer?.getAttribute("href"))`,
		"account theme fallback marker":         `data-termlink-account-link`,
		"account theme stable action group":     `data-termlink-account-actions`,
		"account persistent sidebar return":     `data-termlink-sidebar-return`,
		"account sidebar return target":         `returnLink.href = themedAccountReturnURL()`,
		"account sidebar return priority":       `sidebarBody.insertBefore(returnLink, navigation)`,
		"account sidebar injection idempotence": `returnLink.nextElementSibling !== sectionLabel`,
		"account identity navigation label":     `data-termlink-identity-nav-label`,
		"account native user menu styling":      `[data-testid="page-header"] .pf-v5-c-menu-toggle`,
		"account redundant avatar removal":      `data-termlink-native-avatar`,
		"account desktop sidebar accessibility": `sidebar.setAttribute("aria-hidden", "false")`,
		"account theme product brand":           `tl-account-brand-copy`,
		"account validated brand target":        `brand.href = themedAccountReturnURL()`,
		"account pinned parent template":        "602b4bdf21d5dd386f2f8b9ee18b9d96a3054d430efabe00cea80779291a658f",
		"account pre-paint theme script":        `<script src="${resourceUrl}/js/theme-init.js"></script>`,
		"account static recovery link":          `class="tl-loading-return" href="${termlinkAccountReturnUrl}"`,
		"account static recovery fallback":      `properties.logoUrl!"/account"`,
		"account authorization feature source":  `${(realm.userManagedAccessAllowed && isAuthorizationEnabled)?c}`,
		"account runtime theme script":          `scripts=js/theme.js`,
		"account absolute recovery target":      `logoUrl=https://termlink.kget.cc:8910/account`,
		"account pre-paint stored theme":        `const storageKey = "termlink-theme"`,
		"account pre-paint transferred theme":   `searchParams.get(transferParameter)`,
		"account localized locale placeholder":  "defaultLocale=请选择...",
		"account admin-aligned sidebar":         "grid-template-columns: 236px minmax(0, 1fr) !important;",
		"account content-sized workspace":       "flex: 0 0 auto;",
		"account opaque workspace surface":      ":not(.tl-account-page-workspace)",
		"account redundant jump removal":        "tl-account-single-section",
		"account theme return styling":          ".tl-account-return",
		"account SVG theme control":             `class="theme-icon-moon"`,
		"account SVG return control":            `tl-account-return-icon`,
		"production image account theme":        `COPY acceptance/keycloak-theme/termlink/ /opt/keycloak/themes/termlink-01b-v18/`,
		"production shared identity styles":     `COPY internal/webportal/assets/identity.css /opt/keycloak/themes/termlink-01b-v18/login/resources/css/identity.css`,
		"production shared identity mark":       `COPY internal/webportal/assets/termlink-app-icon.svg /opt/keycloak/themes/termlink-01b-v18/login/resources/img/termlink-mark.svg`,
		"production shared identity favicon":    `COPY internal/webportal/assets/termlink-favicon.svg /opt/keycloak/themes/termlink-01b-v18/login/resources/img/termlink-favicon.svg`,
		"production runtime account theme":      `-s accountTheme=termlink-01b-v18`,
	} {
		var source string
		switch name {
		case "login template account target", "login template hosted registration", "login template accessible label", "login template theme transfer", "login pre-paint theme script", "login SVG theme control", "login SVG account return", "login server registration policy":
			source = loginTemplate
		case "login form authorization continuation":
			source = loginForm
		case "login pre-paint stored theme", "login pre-paint transferred theme":
			source = loginThemeInit
		case "login theme responsive account action":
			source = loginCSS
		case "login shared identity stylesheet":
			source = loginThemeProperties
		case "Chinese personal-center copy":
			source = zhMessages
		case "English personal-center copy":
			source = enMessages
		case "account validated referrer target", "account theme fallback marker", "account theme stable action group", "account persistent sidebar return", "account sidebar return target", "account sidebar return priority", "account sidebar injection idempotence", "account identity navigation label", "account redundant avatar removal", "account desktop sidebar accessibility", "account theme product brand", "account validated brand target", "account redundant jump removal", "account SVG theme control", "account SVG return control":
			source = accountScript
		case "account pinned parent template", "account pre-paint theme script", "account static recovery link", "account static recovery fallback", "account authorization feature source":
			source = accountTemplate
		case "account runtime theme script", "account absolute recovery target":
			source = accountThemeProperties
		case "account pre-paint stored theme", "account pre-paint transferred theme":
			source = accountThemeInit
		case "account localized locale placeholder":
			source = accountZhMessages
		case "account theme return styling", "account native user menu styling", "account admin-aligned sidebar", "account content-sized workspace", "account opaque workspace surface":
			source = accountCSS
		case "production image account theme", "production shared identity styles", "production shared identity mark", "production shared identity favicon":
			source = keycloakDockerfile
		case "production runtime account theme":
			source = acceptanceCompose
		}
		if !strings.Contains(source, content) {
			t.Errorf("%s contract is missing", name)
		}
	}
	if strings.Contains(loginForm, "url.registrationUrl") {
		t.Fatal("identity login still exposes the provider registration URL and can bypass TermLink registration policy")
	}
	if strings.Contains(acceptanceCompose, "../acceptance/keycloak-theme/termlink:/opt/keycloak/themes/") {
		t.Error("production acceptance must exercise the identity theme baked into the Keycloak image")
	}
	if strings.ContainsAny(loginTemplate+accountScript, "◐☼☾←") {
		t.Error("identity themes must not depend on platform control glyphs")
	}
	if strings.Contains(accountScript+accountCSS, "data-termlink-native-account-control") {
		t.Error("account theme must retain Keycloak's current-account and sign-out menu")
	}
	if strings.Contains(loginTemplate, "aria-pressed") ||
		strings.Contains(accountScript, `setAttribute("aria-pressed"`) ||
		strings.Contains(read("../../acceptance/keycloak-theme/termlink/login/resources/js/theme.js"), `setAttribute("aria-pressed"`) {
		t.Error("identity theme action buttons must not expose toggle-button pressed state")
	}
	if !strings.Contains(accountScript, `nativeReferrer.removeAttribute("aria-hidden")`) ||
		strings.Index(accountScript, "const host = findAccountActionHost();") > strings.Index(accountScript, `nativeReferrer.setAttribute("aria-hidden", "true")`) {
		t.Error("account return action must preserve the native referrer until its custom host is available")
	}
	if !strings.Contains(loginCSS, "--text-3: #606b7d;") || !strings.Contains(accountCSS, "--tl-text-3: #606b7d;") {
		t.Error("identity theme tertiary text must meet light-theme contrast requirements")
	}
	prePaint := strings.Index(accountTemplate, `<script src="${resourceUrl}/js/theme-init.js"></script>`)
	entryScript := strings.Index(accountTemplate, "<#if entryScript?has_content>")
	if prePaint < 0 || entryScript < 0 || prePaint > entryScript || strings.Contains(accountTemplate, "matchMedia") {
		t.Error("account theme must resolve the shared product preference before React without a competing system-theme listener")
	}
	if !strings.Contains(accountCSS, "@media (max-width: 380px)") ||
		!strings.Contains(accountCSS, ".tl-account-return-label {\n    display: none;\n  }") {
		t.Error("account return label must collapse before the English label can overflow a narrow toolbar")
	}
}

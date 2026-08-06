package auth

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

type acceptanceRealmClient struct {
	ClientID                  string            `json:"clientId"`
	Name                      string            `json:"name"`
	RootURL                   string            `json:"rootUrl"`
	BaseURL                   string            `json:"baseUrl"`
	PublicClient              bool              `json:"publicClient"`
	StandardFlowEnabled       bool              `json:"standardFlowEnabled"`
	DirectAccessGrantsEnabled bool              `json:"directAccessGrantsEnabled"`
	ImplicitFlowEnabled       bool              `json:"implicitFlowEnabled"`
	ServiceAccountsEnabled    bool              `json:"serviceAccountsEnabled"`
	FullScopeAllowed          bool              `json:"fullScopeAllowed"`
	Secret                    string            `json:"secret"`
	RedirectURIs              []string          `json:"redirectUris"`
	Attributes                map[string]string `json:"attributes"`
	ProtocolMappers           []struct {
		Name   string            `json:"name"`
		Config map[string]string `json:"config"`
	} `json:"protocolMappers"`
}

func TestAcceptanceRealmUsesGatedRegistrationAndNativeClientLogin(t *testing.T) {
	data, err := os.ReadFile("../../acceptance/keycloak-realm.json")
	if err != nil {
		t.Fatal(err)
	}
	var realm struct {
		LoginTheme           string                  `json:"loginTheme"`
		AccountTheme         string                  `json:"accountTheme"`
		RegistrationAllowed  bool                    `json:"registrationAllowed"`
		VerifyEmail          bool                    `json:"verifyEmail"`
		BruteForceProtected  bool                    `json:"bruteForceProtected"`
		AccessTokenLifespan  int                     `json:"accessTokenLifespan"`
		RevokeRefreshToken   bool                    `json:"revokeRefreshToken"`
		RefreshTokenMaxReuse int                     `json:"refreshTokenMaxReuse"`
		Clients              []acceptanceRealmClient `json:"clients"`
	}
	if err := json.Unmarshal(data, &realm); err != nil {
		t.Fatal(err)
	}
	// Imports fail closed. The idempotent initializer opens registration only
	// after it has created or verified the managed bootstrap administrator.
	if realm.LoginTheme != "termlink-01b-v17" || realm.AccountTheme != "termlink-01b-v17" ||
		realm.RegistrationAllowed || realm.VerifyEmail || !realm.BruteForceProtected ||
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
		!containsString(windows.RedirectURIs, "http://127.0.0.1:43971/oauth2/callback") {
		t.Fatal("termlink-windows browser fallback policy is incomplete")
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
}

func containsString(values []string, target string) bool {
	for _, value := range values {
		if value == target {
			return true
		}
	}
	return false
}

func TestAcceptanceBootstrapFailsClosedBeforeOpeningRegistration(t *testing.T) {
	data, err := os.ReadFile("../../compose/acceptance.compose.yml")
	if err != nil {
		t.Fatal(err)
	}
	compose := string(data)
	disableRegistration := strings.Index(compose, "-s registrationAllowed=false")
	managedMarker := strings.Index(compose, "--rolename TERMLINK_BOOTSTRAP_MANAGED_V1")
	unmanagedAdminFailure := strings.Index(compose, "Refusing to grant SUPER_ADMIN to an unmanaged existing admin account.")
	openRegistration := strings.LastIndex(compose, "-s registrationAllowed=true")
	if disableRegistration < 0 || managedMarker < 0 || unmanagedAdminFailure < 0 || openRegistration < 0 ||
		!(disableRegistration < managedMarker && managedMarker < unmanagedAdminFailure && unmanagedAdminFailure < openRegistration) {
		t.Fatal("acceptance identity bootstrap must fail closed before opening registration")
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
	loginCSS := read("../../acceptance/keycloak-theme/termlink/login/resources/css/login.css")
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
		"login template accessible label":       `msg('termlinkBackToPersonalCenter')`,
		"login template theme transfer":         `data-theme-transfer`,
		"login pre-paint theme script":          `src="${url.resourcesPath}/js/theme-init.js"`,
		"login pre-paint stored theme":          `const storageKey = "termlink-theme"`,
		"login pre-paint transferred theme":     `searchParams.get(transferParameter)`,
		"login theme responsive account action": ".identity-account-link",
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
		"production image account theme":        `COPY acceptance/keycloak-theme/termlink/ /opt/keycloak/themes/termlink-01b-v17/`,
		"production runtime account theme":      `-s accountTheme=termlink-01b-v17`,
	} {
		var source string
		switch name {
		case "login template account target", "login template accessible label", "login template theme transfer", "login pre-paint theme script", "login SVG theme control", "login SVG account return":
			source = loginTemplate
		case "login pre-paint stored theme", "login pre-paint transferred theme":
			source = loginThemeInit
		case "login theme responsive account action":
			source = loginCSS
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
		case "production image account theme":
			source = keycloakDockerfile
		case "production runtime account theme":
			source = acceptanceCompose
		}
		if !strings.Contains(source, content) {
			t.Errorf("%s contract is missing", name)
		}
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

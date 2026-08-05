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

func TestAcceptanceRealmUsesGatedRegistrationAndAndroidNativeLogin(t *testing.T) {
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
	if realm.LoginTheme != "termlink-01b-v6" || realm.AccountTheme != "termlink-01b-v6" ||
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
	accountThemeProperties := read("../../acceptance/keycloak-theme/termlink/account/theme.properties")
	accountThemeInit := read("../../acceptance/keycloak-theme/termlink/account/resources/js/theme-init.js")
	accountCSS := read("../../acceptance/keycloak-theme/termlink/account/resources/css/account.css")
	keycloakDockerfile := read("../../acceptance/Keycloak.Dockerfile")
	acceptanceCompose := read("../../compose/acceptance.compose.yml")

	for name, content := range map[string]string{
		"login template account target":         `class="identity-account-link" href="/account"`,
		"login template accessible label":       `msg('termlinkBackToPersonalCenter')`,
		"login pre-paint theme script":          `src="${url.resourcesPath}/js/theme-init.js"`,
		"login pre-paint stored theme":          `localStorage.getItem("termlink-theme")`,
		"login theme responsive account action": ".identity-account-link",
		"Chinese personal-center copy":          "termlinkBackToPersonalCenter=返回个人中心",
		"English personal-center copy":          "termlinkBackToPersonalCenter=Back to account center",
		"account theme fallback target":         `referrer.href = "/account"`,
		"account theme fallback marker":         `data-termlink-account-link`,
		"account theme stable action group":     `data-termlink-account-actions`,
		"account theme product brand":           `tl-account-brand-copy`,
		"account pre-paint script order":        `scripts=js/theme-init.js js/theme.js`,
		"account pre-paint stored theme":        `localStorage.getItem("termlink-theme")`,
		"account theme return styling":          ".tl-account-return",
		"production image account theme":        `COPY acceptance/keycloak-theme/termlink/ /opt/keycloak/themes/termlink-01b-v6/`,
	} {
		var source string
		switch name {
		case "login template account target", "login template accessible label", "login pre-paint theme script":
			source = loginTemplate
		case "login pre-paint stored theme":
			source = loginThemeInit
		case "login theme responsive account action":
			source = loginCSS
		case "Chinese personal-center copy":
			source = zhMessages
		case "English personal-center copy":
			source = enMessages
		case "account theme fallback target", "account theme fallback marker", "account theme stable action group", "account theme product brand":
			source = accountScript
		case "account pre-paint script order":
			source = accountThemeProperties
		case "account pre-paint stored theme":
			source = accountThemeInit
		case "account theme return styling":
			source = accountCSS
		case "production image account theme":
			source = keycloakDockerfile
		}
		if !strings.Contains(source, content) {
			t.Errorf("%s contract is missing", name)
		}
	}
	if strings.Contains(acceptanceCompose, "../acceptance/keycloak-theme/termlink:/opt/keycloak/themes/") {
		t.Error("production acceptance must exercise the identity theme baked into the Keycloak image")
	}
}

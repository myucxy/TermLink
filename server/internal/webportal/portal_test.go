package webportal

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const testPublicHost = "termlink.example:8910"

type testOIDCFlow struct {
	state         string
	nonce         string
	challenge     string
	theme         string
	code          string
	verifier      string
	expectedNonce string
	identity      accountIdentity
	exchangeErr   error
}

type testPortalRepository struct {
	account          store.PortalAccount
	dashboard        store.PortalAdminDashboard
	dashboardErr     error
	roleErr          error
	statusErr        error
	loginFailureCode string
}

func newTestPortalRepository() *testPortalRepository {
	return &testPortalRepository{account: store.PortalAccount{
		ID: uuid.MustParse("11111111-1111-4111-8111-111111111111"), Role: store.RoleUser, Status: "ACTIVE",
		CreatedAt: time.Date(2026, time.August, 4, 9, 0, 0, 0, time.UTC),
	}}
}

func (r *testPortalRepository) SyncPortalIdentity(_ context.Context, _ [32]byte, asserted store.Role, _ string) (store.PortalAccount, error) {
	if r.statusErr != nil {
		return store.PortalAccount{}, r.statusErr
	}
	if asserted == store.RoleSuperAdmin {
		r.account.Role = store.RoleSuperAdmin
	}
	return r.account, nil
}

func (r *testPortalRepository) RecordPortalLoginFailure(_ context.Context, _ string, reason string) error {
	r.loginFailureCode = reason
	return nil
}

func (r *testPortalRepository) GetPortalAccount(context.Context, [32]byte) (store.PortalAccount, error) {
	if r.statusErr != nil {
		return store.PortalAccount{}, r.statusErr
	}
	return r.account, nil
}

func (r *testPortalRepository) PortalAdminDashboard(context.Context, uuid.UUID) (store.PortalAdminDashboard, error) {
	return r.dashboard, r.dashboardErr
}

func (r *testPortalRepository) UpdatePortalAccountRole(context.Context, uuid.UUID, uuid.UUID, store.Role, string) error {
	return r.roleErr
}

func (r *testPortalRepository) UpdatePortalAccountStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return r.statusErr
}

func (f *testOIDCFlow) AuthorizationURL(state, nonce, challenge, theme string) string {
	f.state = state
	f.nonce = nonce
	f.challenge = challenge
	f.theme = theme
	query := url.Values{"state": {state}, "nonce": {nonce}, "code_challenge": {challenge}}
	if theme != "" {
		query.Set(themeQueryParameter, theme)
	}
	return "https://identity.example/authorize?" + query.Encode()
}

func (f *testOIDCFlow) ExchangeAndVerify(_ context.Context, code, verifier, expectedNonce string) (accountIdentity, error) {
	f.code = code
	f.verifier = verifier
	f.expectedNonce = expectedNonce
	if f.exchangeErr != nil {
		return accountIdentity{}, f.exchangeErr
	}
	return f.identity, nil
}

func (*testOIDCFlow) LogoutURL() string { return "https://identity.example/logout" }

func (*testOIDCFlow) AccountURL() string {
	return "https://identity.example/realms/termlink/account/?referrer=termlink-web&referrer_uri=" +
		url.QueryEscape("https://"+testPublicHost+"/account")
}

func newTestPortal(t *testing.T, flow *testOIDCFlow) *Portal {
	t.Helper()
	clock := time.Date(2026, time.August, 4, 10, 0, 0, 0, time.UTC)
	config := Config{
		PublicURL:   "https://" + testPublicHost,
		Issuer:      "https://identity.example/realms/termlink",
		ClientID:    "termlink-web",
		RedirectURI: "https://" + testPublicHost + "/auth/callback",
		CookieKey:   []byte("0123456789abcdef0123456789abcdef"),
		SubjectKey:  []byte("abcdef0123456789abcdef0123456789"),
		Repository:  newTestPortalRepository(),
		Clock:       func() time.Time { return clock },
	}
	origin, err := validateConfig(config)
	if err != nil {
		t.Fatalf("validate test portal config: %v", err)
	}
	portal, err := newPortal(config, origin, flow)
	if err != nil {
		t.Fatalf("create test portal: %v", err)
	}
	return portal
}

func TestValidateConfigRejectsPublicURLWithPath(t *testing.T) {
	config := Config{
		PublicURL:   "https://" + testPublicHost + "/console",
		Issuer:      "https://identity.example/realms/termlink",
		ClientID:    "termlink-web",
		RedirectURI: "https://" + testPublicHost + "/auth/callback",
		CookieKey:   []byte("0123456789abcdef0123456789abcdef"),
		SubjectKey:  []byte("abcdef0123456789abcdef0123456789"),
		Repository:  newTestPortalRepository(),
	}
	if _, err := validateConfig(config); err == nil {
		t.Fatal("validateConfig accepted a public URL with a non-root path")
	}
}

func performRequest(handler http.Handler, method, target string, body io.Reader, cookies ...*http.Cookie) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, "https://"+testPublicHost+target, body)
	request.Host = testPublicHost
	for _, cookie := range cookies {
		request.AddCookie(cookie)
	}
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, request)
	return recorder
}

func performRequestOnHost(handler http.Handler, method, host, target string, body io.Reader, cookies ...*http.Cookie) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, "https://"+host+target, body)
	request.Host = host
	for _, cookie := range cookies {
		request.AddCookie(cookie)
	}
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, request)
	return recorder
}

func responseCookie(t *testing.T, response *http.Response, name string) *http.Cookie {
	t.Helper()
	for _, cookie := range response.Cookies() {
		if cookie.Name == name && cookie.MaxAge >= 0 {
			return cookie
		}
	}
	t.Fatalf("response cookie %q was not set", name)
	return nil
}

func startLogin(t *testing.T, portal *Portal, flow *testOIDCFlow) (*http.Cookie, oauthState) {
	return startLoginAt(t, portal, flow, "/account")
}

func startLoginAt(t *testing.T, portal *Portal, flow *testOIDCFlow, returnTo string) (*http.Cookie, oauthState) {
	t.Helper()
	target := "/login"
	if returnTo != "/account" {
		target += "?return_to=" + url.QueryEscape(returnTo)
	}
	recorder := performRequest(portal.Handler(), http.MethodGet, target, nil)
	if recorder.Code != http.StatusFound {
		t.Fatalf("login status = %d, want %d", recorder.Code, http.StatusFound)
	}
	response := recorder.Result()
	defer response.Body.Close()
	cookie := responseCookie(t, response, oauthCookieName)
	var state oauthState
	if err := portal.oauthCodec.open(cookie.Value, &state); err != nil {
		t.Fatalf("decode OAuth state cookie: %v", err)
	}
	if state.State == "" || state.Nonce == "" || state.Verifier == "" || state.ReturnTo != returnTo {
		t.Fatalf("OAuth state cookie is incomplete: %+v", state)
	}
	if flow.state != state.State || flow.nonce != state.Nonce {
		t.Fatal("authorization URL did not receive the sealed state and nonce")
	}
	challenge := sha256.Sum256([]byte(state.Verifier))
	wantChallenge := base64.RawURLEncoding.EncodeToString(challenge[:])
	if flow.challenge != wantChallenge {
		t.Fatalf("PKCE challenge = %q, want %q", flow.challenge, wantChallenge)
	}
	return cookie, state
}

func startLoginWithParams(t *testing.T, portal *Portal, flow *testOIDCFlow, returnTo, theme string) (*http.Cookie, oauthState) {
	t.Helper()
	query := url.Values{}
	if returnTo != "" {
		query.Set("return_to", returnTo)
	}
	if theme != "" {
		query.Set(themeQueryParameter, theme)
	}
	target := "/login"
	if encoded := query.Encode(); encoded != "" {
		target += "?" + encoded
	}
	recorder := performRequest(portal.Handler(), http.MethodGet, target, nil)
	if recorder.Code != http.StatusFound {
		t.Fatalf("login status = %d, want %d", recorder.Code, http.StatusFound)
	}
	response := recorder.Result()
	defer response.Body.Close()
	cookie := responseCookie(t, response, oauthCookieName)
	var state oauthState
	if err := portal.oauthCodec.open(cookie.Value, &state); err != nil {
		t.Fatalf("decode OAuth state cookie: %v", err)
	}
	if state.State == "" || state.Nonce == "" || state.Verifier == "" {
		t.Fatalf("OAuth state cookie is incomplete: %+v", state)
	}
	if flow.state != state.State || flow.nonce != state.Nonce {
		t.Fatal("authorization URL did not receive the sealed state and nonce")
	}
	return cookie, state
}

func completeLogin(t *testing.T, portal *Portal, flow *testOIDCFlow) (*http.Cookie, webSession) {
	return completeLoginAt(t, portal, flow, "/account")
}

func completeLoginAt(t *testing.T, portal *Portal, flow *testOIDCFlow, wantLocation string) (*http.Cookie, webSession) {
	t.Helper()
	oauthCookie, state := startLoginAt(t, portal, flow, wantLocation)
	target := "/auth/callback?code=production-code&state=" + url.QueryEscape(state.State)
	recorder := performRequest(portal.Handler(), http.MethodGet, target, nil, oauthCookie)
	if recorder.Code != http.StatusSeeOther || recorder.Header().Get("Location") != wantLocation {
		t.Fatalf("callback response = %d %q, want 303 %s", recorder.Code, recorder.Header().Get("Location"), wantLocation)
	}
	if flow.code != "production-code" || flow.verifier != state.Verifier || flow.expectedNonce != state.Nonce {
		t.Fatal("callback did not pass the code, verifier, and nonce to OIDC verification")
	}
	response := recorder.Result()
	defer response.Body.Close()
	sessionCookie := responseCookie(t, response, sessionCookieName)
	var session webSession
	if err := portal.sessionCodec.open(sessionCookie.Value, &session); err != nil {
		t.Fatalf("decode session cookie: %v", err)
	}
	return sessionCookie, session
}

func TestPortalHomeAndSecurityHeaders(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	recorder := performRequest(portal.Handler(), http.MethodGet, "/", nil)
	if recorder.Code != http.StatusOK {
		t.Fatalf("home status = %d, want 200", recorder.Code)
	}
	body := recorder.Body.String()
	for _, text := range []string{"离开电脑", "真实终端", "端到端加密", "安全登录", "/assets/termlink-app-icon.svg"} {
		if !strings.Contains(body, text) {
			t.Fatalf("home page omitted %q", text)
		}
	}
	for name, want := range map[string]string{
		"Content-Security-Policy":   "default-src 'none'",
		"Strict-Transport-Security": "max-age=31536000",
		"X-Content-Type-Options":    "nosniff",
		"X-Frame-Options":           "DENY",
		"Referrer-Policy":           "no-referrer",
	} {
		if got := recorder.Header().Get(name); !strings.Contains(got, want) {
			t.Fatalf("%s = %q, want it to contain %q", name, got, want)
		}
	}

	css := performRequest(portal.Handler(), http.MethodGet, "/assets/site.css", nil)
	if css.Code != http.StatusOK || !strings.HasPrefix(css.Header().Get("Content-Type"), "text/css") || css.Body.Len() < 1000 {
		t.Fatalf("embedded stylesheet response is incomplete: status=%d bytes=%d", css.Code, css.Body.Len())
	}
	themeInit := performRequest(portal.Handler(), http.MethodGet, "/assets/theme-init.js", nil)
	if themeInit.Code != http.StatusOK || !strings.HasPrefix(themeInit.Header().Get("Content-Type"), "text/javascript") ||
		!strings.Contains(themeInit.Body.String(), "termlink-theme") {
		t.Fatalf("embedded pre-paint theme script response is incomplete: status=%d bytes=%d", themeInit.Code, themeInit.Body.Len())
	}
	theme := performRequest(portal.Handler(), http.MethodGet, "/assets/theme.js", nil)
	if theme.Code != http.StatusOK || !strings.HasPrefix(theme.Header().Get("Content-Type"), "text/javascript") ||
		!strings.Contains(theme.Body.String(), "termlink-theme") {
		t.Fatalf("embedded theme script response is incomplete: status=%d bytes=%d", theme.Code, theme.Body.Len())
	}
	for _, asset := range []string{"/assets/termlink-app-icon.svg", "/assets/termlink-favicon.svg"} {
		icon := performRequest(portal.Handler(), http.MethodGet, asset, nil)
		if icon.Code != http.StatusOK || icon.Header().Get("Content-Type") != "image/svg+xml" ||
			!strings.Contains(icon.Body.String(), "TermLink") {
			t.Fatalf("embedded brand asset %q is incomplete: status=%d bytes=%d", asset, icon.Code, icon.Body.Len())
		}
	}
}

func TestPortalTemplatesApplyThemeBeforeStyles(t *testing.T) {
	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html"} {
		data, err := portalAssets.ReadFile("assets/" + name)
		if err != nil {
			t.Fatal(err)
		}
		html := string(data)
		initIndex := strings.Index(html, `/assets/theme-init.js?v=01b-v11`)
		styleIndex := strings.Index(html, `/assets/site.css?v=01b-v11`)
		if initIndex < 0 || styleIndex < 0 || initIndex > styleIndex {
			t.Errorf("%s must restore the selected theme before loading product styles", name)
		}
	}
}

func TestPortalAccountResponsiveAndSVGControlContract(t *testing.T) {
	readAsset := func(name string) string {
		t.Helper()
		data, err := portalAssets.ReadFile("assets/" + name)
		if err != nil {
			t.Fatal(err)
		}
		return string(data)
	}

	siteCSS := readAsset("site.css")
	for name, contract := range map[string]string{
		"small-desktop hero breakpoint":   `@media (max-width: 1280px)`,
		"account tablet breakpoint":       `@media (max-width: 940px)`,
		"tablet hero stacking":            `.hero-copy { width: 100%; max-width: 680px; }`,
		"flow-positioned tablet terminal": `margin: 42px 0 0 auto;`,
		"account shrinkable workspace":    `min-width: 0;`,
		"long identity wrapping":          `overflow-wrap: anywhere;`,
		"long display-name wrapping":      `.profile-panel strong`,
		"accessible tertiary text":        `--text-3: #606b7d;`,
	} {
		if !strings.Contains(siteCSS, contract) {
			t.Errorf("%s is missing", name)
		}
	}

	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html"} {
		html := readAsset(name)
		if !strings.Contains(html, `class="theme-icon-moon"`) || !strings.Contains(html, `class="theme-icon-sun"`) {
			t.Errorf("%s must use the shared SVG theme icon", name)
		}
		if strings.ContainsAny(html, "◐☼☾") {
			t.Errorf("%s must not depend on platform theme glyphs", name)
		}
	}

	adminHTML := readAsset("admin.html")
	if !strings.Contains(adminHTML, `<span class="icon-badge" aria-hidden="true"><svg`) || strings.ContainsAny(adminHTML, "⌁◇") {
		t.Error("administrator decorative badges must use product SVG icons")
	}
	if strings.ContainsAny(readAsset("theme.js"), "☼☾") {
		t.Error("theme behavior must not rewrite controls with platform glyphs")
	}
	adminCSS := readAsset("admin.css")
	if !strings.Contains(adminCSS, "scrollbar-width: thin;") ||
		!strings.Contains(adminHTML, `/assets/admin.css?v=01b-v11`) {
		t.Error("mobile administrator navigation must use the versioned product scrollbar styling")
	}
	if !strings.Contains(siteCSS, `--shadow-color: rgba(30, 40, 60, 0.16);`) ||
		!strings.Contains(adminCSS, `color-mix(in srgb, var(--shadow-color)`) ||
		strings.Contains(adminCSS, `color-mix(in srgb, var(--shadow)`) {
		t.Error("administrator elevation must mix a color token instead of the complete shadow declaration")
	}
	if strings.Contains(readAsset("theme.js"), `setAttribute("aria-pressed"`) {
		t.Error("theme action buttons must not expose toggle-button pressed state")
	}
	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html"} {
		if strings.Contains(readAsset(name), "aria-pressed") {
			t.Errorf("%s theme action must not expose toggle-button pressed state", name)
		}
	}
}

func TestPortalSeparatesUserAndAdministratorDestinations(t *testing.T) {
	userFlow := &testOIDCFlow{identity: accountIdentity{Subject: "user-subject", Username: "user"}}
	userPortal := newTestPortal(t, userFlow)
	userCookie, _ := completeLogin(t, userPortal, userFlow)
	denied := performRequest(userPortal.Handler(), http.MethodGet, "/admin", nil, userCookie)
	if denied.Code != http.StatusForbidden || !strings.Contains(denied.Body.String(), "无权访问管理台") {
		t.Fatalf("regular user admin response = %d %q", denied.Code, denied.Body.String())
	}
	if !strings.Contains(denied.Body.String(), `href="/account">返回个人中心</a>`) {
		t.Fatal("administrator error response must provide a direct return to the account center")
	}

	adminFlow := &testOIDCFlow{identity: accountIdentity{
		Subject: "admin-subject", Username: "admin", Name: "TermLink Administrator",
		Roles: []string{"offline_access", "SUPER_ADMIN"},
	}}
	adminPortal := newTestPortal(t, adminFlow)
	guestAccount := performRequest(adminPortal.Handler(), http.MethodGet, "/account", nil)
	if guestAccount.Code != http.StatusSeeOther || guestAccount.Header().Get("Location") != "/login?return_to=/account" {
		t.Fatalf("guest account response = %d %q", guestAccount.Code, guestAccount.Header().Get("Location"))
	}
	accountCookie, accountSession := completeLogin(t, adminPortal, adminFlow)
	if accountSession.Role != string(store.RoleSuperAdmin) {
		t.Fatalf("admin account session role = %q", accountSession.Role)
	}
	accountPage := performRequest(adminPortal.Handler(), http.MethodGet, "/account", nil, accountCookie)
	if accountPage.Code != http.StatusOK || !strings.Contains(accountPage.Body.String(), "进入管理台") ||
		!strings.Contains(accountPage.Body.String(), `href="/account/identity" data-theme-transfer`) ||
		!strings.Contains(accountPage.Body.String(), `href="/admin">管理台</a>`) {
		t.Fatalf("admin account page = %d %q", accountPage.Code, accountPage.Body.String())
	}
	home := performRequest(adminPortal.Handler(), http.MethodGet, "/", nil, accountCookie)
	if !strings.Contains(home.Body.String(), `href="/account">个人中心`) ||
		!strings.Contains(home.Body.String(), `href="/admin">管理台`) {
		t.Fatal("signed-in administrator home must expose both account center and console")
	}
	adminCookie, session := completeLoginAt(t, adminPortal, adminFlow, "/admin")
	if session.Role != string(store.RoleSuperAdmin) {
		t.Fatalf("admin session role = %q", session.Role)
	}
	console := performRequest(adminPortal.Handler(), http.MethodGet, "/admin?section=accounts", nil, adminCookie)
	if console.Code != http.StatusOK {
		t.Fatalf("admin console status = %d, body=%q", console.Code, console.Body.String())
	}
	for _, text := range []string{"Server Console", "账号与角色", "SUPER_ADMIN", "终端正文始终不可见", "返回个人中心"} {
		if !strings.Contains(console.Body.String(), text) {
			t.Fatalf("admin console omitted %q", text)
		}
	}
	identitySection := performRequest(adminPortal.Handler(), http.MethodGet, "/admin?section=identity", nil, adminCookie)
	if identitySection.Code != http.StatusOK ||
		!strings.Contains(identitySection.Body.String(), `href="/account/identity" data-theme-transfer`) {
		t.Fatalf("admin identity section = %d %q", identitySection.Code, identitySection.Body.String())
	}
}

func TestPortalOverviewKeepsFullAuditLogOutOfTheDashboard(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"},
	}}
	portal := newTestPortal(t, flow)
	repository := portal.config.Repository.(*testPortalRepository)
	for index := 0; index < 10; index++ {
		repository.dashboard.AuditEvents = append(repository.dashboard.AuditEvents, store.PortalAuditEvent{
			Action: "overview-event-" + string(rune('a'+index)), Result: "SUCCEEDED",
		})
	}
	cookie, _ := completeLoginAt(t, portal, flow, "/admin")

	overview := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if overview.Code != http.StatusOK || strings.Count(overview.Body.String(), "overview-event-") != 7 {
		t.Fatalf("overview audit preview status/count = %d/%d", overview.Code, strings.Count(overview.Body.String(), "overview-event-"))
	}
	fullAudit := performRequest(portal.Handler(), http.MethodGet, "/admin?section=audit", nil, cookie)
	if fullAudit.Code != http.StatusOK || strings.Count(fullAudit.Body.String(), "overview-event-") != 10 {
		t.Fatalf("full audit status/count = %d/%d", fullAudit.Code, strings.Count(fullAudit.Body.String(), "overview-event-"))
	}
}

func TestPortalAdminAndAuditorRolesAreReadOnly(t *testing.T) {
	for _, role := range []store.Role{store.RoleAdmin, store.RoleAuditor} {
		t.Run(string(role), func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{Subject: "console-subject", Username: "console-user", Roles: []string{string(role)}}}
			portal := newTestPortal(t, flow)
			portal.config.Repository.(*testPortalRepository).account.Role = role
			cookie, session := completeLoginAt(t, portal, flow, "/admin")
			console := performRequest(portal.Handler(), http.MethodGet, "/admin?section=accounts", nil, cookie)
			if console.Code != http.StatusOK || !strings.Contains(console.Body.String(), "只读") {
				t.Fatalf("%s console response = %d %q", role, console.Code, console.Body.String())
			}

			targetID := uuid.MustParse("22222222-2222-4222-8222-222222222222")
			request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/role", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)+"&role=USER"))
			request.Host = testPublicHost
			request.Header.Set("Origin", "https://"+testPublicHost)
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			request.AddCookie(cookie)
			recorder := httptest.NewRecorder()
			portal.Handler().ServeHTTP(recorder, request)
			if recorder.Code != http.StatusForbidden {
				t.Fatalf("%s account mutation status = %d, want 403", role, recorder.Code)
			}
		})
	}
}

func TestPortalProtectsAdministratorMutationWithCSRFAndLastSuperAdminRule(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"}}}
	portal := newTestPortal(t, flow)
	cookie, session := completeLoginAt(t, portal, flow, "/admin")
	targetID := uuid.MustParse("22222222-2222-4222-8222-222222222222")

	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/role", strings.NewReader("csrf=wrong&role=USER"))
	request.Host = testPublicHost
	request.Header.Set("Origin", "https://"+testPublicHost)
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.AddCookie(cookie)
	recorder := httptest.NewRecorder()
	portal.Handler().ServeHTTP(recorder, request)
	if recorder.Code != http.StatusForbidden {
		t.Fatalf("admin mutation without CSRF status = %d, want 403", recorder.Code)
	}

	repository := portal.config.Repository.(*testPortalRepository)
	repository.roleErr = store.ErrLastSuperAdmin
	request = httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/role", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)+"&role=USER"))
	request.Host = testPublicHost
	request.Header.Set("Origin", "https://"+testPublicHost)
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.AddCookie(cookie)
	recorder = httptest.NewRecorder()
	portal.Handler().ServeHTTP(recorder, request)
	if recorder.Code != http.StatusConflict || !strings.Contains(recorder.Body.String(), "唯一超级管理员") {
		t.Fatalf("last super admin response = %d %q", recorder.Code, recorder.Body.String())
	}
}

func TestPortalPublishesCommittedAccountDisable(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"}}}
	portal := newTestPortal(t, flow)
	cookie, session := completeLoginAt(t, portal, flow, "/admin")
	targetID := uuid.MustParse("22222222-2222-4222-8222-222222222222")
	var publishedID uuid.UUID
	var publishedStatus string
	portal.config.AccountStatusChanged = func(_ context.Context, accountID uuid.UUID, status string) error {
		publishedID = accountID
		publishedStatus = status
		return nil
	}

	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/status", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)+"&status=LOCKED"))
	request.Host = testPublicHost
	request.Header.Set("Origin", "https://"+testPublicHost)
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.AddCookie(cookie)
	recorder := httptest.NewRecorder()
	portal.Handler().ServeHTTP(recorder, request)
	if recorder.Code != http.StatusSeeOther {
		t.Fatalf("account disable status=%d, body=%q", recorder.Code, recorder.Body.String())
	}
	if publishedID != targetID || publishedStatus != "LOCKED" {
		t.Fatalf("published account status id=%s status=%q", publishedID, publishedStatus)
	}
}

func TestPortalLoginUsesPKCEAndSecureHostCookie(t *testing.T) {
	flow := &testOIDCFlow{}
	portal := newTestPortal(t, flow)
	cookie, _ := startLogin(t, portal, flow)
	if !cookie.Secure || !cookie.HttpOnly || cookie.Path != "/" || cookie.Domain != "" || cookie.SameSite != http.SameSiteLaxMode {
		t.Fatalf("OAuth cookie attributes are unsafe: %+v", cookie)
	}
	if !strings.HasPrefix(cookie.Name, "__Host-") || cookie.MaxAge <= 0 {
		t.Fatalf("OAuth cookie does not satisfy __Host- requirements: %+v", cookie)
	}
}

func TestPortalTransfersOnlyValidatedThemeToOIDC(t *testing.T) {
	flow := &testOIDCFlow{}
	portal := newTestPortal(t, flow)

	dark := performRequest(portal.Handler(), http.MethodGet, "/login?termlink_theme=dark", nil)
	if dark.Code != http.StatusFound || flow.theme != "dark" {
		t.Fatalf("dark theme login status/theme = %d/%q", dark.Code, flow.theme)
	}
	location, err := url.Parse(dark.Header().Get("Location"))
	if err != nil || location.Query().Get(themeQueryParameter) != "dark" {
		t.Fatalf("dark theme authorization location = %q", dark.Header().Get("Location"))
	}

	invalid := performRequest(portal.Handler(), http.MethodGet, "/login?termlink_theme=attacker", nil)
	if invalid.Code != http.StatusFound || flow.theme != "" {
		t.Fatalf("invalid theme login status/theme = %d/%q", invalid.Code, flow.theme)
	}
}

func TestValidateIdentityAccountURLFailsClosed(t *testing.T) {
	config := Config{
		PublicURL: "https://" + testPublicHost,
		Issuer:    "https://identity.example/realms/termlink",
		ClientID:  "termlink-web",
	}
	valid := (&testOIDCFlow{}).AccountURL()
	if got, err := validateIdentityAccountURL(config, valid); err != nil || got == "" {
		t.Fatalf("valid identity account URL = %q, %v", got, err)
	}
	for name, candidate := range map[string]string{
		"wrong origin": "https://attacker.example/realms/termlink/account/",
		"wrong path":   "https://identity.example/realms/termlink/admin/",
		"bad referrer": "https://identity.example/realms/termlink/account/?referrer=attacker",
		"bad return":   "https://identity.example/realms/termlink/account/?referrer_uri=https%3A%2F%2Fattacker.example%2Faccount",
		"extra query":  "https://identity.example/realms/termlink/account/?redirect=https%3A%2F%2Fattacker.example",
	} {
		if _, err := validateIdentityAccountURL(config, candidate); err == nil {
			t.Errorf("%s identity account URL was accepted", name)
		}
	}
}

func TestPortalInternalErrorsKeepProductRecoveryNavigation(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"},
	}}
	portal := newTestPortal(t, flow)
	repository := portal.config.Repository.(*testPortalRepository)
	repository.account.Role = store.RoleSuperAdmin
	cookie, _ := completeLoginAt(t, portal, flow, "/admin")
	repository.dashboardErr = errors.New("dashboard unavailable fixture")

	response := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if response.Code != http.StatusInternalServerError ||
		!strings.Contains(response.Header().Get("Content-Type"), "text/html") ||
		!strings.Contains(response.Body.String(), `href="/account">返回个人中心</a>`) ||
		!strings.Contains(response.Body.String(), "/assets/site.css") {
		t.Fatalf("internal error recovery page = %d %q", response.Code, response.Body.String())
	}
}

func TestPortalCallbackAccountEscapingAndCSRFLogout(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject:  "subject-1",
		Username: `<script>alert("user")</script>`,
		Name:     `<img src=x onerror=alert("name")>`,
		Email:    "user@example.test",
	}}
	portal := newTestPortal(t, flow)
	sessionCookie, session := completeLogin(t, portal, flow)
	if !sessionCookie.Secure || !sessionCookie.HttpOnly || sessionCookie.Path != "/" || sessionCookie.Domain != "" || sessionCookie.SameSite != http.SameSiteLaxMode {
		t.Fatalf("session cookie attributes are unsafe: %+v", sessionCookie)
	}
	if session.Subject != flow.identity.Subject || session.CSRF == "" || session.ExpiresAtMS <= portal.now().UnixMilli() {
		t.Fatalf("session cookie payload is incomplete: %+v", session)
	}

	account := performRequest(portal.Handler(), http.MethodGet, "/account", nil, sessionCookie)
	if account.Code != http.StatusOK {
		t.Fatalf("account status = %d, want 200", account.Code)
	}
	body := account.Body.String()
	if strings.Contains(body, flow.identity.Username) || strings.Contains(body, flow.identity.Name) {
		t.Fatal("account page rendered unescaped identity claims")
	}
	if !strings.Contains(body, "&lt;script&gt;") || !strings.Contains(body, "&lt;img") {
		t.Fatal("account page did not safely render escaped identity claims")
	}

	missingCSRF := performRequest(portal.Handler(), http.MethodPost, "/logout", strings.NewReader("csrf=wrong"), sessionCookie)
	missingCSRF.Header().Set("Content-Type", "application/x-www-form-urlencoded")
	if missingCSRF.Code != http.StatusForbidden {
		t.Fatalf("logout without CSRF status = %d, want 403", missingCSRF.Code)
	}

	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/logout", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)))
	request.Host = testPublicHost
	request.Header.Set("Origin", "https://"+testPublicHost)
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.AddCookie(sessionCookie)
	recorder := httptest.NewRecorder()
	portal.Handler().ServeHTTP(recorder, request)
	if recorder.Code != http.StatusSeeOther || recorder.Header().Get("Location") != "https://identity.example/logout" {
		t.Fatalf("valid logout response = %d %q", recorder.Code, recorder.Header().Get("Location"))
	}
	cleared := false
	for _, cookie := range recorder.Result().Cookies() {
		if cookie.Name == sessionCookieName && cookie.MaxAge < 0 && cookie.Secure && cookie.HttpOnly {
			cleared = true
		}
	}
	if !cleared {
		t.Fatal("valid logout did not securely clear the session cookie")
	}
}

func TestPortalRejectsCrossSiteAndTamperedSessions(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "subject-1", Username: "safe-user"}}
	portal := newTestPortal(t, flow)
	sessionCookie, session := completeLogin(t, portal, flow)

	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/logout", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)))
	request.Host = testPublicHost
	request.Header.Set("Origin", "https://attacker.example")
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.AddCookie(sessionCookie)
	recorder := httptest.NewRecorder()
	portal.Handler().ServeHTTP(recorder, request)
	if recorder.Code != http.StatusForbidden {
		t.Fatalf("cross-site logout status = %d, want 403", recorder.Code)
	}

	tampered := *sessionCookie
	index := len(tampered.Value) / 2
	if index < 0 || index >= len(tampered.Value) {
		t.Fatal("session cookie was empty")
	}
	replacement := byte('A')
	if tampered.Value[index] == replacement {
		replacement = 'B'
	}
	tampered.Value = tampered.Value[:index] + string(replacement) + tampered.Value[index+1:]
	account := performRequest(portal.Handler(), http.MethodGet, "/account", nil, &tampered)
	if account.Code != http.StatusSeeOther || account.Header().Get("Location") != "/login?return_to=/account" {
		t.Fatalf("tampered session response = %d %q, want 303 account login", account.Code, account.Header().Get("Location"))
	}

	badStateFlow := &testOIDCFlow{exchangeErr: errors.New("must not be called")}
	badStatePortal := newTestPortal(t, badStateFlow)
	oauthCookie, state := startLogin(t, badStatePortal, badStateFlow)
	callback := performRequest(badStatePortal.Handler(), http.MethodGet, "/auth/callback?code=code&state="+url.QueryEscape(state.State+"-tampered"), nil, oauthCookie)
	if callback.Code != http.StatusBadRequest || badStateFlow.code != "" {
		t.Fatalf("tampered OAuth state response = %d; exchange code = %q", callback.Code, badStateFlow.code)
	}
}

func TestPortalAuditsVerifiedFlowLoginFailureWithoutSensitiveValues(t *testing.T) {
	flow := &testOIDCFlow{exchangeErr: errors.New("verification failed fixture")}
	portal := newTestPortal(t, flow)
	oauthCookie, state := startLogin(t, portal, flow)
	callback := performRequest(portal.Handler(), http.MethodGet, "/auth/callback?code=opaque-test-code&state="+url.QueryEscape(state.State), nil, oauthCookie)
	if callback.Code != http.StatusUnauthorized {
		t.Fatalf("failed token verification status=%d, want 401", callback.Code)
	}
	repository := portal.config.Repository.(*testPortalRepository)
	if repository.loginFailureCode != "token_verification_failed" {
		t.Fatalf("login audit reason=%q", repository.loginFailureCode)
	}
}

func TestPortalOriginComparisonUsesHTTPSOriginSemantics(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	for _, origin := range []string{
		"https://termlink.example:8910",
		"https://TERMLINK.EXAMPLE:8910/",
	} {
		if !portal.usesPublicOrigin(origin) {
			t.Fatalf("usesPublicOrigin(%q) = false, want true", origin)
		}
	}
	for _, origin := range []string{
		"http://termlink.example:8910",
		"https://termlink.example",
		"https://attacker.example",
		"https://termlink.example:8910/path",
		"https://termlink.example:8910/?query=1",
		"null",
	} {
		if portal.usesPublicOrigin(origin) {
			t.Fatalf("usesPublicOrigin(%q) = true, want false", origin)
		}
	}
}

func TestPortalFormOriginAllowsOnlySameOriginProxyFallback(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/logout", nil)
	request.Host = testPublicHost
	request.Header.Set("Origin", "http://lucky-upstream:8080")
	request.Header.Set("Sec-Fetch-Site", "same-origin")
	if !portal.usesTrustedFormOrigin(request) {
		t.Fatal("same-origin browser request rewritten by the trusted proxy was rejected")
	}

	request.Header.Set("Sec-Fetch-Site", "cross-site")
	if portal.usesTrustedFormOrigin(request) {
		t.Fatal("cross-site browser request was accepted")
	}
	request.Header.Set("Sec-Fetch-Site", "same-origin")
	request.Host = "attacker.example"
	if portal.usesTrustedFormOrigin(request) {
		t.Fatal("request for an unexpected host was accepted")
	}
}

func assertIdentityLoginRedirect(t *testing.T, response *httptest.ResponseRecorder, wantHost, wantTheme string) {
	t.Helper()
	if response.Code != http.StatusSeeOther {
		t.Fatalf("identity redirect status = %d, want 303; location=%q", response.Code, response.Header().Get("Location"))
	}
	location, err := url.Parse(response.Header().Get("Location"))
	if err != nil {
		t.Fatalf("parse identity redirect location %q: %v", response.Header().Get("Location"), err)
	}
	if location.Path != "/login" || (wantHost == "" && location.Host != "") || (wantHost != "" && !strings.EqualFold(location.Host, wantHost)) {
		t.Fatalf("identity redirect location = %q, want /login host %q", response.Header().Get("Location"), wantHost)
	}
	query := location.Query()
	if query.Get("return_to") != "/account/identity" {
		t.Fatalf("identity login return_to = %q, want /account/identity", query.Get("return_to"))
	}
	if wantTheme == "" {
		if _, ok := query[themeQueryParameter]; ok {
			t.Fatalf("identity login unexpectedly propagated theme %q", query.Get(themeQueryParameter))
		}
	} else if query.Get(themeQueryParameter) != wantTheme {
		t.Fatalf("identity login theme = %q, want %q", query.Get(themeQueryParameter), wantTheme)
	}
}

func TestPortalIdentityAlwaysStartsFreshOIDCLogin(t *testing.T) {
	tests := []struct {
		name       string
		themeQuery string
		theme      string
		session    string
	}{
		{name: "guest"},
		{name: "recent session", themeQuery: "DARK", theme: "dark", session: "recent"},
		{name: "expired session", themeQuery: "attacker", session: "expired"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{Subject: "identity-subject", Username: "identity-user"}}
			portal := newTestPortal(t, flow)
			var sessionCookie *http.Cookie
			switch tt.session {
			case "recent":
				sessionCookie, _ = completeLogin(t, portal, flow)
			case "expired":
				_, session := completeLogin(t, portal, flow)
				session.AuthenticatedAtMS = portal.now().Add(-(sensitiveActionTTL + time.Second)).UnixMilli()
				sealed, err := portal.sessionCodec.seal(session)
				if err != nil {
					t.Fatalf("seal expired session fixture: %v", err)
				}
				sessionCookie = &http.Cookie{Name: sessionCookieName, Value: sealed}
			}

			target := "/account/identity"
			if tt.themeQuery != "" {
				target += "?" + themeQueryParameter + "=" + url.QueryEscape(tt.themeQuery)
			}
			var response *httptest.ResponseRecorder
			if sessionCookie == nil {
				response = performRequest(portal.Handler(), http.MethodGet, target, nil)
			} else {
				response = performRequest(portal.Handler(), http.MethodGet, target, nil, sessionCookie)
			}
			assertIdentityLoginRedirect(t, response, "", tt.theme)
		})
	}
}

func TestPortalLoginAllowlistSealsIdentityReturnAndNormalizedTheme(t *testing.T) {
	tests := []struct {
		name      string
		returnTo  string
		wantTo    string
		input     string
		wantTheme string
	}{
		{name: "account", returnTo: "/account", wantTo: "/account", input: "DARK", wantTheme: "dark"},
		{name: "admin", returnTo: "/admin", wantTo: "/admin", input: "light", wantTheme: "light"},
		{name: "identity", returnTo: "/account/identity", wantTo: "/account/identity", input: "dark", wantTheme: "dark"},
		{name: "unknown target", returnTo: "https://attacker.example/callback", wantTo: "/account", input: "dark", wantTheme: "dark"},
		{name: "nested target", returnTo: "/account/identity?next=/admin", wantTo: "/account", input: "attacker", wantTheme: ""},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			flow := &testOIDCFlow{}
			portal := newTestPortal(t, flow)
			_, state := startLoginWithParams(t, portal, flow, tt.returnTo, tt.input)
			if state.ReturnTo != tt.wantTo {
				t.Fatalf("sealed return_to = %q, want %q", state.ReturnTo, tt.wantTo)
			}
			if state.Theme != tt.wantTheme {
				t.Fatalf("sealed theme = %q, want %q", state.Theme, tt.wantTheme)
			}
			if flow.theme != tt.wantTheme {
				t.Fatalf("authorization theme = %q, want %q", flow.theme, tt.wantTheme)
			}
		})
	}
}

func TestPortalCallbackPreservesIdentityThemeAndRejectsUnknownReturnTarget(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "identity-subject", Username: "identity-user"}}
	portal := newTestPortal(t, flow)
	oauthCookie, state := startLoginWithParams(t, portal, flow, "/account/identity", "DARK")
	callback := performRequest(portal.Handler(), http.MethodGet, "/auth/callback?code=production-code&state="+url.QueryEscape(state.State), nil, oauthCookie)
	if callback.Code != http.StatusSeeOther {
		t.Fatalf("identity callback status = %d, location=%q", callback.Code, callback.Header().Get("Location"))
	}
	wantURL, err := url.Parse(flow.AccountURL())
	if err != nil {
		t.Fatalf("parse account URL fixture: %v", err)
	}
	wantQuery := wantURL.Query()
	wantQuery.Set(themeQueryParameter, "dark")
	wantURL.RawQuery = wantQuery.Encode()
	if got := callback.Header().Get("Location"); got != wantURL.String() {
		t.Fatalf("identity callback location = %q, want %q", got, wantURL.String())
	}

	unknownFlow := &testOIDCFlow{identity: accountIdentity{Subject: "unknown-target-subject", Username: "unknown-target-user"}}
	unknownPortal := newTestPortal(t, unknownFlow)
	unknownCookie, unknownState := startLoginWithParams(t, unknownPortal, unknownFlow, "/admin?next=/attacker", "dark")
	if unknownState.ReturnTo != "/account" {
		t.Fatalf("unknown return_to was sealed as %q, want /account", unknownState.ReturnTo)
	}
	unknownCallback := performRequest(unknownPortal.Handler(), http.MethodGet, "/auth/callback?code=production-code&state="+url.QueryEscape(unknownState.State), nil, unknownCookie)
	if unknownCallback.Code != http.StatusSeeOther || unknownCallback.Header().Get("Location") != "/account" {
		t.Fatalf("unknown return_to callback = %d %q, want 303 /account", unknownCallback.Code, unknownCallback.Header().Get("Location"))
	}
}

func TestPortalCallbackFallsBackForUnknownSealedReturnTarget(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "sealed-target-subject", Username: "sealed-target-user"}}
	portal := newTestPortal(t, flow)
	pending := oauthState{
		State:      "sealed-state",
		Nonce:      "sealed-nonce",
		Verifier:   "sealed-verifier",
		ReturnTo:   "https://attacker.example/callback",
		Theme:      "dark",
		IssuedAtMS: portal.now().UnixMilli(),
	}
	encoded, err := portal.oauthCodec.seal(pending)
	if err != nil {
		t.Fatalf("seal unknown return target fixture: %v", err)
	}
	cookie := &http.Cookie{Name: oauthCookieName, Value: encoded}
	callback := performRequest(portal.Handler(), http.MethodGet, "/auth/callback?code=production-code&state="+url.QueryEscape(pending.State), nil, cookie)
	if callback.Code != http.StatusSeeOther || callback.Header().Get("Location") != "/account" {
		t.Fatalf("unknown sealed return_to callback = %d %q, want 303 /account", callback.Code, callback.Header().Get("Location"))
	}
}

func TestPortalIdentityCanonicalRedirectPreservesAllowlistedTargetAndTheme(t *testing.T) {
	for _, target := range []string{
		"/login?return_to=%2Faccount%2Fidentity&termlink_theme=DARK",
		"/account/identity?termlink_theme=DARK",
	} {
		t.Run(target, func(t *testing.T) {
			portal := newTestPortal(t, &testOIDCFlow{})
			response := performRequestOnHost(portal.Handler(), http.MethodGet, "lucky-upstream:8080", target, nil)
			assertIdentityLoginRedirect(t, response, testPublicHost, "dark")
			location, err := url.Parse(response.Header().Get("Location"))
			if err != nil {
				t.Fatalf("parse canonical identity redirect: %v", err)
			}
			if location.Scheme != "https" {
				t.Fatalf("canonical identity redirect scheme = %q, want https", location.Scheme)
			}
		})
	}
}

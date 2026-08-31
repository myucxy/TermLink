package webportal

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"html"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/adminroute"
	"github.com/termlink/termlink/server/internal/humanverification"
	"github.com/termlink/termlink/server/internal/serviceemail"
	"github.com/termlink/termlink/server/internal/store"
	"golang.org/x/oauth2"
)

const testPublicHost = "termlink.example:8910"

type testOIDCFlow struct {
	state         string
	nonce         string
	challenge     string
	theme         string
	stepUpMethod  string
	code          string
	verifier      string
	expectedNonce string
	identity      accountIdentity
	exchangeErr   error
	exchangeCalls int
	stepUpCalls   int
}

type testPortalRepository struct {
	account          store.PortalAccount
	dashboard        store.PortalAdminDashboard
	dashboardErr     error
	roleErr          error
	statusErr        error
	loginFailureCode string
	securityEvents   []store.SecurityEvent
	idempotencyKeys  map[string][32]byte
	idempotencyDone  map[string]int
	duplicateClaim   bool
	webSettings      store.WebPortalSettings
	lastAssertion    store.PortalIdentityAssertion
	identityProfile  store.AccountIdentityProfile
}

type unavailableRegistrationEmailRepository struct {
	settings store.ServiceEmailSettings
}

type testIdentityMFAStatus struct {
	configured bool
	err        error
	subject    string
}

func (s *testIdentityMFAStatus) HasConfiguredOTP(_ context.Context, subject string) (bool, error) {
	s.subject = subject
	return s.configured, s.err
}

func (r unavailableRegistrationEmailRepository) GetServiceEmailSettings(context.Context) (store.ServiceEmailSettings, error) {
	return r.settings, nil
}

func (unavailableRegistrationEmailRepository) SaveVerifiedServiceEmailSettings(context.Context, uuid.UUID, store.ServiceEmailSettingsInput) (store.ServiceEmailSettings, error) {
	return store.ServiceEmailSettings{}, store.ErrDenied
}

func (unavailableRegistrationEmailRepository) CreateRegistrationEmailChallenge(context.Context, store.RegistrationEmailChallenge, time.Duration) error {
	return store.ErrDenied
}

func (unavailableRegistrationEmailRepository) ConsumeRegistrationEmailChallenge(context.Context, uuid.UUID, [32]byte, [32]byte, time.Time) error {
	return store.ErrDenied
}

func (unavailableRegistrationEmailRepository) CreateAccountEmailChallenge(context.Context, store.AccountEmailChallenge, time.Duration) error {
	return store.ErrDenied
}

func (unavailableRegistrationEmailRepository) ConsumeAccountEmailChallenge(context.Context, uuid.UUID, uuid.UUID, [32]byte, [32]byte, time.Time) error {
	return store.ErrDenied
}

func newTestPortalRepository() *testPortalRepository {
	return &testPortalRepository{account: store.PortalAccount{
		ID: uuid.MustParse("11111111-1111-4111-8111-111111111111"), Role: store.RoleUser, Status: "ACTIVE",
		CreatedAt: time.Date(2026, time.August, 4, 9, 0, 0, 0, time.UTC),
	}, idempotencyKeys: make(map[string][32]byte), idempotencyDone: make(map[string]int), webSettings: store.WebPortalSettings{Version: 1}}
}

func (r *testPortalRepository) SyncPortalIdentity(_ context.Context, _ [32]byte, assertion store.PortalIdentityAssertion, _ string) (store.PortalAccount, error) {
	r.lastAssertion = assertion
	if r.statusErr != nil {
		return store.PortalAccount{}, r.statusErr
	}
	if assertion.Role == store.RoleSuperAdmin {
		r.account.Role = store.RoleSuperAdmin
	}
	return r.account, nil
}

func (r *testPortalRepository) SyncAccountIdentityProfile(_ context.Context, _ uuid.UUID, profile store.AccountIdentityProfile) error {
	r.identityProfile = profile
	return nil
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

func (r *testPortalRepository) GetWebPortalSettings(context.Context) (store.WebPortalSettings, error) {
	return r.webSettings, nil
}

func (r *testPortalRepository) SaveWebPortalAdminRoute(_ context.Context, _ uuid.UUID, ciphertext, routeHMAC []byte, expectedVersion int64) (store.WebPortalSettings, error) {
	r.webSettings = store.WebPortalSettings{
		AdminRouteCiphertext: append([]byte(nil), ciphertext...), AdminRouteHMAC: append([]byte(nil), routeHMAC...), Version: expectedVersion + 1,
	}
	return r.webSettings, nil
}

func (r *testPortalRepository) ClaimIdempotency(_ context.Context, _ uuid.UUID, key string, requestHash [32]byte) (store.IdempotencyResult, error) {
	if r.duplicateClaim {
		return store.IdempotencyResult{Claimed: false, RequestHash: requestHash}, nil
	}
	r.idempotencyKeys[key] = requestHash
	return store.IdempotencyResult{Claimed: true, RequestHash: requestHash}, nil
}

func (r *testPortalRepository) CompleteIdempotency(_ context.Context, _ uuid.UUID, key string, _ [32]byte, status int, _ json.RawMessage) error {
	r.idempotencyDone[key] = status
	return nil
}

func (r *testPortalRepository) RecordSecurityEvent(_ context.Context, event store.SecurityEvent) error {
	r.securityEvents = append(r.securityEvents, event)
	return nil
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

func (f *testOIDCFlow) StepUpAuthorizationURL(state, nonce, challenge, theme, method string) string {
	f.stepUpMethod = method
	return f.AuthorizationURL(state, nonce, challenge, theme)
}

func (f *testOIDCFlow) ExchangeAndVerify(_ context.Context, code, verifier, expectedNonce string) (accountIdentity, error) {
	f.exchangeCalls++
	f.code = code
	f.verifier = verifier
	f.expectedNonce = expectedNonce
	if f.exchangeErr != nil {
		return accountIdentity{}, f.exchangeErr
	}
	return f.identity, nil
}

func (f *testOIDCFlow) ExchangeStepUpAndVerify(_ context.Context, code, verifier, expectedNonce, method string) (accountIdentity, error) {
	f.stepUpCalls++
	f.stepUpMethod = method
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

func enableTestAdminRoute(t *testing.T, portal *Portal, repository *testPortalRepository, segment string) {
	t.Helper()
	service, err := adminroute.New(repository, []byte("admin-route-test-key-0123456789abcdef"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = service.Update(context.Background(), repository.account.ID, segment, segment, repository.webSettings.Version); err != nil {
		t.Fatal(err)
	}
	portal.config.AdminRoutes = service
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

func TestValidateConfigRejectsInvalidHumanVerificationPair(t *testing.T) {
	base := Config{
		PublicURL:   "https://" + testPublicHost,
		Issuer:      "https://identity.example/realms/termlink",
		ClientID:    "termlink-web",
		RedirectURI: "https://" + testPublicHost + "/auth/callback",
		CookieKey:   []byte("0123456789abcdef0123456789abcdef"),
		SubjectKey:  []byte("abcdef0123456789abcdef0123456789"),
		Repository:  newTestPortalRepository(),
	}
	for _, mutate := range []func(*Config){
		func(config *Config) { config.HumanVerificationSiteKey = "site-key-without-provider" },
		func(config *Config) { config.HumanVerificationProvider = "unsupported" },
		func(config *Config) { config.HumanVerificationProvider = humanverification.ProviderTurnstile },
		func(config *Config) {
			config.HumanVerificationProvider = humanverification.ProviderTurnstile
			config.HumanVerificationSiteKey = "invalid site key"
		},
	} {
		candidate := base
		mutate(&candidate)
		if _, err := validateConfig(candidate); err == nil {
			t.Fatalf("validateConfig accepted invalid human verification config: %+v", candidate)
		}
	}
}

func TestCustomAdminRouteHidesDefaultAndTranslatesPortalLinks(t *testing.T) {
	flow := &testOIDCFlow{}
	portal := newTestPortal(t, flow)
	repository := newTestPortalRepository()
	repository.account.Role = store.RoleSuperAdmin
	portal.config.Repository = repository
	const segment = "4hPzK8mQvX2sN7aB"
	enableTestAdminRoute(t, portal, repository, segment)
	cookie := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "admin-route-csrf", time.Minute)

	hidden := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if hidden.Code != http.StatusNotFound || strings.Contains(hidden.Body.String(), segment) {
		t.Fatalf("default route status/body = %d/%q", hidden.Code, hidden.Body.String())
	}
	target := "/control/" + segment
	page := performRequest(portal.Handler(), http.MethodGet, target+"?section=overview", nil, cookie)
	if page.Code != http.StatusOK {
		t.Fatalf("custom route status=%d body=%s", page.Code, page.Body.String())
	}
	if !strings.Contains(page.Body.String(), `href="`+target+`?section=accounts"`) || strings.Contains(page.Body.String(), `href="/admin`) {
		t.Fatalf("custom route links were not translated: %s", page.Body.String())
	}
	account := performRequest(portal.Handler(), http.MethodGet, "/account", nil, cookie)
	if !strings.Contains(account.Body.String(), `href="`+target+`"`) || strings.Contains(account.Body.String(), segment+"/") {
		t.Fatalf("account did not expose exact management entry to authorized role")
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
	for _, text := range []string{"离开电脑", "真实终端", "端到端加密", "使用 TermLink 账号登录", "/assets/termlink-app-icon.svg"} {
		if !strings.Contains(body, text) {
			t.Fatalf("home page omitted %q", text)
		}
	}
	if strings.Count(body, `href="/login"`) != 1 || strings.Contains(body, "打开管理台") || strings.Contains(body, "进入 Server Console") {
		t.Fatal("public home must expose one account entry and no management destination")
	}
	if strings.Count(body, `href="/register"`) != 1 {
		t.Fatal("home must link directly to registration")
	}
	for _, hiddenEntry := range []string{`href="/downloads"`, `href="/releases"`, `href="/plans"`, `href="/security"`, `href="/status"`} {
		if strings.Contains(body, hiddenEntry) {
			t.Fatalf("home exposed hidden public entry %s", hiddenEntry)
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
	if policy := recorder.Header().Get("Content-Security-Policy"); !strings.Contains(policy, "frame-src 'self'") || !strings.Contains(policy, "frame-ancestors 'none'") {
		t.Fatalf("portal CSP must allow only its same-origin identity dialog while remaining non-embeddable: %q", policy)
	}

	css := performRequest(portal.Handler(), http.MethodGet, "/assets/site.css", nil)
	if css.Code != http.StatusOK || !strings.HasPrefix(css.Header().Get("Content-Type"), "text/css") || css.Body.Len() < 1000 {
		t.Fatalf("embedded stylesheet response is incomplete: status=%d bytes=%d", css.Code, css.Body.Len())
	}
	identityCSS := performRequest(portal.Handler(), http.MethodGet, "/assets/identity.css", nil)
	if identityCSS.Code != http.StatusOK || !strings.HasPrefix(identityCSS.Header().Get("Content-Type"), "text/css") ||
		!strings.Contains(identityCSS.Body.String(), ".identity-layout") ||
		!strings.Contains(identityCSS.Body.String(), ".registration-code-row") {
		t.Fatalf("shared identity stylesheet response is incomplete: status=%d bytes=%d", identityCSS.Code, identityCSS.Body.Len())
	}
	themeInit := performRequest(portal.Handler(), http.MethodGet, "/assets/theme-init.js", nil)
	if themeInit.Code != http.StatusOK || !strings.HasPrefix(themeInit.Header().Get("Content-Type"), "text/javascript") ||
		!strings.Contains(themeInit.Body.String(), "termlink-theme") {
		t.Fatalf("embedded pre-paint theme script response is incomplete: status=%d bytes=%d", themeInit.Code, themeInit.Body.Len())
	}
	theme := performRequest(portal.Handler(), http.MethodGet, "/assets/theme.js", nil)
	if theme.Code != http.StatusOK || !strings.HasPrefix(theme.Header().Get("Content-Type"), "text/javascript") ||
		!strings.Contains(theme.Body.String(), "termlink-theme") ||
		!strings.Contains(theme.Body.String(), "确认高风险操作") ||
		!strings.Contains(theme.Body.String(), "Confirm sensitive operation") ||
		!strings.Contains(theme.Body.String(), "Verification was not completed") {
		t.Fatalf("embedded theme script response is incomplete: status=%d bytes=%d", theme.Code, theme.Body.Len())
	}
	registerScript := performRequest(portal.Handler(), http.MethodGet, "/assets/register.js", nil)
	if registerScript.Code != http.StatusOK ||
		!strings.Contains(registerScript.Body.String(), "注册服务暂时不可用") ||
		!strings.Contains(registerScript.Body.String(), "Registration is temporarily unavailable") ||
		!strings.Contains(registerScript.Body.String(), "PublicRegistrationError") ||
		!strings.Contains(registerScript.Body.String(), "human_verification_token") ||
		strings.Count(registerScript.Body.String(), "resetHumanVerification();") < 2 ||
		!strings.Contains(registerScript.Body.String(), "result.error.code") ||
		strings.Contains(registerScript.Body.String(), "error instanceof Error ? error.message") {
		t.Fatalf("registration prompts must be safely localized in Chinese and English: status=%d", registerScript.Code)
	}
	commerceScript := performRequest(portal.Handler(), http.MethodGet, "/assets/commerce-admin.js", nil)
	if commerceScript.Code != http.StatusOK ||
		!strings.Contains(commerceScript.Body.String(), "data-copy-all-codes") ||
		!strings.Contains(commerceScript.Body.String(), "beforeunload") ||
		!strings.Contains(commerceScript.Body.String(), "Invitation code copied") {
		t.Fatalf("commerce invitation receipt script is incomplete: status=%d", commerceScript.Code)
	}
	for _, asset := range []string{"/assets/termlink-app-icon.svg", "/assets/termlink-favicon.svg"} {
		icon := performRequest(portal.Handler(), http.MethodGet, asset, nil)
		if icon.Code != http.StatusOK || icon.Header().Get("Content-Type") != "image/svg+xml" ||
			!strings.Contains(icon.Body.String(), "TermLink") {
			t.Fatalf("embedded brand asset %q is incomplete: status=%d bytes=%d", asset, icon.Code, icon.Body.Len())
		}
	}
}

func TestPortalOperationNoticesIncludeChineseAndEnglish(t *testing.T) {
	for name, notice := range map[string]string{
		"admin":          adminNotice("role-updated"),
		"account":        commerceNotice("device-revoked"),
		"commerce admin": commerceAdminNotice("order-closed"),
	} {
		if !strings.Contains(notice, " / ") ||
			!strings.ContainsAny(notice, "账号设备订单") ||
			!strings.ContainsAny(notice, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") {
			t.Errorf("%s notice is not bilingual: %q", name, notice)
		}
	}
}

func TestRegistrationPageFailsClosedWhenRequiredEmailDeliveryIsUnavailable(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.Repository = &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationOpen, Version: 1},
	}
	emailService, err := serviceemail.New(unavailableRegistrationEmailRepository{settings: store.ServiceEmailSettings{
		ServiceEnabled: true, RegistrationVerificationEnabled: true, Status: "VERIFIED", Version: 1,
	}}, []byte("registration-email-test-key-0123456789abcdef"), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	portal.config.ServiceEmail = emailService

	response := performRequest(portal.Handler(), http.MethodGet, "/register", nil)
	if response.Code != http.StatusServiceUnavailable ||
		!strings.Contains(response.Body.String(), "当前注册必须验证邮箱") ||
		strings.Contains(response.Body.String(), "当前允许直接创建账号") {
		t.Fatalf("registration e-mail fail-closed response = %d %q", response.Code, response.Body.String())
	}
}

func TestRegistrationPagePreservesOnlyTrustedOIDCContinuation(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.WindowsClientID = "termlink-windows"
	portal.config.Repository = &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationInviteRequired, Version: 1},
	}
	continuation := "https://identity.example/realms/termlink/login-actions/authenticate?" +
		"client_id=termlink-windows&execution=fixture&session_code=secret&tab_id=tab"
	response := performRequest(
		portal.Handler(),
		http.MethodGet,
		"/register?continue="+url.QueryEscape(continuation),
		nil)
	if response.Code != http.StatusOK {
		t.Fatalf("trusted continuation registration response = %d", response.Code)
	}
	escaped := html.EscapeString(continuation)
	body := response.Body.String()
	if !strings.Contains(body, `data-registration-continue="`+escaped+`"`) ||
		!strings.Contains(body, `href="`+escaped+`"`) ||
		!strings.Contains(body, "需要有效邀请码") ||
		!strings.Contains(body, `/assets/identity.css`) ||
		strings.Contains(body, `/assets/site.css`) {
		t.Fatalf("trusted Windows OIDC continuation was not preserved through registration: %q", body)
	}

	for _, untrusted := range []string{
		"https://evil.example/realms/termlink/login-actions/authenticate?client_id=termlink-windows",
		"https://identity.example/realms/termlink/account?client_id=termlink-windows",
		"https://identity.example/realms/termlink/login-actions/authenticate?client_id=unknown-client",
		"https://identity.example/realms/termlink/login-actions/authenticate?client_id=termlink-windows&client_id=termlink-web",
	} {
		response = performRequest(
			portal.Handler(),
			http.MethodGet,
			"/register?continue="+url.QueryEscape(untrusted),
			nil)
		body = response.Body.String()
		if response.Code != http.StatusOK || strings.Contains(body, html.EscapeString(untrusted)) ||
			!strings.Contains(body, `href="/login?return_to=/account"`) {
			t.Fatalf("untrusted registration continuation was accepted: %q", untrusted)
		}
	}
}

func TestRegistrationPageEnablesTurnstileWithoutWeakeningOtherPages(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.Repository = &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationOpen, Version: 1},
	}
	portal.config.HumanVerificationProvider = humanverification.ProviderTurnstile
	portal.config.HumanVerificationSiteKey = "0x4AAAA-public-test-site-key"

	response := performRequest(portal.Handler(), http.MethodGet, "/register", nil)
	body := response.Body.String()
	if response.Code != http.StatusOK ||
		!strings.Contains(body, `id="human-verification"`) ||
		!strings.Contains(body, `data-site-key="0x4AAAA-public-test-site-key"`) ||
		!strings.Contains(body, `data-human-verification-required="true"`) ||
		!strings.Contains(body, humanverification.TurnstileScriptOrigin+`/turnstile/v0/api.js?render=explicit`) ||
		strings.Contains(body, "turnstile-secret-material") {
		t.Fatalf("Turnstile registration page is incomplete or leaked secret material: status=%d body=%q", response.Code, body)
	}
	policy := response.Header().Get("Content-Security-Policy")
	for _, directive := range []string{
		"script-src 'self' " + humanverification.TurnstileScriptOrigin,
		"connect-src 'self' " + humanverification.TurnstileScriptOrigin,
		"frame-src 'self' " + humanverification.TurnstileScriptOrigin,
	} {
		if !strings.Contains(policy, directive) {
			t.Fatalf("Turnstile registration CSP %q omitted %q", policy, directive)
		}
	}

	home := performRequest(portal.Handler(), http.MethodGet, "/", nil)
	if strings.Contains(home.Body.String(), humanverification.TurnstileScriptOrigin) ||
		strings.Contains(home.Header().Get("Content-Security-Policy"), humanverification.TurnstileScriptOrigin) {
		t.Fatal("Turnstile must not be loaded or allowed by CSP on ordinary portal pages")
	}

	disabled := newTestPortal(t, &testOIDCFlow{})
	disabled.config.Repository = &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationOpen, Version: 1},
	}
	disabledResponse := performRequest(disabled.Handler(), http.MethodGet, "/register", nil)
	if strings.Contains(disabledResponse.Body.String(), humanverification.TurnstileScriptOrigin) ||
		strings.Contains(disabledResponse.Body.String(), `id="human-verification"`) ||
		strings.Contains(disabledResponse.Header().Get("Content-Security-Policy"), humanverification.TurnstileScriptOrigin) {
		t.Fatal("disabled human verification must preserve the existing registration page and CSP")
	}
}

func TestPortalTemplatesApplyThemeBeforeStyles(t *testing.T) {
	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html"} {
		data, err := portalAssets.ReadFile("assets/" + name)
		if err != nil {
			t.Fatal(err)
		}
		html := string(data)
		initIndex := strings.Index(html, `/assets/theme-init.js`)
		styleIndex := strings.Index(html, `/assets/site.css`)
		if initIndex < 0 || styleIndex < 0 || initIndex > styleIndex {
			t.Errorf("%s must restore the selected theme before loading product styles", name)
		}
	}
	data, err := portalAssets.ReadFile("assets/register.html")
	if err != nil {
		t.Fatal(err)
	}
	registrationHTML := string(data)
	if initIndex, styleIndex := strings.Index(registrationHTML, `/assets/theme-init.js`), strings.Index(registrationHTML, `/assets/identity.css`); initIndex < 0 || styleIndex < 0 || initIndex > styleIndex {
		t.Error("register.html must restore the selected theme before loading shared identity styles")
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
	identityCSS := readAsset("identity.css")
	for name, contract := range map[string]string{
		"registration identity layout": `.registration-page .identity-layout`,
		"registration mobile code row": `.registration-code-row {`,
		"registration status message":  `.registration-message[data-state="error"]`,
	} {
		if !strings.Contains(identityCSS, contract) {
			t.Errorf("%s is missing", name)
		}
	}

	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html", "register.html"} {
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
		!strings.Contains(adminHTML, `/assets/admin.css?v=01b-v17`) {
		t.Error("mobile administrator navigation must use the versioned product scrollbar styling")
	}
	adminNavigationHTML := readAsset("admin-navigation.html")
	if strings.Contains(adminCSS, `grid-template-columns: 22px 1fr auto;`) ||
		strings.Count(adminNavigationHTML, `class="admin-nav-text"`) != strings.Count(adminNavigationHTML, `<a href=`) ||
		!strings.Contains(adminCSS, `.admin-nav-text`) {
		t.Error("administrator navigation text must not be placed in the obsolete icon-only grid column")
	}
	if strings.Contains(adminNavigationHTML, `class="admin-nav-label"`) ||
		!strings.Contains(adminNavigationHTML, `class="admin-nav-group"`) ||
		!strings.Contains(adminCSS, `.admin-nav a[aria-current="page"]::after`) ||
		!strings.Contains(adminCSS, `border-color: color-mix(in srgb, var(--accent) 34%, var(--line));`) {
		t.Error("administrator navigation must omit visible category labels and expose a layered selected state")
	}
	if !strings.Contains(adminCSS, `.admin-account-menu[open] > .admin-account-trigger`) ||
		!strings.Contains(adminCSS, `background: color-mix(in srgb, var(--surface) 97%, var(--canvas));`) ||
		!strings.Contains(readAsset("theme.js"), `details.admin-account-menu, details.account-menu`) {
		t.Error("administrator account menu must have an opaque elevated surface and dismissible interaction")
	}
	commerceCSS := readAsset("commerce-portal.css")
	portalDashboardHTML := readAsset("portal-dashboard.html")
	commerceAdminHTML := readAsset("commerce-admin.html")
	if strings.Contains(commerceCSS, `var(--surface-1)`) ||
		strings.Contains(commerceCSS, `var(--surface-2)`) ||
		strings.Contains(commerceCSS, `var(--text-1)`) ||
		strings.Contains(adminCSS, `var(--surface-1)`) ||
		strings.Contains(adminCSS, `var(--text-1)`) ||
		!strings.Contains(commerceCSS, `.account-menu-panel`) ||
		!strings.Contains(commerceCSS, `background: var(--surface);`) ||
		!strings.Contains(portalDashboardHTML, `/assets/commerce-portal.css?v=01b-v7`) ||
		!strings.Contains(commerceAdminHTML, `/assets/admin.css?v=01b-v17`) ||
		!strings.Contains(commerceAdminHTML, `/assets/commerce-portal.css?v=01b-v7`) {
		t.Error("account menus must use defined theme surfaces and versioned opaque panel styles")
	}
	for _, contract := range []string{
		`data-identity-dialog="password"`, `data-identity-dialog="mfa"`, `data-identity-dialog="sessions"`,
		`<dialog class="identity-security-dialog"`, `data-identity-dialog-frame`, `data-identity-dialog-fallback`,
	} {
		if !strings.Contains(portalDashboardHTML, contract) {
			t.Errorf("in-page identity security dialog omitted %q", contract)
		}
	}
	if !strings.Contains(commerceCSS, `.identity-security-dialog::backdrop`) ||
		!strings.Contains(commerceCSS, `.identity-security-dialog iframe`) ||
		!strings.Contains(readAsset("theme.js"), "const actionURL = `${baseURL.replace(/\\/$/, \"\")}/${action}`;") {
		t.Error("identity security actions must open their allowlisted Keycloak section in the same-page dialog")
	}
	if !strings.Contains(adminCSS, `overflow-x: clip;`) ||
		!strings.Contains(adminCSS, `.accounts-table td::before`) ||
		!strings.Contains(adminCSS, `overscroll-behavior-inline: contain;`) ||
		!strings.Contains(adminCSS, `contain: inline-size;`) {
		t.Error("mobile administrator account tables must stay inside their own horizontal scroll region")
	}
	if !strings.Contains(siteCSS, `--shadow-color: rgba(30, 40, 60, 0.16);`) ||
		!strings.Contains(adminCSS, `color-mix(in srgb, var(--shadow-color)`) ||
		strings.Contains(adminCSS, `color-mix(in srgb, var(--shadow)`) {
		t.Error("administrator elevation must mix a color token instead of the complete shadow declaration")
	}
	if strings.Contains(readAsset("theme.js"), `setAttribute("aria-pressed"`) {
		t.Error("theme action buttons must not expose toggle-button pressed state")
	}
	for _, name := range []string{"home.html", "account.html", "admin.html", "error.html", "register.html"} {
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
	if !strings.Contains(denied.Body.String(), `href="/account">返回个人中心 / Account</a>`) {
		t.Fatal("administrator error response must provide a direct return to the account center")
	}

	adminFlow := &testOIDCFlow{identity: accountIdentity{
		Subject: "admin-subject", Username: "admin", Name: "TermLink Administrator",
		Roles: []string{"offline_access", "SUPER_ADMIN"}, BootstrapManaged: true, MFA: true,
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
	adminRepository := adminPortal.config.Repository.(*testPortalRepository)
	if adminRepository.lastAssertion.Role != store.RoleSuperAdmin || !adminRepository.lastAssertion.BootstrapManaged {
		t.Fatalf("managed bootstrap assertion was not passed to the database authority: %+v", adminRepository.lastAssertion)
	}
	accountPage := performRequest(adminPortal.Handler(), http.MethodGet, "/account", nil, accountCookie)
	if accountPage.Code != http.StatusOK || !strings.Contains(accountPage.Body.String(), "进入管理台") ||
		!strings.Contains(accountPage.Body.String(), `href="/account/identity" data-theme-transfer`) ||
		!strings.Contains(accountPage.Body.String(), `href="/admin">管理台</a>`) {
		t.Fatalf("admin account page = %d %q", accountPage.Code, accountPage.Body.String())
	}
	home := performRequest(adminPortal.Handler(), http.MethodGet, "/", nil, accountCookie)
	if home.Code != http.StatusSeeOther || home.Header().Get("Location") != "/account" {
		t.Fatal("signed-in administrator home must redirect directly to the account center")
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
		Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"}, MFA: true,
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
			flow := &testOIDCFlow{identity: accountIdentity{Subject: "console-subject", Username: "console-user", Roles: []string{string(role)}, MFA: true}}
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

func TestPortalConsoleAllowsPasswordAuthenticatedAdministrator(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "admin-without-mfa", Username: "admin-without-mfa", Roles: []string{"SUPER_ADMIN"},
	}}
	portal := newTestPortal(t, flow)
	cookie, _ := completeLogin(t, portal, flow)

	response := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "TermLink Server Console") {
		t.Fatalf("admin without MFA response = %d %q", response.Code, response.Body.String())
	}
}

func TestPortalProtectsAdministratorMutationWithCSRFAndLastSuperAdminRule(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"}}}
	portal := newTestPortal(t, flow)
	flow.identity.AuthenticatedAt = portal.now().Add(-time.Minute)
	flow.identity.MFA = true
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
	request = httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/role", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)+"&role=USER&reason=security+review&idempotency_key=account-role-0001"))
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
	flow.identity.AuthenticatedAt = portal.now().Add(-time.Minute)
	flow.identity.MFA = true
	cookie, session := completeLoginAt(t, portal, flow, "/admin")
	targetID := uuid.MustParse("22222222-2222-4222-8222-222222222222")
	var publishedID uuid.UUID
	var publishedStatus string
	portal.config.AccountStatusChanged = func(_ context.Context, accountID uuid.UUID, status string) error {
		publishedID = accountID
		publishedStatus = status
		return nil
	}

	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+"/admin/accounts/"+targetID.String()+"/status", strings.NewReader("csrf="+url.QueryEscape(session.CSRF)+"&status=LOCKED&reason=security+incident&idempotency_key=account-lock-0001"))
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

	system := performRequest(portal.Handler(), http.MethodGet, "/login?termlink_theme=system", nil)
	if system.Code != http.StatusFound || flow.theme != "system" {
		t.Fatalf("system theme login status/theme = %d/%q", system.Code, flow.theme)
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
		Subject: "admin-subject", Username: "admin", Roles: []string{"SUPER_ADMIN"}, MFA: true,
	}}
	portal := newTestPortal(t, flow)
	repository := portal.config.Repository.(*testPortalRepository)
	repository.account.Role = store.RoleSuperAdmin
	cookie, _ := completeLoginAt(t, portal, flow, "/admin")
	repository.dashboardErr = errors.New("dashboard unavailable fixture")

	response := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if response.Code != http.StatusInternalServerError ||
		!strings.Contains(response.Header().Get("Content-Type"), "text/html") ||
		!strings.Contains(response.Body.String(), `href="/account">返回个人中心 / Account</a>`) ||
		!strings.Contains(response.Body.String(), "问题编号 / Reference:") ||
		!strings.Contains(response.Body.String(), "服务暂时不可用") ||
		!strings.Contains(response.Body.String(), "Service temporarily unavailable") ||
		!strings.Contains(response.Body.String(), "/assets/site.css") {
		t.Fatalf("internal error recovery page = %d %q", response.Code, response.Body.String())
	}
}

func TestPortalPublicErrorsIncludeChineseAndEnglishSafeCopy(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	response := performRequest(portal.Handler(), http.MethodGet, "/does-not-exist", nil)
	body := response.Body.String()
	if response.Code != http.StatusNotFound ||
		!strings.Contains(body, "页面不存在") ||
		!strings.Contains(body, "Not found") ||
		!strings.Contains(body, "该地址不属于 TermLink 服务端门户") ||
		!strings.Contains(body, "requested page or record does not exist") ||
		!strings.Contains(body, "返回首页 / Home") ||
		!strings.Contains(body, `lang="zh-CN"`) ||
		!strings.Contains(body, `lang="en"`) {
		t.Fatalf("bilingual public error response = %d %q", response.Code, body)
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
	if !strings.Contains(body, "&lt;script&gt;") || strings.Contains(body, "&lt;img") {
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

func TestOIDCPortalClaimsPreserveAuthoritativeAuthTimeAndMFA(t *testing.T) {
	authenticatedAt := time.Date(2026, time.August, 4, 9, 58, 0, 0, time.UTC)
	claims := oidcPortalClaims{
		Subject: "subject-strong", Nonce: "nonce-strong", PreferredUsername: "strong-user",
		AuthenticatedAt: authenticatedAt.Unix(), AMR: []string{"pwd", "otp"}, ACR: "urn:example:loa:secret-mfa",
	}
	claims.RealmAccess.Roles = []string{"ADMIN", bootstrapManagedOIDCRole}
	claims.ResourceAccess = map[string]struct {
		Roles []string `json:"roles"`
	}{"termlink-web": {Roles: []string{"resource-role"}}}
	identity, err := portalIdentityFromClaims(claims, "termlink-web", "nonce-strong")
	if err != nil {
		t.Fatal(err)
	}
	if !identity.AuthenticatedAt.Equal(authenticatedAt) || !identity.MFA || !identity.Password || identity.ACR != claims.ACR {
		t.Fatalf("OIDC strong-auth identity=%+v", identity)
	}
	if len(identity.Roles) != 3 || identity.Roles[0] != "ADMIN" || !identity.BootstrapManaged {
		t.Fatalf("OIDC roles=%v", identity.Roles)
	}
	claims.RealmAccess.Roles = []string{"ADMIN"}
	claims.ResourceAccess["termlink-web"] = struct {
		Roles []string `json:"roles"`
	}{Roles: []string{bootstrapManagedOIDCRole}}
	identity, err = portalIdentityFromClaims(claims, "termlink-web", "nonce-strong")
	if err != nil || identity.BootstrapManaged {
		t.Fatal("client resource role was trusted as the deployment bootstrap marker")
	}

	claims.AMR = []string{"pwd"}
	claims.AuthenticatedAt = 0
	identity, err = portalIdentityFromClaims(claims, "termlink-web", "nonce-strong")
	if err != nil || identity.MFA || !identity.Password || !identity.AuthenticatedAt.IsZero() {
		t.Fatalf("OIDC password-only identity=%+v err=%v", identity, err)
	}
	if _, err := portalIdentityFromClaims(claims, "termlink-web", "wrong-nonce"); err == nil {
		t.Fatal("OIDC claims with the wrong nonce were accepted")
	}
}

func TestPortalSessionUsesOIDCAuthTimeAndDoesNotPersistACR(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "strong-subject", Username: "strong-admin", Roles: []string{"SUPER_ADMIN"},
		MFA: true, ACR: "urn:example:private-acr", AuthenticatedAt: time.Date(2026, time.August, 4, 9, 57, 0, 0, time.UTC),
	}}
	portal := newTestPortal(t, flow)
	cookie, session := completeLoginAt(t, portal, flow, "/admin")
	if session.AuthenticatedAtMS != flow.identity.AuthenticatedAt.UnixMilli() || !session.MFA {
		t.Fatalf("sealed session did not preserve authoritative auth state: %+v", session)
	}
	if strings.Contains(cookie.Value, flow.identity.ACR) {
		t.Fatal("sealed cookie exposed ACR text")
	}
	page := performRequest(portal.Handler(), http.MethodGet, "/admin", nil, cookie)
	if strings.Contains(page.Body.String(), flow.identity.ACR) || strings.Contains(page.Body.String(), "private-acr") {
		t.Fatal("administrator HTML exposed the OIDC ACR")
	}

	futureFlow := &testOIDCFlow{identity: accountIdentity{
		Subject: "future-subject", Username: "future-admin", Roles: []string{"SUPER_ADMIN"}, MFA: true,
		AuthenticatedAt: portal.now().Add(time.Minute),
	}}
	futurePortal := newTestPortal(t, futureFlow)
	_, futureSession := completeLoginAt(t, futurePortal, futureFlow, "/admin")
	if futureSession.AuthenticatedAtMS != 0 {
		t.Fatalf("future OIDC auth_time was trusted: %+v", futureSession)
	}
}

func TestSensitiveStepUpPreservesSessionAndAcceptsMFAOrPassword(t *testing.T) {
	for _, test := range []struct {
		name     string
		mfa      bool
		password bool
		method   string
	}{
		{name: "bound MFA", mfa: true, password: true, method: sensitiveMethodMFA},
		{name: "password fallback", password: true, method: sensitiveMethodPassword},
	} {
		t.Run(test.name, func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{
				Subject: "step-up-subject", Username: "step-up-admin", Roles: []string{"SUPER_ADMIN"},
				AuthenticatedAt: time.Date(2026, time.August, 4, 9, 58, 0, 0, time.UTC), MFA: test.mfa,
			}}
			portal := newTestPortal(t, flow)
			sessionCookie, original := completeLoginAt(t, portal, flow, "/admin")
			flow.exchangeCalls = 0
			flow.stepUpCalls = 0

			start := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up?termlink_theme=dark", nil, sessionCookie)
			if start.Code != http.StatusFound {
				t.Fatalf("step-up start status=%d location=%q", start.Code, start.Header().Get("Location"))
			}
			startResponse := start.Result()
			oauthCookie := responseCookie(t, startResponse, oauthCookieName)
			_ = startResponse.Body.Close()
			var pending oauthState
			if err := portal.oauthCodec.open(oauthCookie.Value, &pending); err != nil {
				t.Fatal(err)
			}
			if pending.Purpose != sensitiveOIDCPurpose || pending.ReturnTo != "/auth/step-up/result" ||
				pending.Theme != "dark" || pending.Method != test.method || flow.stepUpMethod != test.method {
				t.Fatalf("step-up state=%+v", pending)
			}

			flow.identity.AuthenticatedAt = portal.now()
			flow.identity.MFA = test.mfa
			flow.identity.Password = test.password
			callback := performRequest(portal.Handler(), http.MethodGet,
				"/auth/callback?code=step-up-code&state="+url.QueryEscape(pending.State), nil, oauthCookie, sessionCookie)
			if callback.Code != http.StatusSeeOther || callback.Header().Get("Location") != "/auth/step-up/result" {
				t.Fatalf("step-up callback=%d location=%q body=%q", callback.Code, callback.Header().Get("Location"), callback.Body.String())
			}
			if flow.exchangeCalls != 0 || flow.stepUpCalls != 1 {
				t.Fatalf("step-up exchanged authorization code standard=%d step-up=%d, want 0/1", flow.exchangeCalls, flow.stepUpCalls)
			}
			callbackResponse := callback.Result()
			verifiedCookie := responseCookie(t, callbackResponse, sessionCookieName)
			_ = callbackResponse.Body.Close()
			var verified webSession
			if err := portal.sessionCodec.open(verifiedCookie.Value, &verified); err != nil {
				t.Fatal(err)
			}
			if verified.Subject != original.Subject || verified.CSRF != original.CSRF ||
				verified.SensitiveMethod != test.method || !recentSensitiveVerification(verified, portal.now()) {
				t.Fatalf("verified session=%+v original=%+v", verified, original)
			}

			result := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up/result", nil, verifiedCookie)
			if result.Code != http.StatusOK || result.Header().Get("X-Frame-Options") != "SAMEORIGIN" ||
				!strings.Contains(result.Header().Get("Content-Security-Policy"), "frame-ancestors 'self'") ||
				!strings.Contains(result.Body.String(), `data-sensitive-step-up-status="verified"`) ||
				!strings.Contains(result.Body.String(), `data-sensitive-step-up-method="`+test.method+`"`) {
				t.Fatalf("step-up result=%d headers=%v body=%q", result.Code, result.Header(), result.Body.String())
			}
		})
	}
}

func TestSensitiveStepUpVerificationWindowAndStatus(t *testing.T) {
	if sensitiveActionTTL != 20*time.Minute {
		t.Fatalf("sensitive verification TTL=%s, want 20m", sensitiveActionTTL)
	}
	portal := newTestPortal(t, &testOIDCFlow{})
	seal := func(age time.Duration, method string) *http.Cookie {
		value, err := portal.sessionCodec.seal(webSession{
			Subject: "status-subject", Username: "status-user", Role: string(store.RoleUser), CSRF: "status-csrf",
			AuthenticatedAtMS:     portal.now().Add(-time.Hour).UnixMilli(),
			SensitiveVerifiedAtMS: portal.now().Add(-age).UnixMilli(), SensitiveMethod: method,
			ExpiresAtMS: portal.now().Add(time.Hour).UnixMilli(),
		})
		if err != nil {
			t.Fatal(err)
		}
		return &http.Cookie{Name: sessionCookieName, Value: value, Path: "/"}
	}

	valid := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up/status", nil,
		seal(sensitiveActionTTL, sensitiveMethodMFA))
	if valid.Code != http.StatusOK || valid.Header().Get("Cache-Control") != "no-store" ||
		valid.Header().Get("Pragma") != "no-cache" || !strings.Contains(valid.Body.String(), `"verified":true`) {
		t.Fatalf("valid status=%d headers=%v body=%q", valid.Code, valid.Header(), valid.Body.String())
	}

	expired := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up/status", nil,
		seal(sensitiveActionTTL+time.Millisecond, sensitiveMethodPassword))
	if expired.Code != http.StatusOK || !strings.Contains(expired.Body.String(), `"verified":false`) {
		t.Fatalf("expired status=%d body=%q", expired.Code, expired.Body.String())
	}

	missing := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up/status", nil)
	if missing.Code != http.StatusUnauthorized || !strings.Contains(missing.Body.String(), `"verified":false`) ||
		missing.Header().Get("Cache-Control") != "no-store" {
		t.Fatalf("missing status=%d headers=%v body=%q", missing.Code, missing.Header(), missing.Body.String())
	}
}

func TestSensitiveStepUpStartReusesRecentVerification(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "reuse-subject", Username: "reuse-user"}}
	portal := newTestPortal(t, flow)
	repository := portal.config.Repository.(*testPortalRepository)
	repository.account.Role = store.RoleUser
	cookie, session := completeLogin(t, portal, flow)
	session.SensitiveVerifiedAtMS = portal.now().Add(-time.Minute).UnixMilli()
	session.SensitiveMethod = sensitiveMethodMFA
	encoded, err := portal.sessionCodec.seal(session)
	if err != nil {
		t.Fatal(err)
	}
	cookie.Value = encoded

	response := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up", nil, cookie)
	if response.Code != http.StatusOK || flow.stepUpMethod != "" ||
		!strings.Contains(response.Body.String(), `data-sensitive-step-up-status="verified"`) ||
		!strings.Contains(response.Body.String(), "20 分钟安全验证有效期内无需再次输入") {
		t.Fatalf("reused step-up=%d method=%q body=%q", response.Code, flow.stepUpMethod, response.Body.String())
	}
}

func TestSensitiveStepUpUsesAuthoritativeConfiguredMFAStatus(t *testing.T) {
	for _, test := range []struct {
		name       string
		configured bool
		method     string
	}{
		{name: "configured OTP overrides missing login AMR", configured: true, method: sensitiveMethodMFA},
		{name: "removed OTP overrides stale login AMR", method: sensitiveMethodPassword},
	} {
		t.Run(test.name, func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{
				Subject: "step-up-authoritative-subject", Username: "step-up-user", MFA: !test.configured,
			}}
			portal := newTestPortal(t, flow)
			sessionCookie, _ := completeLogin(t, portal, flow)
			status := &testIdentityMFAStatus{configured: test.configured}
			portal.config.IdentityMFAStatus = status

			start := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up", nil, sessionCookie)
			if start.Code != http.StatusFound || flow.stepUpMethod != test.method || status.subject != flow.identity.Subject {
				t.Fatalf("step-up start=%d method=%q status=%+v", start.Code, flow.stepUpMethod, status)
			}
		})
	}
}

func TestSensitiveStepUpFailsClosedWhenConfiguredMFAStatusIsUnavailable(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "step-up-status-subject", Username: "step-up-user"}}
	portal := newTestPortal(t, flow)
	sessionCookie, _ := completeLogin(t, portal, flow)
	portal.config.IdentityMFAStatus = &testIdentityMFAStatus{err: errors.New("private identity failure")}

	response := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up", nil, sessionCookie)
	if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), "暂时无法确认 MFA 状态") ||
		strings.Contains(response.Body.String(), "private identity failure") || flow.stepUpMethod != "" {
		t.Fatalf("MFA status failure=%d method=%q body=%q", response.Code, flow.stepUpMethod, response.Body.String())
	}
}

func TestProductionStepUpAuthorizationUsesMethodSpecificClientAndPrompt(t *testing.T) {
	flow := &productionOIDCFlow{
		oauthConfig: oauth2.Config{
			ClientID: "termlink-web",
			Endpoint: oauth2.Endpoint{AuthURL: "https://identity.example/authorize"},
		},
		stepUpOAuthConfig: oauth2.Config{
			ClientID: "termlink-web-step-up",
			Endpoint: oauth2.Endpoint{AuthURL: "https://identity.example/authorize"},
		},
	}
	tests := []struct {
		method     string
		clientID   string
		wantPrompt bool
	}{
		{method: sensitiveMethodMFA, clientID: "termlink-web-step-up"},
		{method: sensitiveMethodPassword, clientID: "termlink-web", wantPrompt: true},
	}
	for _, test := range tests {
		t.Run(test.method, func(t *testing.T) {
			location, err := url.Parse(flow.StepUpAuthorizationURL("state", "nonce", "challenge", "dark", test.method))
			if err != nil {
				t.Fatal(err)
			}
			query := location.Query()
			if query.Get("client_id") != test.clientID || (query.Get("prompt") == "login") != test.wantPrompt ||
				query.Get("code_challenge_method") != "S256" || query.Get(themeQueryParameter) != "dark" {
				t.Fatalf("step-up authorization query=%v", query)
			}
		})
	}
}

func TestSensitiveStepUpRejectsDifferentAccountAndMissingPasswordProof(t *testing.T) {
	for _, test := range []struct {
		name     string
		subject  string
		password bool
		message  string
	}{
		{name: "different account", subject: "other-subject", password: true, message: "验证账号不匹配"},
		{name: "no password proof", subject: "step-up-subject", message: "密码验证未完成"},
	} {
		t.Run(test.name, func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{
				Subject: "step-up-subject", Username: "step-up-user", AuthenticatedAt: time.Date(2026, time.August, 4, 9, 58, 0, 0, time.UTC),
			}}
			portal := newTestPortal(t, flow)
			sessionCookie, _ := completeLogin(t, portal, flow)
			start := performRequest(portal.Handler(), http.MethodGet, "/auth/step-up", nil, sessionCookie)
			startResponse := start.Result()
			oauthCookie := responseCookie(t, startResponse, oauthCookieName)
			_ = startResponse.Body.Close()
			var pending oauthState
			if err := portal.oauthCodec.open(oauthCookie.Value, &pending); err != nil {
				t.Fatal(err)
			}
			flow.identity.Subject = test.subject
			flow.identity.Password = test.password
			flow.identity.AuthenticatedAt = portal.now()
			callback := performRequest(portal.Handler(), http.MethodGet,
				"/auth/callback?code=step-up-code&state="+url.QueryEscape(pending.State), nil, oauthCookie, sessionCookie)
			if callback.Code != http.StatusUnauthorized || !strings.Contains(callback.Body.String(), test.message) ||
				callback.Header().Get("X-Frame-Options") != "SAMEORIGIN" {
				t.Fatalf("rejected step-up=%d headers=%v body=%q", callback.Code, callback.Header(), callback.Body.String())
			}
		})
	}
}

func TestSensitiveStepUpFrontendInterceptsOnlyAuditedForms(t *testing.T) {
	script, err := portalAssets.ReadFile("assets/theme.js")
	if err != nil {
		t.Fatal(err)
	}
	contents := string(script)
	for _, required := range []string{
		`form.querySelector('[name="idempotency_key"]')`, `form.querySelector('[name="reason"]')`,
		`fetch("/auth/step-up/status"`, `cache: "no-store"`, `/auth/step-up?`,
		`termlink:sensitive-step-up`, `HTMLFormElement.prototype.submit.call(form)`,
		`document.querySelectorAll('[name="reason"]')`, `hidden.name = "reason"`,
	} {
		if !strings.Contains(contents, required) {
			t.Fatalf("step-up frontend contract omitted %q", required)
		}
	}
}

func TestSensitiveAdminActionRequiresStrongVerificationReasonAndIdempotency(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := portal.config.Repository.(*testPortalRepository)
	repository.account.Role = store.RoleSuperAdmin
	targetID := uuid.MustParse("22222222-2222-4222-8222-222222222222")
	target := "/admin/accounts/" + targetID.String() + "/role"

	seal := func(authenticatedAt time.Time, mfa bool) *http.Cookie {
		value, err := portal.sessionCodec.seal(webSession{
			Subject: "sensitive-subject", Username: "sensitive-admin", Role: string(store.RoleSuperAdmin), CSRF: "sensitive-csrf",
			AuthenticatedAtMS: authenticatedAt.UnixMilli(), MFA: mfa, ExpiresAtMS: portal.now().Add(time.Hour).UnixMilli(),
		})
		if err != nil {
			t.Fatal(err)
		}
		return &http.Cookie{Name: sessionCookieName, Value: value, Path: "/"}
	}
	validValues := url.Values{
		"csrf": {"sensitive-csrf"}, "role": {"USER"}, "reason": {"权限复核后降级账号"},
		"idempotency_key": {"account-role-sensitive-0001"},
	}

	withoutAuthTime := seal(time.UnixMilli(0), true)
	response := performCommerceForm(portal.Handler(), target, validValues, withoutAuthTime)
	if response.Code != http.StatusForbidden || !strings.Contains(response.Body.String(), "需要安全验证") {
		t.Fatalf("missing auth_time response=%d %q", response.Code, response.Body.String())
	}
	withoutMFA := seal(portal.now().Add(-time.Minute), false)
	response = performCommerceForm(portal.Handler(), target, validValues, withoutMFA)
	if response.Code != http.StatusForbidden || !strings.Contains(response.Body.String(), "未绑定时重新验证一次登录密码") {
		t.Fatalf("missing MFA response=%d %q", response.Code, response.Body.String())
	}
	passwordValue, err := portal.sessionCodec.seal(webSession{
		Subject: "sensitive-subject", Username: "sensitive-admin", Role: string(store.RoleSuperAdmin), CSRF: "sensitive-csrf",
		AuthenticatedAtMS: portal.now().Add(-time.Hour).UnixMilli(), MFA: false,
		SensitiveVerifiedAtMS: portal.now().Add(-time.Minute).UnixMilli(), SensitiveMethod: sensitiveMethodPassword,
		ExpiresAtMS: portal.now().Add(time.Hour).UnixMilli(),
	})
	if err != nil {
		t.Fatal(err)
	}
	repository.roleErr = store.ErrLastSuperAdmin
	passwordFallback := url.Values{
		"csrf": {"sensitive-csrf"}, "role": {"USER"}, "reason": {"密码二次验证后执行"},
		"idempotency_key": {"account-role-password-0001"},
	}
	response = performCommerceForm(portal.Handler(), target, passwordFallback,
		&http.Cookie{Name: sessionCookieName, Value: passwordValue, Path: "/"})
	if response.Code != http.StatusConflict {
		t.Fatalf("password fallback did not authorize sensitive action: %d %q", response.Code, response.Body.String())
	}
	strong := seal(portal.now().Add(-time.Minute), true)
	missingReason := url.Values{"csrf": {"sensitive-csrf"}, "role": {"USER"}, "idempotency_key": {"account-role-sensitive-0002"}}
	response = performCommerceForm(portal.Handler(), target, missingReason, strong)
	if response.Code != http.StatusConflict {
		t.Fatalf("automatic reason response=%d body=%q", response.Code, response.Body.String())
	}
	missingKey := url.Values{"csrf": {"sensitive-csrf"}, "role": {"USER"}, "reason": {"权限复核后降级账号"}}
	response = performCommerceForm(portal.Handler(), target, missingKey, strong)
	if response.Code != http.StatusBadRequest || !strings.Contains(response.Body.String(), "请求标识无效") {
		t.Fatalf("missing idempotency key response=%d %q", response.Code, response.Body.String())
	}
	if len(repository.securityEvents) < 3 {
		t.Fatalf("sensitive denials were not persistently audited: %+v", repository.securityEvents)
	}
	for _, event := range repository.securityEvents {
		if event.Action != "web.sensitive_action.denied" || event.Result != "DENIED" ||
			strings.Contains(string(event.Metadata), "sensitive-csrf") || strings.Contains(string(event.Metadata), "private-acr") {
			t.Fatalf("unsafe sensitive denial audit: %+v", event)
		}
	}

	response = performCommerceForm(portal.Handler(), target, validValues, strong)
	if response.Code != http.StatusConflict {
		t.Fatalf("complete strong request response=%d %q", response.Code, response.Body.String())
	}
	if repository.idempotencyDone["account-role-sensitive-0001"] != http.StatusInternalServerError {
		t.Fatalf("sensitive action completion=%v", repository.idempotencyDone)
	}
	repository.duplicateClaim = true
	duplicate := url.Values{
		"csrf": {"sensitive-csrf"}, "role": {"USER"}, "reason": {"权限复核后降级账号"},
		"idempotency_key": {"account-role-sensitive-0003"},
	}
	response = performCommerceForm(portal.Handler(), target, duplicate, strong)
	if response.Code != http.StatusConflict || !strings.Contains(response.Body.String(), "请求已提交") {
		t.Fatalf("duplicate sensitive request response=%d %q", response.Code, response.Body.String())
	}
}

func TestEverySensitiveAdminFormCarriesRequiredFields(t *testing.T) {
	formPattern := regexp.MustCompile(`(?s)<form[^>]+action="/admin[^>]*>.*?</form>`)
	for _, name := range []string{"admin.html", "commerce-admin.html"} {
		contents, err := portalAssets.ReadFile("assets/" + name)
		if err != nil {
			t.Fatal(err)
		}
		forms := formPattern.FindAllString(string(contents), -1)
		if len(forms) == 0 {
			t.Fatalf("%s has no sensitive administrator forms", name)
		}
		for _, form := range forms {
			for _, required := range []string{`name="csrf"`, `name="reason"`, `name="idempotency_key"`} {
				if !strings.Contains(form, required) {
					t.Errorf("%s administrator form omitted %s: %.180s", name, required, form)
				}
			}
		}
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
		{name: "identity password", returnTo: "/account/identity/password", wantTo: "/account/identity/password", input: "dark", wantTheme: "dark"},
		{name: "identity mfa", returnTo: "/account/identity/mfa", wantTo: "/account/identity/mfa", input: "light", wantTheme: "light"},
		{name: "identity sessions", returnTo: "/account/identity/sessions", wantTo: "/account/identity/sessions", input: "system", wantTheme: "system"},
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

func TestPortalIdentityDialogActionsUseAllowlistedFreshLoginAndKeycloakSections(t *testing.T) {
	for _, tt := range []struct {
		action   string
		fragment string
	}{
		{action: "password", fragment: "/security/signingin"},
		{action: "mfa", fragment: "/security/signingin"},
		{action: "sessions", fragment: "/security/device-activity"},
	} {
		t.Run(tt.action, func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{Subject: "dialog-subject", Username: "dialog-user", MFA: true}}
			portal := newTestPortal(t, flow)
			start := performRequest(portal.Handler(), http.MethodGet, "/account/identity/"+tt.action+"?termlink_theme=dark", nil)
			if start.Code != http.StatusSeeOther {
				t.Fatalf("identity dialog start status=%d location=%q", start.Code, start.Header().Get("Location"))
			}
			loginLocation, err := url.Parse(start.Header().Get("Location"))
			if err != nil || loginLocation.Path != "/login" || loginLocation.Query().Get("return_to") != "/account/identity/"+tt.action {
				t.Fatalf("identity dialog login location=%q err=%v", start.Header().Get("Location"), err)
			}
			oauthCookie, state := startLoginWithParams(t, portal, flow, "/account/identity/"+tt.action, "dark")
			callback := performRequest(portal.Handler(), http.MethodGet, "/auth/callback?code=production-code&state="+url.QueryEscape(state.State), nil, oauthCookie)
			target, err := url.Parse(callback.Header().Get("Location"))
			if callback.Code != http.StatusSeeOther || err != nil || target.Fragment != tt.fragment || target.Query().Get(themeQueryParameter) != "dark" {
				t.Fatalf("identity dialog callback=%d location=%q target=%+v err=%v", callback.Code, callback.Header().Get("Location"), target, err)
			}
		})
	}
	portal := newTestPortal(t, &testOIDCFlow{})
	if response := performRequest(portal.Handler(), http.MethodGet, "/account/identity/attacker", nil); response.Code != http.StatusNotFound {
		t.Fatalf("unknown identity dialog action status=%d, want 404", response.Code)
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

func TestPortalMFAClaimUpgradeRotatesAndClearsLegacyWebSessionCookie(t *testing.T) {
	if sessionCookieName == legacySessionCookie || !strings.HasSuffix(sessionCookieName, "-v2") {
		t.Fatal("MFA claim semantics must use a new Web session cookie namespace")
	}
	flow := &testOIDCFlow{identity: accountIdentity{Subject: "rotated-session-subject", Username: "rotated-session-user", MFA: true}}
	portal := newTestPortal(t, flow)
	oauthCookie, state := startLoginWithParams(t, portal, flow, "/account", "dark")
	callback := performRequest(portal.Handler(), http.MethodGet, "/auth/callback?code=production-code&state="+url.QueryEscape(state.State), nil, oauthCookie)
	var issuedCurrent, clearedLegacy bool
	for _, cookie := range callback.Result().Cookies() {
		issuedCurrent = issuedCurrent || (cookie.Name == sessionCookieName && cookie.Value != "" && cookie.MaxAge >= 0)
		clearedLegacy = clearedLegacy || (cookie.Name == legacySessionCookie && cookie.MaxAge < 0)
	}
	if !issuedCurrent || !clearedLegacy {
		t.Fatalf("MFA claim upgrade cookies current=%t legacy-cleared=%t", issuedCurrent, clearedLegacy)
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

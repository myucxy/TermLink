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
)

const testPublicHost = "termlink.example:8910"

type testOIDCFlow struct {
	state         string
	nonce         string
	challenge     string
	code          string
	verifier      string
	expectedNonce string
	identity      accountIdentity
	exchangeErr   error
}

func (f *testOIDCFlow) AuthorizationURL(state, nonce, challenge string) string {
	f.state = state
	f.nonce = nonce
	f.challenge = challenge
	query := url.Values{"state": {state}, "nonce": {nonce}, "code_challenge": {challenge}}
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

func (*testOIDCFlow) AccountURL() string { return "https://identity.example/account/" }

func newTestPortal(t *testing.T, flow *testOIDCFlow) *Portal {
	t.Helper()
	clock := time.Date(2026, time.August, 4, 10, 0, 0, 0, time.UTC)
	config := Config{
		PublicURL:   "https://" + testPublicHost,
		Issuer:      "https://identity.example/realms/termlink",
		ClientID:    "termlink-web",
		RedirectURI: "https://" + testPublicHost + "/auth/callback",
		CookieKey:   []byte("0123456789abcdef0123456789abcdef"),
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
	t.Helper()
	recorder := performRequest(portal.Handler(), http.MethodGet, "/login", nil)
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
	if state.State == "" || state.Nonce == "" || state.Verifier == "" || state.ReturnTo != "/account" {
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

func completeLogin(t *testing.T, portal *Portal, flow *testOIDCFlow) (*http.Cookie, webSession) {
	t.Helper()
	oauthCookie, state := startLogin(t, portal, flow)
	target := "/auth/callback?code=production-code&state=" + url.QueryEscape(state.State)
	recorder := performRequest(portal.Handler(), http.MethodGet, target, nil, oauthCookie)
	if recorder.Code != http.StatusSeeOther || recorder.Header().Get("Location") != "/account" {
		t.Fatalf("callback response = %d %q, want 303 /account", recorder.Code, recorder.Header().Get("Location"))
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
	for _, text := range []string{"离开电脑", "真实终端", "端到端加密", "安全登录"} {
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
	last := len(tampered.Value) - 1
	if last < 0 {
		t.Fatal("session cookie was empty")
	}
	replacement := byte('A')
	if tampered.Value[last] == replacement {
		replacement = 'B'
	}
	tampered.Value = tampered.Value[:last] + string(replacement)
	account := performRequest(portal.Handler(), http.MethodGet, "/account", nil, &tampered)
	if account.Code != http.StatusSeeOther || account.Header().Get("Location") != "/login" {
		t.Fatalf("tampered session response = %d %q, want 303 /login", account.Code, account.Header().Get("Location"))
	}

	badStateFlow := &testOIDCFlow{exchangeErr: errors.New("must not be called")}
	badStatePortal := newTestPortal(t, badStateFlow)
	oauthCookie, state := startLogin(t, badStatePortal, badStateFlow)
	callback := performRequest(badStatePortal.Handler(), http.MethodGet, "/auth/callback?code=code&state="+url.QueryEscape(state.State+"-tampered"), nil, oauthCookie)
	if callback.Code != http.StatusBadRequest || badStateFlow.code != "" {
		t.Fatalf("tampered OAuth state response = %d; exchange code = %q", callback.Code, badStateFlow.code)
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

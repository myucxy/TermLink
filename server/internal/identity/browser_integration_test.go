package identity

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/pquerna/otp/totp"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/humanverification"
)

const identityTestOrigin = "https://identity.example.test"

type capturedPasswordReset struct {
	email string
	link  string
}

type capturePasswordResetMailer struct {
	deliveries chan capturedPasswordReset
}

func (m *capturePasswordResetMailer) SendPasswordResetLink(_ context.Context, email, link string) error {
	m.deliveries <- capturedPasswordReset{email: email, link: link}
	return nil
}

type identityTestBrowser struct {
	handler http.Handler
	cookies map[string]*http.Cookie
}

type browserHumanVerification struct {
	enabled bool
	siteKey string
	calls   []browserHumanCall
}

type browserHumanCall struct {
	token    string
	remoteIP string
	action   string
}

func (h *browserHumanVerification) Runtime(context.Context) (humanverification.Runtime, error) {
	return humanverification.Runtime{Enabled: h.enabled, SiteKey: h.siteKey}, nil
}

func (h *browserHumanVerification) VerifyAction(_ context.Context, token, remoteIP, action string) error {
	if !h.enabled {
		return nil
	}
	h.calls = append(h.calls, browserHumanCall{token: token, remoteIP: remoteIP, action: action})
	if token == "" {
		return humanverification.ErrRequired
	}
	if token != "valid-login-token" || action != humanverification.LoginAction {
		return humanverification.ErrRejected
	}
	return nil
}

func TestEmbeddedLoginTemplatePreservesTermLinkIdentityShell(t *testing.T) {
	var output bytes.Buffer
	err := loginTemplate.Execute(&output, loginPageData{
		Action: "/realms/termlink/protocol/openid-connect/auth?client_id=termlink-web",
		CSRF:   "test-csrf", RegisterURL: "/register?continue=test",
	})
	if err != nil {
		t.Fatal(err)
	}
	body := output.String()
	for _, expected := range []string{
		`class="identity-titlebar"`, `class="identity-layout"`, `class="identity-context"`,
		`class="identity-auth-pane"`, `/assets/identity.css?v=identity-v2`,
		`/assets/theme-init.js?v=theme-sync-v1`, `/assets/theme.js?v=theme-sync-v1`,
		`TermLink Server · Healthy`, `Authorization Code + PKCE`, `href="/account"`,
		`href="/register?continue=test"`, `name="termlink_csrf" value="test-csrf"`,
		`name="username"`, `name="password"`,
	} {
		if !strings.Contains(body, expected) {
			t.Fatalf("embedded login omitted shared identity-shell contract %q", expected)
		}
	}
	for _, retired := range []string{`class="identity-card"`, `Keycloak · Healthy`, `name="otp"`} {
		if strings.Contains(body, retired) {
			t.Fatalf("embedded login retained obsolete markup %q", retired)
		}
	}
}

func TestEmbeddedLoginTemplateRendersConditionalHumanVerification(t *testing.T) {
	var output bytes.Buffer
	err := loginTemplate.Execute(&output, loginPageData{
		Action: "/realms/termlink/protocol/openid-connect/auth?client_id=termlink-web",
		CSRF:   "test-csrf", RegisterURL: "/register?continue=test",
		HumanVerificationRequired: true, HumanVerificationSiteKey: "test-site-key",
	})
	if err != nil {
		t.Fatal(err)
	}
	body := output.String()
	for _, expected := range []string{
		humanverification.TurnstileScriptOrigin + `/turnstile/v0/api.js`,
		`class="cf-turnstile registration-human-widget"`, `data-sitekey="test-site-key"`,
		`data-action="login"`, `data-response-field-name="cf-turnstile-response"`,
	} {
		if !strings.Contains(body, expected) {
			t.Fatalf("enabled login omitted human-verification contract %q", expected)
		}
	}
	output.Reset()
	if err = loginTemplate.Execute(&output, loginPageData{OTPOnly: true}); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(output.String(), "cf-turnstile") {
		t.Fatal("MFA-only login unexpectedly repeated human verification")
	}
}

func newIdentityTestBrowser(handler http.Handler) *identityTestBrowser {
	return &identityTestBrowser{handler: handler, cookies: make(map[string]*http.Cookie)}
}

func TestEmbeddedIdentitySecurityHeadersPreserveSameOriginFormMetadata(t *testing.T) {
	provider := &Provider{}
	tests := []struct {
		path         string
		frameOptions string
		ancestors    string
	}{
		{path: "/realms/termlink/protocol/openid-connect/auth", frameOptions: "SAMEORIGIN", ancestors: "frame-ancestors 'self'"},
		{path: "/realms/termlink/account", frameOptions: "SAMEORIGIN", ancestors: "frame-ancestors 'self'"},
		{path: "/realms/termlink/account/password/change", frameOptions: "SAMEORIGIN", ancestors: "frame-ancestors 'self'"},
		{path: "/realms/termlink/.well-known/openid-configuration", frameOptions: "DENY", ancestors: "frame-ancestors 'none'"},
		{path: "/realms/termlink/protocol/openid-connect/auth/extra", frameOptions: "DENY", ancestors: "frame-ancestors 'none'"},
		{path: "/realms/termlink/accounting", frameOptions: "DENY", ancestors: "frame-ancestors 'none'"},
	}
	for _, test := range tests {
		t.Run(test.path, func(t *testing.T) {
			response := httptest.NewRecorder()
			request := httptest.NewRequest(http.MethodGet, identityTestOrigin+test.path, nil)
			provider.securityHeaders(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(http.StatusOK)
			})).ServeHTTP(response, request)

			if got := response.Header().Get("Referrer-Policy"); got != "strict-origin" {
				t.Fatalf("Referrer-Policy = %q, want strict-origin", got)
			}
			if got := response.Header().Get("X-Frame-Options"); got != test.frameOptions {
				t.Fatalf("X-Frame-Options = %q, want %q", got, test.frameOptions)
			}
			if policy := response.Header().Get("Content-Security-Policy"); !strings.Contains(policy, test.ancestors) {
				t.Fatalf("Content-Security-Policy = %q, want %q", policy, test.ancestors)
			} else if scriptsAllowed := strings.Contains(policy, "script-src 'self'"); scriptsAllowed != (test.frameOptions == "SAMEORIGIN") {
				t.Fatalf("Content-Security-Policy script permission = %t for %q", scriptsAllowed, test.path)
			}
		})
	}
}

func TestEmbeddedIdentityAuxiliaryPagesLoadSharedThemeRuntime(t *testing.T) {
	templates := []struct {
		name   string
		render func(*bytes.Buffer) error
	}{
		{name: "account", render: func(output *bytes.Buffer) error { return accountTemplate.Execute(output, accountPageData{}) }},
		{name: "password-reset", render: func(output *bytes.Buffer) error {
			return passwordResetTemplate.Execute(output, passwordResetPageData{})
		}},
		{name: "mfa-enrollment", render: func(output *bytes.Buffer) error {
			return mfaEnrollmentTemplate.Execute(output, mfaEnrollmentPageData{})
		}},
		{name: "recovery-codes", render: func(output *bytes.Buffer) error {
			return recoveryCodesTemplate.Execute(output, recoveryCodesPageData{})
		}},
		{name: "outcome", render: func(output *bytes.Buffer) error { return identityOutcomeTemplate.Execute(output, outcomePageData{}) }},
	}
	for _, test := range templates {
		t.Run(test.name, func(t *testing.T) {
			var output bytes.Buffer
			if err := test.render(&output); err != nil {
				t.Fatal(err)
			}
			for _, contract := range []string{
				`<meta name="color-scheme" content="light dark">`,
				`/assets/theme-init.js?v=theme-sync-v1`,
				`/assets/theme.js?v=theme-sync-v1`,
			} {
				if !strings.Contains(output.String(), contract) {
					t.Fatalf("identity %s page omitted theme contract %q", test.name, contract)
				}
			}
		})
	}
	for _, contract := range []string{`html[data-theme="dark"]`, `--bg:#f7f8fc`, `--bg:#0b0f14`} {
		if !strings.Contains(identityCSS, contract) {
			t.Fatalf("identity auxiliary stylesheet omitted %q", contract)
		}
	}
}

func TestEmbeddedIdentityFormOriginAllowsOnlySameOriginPrivacyFallback(t *testing.T) {
	provider := &Provider{publicOrigin: identityTestOrigin}
	request := httptest.NewRequest(http.MethodPost, identityTestOrigin+"/realms/termlink/protocol/openid-connect/auth", nil)
	request.Header.Set("Origin", "null")
	request.Header.Set("Sec-Fetch-Site", "same-origin")
	if !provider.trustedIdentityFormOrigin(request) {
		t.Fatal("same-origin browser privacy fallback was rejected")
	}

	request.Header.Set("Sec-Fetch-Site", "cross-site")
	if provider.trustedIdentityFormOrigin(request) {
		t.Fatal("cross-site browser request was accepted")
	}
	request.Header.Set("Origin", "https://attacker.example")
	request.Header.Set("Sec-Fetch-Site", "same-origin")
	if provider.trustedIdentityFormOrigin(request) {
		t.Fatal("same-site fallback accepted an explicit attacker Origin")
	}
	request.Header.Set("Origin", "null")
	request.Header.Set("Sec-Fetch-Site", "same-origin")
	request.Host = "attacker.example"
	if provider.trustedIdentityFormOrigin(request) {
		t.Fatal("privacy fallback accepted an unexpected request host")
	}
}

func (b *identityTestBrowser) request(method, target string, form url.Values, withOrigin bool) *httptest.ResponseRecorder {
	var body *strings.Reader
	if form == nil {
		body = strings.NewReader("")
	} else {
		body = strings.NewReader(form.Encode())
	}
	if strings.HasPrefix(target, "/") {
		target = identityTestOrigin + target
	}
	request := httptest.NewRequest(method, target, body)
	if form != nil {
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}
	if withOrigin {
		request.Header.Set("Origin", identityTestOrigin)
	}
	for _, cookie := range b.cookies {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	b.handler.ServeHTTP(response, request)
	for _, cookie := range response.Result().Cookies() {
		if cookie.MaxAge < 0 {
			delete(b.cookies, cookie.Name)
			continue
		}
		copy := *cookie
		b.cookies[cookie.Name] = &copy
	}
	return response
}

func TestEmbeddedIdentityBrowserSecurityFlows(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for embedded identity integration tests")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	pool, cleanup := newIdentityTestPool(t, ctx, databaseURL)
	defer cleanup()

	now := time.Now().UTC().Truncate(time.Second)
	provider, err := New(ctx, Config{
		Issuer: identityTestOrigin + "/realms/termlink", Pool: pool,
		HMACSecret: make([]byte, 64), KeyEncryptionKey: bytesOf(0x72, 32), Clock: func() time.Time { return now },
		AndroidClientID: "termlink-android", AndroidRedirectURI: "dev.termlink:/oauth2redirect",
		WindowsClientID: "termlink-windows", WindowsRedirectURI: "http://127.0.0.1/oauth2/callback",
		WebClientID: "termlink-web", WebRedirectURI: identityTestOrigin + "/auth/callback",
		StepUpClientID: "termlink-web-step-up", AllowedParties: []string{"termlink-android", "termlink-windows"},
	})
	if err != nil {
		t.Fatal(err)
	}
	mailer := &capturePasswordResetMailer{deliveries: make(chan capturedPasswordReset, 1)}
	provider.SetPasswordResetMailer(mailer)
	humanVerifier := &browserHumanVerification{siteKey: "test-login-site-key"}
	provider.SetHumanVerification(humanVerifier)
	input := auth.DirectRegistrationInput{
		Username: "browser-user", Nickname: "Browser User", Email: "browser@example.test",
		Password: "Initial browser password 42!", Confirm: "Initial browser password 42!",
	}
	if err = provider.Accounts().RegisterVerifiedEmail(ctx, input); err != nil {
		t.Fatal(err)
	}

	browser := newIdentityTestBrowser(provider.Handler())
	resetPath := "/realms/termlink/account/password/reset"
	page := browser.request(http.MethodGet, resetPath, nil, false)
	assertIdentityStatus(t, page, http.StatusOK)
	csrf := identityHiddenValue(t, page.Body.String(), "termlink_form_csrf")
	assertHostCookie(t, page, browserFormCSRFCookie)

	invalidOrigin := browser.request(http.MethodPost, resetPath, url.Values{
		"termlink_form_csrf": {csrf}, "login": {input.Username},
	}, false)
	assertIdentityStatus(t, invalidOrigin, http.StatusForbidden)
	if !strings.Contains(invalidOrigin.Body.String(), "request expired") {
		t.Fatalf("invalid-origin response omitted bilingual expiry message: %s", invalidOrigin.Body.String())
	}

	page = browser.request(http.MethodGet, resetPath, nil, false)
	csrf = identityHiddenValue(t, page.Body.String(), "termlink_form_csrf")
	badCSRF := browser.request(http.MethodPost, resetPath, url.Values{
		"termlink_form_csrf": {"not-the-issued-token"}, "login": {input.Username},
	}, true)
	assertIdentityStatus(t, badCSRF, http.StatusForbidden)

	page = browser.request(http.MethodGet, resetPath, nil, false)
	csrf = identityHiddenValue(t, page.Body.String(), "termlink_form_csrf")
	startedAt := time.Now()
	requested := browser.request(http.MethodPost, resetPath, url.Values{
		"termlink_form_csrf": {csrf}, "login": {input.Username},
	}, true)
	assertIdentityStatus(t, requested, http.StatusOK)
	if time.Since(startedAt) < passwordResetResponseFloor-50*time.Millisecond {
		t.Fatal("password-reset response floor was not applied")
	}
	if !strings.Contains(requested.Body.String(), "If the account is eligible") {
		t.Fatalf("password-reset response leaked a specific account result: %s", requested.Body.String())
	}

	var delivery capturedPasswordReset
	select {
	case delivery = <-mailer.deliveries:
	case <-time.After(2 * time.Second):
		t.Fatal("password-reset email was not queued")
	}
	if delivery.email != input.Email {
		t.Fatalf("password-reset recipient = %q", delivery.email)
	}
	resetURL, err := url.Parse(delivery.link)
	if err != nil || resetURL.Query().Get("token") == "" {
		t.Fatalf("password-reset link = %q, %v", delivery.link, err)
	}
	page = browser.request(http.MethodGet, resetURL.RequestURI(), nil, false)
	assertIdentityStatus(t, page, http.StatusOK)
	csrf = identityHiddenValue(t, page.Body.String(), "termlink_form_csrf")
	newPassword := "Reset browser password 84!"
	completed := browser.request(http.MethodPost, resetPath, url.Values{
		"termlink_form_csrf": {csrf}, "reset_token": {resetURL.Query().Get("token")},
		"password": {newPassword}, "password_confirm": {newPassword},
	}, true)
	assertIdentityStatus(t, completed, http.StatusOK)
	if !strings.Contains(completed.Body.String(), "Your password was updated") {
		t.Fatalf("password-reset completion response = %s", completed.Body.String())
	}
	if _, err = provider.Accounts().Authenticate(ctx, input.Username, newPassword); err != nil {
		t.Fatalf("authentication after browser password reset: %v", err)
	}

	verifier := base64.RawURLEncoding.EncodeToString(bytesOf(0x43, 32))
	challenge := sha256.Sum256([]byte(verifier))
	query := url.Values{
		"client_id": {"termlink-web"}, "response_type": {"code"},
		"redirect_uri": {identityTestOrigin + "/auth/callback"}, "scope": {"openid profile email"},
		"state": {strings.Repeat("b", 32)}, "nonce": {strings.Repeat("c", 32)},
		"code_challenge": {base64.RawURLEncoding.EncodeToString(challenge[:])}, "code_challenge_method": {"S256"},
		"prompt": {"login"},
	}
	authorizePath := "/realms/termlink/protocol/openid-connect/auth?" + query.Encode()
	login := browser.request(http.MethodGet, authorizePath, nil, false)
	assertIdentityStatus(t, login, http.StatusOK)
	loginCSRF := identityHiddenValue(t, login.Body.String(), "termlink_csrf")
	assertHostCookie(t, login, browserCSRFCookie)
	denied := browser.request(http.MethodPost, authorizePath, url.Values{
		"termlink_csrf": {loginCSRF}, "username": {input.Username}, "password": {newPassword},
	}, false)
	assertIdentityStatus(t, denied, http.StatusOK)
	if denied.Header().Get("Location") != "" || !strings.Contains(denied.Body.String(), "sign-in request expired") {
		t.Fatalf("cross-origin login was not rejected: location=%q body=%s", denied.Header().Get("Location"), denied.Body.String())
	}

	login = browser.request(http.MethodGet, authorizePath, nil, false)
	loginCSRF = identityHiddenValue(t, login.Body.String(), "termlink_csrf")
	loggedIn := browser.request(http.MethodPost, authorizePath, url.Values{
		"termlink_csrf": {loginCSRF}, "username": {input.Username}, "password": {newPassword},
	}, true)
	assertIdentityStatus(t, loggedIn, http.StatusSeeOther)
	if loggedIn.Header().Get("Location") == "" {
		t.Fatal("browser authorization omitted callback redirect")
	}
	assertHostCookie(t, loggedIn, browserSessionCookie)

	accountPath := "/realms/termlink/account/"
	account := browser.request(http.MethodGet, accountPath, nil, false)
	assertIdentityStatus(t, account, http.StatusOK)
	if !strings.Contains(account.Body.String(), input.Username) || !strings.Contains(account.Body.String(), input.Nickname) {
		t.Fatalf("account page omitted profile: %s", account.Body.String())
	}
	csrf = identityHiddenValue(t, account.Body.String(), "termlink_form_csrf")
	wrongCSRF := browser.request(http.MethodPost, "/realms/termlink/account/mfa/begin", url.Values{
		"termlink_form_csrf": {"wrong-token"}, "current_password": {newPassword},
	}, true)
	assertIdentityStatus(t, wrongCSRF, http.StatusForbidden)

	account = browser.request(http.MethodGet, accountPath, nil, false)
	csrf = identityHiddenValue(t, account.Body.String(), "termlink_form_csrf")
	enrollment := browser.request(http.MethodPost, "/realms/termlink/account/mfa/begin", url.Values{
		"termlink_form_csrf": {csrf}, "current_password": {newPassword},
	}, true)
	assertIdentityStatus(t, enrollment, http.StatusOK)
	secretMatch := regexp.MustCompile(`<code>([A-Z2-7]+)</code>`).FindStringSubmatch(enrollment.Body.String())
	if len(secretMatch) != 2 {
		t.Fatalf("MFA enrollment omitted secret: %s", enrollment.Body.String())
	}
	csrf = identityHiddenValue(t, enrollment.Body.String(), "termlink_form_csrf")
	code, err := totp.GenerateCode(secretMatch[1], now)
	if err != nil {
		t.Fatal(err)
	}
	recovery := browser.request(http.MethodPost, "/realms/termlink/account/mfa/confirm", url.Values{
		"termlink_form_csrf": {csrf}, "code": {code},
	}, true)
	assertIdentityStatus(t, recovery, http.StatusOK)
	if !strings.Contains(recovery.Body.String(), "These recovery codes are shown only once") {
		t.Fatalf("MFA confirmation omitted recovery codes: %s", recovery.Body.String())
	}
	account = browser.request(http.MethodGet, accountPath, nil, false)
	assertIdentityStatus(t, account, http.StatusOK)
	if !strings.Contains(account.Body.String(), "Enabled") {
		t.Fatalf("account page did not reflect MFA enrollment: %s", account.Body.String())
	}

	// Preserve the original hosted-login flow: the first page asks only for
	// username and password, then an enrolled account receives a dedicated MFA
	// step. Do not expose an unexplained OTP field on the initial login page.
	now = now.Add(30 * time.Second)
	humanVerifier.enabled = true
	login = browser.request(http.MethodGet, authorizePath, nil, false)
	assertIdentityStatus(t, login, http.StatusOK)
	if strings.Contains(login.Body.String(), `name="otp"`) ||
		!strings.Contains(login.Body.String(), `data-sitekey="test-login-site-key"`) ||
		!strings.Contains(login.Header().Get("Content-Security-Policy"), humanverification.TurnstileScriptOrigin) ||
		!strings.Contains(login.Header().Get("Content-Security-Policy"), "frame-ancestors 'self'") ||
		login.Header().Get("X-Frame-Options") != "SAMEORIGIN" {
		t.Fatalf("initial MFA login did not render only password plus human verification: %s", login.Body.String())
	}
	loginCSRF = identityHiddenValue(t, login.Body.String(), "termlink_csrf")
	humanRequired := browser.request(http.MethodPost, authorizePath, url.Values{
		"termlink_csrf": {loginCSRF}, "username": {input.Username}, "password": {newPassword},
	}, true)
	assertIdentityStatus(t, humanRequired, http.StatusOK)
	if !strings.Contains(humanRequired.Body.String(), "Complete the human verification first") {
		t.Fatalf("missing login human verification was not rejected: %s", humanRequired.Body.String())
	}
	loginCSRF = identityHiddenValue(t, humanRequired.Body.String(), "termlink_csrf")
	mfaPrompt := browser.request(http.MethodPost, authorizePath, url.Values{
		"termlink_csrf": {loginCSRF}, "username": {input.Username}, "password": {newPassword},
		"cf-turnstile-response": {"valid-login-token"},
	}, true)
	assertIdentityStatus(t, mfaPrompt, http.StatusOK)
	if !strings.Contains(mfaPrompt.Body.String(), `name="otp"`) ||
		strings.Contains(mfaPrompt.Body.String(), `name="password"`) ||
		strings.Contains(mfaPrompt.Body.String(), "cf-turnstile") ||
		strings.Contains(mfaPrompt.Header().Get("Content-Security-Policy"), humanverification.TurnstileScriptOrigin) {
		t.Fatalf("second MFA step did not render an OTP-only form: %s", mfaPrompt.Body.String())
	}
	if len(humanVerifier.calls) != 2 || humanVerifier.calls[1].action != humanverification.LoginAction ||
		humanVerifier.calls[1].token != "valid-login-token" || humanVerifier.calls[1].remoteIP == "" {
		t.Fatalf("login human-verification calls = %+v", humanVerifier.calls)
	}
	loginCSRF = identityHiddenValue(t, mfaPrompt.Body.String(), "termlink_csrf")
	loginCode, err := totp.GenerateCode(secretMatch[1], now)
	if err != nil {
		t.Fatal(err)
	}
	mfaLoggedIn := browser.request(http.MethodPost, authorizePath, url.Values{
		"termlink_csrf": {loginCSRF}, "otp": {loginCode},
	}, true)
	assertIdentityStatus(t, mfaLoggedIn, http.StatusSeeOther)
	if mfaLoggedIn.Header().Get("Location") == "" {
		t.Fatal("MFA browser authorization omitted callback redirect")
	}

	csrf = identityHiddenValue(t, account.Body.String(), "termlink_form_csrf")
	now = now.Add(30 * time.Second)
	disableCode, err := totp.GenerateCode(secretMatch[1], now)
	if err != nil {
		t.Fatal(err)
	}
	disabled := browser.request(http.MethodPost, "/realms/termlink/account/mfa/disable", url.Values{
		"termlink_form_csrf": {csrf}, "current_password": {newPassword}, "second_factor": {disableCode},
	}, true)
	assertIdentityStatus(t, disabled, http.StatusSeeOther)
	if location := disabled.Header().Get("Location"); location != "/realms/termlink/account/?result=mfa-disabled" {
		t.Fatalf("MFA disable redirect = %q", location)
	}
}

func identityHiddenValue(t *testing.T, body, name string) string {
	t.Helper()
	pattern := regexp.MustCompile(`name="` + regexp.QuoteMeta(name) + `" value="([^"]+)"`)
	match := pattern.FindStringSubmatch(body)
	if len(match) != 2 {
		t.Fatalf("page omitted hidden field %q: %s", name, body)
	}
	return match[1]
}

func assertIdentityStatus(t *testing.T, response *httptest.ResponseRecorder, expected int) {
	t.Helper()
	if response.Code != expected {
		t.Fatalf("identity response status/body = %d/%s, want %d", response.Code, response.Body.String(), expected)
	}
}

func assertHostCookie(t *testing.T, response *httptest.ResponseRecorder, name string) {
	t.Helper()
	for _, cookie := range response.Result().Cookies() {
		if cookie.Name != name {
			continue
		}
		if !cookie.Secure || !cookie.HttpOnly || cookie.Path != "/" || cookie.Domain != "" {
			t.Fatalf("__Host cookie %q attributes = Secure:%t HttpOnly:%t Path:%q Domain:%q", name, cookie.Secure, cookie.HttpOnly, cookie.Path, cookie.Domain)
		}
		return
	}
	t.Fatalf("response omitted cookie %q", name)
}

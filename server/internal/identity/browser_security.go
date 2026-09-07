package identity

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/binary"
	"errors"
	"html/template"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const identityFormCSRFLifetime = 10 * time.Minute

const (
	// A bounded response floor reduces the observable timing difference between
	// eligible and ineligible password-reset accounts. The small random tail
	// prevents an attacker from subtracting a perfectly fixed delay.
	passwordResetResponseFloor  = 250 * time.Millisecond
	passwordResetResponseJitter = 75 * time.Millisecond
)

type PasswordResetMailer interface {
	SendPasswordResetLink(context.Context, string, string) error
}

type passwordResetJob struct {
	mailer    PasswordResetMailer
	email     string
	resetLink string
}

type browserFormCSRF struct {
	Token     string `json:"token"`
	ExpiresAt int64  `json:"exp"`
}

type passwordResetPageData struct {
	Action    string
	CSRF      string
	Token     string
	HasToken  bool
	Completed bool
	Message   string
	Error     string
	LoginURL  string
}

type mfaEnrollmentPageData struct {
	CSRF            string
	Secret          string
	ProvisioningURI string
}

type recoveryCodesPageData struct {
	Codes      []string
	AccountURL string
}

type outcomePageData struct {
	Title   string
	Message string
	LinkURL string
	Link    string
}

var passwordResetTemplate = template.Must(template.New("embedded-password-reset").Parse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>TermLink 密码重置 / Password reset</title><script src="/assets/theme-init.js?v=theme-sync-v1"></script><link rel="stylesheet" href="/realms/termlink/resources/identity.css?v=theme-sync-v1"><script src="/assets/theme.js?v=theme-sync-v1" defer></script></head>
<body><main class="identity-card"><div class="brand">TL</div><h1>重置密码</h1><p class="subtitle">Password reset</p>
{{if .Error}}<div class="error" role="alert">{{.Error}}</div>{{end}}{{if .Message}}<div class="success" role="status">{{.Message}}</div>{{end}}
{{if .Completed}}<a class="button" href="{{.LoginURL}}">重新登录 / Sign in again</a>
{{else if .HasToken}}<form method="post" action="{{.Action}}"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><input type="hidden" name="reset_token" value="{{.Token}}"><label>新密码 / New password<input type="password" name="password" minlength="8" maxlength="1024" autocomplete="new-password" required autofocus></label><label>确认新密码 / Confirm new password<input type="password" name="password_confirm" minlength="8" maxlength="1024" autocomplete="new-password" required></label><button type="submit">设置新密码 / Set new password</button></form>
{{else}}<p class="hint">输入用户名或邮箱。如果账号可重置，我们会发送一封 15 分钟内有效的邮件。<br>Enter your username or email. If the account is eligible, we will send a link valid for 15 minutes.</p><form method="post" action="{{.Action}}"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>用户名或邮箱 / Username or email<input name="login" maxlength="320" autocomplete="username" required autofocus></label><button type="submit">发送重置邮件 / Send reset email</button></form>{{end}}
<a class="secondary" href="{{.LoginURL}}">返回登录 / Back to sign in</a></main></body></html>`))

var mfaEnrollmentTemplate = template.Must(template.New("embedded-mfa-enrollment").Parse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>TermLink 绑定动态验证码 / Set up authenticator</title><script src="/assets/theme-init.js?v=theme-sync-v1"></script><link rel="stylesheet" href="/realms/termlink/resources/identity.css?v=theme-sync-v1"><script src="/assets/theme.js?v=theme-sync-v1" defer></script></head>
<body><main class="identity-card"><div class="brand">TL</div><h1>绑定动态验证码</h1><p class="subtitle">Set up authenticator</p><p class="hint">在认证器应用中输入密钥或导入下面的配置 URI，然后输入当前六位验证码。<br>Enter the secret or import the provisioning URI in your authenticator app, then enter the current six-digit code.</p><label>密钥 / Secret<code>{{.Secret}}</code></label><label>配置 URI / Provisioning URI<code>{{.ProvisioningURI}}</code></label><form method="post" action="/realms/termlink/account/mfa/confirm"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>六位验证码 / Six-digit code<input name="code" inputmode="numeric" pattern="[0-9]{6}" maxlength="6" autocomplete="one-time-code" required autofocus></label><button type="submit">确认并启用 / Confirm and enable</button></form></main></body></html>`))

var recoveryCodesTemplate = template.Must(template.New("embedded-recovery-codes").Parse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>TermLink 恢复码 / Recovery codes</title><script src="/assets/theme-init.js?v=theme-sync-v1"></script><link rel="stylesheet" href="/realms/termlink/resources/identity.css?v=theme-sync-v1"><script src="/assets/theme.js?v=theme-sync-v1" defer></script></head>
<body><main class="identity-card"><div class="brand">TL</div><h1>保存恢复码</h1><p class="subtitle">Save your recovery codes</p><div class="error" role="alert">这些恢复码只显示一次。请离线安全保存；每个恢复码只能使用一次。<br>These recovery codes are shown only once. Store them securely offline; each code can be used once.</div><ul class="recovery-list">{{range .Codes}}<li><code>{{.}}</code></li>{{end}}</ul><a class="button" href="{{.AccountURL}}">我已保存 / I have saved them</a></main></body></html>`))

var identityOutcomeTemplate = template.Must(template.New("embedded-outcome").Parse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>{{.Title}}</title><script src="/assets/theme-init.js?v=theme-sync-v1"></script><link rel="stylesheet" href="/realms/termlink/resources/identity.css?v=theme-sync-v1"><script src="/assets/theme.js?v=theme-sync-v1" defer></script></head>
<body><main class="identity-card"><div class="brand">TL</div><h1>{{.Title}}</h1><div class="success" role="status">{{.Message}}</div><a class="button" href="{{.LinkURL}}">{{.Link}}</a></main></body></html>`))

func (p *Provider) runPasswordResetMailer(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case job := <-p.passwordResetJobs:
			deliveryContext, cancel := context.WithTimeout(ctx, 15*time.Second)
			err := job.mailer.SendPasswordResetLink(deliveryContext, job.email, job.resetLink)
			cancel()
			if err != nil {
				slog.Warn("embedded identity password reset delivery failed", "error_class", "password_reset_delivery_failed")
			}
		}
	}
}

func (p *Provider) passwordResetPage(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet {
		p.renderPasswordReset(w, r, strings.TrimSpace(r.URL.Query().Get("token")), "", "", false, http.StatusOK)
		return
	}
	startedAt := time.Now()
	r.Body = http.MaxBytesReader(w, r.Body, maximumIdentityForm)
	if err := p.validateIdentityForm(r); err != nil {
		p.renderPasswordReset(w, r, "", "", "请求已过期，请重试。 / The request expired. Try again.", false, http.StatusForbidden)
		return
	}
	if token := strings.TrimSpace(r.Form.Get("reset_token")); token != "" {
		if err := p.accounts.CompletePasswordReset(r.Context(), token, r.Form.Get("password"), r.Form.Get("password_confirm")); err != nil {
			p.renderPasswordReset(w, r, token, "", "链接无效或已过期，或者新密码不符合要求。 / The link is invalid or expired, or the new password does not meet the requirements.", false, http.StatusBadRequest)
			return
		}
		p.clearBrowserSession(w)
		p.clearIdentityFormCSRF(w)
		p.renderPasswordReset(w, r, "", "密码已更新，请重新登录。 / Your password was updated. Sign in again.", "", true, http.StatusOK)
		return
	}
	email, rawToken, deliver, err := p.accounts.BeginPasswordReset(r.Context(), r.Form.Get("login"))
	if err != nil {
		slog.Warn("embedded identity password reset request failed", "error_class", "password_reset_request_failed")
	}
	if err == nil && deliver && p.passwordResetMailer != nil {
		job := passwordResetJob{
			mailer: p.passwordResetMailer, email: email,
			resetLink: p.issuer + "/account/password/reset?token=" + url.QueryEscape(rawToken),
		}
		select {
		case p.passwordResetJobs <- job:
		default:
			slog.Warn("embedded identity password reset queue is full", "error_class", "password_reset_queue_full")
		}
	}
	waitForPasswordResetResponseFloor(r.Context(), startedAt)
	p.renderPasswordReset(w, r, "", "如果账号可重置，邮件已经发出。请检查收件箱。 / If the account is eligible, the email has been sent. Check your inbox.", "", false, http.StatusOK)
}

func waitForPasswordResetResponseFloor(ctx context.Context, startedAt time.Time) {
	delay := passwordResetResponseFloor
	if passwordResetResponseJitter > 0 {
		var sample [8]byte
		if _, err := rand.Read(sample[:]); err == nil {
			delay += time.Duration(binary.LittleEndian.Uint64(sample[:]) % uint64(passwordResetResponseJitter))
		}
	}
	remaining := delay - time.Since(startedAt)
	if remaining <= 0 {
		return
	}
	timer := time.NewTimer(remaining)
	defer timer.Stop()
	select {
	case <-ctx.Done():
	case <-timer.C:
	}
}

func (p *Provider) renderPasswordReset(w http.ResponseWriter, r *http.Request, token, message, formError string, completed bool, status int) {
	csrf, err := p.issueIdentityFormCSRF(w)
	if err != nil {
		http.Error(w, "身份服务暂不可用 / Identity service unavailable", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = passwordResetTemplate.Execute(w, passwordResetPageData{
		Action: "/realms/termlink/account/password/reset", CSRF: csrf, Token: token, HasToken: token != "",
		Completed: completed, Message: message, Error: formError, LoginURL: "/login",
	})
}

func (p *Provider) changePassword(w http.ResponseWriter, r *http.Request) {
	session, _, ok := p.authorizeIdentityForm(w, r)
	if !ok {
		return
	}
	err := p.accounts.ChangePassword(r.Context(), session.Subject, r.Form.Get("current_password"), r.Form.Get("password"), r.Form.Get("password_confirm"), r.Form.Get("second_factor"))
	if err != nil {
		p.renderAccountPage(w, r, "", "当前密码、验证码或新密码无效。 / The current password, verification code, or new password is invalid.", http.StatusBadRequest)
		return
	}
	p.clearBrowserSession(w)
	p.clearIdentityFormCSRF(w)
	p.renderIdentityOutcome(w, "密码已修改 / Password changed", "所有现有登录令牌已撤销，请重新登录。 / All existing sign-in tokens were revoked. Sign in again.", "/login", "重新登录 / Sign in again")
}

func (p *Provider) beginMFAEnrollment(w http.ResponseWriter, r *http.Request) {
	session, _, ok := p.authorizeIdentityForm(w, r)
	if !ok {
		return
	}
	secret, provisioningURI, err := p.accounts.BeginTOTPEnrollment(r.Context(), session.Subject, r.Form.Get("current_password"))
	if err != nil {
		p.renderAccountPage(w, r, "", "当前密码无效，或动态验证码已经启用。 / The current password is invalid, or an authenticator is already enabled.", http.StatusBadRequest)
		return
	}
	csrf, err := p.issueIdentityFormCSRF(w)
	if err != nil {
		http.Error(w, "身份服务暂不可用 / Identity service unavailable", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = mfaEnrollmentTemplate.Execute(w, mfaEnrollmentPageData{CSRF: csrf, Secret: secret, ProvisioningURI: provisioningURI})
}

func (p *Provider) confirmMFAEnrollment(w http.ResponseWriter, r *http.Request) {
	session, _, ok := p.authorizeIdentityForm(w, r)
	if !ok {
		return
	}
	codes, err := p.accounts.ConfirmTOTPEnrollment(r.Context(), session.Subject, r.Form.Get("code"))
	if err != nil {
		p.renderAccountPage(w, r, "", "验证码无效、已使用或绑定已过期，请重新开始。 / The code is invalid or already used, or enrollment expired. Start again.", http.StatusBadRequest)
		return
	}
	p.refreshBrowserSession(w, r, session, true)
	p.renderRecoveryCodes(w, codes)
}

func (p *Provider) disableMFA(w http.ResponseWriter, r *http.Request) {
	session, _, ok := p.authorizeIdentityForm(w, r)
	if !ok {
		return
	}
	if err := p.accounts.DisableTOTP(r.Context(), session.Subject, r.Form.Get("current_password"), r.Form.Get("second_factor")); err != nil {
		p.renderAccountPage(w, r, "", "当前密码或验证码无效。 / The current password or verification code is invalid.", http.StatusBadRequest)
		return
	}
	p.refreshBrowserSession(w, r, session, false)
	http.Redirect(w, r, "/realms/termlink/account/?result=mfa-disabled", http.StatusSeeOther)
}

func (p *Provider) regenerateRecoveryCodes(w http.ResponseWriter, r *http.Request) {
	session, _, ok := p.authorizeIdentityForm(w, r)
	if !ok {
		return
	}
	codes, err := p.accounts.RegenerateRecoveryCodes(r.Context(), session.Subject, r.Form.Get("current_password"), r.Form.Get("second_factor"))
	if err != nil {
		p.renderAccountPage(w, r, "", "当前密码或验证码无效。 / The current password or verification code is invalid.", http.StatusBadRequest)
		return
	}
	p.refreshBrowserSession(w, r, session, true)
	p.renderRecoveryCodes(w, codes)
}

func (p *Provider) authorizeIdentityForm(w http.ResponseWriter, r *http.Request) (browserSession, Account, bool) {
	r.Body = http.MaxBytesReader(w, r.Body, maximumIdentityForm)
	if err := p.validateIdentityForm(r); err != nil {
		p.renderAccountPage(w, r, "", "请求已过期，请重试。 / The request expired. Try again.", http.StatusForbidden)
		return browserSession{}, Account{}, false
	}
	session, account, err := p.authenticatedBrowserAccount(r)
	if err != nil {
		p.clearBrowserSession(w)
		http.Redirect(w, r, "/login?return_to=/account/identity", http.StatusSeeOther)
		return browserSession{}, Account{}, false
	}
	return session, account, true
}

func (p *Provider) authenticatedBrowserAccount(r *http.Request) (browserSession, Account, error) {
	session, err := p.readBrowserSession(r)
	if err != nil {
		return browserSession{}, Account{}, err
	}
	account, err := p.accounts.LoadAccount(r.Context(), session.Subject)
	if err != nil || !account.Enabled || account.PasswordResetRequired || account.AuthEpoch != session.AuthEpoch {
		return browserSession{}, Account{}, ErrInvalidCredentials
	}
	return session, account, nil
}

func (p *Provider) renderAccountPage(w http.ResponseWriter, r *http.Request, message, formError string, status int) {
	_, account, err := p.authenticatedBrowserAccount(r)
	if err != nil {
		p.clearBrowserSession(w)
		http.Redirect(w, r, "/login?return_to=/account/identity", http.StatusSeeOther)
		return
	}
	csrf, err := p.issueIdentityFormCSRF(w)
	if err != nil {
		http.Error(w, "身份服务暂不可用 / Identity service unavailable", http.StatusServiceUnavailable)
		return
	}
	returnURL := ""
	if message == "" && r.URL.Query().Get("result") == "mfa-disabled" {
		message = "动态验证码已停用。 / Authenticator disabled."
	}
	if candidate := strings.TrimSpace(r.URL.Query().Get("referrer_uri")); candidate != "" {
		if parsed, parseErr := url.Parse(candidate); parseErr == nil && sameIdentityOrigin(parsed, p.publicOrigin) && parsed.User == nil && parsed.Fragment == "" {
			returnURL = parsed.String()
		}
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = accountTemplate.Execute(w, accountPageData{
		Username: account.Username, Nickname: account.Nickname, Email: account.Email, MFA: account.TOTPConfigured,
		ReturnURL: returnURL, LogoutURL: "/realms/termlink/protocol/openid-connect/logout", CSRF: csrf, Error: formError, Message: message,
	})
}

func (p *Provider) refreshBrowserSession(w http.ResponseWriter, r *http.Request, previous browserSession, mfa bool) {
	account, err := p.accounts.LoadAccount(r.Context(), previous.Subject)
	if err != nil {
		p.clearBrowserSession(w)
		return
	}
	methods := []string{"pwd"}
	if mfa {
		methods = []string{"pwd", "otp", "mfa"}
	}
	now := p.clock().UTC()
	p.setBrowserSession(w, browserSession{
		Subject: previous.Subject, AuthEpoch: account.AuthEpoch, AuthenticatedAt: now.Unix(), Methods: methods,
		ExpiresAt: now.Add(embeddedBrowserSessionLifetime).Unix(),
	})
}

func (p *Provider) renderRecoveryCodes(w http.ResponseWriter, codes []string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = recoveryCodesTemplate.Execute(w, recoveryCodesPageData{Codes: codes, AccountURL: "/realms/termlink/account/"})
}

func (p *Provider) renderIdentityOutcome(w http.ResponseWriter, title, message, linkURL, link string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = identityOutcomeTemplate.Execute(w, outcomePageData{Title: title, Message: message, LinkURL: linkURL, Link: link})
}

func (p *Provider) issueIdentityFormCSRF(w http.ResponseWriter) (string, error) {
	token, err := randomIdentityText(24)
	if err != nil {
		return "", err
	}
	now := p.clock().UTC()
	sealed, err := p.browserCookies.seal(browserFormCSRF{Token: token, ExpiresAt: now.Add(identityFormCSRFLifetime).Unix()}, "form-csrf")
	if err != nil {
		return "", err
	}
	http.SetCookie(w, &http.Cookie{
		Name: browserFormCSRFCookie, Value: sealed, Path: "/", Secure: true, HttpOnly: true,
		SameSite: http.SameSiteLaxMode, Expires: now.Add(identityFormCSRFLifetime), MaxAge: int(identityFormCSRFLifetime.Seconds()),
	})
	return token, nil
}

func (p *Provider) validateIdentityForm(r *http.Request) error {
	if !p.trustedIdentityFormOrigin(r) || r.ParseForm() != nil {
		return errors.New("identity form origin or payload is invalid")
	}
	cookie, err := r.Cookie(browserFormCSRFCookie)
	if err != nil {
		return err
	}
	var state browserFormCSRF
	if err = p.browserCookies.open(cookie.Value, "form-csrf", &state); err != nil || state.ExpiresAt < p.clock().UTC().Unix() {
		return errors.New("identity form csrf expired")
	}
	if subtle.ConstantTimeCompare([]byte(state.Token), []byte(r.Form.Get("termlink_form_csrf"))) != 1 {
		return errors.New("identity form csrf mismatch")
	}
	return nil
}

func (p *Provider) trustedIdentityFormOrigin(r *http.Request) bool {
	candidate := strings.TrimSpace(r.Header.Get("Origin"))
	if candidate == "" {
		candidate = strings.TrimSpace(r.Referer())
	}
	parsed, err := url.Parse(candidate)
	if err == nil && parsed.User == nil && sameIdentityOrigin(parsed, p.publicOrigin) {
		return true
	}
	// A browser using a privacy-preserving referrer policy can serialize a
	// same-origin form navigation as Origin: null with no Referer. Sec-Fetch-Site
	// is a forbidden browser header, so accept that case only for the configured
	// public host. The encrypted CSRF cookie and token are still validated by the
	// caller before any account operation is performed.
	return strings.EqualFold(candidate, "null") && p.identityRequestUsesPublicHost(r) &&
		strings.EqualFold(strings.TrimSpace(r.Header.Get("Sec-Fetch-Site")), "same-origin")
}

func (p *Provider) identityRequestUsesPublicHost(r *http.Request) bool {
	expected, err := url.Parse(p.publicOrigin)
	return err == nil && expected.Host != "" && strings.EqualFold(strings.TrimSpace(r.Host), expected.Host)
}

func (p *Provider) clearIdentityFormCSRF(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name: browserFormCSRFCookie, Value: "", Path: "/", Secure: true, HttpOnly: true,
		SameSite: http.SameSiteLaxMode, MaxAge: -1, Expires: time.Unix(1, 0),
	})
}

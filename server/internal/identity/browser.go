package identity

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"html/template"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/ory/fosite"
	"github.com/termlink/termlink/server/internal/clientaddress"
	"github.com/termlink/termlink/server/internal/humanverification"
)

const (
	browserSessionCookie  = "__Host-termlink-idp"
	browserCSRFCookie     = "__Host-termlink-idp-csrf"
	browserFormCSRFCookie = "__Host-termlink-idp-form-csrf"
	maximumIdentityForm   = 64 << 10
)

type browserCookieCodec struct{ aead cipher.AEAD }

type browserSession struct {
	Subject         string   `json:"sub"`
	AuthEpoch       int64    `json:"auth_epoch"`
	AuthenticatedAt int64    `json:"auth_time"`
	Methods         []string `json:"amr"`
	ExpiresAt       int64    `json:"exp"`
}

type browserCSRF struct {
	Token                   string `json:"token"`
	QueryHash               string `json:"query_hash"`
	ExpiresAt               int64  `json:"exp"`
	PendingSubject          string `json:"pending_subject,omitempty"`
	PendingAuthEpoch        int64  `json:"pending_auth_epoch,omitempty"`
	PasswordAuthenticatedAt int64  `json:"password_authenticated_at,omitempty"`
}

type loginPageData struct {
	Action                    string
	CSRF                      string
	Error                     string
	Username                  string
	OTPOnly                   bool
	RegisterURL               string
	AccountLabel              string
	HumanVerificationRequired bool
	HumanVerificationSiteKey  string
}

type accountPageData struct {
	Username  string
	Nickname  string
	Email     string
	MFA       bool
	ReturnURL string
	LogoutURL string
	CSRF      string
	Error     string
	Message   string
}

var loginTemplate = template.Must(template.New("embedded-login").Parse(`<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta name="robots" content="noindex,nofollow">
  <meta name="color-scheme" content="light dark">
  <title>登录到 TermLink</title>
  <link rel="icon" href="/assets/termlink-favicon.svg" type="image/svg+xml">
  <script src="/assets/theme-init.js?v=theme-sync-v1"></script>
  <link rel="stylesheet" href="/assets/identity.css?v=identity-v2">
  <script src="/assets/theme.js?v=theme-sync-v1" defer></script>
  {{if .HumanVerificationRequired}}<script src="https://challenges.cloudflare.com/turnstile/v0/api.js" async defer></script>{{end}}
</head>
<body class="tl-page" data-page-id="login-login">
  <header class="identity-titlebar">
    <a class="identity-brand" href="/" data-theme-transfer aria-label="TermLink 首页">
      <img src="/assets/termlink-app-icon.svg" alt="">
      <span><strong>TermLink</strong><small>Account Center</small></span>
    </a>
    <div class="identity-title-actions">
      <span class="identity-secure-origin"><span class="lock-icon" aria-hidden="true"></span>HTTPS · 已验证来源</span>
      <a class="identity-account-link" href="/account" data-theme-transfer aria-label="返回个人中心">
        <span class="identity-account-link-icon" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false"><path d="M19 12H5m6-6-6 6 6 6"></path></svg></span>
        <span>个人中心</span>
      </a>
      <button class="theme-toggle" type="button" data-theme-toggle aria-label="切换主题" title="切换主题">
        <span class="theme-toggle-icon" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false"><path class="theme-icon-moon" d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"></path><g class="theme-icon-sun"><circle cx="12" cy="12" r="4"></circle><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.42-1.42M17.66 6.34l1.41-1.41"></path></g></svg></span>
        <span class="theme-toggle-label">主题</span>
      </button>
    </div>
  </header>

  <main class="identity-layout">
    <section class="identity-context" aria-labelledby="identity-context-title">
      <div class="identity-context-copy">
        <p class="overline">SELF-HOSTED IDENTITY</p>
        <h1 id="identity-context-title">一个账号，安全连接<br>你的每台终端</h1>
        <p>Android 与 Windows 客户端都在应用内直接登录和注册，并固定连接当前服务；网页用于个人中心与身份安全管理。</p>
      </div>
      <dl class="identity-trust-list">
        <div><span class="trust-icon shield-icon" aria-hidden="true"></span><dt>身份服务</dt><dd>TermLink Server · Healthy</dd></div>
        <div><span class="trust-icon users-icon" aria-hidden="true"></span><dt>注册策略</dt><dd>由 TermLink 服务端统一控制</dd></div>
        <div><span class="trust-icon lock-icon" aria-hidden="true"></span><dt>授权协议</dt><dd>Authorization Code + PKCE</dd></div>
      </dl>
      <div class="bootstrap-summary">
        <span class="bootstrap-icon shield-icon" aria-hidden="true"></span>
        <span><strong>首次启动 Docker？</strong><small>管理员凭据通过受管 Secret 初始化，不进入网页、镜像或版本库。</small></span>
      </div>
    </section>

    <section class="identity-auth-pane" aria-labelledby="kc-page-title">
      <div class="identity-form-shell">
        <header class="identity-form-heading">
          <p class="overline">TERMLINK ACCOUNT</p>
          <h2 id="kc-page-title">{{if .OTPOnly}}验证身份{{else}}欢迎回来{{end}}</h2>
          <p>{{if .OTPOnly}}输入动态验证码或恢复码以继续。{{else}}继续访问你的设备与 Server Console。{{end}}</p>
        </header>
        {{if .Error}}<div class="tl-alert tl-alert-error" role="alert"><span class="tl-alert-mark" aria-hidden="true"></span><span class="tl-alert-title">{{.Error}}</span></div>{{end}}
        {{if .OTPOnly}}<div class="attempted-user"><span>{{.AccountLabel}}</span></div>{{end}}
        {{if not .OTPOnly}}
        <div class="auth-mode-switch" role="tablist" aria-label="网页账号操作">
          <span class="is-active" role="tab" aria-selected="true">登录</span>
          <a href="{{.RegisterURL}}" data-theme-transfer role="tab" aria-selected="false">创建账号</a>
        </div>
        {{end}}
        <form id="kc-form-login" class="tl-form" method="post" action="{{.Action}}" autocomplete="on">
          <input type="hidden" name="termlink_csrf" value="{{.CSRF}}">
          {{if not .OTPOnly}}
          <div class="tl-form-group">
            <label for="username" class="tl-label">邮箱或用户名</label>
            <div class="tl-input-group tl-input-user"><span class="field-icon users-icon" aria-hidden="true"></span><input id="username" class="tl-input" name="username" value="{{.Username}}" type="text" maxlength="320" autocomplete="username" required autofocus dir="ltr"></div>
          </div>
          <div class="tl-form-group">
            <label for="password" class="tl-label">密码</label>
            <div class="tl-input-group tl-input-password" dir="ltr"><span class="field-icon lock-icon" aria-hidden="true"></span><input id="password" class="tl-input" name="password" type="password" maxlength="1024" autocomplete="current-password" required><button class="tl-password-toggle" type="button" aria-label="显示密码" aria-controls="password" data-password-toggle data-label-show="显示密码" data-label-hide="隐藏密码"><span aria-hidden="true"></span></button></div>
          </div>
          <div class="identity-form-options"><span></span><a class="text-action" href="/realms/termlink/account/password/reset">忘记密码？</a></div>
          {{if .HumanVerificationRequired}}
          <div class="tl-form-group registration-human-group">
            <span class="tl-label">人机验证</span>
            <div class="registration-human-verification" tabindex="-1" role="group" aria-label="人机验证">
              <div class="cf-turnstile registration-human-widget" data-sitekey="{{.HumanVerificationSiteKey}}" data-action="login" data-response-field-name="cf-turnstile-response"></div>
              <noscript><span class="registration-human-loading">请启用 JavaScript 后完成人机验证。 / Enable JavaScript to complete human verification.</span></noscript>
            </div>
          </div>
          {{end}}
          {{else}}
          <div class="tl-form-group">
            <label for="otp" class="tl-label">动态验证码或恢复码</label>
            <div class="tl-input-group"><span class="field-icon shield-icon" aria-hidden="true"></span><input id="otp" class="tl-input" name="otp" maxlength="32" autocomplete="one-time-code" required autofocus></div>
          </div>
          {{end}}
          <button class="tl-button tl-button-primary tl-button-block identity-submit" name="login" id="kc-login" type="submit"><span>{{if .OTPOnly}}继续{{else}}登录{{end}}</span><span aria-hidden="true">›</span></button>
        </form>
        <p class="identity-footnote"><span class="lock-icon" aria-hidden="true"></span><span>请确认这是经过验证的 HTTPS TermLink 身份门户。TermLink 不会通过其他域名索取密码。</span></p>
      </div>
    </section>
  </main>
</body>
</html>`))

var accountTemplate = template.Must(template.New("embedded-account").Parse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark"><title>TermLink 身份设置 / Identity settings</title><script src="/assets/theme-init.js?v=theme-sync-v1"></script><link rel="stylesheet" href="/realms/termlink/resources/identity.css?v=theme-sync-v1"><script src="/assets/theme.js?v=theme-sync-v1" defer></script></head>
<body><main class="identity-card wide"><div class="brand">TL</div><h1>身份与安全</h1><p class="subtitle">Identity and security</p>
{{if .Error}}<div class="error" role="alert">{{.Error}}</div>{{end}}{{if .Message}}<div class="success" role="status">{{.Message}}</div>{{end}}
<dl><dt>用户名 / Username</dt><dd>{{.Username}}</dd><dt>昵称 / Nickname</dt><dd>{{.Nickname}}</dd><dt>邮箱 / Email</dt><dd>{{.Email}}</dd><dt>动态验证码 / Authenticator</dt><dd>{{if .MFA}}已启用 / Enabled{{else}}未启用 / Not enabled{{end}}</dd></dl>
<section><h2>修改密码 / Change password</h2><form method="post" action="/realms/termlink/account/password/change"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>当前密码 / Current password<input type="password" name="current_password" maxlength="1024" autocomplete="current-password" required></label><label>新密码 / New password<input type="password" name="password" minlength="8" maxlength="1024" autocomplete="new-password" required></label><label>确认新密码 / Confirm new password<input type="password" name="password_confirm" minlength="8" maxlength="1024" autocomplete="new-password" required></label>{{if .MFA}}<label>动态验证码或恢复码 / Authenticator or recovery code<input name="second_factor" maxlength="32" autocomplete="one-time-code" required></label>{{end}}<button type="submit">修改密码 / Change password</button></form></section>
<section><h2>多因素认证 / Multi-factor authentication</h2>{{if .MFA}}<p class="hint">恢复码只会在绑定或重新生成后显示一次。<br>Recovery codes are shown only once after enrollment or regeneration.</p><form method="post" action="/realms/termlink/account/recovery-codes/regenerate"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>当前密码 / Current password<input type="password" name="current_password" maxlength="1024" autocomplete="current-password" required></label><label>动态验证码或现有恢复码 / Authenticator or existing recovery code<input name="second_factor" maxlength="32" autocomplete="one-time-code" required></label><button type="submit">重新生成恢复码 / Regenerate recovery codes</button></form><form method="post" action="/realms/termlink/account/mfa/disable" class="danger-zone"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>当前密码 / Current password<input type="password" name="current_password" maxlength="1024" autocomplete="current-password" required></label><label>动态验证码或恢复码 / Authenticator or recovery code<input name="second_factor" maxlength="32" autocomplete="one-time-code" required></label><button class="danger" type="submit">停用动态验证码 / Disable authenticator</button></form>{{else}}<form method="post" action="/realms/termlink/account/mfa/begin"><input type="hidden" name="termlink_form_csrf" value="{{.CSRF}}"><label>当前密码 / Current password<input type="password" name="current_password" maxlength="1024" autocomplete="current-password" required></label><button type="submit">绑定动态验证码 / Set up authenticator</button></form>{{end}}</section>
<div class="actions">{{if .ReturnURL}}<a class="button" href="{{.ReturnURL}}">返回 TermLink / Back</a>{{end}}<a class="secondary" href="{{.LogoutURL}}">退出身份会话 / Sign out</a></div>
</main></body></html>`))

const identityCSS = `:root{color-scheme:light;font-family:Inter,"Segoe UI",system-ui,sans-serif;--bg:#f7f8fc;--bg-accent:#e6e9f2;--card:#fff;--surface:#f0f2f8;--text:#171a24;--muted:#4e5668;--border:#737b8e;--border-soft:#d5d9e3;--accent:#4659a8;--accent-on:#fff;--danger:#b3261e;--danger-bg:#fde8e7;--danger-on:#8a1c16;--success-bg:#e3f4eb;--success-on:#176b45;--code-bg:#edf0f7;--code-on:#273a74;--shadow:rgba(23,26,36,.16)}html[data-theme="dark"]{color-scheme:dark;--bg:#0b0f14;--bg-accent:#1b2430;--card:#121820;--surface:#222c39;--text:#e9eef5;--muted:#aab5c5;--border:#465363;--border-soft:#2c3745;--accent:#9cb2ff;--accent-on:#14203d;--danger:#ff8c91;--danger-bg:#451d24;--danger-on:#ffd9df;--success-bg:#123d31;--success-on:#c8ffe9;--code-bg:#071119;--code-on:#b8f4da;--shadow:rgba(0,0,0,.5)}*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:20px;background:radial-gradient(circle at top,var(--bg-accent),var(--bg) 55%);color:var(--text)}.identity-card{width:min(100%,420px);padding:28px;border:1px solid var(--border-soft);border-radius:18px;background:var(--card);box-shadow:0 20px 60px var(--shadow)}.identity-card.wide{width:min(100%,680px)}.brand{display:grid;place-items:center;width:44px;height:44px;border-radius:12px;background:var(--accent);color:var(--accent-on);font-weight:800}h1{margin:18px 0 2px;font-size:1.55rem}h2{margin:0 0 8px;font-size:1.12rem}.subtitle,.hint{color:var(--muted)}.error,.success{margin:18px 0;padding:12px;border-radius:10px}.error{background:var(--danger-bg);color:var(--danger-on)}.success{background:var(--success-bg);color:var(--success-on)}label{display:grid;gap:7px;margin:16px 0;font-size:.92rem}input{width:100%;min-height:46px;padding:10px 12px;border:1px solid var(--border);border-radius:10px;background:var(--surface);color:inherit;font:inherit}input:focus{outline:2px solid var(--accent);outline-offset:2px}button,.button{display:block;width:100%;min-height:46px;padding:12px;border:0;border-radius:10px;background:var(--accent);color:var(--accent-on);font-weight:750;text-align:center;text-decoration:none;cursor:pointer}button.danger{background:var(--danger);color:var(--accent-on)}.secondary{display:block;margin-top:15px;color:var(--accent);text-align:center}.account-label{padding:10px 0;color:var(--text)}dl{display:grid;grid-template-columns:minmax(120px,1fr) 2fr;gap:12px;margin:22px 0}dt{color:var(--muted)}dd{margin:0;overflow-wrap:anywhere}section{margin:22px 0;padding:18px;border:1px solid var(--border-soft);border-radius:12px}.danger-zone{margin-top:28px;padding-top:12px;border-top:1px solid var(--border-soft)}.actions{display:grid;gap:8px;margin-top:20px}code{display:block;padding:12px;border-radius:8px;background:var(--code-bg);color:var(--code-on);overflow-wrap:anywhere}.recovery-list{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px;padding:0;list-style:none}.recovery-list code{text-align:center}@media(max-width:500px){body{padding:12px}.identity-card{padding:22px 18px}dl{grid-template-columns:1fr;gap:3px}dd{margin-bottom:10px}.recovery-list{grid-template-columns:1fr}}`

func serveIdentityCSS(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "text/css; charset=utf-8")
	w.Header().Set("Cache-Control", "public, max-age=3600")
	_, _ = io.WriteString(w, identityCSS)
}

func newBrowserCookieCodec(master []byte) (browserCookieCodec, error) {
	key := deriveIdentityKey(master, "browser-cookie")
	defer clear(key)
	block, err := aes.NewCipher(key)
	if err != nil {
		return browserCookieCodec{}, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return browserCookieCodec{}, err
	}
	return browserCookieCodec{aead: aead}, nil
}

func (c browserCookieCodec) seal(value any, purpose string) (string, error) {
	plain, err := json.Marshal(value)
	if err != nil {
		return "", err
	}
	defer clear(plain)
	nonce := make([]byte, c.aead.NonceSize())
	if _, err = io.ReadFull(rand.Reader, nonce); err != nil {
		return "", err
	}
	sealed := c.aead.Seal(nonce, nonce, plain, []byte(purpose))
	defer clear(sealed)
	return base64.RawURLEncoding.EncodeToString(sealed), nil
}

func (c browserCookieCodec) open(encoded string, purpose string, target any) error {
	sealed, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil || len(sealed) < c.aead.NonceSize()+c.aead.Overhead() {
		return errors.New("identity cookie is invalid")
	}
	defer clear(sealed)
	plain, err := c.aead.Open(nil, sealed[:c.aead.NonceSize()], sealed[c.aead.NonceSize():], []byte(purpose))
	if err != nil {
		return errors.New("identity cookie authentication failed")
	}
	defer clear(plain)
	return json.Unmarshal(plain, target)
}

func (p *Provider) authorize(w http.ResponseWriter, r *http.Request) {
	if len(r.URL.RawQuery) > 8192 {
		http.Error(w, "授权请求过大 / Authorization request is too large", http.StatusRequestURITooLong)
		return
	}
	if r.Method == http.MethodPost {
		r.Body = http.MaxBytesReader(w, r.Body, maximumIdentityForm)
	}
	authorizeRequest, err := p.oauth.NewAuthorizeRequest(r.Context(), r)
	if err != nil {
		p.oauth.WriteAuthorizeError(r.Context(), w, authorizeRequest, err)
		return
	}
	if r.Method == http.MethodGet {
		p.beginAuthorize(w, r, authorizeRequest)
		return
	}
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "GET, POST")
		http.Error(w, "不支持的请求方法 / Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	p.completeAuthorizeLogin(w, r, authorizeRequest)
}

func (p *Provider) beginAuthorize(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester) {
	session, sessionErr := p.readBrowserSession(r)
	prompts := fosite.Arguments(strings.Fields(request.GetRequestForm().Get("prompt")))
	forceLogin := prompts.Has("login") || prompts.Has("select_account")
	clientID := request.GetClient().GetID()
	if sessionErr == nil && !forceLogin {
		account, err := p.accounts.LoadAccount(r.Context(), session.Subject)
		if err == nil && account.Enabled && !account.PasswordResetRequired && session.AuthEpoch == account.AuthEpoch {
			if clientID == p.stepUpClientID && account.TOTPConfigured {
				if prompts.Has("none") {
					p.oauth.WriteAuthorizeError(r.Context(), w, request, fosite.ErrLoginRequired)
					return
				}
				p.renderLogin(w, r, request, "", account.Username, true, account.Username)
				return
			}
			p.issueAuthorization(w, r, request, account, session.Methods, time.Unix(session.AuthenticatedAt, 0).UTC())
			return
		}
	}
	if prompts.Has("none") {
		p.oauth.WriteAuthorizeError(r.Context(), w, request, fosite.ErrLoginRequired)
		return
	}
	p.renderLogin(w, r, request, "", "", false, "")
}

func (p *Provider) completeAuthorizeLogin(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester) {
	csrfState, csrfErr := p.validateBrowserCSRF(r)
	if !p.trustedIdentityFormOrigin(r) || csrfErr != nil {
		p.renderLogin(w, r, request, "登录请求已过期，请重试。 / The sign-in request expired. Try again.", "", false, "")
		return
	}
	clientID := request.GetClient().GetID()
	if clientID == p.stepUpClientID {
		if existing, err := p.readBrowserSession(r); err == nil {
			account, loadErr := p.accounts.LoadAccount(r.Context(), existing.Subject)
			if loadErr == nil && account.Enabled && account.TOTPConfigured && existing.AuthEpoch == account.AuthEpoch {
				if p.accounts.VerifySecondFactor(r.Context(), existing.Subject, r.Form.Get("otp")) == nil {
					method := "recovery"
					if isSixDigitCode(strings.TrimSpace(r.Form.Get("otp"))) {
						method = "otp"
					}
					methods := appendUnique(existing.Methods, method, "mfa")
					now := p.clock().UTC()
					p.setBrowserSession(w, browserSession{Subject: existing.Subject, AuthEpoch: account.AuthEpoch, AuthenticatedAt: now.Unix(), Methods: methods, ExpiresAt: now.Add(embeddedBrowserSessionLifetime).Unix()})
					p.issueAuthorization(w, r, request, account, methods, now)
					return
				}
				p.renderLogin(w, r, request, "验证码无效或已使用。 / The authenticator code is invalid or was already used.", account.Username, true, account.Username)
				return
			}
			p.clearBrowserSession(w)
		}
	}
	if csrfState.PendingSubject != "" {
		account, loadErr := p.accounts.LoadAccount(r.Context(), csrfState.PendingSubject)
		now := p.clock().UTC()
		if loadErr != nil || !account.Enabled || !account.TOTPConfigured ||
			account.AuthEpoch != csrfState.PendingAuthEpoch || csrfState.PasswordAuthenticatedAt <= 0 ||
			csrfState.PasswordAuthenticatedAt < now.Add(-10*time.Minute).Unix() ||
			csrfState.PasswordAuthenticatedAt > now.Add(time.Minute).Unix() {
			p.renderLogin(w, r, request, "登录请求已过期，请重新输入账号和密码。 / The sign-in request expired. Enter your username and password again.", "", false, "")
			return
		}
		if factorErr := p.accounts.VerifySecondFactor(r.Context(), account.Subject.String(), r.Form.Get("otp")); factorErr != nil {
			p.renderPendingLogin(w, r, request, "验证码无效或已使用。 / The authenticator code is invalid or was already used.", account, csrfState.PasswordAuthenticatedAt)
			return
		}
		method := "recovery"
		if isSixDigitCode(strings.TrimSpace(r.Form.Get("otp"))) {
			method = "otp"
		}
		authenticatedAt := time.Unix(csrfState.PasswordAuthenticatedAt, 0).UTC()
		methods := []string{"pwd", method, "mfa"}
		p.setBrowserSession(w, browserSession{Subject: account.Subject.String(), AuthEpoch: account.AuthEpoch, AuthenticatedAt: authenticatedAt.Unix(), Methods: methods, ExpiresAt: p.clock().UTC().Add(embeddedBrowserSessionLifetime).Unix()})
		p.issueAuthorization(w, r, request, account, methods, authenticatedAt)
		return
	}
	username := strings.TrimSpace(r.Form.Get("username"))
	if !p.verifyLoginHuman(w, r, request, username) {
		return
	}
	subject, err := p.accounts.Authenticate(r.Context(), username, r.Form.Get("password"))
	if err != nil {
		p.renderLogin(w, r, request, "登录失败。请检查凭据；迁移账号请先通过“忘记密码”设置新密码。 / Sign-in failed. Check your credentials; migrated accounts must first set a new password through ‘Forgot password’.", username, false, "")
		return
	}
	account, err := p.accounts.LoadAccount(r.Context(), subject)
	if err != nil || !account.Enabled || account.PasswordResetRequired {
		p.renderLogin(w, r, request, "账号需要先完成安全设置。 / Complete the account security setup before signing in.", username, false, "")
		return
	}
	methods := []string{"pwd"}
	if account.TOTPConfigured {
		if strings.TrimSpace(r.Form.Get("otp")) == "" {
			p.renderPendingLogin(w, r, request, "", account, p.clock().UTC().Unix())
			return
		}
		if err = p.accounts.VerifySecondFactor(r.Context(), subject, r.Form.Get("otp")); err != nil {
			p.renderPendingLogin(w, r, request, "验证码无效或已使用。 / The authenticator code is invalid or was already used.", account, p.clock().UTC().Unix())
			return
		}
		method := "recovery"
		if isSixDigitCode(strings.TrimSpace(r.Form.Get("otp"))) {
			method = "otp"
		}
		methods = append(methods, method, "mfa")
	}
	now := p.clock().UTC()
	p.setBrowserSession(w, browserSession{Subject: subject, AuthEpoch: account.AuthEpoch, AuthenticatedAt: now.Unix(), Methods: methods, ExpiresAt: now.Add(embeddedBrowserSessionLifetime).Unix()})
	p.issueAuthorization(w, r, request, account, methods, now)
}

func (p *Provider) issueAuthorization(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester, account Account, methods []string, authenticatedAt time.Time) {
	for _, scope := range request.GetRequestedScopes() {
		request.GrantScope(scope)
	}
	request.GrantAudience(serverAudience)
	removeCredentialFields(request.GetRequestForm())
	key, err := p.signingKeys.Current()
	if err != nil {
		p.oauth.WriteAuthorizeError(r.Context(), w, request, fosite.ErrServerError)
		return
	}
	session := p.identitySession(account, request.GetClient().GetID(), methods, authenticatedAt, key)
	// Fosite uses RequestedAt together with AuthTime to prove prompt=login.
	// This authorization was authenticated in this POST, so represent both at
	// the same instant instead of introducing sub-millisecond clock skew while
	// the session claims are assembled.
	session.IDClaims.RequestedAt = authenticatedAt.UTC()
	signingContext := context.WithValue(r.Context(), signingKeyContextKey{}, key)
	response, err := p.oauth.NewAuthorizeResponse(signingContext, request, session)
	if err != nil {
		p.oauth.WriteAuthorizeError(r.Context(), w, request, err)
		return
	}
	p.clearBrowserCSRF(w)
	p.oauth.WriteAuthorizeResponse(r.Context(), w, request, response)
}

func (p *Provider) renderLogin(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester, message, username string, otpOnly bool, accountLabel string) {
	p.renderLoginWithState(w, r, request, message, username, otpOnly, accountLabel, browserCSRF{})
}

func (p *Provider) renderPendingLogin(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester, message string, account Account, passwordAuthenticatedAt int64) {
	p.renderLoginWithState(w, r, request, message, account.Username, true, account.Username, browserCSRF{
		PendingSubject: account.Subject.String(), PendingAuthEpoch: account.AuthEpoch,
		PasswordAuthenticatedAt: passwordAuthenticatedAt,
	})
}

func (p *Provider) renderLoginWithState(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester, message, username string, otpOnly bool, accountLabel string, state browserCSRF) {
	humanRuntime := humanverification.Runtime{}
	var err error
	if !otpOnly && p.humanVerification != nil {
		humanRuntime, err = p.humanVerification.Runtime(r.Context())
		if err != nil {
			http.Error(w, "人机验证暂不可用 / Human verification is temporarily unavailable", http.StatusServiceUnavailable)
			return
		}
	}
	csrf, err := randomIdentityText(24)
	if err != nil {
		http.Error(w, "身份服务暂不可用 / Identity service unavailable", http.StatusInternalServerError)
		return
	}
	queryHash := sha256.Sum256([]byte(r.URL.RawQuery))
	now := p.clock().UTC()
	state.Token = csrf
	state.QueryHash = base64.RawURLEncoding.EncodeToString(queryHash[:])
	state.ExpiresAt = now.Add(10 * time.Minute).Unix()
	sealed, err := p.browserCookies.seal(state, "csrf")
	if err != nil {
		http.Error(w, "身份服务暂不可用 / Identity service unavailable", http.StatusInternalServerError)
		return
	}
	http.SetCookie(w, &http.Cookie{Name: browserCSRFCookie, Value: sealed, Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteLaxMode, Expires: now.Add(10 * time.Minute), MaxAge: 600})
	action := r.URL.Path
	if r.URL.RawQuery != "" {
		action += "?" + r.URL.RawQuery
	}
	registerURL := "/register?continue=" + url.QueryEscape(p.issuer+"/protocol/openid-connect/auth?"+r.URL.RawQuery)
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	if humanRuntime.Enabled {
		w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'self' "+humanverification.TurnstileScriptOrigin+"; style-src 'self'; img-src 'self' data:; connect-src 'self' "+humanverification.TurnstileScriptOrigin+"; frame-src 'self' "+humanverification.TurnstileScriptOrigin+"; form-action 'self'; frame-ancestors 'self'; base-uri 'none'")
	}
	w.WriteHeader(http.StatusOK)
	_ = loginTemplate.Execute(w, loginPageData{
		Action: action, CSRF: csrf, Error: message, Username: username, OTPOnly: otpOnly,
		RegisterURL: registerURL, AccountLabel: accountLabel,
		HumanVerificationRequired: humanRuntime.Enabled, HumanVerificationSiteKey: humanRuntime.SiteKey,
	})
}

func (p *Provider) verifyLoginHuman(w http.ResponseWriter, r *http.Request, request fosite.AuthorizeRequester, username string) bool {
	if p.humanVerification == nil {
		return true
	}
	err := p.humanVerification.VerifyAction(
		r.Context(), r.Form.Get("cf-turnstile-response"), clientaddress.IP(r), humanverification.LoginAction)
	if err == nil {
		return true
	}
	message := "人机验证服务暂不可用，请稍后重试。 / Human verification is temporarily unavailable. Try again later."
	if errors.Is(err, humanverification.ErrRequired) {
		message = "请先完成人机验证。 / Complete the human verification first."
	} else if errors.Is(err, humanverification.ErrRejected) {
		message = "人机验证未通过，请重新验证。 / Human verification failed. Complete it again."
	}
	p.renderLogin(w, r, request, message, username, false, "")
	return false
}

func (p *Provider) validateBrowserCSRF(r *http.Request) (browserCSRF, error) {
	cookie, err := r.Cookie(browserCSRFCookie)
	if err != nil {
		return browserCSRF{}, err
	}
	var state browserCSRF
	if err = p.browserCookies.open(cookie.Value, "csrf", &state); err != nil || state.ExpiresAt < p.clock().UTC().Unix() {
		return browserCSRF{}, errors.New("identity csrf expired")
	}
	wantHash := sha256.Sum256([]byte(r.URL.RawQuery))
	encodedHash := base64.RawURLEncoding.EncodeToString(wantHash[:])
	if subtle.ConstantTimeCompare([]byte(state.Token), []byte(r.Form.Get("termlink_csrf"))) != 1 ||
		subtle.ConstantTimeCompare([]byte(state.QueryHash), []byte(encodedHash)) != 1 {
		return browserCSRF{}, errors.New("identity csrf mismatch")
	}
	return state, nil
}

func (p *Provider) clearBrowserCSRF(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{Name: browserCSRFCookie, Value: "", Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteLaxMode, MaxAge: -1, Expires: time.Unix(1, 0)})
}

func (p *Provider) setBrowserSession(w http.ResponseWriter, session browserSession) {
	sealed, err := p.browserCookies.seal(session, "session")
	if err != nil {
		return
	}
	expires := time.Unix(session.ExpiresAt, 0).UTC()
	maxAge := int(expires.Sub(p.clock().UTC()).Seconds())
	if maxAge < 1 {
		maxAge = 1
	}
	http.SetCookie(w, &http.Cookie{Name: browserSessionCookie, Value: sealed, Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteLaxMode, Expires: expires, MaxAge: maxAge})
}

func (p *Provider) readBrowserSession(r *http.Request) (browserSession, error) {
	cookie, err := r.Cookie(browserSessionCookie)
	if err != nil {
		return browserSession{}, err
	}
	var session browserSession
	if err = p.browserCookies.open(cookie.Value, "session", &session); err != nil || session.Subject == "" || session.ExpiresAt <= p.clock().UTC().Unix() {
		return browserSession{}, errors.New("identity browser session is invalid")
	}
	return session, nil
}

func (p *Provider) clearBrowserSession(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{Name: browserSessionCookie, Value: "", Path: "/", Secure: true, HttpOnly: true, SameSite: http.SameSiteLaxMode, MaxAge: -1, Expires: time.Unix(1, 0)})
}

func (p *Provider) token(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, maximumIdentityForm)
	request, err := p.oauth.NewAccessRequest(r.Context(), r, newSession())
	if err != nil {
		p.oauth.WriteAccessError(r.Context(), w, request, err)
		return
	}
	for _, scope := range request.GetRequestedScopes() {
		request.GrantScope(scope)
	}
	request.GrantAudience(serverAudience)
	session, ok := request.GetSession().(*Session)
	if !ok || strings.TrimSpace(session.GetSubject()) == "" {
		p.oauth.WriteAccessError(r.Context(), w, request, fosite.ErrInvalidGrant)
		return
	}
	account, err := p.accounts.LoadAccount(r.Context(), session.GetSubject())
	if err != nil || !account.Enabled || account.PasswordResetRequired {
		p.oauth.WriteAccessError(r.Context(), w, request, fosite.ErrInvalidGrant)
		return
	}
	if !request.GetGrantTypes().Has("password") && session.AuthEpoch != account.AuthEpoch {
		p.oauth.WriteAccessError(r.Context(), w, request, fosite.ErrInvalidGrant)
		return
	}
	methods := session.IDTokenClaims().AuthenticationMethodsReferences
	authenticatedAt := session.IDTokenClaims().AuthTime
	if len(methods) == 0 {
		methods = []string{"pwd"}
	}
	if authenticatedAt.IsZero() {
		authenticatedAt = p.clock().UTC()
	}
	key, err := p.signingKeys.Current()
	if err != nil {
		p.oauth.WriteAccessError(r.Context(), w, request, fosite.ErrServerError)
		return
	}
	p.populateIdentitySession(session, account, request.GetClient().GetID(), methods, authenticatedAt, key)
	removeCredentialFields(request.GetRequestForm())
	signingContext := context.WithValue(r.Context(), signingKeyContextKey{}, key)
	response, err := p.oauth.NewAccessResponse(signingContext, request)
	if err != nil {
		p.oauth.WriteAccessError(r.Context(), w, request, err)
		return
	}
	p.oauth.WriteAccessResponse(r.Context(), w, request, response)
}

func (p *Provider) identitySession(account Account, clientID string, methods []string, authenticatedAt time.Time, key signingKey) *Session {
	session := newSession()
	p.populateIdentitySession(session, account, clientID, methods, authenticatedAt, key)
	return session
}

func (p *Provider) populateIdentitySession(session *Session, account Account, clientID string, methods []string, authenticatedAt time.Time, key signingKey) {
	if session.JWTClaims == nil || session.IDClaims == nil {
		fresh := newSession()
		if session.JWTClaims == nil {
			session.JWTClaims = fresh.JWTClaims
		}
		if session.IDClaims == nil {
			session.IDClaims = fresh.IDClaims
		}
	}
	session.Username = account.Username
	session.AuthEpoch = account.AuthEpoch
	session.SetSubject(account.Subject.String())
	methods = appendUnique(nil, methods...)
	acr := "urn:termlink:acr:password"
	if hasMethod(methods, "otp") || hasMethod(methods, "mfa") {
		acr = "urn:termlink:acr:mfa"
	}
	roles := append([]string(nil), account.RealmRoles...)
	profile := map[string]any{
		"azp": clientID, "auth_time": authenticatedAt.Unix(), "amr": methods, "acr": acr,
		"auth_epoch":         account.AuthEpoch,
		"preferred_username": account.Username, "nickname": account.Nickname, "name": account.Nickname,
		"email": account.Email, "email_verified": account.EmailVerified,
		"realm_access":    map[string]any{"roles": roles},
		"resource_access": map[string]any{clientID: map[string]any{"roles": []string{}}},
	}
	session.JWTClaims.Extra = cloneMap(profile)
	session.JWTClaims.Extra["typ"] = "Bearer"
	session.IDClaims.Extra = cloneMap(profile)
	session.IDClaims.AuthTime = authenticatedAt.UTC()
	session.IDClaims.AuthenticationMethodsReferences = append([]string(nil), methods...)
	session.IDClaims.AuthenticationContextClassReference = acr
	session.JWTHeader.Add("kid", key.Public.KeyID)
	session.IDHeader.Add("kid", key.Public.KeyID)
}

func removeCredentialFields(values url.Values) {
	for _, field := range []string{"username", "password", "otp", "recovery_code", "termlink_csrf"} {
		values.Del(field)
	}
}

func (p *Provider) logout(w http.ResponseWriter, r *http.Request) {
	p.clearBrowserSession(w)
	p.clearBrowserCSRF(w)
	target := "/"
	requested := strings.TrimSpace(r.FormValue("post_logout_redirect_uri"))
	if requested != "" {
		if parsed, err := url.Parse(requested); err == nil && sameIdentityOrigin(parsed, p.publicOrigin) && parsed.User == nil && parsed.Fragment == "" {
			target = parsed.String()
		}
	}
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, target, http.StatusSeeOther)
}

func sameIdentityOrigin(candidate *url.URL, expected string) bool {
	return candidate != nil && strings.EqualFold(candidate.Scheme+"://"+candidate.Host, expected)
}

func (p *Provider) accountPage(w http.ResponseWriter, r *http.Request) {
	p.renderAccountPage(w, r, "", "", http.StatusOK)
}

func randomIdentityText(bytes int) (string, error) {
	buffer := make([]byte, bytes)
	if _, err := io.ReadFull(rand.Reader, buffer); err != nil {
		return "", err
	}
	defer clear(buffer)
	return base64.RawURLEncoding.EncodeToString(buffer), nil
}

func appendUnique(target []string, values ...string) []string {
	for _, value := range values {
		value = strings.TrimSpace(value)
		if value == "" || hasMethod(target, value) {
			continue
		}
		target = append(target, value)
	}
	return target
}

func hasMethod(values []string, expected string) bool {
	for _, value := range values {
		if strings.EqualFold(strings.TrimSpace(value), expected) {
			return true
		}
	}
	return false
}

// Package webportal serves the public TermLink product site and its OIDC-backed
// account page. It never handles terminal content or persists browser tokens.
package webportal

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"embed"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
	"golang.org/x/oauth2"
)

const (
	oauthCookieName     = "__Host-termlink-oauth"
	sessionCookieName   = "__Host-termlink-session"
	oauthStateTTL       = 10 * time.Minute
	webSessionTTL       = 8 * time.Hour
	sensitiveActionTTL  = 10 * time.Minute
	maximumCookieSize   = 3800
	recentAuditLimit    = 7
	themeQueryParameter = "termlink_theme"
)

//go:embed assets/*.html assets/*.css assets/*.js assets/*.svg
var portalAssets embed.FS

// Config contains only the public OIDC metadata and the server-held cookie key.
type Config struct {
	PublicURL            string
	Issuer               string
	ClientID             string
	RedirectURI          string
	CookieKey            []byte
	SubjectKey           []byte
	Repository           portalRepository
	AccountStatusChanged func(context.Context, uuid.UUID, string) error
	Clock                func() time.Time
}

type accountIdentity struct {
	Subject  string
	Username string
	Name     string
	Email    string
	Roles    []string
}

type portalRepository interface {
	SyncPortalIdentity(context.Context, [32]byte, store.Role, string) (store.PortalAccount, error)
	RecordPortalLoginFailure(context.Context, string, string) error
	GetPortalAccount(context.Context, [32]byte) (store.PortalAccount, error)
	PortalAdminDashboard(context.Context, uuid.UUID) (store.PortalAdminDashboard, error)
	UpdatePortalAccountRole(context.Context, uuid.UUID, uuid.UUID, store.Role, string) error
	UpdatePortalAccountStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
}

type oidcFlow interface {
	AuthorizationURL(state, nonce, challenge, theme string) string
	ExchangeAndVerify(context.Context, string, string, string) (accountIdentity, error)
	LogoutURL() string
	AccountURL() string
}

type oauthState struct {
	State      string `json:"state"`
	Nonce      string `json:"nonce"`
	Verifier   string `json:"verifier"`
	ReturnTo   string `json:"return_to"`
	IssuedAtMS int64  `json:"issued_at_ms"`
}

type webSession struct {
	Subject           string `json:"subject"`
	Username          string `json:"username"`
	Name              string `json:"name"`
	Email             string `json:"email"`
	Role              string `json:"role"`
	CSRF              string `json:"csrf"`
	AuthenticatedAtMS int64  `json:"authenticated_at_ms"`
	ExpiresAtMS       int64  `json:"expires_at_ms"`
}

type cookieCodec struct {
	aead    cipher.AEAD
	purpose []byte
}

// Portal is the production web handler.
type Portal struct {
	config       Config
	publicOrigin *url.URL
	flow         oidcFlow
	oauthCodec   cookieCodec
	sessionCodec cookieCodec
	templates    *template.Template
	styles       []byte
	adminStyles  []byte
	themeInit    []byte
	themeScript  []byte
	appIcon      []byte
	favicon      []byte
}

type productionOIDCFlow struct {
	oauthConfig        oauth2.Config
	verifier           *oidc.IDTokenVerifier
	issuer             string
	clientID           string
	publicURL          string
	accountURL         string
	endSessionEndpoint string
}

type homePageData struct {
	SignedIn   bool
	Username   string
	LoginURL   string
	AccountURL string
	ConsoleURL string
}

type accountPageData struct {
	Username           string
	Name               string
	Email              string
	IdentityAccountURL string
	CSRF               string
	Role               store.Role
	RoleLabel          string
	CanOpenConsole     bool
	ConsoleURL         string
}

type adminPageData struct {
	Username           string
	Role               store.Role
	RoleLabel          string
	CSRF               string
	Section            string
	SectionTitle       string
	CanManageAccounts  bool
	Dashboard          store.PortalAdminDashboard
	RecentAuditEvents  []store.PortalAuditEvent
	IdentityAccountURL string
	PublicURL          string
	Issuer             string
	Notice             string
}

type errorPageData struct {
	Title   string
	Message string
}

// New discovers the configured OIDC issuer and constructs the public portal.
func New(ctx context.Context, config Config) (*Portal, error) {
	if ctx == nil {
		return nil, errors.New("web portal context is required")
	}
	publicOrigin, err := validateConfig(config)
	if err != nil {
		return nil, err
	}
	provider, err := oidc.NewProvider(ctx, config.Issuer)
	if err != nil {
		return nil, fmt.Errorf("discover web OIDC issuer: %w", err)
	}
	var discovery struct {
		EndSessionEndpoint string `json:"end_session_endpoint"`
	}
	if err := provider.Claims(&discovery); err != nil {
		return nil, fmt.Errorf("decode web OIDC discovery: %w", err)
	}
	accountURL := strings.TrimRight(config.Issuer, "/") + "/account/"
	accountQuery := url.Values{}
	accountQuery.Set("referrer", config.ClientID)
	accountQuery.Set("referrer_uri", strings.TrimRight(config.PublicURL, "/")+"/account")
	accountURL += "?" + accountQuery.Encode()
	flow := &productionOIDCFlow{
		oauthConfig: oauth2.Config{
			ClientID:    config.ClientID,
			Endpoint:    provider.Endpoint(),
			RedirectURL: config.RedirectURI,
			Scopes:      []string{oidc.ScopeOpenID, "profile", "email"},
		},
		verifier:           provider.Verifier(&oidc.Config{ClientID: config.ClientID}),
		issuer:             strings.TrimRight(config.Issuer, "/"),
		clientID:           config.ClientID,
		publicURL:          strings.TrimRight(config.PublicURL, "/"),
		accountURL:         accountURL,
		endSessionEndpoint: discovery.EndSessionEndpoint,
	}
	return newPortal(config, publicOrigin, flow)
}

func newPortal(config Config, publicOrigin *url.URL, flow oidcFlow) (*Portal, error) {
	parsed, err := template.New("portal").Funcs(template.FuncMap{
		"formatTime":         formatPortalTime,
		"formatOptionalTime": formatOptionalPortalTime,
		"shortID":            shortID,
		"roleLabel":          roleLabel,
		"statusLabel":        statusLabel,
	}).ParseFS(portalAssets, "assets/*.html")
	if err != nil {
		return nil, fmt.Errorf("parse web portal templates: %w", err)
	}
	styles, err := portalAssets.ReadFile("assets/site.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal styles: %w", err)
	}
	adminStyles, err := portalAssets.ReadFile("assets/admin.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal admin styles: %w", err)
	}
	themeInit, err := portalAssets.ReadFile("assets/theme-init.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal pre-paint theme script: %w", err)
	}
	themeScript, err := portalAssets.ReadFile("assets/theme.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal theme script: %w", err)
	}
	appIcon, err := portalAssets.ReadFile("assets/termlink-app-icon.svg")
	if err != nil {
		return nil, fmt.Errorf("read web portal app icon: %w", err)
	}
	favicon, err := portalAssets.ReadFile("assets/termlink-favicon.svg")
	if err != nil {
		return nil, fmt.Errorf("read web portal favicon: %w", err)
	}
	oauthCodec, err := newCookieCodec(config.CookieKey, oauthCookieName)
	if err != nil {
		return nil, err
	}
	sessionCodec, err := newCookieCodec(config.CookieKey, sessionCookieName)
	if err != nil {
		return nil, err
	}
	if config.Clock == nil {
		config.Clock = time.Now
	}
	return &Portal{
		config:       config,
		publicOrigin: publicOrigin,
		flow:         flow,
		oauthCodec:   oauthCodec,
		sessionCodec: sessionCodec,
		templates:    parsed,
		styles:       styles,
		adminStyles:  adminStyles,
		themeInit:    themeInit,
		themeScript:  themeScript,
		appIcon:      appIcon,
		favicon:      favicon,
	}, nil
}

func validateConfig(config Config) (*url.URL, error) {
	publicOrigin, err := url.Parse(strings.TrimSpace(config.PublicURL))
	if err != nil || publicOrigin.Scheme != "https" || publicOrigin.Host == "" || publicOrigin.User != nil ||
		(publicOrigin.Path != "" && publicOrigin.Path != "/") || publicOrigin.RawPath != "" ||
		publicOrigin.RawQuery != "" || publicOrigin.Fragment != "" {
		return nil, errors.New("web portal public URL must be an HTTPS origin")
	}
	issuer, err := url.Parse(strings.TrimSpace(config.Issuer))
	if err != nil || issuer.Scheme != "https" || issuer.Host == "" || issuer.User != nil {
		return nil, errors.New("web portal issuer must use HTTPS")
	}
	redirect, err := url.Parse(strings.TrimSpace(config.RedirectURI))
	if err != nil || redirect.Scheme != "https" || !strings.EqualFold(redirect.Host, publicOrigin.Host) ||
		redirect.Path != "/auth/callback" || redirect.RawQuery != "" || redirect.Fragment != "" || redirect.User != nil {
		return nil, errors.New("web portal redirect URI must use the public HTTPS callback")
	}
	if strings.TrimSpace(config.ClientID) == "" {
		return nil, errors.New("web portal OIDC client ID is required")
	}
	if len(config.CookieKey) < 32 {
		return nil, errors.New("web portal cookie key must contain at least 32 bytes")
	}
	if len(config.SubjectKey) < 32 {
		return nil, errors.New("web portal subject key must contain at least 32 bytes")
	}
	if config.Repository == nil {
		return nil, errors.New("web portal account repository is required")
	}
	return publicOrigin, nil
}

func newCookieCodec(master []byte, purpose string) (cookieCodec, error) {
	material := make([]byte, 0, len(master)+len(purpose)+24)
	material = append(material, []byte("TermLink web cookie V1\x00")...)
	material = append(material, purpose...)
	material = append(material, 0)
	material = append(material, master...)
	key := sha256.Sum256(material)
	clear(material)
	block, err := aes.NewCipher(key[:])
	clear(key[:])
	if err != nil {
		return cookieCodec{}, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return cookieCodec{}, err
	}
	return cookieCodec{aead: aead, purpose: []byte(purpose)}, nil
}

func (c cookieCodec) seal(value any) (string, error) {
	plain, err := json.Marshal(value)
	if err != nil {
		return "", err
	}
	defer clear(plain)
	nonce := make([]byte, c.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return "", err
	}
	sealed := c.aead.Seal(nonce, nonce, plain, c.purpose)
	encoded := base64.RawURLEncoding.EncodeToString(sealed)
	clear(sealed)
	if len(encoded) > maximumCookieSize {
		return "", errors.New("web portal cookie exceeds the safe size")
	}
	return encoded, nil
}

func (c cookieCodec) open(encoded string, destination any) error {
	sealed, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil || len(sealed) < c.aead.NonceSize()+c.aead.Overhead() {
		clear(sealed)
		return errors.New("web portal cookie is invalid")
	}
	defer clear(sealed)
	nonce := sealed[:c.aead.NonceSize()]
	plain, err := c.aead.Open(nil, nonce, sealed[c.aead.NonceSize():], c.purpose)
	if err != nil {
		return errors.New("web portal cookie authentication failed")
	}
	defer clear(plain)
	if err := json.Unmarshal(plain, destination); err != nil {
		return errors.New("web portal cookie payload is invalid")
	}
	return nil
}

// Handler returns the complete public portal router with security headers.
func (p *Portal) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", p.home)
	mux.HandleFunc("GET /assets/site.css", p.siteCSS)
	mux.HandleFunc("GET /assets/admin.css", p.adminCSS)
	mux.HandleFunc("GET /assets/theme-init.js", p.themeInitJS)
	mux.HandleFunc("GET /assets/theme.js", p.themeJS)
	mux.HandleFunc("GET /assets/termlink-app-icon.svg", p.svgAsset(p.appIcon))
	mux.HandleFunc("GET /assets/termlink-favicon.svg", p.svgAsset(p.favicon))
	mux.HandleFunc("GET /login", p.login)
	mux.HandleFunc("GET /auth/callback", p.callback)
	mux.HandleFunc("GET /account", p.account)
	mux.HandleFunc("GET /admin", p.admin)
	mux.HandleFunc("POST /admin/accounts/{id}/role", p.adminUpdateRole)
	mux.HandleFunc("POST /admin/accounts/{id}/status", p.adminUpdateStatus)
	mux.HandleFunc("POST /logout", p.logout)
	mux.HandleFunc("/", p.notFound)
	return p.securityHeaders(mux)
}

func (p *Portal) home(w http.ResponseWriter, r *http.Request) {
	data := homePageData{LoginURL: "/login", AccountURL: "/account"}
	if session, err := p.readSession(r); err == nil {
		if account, accountErr := p.currentAccount(r.Context(), session); accountErr == nil {
			data.SignedIn = true
			data.Username = session.Username
			if account.Role.CanOpenConsole() {
				data.ConsoleURL = "/admin"
			}
		}
	}
	p.render(w, http.StatusOK, "home.html", data)
}

func (p *Portal) siteCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.styles)
}

func (p *Portal) adminCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.adminStyles)
}

func (p *Portal) themeJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.themeScript)
}

func (p *Portal) themeInitJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.themeInit)
}

func (p *Portal) staticAsset(w http.ResponseWriter, contentType string, contents []byte) {
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(contents)
}

func (p *Portal) svgAsset(contents []byte) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "image/svg+xml")
		w.Header().Set("Cache-Control", "public, max-age=86400")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(contents)
	}
}

func (p *Portal) login(w http.ResponseWriter, r *http.Request) {
	if !p.usesPublicHost(r) {
		target := strings.TrimRight(p.config.PublicURL, "/") + "/login"
		query := url.Values{}
		if r.URL.Query().Get("return_to") == "/admin" {
			query.Set("return_to", "/admin")
		}
		if theme := normalizeTheme(r.URL.Query().Get(themeQueryParameter)); theme != "" {
			query.Set(themeQueryParameter, theme)
		}
		if encoded := query.Encode(); encoded != "" {
			target += "?" + encoded
		}
		http.Redirect(w, r, target, http.StatusSeeOther)
		return
	}
	state, err := randomText(32)
	if err != nil {
		p.internalError(w, err)
		return
	}
	nonce, err := randomText(32)
	if err != nil {
		p.internalError(w, err)
		return
	}
	verifier, err := randomText(32)
	if err != nil {
		p.internalError(w, err)
		return
	}
	challenge := sha256.Sum256([]byte(verifier))
	returnTo := "/account"
	if r.URL.Query().Get("return_to") == "/admin" {
		returnTo = "/admin"
	}
	stateCookie, err := p.oauthCodec.seal(oauthState{
		State:      state,
		Nonce:      nonce,
		Verifier:   verifier,
		ReturnTo:   returnTo,
		IssuedAtMS: p.now().UnixMilli(),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(w, oauthCookieName, stateCookie, p.now().Add(oauthStateTTL))
	theme := normalizeTheme(r.URL.Query().Get(themeQueryParameter))
	location := p.flow.AuthorizationURL(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]), theme)
	clear(challenge[:])
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, location, http.StatusFound)
}

func (p *Portal) callback(w http.ResponseWriter, r *http.Request) {
	loginTraceID := uuid.NewString()
	p.clearCookie(w, oauthCookieName)
	if !p.usesPublicHost(r) {
		p.renderError(w, http.StatusBadRequest, "授权地址不匹配", "请从 TermLink 公开 HTTPS 首页重新登录。")
		return
	}
	cookie, err := r.Cookie(oauthCookieName)
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "登录已过期", "授权状态已失效，请返回首页重新登录。")
		return
	}
	var pending oauthState
	if err := p.oauthCodec.open(cookie.Value, &pending); err != nil ||
		p.now().Sub(time.UnixMilli(pending.IssuedAtMS)) < 0 ||
		p.now().Sub(time.UnixMilli(pending.IssuedAtMS)) > oauthStateTTL ||
		!constantTextEqual(pending.State, r.URL.Query().Get("state")) {
		p.renderError(w, http.StatusBadRequest, "登录校验失败", "授权响应未通过状态校验，请返回首页重新登录。")
		return
	}
	code := r.URL.Query().Get("code")
	if code == "" || r.URL.Query().Get("error") != "" {
		_ = p.config.Repository.RecordPortalLoginFailure(r.Context(), loginTraceID, "authorization_incomplete")
		p.renderError(w, http.StatusBadRequest, "登录未完成", "身份服务没有返回有效授权，请返回首页重试。")
		return
	}
	identity, err := p.flow.ExchangeAndVerify(r.Context(), code, pending.Verifier, pending.Nonce)
	if err != nil {
		_ = p.config.Repository.RecordPortalLoginFailure(r.Context(), loginTraceID, "token_verification_failed")
		slog.Warn("web OIDC callback rejected", "error", err)
		p.renderError(w, http.StatusUnauthorized, "登录校验失败", "身份令牌未通过校验，请返回首页重新登录。")
		return
	}
	csrf, err := randomText(32)
	if err != nil {
		p.internalError(w, err)
		return
	}
	username := firstNonEmpty(identity.Username, identity.Name, identity.Email, "TermLink 用户")
	account, err := p.config.Repository.SyncPortalIdentity(
		r.Context(),
		auth.SubjectHash(p.config.SubjectKey, auth.Identity{Issuer: p.config.Issuer, Subject: identity.Subject}),
		assertedRole(identity.Roles),
		loginTraceID,
	)
	if err != nil {
		slog.Warn("web OIDC account authorization rejected", "error", err)
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号未启用或授权状态无效，请联系 TermLink 管理员。")
		return
	}
	sessionCookie, err := p.sessionCodec.seal(webSession{
		Subject:           identity.Subject,
		Username:          username,
		Name:              identity.Name,
		Email:             identity.Email,
		Role:              string(account.Role),
		CSRF:              csrf,
		AuthenticatedAtMS: p.now().UnixMilli(),
		ExpiresAtMS:       p.now().Add(webSessionTTL).UnixMilli(),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(w, sessionCookieName, sessionCookie, p.now().Add(webSessionTTL))
	returnTo := pending.ReturnTo
	if returnTo != "/admin" || !account.Role.CanOpenConsole() {
		returnTo = "/account"
	}
	http.Redirect(w, r, returnTo, http.StatusSeeOther)
}

func (p *Portal) account(w http.ResponseWriter, r *http.Request) {
	if !p.usesPublicHost(r) {
		http.Redirect(w, r, strings.TrimRight(p.config.PublicURL, "/")+"/account", http.StatusSeeOther)
		return
	}
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		http.Redirect(w, r, "/login?return_to=/account", http.StatusSeeOther)
		return
	}
	portalAccount, err := p.currentAccount(r.Context(), session)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号已被停用或浏览器会话需要重新登录。")
		return
	}
	p.render(w, http.StatusOK, "account.html", accountPageData{
		Username:           session.Username,
		Name:               session.Name,
		Email:              session.Email,
		IdentityAccountURL: p.flow.AccountURL(),
		CSRF:               session.CSRF,
		Role:               portalAccount.Role,
		RoleLabel:          roleLabel(portalAccount.Role),
		CanOpenConsole:     portalAccount.Role.CanOpenConsole(),
		ConsoleURL:         "/admin",
	})
}

func (p *Portal) admin(w http.ResponseWriter, r *http.Request) {
	if !p.usesPublicHost(r) {
		http.Redirect(w, r, strings.TrimRight(p.config.PublicURL, "/")+"/admin", http.StatusSeeOther)
		return
	}
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return
	}
	dashboard, err := p.config.Repository.PortalAdminDashboard(r.Context(), account.ID)
	if err != nil {
		p.internalError(w, err)
		return
	}
	section := normalizeAdminSection(r.URL.Query().Get("section"))
	recentAuditEvents := dashboard.AuditEvents
	if len(recentAuditEvents) > recentAuditLimit {
		recentAuditEvents = recentAuditEvents[:recentAuditLimit]
	}
	p.render(w, http.StatusOK, "admin.html", adminPageData{
		Username:           session.Username,
		Role:               account.Role,
		RoleLabel:          roleLabel(account.Role),
		CSRF:               session.CSRF,
		Section:            section,
		SectionTitle:       adminSectionTitle(section),
		CanManageAccounts:  account.Role.CanManageAccounts(),
		Dashboard:          dashboard,
		RecentAuditEvents:  recentAuditEvents,
		IdentityAccountURL: p.flow.AccountURL(),
		PublicURL:          strings.TrimRight(p.config.PublicURL, "/"),
		Issuer:             strings.TrimRight(p.config.Issuer, "/"),
		Notice:             adminNotice(r.URL.Query().Get("notice")),
	})
}

func (p *Portal) adminUpdateRole(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	targetID, err := uuid.Parse(r.PathValue("id"))
	if err != nil || r.ParseForm() != nil {
		p.renderError(w, http.StatusBadRequest, "请求无效", "账号或角色参数无效。")
		return
	}
	role, err := store.ParseRole(r.Form.Get("role"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "请求无效", "请选择受支持的账号角色。")
		return
	}
	traceID, err := randomText(12)
	if err != nil {
		p.internalError(w, err)
		return
	}
	if err := p.config.Repository.UpdatePortalAccountRole(r.Context(), account.ID, targetID, role, traceID); err != nil {
		p.handleAdminMutationError(w, err)
		return
	}
	_ = session
	http.Redirect(w, r, "/admin?section=accounts&notice=role-updated", http.StatusSeeOther)
}

func (p *Portal) adminUpdateStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	targetID, err := uuid.Parse(r.PathValue("id"))
	if err != nil || r.ParseForm() != nil {
		p.renderError(w, http.StatusBadRequest, "请求无效", "账号或状态参数无效。")
		return
	}
	status := strings.ToUpper(strings.TrimSpace(r.Form.Get("status")))
	traceID, err := randomText(12)
	if err != nil {
		p.internalError(w, err)
		return
	}
	if err := p.config.Repository.UpdatePortalAccountStatus(r.Context(), account.ID, targetID, status, traceID); err != nil {
		p.handleAdminMutationError(w, err)
		return
	}
	if status != "ACTIVE" && p.config.AccountStatusChanged != nil {
		if err := p.config.AccountStatusChanged(r.Context(), targetID, status); err != nil {
			// The database mutation is already committed. Periodic Realtime
			// authority checks still fail closed; retain the successful response
			// and surface only a content-free operational warning.
			slog.Warn("publish account status change", "error", err)
		}
	}
	http.Redirect(w, r, "/admin?section=accounts&notice=status-updated", http.StatusSeeOther)
}

func (p *Portal) logout(w http.ResponseWriter, r *http.Request) {
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		http.Redirect(w, r, "/", http.StatusSeeOther)
		return
	}
	if !p.usesTrustedFormOrigin(r) {
		p.renderError(w, http.StatusForbidden, "退出请求被拒绝", "请求来源未通过校验。")
		return
	}
	if err := r.ParseForm(); err != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusForbidden, "退出请求被拒绝", "安全校验失败，请刷新个人页后重试。")
		return
	}
	p.clearCookie(w, sessionCookieName)
	http.Redirect(w, r, p.flow.LogoutURL(), http.StatusSeeOther)
}

func (p *Portal) notFound(w http.ResponseWriter, _ *http.Request) {
	p.renderError(w, http.StatusNotFound, "页面不存在", "该地址不属于 TermLink 服务端门户。")
}

func (p *Portal) readSession(r *http.Request) (webSession, error) {
	cookie, err := r.Cookie(sessionCookieName)
	if err != nil {
		return webSession{}, err
	}
	var session webSession
	if err := p.sessionCodec.open(cookie.Value, &session); err != nil {
		return webSession{}, err
	}
	role, roleErr := store.ParseRole(session.Role)
	if strings.TrimSpace(session.Subject) == "" || strings.TrimSpace(session.CSRF) == "" || roleErr != nil ||
		session.AuthenticatedAtMS <= 0 || session.AuthenticatedAtMS > p.now().UnixMilli() ||
		p.now().UnixMilli() >= session.ExpiresAtMS {
		return webSession{}, errors.New("web session expired")
	}
	session.Role = string(role)
	return session, nil
}

func (p *Portal) render(w http.ResponseWriter, status int, name string, data any) {
	var output bytes.Buffer
	if err := p.templates.ExecuteTemplate(&output, name, data); err != nil {
		p.internalError(w, err)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_, _ = w.Write(output.Bytes())
}

func (p *Portal) renderError(w http.ResponseWriter, status int, title, message string) {
	p.render(w, status, "error.html", errorPageData{Title: title, Message: message})
}

func (p *Portal) internalError(w http.ResponseWriter, err error) {
	slog.Error("web portal request failed", "error", err)
	var output bytes.Buffer
	if templateErr := p.templates.ExecuteTemplate(&output, "error.html", errorPageData{
		Title: "服务暂时不可用", Message: "TermLink 服务端暂时无法完成该请求，请稍后重试。",
	}); templateErr != nil {
		slog.Error("render web portal internal error page", "error", templateErr)
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		http.Error(w, "TermLink portal is temporarily unavailable.", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusInternalServerError)
	_, _ = w.Write(output.Bytes())
}

func (p *Portal) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		w.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Cross-Origin-Opener-Policy", "same-origin")
		w.Header().Set("Strict-Transport-Security", "max-age=31536000")
		next.ServeHTTP(w, r)
	})
}

func (p *Portal) setCookie(w http.ResponseWriter, name, value string, expires time.Time) {
	http.SetCookie(w, &http.Cookie{
		Name:     name,
		Value:    value,
		Path:     "/",
		Expires:  expires.UTC(),
		MaxAge:   int(expires.Sub(p.now()).Seconds()),
		Secure:   true,
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
	})
}

func (p *Portal) clearCookie(w http.ResponseWriter, name string) {
	http.SetCookie(w, &http.Cookie{
		Name:     name,
		Value:    "",
		Path:     "/",
		Expires:  time.Unix(1, 0).UTC(),
		MaxAge:   -1,
		Secure:   true,
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
	})
}

func (p *Portal) usesPublicHost(r *http.Request) bool {
	return strings.EqualFold(r.Host, p.publicOrigin.Host)
}

func (p *Portal) usesPublicOrigin(value string) bool {
	origin, err := url.Parse(strings.TrimSpace(value))
	if err != nil || origin.User != nil || origin.RawQuery != "" || origin.Fragment != "" ||
		(origin.Path != "" && origin.Path != "/") {
		return false
	}
	return strings.EqualFold(origin.Scheme, p.publicOrigin.Scheme) &&
		strings.EqualFold(origin.Host, p.publicOrigin.Host)
}

func (p *Portal) usesTrustedFormOrigin(r *http.Request) bool {
	if !p.usesPublicHost(r) {
		return false
	}
	origin := r.Header.Get("Origin")
	if origin == "" || p.usesPublicOrigin(origin) {
		return true
	}
	return strings.EqualFold(strings.TrimSpace(r.Header.Get("Sec-Fetch-Site")), "same-origin")
}

func (p *Portal) now() time.Time { return p.config.Clock().UTC() }

func (f *productionOIDCFlow) AuthorizationURL(state, nonce, challenge, theme string) string {
	options := []oauth2.AuthCodeOption{
		oauth2.SetAuthURLParam("nonce", nonce),
		oauth2.SetAuthURLParam("code_challenge", challenge),
		oauth2.SetAuthURLParam("code_challenge_method", "S256"),
	}
	if theme == "light" || theme == "dark" {
		options = append(options, oauth2.SetAuthURLParam(themeQueryParameter, theme))
	}
	return f.oauthConfig.AuthCodeURL(state, options...)
}

func (f *productionOIDCFlow) ExchangeAndVerify(ctx context.Context, code, verifier, expectedNonce string) (accountIdentity, error) {
	token, err := f.oauthConfig.Exchange(ctx, code, oauth2.VerifierOption(verifier))
	if err != nil {
		return accountIdentity{}, fmt.Errorf("exchange web OIDC code: %w", err)
	}
	rawIDToken, ok := token.Extra("id_token").(string)
	if !ok || rawIDToken == "" {
		return accountIdentity{}, errors.New("web OIDC ID token is missing")
	}
	idToken, err := f.verifier.Verify(ctx, rawIDToken)
	if err != nil {
		return accountIdentity{}, fmt.Errorf("verify web OIDC ID token: %w", err)
	}
	var claims struct {
		Subject           string `json:"sub"`
		Nonce             string `json:"nonce"`
		PreferredUsername string `json:"preferred_username"`
		Name              string `json:"name"`
		Email             string `json:"email"`
		RealmAccess       struct {
			Roles []string `json:"roles"`
		} `json:"realm_access"`
		ResourceAccess map[string]struct {
			Roles []string `json:"roles"`
		} `json:"resource_access"`
	}
	if err := idToken.Claims(&claims); err != nil {
		return accountIdentity{}, errors.New("web OIDC claims are invalid")
	}
	if strings.TrimSpace(claims.Subject) == "" || !constantTextEqual(claims.Nonce, expectedNonce) {
		return accountIdentity{}, errors.New("web OIDC nonce or subject is invalid")
	}
	roles := append([]string(nil), claims.RealmAccess.Roles...)
	if resource, ok := claims.ResourceAccess[f.clientID]; ok {
		roles = append(roles, resource.Roles...)
	}
	return accountIdentity{
		Subject:  claims.Subject,
		Username: claims.PreferredUsername,
		Name:     claims.Name,
		Email:    claims.Email,
		Roles:    roles,
	}, nil
}

func (f *productionOIDCFlow) LogoutURL() string {
	if strings.TrimSpace(f.endSessionEndpoint) == "" {
		return f.publicURL + "/"
	}
	endpoint, err := url.Parse(f.endSessionEndpoint)
	if err != nil || endpoint.Scheme != "https" {
		return f.publicURL + "/"
	}
	query := endpoint.Query()
	query.Set("client_id", f.clientID)
	query.Set("post_logout_redirect_uri", f.publicURL+"/")
	endpoint.RawQuery = query.Encode()
	return endpoint.String()
}

func (f *productionOIDCFlow) AccountURL() string { return f.accountURL }

func randomText(length int) (string, error) {
	value := make([]byte, length)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	defer clear(value)
	return base64.RawURLEncoding.EncodeToString(value), nil
}

func constantTextEqual(left, right string) bool {
	leftBytes := []byte(left)
	rightBytes := []byte(right)
	defer clear(leftBytes)
	defer clear(rightBytes)
	return len(leftBytes) == len(rightBytes) && subtle.ConstantTimeCompare(leftBytes, rightBytes) == 1
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

func normalizeTheme(value string) string {
	value = strings.ToLower(strings.TrimSpace(value))
	if value == "light" || value == "dark" {
		return value
	}
	return ""
}

func assertedRole(roles []string) store.Role {
	result := store.RoleUser
	for _, value := range roles {
		switch strings.ToUpper(strings.TrimSpace(value)) {
		case string(store.RoleSuperAdmin):
			return store.RoleSuperAdmin
		case string(store.RoleAdmin):
			result = store.RoleAdmin
		case string(store.RoleAuditor):
			if result == store.RoleUser {
				result = store.RoleAuditor
			}
		}
	}
	return result
}

func (p *Portal) currentAccount(ctx context.Context, session webSession) (store.PortalAccount, error) {
	return p.config.Repository.GetPortalAccount(ctx, auth.SubjectHash(
		p.config.SubjectKey,
		auth.Identity{Issuer: p.config.Issuer, Subject: session.Subject},
	))
}

func (p *Portal) requireConsole(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		http.Redirect(w, r, "/login?return_to=/admin", http.StatusSeeOther)
		return webSession{}, store.PortalAccount{}, false
	}
	account, err := p.currentAccount(r.Context(), session)
	if err != nil || !account.Role.CanOpenConsole() || !store.Role(session.Role).CanOpenConsole() {
		p.renderError(w, http.StatusForbidden, "无权访问管理台", "当前账号没有 TermLink Server Console 权限。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) requireSuperAdminAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusForbidden, "管理请求被拒绝", "请求来源或安全校验无效，请刷新管理台后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	if account.Role != store.RoleSuperAdmin || store.Role(session.Role) != store.RoleSuperAdmin {
		p.renderError(w, http.StatusForbidden, "需要超级管理员", "该操作仅允许 SUPER_ADMIN 执行。")
		return webSession{}, store.PortalAccount{}, false
	}
	age := p.now().Sub(time.UnixMilli(session.AuthenticatedAtMS))
	if age < 0 || age > sensitiveActionTTL {
		p.renderError(w, http.StatusForbidden, "需要重新认证", "高风险操作仅允许在最近登录后的 10 分钟内执行，请安全退出并重新登录。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) handleAdminMutationError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrLastSuperAdmin):
		p.renderError(w, http.StatusConflict, "无法修改唯一超级管理员", "请先创建并验证另一个 SUPER_ADMIN，再修改当前唯一超级管理员。")
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "管理请求被拒绝", "当前账号或目标状态不允许该操作。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "账号不存在", "目标账号可能已被删除。")
	default:
		p.internalError(w, err)
	}
}

func normalizeAdminSection(value string) string {
	switch value {
	case "overview", "connections", "deployment", "audit", "billing", "identity", "accounts", "redemptions":
		return value
	default:
		return "overview"
	}
}

func adminSectionTitle(section string) string {
	return map[string]string{
		"overview": "运行概览", "connections": "连接与路由", "deployment": "Docker 部署",
		"audit": "安全审计", "billing": "订阅与权益", "identity": "身份与安全",
		"accounts": "账号与角色", "redemptions": "兑换码管理",
	}[normalizeAdminSection(section)]
}

func adminNotice(value string) string {
	switch value {
	case "role-updated":
		return "账号角色已更新，并已写入安全审计。"
	case "status-updated":
		return "账号状态已更新，并已写入安全审计。"
	default:
		return ""
	}
}

func roleLabel(role store.Role) string {
	switch role {
	case store.RoleSuperAdmin:
		return "超级管理员"
	case store.RoleAdmin:
		return "管理员"
	case store.RoleAuditor:
		return "审计员"
	default:
		return "普通用户"
	}
}

func statusLabel(status string) string {
	switch status {
	case "ACTIVE":
		return "已启用"
	case "LOCKED":
		return "已停用"
	case "DELETED":
		return "已删除"
	default:
		return status
	}
}

func formatPortalTime(value time.Time) string {
	if value.IsZero() {
		return "—"
	}
	return value.UTC().Format("2006-01-02 15:04 UTC")
}

func formatOptionalPortalTime(value *time.Time) string {
	if value == nil {
		return "尚未上报"
	}
	return formatPortalTime(*value)
}

func shortID(value uuid.UUID) string {
	text := value.String()
	if len(text) < 8 {
		return text
	}
	return text[:8] + "…"
}

func clear(value []byte) {
	for index := range value {
		value[index] = 0
	}
}

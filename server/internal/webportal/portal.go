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
	"golang.org/x/oauth2"
)

const (
	oauthCookieName   = "__Host-termlink-oauth"
	sessionCookieName = "__Host-termlink-session"
	oauthStateTTL     = 10 * time.Minute
	webSessionTTL     = 8 * time.Hour
	maximumCookieSize = 3800
)

//go:embed assets/*.html assets/*.css
var portalAssets embed.FS

// Config contains only the public OIDC metadata and the server-held cookie key.
type Config struct {
	PublicURL   string
	Issuer      string
	ClientID    string
	RedirectURI string
	CookieKey   []byte
	Clock       func() time.Time
}

type accountIdentity struct {
	Subject  string
	Username string
	Name     string
	Email    string
}

type oidcFlow interface {
	AuthorizationURL(state, nonce, challenge string) string
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
	Subject     string `json:"subject"`
	Username    string `json:"username"`
	Name        string `json:"name"`
	Email       string `json:"email"`
	CSRF        string `json:"csrf"`
	ExpiresAtMS int64  `json:"expires_at_ms"`
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
}

type accountPageData struct {
	Username           string
	Name               string
	Email              string
	IdentityAccountURL string
	CSRF               string
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
		accountURL:         strings.TrimRight(config.Issuer, "/") + "/account/",
		endSessionEndpoint: discovery.EndSessionEndpoint,
	}
	return newPortal(config, publicOrigin, flow)
}

func newPortal(config Config, publicOrigin *url.URL, flow oidcFlow) (*Portal, error) {
	parsed, err := template.ParseFS(portalAssets, "assets/*.html")
	if err != nil {
		return nil, fmt.Errorf("parse web portal templates: %w", err)
	}
	styles, err := portalAssets.ReadFile("assets/site.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal styles: %w", err)
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
	}, nil
}

func validateConfig(config Config) (*url.URL, error) {
	publicOrigin, err := url.Parse(strings.TrimSpace(config.PublicURL))
	if err != nil || publicOrigin.Scheme != "https" || publicOrigin.Host == "" || publicOrigin.User != nil ||
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
	mux.HandleFunc("GET /login", p.login)
	mux.HandleFunc("GET /auth/callback", p.callback)
	mux.HandleFunc("GET /account", p.account)
	mux.HandleFunc("POST /logout", p.logout)
	mux.HandleFunc("/", p.notFound)
	return p.securityHeaders(mux)
}

func (p *Portal) home(w http.ResponseWriter, r *http.Request) {
	data := homePageData{LoginURL: "/login", AccountURL: "/account"}
	if session, err := p.readSession(r); err == nil {
		data.SignedIn = true
		data.Username = session.Username
	}
	p.render(w, http.StatusOK, "home.html", data)
}

func (p *Portal) siteCSS(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "text/css; charset=utf-8")
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(p.styles)
}

func (p *Portal) login(w http.ResponseWriter, r *http.Request) {
	if !p.usesPublicHost(r) {
		http.Redirect(w, r, strings.TrimRight(p.config.PublicURL, "/")+"/login", http.StatusSeeOther)
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
	stateCookie, err := p.oauthCodec.seal(oauthState{
		State:      state,
		Nonce:      nonce,
		Verifier:   verifier,
		ReturnTo:   "/account",
		IssuedAtMS: p.now().UnixMilli(),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(w, oauthCookieName, stateCookie, p.now().Add(oauthStateTTL))
	location := p.flow.AuthorizationURL(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]))
	clear(challenge[:])
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, location, http.StatusFound)
}

func (p *Portal) callback(w http.ResponseWriter, r *http.Request) {
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
		p.renderError(w, http.StatusBadRequest, "登录未完成", "身份服务没有返回有效授权，请返回首页重试。")
		return
	}
	identity, err := p.flow.ExchangeAndVerify(r.Context(), code, pending.Verifier, pending.Nonce)
	if err != nil {
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
	sessionCookie, err := p.sessionCodec.seal(webSession{
		Subject:     identity.Subject,
		Username:    username,
		Name:        identity.Name,
		Email:       identity.Email,
		CSRF:        csrf,
		ExpiresAtMS: p.now().Add(webSessionTTL).UnixMilli(),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(w, sessionCookieName, sessionCookie, p.now().Add(webSessionTTL))
	returnTo := pending.ReturnTo
	if returnTo != "/account" {
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
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return
	}
	p.render(w, http.StatusOK, "account.html", accountPageData{
		Username:           session.Username,
		Name:               session.Name,
		Email:              session.Email,
		IdentityAccountURL: p.flow.AccountURL(),
		CSRF:               session.CSRF,
	})
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
	if strings.TrimSpace(session.Subject) == "" || strings.TrimSpace(session.CSRF) == "" ||
		p.now().UnixMilli() >= session.ExpiresAtMS {
		return webSession{}, errors.New("web session expired")
	}
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
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	http.Error(w, "TermLink portal is temporarily unavailable.", http.StatusInternalServerError)
}

func (p *Portal) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Security-Policy", "default-src 'none'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
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

func (f *productionOIDCFlow) AuthorizationURL(state, nonce, challenge string) string {
	return f.oauthConfig.AuthCodeURL(
		state,
		oauth2.SetAuthURLParam("nonce", nonce),
		oauth2.SetAuthURLParam("code_challenge", challenge),
		oauth2.SetAuthURLParam("code_challenge_method", "S256"),
	)
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
	}
	if err := idToken.Claims(&claims); err != nil {
		return accountIdentity{}, errors.New("web OIDC claims are invalid")
	}
	if strings.TrimSpace(claims.Subject) == "" || !constantTextEqual(claims.Nonce, expectedNonce) {
		return accountIdentity{}, errors.New("web OIDC nonce or subject is invalid")
	}
	return accountIdentity{
		Subject:  claims.Subject,
		Username: claims.PreferredUsername,
		Name:     claims.Name,
		Email:    claims.Email,
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

func clear(value []byte) {
	for index := range value {
		value[index] = 0
	}
}

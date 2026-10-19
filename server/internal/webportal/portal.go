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
	"strconv"
	"strings"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/accountdeletion"
	"github.com/termlink/termlink/server/internal/adminroute"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/buildinfo"
	"github.com/termlink/termlink/server/internal/humanverification"
	"github.com/termlink/termlink/server/internal/localnet"
	"github.com/termlink/termlink/server/internal/presence"
	"github.com/termlink/termlink/server/internal/serviceemail"
	"github.com/termlink/termlink/server/internal/store"
	"golang.org/x/oauth2"
)

const (
	oauthCookieName         = "__Host-termlink-oauth"
	legacySessionCookie     = "__Host-termlink-session"
	sessionCookieName       = "__Host-termlink-session-v2"
	sensitiveOIDCPurpose    = "sensitive-action"
	sensitiveMethodMFA      = "mfa"
	sensitiveMethodPassword = "password"
	oauthStateTTL           = 10 * time.Minute
	// Sliding inactivity window: requireAccount re-issues the cookie once
	// less than half of it remains, so active browsers stay signed in while
	// abandoned ones age out.
	webSessionTTL       = 20 * 24 * time.Hour
	recentLoginTTL      = 10 * time.Minute
	sensitiveActionTTL  = 20 * time.Minute
	maximumCookieSize   = 3800
	themeQueryParameter = "termlink_theme"
)

//go:embed assets/*.html assets/*.css assets/*.js assets/*.svg
var portalAssets embed.FS

// Config contains only the public OIDC metadata and the server-held cookie key.
type Config struct {
	PublicURL                 string
	Issuer                    string
	BackchannelIssuer         string
	ClientID                  string
	WindowsClientID           string
	StepUpClientID            string
	RedirectURI               string
	HumanVerificationProvider string
	HumanVerificationSiteKey  string
	HumanVerification         *humanverification.Manager
	CookieKey                 []byte
	SubjectKey                []byte
	OIDCDiscovery             *auth.OIDCDiscovery
	OIDCHTTPClient            *http.Client
	ReleaseSource             PublicReleaseSource
	BuildInfo                 buildinfo.Info
	RegistrationCodeKey       []byte
	AccountDeletionBinder     accountdeletion.RequestBinder
	// RegistrationRecoveryEnabled and AccountDeletionEnabled resolve the
	// runtime feature flags (DB override with the environment default as
	// fallback). A nil predicate keeps the deployment-level default.
	RegistrationRecoveryEnabled func(context.Context) bool
	AccountDeletionEnabled      func(context.Context) bool
	// RuntimeSettingStore powers the admin-page deployment parameter panel;
	// Defaults holds the environment bootstrap values for display, and the
	// Apply callbacks push accepted values into the live subsystems.
	RuntimeSettingStore    RuntimeSettingStore
	RuntimeSettingDefaults map[string]string
	ApplyDirectSTUNServers func(raw string) error
	ServiceEmail           *serviceemail.Service
	AdminRoutes            *adminroute.Service
	IdentityProfiles       identityProfileService
	IdentityMFAStatus      identityMFAStatusService
	Repository             portalRepository
	Presence               *presence.Registry
	AccountStatusChanged   func(context.Context, uuid.UUID, string) error
	Clock                  func() time.Time
}

type identityProfileService interface {
	UpdateNickname(context.Context, string, string) error
	UpdateVerifiedEmail(context.Context, string, string) error
}

type identityMFAStatusService interface {
	HasConfiguredOTP(context.Context, string) (bool, error)
}

type accountIdentity struct {
	Subject          string
	Username         string
	Nickname         string
	Name             string
	Email            string
	Roles            []string
	BootstrapManaged bool
	AuthenticatedAt  time.Time
	MFA              bool
	Password         bool
	ACR              string
}

type oidcPortalClaims struct {
	Subject           string   `json:"sub"`
	Nonce             string   `json:"nonce"`
	PreferredUsername string   `json:"preferred_username"`
	Nickname          string   `json:"nickname"`
	Name              string   `json:"name"`
	Email             string   `json:"email"`
	AuthenticatedAt   int64    `json:"auth_time"`
	AMR               []string `json:"amr"`
	ACR               string   `json:"acr"`
	RealmAccess       struct {
		Roles []string `json:"roles"`
	} `json:"realm_access"`
	ResourceAccess map[string]struct {
		Roles []string `json:"roles"`
	} `json:"resource_access"`
}

type portalRepository interface {
	SyncPortalIdentity(context.Context, [32]byte, store.PortalIdentityAssertion, string) (store.PortalAccount, error)
	SyncAccountIdentityProfile(context.Context, uuid.UUID, store.AccountIdentityProfile) error
	RecordPortalLoginFailure(context.Context, string, string) error
	GetPortalAccount(context.Context, [32]byte) (store.PortalAccount, error)
	PortalAdminDashboard(context.Context, uuid.UUID) (store.PortalAdminDashboard, error)
	UpdatePortalAccountRole(context.Context, uuid.UUID, uuid.UUID, store.Role, string) error
	UpdatePortalAccountStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
}

type registrationPortalRepository interface {
	GetRegistrationPolicy(context.Context) (store.RegistrationPolicy, error)
	ConfirmPendingInvitationForEmail(context.Context, uuid.UUID, [32]byte) error
}

type sectionPortalRepository interface {
	PortalAdminSection(context.Context, uuid.UUID, string) (store.PortalAdminDashboard, error)
}

// transportRoutingRepository is implemented by stores that own the runtime
// direct-transport data-plane policy toggled from the management portal.
type transportRoutingRepository interface {
	GetDirectRoutingPolicy(context.Context) (bool, error)
	SetDirectRoutingPolicy(context.Context, bool, uuid.UUID) error
}

// RuntimeSettingStore is the portal-owned persistence for deployment
// parameters that SUPER_ADMIN can override at runtime (see migrations for
// runtime_settings). Absent rows mean the environment default applies.
type RuntimeSettingStore interface {
	GetRuntimeSetting(context.Context, string) (string, bool, error)
	SetRuntimeSetting(context.Context, string, string, uuid.UUID) error
}

// runtimeSettingView is one row of the admin deployment-parameter panel.
type runtimeSettingView struct {
	Key    string
	Label  string
	Value  string
	Source string
	IsBool bool
}

type oidcFlow interface {
	AuthorizationURL(state, nonce, challenge, theme string) string
	StepUpAuthorizationURL(state, nonce, challenge, theme, method string) string
	ExchangeAndVerify(context.Context, string, string, string) (accountIdentity, error)
	ExchangeStepUpAndVerify(context.Context, string, string, string, string) (accountIdentity, error)
	LogoutURL() string
	AccountURL() string
}

type dynamicOIDCFlow interface {
	AuthorizationURLForRedirect(state, nonce, challenge, theme, redirectURI string) string
	StepUpAuthorizationURLForRedirect(state, nonce, challenge, theme, method, redirectURI string) string
	ExchangeAndVerifyForRedirect(context.Context, string, string, string, string) (accountIdentity, error)
	ExchangeStepUpAndVerifyForRedirect(context.Context, string, string, string, string, string) (accountIdentity, error)
	LogoutURLForOrigin(string) string
}

type oauthState struct {
	State       string `json:"state"`
	Nonce       string `json:"nonce"`
	Verifier    string `json:"verifier"`
	ReturnTo    string `json:"return_to"`
	Theme       string `json:"theme,omitempty"`
	Purpose     string `json:"purpose,omitempty"`
	Method      string `json:"method,omitempty"`
	RedirectURI string `json:"redirect_uri,omitempty"`
	CookieName  string `json:"cookie_name,omitempty"`
	IssuedAtMS  int64  `json:"issued_at_ms"`
}

type webSession struct {
	Subject               string `json:"subject"`
	Username              string `json:"username"`
	Nickname              string `json:"nickname,omitempty"`
	Name                  string `json:"name"`
	Email                 string `json:"email"`
	Role                  string `json:"role"`
	CSRF                  string `json:"csrf"`
	AuthenticatedAtMS     int64  `json:"authenticated_at_ms"`
	MFA                   bool   `json:"mfa"`
	SensitiveVerifiedAtMS int64  `json:"sensitive_verified_at_ms,omitempty"`
	SensitiveMethod       string `json:"sensitive_method,omitempty"`
	ExpiresAtMS           int64  `json:"expires_at_ms"`
}

type cookieCodec struct {
	aead    cipher.AEAD
	purpose []byte
}

// Portal is the production web handler.
type Portal struct {
	config             Config
	publicOrigin       *url.URL
	identityURL        string
	flow               oidcFlow
	oauthCodec         cookieCodec
	sessionCodec       cookieCodec
	templates          *template.Template
	styles             []byte
	identityStyles     []byte
	adminStyles        []byte
	portalStyles       []byte
	publicStyles       []byte
	themeInit          []byte
	themeScript        []byte
	registerScript     []byte
	registrationScript []byte
	appIcon            []byte
	favicon            []byte
	releaseSource      PublicReleaseSource
	releaseCache       *publicReleaseCache
	systemVersions     *SystemVersionsService
}

type productionOIDCFlow struct {
	oauthConfig        oauth2.Config
	verifier           *oidc.IDTokenVerifier
	stepUpOAuthConfig  oauth2.Config
	stepUpVerifier     *oidc.IDTokenVerifier
	issuer             string
	clientID           string
	publicURL          string
	accountURL         string
	endSessionEndpoint string
	httpClient         *http.Client
}

type homePageData struct {
	LoginURL string
	SignedIn bool
}

type registerPageData struct {
	Mode                      string
	RegistrationOpen          bool
	InvitationRequired        bool
	EmailVerificationRequired bool
	HumanVerificationRequired bool
	HumanVerificationSiteKey  string
	ContinueURL               string
	LoginURL                  string
}

type accountPageData struct {
	AccountID          uuid.UUID
	Username           string
	Nickname           string
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
	Username               string
	Nickname               string
	Role                   store.Role
	RoleLabel              string
	CSRF                   string
	AdminBasePath          string
	Section                string
	SectionTitle           string
	CanManageAccounts      bool
	Dashboard              store.PortalAdminDashboard
	IdentityAccountURL     string
	PublicURL              string
	Issuer                 string
	Notice                 string
	ActionNonce            string
	SystemVersions         SystemVersions
	ServiceEmail           serviceemail.AdminView
	HumanVerification      humanverification.AdminView
	AdminRoute             adminroute.View
	CanConfigureSystem     bool
	RegistrationNavigation bool

	TransportRoutingPermitted  bool
	TransportRoutingConfigured bool

	RuntimeSettings           []runtimeSettingView
	RuntimeSettingsConfigured bool
}

type errorPageData struct {
	Title          string
	Message        string
	EnglishTitle   string
	EnglishMessage string
	Reference      string
}

// New discovers the configured OIDC issuer and constructs the public portal.
func New(ctx context.Context, config Config) (*Portal, error) {
	if ctx == nil {
		return nil, errors.New("web portal context is required")
	}
	if strings.TrimSpace(config.StepUpClientID) == "" {
		config.StepUpClientID = strings.TrimSpace(config.ClientID) + "-step-up"
	}
	config.HumanVerificationProvider = strings.ToLower(strings.TrimSpace(config.HumanVerificationProvider))
	config.HumanVerificationSiteKey = strings.TrimSpace(config.HumanVerificationSiteKey)
	publicOrigin, err := validateConfig(config)
	if err != nil {
		return nil, err
	}
	var discovery auth.OIDCDiscovery
	if config.OIDCDiscovery != nil {
		discovery = *config.OIDCDiscovery
		if strings.TrimRight(discovery.Issuer, "/") != strings.TrimRight(config.Issuer, "/") ||
			discovery.AuthorizationEndpoint == "" || discovery.TokenEndpoint == "" || discovery.JWKSURL == "" {
			return nil, errors.New("injected web OIDC discovery is invalid")
		}
	} else {
		discovery, err = auth.DiscoverOIDC(ctx, config.Issuer, config.BackchannelIssuer)
		if err != nil {
			return nil, fmt.Errorf("discover web OIDC issuer: %w", err)
		}
	}
	accountURL := strings.TrimRight(config.Issuer, "/") + "/account/"
	accountQuery := url.Values{}
	accountQuery.Set("referrer", config.ClientID)
	accountQuery.Set("referrer_uri", strings.TrimRight(config.PublicURL, "/")+"/account")
	accountURL += "?" + accountQuery.Encode()
	flow := &productionOIDCFlow{
		oauthConfig: oauth2.Config{
			ClientID:    config.ClientID,
			Endpoint:    discovery.Endpoint(),
			RedirectURL: config.RedirectURI,
			Scopes:      []string{oidc.ScopeOpenID, "profile", "email"},
		},
		verifier: discovery.Verifier(config.ClientID),
		stepUpOAuthConfig: oauth2.Config{
			ClientID: config.StepUpClientID, Endpoint: discovery.Endpoint(), RedirectURL: config.RedirectURI,
			Scopes: []string{oidc.ScopeOpenID, "profile", "email"},
		},
		stepUpVerifier:     discovery.Verifier(config.StepUpClientID),
		issuer:             strings.TrimRight(config.Issuer, "/"),
		clientID:           config.ClientID,
		publicURL:          strings.TrimRight(config.PublicURL, "/"),
		accountURL:         accountURL,
		endSessionEndpoint: discovery.EndSessionEndpoint,
		httpClient:         config.OIDCHTTPClient,
	}
	if config.ReleaseSource == nil {
		releaseSource, sourceErr := NewAppShipPublicReleaseSource("", nil)
		if sourceErr != nil {
			return nil, fmt.Errorf("configure public AppShip release source: %w", sourceErr)
		}
		config.ReleaseSource = releaseSource
	}
	return newPortal(config, publicOrigin, flow)
}

func newPortal(config Config, publicOrigin *url.URL, flow oidcFlow) (*Portal, error) {
	identityURL, err := validateIdentityAccountURL(config, flow.AccountURL())
	if err != nil {
		return nil, err
	}
	parsed, err := template.New("portal").Funcs(template.FuncMap{
		"formatTime":                   formatPortalTime,
		"formatOptionalTime":           formatOptionalPortalTime,
		"shortID":                      shortID,
		"roleLabel":                    roleLabel,
		"statusLabel":                  statusLabel,
		"formatOptionalPortalTime":     formatOptionalPortalTime,
		"userPortalControlModeLabel":   userPortalControlModeLabel,
		"userPortalDeletionCanConfirm": userPortalDeletionCanConfirm,
		"userPortalDeletionCanCancel":  userPortalDeletionCanCancel,
		"actionKey":                    sensitiveActionKey,
	}).ParseFS(portalAssets, "assets/*.html")
	if err != nil {
		return nil, fmt.Errorf("parse web portal templates: %w", err)
	}
	styles, err := portalAssets.ReadFile("assets/site.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal styles: %w", err)
	}
	identityStyles, err := portalAssets.ReadFile("assets/identity.css")
	if err != nil {
		return nil, fmt.Errorf("read shared identity styles: %w", err)
	}
	adminStyles, err := portalAssets.ReadFile("assets/admin.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal admin styles: %w", err)
	}
	portalStyles, err := portalAssets.ReadFile("assets/portal.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal portal styles: %w", err)
	}
	publicStyles, err := portalAssets.ReadFile("assets/public-pages.css")
	if err != nil {
		return nil, fmt.Errorf("read web portal public page styles: %w", err)
	}
	themeInit, err := portalAssets.ReadFile("assets/theme-init.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal pre-paint theme script: %w", err)
	}
	themeScript, err := portalAssets.ReadFile("assets/theme.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal theme script: %w", err)
	}
	registerScript, err := portalAssets.ReadFile("assets/register.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal registration script: %w", err)
	}
	registrationScript, err := portalAssets.ReadFile("assets/registration-admin.js")
	if err != nil {
		return nil, fmt.Errorf("read web portal registration admin script: %w", err)
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
		config:             config,
		publicOrigin:       publicOrigin,
		identityURL:        identityURL,
		flow:               flow,
		oauthCodec:         oauthCodec,
		sessionCodec:       sessionCodec,
		templates:          parsed,
		styles:             styles,
		identityStyles:     identityStyles,
		adminStyles:        adminStyles,
		portalStyles:       portalStyles,
		publicStyles:       publicStyles,
		themeInit:          themeInit,
		themeScript:        themeScript,
		registerScript:     registerScript,
		registrationScript: registrationScript,
		appIcon:            appIcon,
		favicon:            favicon,
		releaseSource:      config.ReleaseSource,
		releaseCache:       newPublicReleaseCache(publicReleaseCacheTTL, config.Clock),
		systemVersions: NewSystemVersionsService(SystemVersionsOptions{
			Server: config.BuildInfo, ReleaseSource: config.ReleaseSource, Clock: config.Clock,
		}),
	}, nil
}

func validateConfig(config Config) (*url.URL, error) {
	publicOrigin, err := url.Parse(strings.TrimSpace(config.PublicURL))
	schemeOK := publicOrigin != nil && publicOrigin.Scheme == "https"
	// Local-development relaxation mirrors the config-level switch: an
	// explicit operator opt-in permits a plain-HTTP loopback/private origin.
	if !schemeOK && publicOrigin != nil && publicOrigin.Scheme == "http" &&
		localnet.AllowHTTP() && localnet.IsLoopbackOrPrivateHost(publicOrigin.Hostname()) {
		schemeOK = true
	}
	if err != nil || !schemeOK || publicOrigin == nil || publicOrigin.Host == "" || publicOrigin.User != nil ||
		(publicOrigin.Path != "" && publicOrigin.Path != "/") || publicOrigin.RawPath != "" ||
		publicOrigin.RawQuery != "" || publicOrigin.Fragment != "" {
		return nil, errors.New("web portal public URL must be an HTTPS origin")
	}
	issuer, err := url.Parse(strings.TrimSpace(config.Issuer))
	issuerSchemeOK := issuer != nil && issuer.Scheme == "https"
	if !issuerSchemeOK && issuer != nil && issuer.Scheme == "http" &&
		localnet.AllowHTTP() && localnet.IsLoopbackOrPrivateHost(issuer.Hostname()) {
		issuerSchemeOK = true
	}
	if err != nil || !issuerSchemeOK || issuer == nil || issuer.Host == "" || issuer.User != nil {
		return nil, errors.New("web portal issuer must use HTTPS")
	}
	redirect, err := url.Parse(strings.TrimSpace(config.RedirectURI))
	redirectSchemeOK := redirect != nil && redirect.Scheme == "https"
	if !redirectSchemeOK && redirect != nil && redirect.Scheme == "http" &&
		localnet.AllowHTTP() && localnet.IsLoopbackOrPrivateHost(redirect.Hostname()) {
		redirectSchemeOK = true
	}
	if err != nil || !redirectSchemeOK || redirect.Host == "" ||
		redirect.Path != "/auth/callback" || redirect.RawQuery != "" || redirect.Fragment != "" || redirect.User != nil {
		return nil, errors.New("web portal redirect URI must use the public HTTPS callback")
	}
	if strings.TrimSpace(config.ClientID) == "" {
		return nil, errors.New("web portal OIDC client ID is required")
	}
	switch strings.ToLower(strings.TrimSpace(config.HumanVerificationProvider)) {
	case "":
		if strings.TrimSpace(config.HumanVerificationSiteKey) != "" {
			return nil, errors.New("web portal human verification provider is required when a site key is configured")
		}
	case humanverification.ProviderTurnstile:
		siteKey := strings.TrimSpace(config.HumanVerificationSiteKey)
		if siteKey == "" || len(siteKey) > 256 || strings.ContainsAny(siteKey, " \t\r\n") {
			return nil, errors.New("web portal Turnstile site key is invalid")
		}
	default:
		return nil, errors.New("web portal human verification provider is unsupported")
	}
	if strings.TrimSpace(config.StepUpClientID) == "" {
		config.StepUpClientID = strings.TrimSpace(config.ClientID) + "-step-up"
	}
	if config.ClientID == config.StepUpClientID {
		return nil, errors.New("web portal MFA step-up OIDC client ID must be distinct")
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
	mux.HandleFunc("GET /assets/identity.css", p.identityCSS)
	mux.HandleFunc("GET /assets/admin.css", p.adminCSS)
	mux.HandleFunc("GET /assets/portal-dashboard.css", p.portalDashboardCSS)
	mux.HandleFunc("GET /assets/public-pages.css", p.publicCSS)
	mux.HandleFunc("GET /assets/theme-init.js", p.themeInitJS)
	mux.HandleFunc("GET /assets/theme.js", p.themeJS)
	mux.HandleFunc("GET /assets/register.js", p.registerJS)
	mux.HandleFunc("GET /assets/registration-admin.js", p.registrationAdminJS)
	mux.HandleFunc("GET /assets/termlink-app-icon.svg", p.svgAsset(p.appIcon))
	mux.HandleFunc("GET /assets/termlink-favicon.svg", p.svgAsset(p.favicon))
	mux.HandleFunc("GET /login", p.login)
	mux.HandleFunc("GET /register", p.registerPage)
	mux.HandleFunc("GET /downloads", p.publicDownloads)
	mux.HandleFunc("GET /releases", p.publicReleases)
	mux.HandleFunc("GET /security", p.publicSecurity)
	mux.HandleFunc("GET /status", p.publicStatus)
	mux.HandleFunc("GET /auth/callback", p.callback)
	mux.HandleFunc("GET /auth/step-up", p.sensitiveStepUp)
	mux.HandleFunc("GET /auth/step-up/status", p.sensitiveStepUpStatus)
	mux.HandleFunc("GET /auth/step-up/result", p.sensitiveStepUpResult)
	mux.HandleFunc("GET /account", p.account)
	mux.HandleFunc("POST /account/devices/{id}/revoke", p.strongAccountAction("user.device.revoke", p.userPortalRevokeDevice))
	mux.HandleFunc("POST /account/devices/{id}/disable", p.strongAccountAction("user.device.disable", p.userPortalDisableDevice))
	mux.HandleFunc("POST /account/devices/{id}/delete", p.strongAccountAction("user.device.delete", p.userPortalDeleteDevice))
	mux.HandleFunc("POST /account/pcs/{id}/control-policy", p.strongAccountAction("user.pc.control_policy.update", p.userPortalUpdatePCControlPolicy))
	mux.HandleFunc("POST /account/pcs/control-policy/batch", p.strongAccountAction("user.pc.control_policy.batch_update", p.userPortalBatchUpdatePCControlPolicy))
	mux.HandleFunc("POST /account/privacy/deletions", p.strongAccountAction("user.account_deletion.request", p.userPortalRequestAccountDeletion))
	mux.HandleFunc("POST /account/privacy/deletions/{id}/confirm", p.strongAccountAction("user.account_deletion.confirm", p.userPortalConfirmAccountDeletion))
	mux.HandleFunc("POST /account/privacy/deletions/{id}/cancel", p.strongAccountAction("user.account_deletion.cancel", p.userPortalCancelAccountDeletion))
	mux.HandleFunc("POST /account/profile/nickname", p.accountUpdateNickname)
	mux.HandleFunc("POST /account/profile/email-code", p.accountRequestEmailCode)
	mux.HandleFunc("POST /account/profile/email", p.accountConfirmEmail)
	mux.HandleFunc("GET /account/identity", p.identity)
	mux.HandleFunc("GET /account/identity/{action}", p.identity)
	mux.HandleFunc("GET /admin", p.admin)
	mux.HandleFunc("GET /admin/registration", p.adminRegistrationDashboard)
	mux.HandleFunc("POST /admin/transport/routing", p.sensitiveAction("transport.routing.update", p.adminUpdateTransportRouting))
	mux.HandleFunc("POST /admin/runtime-settings", p.sensitiveAction("runtime.setting.update", p.adminUpdateRuntimeSetting))
	mux.HandleFunc("POST /admin/registration/policy", p.sensitiveAction("registration.policy.update", p.adminUpdateRegistrationPolicy))
	mux.HandleFunc("POST /admin/registration/invitations/batches", p.sensitiveAction("registration.invitation_batch.create", p.adminCreateInvitationBatch))
	mux.HandleFunc("POST /admin/registration/invitations/batches/{id}/codes", p.sensitiveAction("registration.invitation_codes.generate", p.adminGenerateInvitationCodes))
	mux.HandleFunc("POST /admin/registration/invitations/batches/{id}/status", p.sensitiveAction("registration.invitation_batch.status", p.adminSetInvitationBatchStatus))
	mux.HandleFunc("POST /admin/registration/invitations/codes/{id}/status", p.sensitiveAction("registration.invitation_code.status", p.adminSetInvitationCodeStatus))
	mux.HandleFunc("POST /admin/accounts/{id}/role", p.sensitiveAction("admin.account.role.update", p.adminUpdateRole))
	mux.HandleFunc("POST /admin/accounts/{id}/status", p.sensitiveAction("admin.account.status.update", p.adminUpdateStatus))
	mux.HandleFunc("POST /admin/settings/service-email", p.sensitiveAction("admin.service_email.update", p.adminUpdateServiceEmail))
	mux.HandleFunc("POST /admin/settings/human-verification", p.sensitiveAction("admin.human_verification.update", p.adminUpdateHumanVerification))
	mux.HandleFunc("POST /admin/settings/admin-route", p.sensitiveAction("admin.route.update", p.adminUpdateRoute))
	mux.HandleFunc("POST /logout", p.logout)
	mux.HandleFunc("/", p.notFound)
	return p.securityHeaders(p.routeAdminRequests(mux))
}

func (p *Portal) home(w http.ResponseWriter, r *http.Request) {
	data := homePageData{LoginURL: "/login"}
	if session, err := p.readSession(r); err == nil {
		if _, accountErr := p.currentAccount(r.Context(), session); accountErr == nil {
			data.SignedIn = true
		}
	}
	p.render(w, http.StatusOK, "home.html", data)
}

func (p *Portal) siteCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.styles)
}

func (p *Portal) identityCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.identityStyles)
}

func (p *Portal) adminCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.adminStyles)
}

func (p *Portal) portalDashboardCSS(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "text/css; charset=utf-8")
	w.Header().Set("Cache-Control", "public, max-age=3600")
	_, _ = w.Write(p.portalStyles)
}

func (p *Portal) publicCSS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/css; charset=utf-8", p.publicStyles)
}

func (p *Portal) themeJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.themeScript)
}

func (p *Portal) themeInitJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.themeInit)
}

func (p *Portal) registerJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.registerScript)
}

func (p *Portal) registrationAdminJS(w http.ResponseWriter, _ *http.Request) {
	p.staticAsset(w, "text/javascript; charset=utf-8", p.registrationScript)
}

func (p *Portal) registerPage(w http.ResponseWriter, r *http.Request) {
	continueURL := normalizeRegistrationContinueURL(p.config, r.URL.Query().Get("continue"), r)
	repository, ok := p.config.Repository.(registrationPortalRepository)
	if !ok {
		p.renderError(w, http.StatusServiceUnavailable, "注册暂不可用", "服务端尚未启用注册策略。")
		return
	}
	policy, err := repository.GetRegistrationPolicy(r.Context())
	if err != nil {
		p.internalError(w, err)
		return
	}
	emailVerificationRequired := false
	if p.config.ServiceEmail != nil {
		emailVerificationRequired, err = p.config.ServiceEmail.RegistrationVerificationPolicy(r.Context())
		if err != nil {
			p.renderError(w, http.StatusServiceUnavailable, "注册邮箱验证暂不可用", "服务端暂时无法读取邮箱验证策略，请稍后重试。")
			return
		}
		if emailVerificationRequired && !p.config.ServiceEmail.Runtime(r.Context()).ServiceAvailable {
			p.renderError(w, http.StatusServiceUnavailable, "注册邮箱验证暂不可用", "当前注册必须验证邮箱，但邮件服务暂时不可用；系统不会绕过验证创建账号。")
			return
		}
	}
	humanRuntime, err := p.humanVerificationRuntime(r.Context())
	if err != nil {
		p.renderError(w, http.StatusServiceUnavailable, "人机验证暂不可用", "服务端暂时无法读取人机验证设置，请稍后重试。")
		return
	}
	loginURL := "/login"
	if continueURL != "" {
		loginURL = continueURL
	}
	p.render(w, http.StatusOK, "register.html", registerPageData{
		Mode: policy.Mode, RegistrationOpen: policy.Mode != store.RegistrationClosed,
		InvitationRequired:        policy.Mode == store.RegistrationInviteRequired,
		EmailVerificationRequired: emailVerificationRequired,
		HumanVerificationRequired: humanRuntime.Enabled,
		HumanVerificationSiteKey:  humanRuntime.SiteKey,
		ContinueURL:               continueURL,
		LoginURL:                  loginURL,
	})
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
	p.beginOIDC(w, r, normalizePortalReturnTo(r.URL.Query().Get("return_to")), normalizeTheme(r.URL.Query().Get(themeQueryParameter)), "", "")
}

func (p *Portal) beginOIDC(w http.ResponseWriter, r *http.Request, returnTo, theme, purpose, method string) {
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
	redirectURI := p.config.RedirectURI
	if _, dynamic := p.flow.(dynamicOIDCFlow); dynamic {
		if origin, ok := requestOrigin(r); ok {
			redirectURI = origin + "/auth/callback"
		}
	}
	pending := oauthState{
		State:       state,
		Nonce:       nonce,
		Verifier:    verifier,
		ReturnTo:    returnTo,
		Theme:       theme,
		Purpose:     purpose,
		Method:      method,
		RedirectURI: redirectURI,
		IssuedAtMS:  p.now().UnixMilli(),
	}
	pending.CookieName = oauthCookieForState(state)
	stateCookie, err := p.oauthCodec.seal(pending)
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(r, w, pending.CookieName, stateCookie, p.now().Add(oauthStateTTL))
	// Keep the legacy slot for older embedded pages and clients; callbacks prefer
	// the per-flow cookie above, so parallel windows no longer overwrite state.
	p.setCookie(r, w, oauthCookieName, stateCookie, p.now().Add(oauthStateTTL))
	location := p.flow.AuthorizationURL(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]), theme)
	if dynamic, ok := p.flow.(dynamicOIDCFlow); ok {
		location = dynamic.AuthorizationURLForRedirect(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]), theme, redirectURI)
	}
	if purpose == sensitiveOIDCPurpose {
		location = p.flow.StepUpAuthorizationURL(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]), theme, method)
		if dynamic, ok := p.flow.(dynamicOIDCFlow); ok {
			location = dynamic.StepUpAuthorizationURLForRedirect(state, nonce, base64.RawURLEncoding.EncodeToString(challenge[:]), theme, method, redirectURI)
		}
	}
	clear(challenge[:])
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, location, http.StatusFound)
}

func (p *Portal) callback(w http.ResponseWriter, r *http.Request) {
	loginTraceID := uuid.NewString()
	// Read through the same localnet name relaxation setCookie used at
	// initiation: under the local HTTP relaxation the stored cookie lost its
	// __Host- prefix, so the raw lookup would always miss.
	cookieName := localnet.CookieName(r.Host, oauthCookieForState(r.URL.Query().Get("state")))
	cookie, err := r.Cookie(cookieName)
	if err != nil {
		cookieName = localnet.CookieName(r.Host, oauthCookieName)
		cookie, err = r.Cookie(cookieName)
	}
	p.clearCookie(r, w, cookieName)
	p.clearCookie(r, w, oauthCookieName)
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "登录已过期", "授权状态已失效，请返回首页重新登录。")
		return
	}
	var pending oauthState
	if err := p.oauthCodec.open(cookie.Value, &pending); err != nil ||
		p.now().Sub(time.UnixMilli(pending.IssuedAtMS)) < 0 ||
		p.now().Sub(time.UnixMilli(pending.IssuedAtMS)) > oauthStateTTL ||
		!constantTextEqual(pending.State, r.URL.Query().Get("state")) ||
		(pending.CookieName != "" && cookieName != localnet.CookieName(r.Host, oauthCookieName) &&
			pending.CookieName != cookieName && localnet.CookieName(r.Host, pending.CookieName) != cookieName) {
		p.renderError(w, http.StatusBadRequest, "登录校验失败", "授权响应未通过状态校验，请返回首页重新登录。")
		return
	}
	if current, ok := requestOrigin(r); ok && pending.RedirectURI != "" && !sameCallbackURI(pending.RedirectURI, current+"/auth/callback") {
		p.renderError(w, http.StatusBadRequest, "登录校验失败", "授权回调来源与发起来源不一致，请重新登录。")
		return
	}
	if pending.Purpose != "" && pending.Purpose != sensitiveOIDCPurpose {
		p.renderError(w, http.StatusBadRequest, "登录校验失败", "授权用途无效，请重新发起登录。")
		return
	}
	code := r.URL.Query().Get("code")
	if code == "" || r.URL.Query().Get("error") != "" {
		_ = p.config.Repository.RecordPortalLoginFailure(r.Context(), loginTraceID, "authorization_incomplete")
		if pending.Purpose == sensitiveOIDCPurpose {
			p.renderSensitiveStepUpResult(w, http.StatusBadRequest, "failed", "", "安全验证已取消", "本次高风险操作尚未执行；关闭窗口后可重新验证。")
			return
		}
		p.renderError(w, http.StatusBadRequest, "登录未完成", "身份服务没有返回有效授权，请返回首页重试。")
		return
	}
	var identity accountIdentity
	if pending.Purpose == sensitiveOIDCPurpose {
		if dynamic, ok := p.flow.(dynamicOIDCFlow); ok {
			identity, err = dynamic.ExchangeStepUpAndVerifyForRedirect(r.Context(), code, pending.Verifier, pending.Nonce, pending.Method, pending.RedirectURI)
		} else {
			identity, err = p.flow.ExchangeStepUpAndVerify(r.Context(), code, pending.Verifier, pending.Nonce, pending.Method)
		}
	} else {
		if dynamic, ok := p.flow.(dynamicOIDCFlow); ok {
			identity, err = dynamic.ExchangeAndVerifyForRedirect(r.Context(), code, pending.Verifier, pending.Nonce, pending.RedirectURI)
		} else {
			identity, err = p.flow.ExchangeAndVerify(r.Context(), code, pending.Verifier, pending.Nonce)
		}
	}
	if err != nil {
		_ = p.config.Repository.RecordPortalLoginFailure(r.Context(), loginTraceID, "token_verification_failed")
		logPortalFailure(r.Context(), slog.LevelWarn, "web OIDC callback rejected",
			"oidc_token_verification_failed", loginTraceID, err)
		if pending.Purpose == sensitiveOIDCPurpose {
			p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "安全验证未完成", "身份服务未能完成本次验证，请关闭窗口后重试。")
			return
		}
		p.renderError(w, http.StatusUnauthorized, "登录校验失败", "身份令牌未通过校验，请返回首页重新登录。")
		return
	}
	if pending.Purpose == sensitiveOIDCPurpose {
		p.completeSensitiveStepUp(w, r, pending, identity)
		return
	}
	csrf, err := randomText(32)
	if err != nil {
		p.internalError(w, err)
		return
	}
	username := firstNonEmpty(identity.Username, identity.Email, "TermLink 用户")
	nickname := firstNonEmpty(identity.Nickname, identity.Username, identity.Name, identity.Email, "TermLink 用户")
	account, err := p.config.Repository.SyncPortalIdentity(
		r.Context(),
		auth.SubjectHash(p.config.SubjectKey, auth.Identity{Issuer: p.config.Issuer, Subject: identity.Subject}),
		portalIdentityAssertion(identity),
		loginTraceID,
	)
	if err != nil {
		logPortalFailure(r.Context(), slog.LevelWarn, "web OIDC account authorization rejected",
			"oidc_account_authorization_failed", loginTraceID, err)
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号未启用或授权状态无效，请联系 TermLink 管理员。")
		return
	}
	profile := store.NewAccountIdentityProfile(username, nickname, identity.Email)
	if profileErr := p.config.Repository.SyncAccountIdentityProfile(r.Context(), account.ID, profile); profileErr != nil {
		logPortalFailure(r.Context(), slog.LevelWarn, "synchronize account identity display profile",
			"account_identity_profile_sync_failed", loginTraceID, profileErr,
			slog.String("account_id", account.ID.String()))
	}
	if identity.Email != "" && len(p.config.RegistrationCodeKey) >= 32 {
		if repository, ok := p.config.Repository.(registrationPortalRepository); ok {
			email := strings.ToLower(strings.TrimSpace(identity.Email))
			digest := invitationCodeDigest(p.config.RegistrationCodeKey, "registration-email", email)
			if linkErr := repository.ConfirmPendingInvitationForEmail(r.Context(), account.ID, digest); linkErr != nil && !errors.Is(linkErr, store.ErrNotFound) {
				logPortalFailure(r.Context(), slog.LevelWarn, "link registration invitation after OIDC login",
					"registration_invitation_link_failed", loginTraceID, linkErr,
					slog.String("account_id", account.ID.String()))
			}
		}
	}
	sessionCookie, err := p.sessionCodec.seal(webSession{
		Subject:           identity.Subject,
		Username:          username,
		Nickname:          nickname,
		Name:              identity.Name,
		Email:             identity.Email,
		Role:              string(account.Role),
		CSRF:              csrf,
		AuthenticatedAtMS: authenticatedAtMilliseconds(identity.AuthenticatedAt, p.now()),
		MFA:               identity.MFA,
		ExpiresAtMS:       p.now().Add(webSessionTTL).UnixMilli(),
	})
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.setCookie(r, w, sessionCookieName, sessionCookie, p.now().Add(webSessionTTL))
	p.clearCookie(r, w, legacySessionCookie)
	returnTo := "/"
	switch normalizePortalReturnTo(pending.ReturnTo) {
	case "/account":
		returnTo = "/account"
	case "/admin":
		if account.Role.CanOpenConsole() {
			returnTo, err = p.adminBasePath(r.Context())
			if err != nil {
				p.internalError(w, err)
				return
			}
		}
	case "/account/identity", "/account/identity/password", "/account/identity/mfa", "/account/identity/sessions":
		identityURL := p.identityURL
		if _, dynamic := p.flow.(dynamicOIDCFlow); dynamic && pending.RedirectURI != "" {
			if origin, ok := requestOrigin(r); ok {
				if parsed, parseErr := url.Parse(identityURL); parseErr == nil {
					parsed.Scheme, parsed.Host = "https", strings.TrimPrefix(origin, "https://")
					query := parsed.Query()
					query.Set("referrer_uri", origin+"/account")
					parsed.RawQuery = query.Encode()
					identityURL = parsed.String()
				}
			}
		}
		returnTo = identityAccountSectionURL(identityURL, pending.ReturnTo, pending.Theme)
	}
	http.Redirect(w, r, returnTo, http.StatusSeeOther)
}

func (p *Portal) identity(w http.ResponseWriter, r *http.Request) {
	returnTo := identityReturnPath(r.PathValue("action"))
	if returnTo == "" {
		http.NotFound(w, r)
		return
	}
	target := "/login?return_to=" + url.QueryEscape(returnTo)
	if theme := normalizeTheme(r.URL.Query().Get(themeQueryParameter)); theme != "" {
		target += "&" + themeQueryParameter + "=" + url.QueryEscape(theme)
	}
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, target, http.StatusSeeOther)
}

func (p *Portal) account(w http.ResponseWriter, r *http.Request) {
	if _, portalOK := p.config.Repository.(userPortalRepository); portalOK {
		p.accountDashboard(w, r)
		return
	}
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(r, w, sessionCookieName)
		http.Redirect(w, r, "/login?return_to=/account", http.StatusSeeOther)
		return
	}
	portalAccount, err := p.currentAccount(r.Context(), session)
	if err != nil {
		p.clearCookie(r, w, sessionCookieName)
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号已被停用或浏览器会话需要重新登录。")
		return
	}
	consoleURL, _ := p.adminBasePath(r.Context())
	p.render(w, http.StatusOK, "account.html", accountPageData{
		AccountID:          portalAccount.ID,
		Username:           session.Username,
		Nickname:           firstNonEmpty(session.Nickname, session.Username),
		Name:               session.Name,
		Email:              session.Email,
		IdentityAccountURL: "/account/identity",
		CSRF:               session.CSRF,
		Role:               portalAccount.Role,
		RoleLabel:          roleLabel(portalAccount.Role),
		CanOpenConsole:     portalAccount.Role.CanOpenConsole() && consoleURL != "",
		ConsoleURL:         consoleURL,
	})
}

func (p *Portal) admin(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return
	}
	adminBasePath, err := p.adminBasePath(r.Context())
	if err != nil {
		p.internalError(w, err)
		return
	}
	section := normalizeAdminSection(r.URL.Query().Get("section"))
	if section == "settings" && account.Role != store.RoleSuperAdmin {
		p.renderError(w, http.StatusForbidden, "需要超级管理员", "系统安全设置仅允许 SUPER_ADMIN 查看和修改。")
		return
	}
	var dashboard store.PortalAdminDashboard
	if repository, ok := p.config.Repository.(sectionPortalRepository); ok {
		dashboard, err = repository.PortalAdminSection(r.Context(), account.ID, section)
	} else {
		dashboard, err = p.config.Repository.PortalAdminDashboard(r.Context(), account.ID)
	}
	if err != nil {
		p.internalError(w, err)
		return
	}
	if p.config.Presence != nil {
		for index := range dashboard.Connections {
			seen, online := p.config.Presence.OnlineDevice(dashboard.Connections[index].ID)
			dashboard.Connections[index].Online = online
			if online {
				seen = seen.UTC()
				dashboard.Connections[index].LastSeenAt = &seen
			}
		}
	}
	transportRoutingPermitted := false
	transportRoutingConfigured := false
	if section == "connections" {
		if routingRepository, ok := p.config.Repository.(transportRoutingRepository); ok {
			if permitted, policyErr := routingRepository.GetDirectRoutingPolicy(r.Context()); policyErr == nil {
				transportRoutingPermitted = permitted
				transportRoutingConfigured = true
			}
		}
	}
	var runtimeSettings []runtimeSettingView
	runtimeSettingsConfigured := false
	if section == "deployment" {
		runtimeSettings, runtimeSettingsConfigured = p.runtimeSettingViews(r.Context())
	}
	var systemVersions SystemVersions
	if section == "versions" {
		systemVersions = p.systemVersions.Load(r.Context())
	}
	var serviceEmailSettings serviceemail.AdminView
	if section == "settings" && p.config.ServiceEmail != nil {
		serviceEmailSettings, err = p.config.ServiceEmail.Admin(r.Context())
		if err != nil {
			p.internalError(w, err)
			return
		}
	}
	var humanVerificationSettings humanverification.AdminView
	if section == "settings" && p.config.HumanVerification != nil {
		humanVerificationSettings, err = p.config.HumanVerification.Admin(r.Context())
		if err != nil {
			p.internalError(w, err)
			return
		}
	}
	adminRoute := adminroute.View{BasePath: adminroute.DefaultBasePath, Version: 1}
	if p.config.AdminRoutes != nil {
		adminRoute, err = p.config.AdminRoutes.Current(r.Context())
		if err != nil {
			p.internalError(w, err)
			return
		}
	}
	actionNonce, err := randomText(24)
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.render(w, http.StatusOK, "admin.html", adminPageData{
		Username:           session.Username,
		Nickname:           firstNonEmpty(session.Nickname, session.Username),
		Role:               account.Role,
		RoleLabel:          roleLabel(account.Role),
		CSRF:               session.CSRF,
		AdminBasePath:      adminBasePath,
		Section:            section,
		SectionTitle:       adminSectionTitle(section),
		CanManageAccounts:  account.Role.CanManageAccounts(),
		Dashboard:          dashboard,
		IdentityAccountURL: "/account/identity",
		PublicURL:          strings.TrimRight(p.config.PublicURL, "/"),
		Issuer:             strings.TrimRight(p.config.Issuer, "/"),
		Notice:             adminNotice(r.URL.Query().Get("notice")),
		ActionNonce:        actionNonce,
		SystemVersions:     systemVersions,
		ServiceEmail:       serviceEmailSettings,
		HumanVerification:  humanVerificationSettings,
		AdminRoute:         adminRoute,
		CanConfigureSystem: account.Role == store.RoleSuperAdmin,

		TransportRoutingPermitted:  transportRoutingPermitted,
		TransportRoutingConfigured: transportRoutingConfigured,

		RuntimeSettings:           runtimeSettings,
		RuntimeSettingsConfigured: runtimeSettingsConfigured,
	})
}

// runtimeSettingDefinitions fixes the admin panel order, labels, and kinds.
// Keys mirror store.RuntimeSettingKeys; keep the two lists aligned.
var runtimeSettingDefinitions = []struct {
	Key    string
	Label  string
	IsBool bool
}{
	{store.RuntimeSettingRegistrationRecoveryEnabled, "注册找回（邮箱验证码注册）", true},
	{store.RuntimeSettingAccountDeletionEnabled, "账号注销", true},
	{store.RuntimeSettingDirectSTUNServers, "直连 STUN 服务器（逗号分隔）", false},
}

func (p *Portal) runtimeSettingViews(ctx context.Context) ([]runtimeSettingView, bool) {
	settingRepository, ok := p.config.Repository.(RuntimeSettingStore)
	if !ok {
		return nil, false
	}
	views := make([]runtimeSettingView, 0, len(runtimeSettingDefinitions))
	for _, definition := range runtimeSettingDefinitions {
		view := runtimeSettingView{Key: definition.Key, Label: definition.Label, IsBool: definition.IsBool}
		if override, found, err := settingRepository.GetRuntimeSetting(ctx, definition.Key); err == nil && found {
			view.Value = override
			view.Source = "管理页覆盖"
		} else {
			view.Value = p.config.RuntimeSettingDefaults[definition.Key]
			view.Source = "环境默认"
		}
		if view.IsBool {
			view.Value = map[string]string{"true": "已开启", "false": "已关闭"}[view.Value]
			if view.Value == "" {
				view.Value = "已关闭"
			}
		}
		views = append(views, view)
	}
	return views, true
}

func (p *Portal) adminUpdateRuntimeSetting(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	settingRepository, ok := p.config.Repository.(RuntimeSettingStore)
	if !ok {
		p.renderError(w, http.StatusServiceUnavailable, "运行时参数不可用", "当前部署尚未配置运行时参数存储能力。")
		return
	}
	key := strings.TrimSpace(r.Form.Get("setting"))
	value := strings.TrimSpace(r.Form.Get("value"))
	definition, known := runtimeSettingKey(key)
	if !known {
		p.renderError(w, http.StatusBadRequest, "未知参数", "运行时参数不在可配置范围内。")
		return
	}
	if definition.IsBool && value != "true" && value != "false" {
		p.renderError(w, http.StatusBadRequest, "参数值无效", "开关参数只接受 true 或 false。")
		return
	}
	if !definition.IsBool && value == "" {
		p.renderError(w, http.StatusBadRequest, "参数值无效", "参数值不能为空。")
		return
	}
	if err := settingRepository.SetRuntimeSetting(r.Context(), key, value, account.ID); err != nil {
		p.handleAdminMutationError(w, err)
		return
	}
	if applyErr := p.applyRuntimeSetting(key, value); applyErr != nil {
		p.renderError(w, http.StatusInternalServerError, "参数已保存但生效失败", applyErr.Error()+" 请修正参数值后重新保存。")
		return
	}
	adminBasePath, basePathErr := p.adminBasePath(r.Context())
	if basePathErr != nil {
		p.internalError(w, basePathErr)
		return
	}
	http.Redirect(w, r, adminBasePath+"?section=deployment&notice=runtime-setting-updated", http.StatusSeeOther)
	_ = session
}

func runtimeSettingKey(key string) (struct {
	Key    string
	Label  string
	IsBool bool
}, bool) {
	for _, definition := range runtimeSettingDefinitions {
		if definition.Key == key {
			return definition, true
		}
	}
	return struct {
		Key    string
		Label  string
		IsBool bool
	}{}, false
}

func (p *Portal) applyRuntimeSetting(key, value string) error {
	switch key {
	case store.RuntimeSettingDirectSTUNServers:
		if p.config.ApplyDirectSTUNServers == nil {
			return nil
		}
		return p.config.ApplyDirectSTUNServers(value)
	}
	return nil
}

func (p *Portal) adminUpdateServiceEmail(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	if p.config.ServiceEmail == nil || r.ParseForm() != nil || !p.usesTrustedFormOrigin(r) ||
		!constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusServiceUnavailable, "服务邮箱不可用", "当前部署尚未配置安全的服务邮箱管理能力。")
		return
	}
	port, portErr := strconv.Atoi(r.Form.Get("smtp_port"))
	version, versionErr := strconv.ParseInt(r.Form.Get("expected_version"), 10, 64)
	if portErr != nil || versionErr != nil {
		p.renderError(w, http.StatusBadRequest, "邮件配置无效", "SMTP 端口或配置版本无效。")
		return
	}
	_, err := p.config.ServiceEmail.UpdateAndTest(r.Context(), account.ID, serviceemail.UpdateInput{
		ServiceEnabled:                  r.Form.Get("service_enabled") == "on",
		RegistrationVerificationEnabled: r.Form.Get("registration_verification_enabled") == "on",
		PasswordResetEnabled:            r.Form.Get("password_reset_enabled") == "on",
		SMTPHost:                        r.Form.Get("smtp_host"), SMTPPort: port, SMTPSecurity: r.Form.Get("smtp_security"),
		FromName: r.Form.Get("from_name"), FromAddress: r.Form.Get("from_address"), ReplyTo: r.Form.Get("reply_to"),
		SMTPUsername: r.Form.Get("smtp_username"), SMTPPassword: r.Form.Get("smtp_password"),
		TestRecipient: r.Form.Get("test_recipient"), ExpectedVersion: version,
	})
	if err != nil {
		switch {
		case errors.Is(err, serviceemail.ErrInvalidConfiguration):
			p.renderError(w, http.StatusBadRequest, "邮件配置无效", "请检查 SMTP 地址、端口、TLS、发件地址和测试收件地址。")
		case errors.Is(err, serviceemail.ErrDeliveryFailed):
			p.renderError(w, http.StatusBadGateway, "测试邮件发送失败", "邮件服务器拒绝连接、认证或投递；原有配置保持不变。")
		case errors.Is(err, serviceemail.ErrIdentityUnavailable):
			p.renderError(w, http.StatusServiceUnavailable, "身份服务同步失败", "Keycloak 邮件配置未同步，原有配置保持不变。")
		case errors.Is(err, store.ErrConflict):
			p.renderError(w, http.StatusConflict, "配置已经变化", "请刷新系统设置后重新提交。")
		default:
			p.internalError(w, err)
		}
		return
	}
	http.Redirect(w, r, "/admin?section=settings&notice=service-email-updated", http.StatusSeeOther)
}

func (p *Portal) adminUpdateHumanVerification(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	if p.config.HumanVerification == nil || r.ParseForm() != nil || !p.usesTrustedFormOrigin(r) ||
		!constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusServiceUnavailable, "人机验证设置不可用", "当前服务端无法安全地管理人机验证设置。")
		return
	}
	version, err := strconv.ParseInt(r.Form.Get("expected_version"), 10, 64)
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "人机验证设置无效", "配置版本无效，请刷新页面后重试。")
		return
	}
	_, err = p.config.HumanVerification.Update(r.Context(), account.ID, humanverification.UpdateInput{
		Enabled: r.Form.Get("enabled") == "on", SiteKey: r.Form.Get("site_key"), Secret: r.Form.Get("secret"),
		ClearSecret: r.Form.Get("clear_secret") == "on", ExpectedVersion: version,
	})
	if err != nil {
		switch {
		case errors.Is(err, humanverification.ErrInvalidSettings), errors.Is(err, store.ErrInvalidHumanVerificationSettings):
			p.renderError(w, http.StatusBadRequest, "人机验证设置无效", "请检查启用状态、Site Key 和 Secret；启用时两项凭据必须完整。")
		case errors.Is(err, store.ErrConflict):
			p.renderError(w, http.StatusConflict, "配置已经变化", "其他管理员刚刚修改了人机验证设置，请刷新后重试。")
		case errors.Is(err, store.ErrDenied):
			p.renderError(w, http.StatusForbidden, "人机验证设置被拒绝", "只有活动的 SUPER_ADMIN 可以修改此设置。")
		default:
			p.internalError(w, err)
		}
		return
	}
	http.Redirect(w, r, "/admin?section=settings&notice=human-verification-updated", http.StatusSeeOther)
}

func (p *Portal) adminUpdateTransportRouting(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	routingRepository, ok := p.config.Repository.(transportRoutingRepository)
	if !ok {
		p.renderError(w, http.StatusServiceUnavailable, "直连路由策略不可用", "当前部署尚未配置直连路由策略存储能力。")
		return
	}
	permitted := r.Form.Get("routing_permitted") == "true"
	if err := routingRepository.SetDirectRoutingPolicy(r.Context(), permitted, account.ID); err != nil {
		p.handleAdminMutationError(w, err)
		return
	}
	adminBasePath, basePathErr := p.adminBasePath(r.Context())
	if basePathErr != nil {
		p.internalError(w, basePathErr)
		return
	}
	http.Redirect(w, r, adminBasePath+"?section=connections&notice=transport-routing-updated", http.StatusSeeOther)
	_ = session
}

func (p *Portal) adminUpdateRoute(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSuperAdminAction(w, r)
	if !ok {
		return
	}
	if p.config.AdminRoutes == nil {
		p.renderError(w, http.StatusServiceUnavailable, "安全入口不可用", "当前部署尚未配置管理中心安全入口能力。")
		return
	}
	version, err := strconv.ParseInt(r.Form.Get("expected_version"), 10, 64)
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "安全入口无效", "配置版本无效，请刷新页面后重试。")
		return
	}
	updated, err := p.config.AdminRoutes.Update(r.Context(), account.ID, r.Form.Get("route_segment"), r.Form.Get("route_confirmation"), version)
	if err != nil {
		switch {
		case errors.Is(err, adminroute.ErrInvalidRoute):
			p.renderError(w, http.StatusBadRequest, "安全入口无效", "入口必须为 16–64 位字母、数字、短横线或下划线，并且两次输入一致。")
		case errors.Is(err, store.ErrConflict):
			p.renderError(w, http.StatusConflict, "设置已经变化", "其他管理员刚刚修改了系统设置，请刷新后重试。")
		case errors.Is(err, store.ErrDenied):
			p.renderError(w, http.StatusForbidden, "安全入口修改被拒绝", "当前账号状态不允许修改安全入口。")
		default:
			p.internalError(w, err)
		}
		return
	}
	http.Redirect(w, r, updated.BasePath+"?section=settings&notice=admin-route-updated", http.StatusSeeOther)
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
			logPortalFailure(r.Context(), slog.LevelWarn, "publish account status change",
				"account_status_publish_failed", traceID, err,
				slog.String("account_id", targetID.String()))
		}
	}
	http.Redirect(w, r, "/admin?section=accounts&notice=status-updated", http.StatusSeeOther)
}

func (p *Portal) logout(w http.ResponseWriter, r *http.Request) {
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(r, w, sessionCookieName)
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
	p.clearCookie(r, w, sessionCookieName)
	p.clearCookie(r, w, legacySessionCookie)
	target := p.flow.LogoutURL()
	if dynamic, ok := p.flow.(dynamicOIDCFlow); ok {
		if origin, originOK := requestOrigin(r); originOK {
			target = dynamic.LogoutURLForOrigin(origin)
		}
	}
	http.Redirect(w, r, target, http.StatusSeeOther)
}

func (p *Portal) notFound(w http.ResponseWriter, _ *http.Request) {
	p.renderError(w, http.StatusNotFound, "页面不存在", "该地址不属于 TermLink 服务端门户。")
}

func (p *Portal) readSession(r *http.Request) (webSession, error) {
	cookie, err := r.Cookie(localnet.CookieName(r.Host, sessionCookieName))
	if err != nil {
		return webSession{}, err
	}
	var session webSession
	if err := p.sessionCodec.open(cookie.Value, &session); err != nil {
		return webSession{}, err
	}
	role, roleErr := store.ParseRole(session.Role)
	if strings.TrimSpace(session.Subject) == "" || strings.TrimSpace(session.CSRF) == "" || roleErr != nil ||
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
	englishTitle, englishMessage := publicEnglishError(status)
	p.render(w, status, "error.html", errorPageData{
		Title: title, Message: message, EnglishTitle: englishTitle, EnglishMessage: englishMessage,
	})
}

func publicEnglishError(status int) (string, string) {
	switch status {
	case http.StatusBadRequest, http.StatusUnprocessableEntity:
		return "Invalid request", "Check the submitted information, refresh the page, and try again."
	case http.StatusUnauthorized:
		return "Sign-in required", "Return to the home page and sign in again before retrying this request."
	case http.StatusForbidden:
		return "Request rejected", "The account, permission, or security check does not allow this request. Refresh the page or sign in again."
	case http.StatusNotFound:
		return "Not found", "The requested page or record does not exist or is not available to this account."
	case http.StatusConflict:
		return "State changed", "The record changed before this request completed. Refresh the page and try again."
	case http.StatusGone:
		return "Request expired", "This request is no longer valid. Refresh the page and start the operation again."
	case http.StatusTooManyRequests:
		return "Too many requests", "Wait a moment before trying this operation again."
	case http.StatusBadGateway:
		return "Upstream service failed", "A dependent service could not complete the request. No unverified result was applied; try again later."
	default:
		return "Service temporarily unavailable", "TermLink cannot complete this request right now. Try again later."
	}
}

func (p *Portal) internalError(w http.ResponseWriter, err error) {
	requestTraceID := uuid.NewString()
	logPortalFailure(context.Background(), slog.LevelError, "web portal request failed",
		"portal_request_failed", requestTraceID, err)
	var output bytes.Buffer
	englishTitle, englishMessage := publicEnglishError(http.StatusInternalServerError)
	if templateErr := p.templates.ExecuteTemplate(&output, "error.html", errorPageData{
		Title: "服务暂时不可用", Message: "TermLink 服务端暂时无法完成该请求，请稍后重试。",
		EnglishTitle: englishTitle, EnglishMessage: englishMessage, Reference: requestTraceID,
	}); templateErr != nil {
		logPortalFailure(context.Background(), slog.LevelError, "render web portal internal error page",
			"portal_error_page_render_failed", requestTraceID, templateErr)
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
		policy := "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"
		if r.URL.Path == "/register" {
			humanRuntime, humanErr := p.humanVerificationRuntime(r.Context())
			if humanErr != nil || humanRuntime.Enabled {
				policy = "default-src 'none'; script-src 'self' " + humanverification.TurnstileScriptOrigin +
					"; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self' " + humanverification.TurnstileScriptOrigin +
					"; frame-src 'self' " + humanverification.TurnstileScriptOrigin +
					"; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"
			}
		}
		w.Header().Set("Content-Security-Policy", policy)
		w.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()")
		w.Header().Set("Referrer-Policy", "strict-origin")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Cross-Origin-Opener-Policy", "same-origin")
		w.Header().Set("Strict-Transport-Security", "max-age=31536000")
		next.ServeHTTP(w, r)
	})
}

func (p *Portal) humanVerificationRuntime(ctx context.Context) (humanverification.Runtime, error) {
	if p.config.HumanVerification != nil {
		return p.config.HumanVerification.Runtime(ctx)
	}
	return humanverification.Runtime{
		Enabled: p.config.HumanVerificationProvider == humanverification.ProviderTurnstile,
		SiteKey: p.config.HumanVerificationSiteKey,
	}, nil
}

func (p *Portal) setCookie(r *http.Request, w http.ResponseWriter, name, value string, expires time.Time) {
	http.SetCookie(w, &http.Cookie{
		Name:     localnet.CookieName(r.Host, name),
		Value:    value,
		Path:     "/",
		Expires:  expires.UTC(),
		MaxAge:   int(expires.Sub(p.now()).Seconds()),
		Secure:   localnet.CookieSecure(r.Host),
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
	})
}

func (p *Portal) clearCookie(r *http.Request, w http.ResponseWriter, name string) {
	http.SetCookie(w, &http.Cookie{
		Name:     localnet.CookieName(r.Host, name),
		Value:    "",
		Path:     "/",
		Expires:  time.Unix(1, 0).UTC(),
		MaxAge:   -1,
		Secure:   localnet.CookieSecure(r.Host),
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
	})
}

func oauthCookieForState(state string) string {
	state = strings.TrimSpace(state)
	if len(state) < 16 || len(state) > 128 || strings.ContainsAny(state, " \t\r\n;=") {
		return oauthCookieName
	}
	return oauthCookieName + "-" + state
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
	origin := strings.TrimSpace(r.Header.Get("Origin"))
	if origin == "" {
		return p.requestUsesConfiguredPublicHost(r)
	}
	if p.usesPublicOrigin(origin) || originUsesRequestHost(origin, r) {
		return true
	}
	return p.requestUsesConfiguredPublicHost(r) &&
		strings.EqualFold(strings.TrimSpace(r.Header.Get("Sec-Fetch-Site")), "same-origin")
}

func (p *Portal) requestUsesConfiguredPublicHost(r *http.Request) bool {
	if strings.EqualFold(strings.TrimSpace(r.Host), p.publicOrigin.Host) {
		return true
	}
	host, ok := forwardedRequestHost(r)
	return ok && strings.EqualFold(host, p.publicOrigin.Host)
}

func originUsesRequestHost(value string, r *http.Request) bool {
	origin, err := url.Parse(value)
	if err != nil || !strings.EqualFold(origin.Scheme, "https") || origin.Host == "" || origin.User != nil ||
		origin.RawPath != "" || (origin.Path != "" && origin.Path != "/") || origin.RawQuery != "" || origin.Fragment != "" {
		return false
	}
	if strings.EqualFold(origin.Host, strings.TrimSpace(r.Host)) {
		return true
	}
	host, ok := forwardedRequestHost(r)
	return ok && strings.EqualFold(origin.Host, host)
}

func forwardedRequestHost(r *http.Request) (string, bool) {
	values := r.Header.Values("X-Forwarded-Host")
	if len(values) != 1 || strings.Contains(values[0], ",") {
		return "", false
	}
	for _, value := range values {
		host := strings.TrimSpace(value)
		if host == "" || strings.ContainsAny(host, " /\\\t\r\n@") {
			return "", false
		}
		return host, true
	}
	return "", false
}

func requestOrigin(r *http.Request) (string, bool) {
	if r == nil {
		return "", false
	}
	host := strings.TrimSpace(r.Host)
	if forwarded, ok := forwardedRequestHost(r); ok {
		host = forwarded
	}
	if host == "" || strings.ContainsAny(host, " /\\\t\r\n@") {
		return "", false
	}
	proto := strings.TrimSpace(r.Header.Get("X-Forwarded-Proto"))
	if proto == "" {
		if r.TLS == nil {
			return "", false
		}
		proto = "https"
	}
	if !strings.EqualFold(proto, "https") {
		return "", false
	}
	return "https://" + host, true
}

func sameCallbackURI(left, right string) bool {
	a, aErr := url.Parse(left)
	b, bErr := url.Parse(right)
	return aErr == nil && bErr == nil && strings.EqualFold(a.Scheme, b.Scheme) && strings.EqualFold(a.Host, b.Host) && a.Path == b.Path && a.RawQuery == b.RawQuery && a.Fragment == b.Fragment
}

func (p *Portal) now() time.Time { return p.config.Clock().UTC() }

func (f *productionOIDCFlow) AuthorizationURL(state, nonce, challenge, theme string) string {
	return oidcAuthorizationURL(f.oauthConfig, state, nonce, challenge, theme, true)
}

func (f *productionOIDCFlow) AuthorizationURLForRedirect(state, nonce, challenge, theme, redirectURI string) string {
	config := f.oauthConfig
	config.RedirectURL = redirectURI
	config.Endpoint = oidcEndpointForRedirect(config.Endpoint, redirectURI)
	return oidcAuthorizationURL(config, state, nonce, challenge, theme, true)
}

func (f *productionOIDCFlow) StepUpAuthorizationURL(state, nonce, challenge, theme, method string) string {
	if method == sensitiveMethodMFA {
		// The dedicated Keycloak client is bound to Cookie (REQUIRED) followed
		// by OTP Form (REQUIRED), so an existing SSO identity goes directly to
		// its configured authenticator without asking for the password again.
		return oidcAuthorizationURL(f.stepUpOAuthConfig, state, nonce, challenge, theme, false)
	}
	return oidcAuthorizationURL(f.oauthConfig, state, nonce, challenge, theme, true)
}

func (f *productionOIDCFlow) StepUpAuthorizationURLForRedirect(state, nonce, challenge, theme, method, redirectURI string) string {
	config := f.oauthConfig
	if method == sensitiveMethodMFA {
		config = f.stepUpOAuthConfig
	}
	config.RedirectURL = redirectURI
	config.Endpoint = oidcEndpointForRedirect(config.Endpoint, redirectURI)
	return oidcAuthorizationURL(config, state, nonce, challenge, theme, method != sensitiveMethodMFA)
}

func oidcEndpointForRedirect(endpoint oauth2.Endpoint, redirectURI string) oauth2.Endpoint {
	redirect, err := url.Parse(redirectURI)
	if err != nil || redirect.Scheme != "https" || redirect.Host == "" {
		return endpoint
	}
	authorize, err := url.Parse(endpoint.AuthURL)
	if err != nil || authorize.Path == "" {
		return endpoint
	}
	authorize.Scheme, authorize.Host = redirect.Scheme, redirect.Host
	endpoint.AuthURL = authorize.String()
	return endpoint
}

func oidcAuthorizationURL(config oauth2.Config, state, nonce, challenge, theme string, promptLogin bool) string {
	options := []oauth2.AuthCodeOption{
		oauth2.SetAuthURLParam("nonce", nonce),
		oauth2.SetAuthURLParam("code_challenge", challenge),
		oauth2.SetAuthURLParam("code_challenge_method", "S256"),
	}
	if promptLogin {
		// Web authentication and password fallback must evaluate credentials
		// instead of silently accepting an existing native-client SSO cookie.
		options = append(options, oauth2.SetAuthURLParam("prompt", "login"))
	}
	if theme == "system" || theme == "light" || theme == "dark" {
		options = append(options, oauth2.SetAuthURLParam(themeQueryParameter, theme))
	}
	return config.AuthCodeURL(state, options...)
}

func (f *productionOIDCFlow) ExchangeAndVerify(ctx context.Context, code, verifier, expectedNonce string) (accountIdentity, error) {
	if f.httpClient != nil {
		ctx = context.WithValue(ctx, oauth2.HTTPClient, f.httpClient)
	}
	return exchangePortalIdentity(ctx, f.oauthConfig, f.verifier, f.clientID, code, verifier, expectedNonce)
}

func (f *productionOIDCFlow) ExchangeAndVerifyForRedirect(ctx context.Context, code, verifier, expectedNonce, redirectURI string) (accountIdentity, error) {
	if f.httpClient != nil {
		ctx = context.WithValue(ctx, oauth2.HTTPClient, f.httpClient)
	}
	config := f.oauthConfig
	config.RedirectURL = redirectURI
	return exchangePortalIdentity(ctx, config, f.verifier, f.clientID, code, verifier, expectedNonce)
}

func (f *productionOIDCFlow) ExchangeStepUpAndVerify(ctx context.Context, code, verifier, expectedNonce, method string) (accountIdentity, error) {
	if method == sensitiveMethodMFA {
		if f.httpClient != nil {
			ctx = context.WithValue(ctx, oauth2.HTTPClient, f.httpClient)
		}
		return exchangePortalIdentity(ctx, f.stepUpOAuthConfig, f.stepUpVerifier, f.stepUpOAuthConfig.ClientID, code, verifier, expectedNonce)
	}
	return f.ExchangeAndVerify(ctx, code, verifier, expectedNonce)
}

func (f *productionOIDCFlow) ExchangeStepUpAndVerifyForRedirect(ctx context.Context, code, verifier, expectedNonce, method, redirectURI string) (accountIdentity, error) {
	if f.httpClient != nil {
		ctx = context.WithValue(ctx, oauth2.HTTPClient, f.httpClient)
	}
	config, verifierKey, clientID := f.oauthConfig, f.verifier, f.clientID
	if method == sensitiveMethodMFA {
		config, verifierKey, clientID = f.stepUpOAuthConfig, f.stepUpVerifier, f.stepUpOAuthConfig.ClientID
	}
	config.RedirectURL = redirectURI
	return exchangePortalIdentity(ctx, config, verifierKey, clientID, code, verifier, expectedNonce)
}

func exchangePortalIdentity(ctx context.Context, config oauth2.Config, verifier *oidc.IDTokenVerifier, clientID, code, pkceVerifier, expectedNonce string) (accountIdentity, error) {
	token, err := config.Exchange(ctx, code, oauth2.VerifierOption(pkceVerifier))
	if err != nil {
		return accountIdentity{}, fmt.Errorf("exchange web OIDC code: %w", err)
	}
	rawIDToken, ok := token.Extra("id_token").(string)
	if !ok || rawIDToken == "" {
		return accountIdentity{}, errors.New("web OIDC ID token is missing")
	}
	idToken, err := verifier.Verify(ctx, rawIDToken)
	if err != nil {
		return accountIdentity{}, fmt.Errorf("verify web OIDC ID token: %w", err)
	}
	var claims oidcPortalClaims
	if err := idToken.Claims(&claims); err != nil {
		return accountIdentity{}, errors.New("web OIDC claims are invalid")
	}
	return portalIdentityFromClaims(claims, clientID, expectedNonce)
}

func portalIdentityFromClaims(claims oidcPortalClaims, clientID, expectedNonce string) (accountIdentity, error) {
	if strings.TrimSpace(claims.Subject) == "" || !constantTextEqual(claims.Nonce, expectedNonce) {
		return accountIdentity{}, errors.New("web OIDC nonce or subject is invalid")
	}
	roles := append([]string(nil), claims.RealmAccess.Roles...)
	bootstrapManaged := hasOIDCRole(claims.RealmAccess.Roles, bootstrapManagedOIDCRole)
	if resource, ok := claims.ResourceAccess[clientID]; ok {
		roles = append(roles, resource.Roles...)
	}
	var authenticatedAt time.Time
	if claims.AuthenticatedAt > 0 {
		authenticatedAt = time.Unix(claims.AuthenticatedAt, 0).UTC()
	}
	acr := strings.TrimSpace(claims.ACR)
	if len(acr) > 256 || !safePublicText(acr, 256, false) {
		acr = ""
	}
	return accountIdentity{
		Subject: claims.Subject, Username: claims.PreferredUsername, Nickname: claims.Nickname, Name: claims.Name, Email: claims.Email,
		Roles: roles, BootstrapManaged: bootstrapManaged,
		AuthenticatedAt: authenticatedAt, MFA: portalAMRProvesMFA(claims.AMR), Password: portalAMRProvesPassword(claims.AMR), ACR: acr,
	}, nil
}

func portalAMRProvesMFA(methods []string) bool {
	for _, method := range methods {
		switch strings.ToLower(strings.TrimSpace(method)) {
		case "mfa", "otp", "hwk", "swk", "fpt", "face":
			return true
		}
	}
	return false
}

func portalAMRProvesPassword(methods []string) bool {
	for _, method := range methods {
		if strings.EqualFold(strings.TrimSpace(method), "pwd") {
			return true
		}
	}
	return false
}

func authenticatedAtMilliseconds(authenticatedAt, now time.Time) int64 {
	if authenticatedAt.IsZero() || authenticatedAt.After(now) {
		return 0
	}
	return authenticatedAt.UTC().UnixMilli()
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

func (f *productionOIDCFlow) LogoutURLForOrigin(origin string) string {
	if strings.TrimSpace(f.endSessionEndpoint) == "" {
		return origin + "/"
	}
	endpoint, err := url.Parse(f.endSessionEndpoint)
	if err != nil || endpoint.Scheme != "https" {
		return origin + "/"
	}
	if parsedOrigin, originErr := url.Parse(origin); originErr == nil && parsedOrigin.Host != "" {
		endpoint.Scheme, endpoint.Host = parsedOrigin.Scheme, parsedOrigin.Host
	}
	query := endpoint.Query()
	query.Set("client_id", f.clientID)
	query.Set("post_logout_redirect_uri", origin+"/")
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
	if value == "system" || value == "light" || value == "dark" {
		return value
	}
	return ""
}

func normalizePortalReturnTo(value string) string {
	switch strings.TrimSpace(value) {
	case "/":
		return "/"
	case "/account":
		return "/account"
	case "/admin":
		return "/admin"
	case "/account/identity", "/account/identity/password", "/account/identity/mfa", "/account/identity/sessions":
		return strings.TrimSpace(value)
	default:
		return "/"
	}
}

func normalizeRegistrationContinueURL(config Config, value string, requests ...*http.Request) string {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 4096 {
		return ""
	}
	target, err := url.Parse(value)
	if err != nil || target.User != nil || target.RawPath != "" || target.Fragment != "" {
		return ""
	}
	issuer, err := url.Parse(strings.TrimRight(strings.TrimSpace(config.Issuer), "/"))
	if err != nil || !strings.EqualFold(target.Scheme, issuer.Scheme) ||
		(!strings.EqualFold(target.Host, issuer.Host) && !registrationContinueUsesRequestOrigin(target, requests)) {
		return ""
	}
	legacyPath := strings.TrimRight(issuer.Path, "/") + "/login-actions/authenticate"
	embeddedPath := strings.TrimRight(issuer.Path, "/") + "/protocol/openid-connect/auth"
	if target.Path != legacyPath && target.Path != embeddedPath {
		return ""
	}
	query, err := url.ParseQuery(target.RawQuery)
	if err != nil {
		return ""
	}
	windowsClientID := strings.TrimSpace(config.WindowsClientID)
	if windowsClientID == "" {
		windowsClientID = "termlink-windows"
	}
	if len(query["client_id"]) != 1 {
		return ""
	}
	clientID := strings.TrimSpace(query.Get("client_id"))
	if clientID == "" || (clientID != windowsClientID && clientID != strings.TrimSpace(config.ClientID)) {
		return ""
	}
	return target.String()
}

func registrationContinueUsesRequestOrigin(target *url.URL, requests []*http.Request) bool {
	if len(requests) == 0 {
		return false
	}
	origin, ok := requestOrigin(requests[0])
	return ok && strings.EqualFold(target.Scheme+"://"+target.Host, origin)
}

func identityReturnPath(action string) string {
	switch strings.TrimSpace(action) {
	case "":
		return "/account/identity"
	case "password", "mfa", "sessions":
		return "/account/identity/" + strings.TrimSpace(action)
	default:
		return ""
	}
}

func identityAccountSectionURL(base, returnTo, theme string) string {
	target, err := url.Parse(addThemeToURL(base, theme))
	if err != nil {
		return "/account"
	}
	switch normalizePortalReturnTo(returnTo) {
	case "/account/identity/password", "/account/identity/mfa":
		target.Fragment = "/security/signingin"
	case "/account/identity/sessions":
		target.Fragment = "/security/device-activity"
	}
	return target.String()
}

func validateIdentityAccountURL(config Config, value string) (string, error) {
	issuer, err := url.Parse(strings.TrimSpace(config.Issuer))
	if err != nil {
		return "", errors.New("web portal identity issuer is invalid")
	}
	target, err := url.Parse(strings.TrimSpace(value))
	// The target inherits the issuer scheme, which the local-development
	// relaxation may already have downgraded to plain HTTP.
	targetSchemeOK := target != nil && (target.Scheme == "https" ||
		(localnet.AllowHTTP() && target.Scheme == "http" && localnet.IsLoopbackOrPrivateHost(target.Hostname())))
	if err != nil || !targetSchemeOK || target.Host == "" || target.User != nil ||
		target.RawPath != "" || target.Fragment != "" || !strings.EqualFold(target.Scheme, issuer.Scheme) ||
		!strings.EqualFold(target.Host, issuer.Host) {
		return "", errors.New("web portal identity account URL must use the configured issuer origin")
	}
	expectedPath := strings.TrimRight(issuer.Path, "/") + "/account"
	if strings.TrimRight(target.Path, "/") != expectedPath {
		return "", errors.New("web portal identity account URL must use the configured realm account path")
	}
	query := target.Query()
	for key := range query {
		if key != "referrer" && key != "referrer_uri" && key != themeQueryParameter {
			return "", errors.New("web portal identity account URL contains an unsupported query parameter")
		}
	}
	if referrer := query.Get("referrer"); referrer != config.ClientID {
		return "", errors.New("web portal identity account URL contains an invalid referrer")
	}
	if referrerURI := query.Get("referrer_uri"); referrerURI != strings.TrimRight(config.PublicURL, "/")+"/account" {
		return "", errors.New("web portal identity account URL contains an invalid return target")
	}
	query.Del(themeQueryParameter)
	target.RawQuery = query.Encode()
	return target.String(), nil
}

func addThemeToURL(value, theme string) string {
	target, err := url.Parse(value)
	if err != nil {
		return "/account"
	}
	query := target.Query()
	query.Del(themeQueryParameter)
	if theme = normalizeTheme(theme); theme != "" {
		query.Set(themeQueryParameter, theme)
	}
	target.RawQuery = query.Encode()
	return target.String()
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

const bootstrapManagedOIDCRole = "TERMLINK_BOOTSTRAP_MANAGED_V1"

func portalIdentityAssertion(identity accountIdentity) store.PortalIdentityAssertion {
	return store.PortalIdentityAssertion{
		Role:             assertedRole(identity.Roles),
		BootstrapManaged: identity.BootstrapManaged,
	}
}

func hasOIDCRole(roles []string, expected string) bool {
	for _, value := range roles {
		if strings.EqualFold(strings.TrimSpace(value), expected) {
			return true
		}
	}
	return false
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
		p.clearCookie(r, w, sessionCookieName)
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
	return p.requireSensitiveAdminAction(w, r, true)
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
	case "overview", "connections", "deployment", "versions", "identity", "accounts", "settings":
		return value
	default:
		return "overview"
	}
}

func adminSectionTitle(section string) string {
	return map[string]string{
		"overview": "运行概览", "connections": "连接与路由", "deployment": "Docker 部署",
		"versions": "系统版本", "identity": "身份与安全",
		"accounts": "账号与角色",
		"settings": "系统设置",
	}[normalizeAdminSection(section)]
}

func adminNotice(value string) string {
	switch value {
	case "role-updated":
		return bilingualPortalNotice("账号角色已更新，并已写入操作记录。", "The account role was updated and recorded.")
	case "status-updated":
		return bilingualPortalNotice("账号状态已更新，并已写入操作记录。", "The account status was updated and recorded.")
	case "service-email-updated":
		return bilingualPortalNotice("测试邮件已发送，服务邮箱配置和功能开关已发布。", "The test email was sent, and the service email settings are now active.")
	case "human-verification-updated":
		return bilingualPortalNotice("登录与注册人机验证设置已更新并立即生效。", "Login and registration human-verification settings were updated and are active immediately.")
	case "transport-routing-updated":
		return bilingualPortalNotice("网络传输策略已更新；自动模式用于新连接或重连后的设备，仅中转模式会让现有直连有界回退。", "The transport policy was updated: automatic mode applies to new or reconnected devices, while Relay-only mode retires existing Direct paths within a bounded interval.")
	case "runtime-setting-updated":
		return bilingualPortalNotice("运行时参数已保存并即时生效；重启后仍保持。", "The runtime setting was saved and is effective immediately; it persists across restarts.")
	case "admin-route-updated":
		return bilingualPortalNotice("管理中心安全入口已经更新，旧入口已立即失效。请将新地址保存在受信任的密码管理器中。", "The secure admin route was updated and the old route is no longer valid. Store the new address in a trusted password manager.")
	default:
		return ""
	}
}

func bilingualPortalNotice(chinese, english string) string {
	return chinese + " / " + english
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

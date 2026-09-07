package identity

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/go-jose/go-jose/v3"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/ory/fosite"
	"github.com/ory/fosite/compose"
	"github.com/ory/fosite/token/jwt"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/humanverification"
)

const (
	embeddedAccessTokenLifetime    = 5 * time.Minute
	embeddedRefreshTokenLifetime   = 8 * time.Hour
	embeddedAuthorizeCodeLifetime  = time.Minute
	embeddedIDTokenLifetime        = 5 * time.Minute
	embeddedBrowserSessionLifetime = 8 * time.Hour
	serverAudience                 = "termlink-server"
)

// Config is the complete, first-party configuration for the embedded issuer.
// It deliberately has no dynamic client-registration surface: only the four
// TermLink clients are accepted.
type Config struct {
	Issuer               string
	Pool                 *pgxpool.Pool
	HMACSecret           []byte
	KeyEncryptionKey     []byte
	InitialAdminPassword string
	AndroidClientID      string
	AndroidRedirectURI   string
	WindowsClientID      string
	WindowsRedirectURI   string
	WebClientID          string
	WebRedirectURI       string
	StepUpClientID       string
	AllowedParties       []string
	Clock                func() time.Time
}

type HumanVerification interface {
	Runtime(context.Context) (humanverification.Runtime, error)
	VerifyAction(context.Context, string, string, string) error
}

type Provider struct {
	issuer              string
	accounts            *AccountStore
	storage             *oauthStore
	oauth               fosite.OAuth2Provider
	signingKeys         *signingKeyManager
	verifier            auth.TokenVerifier
	handler             http.Handler
	discovery           auth.OIDCDiscovery
	clock               func() time.Time
	browserCookies      browserCookieCodec
	stepUpClientID      string
	publicOrigin        string
	passwordResetMailer PasswordResetMailer
	passwordResetJobs   chan passwordResetJob
	humanVerification   HumanVerification
	authorizeFormAction string
}

// New constructs an embedded OIDC provider without making any network call.
// Migrations must already have been applied to Pool.
func New(ctx context.Context, config Config) (*Provider, error) {
	if ctx == nil || config.Pool == nil {
		return nil, errors.New("embedded identity context and database are required")
	}
	issuer, publicOrigin, err := validateEmbeddedIssuer(config.Issuer)
	if err != nil {
		return nil, err
	}
	if len(config.HMACSecret) < 32 || len(config.KeyEncryptionKey) != 32 {
		return nil, errors.New("embedded identity keys are invalid")
	}
	if config.Clock == nil {
		config.Clock = time.Now
	}
	clients, err := embeddedClients(config)
	if err != nil {
		return nil, err
	}
	authorizeFormAction, err := embeddedAuthorizeFormAction(config)
	if err != nil {
		return nil, err
	}
	accounts, err := NewAccountStore(config.Pool)
	if err != nil {
		return nil, err
	}
	accounts.clock = config.Clock
	accounts.credentialKey = append([]byte(nil), config.KeyEncryptionKey...)
	if config.InitialAdminPassword != "" {
		if _, err = accounts.BootstrapInitialAdministrator(ctx, config.InitialAdminPassword); err != nil {
			return nil, fmt.Errorf("bootstrap embedded identity administrator: %w", err)
		}
	}
	signingKeys, err := newSigningKeyManager(ctx, config.Pool, config.KeyEncryptionKey, config.Clock)
	if err != nil {
		return nil, fmt.Errorf("load embedded OIDC signing key: %w", err)
	}
	verificationKeys, err := loadVerificationKeys(ctx, config.Pool, config.Clock().UTC())
	if err != nil {
		return nil, fmt.Errorf("load embedded OIDC verification keys: %w", err)
	}
	verifier, err := newTokenVerifier(issuer, serverAudience, config.AllowedParties, verificationKeys, config.Clock, accounts)
	if err != nil {
		return nil, err
	}
	storage := &oauthStore{pool: config.Pool, accounts: accounts, clients: clients, clock: config.Clock}
	tokenURL := issuer + "/protocol/openid-connect/token"
	fositeConfig := &fosite.Config{
		GlobalSecret:                   append([]byte(nil), config.HMACSecret...),
		AccessTokenLifespan:            embeddedAccessTokenLifetime,
		RefreshTokenLifespan:           embeddedRefreshTokenLifetime,
		AuthorizeCodeLifespan:          embeddedAuthorizeCodeLifetime,
		IDTokenLifespan:                embeddedIDTokenLifetime,
		IDTokenIssuer:                  issuer,
		AccessTokenIssuer:              issuer,
		TokenURL:                       tokenURL,
		EnforcePKCEForPublicClients:    true,
		EnablePKCEPlainChallengeMethod: false,
		RefreshTokenScopes:             []string{},
		AllowedPromptValues:            []string{"login", "none", "consent", "select_account"},
		JWTScopeClaimKey:               jwt.JWTScopeFieldString,
		SendDebugMessagesToClients:     false,
	}
	keyGetter := func(ctx context.Context) (interface{}, error) {
		key, ok := ctx.Value(signingKeyContextKey{}).(signingKey)
		if !ok {
			key, err = signingKeys.Current()
			if err != nil {
				return nil, err
			}
		}
		return key.Private, nil
	}
	hmacStrategy := compose.NewOAuth2HMACStrategy(fositeConfig)
	jwtStrategy := compose.NewOAuth2JWTStrategy(keyGetter, hmacStrategy, fositeConfig)
	strategy := &compose.CommonStrategy{
		CoreStrategy:               jwtStrategy,
		OpenIDConnectTokenStrategy: compose.NewOpenIDConnectStrategy(keyGetter, fositeConfig),
		Signer:                     &jwt.DefaultSigner{GetPrivateKey: keyGetter},
	}
	oauthProvider := compose.Compose(
		fositeConfig,
		storage,
		strategy,
		compose.OAuth2AuthorizeExplicitFactory,
		compose.OAuth2RefreshTokenGrantFactory,
		compose.OAuth2ResourceOwnerPasswordCredentialsFactory,
		compose.OpenIDConnectExplicitFactory,
		compose.OpenIDConnectRefreshFactory,
		compose.OAuth2PKCEFactory,
	)
	discovery, err := auth.NewLocalOIDCDiscoveryWithKeySet(
		issuer,
		issuer+"/protocol/openid-connect/auth",
		tokenURL,
		issuer+"/protocol/openid-connect/certs",
		issuer+"/protocol/openid-connect/logout",
		databaseOIDCKeySet{pool: config.Pool, clock: config.Clock},
	)
	if err != nil {
		return nil, err
	}
	browserCookies, err := newBrowserCookieCodec(config.HMACSecret)
	if err != nil {
		return nil, err
	}
	p := &Provider{
		issuer: issuer, accounts: accounts, storage: storage, oauth: oauthProvider,
		signingKeys: signingKeys, verifier: verifier, discovery: discovery, clock: config.Clock,
		browserCookies: browserCookies, stepUpClientID: config.StepUpClientID,
		publicOrigin:        publicOrigin,
		authorizeFormAction: authorizeFormAction,
		passwordResetJobs:   make(chan passwordResetJob, 32),
	}
	if err = cleanupExpiredIdentityState(ctx, config.Pool, config.Clock().UTC(), true); err != nil {
		slog.Warn("embedded identity startup cleanup failed", "error_class", "identity_cleanup_failed")
	}
	go p.maintainIdentityState(ctx)
	go p.runPasswordResetMailer(ctx)
	p.handler = p.routes()
	return p, nil
}

type signingKeyContextKey struct{}

func (p *Provider) maintainIdentityState(ctx context.Context) {
	ticker := time.NewTicker(identityMaintenanceInterval)
	defer ticker.Stop()
	defer p.signingKeys.Clear()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			keysRefreshed := true
			if err := p.signingKeys.Refresh(ctx); err != nil {
				keysRefreshed = false
				slog.Warn("embedded identity signing-key refresh failed", "error_class", "identity_signing_key_refresh_failed")
			}
			if err := cleanupExpiredIdentityState(ctx, p.accounts.pool, p.clock().UTC(), keysRefreshed); err != nil {
				slog.Warn("embedded identity cleanup failed", "error_class", "identity_cleanup_failed")
			}
		}
	}
}

func validateEmbeddedIssuer(raw string) (string, string, error) {
	issuer := strings.TrimRight(strings.TrimSpace(raw), "/")
	parsed, err := url.Parse(issuer)
	if err != nil || parsed.Scheme != "https" || parsed.Host == "" || parsed.User != nil ||
		parsed.RawQuery != "" || parsed.Fragment != "" || parsed.RawPath != "" ||
		!strings.HasSuffix(parsed.Path, "/realms/termlink") {
		return "", "", errors.New("embedded OIDC issuer must be an HTTPS /realms/termlink URL")
	}
	basePath := strings.TrimSuffix(parsed.Path, "/realms/termlink")
	if basePath != "" && basePath != "/" {
		return "", "", errors.New("embedded OIDC issuer cannot be mounted below an additional path")
	}
	return issuer, parsed.Scheme + "://" + parsed.Host, nil
}

func embeddedClients(config Config) (map[string]fosite.Client, error) {
	entries := []struct {
		id       string
		redirect string
		password bool
	}{
		{config.AndroidClientID, config.AndroidRedirectURI, true},
		{config.WindowsClientID, config.WindowsRedirectURI, true},
		{config.WebClientID, config.WebRedirectURI, false},
		{config.StepUpClientID, config.WebRedirectURI, false},
	}
	clients := make(map[string]fosite.Client, len(entries))
	for _, entry := range entries {
		entry.id = strings.TrimSpace(entry.id)
		entry.redirect = strings.TrimSpace(entry.redirect)
		if entry.id == "" || len(entry.id) > 128 || strings.ContainsAny(entry.id, " \t\r\n") || entry.redirect == "" {
			return nil, errors.New("embedded OIDC client configuration is invalid")
		}
		if _, duplicate := clients[entry.id]; duplicate {
			return nil, errors.New("embedded OIDC client IDs must be distinct")
		}
		redirect, err := url.Parse(entry.redirect)
		if err != nil || !fosite.IsValidRedirectURI(redirect) {
			return nil, errors.New("embedded OIDC redirect URI is invalid")
		}
		grants := []string{"authorization_code", "refresh_token"}
		if entry.password {
			grants = append(grants, "password")
		}
		clients[entry.id] = &fosite.DefaultOpenIDConnectClient{
			DefaultClient: &fosite.DefaultClient{
				ID: entry.id, RedirectURIs: []string{entry.redirect}, GrantTypes: grants,
				ResponseTypes: []string{"code"}, Scopes: []string{"openid", "profile", "email", "offline_access"},
				Audience: []string{serverAudience}, Public: true,
			},
			TokenEndpointAuthMethod: "none",
		}
	}
	return clients, nil
}

func embeddedAuthorizeFormAction(config Config) (string, error) {
	androidRedirect, androidErr := url.Parse(strings.TrimSpace(config.AndroidRedirectURI))
	if androidErr != nil || androidRedirect.Scheme != "dev.termlink" || androidRedirect.Host != "" ||
		androidRedirect.Path != "/oauth2redirect" || androidRedirect.RawQuery != "" ||
		androidRedirect.Fragment != "" || androidRedirect.User != nil {
		return "", errors.New("embedded Android OIDC redirect URI is invalid")
	}
	windowsRedirect, windowsErr := url.Parse(strings.TrimSpace(config.WindowsRedirectURI))
	if windowsErr != nil || windowsRedirect.Scheme != "http" || windowsRedirect.Host != "127.0.0.1" ||
		windowsRedirect.Path != "/oauth2/callback" || windowsRedirect.RawQuery != "" ||
		windowsRedirect.Fragment != "" || windowsRedirect.User != nil {
		return "", errors.New("embedded Windows OIDC redirect URI is invalid")
	}
	// RFC 8252 requires native applications to choose an ephemeral loopback
	// port. CSP otherwise treats the configured default port as exact and
	// Chromium blocks the login form's 303 callback navigation.
	return "'self' dev.termlink: http://127.0.0.1:*", nil
}

func (p *Provider) routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /realms/termlink/.well-known/openid-configuration", p.serveDiscovery)
	mux.HandleFunc("GET /realms/termlink/protocol/openid-connect/certs", p.serveJWKS)
	mux.HandleFunc("GET /realms/termlink/protocol/openid-connect/auth", p.authorize)
	mux.HandleFunc("POST /realms/termlink/protocol/openid-connect/auth", p.authorize)
	mux.HandleFunc("POST /realms/termlink/protocol/openid-connect/token", p.token)
	mux.HandleFunc("GET /realms/termlink/protocol/openid-connect/logout", p.logout)
	mux.HandleFunc("POST /realms/termlink/protocol/openid-connect/logout", p.logout)
	mux.HandleFunc("GET /realms/termlink/account", p.accountPage)
	mux.HandleFunc("GET /realms/termlink/account/", p.accountPage)
	mux.HandleFunc("GET /realms/termlink/account/password/reset", p.passwordResetPage)
	mux.HandleFunc("POST /realms/termlink/account/password/reset", p.passwordResetPage)
	mux.HandleFunc("POST /realms/termlink/account/password/change", p.changePassword)
	mux.HandleFunc("POST /realms/termlink/account/mfa/begin", p.beginMFAEnrollment)
	mux.HandleFunc("POST /realms/termlink/account/mfa/confirm", p.confirmMFAEnrollment)
	mux.HandleFunc("POST /realms/termlink/account/mfa/disable", p.disableMFA)
	mux.HandleFunc("POST /realms/termlink/account/recovery-codes/regenerate", p.regenerateRecoveryCodes)
	mux.HandleFunc("GET /realms/termlink/resources/identity.css", serveIdentityCSS)
	return p.securityHeaders(mux)
}

func (p *Provider) Handler() http.Handler { return p.handler }

func (p *Provider) Accounts() *AccountStore { return p.accounts }

func (p *Provider) TokenVerifier() auth.TokenVerifier { return p.verifier }

func (p *Provider) Discovery() auth.OIDCDiscovery { return p.discovery }

// SetPasswordResetMailer connects the embedded provider to the already
// configured TermLink service-email delivery path. It must be called during
// process initialization, before Handler is exposed to HTTP traffic.
func (p *Provider) SetPasswordResetMailer(mailer PasswordResetMailer) {
	p.passwordResetMailer = mailer
}

// SetHumanVerification connects the embedded browser login to the same
// managed Turnstile settings used by registration. It must be called during
// process initialization, before Handler is exposed to HTTP traffic.
func (p *Provider) SetHumanVerification(verifier HumanVerification) {
	p.humanVerification = verifier
}

// HTTPClient returns a no-network client for same-process token exchange.
func (p *Provider) HTTPClient() *http.Client {
	return &http.Client{Transport: inProcessRoundTripper{handler: p.handler, origin: p.publicOrigin}, Timeout: 15 * time.Second}
}

func (p *Provider) serveDiscovery(w http.ResponseWriter, _ *http.Request) {
	p.writeJSON(w, http.StatusOK, map[string]any{
		"issuer":                                p.issuer,
		"authorization_endpoint":                p.discovery.AuthorizationEndpoint,
		"token_endpoint":                        p.discovery.TokenEndpoint,
		"jwks_uri":                              p.discovery.JWKSURL,
		"end_session_endpoint":                  p.discovery.EndSessionEndpoint,
		"response_types_supported":              []string{"code"},
		"response_modes_supported":              []string{"query"},
		"grant_types_supported":                 []string{"authorization_code", "refresh_token", "password"},
		"subject_types_supported":               []string{"public"},
		"id_token_signing_alg_values_supported": []string{identityKeyAlgorithm},
		"token_endpoint_auth_methods_supported": []string{"none"},
		"scopes_supported":                      []string{"openid", "profile", "email", "offline_access"},
		"code_challenge_methods_supported":      []string{"S256"},
		"claims_supported":                      []string{"sub", "iss", "aud", "azp", "typ", "jti", "auth_time", "amr", "acr", "auth_epoch", "preferred_username", "nickname", "name", "email", "email_verified", "realm_access", "resource_access"},
	})
}

func (p *Provider) serveJWKS(w http.ResponseWriter, r *http.Request) {
	keys, err := loadVerificationKeys(r.Context(), p.accounts.pool, p.clock().UTC())
	if err != nil {
		http.Error(w, "身份签名密钥暂不可用 / Identity signing keys unavailable", http.StatusServiceUnavailable)
		return
	}
	public := make([]jose.JSONWebKey, 0, len(keys))
	for _, key := range keys {
		public = append(public, key)
	}
	p.writeJSON(w, http.StatusOK, jose.JSONWebKeySet{Keys: public})
}

func (p *Provider) writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func (p *Provider) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		frameAncestors := "'none'"
		frameOptions := "DENY"
		scriptPolicy := ""
		if identityAllowsSameOriginFraming(r.URL.Path) {
			frameAncestors = "'self'"
			frameOptions = "SAMEORIGIN"
			scriptPolicy = "script-src 'self'; "
		}
		w.Header().Set("Content-Security-Policy", "default-src 'none'; "+scriptPolicy+"style-src 'self'; img-src 'self' data:; form-action "+p.formActionForPath(r.URL.Path)+"; frame-ancestors "+frameAncestors+"; base-uri 'none'")
		// Chromium serializes the Origin header as "null" for same-origin form
		// navigations when no-referrer is active. Preserve only the origin so the
		// browser can provide usable form metadata without copying the OIDC query
		// into Referer headers.
		w.Header().Set("Referrer-Policy", "strict-origin")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", frameOptions)
		w.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
		next.ServeHTTP(w, r)
	})
}

func (p *Provider) formActionForPath(path string) string {
	if path == "/realms/termlink/protocol/openid-connect/auth" && p.authorizeFormAction != "" {
		return p.authorizeFormAction
	}
	return "'self'"
}

func identityAllowsSameOriginFraming(path string) bool {
	const (
		authorizePath = "/realms/termlink/protocol/openid-connect/auth"
		accountPath   = "/realms/termlink/account"
	)
	return path == authorizePath || path == accountPath || strings.HasPrefix(path, accountPath+"/")
}

func deriveIdentityKey(master []byte, purpose string) []byte {
	digest := sha256.Sum256(append(append([]byte("TermLink embedded identity V1\x00"), []byte(purpose)...), master...))
	return digest[:]
}

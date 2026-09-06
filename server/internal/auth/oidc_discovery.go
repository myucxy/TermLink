package auth

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"strings"

	"github.com/coreos/go-oidc/v3/oidc"
	"golang.org/x/oauth2"
)

// OIDCDiscovery separates the issuer visible in tokens and browsers from the
// private address used by Server for discovery, JWKS, and token exchange.
// Browser-facing endpoints are never rewritten to the private address.
type OIDCDiscovery struct {
	Issuer                 string
	AuthorizationEndpoint  string
	TokenEndpoint          string
	DeviceAuthorizationURL string
	UserInfoEndpoint       string
	JWKSURL                string
	EndSessionEndpoint     string
	SigningAlgorithms      []string
	keySet                 oidc.KeySet
}

func DiscoverOIDC(ctx context.Context, issuer, backchannelIssuer string) (OIDCDiscovery, error) {
	issuer = strings.TrimRight(strings.TrimSpace(issuer), "/")
	backchannelIssuer = strings.TrimRight(strings.TrimSpace(backchannelIssuer), "/")
	if issuer == "" {
		return OIDCDiscovery{}, errors.New("OIDC issuer is required")
	}
	if backchannelIssuer == "" {
		backchannelIssuer = issuer
	}
	discoveryContext := ctx
	if backchannelIssuer != issuer {
		// The override only controls what issuer go-oidc binds into its temporary
		// provider. The discovery document is checked explicitly below before any
		// endpoint is accepted or rewritten.
		discoveryContext = oidc.InsecureIssuerURLContext(ctx, issuer)
	}
	provider, err := oidc.NewProvider(discoveryContext, backchannelIssuer)
	if err != nil {
		return OIDCDiscovery{}, fmt.Errorf("discover OIDC issuer: %w", err)
	}
	var claims struct {
		Issuer                 string   `json:"issuer"`
		AuthorizationEndpoint  string   `json:"authorization_endpoint"`
		TokenEndpoint          string   `json:"token_endpoint"`
		DeviceAuthorizationURL string   `json:"device_authorization_endpoint"`
		UserInfoEndpoint       string   `json:"userinfo_endpoint"`
		JWKSURL                string   `json:"jwks_uri"`
		EndSessionEndpoint     string   `json:"end_session_endpoint"`
		SigningAlgorithms      []string `json:"id_token_signing_alg_values_supported"`
	}
	if err := provider.Claims(&claims); err != nil {
		return OIDCDiscovery{}, fmt.Errorf("decode OIDC discovery: %w", err)
	}
	if strings.TrimRight(claims.Issuer, "/") != issuer {
		return OIDCDiscovery{}, fmt.Errorf("OIDC discovery issuer mismatch: expected %q", issuer)
	}
	if claims.AuthorizationEndpoint == "" || claims.TokenEndpoint == "" || claims.JWKSURL == "" {
		return OIDCDiscovery{}, errors.New("OIDC discovery is missing required endpoints")
	}
	algorithms := supportedOIDCAlgorithms(claims.SigningAlgorithms)
	tokenURL, err := oidcBackchannelEndpoint(issuer, backchannelIssuer, claims.TokenEndpoint)
	if err != nil {
		return OIDCDiscovery{}, fmt.Errorf("validate OIDC token endpoint: %w", err)
	}
	jwksURL, err := oidcBackchannelEndpoint(issuer, backchannelIssuer, claims.JWKSURL)
	if err != nil {
		return OIDCDiscovery{}, fmt.Errorf("validate OIDC JWKS endpoint: %w", err)
	}
	userInfoURL := claims.UserInfoEndpoint
	if userInfoURL != "" {
		userInfoURL, err = oidcBackchannelEndpoint(issuer, backchannelIssuer, userInfoURL)
		if err != nil {
			return OIDCDiscovery{}, fmt.Errorf("validate OIDC user-info endpoint: %w", err)
		}
	}
	return OIDCDiscovery{
		Issuer: issuer, AuthorizationEndpoint: claims.AuthorizationEndpoint,
		TokenEndpoint: tokenURL, DeviceAuthorizationURL: claims.DeviceAuthorizationURL,
		UserInfoEndpoint: userInfoURL, JWKSURL: jwksURL,
		EndSessionEndpoint: claims.EndSessionEndpoint,
		SigningAlgorithms:  algorithms,
		keySet:             oidc.NewRemoteKeySet(discoveryContext, jwksURL),
	}, nil
}

func (d OIDCDiscovery) Endpoint() oauth2.Endpoint {
	return oauth2.Endpoint{
		AuthURL: d.AuthorizationEndpoint, TokenURL: d.TokenEndpoint,
		DeviceAuthURL: d.DeviceAuthorizationURL,
	}
}

func (d OIDCDiscovery) Verifier(clientID string) *oidc.IDTokenVerifier {
	return oidc.NewVerifier(d.Issuer, d.keySet, &oidc.Config{
		ClientID: clientID, SupportedSigningAlgs: append([]string(nil), d.SigningAlgorithms...),
	})
}

func oidcBackchannelEndpoint(issuer, backchannelIssuer, endpoint string) (string, error) {
	publicURL, err := url.Parse(issuer)
	if err != nil {
		return "", err
	}
	backchannelURL, err := url.Parse(backchannelIssuer)
	if err != nil {
		return "", err
	}
	target, err := url.Parse(endpoint)
	if err != nil || target == nil || target.Scheme == "" || target.Host == "" || target.User != nil || target.Fragment != "" {
		return "", errors.New("endpoint URL is invalid")
	}
	if issuer == backchannelIssuer {
		return target.String(), nil
	}
	if !strings.EqualFold(target.Scheme, publicURL.Scheme) || !strings.EqualFold(target.Host, publicURL.Host) {
		return "", errors.New("endpoint is outside the public issuer origin")
	}
	publicPath := strings.TrimRight(publicURL.Path, "/")
	if target.Path != publicPath && !strings.HasPrefix(target.Path, publicPath+"/") {
		return "", errors.New("endpoint is outside the public issuer path")
	}
	rewritten := *target
	rewritten.Scheme = backchannelURL.Scheme
	rewritten.Host = backchannelURL.Host
	rewritten.Path = strings.TrimRight(backchannelURL.Path, "/") + strings.TrimPrefix(target.Path, publicPath)
	rewritten.RawPath = ""
	return rewritten.String(), nil
}

func supportedOIDCAlgorithms(values []string) []string {
	supported := map[string]struct{}{
		oidc.RS256: {}, oidc.RS384: {}, oidc.RS512: {},
		oidc.ES256: {}, oidc.ES384: {}, oidc.ES512: {},
		oidc.PS256: {}, oidc.PS384: {}, oidc.PS512: {}, oidc.EdDSA: {},
	}
	result := make([]string, 0, len(values))
	for _, value := range values {
		if _, ok := supported[value]; ok {
			result = append(result, value)
		}
	}
	return result
}

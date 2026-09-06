package identity

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/go-jose/go-jose/v3"
	josejwt "github.com/go-jose/go-jose/v3/jwt"
	"github.com/termlink/termlink/server/internal/auth"
)

type tokenVerifier struct {
	issuer         string
	audience       string
	allowedParties map[string]struct{}
	keys           map[string]jose.JSONWebKey
	keysMu         sync.RWMutex
	clock          func() time.Time
	accounts       *AccountStore
}

type accessTokenClaims struct {
	josejwt.Claims
	AuthorizedParty string   `json:"azp"`
	TokenType       string   `json:"typ"`
	AuthTime        int64    `json:"auth_time"`
	AMR             []string `json:"amr"`
	ACR             string   `json:"acr"`
	AuthEpoch       int64    `json:"auth_epoch"`
}

func (v *tokenVerifier) Verify(ctx context.Context, raw string) (auth.Identity, error) {
	if v == nil || strings.TrimSpace(raw) == "" {
		return auth.Identity{}, auth.ErrUnauthenticated
	}
	token, err := josejwt.ParseSigned(raw)
	if err != nil || len(token.Headers) != 1 || token.Headers[0].Algorithm != identityKeyAlgorithm {
		return auth.Identity{}, fmt.Errorf("%w: invalid embedded OIDC token", auth.ErrUnauthenticated)
	}
	key, ok := v.verificationKey(ctx, token.Headers[0].KeyID)
	if !ok {
		return auth.Identity{}, fmt.Errorf("%w: unknown embedded OIDC signing key", auth.ErrUnauthenticated)
	}
	var claims accessTokenClaims
	if err = token.Claims(key.Key, &claims); err != nil {
		return auth.Identity{}, fmt.Errorf("%w: embedded OIDC signature", auth.ErrUnauthenticated)
	}
	now := v.clock().UTC()
	if err = claims.ValidateWithLeeway(josejwt.Expected{
		Issuer: v.issuer, Audience: josejwt.Audience{v.audience}, Time: now,
	}, 30*time.Second); err != nil {
		return auth.Identity{}, fmt.Errorf("%w: embedded OIDC claims", auth.ErrUnauthenticated)
	}
	if claims.Subject == "" || claims.ID == "" || claims.TokenType != "Bearer" || claims.AuthEpoch < 1 {
		return auth.Identity{}, fmt.Errorf("%w: incomplete embedded OIDC claims", auth.ErrUnauthenticated)
	}
	if _, allowed := v.allowedParties[claims.AuthorizedParty]; !allowed {
		return auth.Identity{}, fmt.Errorf("%w: unauthorized embedded OIDC party", auth.ErrUnauthenticated)
	}
	account, err := v.accounts.LoadAccount(ctx, claims.Subject)
	if err != nil || !account.Enabled || account.PasswordResetRequired || account.AuthEpoch != claims.AuthEpoch {
		return auth.Identity{}, fmt.Errorf("%w: embedded OIDC account state changed", auth.ErrUnauthenticated)
	}
	identity := auth.Identity{
		Issuer: v.issuer, Subject: claims.Subject, AuthorizedParty: claims.AuthorizedParty,
		TokenID: claims.ID, MFA: embeddedStrongAuthentication(claims.AMR, claims.ACR), ACR: strings.TrimSpace(claims.ACR),
	}
	if claims.AuthTime > 0 {
		identity.AuthenticatedAt = time.Unix(claims.AuthTime, 0).UTC()
	}
	return identity, nil
}

func (v *tokenVerifier) verificationKey(ctx context.Context, keyID string) (jose.JSONWebKey, bool) {
	v.keysMu.RLock()
	key, ok := v.keys[keyID]
	v.keysMu.RUnlock()
	if ok {
		return key, true
	}
	refreshed, err := loadVerificationKeys(ctx, v.accounts.pool, v.clock().UTC())
	if err != nil {
		return jose.JSONWebKey{}, false
	}
	v.keysMu.Lock()
	v.keys = refreshed
	key, ok = v.keys[keyID]
	v.keysMu.Unlock()
	return key, ok
}

func embeddedStrongAuthentication(methods []string, acr string) bool {
	for _, method := range methods {
		switch strings.ToLower(strings.TrimSpace(method)) {
		case "mfa", "otp", "hwk", "webauthn":
			return true
		}
	}
	return strings.EqualFold(strings.TrimSpace(acr), "urn:termlink:acr:mfa")
}

func newTokenVerifier(issuer, audience string, parties []string, keys map[string]jose.JSONWebKey, clock func() time.Time, accounts *AccountStore) (*tokenVerifier, error) {
	if issuer == "" || audience == "" || len(keys) == 0 || accounts == nil {
		return nil, errors.New("embedded token verifier configuration is incomplete")
	}
	allowed := make(map[string]struct{}, len(parties))
	for _, party := range parties {
		if party = strings.TrimSpace(party); party != "" {
			allowed[party] = struct{}{}
		}
	}
	if len(allowed) == 0 {
		return nil, errors.New("embedded token verifier parties are empty")
	}
	return &tokenVerifier{issuer: issuer, audience: audience, allowedParties: allowed, keys: keys, clock: clock, accounts: accounts}, nil
}

package auth

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/coreos/go-oidc/v3/oidc/oidctest"
)

const (
	testOIDCKeyID    = "termlink-test-key"
	testOIDCAudience = "termlink-server"
	testAndroidParty = "termlink-android"
	testWindowsParty = "termlink-windows"
	testSubject      = "keycloak-user-123"
	testTokenID      = "access-token-123"
)

type accessTokenClaims struct {
	Issuer          string   `json:"iss"`
	Audience        []string `json:"aud"`
	Subject         string   `json:"sub,omitempty"`
	AuthorizedParty string   `json:"azp,omitempty"`
	TokenType       string   `json:"typ,omitempty"`
	TokenID         string   `json:"jti,omitempty"`
	Expiry          int64    `json:"exp"`
	IssuedAt        int64    `json:"iat"`
}

type oidcVerifierFixture struct {
	issuer       string
	privateKey   *rsa.PrivateKey
	verifier     *OIDCVerifier
	discoveryHit atomic.Int32
	keysHit      atomic.Int32
}

func newOIDCVerifierFixture(t *testing.T) *oidcVerifierFixture {
	t.Helper()
	privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatalf("generate RSA key: %v", err)
	}

	provider := &oidctest.Server{
		PublicKeys: []oidctest.PublicKey{{
			PublicKey: privateKey.Public(),
			KeyID:     testOIDCKeyID,
			Algorithm: oidc.RS256,
		}},
	}
	fixture := &oidcVerifierFixture{privateKey: privateKey}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/.well-known/openid-configuration":
			fixture.discoveryHit.Add(1)
		case "/keys":
			fixture.keysHit.Add(1)
		}
		provider.ServeHTTP(w, r)
	}))
	t.Cleanup(server.Close)
	provider.SetIssuer(server.URL)
	fixture.issuer = server.URL

	verifier, err := NewOIDCVerifier(
		context.Background(),
		fixture.issuer,
		testOIDCAudience,
		[]string{testAndroidParty, testWindowsParty},
	)
	if err != nil {
		t.Fatalf("NewOIDCVerifier() error: %v", err)
	}
	fixture.verifier = verifier
	if fixture.discoveryHit.Load() == 0 {
		t.Fatal("OIDC discovery endpoint was not requested")
	}
	return fixture
}

func (f *oidcVerifierFixture) signToken(t *testing.T, claims accessTokenClaims) string {
	t.Helper()
	if claims.Issuer == "" {
		claims.Issuer = f.issuer
	}
	if claims.Audience == nil {
		claims.Audience = []string{testOIDCAudience}
	}
	if claims.Expiry == 0 {
		claims.Expiry = time.Now().Add(10 * time.Minute).Unix()
	}
	if claims.IssuedAt == 0 {
		claims.IssuedAt = time.Now().Unix()
	}
	rawClaims, err := json.Marshal(claims)
	if err != nil {
		t.Fatalf("marshal claims: %v", err)
	}
	return oidctest.SignIDToken(f.privateKey, testOIDCKeyID, oidc.RS256, string(rawClaims))
}

func TestOIDCVerifierValidatesKeycloakAccessTokens(t *testing.T) {
	fixture := newOIDCVerifierFixture(t)
	valid := fixture.signToken(t, accessTokenClaims{
		Subject:         testSubject,
		AuthorizedParty: testAndroidParty,
		TokenType:       "Bearer",
		TokenID:         testTokenID,
	})

	identity, err := fixture.verifier.Verify(context.Background(), valid)
	if err != nil {
		t.Fatalf("Verify(valid access token) error: %v", err)
	}
	want := Identity{
		Issuer:          fixture.issuer,
		Subject:         testSubject,
		AuthorizedParty: testAndroidParty,
		TokenID:         testTokenID,
	}
	if identity != want {
		t.Fatalf("Verify(valid access token) identity = %#v, want %#v", identity, want)
	}
	if fixture.keysHit.Load() == 0 {
		t.Fatal("JWKS endpoint was not requested")
	}
}

func TestOIDCVerifierRejectsInvalidAccessTokens(t *testing.T) {
	fixture := newOIDCVerifierFixture(t)
	now := time.Now()
	base := accessTokenClaims{
		Subject:         testSubject,
		AuthorizedParty: testAndroidParty,
		TokenType:       "Bearer",
		TokenID:         testTokenID,
		Expiry:          now.Add(10 * time.Minute).Unix(),
		IssuedAt:        now.Unix(),
	}

	tests := []struct {
		name   string
		claims accessTokenClaims
	}{
		{
			name: "missing azp",
			claims: func() accessTokenClaims {
				claims := base
				claims.AuthorizedParty = ""
				return claims
			}(),
		},
		{
			name: "unauthorized azp",
			claims: func() accessTokenClaims {
				claims := base
				claims.AuthorizedParty = "unknown-client"
				return claims
			}(),
		},
		{
			name: "ID token typ",
			claims: func() accessTokenClaims {
				claims := base
				claims.TokenType = "ID"
				return claims
			}(),
		},
		{
			name: "missing jti",
			claims: func() accessTokenClaims {
				claims := base
				claims.TokenID = ""
				return claims
			}(),
		},
		{
			name: "missing sub",
			claims: func() accessTokenClaims {
				claims := base
				claims.Subject = ""
				return claims
			}(),
		},
		{
			name: "wrong audience",
			claims: func() accessTokenClaims {
				claims := base
				claims.Audience = []string{"another-client"}
				return claims
			}(),
		},
		{
			name: "expired token",
			claims: func() accessTokenClaims {
				claims := base
				claims.Expiry = now.Add(-time.Minute).Unix()
				return claims
			}(),
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			identity, err := fixture.verifier.Verify(context.Background(), fixture.signToken(t, tt.claims))
			if err == nil {
				t.Fatalf("Verify(%s) unexpectedly succeeded with identity %#v", tt.name, identity)
			}
			if !errors.Is(err, ErrUnauthenticated) {
				t.Fatalf("Verify(%s) error = %v, want ErrUnauthenticated", tt.name, err)
			}
			if identity != (Identity{}) {
				t.Fatalf("Verify(%s) identity = %#v, want zero identity", tt.name, identity)
			}
		})
	}
}

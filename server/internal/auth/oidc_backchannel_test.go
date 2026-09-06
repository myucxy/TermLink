package auth

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/coreos/go-oidc/v3/oidc/oidctest"
)

func TestOIDCBackchannelUsesPrivateDiscoveryAndJWKSWithPublicIssuerValidation(t *testing.T) {
	privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	const publicIssuer = "https://public.example:8910/realms/termlink"
	provider := &oidctest.Server{PublicKeys: []oidctest.PublicKey{{
		PublicKey: privateKey.Public(), KeyID: testOIDCKeyID, Algorithm: oidc.RS256,
	}}}
	provider.SetIssuer(publicIssuer)
	var discoveryHits, keysHits atomic.Int32
	backchannel := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/realms/termlink/.well-known/openid-configuration":
			discoveryHits.Add(1)
			_ = json.NewEncoder(w).Encode(map[string]any{
				"issuer":                                publicIssuer,
				"authorization_endpoint":                publicIssuer + "/protocol/openid-connect/auth",
				"token_endpoint":                        publicIssuer + "/protocol/openid-connect/token",
				"jwks_uri":                              publicIssuer + "/protocol/openid-connect/certs",
				"end_session_endpoint":                  publicIssuer + "/protocol/openid-connect/logout",
				"id_token_signing_alg_values_supported": []string{oidc.RS256},
			})
		case "/realms/termlink/protocol/openid-connect/certs":
			keysHits.Add(1)
			cloned := r.Clone(r.Context())
			cloned.URL.Path = "/keys"
			provider.ServeHTTP(w, cloned)
		default:
			http.NotFound(w, r)
		}
	}))
	defer backchannel.Close()

	verifier, err := NewOIDCVerifierWithBackchannel(context.Background(), publicIssuer,
		backchannel.URL+"/realms/termlink", testOIDCAudience, []string{testAndroidParty})
	if err != nil {
		t.Fatal(err)
	}
	token := oidctest.SignIDToken(privateKey, testOIDCKeyID, oidc.RS256, string(mustJSON(t, accessTokenClaims{
		Issuer: publicIssuer, Audience: []string{testOIDCAudience}, Subject: testSubject,
		AuthorizedParty: testAndroidParty, TokenType: "Bearer", TokenID: testTokenID,
		Expiry: time.Now().Add(time.Minute).Unix(), IssuedAt: time.Now().Unix(),
	})))
	identity, err := verifier.Verify(context.Background(), token)
	if err != nil || identity.Issuer != publicIssuer {
		t.Fatalf("identity=%+v err=%v", identity, err)
	}
	if discoveryHits.Load() != 1 || keysHits.Load() != 1 {
		t.Fatalf("backchannel hits discovery=%d keys=%d", discoveryHits.Load(), keysHits.Load())
	}
}

func mustJSON(t *testing.T, value any) []byte {
	t.Helper()
	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestOIDCBackchannelRejectsMismatchedReportedIssuer(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_ = json.NewEncoder(w).Encode(map[string]any{
			"issuer":                 "https://attacker.example/realms/termlink",
			"authorization_endpoint": "https://attacker.example/auth",
			"token_endpoint":         "https://attacker.example/token",
			"jwks_uri":               "https://attacker.example/keys",
		})
	}))
	defer server.Close()
	if _, err := DiscoverOIDC(context.Background(), "https://public.example/realms/termlink", server.URL+"/realms/termlink"); err == nil {
		t.Fatal("mismatched discovery issuer was accepted")
	}
}

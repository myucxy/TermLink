package auth

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"

	"github.com/google/uuid"
)

type fixedVerifier struct{ identity Identity }

func (v fixedVerifier) Verify(context.Context, string) (Identity, error) { return v.identity, nil }

type fixedDevices struct{ device Device }

func (d fixedDevices) LoadDeviceForSubject(context.Context, [32]byte, uuid.UUID) (Device, error) {
	return d.device, nil
}

type memoryNonces struct{ seen map[string]bool }

func (n *memoryNonces) Consume(_ context.Context, scope, nonce string, _ time.Duration) (bool, error) {
	key := scope + ":" + nonce
	if n.seen[key] {
		return false, nil
	}
	n.seen[key] = true
	return true, nil
}

func TestDeviceProofAuthenticatesAndRejectsReplay(t *testing.T) {
	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Unix(1_800_000_000, 0)
	device := Device{ID: uuid.New(), UserID: uuid.New(), SigningPublicKey: publicKey, TrustState: "TRUSTED"}
	nonces := &memoryNonces{seen: map[string]bool{}}
	middleware := Middleware{
		Tokens: fixedVerifier{Identity{Issuer: "https://issuer.test", Subject: "subject"}}, Devices: fixedDevices{device},
		Nonces: nonces, SubjectKey: make([]byte, 32), Clock: func() time.Time { return now },
	}
	req := httptest.NewRequest(http.MethodPost, "https://server.test/v1/test?b=2", nil)
	req.Header.Set("Authorization", "Bearer token")
	req.Header.Set("X-TermLink-Device", device.ID.String())
	timestamp := strconv.FormatInt(now.UnixMilli(), 10)
	nonce := base64.RawURLEncoding.EncodeToString([]byte("0123456789abcdef"))
	req.Header.Set("X-TermLink-Timestamp", timestamp)
	req.Header.Set("X-TermLink-Nonce", nonce)
	empty := sha256.Sum256(nil)
	signature := ed25519.Sign(privateKey, CanonicalRequest(http.MethodPost, "/v1/test?b=2", timestamp, nonce, empty))
	req.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(signature))

	if _, err := middleware.VerifyRequest(req); err != nil {
		t.Fatalf("VerifyRequest() error = %v", err)
	}
	if _, err := middleware.VerifyRequest(req); err == nil {
		t.Fatal("replayed proof was accepted")
	}
}

func TestSubjectHashSeparatesIssuerAndSubject(t *testing.T) {
	key := []byte("01234567890123456789012345678901")
	a := SubjectHash(key, Identity{Issuer: "a", Subject: "bc"})
	b := SubjectHash(key, Identity{Issuer: "ab", Subject: "c"})
	if a == b {
		t.Fatal("domain-separated subjects collided")
	}
}

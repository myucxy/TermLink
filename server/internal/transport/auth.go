package transport

import (
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
)

const (
	signalProofDomain = "TERMLINK-TRANSPORT-SIGNAL-PROOF-V1"
	signalProofWindow = 60 * time.Second
)

type signalPrincipalKey struct{}

// AuthenticateSignals reuses the production OIDC verifier, registered-device
// loader, device signing key, trust state and nonce store while keeping the
// transport-control proof and nonce domain independent from realtime/API
// requests. A signal proof cannot be replayed as a realtime connection proof.
func AuthenticateSignals(m auth.Middleware, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		principal, err := verifySignalRequest(m, r)
		if err != nil {
			writeTransportError(w, http.StatusUnauthorized, "UNAUTHENTICATED")
			return
		}
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), signalPrincipalKey{}, principal)))
	})
}

func signalPrincipalFromContext(ctx context.Context) (auth.Principal, bool) {
	principal, ok := ctx.Value(signalPrincipalKey{}).(auth.Principal)
	return principal, ok
}

func verifySignalRequest(m auth.Middleware, r *http.Request) (auth.Principal, error) {
	if m.Tokens == nil || m.Devices == nil || m.Nonces == nil || len(m.SubjectKey) < 32 || r.Method != http.MethodGet || r.URL.EscapedPath() != "/v1/transport/signals" || r.URL.RawQuery != "" {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	const bearerPrefix = "Bearer "
	header := r.Header.Get("Authorization")
	if !strings.HasPrefix(header, bearerPrefix) || strings.TrimSpace(strings.TrimPrefix(header, bearerPrefix)) == "" {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	identity, err := m.Tokens.Verify(r.Context(), strings.TrimSpace(strings.TrimPrefix(header, bearerPrefix)))
	if err != nil {
		return auth.Principal{}, err
	}
	deviceID, err := uuid.Parse(r.Header.Get("X-TermLink-Device"))
	if err != nil {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	device, err := m.Devices.LoadDeviceForSubject(r.Context(), auth.SubjectHash(m.SubjectKey, identity), deviceID)
	if err != nil || device.TrustState != "TRUSTED" {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	timestampText := r.Header.Get("X-TermLink-Timestamp")
	timestampMS, err := strconv.ParseInt(timestampText, 10, 64)
	if err != nil {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	now := time.Now()
	if m.Clock != nil {
		now = m.Clock()
	}
	delta := now.Sub(time.UnixMilli(timestampMS))
	if delta < -signalProofWindow || delta > signalProofWindow {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	nonceText := r.Header.Get("X-TermLink-Nonce")
	nonce, err := base64.RawURLEncoding.DecodeString(nonceText)
	if err != nil || len(nonce) < 16 || len(nonce) > 32 {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	signature, err := base64.RawURLEncoding.DecodeString(r.Header.Get("X-TermLink-Signature"))
	if err != nil || len(signature) != ed25519.SignatureSize {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	bodyHash := sha256.Sum256(nil)
	canonical := SignalCanonicalRequest(timestampText, nonceText, bodyHash)
	if !ed25519.Verify(device.SigningPublicKey, canonical, signature) {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	consumed, err := m.Nonces.Consume(r.Context(), "transport-signal:"+device.ID.String(), nonceText, 2*signalProofWindow)
	if err != nil || !consumed {
		return auth.Principal{}, auth.ErrUnauthenticated
	}
	return auth.Principal{Identity: identity, UserID: device.UserID, Device: device}, nil
}

func SignalCanonicalRequest(timestamp, nonce string, bodyHash [32]byte) []byte {
	return []byte(strings.Join([]string{
		signalProofDomain, http.MethodGet, "/v1/transport/signals", timestamp, nonce,
		fmt.Sprintf("%x", bodyHash[:]),
	}, "\n"))
}

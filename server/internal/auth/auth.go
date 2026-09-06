// Package auth authenticates OIDC users and verifies per-request device proof.
package auth

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/google/uuid"
)

const (
	proofDomain  = "TERMLINK-DEVICE-PROOF-V1"
	maxBodyBytes = 1 << 20
	proofWindow  = 60 * time.Second
)

var (
	ErrUnauthenticated = errors.New("authentication failed")
	ErrDevicePending   = errors.New("device approval required")
	ErrDeviceRevoked   = errors.New("device revoked")
)

type Identity struct {
	Issuer          string
	Subject         string
	AuthorizedParty string
	TokenID         string
	AuthenticatedAt time.Time
	MFA             bool
	ACR             string
}

type TokenVerifier interface {
	Verify(context.Context, string) (Identity, error)
}

type OIDCVerifier struct {
	issuer         string
	verifier       *oidc.IDTokenVerifier
	allowedParties map[string]struct{}
}

func NewOIDCVerifier(ctx context.Context, issuer, clientID string, allowedParties []string) (*OIDCVerifier, error) {
	return NewOIDCVerifierWithBackchannel(ctx, issuer, issuer, clientID, allowedParties)
}

func NewOIDCVerifierWithBackchannel(ctx context.Context, issuer, backchannelIssuer, clientID string, allowedParties []string) (*OIDCVerifier, error) {
	discovery, err := DiscoverOIDC(ctx, issuer, backchannelIssuer)
	if err != nil {
		return nil, err
	}
	parties := make(map[string]struct{}, len(allowedParties))
	for _, party := range allowedParties {
		party = strings.TrimSpace(party)
		if party == "" {
			return nil, errors.New("OIDC allowed party is empty")
		}
		parties[party] = struct{}{}
	}
	if len(parties) == 0 {
		return nil, errors.New("OIDC allowed parties are required")
	}
	return &OIDCVerifier{
		issuer:         discovery.Issuer,
		verifier:       discovery.Verifier(clientID),
		allowedParties: parties,
	}, nil
}

func (v *OIDCVerifier) Verify(ctx context.Context, raw string) (Identity, error) {
	token, err := v.verifier.Verify(ctx, raw)
	if err != nil {
		return Identity{}, fmt.Errorf("%w: invalid OIDC token", ErrUnauthenticated)
	}
	var claims struct {
		Subject         string   `json:"sub"`
		AuthorizedParty string   `json:"azp"`
		TokenType       string   `json:"typ"`
		TokenID         string   `json:"jti"`
		AuthTime        int64    `json:"auth_time"`
		AMR             []string `json:"amr"`
		ACR             string   `json:"acr"`
	}
	if err := token.Claims(&claims); err != nil || strings.TrimSpace(claims.Subject) == "" {
		return Identity{}, fmt.Errorf("%w: missing subject", ErrUnauthenticated)
	}
	if claims.TokenType != "Bearer" {
		return Identity{}, fmt.Errorf("%w: token is not an access token", ErrUnauthenticated)
	}
	if _, allowed := v.allowedParties[claims.AuthorizedParty]; !allowed {
		return Identity{}, fmt.Errorf("%w: unauthorized OIDC party", ErrUnauthenticated)
	}
	if strings.TrimSpace(claims.TokenID) == "" {
		return Identity{}, fmt.Errorf("%w: missing token identifier", ErrUnauthenticated)
	}
	identity := Identity{
		Issuer:          v.issuer,
		Subject:         claims.Subject,
		AuthorizedParty: claims.AuthorizedParty,
		TokenID:         claims.TokenID,
		MFA:             isStrongAuthentication(claims.AMR, claims.ACR),
		ACR:             strings.TrimSpace(claims.ACR),
	}
	if claims.AuthTime > 0 {
		identity.AuthenticatedAt = time.Unix(claims.AuthTime, 0).UTC()
	}
	return identity, nil
}

func isStrongAuthentication(amr []string, acr string) bool {
	for _, method := range amr {
		switch strings.ToLower(strings.TrimSpace(method)) {
		case "mfa", "otp", "hwk", "webauthn":
			return true
		}
	}
	switch strings.ToLower(strings.TrimSpace(acr)) {
	case "urn:termlink:acr:mfa", "urn:mace:incommon:iap:silver", "multi-factor":
		return true
	default:
		return false
	}
}

// SubjectHash prevents raw OIDC subjects from being stored in PostgreSQL.
func SubjectHash(key []byte, identity Identity) [32]byte {
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte(identity.Issuer))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(identity.Subject))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

type Device struct {
	ID               uuid.UUID
	UserID           uuid.UUID
	Type             string
	SigningPublicKey ed25519.PublicKey
	TrustState       string
}

type DeviceLoader interface {
	LoadDeviceForSubject(context.Context, [32]byte, uuid.UUID) (Device, error)
}

type NonceConsumer interface {
	Consume(context.Context, string, string, time.Duration) (bool, error)
}

type Principal struct {
	Identity Identity
	UserID   uuid.UUID
	Device   Device
}

type principalKey struct{}

func PrincipalFromContext(ctx context.Context) (Principal, bool) {
	principal, ok := ctx.Value(principalKey{}).(Principal)
	return principal, ok
}

type Middleware struct {
	Tokens     TokenVerifier
	Devices    DeviceLoader
	Nonces     NonceConsumer
	SubjectKey []byte
	Clock      func() time.Time
}

func (m Middleware) Authenticate(next http.Handler) http.Handler {
	return m.authenticate(next, true)
}

// AuthenticateRegistered accepts a cryptographically verified PENDING device.
// It is limited to pairing/status endpoints; normal product routes use
// Authenticate and therefore require TRUSTED.
func (m Middleware) AuthenticateRegistered(next http.Handler) http.Handler {
	return m.authenticate(next, false)
}

func (m Middleware) authenticate(next http.Handler, requireTrusted bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		principal, err := m.VerifyRequest(r)
		if err == nil && requireTrusted && principal.Device.TrustState != "TRUSTED" {
			err = ErrDevicePending
		}
		if err != nil {
			status := http.StatusUnauthorized
			if errors.Is(err, ErrDevicePending) {
				status = http.StatusForbidden
			}
			slog.Warn("request authentication rejected",
				"method", r.Method,
				// Query values participate in the signed request target, but they
				// may carry OAuth codes, tokens, or user-controlled data and must
				// never be copied into authentication logs.
				"path", r.URL.EscapedPath(),
				"reason_class", authenticationFailureClass(err))
			writeAuthError(w, status, err)
			return
		}
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), principalKey{}, principal)))
	})
}

func authenticationFailureClass(err error) string {
	switch {
	case errors.Is(err, ErrDevicePending):
		return "DEVICE_PENDING"
	case errors.Is(err, ErrDeviceRevoked):
		return "DEVICE_REVOKED"
	case errors.Is(err, ErrUnauthenticated):
		return "UNAUTHENTICATED"
	default:
		return "AUTHENTICATION_REJECTED"
	}
}

func (m Middleware) VerifyRequest(r *http.Request) (Principal, error) {
	if m.Tokens == nil || m.Devices == nil || m.Nonces == nil || len(m.SubjectKey) < 32 {
		return Principal{}, fmt.Errorf("%w: verifier unavailable", ErrUnauthenticated)
	}
	rawToken, err := bearerToken(r.Header.Get("Authorization"))
	if err != nil {
		return Principal{}, err
	}
	identity, err := m.Tokens.Verify(r.Context(), rawToken)
	if err != nil {
		return Principal{}, err
	}
	deviceID, err := uuid.Parse(r.Header.Get("X-TermLink-Device"))
	if err != nil {
		return Principal{}, fmt.Errorf("%w: invalid device", ErrUnauthenticated)
	}
	device, err := m.Devices.LoadDeviceForSubject(r.Context(), SubjectHash(m.SubjectKey, identity), deviceID)
	if err != nil {
		return Principal{}, fmt.Errorf("%w: unknown device", ErrUnauthenticated)
	}
	switch device.TrustState {
	case "TRUSTED", "PENDING":
	case "REVOKED":
		return Principal{}, ErrDeviceRevoked
	default:
		return Principal{}, fmt.Errorf("%w: invalid trust state", ErrUnauthenticated)
	}
	body, err := readAndRestoreBody(r)
	if err != nil {
		return Principal{}, fmt.Errorf("%w: request body", ErrUnauthenticated)
	}
	if err := m.verifyProof(r, device, body); err != nil {
		return Principal{}, err
	}
	return Principal{Identity: identity, UserID: device.UserID, Device: device}, nil
}

func (m Middleware) verifyProof(r *http.Request, device Device, body []byte) error {
	timestampText := r.Header.Get("X-TermLink-Timestamp")
	timestampMS, err := strconv.ParseInt(timestampText, 10, 64)
	if err != nil {
		return fmt.Errorf("%w: invalid timestamp", ErrUnauthenticated)
	}
	now := time.Now()
	if m.Clock != nil {
		now = m.Clock()
	}
	delta := now.Sub(time.UnixMilli(timestampMS))
	if delta < -proofWindow || delta > proofWindow {
		return fmt.Errorf("%w: stale proof", ErrUnauthenticated)
	}
	nonce := r.Header.Get("X-TermLink-Nonce")
	nonceBytes, err := base64.RawURLEncoding.DecodeString(nonce)
	if err != nil || len(nonceBytes) < 16 || len(nonceBytes) > 32 {
		return fmt.Errorf("%w: invalid nonce", ErrUnauthenticated)
	}
	signature, err := base64.RawURLEncoding.DecodeString(r.Header.Get("X-TermLink-Signature"))
	if err != nil || len(signature) != ed25519.SignatureSize {
		return fmt.Errorf("%w: invalid signature", ErrUnauthenticated)
	}
	hash := sha256.Sum256(body)
	canonical := CanonicalRequest(r.Method, requestTarget(r), timestampText, nonce, hash)
	if !ed25519.Verify(device.SigningPublicKey, canonical, signature) {
		return fmt.Errorf("%w: invalid signature", ErrUnauthenticated)
	}
	consumed, err := m.Nonces.Consume(r.Context(), device.ID.String(), nonce, 2*proofWindow)
	if err != nil {
		return fmt.Errorf("%w: nonce store unavailable", ErrUnauthenticated)
	}
	if !consumed {
		return fmt.Errorf("%w: replayed proof", ErrUnauthenticated)
	}
	return nil
}

func CanonicalRequest(method, target, timestamp, nonce string, bodyHash [32]byte) []byte {
	return []byte(strings.Join([]string{
		proofDomain, strings.ToUpper(method), target, timestamp, nonce,
		hex.EncodeToString(bodyHash[:]),
	}, "\n"))
}

func bearerToken(header string) (string, error) {
	const prefix = "Bearer "
	if !strings.HasPrefix(header, prefix) || strings.TrimSpace(strings.TrimPrefix(header, prefix)) == "" {
		return "", fmt.Errorf("%w: bearer token required", ErrUnauthenticated)
	}
	return strings.TrimSpace(strings.TrimPrefix(header, prefix)), nil
}

func requestTarget(r *http.Request) string {
	target := r.URL.EscapedPath()
	if r.URL.RawQuery != "" {
		target += "?" + r.URL.RawQuery
	}
	return target
}

func readAndRestoreBody(r *http.Request) ([]byte, error) {
	if r.Body == nil {
		return nil, nil
	}
	data, err := io.ReadAll(io.LimitReader(r.Body, maxBodyBytes+1))
	_ = r.Body.Close()
	if err != nil || len(data) > maxBodyBytes {
		return nil, errors.New("body exceeds limit")
	}
	r.Body = io.NopCloser(bytes.NewReader(data))
	return data, nil
}

func writeAuthError(w http.ResponseWriter, status int, err error) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	code := "UNAUTHENTICATED"
	if errors.Is(err, ErrDevicePending) {
		code = "DEVICE_PENDING"
	} else if errors.Is(err, ErrDeviceRevoked) {
		code = "DEVICE_REVOKED"
	}
	_, _ = fmt.Fprintf(w, `{"error":{"code":%q,"message_key":%q}}`, code, "auth."+strings.ToLower(code))
}

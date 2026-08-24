package e2eserver

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
)

var e2eAccountID = uuid.MustParse("00000000-0000-0000-0000-000000000001")

type loginRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

// login implements the deliberately small development identity flow used by
// the local E2E server. It issues a process-local bearer token; production
// OIDC, account persistence, and refresh tokens remain outside this server's
// scope.
func (s *Server) login(w http.ResponseWriter, r *http.Request) {
	var input loginRequest
	if err := decodeJSON(w, r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	username := strings.TrimSpace(input.Username)
	// Bound input before comparing it with configured credentials. This avoids
	// accepting accidental whitespace or unbounded request bodies while keeping
	// the failure response generic.
	if username == "" || len(username) > 320 || len(input.Password) == 0 || len(input.Password) > 4096 {
		writeError(w, r, http.StatusUnauthorized, "INVALID_CREDENTIALS")
		return
	}
	usernameMatch := subtle.ConstantTimeCompare([]byte(username), []byte(s.developmentUsername)) == 1
	passwordMatch := subtle.ConstantTimeCompare([]byte(input.Password), []byte(s.developmentPassword)) == 1
	if !usernameMatch || !passwordMatch {
		writeError(w, r, http.StatusUnauthorized, "INVALID_CREDENTIALS")
		return
	}

	token, err := newBearerToken()
	if err != nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL_ERROR")
		return
	}
	now := s.now().UTC()
	expiresAt := now.Add(s.developmentTokenTTL)
	s.mu.Lock()
	s.cleanupIssuedTokensLocked(now)
	s.issuedTokens[token] = issuedToken{expiresAt: expiresAt}
	s.mu.Unlock()

	expiresIn := int64(s.developmentTokenTTL / time.Second)
	if expiresIn < 1 {
		expiresIn = 1
	}
	w.Header().Set("Cache-Control", "no-store")
	writeJSON(w, http.StatusOK, map[string]any{
		"access_token":  token,
		"token_type":    "Bearer",
		"expires_in":    expiresIn,
		"user_id":       e2eAccountID,
		"account_label": s.developmentAccountLabel,
		"account": map[string]any{
			"id":    e2eAccountID,
			"label": s.developmentAccountLabel,
		},
	})
}

type registerDeviceRequest struct {
	ID                string `json:"id"`
	Type              string `json:"type"`
	Name              string `json:"name"`
	SigningPublicKey  string `json:"signing_public_key"`
	ExchangePublicKey string `json:"exchange_public_key"`
	KeyID             string `json:"key_id"`
	TimestampMS       int64  `json:"timestamp_ms"`
	Nonce             string `json:"nonce"`
	Signature         string `json:"signature"`
}

func (s *Server) registerDevice(w http.ResponseWriter, r *http.Request) {
	var input registerDeviceRequest
	if err := decodeJSON(w, r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	deviceID, err := uuid.Parse(input.ID)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_ID")
		return
	}
	if input.Type != "WINDOWS_PC" && input.Type != "ANDROID" {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_TYPE")
		return
	}
	input.Name = strings.TrimSpace(input.Name)
	if len(input.Name) < 1 || len(input.Name) > 128 || len(input.KeyID) < 8 || len(input.KeyID) > 128 {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
		return
	}
	now := s.now().UTC()
	if delta := now.Sub(time.UnixMilli(input.TimestampMS)); delta < -proofWindow || delta > proofWindow {
		writeError(w, r, http.StatusUnauthorized, "STALE_PROOF")
		return
	}
	signingKey, err := base64.RawURLEncoding.DecodeString(input.SigningPublicKey)
	if err != nil || len(signingKey) != ed25519.PublicKeySize {
		writeError(w, r, http.StatusBadRequest, "INVALID_PUBLIC_KEY")
		return
	}
	exchangeKey, err := base64.RawURLEncoding.DecodeString(input.ExchangePublicKey)
	if err != nil || len(exchangeKey) != 32 {
		writeError(w, r, http.StatusBadRequest, "INVALID_PUBLIC_KEY")
		return
	}
	nonce, err := base64.RawURLEncoding.DecodeString(input.Nonce)
	if err != nil || len(nonce) < 16 || len(nonce) > 32 {
		writeError(w, r, http.StatusBadRequest, "INVALID_NONCE")
		return
	}
	signature, err := base64.RawURLEncoding.DecodeString(input.Signature)
	if err != nil || len(signature) != ed25519.SignatureSize {
		writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
		return
	}
	canonical := strings.Join([]string{
		"TERMLINK-DEVICE-REGISTER-V1", deviceID.String(), input.Type, input.Name,
		input.SigningPublicKey, input.ExchangePublicKey, input.KeyID,
		strconv.FormatInt(input.TimestampMS, 10), input.Nonce,
	}, "\n")
	if !ed25519.Verify(ed25519.PublicKey(signingKey), []byte(canonical), signature) {
		writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
		return
	}
	if !s.consumeNonce("register:"+deviceID.String(), input.Nonce, now) {
		writeError(w, r, http.StatusUnauthorized, "REPLAYED_PROOF")
		return
	}

	device := Device{
		ID: deviceID, Type: input.Type, Name: input.Name, KeyID: input.KeyID,
		SigningPublicKey:  append([]byte(nil), signingKey...),
		ExchangePublicKey: append([]byte(nil), exchangeKey...),
		TrustState:        "TRUSTED", CreatedAt: now,
	}
	status := http.StatusCreated
	bootstrap := false
	s.mu.Lock()
	if existing, exists := s.devices[deviceID]; exists {
		if existing.Type != device.Type || existing.Name != device.Name || existing.KeyID != device.KeyID ||
			!bytes.Equal(existing.SigningPublicKey, device.SigningPublicKey) ||
			!bytes.Equal(existing.ExchangePublicKey, device.ExchangePublicKey) {
			s.mu.Unlock()
			writeError(w, r, http.StatusConflict, "DEVICE_ALREADY_EXISTS")
			return
		}
		device = existing
		status = http.StatusOK
	} else {
		bootstrap = len(s.devices) == 0 && device.Type == "WINDOWS_PC"
		s.devices[deviceID] = device
	}
	s.mu.Unlock()
	writeJSON(w, status, map[string]any{"user_id": e2eAccountID, "device": device, "bootstrap": bootstrap})
}

func (s *Server) authenticate(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !s.validBearer(r.Header.Get("Authorization")) {
			writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED")
			return
		}
		body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
		if err != nil || len(body) > maxJSONBody {
			writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST")
			return
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		deviceID, err := uuid.Parse(r.Header.Get("X-TermLink-Device"))
		if err != nil {
			writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
			return
		}
		s.mu.RLock()
		device, exists := s.devices[deviceID]
		s.mu.RUnlock()
		if !exists {
			writeError(w, r, http.StatusUnauthorized, "UNKNOWN_DEVICE")
			return
		}
		timestampText := r.Header.Get("X-TermLink-Timestamp")
		timestampMS, err := strconv.ParseInt(timestampText, 10, 64)
		if err != nil {
			writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
			return
		}
		now := s.now().UTC()
		if delta := now.Sub(time.UnixMilli(timestampMS)); delta < -proofWindow || delta > proofWindow {
			writeError(w, r, http.StatusUnauthorized, "STALE_PROOF")
			return
		}
		nonceText := r.Header.Get("X-TermLink-Nonce")
		nonce, err := base64.RawURLEncoding.DecodeString(nonceText)
		if err != nil || len(nonce) < 16 || len(nonce) > 32 {
			writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
			return
		}
		signature, err := base64.RawURLEncoding.DecodeString(r.Header.Get("X-TermLink-Signature"))
		if err != nil || len(signature) != ed25519.SignatureSize {
			writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
			return
		}
		digest := sha256.Sum256(body)
		canonical := strings.Join([]string{
			"TERMLINK-DEVICE-PROOF-V1", strings.ToUpper(r.Method), r.URL.RequestURI(),
			timestampText, nonceText, hex.EncodeToString(digest[:]),
		}, "\n")
		if !ed25519.Verify(ed25519.PublicKey(device.SigningPublicKey), []byte(canonical), signature) {
			writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF")
			return
		}
		if !s.consumeNonce(deviceID.String(), nonceText, now) {
			writeError(w, r, http.StatusUnauthorized, "REPLAYED_PROOF")
			return
		}
		ctx := contextWithPrincipal(r, deviceID)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

func contextWithPrincipal(r *http.Request, deviceID uuid.UUID) context.Context {
	return context.WithValue(r.Context(), principalContextKey{}, deviceID)
}

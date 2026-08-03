// Package api exposes the production metadata API. Terminal content is never
// accepted by these handlers.
package api

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/billing"
	"github.com/termlink/termlink/server/internal/history"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/store"
)

const maxJSONBody = 1 << 20

const pairingCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

type API struct {
	Store        *store.Postgres
	Valkey       *kv.Valkey
	Tokens       auth.TokenVerifier
	Auth         auth.Middleware
	SubjectKey   []byte
	TokenKey     []byte
	History      HistoryObjectStore
	Billing      *billing.Registry
	ClientConfig ClientConfiguration
	Clock        func() time.Time
}

type ClientConfiguration struct {
	OIDCIssuer   string
	OIDCClientID string
	RedirectURI  string
}

type HistoryObjectStore interface {
	PresignPut(context.Context, string, int64, []byte, time.Duration) (history.PresignedRequest, error)
	PresignGet(context.Context, string, time.Duration) (history.PresignedRequest, error)
	VerifyCiphertext(context.Context, string, int64, []byte) error
	Delete(context.Context, string) error
}

func (a *API) Handler() http.Handler {
	public := http.NewServeMux()
	public.HandleFunc("POST /v1/devices/register", a.registerDevice)
	public.HandleFunc("GET /v1/billing/plans", a.listPlans)
	public.HandleFunc("GET /v1/client-config", a.getClientConfiguration)
	registered := http.NewServeMux()
	registered.HandleFunc("POST /v1/pairing/challenges/{id}/complete", a.completePairingChallenge)
	registered.HandleFunc("GET /v1/devices/self", a.getCurrentDevice)

	trusted := http.NewServeMux()
	trusted.HandleFunc("GET /v1/devices", a.listDevices)
	trusted.HandleFunc("POST /v1/pairing/challenges", a.createPairingChallenge)
	trusted.HandleFunc("POST /v1/devices/{id}/revoke", a.revokeDevice)
	trusted.HandleFunc("GET /v1/pcs", a.listPCs)
	trusted.HandleFunc("PUT /v1/pcs/{id}", a.upsertPC)
	trusted.HandleFunc("POST /v1/pcs/{id}/grants", a.putGrant)
	trusted.HandleFunc("GET /v1/pcs/{id}/sessions", a.listSessions)
	trusted.HandleFunc("PUT /v1/sessions/{id}", a.upsertSession)
	trusted.HandleFunc("DELETE /v1/sessions/{id}", a.deleteSession)
	trusted.HandleFunc("POST /v1/sessions/{id}/subscribe", a.subscribeSession)
	trusted.HandleFunc("POST /v1/sessions/{id}/lease", a.acquireLease)
	trusted.HandleFunc("GET /v1/sessions/{id}/lease", a.getLease)
	trusted.HandleFunc("DELETE /v1/sessions/{id}/lease", a.forceReleaseLease)
	trusted.HandleFunc("POST /v1/sessions/{id}/lease/{leaseId}/renew", a.renewLease)
	trusted.HandleFunc("DELETE /v1/sessions/{id}/lease/{leaseId}", a.releaseLease)
	trusted.HandleFunc("GET /v1/sessions/{id}/history", a.listHistory)
	trusted.HandleFunc("POST /v1/sessions/{id}/history/uploads", a.createHistoryUpload)
	trusted.HandleFunc("POST /v1/sessions/{id}/history/uploads/{uploadId}/commit", a.commitHistoryUpload)
	trusted.HandleFunc("GET /v1/audit-events", a.listAudit)
	trusted.HandleFunc("GET /v1/account/entitlements", a.getEntitlement)
	trusted.HandleFunc("POST /v1/billing/google/verify", a.verifyGooglePurchase)
	trusted.HandleFunc("POST /v1/billing/restore", a.restorePurchase)
	trusted.HandleFunc("POST /v1/billing/web/checkout", a.createWebCheckout)
	trusted.HandleFunc("POST /v1/admin/billing/plans", a.createPlanDraft)
	trusted.HandleFunc("PUT /v1/admin/billing/plans/{id}", a.updatePlanDraft)
	trusted.HandleFunc("POST /v1/admin/billing/catalog/publish", a.publishCatalog)

	root := http.NewServeMux()
	root.Handle("/v1/devices/register", public)
	root.Handle("/v1/billing/plans", public)
	root.Handle("/v1/client-config", public)
	root.Handle("/v1/devices/self", a.Auth.AuthenticateRegistered(registered))
	// Register the exact create path as well as the completion subtree. Without
	// this, net/http redirects POST /v1/pairing/challenges to a trailing slash,
	// changing the signed request target and causing a false authentication
	// failure.
	root.Handle("/v1/pairing/challenges", a.Auth.Authenticate(a.idempotency(http.HandlerFunc(a.createPairingChallenge))))
	root.Handle("/v1/pairing/challenges/", a.Auth.AuthenticateRegistered(a.idempotency(registered)))
	root.Handle("/v1/", a.Auth.Authenticate(a.idempotency(trusted)))
	return traceMiddleware(root)
}

func (a *API) getClientConfiguration(w http.ResponseWriter, r *http.Request) {
	if a.ClientConfig.OIDCIssuer == "" || a.ClientConfig.OIDCClientID == "" || a.ClientConfig.RedirectURI == "" {
		writeError(w, r, http.StatusServiceUnavailable, "CLIENT_CONFIGURATION_UNAVAILABLE", errors.New("client configuration is unavailable"))
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"oidc_issuer":       a.ClientConfig.OIDCIssuer,
		"oidc_client_id":    a.ClientConfig.OIDCClientID,
		"redirect_uri":      a.ClientConfig.RedirectURI,
		"protocol_versions": []int{1},
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

func (a *API) registerDevice(w http.ResponseWriter, r *http.Request) {
	identity, err := verifyBearer(r.Context(), a.Tokens, r.Header.Get("Authorization"))
	if err != nil {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", err)
		return
	}
	var input registerDeviceRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	deviceID, err := uuid.Parse(input.ID)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_ID", err)
		return
	}
	signingKey, err := base64.RawURLEncoding.DecodeString(input.SigningPublicKey)
	if err != nil || len(signingKey) != ed25519.PublicKeySize {
		writeError(w, r, http.StatusBadRequest, "INVALID_PUBLIC_KEY", errors.New("invalid signing key"))
		return
	}
	exchangeKey, err := base64.RawURLEncoding.DecodeString(input.ExchangePublicKey)
	if err != nil || len(exchangeKey) != 32 {
		writeError(w, r, http.StatusBadRequest, "INVALID_PUBLIC_KEY", errors.New("invalid exchange key"))
		return
	}
	if input.Type != "WINDOWS_PC" && input.Type != "ANDROID" {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_TYPE", errors.New("invalid device type"))
		return
	}
	input.Name = strings.TrimSpace(input.Name)
	if len(input.Name) < 1 || len(input.Name) > 128 || len(input.KeyID) < 8 || len(input.KeyID) > 128 {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", errors.New("invalid device fields"))
		return
	}
	now := a.now()
	if delta := now.Sub(time.UnixMilli(input.TimestampMS)); delta < -60*time.Second || delta > 60*time.Second {
		writeError(w, r, http.StatusUnauthorized, "STALE_PROOF", errors.New("stale registration proof"))
		return
	}
	nonceBytes, err := base64.RawURLEncoding.DecodeString(input.Nonce)
	if err != nil || len(nonceBytes) < 16 || len(nonceBytes) > 32 {
		writeError(w, r, http.StatusBadRequest, "INVALID_NONCE", errors.New("invalid nonce"))
		return
	}
	signature, err := base64.RawURLEncoding.DecodeString(input.Signature)
	if err != nil || len(signature) != ed25519.SignatureSize {
		writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF", errors.New("invalid signature"))
		return
	}
	canonical := strings.Join([]string{"TERMLINK-DEVICE-REGISTER-V1", deviceID.String(), input.Type, input.Name, input.SigningPublicKey, input.ExchangePublicKey, input.KeyID, strconv.FormatInt(input.TimestampMS, 10), input.Nonce}, "\n")
	if !ed25519.Verify(signingKey, []byte(canonical), signature) {
		writeError(w, r, http.StatusUnauthorized, "INVALID_DEVICE_PROOF", errors.New("signature mismatch"))
		return
	}
	subjectHash := auth.SubjectHash(a.SubjectKey, identity)
	consumed, err := a.Valkey.Consume(r.Context(), "register:"+hex.EncodeToString(subjectHash[:]), input.Nonce, 2*time.Minute)
	if err != nil || !consumed {
		writeError(w, r, http.StatusUnauthorized, "REPLAYED_PROOF", errors.New("registration proof replay"))
		return
	}
	userID, device, bootstrap, err := a.Store.RegisterDevice(r.Context(), subjectHash, store.RegisterDeviceInput{ID: deviceID, Type: input.Type, Name: input.Name, SigningPublicKey: signingKey, ExchangePublicKey: exchangeKey, KeyID: input.KeyID}, traceID(r.Context()))
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"user_id": userID, "device": device, "bootstrap": bootstrap})
}

func (a *API) listDevices(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	items, err := a.Store.ListDevices(r.Context(), p.UserID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": items})
}

func (a *API) getCurrentDevice(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	item, err := a.Store.GetDevice(r.Context(), p.UserID, p.Device.ID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"device": item})
}

func (a *API) createPairingChallenge(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	var input struct {
		TargetDeviceID string `json:"target_device_id"`
		PCID           string `json:"pc_id"`
		Permission     string `json:"permission"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	targetID, err := uuid.Parse(input.TargetDeviceID)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_ID", err)
		return
	}
	pcID, err := uuid.Parse(input.PCID)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID", err)
		return
	}
	if input.Permission != "VIEW" && input.Permission != "CONTROL" && input.Permission != "MANAGE" {
		writeError(w, r, http.StatusBadRequest, "INVALID_PERMISSION", errors.New("invalid permission"))
		return
	}
	code, err := generatePairingCode()
	if err != nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", err)
		return
	}
	var nonce [32]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", err)
		return
	}
	challengeID := uuid.New()
	expiresAt := a.now().Add(5 * time.Minute)
	digest := pairingCodeHash(a.TokenKey, challengeID, p.UserID, targetID, code, nonce[:])
	record, err := a.Store.CreatePairingChallenge(r.Context(), p.UserID, p.Device.ID, store.PairingChallengeInput{
		ID: challengeID, TargetDeviceID: targetID, PCID: pcID, Permission: input.Permission,
		CodeHash: digest, ChallengeNonce: nonce, ExpiresAt: expiresAt,
	})
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	fingerprintHash := sha256.New()
	_, _ = fingerprintHash.Write(record.TargetSigningPublicKey)
	_, _ = fingerprintHash.Write(record.TargetExchangePublicKey)
	fingerprint := hex.EncodeToString(fingerprintHash.Sum(nil))
	formattedCode := code[:4] + "-" + code[4:]
	writeJSON(w, http.StatusCreated, map[string]any{
		"challenge_id":    challengeID,
		"code":            formattedCode,
		"challenge_nonce": base64.RawURLEncoding.EncodeToString(nonce[:]),
		"expires_at":      expiresAt,
		"target":          map[string]any{"device_id": targetID, "name": record.TargetName, "public_key_fingerprint": fingerprint},
		"grant":           map[string]any{"pc_id": pcID, "permission": input.Permission},
	})
}

func (a *API) completePairingChallenge(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	challengeID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_CHALLENGE_ID", err)
		return
	}
	var input struct {
		Code           string `json:"code"`
		ChallengeNonce string `json:"challenge_nonce"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	code, ok := normalizePairingCode(input.Code)
	if !ok {
		writeError(w, r, http.StatusBadRequest, "INVALID_PAIRING_CODE", errors.New("invalid pairing code"))
		return
	}
	nonce, err := base64.RawURLEncoding.DecodeString(input.ChallengeNonce)
	if err != nil || len(nonce) != 32 {
		writeError(w, r, http.StatusBadRequest, "INVALID_CHALLENGE_NONCE", errors.New("invalid challenge nonce"))
		return
	}
	digest := pairingCodeHash(a.TokenKey, challengeID, p.UserID, p.Device.ID, code, nonce)
	if err := a.Store.CompletePairingChallenge(r.Context(), p.UserID, p.Device.ID, challengeID, digest[:], nonce, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"status": "TRUSTED"})
}

func generatePairingCode() (string, error) {
	value := make([]byte, 8)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	for i := range value {
		value[i] = pairingCodeAlphabet[int(value[i])&31]
	}
	return string(value), nil
}

func normalizePairingCode(value string) (string, bool) {
	value = strings.ToUpper(strings.NewReplacer("-", "", " ", "", "\t", "", "\r", "", "\n", "").Replace(value))
	if len(value) != 8 {
		return "", false
	}
	for _, character := range value {
		if !strings.ContainsRune(pairingCodeAlphabet, character) {
			return "", false
		}
	}
	return value, true
}

func pairingCodeHash(key []byte, challengeID, userID, targetID uuid.UUID, code string, nonce []byte) [32]byte {
	mac := hmac.New(sha256.New, key)
	_, _ = fmt.Fprintf(mac, "TERMLINK-PAIRING-CODE-V1\n%s\n%s\n%s\n%s\n%s", challengeID, userID, targetID, code, base64.RawURLEncoding.EncodeToString(nonce))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func (a *API) revokeDevice(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	target, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_DEVICE_ID", err)
		return
	}
	if err := a.Store.RevokeDevice(r.Context(), p.UserID, p.Device.ID, target, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	_ = a.Valkey.Client.Publish(r.Context(), "device-revoked", target.String()).Err()
	writeJSON(w, 200, map[string]any{"status": "REVOKED"})
}

func (a *API) listPCs(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	items, err := a.Store.ListPCs(r.Context(), p.UserID, p.Device.ID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"items": items})
}
func (a *API) upsertPC(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	id, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	var input struct {
		Name         string `json:"name"`
		AgentVersion string `json:"agent_version"`
		KeyEpoch     int64  `json:"key_epoch"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, 400, "INVALID_REQUEST", err)
		return
	}
	if strings.TrimSpace(input.Name) == "" || len(input.Name) > 128 || strings.TrimSpace(input.AgentVersion) == "" || input.KeyEpoch < 1 {
		writeError(w, r, 400, "INVALID_REQUEST", errors.New("invalid PC metadata"))
		return
	}
	if err := a.Store.UpsertPC(r.Context(), p.UserID, p.Device.ID, store.PCRecord{ID: id, Name: input.Name, AgentVersion: input.AgentVersion, KeyEpoch: input.KeyEpoch}); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"id": id})
}
func (a *API) putGrant(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	var input struct {
		DeviceID   string     `json:"device_id"`
		Permission string     `json:"permission"`
		ExpiresAt  *time.Time `json:"expires_at"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, 400, "INVALID_REQUEST", err)
		return
	}
	deviceID, err := uuid.Parse(input.DeviceID)
	if err != nil {
		writeError(w, r, 400, "INVALID_DEVICE_ID", err)
		return
	}
	if input.Permission != "VIEW" && input.Permission != "CONTROL" && input.Permission != "MANAGE" {
		writeError(w, r, 400, "INVALID_PERMISSION", errors.New("invalid permission"))
		return
	}
	if err := a.Store.PutGrant(r.Context(), p.UserID, p.Device.ID, deviceID, pcID, input.Permission, input.ExpiresAt); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"permission": input.Permission})
}

func (a *API) listSessions(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	items, err := a.Store.ListSessions(r.Context(), p.UserID, p.Device.ID, pcID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"items": items})
}
func (a *API) upsertSession(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	var input struct {
		PCID               string     `json:"pc_id"`
		ShellType          string     `json:"shell_type"`
		TitleCipher        string     `json:"title_cipher"`
		State              string     `json:"state"`
		Columns            int        `json:"columns"`
		Rows               int        `json:"rows"`
		LastOutputSequence uint64     `json:"last_output_seq"`
		EndedAt            *time.Time `json:"ended_at"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, 400, "INVALID_REQUEST", err)
		return
	}
	pcID, err := uuid.Parse(input.PCID)
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	title, err := base64.RawURLEncoding.DecodeString(input.TitleCipher)
	if err != nil {
		writeError(w, r, 400, "INVALID_CIPHERTEXT", err)
		return
	}
	if input.State != "RUNNING" && input.State != "INTERRUPTED" && input.State != "EXITED" {
		writeError(w, r, 400, "INVALID_SESSION_STATE", errors.New("invalid state"))
		return
	}
	if input.Columns < 1 || input.Columns > 65535 || input.Rows < 1 || input.Rows > 65535 {
		writeError(w, r, 400, "INVALID_SIZE", errors.New("invalid terminal size"))
		return
	}
	item := store.SessionRecord{ID: sessionID, PCID: pcID, ShellType: input.ShellType, TitleCipher: title, State: input.State, Columns: input.Columns, Rows: input.Rows, LastOutputSequence: input.LastOutputSequence, EndedAt: input.EndedAt}
	if err := a.Store.UpsertSession(r.Context(), p.UserID, p.Device.ID, item); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"id": sessionID})
}

func (a *API) subscribeSession(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	permission, err := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	expires := a.now().Add(2 * time.Minute)
	payload := fmt.Sprintf("v1|%s|%s|%s|%d", p.Device.ID, sessionID, permission, expires.Unix())
	mac := hmac.New(sha256.New, a.TokenKey)
	_, _ = mac.Write([]byte(payload))
	token := base64.RawURLEncoding.EncodeToString([]byte(payload)) + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	writeJSON(w, 201, map[string]any{"token": token, "permission": permission, "expires_at": expires})
}

func (a *API) acquireLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	permission, err := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if permission != "CONTROL" && permission != "MANAGE" {
		writeError(w, r, 403, "CONTROL_NOT_GRANTED", store.ErrDenied)
		return
	}
	lease, err := a.Valkey.Acquire(r.Context(), sessionID, p.Device.ID, 30*time.Second)
	if err != nil {
		if errors.Is(err, kv.ErrLeaseHeld) {
			writeError(w, r, 409, "LEASE_HELD", err)
		} else {
			writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		}
		return
	}
	if err := a.Store.SaveLease(r.Context(), lease.ID, lease.SessionID, lease.HolderDeviceID, lease.ExpiresAt, lease.Version); err != nil {
		_, _ = a.Valkey.Release(r.Context(), lease)
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 201, lease)
}

func (a *API) getLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	permission, err := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if permission != "MANAGE" {
		writeError(w, r, 403, "MANAGE_NOT_GRANTED", store.ErrDenied)
		return
	}
	lease, err := a.Valkey.GetLease(r.Context(), sessionID)
	if errors.Is(err, redis.Nil) {
		writeError(w, r, 404, "LEASE_NOT_FOUND", err)
		return
	}
	if err != nil {
		writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		return
	}
	writeJSON(w, 200, lease)
}
func (a *API) forceReleaseLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	permission, err := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if permission != "MANAGE" {
		writeError(w, r, 403, "MANAGE_NOT_GRANTED", store.ErrDenied)
		return
	}
	lease, released, err := a.Valkey.ForceRelease(r.Context(), sessionID)
	if err != nil {
		writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		return
	}
	if released {
		_ = a.Store.DeleteLease(r.Context(), sessionID, lease.ID)
	}
	writeJSON(w, 200, map[string]any{"released": released})
}
func (a *API) renewLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, leaseID, ok := parseLeasePath(w, r)
	if !ok {
		return
	}
	lease, err := a.Valkey.GetLease(r.Context(), sessionID)
	if err != nil || lease.ID != leaseID || lease.HolderDeviceID != p.Device.ID {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	lease, err = a.Valkey.Renew(r.Context(), lease, 30*time.Second)
	if err != nil {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	if err := a.Store.SaveLease(r.Context(), lease.ID, lease.SessionID, lease.HolderDeviceID, lease.ExpiresAt, lease.Version); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, lease)
}
func (a *API) releaseLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, leaseID, ok := parseLeasePath(w, r)
	if !ok {
		return
	}
	lease, err := a.Valkey.GetLease(r.Context(), sessionID)
	if err != nil || lease.ID != leaseID || lease.HolderDeviceID != p.Device.ID {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	released, err := a.Valkey.Release(r.Context(), lease)
	if err != nil || !released {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	_ = a.Store.DeleteLease(r.Context(), sessionID, leaseID)
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) createHistoryUpload(w http.ResponseWriter, r *http.Request) {
	if a.History == nil {
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", errors.New("history object store is disabled"))
		return
	}
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_SESSION_ID", err)
		return
	}
	var input struct {
		Kind             string  `json:"kind"`
		KeyID            string  `json:"key_id"`
		CiphertextSHA256 string  `json:"ciphertext_sha256"`
		SizeBytes        int64   `json:"size_bytes"`
		StartSequence    *uint64 `json:"start_seq"`
		EndSequence      *uint64 `json:"end_seq"`
		SnapshotSequence *uint64 `json:"snapshot_seq"`
		Rows             *int    `json:"rows"`
		Columns          *int    `json:"columns"`
		FormatName       *string `json:"format_name"`
		FormatVersion    *int    `json:"format_version"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	digest, err := base64.RawURLEncoding.DecodeString(input.CiphertextSHA256)
	if err != nil || len(digest) != sha256.Size || len(input.KeyID) < 8 || len(input.KeyID) > 128 || input.SizeBytes < 1 || input.SizeBytes > history.MaxObjectBytes {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_METADATA", errors.New("invalid ciphertext metadata"))
		return
	}
	item := store.HistoryUploadRecord{ID: uuid.New(), SessionID: sessionID, Kind: input.Kind, KeyID: input.KeyID,
		CiphertextSHA256: digest, SizeBytes: input.SizeBytes, ExpiresAt: a.now().Add(10 * time.Minute)}
	if input.Kind == "CHUNK" {
		if input.StartSequence == nil || input.EndSequence == nil || *input.StartSequence < 1 || *input.EndSequence < *input.StartSequence ||
			*input.StartSequence > math.MaxInt64 || *input.EndSequence > math.MaxInt64 || input.SnapshotSequence != nil || input.Rows != nil || input.Columns != nil || input.FormatName != nil || input.FormatVersion != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_RANGE", errors.New("invalid chunk range"))
			return
		}
		start, end := int64(*input.StartSequence), int64(*input.EndSequence)
		item.StartSequence, item.EndSequence = &start, &end
	} else if input.Kind == "SNAPSHOT" {
		if input.SnapshotSequence == nil || *input.SnapshotSequence > math.MaxInt64 || input.Rows == nil || input.Columns == nil ||
			*input.Rows < 1 || *input.Rows > 65535 || *input.Columns < 1 || *input.Columns > 65535 || input.FormatName == nil ||
			*input.FormatName != "BOUNDED_EVENT_REPLAY" || input.FormatVersion == nil || *input.FormatVersion != 1 ||
			input.StartSequence != nil || input.EndSequence != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_SNAPSHOT", errors.New("unsupported snapshot metadata"))
			return
		}
		sequence := int64(*input.SnapshotSequence)
		item.SnapshotSequence, item.Rows, item.Columns, item.FormatName, item.FormatVersion = &sequence, input.Rows, input.Columns, input.FormatName, input.FormatVersion
	} else {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_KIND", errors.New("invalid history kind"))
		return
	}
	item.ObjectKey = fmt.Sprintf("accounts/%s/sessions/%s/%s/%s.bin", p.UserID, sessionID, strings.ToLower(input.Kind), item.ID)
	if err := a.Store.CreateHistoryUpload(r.Context(), p.UserID, p.Device.ID, item); err != nil {
		writeStoreError(w, r, err)
		return
	}
	upload, err := a.History.PresignPut(r.Context(), item.ObjectKey, item.SizeBytes, item.CiphertextSHA256, 10*time.Minute)
	if err != nil {
		slog.Error("presign history upload failed", "trace_id", traceID(r.Context()), "error", err)
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"upload_id": item.ID, "upload": upload, "expires_at": item.ExpiresAt})
}

func (a *API) commitHistoryUpload(w http.ResponseWriter, r *http.Request) {
	if a.History == nil {
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", errors.New("history object store is disabled"))
		return
	}
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_SESSION_ID", err)
		return
	}
	uploadID, err := uuid.Parse(r.PathValue("uploadId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_UPLOAD_ID", err)
		return
	}
	upload, err := a.Store.PendingHistoryUpload(r.Context(), p.UserID, p.Device.ID, sessionID, uploadID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if err := a.History.VerifyCiphertext(r.Context(), upload.ObjectKey, upload.SizeBytes, upload.CiphertextSHA256); err != nil {
		writeError(w, r, http.StatusUnprocessableEntity, "HISTORY_INTEGRITY_FAILED", err)
		return
	}
	item, err := a.Store.CommitHistoryUpload(r.Context(), p.UserID, p.Device.ID, sessionID, uploadID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"id": item.ID, "kind": item.Kind, "committed": true})
}

func (a *API) deleteSession(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_SESSION_ID", err)
		return
	}
	keys, err := a.Store.SessionHistoryObjectKeys(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if len(keys) > 0 && a.History == nil {
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", errors.New("history deletion is unavailable"))
		return
	}
	for _, key := range keys {
		if err := a.History.Delete(r.Context(), key); err != nil {
			slog.Error("delete history ciphertext failed", "trace_id", traceID(r.Context()), "error", err)
			writeError(w, r, http.StatusServiceUnavailable, "HISTORY_DELETE_PENDING", err)
			return
		}
	}
	if err := a.Store.MarkSessionDeleted(r.Context(), p.UserID, p.Device.ID, sessionID, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) listHistory(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return
	}
	items, err := a.Store.ListHistory(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	responseItems := make([]map[string]any, 0, len(items))
	for _, item := range items {
		if a.History == nil {
			writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", errors.New("history object store is disabled"))
			return
		}
		download, err := a.History.PresignGet(r.Context(), item.ObjectKey, 5*time.Minute)
		if err != nil {
			writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", err)
			return
		}
		responseItems = append(responseItems, map[string]any{
			"kind": item.Kind, "id": item.ID, "start_seq": item.StartSequence, "end_seq": item.EndSequence,
			"snapshot_seq": item.SnapshotSequence, "key_id": item.KeyID,
			"ciphertext_sha256": base64.RawURLEncoding.EncodeToString(item.CiphertextSHA256), "size_bytes": item.SizeBytes,
			"format_name": item.FormatName, "format_version": item.FormatVersion, "created_at": item.CreatedAt, "download": download,
		})
	}
	writeJSON(w, 200, map[string]any{"items": responseItems, "plaintext_index": false})
}
func (a *API) listPlans(w http.ResponseWriter, r *http.Request) {
	version, plans, err := a.Store.PublishedPlans(r.Context())
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"catalog_version": version, "plans": plans})
}
func (a *API) getEntitlement(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	item, err := a.Store.GetEntitlement(r.Context(), p.UserID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, item)
}

type purchaseRequest struct {
	ProductID     string `json:"product_id"`
	PurchaseToken string `json:"purchase_token"`
}

func (a *API) verifyGooglePurchase(w http.ResponseWriter, r *http.Request) {
	var input purchaseRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	p := principal(r)
	if err := validatePurchaseRequest(input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PURCHASE", err)
		return
	}
	if a.Billing == nil {
		writeError(w, r, http.StatusServiceUnavailable, "BILLING_UNAVAILABLE", billing.ErrUnavailable)
		return
	}
	verified, err := a.Billing.Verify(r.Context(), billing.ProviderGooglePlay, input.ProductID, input.PurchaseToken)
	if err != nil {
		writeBillingProviderError(w, r, err)
		return
	}
	entitlement, err := a.Store.ApplyVerifiedPurchase(r.Context(), p.UserID, p.Device.ID, verified, traceID(r.Context()))
	if err != nil {
		writeBillingStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"entitlement": entitlement, "verified": true})
}

type restoreRequest struct {
	Provider      string `json:"provider"`
	ProductID     string `json:"product_id"`
	PurchaseToken string `json:"purchase_token"`
}

func (a *API) restorePurchase(w http.ResponseWriter, r *http.Request) {
	var input restoreRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	input.Provider = strings.ToUpper(strings.TrimSpace(input.Provider))
	if input.Provider != billing.ProviderGooglePlay && input.Provider != billing.ProviderWeb {
		writeError(w, r, http.StatusBadRequest, "INVALID_PURCHASE_PROVIDER", errors.New("unsupported provider"))
		return
	}
	if err := validatePurchaseRequest(purchaseRequest{ProductID: input.ProductID, PurchaseToken: input.PurchaseToken}); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PURCHASE", err)
		return
	}
	if a.Billing == nil {
		writeError(w, r, http.StatusServiceUnavailable, "BILLING_UNAVAILABLE", billing.ErrUnavailable)
		return
	}
	p := principal(r)
	verified, err := a.Billing.Verify(r.Context(), input.Provider, input.ProductID, input.PurchaseToken)
	if err != nil {
		writeBillingProviderError(w, r, err)
		return
	}
	entitlement, err := a.Store.ApplyVerifiedPurchase(r.Context(), p.UserID, p.Device.ID, verified, traceID(r.Context()))
	if err != nil {
		writeBillingStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"entitlement": entitlement, "restored": true})
}

func (a *API) createWebCheckout(w http.ResponseWriter, r *http.Request) {
	// Checkout creation is intentionally provider-owned. Until a signed Web
	// provider is configured, fail closed rather than returning a client-owned
	// session or accepting a forged plan/quota claim.
	writeError(w, r, http.StatusServiceUnavailable, "BILLING_UNAVAILABLE", billing.ErrUnavailable)
}

func validatePurchaseRequest(input purchaseRequest) error {
	if len(strings.TrimSpace(input.ProductID)) < 1 || len(strings.TrimSpace(input.ProductID)) > 256 || len(strings.TrimSpace(input.PurchaseToken)) < 1 || len(input.PurchaseToken) > 4096 {
		return errors.New("invalid purchase fields")
	}
	return nil
}

func writeBillingProviderError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, billing.ErrUnavailable):
		writeError(w, r, http.StatusServiceUnavailable, "BILLING_UNAVAILABLE", err)
	case errors.Is(err, billing.ErrInvalid), errors.Is(err, billing.ErrNotFound):
		writeError(w, r, http.StatusUnprocessableEntity, "PURCHASE_NOT_VERIFIED", err)
	default:
		writeError(w, r, http.StatusBadGateway, "BILLING_PROVIDER_ERROR", err)
	}
}

func writeBillingStoreError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, store.ErrNotFound):
		writeError(w, r, http.StatusUnprocessableEntity, "UNKNOWN_PRODUCT", err)
	case errors.Is(err, store.ErrConflict):
		writeError(w, r, http.StatusConflict, "PURCHASE_CONFLICT", err)
	case errors.Is(err, store.ErrDenied):
		writeError(w, r, http.StatusForbidden, "PURCHASE_DENIED", err)
	default:
		writeStoreError(w, r, err)
	}
}

type planDraftRequest struct {
	PlanID           string          `json:"plan_id"`
	PlanKey          string          `json:"plan_key"`
	DisplayName      string          `json:"display_name"`
	Description      string          `json:"description"`
	BillingPeriod    string          `json:"billing_period"`
	MaxRemotePCs     int             `json:"max_remote_pcs"`
	MaxLiveSessions  int             `json:"max_live_sessions"`
	FeatureFlags     json.RawMessage `json:"feature_flags"`
	ChannelProducts  json.RawMessage `json:"channel_products"`
	SortOrder        int             `json:"sort_order"`
	VisiblePlatforms []string        `json:"visible_platforms"`
	AvailableFrom    *time.Time      `json:"available_from"`
	AvailableUntil   *time.Time      `json:"available_until"`
	FallbackPlanKey  string          `json:"fallback_plan_key"`
}

func (a *API) requireAdmin(w http.ResponseWriter, r *http.Request) (auth.Principal, bool) {
	p := principal(r)
	admin, err := a.Store.IsAdmin(r.Context(), p.UserID)
	if err != nil || !admin {
		_ = a.Store.Audit(r.Context(), p.UserID, p.Device.ID, "billing.admin.denied", "catalog", uuid.Nil, "DENIED", traceID(r.Context()), "{}")
		writeError(w, r, http.StatusForbidden, "ADMIN_REQUIRED", store.ErrDenied)
		return auth.Principal{}, false
	}
	return p, true
}

func (a *API) createPlanDraft(w http.ResponseWriter, r *http.Request) {
	p, ok := a.requireAdmin(w, r)
	if !ok {
		return
	}
	var input planDraftRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	plan, err := a.Store.CreatePlanDraft(r.Context(), p.UserID, p.Device.ID, input.storeInput(), traceID(r.Context()))
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, plan)
}

func (a *API) updatePlanDraft(w http.ResponseWriter, r *http.Request) {
	p, ok := a.requireAdmin(w, r)
	if !ok {
		return
	}
	planID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PLAN_ID", err)
		return
	}
	var input planDraftRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	plan, err := a.Store.UpdatePlanDraft(r.Context(), p.UserID, p.Device.ID, planID, input.storeInput(), traceID(r.Context()))
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, plan)
}

func (a *API) publishCatalog(w http.ResponseWriter, r *http.Request) {
	p, ok := a.requireAdmin(w, r)
	if !ok {
		return
	}
	version, err := a.Store.PublishCatalog(r.Context(), p.UserID, p.Device.ID, traceID(r.Context()))
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"catalog_version": version, "published": true})
}

func (input planDraftRequest) storeInput() store.PlanDraftInput {
	planKey := strings.TrimSpace(input.PlanKey)
	if strings.TrimSpace(input.PlanID) != "" {
		planKey = strings.TrimSpace(input.PlanID)
	}
	period := strings.ToUpper(strings.TrimSpace(input.BillingPeriod))
	if period == "MONTH" {
		period = "MONTHLY"
	} else if period == "YEAR" {
		period = "YEARLY"
	}
	flags := input.FeatureFlags
	if len(flags) == 0 {
		flags = json.RawMessage(`{}`)
	}
	products := input.ChannelProducts
	if len(products) == 0 {
		products = json.RawMessage(`{}`)
	}
	platforms := input.VisiblePlatforms
	if len(platforms) == 0 {
		platforms = []string{"ANDROID", "WEB"}
	}
	return store.PlanDraftInput{PlanKey: planKey, DisplayName: input.DisplayName, Description: input.Description, BillingPeriod: period,
		MaxRemotePCs: input.MaxRemotePCs, MaxLiveSessions: input.MaxLiveSessions, FeatureFlags: flags, ChannelProducts: products,
		SortOrder: input.SortOrder, VisiblePlatforms: platforms, AvailableFrom: input.AvailableFrom, AvailableUntil: input.AvailableUntil, FallbackPlanKey: input.FallbackPlanKey}
}

func (a *API) listAudit(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	before, _ := strconv.ParseInt(r.URL.Query().Get("before"), 10, 64)
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	items, err := a.Store.ListAudit(r.Context(), p.UserID, before, limit)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"items": items})
}

func (a *API) now() time.Time {
	if a.Clock != nil {
		return a.Clock().UTC()
	}
	return time.Now().UTC()
}
func principal(r *http.Request) auth.Principal {
	p, _ := auth.PrincipalFromContext(r.Context())
	return p
}
func parseLeasePath(w http.ResponseWriter, r *http.Request) (uuid.UUID, uuid.UUID, bool) {
	sessionID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_SESSION_ID", err)
		return uuid.Nil, uuid.Nil, false
	}
	leaseID, err := uuid.Parse(r.PathValue("leaseId"))
	if err != nil {
		writeError(w, r, 400, "INVALID_LEASE_ID", err)
		return uuid.Nil, uuid.Nil, false
	}
	return sessionID, leaseID, true
}

func decodeJSON(r *http.Request, destination any) error {
	if r.Body == nil {
		return errors.New("JSON body required")
	}
	decoder := json.NewDecoder(io.LimitReader(r.Body, maxJSONBody+1))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return err
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return errors.New("single JSON value required")
	}
	return nil
}
func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
func writeStoreError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, store.ErrNotFound):
		writeError(w, r, 404, "NOT_FOUND", err)
	case errors.Is(err, store.ErrConflict):
		writeError(w, r, 409, "CONFLICT", err)
	case errors.Is(err, store.ErrDenied):
		writeError(w, r, 403, "FORBIDDEN", err)
	case errors.Is(err, store.ErrEntitlement):
		writeError(w, r, 409, "ENTITLEMENT_LIMIT_REACHED", err)
	default:
		slog.Error("API storage operation failed", "trace_id", traceID(r.Context()), "error", err)
		writeError(w, r, 500, "INTERNAL", err)
	}
}
func writeError(w http.ResponseWriter, r *http.Request, status int, code string, _ error) {
	writeJSON(w, status, map[string]any{"error": map[string]string{"code": code, "message_key": "errors." + strings.ToLower(code), "trace_id": traceID(r.Context())}})
}

func verifyBearer(ctx context.Context, verifier auth.TokenVerifier, header string) (auth.Identity, error) {
	const prefix = "Bearer "
	if verifier == nil || !strings.HasPrefix(header, prefix) {
		return auth.Identity{}, auth.ErrUnauthenticated
	}
	token := strings.TrimSpace(strings.TrimPrefix(header, prefix))
	if token == "" {
		return auth.Identity{}, auth.ErrUnauthenticated
	}
	return verifier.Verify(ctx, token)
}

type traceKey struct{}

func traceMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		id := r.Header.Get("X-Trace-ID")
		if _, err := uuid.Parse(id); err != nil {
			id = uuid.NewString()
		}
		w.Header().Set("X-Trace-ID", id)
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), traceKey{}, id)))
	})
}
func traceID(ctx context.Context) string { id, _ := ctx.Value(traceKey{}).(string); return id }

func (a *API) idempotency(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost || r.Method == http.MethodPut || r.Method == http.MethodDelete {
			key := r.Header.Get("Idempotency-Key")
			if len(key) < 8 || len(key) > 128 {
				writeError(w, r, 400, "IDEMPOTENCY_KEY_REQUIRED", errors.New("missing idempotency key"))
				return
			}
			body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
			if err != nil || len(body) > maxJSONBody {
				writeError(w, r, 400, "INVALID_REQUEST", errors.New("request body exceeds limit"))
				return
			}
			r.Body = io.NopCloser(bytes.NewReader(body))
			digest := sha256.Sum256(append([]byte(r.Method+"\n"+r.URL.RequestURI()+"\n"), body...))
			p := principal(r)
			result, err := a.Store.ClaimIdempotency(r.Context(), p.UserID, key, digest)
			if err != nil {
				writeStoreError(w, r, err)
				return
			}
			if !hmac.Equal(result.RequestHash[:], digest[:]) {
				writeError(w, r, 409, "IDEMPOTENCY_KEY_REUSED", store.ErrConflict)
				return
			}
			if !result.Claimed {
				if result.Status == nil {
					writeError(w, r, 409, "IDEMPOTENCY_IN_PROGRESS", store.ErrConflict)
					return
				}
				w.Header().Set("Content-Type", "application/json")
				w.Header().Set("Idempotency-Replayed", "true")
				w.WriteHeader(*result.Status)
				if responseMayHaveBody(r.Method, *result.Status) && len(result.Body) > 0 && !bytes.Equal(result.Body, []byte("null")) {
					_, _ = w.Write(result.Body)
				}
				return
			}
			capture := &captureWriter{ResponseWriter: w, status: 200}
			next.ServeHTTP(capture, r)
			stored := json.RawMessage(capture.body.Bytes())
			if len(stored) == 0 {
				stored = json.RawMessage("null")
			}
			if !json.Valid(stored) {
				stored, _ = json.Marshal(map[string]string{"code": "NON_JSON_RESPONSE"})
			}
			_ = a.Store.CompleteIdempotency(r.Context(), p.UserID, key, digest, capture.status, stored)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func responseMayHaveBody(method string, status int) bool {
	return method != http.MethodHead && status != http.StatusNoContent && status != http.StatusNotModified &&
		(status < 100 || status >= 200)
}

type captureWriter struct {
	http.ResponseWriter
	status int
	body   bytes.Buffer
}

func (w *captureWriter) WriteHeader(status int) {
	w.status = status
	w.ResponseWriter.WriteHeader(status)
}
func (w *captureWriter) Write(data []byte) (int, error) {
	w.body.Write(data)
	return w.ResponseWriter.Write(data)
}

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

// A 1 MiB opaque session-title envelope expands to roughly 1.34 MiB when the
// Windows Agent transports it as base64url JSON. Keep the HTTP bound above the
// cryptographic envelope bound while still rejecting unbounded request bodies.
const maxJSONBody = 2 << 20

const maxSessionTitleCipherBytes = 1 << 20

const maxHistoryPageTokenBytes = 2048

const maxTranscriptPublicationPageCount = 100_000

const (
	boundedEventReplayFormat                   = store.HistoryFormatBoundedEventReplay
	terminalSnapshotFormat                     = store.HistoryFormatTerminalSnapshot
	terminalTranscriptFormat                   = store.HistoryFormatTerminalTranscript
	maxTerminalSnapshotCiphertextBytes   int64 = store.MaxSnapshotCiphertextBytes
	maxTerminalTranscriptCiphertextBytes int64 = store.MaxTranscriptCiphertextBytes
)

const pairingCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

type API struct {
	Store           *store.Postgres
	Valkey          *kv.Valkey
	Tokens          auth.TokenVerifier
	Auth            auth.Middleware
	SubjectKey      []byte
	TokenKey        []byte
	History         HistoryObjectStore
	Billing         *billing.Registry
	Registration    *auth.DirectRegistrationService
	ClientConfig    ClientConfiguration
	Clock           func() time.Time
	historyPages    historyPageLister
	historyUploads  historyUploadStore
	transcriptPages transcriptPageLister
	fileOperations  fileOperationStore
}

type historyPageLister interface {
	ListHistoryPage(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.HistoryPageRequest) (store.HistoryPage, error)
}

type historyUploadStore interface {
	CreateHistoryUpload(context.Context, uuid.UUID, uuid.UUID, store.HistoryUploadRecord) error
	PendingHistoryUpload(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.HistoryUploadRecord, error)
	CommitHistoryUpload(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, uuid.UUID) (store.HistoryItem, error)
}

type transcriptPageLister interface {
	ListTranscriptPage(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.TranscriptPageRequest) (store.TranscriptPage, error)
}

type ClientConfiguration struct {
	OIDCIssuer                 string
	OIDCClientID               string
	RedirectURI                string
	WindowsClientID            string
	WindowsRegistrationParties []string
	WindowsRedirectURI         string
	WebClientID                string
	WebRedirectURI             string
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
	public.HandleFunc("POST /v1/auth/register", a.registerAccount)
	registered := http.NewServeMux()
	registered.HandleFunc("POST /v1/pairing/challenges/{id}/complete", a.completePairingChallenge)
	registered.HandleFunc("GET /v1/devices/self", a.getCurrentDevice)

	trusted := http.NewServeMux()
	trusted.HandleFunc("GET /v1/devices", a.listDevices)
	trusted.HandleFunc("POST /v1/pairing/challenges", a.createPairingChallenge)
	trusted.HandleFunc("POST /v1/devices/{id}/revoke", a.revokeDevice)
	trusted.HandleFunc("GET /v1/pcs", a.listPCs)
	trusted.HandleFunc("PUT /v1/pcs/{id}", a.upsertPC)
	trusted.HandleFunc("POST /v1/pcs/{pcId}/file-operations", a.createFileOperation)
	trusted.HandleFunc("POST /v1/pcs/{pcId}/file-operations/claim", a.claimFileOperation)
	trusted.HandleFunc("GET /v1/pcs/{pcId}/file-operations/{operationId}", a.getFileOperation)
	trusted.HandleFunc("DELETE /v1/pcs/{pcId}/file-operations/{operationId}", a.cancelFileOperation)
	trusted.HandleFunc("POST /v1/pcs/{pcId}/file-operations/{operationId}/upload", a.uploadFileOperation)
	trusted.HandleFunc("POST /v1/pcs/{pcId}/file-operations/{operationId}/renew", a.renewFileOperation)
	trusted.HandleFunc("POST /v1/pcs/{pcId}/file-operations/{operationId}/complete", a.completeFileOperation)
	trusted.HandleFunc("GET /v1/pcs/{id}/grants", a.listPCGrants)
	trusted.HandleFunc("POST /v1/pcs/{id}/grants", a.putGrant)
	trusted.HandleFunc("DELETE /v1/pcs/{id}/grants/{deviceId}", a.deleteGrant)
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
	root.Handle("/v1/auth/register", public)
	root.Handle("/v1/devices/self", a.Auth.AuthenticateRegistered(registered))
	root.Handle("/v1/devices/self/trust", a.Auth.AuthenticateRegistered(a.idempotency(http.HandlerFunc(a.selfTrustAndroidDevice))))
	// Register the exact create path as well as the completion subtree. Without
	// this, net/http redirects POST /v1/pairing/challenges to a trailing slash,
	// changing the signed request target and causing a false authentication
	// failure.
	root.Handle("/v1/pairing/challenges", a.Auth.Authenticate(a.idempotency(http.HandlerFunc(a.createPairingChallenge))))
	root.Handle("/v1/pairing/challenges/", a.Auth.AuthenticateRegistered(a.idempotency(registered)))
	root.Handle("/v1/", a.Auth.Authenticate(a.idempotency(trusted)))
	return traceMiddleware(root)
}

type registerAccountRequest struct {
	Username  string `json:"username"`
	Email     string `json:"email"`
	Password  string `json:"password"`
	Confirm   string `json:"password_confirm"`
	FirstName string `json:"first_name"`
	LastName  string `json:"last_name"`
}

func (a *API) registerAccount(w http.ResponseWriter, r *http.Request) {
	if a.Registration == nil {
		writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", errors.New("direct registration is unavailable"))
		return
	}
	var input registerAccountRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	registration := auth.DirectRegistrationInput{
		Username: strings.TrimSpace(input.Username), Email: strings.TrimSpace(input.Email), Password: input.Password,
		Confirm: input.Confirm, FirstName: strings.TrimSpace(input.FirstName), LastName: strings.TrimSpace(input.LastName),
	}
	if err := auth.ValidateDirectRegistrationInput(registration); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REGISTRATION", err)
		return
	}
	// Keep registration attempts bounded across replicas. The address is
	// hashed before it becomes a Valkey key and is never logged or returned.
	if a.Valkey != nil {
		allowed, err := a.Valkey.Consume(r.Context(), accountRegistrationRateLimitScope(r), "attempt", time.Minute)
		if err != nil {
			writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", err)
			return
		}
		if !allowed {
			writeError(w, r, http.StatusTooManyRequests, "REGISTRATION_RATE_LIMITED", errors.New("registration rate limit reached"))
			return
		}
	}
	err := a.Registration.Register(r.Context(), registration)
	// Never expose provider error text: it can contain identity-service
	// internals or user-supplied field values. Clients classify by message_key.
	if err != nil {
		writeError(w, r, http.StatusUnprocessableEntity, "REGISTRATION_REJECTED", err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"registered": true})
}

func (a *API) getClientConfiguration(w http.ResponseWriter, r *http.Request) {
	if a.ClientConfig.OIDCIssuer == "" || a.ClientConfig.OIDCClientID == "" || a.ClientConfig.RedirectURI == "" ||
		a.ClientConfig.WindowsClientID == "" || a.ClientConfig.WindowsRedirectURI == "" ||
		a.ClientConfig.WebClientID == "" || a.ClientConfig.WebRedirectURI == "" {
		writeError(w, r, http.StatusServiceUnavailable, "CLIENT_CONFIGURATION_UNAVAILABLE", errors.New("client configuration is unavailable"))
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"oidc_issuer":            a.ClientConfig.OIDCIssuer,
		"oidc_client_id":         a.ClientConfig.OIDCClientID,
		"redirect_uri":           a.ClientConfig.RedirectURI,
		"windows_oidc_client_id": a.ClientConfig.WindowsClientID,
		"windows_redirect_uri":   a.ClientConfig.WindowsRedirectURI,
		"web_oidc_client_id":     a.ClientConfig.WebClientID,
		"web_redirect_uri":       a.ClientConfig.WebRedirectURI,
		"protocol_versions":      []int{1},
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
	if !a.canRegisterDeviceType(input.Type, identity.AuthorizedParty) {
		writeError(w, r, http.StatusForbidden, "DEVICE_TYPE_NOT_ALLOWED", store.ErrDenied)
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

func (a *API) canRegisterDeviceType(deviceType, authorizedParty string) bool {
	authorizedParty = strings.TrimSpace(authorizedParty)
	switch deviceType {
	case "ANDROID":
		return authorizedParty != "" && authorizedParty == a.ClientConfig.OIDCClientID
	case "WINDOWS_PC":
		for _, party := range a.ClientConfig.WindowsRegistrationParties {
			if authorizedParty != "" && authorizedParty == party {
				return true
			}
		}
	}
	return false
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

func (a *API) selfTrustAndroidDevice(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	if p.Device.Type != "ANDROID" || p.Identity.AuthorizedParty != a.ClientConfig.OIDCClientID ||
		strings.TrimSpace(p.Identity.TokenID) == "" || len(a.TokenKey) < 32 {
		writeError(w, r, http.StatusForbidden, "SELF_TRUST_NOT_ALLOWED", store.ErrDenied)
		return
	}
	tokenIDHash := selfTrustTokenIDHash(a.TokenKey, p.Identity)
	device, changed, err := a.Store.SelfTrustAndroidDevice(
		r.Context(), p.UserID, p.Device.ID, tokenIDHash, traceID(r.Context()),
	)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"status":  "TRUSTED",
		"changed": changed,
		"device":  device,
	})
}

func selfTrustTokenIDHash(key []byte, identity auth.Identity) [32]byte {
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte("TERMLINK-ANDROID-SELF-TRUST-JTI-V1"))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(identity.Issuer))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(identity.TokenID))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
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
	pcs, err := a.Store.ListPCs(r.Context(), p.UserID, p.Device.ID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	leaseSessionIDs := make(map[uuid.UUID]struct{})
	for _, pc := range pcs {
		sessions, listErr := a.Store.ListSessionsIncludingEnded(r.Context(), p.UserID, p.Device.ID, pc.ID)
		if listErr != nil {
			writeStoreError(w, r, listErr)
			return
		}
		for _, session := range sessions {
			leaseSessionIDs[session.ID] = struct{}{}
		}
	}
	trace := traceID(r.Context())
	// A self-revoking actor cannot authorize the exact durable-fence delete
	// after its trust row changes. Remove its shared-lease participation first;
	// another participant's lease remains untouched.
	if target == p.Device.ID {
		for sessionID := range leaseSessionIDs {
			if err := a.releaseLeaseParticipant(r.Context(), p.UserID, p.Device.ID, target, sessionID, trace); err != nil {
				writeError(w, r, http.StatusServiceUnavailable, "LEASE_UNAVAILABLE", err)
				return
			}
		}
	}
	if err := a.Store.RevokeDevice(r.Context(), p.UserID, p.Device.ID, target, trace); err != nil {
		writeStoreError(w, r, err)
		return
	}
	if target != p.Device.ID {
		for sessionID := range leaseSessionIDs {
			if err := a.releaseLeaseParticipant(r.Context(), p.UserID, p.Device.ID, target, sessionID, trace); err != nil {
				// Revocation is already durable and Relay authorization fails closed.
				// Keep the successful security response stable; the short Valkey TTL
				// is the fallback if this best-effort reconciliation is unavailable.
				slog.Warn("remove revoked device from shared lease", "trace_id", trace, "target_device_id", target, "session_id", sessionID, "error", err)
			}
		}
	}
	_ = a.Valkey.Client.Publish(r.Context(), "device-revoked", target.String()).Err()
	writeJSON(w, 200, map[string]any{"status": "REVOKED"})
}

func (a *API) releaseLeaseParticipant(
	ctx context.Context,
	userID, actorID, deviceID, sessionID uuid.UUID,
	trace string,
) error {
	lease, err := a.Valkey.GetLease(ctx, sessionID)
	if errors.Is(err, redis.Nil) {
		return nil
	}
	if err != nil {
		return err
	}
	_, released, err := a.Valkey.ReleaseForDevice(ctx, lease, deviceID)
	if err != nil || !released {
		return err
	}
	// Delete only the exact durable fence observed before the CAS release. A
	// replacement lease acquired after that release must survive reconciliation.
	return a.Store.DeleteLease(
		ctx, userID, actorID, sessionID, lease.ID,
		"lease.revoke.permission", trace,
	)
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
		Name                    string   `json:"name"`
		AgentVersion            string   `json:"agent_version"`
		KeyEpoch                int64    `json:"key_epoch"`
		SessionHeartbeatCapable bool     `json:"session_heartbeat_capable"`
		SupportedShells         []string `json:"supported_shells"`
	}
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, 400, "INVALID_REQUEST", err)
		return
	}
	if strings.TrimSpace(input.Name) == "" || len(input.Name) > 128 || strings.TrimSpace(input.AgentVersion) == "" || input.KeyEpoch < 1 {
		writeError(w, r, 400, "INVALID_REQUEST", errors.New("invalid PC metadata"))
		return
	}
	supportedShells, err := normalizeSupportedShells(input.SupportedShells)
	if err != nil {
		writeError(w, r, 400, "INVALID_REQUEST", err)
		return
	}
	if err := a.Store.UpsertPC(r.Context(), p.UserID, p.Device.ID, store.PCRecord{
		ID: id, Name: input.Name, AgentVersion: input.AgentVersion, KeyEpoch: input.KeyEpoch,
		SessionHeartbeatCapable: input.SessionHeartbeatCapable,
		SupportedShells:         supportedShells,
	}); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"id": id})
}

func normalizeSupportedShells(values []string) ([]string, error) {
	if len(values) > 8 {
		return nil, errors.New("too many supported shells")
	}
	seen := make(map[string]bool, len(values))
	for _, value := range values {
		switch value {
		case "command-prompt", "windows-powershell":
			seen[value] = true
		default:
			return nil, errors.New("unsupported shell capability")
		}
	}
	normalized := make([]string, 0, len(seen))
	for _, value := range []string{"command-prompt", "windows-powershell"} {
		if seen[value] {
			normalized = append(normalized, value)
		}
	}
	return normalized, nil
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
	if input.ExpiresAt != nil && !input.ExpiresAt.After(a.now()) {
		writeError(w, r, 400, "INVALID_GRANT_EXPIRY", errors.New("grant expiry must be in the future"))
		return
	}
	if err := a.Store.PutGrant(r.Context(), p.UserID, p.Device.ID, deviceID, pcID, input.Permission, input.ExpiresAt, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	if input.Permission == "VIEW" {
		// This is control-plane cleanup, not the live session directory. Include
		// ended and stale-PC rows so a recently downgraded device does not leave
		// a Valkey lease behind until its TTL expires.
		if sessions, listErr := a.Store.ListSessionsIncludingEnded(r.Context(), p.UserID, p.Device.ID, pcID); listErr == nil {
			for _, session := range sessions {
				if releaseErr := a.releaseLeaseParticipant(r.Context(), p.UserID, p.Device.ID, deviceID, session.ID, traceID(r.Context())); releaseErr != nil {
					slog.Warn("remove downgraded device from shared lease", "trace_id", traceID(r.Context()), "target_device_id", deviceID, "session_id", session.ID, "error", releaseErr)
				}
			}
		} else {
			slog.Warn("list sessions for grant downgrade lease cleanup", "trace_id", traceID(r.Context()), "target_device_id", deviceID, "pc_id", pcID, "error", listErr)
		}
	}
	_ = a.Valkey.Client.Publish(r.Context(), "device-authorization-changed", deviceID.String()).Err()
	writeJSON(w, 200, map[string]any{"permission": input.Permission})
}

func (a *API) deleteGrant(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_PC_ID", err)
		return
	}
	deviceID, err := uuid.Parse(r.PathValue("deviceId"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_DEVICE_ID", err)
		return
	}
	result, err := a.Store.DeleteGrant(r.Context(), p.UserID, p.Device.ID, deviceID, pcID, traceID(r.Context()))
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	// PostgreSQL and the Relay remain authoritative even if Valkey is briefly
	// unavailable. Best-effort CAS release prevents a removed controller from
	// blocking another device while never releasing a newly acquired lease.
	for _, sessionID := range result.LeaseSessionIDs {
		if releaseErr := a.releaseLeaseParticipant(r.Context(), p.UserID, p.Device.ID, deviceID, sessionID, traceID(r.Context())); releaseErr != nil {
			slog.Warn("remove grant-revoked device from shared lease", "trace_id", traceID(r.Context()), "target_device_id", deviceID, "session_id", sessionID, "error", releaseErr)
		}
	}
	_ = a.Valkey.Client.Publish(r.Context(), "device-authorization-changed", deviceID.String()).Err()
	w.WriteHeader(http.StatusNoContent)
}

func (a *API) listPCGrants(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	keyEpoch, items, err := a.Store.ListPCGrants(r.Context(), p.UserID, p.Device.ID, pcID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"pc_id": pcID, "key_epoch": keyEpoch, "items": items})
}

func (a *API) listSessions(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	pcID, err := uuid.Parse(r.PathValue("id"))
	if err != nil {
		writeError(w, r, 400, "INVALID_PC_ID", err)
		return
	}
	includeEnded := false
	if raw := r.URL.Query().Get("include_ended"); raw != "" {
		includeEnded, err = strconv.ParseBool(raw)
		if err != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_INCLUDE_ENDED", err)
			return
		}
	}
	var items []store.SessionRecord
	if includeEnded {
		items, err = a.Store.ListSessionsIncludingEnded(r.Context(), p.UserID, p.Device.ID, pcID)
	} else {
		items, err = a.Store.ListSessions(r.Context(), p.UserID, p.Device.ID, pcID)
	}
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
		PCID                    string     `json:"pc_id"`
		ShellType               string     `json:"shell_type"`
		TitleCipher             *string    `json:"title_cipher"`
		State                   string     `json:"state"`
		Columns                 int        `json:"columns"`
		Rows                    int        `json:"rows"`
		LastOutputSequence      uint64     `json:"last_output_seq"`
		EndedAt                 *time.Time `json:"ended_at"`
		SessionHeartbeatCapable bool       `json:"session_heartbeat_capable"`
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
	title, err := decodeSessionTitleCipher(input.TitleCipher)
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
	item := store.SessionRecord{
		ID: sessionID, PCID: pcID, ShellType: input.ShellType, TitleCipher: title,
		State: input.State, Columns: input.Columns, Rows: input.Rows,
		LastOutputSequence: input.LastOutputSequence, EndedAt: input.EndedAt,
		SessionHeartbeatCapable: input.SessionHeartbeatCapable,
	}
	if err := a.Store.UpsertSession(r.Context(), p.UserID, p.Device.ID, item); err != nil {
		writeStoreError(w, r, err)
		return
	}
	writeJSON(w, 200, map[string]any{"id": sessionID})
}

func decodeSessionTitleCipher(encoded *string) ([]byte, error) {
	// Both an absent field and an explicit empty string mean "no title update".
	// The Windows Agent deliberately sends an empty string when it cannot seal
	// a fresh recipient-bound title; returning nil preserves the stored value in
	// Postgres. A non-empty envelope is the only value that may replace it.
	if encoded == nil || *encoded == "" {
		return nil, nil
	}
	decoded, err := base64.RawURLEncoding.DecodeString(*encoded)
	if err != nil {
		return nil, err
	}
	if len(decoded) > maxSessionTitleCipherBytes {
		return nil, errors.New("session title ciphertext exceeds limit")
	}
	return decoded, nil
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
	if err := a.Store.SaveLease(r.Context(), p.UserID, p.Device.ID, lease.ID, lease.SessionID, lease.HolderDeviceID, lease.ExpiresAt, lease.Version, "lease.acquire", traceID(r.Context())); err != nil {
		_, _, _ = a.Valkey.ReleaseForDevice(r.Context(), lease, p.Device.ID)
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
	// The operation-scoped release tombstone preserves the exact lease fence
	// across a transient PostgreSQL failure. Retrying the same idempotent
	// request therefore reconciles the old durable row without deleting a
	// replacement lease acquired after the first cache deletion.
	releasedLease, released, err := a.Valkey.ForceReleaseForOperation(
		r.Context(), sessionID, p.UserID, r.Header.Get("Idempotency-Key"),
	)
	if err != nil {
		writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		return
	}
	if released {
		if err := a.Store.ForceDeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, releasedLease.ID, releasedLease.Version, "lease.force_release", traceID(r.Context())); err != nil {
			writeStoreError(w, r, err)
			return
		}
	}
	writeJSON(w, 200, map[string]any{"released": released})
}
func (a *API) renewLease(w http.ResponseWriter, r *http.Request) {
	p := principal(r)
	sessionID, leaseID, ok := parseLeasePath(w, r)
	if !ok {
		return
	}
	permission, permissionErr := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if permissionErr != nil && !errors.Is(permissionErr, store.ErrDenied) {
		// A transient authority-store failure must not destructively release a
		// lease that may still be valid. Fail the renewal and let its short TTL
		// or a later authoritative check decide the lease state.
		writeStoreError(w, r, permissionErr)
		return
	}
	if permissionErr != nil || permission != "CONTROL" && permission != "MANAGE" {
		// A downgrade, expiry, route disable, or device revocation invalidates
		// the lease before it can be extended. Best-effort cleanup prevents the
		// stale holder from blocking an authorized controller for another TTL.
		existing, leaseErr := a.Valkey.GetLease(r.Context(), sessionID)
		if leaseErr != nil && !errors.Is(leaseErr, redis.Nil) {
			writeError(w, r, 503, "LEASE_UNAVAILABLE", leaseErr)
			return
		}
		if leaseErr == nil && existing.ID == leaseID {
			accepted, released, releaseErr := a.Valkey.ReleaseForDevice(r.Context(), existing, p.Device.ID)
			if releaseErr != nil {
				writeError(w, r, 503, "LEASE_UNAVAILABLE", releaseErr)
				return
			}
			if accepted && !released {
				writeError(w, r, 403, "CONTROL_NOT_GRANTED", store.ErrDenied)
				return
			}
		}
		if err := a.Store.DeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, leaseID, "lease.revoke.permission", traceID(r.Context())); err != nil {
			writeStoreError(w, r, err)
			return
		}
		writeError(w, r, 403, "CONTROL_NOT_GRANTED", store.ErrDenied)
		return
	}
	lease, err := a.Valkey.GetLease(r.Context(), sessionID)
	if errors.Is(err, redis.Nil) {
		if err := a.Store.DeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, leaseID, "lease.release", traceID(r.Context())); err != nil {
			writeStoreError(w, r, err)
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if err != nil {
		writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		return
	}
	if lease.ID != leaseID {
		writeError(w, r, 409, "LEASE_NOT_HELD", store.ErrConflict)
		return
	}
	lease, err = a.Valkey.RenewForDevice(r.Context(), lease, p.Device.ID, 30*time.Second)
	if err != nil {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	if err := a.Store.SaveLease(r.Context(), p.UserID, p.Device.ID, lease.ID, lease.SessionID, lease.HolderDeviceID, lease.ExpiresAt, lease.Version, "", traceID(r.Context())); err != nil {
		// Valkey is the live arbitration point, but PostgreSQL is the durable
		// fencing record. Do not leave a newly extended cache lease active when
		// its durable renewal was rejected or could not be committed.
		if accepted, released, releaseErr := a.Valkey.ReleaseForDevice(r.Context(), lease, p.Device.ID); releaseErr != nil {
			slog.Warn("roll back lease renewal in Valkey", "error", releaseErr)
		} else if accepted && released {
			if deleteErr := a.Store.DeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, leaseID, "", traceID(r.Context())); deleteErr != nil {
				slog.Warn("roll back durable lease renewal", "error", deleteErr)
			}
		}
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
	permission, err := a.Store.SessionPermission(r.Context(), p.UserID, p.Device.ID, sessionID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if permission != "CONTROL" && permission != "MANAGE" {
		writeError(w, r, 403, "CONTROL_NOT_GRANTED", store.ErrDenied)
		return
	}
	lease, err := a.Valkey.GetLease(r.Context(), sessionID)
	if errors.Is(err, redis.Nil) {
		// A previous attempt may already have removed the exact cache lease and
		// failed before PostgreSQL committed. Retrying the fenced durable delete
		// is safe even if another holder has since acquired a replacement.
		if err := a.Store.DeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, leaseID, "lease.release", traceID(r.Context())); err != nil {
			writeStoreError(w, r, err)
			return
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if err != nil {
		writeError(w, r, 503, "LEASE_UNAVAILABLE", err)
		return
	}
	if lease.ID != leaseID {
		writeError(w, r, 409, "LEASE_NOT_HELD", store.ErrConflict)
		return
	}
	accepted, released, err := a.Valkey.ReleaseForDevice(r.Context(), lease, p.Device.ID)
	if err != nil || !accepted {
		writeError(w, r, 409, "LEASE_NOT_HELD", err)
		return
	}
	if released {
		if err := a.Store.DeleteLease(r.Context(), p.UserID, p.Device.ID, sessionID, leaseID, "lease.release", traceID(r.Context())); err != nil {
			writeStoreError(w, r, err)
			return
		}
	}
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
		Kind                          string  `json:"kind"`
		KeyID                         string  `json:"key_id"`
		CiphertextSHA256              string  `json:"ciphertext_sha256"`
		SizeBytes                     int64   `json:"size_bytes"`
		StartSequence                 *uint64 `json:"start_seq"`
		EndSequence                   *uint64 `json:"end_seq"`
		SnapshotSequence              *uint64 `json:"snapshot_seq"`
		AsOfSnapshotSequence          *uint64 `json:"as_of_snapshot_seq"`
		FirstLineID                   *uint64 `json:"first_line_id"`
		LastLineID                    *uint64 `json:"last_line_id"`
		Rows                          *int    `json:"rows"`
		Columns                       *int    `json:"columns"`
		FormatName                    *string `json:"format_name"`
		FormatVersion                 *int    `json:"format_version"`
		TranscriptInitialBeforeLineID *uint64 `json:"transcript_initial_before_line_id"`
		TranscriptPageCount           *int    `json:"transcript_page_count"`
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
			*input.StartSequence > math.MaxInt64 || *input.EndSequence > math.MaxInt64 || input.SnapshotSequence != nil ||
			input.AsOfSnapshotSequence != nil || input.FirstLineID != nil || input.LastLineID != nil ||
			input.Rows != nil || input.Columns != nil || input.FormatName != nil || input.FormatVersion != nil ||
			input.TranscriptInitialBeforeLineID != nil || input.TranscriptPageCount != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_RANGE", errors.New("invalid chunk range"))
			return
		}
		start, end := int64(*input.StartSequence), int64(*input.EndSequence)
		item.StartSequence, item.EndSequence = &start, &end
	} else if input.Kind == "SNAPSHOT" {
		var transcriptInitialBeforeLineID *int64
		if input.TranscriptInitialBeforeLineID != nil {
			if *input.TranscriptInitialBeforeLineID > math.MaxInt64 {
				writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_SNAPSHOT", errors.New("invalid transcript publication metadata"))
				return
			}
			value := int64(*input.TranscriptInitialBeforeLineID)
			transcriptInitialBeforeLineID = &value
		}
		if input.SnapshotSequence == nil || input.Rows == nil || input.Columns == nil || input.FormatName == nil ||
			input.FormatVersion == nil || input.StartSequence != nil || input.EndSequence != nil ||
			input.AsOfSnapshotSequence != nil || input.FirstLineID != nil || input.LastLineID != nil ||
			validateHistorySnapshotMetadata(*input.SnapshotSequence, input.SizeBytes, *input.Rows, *input.Columns,
				*input.FormatName, *input.FormatVersion) != nil ||
			validateSnapshotTranscriptPublicationMetadata(*input.FormatName, transcriptInitialBeforeLineID, input.TranscriptPageCount) != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_SNAPSHOT", errors.New("unsupported snapshot metadata"))
			return
		}
		sequence := int64(*input.SnapshotSequence)
		item.SnapshotSequence, item.Rows, item.Columns, item.FormatName, item.FormatVersion = &sequence, input.Rows, input.Columns, input.FormatName, input.FormatVersion
		item.TranscriptInitialBeforeLineID, item.TranscriptPageCount = transcriptInitialBeforeLineID, input.TranscriptPageCount
	} else if input.Kind == "TRANSCRIPT" {
		if input.AsOfSnapshotSequence == nil || input.FirstLineID == nil || input.LastLineID == nil ||
			input.FormatName == nil || input.FormatVersion == nil || input.StartSequence != nil ||
			input.EndSequence != nil || input.SnapshotSequence != nil || input.Rows != nil || input.Columns != nil ||
			input.TranscriptInitialBeforeLineID != nil || input.TranscriptPageCount != nil ||
			validateHistoryTranscriptMetadata(*input.AsOfSnapshotSequence, *input.FirstLineID, *input.LastLineID,
				input.SizeBytes, *input.FormatName, *input.FormatVersion) != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_TRANSCRIPT", errors.New("unsupported transcript metadata"))
			return
		}
		asOf, first, last := int64(*input.AsOfSnapshotSequence), int64(*input.FirstLineID), int64(*input.LastLineID)
		item.AsOfSnapshotSequence, item.FirstLineID, item.LastLineID = &asOf, &first, &last
		item.FormatName, item.FormatVersion = input.FormatName, input.FormatVersion
	} else {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_KIND", errors.New("invalid history kind"))
		return
	}
	formatName := ""
	if item.FormatName != nil {
		formatName = *item.FormatName
	}
	recipientID, recipientEpoch, recipientErr := store.ParseHistoryRecipientKeyID(item.Kind, formatName, item.KeyID)
	if recipientErr != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_KEY_ID", recipientErr)
		return
	}
	item.RecipientDeviceID = recipientID
	item.RecipientKeyEpoch = recipientEpoch
	item.ObjectKey = fmt.Sprintf("accounts/%s/sessions/%s/%s/%s.bin", p.UserID, sessionID, strings.ToLower(input.Kind), item.ID)
	uploadStore := a.historyUploads
	if uploadStore == nil && a.Store != nil {
		uploadStore = a.Store
	}
	if uploadStore == nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", errors.New("history upload store is unavailable"))
		return
	}
	if err := uploadStore.CreateHistoryUpload(r.Context(), p.UserID, p.Device.ID, item); err != nil {
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

func validateHistorySnapshotMetadata(sequence uint64, sizeBytes int64, rows, columns int, formatName string, formatVersion int) error {
	if sequence > math.MaxInt64 {
		return store.ErrInvalidHistorySnapshot
	}
	return store.ValidateHistorySnapshotMetadata(int64(sequence), sizeBytes, rows, columns, formatName, formatVersion)
}

func validateHistoryTranscriptMetadata(asOfSnapshotSequence, firstLineID, lastLineID uint64, sizeBytes int64, formatName string, formatVersion int) error {
	if asOfSnapshotSequence > math.MaxInt64 || firstLineID > math.MaxInt64 || lastLineID > math.MaxInt64 {
		return store.ErrInvalidHistoryTranscript
	}
	return store.ValidateHistoryTranscriptMetadata(int64(asOfSnapshotSequence), int64(firstLineID), int64(lastLineID),
		sizeBytes, formatName, formatVersion)
}

func validateSnapshotTranscriptPublicationMetadata(formatName string, initialBeforeLineID *int64, pageCount *int) error {
	if initialBeforeLineID == nil && pageCount == nil {
		return nil
	}
	if initialBeforeLineID == nil || pageCount == nil || formatName != terminalSnapshotFormat ||
		*initialBeforeLineID < 1 || *pageCount < 0 || *pageCount > maxTranscriptPublicationPageCount {
		return store.ErrInvalidHistorySnapshot
	}
	return nil
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
	uploadStore := a.historyUploads
	if uploadStore == nil && a.Store != nil {
		uploadStore = a.Store
	}
	if uploadStore == nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", errors.New("history upload store is unavailable"))
		return
	}
	upload, err := uploadStore.PendingHistoryUpload(r.Context(), p.UserID, p.Device.ID, sessionID, uploadID)
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if upload.Kind == "SNAPSHOT" && (upload.SnapshotSequence == nil || upload.Rows == nil || upload.Columns == nil ||
		upload.FormatName == nil || upload.FormatVersion == nil || upload.StartSequence != nil || upload.EndSequence != nil ||
		upload.AsOfSnapshotSequence != nil || upload.FirstLineID != nil || upload.LastLineID != nil ||
		validateHistorySnapshotMetadata(uint64(*upload.SnapshotSequence), upload.SizeBytes, *upload.Rows, *upload.Columns,
			*upload.FormatName, *upload.FormatVersion) != nil ||
		validateSnapshotTranscriptPublicationMetadata(*upload.FormatName, upload.TranscriptInitialBeforeLineID, upload.TranscriptPageCount) != nil) {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_SNAPSHOT", store.ErrInvalidHistorySnapshot)
		return
	}
	if upload.Kind == "TRANSCRIPT" && (upload.AsOfSnapshotSequence == nil || upload.FirstLineID == nil ||
		upload.LastLineID == nil || upload.FormatName == nil || upload.FormatVersion == nil ||
		upload.StartSequence != nil || upload.EndSequence != nil || upload.SnapshotSequence != nil ||
		upload.Rows != nil || upload.Columns != nil || upload.TranscriptInitialBeforeLineID != nil ||
		upload.TranscriptPageCount != nil ||
		validateHistoryTranscriptMetadata(uint64(*upload.AsOfSnapshotSequence), uint64(*upload.FirstLineID),
			uint64(*upload.LastLineID), upload.SizeBytes, *upload.FormatName, *upload.FormatVersion) != nil) {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_TRANSCRIPT", store.ErrInvalidHistoryTranscript)
		return
	}
	if upload.Kind == "CHUNK" && (upload.TranscriptInitialBeforeLineID != nil || upload.TranscriptPageCount != nil) {
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_RANGE", errors.New("invalid chunk range"))
		return
	}
	if err := a.History.VerifyCiphertext(r.Context(), upload.ObjectKey, upload.SizeBytes, upload.CiphertextSHA256); err != nil {
		writeError(w, r, http.StatusUnprocessableEntity, "HISTORY_INTEGRITY_FAILED", err)
		return
	}
	item, err := uploadStore.CommitHistoryUpload(r.Context(), p.UserID, p.Device.ID, sessionID, uploadID)
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
	if err := a.Store.MarkSessionDeleted(r.Context(), p.UserID, p.Device.ID, sessionID, traceID(r.Context())); err != nil {
		writeStoreError(w, r, err)
		return
	}
	// PostgreSQL is the authority fence. Once its tombstone commits, no new
	// lease can be authorized for this session, so it is safe to remove any
	// stale live-arbitration entry without risking deletion of a replacement.
	// A retry remains idempotent if Valkey is temporarily unavailable here.
	if a.Valkey != nil {
		if _, _, err := a.Valkey.ForceRelease(r.Context(), sessionID); err != nil {
			writeError(w, r, http.StatusServiceUnavailable, "LEASE_UNAVAILABLE", err)
			return
		}
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
	if a.transcriptHistoryRequested(r, p.UserID, p.Device.ID, sessionID) {
		a.listTranscriptHistory(w, r, p.UserID, p.Device.ID, sessionID)
		return
	}
	pageRequest, cursorRequest, queryErr := a.parseHistoryPageRequest(r, p.UserID, p.Device.ID, sessionID)
	if queryErr != nil {
		writeError(w, r, http.StatusBadRequest, queryErr.Code, queryErr)
		return
	}
	pageStore := a.historyPages
	if pageStore == nil && a.Store != nil {
		pageStore = a.Store
	}
	if pageStore == nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", errors.New("history page store is unavailable"))
		return
	}
	page, listErr := pageStore.ListHistoryPage(r.Context(), p.UserID, p.Device.ID, sessionID, pageRequest)
	if listErr != nil {
		switch {
		case errors.Is(listErr, store.ErrHistoryAnchorAhead):
			code := "INVALID_HISTORY_AS_OF"
			if cursorRequest {
				code = "INVALID_HISTORY_CURSOR"
			}
			writeError(w, r, http.StatusBadRequest, code, listErr)
		case errors.Is(listErr, store.ErrInvalidHistoryPage):
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_PAGE", listErr)
		default:
			writeStoreError(w, r, listErr)
		}
		return
	}
	// The PostgreSQL implementation already returns at most request.Limit
	// items. Keep a handler-side bound as defense in depth so a replacement
	// implementation cannot accidentally trigger unbounded URL signing.
	if len(page.Items) > pageRequest.Limit {
		page.Items = page.Items[:pageRequest.Limit]
		page.HasMore = true
	}
	asOf, tokenErr := a.encodeHistoryAsOf(p.UserID, p.Device.ID, sessionID, page.AsOfRevision)
	if tokenErr != nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", tokenErr)
		return
	}
	var nextCursor any
	if page.HasMore && len(page.Items) > 0 {
		last := page.Items[len(page.Items)-1]
		next, encodeErr := a.encodeHistoryCursor(
			p.UserID, p.Device.ID, sessionID, pageRequest.Kind, pageRequest.FormatName, page.AsOfRevision, last,
		)
		if encodeErr != nil {
			writeError(w, r, http.StatusInternalServerError, "INTERNAL", encodeErr)
			return
		}
		nextCursor = next
	}
	// Requests without page selectors historically received the same top-level
	// items/plaintext_index response. Preserve their item metadata shape while
	// sourcing the data from the bounded first page.
	responseItems, responseErr := a.historyResponseItems(r.Context(), page.Items, historyPaginationRequested(r))
	if responseErr != nil {
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", responseErr)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"items": responseItems, "as_of": asOf, "next_cursor": nextCursor,
		"has_more": page.HasMore, "order": "sequence_desc", "plaintext_index": false,
	})
}

func (a *API) transcriptHistoryRequested(r *http.Request, userID, deviceID, sessionID uuid.UUID) bool {
	query := r.URL.Query()
	if _, present := query["as_of_snapshot_seq"]; present {
		return true
	}
	if _, present := query["before_line_id"]; present {
		return true
	}
	if kinds, present := query["kind"]; present {
		for _, kind := range kinds {
			if strings.EqualFold(kind, string(store.HistoryKindTranscript)) {
				return true
			}
		}
	}
	if cursors := query["cursor"]; len(cursors) == 1 {
		_, err := a.decodeTranscriptCursor(cursors[0], userID, deviceID, sessionID)
		return err == nil
	}
	return false
}

func (a *API) listTranscriptHistory(w http.ResponseWriter, r *http.Request, userID, deviceID, sessionID uuid.UUID) {
	request, cursorRequest, queryErr := a.parseTranscriptPageRequest(r, userID, deviceID, sessionID)
	if queryErr != nil {
		writeError(w, r, http.StatusBadRequest, queryErr.Code, queryErr)
		return
	}
	pageStore := a.transcriptPages
	if pageStore == nil && a.Store != nil {
		pageStore = a.Store
	}
	if pageStore == nil {
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", errors.New("transcript page store is unavailable"))
		return
	}
	page, err := pageStore.ListTranscriptPage(r.Context(), userID, deviceID, sessionID, request)
	if err != nil {
		switch {
		case errors.Is(err, store.ErrHistoryAnchorAhead):
			code := "INVALID_HISTORY_PAGE"
			if cursorRequest {
				code = "INVALID_HISTORY_CURSOR"
			}
			writeError(w, r, http.StatusBadRequest, code, err)
		case errors.Is(err, store.ErrInvalidHistoryPage):
			writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_PAGE", err)
		default:
			writeStoreError(w, r, err)
		}
		return
	}
	responseItems, err := a.historyResponseItems(r.Context(), page.Items, true)
	if err != nil {
		writeError(w, r, http.StatusServiceUnavailable, "HISTORY_UNAVAILABLE", err)
		return
	}
	var nextCursor any
	if page.Ready && page.HasMore && len(page.Items) == 1 {
		next, err := a.encodeTranscriptCursor(userID, deviceID, sessionID, page.AsOfRevision,
			request.AsOfSnapshotSequence, int64(page.Items[0].FirstLineID))
		if err != nil {
			writeError(w, r, http.StatusInternalServerError, "INTERNAL", err)
			return
		}
		nextCursor = next
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"items": responseItems, "as_of_snapshot_seq": uint64(request.AsOfSnapshotSequence),
		"next_cursor": nextCursor, "has_more": page.HasMore,
		"ready": page.Ready, "order": "line_id_desc_pages", "plaintext_index": false,
	})
}

type historyPageQueryError struct {
	Code string
}

func (e *historyPageQueryError) Error() string { return e.Code }

type historyAsOfToken struct {
	Version   int    `json:"v"`
	Purpose   string `json:"p"`
	UserID    string `json:"u"`
	DeviceID  string `json:"d"`
	SessionID string `json:"s"`
	Revision  int64  `json:"r"`
}

type historyCursorToken struct {
	Version       int               `json:"v"`
	Purpose       string            `json:"p"`
	UserID        string            `json:"u"`
	DeviceID      string            `json:"d"`
	SessionID     string            `json:"s"`
	Kind          store.HistoryKind `json:"k"`
	FormatName    string            `json:"f,omitempty"`
	Revision      int64             `json:"a"`
	Sequence      int64             `json:"q"`
	BeforeKind    store.HistoryKind `json:"b"`
	HistoryItemID string            `json:"i"`
}

type transcriptCursorToken struct {
	Version              int    `json:"v"`
	Purpose              string `json:"p"`
	UserID               string `json:"u"`
	DeviceID             string `json:"d"`
	SessionID            string `json:"s"`
	Revision             int64  `json:"r"`
	AsOfSnapshotSequence int64  `json:"a"`
	BeforeLineID         int64  `json:"b"`
}

func historyPaginationRequested(r *http.Request) bool {
	query := r.URL.Query()
	for _, name := range []string{"limit", "kind", "format_name", "as_of", "cursor", "as_of_snapshot_seq", "before_line_id"} {
		if _, ok := query[name]; ok {
			return true
		}
	}
	return false
}

func (a *API) parseHistoryPageRequest(r *http.Request, userID, deviceID, sessionID uuid.UUID) (store.HistoryPageRequest, bool, *historyPageQueryError) {
	query := r.URL.Query()
	for name := range query {
		if name != "limit" && name != "kind" && name != "format_name" && name != "as_of" && name != "cursor" && name != "legacy" {
			return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
		}
	}
	// Older clients may send legacy=true. It no longer selects an unbounded
	// implementation; accept one compatibility value and use the normal first
	// page below.
	if _, legacyPresent, legacyErr := singleHistoryQueryValue(query, "legacy"); legacyErr != nil {
		return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	} else if legacyPresent && historyPaginationRequested(r) {
		return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	}
	limit := 50
	if raw, present, valueErr := singleHistoryQueryValue(query, "limit"); valueErr != nil {
		valueErr.Code = "INVALID_HISTORY_LIMIT"
		return store.HistoryPageRequest{}, false, valueErr
	} else if present {
		parsed, parseErr := strconv.ParseUint(raw, 10, 8)
		if parseErr != nil || parsed < 1 || parsed > 100 {
			return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_LIMIT"}
		}
		limit = int(parsed)
	}
	cursor, hasCursor, cursorValueErr := singleHistoryQueryValue(query, "cursor")
	if cursorValueErr != nil {
		cursorValueErr.Code = "INVALID_HISTORY_CURSOR"
		return store.HistoryPageRequest{}, true, cursorValueErr
	}
	if hasCursor {
		if _, hasKind := query["kind"]; hasKind {
			return store.HistoryPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		if _, hasAsOf := query["as_of"]; hasAsOf {
			return store.HistoryPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		if _, hasFormat := query["format_name"]; hasFormat {
			return store.HistoryPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		payload, decodeErr := a.decodeHistoryCursor(cursor, userID, deviceID, sessionID)
		if decodeErr != nil {
			return store.HistoryPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		itemID, parseErr := uuid.Parse(payload.HistoryItemID)
		if parseErr != nil {
			return store.HistoryPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		return store.HistoryPageRequest{
			Kind: payload.Kind, FormatName: payload.FormatName, Limit: limit, AsOfRevision: &payload.Revision,
			Before: &store.HistoryPosition{Sequence: payload.Sequence, Kind: payload.BeforeKind, ID: itemID},
		}, true, nil
	}

	kind := store.HistoryKindAll
	if raw, present, kindErr := singleHistoryQueryValue(query, "kind"); kindErr != nil {
		kindErr.Code = "INVALID_HISTORY_KIND"
		return store.HistoryPageRequest{}, false, kindErr
	} else if present {
		kind = store.HistoryKind(strings.ToLower(raw))
		if kind != store.HistoryKindAll && kind != store.HistoryKindChunk && kind != store.HistoryKindSnapshot {
			return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_KIND"}
		}
	}
	formatName := ""
	if raw, present, formatErr := singleHistoryQueryValue(query, "format_name"); formatErr != nil {
		formatErr.Code = "INVALID_HISTORY_FORMAT"
		return store.HistoryPageRequest{}, false, formatErr
	} else if present {
		if kind != store.HistoryKindSnapshot ||
			raw != store.HistoryFormatBoundedEventReplay && raw != store.HistoryFormatTerminalSnapshot {
			return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_FORMAT"}
		}
		formatName = raw
	}
	var revision *int64
	if raw, present, asOfErr := singleHistoryQueryValue(query, "as_of"); asOfErr != nil {
		asOfErr.Code = "INVALID_HISTORY_AS_OF"
		return store.HistoryPageRequest{}, false, asOfErr
	} else if present {
		payload, decodeErr := a.decodeHistoryAsOf(raw, userID, deviceID, sessionID)
		if decodeErr != nil {
			return store.HistoryPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_AS_OF"}
		}
		revision = &payload.Revision
	}
	return store.HistoryPageRequest{Kind: kind, FormatName: formatName, Limit: limit, AsOfRevision: revision}, false, nil
}

func (a *API) parseTranscriptPageRequest(r *http.Request, userID, deviceID, sessionID uuid.UUID) (store.TranscriptPageRequest, bool, *historyPageQueryError) {
	query := r.URL.Query()
	for name := range query {
		if name != "limit" && name != "kind" && name != "as_of_snapshot_seq" && name != "before_line_id" && name != "cursor" {
			return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
		}
	}
	if raw, present, valueErr := singleHistoryQueryValue(query, "limit"); valueErr != nil {
		valueErr.Code = "INVALID_HISTORY_LIMIT"
		return store.TranscriptPageRequest{}, false, valueErr
	} else if present && raw != "1" {
		return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_LIMIT"}
	}

	cursor, hasCursor, cursorErr := singleHistoryQueryValue(query, "cursor")
	if cursorErr != nil {
		cursorErr.Code = "INVALID_HISTORY_CURSOR"
		return store.TranscriptPageRequest{}, true, cursorErr
	}
	if hasCursor {
		if _, present := query["kind"]; present {
			return store.TranscriptPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		if _, present := query["as_of_snapshot_seq"]; present {
			return store.TranscriptPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		if _, present := query["before_line_id"]; present {
			return store.TranscriptPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		payload, err := a.decodeTranscriptCursor(cursor, userID, deviceID, sessionID)
		if err != nil {
			return store.TranscriptPageRequest{}, true, &historyPageQueryError{Code: "INVALID_HISTORY_CURSOR"}
		}
		return store.TranscriptPageRequest{
			AsOfSnapshotSequence: payload.AsOfSnapshotSequence,
			BeforeLineID:         &payload.BeforeLineID,
			AsOfRevision:         &payload.Revision,
			Limit:                1,
		}, true, nil
	}

	kind, hasKind, kindErr := singleHistoryQueryValue(query, "kind")
	if kindErr != nil || !hasKind || !strings.EqualFold(kind, string(store.HistoryKindTranscript)) {
		return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_KIND"}
	}
	asOfText, hasAsOf, asOfErr := singleHistoryQueryValue(query, "as_of_snapshot_seq")
	if asOfErr != nil || !hasAsOf {
		return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	}
	asOf, err := strconv.ParseUint(asOfText, 10, 64)
	if err != nil || asOf < 1 || asOf > math.MaxInt64 {
		return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	}
	var before *int64
	if beforeText, present, beforeErr := singleHistoryQueryValue(query, "before_line_id"); beforeErr != nil {
		return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	} else if present {
		value, err := strconv.ParseUint(beforeText, 10, 64)
		if err != nil || value < 1 || value > math.MaxInt64 {
			return store.TranscriptPageRequest{}, false, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
		}
		converted := int64(value)
		before = &converted
	}
	return store.TranscriptPageRequest{
		AsOfSnapshotSequence: int64(asOf), BeforeLineID: before, Limit: 1,
	}, false, nil
}

func singleHistoryQueryValue(query map[string][]string, name string) (string, bool, *historyPageQueryError) {
	values, present := query[name]
	if !present {
		return "", false, nil
	}
	if len(values) != 1 || values[0] == "" {
		return "", true, &historyPageQueryError{Code: "INVALID_HISTORY_PAGE"}
	}
	return values[0], true, nil
}

func (a *API) encodeHistoryAsOf(userID, deviceID, sessionID uuid.UUID, revision int64) (string, error) {
	payload := historyAsOfToken{Version: 1, Purpose: "history-as-of", UserID: userID.String(), DeviceID: deviceID.String(), SessionID: sessionID.String(), Revision: revision}
	return a.encodeHistoryPageToken("TERMLINK-HISTORY-ASOF-V1", payload)
}

func (a *API) decodeHistoryAsOf(token string, userID, deviceID, sessionID uuid.UUID) (historyAsOfToken, error) {
	var payload historyAsOfToken
	if err := a.decodeHistoryPageToken("TERMLINK-HISTORY-ASOF-V1", token, &payload); err != nil {
		return historyAsOfToken{}, err
	}
	if payload.Version != 1 || payload.Purpose != "history-as-of" || payload.UserID != userID.String() || payload.DeviceID != deviceID.String() ||
		payload.SessionID != sessionID.String() || payload.Revision < 0 {
		return historyAsOfToken{}, errors.New("invalid history as-of token")
	}
	return payload, nil
}

func (a *API) encodeHistoryCursor(userID, deviceID, sessionID uuid.UUID, kind store.HistoryKind, formatName string, revision int64, item store.HistoryItem) (string, error) {
	beforeKind := store.HistoryKind(item.Kind)
	sequence := int64(item.EndSequence)
	if beforeKind == store.HistoryKindSnapshot {
		sequence = int64(item.SnapshotSequence)
	}
	payload := historyCursorToken{
		Version: 1, Purpose: "history-cursor", UserID: userID.String(), DeviceID: deviceID.String(), SessionID: sessionID.String(),
		Kind: kind, FormatName: formatName, Revision: revision, Sequence: sequence, BeforeKind: beforeKind, HistoryItemID: item.ID.String(),
	}
	return a.encodeHistoryPageToken("TERMLINK-HISTORY-CURSOR-V1", payload)
}

func (a *API) decodeHistoryCursor(token string, userID, deviceID, sessionID uuid.UUID) (historyCursorToken, error) {
	var payload historyCursorToken
	if err := a.decodeHistoryPageToken("TERMLINK-HISTORY-CURSOR-V1", token, &payload); err != nil {
		return historyCursorToken{}, err
	}
	validKind := payload.Kind == store.HistoryKindAll || payload.Kind == store.HistoryKindChunk || payload.Kind == store.HistoryKindSnapshot
	validBeforeKind := payload.BeforeKind == store.HistoryKindChunk || payload.BeforeKind == store.HistoryKindSnapshot
	validFormat := payload.FormatName == "" || payload.Kind == store.HistoryKindSnapshot &&
		(payload.FormatName == store.HistoryFormatBoundedEventReplay || payload.FormatName == store.HistoryFormatTerminalSnapshot)
	if payload.Version != 1 || payload.Purpose != "history-cursor" || payload.UserID != userID.String() || payload.DeviceID != deviceID.String() ||
		payload.SessionID != sessionID.String() || payload.Revision < 0 || payload.Sequence < 0 || !validKind ||
		!validBeforeKind || !validFormat || (payload.Kind != store.HistoryKindAll && payload.Kind != payload.BeforeKind) {
		return historyCursorToken{}, errors.New("invalid history cursor")
	}
	itemID, err := uuid.Parse(payload.HistoryItemID)
	if err != nil || itemID.String() != payload.HistoryItemID {
		return historyCursorToken{}, errors.New("invalid history cursor item")
	}
	return payload, nil
}

func (a *API) encodeTranscriptCursor(userID, deviceID, sessionID uuid.UUID, revision, asOfSnapshotSequence, beforeLineID int64) (string, error) {
	if revision < 0 || asOfSnapshotSequence < 1 || beforeLineID < 1 {
		return "", errors.New("invalid transcript cursor")
	}
	payload := transcriptCursorToken{
		Version: 1, Purpose: "transcript-cursor", UserID: userID.String(), DeviceID: deviceID.String(),
		SessionID: sessionID.String(), Revision: revision, AsOfSnapshotSequence: asOfSnapshotSequence,
		BeforeLineID: beforeLineID,
	}
	return a.encodeHistoryPageToken("TERMLINK-TRANSCRIPT-CURSOR-V1", payload)
}

func (a *API) decodeTranscriptCursor(token string, userID, deviceID, sessionID uuid.UUID) (transcriptCursorToken, error) {
	var payload transcriptCursorToken
	if err := a.decodeHistoryPageToken("TERMLINK-TRANSCRIPT-CURSOR-V1", token, &payload); err != nil {
		return transcriptCursorToken{}, err
	}
	if payload.Version != 1 || payload.Purpose != "transcript-cursor" ||
		payload.UserID != userID.String() || payload.DeviceID != deviceID.String() ||
		payload.SessionID != sessionID.String() || payload.Revision < 0 ||
		payload.AsOfSnapshotSequence < 1 || payload.BeforeLineID < 1 {
		return transcriptCursorToken{}, errors.New("invalid transcript cursor")
	}
	return payload, nil
}

func (a *API) encodeHistoryPageToken(domain string, value any) (string, error) {
	if len(a.TokenKey) < 32 {
		return "", errors.New("history pagination signing key is unavailable")
	}
	payload, err := json.Marshal(value)
	if err != nil {
		return "", err
	}
	encoded := base64.RawURLEncoding.EncodeToString(payload)
	mac := hmac.New(sha256.New, a.TokenKey)
	_, _ = mac.Write([]byte(domain + "\n" + encoded))
	return encoded + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil)), nil
}

func (a *API) decodeHistoryPageToken(domain, token string, destination any) error {
	if len(a.TokenKey) < 32 || len(token) == 0 || len(token) > maxHistoryPageTokenBytes || strings.Count(token, ".") != 1 {
		return errors.New("invalid history page token")
	}
	encoded, signatureText, _ := strings.Cut(token, ".")
	signature, err := base64.RawURLEncoding.DecodeString(signatureText)
	if err != nil || len(signature) != sha256.Size {
		return errors.New("invalid history page signature")
	}
	mac := hmac.New(sha256.New, a.TokenKey)
	_, _ = mac.Write([]byte(domain + "\n" + encoded))
	if !hmac.Equal(signature, mac.Sum(nil)) {
		return errors.New("invalid history page signature")
	}
	payload, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil || len(payload) == 0 || len(payload) > maxHistoryPageTokenBytes {
		return errors.New("invalid history page payload")
	}
	decoder := json.NewDecoder(bytes.NewReader(payload))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return err
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return errors.New("single history page payload required")
	}
	return nil
}

func (a *API) historyResponseItems(ctx context.Context, items []store.HistoryItem, paged bool) ([]map[string]any, error) {
	responseItems := make([]map[string]any, 0, len(items))
	for _, item := range items {
		if a.History == nil {
			return nil, errors.New("history object store is disabled")
		}
		download, err := a.History.PresignGet(ctx, item.ObjectKey, 5*time.Minute)
		if err != nil {
			return nil, err
		}
		response := map[string]any{
			"kind": item.Kind, "id": item.ID, "key_id": item.KeyID,
			"ciphertext_sha256": base64.RawURLEncoding.EncodeToString(item.CiphertextSHA256),
			"size_bytes":        item.SizeBytes, "created_at": item.CreatedAt, "download": download,
		}
		if paged {
			if item.Kind == string(store.HistoryKindSnapshot) {
				response["snapshot_seq"] = item.SnapshotSequence
				response["rows"] = item.Rows
				response["columns"] = item.Columns
				response["format_name"] = item.FormatName
				response["format_version"] = item.FormatVersion
			} else if item.Kind == string(store.HistoryKindTranscript) {
				response["as_of_snapshot_seq"] = item.AsOfSnapshotSequence
				response["first_line_id"] = item.FirstLineID
				response["last_line_id"] = item.LastLineID
				response["format_name"] = item.FormatName
				response["format_version"] = item.FormatVersion
			} else {
				response["start_seq"] = item.StartSequence
				response["end_seq"] = item.EndSequence
			}
		} else {
			response["start_seq"] = item.StartSequence
			response["end_seq"] = item.EndSequence
			response["snapshot_seq"] = item.SnapshotSequence
			response["format_name"] = item.FormatName
			response["format_version"] = item.FormatVersion
			if item.Kind == string(store.HistoryKindSnapshot) {
				response["rows"] = item.Rows
				response["columns"] = item.Columns
			}
		}
		responseItems = append(responseItems, response)
	}
	return responseItems, nil
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
	body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
	if err != nil {
		return err
	}
	if len(body) > maxJSONBody {
		return errors.New("JSON body exceeds limit")
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
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
	case errors.Is(err, store.ErrSessionActive):
		writeError(w, r, http.StatusConflict, "SESSION_ACTIVE", err)
	case errors.Is(err, store.ErrInvalidHistorySnapshot):
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_SNAPSHOT", err)
	case errors.Is(err, store.ErrInvalidHistoryTranscript):
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_TRANSCRIPT", err)
	case errors.Is(err, store.ErrInvalidHistoryRecipient):
		writeError(w, r, http.StatusBadRequest, "INVALID_HISTORY_KEY_ID", err)
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

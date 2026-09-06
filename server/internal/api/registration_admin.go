package api

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	maxGeneratedInvitationCodeCount = 1000
	invitationCodeChars             = 12
	registrationRateValidate        = "invitation-validate"
	registrationRateCodeGeneration  = "invitation-code-generation"
)

const registrationCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

var errInvalidInvitationCodeGeneration = errors.New("invalid invitation code generation request")

func registrationRateSpec(domain string) (int64, time.Duration, bool) {
	switch domain {
	case registrationRateValidate:
		return 12, time.Minute, true
	case registrationRateCodeGeneration:
		return 6, time.Minute, true
	default:
		return 0, 0, false
	}
}

func registrationRateLimitScope(r *http.Request, domain string) (string, error) {
	if _, _, valid := registrationRateSpec(domain); !valid {
		return "", errors.New("unknown registration rate-limit domain")
	}
	identity := accountRegistrationClientIdentity(r)
	if principal := principal(r); principal.UserID != uuid.Nil {
		identity += "\x00account:" + principal.UserID.String() + "\x00device:" + principal.Device.ID.String()
	}
	digest := sha256.Sum256([]byte("TermLink registration rate limit v1\x00" + domain + "\x00" + identity))
	return "rate:registration:" + domain + ":" + hex.EncodeToString(digest[:]), nil
}

func (a *API) consumeRegistrationRateLimit(r *http.Request, domain string) (bool, error) {
	limit, window, valid := registrationRateSpec(domain)
	if !valid || a.Valkey == nil {
		return false, errors.New("registration rate limiter is unavailable")
	}
	key, err := registrationRateLimitScope(r, domain)
	if err != nil {
		return false, err
	}
	return a.Valkey.Allow(r.Context(), key, limit, window)
}

func (a *API) recordRegistrationRateLimitFailure(r *http.Request, domain string, unavailable bool) {
	reason, result := "LIMIT_EXCEEDED", "DENIED"
	if unavailable {
		reason, result = "LIMITER_UNAVAILABLE", "FAILED"
	}
	metadata, _ := json.Marshal(map[string]string{"domain": domain, "reason": reason})
	a.recordSecurityEvent(r, "registration.rate_limit.denied", "registration", result, metadata)
}

func registrationLimit(r *http.Request) (int, error) {
	raw := strings.TrimSpace(r.URL.Query().Get("limit"))
	if raw == "" {
		return 50, nil
	}
	limit, err := strconv.Atoi(raw)
	if err != nil || limit < 1 || limit > 200 {
		return 0, errors.New("limit must be between 1 and 200")
	}
	return limit, nil
}

func registrationPathUUID(r *http.Request, name string) (uuid.UUID, error) {
	value, err := uuid.Parse(r.PathValue(name))
	if err != nil || value == uuid.Nil {
		return uuid.Nil, errors.New("invalid UUID path value")
	}
	return value, nil
}

func (a *API) requireRegistrationAdmin(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	repository := a.securityStore()
	if repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", errors.New("registration store is unavailable"))
		return uuid.Nil, false
	}
	principal := principal(r)
	administrator, err := repository.IsAdmin(r.Context(), principal.UserID)
	if err != nil {
		writeStoreError(w, r, err)
		return uuid.Nil, false
	}
	if !administrator {
		_ = repository.Audit(r.Context(), principal.UserID, principal.Device.ID, "registration.admin.denied", "registration", uuid.Nil, "DENIED", traceID(r.Context()), "{}")
		writeError(w, r, http.StatusForbidden, "ADMIN_REQUIRED", store.ErrDenied)
		return uuid.Nil, false
	}
	return principal.UserID, true
}

func (a *API) requireRegistrationStepUp(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	actorID, ok := a.requireRegistrationAdmin(w, r)
	if !ok || !a.requireRecentStepUp(w, r, actorID) {
		return uuid.Nil, false
	}
	return actorID, true
}

func (a *API) registrationAdminReadActor(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	if a.Store == nil {
		writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", errors.New("registration store is unavailable"))
		return uuid.Nil, false
	}
	return principal(r).UserID, true
}

func (a *API) adminGetRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	if _, ok := a.requireRegistrationAdmin(w, r); !ok {
		return
	}
	policy, err := a.Store.GetRegistrationPolicy(r.Context())
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"policy": registrationPolicyView(policy)})
}

type updateRegistrationPolicyRequest struct {
	Mode            string `json:"mode"`
	ExpectedVersion int64  `json:"expected_version"`
}

func (a *API) adminUpdateRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireRegistrationStepUp(w, r)
	if !ok {
		return
	}
	var input updateRegistrationPolicyRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if input.ExpectedVersion < 1 {
		writeError(w, r, http.StatusBadRequest, "INVALID_POLICY_VERSION", errors.New("expected_version must be positive"))
		return
	}
	policy, err := a.Store.UpdateRegistrationPolicy(r.Context(), actorID, input.Mode, input.ExpectedVersion, traceID(r.Context()))
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	a.auditOperationSuccess(r, actorID, "registration.policy.update.context", "registration_policy", uuid.Nil)
	writeJSON(w, http.StatusOK, map[string]any{"policy": registrationPolicyView(policy)})
}

type invitationBatchRequest struct {
	Name           string     `json:"name"`
	Status         string     `json:"status,omitempty"`
	DefaultMaxUses int        `json:"default_max_uses"`
	ValidFrom      *time.Time `json:"valid_from,omitempty"`
	ValidUntil     *time.Time `json:"valid_until,omitempty"`
	Email          string     `json:"email,omitempty"`
	EmailDomain    string     `json:"email_domain,omitempty"`
	Note           string     `json:"note,omitempty"`
}

func (a *API) createInvitationBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireRegistrationAdmin(w, r)
	if !ok {
		return
	}
	var input invitationBatchRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	if strings.TrimSpace(input.Email) != "" && strings.TrimSpace(input.EmailDomain) != "" {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_SCOPE", errors.New("email and email_domain are mutually exclusive"))
		return
	}
	var emailHash *[32]byte
	if strings.TrimSpace(input.Email) != "" {
		if len(a.registrationDigestKey()) < 32 {
			writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", errors.New("registration digest key is unavailable"))
			return
		}
		email, _, err := normalizedEmail(input.Email)
		if err != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_EMAIL", err)
			return
		}
		digest := keyedRegistrationDigest(a.registrationDigestKey(), "registration-email", email)
		emailHash = &digest
	}
	batch, err := a.Store.CreateInvitationBatch(r.Context(), actorID, store.InvitationBatchInput{
		Name: input.Name, Status: input.Status, DefaultMaxUses: input.DefaultMaxUses,
		ValidFrom: input.ValidFrom, ValidUntil: input.ValidUntil, EmailHash: emailHash,
		EmailDomain: input.EmailDomain, Note: input.Note,
	}, traceID(r.Context()))
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"batch": invitationBatchView(batch)})
}

func (a *API) listInvitationBatches(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.registrationAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := registrationLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationBatches(r.Context(), actorID, limit)
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, invitationBatchView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

func (a *API) listInvitationCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.registrationAdminReadActor(w, r)
	if !ok {
		return
	}
	batchID, err := uuid.Parse(strings.TrimSpace(r.URL.Query().Get("batch_id")))
	if err != nil || batchID == uuid.Nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", errors.New("batch_id is required"))
		return
	}
	limit, err := registrationLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationCodes(r.Context(), actorID, batchID, limit)
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": invitationCodeViews(items)})
}

func (a *API) disableInvitationBatch(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireRegistrationAdmin(w, r)
	if !ok {
		return
	}
	batchID, err := registrationPathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", err)
		return
	}
	if err := a.Store.SetInvitationBatchStatus(r.Context(), actorID, batchID, "DISABLED", traceID(r.Context())); err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "batch_id": batchID})
}

func (a *API) disableInvitationCode(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireRegistrationAdmin(w, r)
	if !ok {
		return
	}
	codeID, err := registrationPathUUID(r, "codeId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_CODE_ID", err)
		return
	}
	if err := a.Store.SetInvitationCodeStatus(r.Context(), actorID, codeID, "DISABLED", traceID(r.Context())); err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"disabled": true, "code_id": codeID})
}

func (a *API) listInvitationRedemptions(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.registrationAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := registrationLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := a.Store.ListInvitationRedemptions(r.Context(), actorID, limit)
	if err != nil {
		writeRegistrationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": invitationRedemptionViews(items)})
}

type generateInvitationCodesRequest struct {
	Count      int        `json:"count"`
	MaxUses    int        `json:"max_uses"`
	ValidFrom  *time.Time `json:"valid_from,omitempty"`
	ValidUntil *time.Time `json:"valid_until,omitempty"`
}

func (a *API) generateInvitationCodes(w http.ResponseWriter, r *http.Request) {
	actorID, ok := a.requireRegistrationStepUp(w, r)
	if !ok {
		return
	}
	allowed, err := a.consumeRegistrationRateLimit(r, registrationRateCodeGeneration)
	if err != nil {
		a.recordRegistrationRateLimitFailure(r, registrationRateCodeGeneration, true)
		writeError(w, r, http.StatusServiceUnavailable, "CODE_GENERATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordRegistrationRateLimitFailure(r, registrationRateCodeGeneration, false)
		writeError(w, r, http.StatusTooManyRequests, "CODE_GENERATION_RATE_LIMITED", errors.New("code generation rate limit reached"))
		return
	}
	batchID, err := registrationPathUUID(r, "batchId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_INVITATION_BATCH_ID", err)
		return
	}
	var input generateInvitationCodesRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	plaintext, records, err := generateInvitationCodes(a.registrationDigestKey(), input)
	if err != nil {
		writeInvitationCodeGenerationError(w, r, err)
		return
	}
	created, err := a.Store.AddInvitationCodes(r.Context(), actorID, batchID, records, traceID(r.Context()))
	if err != nil {
		writeInvitationCodeGenerationError(w, r, err)
		return
	}
	if len(created) != len(plaintext) {
		writeError(w, r, http.StatusInternalServerError, "CODE_GENERATION_INCOMPLETE", errors.New("persisted code count mismatch"))
		return
	}
	a.auditOperationSuccess(r, actorID, "registration.invitation.codes.generate.context", "invitation_batch", batchID)
	writeJSON(w, http.StatusCreated, map[string]any{
		"batch_id": batchID, "items": generatedInvitationCodeViews(plaintext, created),
		"plaintext_available_once": true,
	})
}

func generateInvitationCodes(key []byte, input generateInvitationCodesRequest) ([]string, []store.CodeDigestRecord, error) {
	if input.Count < 1 || input.Count > maxGeneratedInvitationCodeCount || input.MaxUses < 1 || input.MaxUses > 1_000_000 || len(key) < 32 {
		return nil, nil, errInvalidInvitationCodeGeneration
	}
	if input.ValidFrom != nil && input.ValidUntil != nil && !input.ValidUntil.After(*input.ValidFrom) {
		return nil, nil, errInvalidInvitationCodeGeneration
	}
	plaintext := make([]string, 0, input.Count)
	records := make([]store.CodeDigestRecord, 0, input.Count)
	seen := make(map[[32]byte]struct{}, input.Count)
	for len(records) < input.Count {
		normalized, display, err := newInvitationCode()
		if err != nil {
			return nil, nil, err
		}
		digest := keyedRegistrationDigest(key, "invitation-code", normalized)
		if _, duplicate := seen[digest]; duplicate {
			continue
		}
		seen[digest] = struct{}{}
		plaintext = append(plaintext, display)
		records = append(records, store.CodeDigestRecord{
			ID: uuid.New(), Digest: digest, Prefix: normalized[:8], MaxUses: input.MaxUses,
			ValidFrom: input.ValidFrom, ValidUntil: input.ValidUntil,
		})
	}
	return plaintext, records, nil
}

func newInvitationCode() (string, string, error) {
	randomBytes := make([]byte, invitationCodeChars)
	if _, err := rand.Read(randomBytes); err != nil {
		return "", "", err
	}
	characters := make([]byte, invitationCodeChars)
	for index, value := range randomBytes {
		characters[index] = registrationCodeAlphabet[int(value)&31]
	}
	randomPart := string(characters)
	normalized := "TLI" + randomPart
	var display strings.Builder
	display.WriteString("TLI")
	for index, character := range randomPart {
		if index%4 == 0 {
			display.WriteByte('-')
		}
		display.WriteRune(character)
	}
	return normalized, display.String(), nil
}

func generatedInvitationCodeViews(plaintext []string, stored []store.InvitationCode) []map[string]any {
	items := make([]map[string]any, 0, len(stored))
	for index, item := range stored {
		items = append(items, map[string]any{
			"id": item.ID, "code": plaintext[index], "prefix": item.Prefix,
			"max_uses": item.MaxUses, "valid_from": item.ValidFrom, "valid_until": item.ValidUntil,
		})
	}
	return items
}

func registrationPolicyView(item store.RegistrationPolicy) map[string]any {
	return map[string]any{"mode": item.Mode, "version": item.Version, "updated_by": item.UpdatedBy, "updated_at": item.UpdatedAt}
}

func invitationRedemptionViews(items []store.InvitationRedemption) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, map[string]any{
			"id": item.ID, "code_id": item.CodeID, "code_prefix": item.CodePrefix,
			"batch_name": item.BatchName, "registration_attempt_id": item.AttemptID,
			"user_id": item.UserID, "username": item.Username, "nickname": item.Nickname,
			"email_masked": item.EmailMasked, "profile_source": item.ProfileSource,
			"state": item.State, "reserved_until": item.ReservedUntil,
			"provider_accepted_at": item.ProviderAcceptedAt, "redeemed_at": item.RedeemedAt,
			"released_at": item.ReleasedAt, "created_at": item.CreatedAt,
		})
	}
	return views
}

func invitationBatchView(item store.InvitationBatch) map[string]any {
	return map[string]any{
		"id": item.ID, "name": item.Name, "status": item.Status,
		"default_max_uses": item.DefaultMaxUses, "valid_from": item.ValidFrom,
		"valid_until": item.ValidUntil, "email_domain": item.EmailDomain,
		"email_restricted": len(item.EmailHash) != 0, "note": item.Note,
		"created_by": item.CreatedBy, "created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

func invitationCodeViews(items []store.InvitationCode) []map[string]any {
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, map[string]any{
			"id": item.ID, "batch_id": item.BatchID, "prefix": item.Prefix,
			"status": item.Status, "max_uses": item.MaxUses, "used_count": item.UsedCount,
			"valid_from": item.ValidFrom, "valid_until": item.ValidUntil,
			"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
		})
	}
	return views
}

func writeInvitationCodeGenerationError(w http.ResponseWriter, r *http.Request, err error) {
	if errors.Is(err, errInvalidInvitationCodeGeneration) {
		writeError(w, r, http.StatusBadRequest, "INVALID_CODE_GENERATION", err)
		return
	}
	writeRegistrationError(w, r, err)
}

func writeRegistrationError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, store.ErrInvitationRequired):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_REQUIRED", err)
	case errors.Is(err, store.ErrInvitationInvalid):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_INVALID", err)
	case errors.Is(err, store.ErrInvitationExpired):
		writeError(w, r, http.StatusGone, "INVITATION_EXPIRED", err)
	case errors.Is(err, store.ErrInvitationExhausted):
		writeError(w, r, http.StatusConflict, "INVITATION_EXHAUSTED", err)
	case errors.Is(err, store.ErrInvitationEmailMismatch):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_EMAIL_MISMATCH", err)
	case errors.Is(err, store.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "NOT_FOUND", err)
	case errors.Is(err, store.ErrConflict):
		writeError(w, r, http.StatusConflict, "CONFLICT", err)
	case errors.Is(err, store.ErrDenied):
		writeError(w, r, http.StatusForbidden, "FORBIDDEN", err)
	default:
		writeStoreError(w, r, err)
	}
}

package api

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"strings"
	"time"
	"unicode"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const invitationReservationTTL = 20 * time.Minute

type invitationValidationRequest struct {
	InvitationCode string `json:"invitation_code"`
	Email          string `json:"email"`
}

// getRegistrationPolicy intentionally exposes only fields needed to render a
// registration form. Inventory, batches and risk rules remain private.
func (a *API) getRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	policy, err := a.Store.GetRegistrationPolicy(r.Context())
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	emailVerificationRequired := false
	if a.ServiceEmail != nil {
		var policyErr error
		emailVerificationRequired, policyErr = a.ServiceEmail.RegistrationVerificationPolicy(r.Context())
		if policyErr != nil {
			writeError(w, r, http.StatusServiceUnavailable, "EMAIL_SERVICE_UNAVAILABLE", policyErr)
			return
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"mode":                        policy.Mode,
		"invitation_needed":           policy.Mode == store.RegistrationInviteRequired,
		"registration_open":           policy.Mode != store.RegistrationClosed,
		"version":                     policy.Version,
		"email_verification_required": emailVerificationRequired,
	})
}

// validateInvitation is deliberately non-oracular: it validates the public
// form shape and policy requirement but never reveals whether a code exists,
// is exhausted, expired or belongs to another email address. Registration is
// the only endpoint that performs the atomic authoritative check.
func (a *API) validateInvitation(w http.ResponseWriter, r *http.Request) {
	allowed, err := a.consumeCommerceRateLimit(r, commerceRateInvitationValidate)
	if err != nil {
		a.recordCommerceRateLimitFailure(r, commerceRateInvitationValidate, true)
		writeError(w, r, http.StatusServiceUnavailable, "INVITATION_VALIDATION_UNAVAILABLE", err)
		return
	}
	if !allowed {
		a.recordCommerceRateLimitFailure(r, commerceRateInvitationValidate, false)
		writeError(w, r, http.StatusTooManyRequests, "INVITATION_RATE_LIMITED", errors.New("invitation validation rate limit reached"))
		return
	}
	var input invitationValidationRequest
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	policy, err := a.Store.GetRegistrationPolicy(r.Context())
	if err != nil {
		writeStoreError(w, r, err)
		return
	}
	if policy.Mode == store.RegistrationClosed {
		writeError(w, r, http.StatusForbidden, "REGISTRATION_CLOSED", store.ErrRegistrationClosed)
		return
	}
	code := normalizeCommerceCode(input.InvitationCode)
	if policy.Mode == store.RegistrationInviteRequired && code == "" {
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_REQUIRED", store.ErrInvitationRequired)
		return
	}
	if code != "" && !validCommerceCode(code, "TLI") {
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_INVALID", store.ErrInvitationInvalid)
		return
	}
	if strings.TrimSpace(input.Email) != "" {
		if _, _, err := normalizedEmail(input.Email); err != nil {
			writeError(w, r, http.StatusBadRequest, "INVALID_EMAIL", err)
			return
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		// Retained for older clients. This means only that the request shape may
		// proceed to registration; it is not an existence or inventory oracle.
		"accepted_for_registration": true,
		"validation_scope":          "FORMAT_AND_POLICY_ONLY",
		"authoritative":             false,
	})
}

func (a *API) reserveRegistrationInvitation(r *http.Request, rawCode, username, nickname, rawEmail string) (uuid.UUID, bool, error) {
	// Focused legacy unit tests construct the registration boundary without a
	// repository. Production startup always supplies Store and verifies schema.
	if a.Store == nil {
		return uuid.Nil, false, nil
	}
	policy, err := a.Store.GetRegistrationPolicy(r.Context())
	if err != nil {
		return uuid.Nil, false, err
	}
	if policy.Mode == store.RegistrationClosed {
		return uuid.Nil, false, store.ErrRegistrationClosed
	}
	code := normalizeCommerceCode(rawCode)
	if code == "" {
		if policy.Mode == store.RegistrationInviteRequired {
			return uuid.Nil, false, store.ErrInvitationRequired
		}
		return uuid.Nil, false, nil
	}
	if !validCommerceCode(code, "TLI") {
		return uuid.Nil, false, store.ErrInvitationInvalid
	}
	if len(a.commerceDigestKey()) < 32 {
		return uuid.Nil, false, errors.New("commerce digest key is unavailable")
	}
	email, domain, err := normalizedEmail(rawEmail)
	if err != nil {
		return uuid.Nil, false, err
	}
	attemptID := uuid.New()
	_, err = a.Store.ReserveInvitation(
		r.Context(), attemptID,
		keyedCommerceDigest(a.commerceDigestKey(), "invitation-code", code),
		keyedCommerceDigest(a.commerceDigestKey(), "registration-email", email),
		domain, invitationReservationTTL,
	)
	if err != nil {
		return uuid.Nil, false, err
	}
	profile := store.NewAccountIdentityProfile(username, nickname, email)
	if err = a.Store.RecordInvitationRegistrationProfile(r.Context(), attemptID, profile); err != nil {
		if releaseErr := a.Store.ReleaseInvitation(r.Context(), attemptID); releaseErr != nil {
			logAPIFailure(r.Context(), slog.LevelError, "registration profile rejected but invitation release failed",
				"registration_profile_compensation_failed", releaseErr,
				slog.String("registration_attempt_id", attemptID.String()))
		}
		return uuid.Nil, false, err
	}
	return attemptID, true, nil
}

func writeRegistrationAdmissionError(w http.ResponseWriter, r *http.Request, err error) bool {
	switch {
	case errors.Is(err, store.ErrRegistrationClosed):
		writeError(w, r, http.StatusForbidden, "REGISTRATION_CLOSED", err)
	case errors.Is(err, store.ErrInvitationRequired):
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_REQUIRED", err)
	case errors.Is(err, store.ErrInvitationInvalid), errors.Is(err, store.ErrInvitationExpired),
		errors.Is(err, store.ErrInvitationExhausted), errors.Is(err, store.ErrInvitationEmailMismatch):
		// Public callers receive one indistinguishable error to resist code
		// enumeration; precise causes remain available to internal metrics.
		writeError(w, r, http.StatusUnprocessableEntity, "INVITATION_INVALID", err)
	default:
		return false
	}
	return true
}

func keyedCommerceDigest(key []byte, purpose, value string) [32]byte {
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte("TermLink commerce v1\x00"))
	_, _ = mac.Write([]byte(purpose))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(value))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func (a *API) commerceDigestKey() []byte {
	if len(a.CommerceKey) >= 32 {
		return a.CommerceKey
	}
	// Compatibility for focused tests and staged upgrades. Production startup
	// injects a purpose-derived CommerceKey and never takes this branch.
	return a.TokenKey
}

func normalizeCommerceCode(value string) string {
	value = strings.ToUpper(strings.TrimSpace(value))
	var output strings.Builder
	output.Grow(len(value))
	for _, character := range value {
		if character == '-' || unicode.IsSpace(character) {
			continue
		}
		if character >= 'A' && character <= 'Z' || character >= '2' && character <= '9' {
			output.WriteRune(character)
			continue
		}
		return ""
	}
	return output.String()
}

func validCommerceCode(code, prefix string) bool {
	minimumLength := 24
	if prefix == "TLI" {
		minimumLength = len(prefix) + invitationCodeChars
	}
	if len(code) < minimumLength || len(code) > 64 || !strings.HasPrefix(code, prefix) {
		return false
	}
	for _, character := range code {
		if character < 'A' || character > 'Z' {
			if character < '2' || character > '9' {
				return false
			}
		}
	}
	return true
}

func normalizedEmail(value string) (string, string, error) {
	email := strings.ToLower(strings.TrimSpace(value))
	if len(email) < 3 || len(email) > 320 || strings.ContainsAny(email, "\r\n\t ") {
		return "", "", errors.New("email is invalid")
	}
	local, domain, ok := strings.Cut(email, "@")
	if !ok || local == "" || domain == "" || strings.Contains(domain, "@") || len(domain) > 253 {
		return "", "", errors.New("email is invalid")
	}
	return email, domain, nil
}

func commerceJSON(value any) json.RawMessage {
	encoded, _ := json.Marshal(value)
	return encoded
}

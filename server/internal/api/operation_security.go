package api

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	operationStepUpMaximumAge = 10 * time.Minute
	operationReasonHeader     = "X-TermLink-Operation-Reason"
)

type operationAuditKey struct{}

type operationAudit struct {
	reason string
}

type operationSecurityStore interface {
	IsAdmin(context.Context, uuid.UUID) (bool, error)
	Audit(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID, string, string, string) error
	RecordSecurityEvent(context.Context, store.SecurityEvent) error
}

func (a *API) securityStore() operationSecurityStore {
	if a.operationSecurity != nil {
		return a.operationSecurity
	}
	if a.Store == nil {
		return nil
	}
	return a.Store
}

func securitySourceHash(r *http.Request) []byte {
	digest := sha256.Sum256([]byte("TermLink security event source v1\x00" + accountRegistrationClientIdentity(r)))
	return digest[:]
}

func (a *API) recordSecurityEvent(r *http.Request, action, targetType, result string, metadata json.RawMessage) {
	repository := a.securityStore()
	if repository == nil {
		return
	}
	principal := principal(r)
	event := store.SecurityEvent{
		Action: action, TargetType: targetType, Result: result,
		TraceID: traceID(r.Context()), SourceHash: securitySourceHash(r), Metadata: metadata,
	}
	if principal.UserID != uuid.Nil {
		event.UserID = &principal.UserID
	}
	if principal.Device.ID != uuid.Nil {
		event.ActorDeviceID = &principal.Device.ID
	}
	if err := repository.RecordSecurityEvent(r.Context(), event); err != nil {
		logAPIFailure(r.Context(), slog.LevelError, "record security event",
			"security_event_record_failed", err, slog.String("action", action))
	}
}

func requestIdempotencyKey(r *http.Request) (string, error) {
	key := r.Header.Get("Idempotency-Key")
	if len(key) < 8 || len(key) > 128 || key != strings.TrimSpace(key) {
		return "", errors.New("idempotency key must contain 8 to 128 non-padding characters")
	}
	for _, character := range key {
		if character < 0x21 || character > 0x7e {
			return "", errors.New("idempotency key must be printable ASCII")
		}
	}
	return key, nil
}

func (a *API) requireRecentStepUp(w http.ResponseWriter, r *http.Request, actorID uuid.UUID) bool {
	identity := principal(r).Identity
	now := a.now()
	reason := "MFA_REQUIRED"
	valid := identity.MFA && !identity.AuthenticatedAt.IsZero()
	if valid {
		age := now.Sub(identity.AuthenticatedAt.UTC())
		valid = age >= 0 && age <= operationStepUpMaximumAge
		if !valid {
			reason = "AUTHENTICATION_STALE"
		}
	}
	if valid {
		return a.requireOperationReason(w, r)
	}
	metadata, _ := json.Marshal(map[string]string{"reason": reason})
	a.recordSecurityEvent(r, "admin.step_up.denied", "admin_operation", "DENIED", metadata)
	writeError(w, r, http.StatusForbidden, "STEP_UP_REQUIRED", store.ErrDenied)
	return false
}

func (a *API) requireOperationReason(w http.ResponseWriter, r *http.Request) bool {
	reason := strings.TrimSpace(r.Header.Get(operationReasonHeader))
	runeCount := utf8.RuneCountInString(reason)
	valid := utf8.ValidString(reason) && runeCount >= 3 && runeCount <= 500
	if valid {
		for _, character := range reason {
			if unicode.IsControl(character) {
				valid = false
				break
			}
		}
	}
	if !valid {
		a.recordSecurityEvent(r, "admin.operation_reason.denied", "admin_operation", "DENIED", json.RawMessage(`{"reason":"MISSING_OR_INVALID"}`))
		writeError(w, r, http.StatusBadRequest, "OPERATION_REASON_REQUIRED", errors.New("operation reason must contain 3 to 500 characters"))
		return false
	}
	*r = *r.WithContext(context.WithValue(r.Context(), operationAuditKey{}, operationAudit{reason: reason}))
	return true
}

func (a *API) auditOperationSuccess(r *http.Request, actorID uuid.UUID, action, targetType string, targetID uuid.UUID) {
	auditContext, ok := r.Context().Value(operationAuditKey{}).(operationAudit)
	if !ok || auditContext.reason == "" {
		slog.Error("operation audit context missing", "trace_id", traceID(r.Context()), "action", action)
		return
	}
	repository := a.securityStore()
	if repository == nil {
		return
	}
	metadata, _ := json.Marshal(map[string]string{
		"operation_reason":     auditContext.reason,
		"idempotency_key_hash": a.idempotencyAuditHash(r.Header.Get("Idempotency-Key")),
	})
	if err := repository.Audit(r.Context(), actorID, principal(r).Device.ID, action, targetType, targetID, "SUCCEEDED", traceID(r.Context()), string(metadata)); err != nil {
		logAPIFailure(r.Context(), slog.LevelError, "record operation audit",
			"operation_audit_failed", err, slog.String("action", action))
	}
}

func (a *API) idempotencyAuditHash(key string) string {
	key = strings.TrimSpace(key)
	if key == "" {
		return ""
	}
	digestKey := a.registrationDigestKey()
	if len(digestKey) >= 32 {
		digest := keyedRegistrationDigest(digestKey, "idempotency-audit", key)
		return hex.EncodeToString(digest[:])
	}
	digest := sha256.Sum256([]byte("TermLink idempotency audit v1\x00" + key))
	return hex.EncodeToString(digest[:])
}

// oneTimeSecretIdempotency avoids retaining or replaying generated invitation
// bearer secrets while preserving retry semantics for failed requests.
func (a *API) oneTimeSecretIdempotency(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		key, err := requestIdempotencyKey(r)
		if err != nil {
			writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
			return
		}
		if a.Store == nil {
			writeError(w, r, http.StatusServiceUnavailable, "REGISTRATION_UNAVAILABLE", errors.New("registration store is unavailable"))
			return
		}
		body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
		if err != nil || len(body) > maxJSONBody {
			writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", errors.New("request body exceeds limit"))
			return
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		digest := idempotencyRequestDigest(r, body)
		principal := principal(r)
		result, err := a.Store.ClaimIdempotency(r.Context(), principal.UserID, key, digest)
		if err != nil {
			writeStoreError(w, r, err)
			return
		}
		if !bytes.Equal(result.RequestHash[:], digest[:]) {
			writeError(w, r, http.StatusConflict, "IDEMPOTENCY_KEY_REUSED", store.ErrConflict)
			return
		}
		if !result.Claimed {
			if result.Status == nil {
				writeError(w, r, http.StatusConflict, "IDEMPOTENCY_IN_PROGRESS", store.ErrConflict)
				return
			}
			if *result.Status >= 200 && *result.Status < 300 {
				writeError(w, r, http.StatusConflict, "ONE_TIME_RESPONSE_CONSUMED", store.ErrConflict)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Idempotency-Replayed", "true")
			w.WriteHeader(*result.Status)
			if len(result.Body) != 0 && !bytes.Equal(result.Body, []byte("null")) {
				_, _ = w.Write(result.Body)
			}
			return
		}
		capture := &captureWriter{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(capture, r)
		stored := json.RawMessage(capture.body.Bytes())
		if capture.status >= 200 && capture.status < 300 {
			stored = json.RawMessage(`{"generated":true,"plaintext_available":false}`)
		} else if len(stored) == 0 || !json.Valid(stored) {
			stored = json.RawMessage(`{"error":{"code":"NON_JSON_RESPONSE"}}`)
		}
		if err := a.Store.CompleteIdempotency(r.Context(), principal.UserID, key, digest, capture.status, stored); err != nil {
			logAPIFailure(r.Context(), slog.LevelError, "complete one-time invitation idempotency",
				"invitation_idempotency_completion_failed", err)
		}
	})
}

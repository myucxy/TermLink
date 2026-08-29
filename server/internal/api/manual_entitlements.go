package api

import (
	"context"
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type manualEntitlementRepository interface {
	ListManualEntitlementGrants(context.Context, uuid.UUID, store.ManualEntitlementGrantFilter) ([]store.ManualEntitlementGrant, error)
	CreateManualEntitlementGrant(context.Context, uuid.UUID, uuid.UUID, store.CreateManualEntitlementGrantInput) (store.ManualEntitlementGrant, error)
	RevokeManualEntitlementGrant(context.Context, uuid.UUID, uuid.UUID, store.RevokeManualEntitlementGrantInput) (store.ManualEntitlementGrant, error)
}

type manualEntitlementHandlers struct {
	api        *API
	repository manualEntitlementRepository
}

func (a *API) manualEntitlements() manualEntitlementHandlers {
	var repository manualEntitlementRepository
	if a.Store != nil {
		repository = a.Store
	}
	return manualEntitlementHandlers{api: a, repository: repository}
}

// These methods are intentionally not registered here. The main router can
// attach them after choosing the final admin URL shape.
func (a *API) adminListManualEntitlementGrants(w http.ResponseWriter, r *http.Request) {
	a.manualEntitlements().list(w, r)
}

func (a *API) adminCreateManualEntitlementGrant(w http.ResponseWriter, r *http.Request) {
	a.manualEntitlements().create(w, r)
}

func (a *API) adminRevokeManualEntitlementGrant(w http.ResponseWriter, r *http.Request) {
	a.manualEntitlements().revoke(w, r)
}

func (h manualEntitlementHandlers) available(w http.ResponseWriter, r *http.Request) bool {
	if h.api == nil || h.repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "MANUAL_ENTITLEMENTS_UNAVAILABLE", errors.New("manual entitlement repository is unavailable"))
		return false
	}
	return true
}

func (h manualEntitlementHandlers) list(w http.ResponseWriter, r *http.Request) {
	if !h.available(w, r) {
		return
	}
	actorID, ok := h.api.commerceAdminReadActor(w, r)
	if !ok {
		return
	}
	limit, err := manualEntitlementLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	accountID, err := optionalManualEntitlementAccountID(r.URL.Query().Get("account_id"))
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_ACCOUNT_ID", err)
		return
	}
	items, err := h.repository.ListManualEntitlementGrants(r.Context(), actorID, store.ManualEntitlementGrantFilter{
		AccountID: accountID,
		Limit:     limit,
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, manualEntitlementGrantView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

type createManualEntitlementRequest struct {
	AccountID    uuid.UUID `json:"account_id"`
	PlanKey      string    `json:"plan_key"`
	DurationDays int       `json:"duration_days"`
}

func (h manualEntitlementHandlers) create(w http.ResponseWriter, r *http.Request) {
	if !h.available(w, r) {
		return
	}
	actorID, ok := h.api.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	var request createManualEntitlementRequest
	if err := decodeJSON(r, &request); err != nil || request.AccountID == uuid.Nil ||
		strings.TrimSpace(request.PlanKey) == "" || request.DurationDays < 1 || request.DurationDays > 365 {
		writeError(w, r, http.StatusBadRequest, "INVALID_MANUAL_ENTITLEMENT", errors.New("account_id, plan_key, and duration_days between 1 and 365 are required"))
		return
	}
	auditContext, ok := r.Context().Value(commerceOperationAuditKey{}).(commerceOperationAudit)
	if !ok || auditContext.reason == "" {
		writeError(w, r, http.StatusBadRequest, "OPERATION_REASON_REQUIRED", errors.New("operation reason is required"))
		return
	}
	principal := principal(r)
	item, err := h.repository.CreateManualEntitlementGrant(r.Context(), actorID, principal.Device.ID, store.CreateManualEntitlementGrantInput{
		AccountID: request.AccountID, PlanKey: request.PlanKey, DurationDays: request.DurationDays,
		Reason: auditContext.reason, IdempotencyKey: key, TraceID: traceID(r.Context()),
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"grant": manualEntitlementGrantView(item)})
}

func (h manualEntitlementHandlers) revoke(w http.ResponseWriter, r *http.Request) {
	if !h.available(w, r) {
		return
	}
	actorID, ok := h.api.requireCommerceStepUp(w, r)
	if !ok {
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	grantID, err := commercePathUUID(r, "grantId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_GRANT_ID", err)
		return
	}
	if err := decodeEmptyCommerceJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return
	}
	auditContext, ok := r.Context().Value(commerceOperationAuditKey{}).(commerceOperationAudit)
	if !ok || auditContext.reason == "" {
		writeError(w, r, http.StatusBadRequest, "OPERATION_REASON_REQUIRED", errors.New("operation reason is required"))
		return
	}
	principal := principal(r)
	item, err := h.repository.RevokeManualEntitlementGrant(r.Context(), actorID, principal.Device.ID, store.RevokeManualEntitlementGrantInput{
		GrantID: grantID, Reason: auditContext.reason, IdempotencyKey: key, TraceID: traceID(r.Context()),
	})
	if err != nil {
		writeCommerceError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"grant": manualEntitlementGrantView(item)})
}

func manualEntitlementLimit(r *http.Request) (int, error) {
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

func optionalManualEntitlementAccountID(raw string) (uuid.UUID, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return uuid.Nil, nil
	}
	id, err := uuid.Parse(raw)
	if err != nil || id == uuid.Nil || id.String() != strings.ToLower(raw) {
		return uuid.Nil, errors.New("account_id must be a canonical UUID")
	}
	return id, nil
}

func manualEntitlementGrantView(item store.ManualEntitlementGrant) map[string]any {
	return map[string]any{
		"id": item.ID, "account_id": item.AccountID, "plan_key": item.PlanKey,
		"catalog_version": item.CatalogVersion, "starts_at": item.StartsAt,
		"ends_at": item.EndsAt, "status": item.Status,
		"created_at": item.CreatedAt, "updated_at": item.UpdatedAt,
	}
}

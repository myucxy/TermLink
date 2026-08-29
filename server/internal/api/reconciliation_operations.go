package api

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"net/url"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/reconciliation"
	"github.com/termlink/termlink/server/internal/store"
)

const maxReconciliationQueryBytes = 2048

// ReconciliationOperations is the narrow administration capability exposed
// to the API. Production supplies *reconciliation.Operations, whose
// repository performs the authoritative database role checks and owns HMAC
// idempotency, auditing, and state transitions.
type ReconciliationOperations interface {
	List(context.Context, reconciliation.DiscrepancyListRequest) (reconciliation.DiscrepancyPage, error)
	Transition(context.Context, reconciliation.DiscrepancyTransitionRequest) (reconciliation.OperationalDiscrepancy, error)
}

func (a *API) listReconciliationDiscrepancies(w http.ResponseWriter, r *http.Request) {
	operations, actorID, ok := a.reconciliationActor(w, r)
	if !ok {
		return
	}
	request, err := reconciliationListRequest(r, actorID)
	if err != nil {
		writeReconciliationError(w, r, reconciliation.ErrInvalidOperation)
		return
	}
	page, err := operations.List(r.Context(), request)
	if err != nil {
		writeReconciliationError(w, r, err)
		return
	}
	if page.Items == nil {
		page.Items = []reconciliation.OperationalDiscrepancy{}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"items": page.Items, "next_cursor": page.Next,
	})
}

type reconciliationTransitionInput struct {
	TargetStatus    string `json:"target_status"`
	ExpectedVersion int64  `json:"expected_version"`
	ReasonCode      string `json:"reason_code"`
}

func (a *API) transitionReconciliationDiscrepancy(w http.ResponseWriter, r *http.Request) {
	operations, actorID, ok := a.reconciliationActor(w, r)
	if !ok {
		return
	}
	key, err := commerceIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return
	}
	if !a.requireReconciliationRecentMFA(w, r) {
		return
	}
	discrepancyID, err := commercePathUUID(r, "discrepancyId")
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_RECONCILIATION_DISCREPANCY_ID", err)
		return
	}
	var input reconciliationTransitionInput
	if err := decodeJSON(r, &input); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_RECONCILIATION_OPERATION", err)
		return
	}
	item, err := operations.Transition(r.Context(), reconciliation.DiscrepancyTransitionRequest{
		ActorID: actorID, DiscrepancyID: discrepancyID,
		TargetStatus: input.TargetStatus, ExpectedVersion: input.ExpectedVersion,
		ReasonCode: input.ReasonCode, TraceID: traceID(r.Context()), IdempotencyKey: key,
	})
	if err != nil {
		writeReconciliationError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"discrepancy": item})
}

func (a *API) reconciliationActor(w http.ResponseWriter, r *http.Request) (ReconciliationOperations, uuid.UUID, bool) {
	if a.ReconciliationOperations == nil {
		writeError(w, r, http.StatusServiceUnavailable, "RECONCILIATION_OPERATIONS_UNAVAILABLE", reconciliation.ErrOperationsUnavailable)
		return nil, uuid.Nil, false
	}
	actorID := principal(r).UserID
	if actorID == uuid.Nil {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", errors.New("authenticated account is required"))
		return nil, uuid.Nil, false
	}
	return a.ReconciliationOperations, actorID, true
}

func (a *API) requireReconciliationRecentMFA(w http.ResponseWriter, r *http.Request) bool {
	identity := principal(r).Identity
	authenticatedAt := identity.AuthenticatedAt.UTC()
	age := a.now().Sub(authenticatedAt)
	if identity.MFA && !identity.AuthenticatedAt.IsZero() && age >= 0 && age <= commerceStepUpMaximumAge {
		return true
	}
	writeError(w, r, http.StatusForbidden, "STEP_UP_REQUIRED", store.ErrDenied)
	return false
}

func reconciliationListRequest(r *http.Request, actorID uuid.UUID) (reconciliation.DiscrepancyListRequest, error) {
	if len(r.URL.RawQuery) > maxReconciliationQueryBytes {
		return reconciliation.DiscrepancyListRequest{}, errors.New("query exceeds limit")
	}
	values, err := url.ParseQuery(r.URL.RawQuery)
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, err
	}
	allowed := map[string]bool{
		"provider": true, "status": true, "run_id": true,
		"after_last_seen_at": true, "after_id": true, "limit": true,
	}
	for name, items := range values {
		if !allowed[name] || len(items) != 1 || len(items[0]) > 128 {
			return reconciliation.DiscrepancyListRequest{}, errors.New("invalid reconciliation filter")
		}
	}
	runID, err := optionalCommerceUUID(r, "run_id")
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, err
	}
	afterAt, err := optionalCommerceTime(r, "after_last_seen_at")
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, err
	}
	afterID, err := optionalCommerceUUID(r, "after_id")
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, err
	}
	if (afterAt == nil) != (afterID == nil) {
		return reconciliation.DiscrepancyListRequest{}, errors.New("complete discrepancy cursor is required")
	}
	limit, err := optionalCommerceLimit(r, reconciliation.MaxOperationsPageSize)
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, err
	}
	request := reconciliation.DiscrepancyListRequest{
		ActorID: actorID, Provider: values.Get("provider"), Status: values.Get("status"), Limit: limit,
	}
	if runID != nil {
		request.RunID = *runID
	}
	if afterAt != nil {
		request.After = &reconciliation.DiscrepancyCursor{LastSeenAt: *afterAt, ID: *afterID}
	}
	return request, nil
}

func writeReconciliationError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, reconciliation.ErrInvalidOperation):
		writeError(w, r, http.StatusBadRequest, "INVALID_RECONCILIATION_OPERATION", err)
	case errors.Is(err, store.ErrDenied):
		writeError(w, r, http.StatusForbidden, "RECONCILIATION_ACCESS_DENIED", err)
	case errors.Is(err, store.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "RECONCILIATION_DISCREPANCY_NOT_FOUND", err)
	case errors.Is(err, reconciliation.ErrIdempotencyConflict):
		writeError(w, r, http.StatusConflict, "RECONCILIATION_IDEMPOTENCY_CONFLICT", err)
	case errors.Is(err, reconciliation.ErrOperationInProgress):
		writeError(w, r, http.StatusConflict, "RECONCILIATION_OPERATION_IN_PROGRESS", err)
	case errors.Is(err, reconciliation.ErrInvalidTransition), errors.Is(err, store.ErrConflict):
		writeError(w, r, http.StatusConflict, "RECONCILIATION_TRANSITION_CONFLICT", err)
	case errors.Is(err, reconciliation.ErrOperationsUnavailable):
		writeError(w, r, http.StatusServiceUnavailable, "RECONCILIATION_OPERATIONS_UNAVAILABLE", err)
	default:
		logAPIFailure(r.Context(), slog.LevelError, "reconciliation administration failed", "reconciliation_operations_backend_failed", err)
		writeError(w, r, http.StatusServiceUnavailable, "RECONCILIATION_OPERATION_FAILED", err)
	}
}

package api

import (
	"bytes"
	"context"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"strconv"
	"strings"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/accountdeletion"
	"github.com/termlink/termlink/server/internal/store"
)

// userPortalActionStore is deliberately narrower than *store.Postgres. It
// keeps the HTTP boundary directly testable and makes every account-scoped
// store call visible in one contract.
type userPortalActionStore interface {
	RevokeUserPortalDevice(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	ListUserPortalPCControlPolicies(context.Context, uuid.UUID, int) ([]store.UserPortalPCControlPolicy, error)
	SetUserPortalPCRemoteRouting(context.Context, uuid.UUID, uuid.UUID, bool, string, string) (store.UserPortalPCControlPolicy, error)
	SetUserPortalPCControlPolicy(context.Context, uuid.UUID, uuid.UUID, store.UserPortalPCControlPolicyInput, string, string) (store.UserPortalPCControlPolicy, error)
	RequestUserPortalHistoryDeletion(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	RequestUserPortalDataExport(context.Context, uuid.UUID, string, string) (store.UserPortalDataExport, error)
	GetUserPortalDataExport(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalDataExport, error)
	GetUserPortalAccountDeletionPreflight(context.Context, uuid.UUID) (store.UserPortalAccountDeletionPreflight, error)
	RequestUserPortalAccountDeletionState(context.Context, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
	GetUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalAccountDeletion, error)
	CancelUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
}

type userPortalActionHandlers struct {
	api        *API
	repository userPortalActionStore
	binder     accountdeletion.RequestBinder
}

func (a *API) userPortalActions() userPortalActionHandlers {
	var repository userPortalActionStore
	if a.Store != nil {
		repository = a.Store
	}
	return userPortalActionHandlers{api: a, repository: repository, binder: a.AccountDeletionBinder}
}

// These API methods are registered as exact authenticated routes in Handler.
// They must not be placed behind API.idempotency: the portal store methods own
// their atomic action idempotency records and intentionally bind them to the
// authenticated account and operation payload.
func (a *API) revokeUserPortalDevice(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().revokeDevice(w, r)
}

func (a *API) listUserPortalPCControlPolicies(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().listPCControlPolicies(w, r)
}

func (a *API) setUserPortalPCRemoteRouting(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().setPCRemoteRouting(w, r)
}

func (a *API) setUserPortalPCControlPolicy(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().setPCControlPolicy(w, r)
}

func (a *API) requestUserPortalHistoryDeletion(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().requestHistoryDeletion(w, r)
}

func (a *API) requestUserPortalDataExport(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().requestDataExport(w, r)
}

func (a *API) getUserPortalDataExport(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().getDataExport(w, r)
}

func (a *API) getUserPortalAccountDeletionPreflight(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().getAccountDeletionPreflight(w, r)
}

func (a *API) requestUserPortalAccountDeletion(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().requestAccountDeletion(w, r)
}

func (a *API) getUserPortalAccountDeletion(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().getAccountDeletion(w, r)
}

func (a *API) cancelUserPortalAccountDeletion(w http.ResponseWriter, r *http.Request) {
	a.userPortalActions().cancelAccountDeletion(w, r)
}

func (h userPortalActionHandlers) actor(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	if h.repository == nil {
		writeError(w, r, http.StatusServiceUnavailable, "USER_PORTAL_UNAVAILABLE", errors.New("user portal store is unavailable"))
		return uuid.Nil, false
	}
	accountID := principal(r).UserID
	if accountID == uuid.Nil {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", errors.New("authenticated account is required"))
		return uuid.Nil, false
	}
	return accountID, true
}

func (h userPortalActionHandlers) sensitiveActor(w http.ResponseWriter, r *http.Request) (uuid.UUID, string, bool) {
	accountID, ok := h.actor(w, r)
	if !ok {
		return uuid.Nil, "", false
	}
	key, err := requestIdempotencyKey(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "IDEMPOTENCY_KEY_REQUIRED", err)
		return uuid.Nil, "", false
	}
	// These actions require recent MFA but no administrator role because they
	// belong to the authenticated account owner.
	if !h.api.requireRecentStepUp(w, r, accountID) {
		return uuid.Nil, "", false
	}
	return accountID, key, true
}

func (h userPortalActionHandlers) revokeDevice(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	deviceID, ok := userPortalPathUUID(w, r, "deviceId", "INVALID_DEVICE_ID")
	if !ok || !decodeEmptyUserPortalRequest(w, r) {
		return
	}
	result, err := h.repository.RevokeUserPortalDevice(r.Context(), accountID, deviceID, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.device.revoke.context", "device", deviceID)
	writeJSON(w, http.StatusOK, map[string]any{"changed": result.Changed})
}

func (h userPortalActionHandlers) listPCControlPolicies(w http.ResponseWriter, r *http.Request) {
	accountID, ok := h.actor(w, r)
	if !ok {
		return
	}
	limit, err := userPortalLimit(r)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_LIMIT", err)
		return
	}
	items, err := h.repository.ListUserPortalPCControlPolicies(r.Context(), accountID, limit)
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	views := make([]map[string]any, 0, len(items))
	for _, item := range items {
		views = append(views, userPortalPCControlPolicyView(item))
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": views})
}

type userPortalRemoteRoutingRequest struct {
	Enabled *bool `json:"enabled"`
}

func (h userPortalActionHandlers) setPCRemoteRouting(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	pcID, ok := userPortalPathUUID(w, r, "pcId", "INVALID_PC_ID")
	if !ok {
		return
	}
	var input userPortalRemoteRoutingRequest
	if err := decodeJSON(r, &input); err != nil || input.Enabled == nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", errors.New("enabled is required"))
		return
	}
	policy, err := h.repository.SetUserPortalPCRemoteRouting(r.Context(), accountID, pcID, *input.Enabled, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.pc.remote_routing.update.context", "pc", pcID)
	writeJSON(w, http.StatusOK, map[string]any{"policy": userPortalPCControlPolicyView(policy)})
}

type userPortalControlPolicyRequest struct {
	RemoteRoutingEnabled *bool             `json:"remote_routing_enabled"`
	Mode                 store.ControlMode `json:"mode"`
	LocalPriority        *bool             `json:"local_priority"`
	ExpectedVersion      int64             `json:"expected_version"`
}

func (h userPortalActionHandlers) setPCControlPolicy(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	pcID, ok := userPortalPathUUID(w, r, "pcId", "INVALID_PC_ID")
	if !ok {
		return
	}
	var input userPortalControlPolicyRequest
	if err := decodeJSON(r, &input); err != nil || input.RemoteRoutingEnabled == nil || input.LocalPriority == nil ||
		!userPortalControlModeValid(input.Mode) || input.ExpectedVersion < 1 {
		writeError(w, r, http.StatusBadRequest, "INVALID_CONTROL_POLICY", errors.New("complete valid control policy is required"))
		return
	}
	policy, err := h.repository.SetUserPortalPCControlPolicy(r.Context(), accountID, pcID, store.UserPortalPCControlPolicyInput{
		RemoteRoutingEnabled: *input.RemoteRoutingEnabled,
		Mode:                 input.Mode,
		LocalPriority:        *input.LocalPriority,
		ExpectedVersion:      input.ExpectedVersion,
	}, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.pc.control_policy.update.context", "pc", pcID)
	writeJSON(w, http.StatusOK, map[string]any{"policy": userPortalPCControlPolicyView(policy)})
}

func (h userPortalActionHandlers) requestHistoryDeletion(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	sessionID, ok := userPortalPathUUID(w, r, "sessionId", "INVALID_SESSION_ID")
	if !ok || !decodeEmptyUserPortalRequest(w, r) {
		return
	}
	result, err := h.repository.RequestUserPortalHistoryDeletion(r.Context(), accountID, sessionID, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.history.delete.context", "session", sessionID)
	writeJSON(w, http.StatusOK, map[string]any{"changed": result.Changed})
}

func (h userPortalActionHandlers) requestDataExport(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	// Never create a durable REQUESTED row unless this process can also serve
	// the resulting encrypted artifact. Otherwise a partial deployment would
	// leave the account with an export request that can never be downloaded.
	if h.api.DataExportDownloader == nil {
		writeError(w, r, http.StatusServiceUnavailable, "DATA_EXPORT_UNAVAILABLE", errors.New("data export service is unavailable"))
		return
	}
	if !decodeEmptyUserPortalRequest(w, r) {
		return
	}
	export, err := h.repository.RequestUserPortalDataExport(r.Context(), accountID, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.data_export.request.context", "data_export", export.ID)
	writeJSON(w, http.StatusAccepted, map[string]any{"export": userPortalDataExportView(export)})
}

func (h userPortalActionHandlers) getDataExport(w http.ResponseWriter, r *http.Request) {
	accountID, ok := h.actor(w, r)
	if !ok {
		return
	}
	exportID, ok := userPortalPathUUID(w, r, "exportId", "INVALID_EXPORT_ID")
	if !ok {
		return
	}
	export, err := h.repository.GetUserPortalDataExport(r.Context(), accountID, exportID)
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"export": userPortalDataExportView(export)})
}

func (h userPortalActionHandlers) getAccountDeletionPreflight(w http.ResponseWriter, r *http.Request) {
	accountID, ok := h.actor(w, r)
	if !ok {
		return
	}
	preflight, err := h.repository.GetUserPortalAccountDeletionPreflight(r.Context(), accountID)
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"preflight": userPortalAccountDeletionPreflightView(preflight)})
}

func (h userPortalActionHandlers) requestAccountDeletion(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok || !decodeEmptyUserPortalRequest(w, r) {
		return
	}
	if h.binder == nil {
		writeError(w, r, http.StatusServiceUnavailable, "ACCOUNT_DELETION_UNAVAILABLE", accountdeletion.ErrServiceUnavailable)
		return
	}
	deletion, err := h.binder.RequestAndBind(
		r.Context(), accountID, principal(r).Identity.Subject, key, traceID(r.Context()),
	)
	if err != nil {
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) ||
			errors.Is(err, store.ErrConflict) || errors.Is(err, store.ErrSessionActive) ||
			errors.Is(err, store.ErrUserPortalActionUnsupported) {
			writeUserPortalError(w, r, err)
		} else {
			writeError(w, r, http.StatusServiceUnavailable, "ACCOUNT_DELETION_UNAVAILABLE", accountdeletion.ErrServiceUnavailable)
		}
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.account_deletion.request.context", "account_deletion", deletion.ID)
	writeJSON(w, http.StatusAccepted, map[string]any{"deletion": userPortalAccountDeletionView(deletion)})
}

func (h userPortalActionHandlers) getAccountDeletion(w http.ResponseWriter, r *http.Request) {
	accountID, ok := h.actor(w, r)
	if !ok {
		return
	}
	requestID, ok := userPortalPathUUID(w, r, "requestId", "INVALID_DELETION_REQUEST_ID")
	if !ok {
		return
	}
	deletion, err := h.repository.GetUserPortalAccountDeletion(r.Context(), accountID, requestID)
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"deletion": userPortalAccountDeletionView(deletion)})
}

func (h userPortalActionHandlers) cancelAccountDeletion(w http.ResponseWriter, r *http.Request) {
	accountID, key, ok := h.sensitiveActor(w, r)
	if !ok {
		return
	}
	requestID, ok := userPortalPathUUID(w, r, "requestId", "INVALID_DELETION_REQUEST_ID")
	if !ok || !decodeEmptyUserPortalRequest(w, r) {
		return
	}
	deletion, err := h.repository.CancelUserPortalAccountDeletion(r.Context(), accountID, requestID, key, traceID(r.Context()))
	if err != nil {
		writeUserPortalError(w, r, err)
		return
	}
	h.api.auditOperationSuccess(r, accountID, "user_portal.account_deletion.cancel.context", "account_deletion", requestID)
	writeJSON(w, http.StatusOK, map[string]any{"deletion": userPortalAccountDeletionView(deletion)})
}

func decodeEmptyUserPortalRequest(w http.ResponseWriter, r *http.Request) bool {
	if err := decodeEmptyJSON(r); err != nil {
		writeError(w, r, http.StatusBadRequest, "INVALID_REQUEST", err)
		return false
	}
	return true
}

func userPortalPathUUID(w http.ResponseWriter, r *http.Request, name, errorCode string) (uuid.UUID, bool) {
	id, err := pathUUID(r, name)
	if err != nil {
		writeError(w, r, http.StatusBadRequest, errorCode, err)
		return uuid.Nil, false
	}
	return id, true
}

func pathUUID(r *http.Request, name string) (uuid.UUID, error) {
	value, err := uuid.Parse(r.PathValue(name))
	if err != nil || value == uuid.Nil {
		return uuid.Nil, errors.New("invalid UUID path value")
	}
	return value, nil
}

func decodeEmptyJSON(r *http.Request) error {
	if r.Body == nil {
		return nil
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxJSONBody+1))
	if err != nil || len(body) > maxJSONBody {
		return errors.New("request body exceeds limit")
	}
	if len(bytes.TrimSpace(body)) == 0 {
		return nil
	}
	clone := r.Clone(r.Context())
	clone.Body = io.NopCloser(bytes.NewReader(body))
	var input struct{}
	return decodeJSON(clone, &input)
}

func userPortalLimit(r *http.Request) (int, error) {
	raw := strings.TrimSpace(r.URL.Query().Get("limit"))
	if raw == "" {
		return 50, nil
	}
	limit, err := strconv.Atoi(raw)
	if err != nil || limit < 1 || limit > 100 {
		return 0, errors.New("limit must be between 1 and 100")
	}
	return limit, nil
}

func userPortalControlModeValid(mode store.ControlMode) bool {
	return mode == store.ControlModeSingle || mode == store.ControlModeTakeover || mode == store.ControlModeCollaborative
}

// ErrDenied and ErrNotFound intentionally collapse to the same public result.
// This prevents the endpoint from becoming a cross-account resource oracle.
func writeUserPortalError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, store.ErrDenied), errors.Is(err, store.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "USER_PORTAL_RESOURCE_NOT_FOUND", err)
	case errors.Is(err, store.ErrSessionActive):
		writeError(w, r, http.StatusConflict, "SESSION_ACTIVE", err)
	case errors.Is(err, store.ErrConflict):
		writeError(w, r, http.StatusConflict, "CONFLICT", err)
	case errors.Is(err, store.ErrUserPortalActionUnsupported):
		writeError(w, r, http.StatusServiceUnavailable, "USER_PORTAL_ACTION_UNAVAILABLE", err)
	default:
		logAPIFailure(r.Context(), slog.LevelError, "user portal API storage operation failed", "user_portal_storage_operation_failed", err)
		writeError(w, r, http.StatusInternalServerError, "INTERNAL", err)
	}
}

func userPortalPCControlPolicyView(policy store.UserPortalPCControlPolicy) map[string]any {
	return map[string]any{
		"pc_id": policy.PCID, "name": policy.Name,
		"remote_routing_enabled": policy.RemoteRoutingEnabled,
		"mode":                   policy.Mode, "local_priority": policy.LocalPriority,
		"policy_version": policy.PolicyVersion,
	}
}

func userPortalDataExportView(export store.UserPortalDataExport) map[string]any {
	view := map[string]any{
		"id": export.ID, "status": export.Status, "requested_at": export.RequestedAt,
	}
	if export.ExpiresAt != nil {
		view["expires_at"] = export.ExpiresAt
	}
	return view
}

func userPortalAccountDeletionPreflightView(preflight store.UserPortalAccountDeletionPreflight) map[string]any {
	return map[string]any{
		"status": preflight.Status, "active_devices": preflight.ActiveDevices,
		"live_sessions": preflight.LiveSessions,
		"ready":         preflight.Ready, "request_supported": preflight.RequestSupported,
	}
}

func userPortalAccountDeletionView(deletion store.UserPortalAccountDeletion) map[string]any {
	view := map[string]any{
		"id": deletion.ID, "status": deletion.Status, "version": deletion.Version,
		"requested_at": deletion.RequestedAt, "confirmation_expires_at": deletion.ConfirmationExpiresAt,
	}
	if deletion.ScheduledFor != nil {
		view["scheduled_for"] = deletion.ScheduledFor
	}
	if deletion.RetentionDeadline != nil {
		view["retention_deadline"] = deletion.RetentionDeadline
	}
	if deletion.CompletedAt != nil {
		view["completed_at"] = deletion.CompletedAt
	}
	if deletion.CanceledAt != nil {
		view["canceled_at"] = deletion.CanceledAt
	}
	return view
}

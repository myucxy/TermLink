package webportal

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/reconciliation"
	"github.com/termlink/termlink/server/internal/store"
)

const reconciliationCursorMaximumBytes = 512

var errReconciliationFilter = errors.New("invalid reconciliation filter")

func isInvalidReconciliationError(err error) bool {
	return errors.Is(err, reconciliation.ErrInvalidOperation)
}

func isUnavailableReconciliationError(err error) bool {
	return errors.Is(err, reconciliation.ErrOperationsUnavailable)
}

// ReconciliationOperations exposes only the sanitized operational list and
// state transition contracts required by the browser console.
type ReconciliationOperations interface {
	List(context.Context, reconciliation.DiscrepancyListRequest) (reconciliation.DiscrepancyPage, error)
	Transition(context.Context, reconciliation.DiscrepancyTransitionRequest) (reconciliation.OperationalDiscrepancy, error)
}

// Aliases keep the main commerce page independent from the reconciliation
// implementation import while preserving the service's validated contracts.
type reconciliationListRequest = reconciliation.DiscrepancyListRequest
type reconciliationPage = reconciliation.DiscrepancyPage

type reconciliationFilterView struct {
	Provider string
	Status   string
	RunID    string
	Limit    int
}

type reconciliationDiscrepancyView struct {
	ID               uuid.UUID
	RunID            uuid.UUID
	Provider         string
	Category         string
	SummaryCode      string
	Status           string
	Version          int64
	LocalSnapshot    string
	ProviderSnapshot string
	ResolutionCode   string
	FirstSeenAt      time.Time
	LastSeenAt       time.Time
}

type reconciliationPageView struct {
	Items []reconciliationDiscrepancyView
}

type reconciliationCursorPayload struct {
	LastSeenAt string `json:"last_seen_at"`
	ID         string `json:"id"`
}

func newReconciliationPageView(page reconciliation.DiscrepancyPage) reconciliationPageView {
	view := reconciliationPageView{Items: make([]reconciliationDiscrepancyView, 0, len(page.Items))}
	for _, item := range page.Items {
		resolution := ""
		if item.ResolutionCode != nil {
			resolution = *item.ResolutionCode
		}
		view.Items = append(view.Items, reconciliationDiscrepancyView{
			ID: item.ID, RunID: item.RunID, Provider: item.Provider, Category: item.Category,
			SummaryCode: item.SummaryCode, Status: item.Status, Version: item.Version,
			LocalSnapshot:    reconciliationSnapshot(item.LocalStatus, item.LocalAmountMinor, item.LocalCurrency),
			ProviderSnapshot: reconciliationSnapshot(item.ProviderStatus, item.ProviderAmountMinor, item.ProviderCurrency),
			ResolutionCode:   resolution, FirstSeenAt: item.FirstSeenAt, LastSeenAt: item.LastSeenAt,
		})
	}
	return view
}

func reconciliationSnapshot(status *string, amount *int64, currency *string) string {
	parts := make([]string, 0, 2)
	if status != nil && *status != "" {
		parts = append(parts, *status)
	}
	if amount != nil && currency != nil && *currency != "" {
		parts = append(parts, formatCommerceMoney(*amount, *currency))
	}
	if len(parts) == 0 {
		return "—"
	}
	return strings.Join(parts, " · ")
}

func parseReconciliationQuery(values url.Values) (reconciliation.DiscrepancyListRequest, reconciliationFilterView, error) {
	allowed := map[string]bool{
		"section": true, "notice": true, "provider": true, "status": true,
		"run_id": true, "limit": true, "cursor": true,
	}
	if validateCommerceFormValues(values, allowed) != nil || values.Get("section") != "reconciliation" {
		return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
	}
	provider := strings.ToUpper(strings.TrimSpace(values.Get("provider")))
	if provider != "" && provider != "ALIPAY" && provider != "WECHAT_PAY" {
		return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
	}
	status := strings.ToUpper(strings.TrimSpace(values.Get("status")))
	if status != "" && status != reconciliation.DiscrepancyStatusOpen && status != reconciliation.DiscrepancyStatusAcknowledged &&
		status != reconciliation.DiscrepancyStatusResolved && status != reconciliation.DiscrepancyStatusIgnored {
		return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
	}
	runID := uuid.Nil
	runText := strings.TrimSpace(values.Get("run_id"))
	if runText != "" {
		var err error
		runID, err = parseCanonicalPortalUUID(runText)
		if err != nil {
			return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
		}
	}
	limit, err := parseCommerceReportLimit(values.Get("limit"), reconciliation.DefaultOperationsPageSize, reconciliation.MaxOperationsPageSize)
	if err != nil {
		return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
	}
	request := reconciliation.DiscrepancyListRequest{Provider: provider, Status: status, RunID: runID, Limit: limit}
	if cursorText := strings.TrimSpace(values.Get("cursor")); cursorText != "" {
		request.After, err = decodeReconciliationCursor(cursorText)
		if err != nil {
			return reconciliation.DiscrepancyListRequest{}, reconciliationFilterView{}, errReconciliationFilter
		}
	}
	return request, reconciliationFilterView{Provider: provider, Status: status, RunID: runText, Limit: limit}, nil
}

func decodeReconciliationCursor(value string) (*reconciliation.DiscrepancyCursor, error) {
	if value == "" || len(value) > reconciliationCursorMaximumBytes {
		return nil, errReconciliationFilter
	}
	decoded, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil || len(decoded) == 0 || len(decoded) > reconciliationCursorMaximumBytes ||
		base64.RawURLEncoding.EncodeToString(decoded) != value {
		return nil, errReconciliationFilter
	}
	var payload reconciliationCursorPayload
	if json.Unmarshal(decoded, &payload) != nil {
		return nil, errReconciliationFilter
	}
	seenAt, err := time.Parse(time.RFC3339Nano, payload.LastSeenAt)
	if err != nil || seenAt.IsZero() {
		return nil, errReconciliationFilter
	}
	id, err := parseCanonicalPortalUUID(payload.ID)
	if err != nil {
		return nil, errReconciliationFilter
	}
	return &reconciliation.DiscrepancyCursor{LastSeenAt: seenAt.UTC(), ID: id}, nil
}

func encodeReconciliationCursor(cursor reconciliation.DiscrepancyCursor) (string, error) {
	if cursor.LastSeenAt.IsZero() || cursor.ID == uuid.Nil {
		return "", errReconciliationFilter
	}
	payload, err := json.Marshal(reconciliationCursorPayload{
		LastSeenAt: cursor.LastSeenAt.UTC().Format(time.RFC3339Nano), ID: cursor.ID.String(),
	})
	if err != nil || len(payload) > reconciliationCursorMaximumBytes {
		return "", errReconciliationFilter
	}
	return base64.RawURLEncoding.EncodeToString(payload), nil
}

func reconciliationNextURL(filter reconciliationFilterView, cursor reconciliation.DiscrepancyCursor) (string, error) {
	encoded, err := encodeReconciliationCursor(cursor)
	if err != nil {
		return "", err
	}
	query := url.Values{"section": {"reconciliation"}, "limit": {strconv.Itoa(filter.Limit)}, "cursor": {encoded}}
	if filter.Provider != "" {
		query.Set("provider", filter.Provider)
	}
	if filter.Status != "" {
		query.Set("status", filter.Status)
	}
	if filter.RunID != "" {
		query.Set("run_id", filter.RunID)
	}
	return "/admin/commerce?" + query.Encode(), nil
}

// strongConsoleAction establishes only the audit namespace. Reconciliation
// Operations owns its HMAC idempotency record and must not be wrapped in the
// portal's second ClaimIdempotency transaction.
func (p *Portal) strongConsoleAction(action string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		slot := &sensitiveActionSlot{action: action}
		next(w, r.WithContext(context.WithValue(r.Context(), sensitiveActionContextKey{}, slot)))
	}
}

func (p *Portal) requireStrongConsoleAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "csrf_invalid", "管理请求被拒绝", "请求来源或安全校验无效，请刷新管理台后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	sessionRole := store.Role(session.Role)
	if (account.Role != store.RoleAdmin && account.Role != store.RoleSuperAdmin) ||
		(sessionRole != store.RoleAdmin && sessionRole != store.RoleSuperAdmin) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "role_denied", "需要运营管理员", "AUDITOR 只能查看对账差异，不能修改处理状态。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !recentStrongVerification(session, p.now()) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "step_up_required", "需要安全验证", "请返回管理页面完成弹出的安全验证：已绑定 MFA 时完成多因素认证，未绑定时重新验证一次登录密码。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !validSensitiveReason(strings.TrimSpace(r.Form.Get("reason"))) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "reason_invalid", "操作原因无效", "请填写 3 到 500 个字符的操作原因。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !validSensitiveIdempotencyKey(strings.TrimSpace(r.Form.Get("idempotency_key"))) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "idempotency_key_invalid", "请求标识无效", "请刷新管理页面后重新提交。")
		return webSession{}, store.PortalAccount{}, false
	}
	if slot, exists := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot); !exists || slot == nil || slot.action == "" {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "audit_unavailable", "管理请求暂不可用", "安全审计配置不完整，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) adminReconciliationTransition(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireStrongConsoleAction(w, r)
	if !ok {
		return
	}
	if p.config.Reconciliation == nil {
		p.renderError(w, http.StatusServiceUnavailable, "对账操作暂不可用", "服务端尚未配置对账差异处理能力。")
		return
	}
	allowed := map[string]bool{
		"csrf": true, "reason": true, "idempotency_key": true, "expected_version": true,
		"target_status": true, "reason_code": true,
	}
	discrepancyID, idErr := parseCanonicalPortalUUID(r.PathValue("id"))
	expectedVersion, versionErr := strconv.ParseInt(strings.TrimSpace(r.Form.Get("expected_version")), 10, 64)
	targetStatus := strings.ToUpper(strings.TrimSpace(r.Form.Get("target_status")))
	reasonCode := strings.ToUpper(strings.TrimSpace(r.Form.Get("reason_code")))
	if validateCommerceFormValues(r.Form, allowed) != nil || idErr != nil || versionErr != nil || expectedVersion < 1 ||
		!validReconciliationReason(targetStatus, reasonCode) {
		p.renderError(w, http.StatusBadRequest, "对账状态请求无效", "请刷新差异列表并选择有效的处理状态和原因代码。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = p.config.Reconciliation.Transition(r.Context(), reconciliation.DiscrepancyTransitionRequest{
			ActorID: account.ID, DiscrepancyID: discrepancyID, TargetStatus: targetStatus,
			ExpectedVersion: expectedVersion, ReasonCode: reasonCode, TraceID: traceID,
			IdempotencyKey: strings.TrimSpace(r.Form.Get("idempotency_key")),
		})
	}
	if err != nil {
		p.handleReconciliationTransitionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.commerce.reconciliation.discrepancy.transition.context", "reconciliation_discrepancy", discrepancyID)
	p.redirectAdminCommerce(w, r, "reconciliation", "reconciliation-updated", uuid.Nil)
}

func validReconciliationReason(targetStatus, reasonCode string) bool {
	allowed := map[string]map[string]bool{
		reconciliation.DiscrepancyStatusAcknowledged: {"OPERATOR_REVIEW_STARTED": true},
		reconciliation.DiscrepancyStatusResolved: {
			"MATCH_CONFIRMED": true, "PROVIDER_CORRECTED": true,
			"LOCAL_CORRECTED": true, "MANUAL_ADJUSTMENT": true,
		},
		reconciliation.DiscrepancyStatusIgnored: {
			"EXPECTED_TIMING_DIFFERENCE": true, "DUPLICATE_ALERT": true, "NON_ACTIONABLE": true,
		},
	}
	return allowed[targetStatus][reasonCode]
}

func (p *Portal) handleReconciliationTransitionError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, reconciliation.ErrInvalidOperation):
		p.renderError(w, http.StatusBadRequest, "对账状态请求无效", "状态、版本、原因代码或请求标识无效。")
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "对账操作被拒绝", "当前账号角色或状态不允许修改对账差异。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "对账差异不存在", "该差异不存在或已不再可处理。")
	case errors.Is(err, store.ErrConflict), errors.Is(err, reconciliation.ErrInvalidTransition),
		errors.Is(err, reconciliation.ErrIdempotencyConflict), errors.Is(err, reconciliation.ErrOperationInProgress):
		p.renderError(w, http.StatusConflict, "对账状态已变化", "该差异已由其他管理员处理或同一请求仍在执行，请刷新列表后重试。")
	case errors.Is(err, reconciliation.ErrOperationsUnavailable), errors.Is(err, reconciliation.ErrOperationIntegrity):
		p.renderError(w, http.StatusServiceUnavailable, "对账操作暂不可用", "对账操作存储或完整性校验暂不可用，请稍后重试。")
	default:
		p.internalError(w, err)
	}
}

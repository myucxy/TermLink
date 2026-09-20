package webportal

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	userPortalDeletionCoolingPeriod = 7 * 24 * time.Hour
	userPortalDeletionRetention     = 30 * 24 * time.Hour
	userPortalDeletionConfirmation  = "删除我的账号"
)

var errUserPortalRequest = errors.New("invalid user portal request")

type userPortalActionRepository interface {
	RevokeUserPortalDevice(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	ListUserPortalPCControlPolicies(context.Context, uuid.UUID, int) ([]store.UserPortalPCControlPolicy, error)
	SetUserPortalPCControlPolicy(context.Context, uuid.UUID, uuid.UUID, store.UserPortalPCControlPolicyInput, string, string) (store.UserPortalPCControlPolicy, error)
	GetUserPortalAccountDeletionPreflight(context.Context, uuid.UUID) (store.UserPortalAccountDeletionPreflight, error)
	RequestUserPortalAccountDeletionState(context.Context, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
	GetUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalAccountDeletion, error)
	ConfirmUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, time.Time, time.Time, string, string) (store.UserPortalAccountDeletion, error)
	CancelUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
}

func (p *Portal) userPortalDisableDevice(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	deviceID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "设备标识无效 / Invalid device", "请刷新设备页面后重试。 / Refresh and try again.")
		return
	}
	traceID, _ := randomText(12)
	action, ok := repository.(interface {
		DisableUserPortalDevice(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	})
	if !ok {
		p.renderError(w, http.StatusServiceUnavailable, "设备操作暂不可用", "服务端尚未支持设备禁用。 / Device disable is unavailable.")
		return
	}
	if _, err = action.DisableUserPortalDevice(r.Context(), account.ID, deviceID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID); err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.redirectUserPortal(w, r, "devices", "device-disabled", url.Values{})
}

func (p *Portal) userPortalDeleteDevice(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	deviceID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "设备标识无效 / Invalid device", "请刷新设备页面后重试。 / Refresh and try again.")
		return
	}
	traceID, _ := randomText(12)
	action, ok := repository.(interface {
		DeleteUserPortalDevice(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	})
	if !ok {
		p.renderError(w, http.StatusServiceUnavailable, "设备操作暂不可用", "服务端尚未支持设备删除。 / Device deletion is unavailable.")
		return
	}
	if _, err = action.DeleteUserPortalDevice(r.Context(), account.ID, deviceID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID); err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.redirectUserPortal(w, r, "devices", "device-deleted", url.Values{})
}

type portalAuditRepository interface {
	Audit(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID, string, string, string) error
}

func (p *Portal) loadUserPortalPrivacySelection(ctx context.Context, repository userPortalActionRepository, accountID uuid.UUID, values url.Values, data *userPortalPageData) error {
	allowed := map[string]bool{"section": true, "notice": true, "deletion_id": true}
	if data == nil || (values.Get("section") != "privacy" && values.Get("section") != "settings") || validatePortalFormValues(values, allowed) != nil {
		return errUserPortalRequest
	}
	if deletionText := strings.TrimSpace(values.Get("deletion_id")); deletionText != "" {
		deletionID, err := parseCanonicalPortalUUID(deletionText)
		if err != nil {
			return err
		}
		item, err := repository.GetUserPortalAccountDeletion(ctx, accountID, deletionID)
		if err != nil {
			return err
		}
		data.AccountDeletion = &item
	}
	return nil
}

func (p *Portal) userPortalRevokeDevice(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	deviceID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "设备标识无效", "请刷新设备页面后重试。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.RevokeUserPortalDevice(r.Context(), account.ID, deviceID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.device.revoke.context", "device", deviceID)
	p.redirectUserPortal(w, r, "devices", "device-revoked", url.Values{})
}

func (p *Portal) userPortalUpdatePCControlPolicy(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	pcID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	mode := store.ControlMode(strings.ToUpper(strings.TrimSpace(r.Form.Get("mode"))))
	remoteRouting, remoteErr := strconv.ParseBool(strings.TrimSpace(r.Form.Get("remote_routing_enabled")))
	localPriority, localErr := strconv.ParseBool(strings.TrimSpace(r.Form.Get("local_priority")))
	version, versionErr := strconv.ParseInt(strings.TrimSpace(r.Form.Get("expected_version")), 10, 64)
	if err != nil || remoteErr != nil || localErr != nil || versionErr != nil || version < 1 ||
		(mode != store.ControlModeSingle && mode != store.ControlModeTakeover && mode != store.ControlModeCollaborative) {
		p.renderError(w, http.StatusBadRequest, "控制策略无效", "请刷新 PC 页面并选择有效的路由和控制策略。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.SetUserPortalPCControlPolicy(r.Context(), account.ID, pcID, store.UserPortalPCControlPolicyInput{
			RemoteRoutingEnabled: remoteRouting, Mode: mode, LocalPriority: localPriority, ExpectedVersion: version,
		}, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.pc.control_policy.update.context", "pc", pcID)
	p.redirectUserPortal(w, r, "pcs", "pc-policy-updated", url.Values{})
}

func (p *Portal) userPortalBatchUpdatePCControlPolicy(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	remoteRouting, remoteErr := strconv.ParseBool(strings.TrimSpace(r.Form.Get("remote_routing_enabled")))
	localPriority, localErr := strconv.ParseBool(strings.TrimSpace(r.Form.Get("local_priority")))
	mode := store.ControlMode(strings.ToUpper(strings.TrimSpace(r.Form.Get("mode"))))
	if remoteErr != nil || localErr != nil || (mode != store.ControlModeSingle && mode != store.ControlModeTakeover && mode != store.ControlModeCollaborative) {
		p.renderError(w, http.StatusBadRequest, "批量控制策略无效", "请选择有效的路由和控制策略。 / Choose valid routing and control values.")
		return
	}
	policies, err := repository.ListUserPortalPCControlPolicies(r.Context(), account.ID, 100)
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	for _, policy := range policies {
		key, keyErr := randomText(18)
		if keyErr != nil {
			p.internalError(w, keyErr)
			return
		}
		traceID, traceErr := randomText(12)
		if traceErr != nil {
			p.internalError(w, traceErr)
			return
		}
		if _, err = repository.SetUserPortalPCControlPolicy(r.Context(), account.ID, policy.PCID, store.UserPortalPCControlPolicyInput{RemoteRoutingEnabled: remoteRouting, Mode: mode, LocalPriority: localPriority, ExpectedVersion: policy.PolicyVersion}, key, traceID); err != nil {
			p.handleUserPortalActionError(w, err)
			return
		}
	}
	p.redirectUserPortal(w, r, "pcs", "pc-policy-updated", url.Values{})
}

func (p *Portal) userPortalRequestAccountDeletion(w http.ResponseWriter, r *http.Request) {
	session, account, _, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	if strings.TrimSpace(r.Form.Get("confirmation")) != userPortalDeletionConfirmation {
		p.renderError(w, http.StatusBadRequest, "确认文字不匹配", "请输入“删除我的账号”后再创建注销申请。")
		return
	}
	if p.config.AccountDeletionBinder == nil {
		p.renderError(w, http.StatusServiceUnavailable, "账号注销暂不可用", "服务端尚未配置安全的身份注销能力，本次没有创建注销申请。")
		return
	}
	if p.config.AccountDeletionEnabled != nil && !p.config.AccountDeletionEnabled(r.Context()) {
		p.renderError(w, http.StatusForbidden, "账号注销未开放", "当前部署的管理员已暂停账号注销功能，本次没有创建注销申请。")
		return
	}
	traceID, err := randomText(12)
	var item store.UserPortalAccountDeletion
	if err == nil {
		item, err = p.config.AccountDeletionBinder.RequestAndBind(
			r.Context(), account.ID, session.Subject, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID,
		)
	}
	if err != nil {
		if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) ||
			errors.Is(err, store.ErrConflict) || errors.Is(err, store.ErrSessionActive) ||
			errors.Is(err, store.ErrUserPortalActionUnsupported) {
			p.handleUserPortalActionError(w, err)
		} else {
			p.renderError(w, http.StatusServiceUnavailable, "账号注销暂不可用", "身份绑定或安全注销服务暂不可用，本次申请尚未确认，请稍后使用相同请求重试。")
		}
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.account_deletion.request.context", "account_deletion", item.ID)
	p.redirectUserPortal(w, r, "settings", "deletion-requested", url.Values{"deletion_id": {item.ID.String()}})
}

func (p *Portal) userPortalConfirmAccountDeletion(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	requestID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil || strings.TrimSpace(r.Form.Get("confirmation")) != userPortalDeletionConfirmation {
		p.renderError(w, http.StatusBadRequest, "注销确认无效", "请刷新隐私页面并输入“删除我的账号”。")
		return
	}
	current, err := repository.GetUserPortalAccountDeletion(r.Context(), account.ID, requestID)
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	if current.Status != "REQUESTED" {
		p.renderError(w, http.StatusConflict, "注销状态已变化", "该申请已确认、取消或进入处理，请刷新状态。")
		return
	}
	scheduledFor := current.RequestedAt.UTC().Add(userPortalDeletionCoolingPeriod)
	retentionDeadline := scheduledFor.Add(userPortalDeletionRetention)
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.ConfirmUserPortalAccountDeletion(
			r.Context(), account.ID, requestID, scheduledFor, retentionDeadline,
			strings.TrimSpace(r.Form.Get("idempotency_key")), traceID,
		)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.account_deletion.confirm.context", "account_deletion", requestID)
	p.redirectUserPortal(w, r, "settings", "deletion-confirmed", url.Values{"deletion_id": {requestID.String()}})
}

func (p *Portal) userPortalCancelAccountDeletion(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	requestID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "注销申请无效", "请刷新隐私页面后重试。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.CancelUserPortalAccountDeletion(
			r.Context(), account.ID, requestID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID,
		)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.account_deletion.cancel.context", "account_deletion", requestID)
	p.redirectUserPortal(w, r, "settings", "deletion-cancelled", url.Values{"deletion_id": {requestID.String()}})
}

func (p *Portal) requireUserPortalAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, userPortalActionRepository, bool) {
	session, account, ok := p.requireStrongAccountAction(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, nil, false
	}
	repository, available := p.config.Repository.(userPortalActionRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "账号操作暂不可用", "服务端尚未完成账号操作存储配置。")
		return webSession{}, store.PortalAccount{}, nil, false
	}
	return session, account, repository, true
}

func parseCanonicalPortalUUID(value string) (uuid.UUID, error) {
	value = strings.TrimSpace(value)
	id, err := uuid.Parse(value)
	if err != nil || id == uuid.Nil || id.String() != value {
		return uuid.Nil, errUserPortalRequest
	}
	return id, nil
}

func (p *Portal) handleUserPortalActionError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrDenied), errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "账号资源不存在", "目标资源不存在、已变化或不属于当前账号。")
	case errors.Is(err, store.ErrSessionActive):
		p.renderError(w, http.StatusConflict, "会话仍在运行", "请先结束或手动关闭仍在运行的会话，再继续账号操作。")
	case errors.Is(err, store.ErrConflict):
		p.renderError(w, http.StatusConflict, "账号状态已变化", "请刷新用户中心并确认最新状态后重试。")
	case errors.Is(err, store.ErrUserPortalActionUnsupported):
		p.renderError(w, http.StatusServiceUnavailable, "账号操作尚未启用", "当前服务端尚未启用该状态机，请稍后重试。")
	default:
		p.internalError(w, err)
	}
}

func (p *Portal) redirectUserPortal(w http.ResponseWriter, r *http.Request, section, notice string, extra url.Values) {
	query := url.Values{"section": {section}, "notice": {notice}}
	for key, values := range extra {
		for _, value := range values {
			query.Add(key, value)
		}
	}
	http.Redirect(w, r, "/account?"+query.Encode(), http.StatusSeeOther)
}

func userPortalControlModeLabel(mode store.ControlMode) string {
	switch mode {
	case store.ControlModeSingle:
		return "单人控制"
	case store.ControlModeTakeover:
		return "申请接管"
	case store.ControlModeCollaborative:
		return "协同控制"
	default:
		return "未知策略"
	}
}

func userPortalDeletionCanConfirm(item *store.UserPortalAccountDeletion) bool {
	return item != nil && item.Status == "REQUESTED"
}

func userPortalDeletionCanCancel(item *store.UserPortalAccountDeletion) bool {
	if item == nil {
		return false
	}
	return item.Status == "REQUESTED" || item.Status == "CONFIRMED" || item.Status == "FAILED"
}

func (p *Portal) auditUserPortalActionSuccess(r *http.Request, accountID uuid.UUID, action, targetType string, targetID uuid.UUID) {
	if err := p.recordUserPortalActionAudit(r, accountID, action, targetType, targetID); err != nil {
		traceID := uuid.NewString()
		logPortalFailure(r.Context(), slog.LevelError, "record user portal operation audit",
			"user_portal_operation_audit_failed", traceID, err, slog.String("action", action))
	}
}

func (p *Portal) recordUserPortalActionAudit(r *http.Request, accountID uuid.UUID, action, targetType string, targetID uuid.UUID) error {
	repository, ok := p.config.Repository.(portalAuditRepository)
	if !ok {
		return errors.New("web portal audit repository unavailable")
	}
	metadata, err := json.Marshal(map[string]string{"operation_reason": strings.TrimSpace(r.Form.Get("reason"))})
	if err != nil {
		return err
	}
	return repository.Audit(r.Context(), accountID, uuid.Nil, action, targetType, targetID, "SUCCEEDED", uuid.NewString(), string(metadata))
}

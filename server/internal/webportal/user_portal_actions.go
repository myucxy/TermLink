package webportal

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"net/url"
	"reflect"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/privacyexport"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	userPortalDeletionCoolingPeriod = 7 * 24 * time.Hour
	userPortalDeletionRetention     = 30 * 24 * time.Hour
	userPortalDeletionConfirmation  = "删除我的账号"
	maximumWebPrivacyExportBytes    = 16 << 20
)

var errUserPortalRequest = errors.New("invalid user portal request")

// PrivacyExportDownloader is the only object-storage capability available to
// the browser portal. It returns account-scoped, decrypted JSON and has no API
// for exposing an object key or a presigned storage URL.
type PrivacyExportDownloader interface {
	Download(context.Context, uuid.UUID, uuid.UUID) ([]byte, error)
}

type userPortalActionRepository interface {
	RevokeUserPortalDevice(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	ListUserPortalPCControlPolicies(context.Context, uuid.UUID, int) ([]store.UserPortalPCControlPolicy, error)
	SetUserPortalPCControlPolicy(context.Context, uuid.UUID, uuid.UUID, store.UserPortalPCControlPolicyInput, string, string) (store.UserPortalPCControlPolicy, error)
	RequestUserPortalHistoryDeletion(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalActionResult, error)
	RequestUserPortalDataExport(context.Context, uuid.UUID, string, string) (store.UserPortalDataExport, error)
	GetUserPortalDataExport(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalDataExport, error)
	GetUserPortalAccountDeletionPreflight(context.Context, uuid.UUID) (store.UserPortalAccountDeletionPreflight, error)
	RequestUserPortalAccountDeletionState(context.Context, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
	GetUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID) (store.UserPortalAccountDeletion, error)
	ConfirmUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, time.Time, time.Time, string, string) (store.UserPortalAccountDeletion, error)
	CancelUserPortalAccountDeletion(context.Context, uuid.UUID, uuid.UUID, string, string) (store.UserPortalAccountDeletion, error)
}

func (p *Portal) loadUserPortalPrivacySelection(ctx context.Context, repository userPortalActionRepository, accountID uuid.UUID, values url.Values, data *commerceAccountPageData) error {
	allowed := map[string]bool{"section": true, "notice": true, "export_id": true, "deletion_id": true}
	if data == nil || values.Get("section") != "privacy" || validateCommerceFormValues(values, allowed) != nil {
		return errUserPortalRequest
	}
	if exportText := strings.TrimSpace(values.Get("export_id")); exportText != "" {
		exportID, err := parseCanonicalPortalUUID(exportText)
		if err != nil {
			return err
		}
		item, err := repository.GetUserPortalDataExport(ctx, accountID, exportID)
		if err != nil {
			return err
		}
		data.DataExport = &item
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

func (p *Portal) userPortalDeleteSessionHistory(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	sessionID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "会话标识无效", "请刷新会话页面后重试。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.RequestUserPortalHistoryDeletion(r.Context(), account.ID, sessionID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.session_history.delete.context", "session", sessionID)
	p.redirectUserPortal(w, r, "sessions", "history-deleted", url.Values{})
}

func (p *Portal) userPortalRequestDataExport(w http.ResponseWriter, r *http.Request) {
	_, account, repository, ok := p.requireUserPortalAction(w, r)
	if !ok {
		return
	}
	if !dataExportDownloaderAvailable(p.config.DataExportDownloader) {
		p.renderError(w, http.StatusServiceUnavailable, "数据导出暂不可用", "服务端尚未配置安全的导出生成与领取能力，本次没有创建导出任务。")
		return
	}
	traceID, err := randomText(12)
	var item store.UserPortalDataExport
	if err == nil {
		item, err = repository.RequestUserPortalDataExport(r.Context(), account.ID, strings.TrimSpace(r.Form.Get("idempotency_key")), traceID)
	}
	if err != nil {
		p.handleUserPortalActionError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.user.data_export.request.context", "data_export", item.ID)
	p.redirectUserPortal(w, r, "privacy", "export-requested", url.Values{"export_id": {item.ID.String()}})
}

func (p *Portal) userPortalDownloadDataExport(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireIdempotentStrongAccountRead(w, r)
	if !ok {
		return
	}
	exportID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	if err != nil {
		p.renderError(w, http.StatusNotFound, "数据导出不存在", "该导出不存在、尚未就绪、已过期或不属于当前账号。")
		return
	}
	downloader := p.config.DataExportDownloader
	if !dataExportDownloaderAvailable(downloader) {
		p.renderError(w, http.StatusServiceUnavailable, "数据导出暂不可用", "服务端尚未配置安全的导出领取能力，请稍后重试。")
		return
	}
	body, err := downloader.Download(r.Context(), account.ID, exportID)
	if err != nil {
		if errors.Is(err, privacyexport.ErrExportUnavailable) {
			p.renderError(w, http.StatusNotFound, "数据导出不存在", "该导出不存在、尚未就绪、已过期或不属于当前账号。")
			return
		}
		traceID := uuid.NewString()
		logPortalFailure(r.Context(), slog.LevelError, "download web privacy export",
			"web_privacy_export_download_failed", traceID, err)
		p.renderError(w, http.StatusServiceUnavailable, "数据导出暂不可用", "导出完整性校验或安全存储读取失败，请稍后重试。")
		return
	}
	defer clear(body)
	if len(body) == 0 || len(body) > maximumWebPrivacyExportBytes || !json.Valid(body) {
		traceID := uuid.NewString()
		logPortalFailure(r.Context(), slog.LevelError, "validate web privacy export",
			"web_privacy_export_invalid", traceID, privacyexport.ErrExportIntegrity)
		p.renderError(w, http.StatusServiceUnavailable, "数据导出暂不可用", "导出完整性校验失败，请稍后重试。")
		return
	}
	if err := p.recordUserPortalActionAudit(r, account.ID, "web.user.data_export.download", "data_export", exportID); err != nil {
		traceID := uuid.NewString()
		logPortalFailure(r.Context(), slog.LevelError, "record privacy export download audit",
			"web_privacy_export_audit_failed", traceID, err)
		p.renderError(w, http.StatusServiceUnavailable, "数据导出暂不可用", "安全审计暂不可用，为保护账号数据，本次未发送导出内容。")
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="termlink-data-export.json"`)
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(body)
}

// requireIdempotentStrongAccountRead adds portal idempotency only for a
// high-risk read capability. Store-backed user mutations use
// requireStrongAccountAction directly because they own their atomic
// idempotency transaction; the export downloader is deliberately read-only
// and therefore has no Store mutation in which to claim the key.
func (p *Portal) requireIdempotentStrongAccountRead(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireStrongAccountAction(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	slot, slotOK := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
	repository, repositoryOK := p.config.Repository.(sensitiveActionRepository)
	if !slotOK || slot == nil || slot.action == "" || !repositoryOK {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "idempotency_unavailable", "数据导出暂不可用", "安全审计或幂等保护暂不可用，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	key := strings.TrimSpace(r.Form.Get("idempotency_key"))
	requestHash := sensitiveRequestHash(slot.action, r.Form)
	claim, err := repository.ClaimIdempotency(r.Context(), account.ID, key, requestHash)
	if err != nil {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "idempotency_unavailable", "数据导出暂不可用", "安全审计或幂等保护暂不可用，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !claim.Claimed {
		p.rejectSensitiveAction(w, r, account, http.StatusConflict, "duplicate_request", "下载请求已提交", "该下载请求已处理或正在处理，请刷新隐私页面后重新领取。")
		return webSession{}, store.PortalAccount{}, false
	}
	slot.claim = &sensitiveActionClaim{repository: repository, accountID: account.ID, key: key, requestHash: requestHash}
	return session, account, true
}

func dataExportDownloaderAvailable(downloader PrivacyExportDownloader) bool {
	if downloader == nil {
		return false
	}
	value := reflect.ValueOf(downloader)
	switch value.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return !value.IsNil()
	default:
		return true
	}
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
	p.redirectUserPortal(w, r, "privacy", "deletion-requested", url.Values{"deletion_id": {item.ID.String()}})
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
	p.redirectUserPortal(w, r, "privacy", "deletion-confirmed", url.Values{"deletion_id": {requestID.String()}})
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
	p.redirectUserPortal(w, r, "privacy", "deletion-cancelled", url.Values{"deletion_id": {requestID.String()}})
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
		p.renderError(w, http.StatusConflict, "会话仍在运行", "只有已经退出的会话才能永久删除服务端历史。")
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
	repository, ok := p.config.Repository.(commerceExportAuditRepository)
	if !ok {
		return errors.New("web portal audit repository unavailable")
	}
	metadata, err := json.Marshal(map[string]string{"operation_reason": strings.TrimSpace(r.Form.Get("reason"))})
	if err != nil {
		return err
	}
	return repository.Audit(r.Context(), accountID, uuid.Nil, action, targetType, targetID, "SUCCEEDED", uuid.NewString(), string(metadata))
}

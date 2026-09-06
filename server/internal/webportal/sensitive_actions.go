package webportal

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const sensitiveAuditTimeout = 2 * time.Second

type sensitiveActionRepository interface {
	ClaimIdempotency(context.Context, uuid.UUID, string, [32]byte) (store.IdempotencyResult, error)
	CompleteIdempotency(context.Context, uuid.UUID, string, [32]byte, int, json.RawMessage) error
	RecordSecurityEvent(context.Context, store.SecurityEvent) error
}

type sensitiveActionContextKey struct{}

type sensitiveActionSlot struct {
	action string
	claim  *sensitiveActionClaim
}

type sensitiveActionClaim struct {
	repository  sensitiveActionRepository
	accountID   uuid.UUID
	key         string
	requestHash [32]byte
}

type sensitiveStatusWriter struct {
	http.ResponseWriter
	status int
}

func (w *sensitiveStatusWriter) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
	w.ResponseWriter.WriteHeader(status)
}

func (w *sensitiveStatusWriter) Write(contents []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	return w.ResponseWriter.Write(contents)
}

func (w *sensitiveStatusWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func (p *Portal) sensitiveAction(action string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		slot := &sensitiveActionSlot{action: action}
		request := r.WithContext(context.WithValue(r.Context(), sensitiveActionContextKey{}, slot))
		response := &sensitiveStatusWriter{ResponseWriter: w}
		next(response, request)
		if slot.claim == nil {
			return
		}
		status := response.status
		if status == 0 {
			status = http.StatusOK
		}
		completionStatus := status
		if status < http.StatusOK || status >= http.StatusBadRequest {
			// ClaimIdempotency permits the same request hash to retry only when
			// the stored status is 5xx. A handler failure must therefore never
			// become a permanent 4xx idempotency result even though the original
			// client still receives that handler status.
			completionStatus = http.StatusInternalServerError
		}
		ctx, cancel := context.WithTimeout(context.WithoutCancel(r.Context()), sensitiveAuditTimeout)
		defer cancel()
		if err := slot.claim.repository.CompleteIdempotency(
			ctx, slot.claim.accountID, slot.claim.key, slot.claim.requestHash, completionStatus, nil,
		); err != nil {
			// The handler response has already been committed. Completion failure
			// can only be recorded safely here; attempting to render another HTTP
			// response would corrupt the response already sent to the client.
			logPortalFailure(ctx, slog.LevelError, "complete sensitive web action idempotency",
				"sensitive_idempotency_completion_failed", uuid.NewString(), err,
				slog.String("action", action),
				slog.Int("response_status", status),
				slog.Int("completion_status", completionStatus),
			)
		}
	}
}

// strongAccountAction supplies the sensitive-action audit namespace without
// claiming the shared idempotency record. Account-scoped Store methods own
// their idempotency transaction and must not be placed behind a second claim.
func (p *Portal) strongAccountAction(action string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		slot := &sensitiveActionSlot{action: action}
		next(w, r.WithContext(context.WithValue(r.Context(), sensitiveActionContextKey{}, slot)))
	}
}

func (p *Portal) requireStrongAccountAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireAccount(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "csrf_invalid", "账号请求被拒绝", "请求来源或安全校验无效，请刷新用户中心后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !account.Role.Valid() || !store.Role(session.Role).Valid() {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "role_denied", "账号请求被拒绝", "当前账号角色或浏览器会话无效。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !recentStrongVerification(session, p.now()) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "step_up_required", "需要安全验证", "请返回操作页面完成弹出的安全验证：已绑定 MFA 时完成多因素认证，未绑定时重新验证一次登录密码。")
		return webSession{}, store.PortalAccount{}, false
	}
	slot, slotOK := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
	if !slotOK || slot == nil || slot.action == "" {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "audit_unavailable", "账号操作暂不可用", "操作记录配置不完整，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	reason := ensureSensitiveActionReason(r.Form, slot.action)
	if !validSensitiveReason(reason) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "reason_invalid", "操作原因无效", "请填写 3 到 500 个字符的操作原因。")
		return webSession{}, store.PortalAccount{}, false
	}
	key := strings.TrimSpace(r.Form.Get("idempotency_key"))
	if !validSensitiveIdempotencyKey(key) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "idempotency_key_invalid", "请求标识无效", "请刷新用户中心后重新提交。")
		return webSession{}, store.PortalAccount{}, false
	}
	// Successful account actions are audited atomically by the Store mutation.
	// RecordSecurityEvent is deliberately denial/failure-only, so emitting a
	// pre-authorization success event here would both violate that contract and
	// separate the audit result from the actual state change.
	return session, account, true
}

func (p *Portal) requireSensitiveAdminAction(w http.ResponseWriter, r *http.Request, superOnly bool) (webSession, store.PortalAccount, bool) {
	return p.requireSensitiveConsoleAction(w, r, superOnly, false)
}

// requireSensitiveReportingAction applies the same strong-authentication and
// replay protections as a mutating administrator action, while retaining the
// Store's read-only AUDITOR role for bounded diagnostic access.
func (p *Portal) requireSensitiveReportingAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	return p.requireSensitiveConsoleAction(w, r, false, true)
}

func (p *Portal) requireSensitiveConsoleAction(w http.ResponseWriter, r *http.Request, superOnly, allowAuditor bool) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "csrf_invalid", "管理请求被拒绝", "请求来源或安全校验无效，请刷新管理台后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	currentRole := account.Role
	sessionRole := store.Role(session.Role)
	if superOnly {
		if currentRole != store.RoleSuperAdmin || sessionRole != store.RoleSuperAdmin {
			p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "role_denied", "需要超级管理员", "该操作仅允许 SUPER_ADMIN 执行。")
			return webSession{}, store.PortalAccount{}, false
		}
	} else if allowAuditor {
		if !currentRole.CanOpenConsole() || !sessionRole.CanOpenConsole() {
			p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "role_denied", "需要审计权限", "该导出仅允许 AUDITOR、ADMIN 或 SUPER_ADMIN 执行。")
			return webSession{}, store.PortalAccount{}, false
		}
	} else if (currentRole != store.RoleAdmin && currentRole != store.RoleSuperAdmin) ||
		(sessionRole != store.RoleAdmin && sessionRole != store.RoleSuperAdmin) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "role_denied", "需要运营管理员", "AUDITOR 仅能查看管理数据，不能执行修改。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !recentStrongVerification(session, p.now()) {
		p.rejectSensitiveAction(w, r, account, http.StatusForbidden, "step_up_required", "需要安全验证", "请返回管理页面完成弹出的安全验证：已绑定 MFA 时完成多因素认证，未绑定时重新验证一次登录密码。")
		return webSession{}, store.PortalAccount{}, false
	}
	slot, slotOK := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot)
	if !slotOK || slot == nil || slot.action == "" {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "idempotency_unavailable", "管理请求暂不可用", "操作记录或幂等保护暂不可用，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	reason := ensureSensitiveActionReason(r.Form, slot.action)
	if !validSensitiveReason(reason) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "reason_invalid", "操作原因无效", "请填写 3 到 500 个字符的操作原因。")
		return webSession{}, store.PortalAccount{}, false
	}
	key := strings.TrimSpace(r.Form.Get("idempotency_key"))
	if !validSensitiveIdempotencyKey(key) {
		p.rejectSensitiveAction(w, r, account, http.StatusBadRequest, "idempotency_key_invalid", "请求标识无效", "请刷新管理页面后重新提交。")
		return webSession{}, store.PortalAccount{}, false
	}
	repository, repositoryOK := p.config.Repository.(sensitiveActionRepository)
	if !repositoryOK {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "idempotency_unavailable", "管理请求暂不可用", "操作记录或幂等保护暂不可用，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	requestHash := sensitiveRequestHash(slot.action, r.Form)
	claim, err := repository.ClaimIdempotency(r.Context(), account.ID, key, requestHash)
	if err != nil {
		p.rejectSensitiveAction(w, r, account, http.StatusServiceUnavailable, "idempotency_unavailable", "管理请求暂不可用", "操作记录或幂等保护暂不可用，请稍后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	if !claim.Claimed {
		p.rejectSensitiveAction(w, r, account, http.StatusConflict, "duplicate_request", "请求已提交", "该操作请求已处理或正在处理，请刷新页面确认最新状态。")
		return webSession{}, store.PortalAccount{}, false
	}
	slot.claim = &sensitiveActionClaim{repository: repository, accountID: account.ID, key: key, requestHash: requestHash}
	return session, account, true
}

func (p *Portal) rejectSensitiveAction(w http.ResponseWriter, r *http.Request, account store.PortalAccount, status int, reason, title, message string) {
	action := "unknown"
	if slot, ok := r.Context().Value(sensitiveActionContextKey{}).(*sensitiveActionSlot); ok && slot != nil && slot.action != "" {
		action = slot.action
	}
	metadata, _ := json.Marshal(map[string]string{"action": action, "reason": reason})
	if repository, ok := p.config.Repository.(interface {
		RecordSecurityEvent(context.Context, store.SecurityEvent) error
	}); ok {
		accountID := account.ID
		ctx, cancel := context.WithTimeout(context.WithoutCancel(r.Context()), sensitiveAuditTimeout)
		_ = repository.RecordSecurityEvent(ctx, store.SecurityEvent{
			UserID: &accountID, Action: "web.sensitive_action.denied", TargetType: "web_action",
			Result: "DENIED", TraceID: uuid.NewString(), Metadata: metadata,
		})
		cancel()
	}
	p.renderError(w, status, title, message)
}

func sensitiveRequestHash(action string, form url.Values) [32]byte {
	values := make(url.Values, len(form))
	for name, items := range form {
		if name == "csrf" {
			continue
		}
		values[name] = append([]string(nil), items...)
	}
	material := "TermLink sensitive web action v1\x00" + action + "\x00" + values.Encode()
	return sha256.Sum256([]byte(material))
}

func sensitiveActionKey(nonce, scope string) string {
	digest := sha256.Sum256([]byte("TermLink sensitive form v1\x00" + nonce + "\x00" + scope))
	return base64.RawURLEncoding.EncodeToString(digest[:])
}

func validSensitiveReason(value string) bool {
	count := utf8.RuneCountInString(value)
	return count >= 3 && count <= 500 && safePublicText(value, 2000, true)
}

func ensureSensitiveActionReason(values url.Values, action string) string {
	reason := strings.TrimSpace(values.Get("reason"))
	if reason == "" || reason == "系统自动记录" {
		reason = "TermLink Web auto audit: " + action
		values.Set("reason", reason)
	}
	return reason
}

func validSensitiveIdempotencyKey(value string) bool {
	if len(value) < 8 || len(value) > 128 {
		return false
	}
	for _, character := range value {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || strings.ContainsRune("._:-", character) {
			continue
		}
		return false
	}
	return true
}

func recentStrongVerification(session webSession, now time.Time) bool {
	if session.MFA && recentVerificationTime(session.AuthenticatedAtMS, now) {
		return true
	}
	return recentSensitiveVerification(session, now)
}

func recentSensitiveVerification(session webSession, now time.Time) bool {
	if session.SensitiveMethod != sensitiveMethodMFA && session.SensitiveMethod != sensitiveMethodPassword {
		return false
	}
	return recentVerificationTime(session.SensitiveVerifiedAtMS, now)
}

func recentVerificationTime(milliseconds int64, now time.Time) bool {
	if milliseconds <= 0 {
		return false
	}
	age := now.Sub(time.UnixMilli(milliseconds))
	return age >= 0 && age <= sensitiveActionTTL
}

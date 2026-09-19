package webportal

import (
	"encoding/json"
	"net/http"

	"github.com/termlink/termlink/server/internal/store"
)

type sensitiveStepUpPageData struct {
	Status  string
	Method  string
	Title   string
	Message string
}

type sensitiveStepUpStatusResponse struct {
	Verified bool `json:"verified"`
}

// sensitiveStepUp starts a same-account verification inside the portal modal.
// Keycloak receives the OTP or password; TermLink only verifies the resulting
// signed identity. The callback is bound to the encrypted OAuth state and the
// already authenticated TermLink browser session.
func (p *Portal) sensitiveStepUp(w http.ResponseWriter, r *http.Request) {
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(r, w, sessionCookieName)
		p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "登录已失效", "请关闭验证窗口，重新登录后再执行该操作。")
		return
	}
	account, err := p.currentAccount(r.Context(), session)
	if err != nil || !account.Role.Valid() || account.Role != store.Role(session.Role) {
		p.renderSensitiveStepUpResult(w, http.StatusForbidden, "failed", "", "账号不可用", "账号权限已变化，请重新登录后再试。")
		return
	}
	// A status check can race with another tab completing verification. Keep the
	// start endpoint idempotent so that race never opens another identity prompt.
	if recentSensitiveVerification(session, p.now()) {
		p.renderSensitiveStepUpResult(w, http.StatusOK, "verified", session.SensitiveMethod, "安全验证仍然有效", "20 分钟安全验证有效期内无需再次输入，正在继续原操作。")
		return
	}
	method := sensitiveMethodPassword
	configuredMFA := session.MFA
	if p.config.IdentityMFAStatus != nil {
		configuredMFA, err = p.config.IdentityMFAStatus.HasConfiguredOTP(r.Context(), session.Subject)
		if err != nil {
			p.renderSensitiveStepUpResult(w, http.StatusServiceUnavailable, "failed", "", "暂时无法确认 MFA 状态", "身份服务暂时不可用，请关闭窗口后重试；本次高风险操作尚未执行。")
			return
		}
	}
	if configuredMFA {
		method = sensitiveMethodMFA
	}
	p.beginOIDC(w, r, "/auth/step-up/result", normalizeTheme(r.URL.Query().Get(themeQueryParameter)), sensitiveOIDCPurpose, method)
}

func (p *Portal) sensitiveStepUpStatus(w http.ResponseWriter, r *http.Request) {
	verified := false
	status := http.StatusOK
	if session, err := p.readSession(r); err != nil {
		p.clearCookie(r, w, sessionCookieName)
		status = http.StatusUnauthorized
	} else {
		verified = recentSensitiveVerification(session, p.now())
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(sensitiveStepUpStatusResponse{Verified: verified})
}

func (p *Portal) completeSensitiveStepUp(w http.ResponseWriter, r *http.Request, pending oauthState, identity accountIdentity) {
	session, err := p.readSession(r)
	if err != nil || !constantTextEqual(session.Subject, identity.Subject) {
		p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "验证账号不匹配", "必须使用当前登录账号完成安全验证。")
		return
	}
	account, err := p.currentAccount(r.Context(), session)
	if err != nil || !account.Role.Valid() {
		p.renderSensitiveStepUpResult(w, http.StatusForbidden, "failed", "", "账号不可用", "账号状态或权限已变化，请重新登录后再试。")
		return
	}
	method := pending.Method
	if method == sensitiveMethodMFA && !identity.MFA {
		p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "MFA 验证未完成", "身份服务没有返回本次操作所需的动态验证码认证。")
		return
	}
	if method == sensitiveMethodPassword && !identity.Password {
		p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "密码验证未完成", "未绑定 MFA 的账号必须重新验证一次登录密码。")
		return
	}
	if method != sensitiveMethodMFA && method != sensitiveMethodPassword {
		p.renderSensitiveStepUpResult(w, http.StatusBadRequest, "failed", "", "验证方式无效", "请关闭窗口后重新发起安全验证。")
		return
	}
	verifiedAtMS := p.now().UnixMilli()
	session.Role = string(account.Role)
	session.AuthenticatedAtMS = verifiedAtMS
	session.MFA = identity.MFA
	session.SensitiveVerifiedAtMS = verifiedAtMS
	session.SensitiveMethod = method
	if err := p.refreshSessionCookie(r, w, session); err != nil {
		p.internalError(w, err)
		return
	}
	http.Redirect(w, r, "/auth/step-up/result", http.StatusSeeOther)
}

func (p *Portal) sensitiveStepUpResult(w http.ResponseWriter, r *http.Request) {
	session, err := p.readSession(r)
	if err != nil || !recentSensitiveVerification(session, p.now()) {
		p.renderSensitiveStepUpResult(w, http.StatusUnauthorized, "failed", "", "安全验证未完成", "请关闭窗口后重新发起验证。")
		return
	}
	message := "登录密码验证通过，正在继续原操作。"
	if session.SensitiveMethod == sensitiveMethodMFA {
		message = "多因素认证通过，正在继续原操作。"
	}
	p.renderSensitiveStepUpResult(w, http.StatusOK, "verified", session.SensitiveMethod, "安全验证通过", message)
}

func (p *Portal) renderSensitiveStepUpResult(w http.ResponseWriter, status int, result, method, title, message string) {
	// This single result document is intentionally embeddable only by the same
	// TermLink origin. All ordinary portal pages remain frame-ancestors 'none'.
	w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'")
	w.Header().Set("X-Frame-Options", "SAMEORIGIN")
	p.render(w, status, "sensitive-step-up.html", sensitiveStepUpPageData{
		Status: result, Method: method, Title: title, Message: message,
	})
}

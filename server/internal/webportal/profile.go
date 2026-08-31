package webportal

import (
	"errors"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/serviceemail"
	"github.com/termlink/termlink/server/internal/store"
)

func (p *Portal) accountUpdateNickname(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, map[string]bool{"csrf": true, "nickname": true}); err != nil {
		p.renderError(w, http.StatusBadRequest, "昵称请求无效", "请刷新个人设置后重试。")
		return
	}
	nickname := strings.TrimSpace(r.Form.Get("nickname"))
	if p.config.IdentityProfiles == nil {
		p.renderError(w, http.StatusServiceUnavailable, "资料修改暂不可用", "当前部署尚未配置身份资料同步能力。")
		return
	}
	if !auth.ValidateNickname(nickname) {
		p.renderError(w, http.StatusBadRequest, "昵称无效", "昵称需要包含 1–64 个可见字符，不能包含控制符或危险的不可见字符。")
		return
	}
	if err := p.config.IdentityProfiles.UpdateNickname(r.Context(), session.Subject, nickname); err != nil {
		p.renderProfileIdentityError(w, err)
		return
	}
	session.Nickname = nickname
	if profileErr := p.config.Repository.SyncAccountIdentityProfile(r.Context(), account.ID,
		store.NewAccountIdentityProfile(session.Username, session.Nickname, session.Email)); profileErr != nil {
		logPortalFailure(r.Context(), slog.LevelWarn, "synchronize nickname display profile",
			"account_identity_profile_sync_failed", uuid.NewString(), profileErr,
			slog.String("account_id", account.ID.String()))
	}
	if err := p.refreshSessionCookie(w, session); err != nil {
		p.internalError(w, err)
		return
	}
	http.Redirect(w, r, "/account?section=settings&notice=nickname-updated", http.StatusSeeOther)
}

func (p *Portal) accountRequestEmailCode(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, map[string]bool{"csrf": true, "new_email": true}); err != nil {
		p.renderError(w, http.StatusBadRequest, "邮箱请求无效", "请刷新个人设置后重试。")
		return
	}
	if p.config.ServiceEmail == nil || p.config.IdentityProfiles == nil {
		p.renderError(w, http.StatusServiceUnavailable, "邮箱修改暂不可用", "当前部署尚未配置已验证的服务邮箱与身份资料同步。")
		return
	}
	newEmail := strings.TrimSpace(r.Form.Get("new_email"))
	if strings.EqualFold(newEmail, strings.TrimSpace(session.Email)) {
		p.renderError(w, http.StatusBadRequest, "邮箱没有变化", "请输入不同于当前邮箱的新地址。")
		return
	}
	challenge, err := p.config.ServiceEmail.RequestAccountEmailCode(r.Context(), account.ID, newEmail)
	if err != nil {
		p.renderAccountEmailError(w, err)
		return
	}
	data, err := p.loadCommerceUserSection(r.Context(), session, account, "settings", "验证码已经发送到新邮箱；完成验证前，当前邮箱和登录方式不会变化。")
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.PendingEmail = newEmail
	data.EmailChallengeID = challenge.ID
	data.EmailChallengeExpiresAt = challenge.ExpiresAt
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) accountConfirmEmail(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, map[string]bool{
		"csrf": true, "new_email": true, "verification_id": true, "email_code": true,
	}); err != nil {
		p.renderError(w, http.StatusBadRequest, "邮箱验证无效", "请重新发送验证码后再试。")
		return
	}
	age := p.now().Sub(time.UnixMilli(session.AuthenticatedAtMS))
	if session.AuthenticatedAtMS <= 0 || age < 0 || age > recentLoginTTL {
		p.renderError(w, http.StatusForbidden, "需要重新认证", "邮箱变更要求最近 10 分钟内完成网页登录，请退出后重新登录再试。")
		return
	}
	challengeID, err := uuid.Parse(strings.TrimSpace(r.Form.Get("verification_id")))
	if err != nil || p.config.ServiceEmail == nil || p.config.IdentityProfiles == nil {
		p.renderError(w, http.StatusBadRequest, "邮箱验证无效", "请重新发送验证码后再试。")
		return
	}
	newEmail := strings.TrimSpace(r.Form.Get("new_email"))
	if err = p.config.ServiceEmail.VerifyAndConsumeAccountEmail(r.Context(), account.ID, challengeID, newEmail, r.Form.Get("email_code")); err != nil {
		p.renderAccountEmailError(w, err)
		return
	}
	if err = p.config.IdentityProfiles.UpdateVerifiedEmail(r.Context(), session.Subject, newEmail); err != nil {
		p.renderProfileIdentityError(w, err)
		return
	}
	session.Email = newEmail
	if profileErr := p.config.Repository.SyncAccountIdentityProfile(r.Context(), account.ID,
		store.NewAccountIdentityProfile(session.Username, session.Nickname, session.Email)); profileErr != nil {
		logPortalFailure(r.Context(), slog.LevelWarn, "synchronize e-mail display profile",
			"account_identity_profile_sync_failed", uuid.NewString(), profileErr,
			slog.String("account_id", account.ID.String()))
	}
	if err = p.refreshSessionCookie(w, session); err != nil {
		p.internalError(w, err)
		return
	}
	http.Redirect(w, r, "/account?section=settings&notice=email-updated", http.StatusSeeOther)
}

func (p *Portal) refreshSessionCookie(w http.ResponseWriter, session webSession) error {
	encoded, err := p.sessionCodec.seal(session)
	if err != nil {
		return err
	}
	p.setCookie(w, sessionCookieName, encoded, time.UnixMilli(session.ExpiresAtMS))
	return nil
}

func (p *Portal) renderAccountEmailError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrEmailChallengeRateLimit):
		p.renderError(w, http.StatusTooManyRequests, "发送过于频繁", "同一邮箱每 60 秒只能发送一次验证码，请稍后重试。")
	case errors.Is(err, serviceemail.ErrVerificationInvalid):
		p.renderError(w, http.StatusBadRequest, "验证码无效", "验证码错误、已过期、已使用或尝试次数已耗尽，请重新发送。")
	case errors.Is(err, serviceemail.ErrDeliveryFailed):
		p.renderError(w, http.StatusBadGateway, "验证码发送失败", "邮件服务暂时无法完成投递，请稍后重试。")
	default:
		p.renderError(w, http.StatusServiceUnavailable, "邮箱验证暂不可用", "服务邮箱或身份同步当前不可用，请稍后重试。")
	}
}

func (p *Portal) renderProfileIdentityError(w http.ResponseWriter, err error) {
	if errors.Is(err, auth.ErrKeycloakAdminRejected) {
		p.renderError(w, http.StatusConflict, "资料无法更新", "该昵称或邮箱不能使用，或者邮箱已经属于其他账号；原资料保持不变。")
		return
	}
	p.renderError(w, http.StatusServiceUnavailable, "身份服务暂不可用", "资料尚未更新，请稍后重试。")
}

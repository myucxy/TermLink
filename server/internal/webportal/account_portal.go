package webportal

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type userPortalRepository interface {
	GetUserPortalAccountOverview(context.Context, uuid.UUID) (store.UserPortalAccountOverview, error)
	ListUserPortalDevices(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalDevice, error)
	ListUserPortalPCs(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalPC, error)
	ListUserPortalSessions(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalSession, error)
}

type userPortalPageData struct {
	AccountID                 uuid.UUID
	Username                  string
	Nickname                  string
	Email                     string
	Role                      store.Role
	RoleLabel                 string
	CanOpenConsole            bool
	ConsoleURL                string
	IdentityAccountURL        string
	CSRF                      string
	Section                   string
	SectionTitle              string
	Overview                  store.UserPortalAccountOverview
	Devices                   []store.UserPortalDevice
	PCs                       []store.UserPortalPC
	Sessions                  []store.UserPortalSession
	IdempotencyKey            string
	Notice                    string
	SensitiveActionsAvailable bool
	PCPolicies                []store.UserPortalPCControlPolicy
	PCRows                    []userPortalPCRow
	PrivacyPreflight          store.UserPortalAccountDeletionPreflight
	AccountDeletion           *store.UserPortalAccountDeletion
	ProfileChangeAvailable    bool
	EmailChangeAvailable      bool
	PendingEmail              string
	EmailChallengeID          uuid.UUID
	EmailChallengeExpiresAt   time.Time
}

type userPortalPCRow struct {
	PC              store.UserPortalPC
	Policy          store.UserPortalPCControlPolicy
	PolicyAvailable bool
}

func (p *Portal) accountDashboard(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireAccount(w, r)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(userPortalRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "用户中心暂不可用", "服务端尚未完成用户门户存储配置。")
		return
	}
	section := normalizeUserPortalSection(r.URL.Query().Get("section"))
	if section == "privacy" {
		section = "settings"
	}
	data, err := p.loadUserPortalPage(r.Context(), repository, session, account, section, userPortalNotice(r.URL.Query().Get("notice")))
	if err != nil {
		p.internalError(w, err)
		return
	}
	if (section == "privacy" || section == "settings") && data.SensitiveActionsAvailable {
		actionRepository := p.config.Repository.(userPortalActionRepository)
		if err := p.loadUserPortalPrivacySelection(r.Context(), actionRepository, account.ID, r.URL.Query(), &data); err != nil {
			if errors.Is(err, errUserPortalRequest) {
				p.renderError(w, http.StatusBadRequest, "隐私请求无效", "请从用户中心重新打开数据隐私页面。")
			} else if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
				p.renderError(w, http.StatusNotFound, "隐私请求不存在", "该请求不存在、已失效或不属于当前账号。")
			} else {
				p.internalError(w, err)
			}
			return
		}
	}
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) loadUserPortalSection(ctx context.Context, session webSession, account store.PortalAccount, section, notice string) (userPortalPageData, error) {
	repository, available := p.config.Repository.(userPortalRepository)
	if !available {
		return userPortalPageData{}, errors.New("user portal repository is unavailable")
	}
	return p.loadUserPortalPage(ctx, repository, session, account, section, notice)
}

func (p *Portal) loadUserPortalPage(ctx context.Context, repository userPortalRepository, session webSession, account store.PortalAccount, section, notice string) (userPortalPageData, error) {
	section = normalizeUserPortalSection(section)
	idempotencyKey, err := randomText(24)
	if err != nil {
		return userPortalPageData{}, err
	}
	consoleURL, _ := p.adminBasePath(ctx)
	data := userPortalPageData{
		AccountID: account.ID, Username: session.Username, Nickname: firstNonEmpty(session.Nickname, session.Username),
		Email: session.Email, Role: account.Role, RoleLabel: roleLabel(account.Role),
		CanOpenConsole: account.Role.CanOpenConsole() && consoleURL != "", ConsoleURL: consoleURL,
		IdentityAccountURL: "/account/identity", CSRF: session.CSRF, Section: section,
		SectionTitle: userPortalSectionTitle(section), IdempotencyKey: idempotencyKey, Notice: notice,
	}
	data.ProfileChangeAvailable = p.config.IdentityProfiles != nil
	if p.config.ServiceEmail != nil {
		data.EmailChangeAvailable = data.ProfileChangeAvailable && p.config.ServiceEmail.Runtime(ctx).ServiceAvailable
	}
	actionRepository, actionsAvailable := p.config.Repository.(userPortalActionRepository)
	data.SensitiveActionsAvailable = actionsAvailable
	request := store.UserPortalPageRequest{Limit: 50}
	switch section {
	case "overview":
		data.Overview, err = repository.GetUserPortalAccountOverview(ctx, account.ID)
	case "devices":
		data.Devices, err = repository.ListUserPortalDevices(ctx, account.ID, request)
	case "pcs":
		data.PCs, err = repository.ListUserPortalPCs(ctx, account.ID, request)
		if err == nil {
			data.PCRows = make([]userPortalPCRow, 0, len(data.PCs))
			for _, pc := range data.PCs {
				data.PCRows = append(data.PCRows, userPortalPCRow{PC: pc, Policy: store.UserPortalPCControlPolicy{PCID: pc.ID, Name: pc.Name}})
			}
			if actionsAvailable {
				data.PCPolicies, err = actionRepository.ListUserPortalPCControlPolicies(ctx, account.ID, 50)
				if err == nil {
					byID := make(map[uuid.UUID]store.UserPortalPCControlPolicy, len(data.PCPolicies))
					for _, policy := range data.PCPolicies {
						byID[policy.PCID] = policy
					}
					for index := range data.PCRows {
						if policy, ok := byID[data.PCRows[index].PC.ID]; ok {
							data.PCRows[index].Policy = policy
							data.PCRows[index].PolicyAvailable = true
						}
					}
				}
			}
		}
	case "sessions":
		data.Sessions, err = repository.ListUserPortalSessions(ctx, account.ID, request)
	case "privacy", "settings":
		if actionsAvailable {
			data.PrivacyPreflight, err = actionRepository.GetUserPortalAccountDeletionPreflight(ctx, account.ID)
		}
	}
	if err == nil && p.config.Presence != nil {
		p.applyCachedPortalPresence(account.ID, &data)
	}
	return data, err
}

func (p *Portal) applyCachedPortalPresence(accountID uuid.UUID, data *userPortalPageData) {
	if p == nil || p.config.Presence == nil || data == nil {
		return
	}
	if data.Section == "overview" {
		data.Overview.OnlinePCs = p.config.Presence.OnlineDeviceCount(accountID, "pc")
	}
	for index := range data.Devices {
		seen, online := p.config.Presence.Online(accountID, data.Devices[index].ID)
		data.Devices[index].Online = online
		if online {
			seen = seen.UTC()
			data.Devices[index].LastSeenAt = &seen
		}
	}
	for index := range data.PCs {
		seen, online := p.config.Presence.Online(accountID, data.PCs[index].DeviceID)
		data.PCs[index].Online = online
		if online {
			seen = seen.UTC()
			data.PCs[index].LastSeenAt = &seen
		}
	}
}

func normalizeUserPortalSection(section string) string {
	switch strings.ToLower(strings.TrimSpace(section)) {
	case "overview", "devices", "pcs", "sessions", "privacy", "settings":
		return strings.ToLower(strings.TrimSpace(section))
	default:
		return "overview"
	}
}

func userPortalSectionTitle(section string) string {
	return map[string]string{
		"overview": "账号概览", "devices": "设备", "pcs": "Windows PC", "sessions": "会话元数据",
		"privacy": "数据隐私", "settings": "个人设置与登录安全",
	}[normalizeUserPortalSection(section)]
}

func userPortalNotice(value string) string {
	return map[string]string{
		"device-revoked":     bilingualPortalNotice("设备已撤销；相关授权、控制租约和后续密钥分发已停止。", "The device was revoked. Its grants, control leases, and future key distribution were stopped."),
		"device-disabled":    bilingualPortalNotice("设备已禁用；该设备将无法继续登录。", "The device was disabled and can no longer sign in."),
		"device-deleted":     bilingualPortalNotice("设备已删除；使用该设备重新登录后会重新注册。", "The device was deleted and will register again on its next sign-in."),
		"pc-policy-updated":  bilingualPortalNotice("PC 远程路由和控制策略已更新。", "The PC remote route and control policy were updated."),
		"deletion-requested": bilingualPortalNotice("账号注销申请已创建，仍需在确认时限内完成二次确认。", "The account deletion request was created and still requires confirmation before the deadline."),
		"deletion-confirmed": bilingualPortalNotice("账号注销已确认并进入 7 天冷静期；当前仍未删除，可在允许状态下取消。", "Account deletion was confirmed and entered the 7-day cooling-off period. Nothing has been deleted yet, and cancellation remains available when permitted."),
		"deletion-cancelled": bilingualPortalNotice("账号注销申请已取消，账号数据没有被删除。", "The account deletion request was cancelled. Account data was not deleted."),
		"nickname-updated":   bilingualPortalNotice("昵称已更新；用户名和用户 ID 保持不变。", "The nickname was updated. The username and user ID did not change."),
		"email-updated":      bilingualPortalNotice("新邮箱已验证并生效，现在可以使用该邮箱登录。", "The new email address was verified and is now active for sign-in."),
	}[value]
}

func (p *Portal) requireAccount(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		http.Redirect(w, r, "/login?return_to=/account", http.StatusSeeOther)
		return webSession{}, store.PortalAccount{}, false
	}
	account, err := p.currentAccount(r.Context(), session)
	if err != nil {
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号已被停用或浏览器会话需要重新登录。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) requireAccountAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireAccount(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusForbidden, "请求被拒绝", "请求来源或安全校验无效，请刷新页面后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func validatePortalFormValues(values url.Values, allowed map[string]bool) error {
	for name, entries := range values {
		if !allowed[name] || len(entries) != 1 {
			return errors.New("invalid portal form")
		}
	}
	return nil
}

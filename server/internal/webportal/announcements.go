package webportal

import (
	"context"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

const announcementDateTimeLayout = "2006-01-02T15:04"

type announcementRepository interface {
	ListActiveWebAnnouncements(context.Context, int) ([]store.WebAnnouncement, error)
	ListAdminWebAnnouncements(context.Context, uuid.UUID, int) ([]store.WebAnnouncement, error)
	CreateWebAnnouncement(context.Context, uuid.UUID, store.WebAnnouncementInput, string) (store.WebAnnouncement, error)
	UpdateWebAnnouncement(context.Context, uuid.UUID, uuid.UUID, int64, store.WebAnnouncementInput, string) (store.WebAnnouncement, error)
	TransitionWebAnnouncement(context.Context, uuid.UUID, uuid.UUID, int64, string, string, string) (store.WebAnnouncement, error)
}

func (p *Portal) adminCreateAnnouncement(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(announcementRepository)
	input, inputErr := parseWebAnnouncementInput(r)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "公告管理暂不可用", "当前服务端尚未完成公告存储配置。")
		return
	}
	if inputErr != nil {
		p.renderError(w, http.StatusBadRequest, "公告内容无效", "请检查标题、正文、级别和生效时间。")
		return
	}
	traceID, err := randomText(12)
	if err != nil {
		p.internalError(w, err)
		return
	}
	if _, err = repository.CreateWebAnnouncement(r.Context(), account.ID, input, traceID); err != nil {
		p.handleAnnouncementMutationError(w, err)
		return
	}
	http.Redirect(w, r, "/admin?section=announcements&notice=announcement-created", http.StatusSeeOther)
}

func (p *Portal) adminUpdateAnnouncement(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(announcementRepository)
	announcementID, idErr := uuid.Parse(r.PathValue("id"))
	expectedVersion, versionErr := strconv.ParseInt(strings.TrimSpace(r.Form.Get("expected_version")), 10, 64)
	input, inputErr := parseWebAnnouncementInput(r)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "公告管理暂不可用", "当前服务端尚未完成公告存储配置。")
		return
	}
	if idErr != nil || announcementID == uuid.Nil || versionErr != nil || expectedVersion < 1 || inputErr != nil {
		p.renderError(w, http.StatusBadRequest, "公告内容无效", "请刷新页面后检查标题、正文、级别和生效时间。")
		return
	}
	traceID, err := randomText(12)
	if err != nil {
		p.internalError(w, err)
		return
	}
	if _, err = repository.UpdateWebAnnouncement(r.Context(), account.ID, announcementID, expectedVersion, input, traceID); err != nil {
		p.handleAnnouncementMutationError(w, err)
		return
	}
	http.Redirect(w, r, "/admin?section=announcements&notice=announcement-updated", http.StatusSeeOther)
}

func (p *Portal) adminTransitionAnnouncement(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireSensitiveAdminAction(w, r, false)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(announcementRepository)
	announcementID, idErr := uuid.Parse(r.PathValue("id"))
	expectedVersion, versionErr := strconv.ParseInt(strings.TrimSpace(r.Form.Get("expected_version")), 10, 64)
	targetStatus := strings.ToUpper(strings.TrimSpace(r.Form.Get("target_status")))
	formErr := validateCommerceFormValues(r.Form, map[string]bool{
		"csrf": true, "idempotency_key": true, "reason": true, "expected_version": true, "target_status": true,
	})
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "公告管理暂不可用", "当前服务端尚未完成公告存储配置。")
		return
	}
	if formErr != nil || idErr != nil || announcementID == uuid.Nil || versionErr != nil || expectedVersion < 1 ||
		(targetStatus != store.AnnouncementPublished && targetStatus != store.AnnouncementArchived) {
		p.renderError(w, http.StatusBadRequest, "公告状态无效", "请刷新页面后重新选择发布或下线操作。")
		return
	}
	traceID, err := randomText(12)
	if err != nil {
		p.internalError(w, err)
		return
	}
	if _, err = repository.TransitionWebAnnouncement(
		r.Context(), account.ID, announcementID, expectedVersion, targetStatus, r.Form.Get("reason"), traceID,
	); err != nil {
		p.handleAnnouncementMutationError(w, err)
		return
	}
	notice := "announcement-archived"
	if targetStatus == store.AnnouncementPublished {
		notice = "announcement-published"
	}
	http.Redirect(w, r, "/admin?section=announcements&notice="+notice, http.StatusSeeOther)
}

func parseWebAnnouncementInput(r *http.Request) (store.WebAnnouncementInput, error) {
	if r == nil || r.Form == nil {
		return store.WebAnnouncementInput{}, errors.New("announcement form is unavailable")
	}
	allowed := map[string]bool{
		"csrf": true, "idempotency_key": true, "reason": true, "expected_version": true,
		"title": true, "body": true, "severity": true, "starts_at": true, "ends_at": true,
	}
	if validateCommerceFormValues(r.Form, allowed) != nil {
		return store.WebAnnouncementInput{}, errors.New("announcement form contains unsupported fields")
	}
	startsAt, err := parseOptionalAnnouncementTime(r.Form.Get("starts_at"))
	if err != nil {
		return store.WebAnnouncementInput{}, err
	}
	endsAt, err := parseOptionalAnnouncementTime(r.Form.Get("ends_at"))
	if err != nil {
		return store.WebAnnouncementInput{}, err
	}
	return store.WebAnnouncementInput{
		Title: r.Form.Get("title"), Body: r.Form.Get("body"), Severity: r.Form.Get("severity"),
		StartsAt: startsAt, EndsAt: endsAt, Reason: r.Form.Get("reason"),
	}, nil
}

func parseOptionalAnnouncementTime(value string) (*time.Time, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parsed, err := time.ParseInLocation(announcementDateTimeLayout, value, time.UTC)
	if err != nil {
		return nil, err
	}
	parsed = parsed.UTC()
	return &parsed, nil
}

func formatAnnouncementTimeInput(value *time.Time) string {
	if value == nil {
		return ""
	}
	return value.UTC().Format(announcementDateTimeLayout)
}

func announcementSeverityLabel(value string) string {
	switch value {
	case store.AnnouncementCritical:
		return "重要"
	case store.AnnouncementWarning:
		return "提醒"
	default:
		return "通知"
	}
}

func announcementStatusLabel(value string) string {
	switch value {
	case store.AnnouncementPublished:
		return "已发布"
	case store.AnnouncementArchived:
		return "已下线"
	default:
		return "草稿"
	}
}

func (p *Portal) handleAnnouncementMutationError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrConflict):
		p.renderError(w, http.StatusConflict, "公告已发生变化", "其他管理员已更新该公告，请刷新页面后重试。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "公告不存在", "该公告可能已被移除。")
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "公告操作被拒绝", "当前账号、公告状态或提交内容不允许该操作。")
	default:
		p.internalError(w, err)
	}
}

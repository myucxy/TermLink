package webportal

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

func (*testPortalRepository) ListActiveWebAnnouncements(context.Context, int) ([]store.WebAnnouncement, error) {
	return nil, nil
}

func (*testPortalRepository) ListAdminWebAnnouncements(context.Context, uuid.UUID, int) ([]store.WebAnnouncement, error) {
	return []store.WebAnnouncement{{
		ID: uuid.MustParse("33333333-3333-4333-8333-333333333333"), Title: `<script>alert("title")</script>`,
		Body: "维护窗口\n不会解析 <b>HTML</b>", Severity: store.AnnouncementWarning,
		Status: store.AnnouncementDraft, Version: 2, CreatedAt: time.Date(2026, 8, 27, 1, 0, 0, 0, time.UTC),
		UpdatedAt: time.Date(2026, 8, 27, 2, 0, 0, 0, time.UTC),
	}}, nil
}

func (*testPortalRepository) CreateWebAnnouncement(_ context.Context, _ uuid.UUID, input store.WebAnnouncementInput, _ string) (store.WebAnnouncement, error) {
	return store.WebAnnouncement{ID: uuid.New(), Title: input.Title, Body: input.Body, Severity: input.Severity, Status: store.AnnouncementDraft, Version: 1}, nil
}

func (*testPortalRepository) UpdateWebAnnouncement(_ context.Context, _, id uuid.UUID, version int64, input store.WebAnnouncementInput, _ string) (store.WebAnnouncement, error) {
	return store.WebAnnouncement{ID: id, Title: input.Title, Body: input.Body, Severity: input.Severity, Status: store.AnnouncementDraft, Version: version + 1}, nil
}

func (*testPortalRepository) TransitionWebAnnouncement(_ context.Context, _, id uuid.UUID, version int64, status, _, _ string) (store.WebAnnouncement, error) {
	return store.WebAnnouncement{ID: id, Status: status, Version: version + 1}, nil
}

func TestPortalAnnouncementManagementRendersEscapedPlainTextAndMutatesSafely(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "announcement-admin", Username: "announcement-admin", Roles: []string{string(store.RoleSuperAdmin)}, MFA: true,
	}}
	portal := newTestPortal(t, flow)
	flow.identity.AuthenticatedAt = portal.now().Add(-time.Minute)
	cookie, session := completeLoginAt(t, portal, flow, "/admin")

	page := performRequest(portal.Handler(), http.MethodGet, "/admin?section=announcements", nil, cookie)
	if page.Code != http.StatusOK {
		t.Fatalf("announcement page status=%d body=%q", page.Code, page.Body.String())
	}
	body := page.Body.String()
	for _, expected := range []string{"公告管理", "创建草稿", "维护窗口", "&lt;script&gt;", "&lt;b&gt;HTML&lt;/b&gt;"} {
		if !strings.Contains(body, expected) {
			t.Errorf("announcement page omitted %q", expected)
		}
	}
	if strings.Contains(body, `<script>alert("title")</script>`) || strings.Contains(body, "<b>HTML</b>") {
		t.Fatal("announcement page rendered administrator content as HTML")
	}

	values := url.Values{
		"csrf": {session.CSRF}, "idempotency_key": {"announcement-create-test-0001"},
		"title": {"服务维护"}, "body": {"今晚进行例行维护"}, "severity": {store.AnnouncementInfo},
		"starts_at": {"2026-08-28T01:00"}, "ends_at": {"2026-08-28T02:00"}, "reason": {"发布维护提醒"},
	}
	response := performPortalForm(portal.Handler(), "/admin/announcements", values, cookie)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/admin?section=announcements&notice=announcement-created" {
		t.Fatalf("announcement create status=%d location=%q body=%q", response.Code, response.Header().Get("Location"), response.Body.String())
	}
}

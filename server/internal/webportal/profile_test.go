package webportal

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/store"
)

type testIdentityProfiles struct {
	subject  string
	nickname string
	email    string
	err      error
}

func (p *testIdentityProfiles) UpdateNickname(_ context.Context, subject, nickname string) error {
	p.subject, p.nickname = subject, nickname
	return p.err
}

func (p *testIdentityProfiles) UpdateVerifiedEmail(_ context.Context, subject, email string) error {
	p.subject, p.email = subject, email
	return p.err
}

func TestAccountNicknameUpdateUsesIdentityAuthorityAndRefreshesSession(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	profiles := &testIdentityProfiles{}
	portal.config.Repository = repository
	portal.config.IdentityProfiles = profiles
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "nickname-csrf", time.Minute)

	response := performCommerceForm(portal.Handler(), "/account/profile/nickname", url.Values{
		"csrf": {"nickname-csrf"}, "nickname": {"清风惹尘丶"},
	}, cookie)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/account?section=settings&notice=nickname-updated" {
		t.Fatalf("nickname response=%d location=%q body=%s", response.Code, response.Header().Get("Location"), response.Body.String())
	}
	if profiles.subject != "commerce-subject" || profiles.nickname != "清风惹尘丶" {
		t.Fatalf("identity profile update=%q/%q", profiles.subject, profiles.nickname)
	}
	updated := response.Result().Cookies()
	if len(updated) == 0 {
		t.Fatal("updated session cookie missing")
	}
	recorder := performRequest(portal.Handler(), http.MethodGet, "/account?section=settings", nil, updated[0])
	if recorder.Code != http.StatusOK || !strings.Contains(recorder.Body.String(), "清风惹尘丶") || !strings.Contains(recorder.Body.String(), "不可修改") {
		t.Fatalf("updated settings page=%d body=%s", recorder.Code, recorder.Body.String())
	}
}

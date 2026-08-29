package webportal

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/buildinfo"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPortalSystemVersionsRoleAccessAndSafeRendering(t *testing.T) {
	now := time.Date(2026, time.August, 27, 9, 15, 0, 0, time.UTC)
	revision := strings.Repeat("d", 40)
	minimumWindowsBuild := int64(41)
	forceAfter := now.Add(72 * time.Hour)
	source := publicReleaseSourceFunc(func(_ context.Context, target PublicReleaseTarget) (PublicRelease, error) {
		release := systemReleaseFixture(target, now)
		release.DownloadURL = "https://user:provider-secret@appship.example/private-artifact"
		release.SHA256 = strings.Repeat("e", 64)
		release.SignerFingerprint = strings.Repeat("f", 40)
		if target.Platform == "android" {
			release.Version = "0.2.7"
			release.Build = 48
			release.MinimumOS = "Android 10"
			release.ReleaseNotes = `<script>alert("release")</script>`
			return release, nil
		}
		release.Version = "0.1.36"
		release.Build = 49
		release.MinimumOS = "Windows 10 1809"
		release.Required = true
		release.MinimumSupportedBuild = &minimumWindowsBuild
		release.ForceAfter = &forceAfter
		return release, nil
	})

	for _, role := range []store.Role{store.RoleSuperAdmin, store.RoleAdmin, store.RoleAuditor} {
		t.Run(string(role), func(t *testing.T) {
			flow := &testOIDCFlow{identity: accountIdentity{
				Subject: "version-viewer-" + string(role), Username: "version-viewer", Roles: []string{string(role)}, MFA: true,
			}}
			portal := newTestPortal(t, flow)
			portal.config.Repository.(*testPortalRepository).account.Role = role
			portal.systemVersions = NewSystemVersionsService(SystemVersionsOptions{
				Server:        buildinfo.Parse("0.1.35", revision, "2026-08-26T15:44:03Z"),
				ReleaseSource: source, Clock: func() time.Time { return now },
			})
			cookie, _ := completeLoginAt(t, portal, flow, "/admin")
			response := performRequest(portal.Handler(), http.MethodGet, "/admin?section=versions", nil, cookie)
			if response.Code != http.StatusOK {
				t.Fatalf("system versions status = %d, body=%q", response.Code, response.Body.String())
			}
			body := response.Body.String()
			for _, expected := range []string{
				"系统版本", "0.1.35", revision, "Android 10", "0.2.7", "Build 48",
				"Windows 10 1809", "0.1.36", "Build 49", "最低支持 Build 41",
				"发布源未定义，不进行推断", "必须更新", `href="/downloads"`,
				`&lt;script&gt;alert(&#34;release&#34;)&lt;/script&gt;`,
			} {
				if !strings.Contains(body, expected) {
					t.Errorf("system versions page omitted %q", expected)
				}
			}
			for _, forbidden := range []string{
				`<script>alert("release")</script>`, "provider-secret", "private-artifact",
				strings.Repeat("e", 64), strings.Repeat("f", 40),
			} {
				if strings.Contains(body, forbidden) {
					t.Errorf("system versions page exposed %q", forbidden)
				}
			}
		})
	}

	flow := &testOIDCFlow{identity: accountIdentity{Subject: "regular-version-user", Username: "regular-user"}}
	portal := newTestPortal(t, flow)
	cookie, _ := completeLogin(t, portal, flow)
	response := performRequest(portal.Handler(), http.MethodGet, "/admin?section=versions", nil, cookie)
	if response.Code != http.StatusForbidden || !strings.Contains(response.Body.String(), "无权访问管理台") {
		t.Fatalf("regular user system versions response = %d %q", response.Code, response.Body.String())
	}
}

func TestPortalSystemVersionsKeepsUnknownAndProviderErrorsCoarse(t *testing.T) {
	flow := &testOIDCFlow{identity: accountIdentity{
		Subject: "unknown-version-admin", Username: "unknown-version-admin", Roles: []string{string(store.RoleSuperAdmin)}, MFA: true,
	}}
	portal := newTestPortal(t, flow)
	portal.systemVersions = NewSystemVersionsService(SystemVersionsOptions{
		Server: buildinfo.Info{Version: "dev"},
		ReleaseSource: publicReleaseSourceFunc(func(context.Context, PublicReleaseTarget) (PublicRelease, error) {
			return PublicRelease{}, errors.New("https://user:credential@appship.internal release_id=private-id")
		}),
	})
	cookie, _ := completeLoginAt(t, portal, flow, "/admin")
	response := performRequest(portal.Handler(), http.MethodGet, "/admin?section=versions", nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("unknown system versions status = %d, body=%q", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{"本地部署构建", "构建信息未注入", "暂不可用", "不会展示上游错误或过期缓存"} {
		if !strings.Contains(body, expected) {
			t.Errorf("unknown system versions page omitted %q", expected)
		}
	}
	for _, forbidden := range []string{"credential", "appship.internal", "release_id", "private-id", ">dev<"} {
		if strings.Contains(strings.ToLower(body), forbidden) {
			t.Errorf("unknown system versions page exposed %q", forbidden)
		}
	}
}

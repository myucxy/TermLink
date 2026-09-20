package webportal

import (
	"context"
	"net/http"
	"net/url"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type testRuntimeSettingRepository struct {
	*testPortalRepository
	values map[string]string
}

func (r *testRuntimeSettingRepository) GetRuntimeSetting(_ context.Context, key string) (string, bool, error) {
	value, found := r.values[key]
	return value, found, nil
}

func (r *testRuntimeSettingRepository) SetRuntimeSetting(_ context.Context, key, value string, _ uuid.UUID) error {
	r.values[key] = value
	return nil
}

func TestAdminUpdateRuntimeSettingPersistsAndApplies(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	base := newTestPortalRepository()
	base.account.Role = store.RoleSuperAdmin
	repository := &testRuntimeSettingRepository{testPortalRepository: base, values: map[string]string{}}
	portal.config.Repository = repository
	portal.config.RuntimeSettingDefaults = map[string]string{
		store.RuntimeSettingDirectSTUNServers: "stun:stun.default.example:3478",
	}
	applied := ""
	portal.config.ApplyDirectSTUNServers = func(raw string) error {
		applied = raw
		return nil
	}
	cookie := portalSessionCookie(t, portal, store.RoleSuperAdmin, "runtime-csrf", time.Minute)

	response := performPortalForm(portal.Handler(), "/admin/runtime-settings", url.Values{
		"csrf": {"runtime-csrf"}, "idempotency_key": {"runtime-setting-0001"}, "reason": {"测试运行时参数更新"},
		"setting": {store.RuntimeSettingDirectSTUNServers}, "value": {"stun:stun.override.example:3478"},
	}, cookie)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("runtime setting response=%d body=%s", response.Code, response.Body.String())
	}
	if repository.values[store.RuntimeSettingDirectSTUNServers] != "stun:stun.override.example:3478" {
		t.Fatalf("persisted value=%q", repository.values[store.RuntimeSettingDirectSTUNServers])
	}
	if applied != "stun:stun.override.example:3478" {
		t.Fatalf("applied value=%q", applied)
	}
}

func TestAdminUpdateRuntimeSettingRejectsUnknownSettingAndBadBool(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	base := newTestPortalRepository()
	base.account.Role = store.RoleSuperAdmin
	repository := &testRuntimeSettingRepository{testPortalRepository: base, values: map[string]string{}}
	portal.config.Repository = repository
	cookie := portalSessionCookie(t, portal, store.RoleSuperAdmin, "runtime-csrf", time.Minute)

	response := performPortalForm(portal.Handler(), "/admin/runtime-settings", url.Values{
		"csrf": {"runtime-csrf"}, "idempotency_key": {"runtime-setting-0002"}, "reason": {"测试运行时参数更新"},
		"setting": {"not_a_setting"}, "value": {"x"},
	}, cookie)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("unknown setting response=%d", response.Code)
	}

	response = performPortalForm(portal.Handler(), "/admin/runtime-settings", url.Values{
		"csrf": {"runtime-csrf"}, "idempotency_key": {"runtime-setting-0003"}, "reason": {"测试运行时参数更新"},
		"setting": {store.RuntimeSettingRegistrationRecoveryEnabled}, "value": {"maybe"},
	}, cookie)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("bad bool response=%d", response.Code)
	}
	if len(repository.values) != 0 {
		t.Fatalf("rejected requests must not persist: %v", repository.values)
	}
}

func TestAdminRuntimeSettingsRequireSuperAdmin(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	base := newTestPortalRepository()
	base.account.Role = store.RoleAdmin
	repository := &testRuntimeSettingRepository{testPortalRepository: base, values: map[string]string{}}
	portal.config.Repository = repository
	cookie := portalSessionCookie(t, portal, store.RoleAdmin, "runtime-csrf", time.Minute)

	response := performPortalForm(portal.Handler(), "/admin/runtime-settings", url.Values{
		"csrf": {"runtime-csrf"}, "idempotency_key": {"runtime-setting-0004"}, "reason": {"测试运行时参数更新"},
		"setting": {store.RuntimeSettingAccountDeletionEnabled}, "value": {"false"},
	}, cookie)
	if response.Code == http.StatusSeeOther {
		t.Fatalf("non-super-admin must not update runtime settings: %d", response.Code)
	}
	if len(repository.values) != 0 {
		t.Fatalf("rejected role must not persist: %v", repository.values)
	}
}

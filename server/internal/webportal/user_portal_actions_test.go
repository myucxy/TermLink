package webportal

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/accountdeletion"
	"github.com/termlink/termlink/server/internal/store"
)

type testUserPortalActionRepository struct {
	*testCommerceAccountRepository
	devices              []store.UserPortalDevice
	pcs                  []store.UserPortalPC
	sessions             []store.UserPortalSession
	policies             []store.UserPortalPCControlPolicy
	preflight            store.UserPortalAccountDeletionPreflight
	export               store.UserPortalDataExport
	deletion             store.UserPortalAccountDeletion
	actionErr            error
	revokeAccountID      uuid.UUID
	revokedDeviceID      uuid.UUID
	policyAccountID      uuid.UUID
	policyPCID           uuid.UUID
	policyInput          store.UserPortalPCControlPolicyInput
	historyAccountID     uuid.UUID
	historySessionID     uuid.UUID
	exportAccountID      uuid.UUID
	deletionAccountID    uuid.UUID
	confirmedRequestID   uuid.UUID
	confirmedScheduled   time.Time
	confirmedRetention   time.Time
	cancelledRequestID   uuid.UUID
	storeIdempotencyKeys []string
	auditCalls           []userPortalAuditCall
}

type userPortalAuditCall struct {
	action, targetType, result, traceID, metadata string
	accountID, targetID                           uuid.UUID
}

type testWebPrivacyExportDownloader struct {
	body      []byte
	err       error
	accountID uuid.UUID
	exportID  uuid.UUID
	calls     int
}

type testWebAccountDeletionBinder struct {
	repository *testUserPortalActionRepository
	err        error
	subject    string
	calls      int
}

func (b *testWebAccountDeletionBinder) RequestAndBind(ctx context.Context, accountID uuid.UUID, subject, key, trace string) (store.UserPortalAccountDeletion, error) {
	b.calls++
	b.subject = subject
	if b.err != nil {
		return store.UserPortalAccountDeletion{}, b.err
	}
	return b.repository.RequestUserPortalAccountDeletionState(ctx, accountID, key, trace)
}

func (d *testWebPrivacyExportDownloader) Download(_ context.Context, accountID, exportID uuid.UUID) ([]byte, error) {
	d.calls++
	d.accountID, d.exportID = accountID, exportID
	return append([]byte(nil), d.body...), d.err
}

func newTestUserPortalActionRepository(now time.Time) *testUserPortalActionRepository {
	base := newTestPortalRepository()
	base.account.Role = store.RoleUser
	commerce := &testCommerceAccountRepository{testPortalRepository: base}
	pcID := uuid.MustParse("77777777-7777-4777-8777-777777777777")
	sessionID := uuid.MustParse("88888888-8888-4888-8888-888888888888")
	return &testUserPortalActionRepository{
		testCommerceAccountRepository: commerce,
		devices: []store.UserPortalDevice{{
			ID: uuid.MustParse("66666666-6666-4666-8666-666666666666"), Type: "ANDROID", Name: "Pixel",
			TrustState: "TRUSTED", Online: true, CreatedAt: now.Add(-24 * time.Hour),
		}},
		pcs: []store.UserPortalPC{{
			ID: pcID, Name: "Workstation", Platform: "windows", Architecture: "x64",
			AgentVersion: "2.0.0", RemoteRoutingEnabled: true, Online: true,
		}},
		sessions: []store.UserPortalSession{{
			ID: sessionID, PCID: pcID, PCName: "Workstation", State: "EXITED",
			CreatedAt: now.Add(-2 * time.Hour), EndedAt: timePointer(now.Add(-time.Hour)),
		}},
		policies: []store.UserPortalPCControlPolicy{{
			PCID: pcID, Name: "Workstation", RemoteRoutingEnabled: true,
			Mode: store.ControlModeSingle, LocalPriority: true, PolicyVersion: 4,
		}},
		preflight: store.UserPortalAccountDeletionPreflight{
			AccountID: base.account.ID, Status: "ACTIVE", ActiveDevices: 1, Ready: true, RequestSupported: true,
		},
		export: store.UserPortalDataExport{
			ID: uuid.MustParse("99999999-9999-4999-8999-999999999999"), Status: "READY",
			RequestedAt: now.Add(-time.Hour), ExpiresAt: timePointer(now.Add(24 * time.Hour)),
		},
		deletion: store.UserPortalAccountDeletion{
			ID: uuid.MustParse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Status: "REQUESTED", Version: 1,
			RequestedAt: now.Add(-time.Minute), ConfirmationExpiresAt: now.Add(14 * time.Minute),
		},
	}
}

func timePointer(value time.Time) *time.Time { return &value }

func (r *testUserPortalActionRepository) ListUserPortalDevices(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalDevice, error) {
	return append([]store.UserPortalDevice(nil), r.devices...), r.actionErr
}

func (r *testUserPortalActionRepository) ListUserPortalPCs(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalPC, error) {
	return append([]store.UserPortalPC(nil), r.pcs...), r.actionErr
}

func (r *testUserPortalActionRepository) ListUserPortalSessions(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalSession, error) {
	return append([]store.UserPortalSession(nil), r.sessions...), r.actionErr
}

func (r *testUserPortalActionRepository) PreviewRedemption(context.Context, uuid.UUID, [32]byte) (store.RedemptionPreview, error) {
	return store.RedemptionPreview{}, r.actionErr
}

func (r *testUserPortalActionRepository) ListAccountRedemptions(context.Context, uuid.UUID, int) ([]store.AccountRedemption, error) {
	return nil, r.actionErr
}

func (r *testUserPortalActionRepository) ListAccountRefunds(context.Context, uuid.UUID, int) ([]store.Refund, error) {
	return nil, r.actionErr
}

func (r *testUserPortalActionRepository) RevokeUserPortalDevice(_ context.Context, accountID, deviceID uuid.UUID, key, _ string) (store.UserPortalActionResult, error) {
	r.revokeAccountID, r.revokedDeviceID = accountID, deviceID
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	return store.UserPortalActionResult{Changed: true}, r.actionErr
}

func (r *testUserPortalActionRepository) ListUserPortalPCControlPolicies(context.Context, uuid.UUID, int) ([]store.UserPortalPCControlPolicy, error) {
	return append([]store.UserPortalPCControlPolicy(nil), r.policies...), r.actionErr
}

func (r *testUserPortalActionRepository) SetUserPortalPCControlPolicy(_ context.Context, accountID, pcID uuid.UUID, input store.UserPortalPCControlPolicyInput, key, _ string) (store.UserPortalPCControlPolicy, error) {
	r.policyAccountID, r.policyPCID, r.policyInput = accountID, pcID, input
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	return store.UserPortalPCControlPolicy{PCID: pcID, PolicyVersion: input.ExpectedVersion + 1}, r.actionErr
}

func (r *testUserPortalActionRepository) RequestUserPortalHistoryDeletion(_ context.Context, accountID, sessionID uuid.UUID, key, _ string) (store.UserPortalActionResult, error) {
	r.historyAccountID, r.historySessionID = accountID, sessionID
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	return store.UserPortalActionResult{Changed: true}, r.actionErr
}

func (r *testUserPortalActionRepository) RequestUserPortalDataExport(_ context.Context, accountID uuid.UUID, key, _ string) (store.UserPortalDataExport, error) {
	r.exportAccountID = accountID
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	return r.export, r.actionErr
}

func (r *testUserPortalActionRepository) GetUserPortalDataExport(_ context.Context, accountID, exportID uuid.UUID) (store.UserPortalDataExport, error) {
	if accountID != r.account.ID || exportID != r.export.ID {
		return store.UserPortalDataExport{}, store.ErrDenied
	}
	return r.export, r.actionErr
}

func (r *testUserPortalActionRepository) GetUserPortalAccountDeletionPreflight(context.Context, uuid.UUID) (store.UserPortalAccountDeletionPreflight, error) {
	return r.preflight, r.actionErr
}

func (r *testUserPortalActionRepository) RequestUserPortalAccountDeletionState(_ context.Context, accountID uuid.UUID, key, _ string) (store.UserPortalAccountDeletion, error) {
	r.deletionAccountID = accountID
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	return r.deletion, r.actionErr
}

func (r *testUserPortalActionRepository) GetUserPortalAccountDeletion(_ context.Context, accountID, requestID uuid.UUID) (store.UserPortalAccountDeletion, error) {
	if accountID != r.account.ID || requestID != r.deletion.ID {
		return store.UserPortalAccountDeletion{}, store.ErrDenied
	}
	return r.deletion, r.actionErr
}

func (r *testUserPortalActionRepository) ConfirmUserPortalAccountDeletion(_ context.Context, accountID, requestID uuid.UUID, scheduledFor, retentionDeadline time.Time, key, _ string) (store.UserPortalAccountDeletion, error) {
	r.deletionAccountID, r.confirmedRequestID = accountID, requestID
	r.confirmedScheduled, r.confirmedRetention = scheduledFor, retentionDeadline
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	result := r.deletion
	result.Status, result.Version = "CONFIRMED", result.Version+1
	result.ScheduledFor, result.RetentionDeadline = &scheduledFor, &retentionDeadline
	return result, r.actionErr
}

func (r *testUserPortalActionRepository) CancelUserPortalAccountDeletion(_ context.Context, accountID, requestID uuid.UUID, key, _ string) (store.UserPortalAccountDeletion, error) {
	r.deletionAccountID, r.cancelledRequestID = accountID, requestID
	r.storeIdempotencyKeys = append(r.storeIdempotencyKeys, key)
	result := r.deletion
	result.Status = "CANCELED"
	return result, r.actionErr
}

func (r *testUserPortalActionRepository) Audit(_ context.Context, accountID, _ uuid.UUID, action, targetType string, targetID uuid.UUID, result, traceID, metadata string) error {
	r.auditCalls = append(r.auditCalls, userPortalAuditCall{
		action: action, targetType: targetType, result: result, traceID: traceID,
		metadata: metadata, accountID: accountID, targetID: targetID,
	})
	return nil
}

func TestUserPortalPagesRenderScopedSensitiveActionsAndPrivacyStates(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	portal.config.Repository = repository
	portal.config.DataExportDownloader = &testWebPrivacyExportDownloader{body: []byte(`{"account":{"status":"ACTIVE"}}`)}
	binder := &testWebAccountDeletionBinder{repository: repository}
	portal.config.AccountDeletionBinder = binder
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "user-page-csrf", time.Hour)

	for _, fixture := range []struct {
		target   string
		contains []string
	}{
		{"/account?section=devices", []string{"撤销设备", `type="hidden" name="reason" value="系统自动记录"`}},
		{"/account?section=pcs", []string{"单人控制（默认）", "申请接管", "协同控制", "本地输入优先", "策略 v4"}},
		{"/account?section=sessions", []string{"永久删除", `type="hidden" name="reason" value="系统自动记录"`}},
		{"/account?section=privacy&export_id=" + repository.export.ID.String() + "&deletion_id=" + repository.deletion.ID.String(), []string{"账号数据导出", "READY", "安全下载 JSON", "7 天冷静期", "再次输入“删除我的账号”"}},
	} {
		response := performRequest(portal.Handler(), http.MethodGet, fixture.target, nil, cookie)
		if response.Code != http.StatusOK {
			t.Fatalf("GET %s status=%d body=%q", fixture.target, response.Code, response.Body.String())
		}
		for _, expected := range fixture.contains {
			if !strings.Contains(response.Body.String(), expected) {
				t.Errorf("GET %s omitted %q", fixture.target, expected)
			}
		}
		for _, forbidden := range []string{"artifact_object_key", "object_key", "terminal_content", "private-key"} {
			if strings.Contains(response.Body.String(), forbidden) {
				t.Errorf("GET %s exposed %q", fixture.target, forbidden)
			}
		}
	}
}

func TestUserPortalDevicePolicyAndHistoryActionsRequireStrongOwnerContext(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	portal.config.Repository = repository
	portal.config.DataExportDownloader = &testWebPrivacyExportDownloader{body: []byte(`{"account":{"status":"ACTIVE"}}`)}
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "user-action-csrf", time.Minute)

	deviceID := repository.devices[0].ID
	revoke := performCommerceForm(portal.Handler(), "/account/devices/"+deviceID.String()+"/revoke", url.Values{
		"csrf": {"user-action-csrf"}, "reason": {"手机已经丢失"}, "idempotency_key": {"user-device-revoke-0001"},
	}, cookie)
	if revoke.Code != http.StatusSeeOther || repository.revokeAccountID != repository.account.ID || repository.revokedDeviceID != deviceID {
		t.Fatalf("revoke response=%d account=%s device=%s", revoke.Code, repository.revokeAccountID, repository.revokedDeviceID)
	}
	if len(repository.idempotencyKeys) != 0 || repository.storeIdempotencyKeys[0] != "user-device-revoke-0001" {
		t.Fatalf("web wrapper double-claimed Store idempotency: web=%v store=%v", repository.idempotencyKeys, repository.storeIdempotencyKeys)
	}
	if len(repository.auditCalls) != 1 || repository.auditCalls[0].action != "web.user.device.revoke.context" ||
		repository.auditCalls[0].accountID != repository.account.ID || repository.auditCalls[0].targetID != deviceID ||
		!strings.Contains(repository.auditCalls[0].metadata, "手机已经丢失") ||
		strings.Contains(repository.auditCalls[0].metadata, "user-action-csrf") || strings.Contains(repository.auditCalls[0].metadata, "user-device-revoke-0001") {
		t.Fatalf("unsafe success context audit=%+v", repository.auditCalls)
	}

	pcID := repository.policies[0].PCID
	policy := performCommerceForm(portal.Handler(), "/account/pcs/"+pcID.String()+"/control-policy", url.Values{
		"csrf": {"user-action-csrf"}, "reason": {"开启远程协同排障"}, "idempotency_key": {"user-pc-policy-0001"},
		"remote_routing_enabled": {"true"}, "mode": {"COLLABORATIVE"}, "local_priority": {"true"}, "expected_version": {"4"},
	}, cookie)
	if policy.Code != http.StatusSeeOther || repository.policyAccountID != repository.account.ID || repository.policyPCID != pcID ||
		repository.policyInput.Mode != store.ControlModeCollaborative || repository.policyInput.ExpectedVersion != 4 {
		t.Fatalf("policy response=%d input=%+v", policy.Code, repository.policyInput)
	}

	sessionID := repository.sessions[0].ID
	history := performCommerceForm(portal.Handler(), "/account/sessions/"+sessionID.String()+"/history/delete", url.Values{
		"csrf": {"user-action-csrf"}, "reason": {"清理已退出会话记录"}, "idempotency_key": {"user-history-delete-0001"},
	}, cookie)
	if history.Code != http.StatusSeeOther || repository.historyAccountID != repository.account.ID || repository.historySessionID != sessionID {
		t.Fatalf("history response=%d account=%s session=%s", history.Code, repository.historyAccountID, repository.historySessionID)
	}

	stale := commerceSessionCookie(t, portal, store.RoleUser, "user-action-csrf", sensitiveActionTTL+time.Second)
	denied := performCommerceForm(portal.Handler(), "/account/devices/"+deviceID.String()+"/revoke", url.Values{
		"csrf": {"user-action-csrf"}, "reason": {"再次撤销设备"}, "idempotency_key": {"user-device-revoke-0002"},
	}, stale)
	if denied.Code != http.StatusForbidden || len(repository.storeIdempotencyKeys) != 3 {
		t.Fatalf("stale action status=%d store calls=%v", denied.Code, repository.storeIdempotencyKeys)
	}
}

func TestUserPortalReadyExportDownloadsThroughAccountScopedAuditedProxy(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	downloader := &testWebPrivacyExportDownloader{body: []byte(`{"account":{"status":"ACTIVE"},"devices":[]}`)}
	portal.config.Repository = repository
	portal.config.DataExportDownloader = downloader
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "download-csrf", time.Minute)

	response := performCommerceForm(portal.Handler(), "/account/privacy/exports/"+repository.export.ID.String()+"/download", url.Values{
		"csrf": {"download-csrf"}, "reason": {"下载我的账号数据备份"}, "idempotency_key": {"privacy-export-download-0001"},
	}, cookie)
	if response.Code != http.StatusOK || response.Body.String() != string(downloader.body) {
		t.Fatalf("download status/body=%d/%q", response.Code, response.Body.String())
	}
	if downloader.calls != 1 || downloader.accountID != repository.account.ID || downloader.exportID != repository.export.ID {
		t.Fatalf("downloader calls/account/export=%d/%s/%s", downloader.calls, downloader.accountID, downloader.exportID)
	}
	for name, want := range map[string]string{
		"Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store",
		"X-Content-Type-Options": "nosniff", "Content-Disposition": `attachment; filename="termlink-data-export.json"`,
	} {
		if got := response.Header().Get(name); got != want {
			t.Errorf("%s=%q want %q", name, got, want)
		}
	}
	if len(repository.auditCalls) == 0 || repository.auditCalls[len(repository.auditCalls)-1].action != "web.user.data_export.download" ||
		strings.Contains(repository.auditCalls[len(repository.auditCalls)-1].metadata, "privacy-export-download-0001") {
		t.Fatalf("download audit=%+v", repository.auditCalls)
	}
	if repository.idempotencyDone["privacy-export-download-0001"] != http.StatusOK {
		t.Fatalf("download idempotency completion=%v", repository.idempotencyDone)
	}
}

func TestUserPortalExportRequestWithoutDownloaderDoesNotCreateTask(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "privacy-csrf", time.Minute)
	response := performCommerceForm(portal.Handler(), "/account/privacy/exports", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"导出我的账号元数据"}, "idempotency_key": {"privacy-export-unavailable"},
	}, cookie)
	if response.Code != http.StatusServiceUnavailable || repository.exportAccountID != uuid.Nil || len(repository.storeIdempotencyKeys) != 0 {
		t.Fatalf("status/account/store keys=%d/%s/%v", response.Code, repository.exportAccountID, repository.storeIdempotencyKeys)
	}
}

func TestUserPortalPrivacyWorkflowKeepsPendingAndUsesServerCoolingPolicy(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	portal.config.Repository = repository
	portal.config.DataExportDownloader = &testWebPrivacyExportDownloader{body: []byte(`{"account":{"status":"ACTIVE"}}`)}
	binder := &testWebAccountDeletionBinder{repository: repository}
	portal.config.AccountDeletionBinder = binder
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "privacy-csrf", time.Minute)

	exportResponse := performCommerceForm(portal.Handler(), "/account/privacy/exports", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"导出我的账号元数据"}, "idempotency_key": {"privacy-export-0001"},
	}, cookie)
	if exportResponse.Code != http.StatusSeeOther || !strings.Contains(exportResponse.Header().Get("Location"), "export_id="+repository.export.ID.String()) || repository.exportAccountID != repository.account.ID {
		t.Fatalf("export response=%d location=%q account=%s", exportResponse.Code, exportResponse.Header().Get("Location"), repository.exportAccountID)
	}

	wrongConfirmation := performCommerceForm(portal.Handler(), "/account/privacy/deletions", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"准备注销账号"}, "idempotency_key": {"privacy-delete-incorrect"}, "confirmation": {"删除"},
	}, cookie)
	if wrongConfirmation.Code != http.StatusBadRequest || repository.deletionAccountID != uuid.Nil {
		t.Fatalf("wrong confirmation status=%d account=%s", wrongConfirmation.Code, repository.deletionAccountID)
	}

	requestResponse := performCommerceForm(portal.Handler(), "/account/privacy/deletions", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"不再使用此服务"}, "idempotency_key": {"privacy-delete-request-0001"}, "confirmation": {userPortalDeletionConfirmation},
	}, cookie)
	if requestResponse.Code != http.StatusSeeOther || repository.deletionAccountID != repository.account.ID || !strings.Contains(requestResponse.Header().Get("Location"), "deletion_id="+repository.deletion.ID.String()) {
		t.Fatalf("deletion request response=%d account=%s location=%q", requestResponse.Code, repository.deletionAccountID, requestResponse.Header().Get("Location"))
	}
	if binder.calls != 1 || binder.subject != "commerce-subject" {
		t.Fatalf("web deletion binder calls/subject=%d/%q", binder.calls, binder.subject)
	}

	confirmResponse := performCommerceForm(portal.Handler(), "/account/privacy/deletions/"+repository.deletion.ID.String()+"/confirm", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"确认进入冷静期"}, "idempotency_key": {"privacy-delete-confirm-0001"}, "confirmation": {userPortalDeletionConfirmation},
	}, cookie)
	wantScheduled := repository.deletion.RequestedAt.UTC().Add(userPortalDeletionCoolingPeriod)
	if confirmResponse.Code != http.StatusSeeOther || repository.confirmedRequestID != repository.deletion.ID || !repository.confirmedScheduled.Equal(wantScheduled) ||
		!repository.confirmedRetention.Equal(wantScheduled.Add(userPortalDeletionRetention)) {
		t.Fatalf("confirm response=%d scheduled=%s retention=%s", confirmResponse.Code, repository.confirmedScheduled, repository.confirmedRetention)
	}

	cancelResponse := performCommerceForm(portal.Handler(), "/account/privacy/deletions/"+repository.deletion.ID.String()+"/cancel", url.Values{
		"csrf": {"privacy-csrf"}, "reason": {"决定保留账号"}, "idempotency_key": {"privacy-delete-cancel-0001"},
	}, cookie)
	if cancelResponse.Code != http.StatusSeeOther || repository.cancelledRequestID != repository.deletion.ID {
		t.Fatalf("cancel response=%d request=%s", cancelResponse.Code, repository.cancelledRequestID)
	}
	if strings.Contains(cancelResponse.Header().Get("Location"), "completed") {
		t.Fatal("cancel response falsely claimed account deletion completion")
	}
}

func TestWebAccountDeletionRequestFailsClosedWithoutIdentityBinding(t *testing.T) {
	t.Parallel()
	for name, binder := range map[string]accountdeletion.RequestBinder{
		"missing": nil,
		"binding failure": &testWebAccountDeletionBinder{
			err: errors.New("raw keycloak subject and ciphertext must-not-leak"),
		},
	} {
		binder := binder
		t.Run(name, func(t *testing.T) {
			portal := newTestPortal(t, &testOIDCFlow{})
			repository := newTestUserPortalActionRepository(portal.now())
			portal.config.Repository = repository
			portal.config.AccountDeletionBinder = binder
			cookie := commerceSessionCookie(t, portal, store.RoleUser, "privacy-csrf", time.Minute)
			response := performCommerceForm(portal.Handler(), "/account/privacy/deletions", url.Values{
				"csrf": {"privacy-csrf"}, "reason": {"不再使用此服务"},
				"idempotency_key": {"privacy-delete-request-0001"},
				"confirmation":    {userPortalDeletionConfirmation},
			}, cookie)
			body := strings.ToLower(response.Body.String())
			if response.Code != http.StatusServiceUnavailable || repository.deletionAccountID != uuid.Nil {
				t.Fatalf("status/account=%d/%s body=%s", response.Code, repository.deletionAccountID, response.Body.String())
			}
			for _, forbidden := range []string{"keycloak", "subject", "ciphertext", "must-not-leak"} {
				if strings.Contains(body, forbidden) {
					t.Fatalf("binding failure leaked %q: %s", forbidden, response.Body.String())
				}
			}
		})
	}
}

func TestEveryStrongUserPortalFormCarriesRequiredProtection(t *testing.T) {
	contents, err := portalAssets.ReadFile("assets/portal-dashboard.html")
	if err != nil {
		t.Fatal(err)
	}
	pattern := regexp.MustCompile(`(?s)<form[^>]+action="/account/(?:devices|pcs|sessions|privacy)[^"]*"[^>]*>.*?</form>`)
	forms := pattern.FindAllString(string(contents), -1)
	if len(forms) < 7 {
		t.Fatalf("found %d strong user forms, want at least 7", len(forms))
	}
	for _, form := range forms {
		for _, field := range []string{`name="csrf"`, `name="reason"`, `name="idempotency_key"`} {
			if !strings.Contains(form, field) {
				t.Errorf("strong user form omitted %s: %.240s", field, form)
			}
		}
	}
}

func TestUserPortalActionErrorsRemainNonOracular(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestUserPortalActionRepository(portal.now())
	repository.actionErr = store.ErrDenied
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "oracle-csrf", time.Minute)
	response := performCommerceForm(portal.Handler(), "/account/devices/"+repository.devices[0].ID.String()+"/revoke", url.Values{
		"csrf": {"oracle-csrf"}, "reason": {"撤销未知设备"}, "idempotency_key": {"oracle-device-0001"},
	}, cookie)
	if response.Code != http.StatusNotFound || strings.Contains(response.Body.String(), repository.devices[0].ID.String()) || !strings.Contains(response.Body.String(), "不存在、已变化或不属于当前账号") {
		t.Fatalf("oracle response=%d body=%q", response.Code, response.Body.String())
	}
	repository.actionErr = store.ErrSessionActive
	response = performCommerceForm(portal.Handler(), "/account/sessions/"+repository.sessions[0].ID.String()+"/history/delete", url.Values{
		"csrf": {"oracle-csrf"}, "reason": {"尝试删除活动会话"}, "idempotency_key": {"oracle-history-0001"},
	}, cookie)
	if response.Code != http.StatusConflict || !strings.Contains(response.Body.String(), "会话仍在运行") {
		t.Fatalf("active session response=%d body=%q", response.Code, response.Body.String())
	}
}

var _ userPortalActionRepository = (*testUserPortalActionRepository)(nil)

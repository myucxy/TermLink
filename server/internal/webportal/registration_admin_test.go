package webportal

import (
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type testRegistrationAdminRepository struct {
	*testPortalRepository
	policy       store.RegistrationPolicy
	batches      []store.InvitationBatch
	codes        []store.InvitationCode
	uses         []store.InvitationRedemption
	addedBatchID uuid.UUID
	addedCodes   []store.CodeDigestRecord
}

func (r *testRegistrationAdminRepository) GetRegistrationPolicy(context.Context) (store.RegistrationPolicy, error) {
	return r.policy, nil
}

func (r *testRegistrationAdminRepository) ConfirmPendingInvitationForEmail(context.Context, uuid.UUID, [32]byte) error {
	return store.ErrNotFound
}

func (r *testRegistrationAdminRepository) UpdateRegistrationPolicy(_ context.Context, _ uuid.UUID, mode string, version int64, _ string) (store.RegistrationPolicy, error) {
	r.policy.Mode, r.policy.Version = mode, version+1
	return r.policy, nil
}

func (r *testRegistrationAdminRepository) CreateInvitationBatch(_ context.Context, actorID uuid.UUID, input store.InvitationBatchInput, _ string) (store.InvitationBatch, error) {
	batch := store.InvitationBatch{ID: uuid.New(), Name: input.Name, Status: input.Status, DefaultMaxUses: input.DefaultMaxUses, EmailDomain: input.EmailDomain, ValidUntil: input.ValidUntil, Note: input.Note, CreatedBy: actorID}
	r.batches = append(r.batches, batch)
	return batch, nil
}

func (r *testRegistrationAdminRepository) AddInvitationCodes(_ context.Context, _ uuid.UUID, batchID uuid.UUID, codes []store.CodeDigestRecord, _ string) ([]store.InvitationCode, error) {
	r.addedBatchID = batchID
	r.addedCodes = append([]store.CodeDigestRecord(nil), codes...)
	return nil, nil
}

func (r *testRegistrationAdminRepository) ListInvitationBatches(context.Context, uuid.UUID, int) ([]store.InvitationBatch, error) {
	return append([]store.InvitationBatch(nil), r.batches...), nil
}

func (r *testRegistrationAdminRepository) ListInvitationCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.InvitationCode, error) {
	return append([]store.InvitationCode(nil), r.codes...), nil
}

func (r *testRegistrationAdminRepository) ListInvitationRedemptions(context.Context, uuid.UUID, int) ([]store.InvitationRedemption, error) {
	return append([]store.InvitationRedemption(nil), r.uses...), nil
}

func (r *testRegistrationAdminRepository) SetInvitationBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

func (r *testRegistrationAdminRepository) SetInvitationCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

func portalSessionCookie(t *testing.T, portal *Portal, role store.Role, csrf string, age time.Duration) *http.Cookie {
	t.Helper()
	value, err := portal.sessionCodec.seal(webSession{
		Subject: "portal-subject", Username: "portal-user", Nickname: "Portal User", Role: string(role), CSRF: csrf,
		AuthenticatedAtMS: portal.now().Add(-age).UnixMilli(), MFA: true,
		SensitiveVerifiedAtMS: portal.now().Add(-age).UnixMilli(), SensitiveMethod: sensitiveMethodMFA,
		ExpiresAtMS: portal.now().Add(time.Hour).UnixMilli(),
	})
	if err != nil {
		t.Fatal(err)
	}
	return &http.Cookie{Name: sessionCookieName, Value: value, Path: "/"}
}

func performPortalForm(handler http.Handler, target string, values url.Values, cookies ...*http.Cookie) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+target, strings.NewReader(values.Encode()))
	request.Host = testPublicHost
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Origin", "https://"+testPublicHost)
	for _, cookie := range cookies {
		request.AddCookie(cookie)
	}
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, request)
	return recorder
}

func TestRegistrationAdminGeneratesOneTimeInvitationReceipt(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	base := newTestPortalRepository()
	base.account.Role = store.RoleSuperAdmin
	batchID := uuid.MustParse("77777777-7777-4777-8777-777777777777")
	repository := &testRegistrationAdminRepository{
		testPortalRepository: base,
		policy:               store.RegistrationPolicy{Mode: store.RegistrationInviteRequired, Version: 2},
		batches:              []store.InvitationBatch{{ID: batchID, Name: "test", Status: "ACTIVE", DefaultMaxUses: 1}},
	}
	portal.config.Repository = repository
	portal.config.RegistrationCodeKey = []byte("0123456789abcdef0123456789abcdef")
	cookie := portalSessionCookie(t, portal, store.RoleSuperAdmin, "registration-csrf", time.Minute)
	response := performPortalForm(portal.Handler(), "/admin/registration/invitations/batches/"+batchID.String()+"/codes", url.Values{
		"csrf": {"registration-csrf"}, "idempotency_key": {"registration-generate-0001"},
		"reason": {"生成测试邀请码"}, "count": {"2"}, "max_uses": {"1"},
	}, cookie)
	if response.Code != http.StatusCreated || len(repository.addedCodes) != 2 || repository.addedBatchID != batchID {
		t.Fatalf("generate response=%d codes=%d batch=%s body=%s", response.Code, len(repository.addedCodes), repository.addedBatchID, response.Body.String())
	}
	if strings.Count(response.Body.String(), "TLI-") != 2 || !strings.Contains(response.Body.String(), "仅在本页完整显示一次") {
		t.Fatalf("one-time invitation receipt is incomplete: %s", response.Body.String())
	}
}

func TestRegistrationAdminUsesCurrentCustomAdminRoute(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	base := newTestPortalRepository()
	base.account.Role = store.RoleSuperAdmin
	batchID := uuid.MustParse("88888888-8888-4888-8888-888888888888")
	repository := &testRegistrationAdminRepository{
		testPortalRepository: base,
		policy:               store.RegistrationPolicy{Mode: store.RegistrationInviteRequired, Version: 2},
		batches:              []store.InvitationBatch{{ID: batchID, Name: "custom-route", Status: "ACTIVE", DefaultMaxUses: 1}},
	}
	portal.config.Repository = repository
	portal.config.RegistrationCodeKey = []byte("0123456789abcdef0123456789abcdef")
	const segment = "4hPzK8mQvX2sN7aB"
	enableTestAdminRoute(t, portal, base, segment)
	cookie := portalSessionCookie(t, portal, store.RoleSuperAdmin, "registration-route-csrf", time.Minute)
	basePath := "/control/" + segment

	overview := performRequest(portal.Handler(), http.MethodGet, basePath, nil, cookie)
	if overview.Code != http.StatusOK || !strings.Contains(overview.Body.String(), `href="`+basePath+`/registration"`) {
		t.Fatalf("custom management navigation status/body = %d/%q", overview.Code, overview.Body.String())
	}
	registration := performRequest(portal.Handler(), http.MethodGet, basePath+"/registration", nil, cookie)
	if registration.Code != http.StatusOK {
		t.Fatalf("custom registration page status/body = %d/%q", registration.Code, registration.Body.String())
	}
	for _, expected := range []string{
		`href="` + basePath + `?section=overview"`,
		`action="` + basePath + `/registration/policy"`,
		`action="` + basePath + `/registration/invitations/batches"`,
	} {
		if !strings.Contains(registration.Body.String(), expected) {
			t.Fatalf("custom registration page omitted %q", expected)
		}
	}
	if strings.Contains(registration.Body.String(), `href="/admin`) || strings.Contains(registration.Body.String(), `action="/admin`) {
		t.Fatal("custom registration page leaked the disabled default management route")
	}
	receipt := performPortalForm(portal.Handler(), basePath+"/registration/invitations/batches/"+batchID.String()+"/codes", url.Values{
		"csrf": {"registration-route-csrf"}, "idempotency_key": {"custom-route-generate-0001"},
		"reason": {"验证自定义入口"}, "count": {"1"}, "max_uses": {"1"},
	}, cookie)
	if receipt.Code != http.StatusCreated || !strings.Contains(receipt.Body.String(), `href="`+basePath+`/registration?id=`+batchID.String()+`"`) {
		t.Fatalf("custom registration receipt status/body = %d/%q", receipt.Code, receipt.Body.String())
	}
}

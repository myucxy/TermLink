package api

import (
	"context"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/accountdeletion"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type fakeUserPortalActionStore struct {
	called      string
	accountID   uuid.UUID
	targetID    uuid.UUID
	idempotency string
	traceID     string
	limit       int
	enabled     bool
	policyInput store.UserPortalPCControlPolicyInput
	err         error
	now         time.Time
}

type userPortalIdentityVerifier struct{ identity auth.Identity }

func (v userPortalIdentityVerifier) Verify(context.Context, string) (auth.Identity, error) {
	return v.identity, nil
}

type fakeAPIAccountDeletionBinder struct {
	repository *fakeUserPortalActionStore
	err        error
	subject    string
	calls      int
}

func (b *fakeAPIAccountDeletionBinder) RequestAndBind(ctx context.Context, accountID uuid.UUID, subject, key, trace string) (store.UserPortalAccountDeletion, error) {
	b.calls++
	b.subject = subject
	if b.err != nil {
		return store.UserPortalAccountDeletion{}, b.err
	}
	return b.repository.RequestUserPortalAccountDeletionState(ctx, accountID, key, trace)
}

func (f *fakeUserPortalActionStore) record(call string, accountID, targetID uuid.UUID, key, trace string) {
	f.called, f.accountID, f.targetID, f.idempotency, f.traceID = call, accountID, targetID, key, trace
}

func (f *fakeUserPortalActionStore) RevokeUserPortalDevice(_ context.Context, accountID, deviceID uuid.UUID, key, trace string) (store.UserPortalActionResult, error) {
	f.record("revoke-device", accountID, deviceID, key, trace)
	return store.UserPortalActionResult{Changed: true}, f.err
}

func (f *fakeUserPortalActionStore) ListUserPortalPCControlPolicies(_ context.Context, accountID uuid.UUID, limit int) ([]store.UserPortalPCControlPolicy, error) {
	f.record("list-policies", accountID, uuid.Nil, "", "")
	f.limit = limit
	return []store.UserPortalPCControlPolicy{{
		PCID: uuid.MustParse("10000000-0000-0000-0000-000000000001"), Name: "Office PC",
		RemoteRoutingEnabled: true, Mode: store.ControlModeSingle, LocalPriority: true, PolicyVersion: 4,
	}}, f.err
}

func (f *fakeUserPortalActionStore) SetUserPortalPCRemoteRouting(_ context.Context, accountID, pcID uuid.UUID, enabled bool, key, trace string) (store.UserPortalPCControlPolicy, error) {
	f.record("set-routing", accountID, pcID, key, trace)
	f.enabled = enabled
	return f.policy(pcID), f.err
}

func (f *fakeUserPortalActionStore) SetUserPortalPCControlPolicy(_ context.Context, accountID, pcID uuid.UUID, input store.UserPortalPCControlPolicyInput, key, trace string) (store.UserPortalPCControlPolicy, error) {
	f.record("set-policy", accountID, pcID, key, trace)
	f.policyInput = input
	return f.policy(pcID), f.err
}

func (f *fakeUserPortalActionStore) RequestUserPortalHistoryDeletion(_ context.Context, accountID, sessionID uuid.UUID, key, trace string) (store.UserPortalActionResult, error) {
	f.record("delete-history", accountID, sessionID, key, trace)
	return store.UserPortalActionResult{Changed: true}, f.err
}

func (f *fakeUserPortalActionStore) RequestUserPortalDataExport(_ context.Context, accountID uuid.UUID, key, trace string) (store.UserPortalDataExport, error) {
	f.record("request-export", accountID, uuid.Nil, key, trace)
	return f.dataExport(uuid.MustParse("20000000-0000-0000-0000-000000000002")), f.err
}

func (f *fakeUserPortalActionStore) GetUserPortalDataExport(_ context.Context, accountID, exportID uuid.UUID) (store.UserPortalDataExport, error) {
	f.record("get-export", accountID, exportID, "", "")
	return f.dataExport(exportID), f.err
}

func (f *fakeUserPortalActionStore) GetUserPortalAccountDeletionPreflight(_ context.Context, accountID uuid.UUID) (store.UserPortalAccountDeletionPreflight, error) {
	f.record("deletion-preflight", accountID, uuid.Nil, "", "")
	return store.UserPortalAccountDeletionPreflight{
		AccountID: accountID, Status: "ACTIVE", ActiveDevices: 2, Ready: true, RequestSupported: true,
	}, f.err
}

func (f *fakeUserPortalActionStore) RequestUserPortalAccountDeletionState(_ context.Context, accountID uuid.UUID, key, trace string) (store.UserPortalAccountDeletion, error) {
	f.record("request-deletion", accountID, uuid.Nil, key, trace)
	return f.deletion(uuid.MustParse("30000000-0000-0000-0000-000000000003")), f.err
}

func (f *fakeUserPortalActionStore) GetUserPortalAccountDeletion(_ context.Context, accountID, requestID uuid.UUID) (store.UserPortalAccountDeletion, error) {
	f.record("get-deletion", accountID, requestID, "", "")
	return f.deletion(requestID), f.err
}

func (f *fakeUserPortalActionStore) CancelUserPortalAccountDeletion(_ context.Context, accountID, requestID uuid.UUID, key, trace string) (store.UserPortalAccountDeletion, error) {
	f.record("cancel-deletion", accountID, requestID, key, trace)
	result := f.deletion(requestID)
	result.Status = "CANCELED"
	result.CanceledAt = &f.now
	return result, f.err
}

func (f *fakeUserPortalActionStore) policy(pcID uuid.UUID) store.UserPortalPCControlPolicy {
	return store.UserPortalPCControlPolicy{
		PCID: pcID, Name: "Office PC", RemoteRoutingEnabled: true,
		Mode: store.ControlModeCollaborative, LocalPriority: true, PolicyVersion: 5,
	}
}

func (f *fakeUserPortalActionStore) dataExport(exportID uuid.UUID) store.UserPortalDataExport {
	expires := f.now.Add(time.Hour)
	return store.UserPortalDataExport{ID: exportID, Status: "REQUESTED", RequestedAt: f.now, ExpiresAt: &expires}
}

func (f *fakeUserPortalActionStore) deletion(requestID uuid.UUID) store.UserPortalAccountDeletion {
	return store.UserPortalAccountDeletion{
		ID: requestID, Status: "REQUESTED", Version: 1, RequestedAt: f.now,
		ConfirmationExpiresAt: f.now.Add(15 * time.Minute),
	}
}

func TestUserPortalActionHandlersBindEveryStoreCallToAuthenticatedAccount(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID := uuid.New(), uuid.New()
	targetID := uuid.MustParse("40000000-0000-0000-0000-000000000004")
	tests := []struct {
		name, pattern, method, target, body, wantCall string
		status                                        int
		sensitive                                     bool
		serve                                         func(userPortalActionHandlers, http.ResponseWriter, *http.Request)
	}{
		{"revoke device", "POST /v1/account/devices/{deviceId}/revoke", http.MethodPost, "/v1/account/devices/" + targetID.String() + "/revoke", `{}`, "revoke-device", 200, true, userPortalActionHandlers.revokeDevice},
		{"list control policies", "GET /v1/account/pcs/control-policies", http.MethodGet, "/v1/account/pcs/control-policies?limit=25", "", "list-policies", 200, false, userPortalActionHandlers.listPCControlPolicies},
		{"set remote routing", "PUT /v1/account/pcs/{pcId}/remote-routing", http.MethodPut, "/v1/account/pcs/" + targetID.String() + "/remote-routing", `{"enabled":true}`, "set-routing", 200, true, userPortalActionHandlers.setPCRemoteRouting},
		{"set control policy", "PUT /v1/account/pcs/{pcId}/control-policy", http.MethodPut, "/v1/account/pcs/" + targetID.String() + "/control-policy", `{"remote_routing_enabled":true,"mode":"COLLABORATIVE","local_priority":true,"expected_version":4}`, "set-policy", 200, true, userPortalActionHandlers.setPCControlPolicy},
		{"delete history", "DELETE /v1/account/sessions/{sessionId}/history", http.MethodDelete, "/v1/account/sessions/" + targetID.String() + "/history", `{}`, "delete-history", 200, true, userPortalActionHandlers.requestHistoryDeletion},
		{"request export", "POST /v1/account/data-exports", http.MethodPost, "/v1/account/data-exports", `{}`, "request-export", 202, true, userPortalActionHandlers.requestDataExport},
		{"get export", "GET /v1/account/data-exports/{exportId}", http.MethodGet, "/v1/account/data-exports/" + targetID.String(), "", "get-export", 200, false, userPortalActionHandlers.getDataExport},
		{"deletion preflight", "GET /v1/account/deletion-preflight", http.MethodGet, "/v1/account/deletion-preflight", "", "deletion-preflight", 200, false, userPortalActionHandlers.getAccountDeletionPreflight},
		{"request deletion", "POST /v1/account/deletion-requests", http.MethodPost, "/v1/account/deletion-requests", `{}`, "request-deletion", 202, true, userPortalActionHandlers.requestAccountDeletion},
		{"get deletion", "GET /v1/account/deletion-requests/{requestId}", http.MethodGet, "/v1/account/deletion-requests/" + targetID.String(), "", "get-deletion", 200, false, userPortalActionHandlers.getAccountDeletion},
		{"cancel deletion", "POST /v1/account/deletion-requests/{requestId}/cancel", http.MethodPost, "/v1/account/deletion-requests/" + targetID.String() + "/cancel", `{}`, "cancel-deletion", 200, true, userPortalActionHandlers.cancelAccountDeletion},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &fakeUserPortalActionStore{now: now}
			binder := &fakeAPIAccountDeletionBinder{repository: repository}
			security := &memoryCommerceSecurityStore{}
			service := &API{
				Clock: func() time.Time { return now }, commerceSecurity: security,
				CommerceKey:           []byte("portal-audit-key-0123456789abcdef"),
				DataExportDownloader:  &fakePrivacyExportDownloader{body: []byte(`{"status":"ready"}`)},
				AccountDeletionBinder: binder,
			}
			actions := userPortalActionHandlers{api: service, repository: repository, binder: binder}
			endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now, test.pattern, auth.Identity{Subject: "30000000-0000-4000-8000-000000000003", MFA: true, AuthenticatedAt: now}, func(w http.ResponseWriter, r *http.Request) {
				test.serve(actions, w, r)
			})
			request := endpoint.request(t, deviceID, now, test.method, test.target, test.body, test.sensitive, true)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != test.status {
				t.Fatalf("status/body = %d/%s, want %d", response.Code, response.Body.String(), test.status)
			}
			if repository.called != test.wantCall || repository.accountID != accountID {
				t.Fatalf("store call/account = %q/%s, want %q/%s", repository.called, repository.accountID, test.wantCall, accountID)
			}
			if test.wantCall == "request-deletion" && (binder.calls != 1 || binder.subject != "30000000-0000-4000-8000-000000000003") {
				t.Fatalf("deletion binder calls/subject=%d/%q", binder.calls, binder.subject)
			}
			if test.sensitive {
				if repository.idempotency != "portal-idempotency-key" || repository.traceID == "" {
					t.Fatalf("idempotency/trace not forwarded safely: %q/%q", repository.idempotency, repository.traceID)
				}
				if len(security.audits) != 1 || strings.Contains(response.Body.String(), "portal-idempotency-key") || strings.Contains(response.Body.String(), "Portal owner action") {
					t.Fatalf("sensitive audit/response = %d/%s", len(security.audits), response.Body.String())
				}
			}
		})
	}
}

func TestAccountDeletionRequestFailsClosedWithoutConfiguredBinder(t *testing.T) {
	t.Parallel()
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID := uuid.New(), uuid.New()
	for name, binder := range map[string]accountdeletion.RequestBinder{
		"missing": nil,
		"binding failure": &fakeAPIAccountDeletionBinder{
			err: errors.New("raw subject and ciphertext must-not-leak"),
		},
	} {
		binder := binder
		t.Run(name, func(t *testing.T) {
			repository := &fakeUserPortalActionStore{now: now}
			service := &API{
				Clock: func() time.Time { return now }, commerceSecurity: &memoryCommerceSecurityStore{},
				CommerceKey: []byte("portal-audit-key-0123456789abcdef"), AccountDeletionBinder: binder,
			}
			actions := userPortalActionHandlers{api: service, repository: repository, binder: binder}
			endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
				"POST /v1/account/deletion-requests",
				auth.Identity{Subject: uuid.NewString(), MFA: true, AuthenticatedAt: now}, actions.requestAccountDeletion)
			request := endpoint.request(t, deviceID, now, http.MethodPost,
				"/v1/account/deletion-requests", `{}`, true, true)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			body := strings.ToLower(response.Body.String())
			if response.Code != http.StatusServiceUnavailable || !strings.Contains(body, `"code":"account_deletion_unavailable"`) {
				t.Fatalf("status/body=%d/%s", response.Code, response.Body.String())
			}
			if repository.called != "" || strings.Contains(body, "subject") || strings.Contains(body, "cipher") || strings.Contains(body, "must-not-leak") {
				t.Fatalf("failed-closed request reached store or leaked detail: call=%q body=%s", repository.called, response.Body.String())
			}
		})
	}
}

func TestUserPortalSensitiveActionsRequireKeyRecentMFAAndReason(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	tests := []struct {
		name               string
		identity           auth.Identity
		includeKey, reason bool
		wantStatus         int
		wantCode           string
	}{
		{"idempotency key required", auth.Identity{MFA: true, AuthenticatedAt: now}, false, true, 400, "IDEMPOTENCY_KEY_REQUIRED"},
		{"MFA required", auth.Identity{AuthenticatedAt: now}, true, true, 403, "STEP_UP_REQUIRED"},
		{"recent authentication required", auth.Identity{MFA: true, AuthenticatedAt: now.Add(-commerceStepUpMaximumAge - time.Second)}, true, true, 403, "STEP_UP_REQUIRED"},
		{"operation reason required", auth.Identity{MFA: true, AuthenticatedAt: now}, true, false, 400, "OPERATION_REASON_REQUIRED"},
		{"complete high risk context accepted", auth.Identity{MFA: true, AuthenticatedAt: now}, true, true, 200, ""},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			repository := &fakeUserPortalActionStore{now: now}
			service := &API{Clock: func() time.Time { return now }, commerceSecurity: &memoryCommerceSecurityStore{}}
			actions := userPortalActionHandlers{api: service, repository: repository}
			endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
				"PUT /v1/account/pcs/{pcId}/remote-routing", test.identity, actions.setPCRemoteRouting)
			request := endpoint.request(t, deviceID, now, http.MethodPut,
				"/v1/account/pcs/"+pcID.String()+"/remote-routing", `{"enabled":true}`, test.includeKey, test.reason)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || (test.wantCode != "" && !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`)) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
			if test.wantStatus == http.StatusOK && repository.called != "set-routing" {
				t.Fatal("validated action did not reach store")
			}
			if test.wantStatus != http.StatusOK && repository.called != "" {
				t.Fatalf("rejected action reached store: %q", repository.called)
			}
		})
	}
}

func TestUserPortalResourceErrorsAreNonOracular(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, exportID := uuid.New(), uuid.New(), uuid.New()
	trace := uuid.NewString()
	responses := make([]*httptest.ResponseRecorder, 0, 2)
	for _, repositoryError := range []error{store.ErrDenied, store.ErrNotFound} {
		repository := &fakeUserPortalActionStore{now: now, err: repositoryError}
		service := &API{Clock: func() time.Time { return now }}
		actions := userPortalActionHandlers{api: service, repository: repository}
		endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
			"GET /v1/account/data-exports/{exportId}", auth.Identity{MFA: true, AuthenticatedAt: now}, actions.getDataExport)
		request := endpoint.request(t, deviceID, now, http.MethodGet,
			"/v1/account/data-exports/"+exportID.String(), "", false, false)
		request.Header.Set("X-Trace-ID", trace)
		response := httptest.NewRecorder()
		endpoint.handler.ServeHTTP(response, request)
		responses = append(responses, response)
	}
	if responses[0].Code != http.StatusNotFound || responses[1].Code != http.StatusNotFound || responses[0].Body.String() != responses[1].Body.String() {
		t.Fatalf("denied/not-found responses differ: %d %s / %d %s", responses[0].Code, responses[0].Body.String(), responses[1].Code, responses[1].Body.String())
	}
	if strings.Contains(responses[0].Body.String(), exportID.String()) {
		t.Fatal("resource error reflected a caller-supplied identifier")
	}
}

func TestUserPortalResponsesAreExplicitMetadataOnlyViews(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID := uuid.New()
	preflight := userPortalAccountDeletionPreflightView(store.UserPortalAccountDeletionPreflight{
		AccountID: accountID, Status: "ACTIVE", Ready: true, RequestSupported: true,
	})
	if _, exposed := preflight["account_id"]; exposed {
		t.Fatal("preflight response redundantly exposed the authenticated account ID")
	}
	export := userPortalDataExportView(store.UserPortalDataExport{ID: uuid.New(), Status: "READY", RequestedAt: now})
	encoded, err := json.Marshal(map[string]any{"preflight": preflight, "export": export})
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"object_key", "artifact", "ciphertext", "terminal", "secret", "operation_reason", "idempotency"} {
		if strings.Contains(strings.ToLower(string(encoded)), forbidden) {
			t.Fatalf("metadata view contains forbidden field %q: %s", forbidden, encoded)
		}
	}
}

type userPortalTestEndpoint struct {
	handler    http.Handler
	privateKey ed25519.PrivateKey
}

func userPortalAuthenticatedEndpoint(t *testing.T, accountID, deviceID uuid.UUID, now time.Time, pattern string, identity auth.Identity, handler http.HandlerFunc) userPortalTestEndpoint {
	t.Helper()
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	authentication.Tokens = userPortalIdentityVerifier{identity: identity}
	mux := http.NewServeMux()
	mux.HandleFunc(pattern, handler)
	return userPortalTestEndpoint{handler: traceMiddleware(authentication.Authenticate(mux)), privateKey: privateKey}
}

func (e userPortalTestEndpoint) request(t *testing.T, deviceID uuid.UUID, now time.Time, method, target, body string, includeKey, includeReason bool) *http.Request {
	t.Helper()
	request := signedFileOperationRequest(t, e.privateKey, deviceID, now, method, target, []byte(body))
	if includeKey {
		request.Header.Set("Idempotency-Key", "portal-idempotency-key")
	}
	if includeReason {
		request.Header.Set(commerceOperationReasonHeader, "Portal owner action")
	}
	return request
}

func TestUserPortalRejectsAccountAndSecretInjection(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, pcID := uuid.New(), uuid.New(), uuid.New()
	repository := &fakeUserPortalActionStore{now: now}
	service := &API{Clock: func() time.Time { return now }, commerceSecurity: &memoryCommerceSecurityStore{}}
	actions := userPortalActionHandlers{api: service, repository: repository}
	endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
		"PUT /v1/account/pcs/{pcId}/remote-routing", auth.Identity{MFA: true, AuthenticatedAt: now}, actions.setPCRemoteRouting)
	body := `{"enabled":true,"account_id":"` + uuid.NewString() + `","secret":"must-not-cross-boundary"}`
	request := endpoint.request(t, deviceID, now, http.MethodPut,
		"/v1/account/pcs/"+pcID.String()+"/remote-routing", body, true, true)
	response := httptest.NewRecorder()
	endpoint.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || repository.called != "" {
		t.Fatalf("injected request status/store = %d/%q: %s", response.Code, repository.called, response.Body.String())
	}
	if strings.Contains(response.Body.String(), "must-not-cross-boundary") {
		t.Fatal("invalid-request response reflected a secret")
	}
}

var _ userPortalActionStore = (*fakeUserPortalActionStore)(nil)

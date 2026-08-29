package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type fakeManualEntitlementRepository struct {
	called        string
	actorID       uuid.UUID
	actorDeviceID uuid.UUID
	create        store.CreateManualEntitlementGrantInput
	revoke        store.RevokeManualEntitlementGrantInput
	grant         store.ManualEntitlementGrant
}

func (f *fakeManualEntitlementRepository) ListManualEntitlementGrants(_ context.Context, actorID uuid.UUID, _ store.ManualEntitlementGrantFilter) ([]store.ManualEntitlementGrant, error) {
	f.called, f.actorID = "list", actorID
	return []store.ManualEntitlementGrant{f.grant}, nil
}

func (f *fakeManualEntitlementRepository) CreateManualEntitlementGrant(_ context.Context, actorID, deviceID uuid.UUID, input store.CreateManualEntitlementGrantInput) (store.ManualEntitlementGrant, error) {
	f.called, f.actorID, f.actorDeviceID, f.create = "create", actorID, deviceID, input
	return f.grant, nil
}

func (f *fakeManualEntitlementRepository) RevokeManualEntitlementGrant(_ context.Context, actorID, deviceID uuid.UUID, input store.RevokeManualEntitlementGrantInput) (store.ManualEntitlementGrant, error) {
	f.called, f.actorID, f.actorDeviceID, f.revoke = "revoke", actorID, deviceID, input
	return f.grant, nil
}

func TestManualEntitlementViewIsMinimal(t *testing.T) {
	t.Parallel()
	view := manualEntitlementGrantView(store.ManualEntitlementGrant{ID: uuid.New(), AccountID: uuid.New()})
	expected := []string{"id", "account_id", "plan_key", "catalog_version", "starts_at", "ends_at", "status", "created_at", "updated_at"}
	if len(view) != len(expected) {
		t.Fatalf("view fields=%d, want %d", len(view), len(expected))
	}
	for _, field := range expected {
		if _, ok := view[field]; !ok {
			t.Fatalf("view lacks %q", field)
		}
	}
	encoded, _ := json.Marshal(view)
	for _, forbidden := range []string{"provider", "hash", "secret", "idempotency", "reason", "terminal", "ciphertext", "email"} {
		if strings.Contains(strings.ToLower(string(encoded)), forbidden) {
			t.Fatalf("response contains forbidden %q: %s", forbidden, encoded)
		}
	}
}

func TestManualEntitlementCreateRequiresAdminMFAReasonAndKey(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID, targetID := uuid.New(), uuid.New(), uuid.New()
	for _, test := range []struct {
		name        string
		admin, mfa  bool
		key, reason bool
		wantStatus  int
		wantCall    bool
	}{
		{"admin required", false, true, true, true, http.StatusForbidden, false},
		{"MFA required", true, false, true, true, http.StatusForbidden, false},
		{"reason required", true, true, true, false, http.StatusBadRequest, false},
		{"key required", true, true, false, true, http.StatusBadRequest, false},
		{"accepted", true, true, true, true, http.StatusCreated, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			repository := &fakeManualEntitlementRepository{grant: store.ManualEntitlementGrant{ID: uuid.New(), AccountID: targetID}}
			security := &memoryCommerceSecurityStore{admin: test.admin}
			service := &API{Clock: func() time.Time { return now }, commerceSecurity: security}
			handlers := manualEntitlementHandlers{api: service, repository: repository}
			endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
				"POST /v1/admin/manual-entitlements", auth.Identity{MFA: test.mfa, AuthenticatedAt: now}, handlers.create)
			request := endpoint.request(t, deviceID, now, http.MethodPost, "/v1/admin/manual-entitlements",
				`{"account_id":"`+targetID.String()+`","plan_key":"pro","duration_days":30}`, test.key, test.reason)
			response := httptest.NewRecorder()
			endpoint.handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus {
				t.Fatalf("status/body=%d/%s, want %d", response.Code, response.Body.String(), test.wantStatus)
			}
			if (repository.called == "create") != test.wantCall {
				t.Fatalf("store call=%q, wantCall=%v", repository.called, test.wantCall)
			}
			if test.wantCall {
				if repository.actorID != accountID || repository.actorDeviceID != deviceID || repository.create.AccountID != targetID ||
					repository.create.Reason != "Portal owner action" || repository.create.IdempotencyKey != "portal-idempotency-key" {
					t.Fatalf("unsafe forwarding: actor/device/input=%s/%s/%+v", repository.actorID, repository.actorDeviceID, repository.create)
				}
				if strings.Contains(response.Body.String(), "Portal owner action") || strings.Contains(response.Body.String(), "portal-idempotency-key") {
					t.Fatal("reason or idempotency key reflected")
				}
			}
		})
	}
}

func TestManualEntitlementRejectsUnknownAndSensitiveRequestFields(t *testing.T) {
	now := time.Unix(1_800_000_000, 0).UTC()
	accountID, deviceID := uuid.New(), uuid.New()
	repository := &fakeManualEntitlementRepository{}
	service := &API{Clock: func() time.Time { return now }, commerceSecurity: &memoryCommerceSecurityStore{admin: true}}
	handlers := manualEntitlementHandlers{api: service, repository: repository}
	endpoint := userPortalAuthenticatedEndpoint(t, accountID, deviceID, now,
		"POST /v1/admin/manual-entitlements", auth.Identity{MFA: true, AuthenticatedAt: now}, handlers.create)
	request := endpoint.request(t, deviceID, now, http.MethodPost, "/v1/admin/manual-entitlements",
		`{"account_id":"`+uuid.NewString()+`","plan_key":"pro","duration_days":30,"provider_ref":"must-not-cross"}`, true, true)
	response := httptest.NewRecorder()
	endpoint.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || repository.called != "" {
		t.Fatalf("status/store=%d/%q: %s", response.Code, repository.called, response.Body.String())
	}
	if strings.Contains(response.Body.String(), "must-not-cross") {
		t.Fatal("sensitive injected field reflected")
	}
}

var _ manualEntitlementRepository = (*fakeManualEntitlementRepository)(nil)

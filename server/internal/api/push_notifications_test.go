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
	"github.com/termlink/termlink/server/internal/pushnotify"
	"github.com/termlink/termlink/server/internal/store"
)

type pushStoreFixture struct {
	registrations, events, unregistered int
	input                               store.PushRegistrationInput
	account, device                     uuid.UUID
	rateLimited                         bool
}

func (f *pushStoreFixture) GetPushRegistration(context.Context, uuid.UUID, uuid.UUID, string, string) (*store.PushRegistration, error) {
	return nil, nil
}
func (f *pushStoreFixture) RegisterPushDevice(_ context.Context, account, device uuid.UUID, input store.PushRegistrationInput) (store.PushRegistration, error) {
	f.registrations++
	f.input = input
	f.account = account
	f.device = device
	return store.PushRegistration{ID: uuid.New(), Generation: uuid.New(), Enabled: true, Topic: input.Topic, Environment: input.Environment}, nil
}
func (f *pushStoreFixture) UnregisterPushDevice(context.Context, uuid.UUID, uuid.UUID, string, string, uuid.UUID) error {
	f.unregistered++
	return nil
}
func (f *pushStoreFixture) EnqueuePushEvent(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.PushEventInput) (int, error) {
	f.events++
	if f.rateLimited {
		return 0, store.ErrPushRateLimited
	}
	return 1, nil
}

func TestSignedIOSPushRegistrationEncryptsTokenAndLocksEnvironment(t *testing.T) {
	account, device := uuid.New(), uuid.New()
	now := time.Now().UTC()
	authentication, key := fileOperationTestAuth(t, account, device, "IOS", now)
	codec, err := pushnotify.New([]byte(strings.Repeat("k", 32)))
	if err != nil {
		t.Fatal(err)
	}
	repo := &pushStoreFixture{}
	a := &API{Auth: authentication, pushNotifications: repo, PushNotifications: PushNotificationConfiguration{Enabled: true, Codec: codec, Topic: "dev.termlink.ios", Environment: "sandbox"}}
	token := strings.Repeat("ab", 48)
	body, _ := json.Marshal(map[string]any{"token": token})
	request := signedFileOperationRequest(t, key, device, now, http.MethodPut, "/v1/devices/self/push", body)
	request.Header.Set("Idempotency-Key", uuid.NewString())
	response := httptest.NewRecorder()
	a.Handler().ServeHTTP(response, request)
	if response.Code != 200 || repo.registrations != 1 || repo.account != account || repo.device != device || repo.input.Environment != "sandbox" || repo.input.Topic != "dev.termlink.ios" {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	decoded, err := codec.Open(repo.input.TokenCipher, account, device, "dev.termlink.ios", "sandbox")
	if err != nil || decoded != token {
		t.Fatal("token encrypted with wrong scope")
	}
	if strings.Contains(response.Body.String(), token) || strings.Contains(response.Body.String(), "token_cipher") {
		t.Fatal("token exposed")
	}
	for _, bad := range []string{`{"token":"` + token + `","environment":"production"}`, `{"token":"` + token + `","alert":"conversation content"}`, `{"token":"invalid"}`} {
		req := signedFileOperationRequest(t, key, device, now, http.MethodPut, "/v1/devices/self/push", []byte(bad))
		r := httptest.NewRecorder()
		authentication.Authenticate(http.HandlerFunc(a.devicePush)).ServeHTTP(r, req)
		if r.Code != 400 {
			t.Fatalf("untrusted fields accepted %d", r.Code)
		}
	}
}

func TestPushEndpointsRejectWrongIdentityAndDisabledFeature(t *testing.T) {
	codec, _ := pushnotify.New([]byte(strings.Repeat("k", 32)))
	for _, role := range []string{"ANDROID", "PC_AGENT", "WINDOWS_PC", "IOS"} {
		for _, enabled := range []bool{false, true} {
			account, id := uuid.New(), uuid.New()
			auth, key := fileOperationTestAuth(t, account, id, role, time.Now().UTC())
			repo := &pushStoreFixture{}
			a := &API{pushNotifications: repo, PushNotifications: PushNotificationConfiguration{Enabled: enabled, Codec: codec, Topic: "dev.termlink.ios", Environment: "sandbox"}}
			req := signedFileOperationRequest(t, key, id, time.Now().UTC(), http.MethodGet, "/v1/devices/self/push", nil)
			res := httptest.NewRecorder()
			auth.Authenticate(http.HandlerFunc(a.devicePush)).ServeHTTP(res, req)
			want := 403
			if role == "IOS" {
				want = 503
				if enabled {
					want = 200
				}
			}
			if res.Code != want {
				t.Fatalf("role=%s enabled=%v status=%d want=%d", role, enabled, res.Code, want)
			}
		}
	}
}

func TestSignedPCNotificationEventAcceptsOnlyBoundedStatusMetadata(t *testing.T) {
	now := time.Now().UTC()
	account, agent, pc, mobile := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	authentication, key := fileOperationTestAuth(t, account, agent, "PC_AGENT", now)
	input := map[string]any{"event_id": uuid.New(), "kind": "ai.needs_input", "session_id": uuid.New(), "recipient_device_ids": []uuid.UUID{mobile}, "occurred_at": now}
	path := "/v1/pcs/" + pc.String() + "/notification-events"
	for _, test := range []struct {
		name    string
		change  func(map[string]any)
		want    int
		limited bool
	}{
		{"valid", func(map[string]any) {}, 202, false},
		{"cannot forge upgrade", func(v map[string]any) { v["kind"] = "upgrade.succeeded" }, 400, false},
		{"content denied", func(v map[string]any) { v["text"] = "secret body" }, 400, false},
		{"too old", func(v map[string]any) { v["occurred_at"] = now.Add(-time.Hour) }, 400, false},
		{"future", func(v map[string]any) { v["occurred_at"] = now.Add(time.Hour) }, 400, false},
		{"no recipient", func(v map[string]any) { v["recipient_device_ids"] = []uuid.UUID{} }, 400, false},
		{"rate limit", func(map[string]any) {}, 429, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			v := map[string]any{}
			for k, x := range input {
				v[k] = x
			}
			test.change(v)
			body, _ := json.Marshal(v)
			repo := &pushStoreFixture{rateLimited: test.limited}
			a := &API{Clock: func() time.Time { return now }, pushNotifications: repo, PushNotifications: PushNotificationConfiguration{Enabled: true}}
			req := signedFileOperationRequest(t, key, agent, now, http.MethodPost, path, body)
			req.SetPathValue("pcId", pc.String())
			res := httptest.NewRecorder()
			authentication.Authenticate(http.HandlerFunc(a.notificationEvent)).ServeHTTP(res, req)
			if res.Code != test.want {
				t.Fatalf("status=%d body=%s", res.Code, res.Body.String())
			}
			if test.want == 400 && repo.events != 0 {
				t.Fatal("invalid event reached database")
			}
			if test.want == 429 && res.Header().Get("Retry-After") != "60" {
				t.Fatal("rate limit has no backoff")
			}
		})
	}
}

func TestPushUnregisterRemainsAvailableWhenDeliveryIsDisabled(t *testing.T) {
	account, device := uuid.New(), uuid.New()
	now := time.Now().UTC()
	authentication, key := fileOperationTestAuth(t, account, device, "IOS", now)
	repo := &pushStoreFixture{}
	a := &API{Auth: authentication, pushNotifications: repo}
	body, _ := json.Marshal(map[string]any{"expected_generation": uuid.New()})
	req := signedFileOperationRequest(t, key, device, now, http.MethodDelete, "/v1/devices/self/push", body)
	req.Header.Set("Idempotency-Key", uuid.NewString())
	response := httptest.NewRecorder()
	a.Handler().ServeHTTP(response, req)
	if response.Code != 204 || repo.unregistered != 1 {
		t.Fatalf("disabled feature prevented logout cleanup %d %s", response.Code, response.Body.String())
	}
}

func TestPushRegistrationRejectsUnsupportedMethods(t *testing.T) {
	now := time.Now().UTC()
	account, device := uuid.New(), uuid.New()
	authentication, key := fileOperationTestAuth(t, account, device, "IOS", now)
	a := &API{}
	req := signedFileOperationRequest(t, key, device, now, http.MethodPost, "/v1/devices/self/push", []byte(`{}`))
	response := httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(a.devicePush)).ServeHTTP(response, req)
	if response.Code != 405 || response.Header().Get("Allow") == "" {
		t.Fatal("unsupported method accepted")
	}
}

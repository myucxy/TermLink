package humanverification

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type managerMemoryRepository struct {
	settings store.HumanVerificationSettings
	saved    store.HumanVerificationSettingsInput
	err      error
}

func (r *managerMemoryRepository) GetHumanVerificationSettings(context.Context) (store.HumanVerificationSettings, error) {
	return r.settings, r.err
}

func (r *managerMemoryRepository) SaveHumanVerificationSettings(_ context.Context, _ uuid.UUID, input store.HumanVerificationSettingsInput) (store.HumanVerificationSettings, error) {
	if r.err != nil {
		return store.HumanVerificationSettings{}, r.err
	}
	r.saved = input
	r.settings = store.HumanVerificationSettings{
		Enabled: input.Enabled, SiteKey: input.SiteKey, SecretCiphertext: append([]byte(nil), input.SecretCiphertext...),
		Version: input.ExpectedVersion + 1, UpdatedAt: time.Date(2026, time.August, 31, 8, 0, 0, 0, time.UTC),
	}
	return r.settings, nil
}

func TestManagerMigratesDeploymentFallbackToEncryptedManagedSettings(t *testing.T) {
	repository := &managerMemoryRepository{settings: store.HumanVerificationSettings{Version: 1}}
	manager, err := NewManager(ManagerConfig{
		Repository: repository, EncryptionKey: []byte("human-verification-test-key-0123456789"),
		ExpectedHostname: "termlink.example.test", FallbackSiteKey: "deployment-site", FallbackSecret: "deployment-secret",
	})
	if err != nil {
		t.Fatal(err)
	}
	view, err := manager.Admin(context.Background())
	if err != nil || !view.Enabled || view.SiteKey != "deployment-site" || !view.SecretConfigured || view.Source != "DEPLOYMENT" {
		t.Fatalf("fallback admin view = %+v, err=%v", view, err)
	}
	updated, err := manager.Update(context.Background(), uuid.New(), UpdateInput{
		Enabled: true, SiteKey: "managed-site", ExpectedVersion: 1,
	})
	if err != nil || !updated.Enabled || updated.Source != "MANAGED" || updated.Version != 2 {
		t.Fatalf("managed update = %+v, err=%v", updated, err)
	}
	if len(repository.saved.SecretCiphertext) == 0 || bytes.Contains(repository.saved.SecretCiphertext, []byte("deployment-secret")) {
		t.Fatal("managed settings did not encrypt the preserved deployment secret")
	}
	loaded, err := manager.Admin(context.Background())
	if err != nil || loaded.SiteKey != "managed-site" || !loaded.SecretConfigured || loaded.Source != "MANAGED" {
		t.Fatalf("managed admin view = %+v, err=%v", loaded, err)
	}
}

func TestManagerUpdateAppliesImmediatelyAndNeverExposesSecret(t *testing.T) {
	var providerCalls int
	provider := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		providerCalls++
		if err := r.ParseForm(); err != nil {
			t.Fatal(err)
		}
		if r.PostForm.Get("secret") != "managed-secret" || r.PostForm.Get("response") != "managed-token" {
			t.Fatalf("Siteverify form = %v", r.PostForm)
		}
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": true, "hostname": "termlink.example.test", "action": RegistrationAction,
		})
	}))
	defer provider.Close()
	repository := &managerMemoryRepository{settings: store.HumanVerificationSettings{Version: 1}}
	manager, err := NewManager(ManagerConfig{
		Repository: repository, EncryptionKey: []byte("human-verification-test-key-0123456789"),
		ExpectedHostname: "termlink.example.test", VerifyURL: provider.URL, HTTPClient: provider.Client(),
	})
	if err != nil {
		t.Fatal(err)
	}
	view, err := manager.Update(context.Background(), uuid.New(), UpdateInput{
		Enabled: true, SiteKey: "managed-site", Secret: "managed-secret", ExpectedVersion: 1,
	})
	if err != nil || !view.Enabled || view.SiteKey != "managed-site" || !view.SecretConfigured {
		t.Fatalf("Update() = %+v, err=%v", view, err)
	}
	if err = manager.Verify(context.Background(), "managed-token", "203.0.113.9"); err != nil || providerCalls != 1 {
		t.Fatalf("Verify() err=%v providerCalls=%d", err, providerCalls)
	}
	if viewText, _ := json.Marshal(view); strings.Contains(string(viewText), "managed-secret") || bytes.Contains(repository.saved.SecretCiphertext, []byte("managed-secret")) {
		t.Fatal("admin view or repository boundary exposed plaintext Secret")
	}
	view, err = manager.Update(context.Background(), uuid.New(), UpdateInput{
		Enabled: false, SiteKey: "managed-site", ClearSecret: true, ExpectedVersion: 2,
	})
	if err != nil || view.Enabled || view.SecretConfigured || len(repository.saved.SecretCiphertext) != 0 {
		t.Fatalf("clear update = %+v saved=%+v err=%v", view, repository.saved, err)
	}
	if err = manager.Verify(context.Background(), "", ""); err != nil || providerCalls != 1 {
		t.Fatalf("disabled Verify() err=%v providerCalls=%d", err, providerCalls)
	}
}

func TestManagerFailsClosedForInvalidOrUnavailableSettings(t *testing.T) {
	repository := &managerMemoryRepository{settings: store.HumanVerificationSettings{Version: 1}}
	manager, err := NewManager(ManagerConfig{
		Repository: repository, EncryptionKey: []byte("human-verification-test-key-0123456789"), ExpectedHostname: "termlink.example.test",
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = manager.Update(context.Background(), uuid.New(), UpdateInput{Enabled: true, SiteKey: "site", ExpectedVersion: 1}); !errors.Is(err, ErrInvalidSettings) {
		t.Fatalf("incomplete enabled settings error = %v", err)
	}
	repository.err = errors.New("database unavailable")
	if err = manager.Verify(context.Background(), "token", ""); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unavailable settings Verify() error = %v", err)
	}
}

func TestManagerReportsStaleAdminVersionAsConflict(t *testing.T) {
	repository := &managerMemoryRepository{settings: store.HumanVerificationSettings{Version: 2}}
	manager, err := NewManager(ManagerConfig{
		Repository: repository, EncryptionKey: []byte("human-verification-test-key-0123456789"), ExpectedHostname: "termlink.example.test",
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = manager.Update(context.Background(), uuid.New(), UpdateInput{ExpectedVersion: 1})
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("stale settings error = %v, want %v", err, store.ErrConflict)
	}
}

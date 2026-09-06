package humanverification

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestTurnstileVerifiesBoundedRegistrationChallenge(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.Header.Get("Accept") != "application/json" {
			t.Fatalf("unexpected request: %s headers=%v", r.Method, r.Header)
		}
		if err := r.ParseForm(); err != nil {
			t.Fatal(err)
		}
		if r.PostForm.Get("secret") != "test-secret" || r.PostForm.Get("response") != "valid-token" ||
			r.PostForm.Get("remoteip") != "203.0.113.18" {
			t.Fatalf("unexpected Siteverify form keys: %v", r.PostForm)
		}
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": true, "hostname": "termlink.example.test", "action": RegistrationAction,
		})
	}))
	defer server.Close()
	verifier, err := NewTurnstile(TurnstileConfig{
		SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
		VerifyURL: server.URL, HTTPClient: server.Client(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if verifier.SiteKey() != "test-site-key" {
		t.Fatalf("site key = %q", verifier.SiteKey())
	}
	if err := verifier.Verify(context.Background(), "valid-token", "203.0.113.18"); err != nil {
		t.Fatalf("Verify() error = %v", err)
	}
}

func TestTurnstileBindsLoginChallengeToLoginAction(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			t.Fatal(err)
		}
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": true, "hostname": "termlink.example.test", "action": LoginAction,
		})
	}))
	defer server.Close()
	verifier, err := NewTurnstile(TurnstileConfig{
		SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
		VerifyURL: server.URL, HTTPClient: server.Client(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if err = verifier.VerifyAction(context.Background(), "login-token", "203.0.113.19", LoginAction); err != nil {
		t.Fatalf("VerifyAction(login) error = %v", err)
	}
	if err = verifier.Verify(context.Background(), "login-token", "203.0.113.19"); !errors.Is(err, ErrRejected) {
		t.Fatalf("login token crossed into registration: %v", err)
	}
	if err = verifier.VerifyAction(context.Background(), "login-token", "", "unsupported"); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("unsupported action error = %v", err)
	}
}

func TestTurnstileFailsClosedWithoutLeakingProviderDetail(t *testing.T) {
	tests := []struct {
		name   string
		status int
		body   any
		want   error
	}{
		{name: "provider rejection", status: 200, body: map[string]any{"success": false, "error-codes": []string{"secret-provider-detail"}}, want: ErrRejected},
		{name: "wrong action", status: 200, body: map[string]any{"success": true, "hostname": "termlink.example.test", "action": "other"}, want: ErrRejected},
		{name: "wrong hostname", status: 200, body: map[string]any{"success": true, "hostname": "attacker.example", "action": RegistrationAction}, want: ErrRejected},
		{name: "provider unavailable", status: 503, body: map[string]any{"secret": "provider-detail"}, want: ErrUnavailable},
		{name: "invalid response", status: 200, body: "not-json", want: ErrUnavailable},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(tt.status)
				if text, ok := tt.body.(string); ok {
					_, _ = w.Write([]byte(text))
				} else {
					_ = json.NewEncoder(w).Encode(tt.body)
				}
			}))
			defer server.Close()
			verifier, err := NewTurnstile(TurnstileConfig{
				SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
				VerifyURL: server.URL, HTTPClient: server.Client(),
			})
			if err != nil {
				t.Fatal(err)
			}
			err = verifier.Verify(context.Background(), "valid-token", "")
			if !errors.Is(err, tt.want) || (err != nil && (err.Error() == "provider-detail" || err.Error() == "secret-provider-detail")) {
				t.Fatalf("Verify() error = %v, want %v", err, tt.want)
			}
		})
	}
	verifier := &Turnstile{}
	if err := verifier.Verify(context.Background(), "", ""); !errors.Is(err, ErrRequired) {
		t.Fatalf("empty token error = %v", err)
	}
}

func TestTurnstileRejectsRedirectAndTimesOut(t *testing.T) {
	t.Run("redirect", func(t *testing.T) {
		target := httptest.NewTLSServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
			t.Fatal("redirected request reached target")
		}))
		defer target.Close()
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
		}))
		defer server.Close()
		verifier, err := NewTurnstile(TurnstileConfig{
			SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
			VerifyURL: server.URL, HTTPClient: server.Client(),
		})
		if err != nil {
			t.Fatal(err)
		}
		if err := verifier.Verify(context.Background(), "valid-token", ""); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("redirect error = %v", err)
		}
	})
	t.Run("timeout", func(t *testing.T) {
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			time.Sleep(250 * time.Millisecond)
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true})
		}))
		defer server.Close()
		verifier, err := NewTurnstile(TurnstileConfig{
			SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
			VerifyURL: server.URL, HTTPClient: server.Client(), Timeout: 100 * time.Millisecond,
		})
		if err != nil {
			t.Fatal(err)
		}
		if err := verifier.Verify(context.Background(), "valid-token", ""); !errors.Is(err, ErrUnavailable) {
			t.Fatalf("timeout error = %v", err)
		}
	})
}

func TestTurnstileRejectsOversizedProviderResponse(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(`{"success":true,"hostname":"termlink.example.test","action":"registration"}`))
		_, _ = w.Write([]byte(strings.Repeat(" ", maximumResponseBytes)))
	}))
	defer server.Close()
	verifier, err := NewTurnstile(TurnstileConfig{
		SiteKey: "test-site-key", Secret: "test-secret", ExpectedHostname: "termlink.example.test",
		VerifyURL: server.URL, HTTPClient: server.Client(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := verifier.Verify(context.Background(), "valid-token", ""); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("oversized response error = %v", err)
	}
}

func TestTurnstileConfigurationRejectsUnsafeInputs(t *testing.T) {
	for _, config := range []TurnstileConfig{
		{},
		{SiteKey: "site key", Secret: "secret", ExpectedHostname: "termlink.example.test"},
		{SiteKey: "site-key", Secret: "secret\nvalue", ExpectedHostname: "termlink.example.test"},
		{SiteKey: "site-key", Secret: "secret", ExpectedHostname: "https://termlink.example.test"},
		{SiteKey: "site-key", Secret: "secret", ExpectedHostname: "termlink.example.test", VerifyURL: "http://example.test/siteverify"},
	} {
		if _, err := NewTurnstile(config); err == nil {
			t.Fatalf("unsafe config was accepted: site=%q host=%q url=%q", config.SiteKey, config.ExpectedHostname, config.VerifyURL)
		}
	}
}

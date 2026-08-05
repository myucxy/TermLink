package main

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"sync/atomic"
	"testing"
)

func TestVerifyAnonymousRegistrationUsesOnlyThemedGETFlow(t *testing.T) {
	var registrationGets atomic.Int32
	var mutationRequests atomic.Int32
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			mutationRequests.Add(1)
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		switch r.URL.Path {
		case "/login":
			query := url.Values{
				"client_id":             {"termlink-web"},
				"response_type":         {"code"},
				"redirect_uri":          {"https://" + r.Host + "/auth/callback"},
				"scope":                 {"openid profile email"},
				"state":                 {"opaque-state"},
				"nonce":                 {"opaque-nonce"},
				"code_challenge":        {"opaque-pkce-challenge"},
				"code_challenge_method": {"S256"},
			}
			http.Redirect(w, r, "/realms/termlink/protocol/openid-connect/auth?"+query.Encode(), http.StatusFound)
		case "/realms/termlink/protocol/openid-connect/auth":
			http.SetCookie(w, &http.Cookie{Name: "AUTH_SESSION_ID", Value: "opaque-session", Path: "/realms/termlink", Secure: true, HttpOnly: true})
			_, _ = fmt.Fprint(w, `<!doctype html><div class="identity-form-shell"><a href="/realms/termlink/login-actions/registration?session_code=opaque&amp;client_id=termlink-web">Create account</a></div>`)
		case "/realms/termlink/login-actions/registration":
			if _, err := r.Cookie("AUTH_SESSION_ID"); err != nil {
				http.Error(w, "missing browser session", http.StatusUnauthorized)
				return
			}
			registrationGets.Add(1)
			_, _ = fmt.Fprint(w, `<!doctype html><div class="identity-form-shell"><form id="kc-register-form" method="post"><input name="username"><input name="email" type="email"><input name="password" type="password"><input name="password-confirm" type="password"></form></div>`)
		default:
			http.NotFound(w, r)
		}
	})
	server := httptest.NewTLSServer(handler)
	defer server.Close()

	p := &probe{issuer: server.URL + "/realms/termlink", http: server.Client()}
	if err := p.verifyAnonymousRegistration(); err != nil {
		t.Fatal(err)
	}
	if registrationGets.Load() != 1 {
		t.Fatalf("registration GET count=%d, want 1", registrationGets.Load())
	}
	if mutationRequests.Load() != 0 {
		t.Fatalf("anonymous registration probe sent %d mutation requests", mutationRequests.Load())
	}
}

func TestVerifyAnonymousRegistrationRejectsCrossOriginAuthorization(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "https://attacker.invalid/realms/termlink/protocol/openid-connect/auth", http.StatusSeeOther)
	}))
	defer server.Close()

	p := &probe{issuer: server.URL + "/realms/termlink", http: server.Client()}
	if err := p.verifyAnonymousRegistration(); err == nil {
		t.Fatal("anonymous registration probe accepted a cross-origin authorization redirect")
	}
}

func TestRegistrationFormFieldContract(t *testing.T) {
	page := []byte(`<form id='kc-register-form'><input name='username'><input name='email'><input name='password'><input name='password-confirm'></form>`)
	for _, field := range []string{"username", "email", "password", "password-confirm"} {
		if !hasHTMLAttribute(page, "input", "name", field) {
			t.Fatalf("field %q was not detected", field)
		}
	}
	if hasHTMLAttribute(page, "input", "name", "phone") {
		t.Fatal("unexpected field was detected")
	}
}

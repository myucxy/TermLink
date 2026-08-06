package auth

import (
	"context"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

func TestDirectRegistrationServiceSubmitsBoundedForm(t *testing.T) {
	var posted url.Values
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
			w.Header().Set("Content-Type", "text/html")
			_, _ = w.Write([]byte(`<a href="/realms/test/login-actions/registration?session_code=opaque">Create</a>`))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
			_, _ = w.Write([]byte(`<form id="kc-register-form" action="http://` + r.Host + `/realms/test/login-actions/registration?session_code=opaque" method="post"><input type="hidden" name="locale" value="en"/></form>`))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
			if err := r.ParseForm(); err != nil {
				t.Fatal(err)
			}
			posted = r.PostForm
			w.Header().Set("Location", "http://"+r.Host+"/realms/test/protocol/openid-connect/auth?code=created")
			w.WriteHeader(http.StatusFound)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	issuer, _ := url.Parse(server.URL + "/realms/test")
	jar, _ := cookiejar.New(nil)
	service := &DirectRegistrationService{issuer: issuer, clientID: "termlink-android", redirectURI: "dev.termlink:/oauth2redirect", http: &http.Client{Jar: jar}}
	err := service.Register(context.Background(), DirectRegistrationInput{
		Username: "alice", Email: "alice@example.test", Password: "correct horse battery staple", Confirm: "correct horse battery staple",
	})
	if err != nil {
		t.Fatal(err)
	}
	if posted.Get("username") != "alice" || posted.Get("email") != "alice@example.test" || posted.Get("locale") != "en" {
		t.Fatalf("registration form was not preserved: %v", posted)
	}
	if !strings.Contains(posted.Get("password"), "correct horse") {
		t.Fatal("password was not submitted to the identity provider")
	}
}

func TestValidateDirectRegistrationInput(t *testing.T) {
	if err := ValidateDirectRegistrationInput(DirectRegistrationInput{Username: "ab", Email: "x@y", Password: "short", Confirm: "short"}); err == nil {
		t.Fatal("short registration fields unexpectedly accepted")
	}
	if err := ValidateDirectRegistrationInput(DirectRegistrationInput{Username: "alice", Email: "alice@example.test", Password: "correct horse battery staple", Confirm: "different"}); err == nil {
		t.Fatal("mismatched password unexpectedly accepted")
	}
}

func TestNewDirectRegistrationServiceAcceptsAndroidPrivateUseRedirect(t *testing.T) {
	service, err := NewDirectRegistrationService(DirectRegistrationConfig{
		Issuer: "https://identity.example.test/realms/termlink",
		ClientID: "termlink-android",
		RedirectURI: "dev.termlink:/oauth2redirect",
	})
	if err != nil || service == nil {
		t.Fatalf("private-use Android redirect rejected: %v", err)
	}
}

func TestNewDirectRegistrationServiceRejectsUnknownPrivateUseRedirect(t *testing.T) {
	if _, err := NewDirectRegistrationService(DirectRegistrationConfig{
		Issuer: "https://identity.example.test/realms/termlink",
		ClientID: "termlink-android",
		RedirectURI: "javascript:/oauth2redirect",
	}); err == nil {
		t.Fatal("unknown redirect scheme unexpectedly accepted")
	}
}

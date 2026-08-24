package auth

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestDirectRegistrationServiceSubmitsBoundedForm(t *testing.T) {
	var posted url.Values
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
			w.Header().Set("Content-Type", "text/html")
			_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s">Create</a>`, url.QueryEscape(r.URL.Query().Get("state")))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
			_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="http://%s/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s" method="post"><input type="hidden" name="locale" value="en"/></form>`, r.Host, url.QueryEscape(r.URL.Query().Get("flow_state")))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
			if err := r.ParseForm(); err != nil {
				t.Fatal(err)
			}
			posted = r.PostForm
			w.Header().Set("Location", "dev.termlink:/oauth2redirect?code=created&state="+url.QueryEscape(r.URL.Query().Get("flow_state")))
			w.WriteHeader(http.StatusFound)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	issuer, _ := url.Parse(server.URL + "/realms/test")
	jar, _ := cookiejar.New(nil)
	service := &DirectRegistrationService{
		issuer: issuer, clientID: "termlink-android", redirectURI: "dev.termlink:/oauth2redirect",
		http: &http.Client{Jar: jar, CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }},
	}
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

func TestDirectRegistrationServiceIsolatesConcurrentSessions(t *testing.T) {
	var (
		mu            sync.Mutex
		nextFlow      int
		postedFlows   []string
		usedFlows     = make(map[string]bool)
		firstPageSeen bool
		releaseOnce   sync.Once
	)
	firstPageStarted := make(chan struct{})
	releaseFirstPage := make(chan struct{})
	releaseFirst := func() {
		releaseOnce.Do(func() { close(releaseFirstPage) })
	}

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
			flow := ""
			if cookie, err := r.Cookie("registration_flow"); err == nil {
				flow = cookie.Value
			}
			if flow == "" {
				mu.Lock()
				nextFlow++
				flow = fmt.Sprintf("flow-%d", nextFlow)
				mu.Unlock()
				http.SetCookie(w, &http.Cookie{Name: "registration_flow", Value: flow, Path: "/realms/test"})
			}
			_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=%s&amp;flow_state=%s">Create</a>`, flow, url.QueryEscape(r.URL.Query().Get("state")))

		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
			mu.Lock()
			block := !firstPageSeen
			firstPageSeen = true
			mu.Unlock()
			if block {
				close(firstPageStarted)
				<-releaseFirstPage
			}
			flow := r.URL.Query().Get("session_code")
			_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="http://%s/realms/test/login-actions/registration?session_code=%s&amp;flow_state=%s" method="post"></form>`, r.Host, flow, url.QueryEscape(r.URL.Query().Get("flow_state")))

		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
			flow := r.URL.Query().Get("session_code")
			mu.Lock()
			postedFlows = append(postedFlows, flow)
			duplicate := usedFlows[flow]
			usedFlows[flow] = true
			mu.Unlock()
			if duplicate {
				w.WriteHeader(http.StatusOK)
				_, _ = w.Write([]byte(`<form id="kc-register-form"><span id="kc-feedback-text">expired flow</span></form>`))
				return
			}
			w.Header().Set("Location", "dev.termlink:/oauth2redirect?code=created&state="+url.QueryEscape(r.URL.Query().Get("flow_state")))
			w.WriteHeader(http.StatusFound)

		default:
			http.NotFound(w, r)
		}
	}))
	defer func() {
		releaseFirst()
		server.Close()
	}()

	issuer, _ := url.Parse(server.URL + "/realms/test")
	sharedJar, _ := cookiejar.New(nil)
	service := &DirectRegistrationService{
		issuer:      issuer,
		clientID:    "termlink-android",
		redirectURI: "dev.termlink:/oauth2redirect",
		http: &http.Client{
			Jar:           sharedJar,
			Timeout:       2 * time.Second,
			CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse },
		},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	results := make(chan error, 2)
	register := func(username string) {
		results <- service.Register(ctx, DirectRegistrationInput{
			Username: username,
			Email:    username + "@example.test",
			Password: "correct horse battery staple",
			Confirm:  "correct horse battery staple",
		})
	}

	go register("alice")
	select {
	case <-firstPageStarted:
	case <-ctx.Done():
		t.Fatal("first registration did not reach the provider form")
	}
	go register("bob")
	select {
	case err := <-results:
		if err != nil {
			t.Fatalf("overlapping registration failed: %v", err)
		}
	case <-ctx.Done():
		t.Fatal("second registration did not complete independently")
	}
	releaseFirst()
	select {
	case err := <-results:
		if err != nil {
			t.Fatalf("first registration failed: %v", err)
		}
	case <-ctx.Done():
		t.Fatal("first registration did not complete")
	}

	mu.Lock()
	defer mu.Unlock()
	if len(postedFlows) != 2 || postedFlows[0] == postedFlows[1] {
		t.Fatalf("registration flows were not isolated: %v", postedFlows)
	}
}

func TestDirectRegistrationServiceRejectsInvalidCallbacks(t *testing.T) {
	tests := []struct {
		name        string
		redirectURI string
		location    func(string) string
	}{
		{
			name:        "state mismatch",
			redirectURI: "dev.termlink:/oauth2redirect",
			location:    func(string) string { return "dev.termlink:/oauth2redirect?code=created&state=other" },
		},
		{
			name:        "wrong scheme",
			redirectURI: "dev.termlink:/oauth2redirect",
			location: func(state string) string {
				return "other.termlink:/oauth2redirect?code=created&state=" + url.QueryEscape(state)
			},
		},
		{
			name:        "wrong path",
			redirectURI: "dev.termlink:/oauth2redirect",
			location:    func(state string) string { return "dev.termlink:/other?code=created&state=" + url.QueryEscape(state) },
		},
		{
			name:        "OAuth error",
			redirectURI: "dev.termlink:/oauth2redirect",
			location: func(state string) string {
				return "dev.termlink:/oauth2redirect?code=created&error=access_denied&state=" + url.QueryEscape(state)
			},
		},
		{
			name:        "empty code",
			redirectURI: "dev.termlink:/oauth2redirect",
			location:    func(state string) string { return "dev.termlink:/oauth2redirect?code=&state=" + url.QueryEscape(state) },
		},
		{
			name:        "wrong origin",
			redirectURI: "https://callback.example.test/oauth2redirect",
			location: func(state string) string {
				return "https://other.example.test/oauth2redirect?code=created&state=" + url.QueryEscape(state)
			},
		},
		{
			name:        "userinfo",
			redirectURI: "https://callback.example.test/oauth2redirect",
			location: func(state string) string {
				return "https://user@callback.example.test/oauth2redirect?code=created&state=" + url.QueryEscape(state)
			},
		},
		{
			name:        "fragment",
			redirectURI: "dev.termlink:/oauth2redirect",
			location: func(state string) string {
				return "dev.termlink:/oauth2redirect?code=created&state=" + url.QueryEscape(state) + "#provider"
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch {
				case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
					_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s">Create</a>`, url.QueryEscape(r.URL.Query().Get("state")))
				case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
					_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="http://%s/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s" method="post"></form>`, r.Host, url.QueryEscape(r.URL.Query().Get("flow_state")))
				case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
					w.Header().Set("Location", test.location(r.URL.Query().Get("flow_state")))
					w.WriteHeader(http.StatusFound)
				default:
					http.NotFound(w, r)
				}
			}))
			defer server.Close()

			issuer, _ := url.Parse(server.URL + "/realms/test")
			service := &DirectRegistrationService{
				issuer: issuer, clientID: "termlink-android", redirectURI: test.redirectURI,
				http: &http.Client{CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }},
			}
			err := service.Register(context.Background(), DirectRegistrationInput{
				Username: "alice", Email: "alice@example.test", Password: "correct horse battery staple", Confirm: "correct horse battery staple",
			})
			if !errors.Is(err, errRegistrationRejected) {
				t.Fatalf("invalid callback was not rejected: %v", err)
			}
		})
	}
}

func TestDirectRegistrationServiceHTTP200RequiresExplicitSuccessMarker(t *testing.T) {
	tests := []struct {
		name        string
		body        string
		contentType string
		success     bool
	}{
		{
			name:    "TermLink Keycloak success alert",
			body:    `<div class="tl-alert tl-alert-success" role="alert"><span class="tl-alert-title">Account created</span></div>`,
			success: true,
		},
		{
			name:    "base Keycloak success alert",
			body:    `<div class="pf-v5-c-alert pf-m-success"><span class="pf-v5-c-alert__title kc-feedback-text">Account created</span></div>`,
			success: true,
		},
		{
			name: "unknown HTML",
			body: `<!doctype html><html><body><h1>Welcome</h1></body></html>`,
		},
		{
			name: "login page",
			body: `<form id="kc-form-login" action="/realms/test/login-actions/authenticate"><input name="username"><input name="password"></form>`,
		},
		{
			name: "maintenance page",
			body: `<!doctype html><html><head><title>Maintenance</title></head><body data-class="tl-alert-success"><h1>Service temporarily unavailable</h1></body></html>`,
		},
		{
			name: "generic success banner",
			body: `<div class="alert-success">Request accepted</div>`,
		},
		{
			name:        "non HTML body containing a marker",
			body:        `{"template":"<div class=\"tl-alert-success\">created</div>"}`,
			contentType: "application/json",
		},
		{
			name: "login page with stale success alert",
			body: `<div class="tl-alert-success">Previous action completed</div><form id='kc-form-login'></form>`,
		},
		{
			name: "registration validation error",
			body: `<div class="tl-alert-error">Username exists</div><form id='kc-register-form'><input aria-invalid='true'></form>`,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			contentType := test.contentType
			if contentType == "" {
				contentType = "text/html; charset=utf-8"
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch {
				case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
					_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s">Create</a>`, url.QueryEscape(r.URL.Query().Get("state")))
				case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
					_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="http://%s/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s" method="post"></form>`, r.Host, url.QueryEscape(r.URL.Query().Get("flow_state")))
				case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
					w.Header().Set("Content-Type", contentType)
					w.WriteHeader(http.StatusOK)
					_, _ = w.Write([]byte(test.body))
				default:
					http.NotFound(w, r)
				}
			}))
			defer server.Close()

			issuer, _ := url.Parse(server.URL + "/realms/test")
			service := &DirectRegistrationService{
				issuer: issuer, clientID: "termlink-android", redirectURI: "dev.termlink:/oauth2redirect",
				http: &http.Client{CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }},
			}
			err := service.Register(context.Background(), DirectRegistrationInput{
				Username: "alice", Email: "alice@example.test", Password: "correct horse battery staple", Confirm: "correct horse battery staple",
			})
			if test.success && err != nil {
				t.Fatalf("explicit Keycloak success marker was rejected: %v", err)
			}
			if !test.success && !errors.Is(err, errRegistrationRejected) {
				t.Fatalf("HTTP 200 page was not rejected: %v", err)
			}
		})
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
		Issuer:      "https://identity.example.test/realms/termlink",
		ClientID:    "termlink-android",
		RedirectURI: "dev.termlink:/oauth2redirect",
	})
	if err != nil || service == nil {
		t.Fatalf("private-use Android redirect rejected: %v", err)
	}
}

func TestNewDirectRegistrationServiceRejectsUnknownPrivateUseRedirect(t *testing.T) {
	if _, err := NewDirectRegistrationService(DirectRegistrationConfig{
		Issuer:      "https://identity.example.test/realms/termlink",
		ClientID:    "termlink-android",
		RedirectURI: "javascript:/oauth2redirect",
	}); err == nil {
		t.Fatal("unknown redirect scheme unexpectedly accepted")
	}
}

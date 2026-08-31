package api

import (
	"bytes"
	"context"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/humanverification"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/serviceemail"
	"github.com/termlink/termlink/server/internal/store"
)

func TestAccountRegistrationClientIdentity(t *testing.T) {
	tests := []struct {
		name       string
		remoteAddr string
		xForwarded string
		xRealIP    string
		want       string
	}{
		{
			name:       "direct public client ignores spoofed headers",
			remoteAddr: "203.0.113.10:43120",
			xForwarded: "198.51.100.99",
			xRealIP:    "198.51.100.100",
			want:       "ip:203.0.113.10",
		},
		{
			name:       "Caddy forwarded client",
			remoteAddr: "172.18.0.4:43120",
			xForwarded: "198.51.100.20",
			want:       "ip:198.51.100.20",
		},
		{
			name:       "Lucky through Caddy",
			remoteAddr: "172.18.0.4:43120",
			xForwarded: "192.168.9.28",
			xRealIP:    "198.51.100.30",
			want:       "ip:198.51.100.30",
		},
		{
			name:       "private direct client",
			remoteAddr: "172.18.0.4:43120",
			xForwarded: "192.168.9.30",
			want:       "ip:192.168.9.30",
		},
		{
			name:       "ambiguous forwarded chain fails closed",
			remoteAddr: "172.18.0.4:43120",
			xForwarded: "198.51.100.40, 198.51.100.41",
			want:       "ip:172.18.0.4",
		},
		{
			name:       "IPv6 socket address",
			remoteAddr: "[2001:db8::10]:43120",
			xForwarded: "198.51.100.50",
			want:       "ip:2001:db8::10",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", nil)
			request.RemoteAddr = test.remoteAddr
			if test.xForwarded != "" {
				request.Header.Set("X-Forwarded-For", test.xForwarded)
			}
			if test.xRealIP != "" {
				request.Header.Set("X-Real-IP", test.xRealIP)
			}
			if got := accountRegistrationClientIdentity(request); got != test.want {
				t.Fatalf("client identity = %q, want %q", got, test.want)
			}
		})
	}
}

func TestAccountRegistrationRateLimitSeparatesConcurrentLuckyClients(t *testing.T) {
	registration := newRegistrationServiceForRateLimitTest(t)
	redisClient := redis.NewClient(&redis.Options{Addr: "unused.invalid:6379"})
	redisClient.AddHook(&memorySetNXHook{keys: make(map[string]struct{})})
	t.Cleanup(func() { _ = redisClient.Close() })
	service := (&API{
		Valkey:       &kv.Valkey{Client: redisClient},
		Registration: registration,
	}).Handler()

	type result struct {
		client string
		status int
	}
	results := make(chan result, 2)
	start := make(chan struct{})
	for index, clientIP := range []string{"198.51.100.60", "198.51.100.61"} {
		index, clientIP := index, clientIP
		go func() {
			<-start
			status := performAccountRegistration(t, service, fmt.Sprintf("client-%d", index), clientIP)
			results <- result{client: clientIP, status: status}
		}()
	}
	close(start)
	for range 2 {
		outcome := <-results
		if outcome.status != http.StatusCreated {
			t.Fatalf("client %s registration status = %d, want %d", outcome.client, outcome.status, http.StatusCreated)
		}
	}

	if status := performAccountRegistration(t, service, "client-repeat", "198.51.100.60"); status != http.StatusTooManyRequests {
		t.Fatalf("repeated client registration status = %d, want %d", status, http.StatusTooManyRequests)
	}
}

func TestAccountRegistrationFailsClosedWhenIdentityAdminIsUnavailable(t *testing.T) {
	service := (&API{Registration: unavailableAccountRegistration{}}).Handler()
	if status := performAccountRegistration(t, service, "unavailable-user", "198.51.100.70"); status != http.StatusServiceUnavailable {
		t.Fatalf("unavailable identity administration status = %d, want %d", status, http.StatusServiceUnavailable)
	}
}

func TestRegistrationHumanVerificationFailsClosedBeforeAccountCreation(t *testing.T) {
	tests := []struct {
		name       string
		token      string
		verifyErr  error
		wantStatus int
		wantCode   string
		wantCalls  int
	}{
		{name: "missing token", verifyErr: humanverification.ErrRequired, wantStatus: http.StatusUnprocessableEntity, wantCode: "HUMAN_VERIFICATION_REQUIRED"},
		{name: "rejected token", token: "rejected-token", verifyErr: fmt.Errorf("%w: secret-provider-detail", humanverification.ErrRejected), wantStatus: http.StatusUnprocessableEntity, wantCode: "HUMAN_VERIFICATION_FAILED"},
		{name: "provider unavailable", token: "unavailable-token", verifyErr: humanverification.ErrUnavailable, wantStatus: http.StatusServiceUnavailable, wantCode: "HUMAN_VERIFICATION_UNAVAILABLE"},
		{name: "verified", token: "verified-token", wantStatus: http.StatusCreated, wantCalls: 1},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			registration := &recordingAccountRegistration{}
			verifier := &recordingHumanVerifier{err: test.verifyErr}
			handler := (&API{Registration: registration, HumanVerification: verifier}).Handler()
			response := performAccountRegistrationWithToken(t, handler, "human-test-user", "198.51.100.80", test.token)
			if response.Code != test.wantStatus || registration.calls != test.wantCalls {
				t.Fatalf("registration status/calls = %d/%d, want %d/%d; body=%q", response.Code, registration.calls, test.wantStatus, test.wantCalls, response.Body.String())
			}
			if verifier.calls != 1 || verifier.token != test.token || verifier.remoteIP != "198.51.100.80" {
				t.Fatalf("human verifier calls/token/ip = %d/%q/%q", verifier.calls, verifier.token, verifier.remoteIP)
			}
			if test.wantCode != "" && (!strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) ||
				strings.Contains(response.Body.String(), "secret-provider-detail")) {
				t.Fatalf("registration error was misclassified or leaked provider detail: %q", response.Body.String())
			}
		})
	}
}

func TestRegistrationAcceptsCachedTurnstileResponseField(t *testing.T) {
	registration := &recordingAccountRegistration{}
	verifier := &recordingHumanVerifier{}
	handler := (&API{Registration: registration, HumanVerification: verifier}).Handler()
	body := `{"username":"cached-widget-user","nickname":"Cached widget","email":"cached-widget@example.test",` +
		`"password":"correct horse battery staple","password_confirm":"correct horse battery staple",` +
		`"human_verification_token":"cached-token","cf-turnstile-response":"cached-token"}`
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", strings.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusCreated || registration.calls != 1 || verifier.calls != 1 || verifier.token != "cached-token" {
		t.Fatalf("cached Turnstile field status/register/verify/token = %d/%d/%d/%q; body=%q",
			response.Code, registration.calls, verifier.calls, verifier.token, response.Body.String())
	}
}

func TestRegistrationRejectsConflictingTurnstileResponseFields(t *testing.T) {
	registration := &recordingAccountRegistration{}
	verifier := &recordingHumanVerifier{}
	handler := (&API{Registration: registration, HumanVerification: verifier}).Handler()
	body := `{"username":"conflicting-widget-user","nickname":"Conflicting widget","email":"conflicting-widget@example.test",` +
		`"password":"correct horse battery staple","password_confirm":"correct horse battery staple",` +
		`"human_verification_token":"canonical-token","cf-turnstile-response":"different-token"}`
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", strings.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusUnprocessableEntity || registration.calls != 0 || verifier.calls != 0 ||
		!strings.Contains(response.Body.String(), `"code":"HUMAN_VERIFICATION_FAILED"`) {
		t.Fatalf("conflicting Turnstile fields status/register/verify/body = %d/%d/%d/%q",
			response.Code, registration.calls, verifier.calls, response.Body.String())
	}
}

func TestRegistrationEmailCodeVerifiesHumanBeforeMailService(t *testing.T) {
	repository := &humanVerificationEmailProbe{}
	emailService, err := serviceemail.New(repository, []byte("human-verification-email-key-0123456789"), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	verifier := &recordingHumanVerifier{err: humanverification.ErrRequired}
	handler := (&API{ServiceEmail: emailService, HumanVerification: verifier}).Handler()

	response := performRegistrationEmailCodeRequest(t, handler, "", "198.51.100.81")
	if response.Code != http.StatusUnprocessableEntity || repository.settingsCalls != 0 ||
		!strings.Contains(response.Body.String(), `"code":"HUMAN_VERIFICATION_REQUIRED"`) {
		t.Fatalf("missing challenge status/settings calls/body = %d/%d/%q", response.Code, repository.settingsCalls, response.Body.String())
	}
	if verifier.remoteIP != "198.51.100.81" {
		t.Fatalf("email-code verifier remote IP = %q", verifier.remoteIP)
	}

	verifier.err = nil
	response = performRegistrationEmailCodeRequest(t, handler, "verified-email-token", "198.51.100.82")
	if response.Code != http.StatusConflict || repository.settingsCalls != 1 || verifier.token != "verified-email-token" {
		t.Fatalf("verified challenge did not continue to email service: status/settings/token = %d/%d/%q body=%q", response.Code, repository.settingsCalls, verifier.token, response.Body.String())
	}
}

type recordingHumanVerifier struct {
	err      error
	calls    int
	token    string
	remoteIP string
}

func (v *recordingHumanVerifier) Verify(_ context.Context, token, remoteIP string) error {
	v.calls++
	v.token = token
	v.remoteIP = remoteIP
	return v.err
}

type recordingAccountRegistration struct {
	calls int
}

func (r *recordingAccountRegistration) Register(context.Context, auth.DirectRegistrationInput) error {
	r.calls++
	return nil
}

func (r *recordingAccountRegistration) RegisterAttempt(context.Context, uuid.UUID, auth.DirectRegistrationInput) error {
	r.calls++
	return nil
}

type humanVerificationEmailProbe struct {
	settingsCalls int
}

func (r *humanVerificationEmailProbe) GetServiceEmailSettings(context.Context) (store.ServiceEmailSettings, error) {
	r.settingsCalls++
	return store.ServiceEmailSettings{}, nil
}

func (*humanVerificationEmailProbe) SaveVerifiedServiceEmailSettings(context.Context, uuid.UUID, store.ServiceEmailSettingsInput) (store.ServiceEmailSettings, error) {
	return store.ServiceEmailSettings{}, store.ErrDenied
}

func (*humanVerificationEmailProbe) CreateRegistrationEmailChallenge(context.Context, store.RegistrationEmailChallenge, time.Duration) error {
	return store.ErrDenied
}

func (*humanVerificationEmailProbe) ConsumeRegistrationEmailChallenge(context.Context, uuid.UUID, [32]byte, [32]byte, time.Time) error {
	return store.ErrDenied
}

func (*humanVerificationEmailProbe) CreateAccountEmailChallenge(context.Context, store.AccountEmailChallenge, time.Duration) error {
	return store.ErrDenied
}

func (*humanVerificationEmailProbe) ConsumeAccountEmailChallenge(context.Context, uuid.UUID, uuid.UUID, [32]byte, [32]byte, time.Time) error {
	return store.ErrDenied
}

type unavailableAccountRegistration struct{}

func (unavailableAccountRegistration) Register(context.Context, auth.DirectRegistrationInput) error {
	return auth.ErrKeycloakAdminUnavailable
}

func (unavailableAccountRegistration) RegisterAttempt(context.Context, uuid.UUID, auth.DirectRegistrationInput) error {
	return auth.ErrKeycloakAdminUnavailable
}

func newRegistrationServiceForRateLimitTest(t *testing.T) *auth.DirectRegistrationService {
	t.Helper()
	provider := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/realms/test/protocol/openid-connect/auth":
			_, _ = fmt.Fprintf(w, `<a href="/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s">Create</a>`, url.QueryEscape(r.URL.Query().Get("state")))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodGet:
			_, _ = fmt.Fprintf(w, `<form id="kc-register-form" action="https://%s/realms/test/login-actions/registration?session_code=opaque&amp;flow_state=%s" method="post"></form>`, r.Host, url.QueryEscape(r.URL.Query().Get("flow_state")))
		case r.URL.Path == "/realms/test/login-actions/registration" && r.Method == http.MethodPost:
			w.Header().Set("Location", "dev.termlink:/oauth2redirect?code=created&state="+url.QueryEscape(r.URL.Query().Get("flow_state")))
			w.WriteHeader(http.StatusFound)
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(provider.Close)
	client := provider.Client()
	client.CheckRedirect = func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }
	service, err := auth.NewDirectRegistrationService(auth.DirectRegistrationConfig{
		Issuer:      provider.URL + "/realms/test",
		ClientID:    "termlink-android",
		RedirectURI: "dev.termlink:/oauth2redirect",
		HTTPClient:  client,
	})
	if err != nil {
		t.Fatal(err)
	}
	return service
}

func performAccountRegistration(t *testing.T, handler http.Handler, username, clientIP string) int {
	t.Helper()
	return performAccountRegistrationWithToken(t, handler, username, clientIP, "").Code
}

func performAccountRegistrationWithToken(t *testing.T, handler http.Handler, username, clientIP, humanToken string) *httptest.ResponseRecorder {
	t.Helper()
	body := []byte(fmt.Sprintf(`{"username":%q,"nickname":%q,"email":%q,"password":"correct horse battery staple","password_confirm":"correct horse battery staple","human_verification_token":%q}`, username, username, username+"@example.test", humanToken))
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", bytes.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-Forwarded-For", "192.168.9.28")
	request.Header.Set("X-Real-IP", clientIP)
	request.RemoteAddr = net.JoinHostPort("172.18.0.4", "43120")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}

func performRegistrationEmailCodeRequest(t *testing.T, handler http.Handler, humanToken, clientIP string) *httptest.ResponseRecorder {
	t.Helper()
	body := []byte(fmt.Sprintf(`{"email":"human@example.test","human_verification_token":%q}`, humanToken))
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register/email-code", bytes.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-Forwarded-For", "192.168.9.28")
	request.Header.Set("X-Real-IP", clientIP)
	request.RemoteAddr = net.JoinHostPort("172.18.0.4", "43120")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}

type memorySetNXHook struct {
	mu   sync.Mutex
	keys map[string]struct{}
}

func (h *memorySetNXHook) DialHook(next redis.DialHook) redis.DialHook {
	return next
}

func (h *memorySetNXHook) ProcessHook(next redis.ProcessHook) redis.ProcessHook {
	return func(ctx context.Context, command redis.Cmder) error {
		result, ok := command.(*redis.BoolCmd)
		if !ok || (command.Name() != "set" && command.Name() != "setnx") || len(command.Args()) < 2 {
			return next(ctx, command)
		}
		key := fmt.Sprint(command.Args()[1])
		h.mu.Lock()
		_, exists := h.keys[key]
		if !exists {
			h.keys[key] = struct{}{}
		}
		h.mu.Unlock()
		result.SetVal(!exists)
		return nil
	}
}

func (h *memorySetNXHook) ProcessPipelineHook(next redis.ProcessPipelineHook) redis.ProcessPipelineHook {
	return next
}

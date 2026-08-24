package api

import (
	"bytes"
	"context"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"sync"
	"testing"

	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
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
	body := []byte(fmt.Sprintf(`{"username":%q,"email":%q,"password":"correct horse battery staple","password_confirm":"correct horse battery staple"}`, username, username+"@example.test"))
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", bytes.NewReader(body))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-Forwarded-For", "192.168.9.28")
	request.Header.Set("X-Real-IP", clientIP)
	request.RemoteAddr = net.JoinHostPort("172.18.0.4", "43120")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response.Code
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

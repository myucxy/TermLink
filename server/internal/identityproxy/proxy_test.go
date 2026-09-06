package identityproxy

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
)

func TestProxyPreservesMutationOnceAndSetsPublicForwardingHeaders(t *testing.T) {
	var requests atomic.Int32
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		body, _ := io.ReadAll(r.Body)
		if r.Method != http.MethodPost || r.URL.Path != "/realms/termlink/protocol/openid-connect/token" || string(body) != "grant=test" {
			t.Errorf("backend request method=%s path=%s body=%q", r.Method, r.URL.Path, body)
		}
		if r.Header.Get("X-Forwarded-Proto") != "https" || r.Header.Get("X-Forwarded-Host") != "public.example:8910" || r.Header.Get("X-Forwarded-Port") != "8910" {
			t.Errorf("forwarded headers=%q/%q/%q", r.Header.Get("X-Forwarded-Proto"), r.Header.Get("X-Forwarded-Host"), r.Header.Get("X-Forwarded-Port"))
		}
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer backend.Close()
	proxy, err := New("https://public.example:8910/realms/termlink", backend.URL+"/realms/termlink")
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodPost, "http://server/realms/termlink/protocol/openid-connect/token", strings.NewReader("grant=test"))
	request.Header.Set("X-Forwarded-Host", "attacker.example")
	response := httptest.NewRecorder()
	proxy.Handler().ServeHTTP(response, request)
	if response.Code != http.StatusServiceUnavailable || requests.Load() != 1 {
		t.Fatalf("response=%d backend requests=%d", response.Code, requests.Load())
	}
}

func TestProxyRewritesBackchannelRedirectToPublicOrigin(t *testing.T) {
	var backendURL string
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Location", backendURL+"/realms/termlink/login-actions/authenticate?code=one")
		w.WriteHeader(http.StatusFound)
	}))
	defer backend.Close()
	backendURL = backend.URL
	proxy, err := New("https://public.example/realms/termlink", backend.URL+"/realms/termlink")
	if err != nil {
		t.Fatal(err)
	}
	response := httptest.NewRecorder()
	proxy.Handler().ServeHTTP(response, httptest.NewRequest(http.MethodGet, "http://server/realms/termlink/account", nil))
	if got := response.Header().Get("Location"); got != "https://public.example/realms/termlink/login-actions/authenticate?code=one" {
		t.Fatalf("Location=%q", got)
	}
}

package observability

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
)

func TestHTTPMetricsUseRouteTemplatesWithoutRawIdentifiers(t *testing.T) {
	metrics := New(nil)
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/sessions/{id}", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	})

	identifier := "ec18c575-8289-41e0-a646-a246d73c463e"
	request := httptest.NewRequest(http.MethodGet, "/v1/sessions/"+identifier, nil)
	response := httptest.NewRecorder()
	metrics.HTTPMiddleware(mux).ServeHTTP(response, request)
	if response.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want %d", response.Code, http.StatusNoContent)
	}

	body := scrape(t, metrics)
	if strings.Contains(body, identifier) {
		t.Fatalf("metrics leaked raw route identifier: %s", body)
	}
	if !strings.Contains(body, `termlink_http_requests_total{method="GET",route="GET /v1/sessions/{id}",status_class="2xx"} 1`) {
		t.Fatalf("route-template counter missing: %s", body)
	}
}

func TestRealtimeMetricsHaveBoundedLabels(t *testing.T) {
	metrics := New(nil)
	metrics.ConnectionOpened("pc")
	metrics.ConnectionOpened("attacker-role")
	metrics.ConnectionClosed("attacker-role")
	metrics.SubscriptionsChanged(2)
	metrics.EnvelopeRouted("pc_to_android", "20", "accepted", 128, 2*time.Millisecond)
	metrics.EnvelopeRouted("unbounded-direction", strings.Repeat("9", 200), "unexpected", 64, time.Millisecond)
	metrics.SlowConsumerDisconnected()

	body := scrape(t, metrics)
	for _, expected := range []string{
		`termlink_realtime_connections{role="pc"} 1`,
		`termlink_realtime_connections{role="unknown"} 0`,
		`termlink_realtime_subscriptions 2`,
		`termlink_realtime_envelopes_total{direction="pc_to_android",message_type="20",result="accepted"} 1`,
		`termlink_realtime_envelopes_total{direction="unknown",message_type="unknown",result="error"} 1`,
		`termlink_realtime_slow_consumer_disconnects_total 1`,
	} {
		if !strings.Contains(body, expected) {
			t.Fatalf("metric %q missing: %s", expected, body)
		}
	}
}

func TestRegistrationHTTPMetricsUseBoundedOperations(t *testing.T) {
	metrics := New(nil)
	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/auth/register", func(w http.ResponseWriter, _ *http.Request) {
		http.Error(w, "denied", http.StatusConflict)
	})
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/register", nil)
	response := httptest.NewRecorder()
	metrics.HTTPMiddleware(mux).ServeHTTP(response, request)

	body := scrape(t, metrics)
	if !strings.Contains(body, `termlink_registration_http_operations_total{operation="registration",result="denied"} 1`) {
		t.Fatalf("bounded registration operation counter missing: %s", body)
	}
	if boundedRegistrationOperation("POST /v1/auth/register/attacker-controlled") != "" {
		t.Fatal("attacker-controlled route unexpectedly created a registration label")
	}
}

func TestRegistrationRouteOperationsUseFixedLabels(t *testing.T) {
	tests := map[string]string{
		"POST /v1/auth/register":                               "registration",
		"POST /v1/auth/invitations/validate":                   "invitation_validation",
		"PATCH /v1/admin/registration-policy":                  "registration_policy_admin",
		"POST /v1/admin/invitation-batches/{batchId}/generate": "invitation_generate",
	}
	for route, want := range tests {
		if got := boundedRegistrationOperation(route); got != want {
			t.Fatalf("boundedRegistrationOperation(%q) = %q, want %q", route, got, want)
		}
	}
	for route, operation := range registrationRouteOperations {
		if len(route) > 160 || strings.ContainsAny(route, "\r\n") || operation == "" || len(operation) > 40 {
			t.Fatalf("unbounded registration route mapping %q -> %q", route, operation)
		}
		for _, character := range operation {
			if character != '_' && (character < 'a' || character > 'z') {
				t.Fatalf("registration operation has unsafe label character: %q", operation)
			}
		}
	}
}

func TestMetricsHandlerPublishesOnlyMetricsPath(t *testing.T) {
	metrics := New(nil)
	request := httptest.NewRequest(http.MethodGet, "/debug/pprof", nil)
	response := httptest.NewRecorder()
	metrics.Handler().ServeHTTP(response, request)
	if response.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", response.Code)
	}
}

func TestHTTPMiddlewarePreservesWebSocketUpgrade(t *testing.T) {
	metrics := New(nil)
	upgrader := websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /socket", func(w http.ResponseWriter, r *http.Request) {
		connection, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			t.Errorf("Upgrade() error = %v", err)
			return
		}
		_ = connection.Close()
	})
	server := httptest.NewServer(metrics.HTTPMiddleware(mux))
	defer server.Close()

	connection, response, err := websocket.DefaultDialer.Dial("ws"+strings.TrimPrefix(server.URL, "http")+"/socket", nil)
	if response != nil {
		defer response.Body.Close()
	}
	if err != nil {
		t.Fatalf("Dial() error = %v", err)
	}
	_ = connection.Close()

	body := scrape(t, metrics)
	if !strings.Contains(body, `termlink_http_requests_total{method="GET",route="GET /socket",status_class="1xx"} 1`) {
		t.Fatalf("websocket upgrade counter missing: %s", body)
	}
}

func scrape(t *testing.T, metrics *Metrics) string {
	t.Helper()
	server := httptest.NewServer(metrics.Handler())
	defer server.Close()
	response, err := http.Get(server.URL + "/metrics")
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	return string(body)
}

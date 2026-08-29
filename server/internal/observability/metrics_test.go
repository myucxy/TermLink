package observability

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
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

func TestCommerceHTTPMetricsUseBoundedOperations(t *testing.T) {
	metrics := New(nil)
	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/billing/orders", func(w http.ResponseWriter, _ *http.Request) {
		http.Error(w, "denied", http.StatusConflict)
	})
	request := httptest.NewRequest(http.MethodPost, "/v1/billing/orders", nil)
	response := httptest.NewRecorder()
	metrics.HTTPMiddleware(mux).ServeHTTP(response, request)

	body := scrape(t, metrics)
	if !strings.Contains(body, `termlink_commerce_http_operations_total{operation="order_create",result="denied"} 1`) {
		t.Fatalf("bounded commerce operation counter missing: %s", body)
	}
	if boundedCommerceOperation("POST /v1/billing/orders/attacker-controlled") != "" {
		t.Fatal("attacker-controlled route unexpectedly created a commerce label")
	}
}

func TestCommerceRouteOperationsCoverLifecycleWithFixedLabels(t *testing.T) {
	tests := map[string]string{
		"POST /v1/auth/register":                                    "registration",
		"POST /v1/auth/invitations/validate":                        "invitation_validation",
		"POST /v1/billing/redemptions/claim":                        "redemption_claim",
		"POST /v1/billing/orders":                                   "order_create",
		"POST /v1/billing/orders/{orderId}/pay":                     "payment_start",
		"POST /v1/billing/webhooks/alipay":                          "webhook_alipay",
		"POST /v1/admin/billing/orders/{orderId}/refunds":           "refund_create",
		"POST /v1/admin/billing/orders/{orderId}/retry-fulfillment": "fulfillment_recovery",
		"POST /v1/billing/orders/{orderId}/query":                   "payment_recovery",
		"POST /v1/admin/billing/reconciliation-runs":                "reconciliation_create",
		"GET /v1/account/entitlements":                              "entitlement_read",
	}
	for route, want := range tests {
		if got := boundedCommerceOperation(route); got != want {
			t.Fatalf("boundedCommerceOperation(%q) = %q, want %q", route, got, want)
		}
	}
	for route, operation := range commerceRouteOperations {
		if len(route) > 160 || strings.ContainsAny(route, "\r\n") || operation == "" || len(operation) > 40 {
			t.Fatalf("unbounded commerce route mapping %q -> %q", route, operation)
		}
		for _, character := range operation {
			if character != '_' && (character < 'a' || character > 'z') {
				t.Fatalf("commerce operation has unsafe label character: %q", operation)
			}
		}
	}
}

func TestCommerceMetricsNeverUseRequestValuesAsLabels(t *testing.T) {
	metrics := New(nil)
	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/billing/orders/{orderId}/query", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	})
	secretValues := []string{
		"ec18c575-8289-41e0-a646-a246d73c463e",
		"user-sensitive@example.test",
		"TLR-SENSITIVE-CODE-VALUE",
		"external-provider-trade-number",
		"docker-secret://merchant-private-key",
	}
	target := "/v1/billing/orders/" + secretValues[0] + "/query?email=" + secretValues[1] + "&code=" + secretValues[2]
	request := httptest.NewRequest(http.MethodPost, target, strings.NewReader(strings.Join(secretValues, "\n")))
	request.Header.Set("X-Provider-Trade-No", secretValues[3])
	response := httptest.NewRecorder()
	metrics.HTTPMiddleware(mux).ServeHTTP(response, request)

	body := scrape(t, metrics)
	for _, secret := range secretValues {
		if strings.Contains(body, secret) {
			t.Fatalf("metrics leaked request-controlled or secret value %q", secret)
		}
	}
	if !strings.Contains(body, `termlink_commerce_http_operations_total{operation="payment_recovery",result="error"} 1`) {
		t.Fatalf("payment recovery metric missing: %s", body)
	}
}

func TestCommerceSnapshotLabelsAndQueriesAreBounded(t *testing.T) {
	metrics := New(nil)
	body := scrape(t, metrics)
	for workflow, states := range commerceWorkflowStates {
		for _, state := range states {
			expected := `termlink_commerce_workflow_items{state="` + state + `",workflow="` + workflow + `"} 0`
			if !strings.Contains(body, expected) {
				t.Fatalf("initialized workflow metric missing %q", expected)
			}
			if !validCommerceWorkflowState(workflow, state) {
				t.Fatalf("declared workflow state rejected: %s/%s", workflow, state)
			}
		}
	}
	if validCommerceWorkflowState("order", "attacker-state") || validCommerceConsistencyCheck("account-uuid") {
		t.Fatal("unbounded commercial snapshot label was accepted")
	}
	for _, check := range commerceConsistencyChecks {
		expected := `termlink_commerce_consistency_issues{check="` + check + `"} 0`
		if !strings.Contains(body, expected) {
			t.Fatalf("initialized consistency metric missing %q", expected)
		}
	}
	queries := strings.ToLower(commerceWorkflowSnapshotSQL + commerceConsistencySnapshotSQL)
	for _, forbidden := range []string{"email_hash", "code_hash", "secret_refs", "payment_payload", "provider_trade_no_hash", "notification_id_hash", "metadata"} {
		if strings.Contains(queries, forbidden) {
			t.Fatalf("commerce snapshot query reads sensitive column %q", forbidden)
		}
	}
}

func TestCommerceAlertAndDashboardContracts(t *testing.T) {
	root := filepath.Join("..", "..", "observability")
	rules, err := os.ReadFile(filepath.Join(root, "prometheus", "rules", "termlink.rules.yml"))
	if err != nil {
		t.Fatal(err)
	}
	ruleText := string(rules)
	for _, expected := range []string{
		"TermlinkCommerceSnapshotUnavailable",
		"TermlinkCommerceOperationErrorRatioHigh",
		"TermlinkRegistrationFailureRatioHigh",
		"TermlinkInvitationValidationDenialsHigh",
		"TermlinkRedemptionFailuresHigh",
		"TermlinkPaymentWebhookErrors",
		"TermlinkPaymentOrOrderErrors",
		"TermlinkRefundOrReconciliationErrors",
		"TermlinkWebhookRetryBacklog",
		"TermlinkRefundBacklog",
		"TermlinkReconciliationBacklog",
		"TermlinkCommerceConsistencyIssue",
	} {
		if !strings.Contains(ruleText, "alert: "+expected) {
			t.Fatalf("commerce alert %q missing", expected)
		}
	}
	for _, metric := range []string{
		"termlink_commerce_http_operations_total",
		"termlink_commerce_workflow_items",
		"termlink_commerce_consistency_issues",
		"termlink_commerce_snapshot_up",
	} {
		if !strings.Contains(ruleText, metric) {
			t.Fatalf("commerce alert rules do not reference %q", metric)
		}
	}
	for _, forbidden := range []string{"email_hash", "code_hash", "secret_refs", "payment_payload", "provider_trade_no", "notification_id", "order_no", "account_id"} {
		if strings.Contains(strings.ToLower(ruleText), forbidden) {
			t.Fatalf("alert rules contain sensitive or high-cardinality field %q", forbidden)
		}
	}

	dashboardBytes, err := os.ReadFile(filepath.Join(root, "grafana", "dashboards", "termlink-overview.json"))
	if err != nil {
		t.Fatal(err)
	}
	var dashboard struct {
		Panels []struct {
			Title   string `json:"title"`
			Targets []struct {
				Expression string `json:"expr"`
			} `json:"targets"`
		} `json:"panels"`
	}
	if err := json.Unmarshal(dashboardBytes, &dashboard); err != nil {
		t.Fatalf("dashboard is not valid JSON: %v", err)
	}
	panels := make(map[string]string)
	for _, panel := range dashboard.Panels {
		for _, target := range panel.Targets {
			panels[panel.Title] += target.Expression
		}
	}
	for _, title := range []string{
		"Commerce operation rate",
		"Commerce server-error ratio",
		"Commerce workflow state",
		"Commerce consistency issues",
		"Commerce snapshot health",
	} {
		if panels[title] == "" {
			t.Fatalf("dashboard panel %q missing or has no query", title)
		}
	}
	joinedQueries := strings.ToLower(strings.Join(mapValues(panels), "\n"))
	for _, forbidden := range []string{"email", "code_hash", "secret", "uuid", "trade_no", "order_no", "account_id"} {
		if strings.Contains(joinedQueries, forbidden) {
			t.Fatalf("dashboard query contains sensitive or high-cardinality field %q", forbidden)
		}
	}
}

func mapValues(values map[string]string) []string {
	result := make([]string, 0, len(values))
	for _, value := range values {
		result = append(result, value)
	}
	return result
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

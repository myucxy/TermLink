package api

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type memoryCommerceReportingStore struct {
	draft                 *store.CommerceCatalogDraft
	reportPage            store.CommerceOrderReportPage
	exportResult          store.CommerceOrderExport
	consistencyPage       store.CommerceConsistencyPage
	draftCalls            int
	reportCalls           int
	exportCalls           int
	consistencyCalls      int
	lastActor             uuid.UUID
	lastReportFilter      store.CommerceOrderReportFilter
	lastExportFilter      store.CommerceOrderExportFilter
	lastConsistencyFilter store.CommerceConsistencyFilter
}

func (repository *memoryCommerceReportingStore) GetCommerceCatalogDraft(_ context.Context, actorID uuid.UUID) (*store.CommerceCatalogDraft, error) {
	repository.draftCalls++
	repository.lastActor = actorID
	return repository.draft, nil
}

func (repository *memoryCommerceReportingStore) ListCommerceOrderReport(_ context.Context, actorID uuid.UUID, filter store.CommerceOrderReportFilter) (store.CommerceOrderReportPage, error) {
	repository.reportCalls++
	repository.lastActor = actorID
	repository.lastReportFilter = filter
	return repository.reportPage, nil
}

func (repository *memoryCommerceReportingStore) ExportCommerceOrders(_ context.Context, actorID uuid.UUID, filter store.CommerceOrderExportFilter) (store.CommerceOrderExport, error) {
	repository.exportCalls++
	repository.lastActor = actorID
	repository.lastExportFilter = filter
	return repository.exportResult, nil
}

func (repository *memoryCommerceReportingStore) ListCommerceConsistencyDetails(_ context.Context, actorID uuid.UUID, filter store.CommerceConsistencyFilter) (store.CommerceConsistencyPage, error) {
	repository.consistencyCalls++
	repository.lastActor = actorID
	repository.lastConsistencyFilter = filter
	return repository.consistencyPage, nil
}

func TestCommerceOperationsRoutesRequireAuthentication(t *testing.T) {
	handler := (&API{}).Handler()
	for _, test := range []struct {
		method string
		path   string
		body   string
	}{
		{http.MethodGet, "/v1/admin/billing/catalog/draft", ""},
		{http.MethodGet, "/v1/admin/billing/order-report", ""},
		{http.MethodPost, "/v1/admin/billing/order-export", `{}`},
		{http.MethodGet, "/v1/admin/billing/consistency", ""},
	} {
		request := httptest.NewRequest(test.method, test.path, strings.NewReader(test.body))
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusUnauthorized {
			t.Fatalf("%s %s status = %d, want 401", test.method, test.path, response.Code)
		}
	}
}

func TestCommerceReadOperationsDelegateDatabaseRoleBoundaryWithoutMFA(t *testing.T) {
	now := time.Date(2026, time.August, 27, 8, 0, 0, 0, time.UTC)
	accountID, deviceID := uuid.New(), uuid.New()
	repository := &memoryCommerceReportingStore{draft: &store.CommerceCatalogDraft{ID: uuid.New(), Version: 3}}
	service := &API{commerceReporting: repository}
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	authentication.Tokens = commerceIdentityVerifier{identity: auth.Identity{AuthenticatedAt: now.Add(-24 * time.Hour), MFA: false}}

	tests := []struct {
		path    string
		handler http.HandlerFunc
		calls   func() int
	}{
		{"/v1/admin/billing/catalog/draft", service.getCommerceCatalogDraft, func() int { return repository.draftCalls }},
		{"/v1/admin/billing/order-report", service.listCommerceOrderReport, func() int { return repository.reportCalls }},
		{"/v1/admin/billing/consistency", service.listCommerceConsistency, func() int { return repository.consistencyCalls }},
	}
	for _, test := range tests {
		handler := traceMiddleware(authentication.Authenticate(test.handler))
		request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodGet, test.path, nil)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK || test.calls() != 1 || repository.lastActor != accountID {
			t.Fatalf("%s status/calls/actor = %d/%d/%s", test.path, response.Code, test.calls(), repository.lastActor)
		}
	}
}

func TestCommerceOrderExportRequiresAdminRecentMFAAndReason(t *testing.T) {
	now := time.Date(2026, time.August, 27, 8, 0, 0, 0, time.UTC)
	body := []byte(`{"created_from":"2026-08-01T00:00:00Z","created_to":"2026-08-20T00:00:00Z","limit":10}`)
	tests := []struct {
		name       string
		admin      bool
		identity   auth.Identity
		reason     string
		wantStatus int
		wantCode   string
	}{
		{"database role", false, auth.Identity{MFA: true, AuthenticatedAt: now}, "Export orders for finance review", http.StatusForbidden, "ADMIN_REQUIRED"},
		{"recent MFA", true, auth.Identity{MFA: false, AuthenticatedAt: now}, "Export orders for finance review", http.StatusForbidden, "STEP_UP_REQUIRED"},
		{"operation reason", true, auth.Identity{MFA: true, AuthenticatedAt: now}, "", http.StatusBadRequest, "OPERATION_REASON_REQUIRED"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			accountID, deviceID := uuid.New(), uuid.New()
			security := &memoryCommerceSecurityStore{admin: test.admin}
			reporting := &memoryCommerceReportingStore{}
			service := &API{Clock: func() time.Time { return now }, commerceSecurity: security, commerceReporting: reporting}
			authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
			authentication.Tokens = commerceIdentityVerifier{identity: test.identity}
			handler := traceMiddleware(authentication.Authenticate(http.HandlerFunc(service.exportCommerceOrders)))
			request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, "/v1/admin/billing/order-export", body)
			request.Header.Set("Idempotency-Key", "order-export-test-key")
			if test.reason != "" {
				request.Header.Set(commerceOperationReasonHeader, test.reason)
			}
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != test.wantStatus || !strings.Contains(response.Body.String(), `"code":"`+test.wantCode+`"`) {
				t.Fatalf("status/body = %d/%s, want %d/%s", response.Code, response.Body.String(), test.wantStatus, test.wantCode)
			}
			if reporting.exportCalls != 0 {
				t.Fatal("rejected export reached reporting store")
			}
		})
	}
}

func TestCommerceOrderExportWritesSanitizedSuccessAudit(t *testing.T) {
	now := time.Date(2026, time.August, 27, 8, 0, 0, 0, time.UTC)
	accountID, deviceID := uuid.New(), uuid.New()
	security := &memoryCommerceSecurityStore{admin: true}
	reporting := &memoryCommerceReportingStore{
		exportResult: store.CommerceOrderExport{
			Rows: []store.CommerceOrderExportRow{{
				OrderNo: "TL-ORDER-SAFE-0001", AccountID: accountID, PlanKey: "pro", CatalogVersion: 5,
				Provider: "ALIPAY", PaymentScene: "WEB", AmountMinor: 1200, Currency: "CNY", Status: "PAID", CreatedAt: now.Add(-time.Hour),
			}},
		},
	}
	service := &API{
		Clock: func() time.Time { return now }, commerceSecurity: security, commerceReporting: reporting,
		CommerceKey: []byte("commerce-export-audit-key-0123456789"),
	}
	authentication, privateKey := fileOperationTestAuth(t, accountID, deviceID, "ANDROID", now)
	authentication.Tokens = commerceIdentityVerifier{identity: auth.Identity{MFA: true, AuthenticatedAt: now}}
	handler := traceMiddleware(authentication.Authenticate(http.HandlerFunc(service.exportCommerceOrders)))
	body := []byte(`{"created_from":"2026-08-01T00:00:00Z","created_to":"2026-08-20T00:00:00Z","limit":10}`)
	request := signedFileOperationRequest(t, privateKey, deviceID, now, http.MethodPost, "/v1/admin/billing/order-export", body)
	reason := "Export paid orders for monthly finance review"
	rawKey := "raw-order-export-idempotency-key"
	request.Header.Set(commerceOperationReasonHeader, reason)
	request.Header.Set("Idempotency-Key", rawKey)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK || reporting.exportCalls != 1 || len(security.audits) != 1 {
		t.Fatalf("status/export calls/audits = %d/%d/%d, body=%s", response.Code, reporting.exportCalls, len(security.audits), response.Body.String())
	}
	if disposition := response.Header().Get("Content-Disposition"); disposition != `attachment; filename="termlink-order-export.json"` {
		t.Fatalf("unexpected content disposition %q", disposition)
	}
	audit := security.audits[0]
	if audit.action != "commerce.order.export" || audit.result != "SUCCEEDED" || strings.Contains(audit.metadata, rawKey) {
		t.Fatalf("unexpected export audit: %+v", audit)
	}
	var metadata map[string]string
	if err := json.Unmarshal([]byte(audit.metadata), &metadata); err != nil {
		t.Fatal(err)
	}
	if metadata["operation_reason"] != reason || metadata["idempotency_key_hash"] != service.commerceIdempotencyAuditHash(rawKey) {
		t.Fatalf("unexpected audit metadata: %#v", metadata)
	}
}

func TestCommerceOrderReportFilterRejectsMalformedBoundsAndCursor(t *testing.T) {
	validFrom := url.QueryEscape("2026-08-01T00:00:00Z")
	validTo := url.QueryEscape("2026-08-20T00:00:00Z")
	tests := []string{
		"account_id=not-a-uuid",
		"created_from=" + validFrom,
		"created_from=" + validTo + "&created_to=" + validFrom,
		"after_created_at=" + validFrom,
		"after_id=" + uuid.NewString(),
		"limit=201",
	}
	for _, query := range tests {
		request := httptest.NewRequest(http.MethodGet, "/v1/admin/billing/order-report?"+query, nil)
		if _, err := commerceOrderReportFilter(request); err == nil {
			t.Fatalf("malformed report query accepted: %s", query)
		}
	}

	request := httptest.NewRequest(http.MethodGet, "/v1/admin/billing/order-report?created_from="+validFrom+"&created_to="+validTo+"&after_created_at="+validFrom+"&after_id="+uuid.NewString()+"&limit=200", nil)
	filter, err := commerceOrderReportFilter(request)
	if err != nil || filter.CreatedFrom == nil || filter.CreatedTo == nil || filter.AfterCreatedAt == nil || filter.AfterID == nil || filter.Limit != 200 {
		t.Fatalf("valid report filter rejected or lost fields: %+v, err=%v", filter, err)
	}
}

func TestCommerceConsistencyFilterRequiresCompleteCursorAndBoundedRange(t *testing.T) {
	from := url.QueryEscape("2026-08-01T00:00:00Z")
	to := url.QueryEscape("2026-08-20T00:00:00Z")
	entityID := uuid.NewString()
	for _, query := range []string{
		"created_to=" + to,
		"after_occurred_at=" + from + "&after_entity_id=" + entityID,
		"after_occurred_at=" + from + "&after_category=COMPLETED_ORDER_WITHOUT_GRANT",
		"after_entity_id=" + entityID + "&after_category=COMPLETED_ORDER_WITHOUT_GRANT",
		"limit=201",
	} {
		request := httptest.NewRequest(http.MethodGet, "/v1/admin/billing/consistency?"+query, nil)
		if _, err := commerceConsistencyFilter(request); err == nil {
			t.Fatalf("malformed consistency query accepted: %s", query)
		}
	}
	request := httptest.NewRequest(http.MethodGet, "/v1/admin/billing/consistency?created_from="+from+"&created_to="+to+"&after_occurred_at="+from+"&after_entity_id="+entityID+"&after_category=COMPLETED_ORDER_WITHOUT_GRANT", nil)
	filter, err := commerceConsistencyFilter(request)
	if err != nil || filter.After == nil || filter.After.EntityID.String() != entityID {
		t.Fatalf("valid consistency filter rejected: %+v, err=%v", filter, err)
	}
}

func TestCommerceOrderExportRequestRejectsUnsafeBounds(t *testing.T) {
	from := time.Date(2026, time.August, 1, 0, 0, 0, 0, time.UTC)
	to := from.Add(32 * 24 * time.Hour)
	zeroID := uuid.Nil
	for _, input := range []commerceOrderExportRequest{
		{CreatedFrom: &from},
		{CreatedFrom: &to, CreatedTo: &from},
		{CreatedFrom: &from, CreatedTo: &to},
		{AccountID: &zeroID},
		{Limit: 1001},
		{Limit: -1},
	} {
		if err := validateCommerceOrderExportRequest(input); err == nil {
			t.Fatalf("unsafe export filter accepted: %+v", input)
		}
	}
	to = from.Add(24 * time.Hour)
	if err := validateCommerceOrderExportRequest(commerceOrderExportRequest{CreatedFrom: &from, CreatedTo: &to, Limit: 1000}); err != nil {
		t.Fatalf("valid export filter rejected: %v", err)
	}
}

func TestCommerceOrderExportViewOmitsInternalPaymentMaterial(t *testing.T) {
	encoded, err := json.Marshal(commerceOrderExportView(store.CommerceOrderExport{Rows: []store.CommerceOrderExportRow{{
		OrderNo: "TL-ORDER-SAFE-0001", AccountID: uuid.New(), PlanKey: "pro", Provider: "ALIPAY", Currency: "CNY",
	}}}))
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"idempotency_key", "payment_payload", "provider_trade_no", "secret", "result_summary"} {
		if bytes.Contains(bytes.ToLower(encoded), []byte(forbidden)) {
			t.Fatalf("order export exposed %q: %s", forbidden, encoded)
		}
	}
}

func TestSafeReconciliationSummaryUsesWhitelistAndRejectsInvalidState(t *testing.T) {
	valid := json.RawMessage(`{
		"schema_version":1,"provider":"ALIPAY","period_start":"2026-08-01T00:00:00Z",
		"period_end":"2026-08-02T00:00:00Z","result":"BALANCED","balanced":true,
		"provider_entries":2,"local_entries":2,"matched_entries":2,
		"differences":{"provider_only":0,"local_only":0,"amount_mismatch":0,"currency_mismatch":0,"status_mismatch":0,"transaction_mismatch":0,"duplicate_provider":0,"duplicate_local":0},
		"completed_at":"2026-08-02T00:01:00Z","failure_code":"",
		"secret":"must-not-escape","provider_trade_no":"must-not-escape","attacker":{"nested":true}
	}`)
	encoded, err := json.Marshal(safeReconciliationSummaryView(valid))
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"must-not-escape", "provider_trade_no", "attacker", "secret"} {
		if bytes.Contains(encoded, []byte(forbidden)) {
			t.Fatalf("summary whitelist exposed %q: %s", forbidden, encoded)
		}
	}
	if bytes.Contains(encoded, []byte(`"available":false`)) {
		t.Fatalf("valid summary was rejected: %s", encoded)
	}

	invalid := []json.RawMessage{
		json.RawMessage(strings.Replace(string(valid), `"schema_version":1`, `"schema_version":2`, 1)),
		json.RawMessage(strings.Replace(string(valid), `"provider_entries":2`, `"provider_entries":-1`, 1)),
		json.RawMessage(strings.Replace(string(valid), `"period_end":"2026-08-02T00:00:00Z"`, `"period_end":"2026-07-31T00:00:00Z"`, 1)),
		json.RawMessage(strings.Replace(string(valid), `"result":"BALANCED"`, `"result":"FAILED"`, 1)),
	}
	for _, raw := range invalid {
		encoded, err := json.Marshal(safeReconciliationSummaryView(raw))
		if err != nil {
			t.Fatal(err)
		}
		if string(encoded) != `{"available":false}` {
			t.Fatalf("invalid summary was exposed: %s", encoded)
		}
	}
}

func TestCommerceConsistencyRepairOnlyExposesImplementedSafeActions(t *testing.T) {
	orderID := uuid.New()
	tests := []struct {
		item      store.CommerceConsistencyDetail
		operation string
	}{
		{store.CommerceConsistencyDetail{Category: "ORDER_WITHOUT_COMPATIBLE_TRANSACTION", OrderID: &orderID}, "QUERY_PROVIDER"},
		{store.CommerceConsistencyDetail{Category: "SETTLED_TRANSACTION_ORDER_MISMATCH", OrderID: &orderID}, "QUERY_PROVIDER"},
		{store.CommerceConsistencyDetail{Category: "COMPLETED_ORDER_WITHOUT_GRANT", OrderID: &orderID, OrderStatus: "FULFILLED"}, "RETRY_FULFILLMENT"},
		{store.CommerceConsistencyDetail{Category: "COMPLETED_ORDER_WITHOUT_GRANT", OrderID: &orderID, OrderStatus: "PAID"}, ""},
		{store.CommerceConsistencyDetail{Category: "REFUNDED_ORDER_WITH_LIVE_GRANT", OrderID: &orderID}, ""},
		{store.CommerceConsistencyDetail{Category: "MATERIALIZED_ENTITLEMENT_MISMATCH", OrderID: &orderID}, ""},
	}
	for _, test := range tests {
		view := commerceConsistencyRepairView(test.item)
		if test.operation == "" {
			if view != nil {
				t.Fatalf("unsupported consistency repair exposed for %s: %#v", test.item.Category, view)
			}
			continue
		}
		encoded, err := json.Marshal(view)
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Contains(encoded, []byte(`"operation":"`+test.operation+`"`)) || !bytes.Contains(encoded, []byte(orderID.String())) {
			t.Fatalf("safe repair action missing: %s", encoded)
		}
	}
}

func TestPlanDraftWriteRoutesUseCommerceHighRiskWrapper(t *testing.T) {
	source, err := os.ReadFile("api.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, route := range []string{"POST /v1/admin/billing/plans", "PUT /v1/admin/billing/plans/{id}"} {
		lineStart := bytes.Index(source, []byte(`trusted.Handle("`+route+`"`))
		if lineStart < 0 {
			t.Fatalf("plan draft route is missing: %s", route)
		}
		lineEnd := bytes.IndexByte(source[lineStart:], '\n')
		if lineEnd < 0 || !bytes.Contains(source[lineStart:lineStart+lineEnd], []byte("commerceHighRiskOperation(")) {
			t.Fatalf("plan draft route lacks commerce high-risk wrapper: %s", route)
		}
	}
}

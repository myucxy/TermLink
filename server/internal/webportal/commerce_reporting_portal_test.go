package webportal

import (
	"context"
	"encoding/base64"
	"encoding/csv"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type testCommerceReportingRepository struct {
	*testPortalRepository
	draft             *store.CommerceCatalogDraft
	orderPage         store.CommerceOrderReportPage
	export            store.CommerceOrderExport
	consistency       store.CommerceConsistencyPage
	orderFilter       store.CommerceOrderReportFilter
	exportFilter      store.CommerceOrderExportFilter
	consistencyFilter store.CommerceConsistencyFilter
	orderCalls        int
	exportCalls       int
	consistencyCalls  int
	reportErr         error
	auditCalls        []testCommerceExportAuditCall
	auditErr          error
}

type testCommerceExportAuditCall struct {
	userID     uuid.UUID
	actorID    uuid.UUID
	action     string
	targetType string
	targetID   uuid.UUID
	result     string
	traceID    string
	metadata   string
}

func (r *testCommerceReportingRepository) GetCommerceCatalogDraft(context.Context, uuid.UUID) (*store.CommerceCatalogDraft, error) {
	return r.draft, r.reportErr
}

func (r *testCommerceReportingRepository) ListCommerceOrderReport(_ context.Context, _ uuid.UUID, filter store.CommerceOrderReportFilter) (store.CommerceOrderReportPage, error) {
	r.orderCalls++
	r.orderFilter = filter
	return r.orderPage, r.reportErr
}

func (r *testCommerceReportingRepository) ExportCommerceOrders(_ context.Context, _ uuid.UUID, filter store.CommerceOrderExportFilter) (store.CommerceOrderExport, error) {
	r.exportCalls++
	r.exportFilter = filter
	return r.export, r.reportErr
}

func (r *testCommerceReportingRepository) ListCommerceConsistencyDetails(_ context.Context, _ uuid.UUID, filter store.CommerceConsistencyFilter) (store.CommerceConsistencyPage, error) {
	r.consistencyCalls++
	r.consistencyFilter = filter
	return r.consistency, r.reportErr
}

func (r *testCommerceReportingRepository) Audit(_ context.Context, userID, actorID uuid.UUID, action, targetType string, targetID uuid.UUID, result, traceID, metadata string) error {
	r.auditCalls = append(r.auditCalls, testCommerceExportAuditCall{
		userID: userID, actorID: actorID, action: action, targetType: targetType,
		targetID: targetID, result: result, traceID: traceID, metadata: metadata,
	})
	return r.auditErr
}

func newTestCommerceReportingRepository(role store.Role) *testCommerceReportingRepository {
	repository := &testCommerceReportingRepository{testPortalRepository: newTestPortalRepository()}
	repository.account.Role = role
	return repository
}

func TestAdminCommerceCatalogUsesReadOnlySafeView(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	repository.draft = &store.CommerceCatalogDraft{
		ID: uuid.MustParse("22222222-2222-4222-8222-222222222222"), Version: 31,
		CreatedAt: portal.now().Add(-time.Hour),
		Plans: []store.PlanDraft{{
			ID: uuid.MustParse("33333333-3333-4333-8333-333333333333"), Key: "pro", DisplayName: "专业版",
			Description: "用于多设备远程连接", BillingPeriod: "MONTHLY", MaxRemotePCs: -1,
			MaxLiveSessions: -1, VisiblePlatforms: []string{"android", "windows"}, PriceMinor: 1200,
			Currency: "CNY", FeatureFlags: json.RawMessage(`{"private":"FEATURE-SECRET-MUST-NOT-RENDER"}`),
			ChannelProducts: json.RawMessage(`{"sku":"CHANNEL-SECRET-MUST-NOT-RENDER"}`),
		}},
		Validation: store.CommerceCatalogValidationSummary{
			Valid: false, PlanCount: 1,
			Issues: []store.CommerceCatalogValidationIssue{{
				Code: store.CommerceCatalogIssueFreePlanRequired, Field: "plan_key",
			}},
		},
	}
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "catalog-csrf", time.Hour)

	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=catalog", nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("catalog status=%d body=%q", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{"待发布套餐目录", "专业版", "缺少 free 基础套餐", "留在服务端"} {
		if !strings.Contains(body, expected) {
			t.Errorf("catalog omitted %q", expected)
		}
	}
	for _, forbidden := range []string{"FEATURE-SECRET-MUST-NOT-RENDER", "CHANNEL-SECRET-MUST-NOT-RENDER"} {
		if strings.Contains(body, forbidden) {
			t.Errorf("catalog exposed %q", forbidden)
		}
	}
	if strings.Contains(body, `action="/admin/commerce/catalog`) {
		t.Fatal("read-only catalog unexpectedly rendered a browser mutation")
	}
}

func TestAdminCommerceOrderReportStrictFiltersAndOpaquePagination(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	createdAt := portal.now().Add(-time.Hour)
	orderID := uuid.MustParse("44444444-4444-4444-8444-444444444444")
	repository.orderPage = store.CommerceOrderReportPage{
		Rows: []store.CommerceOrderReportRow{{
			OrderID: orderID, OrderNo: "TL2026080412345678", AccountID: repository.account.ID,
			PlanKey: "pro", CatalogVersion: 31, Provider: "ALIPAY", PaymentScene: "NATIVE",
			AmountMinor: 1200, Currency: "CNY", Status: "FULFILLED", CreatedAt: createdAt,
		}},
		NextCursor: &store.CommerceOrderReportCursor{CreatedAt: createdAt, OrderID: orderID},
	}
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "report-csrf", time.Hour)
	target := "/admin/commerce?section=reports&provider=ALIPAY&status=FULFILLED&from=2026-08-03T10%3A00&to=2026-08-04T10%3A00&limit=25"

	response := performRequest(portal.Handler(), http.MethodGet, target, nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("report status=%d body=%q", response.Code, response.Body.String())
	}
	if repository.orderCalls != 1 || repository.orderFilter.Provider != "ALIPAY" || repository.orderFilter.Status != "FULFILLED" || repository.orderFilter.Limit != 25 {
		t.Fatalf("unexpected order filter: calls=%d filter=%+v", repository.orderCalls, repository.orderFilter)
	}
	body := response.Body.String()
	if !strings.Contains(body, "TL2026080412345678") || !strings.Contains(body, "cursor=") || !strings.Contains(body, "安全导出 CSV") {
		t.Fatalf("report omitted row, cursor, or export: %q", body)
	}
	if strings.Contains(body, "provider_reference") || strings.Contains(body, "idempotency_key_from_store") {
		t.Fatal("report exposed a forbidden payment field")
	}

	invalid := performRequest(portal.Handler(), http.MethodGet, target+"&unexpected=1", nil, cookie)
	if invalid.Code != http.StatusBadRequest || repository.orderCalls != 1 {
		t.Fatalf("unknown report filter response=%d calls=%d", invalid.Code, repository.orderCalls)
	}
	duplicate := performRequest(portal.Handler(), http.MethodGet, target+"&provider=WECHAT_PAY", nil, cookie)
	if duplicate.Code != http.StatusBadRequest || repository.orderCalls != 1 {
		t.Fatalf("duplicate report filter response=%d calls=%d", duplicate.Code, repository.orderCalls)
	}
}

func TestAdminCommerceConsistencyReportIsReadOnlyAndPaged(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAdmin)
	occurredAt := portal.now().Add(-30 * time.Minute)
	entityID := uuid.MustParse("55555555-5555-4555-8555-555555555555")
	repository.consistency = store.CommerceConsistencyPage{
		Rows: []store.CommerceConsistencyDetail{{
			Category: "MATERIALIZED_ENTITLEMENT_MISMATCH", Severity: "WARNING",
			RecommendedAction: "RECOMPUTE_ENTITLEMENT_FROM_VERIFIED_GRANTS", EntityID: entityID,
			AccountID: repository.account.ID, MaterializedPlan: "free", ExpectedPlan: "pro", OccurredAt: occurredAt,
		}},
		NextCursor: &store.CommerceConsistencyCursor{
			OccurredAt: occurredAt, EntityID: entityID, Category: "MATERIALIZED_ENTITLEMENT_MISMATCH",
		},
	}
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "consistency-csrf", time.Hour)

	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=consistency&category=MATERIALIZED_ENTITLEMENT_MISMATCH&from=2026-08-04T09%3A00&to=2026-08-04T10%3A00&limit=50", nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("consistency status=%d body=%q", response.Code, response.Body.String())
	}
	if repository.consistencyCalls != 1 || repository.consistencyFilter.Category != "MATERIALIZED_ENTITLEMENT_MISMATCH" {
		t.Fatalf("unexpected consistency filter: %+v", repository.consistencyFilter)
	}
	body := response.Body.String()
	for _, expected := range []string{"当前权益与授予记录不一致", "从已验证授予记录重算权益", "cursor="} {
		if !strings.Contains(body, expected) {
			t.Errorf("consistency report omitted %q", expected)
		}
	}
	if strings.Contains(body, "action=\"/admin/commerce/consistency") {
		t.Fatal("consistency report unexpectedly rendered an automatic repair action")
	}
}

func TestAdminCommerceOrderExportRequiresStrongAuditedRequestAndEscapesCSV(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	repository.export = store.CommerceOrderExport{
		Rows: []store.CommerceOrderExportRow{{
			OrderNo: "=HYPERLINK(\"https://invalid\")", AccountID: repository.account.ID,
			PlanKey: " +SUM(1,1)", CatalogVersion: 31, Provider: "ALIPAY", PaymentScene: "NATIVE",
			AmountMinor: 1200, Currency: "CNY", Status: "FULFILLED", CreatedAt: portal.now().Add(-time.Hour),
		}},
		Truncated: true,
	}
	portal.config.Repository = repository
	values := url.Values{
		"csrf": {"export-csrf"}, "reason": {"月度财务复核"}, "idempotency_key": {"order-export-test-0001"},
		"from": {"2026-08-03T10:00"}, "to": {"2026-08-04T10:00"}, "limit": {"1000"},
		"order_no": {""}, "account_id": {""}, "provider": {"ALIPAY"}, "status": {"FULFILLED"},
	}
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "export-csrf", time.Minute)

	response := performCommerceForm(portal.Handler(), "/admin/commerce/orders/export.csv", values, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("export status=%d body=%q", response.Code, response.Body.String())
	}
	if got := response.Header().Get("Content-Type"); got != "text/csv; charset=utf-8" {
		t.Fatalf("export content-type=%q", got)
	}
	if response.Header().Get("Cache-Control") != "no-store" || response.Header().Get("X-Content-Type-Options") != "nosniff" || response.Header().Get("X-TermLink-Export-Truncated") != "true" {
		t.Fatalf("unsafe export headers: %+v", response.Header())
	}
	rows, err := csv.NewReader(strings.NewReader(response.Body.String())).ReadAll()
	if err != nil || len(rows) != 2 {
		t.Fatalf("decode export CSV: rows=%v err=%v", rows, err)
	}
	if rows[1][0] != "'=HYPERLINK(\"https://invalid\")" || rows[1][2] != "' +SUM(1,1)" {
		t.Fatalf("CSV formula cells were not neutralized: %#v", rows[1])
	}
	if repository.exportCalls != 1 || repository.exportFilter.Limit != 1000 || repository.exportFilter.Provider != "ALIPAY" {
		t.Fatalf("unexpected export call/filter: calls=%d filter=%+v", repository.exportCalls, repository.exportFilter)
	}
	if repository.idempotencyDone["order-export-test-0001"] != http.StatusOK {
		t.Fatalf("export idempotency was not completed: %+v", repository.idempotencyDone)
	}
	if len(repository.securityEvents) != 0 {
		t.Fatalf("successful export used failure-only security event storage: %+v", repository.securityEvents)
	}
	if len(repository.auditCalls) != 1 {
		t.Fatalf("export audit calls=%+v", repository.auditCalls)
	}
	audit := repository.auditCalls[0]
	if audit.userID != repository.account.ID || audit.actorID != uuid.Nil || audit.action != "web.commerce.orders.export" ||
		audit.targetType != "commerce_order_export" || audit.targetID != uuid.Nil || audit.result != "SUCCEEDED" || audit.traceID == "" {
		t.Fatalf("export audit=%+v", audit)
	}
	metadata := audit.metadata
	if !strings.Contains(metadata, "月度财务复核") || strings.Contains(metadata, "export-csrf") || strings.Contains(metadata, "order-export-test-0001") {
		t.Fatalf("unsafe export audit metadata=%q", metadata)
	}

	staleCookie := commerceSessionCookie(t, portal, store.RoleAuditor, "export-csrf", sensitiveActionTTL+time.Second)
	stale := performCommerceForm(portal.Handler(), "/admin/commerce/orders/export.csv", values, staleCookie)
	if stale.Code != http.StatusForbidden || repository.exportCalls != 1 {
		t.Fatalf("stale export response=%d calls=%d", stale.Code, repository.exportCalls)
	}
	get := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce/orders/export.csv", nil, cookie)
	if get.Code != http.StatusNotFound && get.Code != http.StatusMethodNotAllowed {
		t.Fatalf("GET export status=%d, want 404/405", get.Code)
	}
}

func TestAdminCommerceOrderExportFailsClosedBeforeCSVWhenAuditFails(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	repository.export = store.CommerceOrderExport{Rows: []store.CommerceOrderExportRow{{
		OrderNo: "TL2026080412345678", AccountID: repository.account.ID, PlanKey: "pro",
		CatalogVersion: 31, Provider: "ALIPAY", PaymentScene: "NATIVE", AmountMinor: 1200,
		Currency: "CNY", Status: "FULFILLED", CreatedAt: portal.now().Add(-time.Hour),
	}}}
	repository.auditErr = errors.New("postgresql://audit-secret@db.internal")
	portal.config.Repository = repository
	values := url.Values{
		"csrf": {"export-csrf"}, "reason": {"月度财务复核"}, "idempotency_key": {"order-export-audit-failure-0001"},
		"from": {"2026-08-03T10:00"}, "to": {"2026-08-04T10:00"}, "limit": {"1000"},
	}
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "export-csrf", time.Minute)

	response := performCommerceForm(portal.Handler(), "/admin/commerce/orders/export.csv", values, cookie)
	if response.Code != http.StatusInternalServerError {
		t.Fatalf("export status=%d body=%q", response.Code, response.Body.String())
	}
	if strings.Contains(response.Body.String(), "TL2026080412345678") ||
		strings.Contains(response.Body.String(), "audit-secret") ||
		response.Header().Get("Content-Type") == "text/csv; charset=utf-8" ||
		response.Header().Get("Content-Disposition") != "" {
		t.Fatalf("failed audit leaked CSV or internal details: headers=%+v body=%q", response.Header(), response.Body.String())
	}
	if len(repository.auditCalls) != 1 || repository.idempotencyDone["order-export-audit-failure-0001"] != http.StatusInternalServerError {
		t.Fatalf("audit failure was not retryable: audits=%+v completions=%+v", repository.auditCalls, repository.idempotencyDone)
	}
}

func TestCommerceReportCursorDecoderRejectsTampering(t *testing.T) {
	validID := uuid.MustParse("66666666-6666-4666-8666-666666666666")
	encoded, err := encodeCommerceCursor(commerceOrderCursorPayload{
		CreatedAt: time.Date(2026, 8, 4, 9, 0, 0, 0, time.UTC).Format(time.RFC3339Nano),
		OrderID:   validID.String(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if cursor, err := decodeCommerceOrderCursor(encoded); err != nil || cursor.OrderID != validID {
		t.Fatalf("valid cursor rejected: %+v %v", cursor, err)
	}
	unknownField := base64RawURL(`{"created_at":"2026-08-04T09:00:00Z","order_id":"` + validID.String() + `","secret":"x"}`)
	for _, value := range []string{"not-base64!", unknownField, strings.Repeat("A", 800)} {
		if _, err := decodeCommerceOrderCursor(value); !errors.Is(err, errCommerceReportFilter) {
			t.Fatalf("tampered cursor %q error=%v", value, err)
		}
	}
}

func base64RawURL(value string) string {
	return base64.RawURLEncoding.EncodeToString([]byte(value))
}

package webportal

import (
	"context"
	"errors"
	"html"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/reconciliation"
	"github.com/termlink/termlink/server/internal/store"
)

type testReconciliationOperations struct {
	page        reconciliation.DiscrepancyPage
	listRequest reconciliation.DiscrepancyListRequest
	transition  reconciliation.DiscrepancyTransitionRequest
	listErr     error
	writeErr    error
	listCalls   int
	writeCalls  int
}

func (o *testReconciliationOperations) List(_ context.Context, request reconciliation.DiscrepancyListRequest) (reconciliation.DiscrepancyPage, error) {
	o.listCalls++
	o.listRequest = request
	return o.page, o.listErr
}

func (o *testReconciliationOperations) Transition(_ context.Context, request reconciliation.DiscrepancyTransitionRequest) (reconciliation.OperationalDiscrepancy, error) {
	o.writeCalls++
	o.transition = request
	return reconciliation.OperationalDiscrepancy{ID: request.DiscrepancyID, Status: request.TargetStatus}, o.writeErr
}

func TestAdminReconciliationListIsBoundedFilteredAndSanitized(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	runID := uuid.MustParse("22222222-2222-4222-8222-222222222222")
	discrepancyID := uuid.MustParse("33333333-3333-4333-8333-333333333333")
	operations := &testReconciliationOperations{page: reconciliation.DiscrepancyPage{Items: []reconciliation.OperationalDiscrepancy{{
		ID: discrepancyID, RunID: runID, Provider: "ALIPAY", Category: "STATUS_MISMATCH",
		SummaryCode: "PAYMENT_STATUS_DIFF", Status: reconciliation.DiscrepancyStatusOpen, Version: 4,
		LocalStatus: textPointer("PAID"), ProviderStatus: textPointer("PENDING"),
		FirstSeenAt: portal.now().Add(-time.Hour), LastSeenAt: portal.now().Add(-time.Minute),
	}}}}
	portal.config.Repository = repository
	portal.config.Reconciliation = operations
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "reconciliation-csrf", time.Hour)
	target := "/admin/commerce?section=reconciliation&provider=ALIPAY&status=OPEN&run_id=" + runID.String() + "&limit=25"
	response := performRequest(portal.Handler(), http.MethodGet, target, nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("status/body=%d/%q", response.Code, response.Body.String())
	}
	if operations.listCalls != 1 || operations.listRequest.ActorID != repository.account.ID ||
		operations.listRequest.Provider != "ALIPAY" || operations.listRequest.Status != "OPEN" ||
		operations.listRequest.RunID != runID || operations.listRequest.Limit != 25 {
		t.Fatalf("list request=%+v calls=%d", operations.listRequest, operations.listCalls)
	}
	body := response.Body.String()
	for _, expected := range []string{"PAYMENT_STATUS_DIFF", "STATUS_MISMATCH", "PAID", "PENDING", "只读"} {
		if !strings.Contains(body, expected) {
			t.Errorf("list omitted %q", expected)
		}
	}
	for _, forbidden := range []string{"discrepancy_key", "external_reference_hmac", "statement_row", "provider_reference"} {
		if strings.Contains(strings.ToLower(body), forbidden) {
			t.Errorf("list exposed forbidden field %q", forbidden)
		}
	}
	if strings.Contains(body, "/transition") {
		t.Fatal("AUDITOR received a discrepancy transition form")
	}
}

func TestAdminReconciliationTransitionUsesOperationsOwnedIdempotency(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAdmin)
	operations := &testReconciliationOperations{}
	portal.config.Repository = repository
	portal.config.Reconciliation = operations
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "reconciliation-csrf", time.Minute)
	discrepancyID := uuid.MustParse("44444444-4444-4444-8444-444444444444")
	response := performCommerceForm(portal.Handler(), "/admin/commerce/reconciliation/discrepancies/"+discrepancyID.String()+"/transition", url.Values{
		"csrf": {"reconciliation-csrf"}, "reason": {"已核对渠道结算记录并确认差异"},
		"idempotency_key": {"reconciliation-transition-0001"}, "expected_version": {"7"},
		"target_status": {"RESOLVED"}, "reason_code": {"MATCH_CONFIRMED"},
	}, cookie)
	if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "section=reconciliation") {
		t.Fatalf("status/location=%d/%q body=%q", response.Code, response.Header().Get("Location"), response.Body.String())
	}
	request := operations.transition
	if operations.writeCalls != 1 || request.ActorID != repository.account.ID || request.DiscrepancyID != discrepancyID ||
		request.TargetStatus != "RESOLVED" || request.ExpectedVersion != 7 || request.ReasonCode != "MATCH_CONFIRMED" ||
		request.IdempotencyKey != "reconciliation-transition-0001" || request.TraceID == "" {
		t.Fatalf("transition request=%+v calls=%d", request, operations.writeCalls)
	}
	if len(repository.idempotencyKeys) != 0 {
		t.Fatalf("portal double-claimed Operations idempotency: %v", repository.idempotencyKeys)
	}
	if len(repository.auditCalls) != 1 || !strings.Contains(repository.auditCalls[0].metadata, "已核对渠道结算记录并确认差异") ||
		strings.Contains(repository.auditCalls[0].metadata, "reconciliation-transition-0001") {
		t.Fatalf("context audit=%+v", repository.auditCalls)
	}
}

func TestAdminReconciliationTransitionRejectsInvalidRoleStepUpAndReasonCode(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := newTestCommerceReportingRepository(store.RoleAuditor)
	operations := &testReconciliationOperations{}
	portal.config.Repository = repository
	portal.config.Reconciliation = operations
	discrepancyID := uuid.New()
	values := url.Values{
		"csrf": {"reconciliation-csrf"}, "reason": {"开始人工复核差异"},
		"idempotency_key": {"reconciliation-transition-0002"}, "expected_version": {"1"},
		"target_status": {"ACKNOWLEDGED"}, "reason_code": {"OPERATOR_REVIEW_STARTED"},
	}
	auditor := commerceSessionCookie(t, portal, store.RoleAuditor, "reconciliation-csrf", time.Minute)
	response := performCommerceForm(portal.Handler(), "/admin/commerce/reconciliation/discrepancies/"+discrepancyID.String()+"/transition", values, auditor)
	if response.Code != http.StatusForbidden || operations.writeCalls != 0 {
		t.Fatalf("auditor status/calls=%d/%d", response.Code, operations.writeCalls)
	}

	repository.account.Role = store.RoleAdmin
	stale := commerceSessionCookie(t, portal, store.RoleAdmin, "reconciliation-csrf", sensitiveActionTTL+time.Second)
	response = performCommerceForm(portal.Handler(), "/admin/commerce/reconciliation/discrepancies/"+discrepancyID.String()+"/transition", values, stale)
	if response.Code != http.StatusForbidden || operations.writeCalls != 0 {
		t.Fatalf("stale status/calls=%d/%d", response.Code, operations.writeCalls)
	}

	values.Set("reason_code", "FREE_TEXT_NOT_ALLOWED")
	fresh := commerceSessionCookie(t, portal, store.RoleAdmin, "reconciliation-csrf", time.Minute)
	response = performCommerceForm(portal.Handler(), "/admin/commerce/reconciliation/discrepancies/"+discrepancyID.String()+"/transition", values, fresh)
	if response.Code != http.StatusBadRequest || operations.writeCalls != 0 {
		t.Fatalf("reason code status/calls=%d/%d", response.Code, operations.writeCalls)
	}
}

func TestReconciliationCursorRoundTripAndUnknownFilterRejection(t *testing.T) {
	cursor := reconciliation.DiscrepancyCursor{LastSeenAt: time.Date(2026, 8, 27, 9, 10, 11, 12, time.UTC), ID: uuid.New()}
	encoded, err := encodeReconciliationCursor(cursor)
	if err != nil {
		t.Fatal(err)
	}
	decoded, err := decodeReconciliationCursor(encoded)
	if err != nil || decoded.ID != cursor.ID || !decoded.LastSeenAt.Equal(cursor.LastSeenAt) {
		t.Fatalf("decoded=%+v err=%v", decoded, err)
	}
	next, err := reconciliationNextURL(reconciliationFilterView{Provider: "ALIPAY", Status: "OPEN", Limit: 50}, cursor)
	if err != nil || !strings.Contains(html.UnescapeString(next), "cursor=") {
		t.Fatalf("next=%q err=%v", next, err)
	}
	if _, _, err := parseReconciliationQuery(url.Values{"section": {"reconciliation"}, "unexpected": {"value"}}); !errors.Is(err, errReconciliationFilter) {
		t.Fatalf("unknown filter err=%v", err)
	}
}

func textPointer(value string) *string { return &value }

var _ ReconciliationOperations = (*testReconciliationOperations)(nil)

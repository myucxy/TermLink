package webportal

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	commercecontrol "github.com/termlink/termlink/server/internal/commerce"
	"github.com/termlink/termlink/server/internal/store"
)

type testRefundPortalRepository struct {
	*testCommerceReportingRepository
	orders          []store.Order
	refunds         []store.Refund
	transactions    []store.PaymentTransaction
	notifications   []store.PaymentNotification
	reconciliations []store.ReconciliationRun
	err             error
}

func (r *testRefundPortalRepository) ListCommerceOrders(context.Context, uuid.UUID, int) ([]store.Order, error) {
	return append([]store.Order(nil), r.orders...), r.err
}

func (r *testRefundPortalRepository) ListCommerceTransactions(context.Context, uuid.UUID, int) ([]store.PaymentTransaction, error) {
	return append([]store.PaymentTransaction(nil), r.transactions...), r.err
}

func (r *testRefundPortalRepository) ListPaymentNotifications(context.Context, uuid.UUID, int) ([]store.PaymentNotification, error) {
	return append([]store.PaymentNotification(nil), r.notifications...), r.err
}

func (r *testRefundPortalRepository) ListRefunds(context.Context, uuid.UUID, int) ([]store.Refund, error) {
	return append([]store.Refund(nil), r.refunds...), r.err
}

func (r *testRefundPortalRepository) ListReconciliationRuns(context.Context, uuid.UUID, int) ([]store.ReconciliationRun, error) {
	return append([]store.ReconciliationRun(nil), r.reconciliations...), r.err
}

func (r *testRefundPortalRepository) CreateReconciliationRun(context.Context, uuid.UUID, string, time.Time, time.Time, string) (store.ReconciliationRun, error) {
	return store.ReconciliationRun{}, r.err
}

func newTestRefundPortal(t *testing.T) (*Portal, *testRefundPortalRepository, *testCommerceService, store.Refund) {
	t.Helper()
	portal := newTestPortal(t, &testOIDCFlow{})
	reporting := newTestCommerceReportingRepository(store.RoleAdmin)
	orderID := uuid.MustParse("55555555-5555-4555-8555-555555555555")
	refundID := uuid.MustParse("66666666-6666-4666-8666-666666666666")
	expiresAt := portal.now().Add(10 * time.Minute)
	refund := store.Refund{
		ID: refundID, OrderID: orderID, TransactionID: uuid.New(), RefundNo: "RF-APPROVAL-001",
		AmountMinor: 12800, Reason: "用户申请终止服务", Status: "APPROVAL_PENDING", Version: 3,
		ProviderRefHash: []byte("PROVIDER-REFERENCE-HASH-MUST-NOT-RENDER"), RequestedBy: reporting.account.ID,
		RequestedAt: portal.now().Add(-time.Minute), ApprovalExpiresAt: &expiresAt, UpdatedAt: portal.now().Add(-time.Minute),
	}
	repository := &testRefundPortalRepository{
		testCommerceReportingRepository: reporting,
		orders: []store.Order{{
			ID: orderID, AccountID: uuid.New(), OrderNo: "TL-ORDER-001", PlanKey: "pro",
			Provider: "ALIPAY", AmountMinor: refund.AmountMinor, Currency: "CNY", Status: "FULFILLED", CreatedAt: portal.now().Add(-time.Hour),
		}},
		refunds: []store.Refund{refund},
	}
	service := &testCommerceService{}
	portal.config.Repository = repository
	portal.config.Commerce = service
	return portal, repository, service, refund
}

func TestAdminRefundPageShowsOnlySanitizedExplicitSecondConfirmation(t *testing.T) {
	portal, repository, _, refund := newTestRefundPortal(t)
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "refund-csrf", time.Minute)
	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=orders", nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("status/body=%d/%q", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{
		"第一步只创建 APPROVAL_PENDING", "等待二次确认", "/admin/commerce/refunds/" + refund.ID.String() + "/confirm",
		`name="expected_version" value="3"`, "确认并向渠道退款",
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("refund page omitted %q", expected)
		}
	}
	for _, forbidden := range []string{"PROVIDER-REFERENCE-HASH-MUST-NOT-RENDER", "provider_ref_hash", "secret_ref", "provider_ref"} {
		if strings.Contains(strings.ToLower(body), strings.ToLower(forbidden)) {
			t.Errorf("refund page exposed %q", forbidden)
		}
	}
	if repository.account.Role != store.RoleAdmin {
		t.Fatal("test repository role changed unexpectedly")
	}
}

func TestAdminRefundCreateStopsAtApprovalPendingThenConfirmsExplicitVersion(t *testing.T) {
	portal, repository, service, refund := newTestRefundPortal(t)
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "refund-csrf", time.Minute)
	service.refundCreateOutcome = commercecontrol.RefundOutcome{Refund: refund, ApprovalRequired: true}
	create := performCommerceForm(portal.Handler(), "/admin/commerce/refunds", url.Values{
		"csrf": {"refund-csrf"}, "reason": {"用户申请终止服务"}, "idempotency_key": {"refund-create-approval-0001"},
		"order_id": {refund.OrderID.String()}, "amount_minor": {"12800"},
	}, cookie)
	if create.Code != http.StatusSeeOther || !strings.Contains(create.Header().Get("Location"), "notice=refund-created") {
		t.Fatalf("create status/location=%d/%q body=%q", create.Code, create.Header().Get("Location"), create.Body.String())
	}
	if service.refundCreateCalls != 1 || service.refundConfirmCalls != 0 || service.refundCreateRequest.OrderID != refund.OrderID ||
		service.refundCreateRequest.AmountMinor != refund.AmountMinor || service.refundCreateRequest.ActorID != repository.account.ID {
		t.Fatalf("create request=%+v create/confirm calls=%d/%d", service.refundCreateRequest, service.refundCreateCalls, service.refundConfirmCalls)
	}

	confirmed := refund
	confirmed.Status, confirmed.Version = "CREATED", refund.Version+1
	approvedAt := portal.now()
	confirmed.ApprovedAt = &approvedAt
	service.refundConfirmOutcome = commercecontrol.RefundOutcome{Refund: confirmed}
	confirm := performCommerceForm(portal.Handler(), "/admin/commerce/refunds/"+refund.ID.String()+"/confirm", url.Values{
		"csrf": {"refund-csrf"}, "reason": {"已复核原订单与全额退款依据"},
		"idempotency_key": {"refund-confirm-approval-0001"}, "expected_version": {"3"},
	}, cookie)
	if confirm.Code != http.StatusSeeOther || !strings.Contains(confirm.Header().Get("Location"), "notice=refund-confirmed-pending") {
		t.Fatalf("confirm status/location=%d/%q body=%q", confirm.Code, confirm.Header().Get("Location"), confirm.Body.String())
	}
	if service.refundConfirmCalls != 1 || service.refundConfirmRequest.ActorID != repository.account.ID ||
		service.refundConfirmRequest.RefundID != refund.ID || service.refundConfirmRequest.ExpectedVersion != refund.Version ||
		service.refundConfirmRequest.TraceID == "" {
		t.Fatalf("confirm request=%+v calls=%d", service.refundConfirmRequest, service.refundConfirmCalls)
	}
	if len(repository.auditCalls) != 1 || repository.auditCalls[0].action != "web.commerce.refund.confirm.context" ||
		!strings.Contains(repository.auditCalls[0].metadata, "已复核原订单与全额退款依据") ||
		strings.Contains(repository.auditCalls[0].metadata, "refund-confirm-approval-0001") {
		t.Fatalf("confirmation audit=%+v", repository.auditCalls)
	}
}

func TestAdminRefundConfirmationClassifiesExpiredConflictAndDuplicate(t *testing.T) {
	portal, repository, service, refund := newTestRefundPortal(t)
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "refund-csrf", time.Minute)
	target := "/admin/commerce/refunds/" + refund.ID.String() + "/confirm"
	values := func(key string) url.Values {
		return url.Values{
			"csrf": {"refund-csrf"}, "reason": {"复核全额退款审批"},
			"idempotency_key": {key}, "expected_version": {"3"},
		}
	}

	service.refundConfirmErr = store.ErrRefundApprovalExpired
	expired := performCommerceForm(portal.Handler(), target, values("refund-confirm-expired-0001"), cookie)
	if expired.Code != http.StatusGone || !strings.Contains(expired.Body.String(), "退款审批已过期") {
		t.Fatalf("expired status/body=%d/%q", expired.Code, expired.Body.String())
	}

	service.refundConfirmErr = store.ErrRefundVersionConflict
	conflict := performCommerceForm(portal.Handler(), target, values("refund-confirm-conflict-0001"), cookie)
	if conflict.Code != http.StatusConflict || !strings.Contains(conflict.Body.String(), "审批版本已变化") {
		t.Fatalf("conflict status/body=%d/%q", conflict.Code, conflict.Body.String())
	}

	before := service.refundConfirmCalls
	repository.duplicateClaim = true
	duplicate := performCommerceForm(portal.Handler(), target, values("refund-confirm-duplicate-0001"), cookie)
	if duplicate.Code != http.StatusConflict || !strings.Contains(duplicate.Body.String(), "请求已提交") || service.refundConfirmCalls != before {
		t.Fatalf("duplicate status/body/calls=%d/%q/%d want calls=%d", duplicate.Code, duplicate.Body.String(), service.refundConfirmCalls, before)
	}
}

func TestAdminRefundCreationRejectsAnyImmediateProviderState(t *testing.T) {
	portal, _, service, refund := newTestRefundPortal(t)
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "refund-csrf", time.Minute)
	unsafe := refund
	unsafe.Status = "SUCCEEDED"
	service.refundCreateOutcome = commercecontrol.RefundOutcome{Refund: unsafe, ApprovalRequired: false}
	response := performCommerceForm(portal.Handler(), "/admin/commerce/refunds", url.Values{
		"csrf": {"refund-csrf"}, "reason": {"用户申请终止服务"}, "idempotency_key": {"refund-create-unsafe-0001"},
		"order_id": {refund.OrderID.String()}, "amount_minor": {"12800"},
	}, cookie)
	if response.Code != http.StatusInternalServerError || !strings.Contains(response.Body.String(), "服务端暂时无法完成") {
		t.Fatalf("unsafe create status/body=%d/%q", response.Code, response.Body.String())
	}
}

var _ commercePaymentAdminRepository = (*testRefundPortalRepository)(nil)

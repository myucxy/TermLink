package webportal

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	commercecontrol "github.com/termlink/termlink/server/internal/commerce"
	"github.com/termlink/termlink/server/internal/store"
)

type testCommerceAccountRepository struct {
	*testPortalRepository
	claimDigest      [32]byte
	claimKey         string
	previewDigest    [32]byte
	queriedAccountID uuid.UUID
	redemptions      []store.AccountRedemption
	refunds          []store.Refund
}

func (r *testCommerceAccountRepository) PublishedPlans(context.Context) (int64, []store.Plan, error) {
	return 24, []store.Plan{{Key: "pro", DisplayName: "专业版", Description: "完整远程能力", BillingPeriod: "MONTHLY", PriceMinor: 1200, Currency: "CNY"}}, nil
}

func (r *testCommerceAccountRepository) GetEntitlement(context.Context, uuid.UUID) (store.Entitlement, error) {
	return store.Entitlement{PlanKey: "pro", CatalogVersion: 24, Version: 7, Source: "REDEMPTION", EffectiveAt: time.Date(2026, 8, 1, 0, 0, 0, 0, time.UTC)}, nil
}

func (r *testCommerceAccountRepository) GetUserPortalAccountOverview(_ context.Context, accountID uuid.UUID) (store.UserPortalAccountOverview, error) {
	r.queriedAccountID = accountID
	return store.UserPortalAccountOverview{ID: accountID, Role: r.account.Role, Status: "ACTIVE", AndroidDevices: 2, PCs: 1, OnlinePCs: 1, Sessions: 3, ActiveSessions: 1}, nil
}

func (r *testCommerceAccountRepository) GetUserPortalEntitlement(_ context.Context, accountID uuid.UUID) (store.UserPortalEntitlement, error) {
	r.queriedAccountID = accountID
	return store.UserPortalEntitlement{PlanKey: "pro", CatalogVersion: 24, Version: 7, Source: "REDEMPTION", EffectiveAt: time.Date(2026, 8, 1, 0, 0, 0, 0, time.UTC)}, nil
}

func (r *testCommerceAccountRepository) ListUserPortalDevices(_ context.Context, accountID uuid.UUID, _ store.UserPortalPageRequest) ([]store.UserPortalDevice, error) {
	r.queriedAccountID = accountID
	return []store.UserPortalDevice{{ID: uuid.New(), Type: "ANDROID", Name: "Pixel", TrustState: "TRUSTED", Online: true}}, nil
}

func (r *testCommerceAccountRepository) ListUserPortalPCs(_ context.Context, accountID uuid.UUID, _ store.UserPortalPageRequest) ([]store.UserPortalPC, error) {
	r.queriedAccountID = accountID
	return []store.UserPortalPC{{ID: uuid.New(), Name: "Workstation", AgentVersion: "1.2.3", Platform: "windows", Online: true}}, nil
}

func (r *testCommerceAccountRepository) ListUserPortalSessions(_ context.Context, accountID uuid.UUID, _ store.UserPortalPageRequest) ([]store.UserPortalSession, error) {
	r.queriedAccountID = accountID
	return []store.UserPortalSession{{ID: uuid.New(), PCID: uuid.New(), PCName: "Workstation", State: "RUNNING", Online: true}}, nil
}

func (r *testCommerceAccountRepository) ListUserPortalSecurityActivity(_ context.Context, accountID uuid.UUID, _ store.UserPortalAuditPageRequest) ([]store.UserPortalSecurityActivity, error) {
	r.queriedAccountID = accountID
	return []store.UserPortalSecurityActivity{{ID: 7, Action: "device.register", TargetType: "device", Result: "SUCCEEDED", CreatedAt: time.Now().UTC()}}, nil
}

func (r *testCommerceAccountRepository) ListOrders(context.Context, uuid.UUID, int) ([]store.Order, error) {
	return []store.Order{{
		ID: uuid.MustParse("22222222-2222-4222-8222-222222222222"), OrderNo: "TLTESTORDER", PlanKey: "pro",
		Provider: "ALIPAY", PaymentScene: "NATIVE", AmountMinor: 1200, Currency: "CNY", Status: "CREATED",
		CreatedAt: time.Date(2026, 8, 25, 10, 0, 0, 0, time.UTC),
	}}, nil
}

func (r *testCommerceAccountRepository) ClaimRedemption(_ context.Context, _ uuid.UUID, digest [32]byte, key string) (store.RedemptionClaim, store.Entitlement, error) {
	r.claimDigest = digest
	r.claimKey = key
	entitlement, _ := r.GetEntitlement(context.Background(), uuid.Nil)
	return store.RedemptionClaim{ID: uuid.New()}, entitlement, nil
}

func (r *testCommerceAccountRepository) PreviewRedemption(_ context.Context, accountID uuid.UUID, digest [32]byte) (store.RedemptionPreview, error) {
	r.queriedAccountID, r.previewDigest = accountID, digest
	projected := time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC)
	return store.RedemptionPreview{PlanKey: "pro", CatalogVersion: 24, DurationDays: 30, StackingRule: "EXTEND_SAME_OR_UPGRADE", CurrentPlanKey: "free", ProjectedEndAt: &projected}, nil
}

func (r *testCommerceAccountRepository) ListAccountRedemptions(_ context.Context, accountID uuid.UUID, limit int) ([]store.AccountRedemption, error) {
	r.queriedAccountID = accountID
	if limit > commercePageLimit {
		return nil, errors.New("unbounded redemption history")
	}
	return append([]store.AccountRedemption(nil), r.redemptions...), nil
}

func (r *testCommerceAccountRepository) ListAccountRefunds(_ context.Context, accountID uuid.UUID, limit int) ([]store.Refund, error) {
	r.queriedAccountID = accountID
	if limit > commercePageLimit {
		return nil, errors.New("unbounded refund history")
	}
	return append([]store.Refund(nil), r.refunds...), nil
}

func (r *testCommerceAccountRepository) CancelOrder(context.Context, uuid.UUID, uuid.UUID) (store.Order, error) {
	return store.Order{Status: "CLOSED"}, nil
}

func (r *testCommerceAccountRepository) GetActivePaymentConfiguration(_ context.Context, provider, scene string) (store.PaymentChannel, store.PaymentChannelVersion, error) {
	if provider != "ALIPAY" || scene != "NATIVE" {
		return store.PaymentChannel{}, store.PaymentChannelVersion{}, store.ErrPaymentChannelUnavailable
	}
	channelID := uuid.MustParse("12121212-1212-4212-8212-121212121212")
	versionID := uuid.MustParse("34343434-3434-4434-8434-343434343434")
	return store.PaymentChannel{
		ID: channelID, Provider: provider, Scene: scene, Enabled: true,
		ActiveVersionID: &versionID, HealthStatus: "HEALTHY",
	}, store.PaymentChannelVersion{ID: versionID, ChannelID: channelID, Status: "VALIDATED"}, nil
}

type testRegistrationAdminRepository struct {
	*testPortalRepository
	policy       store.RegistrationPolicy
	updatedMode  string
	updatedActor uuid.UUID
	batches      []store.InvitationBatch
	redemptions  []store.InvitationRedemption
}

type testRedemptionAdminRepository struct {
	*testPortalRepository
	claims []store.AccountRedemption
}

func (r *testRedemptionAdminRepository) CreateRedemptionBatch(context.Context, uuid.UUID, store.RedemptionBatchInput, string) (store.RedemptionBatch, error) {
	return store.RedemptionBatch{}, nil
}

func (r *testRedemptionAdminRepository) AddRedemptionCodes(context.Context, uuid.UUID, uuid.UUID, []store.CodeDigestRecord, string) ([]store.RedemptionCode, error) {
	return nil, nil
}

func (r *testRedemptionAdminRepository) ListRedemptionBatches(context.Context, uuid.UUID, int) ([]store.RedemptionBatch, error) {
	return nil, nil
}

func (r *testRedemptionAdminRepository) ListRedemptionCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.RedemptionCode, error) {
	return nil, nil
}

func (r *testRedemptionAdminRepository) ListRedemptionClaims(context.Context, uuid.UUID, int) ([]store.AccountRedemption, error) {
	return append([]store.AccountRedemption(nil), r.claims...), nil
}

func (r *testRedemptionAdminRepository) SetRedemptionBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

func (r *testRedemptionAdminRepository) SetRedemptionCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

func (r *testRegistrationAdminRepository) GetRegistrationPolicy(context.Context) (store.RegistrationPolicy, error) {
	return r.policy, nil
}

func (r *testRegistrationAdminRepository) ConfirmPendingInvitationForEmail(context.Context, uuid.UUID, [32]byte) (store.Entitlement, error) {
	return store.Entitlement{}, store.ErrNotFound
}

func (r *testRegistrationAdminRepository) UpdateRegistrationPolicy(_ context.Context, actor uuid.UUID, mode string, version int64, _ string) (store.RegistrationPolicy, error) {
	r.updatedActor, r.updatedMode = actor, mode
	r.policy.Mode, r.policy.Version = mode, version+1
	return r.policy, nil
}

func (r *testRegistrationAdminRepository) CreateInvitationBatch(context.Context, uuid.UUID, store.InvitationBatchInput, string) (store.InvitationBatch, error) {
	return store.InvitationBatch{}, nil
}

func (r *testRegistrationAdminRepository) AddInvitationCodes(context.Context, uuid.UUID, uuid.UUID, []store.CodeDigestRecord, string) ([]store.InvitationCode, error) {
	return nil, nil
}

func (r *testRegistrationAdminRepository) ListInvitationBatches(context.Context, uuid.UUID, int) ([]store.InvitationBatch, error) {
	return append([]store.InvitationBatch(nil), r.batches...), nil
}

func (r *testRegistrationAdminRepository) ListInvitationCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.InvitationCode, error) {
	return nil, nil
}

func (r *testRegistrationAdminRepository) ListInvitationRedemptions(context.Context, uuid.UUID, int) ([]store.InvitationRedemption, error) {
	return append([]store.InvitationRedemption(nil), r.redemptions...), nil
}

func (r *testRegistrationAdminRepository) SetInvitationBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

func (r *testRegistrationAdminRepository) SetInvitationCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error {
	return nil
}

type testChannelAdminRepository struct {
	*testPortalRepository
	channelID        uuid.UUID
	createdVersion   store.PaymentChannelVersion
	validationResult bool
}

func (r *testChannelAdminRepository) ListPaymentChannels(context.Context, uuid.UUID) ([]store.PaymentChannel, error) {
	activeVersionID := uuid.MustParse("44444444-4444-4444-8444-444444444444")
	return []store.PaymentChannel{{
		ID: r.channelID, Provider: "WECHAT_PAY", Scene: "NATIVE", HealthStatus: "HEALTHY", Enabled: true,
		ActiveVersionID: &activeVersionID, LastErrorCode: "TLS_HANDSHAKE_FAILED",
	}}, nil
}

func (r *testChannelAdminRepository) ListPaymentChannelVersions(context.Context, uuid.UUID, uuid.UUID) ([]store.PaymentChannelVersion, error) {
	return []store.PaymentChannelVersion{{
		ID: uuid.MustParse("44444444-4444-4444-8444-444444444444"), ChannelID: r.channelID, Version: 3,
		Environment: "PRODUCTION", Status: "VALIDATED", ValidationSummary: "certificate valid",
		PublicConfig: json.RawMessage(`{
			"app_id":"wx-app-1234567890","mch_id":"merchant-1234567890",
			"notify_url":"https://user:password@pay.example/notify/wechat?token=QUERY-MUST-NOT-ECHO#fragment",
			"merchant_serial":"SERIAL-1234567890","raw_provider_error":"PROVIDER-RAW-MUST-NOT-ECHO",
			"private_key":"PRIVATE-CONFIG-MUST-NOT-ECHO"
		}`),
		SecretRefs: json.RawMessage(`{"key":"ULTRA-SECRET-REF-SHOULD-NOT-ECHO"}`),
	}}, nil
}

func (r *testChannelAdminRepository) EnsurePaymentChannel(context.Context, uuid.UUID, string, string, string) (store.PaymentChannel, error) {
	return store.PaymentChannel{}, nil
}

func (r *testChannelAdminRepository) CreatePaymentChannelVersion(_ context.Context, _ uuid.UUID, channelID uuid.UUID, input store.PaymentChannelVersionInput, _ string) (store.PaymentChannelVersion, error) {
	r.createdVersion = store.PaymentChannelVersion{
		ID: uuid.MustParse("55555555-5555-4555-8555-555555555555"), ChannelID: channelID,
		Version: 4, Environment: input.Environment, PublicConfig: append(json.RawMessage(nil), input.PublicConfig...),
		SecretRefs: append(json.RawMessage(nil), input.SecretRefs...), Status: "DRAFT",
	}
	return r.createdVersion, nil
}

func (r *testChannelAdminRepository) ValidatePaymentChannelVersion(_ context.Context, _ uuid.UUID, _ uuid.UUID, valid bool, summary, _ string) (store.PaymentChannelVersion, error) {
	r.validationResult = valid
	r.createdVersion.ValidationSummary = summary
	return r.createdVersion, nil
}

func (r *testChannelAdminRepository) ActivatePaymentChannelVersion(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, string) (store.PaymentChannel, error) {
	return store.PaymentChannel{}, nil
}

func (r *testChannelAdminRepository) DisablePaymentChannel(context.Context, uuid.UUID, uuid.UUID, string) (store.PaymentChannel, error) {
	return store.PaymentChannel{}, nil
}

type testPaymentAdminRepository struct {
	*testPortalRepository
	orders         []store.Order
	refunds        []store.Refund
	currentOrder   store.Order
	fulfilledOrder store.Order
	getOrderID     uuid.UUID
	fulfillOrderID uuid.UUID
	closeInput     store.AdminCloseOrderInput
	closeResult    store.AdminCloseOrderResult
	closeErr       error
}

func (r *testPaymentAdminRepository) ListCommerceOrders(context.Context, uuid.UUID, int) ([]store.Order, error) {
	return append([]store.Order(nil), r.orders...), nil
}

func (r *testPaymentAdminRepository) ListCommerceTransactions(context.Context, uuid.UUID, int) ([]store.PaymentTransaction, error) {
	return nil, nil
}

func (r *testPaymentAdminRepository) ListPaymentNotifications(context.Context, uuid.UUID, int) ([]store.PaymentNotification, error) {
	return nil, nil
}

func (r *testPaymentAdminRepository) ListRefunds(context.Context, uuid.UUID, int) ([]store.Refund, error) {
	return append([]store.Refund(nil), r.refunds...), nil
}

func (r *testPaymentAdminRepository) ListReconciliationRuns(context.Context, uuid.UUID, int) ([]store.ReconciliationRun, error) {
	return nil, nil
}

func (r *testPaymentAdminRepository) CreateReconciliationRun(context.Context, uuid.UUID, string, time.Time, time.Time, string) (store.ReconciliationRun, error) {
	return store.ReconciliationRun{}, nil
}

func (r *testPaymentAdminRepository) GetOrderByID(_ context.Context, orderID uuid.UUID) (store.Order, error) {
	r.getOrderID = orderID
	if r.currentOrder.ID == uuid.Nil {
		return store.Order{}, store.ErrNotFound
	}
	return r.currentOrder, nil
}

func (r *testPaymentAdminRepository) FulfillOrder(_ context.Context, orderID uuid.UUID) (store.Order, store.Entitlement, error) {
	r.fulfillOrderID = orderID
	if r.fulfilledOrder.ID == uuid.Nil {
		r.fulfilledOrder = r.currentOrder
		r.fulfilledOrder.Status = "FULFILLED"
	}
	return r.fulfilledOrder, store.Entitlement{}, nil
}

func (r *testPaymentAdminRepository) AdminCloseUnpaidOrderFromWeb(_ context.Context, input store.AdminCloseOrderInput) (store.AdminCloseOrderResult, error) {
	r.closeInput = input
	return r.closeResult, r.closeErr
}

type testCatalogAdminRepository struct {
	*testPortalRepository
	draft           *store.CommerceCatalogDraft
	createdInput    store.PlanDraftInput
	updatedInput    store.PlanDraftInput
	createdActor    uuid.UUID
	createdDevice   uuid.UUID
	updatedActor    uuid.UUID
	updatedDevice   uuid.UUID
	updatedPlan     uuid.UUID
	publishedActor  uuid.UUID
	publishedDevice uuid.UUID
	publishCalls    int
}

func (r *testCatalogAdminRepository) GetCommerceCatalogDraft(context.Context, uuid.UUID) (*store.CommerceCatalogDraft, error) {
	return r.draft, nil
}

func (r *testCatalogAdminRepository) ListCommerceOrderReport(context.Context, uuid.UUID, store.CommerceOrderReportFilter) (store.CommerceOrderReportPage, error) {
	return store.CommerceOrderReportPage{}, nil
}

func (r *testCatalogAdminRepository) ExportCommerceOrders(context.Context, uuid.UUID, store.CommerceOrderExportFilter) (store.CommerceOrderExport, error) {
	return store.CommerceOrderExport{}, nil
}

func (r *testCatalogAdminRepository) ListCommerceConsistencyDetails(context.Context, uuid.UUID, store.CommerceConsistencyFilter) (store.CommerceConsistencyPage, error) {
	return store.CommerceConsistencyPage{}, nil
}

func (r *testCatalogAdminRepository) CreatePlanDraft(_ context.Context, actorID, deviceID uuid.UUID, input store.PlanDraftInput, _ string) (store.PlanDraft, error) {
	r.createdActor, r.createdDevice, r.createdInput = actorID, deviceID, input
	return store.PlanDraft{ID: uuid.New(), Key: input.PlanKey}, nil
}

func (r *testCatalogAdminRepository) UpdatePlanDraft(_ context.Context, actorID, deviceID, planID uuid.UUID, input store.PlanDraftInput, _ string) (store.PlanDraft, error) {
	r.updatedActor, r.updatedDevice, r.updatedPlan, r.updatedInput = actorID, deviceID, planID, input
	return store.PlanDraft{ID: planID, Key: input.PlanKey}, nil
}

func (r *testCatalogAdminRepository) PublishCatalog(_ context.Context, actorID, deviceID uuid.UUID, _ string) (int64, error) {
	r.publishedActor, r.publishedDevice = actorID, deviceID
	r.publishCalls++
	return 42, nil
}

type testManualEntitlementAdminRepository struct {
	*testPortalRepository
	grants      []store.ManualEntitlementGrant
	listActor   uuid.UUID
	listFilter  store.ManualEntitlementGrantFilter
	createActor uuid.UUID
	createInput store.CreateManualEntitlementGrantInput
	revokeActor uuid.UUID
	revokeInput store.RevokeManualEntitlementGrantInput
	createCalls int
	revokeCalls int
}

func (r *testManualEntitlementAdminRepository) PublishedPlans(context.Context) (int64, []store.Plan, error) {
	return 24, []store.Plan{{Key: "pro", DisplayName: "专业版", BillingPeriod: "MONTHLY", PriceMinor: 1200, Currency: "CNY"}}, nil
}

func (r *testManualEntitlementAdminRepository) ListManualEntitlementGrants(_ context.Context, actorID uuid.UUID, filter store.ManualEntitlementGrantFilter) ([]store.ManualEntitlementGrant, error) {
	r.listActor, r.listFilter = actorID, filter
	return append([]store.ManualEntitlementGrant(nil), r.grants...), nil
}

func (r *testManualEntitlementAdminRepository) CreateManualEntitlementGrantFromWeb(_ context.Context, actorID uuid.UUID, input store.CreateManualEntitlementGrantInput) (store.ManualEntitlementGrant, error) {
	r.createActor, r.createInput = actorID, input
	r.createCalls++
	return store.ManualEntitlementGrant{ID: uuid.New(), AccountID: input.AccountID, PlanKey: input.PlanKey, Status: "ACTIVE"}, nil
}

func (r *testManualEntitlementAdminRepository) RevokeManualEntitlementGrantFromWeb(_ context.Context, actorID uuid.UUID, input store.RevokeManualEntitlementGrantInput) (store.ManualEntitlementGrant, error) {
	r.revokeActor, r.revokeInput = actorID, input
	r.revokeCalls++
	return store.ManualEntitlementGrant{ID: input.GrantID, Status: "REVOKED"}, nil
}

var (
	_ commerceAdminCloseRepository             = (*store.Postgres)(nil)
	_ commerceManualEntitlementAdminRepository = (*store.Postgres)(nil)
)

type testCommerceService struct {
	validationCalls      int
	probeCalls           int
	probeErr             error
	validated            store.PaymentChannelVersion
	createdRequest       commercecontrol.CreateOrderRequest
	startedAccount       uuid.UUID
	startedOrder         uuid.UUID
	queriedAccount       uuid.UUID
	queriedOrder         uuid.UUID
	order                store.Order
	instruction          commercecontrol.PaymentInstruction
	outcome              commercecontrol.PaymentOutcome
	refundCreateRequest  commercecontrol.CreateRefundRequest
	refundCreateOutcome  commercecontrol.RefundOutcome
	refundCreateErr      error
	refundConfirmRequest commercecontrol.ConfirmRefundRequest
	refundConfirmOutcome commercecontrol.RefundOutcome
	refundConfirmErr     error
	refundCreateCalls    int
	refundConfirmCalls   int
}

func (s *testCommerceService) CreateOrder(_ context.Context, request commercecontrol.CreateOrderRequest) (store.Order, error) {
	s.createdRequest = request
	if s.order.ID == uuid.Nil {
		s.order = store.Order{ID: uuid.New(), AccountID: request.AccountID, OrderNo: "TL-CREATED", PlanKey: request.PlanKey, Provider: request.Provider, PaymentScene: request.Scene, Status: "CREATED", ExpiresAt: request.ExpiresAt}
	}
	return s.order, nil
}

func (s *testCommerceService) StartPayment(_ context.Context, accountID, orderID uuid.UUID) (commercecontrol.PaymentInstruction, error) {
	s.startedAccount, s.startedOrder = accountID, orderID
	if s.instruction.MerchantOrderID == "" {
		s.instruction = commercecontrol.PaymentInstruction{Provider: "ALIPAY", MerchantOrderID: s.order.OrderNo, Kind: "QR_CODE", QRCodeURL: "https://pay.example/qr", ExpiresAt: s.order.ExpiresAt}
	}
	return s.instruction, nil
}

func (s *testCommerceService) QueryAndFulfill(_ context.Context, accountID, orderID uuid.UUID) (commercecontrol.PaymentOutcome, error) {
	s.queriedAccount, s.queriedOrder = accountID, orderID
	return s.outcome, nil
}

func (s *testCommerceService) ValidatePaymentChannelVersion(_ context.Context, _ store.PaymentChannel, version store.PaymentChannelVersion) error {
	s.validationCalls++
	s.validated = version
	return nil
}

func (s *testCommerceService) ProbePaymentChannelVersion(_ context.Context, _ store.PaymentChannel, version store.PaymentChannelVersion) error {
	s.probeCalls++
	s.validated = version
	return s.probeErr
}

type testPaymentCertificateHealthSource struct {
	health commercecontrol.PaymentCertificateHealth
}

func (s testPaymentCertificateHealthSource) CachedPaymentCertificateHealth(provider, scene string) (commercecontrol.PaymentCertificateHealth, bool) {
	if provider != "WECHAT_PAY" || scene != "NATIVE" {
		return commercecontrol.PaymentCertificateHealth{}, false
	}
	return s.health, true
}

func TestCommercePaymentCertificateHealthViewUsesOnlySafeSnapshot(t *testing.T) {
	expiry := time.Date(2026, 10, 1, 8, 0, 0, 0, time.UTC)
	refresh := expiry.Add(-12 * time.Hour)
	views := []commercePaymentChannelView{{Provider: "WECHAT_PAY", Scene: "NATIVE"}, {Provider: "ALIPAY", Scene: "NATIVE"}}
	applyCommercePaymentCertificateHealth(views, testPaymentCertificateHealthSource{health: commercecontrol.PaymentCertificateHealth{
		Status: "HEALTHY", ActiveCertificates: 2, RemainingDays: 35,
		EarliestExpiryAt: expiry, LastRefreshAt: refresh,
	}})
	if views[0].CertificateStatus != "HEALTHY" || views[0].CertificateExpiryAt == nil ||
		!views[0].CertificateExpiryAt.Equal(expiry) || views[0].CertificateRemaining != 35 ||
		views[0].CertificateLastRefresh == nil || !views[0].CertificateLastRefresh.Equal(refresh) {
		t.Fatalf("certificate view = %+v", views[0])
	}
	if views[1].CertificateStatus != "" || views[1].CertificateExpiryAt != nil {
		t.Fatalf("non-WeChat channel received certificate state: %+v", views[1])
	}
}

func (s *testCommerceService) CreateRefund(_ context.Context, request commercecontrol.CreateRefundRequest) (commercecontrol.RefundOutcome, error) {
	s.refundCreateCalls++
	s.refundCreateRequest = request
	return s.refundCreateOutcome, s.refundCreateErr
}

func (s *testCommerceService) ConfirmRefund(_ context.Context, request commercecontrol.ConfirmRefundRequest) (commercecontrol.RefundOutcome, error) {
	s.refundConfirmCalls++
	s.refundConfirmRequest = request
	return s.refundConfirmOutcome, s.refundConfirmErr
}

func commerceSessionCookie(t *testing.T, portal *Portal, role store.Role, csrf string, age time.Duration) *http.Cookie {
	t.Helper()
	authenticatedAt := portal.now().Add(-age)
	value, err := portal.sessionCodec.seal(webSession{
		Subject: "commerce-subject", Username: "commerce-user", Name: "Commerce User", Role: string(role), CSRF: csrf,
		AuthenticatedAtMS: authenticatedAt.UnixMilli(), MFA: true, ExpiresAtMS: portal.now().Add(time.Hour).UnixMilli(),
	})
	if err != nil {
		t.Fatalf("seal commerce session: %v", err)
	}
	return &http.Cookie{Name: sessionCookieName, Value: value, Path: "/"}
}

func performCommerceForm(handler http.Handler, target string, values url.Values, cookie *http.Cookie) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "https://"+testPublicHost+target, strings.NewReader(values.Encode()))
	request.Host = testPublicHost
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Origin", "https://"+testPublicHost)
	if cookie != nil {
		request.AddCookie(cookie)
	}
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, request)
	return recorder
}

func TestCommerceDashboardRequiresLoginAndRendersOwnedData(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	portal.config.Repository = repository

	legacy := performRequest(portal.Handler(), http.MethodGet, "/commerce?section=overview", nil)
	if legacy.Code != http.StatusPermanentRedirect || legacy.Header().Get("Location") != "/account?section=overview" {
		t.Fatalf("legacy commerce redirect = %d %q", legacy.Code, legacy.Header().Get("Location"))
	}
	unauthenticated := performRequest(portal.Handler(), http.MethodGet, "/account", nil)
	if unauthenticated.Code != http.StatusSeeOther {
		t.Fatalf("unauthenticated commerce status = %d, want 303", unauthenticated.Code)
	}

	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)
	response := performRequest(portal.Handler(), http.MethodGet, "/account?section=overview", nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("commerce status = %d, want 200; body=%s", response.Code, response.Body.String())
	}
	for _, want := range []string{"账号概览", "远程连接概览", "在线 PC", "活动会话"} {
		if !strings.Contains(response.Body.String(), want) {
			t.Errorf("commerce dashboard omitted %q", want)
		}
	}
	if repository.queriedAccountID != repository.account.ID {
		t.Fatalf("user portal queried account %s, want authenticated account %s", repository.queriedAccountID, repository.account.ID)
	}

	subscription := performRequest(portal.Handler(), http.MethodGet, "/account?section=subscription", nil, cookie)
	for _, want := range []string{"订阅与权益", "专业版", "预览兑换权益", "¥12.00"} {
		if !strings.Contains(subscription.Body.String(), want) {
			t.Errorf("subscription section omitted %q", want)
		}
	}
	orders := performRequest(portal.Handler(), http.MethodGet, "/account?section=orders", nil, cookie)
	if !strings.Contains(orders.Body.String(), "TLTESTORDER") {
		t.Fatal("order section omitted the authenticated account order")
	}

	settings := performRequest(portal.Handler(), http.MethodGet, "/account?section=settings", nil, cookie)
	if settings.Code != http.StatusOK {
		t.Fatalf("settings status = %d, want 200; body=%s", settings.Code, settings.Body.String())
	}
	for _, want := range []string{
		`data-identity-dialog="password"`,
		`data-identity-dialog="mfa"`,
		`data-identity-dialog="sessions"`,
		`data-identity-url="/account/identity"`,
	} {
		if !strings.Contains(settings.Body.String(), want) {
			t.Errorf("settings dialog omitted %q", want)
		}
	}
	if strings.Contains(settings.Body.String(), `data-identity-url="`+portal.identityURL+`"`) {
		t.Fatal("settings dialog bypassed the allowlisted in-product identity entry point")
	}
}

func TestCommerceRedemptionRejectsCSRFAndUsesCompatibleDigest(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	portal.config.Repository = repository
	portal.config.CommerceKey = []byte("commerce-key-0123456789abcdef012345")
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)
	code := "TLR-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA"

	denied := performCommerceForm(portal.Handler(), "/account/redemptions", url.Values{
		"csrf": {"wrong"}, "idempotency_key": {"redemption-request-1"}, "code": {code},
	}, cookie)
	if denied.Code != http.StatusForbidden {
		t.Fatalf("bad-CSRF redemption status = %d, want 403", denied.Code)
	}

	accepted := performCommerceForm(portal.Handler(), "/account/redemptions", url.Values{
		"csrf": {"csrf-user"}, "idempotency_key": {"redemption-request-1"}, "code": {code},
	}, cookie)
	if accepted.Code != http.StatusOK {
		t.Fatalf("redemption status = %d, want 200; body=%s", accepted.Code, accepted.Body.String())
	}
	wantDigest := portalCommerceDigest(portal.config.CommerceKey, "redemption-code", normalizePortalCommerceCode(code))
	if repository.claimDigest != wantDigest || repository.claimKey != "redemption-request-1" {
		t.Fatal("redemption did not preserve the API-compatible domain-separated digest and idempotency key")
	}
}

func TestCommerceRedemptionPreviewIsStrictScopedAndNeverEchoesCode(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	portal.config.Repository = repository
	portal.config.CommerceKey = []byte("commerce-key-0123456789abcdef012345")
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)
	code := "TLR-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ"

	rejected := performCommerceForm(portal.Handler(), "/account/redemptions/preview", url.Values{
		"csrf": {"csrf-user"}, "code": {code}, "unexpected": {"must-fail"},
	}, cookie)
	if rejected.Code != http.StatusBadRequest || repository.previewDigest != ([32]byte{}) {
		t.Fatalf("preview with unknown field status=%d digest=%x, want 400 and no repository call", rejected.Code, repository.previewDigest)
	}

	response := performCommerceForm(portal.Handler(), "/account/redemptions/preview", url.Values{
		"csrf": {"csrf-user"}, "code": {code},
	}, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("preview status=%d, want 200; body=%s", response.Code, response.Body.String())
	}
	wantDigest := portalCommerceDigest(portal.config.CommerceKey, "redemption-code", normalizePortalCommerceCode(code))
	if repository.queriedAccountID != repository.account.ID || repository.previewDigest != wantDigest {
		t.Fatalf("preview scope/digest account=%s digest=%x", repository.queriedAccountID, repository.previewDigest)
	}
	body := response.Body.String()
	if strings.Contains(body, code) || !strings.Contains(body, "兑换后将获得的权益") || !strings.Contains(body, "重新输入兑换码确认核销") {
		t.Fatal("preview either echoed the plaintext code or omitted the explicit confirmation step")
	}
}

func TestCommerceAccountHistoryUsesSafeScopedViews(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	requestedBy := uuid.MustParse("77777777-7777-4777-8777-777777777777")
	repository := &testCommerceAccountRepository{
		testPortalRepository: newTestPortalRepository(),
		redemptions: []store.AccountRedemption{{
			ClaimID: uuid.New(), AccountID: uuid.MustParse("11111111-1111-4111-8111-111111111111"),
			CodePrefix: "TLR-SAFE", PlanKey: "pro", CatalogVersion: 24, Status: "ACTIVE",
			ClaimedAt: portal.now(), StartsAt: portal.now(),
		}},
		refunds: []store.Refund{{
			ID: uuid.New(), OrderID: uuid.MustParse("88888888-8888-4888-8888-888888888888"), RequestedBy: requestedBy,
			RefundNo: "RF-SAFE-VIEW", AmountMinor: 1200, Reason: "INTERNAL-REFUND-REASON-MUST-NOT-RENDER",
			Status: "SUCCEEDED", FailureCode: "INTERNAL-FAILURE-MUST-NOT-RENDER",
			ProviderRefHash: []byte("PROVIDER-HASH-MUST-NOT-RENDER"), RequestedAt: portal.now(),
		}},
	}
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)

	subscription := performRequest(portal.Handler(), http.MethodGet, "/account?section=subscription", nil, cookie)
	if subscription.Code != http.StatusOK || !strings.Contains(subscription.Body.String(), "TLR-SAFE") {
		t.Fatalf("owned redemption history status=%d body=%s", subscription.Code, subscription.Body.String())
	}
	orders := performRequest(portal.Handler(), http.MethodGet, "/account?section=orders", nil, cookie)
	if orders.Code != http.StatusOK || !strings.Contains(orders.Body.String(), "RF-SAFE-VIEW") {
		t.Fatalf("owned refund history status=%d body=%s", orders.Code, orders.Body.String())
	}
	for _, forbidden := range []string{
		"INTERNAL-REFUND-REASON-MUST-NOT-RENDER", "INTERNAL-FAILURE-MUST-NOT-RENDER",
		"PROVIDER-HASH-MUST-NOT-RENDER", requestedBy.String(),
	} {
		if strings.Contains(orders.Body.String(), forbidden) {
			t.Fatalf("account refund history leaked %q", forbidden)
		}
	}
	if repository.queriedAccountID != repository.account.ID {
		t.Fatalf("history queried account %s, want %s", repository.queriedAccountID, repository.account.ID)
	}
}

func TestCommercePurchaseRequiresCSRFAndScopesOrderToAuthenticatedAccount(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	service := &testCommerceService{}
	portal.config.Repository = repository
	portal.config.Commerce = service
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)
	values := url.Values{
		"csrf": {"csrf-user"}, "plan_key": {"pro"}, "payment_method": {"ALIPAY:NATIVE"},
		"idempotency_key": {"checkout-request-123"},
	}

	badCSRF := url.Values{
		"csrf": {"wrong"}, "plan_key": {"pro"}, "payment_method": {"ALIPAY:NATIVE"},
		"idempotency_key": {"checkout-request-123"},
	}
	denied := performCommerceForm(portal.Handler(), "/account/orders", badCSRF, cookie)
	if denied.Code != http.StatusForbidden || service.createdRequest.AccountID != uuid.Nil {
		t.Fatalf("bad-CSRF purchase status=%d account=%s, want 403 and no service call", denied.Code, service.createdRequest.AccountID)
	}

	created := performCommerceForm(portal.Handler(), "/account/orders", values, cookie)
	if created.Code != http.StatusCreated {
		t.Fatalf("purchase status=%d, want 201; body=%s", created.Code, created.Body.String())
	}
	if service.createdRequest.AccountID != repository.account.ID || service.startedAccount != repository.account.ID || service.startedOrder != service.order.ID {
		t.Fatalf("purchase account scope/create-start mismatch: request=%s start=%s order=%s", service.createdRequest.AccountID, service.startedAccount, service.startedOrder)
	}
	if service.createdRequest.PlanKey != "pro" || service.createdRequest.Provider != "ALIPAY" || service.createdRequest.Scene != "NATIVE" {
		t.Fatalf("purchase request mismatch: %+v", service.createdRequest)
	}
	body := created.Body.String()
	for _, want := range []string{"https://pay.example/qr", "浏览器返回页不是支付凭证", "查询渠道结果"} {
		if !strings.Contains(body, want) {
			t.Errorf("payment instruction omitted %q", want)
		}
	}
}

func TestCommerceQueryUsesProviderOutcomeAndNeverTrustsReturnParameters(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCommerceAccountRepository{testPortalRepository: newTestPortalRepository()}
	orderID := uuid.MustParse("66666666-6666-4666-8666-666666666666")
	service := &testCommerceService{outcome: commercecontrol.PaymentOutcome{
		Order:          store.Order{ID: orderID, AccountID: repository.account.ID, OrderNo: "TL-AUTHORITATIVE", Status: "PENDING_PAYMENT"},
		ProviderStatus: "PENDING", Fulfilled: false,
	}}
	portal.config.Repository = repository
	portal.config.Commerce = service
	cookie := commerceSessionCookie(t, portal, store.RoleUser, "csrf-user", time.Minute)

	response := performCommerceForm(portal.Handler(), "/account/orders/"+orderID.String()+"/query?status=SUCCESS&paid=true&order_no=ATTACKER", url.Values{
		"csrf": {"csrf-user"}, "status": {"SUCCESS"}, "paid": {"true"},
	}, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("query status=%d, want 200; body=%s", response.Code, response.Body.String())
	}
	if service.queriedAccount != repository.account.ID || service.queriedOrder != orderID {
		t.Fatalf("query scope account=%s order=%s", service.queriedAccount, service.queriedOrder)
	}
	body := response.Body.String()
	if strings.Contains(body, "ATTACKER") || strings.Contains(body, "已履约</span>") || !strings.Contains(body, "未确认成功") || !strings.Contains(body, "TL-AUTHORITATIVE") {
		t.Fatal("query page trusted browser return parameters instead of the provider-backed service outcome")
	}
}

func TestSafePaymentInstructionRejectsUnsafeRedirect(t *testing.T) {
	orderID := uuid.New()
	_, err := safePaymentInstructionView(store.Order{ID: orderID, OrderNo: "TL-1", Provider: "ALIPAY"}, commercecontrol.PaymentInstruction{
		Provider: "ALIPAY", MerchantOrderID: "TL-1", Kind: "REDIRECT", RedirectURL: "javascript:alert(1)",
	})
	if !errors.Is(err, commercecontrol.ErrPaymentResultMismatch) {
		t.Fatalf("unsafe redirect error=%v, want payment result mismatch", err)
	}
	view, err := safePaymentInstructionView(store.Order{ID: orderID, OrderNo: "TL-1", Provider: "ALIPAY"}, commercecontrol.PaymentInstruction{
		Provider: "ALIPAY", MerchantOrderID: "TL-1", Kind: "REDIRECT", RedirectURL: "https://pay.example/checkout",
	})
	if err != nil || view.RedirectURL != "https://pay.example/checkout" {
		t.Fatalf("safe redirect rejected: view=%+v err=%v", view, err)
	}
}

func TestUserPortalTemplateAndDTOBoundaryOmitSensitiveTerminalAndPaymentFields(t *testing.T) {
	data, err := portalAssets.ReadFile("assets/portal-dashboard.html")
	if err != nil {
		t.Fatal(err)
	}
	lower := strings.ToLower(string(data))
	for _, forbidden := range []string{
		"title_cipher", "last_output_seq", "ciphertext", "event_chunks", "snapshots", "object_key",
		"payment_payload", "secret_refs", "provider_trade", "provider_reference", "trace_id", "audit.metadata",
	} {
		if strings.Contains(lower, forbidden) {
			t.Fatalf("user portal template references sensitive field %q", forbidden)
		}
	}
}

func TestAdminCommercePolicyRequiresSuperAdminCSRFAndRecentLogin(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationInviteRequired, Version: 5, UpdatedAt: portal.now()},
	}
	portal.config.Repository = repository
	values := url.Values{
		"csrf": {"csrf-admin"}, "version": {"5"}, "mode": {store.RegistrationClosed},
		"reason": {"关闭公开注册以执行安全维护"}, "idempotency_key": {"policy-change-0001"},
	}

	repository.account.Role = store.RoleAuditor
	auditor := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-admin", time.Minute)
	if response := performCommerceForm(portal.Handler(), "/admin/commerce/registration-policy", values, auditor); response.Code != http.StatusForbidden {
		t.Fatalf("auditor mutation status = %d, want 403", response.Code)
	}

	repository.account.Role = store.RoleSuperAdmin
	stale := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "csrf-admin", sensitiveActionTTL+time.Second)
	if response := performCommerceForm(portal.Handler(), "/admin/commerce/registration-policy", values, stale); response.Code != http.StatusForbidden {
		t.Fatalf("stale super-admin mutation status = %d, want 403", response.Code)
	}

	fresh := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "csrf-admin", time.Minute)
	badCSRF := url.Values{"csrf": {"wrong"}, "version": {"5"}, "mode": {store.RegistrationClosed}}
	if response := performCommerceForm(portal.Handler(), "/admin/commerce/registration-policy", badCSRF, fresh); response.Code != http.StatusForbidden {
		t.Fatalf("bad-CSRF super-admin mutation status = %d, want 403", response.Code)
	}

	response := performCommerceForm(portal.Handler(), "/admin/commerce/registration-policy", values, fresh)
	if response.Code != http.StatusSeeOther || repository.updatedMode != store.RegistrationClosed || repository.updatedActor != repository.account.ID {
		t.Fatalf("fresh super-admin mutation = status %d mode %q actor %s", response.Code, repository.updatedMode, repository.updatedActor)
	}
}

func TestAdminCommerceChannelPageNeverRendersConfigOrSecretRefs(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	channelID := uuid.MustParse("33333333-3333-4333-8333-333333333333")
	repository := &testChannelAdminRepository{testPortalRepository: newTestPortalRepository(), channelID: channelID}
	repository.account.Role = store.RoleSuperAdmin
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "csrf-admin", time.Minute)

	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=channels&id="+channelID.String(), nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("payment channel page status = %d, want 200; body=%s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, forbidden := range []string{
		"wx-app-1234567890", "merchant-1234567890", "SERIAL-1234567890",
		"user:password", "QUERY-MUST-NOT-ECHO", "PROVIDER-RAW-MUST-NOT-ECHO",
		"PRIVATE-CONFIG-MUST-NOT-ECHO", "ULTRA-SECRET-REF-SHOULD-NOT-ECHO",
	} {
		if strings.Contains(body, forbidden) {
			t.Fatalf("payment channel page leaked %q", forbidden)
		}
	}
	if strings.Contains(body, "certificate valid") {
		t.Fatal("payment channel page rendered a free-form validation summary")
	}
	for _, want := range []string{
		"本地材料和渠道连通性验证通过", "当前活动版本", "当前活动", "TLS_HANDSHAKE_FAILED",
		"wx••••7890", "me••••7890", "SE••••7890", "https://pay.example/notify/wechat",
	} {
		if !strings.Contains(body, want) {
			t.Fatalf("payment channel safe operations summary omitted %q", want)
		}
	}
	if strings.Contains(body, "SecretRefs") || strings.Contains(body, "PublicConfig") {
		t.Fatal("payment channel page rendered an unsafe storage object")
	}
}

func TestAdminCommerceUsageDetailsRenderOnlySafeViews(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	userID := uuid.MustParse("99999999-9999-4999-8999-999999999999")
	registration := &testRegistrationAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		policy:               store.RegistrationPolicy{Mode: store.RegistrationInviteRequired, Version: 3},
		batches: []store.InvitationBatch{{
			ID: uuid.New(), Name: "受控邀请", Status: "ACTIVE", EmailHash: []byte("EMAIL-DIGEST-MUST-NOT-RENDER"),
		}},
		redemptions: []store.InvitationRedemption{{
			ID:     uuid.MustParse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
			CodeID: uuid.MustParse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), UserID: &userID,
			State: "REDEEMED", ReservedUntil: portal.now(), CreatedAt: portal.now(),
		}},
	}
	registration.account.Role = store.RoleAuditor
	portal.config.Repository = registration
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-auditor", time.Minute)
	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=registration", nil, cookie)
	if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "最近邀请码使用明细") || !strings.Contains(response.Body.String(), "受控邀请") {
		t.Fatalf("invitation usage page status=%d body=%s", response.Code, response.Body.String())
	}
	if strings.Contains(response.Body.String(), "EMAIL-DIGEST-MUST-NOT-RENDER") {
		t.Fatal("invitation batch email digest crossed the HTML boundary")
	}

	redemptions := &testRedemptionAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		claims: []store.AccountRedemption{{
			ClaimID: uuid.New(), AccountID: userID, CodePrefix: "TLR-SAFE-PREFIX", PlanKey: "pro",
			CatalogVersion: 24, Status: "ACTIVE", ClaimedAt: portal.now(), StartsAt: portal.now(),
		}},
	}
	redemptions.account.Role = store.RoleAuditor
	portal.config.Repository = redemptions
	response = performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=redemptions", nil, cookie)
	if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "TLR-SAFE-PREFIX") || !strings.Contains(response.Body.String(), "最近兑换核销明细") {
		t.Fatalf("redemption usage page status=%d body=%s", response.Code, response.Body.String())
	}

	templateBytes, err := portalAssets.ReadFile("assets/commerce-admin.html")
	if err != nil {
		t.Fatal(err)
	}
	lower := strings.ToLower(string(templateBytes))
	for _, forbidden := range []string{"emailhash", "email_hash", "providerrefhash", "provider_ref_hash", "attemptid", "attempt_id"} {
		if strings.Contains(lower, forbidden) {
			t.Fatalf("admin commerce template references sensitive usage-detail field %q", forbidden)
		}
	}
}

func TestAdminCommerceOrderOperationsEnforceRoleFieldsAndAuthoritativeState(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	orderID := uuid.MustParse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")
	ownerID := uuid.MustParse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")
	repository := &testPaymentAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		currentOrder:         store.Order{ID: orderID, AccountID: ownerID, OrderNo: "TL-ADMIN-ORDER", Status: "PENDING_PAYMENT"},
	}
	service := &testCommerceService{outcome: commercecontrol.PaymentOutcome{
		Order: store.Order{ID: orderID, AccountID: ownerID, Status: "PENDING_PAYMENT"}, ProviderStatus: "PENDING",
	}}
	portal.config.Repository = repository
	portal.config.Commerce = service
	values := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"管理员主动核对支付渠道状态"}, "idempotency_key": {"admin-query-order-0001"},
	}

	repository.account.Role = store.RoleAuditor
	auditor := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-admin", time.Minute)
	denied := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/query", values, auditor)
	if denied.Code != http.StatusForbidden || service.queriedOrder != uuid.Nil {
		t.Fatalf("auditor query status=%d queried=%s, want 403 and no operation", denied.Code, service.queriedOrder)
	}

	repository.account.Role = store.RoleAdmin
	admin := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	unknown := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"管理员主动核对支付渠道状态"},
		"idempotency_key": {"admin-query-order-0002"}, "account_id": {uuid.NewString()},
	}
	rejected := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/query", unknown, admin)
	if rejected.Code != http.StatusBadRequest || service.queriedOrder != uuid.Nil {
		t.Fatalf("query with unknown field status=%d queried=%s", rejected.Code, service.queriedOrder)
	}

	response := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/query", values, admin)
	if response.Code != http.StatusSeeOther || repository.getOrderID != orderID || service.queriedAccount != ownerID || service.queriedOrder != orderID {
		t.Fatalf("query status=%d get=%s service-account=%s service-order=%s", response.Code, repository.getOrderID, service.queriedAccount, service.queriedOrder)
	}

	repository.currentOrder.Status = "CREATED"
	retryValues := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"重试已支付订单的幂等履约"}, "idempotency_key": {"admin-fulfill-order-0001"},
	}
	conflict := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/retry-fulfillment", retryValues, admin)
	if conflict.Code != http.StatusConflict || repository.fulfillOrderID != uuid.Nil {
		t.Fatalf("unpaid fulfillment status=%d fulfill=%s", conflict.Code, repository.fulfillOrderID)
	}
	repository.currentOrder.Status = "PAID"
	retried := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/retry-fulfillment", retryValues, admin)
	if retried.Code != http.StatusSeeOther || repository.fulfillOrderID != orderID {
		t.Fatalf("paid fulfillment status=%d fulfill=%s", retried.Code, repository.fulfillOrderID)
	}
}

func TestAdminCommerceCloseOrderUsesDedicatedAtomicIdempotency(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	orderID := uuid.MustParse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")
	repository := &testPaymentAdminRepository{testPortalRepository: newTestPortalRepository()}
	repository.account.Role = store.RoleAdmin
	repository.closeResult = store.AdminCloseOrderResult{Order: store.Order{ID: orderID, Status: "CLOSED"}}
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	values := url.Values{
		"csrf": {"csrf-admin"}, "reason_code": {"payment_timeout"}, "reason": {"支付截止后执行安全关单"},
		"idempotency_key": {"admin-close-order-0001"},
	}

	response := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/close", values, cookie)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("admin close status=%d, want 303; body=%s", response.Code, response.Body.String())
	}
	input := repository.closeInput
	wantHash := adminCommerceCloseRequestHash(repository.account.ID, orderID, "PAYMENT_TIMEOUT", "支付截止后执行安全关单")
	if input.ActorID != repository.account.ID || input.OrderID != orderID || input.ReasonCode != "PAYMENT_TIMEOUT" || input.RequestHash != wantHash {
		t.Fatalf("admin close input mismatch: %+v", input)
	}
	if input.IdempotencyKey != adminCommerceCloseStoreKey("admin-close-order-0001") || input.IdempotencyKey == "admin-close-order-0001" {
		t.Fatalf("admin close did not isolate Store idempotency from sensitiveAction: %q", input.IdempotencyKey)
	}

	repository.closeInput = store.AdminCloseOrderInput{}
	invalid := performCommerceForm(portal.Handler(), "/admin/commerce/orders/"+orderID.String()+"/close", url.Values{
		"csrf": {"csrf-admin"}, "reason_code": {"PAYMENT_TIMEOUT"}, "reason": {"支付截止后执行安全关单"},
		"idempotency_key": {"admin-close-order-0002"}, "account_id": {uuid.NewString()},
	}, cookie)
	if invalid.Code != http.StatusBadRequest || repository.closeInput.OrderID != uuid.Nil {
		t.Fatalf("close with unknown field status=%d input=%+v", invalid.Code, repository.closeInput)
	}

	views := adminCommerceOrderViews([]store.Order{{Status: "CREATED"}, {Status: "PENDING_PAYMENT"}, {Status: "PAID"}})
	if !views[0].Closable || !views[1].Closable || views[2].Closable {
		t.Fatalf("admin close visibility mismatch: %+v", views)
	}
}

func TestAdminCommerceCatalogMutationsValidatePreserveAndPublish(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	planID := uuid.MustParse("ffffffff-ffff-4fff-8fff-ffffffffffff")
	repository := &testCatalogAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		draft: &store.CommerceCatalogDraft{ID: uuid.New(), Version: 41, Plans: []store.PlanDraft{{
			ID: planID, Key: "pro", DisplayName: "专业版", BillingPeriod: "MONTHLY",
			FeatureFlags:     json.RawMessage(`{"private":"FEATURE-MUST-STAY-SERVER-SIDE"}`),
			ChannelProducts:  json.RawMessage(`{"sku":"CHANNEL-MUST-STAY-SERVER-SIDE"}`),
			VisiblePlatforms: []string{"ANDROID", "WEB", "WINDOWS"}, PriceMinor: 1200, Currency: "CNY",
		}}},
	}
	repository.account.Role = store.RoleAdmin
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	form := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"创建新的商业套餐草稿"}, "idempotency_key": {"catalog-plan-create-0001"},
		"plan_key": {"team"}, "display_name": {"团队版"}, "description": {"团队远程能力"},
		"billing_period": {"monthly"}, "sort_order": {"20"}, "visible_platforms": {"android/web/windows"},
		"available_from": {"2026-09-01T00:00"}, "available_until": {"2026-10-01T00:00"},
		"fallback_plan_key": {"free"}, "price_minor": {"3200"}, "currency": {"cny"},
	}
	created := performCommerceForm(portal.Handler(), "/admin/commerce/catalog/plans", form, cookie)
	if created.Code != http.StatusSeeOther {
		t.Fatalf("catalog create status=%d, want 303; body=%s", created.Code, created.Body.String())
	}
	input := repository.createdInput
	if repository.createdActor != repository.account.ID || repository.createdDevice != uuid.Nil || input.PlanKey != "team" ||
		input.BillingPeriod != "MONTHLY" || input.MaxRemotePCs != store.UnlimitedQuota || input.MaxLiveSessions != store.UnlimitedQuota ||
		string(input.FeatureFlags) != "{}" || string(input.ChannelProducts) != "{}" || strings.Join(input.VisiblePlatforms, ",") != "ANDROID,WEB,WINDOWS" {
		t.Fatalf("catalog create input mismatch: actor=%s device=%s input=%+v", repository.createdActor, repository.createdDevice, input)
	}

	update := form
	update.Set("reason", "修改套餐展示字段但保留高级配置")
	update.Set("idempotency_key", "catalog-plan-update-0001")
	update.Set("plan_key", "pro")
	update.Set("display_name", "专业增强版")
	updated := performCommerceForm(portal.Handler(), "/admin/commerce/catalog/plans/"+planID.String(), update, cookie)
	if updated.Code != http.StatusSeeOther || repository.updatedPlan != planID || repository.updatedDevice != uuid.Nil {
		t.Fatalf("catalog update status=%d plan=%s device=%s", updated.Code, repository.updatedPlan, repository.updatedDevice)
	}
	if string(repository.updatedInput.FeatureFlags) != `{"private":"FEATURE-MUST-STAY-SERVER-SIDE"}` ||
		string(repository.updatedInput.ChannelProducts) != `{"sku":"CHANNEL-MUST-STAY-SERVER-SIDE"}` {
		t.Fatal("catalog update did not preserve server-side feature/channel configuration")
	}

	page := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=catalog", nil, cookie)
	if page.Code != http.StatusOK {
		t.Fatalf("catalog page status=%d body=%s", page.Code, page.Body.String())
	}
	if strings.Contains(page.Body.String(), "FEATURE-MUST-STAY-SERVER-SIDE") || strings.Contains(page.Body.String(), "CHANNEL-MUST-STAY-SERVER-SIDE") {
		t.Fatal("catalog page rendered server-only configuration")
	}

	published := performCommerceForm(portal.Handler(), "/admin/commerce/catalog/publish", url.Values{
		"csrf": {"csrf-admin"}, "reason": {"校验通过后发布套餐目录"}, "idempotency_key": {"catalog-publish-0001"},
	}, cookie)
	if published.Code != http.StatusSeeOther || repository.publishCalls != 1 || repository.publishedActor != repository.account.ID || repository.publishedDevice != uuid.Nil {
		t.Fatalf("catalog publish status=%d calls=%d actor=%s device=%s", published.Code, repository.publishCalls, repository.publishedActor, repository.publishedDevice)
	}
}

func TestAdminCommerceCatalogWritesRejectAuditorAndUnknownFields(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testCatalogAdminRepository{testPortalRepository: newTestPortalRepository()}
	repository.account.Role = store.RoleAuditor
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-auditor", time.Minute)
	form := url.Values{
		"csrf": {"csrf-auditor"}, "reason": {"不应允许审计员创建套餐"}, "idempotency_key": {"catalog-plan-denied-0001"},
		"plan_key": {"team"}, "display_name": {"团队版"}, "description": {"团队远程能力"},
		"billing_period": {"MONTHLY"}, "sort_order": {"20"}, "visible_platforms": {"ANDROID,WEB"},
		"price_minor": {"3200"}, "currency": {"CNY"},
	}
	denied := performCommerceForm(portal.Handler(), "/admin/commerce/catalog/plans", form, cookie)
	if denied.Code != http.StatusForbidden || repository.createdInput.PlanKey != "" {
		t.Fatalf("auditor catalog create status=%d input=%+v", denied.Code, repository.createdInput)
	}

	repository.account.Role = store.RoleAdmin
	admin := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	form.Set("csrf", "csrf-admin")
	form.Set("idempotency_key", "catalog-plan-invalid-0001")
	form.Set("feature_flags", `{"escalate":true}`)
	rejected := performCommerceForm(portal.Handler(), "/admin/commerce/catalog/plans", form, admin)
	if rejected.Code != http.StatusBadRequest || repository.createdInput.PlanKey != "" {
		t.Fatalf("catalog unknown-field status=%d input=%+v", rejected.Code, repository.createdInput)
	}
}

func TestParseCommercePlanDraftFormRejectsInvalidCatalogFields(t *testing.T) {
	base := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"校验套餐草稿字段"}, "idempotency_key": {"catalog-parse-0001"},
		"plan_key": {"pro"}, "display_name": {"专业版"}, "description": {"远程能力"},
		"billing_period": {"MONTHLY"}, "sort_order": {"0"}, "visible_platforms": {"ANDROID,WEB,WINDOWS"},
		"available_from": {"2026-09-01T00:00"}, "available_until": {"2026-10-01T00:00"},
		"price_minor": {"1200"}, "currency": {"CNY"},
	}
	tests := []struct {
		name   string
		mutate func(url.Values)
	}{
		{name: "unsafe plan key", mutate: func(values url.Values) { values.Set("plan_key", "pro/../../") }},
		{name: "unsupported platform", mutate: func(values url.Values) { values.Set("visible_platforms", "ANDROID,LINUX") }},
		{name: "duplicate platform", mutate: func(values url.Values) { values.Set("visible_platforms", "WEB,web") }},
		{name: "reversed availability", mutate: func(values url.Values) { values.Set("available_until", "2026-08-01T00:00") }},
		{name: "free plan with price", mutate: func(values url.Values) { values.Set("billing_period", "FREE") }},
		{name: "unknown field", mutate: func(values url.Values) { values.Set("feature_flags", "{}") }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			values := make(url.Values, len(base))
			for key, items := range base {
				values[key] = append([]string(nil), items...)
			}
			test.mutate(values)
			if _, err := parseCommercePlanDraftForm(values, nil); err == nil {
				t.Fatal("invalid catalog fields were accepted")
			}
		})
	}
}

func TestAdminManualEntitlementPageIsScopedReadOnlyForAuditor(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	accountID := uuid.MustParse("12121212-1212-4212-8212-121212121212")
	grantID := uuid.MustParse("13131313-1313-4313-8313-131313131313")
	repository := &testManualEntitlementAdminRepository{
		testPortalRepository: newTestPortalRepository(),
		grants: []store.ManualEntitlementGrant{{
			ID: grantID, AccountID: accountID, PlanKey: "pro", CatalogVersion: 24,
			StartsAt: portal.now(), EndsAt: portal.now().Add(30 * 24 * time.Hour), Status: "ACTIVE",
			CreatedAt: portal.now(), UpdatedAt: portal.now(),
		}},
	}
	repository.account.Role = store.RoleAuditor
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-auditor", time.Minute)
	response := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=entitlements&account_id="+accountID.String(), nil, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("manual entitlement page status=%d body=%s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, want := range []string{"独立人工权益流程", "人工权益记录", "pro", "不会把订单改成已支付"} {
		if !strings.Contains(body, want) {
			t.Errorf("manual entitlement page omitted %q", want)
		}
	}
	if repository.listActor != repository.account.ID || repository.listFilter.AccountID != accountID || repository.listFilter.Limit != commercePageLimit {
		t.Fatalf("manual entitlement list scope actor=%s filter=%+v", repository.listActor, repository.listFilter)
	}
	if strings.Contains(body, `action="/admin/commerce/manual-entitlements`) || strings.Contains(body, "撤销并重算权益") {
		t.Fatal("auditor page rendered a manual entitlement mutation")
	}
	for _, forbidden := range []string{"idempotency_key", "trace_id", "actor_device_id", "provider_reference"} {
		if strings.Contains(strings.ToLower(body), forbidden) {
			t.Fatalf("manual entitlement page crossed sensitive field %q", forbidden)
		}
	}

	invalid := performRequest(portal.Handler(), http.MethodGet, "/admin/commerce?section=entitlements&unexpected=value", nil, cookie)
	if invalid.Code != http.StatusBadRequest {
		t.Fatalf("manual entitlement unknown filter status=%d, want 400", invalid.Code)
	}
}

func TestAdminManualEntitlementMutationsUseStrongAuthStrictFieldsAndStoreFence(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	targetID := uuid.MustParse("14141414-1414-4414-8414-141414141414")
	grantID := uuid.MustParse("15151515-1515-4515-8515-151515151515")
	repository := &testManualEntitlementAdminRepository{testPortalRepository: newTestPortalRepository()}
	portal.config.Repository = repository
	create := url.Values{
		"csrf": {"csrf-admin"}, "account_id": {targetID.String()}, "plan_key": {"pro"}, "duration_days": {"30"},
		"reason": {"客户支持审批通过后赠送临时权益"}, "idempotency_key": {"manual-entitlement-create-0001"},
	}

	repository.account.Role = store.RoleAuditor
	auditor := commerceSessionCookie(t, portal, store.RoleAuditor, "csrf-admin", time.Minute)
	denied := performCommerceForm(portal.Handler(), "/admin/commerce/manual-entitlements", create, auditor)
	if denied.Code != http.StatusForbidden || repository.createCalls != 0 {
		t.Fatalf("auditor manual grant status=%d calls=%d", denied.Code, repository.createCalls)
	}

	repository.account.Role = store.RoleAdmin
	admin := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	unknown := url.Values{
		"csrf": {"csrf-admin"}, "account_id": {targetID.String()}, "plan_key": {"pro"}, "duration_days": {"30"},
		"reason": {"客户支持审批通过后赠送临时权益"}, "idempotency_key": {"manual-entitlement-create-0002"},
		"catalog_version": {"999"},
	}
	rejected := performCommerceForm(portal.Handler(), "/admin/commerce/manual-entitlements", unknown, admin)
	if rejected.Code != http.StatusBadRequest || repository.createCalls != 0 {
		t.Fatalf("manual grant unknown-field status=%d calls=%d", rejected.Code, repository.createCalls)
	}

	created := performCommerceForm(portal.Handler(), "/admin/commerce/manual-entitlements", create, admin)
	if created.Code != http.StatusSeeOther || repository.createCalls != 1 {
		t.Fatalf("manual grant create status=%d calls=%d body=%s", created.Code, repository.createCalls, created.Body.String())
	}
	input := repository.createInput
	if repository.createActor != repository.account.ID || input.AccountID != targetID || input.PlanKey != "pro" || input.DurationDays != 30 ||
		input.Reason != "客户支持审批通过后赠送临时权益" || input.IdempotencyKey != manualEntitlementStoreKey("create", "manual-entitlement-create-0001") ||
		input.IdempotencyKey == "manual-entitlement-create-0001" || input.TraceID == "" {
		t.Fatalf("manual grant create input mismatch: actor=%s input=%+v", repository.createActor, input)
	}
	landing := performRequest(portal.Handler(), http.MethodGet, created.Header().Get("Location"), nil, admin)
	if landing.Code != http.StatusOK || !strings.Contains(landing.Body.String(), "人工权益已创建并完成权益重算") {
		t.Fatalf("manual grant create landing status=%d body=%s", landing.Code, landing.Body.String())
	}

	revoke := url.Values{
		"csrf": {"csrf-admin"}, "reason": {"客户支持审批撤回，撤销临时权益"},
		"idempotency_key": {"manual-entitlement-revoke-0001"},
	}
	revoked := performCommerceForm(portal.Handler(), "/admin/commerce/manual-entitlements/"+grantID.String()+"/revoke", revoke, admin)
	if revoked.Code != http.StatusSeeOther || repository.revokeCalls != 1 {
		t.Fatalf("manual grant revoke status=%d calls=%d body=%s", revoked.Code, repository.revokeCalls, revoked.Body.String())
	}
	if repository.revokeActor != repository.account.ID || repository.revokeInput.GrantID != grantID ||
		repository.revokeInput.Reason != "客户支持审批撤回，撤销临时权益" ||
		repository.revokeInput.IdempotencyKey != manualEntitlementStoreKey("revoke", "manual-entitlement-revoke-0001") ||
		repository.revokeInput.IdempotencyKey == "manual-entitlement-revoke-0001" || repository.revokeInput.TraceID == "" {
		t.Fatalf("manual grant revoke input mismatch: actor=%s input=%+v", repository.revokeActor, repository.revokeInput)
	}
}

func TestAdminManualEntitlementRejectsInvalidBusinessFields(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	repository := &testManualEntitlementAdminRepository{testPortalRepository: newTestPortalRepository()}
	repository.account.Role = store.RoleAdmin
	portal.config.Repository = repository
	cookie := commerceSessionCookie(t, portal, store.RoleAdmin, "csrf-admin", time.Minute)
	base := url.Values{
		"csrf": {"csrf-admin"}, "account_id": {"abcdefab-cdef-4abc-8def-abcdefabcdef"}, "plan_key": {"pro"}, "duration_days": {"30"},
		"reason": {"校验人工权益输入字段"}, "idempotency_key": {"manual-entitlement-invalid-0001"},
	}
	tests := []struct {
		name   string
		mutate func(url.Values)
	}{
		{name: "noncanonical account", mutate: func(values url.Values) { values.Set("account_id", strings.ToUpper(values.Get("account_id"))) }},
		{name: "unsafe plan", mutate: func(values url.Values) { values.Set("plan_key", "pro/../../") }},
		{name: "zero days", mutate: func(values url.Values) { values.Set("duration_days", "0") }},
		{name: "too many days", mutate: func(values url.Values) { values.Set("duration_days", "366") }},
	}
	for index, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			values := make(url.Values, len(base))
			for key, items := range base {
				values[key] = append([]string(nil), items...)
			}
			values.Set("idempotency_key", fmt.Sprintf("manual-entitlement-invalid-%04d", index+10))
			test.mutate(values)
			response := performCommerceForm(portal.Handler(), "/admin/commerce/manual-entitlements", values, cookie)
			if response.Code != http.StatusBadRequest {
				t.Fatalf("invalid manual entitlement status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
	if repository.createCalls != 0 {
		t.Fatalf("invalid manual entitlement reached repository %d times", repository.createCalls)
	}
}

func TestAdminPaymentVersionRequiresServerSideRealValidation(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	channelID := uuid.MustParse("33333333-3333-4333-8333-333333333333")
	repository := &testChannelAdminRepository{testPortalRepository: newTestPortalRepository(), channelID: channelID}
	repository.account.Role = store.RoleSuperAdmin
	validator := &testCommerceService{}
	portal.config.Repository = repository
	portal.config.Commerce = validator
	cookie := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "csrf-admin", time.Minute)

	response := performCommerceForm(portal.Handler(), "/admin/commerce/payment-channels/"+channelID.String()+"/versions", url.Values{
		"csrf": {"csrf-admin"}, "environment": {"PRODUCTION"},
		"public_config": {`{"app_id":"public-id"}`},
		"secret_refs":   {`{"merchant_private_key":"docker-secret://merchant-key"}`},
		"reason":        {"轮换生产支付配置"}, "idempotency_key": {"payment-version-0001"},
	}, cookie)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("create-and-validate payment version status = %d, want 303; body=%s", response.Code, response.Body.String())
	}
	if validator.validationCalls != 1 || validator.probeCalls != 1 || validator.validated.Status != "DRAFT" || !repository.validationResult {
		t.Fatalf("payment version was not validated and probed through the injected commerce service: validation=%d probe=%d status=%q recorded=%t", validator.validationCalls, validator.probeCalls, validator.validated.Status, repository.validationResult)
	}
	if strings.Contains(repository.createdVersion.ValidationSummary, "merchant-key") {
		t.Fatal("payment validation summary retained a Secret reference")
	}
}

func TestAdminPaymentVersionProbeFailureRemainsDraftWithoutProviderErrorDisclosure(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	channelID := uuid.MustParse("33333333-3333-4333-8333-333333333333")
	repository := &testChannelAdminRepository{testPortalRepository: newTestPortalRepository(), channelID: channelID}
	repository.account.Role = store.RoleSuperAdmin
	service := &testCommerceService{probeErr: errors.New("provider raw response with internal details")}
	portal.config.Repository = repository
	portal.config.Commerce = service
	cookie := commerceSessionCookie(t, portal, store.RoleSuperAdmin, "csrf-admin", time.Minute)

	response := performCommerceForm(portal.Handler(), "/admin/commerce/payment-channels/"+channelID.String()+"/versions", url.Values{
		"csrf": {"csrf-admin"}, "environment": {"PRODUCTION"},
		"public_config": {`{"app_id":"public-id"}`}, "secret_refs": {`{"merchant_private_key":"docker-secret://merchant-key"}`},
		"reason": {"验证支付渠道异常降级"}, "idempotency_key": {"payment-version-0002"},
	}, cookie)
	if response.Code != http.StatusInternalServerError {
		t.Fatalf("failed probe status=%d, want generic 500", response.Code)
	}
	if repository.validationResult || repository.createdVersion.ValidationSummary != "PROVIDER_CONNECTIVITY_PROBE_FAILED" {
		t.Fatalf("probe failure was not retained as fixed-category DRAFT: valid=%t summary=%q", repository.validationResult, repository.createdVersion.ValidationSummary)
	}
	if strings.Contains(response.Body.String(), "provider raw response") || strings.Contains(response.Body.String(), "internal details") {
		t.Fatal("raw provider error was disclosed in the HTML response")
	}
}

func TestGeneratedCommerceCodesAreHighEntropyCompatibleAndUnique(t *testing.T) {
	portal := newTestPortal(t, &testOIDCFlow{})
	portal.config.CommerceKey = []byte("commerce-key-0123456789abcdef012345")
	for _, test := range []struct {
		prefix, purpose, shape string
		length                 int
	}{
		{prefix: "TLI", purpose: "invitation-code", shape: `^TLI(?:-[A-HJ-NP-Z2-9]{4}){3}$`, length: 15},
		{prefix: "TLR", purpose: "redemption-code", shape: `^TLR(?:-[A-HJ-NP-Z2-9]{7}){3}$`, length: 24},
	} {
		raw, records, err := portal.generateCommerceCodes(test.prefix, test.purpose, 40, 1)
		if err != nil {
			t.Fatal(err)
		}
		if len(raw) != 40 || len(records) != 40 {
			t.Fatalf("generated %d raw / %d records, want 40", len(raw), len(records))
		}
		seen := map[[32]byte]bool{}
		for index, code := range raw {
			normalized := normalizePortalCommerceCode(code)
			if matched, _ := regexp.MatchString(test.shape, code); !matched || len(normalized) != test.length ||
				!validPortalCommerceCode(normalized, test.prefix) || records[index].Digest != portalCommerceDigest(portal.config.CommerceKey, test.purpose, normalized) {
				t.Fatalf("generated %s code %d is incompatible: %q", test.prefix, index, code)
			}
			if seen[records[index].Digest] {
				t.Fatalf("generated duplicate %s digest at index %d", test.prefix, index)
			}
			seen[records[index].Digest] = true
		}
	}
	if !validPortalCommerceCode("TLIABCDEFGHJKLMNPQRSTUVWXYZ", "TLI") {
		t.Fatal("legacy long invitation code is no longer accepted")
	}
}

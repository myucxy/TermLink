package webportal

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/google/uuid"
	commercecontrol "github.com/termlink/termlink/server/internal/commerce"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	commercePageLimit    = 100
	maxGeneratedCodeRows = 200
)

type commerceAccountRepository interface {
	PublishedPlans(context.Context) (int64, []store.Plan, error)
	ListOrders(context.Context, uuid.UUID, int) ([]store.Order, error)
	PreviewRedemption(context.Context, uuid.UUID, [32]byte) (store.RedemptionPreview, error)
	ClaimRedemption(context.Context, uuid.UUID, [32]byte, string) (store.RedemptionClaim, store.Entitlement, error)
	ListAccountRedemptions(context.Context, uuid.UUID, int) ([]store.AccountRedemption, error)
	ListAccountRefunds(context.Context, uuid.UUID, int) ([]store.Refund, error)
	CancelOrder(context.Context, uuid.UUID, uuid.UUID) (store.Order, error)
}

type userPortalRepository interface {
	GetUserPortalAccountOverview(context.Context, uuid.UUID) (store.UserPortalAccountOverview, error)
	GetUserPortalEntitlement(context.Context, uuid.UUID) (store.UserPortalEntitlement, error)
	ListUserPortalDevices(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalDevice, error)
	ListUserPortalPCs(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalPC, error)
	ListUserPortalSessions(context.Context, uuid.UUID, store.UserPortalPageRequest) ([]store.UserPortalSession, error)
	ListUserPortalSecurityActivity(context.Context, uuid.UUID, store.UserPortalAuditPageRequest) ([]store.UserPortalSecurityActivity, error)
}

type commerceRegistrationAdminRepository interface {
	GetRegistrationPolicy(context.Context) (store.RegistrationPolicy, error)
	UpdateRegistrationPolicy(context.Context, uuid.UUID, string, int64, string) (store.RegistrationPolicy, error)
	CreateInvitationBatch(context.Context, uuid.UUID, store.InvitationBatchInput, string) (store.InvitationBatch, error)
	AddInvitationCodes(context.Context, uuid.UUID, uuid.UUID, []store.CodeDigestRecord, string) ([]store.InvitationCode, error)
	ListInvitationBatches(context.Context, uuid.UUID, int) ([]store.InvitationBatch, error)
	ListInvitationCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.InvitationCode, error)
	ListInvitationRedemptions(context.Context, uuid.UUID, int) ([]store.InvitationRedemption, error)
	SetInvitationBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
	SetInvitationCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
}

type commerceRedemptionAdminRepository interface {
	CreateRedemptionBatch(context.Context, uuid.UUID, store.RedemptionBatchInput, string) (store.RedemptionBatch, error)
	AddRedemptionCodes(context.Context, uuid.UUID, uuid.UUID, []store.CodeDigestRecord, string) ([]store.RedemptionCode, error)
	ListRedemptionBatches(context.Context, uuid.UUID, int) ([]store.RedemptionBatch, error)
	ListRedemptionCodes(context.Context, uuid.UUID, uuid.UUID, int) ([]store.RedemptionCode, error)
	ListRedemptionClaims(context.Context, uuid.UUID, int) ([]store.AccountRedemption, error)
	SetRedemptionBatchStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
	SetRedemptionCodeStatus(context.Context, uuid.UUID, uuid.UUID, string, string) error
}

type commercePaymentAdminRepository interface {
	ListCommerceOrders(context.Context, uuid.UUID, int) ([]store.Order, error)
	ListCommerceTransactions(context.Context, uuid.UUID, int) ([]store.PaymentTransaction, error)
	ListPaymentNotifications(context.Context, uuid.UUID, int) ([]store.PaymentNotification, error)
	ListRefunds(context.Context, uuid.UUID, int) ([]store.Refund, error)
	ListReconciliationRuns(context.Context, uuid.UUID, int) ([]store.ReconciliationRun, error)
	CreateReconciliationRun(context.Context, uuid.UUID, string, time.Time, time.Time, string) (store.ReconciliationRun, error)
}

type commerceChannelAdminRepository interface {
	ListPaymentChannels(context.Context, uuid.UUID) ([]store.PaymentChannel, error)
	ListPaymentChannelVersions(context.Context, uuid.UUID, uuid.UUID) ([]store.PaymentChannelVersion, error)
	EnsurePaymentChannel(context.Context, uuid.UUID, string, string, string) (store.PaymentChannel, error)
	CreatePaymentChannelVersion(context.Context, uuid.UUID, uuid.UUID, store.PaymentChannelVersionInput, string) (store.PaymentChannelVersion, error)
	ValidatePaymentChannelVersion(context.Context, uuid.UUID, uuid.UUID, bool, string, string) (store.PaymentChannelVersion, error)
	ActivatePaymentChannelVersion(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, string) (store.PaymentChannel, error)
	DisablePaymentChannel(context.Context, uuid.UUID, uuid.UUID, string) (store.PaymentChannel, error)
}

type commerceChannelValidationTraceRepository interface {
	ListPaymentChannelValidationTraces(context.Context, uuid.UUID, uuid.UUID) ([]store.PaymentChannelValidationTrace, error)
}

type commercePaymentCertificateHealthSource interface {
	CachedPaymentCertificateHealth(provider, scene string) (commercecontrol.PaymentCertificateHealth, bool)
}

type commercePaymentOperationsRepository interface {
	GetOrderByID(context.Context, uuid.UUID) (store.Order, error)
	FulfillOrder(context.Context, uuid.UUID) (store.Order, store.Entitlement, error)
}

type commerceAdminCloseRepository interface {
	AdminCloseUnpaidOrderFromWeb(context.Context, store.AdminCloseOrderInput) (store.AdminCloseOrderResult, error)
}

type commerceManualEntitlementAdminRepository interface {
	ListManualEntitlementGrants(context.Context, uuid.UUID, store.ManualEntitlementGrantFilter) ([]store.ManualEntitlementGrant, error)
	CreateManualEntitlementGrantFromWeb(context.Context, uuid.UUID, store.CreateManualEntitlementGrantInput) (store.ManualEntitlementGrant, error)
	RevokeManualEntitlementGrantFromWeb(context.Context, uuid.UUID, store.RevokeManualEntitlementGrantInput) (store.ManualEntitlementGrant, error)
}

type commercePublishedPlansRepository interface {
	PublishedPlans(context.Context) (int64, []store.Plan, error)
}

type commerceRuntimeRepository interface {
	GetActivePaymentConfiguration(context.Context, string, string) (store.PaymentChannel, store.PaymentChannelVersion, error)
}

type commerceCatalogAdminRepository interface {
	GetCommerceCatalogDraft(context.Context, uuid.UUID) (*store.CommerceCatalogDraft, error)
	CreatePlanDraft(context.Context, uuid.UUID, uuid.UUID, store.PlanDraftInput, string) (store.PlanDraft, error)
	UpdatePlanDraft(context.Context, uuid.UUID, uuid.UUID, uuid.UUID, store.PlanDraftInput, string) (store.PlanDraft, error)
	PublishCatalog(context.Context, uuid.UUID, uuid.UUID, string) (int64, error)
}

type commerceOrderView struct {
	ID           uuid.UUID
	OrderNo      string
	PlanKey      string
	Provider     string
	PaymentScene string
	AmountMinor  int64
	Currency     string
	Status       string
	ExpiresAt    time.Time
	PaidAt       *time.Time
	FulfilledAt  *time.Time
	ClosedAt     *time.Time
	CreatedAt    time.Time
	Cancelable   bool
	Payable      bool
	Queryable    bool
}

type adminCommerceOrderView struct {
	ID           uuid.UUID
	AccountID    uuid.UUID
	OrderNo      string
	PlanKey      string
	Provider     string
	PaymentScene string
	AmountMinor  int64
	Currency     string
	Status       string
	ExpiresAt    time.Time
	PaidAt       *time.Time
	FulfilledAt  *time.Time
	ClosedAt     *time.Time
	CreatedAt    time.Time
	Queryable    bool
	FulfillRetry bool
	Closable     bool
}

type invitationRedemptionView struct {
	RecordReference    string
	CodePrefix         string
	BatchName          string
	AccountReference   string
	Username           string
	Nickname           string
	EmailMasked        string
	HasIdentityProfile bool
	HasUser            bool
	State              string
	ReservedUntil      time.Time
	ProviderAcceptedAt *time.Time
	RedeemedAt         *time.Time
	ReleasedAt         *time.Time
	CreatedAt          time.Time
}

type redemptionClaimView struct {
	ClaimID        uuid.UUID
	AccountID      uuid.UUID
	CodePrefix     string
	PlanKey        string
	CatalogVersion int64
	ClaimedAt      time.Time
	StartsAt       time.Time
	EndsAt         *time.Time
	Status         string
}

type commercePlanView struct {
	Key           string
	DisplayName   string
	Description   string
	BillingPeriod string
	PriceMinor    int64
	Currency      string
	Purchasable   bool
}

type commercePaymentInstructionView struct {
	OrderID     uuid.UUID
	OrderNo     string
	Provider    string
	Kind        string
	DisplayURL  string
	RedirectURL string
	ExpiresAt   time.Time
}

type commercePaymentResultView struct {
	OrderNo        string
	OrderStatus    string
	ProviderStatus string
	Fulfilled      bool
}

// commerceRefundView is the browser boundary for refund operations. Provider
// reference hashes, requester/approver identities and gateway details are
// intentionally omitted.
type commerceRefundView struct {
	ID                uuid.UUID
	OrderID           uuid.UUID
	RefundNo          string
	AmountMinor       int64
	Reason            string
	Status            string
	FailureCode       string
	Version           int64
	RequestedAt       time.Time
	ApprovalExpiresAt *time.Time
	ApprovedAt        *time.Time
	CompletedAt       *time.Time
	CanConfirm        bool
	ApprovalExpired   bool
}

type commerceAccountRefundView struct {
	ID          uuid.UUID
	OrderID     uuid.UUID
	RefundNo    string
	AmountMinor int64
	Status      string
	RequestedAt time.Time
	CompletedAt *time.Time
}

type manualEntitlementGrantView struct {
	ID             uuid.UUID
	AccountID      uuid.UUID
	PlanKey        string
	CatalogVersion int64
	StartsAt       time.Time
	EndsAt         time.Time
	Status         string
	CreatedAt      time.Time
	UpdatedAt      time.Time
	Revocable      bool
}

type commerceAccountPageData struct {
	AccountID                   uuid.UUID
	Username                    string
	Nickname                    string
	Email                       string
	Role                        store.Role
	RoleLabel                   string
	CanOpenConsole              bool
	ConsoleURL                  string
	IdentityAccountURL          string
	CSRF                        string
	Section                     string
	SectionTitle                string
	CatalogVersion              int64
	Plans                       []commercePlanView
	Entitlement                 store.UserPortalEntitlement
	Overview                    store.UserPortalAccountOverview
	Devices                     []store.UserPortalDevice
	PCs                         []store.UserPortalPC
	Sessions                    []store.UserPortalSession
	SecurityActivities          []store.UserPortalSecurityActivity
	Orders                      []commerceOrderView
	RedemptionPreview           *store.RedemptionPreview
	Redemptions                 []redemptionClaimView
	Refunds                     []commerceAccountRefundView
	IdempotencyKey              string
	Notice                      string
	Claimed                     bool
	CanPurchase                 bool
	PaymentMethods              []portalPaymentMethodView
	Payment                     *commercePaymentInstructionView
	PaymentResult               *commercePaymentResultView
	SensitiveActionsAvailable   bool
	PCPolicies                  []store.UserPortalPCControlPolicy
	PrivacyPreflight            store.UserPortalAccountDeletionPreflight
	DataExport                  *store.UserPortalDataExport
	DataExportDownloadAvailable bool
	AccountDeletion             *store.UserPortalAccountDeletion
	ProfileChangeAvailable      bool
	EmailChangeAvailable        bool
	PendingEmail                string
	EmailChallengeID            uuid.UUID
	EmailChallengeExpiresAt     time.Time
	Announcements               []store.WebAnnouncement
}

type commercePaymentVersionView struct {
	ID                   uuid.UUID
	ChannelID            uuid.UUID
	Version              int64
	Environment          string
	Status               string
	ValidatedAt          *time.Time
	ValidationLabel      string
	CreatedAt            time.Time
	Active               bool
	AppIDMasked          string
	MerchantIDMasked     string
	NotifyURL            string
	MerchantSerialMasked string
	ValidationCode       string
	ValidationTraceID    string
}

type commercePaymentChannelView struct {
	ID                     uuid.UUID
	Provider               string
	Scene                  string
	Enabled                bool
	ActiveVersionID        *uuid.UUID
	HealthStatus           string
	LastWebhookAt          *time.Time
	LastErrorCode          string
	CertificateStatus      string
	CertificateExpiryAt    *time.Time
	CertificateRemaining   int
	CertificateLastRefresh *time.Time
}

type adminCommercePageData struct {
	Username                string
	Role                    store.Role
	RoleLabel               string
	CSRF                    string
	Section                 string
	SectionTitle            string
	CanOperate              bool
	CanConfigure            bool
	CanRefund               bool
	CanCreateVersion        bool
	CanConfigureSystem      bool
	CommerceNavigation      bool
	Policy                  store.RegistrationPolicy
	InvitationBatches       []store.InvitationBatch
	InvitationCodes         []store.InvitationCode
	InvitationRedemptions   []invitationRedemptionView
	RedemptionBatches       []store.RedemptionBatch
	RedemptionCodes         []store.RedemptionCode
	RedemptionClaims        []redemptionClaimView
	ManualGrants            []manualEntitlementGrantView
	ManualGrantPlans        []commercePlanView
	ManualGrantAccount      string
	Orders                  []adminCommerceOrderView
	Transactions            []store.PaymentTransaction
	Notifications           []store.PaymentNotification
	Refunds                 []commerceRefundView
	Reconciliations         []store.ReconciliationRun
	Channels                []commercePaymentChannelView
	ChannelVersions         []commercePaymentVersionView
	SelectedID              uuid.UUID
	GeneratedCodes          []string
	GeneratedBatchName      string
	GeneratedAt             time.Time
	Notice                  string
	ActionNonce             string
	CatalogDraft            commerceCatalogDraftView
	OrderReport             store.CommerceOrderReportPage
	OrderReportFilter       commerceOrderReportFilterView
	OrderReportNext         string
	Consistency             store.CommerceConsistencyPage
	ConsistencyFilter       commerceConsistencyFilterView
	ConsistencyNext         string
	Reconciliation          reconciliationPageView
	ReconciliationFilter    reconciliationFilterView
	ReconciliationNext      string
	ReconciliationAvailable bool
}

func (view commerceCatalogPlanView) AvailableFromInput() string {
	return commerceCatalogTimeInput(view.AvailableFrom)
}

func (view commerceCatalogPlanView) AvailableUntilInput() string {
	return commerceCatalogTimeInput(view.AvailableUntil)
}

func commerceCatalogTimeInput(value *time.Time) string {
	if value == nil {
		return ""
	}
	return value.UTC().Format(commerceReportTimeLayout)
}

func (p *Portal) commerceDashboard(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccount(w, r)
	if !ok {
		return
	}
	repository, commerceAvailable := p.config.Repository.(commerceAccountRepository)
	userRepository, portalAvailable := p.config.Repository.(userPortalRepository)
	if !commerceAvailable || !portalAvailable {
		p.renderError(w, http.StatusServiceUnavailable, "用户中心暂不可用", "服务端尚未完成用户门户存储配置。")
		return
	}
	section := normalizeUserPortalSection(r.URL.Query().Get("section"))
	data, err := p.loadCommerceAccountPage(r.Context(), repository, userRepository, session, account, section, commerceNotice(r.URL.Query().Get("notice")))
	if err != nil {
		p.internalError(w, err)
		return
	}
	if section == "privacy" && data.SensitiveActionsAvailable {
		actionRepository := p.config.Repository.(userPortalActionRepository)
		if err := p.loadUserPortalPrivacySelection(r.Context(), actionRepository, account.ID, r.URL.Query(), &data); err != nil {
			if errors.Is(err, errUserPortalRequest) {
				p.renderError(w, http.StatusBadRequest, "隐私请求无效", "请从用户中心重新打开数据隐私页面。")
			} else if errors.Is(err, store.ErrDenied) || errors.Is(err, store.ErrNotFound) {
				p.renderError(w, http.StatusNotFound, "隐私请求不存在", "该请求不存在、已失效或不属于当前账号。")
			} else {
				p.internalError(w, err)
			}
			return
		}
	}
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) commerceClaimRedemption(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, map[string]bool{
		"csrf": true, "code": true, "idempotency_key": true,
	}); err != nil {
		p.renderError(w, http.StatusBadRequest, "兑换请求无效", "请刷新兑换页面后重新提交。")
		return
	}
	repository, commerceAvailable := p.config.Repository.(commerceAccountRepository)
	userRepository, portalAvailable := p.config.Repository.(userPortalRepository)
	if !commerceAvailable || !portalAvailable || len(p.config.CommerceKey) < 32 {
		p.renderError(w, http.StatusServiceUnavailable, "兑换服务暂不可用", "服务端尚未完成兑换能力配置。")
		return
	}
	code := normalizePortalCommerceCode(r.Form.Get("code"))
	if !validPortalCommerceCode(code, "TLR") {
		p.renderCommerceClaimError(w, store.ErrRedemptionInvalid)
		return
	}
	idempotencyKey := strings.TrimSpace(r.Form.Get("idempotency_key"))
	_, _, err := repository.ClaimRedemption(r.Context(), account.ID, portalCommerceDigest(p.config.CommerceKey, "redemption-code", code), idempotencyKey)
	if err != nil {
		p.renderCommerceClaimError(w, err)
		return
	}
	data, err := p.loadCommerceAccountPage(r.Context(), repository, userRepository, session, account, "subscription", bilingualPortalNotice(
		"兑换成功，新的权益已立即聚合生效。",
		"Redemption succeeded, and the updated entitlement is now active.",
	))
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.Claimed = true
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) commercePreviewRedemption(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, map[string]bool{"csrf": true, "code": true}); err != nil {
		p.renderError(w, http.StatusBadRequest, "兑换预览无效", "请刷新兑换页面后重新提交。")
		return
	}
	repository, commerceAvailable := p.config.Repository.(commerceAccountRepository)
	userRepository, portalAvailable := p.config.Repository.(userPortalRepository)
	if !commerceAvailable || !portalAvailable || len(p.config.CommerceKey) < 32 {
		p.renderError(w, http.StatusServiceUnavailable, "兑换服务暂不可用", "服务端尚未完成兑换能力配置。")
		return
	}
	code := normalizePortalCommerceCode(r.Form.Get("code"))
	if !validPortalCommerceCode(code, "TLR") {
		p.renderCommerceClaimError(w, store.ErrRedemptionInvalid)
		return
	}
	preview, err := repository.PreviewRedemption(
		r.Context(), account.ID, portalCommerceDigest(p.config.CommerceKey, "redemption-code", code),
	)
	if err != nil {
		p.renderCommerceClaimError(w, err)
		return
	}
	data, err := p.loadCommerceAccountPage(
		r.Context(), repository, userRepository, session, account, "subscription",
		bilingualPortalNotice(
			"兑换预览已生成；页面不会保留或回显兑换码，请确认权益后重新输入代码完成核销。",
			"The redemption preview is ready. The code is not retained or displayed; review the entitlement, then enter the code again to redeem it.",
		),
	)
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.RedemptionPreview = &preview
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) commerceCreateOrder(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	method, validMethod := portalPaymentMethod(r.Form.Get("payment_method"))
	methods, methodsErr := loadPortalPaymentMethods(r.Context(), p.config.Repository)
	if methodsErr != nil {
		p.renderError(w, http.StatusServiceUnavailable, "支付服务暂不可用", "当前无法确认支付渠道状态，请稍后重试。")
		return
	}
	if p.config.Commerce == nil || !validMethod || !portalPaymentMethodAvailable(methods, method) {
		p.renderError(w, http.StatusBadRequest, "支付方式无效", "请选择受支持且当前已配置的支付渠道。")
		return
	}
	order, err := p.config.Commerce.CreateOrder(r.Context(), commercecontrol.CreateOrderRequest{
		AccountID: account.ID, PlanKey: r.Form.Get("plan_key"), Provider: method.provider,
		Scene: method.scene, IdempotencyKey: r.Form.Get("idempotency_key"), ExpiresAt: p.now().Add(20 * time.Minute),
	})
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	instruction, err := p.config.Commerce.StartPayment(r.Context(), account.ID, order.ID)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	view, err := safePaymentInstructionView(order, instruction)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	data, err := p.loadCommerceUserSection(r.Context(), session, account, "orders", bilingualPortalNotice(
		"订单已创建，请在有效期内完成支付。",
		"The order was created. Complete payment before it expires.",
	))
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.Payment = &view
	p.render(w, http.StatusCreated, "portal-dashboard.html", data)
}

func (p *Portal) commerceStartPayment(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	orderID, err := uuid.Parse(r.PathValue("id"))
	if p.config.Commerce == nil || err != nil {
		p.renderError(w, http.StatusBadRequest, "支付请求无效", "订单标识无效或支付服务不可用。")
		return
	}
	instruction, err := p.config.Commerce.StartPayment(r.Context(), account.ID, orderID)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	view, err := safePaymentInstructionView(store.Order{
		ID: orderID, OrderNo: instruction.MerchantOrderID, Provider: string(instruction.Provider), ExpiresAt: instruction.ExpiresAt,
	}, instruction)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	data, err := p.loadCommerceUserSection(r.Context(), session, account, "orders", bilingualPortalNotice(
		"请继续完成支付；页面不会根据浏览器跳转自行标记成功。",
		"Continue with payment. A browser redirect alone will not mark the order as paid.",
	))
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.Payment = &view
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) commerceQueryPayment(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	orderID, err := uuid.Parse(r.PathValue("id"))
	if p.config.Commerce == nil || err != nil {
		p.renderError(w, http.StatusBadRequest, "查单请求无效", "订单标识无效或支付服务不可用。")
		return
	}
	outcome, err := p.config.Commerce.QueryAndFulfill(r.Context(), account.ID, orderID)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	notice := bilingualPortalNotice(
		"支付渠道尚未确认成功，权益没有发生变化。",
		"The payment provider has not confirmed success, so the entitlement was not changed.",
	)
	if outcome.Fulfilled {
		notice = bilingualPortalNotice(
			"支付渠道已确认成功，订单已完成幂等履约。",
			"The payment provider confirmed success, and the order was fulfilled idempotently.",
		)
	}
	data, err := p.loadCommerceUserSection(r.Context(), session, account, "orders", notice)
	if err != nil {
		p.internalError(w, err)
		return
	}
	data.PaymentResult = &commercePaymentResultView{
		OrderNo: outcome.Order.OrderNo, OrderStatus: outcome.Order.Status,
		ProviderStatus: string(outcome.ProviderStatus), Fulfilled: outcome.Fulfilled,
	}
	p.render(w, http.StatusOK, "portal-dashboard.html", data)
}

func (p *Portal) commerceCancelOrder(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAccountAction(w, r)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceAccountRepository)
	orderID, err := uuid.Parse(r.PathValue("id"))
	if !available || err != nil {
		p.renderError(w, http.StatusBadRequest, "订单请求无效", "订单标识无效或订单服务不可用。")
		return
	}
	if _, err = repository.CancelOrder(r.Context(), account.ID, orderID); err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	http.Redirect(w, r, "/account?section=orders&notice=order-cancelled", http.StatusSeeOther)
}

func (p *Portal) loadCommerceUserSection(ctx context.Context, session webSession, account store.PortalAccount, section, notice string) (commerceAccountPageData, error) {
	repository, commerceAvailable := p.config.Repository.(commerceAccountRepository)
	userRepository, portalAvailable := p.config.Repository.(userPortalRepository)
	if !commerceAvailable || !portalAvailable {
		return commerceAccountPageData{}, errors.New("user portal repository is unavailable")
	}
	return p.loadCommerceAccountPage(ctx, repository, userRepository, session, account, section, notice)
}

func (p *Portal) loadCommerceAccountPage(ctx context.Context, repository commerceAccountRepository, userRepository userPortalRepository, session webSession, account store.PortalAccount, section, notice string) (commerceAccountPageData, error) {
	section = normalizeUserPortalSection(section)
	entitlement, err := userRepository.GetUserPortalEntitlement(ctx, account.ID)
	if err != nil {
		return commerceAccountPageData{}, err
	}
	idempotencyKey, err := randomText(24)
	if err != nil {
		return commerceAccountPageData{}, err
	}
	consoleURL, _ := p.adminBasePath(ctx)
	data := commerceAccountPageData{
		AccountID: account.ID, Username: session.Username, Nickname: firstNonEmpty(session.Nickname, session.Username),
		Email: session.Email, Role: account.Role, RoleLabel: roleLabel(account.Role),
		CanOpenConsole: account.Role.CanOpenConsole() && consoleURL != "", ConsoleURL: consoleURL, IdentityAccountURL: "/account/identity",
		CSRF: session.CSRF, Section: section, SectionTitle: userPortalSectionTitle(section),
		Entitlement: entitlement, IdempotencyKey: idempotencyKey, Notice: notice,
	}
	if announcementRepository, available := p.config.Repository.(interface {
		ListActiveWebAnnouncements(context.Context, int) ([]store.WebAnnouncement, error)
	}); available {
		data.Announcements, err = announcementRepository.ListActiveWebAnnouncements(ctx, 5)
		if err != nil {
			return commerceAccountPageData{}, err
		}
	}
	if section == "subscription" || section == "orders" {
		data.PaymentMethods, err = loadPortalPaymentMethods(ctx, p.config.Repository)
		if err != nil {
			return commerceAccountPageData{}, err
		}
		data.CanPurchase = p.config.Commerce != nil && len(data.PaymentMethods) > 0
	}
	data.DataExportDownloadAvailable = dataExportDownloaderAvailable(p.config.DataExportDownloader)
	data.ProfileChangeAvailable = p.config.IdentityProfiles != nil
	if p.config.ServiceEmail != nil {
		data.EmailChangeAvailable = data.ProfileChangeAvailable && p.config.ServiceEmail.Runtime(ctx).ServiceAvailable
	}
	actionRepository, actionsAvailable := p.config.Repository.(userPortalActionRepository)
	data.SensitiveActionsAvailable = actionsAvailable
	request := store.UserPortalPageRequest{Limit: 50}
	switch section {
	case "overview":
		data.Overview, err = userRepository.GetUserPortalAccountOverview(ctx, account.ID)
	case "devices":
		data.Devices, err = userRepository.ListUserPortalDevices(ctx, account.ID, request)
	case "pcs":
		data.PCs, err = userRepository.ListUserPortalPCs(ctx, account.ID, request)
		if err == nil && actionsAvailable {
			data.PCPolicies, err = actionRepository.ListUserPortalPCControlPolicies(ctx, account.ID, 50)
		}
	case "sessions":
		data.Sessions, err = userRepository.ListUserPortalSessions(ctx, account.ID, request)
	case "subscription":
		data.CatalogVersion, data.Plans, err = loadCommercePlanViews(ctx, repository, data.CanPurchase)
		if err == nil {
			var redemptions []store.AccountRedemption
			redemptions, err = repository.ListAccountRedemptions(ctx, account.ID, commercePageLimit)
			data.Redemptions = redemptionClaimViews(redemptions)
		}
	case "orders":
		data.CatalogVersion, data.Plans, err = loadCommercePlanViews(ctx, repository, data.CanPurchase)
		if err == nil {
			var orders []store.Order
			orders, err = repository.ListOrders(ctx, account.ID, commercePageLimit)
			data.Orders = commerceOrderViews(orders)
		}
		if err == nil {
			var refunds []store.Refund
			refunds, err = repository.ListAccountRefunds(ctx, account.ID, commercePageLimit)
			data.Refunds = commerceAccountRefundViews(refunds)
		}
	case "security":
		data.SecurityActivities, err = userRepository.ListUserPortalSecurityActivity(ctx, account.ID, store.UserPortalAuditPageRequest{Limit: 50})
	case "privacy":
		if actionsAvailable {
			data.PrivacyPreflight, err = actionRepository.GetUserPortalAccountDeletionPreflight(ctx, account.ID)
		}
	}
	return data, err
}

type portalPaymentSelection struct {
	provider string
	scene    string
}

type portalPaymentMethodView struct {
	Value string
	Label string
}

var supportedPortalPaymentMethods = []struct {
	selection portalPaymentSelection
	view      portalPaymentMethodView
}{
	{portalPaymentSelection{provider: "ALIPAY", scene: "NATIVE"}, portalPaymentMethodView{Value: "ALIPAY:NATIVE", Label: "支付宝扫码"}},
	{portalPaymentSelection{provider: "ALIPAY", scene: "WEB"}, portalPaymentMethodView{Value: "ALIPAY:WEB", Label: "支付宝网页"}},
	{portalPaymentSelection{provider: "WECHAT_PAY", scene: "NATIVE"}, portalPaymentMethodView{Value: "WECHAT_PAY:NATIVE", Label: "微信扫码"}},
}

func loadPortalPaymentMethods(ctx context.Context, repository any) ([]portalPaymentMethodView, error) {
	runtimeRepository, ok := repository.(commerceRuntimeRepository)
	if !ok {
		return nil, nil
	}
	methods := make([]portalPaymentMethodView, 0, len(supportedPortalPaymentMethods))
	for _, candidate := range supportedPortalPaymentMethods {
		channel, version, err := runtimeRepository.GetActivePaymentConfiguration(ctx, candidate.selection.provider, candidate.selection.scene)
		if errors.Is(err, store.ErrPaymentChannelUnavailable) || errors.Is(err, store.ErrNotFound) {
			continue
		}
		if err != nil {
			return nil, err
		}
		if channel.Enabled && channel.HealthStatus == "HEALTHY" && channel.ActiveVersionID != nil &&
			*channel.ActiveVersionID == version.ID && version.ChannelID == channel.ID && version.Status == "VALIDATED" {
			methods = append(methods, candidate.view)
		}
	}
	return methods, nil
}

func portalPaymentMethodAvailable(methods []portalPaymentMethodView, selection portalPaymentSelection) bool {
	value := selection.provider + ":" + selection.scene
	for _, method := range methods {
		if method.Value == value {
			return true
		}
	}
	return false
}

func portalPaymentMethod(value string) (portalPaymentSelection, bool) {
	switch strings.ToUpper(strings.TrimSpace(value)) {
	case "ALIPAY:NATIVE":
		return portalPaymentSelection{provider: "ALIPAY", scene: "NATIVE"}, true
	case "ALIPAY:WEB":
		return portalPaymentSelection{provider: "ALIPAY", scene: "WEB"}, true
	case "WECHAT_PAY:NATIVE":
		return portalPaymentSelection{provider: "WECHAT_PAY", scene: "NATIVE"}, true
	default:
		return portalPaymentSelection{}, false
	}
}

func safePaymentInstructionView(order store.Order, instruction commercecontrol.PaymentInstruction) (commercePaymentInstructionView, error) {
	provider := string(instruction.Provider)
	if order.ID == uuid.Nil || strings.TrimSpace(instruction.MerchantOrderID) == "" ||
		(order.OrderNo != "" && instruction.MerchantOrderID != order.OrderNo) ||
		(order.Provider != "" && provider != order.Provider) {
		return commercePaymentInstructionView{}, commercecontrol.ErrPaymentResultMismatch
	}
	view := commercePaymentInstructionView{
		OrderID: order.ID, OrderNo: instruction.MerchantOrderID, Provider: provider,
		Kind: string(instruction.Kind), ExpiresAt: instruction.ExpiresAt,
	}
	if view.ExpiresAt.IsZero() {
		view.ExpiresAt = order.ExpiresAt
	}
	switch view.Kind {
	case "QR_CODE":
		if instruction.RedirectURL != "" || !validPortalPaymentURL(instruction.QRCodeURL, true) {
			return commercePaymentInstructionView{}, commercecontrol.ErrPaymentResultMismatch
		}
		view.DisplayURL = instruction.QRCodeURL
	case "REDIRECT":
		if instruction.QRCodeURL != "" || !validPortalPaymentURL(instruction.RedirectURL, false) {
			return commercePaymentInstructionView{}, commercecontrol.ErrPaymentResultMismatch
		}
		view.DisplayURL = instruction.RedirectURL
		view.RedirectURL = instruction.RedirectURL
	default:
		return commercePaymentInstructionView{}, commercecontrol.ErrPaymentResultMismatch
	}
	return view, nil
}

func validPortalPaymentURL(value string, allowWeixin bool) bool {
	if len(value) < 1 || len(value) > 4096 {
		return false
	}
	for _, character := range value {
		if unicode.IsControl(character) {
			return false
		}
	}
	parsed, err := url.Parse(value)
	if err != nil || parsed.User != nil || parsed.Fragment != "" {
		return false
	}
	if parsed.Scheme == "https" && parsed.Host != "" {
		return true
	}
	return allowWeixin && parsed.Scheme == "weixin" && (parsed.Opaque != "" || parsed.Host != "")
}

func loadCommercePlanViews(ctx context.Context, repository commercePublishedPlansRepository, canPurchase bool) (int64, []commercePlanView, error) {
	version, plans, err := repository.PublishedPlans(ctx)
	if err != nil {
		return 0, nil, err
	}
	views := make([]commercePlanView, 0, len(plans))
	for _, plan := range plans {
		views = append(views, commercePlanView{
			Key: plan.Key, DisplayName: plan.DisplayName, Description: plan.Description,
			BillingPeriod: plan.BillingPeriod, PriceMinor: plan.PriceMinor, Currency: plan.Currency,
			Purchasable: canPurchase && plan.PriceMinor > 0,
		})
	}
	return version, views, nil
}

func commerceOrderViews(orders []store.Order) []commerceOrderView {
	views := make([]commerceOrderView, 0, len(orders))
	for _, order := range orders {
		views = append(views, commerceOrderView{
			ID: order.ID, OrderNo: order.OrderNo, PlanKey: order.PlanKey,
			Provider: order.Provider, PaymentScene: order.PaymentScene,
			AmountMinor: order.AmountMinor, Currency: order.Currency, Status: order.Status,
			ExpiresAt: order.ExpiresAt, PaidAt: order.PaidAt, FulfilledAt: order.FulfilledAt,
			ClosedAt: order.ClosedAt, CreatedAt: order.CreatedAt,
			Cancelable: order.Status == "CREATED" || order.Status == "PENDING_PAYMENT",
			Payable:    order.Status == "CREATED" || order.Status == "PENDING_PAYMENT",
			Queryable:  order.Status == "PENDING_PAYMENT" || order.Status == "PAID" || order.Status == "FULFILLING",
		})
	}
	return views
}

func adminCommerceOrderViews(orders []store.Order) []adminCommerceOrderView {
	views := make([]adminCommerceOrderView, 0, len(orders))
	for _, order := range orders {
		views = append(views, adminCommerceOrderView{
			ID: order.ID, AccountID: order.AccountID, OrderNo: order.OrderNo, PlanKey: order.PlanKey,
			Provider: order.Provider, PaymentScene: order.PaymentScene, AmountMinor: order.AmountMinor,
			Currency: order.Currency, Status: order.Status, ExpiresAt: order.ExpiresAt,
			PaidAt: order.PaidAt, FulfilledAt: order.FulfilledAt, ClosedAt: order.ClosedAt,
			CreatedAt:    order.CreatedAt,
			Queryable:    order.Status == "PENDING_PAYMENT",
			FulfillRetry: order.Status == "PAID" || order.Status == "FULFILLING",
			Closable:     order.Status == "CREATED" || order.Status == "PENDING_PAYMENT",
		})
	}
	return views
}

func invitationRedemptionViews(items []store.InvitationRedemption) []invitationRedemptionView {
	views := make([]invitationRedemptionView, 0, len(items))
	for _, item := range items {
		userID := uuid.Nil
		if item.UserID != nil {
			userID = *item.UserID
		}
		views = append(views, invitationRedemptionView{
			RecordReference:    commerceReference("reg", item.ID),
			CodePrefix:         item.CodePrefix,
			BatchName:          item.BatchName,
			AccountReference:   commerceReference("acct", userID),
			Username:           item.Username,
			Nickname:           item.Nickname,
			EmailMasked:        item.EmailMasked,
			HasIdentityProfile: item.Username != "" || item.Nickname != "" || item.EmailMasked != "",
			HasUser:            item.UserID != nil, State: item.State,
			ReservedUntil: item.ReservedUntil, ProviderAcceptedAt: item.ProviderAcceptedAt,
			RedeemedAt: item.RedeemedAt, ReleasedAt: item.ReleasedAt, CreatedAt: item.CreatedAt,
		})
	}
	return views
}

func commerceReference(prefix string, value uuid.UUID) string {
	if value == uuid.Nil {
		return ""
	}
	compact := strings.ReplaceAll(value.String(), "-", "")
	return prefix + "_" + compact[:8]
}

func redemptionClaimViews(items []store.AccountRedemption) []redemptionClaimView {
	views := make([]redemptionClaimView, 0, len(items))
	for _, item := range items {
		views = append(views, redemptionClaimView{
			ClaimID: item.ClaimID, AccountID: item.AccountID, CodePrefix: item.CodePrefix,
			PlanKey: item.PlanKey, CatalogVersion: item.CatalogVersion, ClaimedAt: item.ClaimedAt,
			StartsAt: item.StartsAt, EndsAt: item.EndsAt, Status: item.Status,
		})
	}
	return views
}

func newCommerceRefundViews(refunds []store.Refund, now time.Time, canOperate bool) []commerceRefundView {
	views := make([]commerceRefundView, 0, len(refunds))
	for _, refund := range refunds {
		expired := refund.Status == "APPROVAL_PENDING" &&
			(refund.ApprovalExpiresAt == nil || !refund.ApprovalExpiresAt.After(now))
		views = append(views, commerceRefundView{
			ID: refund.ID, OrderID: refund.OrderID, RefundNo: refund.RefundNo,
			AmountMinor: refund.AmountMinor, Reason: refund.Reason, Status: refund.Status,
			FailureCode: refund.FailureCode, Version: refund.Version, RequestedAt: refund.RequestedAt,
			ApprovalExpiresAt: refund.ApprovalExpiresAt, ApprovedAt: refund.ApprovedAt, CompletedAt: refund.CompletedAt,
			CanConfirm:      canOperate && refund.Status == "APPROVAL_PENDING" && refund.Version > 0 && !expired,
			ApprovalExpired: expired,
		})
	}
	return views
}

func commerceAccountRefundViews(refunds []store.Refund) []commerceAccountRefundView {
	views := make([]commerceAccountRefundView, 0, len(refunds))
	for _, refund := range refunds {
		views = append(views, commerceAccountRefundView{
			ID: refund.ID, OrderID: refund.OrderID, RefundNo: refund.RefundNo,
			AmountMinor: refund.AmountMinor, Status: refund.Status,
			RequestedAt: refund.RequestedAt, CompletedAt: refund.CompletedAt,
		})
	}
	return views
}

func normalizeUserPortalSection(section string) string {
	switch strings.ToLower(strings.TrimSpace(section)) {
	case "overview", "devices", "pcs", "sessions", "subscription", "orders", "security", "privacy", "settings":
		return strings.ToLower(strings.TrimSpace(section))
	default:
		return "overview"
	}
}

func userPortalSectionTitle(section string) string {
	return map[string]string{
		"overview": "账号概览", "devices": "设备", "pcs": "Windows PC", "sessions": "会话元数据",
		"subscription": "订阅与权益", "orders": "订单与支付", "security": "安全活动", "privacy": "数据隐私",
		"settings": "个人设置与登录安全",
	}[normalizeUserPortalSection(section)]
}

func (p *Portal) handleCommerceServiceError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, commercecontrol.ErrInvalidRequest), errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusBadRequest, "支付请求无效", "套餐、支付渠道或订单状态不允许该操作。")
	case errors.Is(err, commercecontrol.ErrPaymentChannelUnavailable), errors.Is(err, store.ErrPaymentChannelUnavailable):
		p.renderError(w, http.StatusServiceUnavailable, "支付渠道不可用", "所选支付渠道当前没有健康且已验证的配置。")
	case errors.Is(err, commercecontrol.ErrPaymentStateNotProgressable), errors.Is(err, store.ErrOrderNotPayable), errors.Is(err, store.ErrConflict):
		p.renderError(w, http.StatusConflict, "订单状态已变化", "请返回订单页面刷新状态后重试。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "订单不存在", "目标订单不存在或不属于当前账号。")
	case errors.Is(err, commercecontrol.ErrPaymentResultMismatch), errors.Is(err, commercecontrol.ErrPaymentTransactionMissing):
		p.renderError(w, http.StatusBadGateway, "支付结果校验失败", "支付渠道返回的信息未通过服务端一致性校验，权益没有发生变化。")
	default:
		p.internalError(w, err)
	}
}

func (p *Portal) adminCommerceDashboard(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireConsole(w, r)
	if !ok {
		return
	}
	section := normalizeCommerceAdminSection(r.URL.Query().Get("section"))
	actionNonce, err := randomText(24)
	if err != nil {
		p.internalError(w, err)
		return
	}
	data := adminCommercePageData{
		Username: session.Username, Role: account.Role, RoleLabel: roleLabel(account.Role), CSRF: session.CSRF,
		Section: section, SectionTitle: commerceAdminSectionTitle(section),
		CanOperate:         account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin,
		CanConfigure:       account.Role == store.RoleSuperAdmin,
		CanRefund:          (account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin) && p.config.Commerce != nil,
		CanCreateVersion:   account.Role == store.RoleSuperAdmin && p.config.Commerce != nil,
		CanConfigureSystem: account.Role == store.RoleSuperAdmin,
		CommerceNavigation: true,
		Notice:             commerceAdminNotice(r.URL.Query().Get("notice")),
		ActionNonce:        actionNonce,
	}
	err = nil
	switch section {
	case "catalog":
		repository, available := p.config.Repository.(commerceReportingRepository)
		if !available {
			err = errors.New("commerce reporting repository is unavailable")
			break
		}
		var draft *store.CommerceCatalogDraft
		draft, err = repository.GetCommerceCatalogDraft(r.Context(), account.ID)
		if err == nil {
			data.CatalogDraft = newCommerceCatalogDraftView(draft)
		}
	case "reports":
		repository, available := p.config.Repository.(commerceReportingRepository)
		if !available {
			err = errors.New("commerce reporting repository is unavailable")
			break
		}
		var filter store.CommerceOrderReportFilter
		filter, data.OrderReportFilter, err = parseCommerceOrderReportQuery(r.URL.Query(), p.now())
		if err == nil {
			data.OrderReport, err = repository.ListCommerceOrderReport(r.Context(), account.ID, filter)
		}
		if err == nil && data.OrderReport.NextCursor != nil {
			data.OrderReportNext, err = commerceOrderReportNextURL(data.OrderReportFilter, *data.OrderReport.NextCursor)
		}
	case "consistency":
		repository, available := p.config.Repository.(commerceReportingRepository)
		if !available {
			err = errors.New("commerce reporting repository is unavailable")
			break
		}
		var filter store.CommerceConsistencyFilter
		filter, data.ConsistencyFilter, err = parseCommerceConsistencyQuery(r.URL.Query(), p.now())
		if err == nil {
			data.Consistency, err = repository.ListCommerceConsistencyDetails(r.Context(), account.ID, filter)
		}
		if err == nil && data.Consistency.NextCursor != nil {
			data.ConsistencyNext, err = commerceConsistencyNextURL(data.ConsistencyFilter, *data.Consistency.NextCursor)
		}
	case "reconciliation":
		data.ReconciliationAvailable = p.config.Reconciliation != nil
		if !data.ReconciliationAvailable {
			break
		}
		var request reconciliationListRequest
		request, data.ReconciliationFilter, err = parseReconciliationQuery(r.URL.Query())
		if err == nil {
			request.ActorID = account.ID
			var page reconciliationPage
			page, err = p.config.Reconciliation.List(r.Context(), request)
			if err == nil {
				data.Reconciliation = newReconciliationPageView(page)
				if page.Next != nil {
					data.ReconciliationNext, err = reconciliationNextURL(data.ReconciliationFilter, *page.Next)
				}
			}
		}
	case "registration":
		repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
		if !available {
			err = errors.New("registration commerce repository is unavailable")
			break
		}
		data.Policy, err = repository.GetRegistrationPolicy(r.Context())
		if err == nil {
			data.InvitationBatches, err = repository.ListInvitationBatches(r.Context(), account.ID, commercePageLimit)
		}
		if err == nil {
			data.SelectedID, data.InvitationCodes, err = selectedInvitationCodes(r, repository, account.ID)
		}
		if err == nil {
			var redemptions []store.InvitationRedemption
			redemptions, err = repository.ListInvitationRedemptions(r.Context(), account.ID, commercePageLimit)
			data.InvitationRedemptions = invitationRedemptionViews(redemptions)
		}
	case "redemptions":
		repository, available := p.config.Repository.(commerceRedemptionAdminRepository)
		if !available {
			err = errors.New("redemption commerce repository is unavailable")
			break
		}
		data.RedemptionBatches, err = repository.ListRedemptionBatches(r.Context(), account.ID, commercePageLimit)
		if err == nil {
			data.SelectedID, data.RedemptionCodes, err = selectedRedemptionCodes(r, repository, account.ID)
		}
		if err == nil {
			var claims []store.AccountRedemption
			claims, err = repository.ListRedemptionClaims(r.Context(), account.ID, commercePageLimit)
			data.RedemptionClaims = redemptionClaimViews(claims)
		}
	case "entitlements":
		repository, available := p.config.Repository.(commerceManualEntitlementAdminRepository)
		planRepository, plansAvailable := p.config.Repository.(commercePublishedPlansRepository)
		if !available || !plansAvailable {
			err = errors.New("manual entitlement repository is unavailable")
			break
		}
		if validateCommerceFormValues(r.URL.Query(), map[string]bool{"section": true, "account_id": true, "notice": true}) != nil {
			err = errCommerceReportFilter
			break
		}
		var accountID *uuid.UUID
		accountID, data.ManualGrantAccount, err = parseOptionalCommerceAccountID(r.URL.Query().Get("account_id"))
		if err != nil {
			break
		}
		filter := store.ManualEntitlementGrantFilter{Limit: commercePageLimit}
		if accountID != nil {
			filter.AccountID = *accountID
		}
		var grants []store.ManualEntitlementGrant
		grants, err = repository.ListManualEntitlementGrants(r.Context(), account.ID, filter)
		data.ManualGrants = manualEntitlementGrantViews(grants)
		if err == nil {
			_, data.ManualGrantPlans, err = loadCommercePlanViews(r.Context(), planRepository, false)
		}
	case "orders":
		repository, available := p.config.Repository.(commercePaymentAdminRepository)
		if !available {
			err = errors.New("payment commerce repository is unavailable")
			break
		}
		var orders []store.Order
		orders, err = repository.ListCommerceOrders(r.Context(), account.ID, commercePageLimit)
		data.Orders = adminCommerceOrderViews(orders)
		if err == nil {
			var refunds []store.Refund
			refunds, err = repository.ListRefunds(r.Context(), account.ID, commercePageLimit)
			if err == nil {
				data.Refunds = newCommerceRefundViews(refunds, p.now(), data.CanRefund)
			}
		}
	case "payments":
		repository, available := p.config.Repository.(commercePaymentAdminRepository)
		if !available {
			err = errors.New("payment commerce repository is unavailable")
			break
		}
		data.Transactions, err = repository.ListCommerceTransactions(r.Context(), account.ID, commercePageLimit)
		if err == nil {
			data.Notifications, err = repository.ListPaymentNotifications(r.Context(), account.ID, commercePageLimit)
		}
		if err == nil {
			data.Reconciliations, err = repository.ListReconciliationRuns(r.Context(), account.ID, commercePageLimit)
		}
	case "channels":
		repository, available := p.config.Repository.(commerceChannelAdminRepository)
		if !available {
			err = errors.New("payment channel repository is unavailable")
			break
		}
		var channels []store.PaymentChannel
		channels, err = repository.ListPaymentChannels(r.Context(), account.ID)
		data.Channels = commercePaymentChannelViews(channels)
		if healthSource, ok := p.config.Commerce.(commercePaymentCertificateHealthSource); ok {
			applyCommercePaymentCertificateHealth(data.Channels, healthSource)
		}
		if err == nil {
			data.SelectedID, data.ChannelVersions, err = selectedChannelVersions(
				r, repository, account.ID, activeCommercePaymentVersion(channels, strings.TrimSpace(r.URL.Query().Get("id"))),
			)
		}
	}
	if err != nil {
		if errors.Is(err, errCommerceReportFilter) || errors.Is(err, errReconciliationFilter) {
			p.renderError(w, http.StatusBadRequest, "筛选条件无效", "请检查时间范围、账号、渠道、状态和分页参数后重试。")
			return
		}
		if isInvalidReconciliationError(err) {
			p.renderError(w, http.StatusBadRequest, "对账筛选条件无效", "请检查渠道、状态、任务标识和分页参数后重试。")
			return
		}
		if isUnavailableReconciliationError(err) {
			p.renderError(w, http.StatusServiceUnavailable, "对账差异暂不可用", "对账差异存储暂不可用，请稍后重试。")
			return
		}
		if errors.Is(err, store.ErrDenied) {
			p.renderError(w, http.StatusForbidden, "无权读取商业化报表", "当前账号角色或报表范围不允许该查询。")
			return
		}
		if errors.Is(err, store.ErrNotFound) {
			p.renderError(w, http.StatusNotFound, "商业化记录不存在", "所选批次或支付渠道不存在。")
		} else {
			p.internalError(w, err)
		}
		return
	}
	p.render(w, http.StatusOK, "commerce-admin.html", data)
}

func manualEntitlementGrantViews(items []store.ManualEntitlementGrant) []manualEntitlementGrantView {
	views := make([]manualEntitlementGrantView, 0, len(items))
	for _, item := range items {
		views = append(views, manualEntitlementGrantView{
			ID: item.ID, AccountID: item.AccountID, PlanKey: item.PlanKey, CatalogVersion: item.CatalogVersion,
			StartsAt: item.StartsAt, EndsAt: item.EndsAt, Status: item.Status,
			CreatedAt: item.CreatedAt, UpdatedAt: item.UpdatedAt, Revocable: item.Status == "ACTIVE",
		})
	}
	return views
}

func selectedInvitationCodes(r *http.Request, repository commerceRegistrationAdminRepository, actorID uuid.UUID) (uuid.UUID, []store.InvitationCode, error) {
	id, err := selectedCommerceID(r)
	if err != nil || id == uuid.Nil {
		return id, nil, err
	}
	codes, err := repository.ListInvitationCodes(r.Context(), actorID, id, commercePageLimit)
	return id, codes, err
}

func selectedRedemptionCodes(r *http.Request, repository commerceRedemptionAdminRepository, actorID uuid.UUID) (uuid.UUID, []store.RedemptionCode, error) {
	id, err := selectedCommerceID(r)
	if err != nil || id == uuid.Nil {
		return id, nil, err
	}
	codes, err := repository.ListRedemptionCodes(r.Context(), actorID, id, commercePageLimit)
	return id, codes, err
}

func selectedChannelVersions(r *http.Request, repository commerceChannelAdminRepository, actorID uuid.UUID, activeVersionID *uuid.UUID) (uuid.UUID, []commercePaymentVersionView, error) {
	id, err := selectedCommerceID(r)
	if err != nil || id == uuid.Nil {
		return id, nil, err
	}
	versions, err := repository.ListPaymentChannelVersions(r.Context(), actorID, id)
	if err != nil {
		return id, nil, err
	}
	validationTraces := make(map[int64]string)
	if traceRepository, ok := repository.(commerceChannelValidationTraceRepository); ok {
		traces, traceErr := traceRepository.ListPaymentChannelValidationTraces(r.Context(), actorID, id)
		if traceErr != nil {
			return id, nil, traceErr
		}
		for _, trace := range traces {
			validationTraces[trace.Version] = safeCommerceTraceID(trace.TraceID)
		}
	}
	views := make([]commercePaymentVersionView, 0, len(versions))
	for _, version := range versions {
		// SecretRefs and free-form validation summaries deliberately never cross
		// the template boundary. Only recognized public fields are parsed into a
		// separately masked operational summary.
		validationLabel := "等待安全校验"
		if version.Status == "VALIDATED" {
			validationLabel = "本地材料和渠道连通性验证通过"
		} else if version.ValidatedAt != nil {
			validationLabel = "安全校验未通过"
		}
		summary := safeCommercePaymentPublicSummary(version.PublicConfig)
		views = append(views, commercePaymentVersionView{
			ID: version.ID, ChannelID: version.ChannelID, Version: version.Version,
			Environment: version.Environment, Status: version.Status, ValidatedAt: version.ValidatedAt,
			ValidationLabel: validationLabel, CreatedAt: version.CreatedAt,
			Active:      activeVersionID != nil && *activeVersionID == version.ID,
			AppIDMasked: summary.AppIDMasked, MerchantIDMasked: summary.MerchantIDMasked,
			NotifyURL: summary.NotifyURL, MerchantSerialMasked: summary.MerchantSerialMasked,
			ValidationCode:    stableCommerceErrorCode(version.ValidationSummary),
			ValidationTraceID: validationTraces[version.Version],
		})
	}
	return id, views, nil
}

type commercePaymentPublicSummary struct {
	AppIDMasked          string
	MerchantIDMasked     string
	NotifyURL            string
	MerchantSerialMasked string
}

func commercePaymentChannelViews(channels []store.PaymentChannel) []commercePaymentChannelView {
	views := make([]commercePaymentChannelView, 0, len(channels))
	for _, channel := range channels {
		views = append(views, commercePaymentChannelView{
			ID: channel.ID, Provider: channel.Provider, Scene: channel.Scene, Enabled: channel.Enabled,
			ActiveVersionID: channel.ActiveVersionID, HealthStatus: channel.HealthStatus,
			LastWebhookAt: channel.LastWebhookAt, LastErrorCode: stableCommerceErrorCode(channel.LastErrorCode),
		})
	}
	return views
}

func applyCommercePaymentCertificateHealth(views []commercePaymentChannelView, source commercePaymentCertificateHealthSource) {
	for index := range views {
		health, ok := source.CachedPaymentCertificateHealth(views[index].Provider, views[index].Scene)
		if !ok {
			continue
		}
		status := strings.ToUpper(strings.TrimSpace(health.Status))
		switch status {
		case "HEALTHY", "EXPIRING", "UNKNOWN", "UNAVAILABLE":
			views[index].CertificateStatus = status
		default:
			views[index].CertificateStatus = "UNAVAILABLE"
		}
		if !health.EarliestExpiryAt.IsZero() {
			expiry := health.EarliestExpiryAt.UTC()
			views[index].CertificateExpiryAt = &expiry
			views[index].CertificateRemaining = health.RemainingDays
		}
		if !health.LastRefreshAt.IsZero() {
			refresh := health.LastRefreshAt.UTC()
			views[index].CertificateLastRefresh = &refresh
		}
	}
}

func activeCommercePaymentVersion(channels []store.PaymentChannel, selectedID string) *uuid.UUID {
	channelID, err := parseCanonicalPortalUUID(selectedID)
	if err != nil {
		return nil
	}
	for _, channel := range channels {
		if channel.ID == channelID && channel.ActiveVersionID != nil {
			active := *channel.ActiveVersionID
			return &active
		}
	}
	return nil
}

func safeCommercePaymentPublicSummary(raw json.RawMessage) commercePaymentPublicSummary {
	var public struct {
		AppID          string `json:"app_id"`
		SellerID       string `json:"seller_id"`
		MerchantID     string `json:"merchant_id"`
		MchID          string `json:"mch_id"`
		NotifyURL      string `json:"notify_url"`
		MerchantSerial string `json:"merchant_serial"`
	}
	if len(raw) == 0 || len(raw) > 64<<10 || json.Unmarshal(raw, &public) != nil {
		return commercePaymentPublicSummary{}
	}
	merchantID := firstNonEmpty(public.MchID, public.MerchantID, public.SellerID)
	return commercePaymentPublicSummary{
		AppIDMasked: maskCommerceIdentifier(public.AppID), MerchantIDMasked: maskCommerceIdentifier(merchantID),
		NotifyURL: safeCommerceNotifyURL(public.NotifyURL), MerchantSerialMasked: maskCommerceIdentifier(public.MerchantSerial),
	}
}

func maskCommerceIdentifier(value string) string {
	value = strings.TrimSpace(value)
	if value == "" || !safePublicText(value, 256, false) {
		return ""
	}
	characters := []rune(value)
	if len(characters) <= 6 {
		return "••••"
	}
	return string(characters[:2]) + "••••" + string(characters[len(characters)-4:])
}

func safeCommerceNotifyURL(value string) string {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 2048 || !safePublicText(value, 4096, false) {
		return ""
	}
	parsed, err := url.Parse(value)
	if err != nil || parsed.Host == "" || parsed.Scheme != "https" && parsed.Scheme != "http" {
		return ""
	}
	parsed.User = nil
	parsed.RawQuery = ""
	parsed.ForceQuery = false
	parsed.Fragment = ""
	return parsed.String()
}

func stableCommerceErrorCode(value string) string {
	value = strings.ToUpper(strings.TrimSpace(value))
	if value == "" || len(value) > 64 {
		return ""
	}
	for _, character := range value {
		if character >= 'A' && character <= 'Z' || character >= '0' && character <= '9' ||
			character == '_' || character == '-' || character == '.' {
			continue
		}
		return "UNCLASSIFIED"
	}
	return value
}

func safeCommerceTraceID(value string) string {
	value = strings.TrimSpace(value)
	if value == "" || len(value) > 256 {
		return ""
	}
	for _, character := range value {
		if character >= 'A' && character <= 'Z' || character >= 'a' && character <= 'z' ||
			character >= '0' && character <= '9' || character == '_' || character == '-' ||
			character == '.' || character == ':' {
			continue
		}
		return ""
	}
	return value
}

func selectedCommerceID(r *http.Request) (uuid.UUID, error) {
	value := strings.TrimSpace(r.URL.Query().Get("id"))
	if value == "" {
		return uuid.Nil, nil
	}
	id, err := uuid.Parse(value)
	if err != nil {
		return uuid.Nil, store.ErrNotFound
	}
	return id, nil
}

func (p *Portal) adminCommerceRegistrationPolicy(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, true, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
	version, err := strconv.ParseInt(r.Form.Get("version"), 10, 64)
	if !available || err != nil {
		p.renderError(w, http.StatusBadRequest, "注册策略无效", "策略版本或服务配置无效。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.UpdateRegistrationPolicy(r.Context(), account.ID, r.Form.Get("mode"), version, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "registration", "policy-updated", uuid.Nil)
}

func (p *Portal) adminCommerceCreateInvitationBatch(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
	maxUses, maxErr := parseBoundedInt(r.Form.Get("max_uses"), 1, 1000000)
	giftDays, giftErr := parseBoundedInt(r.Form.Get("gift_days"), 0, 3650)
	validUntil, dateErr := parseOptionalPortalDate(r.Form.Get("valid_until"), false)
	if !available || maxErr != nil || giftErr != nil || dateErr != nil {
		p.renderError(w, http.StatusBadRequest, "邀请码批次无效", "请检查使用次数、赠送天数和有效期。")
		return
	}
	input := store.InvitationBatchInput{
		Name: strings.TrimSpace(r.Form.Get("name")), Status: "ACTIVE", DefaultMaxUses: maxUses,
		EmailDomain: strings.ToLower(strings.TrimSpace(r.Form.Get("email_domain"))),
		GiftPlanKey: strings.TrimSpace(r.Form.Get("gift_plan_key")), GiftDurationDays: giftDays,
		ValidUntil: validUntil, Note: strings.TrimSpace(r.Form.Get("note")),
	}
	traceID, err := randomText(12)
	var batch store.InvitationBatch
	if err == nil {
		batch, err = repository.CreateInvitationBatch(r.Context(), account.ID, input, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "registration", "invitation-batch-created", batch.ID)
}

func (p *Portal) adminCommerceGenerateInvitationCodes(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
	batchID, idErr := uuid.Parse(r.PathValue("id"))
	count, countErr := parseBoundedInt(r.Form.Get("count"), 1, maxGeneratedCodeRows)
	maxUses, usesErr := parseBoundedInt(r.Form.Get("max_uses"), 1, 1000000)
	if !available || len(p.config.CommerceKey) < 32 || idErr != nil || countErr != nil || usesErr != nil {
		p.renderError(w, http.StatusBadRequest, "邀请码生成请求无效", "批次、数量、使用次数或商业化密钥无效。")
		return
	}
	batchName := ""
	if batches, listErr := repository.ListInvitationBatches(r.Context(), account.ID, commercePageLimit); listErr == nil {
		for _, batch := range batches {
			if batch.ID == batchID {
				batchName = batch.Name
				break
			}
		}
	}
	raw, records, err := p.generateCommerceCodes("TLI", "invitation-code", count, maxUses)
	traceID, traceErr := randomText(12)
	if err == nil {
		err = traceErr
	}
	if err == nil {
		_, err = repository.AddInvitationCodes(r.Context(), account.ID, batchID, records, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.renderGeneratedAdminCodes(w, session, account, "registration", batchID, batchName, raw)
}

func (p *Portal) adminCommerceInvitationBatchStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
	p.adminCommerceStatusMutation(w, r, available, "registration", func(id uuid.UUID, status, trace string) error {
		return repository.SetInvitationBatchStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) adminCommerceInvitationCodeStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRegistrationAdminRepository)
	p.adminCommerceStatusMutation(w, r, available, "registration", func(id uuid.UUID, status, trace string) error {
		return repository.SetInvitationCodeStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) adminCommerceCreateRedemptionBatch(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRedemptionAdminRepository)
	catalogVersion, versionErr := strconv.ParseInt(r.Form.Get("catalog_version"), 10, 64)
	durationDays, durationErr := strconv.ParseInt(r.Form.Get("duration_days"), 10, 64)
	maxClaims, claimsErr := parseBoundedInt(r.Form.Get("max_claims"), 1, 1000)
	validUntil, dateErr := parseOptionalPortalDate(r.Form.Get("valid_until"), false)
	if !available || versionErr != nil || durationErr != nil || durationDays < 1 || durationDays > 3650 || claimsErr != nil || dateErr != nil {
		p.renderError(w, http.StatusBadRequest, "兑换批次无效", "请检查套餐版本、赠送天数、单账号次数和有效期。")
		return
	}
	input := store.RedemptionBatchInput{
		Name: strings.TrimSpace(r.Form.Get("name")), PlanKey: strings.TrimSpace(r.Form.Get("plan_key")),
		CatalogVersion: catalogVersion, DurationDays: durationDays, MaxClaimsPerAccount: maxClaims,
		StackingRule: strings.TrimSpace(r.Form.Get("stacking_rule")), Status: "ACTIVE",
		ValidUntil: validUntil, Note: strings.TrimSpace(r.Form.Get("note")),
	}
	traceID, err := randomText(12)
	var batch store.RedemptionBatch
	if err == nil {
		batch, err = repository.CreateRedemptionBatch(r.Context(), account.ID, input, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "redemptions", "redemption-batch-created", batch.ID)
}

func (p *Portal) adminCommerceGenerateRedemptionCodes(w http.ResponseWriter, r *http.Request) {
	session, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRedemptionAdminRepository)
	batchID, idErr := uuid.Parse(r.PathValue("id"))
	count, countErr := parseBoundedInt(r.Form.Get("count"), 1, maxGeneratedCodeRows)
	maxUses, usesErr := parseBoundedInt(r.Form.Get("max_uses"), 1, 1000000)
	if !available || len(p.config.CommerceKey) < 32 || idErr != nil || countErr != nil || usesErr != nil {
		p.renderError(w, http.StatusBadRequest, "兑换码生成请求无效", "批次、数量、使用次数或商业化密钥无效。")
		return
	}
	raw, records, err := p.generateCommerceCodes("TLR", "redemption-code", count, maxUses)
	traceID, traceErr := randomText(12)
	if err == nil {
		err = traceErr
	}
	if err == nil {
		_, err = repository.AddRedemptionCodes(r.Context(), account.ID, batchID, records, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.renderGeneratedAdminCodes(w, session, account, "redemptions", batchID, "", raw)
}

func (p *Portal) adminCommerceRedemptionBatchStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRedemptionAdminRepository)
	p.adminCommerceStatusMutation(w, r, available, "redemptions", func(id uuid.UUID, status, trace string) error {
		return repository.SetRedemptionBatchStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) adminCommerceRedemptionCodeStatus(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceRedemptionAdminRepository)
	p.adminCommerceStatusMutation(w, r, available, "redemptions", func(id uuid.UUID, status, trace string) error {
		return repository.SetRedemptionCodeStatus(r.Context(), account.ID, id, status, trace)
	})
}

func (p *Portal) adminCommerceQueryOrder(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, commerceSecurityFormFields()); err != nil {
		p.renderError(w, http.StatusBadRequest, "查单请求无效", "请刷新订单页面后重新提交。")
		return
	}
	orderID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	repository, available := p.config.Repository.(commercePaymentOperationsRepository)
	if err != nil || !available || p.config.Commerce == nil {
		p.renderError(w, http.StatusBadRequest, "查单请求无效", "订单标识无效或支付服务不可用。")
		return
	}
	order, err := repository.GetOrderByID(r.Context(), orderID)
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	outcome, err := p.config.Commerce.QueryAndFulfill(r.Context(), order.AccountID, order.ID)
	if err != nil {
		p.handleCommerceServiceError(w, err)
		return
	}
	notice := "order-query-pending"
	if outcome.Fulfilled {
		notice = "order-query-fulfilled"
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.commerce.order.query.context", "order", order.ID)
	p.redirectAdminCommerce(w, r, "orders", notice, uuid.Nil)
}

func (p *Portal) adminCommerceRetryOrderFulfillment(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, commerceSecurityFormFields()); err != nil {
		p.renderError(w, http.StatusBadRequest, "履约重试无效", "请刷新订单页面后重新提交。")
		return
	}
	orderID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	repository, available := p.config.Repository.(commercePaymentOperationsRepository)
	if err != nil || !available {
		p.renderError(w, http.StatusBadRequest, "履约重试无效", "订单标识无效或订单服务不可用。")
		return
	}
	current, err := repository.GetOrderByID(r.Context(), orderID)
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	if current.Status != "PAID" && current.Status != "FULFILLING" && current.Status != "FULFILLED" {
		p.renderError(w, http.StatusConflict, "订单尚不能履约", "只有已确认支付的订单可以重试履约。")
		return
	}
	order, _, err := repository.FulfillOrder(r.Context(), orderID)
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.commerce.order.fulfillment.retry.context", "order", order.ID)
	p.redirectAdminCommerce(w, r, "orders", "order-fulfillment-retried", uuid.Nil)
}

func (p *Portal) adminCommerceCloseOrder(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	allowedFields := commerceSecurityFormFields()
	allowedFields["reason_code"] = true
	reasonCode := strings.ToUpper(strings.TrimSpace(r.Form.Get("reason_code")))
	if validateCommerceFormValues(r.Form, allowedFields) != nil || !store.ValidAdminOrderCloseReasonCode(reasonCode) {
		p.renderError(w, http.StatusBadRequest, "关单请求无效", "请选择有效的关单原因并刷新订单页面后重试。")
		return
	}
	orderID, err := parseCanonicalPortalUUID(r.PathValue("id"))
	repository, available := p.config.Repository.(commerceAdminCloseRepository)
	if err != nil || !available {
		p.renderError(w, http.StatusBadRequest, "关单请求无效", "订单标识无效或安全关单服务不可用。")
		return
	}
	browserKey := strings.TrimSpace(r.Form.Get("idempotency_key"))
	requestHash := adminCommerceCloseRequestHash(
		account.ID, orderID, reasonCode, strings.TrimSpace(r.Form.Get("reason")),
	)
	traceID, err := randomText(12)
	var result store.AdminCloseOrderResult
	if err == nil {
		result, err = repository.AdminCloseUnpaidOrderFromWeb(r.Context(), store.AdminCloseOrderInput{
			ActorID: account.ID, OrderID: orderID, ReasonCode: reasonCode,
			IdempotencyKey: adminCommerceCloseStoreKey(browserKey), RequestHash: requestHash, TraceID: traceID,
		})
	}
	if err != nil {
		switch {
		case errors.Is(err, store.ErrAdminOrderNotClosable):
			p.renderError(w, http.StatusConflict, "订单不能安全关闭", "订单可能已经支付、进入履约或由其他请求更新，请刷新页面确认最新状态。")
		case errors.Is(err, store.ErrAdminCloseIdempotencyConflict):
			p.renderError(w, http.StatusConflict, "关单请求冲突", "该请求标识已用于不同的关单操作，请刷新订单页面后重试。")
		default:
			p.handleCommerceMutationError(w, err)
		}
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.commerce.order.admin_close.context", "order", result.Order.ID)
	p.redirectAdminCommerce(w, r, "orders", "order-closed", uuid.Nil)
}

func (p *Portal) adminCommerceCreateManualEntitlement(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	allowedFields := commerceSecurityFormFields()
	allowedFields["account_id"] = true
	allowedFields["plan_key"] = true
	allowedFields["duration_days"] = true
	targetID, idErr := parseCanonicalPortalUUID(strings.TrimSpace(r.Form.Get("account_id")))
	planKey := strings.TrimSpace(r.Form.Get("plan_key"))
	durationDays, daysErr := parseBoundedInt(r.Form.Get("duration_days"), 1, 365)
	if validateCommerceFormValues(r.Form, allowedFields) != nil || idErr != nil ||
		!validCommercePlanKey(planKey) || daysErr != nil {
		p.renderError(w, http.StatusBadRequest, "人工权益请求无效", "请选择有效账号、已发布套餐和 1 到 365 天的赠送时长。")
		return
	}
	repository, available := p.config.Repository.(commerceManualEntitlementAdminRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "人工权益暂不可用", "人工权益存储尚未就绪。")
		return
	}
	browserKey := strings.TrimSpace(r.Form.Get("idempotency_key"))
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.CreateManualEntitlementGrantFromWeb(r.Context(), account.ID, store.CreateManualEntitlementGrantInput{
			AccountID: targetID, PlanKey: planKey, DurationDays: durationDays,
			Reason:         strings.TrimSpace(r.Form.Get("reason")),
			IdempotencyKey: manualEntitlementStoreKey("create", browserKey), TraceID: traceID,
		})
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "entitlements", "manual-entitlement-created", uuid.Nil)
}

func (p *Portal) adminCommerceRevokeManualEntitlement(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	if validateCommerceFormValues(r.Form, commerceSecurityFormFields()) != nil {
		p.renderError(w, http.StatusBadRequest, "撤销人工权益请求无效", "请刷新人工权益页面后重新提交。")
		return
	}
	grantID, idErr := parseCanonicalPortalUUID(r.PathValue("id"))
	if idErr != nil {
		p.renderError(w, http.StatusBadRequest, "撤销人工权益请求无效", "权益标识无效。")
		return
	}
	repository, available := p.config.Repository.(commerceManualEntitlementAdminRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "撤销人工权益暂不可用", "人工权益存储尚未就绪。")
		return
	}
	browserKey := strings.TrimSpace(r.Form.Get("idempotency_key"))
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.RevokeManualEntitlementGrantFromWeb(r.Context(), account.ID, store.RevokeManualEntitlementGrantInput{
			GrantID: grantID, Reason: strings.TrimSpace(r.Form.Get("reason")),
			IdempotencyKey: manualEntitlementStoreKey("revoke", browserKey), TraceID: traceID,
		})
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "entitlements", "manual-entitlement-revoked", uuid.Nil)
}

func manualEntitlementStoreKey(operation, browserKey string) string {
	digest := sha256.Sum256([]byte("TermLink admin web manual entitlement idempotency v1\x00" + operation + "\x00" + browserKey))
	return fmt.Sprintf("web-manual-%s:%x", operation, digest[:])
}

func adminCommerceCloseStoreKey(browserKey string) string {
	digest := sha256.Sum256([]byte("TermLink admin web order close idempotency v1\x00" + browserKey))
	return fmt.Sprintf("web-close:%x", digest[:])
}

func adminCommerceCloseRequestHash(actorID, orderID uuid.UUID, reasonCode, reason string) [32]byte {
	return sha256.Sum256([]byte("TermLink admin web order close v1\x00" + actorID.String() + "\x00" +
		orderID.String() + "\x00" + reasonCode + "\x00" + reason))
}

func (p *Portal) adminCommerceCreatePlanDraft(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	input, err := parseCommercePlanDraftForm(r.Form, nil)
	repository, available := p.config.Repository.(commerceCatalogAdminRepository)
	if err != nil || !available {
		p.renderError(w, http.StatusBadRequest, "套餐草稿无效", "请检查套餐字段、价格、可见平台和销售时间。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.CreatePlanDraft(r.Context(), account.ID, uuid.Nil, input, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "catalog", "catalog-plan-created", uuid.Nil)
}

func (p *Portal) adminCommerceUpdatePlanDraft(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	planID, idErr := parseCanonicalPortalUUID(r.PathValue("id"))
	repository, available := p.config.Repository.(commerceCatalogAdminRepository)
	if idErr != nil || !available {
		p.renderError(w, http.StatusBadRequest, "套餐草稿无效", "套餐标识或服务配置无效。")
		return
	}
	draft, err := repository.GetCommerceCatalogDraft(r.Context(), account.ID)
	var current *store.PlanDraft
	if err == nil && draft != nil {
		for index := range draft.Plans {
			if draft.Plans[index].ID == planID {
				current = &draft.Plans[index]
				break
			}
		}
	}
	if err != nil || current == nil {
		p.handleCommerceMutationError(w, store.ErrNotFound)
		return
	}
	input, err := parseCommercePlanDraftForm(r.Form, current)
	if err != nil {
		p.renderError(w, http.StatusBadRequest, "套餐草稿无效", "请检查套餐字段、价格、可见平台和销售时间。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.UpdatePlanDraft(r.Context(), account.ID, uuid.Nil, planID, input, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "catalog", "catalog-plan-updated", uuid.Nil)
}

func (p *Portal) adminCommercePublishCatalog(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	if err := validateCommerceFormValues(r.Form, commerceSecurityFormFields()); err != nil {
		p.renderError(w, http.StatusBadRequest, "发布请求无效", "请刷新套餐草稿页面后重新提交。")
		return
	}
	repository, available := p.config.Repository.(commerceCatalogAdminRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "套餐发布暂不可用", "套餐草稿存储尚未就绪。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.PublishCatalog(r.Context(), account.ID, uuid.Nil, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "catalog", "catalog-published", uuid.Nil)
}

func (p *Portal) adminCommerceStatusMutation(w http.ResponseWriter, r *http.Request, available bool, section string, mutate func(uuid.UUID, string, string) error) {
	id, err := uuid.Parse(r.PathValue("id"))
	if !available || err != nil {
		p.renderError(w, http.StatusBadRequest, "状态请求无效", "目标记录或服务配置无效。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		err = mutate(id, r.Form.Get("status"), traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, section, "status-updated", uuid.Nil)
}

func (p *Portal) adminCommerceCreateRefund(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	orderID, idErr := uuid.Parse(r.Form.Get("order_id"))
	amount, amountErr := strconv.ParseInt(r.Form.Get("amount_minor"), 10, 64)
	reason := strings.TrimSpace(r.Form.Get("reason"))
	if p.config.Commerce == nil {
		p.renderError(w, http.StatusServiceUnavailable, "退款服务不可用", "支付网关与退款编排服务尚未就绪。")
		return
	}
	if idErr != nil || amountErr != nil || amount < 1 || len(reason) < 3 {
		p.renderError(w, http.StatusBadRequest, "退款请求无效", "请填写有效订单、退款金额和原因。")
		return
	}
	traceID, err := randomText(12)
	var outcome commercecontrol.RefundOutcome
	if err == nil {
		outcome, err = p.config.Commerce.CreateRefund(r.Context(), commercecontrol.CreateRefundRequest{
			ActorID: account.ID, OrderID: orderID, AmountMinor: amount, Reason: reason, TraceID: traceID,
		})
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	if !outcome.ApprovalRequired || outcome.Refund.ID == uuid.Nil || outcome.Refund.OrderID != orderID ||
		outcome.Refund.AmountMinor != amount || outcome.Refund.Status != "APPROVAL_PENDING" || outcome.Refund.Version < 1 {
		p.internalError(w, errors.New("refund request did not enter approval pending"))
		return
	}
	p.redirectAdminCommerce(w, r, "orders", "refund-created", uuid.Nil)
}

func (p *Portal) adminCommerceConfirmRefund(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	if p.config.Commerce == nil {
		p.renderError(w, http.StatusServiceUnavailable, "退款服务不可用", "支付网关与退款编排服务尚未就绪。")
		return
	}
	allowed := map[string]bool{
		"csrf": true, "reason": true, "idempotency_key": true, "expected_version": true,
	}
	refundID, idErr := parseCanonicalPortalUUID(r.PathValue("id"))
	expectedVersion, versionErr := strconv.ParseInt(strings.TrimSpace(r.Form.Get("expected_version")), 10, 64)
	if validateCommerceFormValues(r.Form, allowed) != nil || idErr != nil || versionErr != nil || expectedVersion < 1 {
		p.renderError(w, http.StatusBadRequest, "退款确认无效", "请刷新退款列表并重新确认当前审批版本。")
		return
	}
	traceID, err := randomText(12)
	var outcome commercecontrol.RefundOutcome
	if err == nil {
		outcome, err = p.config.Commerce.ConfirmRefund(r.Context(), commercecontrol.ConfirmRefundRequest{
			ActorID: account.ID, RefundID: refundID, ExpectedVersion: expectedVersion, TraceID: traceID,
		})
	}
	if err != nil && outcome.Refund.ID == uuid.Nil {
		p.handleRefundConfirmationError(w, err)
		return
	}
	if outcome.Refund.ID != refundID || outcome.Refund.Version <= expectedVersion || outcome.Refund.Status == "APPROVAL_PENDING" {
		p.internalError(w, errors.New("refund confirmation returned an invalid state"))
		return
	}
	p.auditUserPortalActionSuccess(r, account.ID, "web.commerce.refund.confirm.context", "refund", refundID)
	notice := "refund-confirmed-pending"
	switch outcome.Refund.Status {
	case "SUCCEEDED":
		notice = "refund-confirmed"
	case "FAILED":
		notice = "refund-confirmation-failed"
	}
	// Once approval has transitioned, a provider transport/configuration error
	// is intentionally treated as an ambiguous pending result. The stable
	// merchant refund number and recovery runner must reconcile it; the browser
	// must not issue a second money-moving request.
	p.redirectAdminCommerce(w, r, "orders", notice, uuid.Nil)
}

func (p *Portal) handleRefundConfirmationError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrRefundApprovalExpired):
		p.renderError(w, http.StatusGone, "退款审批已过期", "该二次确认窗口已经过期，请刷新列表并重新创建退款申请。")
	case errors.Is(err, store.ErrRefundVersionConflict), errors.Is(err, store.ErrConflict):
		p.renderError(w, http.StatusConflict, "退款审批版本已变化", "该退款已确认、过期或由其他管理员处理，请刷新列表确认最新状态。")
	case errors.Is(err, commercecontrol.ErrInvalidRequest):
		p.renderError(w, http.StatusBadRequest, "退款确认无效", "退款标识、审批版本或确认参数无效。")
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "退款确认被拒绝", "当前管理员或退款状态不允许执行确认。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "退款记录不存在", "该退款记录不存在或已不再可访问。")
	default:
		p.internalError(w, err)
	}
}

func (p *Portal) adminCommerceCreateReconciliation(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, false, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commercePaymentAdminRepository)
	start, startErr := parseOptionalPortalDate(r.Form.Get("period_start"), true)
	end, endErr := parseOptionalPortalDate(r.Form.Get("period_end"), false)
	if !available || startErr != nil || endErr != nil || start == nil || end == nil || !end.After(*start) {
		p.renderError(w, http.StatusBadRequest, "对账请求无效", "请选择有效且结束时间晚于开始时间的对账周期。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.CreateReconciliationRun(r.Context(), account.ID, r.Form.Get("provider"), *start, *end, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "payments", "reconciliation-created", uuid.Nil)
}

func (p *Portal) adminCommerceEnsurePaymentChannel(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, true, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceChannelAdminRepository)
	if !available {
		p.renderError(w, http.StatusServiceUnavailable, "支付配置不可用", "支付渠道存储尚未就绪。")
		return
	}
	traceID, err := randomText(12)
	var channel store.PaymentChannel
	if err == nil {
		channel, err = repository.EnsurePaymentChannel(r.Context(), account.ID, r.Form.Get("provider"), r.Form.Get("scene"), traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "channels", "channel-created", channel.ID)
}

func (p *Portal) adminCommerceCreatePaymentVersion(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, true, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceChannelAdminRepository)
	channelID, idErr := uuid.Parse(r.PathValue("id"))
	publicConfig := json.RawMessage(strings.TrimSpace(r.Form.Get("public_config")))
	secretRefs := json.RawMessage(strings.TrimSpace(r.Form.Get("secret_refs")))
	if !available || p.config.Commerce == nil || idErr != nil || !json.Valid(publicConfig) || !json.Valid(secretRefs) {
		p.renderError(w, http.StatusBadRequest, "支付版本无效", "公开配置和 Secret 引用必须是有效 JSON 对象。")
		return
	}
	traceID, err := randomText(12)
	var version store.PaymentChannelVersion
	if err == nil {
		version, err = repository.CreatePaymentChannelVersion(r.Context(), account.ID, channelID, store.PaymentChannelVersionInput{
			Environment: r.Form.Get("environment"), PublicConfig: publicConfig, SecretRefs: secretRefs,
		}, traceID)
	}
	if err == nil {
		var channel store.PaymentChannel
		channels, listErr := repository.ListPaymentChannels(r.Context(), account.ID)
		if listErr != nil {
			err = listErr
		} else {
			for _, candidate := range channels {
				if candidate.ID == channelID {
					channel = candidate
					break
				}
			}
			if channel.ID == uuid.Nil {
				err = store.ErrNotFound
			} else if validationErr := p.config.Commerce.ValidatePaymentChannelVersion(r.Context(), channel, version); validationErr != nil {
				validationTrace, _ := randomText(12)
				_, _ = repository.ValidatePaymentChannelVersion(r.Context(), account.ID, version.ID, false, "LOCAL_MATERIAL_VALIDATION_FAILED", validationTrace)
				err = validationErr
			} else if probeErr := p.config.Commerce.ProbePaymentChannelVersion(r.Context(), channel, version); probeErr != nil {
				validationTrace, _ := randomText(12)
				_, _ = repository.ValidatePaymentChannelVersion(r.Context(), account.ID, version.ID, false, "PROVIDER_CONNECTIVITY_PROBE_FAILED", validationTrace)
				err = probeErr
			} else {
				validationTrace, traceErr := randomText(12)
				if traceErr != nil {
					err = traceErr
				} else {
					_, err = repository.ValidatePaymentChannelVersion(r.Context(), account.ID, version.ID, true, "LOCAL_MATERIAL_AND_PROVIDER_CONNECTIVITY_VALIDATED", validationTrace)
				}
			}
		}
	}
	// Form buffers are not retained, logged, redirected, or rendered.
	clear(publicConfig)
	clear(secretRefs)
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "channels", "channel-version-created", channelID)
}

func (p *Portal) adminCommerceActivatePaymentVersion(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, true, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceChannelAdminRepository)
	channelID, channelErr := uuid.Parse(r.PathValue("id"))
	versionID, versionErr := uuid.Parse(r.Form.Get("version_id"))
	if !available || channelErr != nil || versionErr != nil {
		p.renderError(w, http.StatusBadRequest, "支付启用请求无效", "请选择有效渠道和已验证配置版本。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.ActivatePaymentChannelVersion(r.Context(), account.ID, channelID, versionID, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "channels", "channel-activated", channelID)
}

func (p *Portal) adminCommerceDisablePaymentChannel(w http.ResponseWriter, r *http.Request) {
	_, account, ok := p.requireCommerceAdminAction(w, r, true, true)
	if !ok {
		return
	}
	repository, available := p.config.Repository.(commerceChannelAdminRepository)
	channelID, idErr := uuid.Parse(r.PathValue("id"))
	if !available || idErr != nil {
		p.renderError(w, http.StatusBadRequest, "支付停用请求无效", "支付渠道标识无效。")
		return
	}
	traceID, err := randomText(12)
	if err == nil {
		_, err = repository.DisablePaymentChannel(r.Context(), account.ID, channelID, traceID)
	}
	if err != nil {
		p.handleCommerceMutationError(w, err)
		return
	}
	p.redirectAdminCommerce(w, r, "channels", "channel-disabled", channelID)
}

func (p *Portal) requireCommerceAccount(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	if !p.usesPublicHost(r) {
		http.Redirect(w, r, strings.TrimRight(p.config.PublicURL, "/")+r.URL.Path, http.StatusSeeOther)
		return webSession{}, store.PortalAccount{}, false
	}
	session, err := p.readSession(r)
	if err != nil {
		p.clearCookie(w, sessionCookieName)
		http.Redirect(w, r, "/login?return_to=/account", http.StatusSeeOther)
		return webSession{}, store.PortalAccount{}, false
	}
	account, err := p.currentAccount(r.Context(), session)
	if err != nil {
		p.renderError(w, http.StatusForbidden, "账号不可用", "账号已被停用或浏览器会话需要重新登录。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) requireCommerceAccountAction(w http.ResponseWriter, r *http.Request) (webSession, store.PortalAccount, bool) {
	session, account, ok := p.requireCommerceAccount(w, r)
	if !ok {
		return webSession{}, store.PortalAccount{}, false
	}
	if !p.usesTrustedFormOrigin(r) || r.ParseForm() != nil || !constantTextEqual(session.CSRF, r.Form.Get("csrf")) {
		p.renderError(w, http.StatusForbidden, "请求被拒绝", "请求来源或安全校验无效，请刷新页面后重试。")
		return webSession{}, store.PortalAccount{}, false
	}
	return session, account, true
}

func (p *Portal) requireCommerceAdminAction(w http.ResponseWriter, r *http.Request, superOnly, recent bool) (webSession, store.PortalAccount, bool) {
	_ = recent
	return p.requireSensitiveAdminAction(w, r, superOnly)
}

func (p *Portal) generateCommerceCodes(prefix, purpose string, count, maxUses int) ([]string, []store.CodeDigestRecord, error) {
	raw := make([]string, 0, count)
	records := make([]store.CodeDigestRecord, 0, count)
	seen := make(map[[32]byte]struct{}, count)
	for len(raw) < count {
		code, err := randomPortalCommerceCode(prefix)
		if err != nil {
			return nil, nil, err
		}
		normalized := normalizePortalCommerceCode(code)
		digest := portalCommerceDigest(p.config.CommerceKey, purpose, normalized)
		if _, exists := seen[digest]; exists {
			continue
		}
		seen[digest] = struct{}{}
		raw = append(raw, code)
		records = append(records, store.CodeDigestRecord{
			ID: uuid.New(), Digest: digest, Prefix: normalized[:8], MaxUses: maxUses,
		})
	}
	return raw, records, nil
}

func randomPortalCommerceCode(prefix string) (string, error) {
	const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
	randomCharacterCount := 21
	groupSize := 7
	if prefix == "TLI" {
		randomCharacterCount = 12
		groupSize = 4
	}
	bytes := make([]byte, randomCharacterCount)
	if _, err := rand.Read(bytes); err != nil {
		return "", err
	}
	var normalized strings.Builder
	normalized.Grow(len(prefix) + len(bytes))
	normalized.WriteString(prefix)
	for _, value := range bytes {
		normalized.WriteByte(alphabet[int(value)%len(alphabet)])
	}
	value := normalized.String()
	var display strings.Builder
	display.WriteString(prefix)
	for index, character := range value[len(prefix):] {
		if index%groupSize == 0 {
			display.WriteByte('-')
		}
		display.WriteRune(character)
	}
	return display.String(), nil
}

func portalCommerceDigest(key []byte, purpose, value string) [32]byte {
	mac := hmac.New(sha256.New, key)
	_, _ = mac.Write([]byte("TermLink commerce v1\x00"))
	_, _ = mac.Write([]byte(purpose))
	_, _ = mac.Write([]byte{0})
	_, _ = mac.Write([]byte(value))
	var result [32]byte
	copy(result[:], mac.Sum(nil))
	return result
}

func normalizePortalCommerceCode(value string) string {
	value = strings.ToUpper(strings.TrimSpace(value))
	var output strings.Builder
	output.Grow(len(value))
	for _, character := range value {
		if character == '-' || unicode.IsSpace(character) {
			continue
		}
		if character >= 'A' && character <= 'Z' || character >= '2' && character <= '9' {
			output.WriteRune(character)
			continue
		}
		return ""
	}
	return output.String()
}

func validPortalCommerceCode(code, prefix string) bool {
	minimumLength := 24
	if prefix == "TLI" {
		minimumLength = 15
	}
	if len(code) < minimumLength || len(code) > 64 || !strings.HasPrefix(code, prefix) {
		return false
	}
	for _, character := range code {
		if character < 'A' || character > 'Z' {
			if character < '2' || character > '9' {
				return false
			}
		}
	}
	return true
}

func (p *Portal) renderGeneratedAdminCodes(w http.ResponseWriter, session webSession, account store.PortalAccount, section string, selectedID uuid.UUID, batchName string, raw []string) {
	actionNonce, err := randomText(24)
	if err != nil {
		p.internalError(w, err)
		return
	}
	p.render(w, http.StatusCreated, "commerce-admin.html", adminCommercePageData{
		Username: session.Username, Role: account.Role, RoleLabel: roleLabel(account.Role), CSRF: session.CSRF,
		Section: section, SectionTitle: commerceAdminSectionTitle(section),
		CanOperate:         account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin,
		CanConfigure:       account.Role == store.RoleSuperAdmin,
		CanRefund:          (account.Role == store.RoleAdmin || account.Role == store.RoleSuperAdmin) && p.config.Commerce != nil,
		CanCreateVersion:   account.Role == store.RoleSuperAdmin && p.config.Commerce != nil,
		CanConfigureSystem: account.Role == store.RoleSuperAdmin,
		CommerceNavigation: true,
		SelectedID:         selectedID, GeneratedCodes: raw,
		GeneratedBatchName: batchName, GeneratedAt: p.now().UTC(),
		ActionNonce: actionNonce,
		Notice: bilingualPortalNotice(
			"明文代码仅在本页显示一次。请立即保存到受控位置，离开后无法恢复。",
			"Plaintext codes are shown only once on this page. Store them in a controlled location now; they cannot be recovered after leaving.",
		),
	})
}

func (p *Portal) redirectAdminCommerce(w http.ResponseWriter, r *http.Request, section, notice string, id uuid.UUID) {
	query := url.Values{"section": {section}, "notice": {notice}}
	if id != uuid.Nil {
		query.Set("id", id.String())
	}
	http.Redirect(w, r, "/admin/commerce?"+query.Encode(), http.StatusSeeOther)
}

func (p *Portal) renderCommerceClaimError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrRedemptionExpired):
		p.renderError(w, http.StatusUnprocessableEntity, "兑换码已过期", "该兑换码已超过有效期。")
	case errors.Is(err, store.ErrRedemptionExhausted):
		p.renderError(w, http.StatusConflict, "兑换次数已用完", "该兑换码已达到最大核销次数。")
	case errors.Is(err, store.ErrRedemptionAlreadyUsed):
		p.renderError(w, http.StatusConflict, "已经兑换过", "当前账号已达到该兑换码允许的核销次数。")
	case errors.Is(err, store.ErrRedemptionConflict), errors.Is(err, store.ErrRedemptionNotApplicable):
		p.renderError(w, http.StatusConflict, "当前套餐不适用", "该赠送权益与当前套餐的叠加规则冲突。")
	case errors.Is(err, store.ErrRedemptionInvalid):
		p.renderError(w, http.StatusUnprocessableEntity, "兑换码无效", "请检查兑换码后重试。")
	default:
		p.internalError(w, err)
	}
}

func (p *Portal) handleCommerceMutationError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, store.ErrDenied):
		p.renderError(w, http.StatusForbidden, "操作被拒绝", "当前账号、输入或目标状态不允许该操作。")
	case errors.Is(err, store.ErrNotFound):
		p.renderError(w, http.StatusNotFound, "记录不存在", "目标记录可能已删除或不属于当前账号。")
	case errors.Is(err, store.ErrConflict), errors.Is(err, store.ErrOrderNotPayable):
		p.renderError(w, http.StatusConflict, "状态已变化", "记录已由其他请求更新，请刷新页面确认最新状态。")
	case errors.Is(err, store.ErrPaymentChannelUnavailable):
		p.renderError(w, http.StatusServiceUnavailable, "支付渠道不可用", "当前支付渠道没有可用的已验证配置。")
	default:
		p.internalError(w, err)
	}
}

func parseBoundedInt(value string, minimum, maximum int) (int, error) {
	parsed, err := strconv.Atoi(strings.TrimSpace(value))
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, store.ErrDenied
	}
	return parsed, nil
}

func commerceSecurityFormFields() map[string]bool {
	return map[string]bool{"csrf": true, "reason": true, "idempotency_key": true}
}

func commercePlanDraftFormFields() map[string]bool {
	fields := commerceSecurityFormFields()
	for _, name := range []string{
		"plan_key", "display_name", "description", "billing_period", "sort_order",
		"visible_platforms", "available_from", "available_until", "fallback_plan_key",
		"price_minor", "currency",
	} {
		fields[name] = true
	}
	return fields
}

func parseCommercePlanDraftForm(values url.Values, current *store.PlanDraft) (store.PlanDraftInput, error) {
	if validateCommerceFormValues(values, commercePlanDraftFormFields()) != nil {
		return store.PlanDraftInput{}, store.ErrDenied
	}
	planKey := strings.TrimSpace(values.Get("plan_key"))
	displayName := strings.TrimSpace(values.Get("display_name"))
	description := strings.TrimSpace(values.Get("description"))
	period := strings.ToUpper(strings.TrimSpace(values.Get("billing_period")))
	currency := strings.ToUpper(strings.TrimSpace(values.Get("currency")))
	sortOrder, sortErr := parseBoundedInt(values.Get("sort_order"), -100000, 100000)
	priceMinor, priceErr := strconv.ParseInt(strings.TrimSpace(values.Get("price_minor")), 10, 64)
	from, fromErr := parseOptionalCommercePlanTime(values.Get("available_from"))
	until, untilErr := parseOptionalCommercePlanTime(values.Get("available_until"))
	platforms, platformErr := parseCommercePlatforms(values.Get("visible_platforms"))
	if sortErr != nil || priceErr != nil || fromErr != nil || untilErr != nil || platformErr != nil ||
		!validCommercePlanKey(planKey) || displayName == "" || len(displayName) > 128 ||
		len(description) > 4096 || (period != "FREE" && period != "MONTHLY" && period != "YEARLY") ||
		currency != "CNY" || priceMinor < 0 || priceMinor > 100_000_000 || period == "FREE" && priceMinor != 0 ||
		(from != nil && until != nil && !until.After(*from)) ||
		len(strings.TrimSpace(values.Get("fallback_plan_key"))) > 64 {
		return store.PlanDraftInput{}, store.ErrDenied
	}
	featureFlags := json.RawMessage(`{}`)
	channelProducts := json.RawMessage(`{}`)
	if current != nil {
		featureFlags = append(json.RawMessage(nil), current.FeatureFlags...)
		channelProducts = append(json.RawMessage(nil), current.ChannelProducts...)
	}
	return store.PlanDraftInput{
		PlanKey: planKey, DisplayName: displayName, Description: description, BillingPeriod: period,
		MaxRemotePCs: store.UnlimitedQuota, MaxLiveSessions: store.UnlimitedQuota,
		FeatureFlags: featureFlags, ChannelProducts: channelProducts, SortOrder: sortOrder,
		VisiblePlatforms: platforms, AvailableFrom: from, AvailableUntil: until,
		FallbackPlanKey: strings.TrimSpace(values.Get("fallback_plan_key")),
		PriceMinor:      priceMinor, Currency: currency,
	}, nil
}

func validCommercePlanKey(value string) bool {
	if value == "" || len(value) > 64 {
		return false
	}
	for _, character := range value {
		if character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
			character >= '0' && character <= '9' || character == '_' || character == '-' || character == '.' {
			continue
		}
		return false
	}
	return true
}

func parseOptionalCommercePlanTime(value string) (*time.Time, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parsed, err := time.ParseInLocation(commerceReportTimeLayout, value, time.UTC)
	if err != nil {
		return nil, err
	}
	parsed = parsed.UTC()
	return &parsed, nil
}

func parseCommercePlatforms(value string) ([]string, error) {
	parts := strings.FieldsFunc(value, func(character rune) bool { return character == ',' || character == '/' })
	if len(parts) < 1 || len(parts) > 8 {
		return nil, store.ErrDenied
	}
	result := make([]string, 0, len(parts))
	seen := make(map[string]bool, len(parts))
	for _, part := range parts {
		platform := strings.ToUpper(strings.TrimSpace(part))
		if platform != "ANDROID" && platform != "WEB" && platform != "WINDOWS" || seen[platform] {
			return nil, store.ErrDenied
		}
		seen[platform] = true
		result = append(result, platform)
	}
	return result, nil
}

func parseOptionalPortalDate(value string, start bool) (*time.Time, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil, nil
	}
	parsed, err := time.Parse("2006-01-02", value)
	if err != nil {
		return nil, err
	}
	parsed = parsed.UTC()
	if !start {
		parsed = parsed.Add(24*time.Hour - time.Nanosecond)
	}
	return &parsed, nil
}

func formatCommerceMoney(amount int64, currency string) string {
	if strings.EqualFold(currency, "CNY") {
		sign := ""
		if amount < 0 {
			sign = "-"
			amount = -amount
		}
		return fmt.Sprintf("%s¥%d.%02d", sign, amount/100, amount%100)
	}
	return fmt.Sprintf("%s %d", strings.ToUpper(currency), amount)
}

func commerceStatusLabel(status string) string {
	labels := map[string]string{
		"ACTIVE": "生效中", "DISABLED": "已停用", "DRAFT": "草稿", "EXPIRED": "已过期",
		"CREATED": "已创建", "PENDING_PAYMENT": "待支付", "PAID": "已支付", "FULFILLING": "履约中",
		"FULFILLED": "已履约", "CLOSED": "已关闭", "FAILED": "失败", "REFUNDING": "退款中", "REFUNDED": "已退款",
		"APPROVAL_PENDING": "等待二次确认", "REVOKED": "已撤销",
		"RESERVED": "注册处理中", "PROVIDER_ACCEPTED": "身份已创建", "REDEEMED": "已绑定账号", "RELEASED": "已释放",
		"SUCCEEDED": "成功", "PENDING": "处理中", "VALIDATED": "已验证", "RETIRED": "已退役", "HEALTHY": "健康",
	}
	if label := labels[strings.ToUpper(status)]; label != "" {
		return label
	}
	return status
}

func normalizeCommerceAdminSection(section string) string {
	switch section {
	case "catalog", "reports", "consistency", "reconciliation", "registration", "redemptions", "entitlements", "orders", "payments", "channels":
		return section
	default:
		return "registration"
	}
}

func commerceAdminSectionTitle(section string) string {
	return map[string]string{
		"catalog": "套餐草稿", "reports": "订单报表", "consistency": "账务一致性",
		"reconciliation": "对账差异处理",
		"registration":   "注册与邀请码", "redemptions": "兑换码管理", "entitlements": "人工权益",
		"orders":   "订单与退款",
		"payments": "支付事件与对账", "channels": "支付渠道",
	}[normalizeCommerceAdminSection(section)]
}

func commerceNotice(value string) string {
	labels := map[string]string{
		"order-cancelled":    bilingualPortalNotice("订单已关闭。已支付或正在履约的订单不会被浏览器取消。", "The order was closed. A browser cannot cancel an order that is paid or being fulfilled."),
		"device-revoked":     bilingualPortalNotice("设备已撤销；相关授权、控制租约和后续密钥分发已停止。", "The device was revoked. Its grants, control leases, and future key distribution were stopped."),
		"pc-policy-updated":  bilingualPortalNotice("PC 远程路由和控制策略已更新。", "The PC remote route and control policy were updated."),
		"history-deleted":    bilingualPortalNotice("已退出会话的服务端加密历史已永久删除。", "The encrypted server history for the exited session was permanently deleted."),
		"export-requested":   bilingualPortalNotice("数据导出申请已进入队列，请刷新状态查看处理结果。", "The data export was queued. Refresh its status to see the result."),
		"deletion-requested": bilingualPortalNotice("账号注销申请已创建，仍需在确认时限内完成二次确认。", "The account deletion request was created and still requires confirmation before the deadline."),
		"deletion-confirmed": bilingualPortalNotice("账号注销已确认并进入 7 天冷静期；当前仍未删除，可在允许状态下取消。", "Account deletion was confirmed and entered the 7-day cooling-off period. Nothing has been deleted yet, and cancellation remains available when permitted."),
		"deletion-cancelled": bilingualPortalNotice("账号注销申请已取消，账号数据没有被删除。", "The account deletion request was cancelled. Account data was not deleted."),
		"nickname-updated":   bilingualPortalNotice("昵称已更新；用户名和用户 ID 保持不变。", "The nickname was updated. The username and user ID did not change."),
		"email-updated":      bilingualPortalNotice("新邮箱已验证并生效，现在可以使用该邮箱登录。", "The new email address was verified and is now active for sign-in."),
	}
	return labels[value]
}

func commerceAdminNotice(value string) string {
	labels := map[string]string{
		"policy-updated":             bilingualPortalNotice("注册策略已更新并写入审计。", "The registration policy was updated and audited."),
		"invitation-batch-created":   bilingualPortalNotice("邀请码批次已创建。", "The invitation-code batch was created."),
		"redemption-batch-created":   bilingualPortalNotice("兑换码批次已创建。", "The redemption-code batch was created."),
		"status-updated":             bilingualPortalNotice("状态已更新并写入审计。", "The status was updated and audited."),
		"order-query-pending":        bilingualPortalNotice("主动查单已完成，支付渠道尚未确认成功。", "The authoritative payment query completed, but the provider has not confirmed success."),
		"order-query-fulfilled":      bilingualPortalNotice("主动查单已确认支付成功，订单已完成幂等履约。", "The authoritative query confirmed payment, and the order was fulfilled idempotently."),
		"order-fulfillment-retried":  bilingualPortalNotice("已安全重试订单履约，请确认最新订单与权益状态。", "Order fulfillment was retried safely. Review the latest order and entitlement state."),
		"order-closed":               bilingualPortalNotice("未支付订单已安全关闭；服务端已再次确认没有成功支付交易。", "The unpaid order was closed safely after the server reconfirmed that no successful payment exists."),
		"catalog-plan-created":       bilingualPortalNotice("套餐草稿已创建；发布前请复核校验结果。", "The plan draft was created. Review validation results before publishing."),
		"catalog-plan-updated":       bilingualPortalNotice("套餐草稿已更新；渠道映射与 Feature Flags 保持服务端原值。", "The plan draft was updated while provider mappings and feature flags retained their server values."),
		"catalog-published":          bilingualPortalNotice("套餐目录已发布，新的公开目录现已生效。", "The plan catalog was published and is now active."),
		"manual-entitlement-created": bilingualPortalNotice("人工权益已创建并完成权益重算；该操作不会修改任何订单支付状态。", "The manual entitlement was created and recalculated without changing any order payment state."),
		"manual-entitlement-revoked": bilingualPortalNotice("人工权益已撤销并完成权益重算。", "The manual entitlement was revoked and entitlements were recalculated."),
		"refund-created":             bilingualPortalNotice("全额退款申请已创建，当前仅处于 APPROVAL_PENDING；尚未向支付渠道发起退款。", "The full-refund request is APPROVAL_PENDING. No refund has been sent to the provider yet."),
		"refund-confirmed":           bilingualPortalNotice("退款二次确认已完成，支付渠道已确认退款成功。", "The refund was confirmed a second time, and the provider confirmed success."),
		"refund-confirmed-pending":   bilingualPortalNotice("退款二次确认已完成；渠道结果仍在确认中，请勿重复创建退款。", "The second confirmation completed, but the provider result is still pending. Do not create a duplicate refund."),
		"refund-confirmation-failed": bilingualPortalNotice("退款二次确认已完成，但支付渠道明确拒绝了请求；请查看失败代码。", "The second confirmation completed, but the provider rejected the refund. Review the failure code."),
		"reconciliation-created":     bilingualPortalNotice("对账任务已进入队列。", "The reconciliation job was queued."),
		"reconciliation-updated":     bilingualPortalNotice("对账差异状态已更新并写入审计。", "The reconciliation discrepancy was updated and audited."),
		"channel-created":            bilingualPortalNotice("支付渠道记录已创建。", "The payment-provider record was created."),
		"channel-version-created":    bilingualPortalNotice("新的支付配置版本已保存，Secret 引用不会回显。", "The new payment configuration version was saved. Secret references are not displayed."),
		"channel-activated":          bilingualPortalNotice("已验证的支付配置版本已启用。", "The verified payment configuration version was activated."),
		"channel-disabled":           bilingualPortalNotice("支付渠道已停用，新订单不会再使用该渠道。", "The payment provider was disabled and will not be used for new orders."),
	}
	return labels[value]
}

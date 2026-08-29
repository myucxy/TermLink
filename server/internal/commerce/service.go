// Package commerce orchestrates provider-neutral Web orders, payments,
// verified notifications, fulfillment, and refunds. Browser return URLs are
// intentionally not part of this package: only an authenticated provider
// query or verified webhook may mark an order paid.
package commerce

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	paymentInstructionSchema = 1
	maxWebhookBodyBytes      = 1 << 20
	refundApprovalLifetime   = 15 * time.Minute
)

type Config struct {
	Repository  Repository
	Gateways    GatewayResolver
	SecretRoot  string
	SecretLimit int64
	HTTPClient  payment.HTTPClient
	Clock       func() time.Time
	NewID       func() uuid.UUID
	DigestKey   []byte
}

type Service struct {
	repository Repository
	gateways   GatewayResolver
	now        func() time.Time
	newID      func() uuid.UUID
	digestKey  []byte
}

type PaymentCertificateHealth struct {
	Status               string
	ActiveCertificates   int
	RetiringCertificates int
	RemainingDays        int
	EarliestExpiryAt     time.Time
	LastRefreshAt        time.Time
}

func NewService(config Config) (*Service, error) {
	if config.Repository == nil || len(config.DigestKey) < 32 {
		return nil, fmt.Errorf("%w: repository and a 32-byte digest key are required", ErrInvalidChannelConfig)
	}
	if config.Clock == nil {
		config.Clock = time.Now
	}
	if config.NewID == nil {
		config.NewID = uuid.New
	}
	if config.Gateways == nil {
		resolver, err := NewChannelGatewayResolver(GatewayResolverConfig{
			Repository: config.Repository, SecretRoot: config.SecretRoot, SecretLimit: config.SecretLimit,
			HTTPClient: config.HTTPClient, Clock: config.Clock,
		})
		if err != nil {
			return nil, err
		}
		config.Gateways = resolver
	}
	return &Service{
		repository: config.Repository, gateways: config.Gateways, now: config.Clock, newID: config.NewID,
		digestKey: append([]byte(nil), config.DigestKey...),
	}, nil
}

type CreateOrderRequest struct {
	AccountID      uuid.UUID
	PlanKey        string
	Provider       string
	Scene          string
	IdempotencyKey string
	ExpiresAt      time.Time
}

func (s *Service) CreateOrder(ctx context.Context, request CreateOrderRequest) (store.Order, error) {
	request.PlanKey = strings.TrimSpace(request.PlanKey)
	request.Provider = strings.ToUpper(strings.TrimSpace(request.Provider))
	request.Scene = strings.ToUpper(strings.TrimSpace(request.Scene))
	request.IdempotencyKey = strings.TrimSpace(request.IdempotencyKey)
	if request.AccountID == uuid.Nil || request.PlanKey == "" || len(request.PlanKey) > 128 || !supportedProviderScene(request.Provider, request.Scene) || len(request.IdempotencyKey) < 8 || len(request.IdempotencyKey) > 128 || request.ExpiresAt.IsZero() {
		return store.Order{}, ErrInvalidRequest
	}
	return s.repository.CreateOrder(ctx, request.AccountID, store.CreateOrderInput{
		PlanKey: request.PlanKey, Provider: request.Provider, Scene: request.Scene,
		IdempotencyKey: request.IdempotencyKey, ExpiresAt: request.ExpiresAt.UTC(),
	})
}

// ValidatePaymentChannelVersion validates a DRAFT configuration locally. It
// resolves referenced Docker secrets and constructs the provider gateway, but
// deliberately performs no network request and cannot create a charge.
func (s *Service) ValidatePaymentChannelVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error {
	validator, ok := s.gateways.(GatewayValidator)
	if !ok {
		return ErrGatewayValidationUnavailable
	}
	return validator.ValidateVersion(ctx, channel, version)
}

func (s *Service) ProbePaymentChannelVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error {
	prober, ok := s.gateways.(GatewayProber)
	if !ok {
		return ErrGatewayValidationUnavailable
	}
	return prober.ProbeVersion(ctx, channel, version)
}

// CachedPaymentCertificateHealth returns the certificate-maintenance snapshot
// already held by the active gateway. It performs no network or Secret I/O.
func (s *Service) CachedPaymentCertificateHealth(provider, scene string) (PaymentCertificateHealth, bool) {
	source, ok := s.gateways.(cachedCertificateHealthSource)
	if !ok {
		return PaymentCertificateHealth{}, false
	}
	health, ok := source.CachedPaymentCertificateHealth(provider, scene)
	if !ok {
		return PaymentCertificateHealth{}, false
	}
	return PaymentCertificateHealth{
		Status: health.Status, ActiveCertificates: health.ActiveCertificates,
		RetiringCertificates: health.RetiringCertificates, RemainingDays: health.RemainingDays,
		EarliestExpiryAt: health.EarliestExpiryAt, LastRefreshAt: health.LastRefreshAt,
	}, true
}

// PaymentInstruction is the only initiation data safe to persist and return
// to a browser. Provider tokens, signatures, keys, and raw responses have no
// representation in this type.
type PaymentInstruction struct {
	Provider        payment.Provider        `json:"provider"`
	MerchantOrderID string                  `json:"merchant_order_id"`
	Kind            payment.InstructionKind `json:"kind"`
	QRCodeURL       string                  `json:"qr_code_url,omitempty"`
	RedirectURL     string                  `json:"redirect_url,omitempty"`
	ExpiresAt       time.Time               `json:"expires_at,omitempty"`
}

type storedPaymentInstruction struct {
	SchemaVersion int `json:"schema_version"`
	PaymentInstruction
}

func (s *Service) StartPayment(ctx context.Context, accountID, orderID uuid.UUID) (PaymentInstruction, error) {
	if accountID == uuid.Nil || orderID == uuid.Nil {
		return PaymentInstruction{}, ErrInvalidRequest
	}
	order, err := s.repository.GetOrder(ctx, accountID, orderID)
	if err != nil {
		return PaymentInstruction{}, err
	}
	if order.Status == "PENDING_PAYMENT" {
		return s.loadPendingInstruction(ctx, order)
	}
	if order.Status != "CREATED" || !order.ExpiresAt.After(s.now()) {
		return PaymentInstruction{}, ErrPaymentStateNotProgressable
	}
	gateway, err := s.gateways.Resolve(ctx, order.Provider, order.PaymentScene)
	if err != nil {
		return PaymentInstruction{}, err
	}
	providerInstruction, err := gateway.CreatePayment(ctx, payment.OrderRequest{
		MerchantOrderID: order.OrderNo,
		Subject:         paymentSubject(order.PlanKey),
		AmountMinor:     order.AmountMinor,
		Currency:        order.Currency,
		Flow:            payment.PaymentFlow(order.PaymentScene),
		ExpiresAt:       order.ExpiresAt,
	})
	if err != nil {
		return PaymentInstruction{}, err
	}
	instruction := PaymentInstruction{
		Provider: providerInstruction.Provider, MerchantOrderID: providerInstruction.MerchantOrderID,
		Kind: providerInstruction.Kind, QRCodeURL: providerInstruction.QRCodeURL,
		RedirectURL: providerInstruction.RedirectURL, ExpiresAt: providerInstruction.ExpiresAt,
	}
	if instruction.Provider != payment.Provider(order.Provider) || instruction.MerchantOrderID != order.OrderNo || !validInstruction(instruction) {
		return PaymentInstruction{}, ErrPaymentResultMismatch
	}
	payload, err := json.Marshal(storedPaymentInstruction{SchemaVersion: paymentInstructionSchema, PaymentInstruction: instruction})
	if err != nil {
		return PaymentInstruction{}, ErrPaymentResultMismatch
	}
	if _, err = s.repository.PersistPaymentInitiation(ctx, accountID, order.ID, s.newID(), payload); err != nil {
		// Provider initiation is idempotent by merchant order number. If another
		// request won the persistence race, return that canonical instruction.
		if existing, loadErr := s.loadPendingInstruction(ctx, order); loadErr == nil {
			return existing, nil
		}
		return PaymentInstruction{}, err
	}
	return instruction, nil
}

func (s *Service) loadPendingInstruction(ctx context.Context, order store.Order) (PaymentInstruction, error) {
	transaction, err := s.repository.GetPendingPaymentTransaction(ctx, order.ID)
	if err != nil {
		return PaymentInstruction{}, ErrPaymentTransactionMissing
	}
	var stored storedPaymentInstruction
	if err := decodeStrictObject(transaction.PaymentPayload, 64<<10, &stored); err != nil || stored.SchemaVersion != paymentInstructionSchema || stored.Provider != payment.Provider(order.Provider) || stored.MerchantOrderID != order.OrderNo || !validInstruction(stored.PaymentInstruction) {
		return PaymentInstruction{}, ErrPaymentResultMismatch
	}
	return stored.PaymentInstruction, nil
}

type PaymentOutcome struct {
	Order          store.Order
	ProviderStatus payment.PaymentStatus
	Fulfilled      bool
}

func (s *Service) QueryAndFulfill(ctx context.Context, accountID, orderID uuid.UUID) (PaymentOutcome, error) {
	if accountID == uuid.Nil || orderID == uuid.Nil {
		return PaymentOutcome{}, ErrInvalidRequest
	}
	order, err := s.repository.GetOrder(ctx, accountID, orderID)
	if err != nil {
		return PaymentOutcome{}, err
	}
	switch order.Status {
	case "FULFILLED":
		return PaymentOutcome{Order: order, ProviderStatus: payment.StatusSucceeded, Fulfilled: true}, nil
	case "PAID", "FULFILLING":
		fulfilled, _, err := s.repository.FulfillOrder(ctx, order.ID)
		return PaymentOutcome{Order: fulfilled, ProviderStatus: payment.StatusSucceeded, Fulfilled: err == nil}, err
	case "PENDING_PAYMENT":
		// Only an initiated, pending payment may be queried. CREATED has no
		// persisted transaction and terminal/refund states must never be revived.
	default:
		return PaymentOutcome{Order: order}, ErrPaymentStateNotProgressable
	}
	gateway, err := s.gateways.Resolve(ctx, order.Provider, order.PaymentScene)
	if err != nil {
		return PaymentOutcome{}, err
	}
	result, err := gateway.QueryPayment(ctx, order.OrderNo)
	if err != nil {
		return PaymentOutcome{}, err
	}
	if err := validatePaymentResult(order, result.Provider, result.MerchantOrderID, result.AmountMinor, result.Currency); err != nil {
		return PaymentOutcome{}, err
	}
	outcome := PaymentOutcome{Order: order, ProviderStatus: result.Status}
	if result.Status != payment.StatusSucceeded {
		return outcome, nil
	}
	settled, fulfilled, err := s.settlePayment(ctx, order, result.ProviderTransactionID, result.PaidAt)
	outcome.Order, outcome.Fulfilled = settled, fulfilled
	return outcome, err
}

// RecoverFulfillment advances only an already-authoritative PAID or
// FULFILLING order. It never calls a payment provider and is safe to retry.
func (s *Service) RecoverFulfillment(ctx context.Context, orderID uuid.UUID) (store.Order, error) {
	if orderID == uuid.Nil {
		return store.Order{}, ErrInvalidRequest
	}
	order, err := s.repository.GetOrderByID(ctx, orderID)
	if err != nil {
		return store.Order{}, err
	}
	switch order.Status {
	case "FULFILLED":
		return order, nil
	case "PAID", "FULFILLING":
		fulfilled, _, err := s.repository.FulfillOrder(ctx, order.ID)
		return fulfilled, err
	default:
		return order, ErrPaymentStateNotProgressable
	}
}

type WebhookAcknowledgement struct {
	Provider    payment.Provider
	Accepted    bool
	StatusCode  int
	ContentType string
	Body        []byte
}

func (s *Service) HandleWebhook(ctx context.Context, providerName, scene string, headers http.Header, body []byte) (WebhookAcknowledgement, error) {
	providerName = strings.ToUpper(strings.TrimSpace(providerName))
	scene = strings.ToUpper(strings.TrimSpace(scene))
	provider := payment.Provider(providerName)
	failure := webhookAcknowledgement(provider, false)
	if !supportedProviderScene(providerName, scene) || len(body) == 0 {
		return failure, ErrInvalidRequest
	}
	if len(body) > maxWebhookBodyBytes {
		return failure, payment.ErrPayloadTooLarge
	}
	gateway, err := s.gateways.Resolve(ctx, providerName, scene)
	if err != nil {
		return failure, err
	}
	event, err := gateway.VerifyWebhook(ctx, headers, body)
	if err != nil {
		return failure, err
	}
	if event.Provider != provider || event.EventID == "" || len(event.EventID) > 512 || event.MerchantOrderID == "" {
		return failure, ErrPaymentResultMismatch
	}
	notificationDigest := s.digest("payment-notification", providerName+"\x00"+event.EventID)
	payloadDigest := sha256.Sum256(body)
	notification, created, err := s.repository.RecordPaymentNotification(ctx, store.PaymentNotificationInput{
		ID: s.newID(), Provider: providerName, NotificationIDHash: notificationDigest, PayloadDigest: payloadDigest,
		OrderNo: event.MerchantOrderID, SignatureValid: true, ProcessingState: "RECEIVED",
		// Raw provider bodies are intentionally not persisted.
		PayloadCiphertext: nil,
	})
	if err != nil {
		return failure, err
	}
	if !created {
		if notification.Provider != providerName || notification.OrderNo != event.MerchantOrderID || len(notification.PayloadDigest) != sha256.Size || subtle.ConstantTimeCompare(notification.PayloadDigest, payloadDigest[:]) != 1 {
			return failure, ErrPaymentResultMismatch
		}
		switch notification.ProcessingState {
		case "PROCESSED":
			return webhookAcknowledgement(provider, true), nil
		case "REJECTED":
			return failure, ErrPaymentStateNotProgressable
		case "RECEIVED", "RETRY":
			// State transitions below are idempotent. Reprocessing RECEIVED also
			// recovers a crash after the replay fence but before completion.
		default:
			return failure, ErrPaymentStateNotProgressable
		}
	}
	if err := s.processWebhookEvent(ctx, event); err != nil {
		_ = s.repository.CompletePaymentNotification(ctx, notification.ID, false, webhookFailureCode(err))
		return failure, err
	}
	if err := s.repository.CompletePaymentNotification(ctx, notification.ID, true, ""); err != nil {
		return failure, err
	}
	return webhookAcknowledgement(provider, true), nil
}

func (s *Service) processWebhookEvent(ctx context.Context, event payment.WebhookResult) error {
	order, err := s.repository.GetOrderByNumber(ctx, event.MerchantOrderID)
	if err != nil {
		return err
	}
	switch event.EventType {
	case payment.WebhookPayment:
		if err := validatePaymentResult(order, event.Provider, event.MerchantOrderID, event.AmountMinor, event.Currency); err != nil {
			return err
		}
		if event.Status != payment.StatusSucceeded {
			if event.Status == payment.StatusPending || event.Status == payment.StatusClosed {
				return nil
			}
			return ErrPaymentStateNotProgressable
		}
		_, _, err = s.settlePayment(ctx, order, event.ProviderTransactionID, event.OccurredAt)
		return err
	case payment.WebhookRefund:
		if event.Provider != payment.Provider(order.Provider) || event.MerchantOrderID != order.OrderNo || event.Currency != order.Currency {
			return ErrPaymentResultMismatch
		}
		refund, err := s.repository.GetRefundByNumber(ctx, event.MerchantRefundID)
		if err != nil {
			return err
		}
		if refund.OrderID != order.ID || refund.RefundNo != event.MerchantRefundID || refund.AmountMinor != event.AmountMinor {
			return ErrPaymentResultMismatch
		}
		switch event.Status {
		case payment.StatusSucceeded, payment.StatusRefunded:
			_, err = s.repository.CompleteRefund(ctx, refund.ID, s.digest("provider-refund", event.ProviderRefundID), true, "")
			return err
		case payment.StatusRefunding, payment.StatusPending:
			return nil
		case payment.StatusFailed, payment.StatusClosed:
			_, err = s.repository.CompleteRefund(ctx, refund.ID, [32]byte{}, false, "PROVIDER_REFUND_FAILED")
			return err
		default:
			return ErrPaymentStateNotProgressable
		}
	default:
		return ErrPaymentStateNotProgressable
	}
}

func (s *Service) settlePayment(ctx context.Context, order store.Order, providerTransactionID string, paidAt time.Time) (store.Order, bool, error) {
	if providerTransactionID == "" || len(providerTransactionID) > 512 {
		return store.Order{}, false, ErrPaymentResultMismatch
	}
	switch order.Status {
	case "FULFILLED":
		return order, true, nil
	case "PAID", "FULFILLING":
		fulfilled, _, err := s.repository.FulfillOrder(ctx, order.ID)
		return fulfilled, err == nil, err
	case "CREATED", "PENDING_PAYMENT":
		transaction, err := s.repository.GetPendingPaymentTransaction(ctx, order.ID)
		if err != nil {
			return store.Order{}, false, ErrPaymentTransactionMissing
		}
		if paidAt.IsZero() {
			paidAt = s.now()
		}
		paid, err := s.repository.MarkOrderPaid(ctx, order.OrderNo, order.Provider, transaction.ID, s.digest("provider-transaction", providerTransactionID), order.AmountMinor, order.Currency, paidAt)
		if err != nil {
			return store.Order{}, false, err
		}
		fulfilled, _, err := s.repository.FulfillOrder(ctx, paid.ID)
		return fulfilled, err == nil, err
	default:
		// In particular, CLOSED, REFUNDING, and REFUNDED orders must never be
		// restored by a delayed notification.
		return order, false, ErrPaymentStateNotProgressable
	}
}

type CreateRefundRequest struct {
	ActorID     uuid.UUID
	OrderID     uuid.UUID
	AmountMinor int64
	Reason      string
	TraceID     string
}

type RefundOutcome struct {
	Refund           store.Refund
	ProviderStatus   payment.PaymentStatus
	ApprovalRequired bool
}

type ConfirmRefundRequest struct {
	ActorID         uuid.UUID
	RefundID        uuid.UUID
	ExpectedVersion int64
	TraceID         string
}

// QueryAndCompleteRefund reconciles one existing refund from the provider's
// authoritative query response. Pending results remain unchanged; only a
// strictly matching terminal result reaches CompleteRefund.
func (s *Service) QueryAndCompleteRefund(ctx context.Context, refundID uuid.UUID) (RefundOutcome, error) {
	if refundID == uuid.Nil {
		return RefundOutcome{}, ErrInvalidRequest
	}
	refund, err := s.repository.GetRefundByID(ctx, refundID)
	if err != nil {
		return RefundOutcome{}, err
	}
	switch refund.Status {
	case "SUCCEEDED":
		return RefundOutcome{Refund: refund, ProviderStatus: payment.StatusRefunded}, nil
	case "CREATED", "PENDING":
	case "FAILED":
		return RefundOutcome{Refund: refund, ProviderStatus: payment.StatusFailed}, nil
	default:
		return RefundOutcome{Refund: refund}, ErrPaymentStateNotProgressable
	}
	order, err := s.repository.GetOrderByID(ctx, refund.OrderID)
	if err != nil {
		return RefundOutcome{}, err
	}
	if refund.OrderID != order.ID || refund.RefundNo == "" || refund.AmountMinor <= 0 || refund.AmountMinor > order.AmountMinor ||
		(order.Status != "REFUNDING" && order.Status != "FULFILLED" && order.Status != "REFUNDED") {
		return RefundOutcome{Refund: refund}, ErrPaymentResultMismatch
	}
	gateway, err := s.gateways.Resolve(ctx, order.Provider, order.PaymentScene)
	if err != nil {
		return RefundOutcome{Refund: refund}, err
	}
	result, err := gateway.QueryRefund(ctx, payment.RefundRequest{
		MerchantOrderID: order.OrderNo, MerchantRefundID: refund.RefundNo,
		AmountMinor: refund.AmountMinor, TotalAmountMinor: order.AmountMinor,
		Currency: order.Currency,
	})
	if err != nil {
		return RefundOutcome{Refund: refund}, err
	}
	outcome := RefundOutcome{Refund: refund, ProviderStatus: result.Status}
	if result.Provider != payment.Provider(order.Provider) || result.MerchantOrderID != order.OrderNo ||
		result.MerchantRefundID != refund.RefundNo || result.AmountMinor != refund.AmountMinor || result.Currency != order.Currency {
		return outcome, ErrPaymentResultMismatch
	}
	switch result.Status {
	case payment.StatusPending, payment.StatusRefunding:
		return outcome, nil
	case payment.StatusSucceeded, payment.StatusRefunded:
		providerReference := result.ProviderRefundID
		if providerReference == "" {
			providerReference = refund.RefundNo
		}
		outcome.Refund, err = s.repository.CompleteRefund(ctx, refund.ID, s.digest("provider-refund", providerReference), true, "")
		return outcome, err
	case payment.StatusFailed, payment.StatusClosed:
		outcome.Refund, err = s.repository.CompleteRefund(ctx, refund.ID, [32]byte{}, false, "PROVIDER_REFUND_FAILED")
		return outcome, err
	default:
		return outcome, ErrPaymentStateNotProgressable
	}
}

func (s *Service) CreateRefund(ctx context.Context, request CreateRefundRequest) (RefundOutcome, error) {
	request.Reason = strings.TrimSpace(request.Reason)
	request.TraceID = strings.TrimSpace(request.TraceID)
	if request.ActorID == uuid.Nil || request.OrderID == uuid.Nil || request.AmountMinor <= 0 || len(request.Reason) < 1 || len(request.Reason) > 500 || len(request.TraceID) > 256 {
		return RefundOutcome{}, ErrInvalidRequest
	}
	refund, err := s.repository.CreateRefund(ctx, request.ActorID, request.OrderID, request.AmountMinor, request.Reason, request.TraceID, s.now().Add(refundApprovalLifetime))
	if err != nil {
		return RefundOutcome{}, err
	}
	return RefundOutcome{Refund: refund, ApprovalRequired: true}, nil
}

// ConfirmRefund consumes the approval version before resolving or calling the
// provider. Store replays return transitioned=false, which deliberately does
// not issue another money-moving request; recovery/query uses the stable
// merchant refund number if the first provider result was ambiguous.
func (s *Service) ConfirmRefund(ctx context.Context, request ConfirmRefundRequest) (RefundOutcome, error) {
	request.TraceID = strings.TrimSpace(request.TraceID)
	if request.ActorID == uuid.Nil || request.RefundID == uuid.Nil || request.ExpectedVersion < 1 || len(request.TraceID) > 256 {
		return RefundOutcome{}, ErrInvalidRequest
	}
	refund, transitioned, err := s.repository.ApproveRefund(ctx, request.ActorID, request.RefundID, request.ExpectedVersion, request.TraceID)
	if err != nil {
		return RefundOutcome{}, err
	}
	if !transitioned {
		outcome := RefundOutcome{Refund: refund}
		switch refund.Status {
		case "SUCCEEDED":
			outcome.ProviderStatus = payment.StatusRefunded
		case "FAILED":
			outcome.ProviderStatus = payment.StatusFailed
		}
		return outcome, nil
	}
	order, err := s.repository.GetOrderByID(ctx, refund.OrderID)
	if err != nil {
		return RefundOutcome{Refund: refund}, err
	}
	gateway, err := s.gateways.Resolve(ctx, order.Provider, order.PaymentScene)
	if err != nil {
		return RefundOutcome{Refund: refund}, err
	}
	return s.submitRefund(ctx, gateway, order, refund)
}

func (s *Service) submitRefund(ctx context.Context, gateway payment.Gateway, order store.Order, refund store.Refund) (RefundOutcome, error) {
	result, providerErr := gateway.Refund(ctx, payment.RefundRequest{
		MerchantOrderID: order.OrderNo, MerchantRefundID: refund.RefundNo,
		AmountMinor: refund.AmountMinor, TotalAmountMinor: order.AmountMinor,
		Currency: order.Currency, Reason: refund.Reason,
	})
	if providerErr != nil {
		// A transport or malformed-response error is ambiguous: the provider
		// may already have accepted the refund. Keep the same refund number in
		// CREATED so a later verified webhook can complete it and a second
		// refund is not issued. Only an authoritative rejection is terminal.
		if errors.Is(providerErr, payment.ErrProviderRejected) || errors.Is(providerErr, payment.ErrInvalidRequest) || errors.Is(providerErr, payment.ErrUnsupported) {
			failed, completeErr := s.repository.CompleteRefund(ctx, refund.ID, [32]byte{}, false, "PROVIDER_REQUEST_REJECTED")
			if completeErr == nil {
				refund = failed
			}
		}
		return RefundOutcome{Refund: refund}, providerErr
	}
	if result.Provider != payment.Provider(order.Provider) || result.MerchantOrderID != order.OrderNo || result.MerchantRefundID != refund.RefundNo || result.AmountMinor != refund.AmountMinor || result.Currency != order.Currency {
		return RefundOutcome{Refund: refund, ProviderStatus: result.Status}, ErrPaymentResultMismatch
	}
	outcome := RefundOutcome{Refund: refund, ProviderStatus: result.Status}
	var err error
	switch result.Status {
	case payment.StatusSucceeded, payment.StatusRefunded:
		providerReference := result.ProviderRefundID
		if providerReference == "" {
			providerReference = refund.RefundNo
		}
		outcome.Refund, err = s.repository.CompleteRefund(ctx, refund.ID, s.digest("provider-refund", providerReference), true, "")
		return outcome, err
	case payment.StatusPending, payment.StatusRefunding:
		return outcome, nil
	case payment.StatusFailed, payment.StatusClosed:
		outcome.Refund, err = s.repository.CompleteRefund(ctx, refund.ID, [32]byte{}, false, "PROVIDER_REFUND_FAILED")
		return outcome, err
	default:
		_, _ = s.repository.CompleteRefund(ctx, refund.ID, [32]byte{}, false, "PROVIDER_RESULT_UNKNOWN")
		return outcome, ErrPaymentStateNotProgressable
	}
}

func validatePaymentResult(order store.Order, provider payment.Provider, orderNo string, amount int64, currency string) error {
	if provider != payment.Provider(order.Provider) || orderNo != order.OrderNo || amount != order.AmountMinor || currency != order.Currency {
		return ErrPaymentResultMismatch
	}
	return nil
}

func validInstruction(instruction PaymentInstruction) bool {
	if instruction.MerchantOrderID == "" || len(instruction.MerchantOrderID) > 64 || !supportedProvider(string(instruction.Provider)) {
		return false
	}
	switch instruction.Kind {
	case payment.InstructionQRCode:
		return instruction.RedirectURL == "" && validProviderURL(instruction.QRCodeURL, true)
	case payment.InstructionRedirect:
		return instruction.QRCodeURL == "" && validProviderURL(instruction.RedirectURL, false)
	default:
		return false
	}
}

func validProviderURL(value string, qrCode bool) bool {
	if len(value) < 1 || len(value) > 4096 {
		return false
	}
	for _, char := range value {
		if unicode.IsControl(char) {
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
	return qrCode && parsed.Scheme == "weixin" && (parsed.Opaque != "" || parsed.Host != "")
}

func paymentSubject(planKey string) string {
	planKey = strings.TrimSpace(planKey)
	if len([]rune(planKey)) > 100 {
		planKey = string([]rune(planKey)[:100])
	}
	return "TermLink " + planKey
}

func (s *Service) digest(domain, value string) [32]byte {
	hash := hmac.New(sha256.New, s.digestKey)
	_, _ = hash.Write([]byte("termlink-commerce-v1\x00" + domain + "\x00" + value))
	var digest [32]byte
	copy(digest[:], hash.Sum(nil))
	return digest
}

func webhookAcknowledgement(provider payment.Provider, accepted bool) WebhookAcknowledgement {
	ack := WebhookAcknowledgement{Provider: provider, Accepted: accepted}
	switch provider {
	case payment.ProviderAlipay:
		ack.StatusCode, ack.ContentType = http.StatusOK, "text/plain; charset=utf-8"
		if accepted {
			ack.Body = []byte("success")
		} else {
			ack.Body = []byte("failure")
		}
	case payment.ProviderWeChatPay:
		ack.ContentType = "application/json; charset=utf-8"
		if accepted {
			ack.StatusCode, ack.Body = http.StatusOK, []byte(`{"code":"SUCCESS","message":"成功"}`)
		} else {
			ack.StatusCode, ack.Body = http.StatusInternalServerError, []byte(`{"code":"FAIL","message":"processing failed"}`)
		}
	default:
		ack.StatusCode, ack.ContentType, ack.Body = http.StatusBadRequest, "text/plain; charset=utf-8", []byte("failure")
	}
	return ack
}

func webhookFailureCode(err error) string {
	switch {
	case errors.Is(err, ErrPaymentResultMismatch):
		return "PAYMENT_RESULT_MISMATCH"
	case errors.Is(err, ErrPaymentTransactionMissing):
		return "PAYMENT_TRANSACTION_MISSING"
	case errors.Is(err, ErrPaymentStateNotProgressable):
		return "PAYMENT_STATE_REJECTED"
	default:
		return "PAYMENT_PROCESSING_FAILED"
	}
}

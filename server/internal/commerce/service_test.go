package commerce

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

var errFakeProvider = errors.New("fake provider transport failure")

type fakeGateway struct {
	provider         payment.Provider
	createResult     payment.PaymentInstruction
	queryResult      payment.QueryResult
	refundResult     payment.RefundResult
	webhookResult    payment.WebhookResult
	createErr        error
	queryErr         error
	refundErr        error
	webhookErr       error
	createCalls      int
	queryCalls       int
	refundCalls      int
	queryRefundCalls int
	webhookCalls     int
	queryFunc        func(context.Context, string) (payment.QueryResult, error)
	queryRefundFunc  func(context.Context, payment.RefundRequest) (payment.RefundResult, error)
}

func (g *fakeGateway) Provider() payment.Provider { return g.provider }
func (g *fakeGateway) CreatePayment(context.Context, payment.OrderRequest) (payment.PaymentInstruction, error) {
	g.createCalls++
	return g.createResult, g.createErr
}
func (g *fakeGateway) QueryPayment(ctx context.Context, orderNo string) (payment.QueryResult, error) {
	g.queryCalls++
	if g.queryFunc != nil {
		return g.queryFunc(ctx, orderNo)
	}
	return g.queryResult, g.queryErr
}
func (g *fakeGateway) HealthCheck(context.Context) error { return nil }
func (g *fakeGateway) Refund(context.Context, payment.RefundRequest) (payment.RefundResult, error) {
	g.refundCalls++
	return g.refundResult, g.refundErr
}
func (g *fakeGateway) QueryRefund(ctx context.Context, request payment.RefundRequest) (payment.RefundResult, error) {
	g.queryRefundCalls++
	if g.queryRefundFunc != nil {
		return g.queryRefundFunc(ctx, request)
	}
	return g.refundResult, g.refundErr
}

func (g *fakeGateway) VerifyWebhook(context.Context, http.Header, []byte) (payment.WebhookResult, error) {
	g.webhookCalls++
	return g.webhookResult, g.webhookErr
}

type fakeGatewayResolver struct {
	gateway payment.Gateway
	err     error
}

func (r fakeGatewayResolver) Resolve(context.Context, string, string) (payment.Gateway, error) {
	return r.gateway, r.err
}

type fakeRepository struct {
	channel             store.PaymentChannel
	version             store.PaymentChannelVersion
	channelErr          error
	order               store.Order
	transaction         store.PaymentTransaction
	refund              store.Refund
	notifications       map[[32]byte]store.PaymentNotification
	markPaidCalls       int
	fulfillCalls        int
	persistCalls        int
	completeRefundCalls int
	approveRefundCalls  int
	approveRefundErr    error
	persistedRawBytes   int
	fulfillErrOnce      error
}

func (r *fakeRepository) GetActivePaymentConfiguration(context.Context, string, string) (store.PaymentChannel, store.PaymentChannelVersion, error) {
	return r.channel, r.version, r.channelErr
}
func (r *fakeRepository) CreateOrder(_ context.Context, accountID uuid.UUID, input store.CreateOrderInput) (store.Order, error) {
	r.order.AccountID = accountID
	r.order.PlanKey = input.PlanKey
	r.order.Provider = input.Provider
	r.order.PaymentScene = input.Scene
	return r.order, nil
}
func (r *fakeRepository) GetOrder(_ context.Context, accountID, orderID uuid.UUID) (store.Order, error) {
	if r.order.ID != orderID || r.order.AccountID != accountID {
		return store.Order{}, store.ErrNotFound
	}
	return r.order, nil
}
func (r *fakeRepository) GetOrderByID(_ context.Context, orderID uuid.UUID) (store.Order, error) {
	if r.order.ID != orderID {
		return store.Order{}, store.ErrNotFound
	}
	return r.order, nil
}
func (r *fakeRepository) GetOrderByNumber(_ context.Context, orderNo string) (store.Order, error) {
	if r.order.OrderNo != orderNo {
		return store.Order{}, store.ErrNotFound
	}
	return r.order, nil
}
func (r *fakeRepository) GetPendingPaymentTransaction(_ context.Context, orderID uuid.UUID) (store.PaymentTransaction, error) {
	if r.transaction.OrderID != orderID || r.transaction.ID == uuid.Nil {
		return store.PaymentTransaction{}, store.ErrNotFound
	}
	return r.transaction, nil
}
func (r *fakeRepository) PersistPaymentInitiation(_ context.Context, _, orderID, transactionID uuid.UUID, payload json.RawMessage) (store.PaymentTransaction, error) {
	r.persistCalls++
	r.transaction = store.PaymentTransaction{ID: transactionID, OrderID: orderID, Provider: r.order.Provider, Status: "PENDING", AmountMinor: r.order.AmountMinor, Currency: r.order.Currency, PaymentPayload: append(json.RawMessage(nil), payload...)}
	r.order.Status = "PENDING_PAYMENT"
	return r.transaction, nil
}
func (r *fakeRepository) MarkOrderPaid(_ context.Context, orderNo, provider string, transactionID uuid.UUID, _ [32]byte, amount int64, currency string, paidAt time.Time) (store.Order, error) {
	if r.order.OrderNo != orderNo || r.order.Provider != provider || r.transaction.ID != transactionID || r.order.AmountMinor != amount || r.order.Currency != currency {
		return store.Order{}, store.ErrOrderAmountMismatch
	}
	r.markPaidCalls++
	r.order.Status = "PAID"
	r.order.PaidAt = &paidAt
	return r.order, nil
}
func (r *fakeRepository) FulfillOrder(_ context.Context, orderID uuid.UUID) (store.Order, store.Entitlement, error) {
	if r.order.ID != orderID {
		return store.Order{}, store.Entitlement{}, store.ErrNotFound
	}
	r.fulfillCalls++
	if r.fulfillErrOnce != nil {
		err := r.fulfillErrOnce
		r.fulfillErrOnce = nil
		return store.Order{}, store.Entitlement{}, err
	}
	r.order.Status = "FULFILLED"
	return r.order, store.Entitlement{}, nil
}
func (r *fakeRepository) RecordPaymentNotification(_ context.Context, input store.PaymentNotificationInput) (store.PaymentNotification, bool, error) {
	r.persistedRawBytes += len(input.PayloadCiphertext)
	if r.notifications == nil {
		r.notifications = make(map[[32]byte]store.PaymentNotification)
	}
	if existing, ok := r.notifications[input.NotificationIDHash]; ok {
		return existing, false, nil
	}
	notification := store.PaymentNotification{ID: input.ID, Provider: input.Provider, NotificationIDHash: input.NotificationIDHash[:], OrderNo: input.OrderNo, SignatureValid: input.SignatureValid, ProcessingState: input.ProcessingState, PayloadDigest: input.PayloadDigest[:]}
	r.notifications[input.NotificationIDHash] = notification
	return notification, true, nil
}
func (r *fakeRepository) CompletePaymentNotification(_ context.Context, notificationID uuid.UUID, success bool, failureCode string) error {
	for digest, notification := range r.notifications {
		if notification.ID == notificationID {
			if success {
				notification.ProcessingState = "PROCESSED"
			} else {
				notification.ProcessingState = "RETRY"
				notification.FailureCode = failureCode
			}
			r.notifications[digest] = notification
			return nil
		}
	}
	return store.ErrNotFound
}
func (r *fakeRepository) CreateRefund(_ context.Context, actorID, orderID uuid.UUID, amount int64, reason, _ string, approvalExpiresAt time.Time) (store.Refund, error) {
	r.refund = store.Refund{ID: uuid.New(), OrderID: orderID, TransactionID: r.transaction.ID, RequestedBy: actorID, RefundNo: "RF123456789", AmountMinor: amount, Reason: reason, Status: "APPROVAL_PENDING", Version: 1, ApprovalExpiresAt: &approvalExpiresAt}
	return r.refund, nil
}
func (r *fakeRepository) ApproveRefund(_ context.Context, actorID, refundID uuid.UUID, expectedVersion int64, _ string) (store.Refund, bool, error) {
	if r.refund.ID != refundID {
		return store.Refund{}, false, store.ErrNotFound
	}
	if r.approveRefundErr != nil {
		return store.Refund{}, false, r.approveRefundErr
	}
	r.approveRefundCalls++
	if r.refund.Status != "APPROVAL_PENDING" {
		if r.refund.Version >= expectedVersion+1 && r.refund.ApprovedBy != nil {
			return r.refund, false, nil
		}
		return store.Refund{}, false, store.ErrRefundVersionConflict
	}
	if r.refund.Version != expectedVersion {
		return store.Refund{}, false, store.ErrRefundVersionConflict
	}
	now := time.Now().UTC()
	r.refund.Status = "CREATED"
	r.refund.Version++
	r.refund.ApprovedBy = &actorID
	r.refund.ApprovedAt = &now
	r.order.Status = "REFUNDING"
	return r.refund, true, nil
}
func (r *fakeRepository) GetRefundByNumber(_ context.Context, refundNo string) (store.Refund, error) {
	if r.refund.RefundNo != refundNo {
		return store.Refund{}, store.ErrNotFound
	}
	return r.refund, nil
}
func (r *fakeRepository) GetRefundByID(_ context.Context, refundID uuid.UUID) (store.Refund, error) {
	if r.refund.ID != refundID {
		return store.Refund{}, store.ErrNotFound
	}
	return r.refund, nil
}
func (r *fakeRepository) CompleteRefund(_ context.Context, refundID uuid.UUID, providerRefHash [32]byte, success bool, _ string) (store.Refund, error) {
	if r.refund.ID != refundID {
		return store.Refund{}, store.ErrNotFound
	}
	r.completeRefundCalls++
	if success {
		r.refund.Status = "SUCCEEDED"
		r.refund.Version++
		r.refund.ProviderRefHash = providerRefHash[:]
	} else {
		r.refund.Status = "FAILED"
		r.refund.Version++
	}
	return r.refund, nil
}

func testOrder(now time.Time, status string) store.Order {
	return store.Order{
		ID: uuid.New(), AccountID: uuid.New(), OrderNo: "TL123456789", PlanKey: "PRO_MONTHLY", CatalogVersion: 1,
		Provider: string(payment.ProviderAlipay), PaymentScene: string(payment.FlowNative), AmountMinor: 990, Currency: payment.CurrencyCNY,
		Status: status, ExpiresAt: now.Add(15 * time.Minute),
	}
}

func testService(t *testing.T, repository *fakeRepository, gateway *fakeGateway, now time.Time) *Service {
	t.Helper()
	service, err := NewService(Config{Repository: repository, Gateways: fakeGatewayResolver{gateway: gateway}, Clock: func() time.Time { return now }, DigestKey: []byte("01234567890123456789012345678901")})
	if err != nil {
		t.Fatal(err)
	}
	return service
}

func TestStartPaymentPersistsOnlySafeInstructionAndReusesIt(t *testing.T) {
	now := time.Date(2026, 8, 27, 1, 0, 0, 0, time.UTC)
	repository := &fakeRepository{order: testOrder(now, "CREATED")}
	gateway := &fakeGateway{provider: payment.ProviderAlipay}
	gateway.createResult = payment.PaymentInstruction{Provider: payment.ProviderAlipay, MerchantOrderID: repository.order.OrderNo, Kind: payment.InstructionQRCode, QRCodeURL: "https://qr.alipay.example/order", ProviderTransactionID: "must-not-be-persisted", ExpiresAt: repository.order.ExpiresAt}
	service := testService(t, repository, gateway, now)

	first, err := service.StartPayment(context.Background(), repository.order.AccountID, repository.order.ID)
	if err != nil {
		t.Fatal(err)
	}
	second, err := service.StartPayment(context.Background(), repository.order.AccountID, repository.order.ID)
	if err != nil {
		t.Fatal(err)
	}
	if first.QRCodeURL != second.QRCodeURL || gateway.createCalls != 1 || repository.persistCalls != 1 {
		t.Fatalf("initiation was not reused: first=%+v second=%+v calls=%d/%d", first, second, gateway.createCalls, repository.persistCalls)
	}
	if strings.Contains(string(repository.transaction.PaymentPayload), "must-not-be-persisted") {
		t.Fatal("provider transaction identifier leaked into persisted instruction")
	}
}

func TestQueryAndFulfillIsIdempotent(t *testing.T) {
	now := time.Date(2026, 8, 27, 1, 0, 0, 0, time.UTC)
	repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryResult: payment.QueryResult{Provider: payment.ProviderAlipay, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-1", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor, Currency: repository.order.Currency, PaidAt: now}}
	service := testService(t, repository, gateway, now)

	for range 2 {
		outcome, err := service.QueryAndFulfill(context.Background(), repository.order.AccountID, repository.order.ID)
		if err != nil || !outcome.Fulfilled {
			t.Fatalf("query outcome=%+v err=%v", outcome, err)
		}
	}
	if repository.markPaidCalls != 1 || repository.fulfillCalls != 1 {
		t.Fatalf("state advanced more than once: paid=%d fulfilled=%d", repository.markPaidCalls, repository.fulfillCalls)
	}
}

func TestQueryRejectsAmountMismatch(t *testing.T) {
	now := time.Now().UTC()
	repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryResult: payment.QueryResult{Provider: payment.ProviderAlipay, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-1", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor + 1, Currency: repository.order.Currency}}
	service := testService(t, repository, gateway, now)

	_, err := service.QueryAndFulfill(context.Background(), repository.order.AccountID, repository.order.ID)
	if !errors.Is(err, ErrPaymentResultMismatch) || repository.markPaidCalls != 0 || repository.fulfillCalls != 0 {
		t.Fatalf("amount mismatch advanced state: err=%v", err)
	}
}

func TestPaymentWebhookDuplicateAndOutOfOrder(t *testing.T) {
	now := time.Now().UTC()
	t.Run("duplicate", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID, Status: "PENDING"}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "EVENT-1", EventType: payment.WebhookPayment, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-1", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor, Currency: repository.order.Currency, OccurredAt: now}}
		service := testService(t, repository, gateway, now)
		for range 2 {
			ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified body"))
			if err != nil || !ack.Accepted || string(ack.Body) != "success" {
				t.Fatalf("ack=%+v err=%v", ack, err)
			}
		}
		if repository.markPaidCalls != 1 || repository.fulfillCalls != 1 {
			t.Fatalf("duplicate advanced state: paid=%d fulfill=%d", repository.markPaidCalls, repository.fulfillCalls)
		}
		if repository.persistedRawBytes != 0 {
			t.Fatal("raw webhook body was persisted")
		}
	})

	t.Run("refunded_order_is_not_restored", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "REFUNDED")}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "EVENT-2", EventType: payment.WebhookPayment, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-2", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor, Currency: repository.order.Currency, OccurredAt: now}}
		service := testService(t, repository, gateway, now)
		ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified body 2"))
		if !errors.Is(err, ErrPaymentStateNotProgressable) || ack.Accepted || repository.markPaidCalls != 0 || repository.fulfillCalls != 0 {
			t.Fatalf("out-of-order webhook was accepted: ack=%+v err=%v", ack, err)
		}
	})

	t.Run("amount_mismatch_is_rejected", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID, Status: "PENDING"}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "EVENT-3", EventType: payment.WebhookPayment, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-3", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor - 1, Currency: repository.order.Currency, OccurredAt: now}}
		service := testService(t, repository, gateway, now)
		ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified wrong amount"))
		if !errors.Is(err, ErrPaymentResultMismatch) || ack.Accepted || repository.markPaidCalls != 0 || repository.fulfillCalls != 0 {
			t.Fatalf("mismatched webhook was accepted: ack=%+v err=%v", ack, err)
		}
	})

	t.Run("signature_error_is_not_recorded", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookErr: payment.ErrInvalidSignature}
		service := testService(t, repository, gateway, now)
		ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("forged"))
		if !errors.Is(err, payment.ErrInvalidSignature) || ack.Accepted || ack.StatusCode != http.StatusOK || string(ack.Body) != "failure" || len(repository.notifications) != 0 {
			t.Fatalf("invalid signature was recorded: ack=%+v err=%v notifications=%d", ack, err, len(repository.notifications))
		}
	})

	t.Run("wechat_signature_error_requests_retry", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		gateway := &fakeGateway{provider: payment.ProviderWeChatPay, webhookErr: payment.ErrInvalidSignature}
		service := testService(t, repository, gateway, now)
		ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderWeChatPay), string(payment.FlowNative), nil, []byte("forged"))
		if !errors.Is(err, payment.ErrInvalidSignature) || ack.Accepted || ack.StatusCode != http.StatusInternalServerError || string(ack.Body) != `{"code":"FAIL","message":"processing failed"}` || len(repository.notifications) != 0 {
			t.Fatalf("invalid wechat signature ack=%+v err=%v notifications=%d", ack, err, len(repository.notifications))
		}
	})

	t.Run("retry_resumes_transient_fulfillment_failure", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT"), fulfillErrOnce: errors.New("transient store error")}
		repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID, Status: "PENDING"}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "EVENT-RETRY", EventType: payment.WebhookPayment, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-RETRY", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor, Currency: repository.order.Currency, OccurredAt: now}}
		service := testService(t, repository, gateway, now)
		firstAck, firstErr := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified retry body"))
		if firstErr == nil || firstAck.Accepted {
			t.Fatalf("transient failure unexpectedly accepted: ack=%+v err=%v", firstAck, firstErr)
		}
		secondAck, secondErr := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified retry body"))
		if secondErr != nil || !secondAck.Accepted || repository.markPaidCalls != 1 || repository.fulfillCalls != 2 {
			t.Fatalf("provider retry did not recover: ack=%+v err=%v paid=%d fulfill=%d", secondAck, secondErr, repository.markPaidCalls, repository.fulfillCalls)
		}
	})

	t.Run("same_event_id_with_different_body_is_rejected", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		repository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: repository.order.ID, Status: "PENDING"}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "EVENT-COLLISION", EventType: payment.WebhookPayment, MerchantOrderID: repository.order.OrderNo, ProviderTransactionID: "TRADE-COLLISION", Status: payment.StatusSucceeded, AmountMinor: repository.order.AmountMinor, Currency: repository.order.Currency, OccurredAt: now}}
		service := testService(t, repository, gateway, now)
		if _, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("first signed body")); err != nil {
			t.Fatal(err)
		}
		ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("different signed body"))
		if !errors.Is(err, ErrPaymentResultMismatch) || ack.Accepted {
			t.Fatalf("event collision accepted: ack=%+v err=%v", ack, err)
		}
	})
}

func TestCreateRefundRequiresApprovalAndConfirmCallsProviderOnce(t *testing.T) {
	now := time.Now().UTC()
	t.Run("success", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "FULFILLED"), transaction: store.PaymentTransaction{ID: uuid.New()}}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, refundResult: payment.RefundResult{Provider: payment.ProviderAlipay, MerchantOrderID: repository.order.OrderNo, MerchantRefundID: "RF123456789", Status: payment.StatusSucceeded, AmountMinor: 200, Currency: payment.CurrencyCNY}}
		service := testService(t, repository, gateway, now)
		outcome, err := service.CreateRefund(context.Background(), CreateRefundRequest{ActorID: uuid.New(), OrderID: repository.order.ID, AmountMinor: 200, Reason: "user request", TraceID: "trace"})
		if err != nil || outcome.Refund.Status != "APPROVAL_PENDING" || !outcome.ApprovalRequired || gateway.refundCalls != 0 || repository.completeRefundCalls != 0 {
			t.Fatalf("refund request outcome=%+v provider_calls=%d err=%v", outcome, gateway.refundCalls, err)
		}
		confirmed, err := service.ConfirmRefund(context.Background(), ConfirmRefundRequest{ActorID: uuid.New(), RefundID: outcome.Refund.ID, ExpectedVersion: 1, TraceID: "confirm-trace"})
		if err != nil || confirmed.Refund.Status != "SUCCEEDED" || gateway.refundCalls != 1 || repository.completeRefundCalls != 1 {
			t.Fatalf("confirmed outcome=%+v provider_calls=%d err=%v", confirmed, gateway.refundCalls, err)
		}
		replayed, err := service.ConfirmRefund(context.Background(), ConfirmRefundRequest{ActorID: uuid.New(), RefundID: outcome.Refund.ID, ExpectedVersion: 1, TraceID: "confirm-replay"})
		if err != nil || replayed.Refund.Status != "SUCCEEDED" || gateway.refundCalls != 1 || repository.completeRefundCalls != 1 {
			t.Fatalf("replayed outcome=%+v provider_calls=%d err=%v", replayed, gateway.refundCalls, err)
		}
	})

	t.Run("ambiguous_provider_error_stays_pending", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "FULFILLED"), transaction: store.PaymentTransaction{ID: uuid.New()}}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, refundErr: errFakeProvider}
		service := testService(t, repository, gateway, now)
		outcome, err := service.CreateRefund(context.Background(), CreateRefundRequest{ActorID: uuid.New(), OrderID: repository.order.ID, AmountMinor: 200, Reason: "user request"})
		if err != nil || gateway.refundCalls != 0 {
			t.Fatalf("request called provider: outcome=%+v calls=%d err=%v", outcome, gateway.refundCalls, err)
		}
		outcome, err = service.ConfirmRefund(context.Background(), ConfirmRefundRequest{ActorID: uuid.New(), RefundID: outcome.Refund.ID, ExpectedVersion: 1})
		if !errors.Is(err, errFakeProvider) || outcome.Refund.Status != "CREATED" || repository.completeRefundCalls != 0 || gateway.refundCalls != 1 {
			t.Fatalf("provider error outcome=%+v err=%v", outcome, err)
		}
	})

	t.Run("provider_rejection_is_terminal", func(t *testing.T) {
		repository := &fakeRepository{order: testOrder(now, "FULFILLED"), transaction: store.PaymentTransaction{ID: uuid.New()}}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, refundErr: payment.ErrProviderRejected}
		service := testService(t, repository, gateway, now)
		outcome, err := service.CreateRefund(context.Background(), CreateRefundRequest{ActorID: uuid.New(), OrderID: repository.order.ID, AmountMinor: 200, Reason: "user request"})
		if err != nil || gateway.refundCalls != 0 {
			t.Fatalf("request called provider: outcome=%+v calls=%d err=%v", outcome, gateway.refundCalls, err)
		}
		outcome, err = service.ConfirmRefund(context.Background(), ConfirmRefundRequest{ActorID: uuid.New(), RefundID: outcome.Refund.ID, ExpectedVersion: 1})
		if !errors.Is(err, payment.ErrProviderRejected) || outcome.Refund.Status != "FAILED" || repository.completeRefundCalls != 1 {
			t.Fatalf("provider rejection outcome=%+v err=%v", outcome, err)
		}
	})

	t.Run("wrong_version_and_expired_approval_are_rejected_before_provider", func(t *testing.T) {
		for _, approvalErr := range []error{store.ErrRefundVersionConflict, store.ErrRefundApprovalExpired} {
			repository := &fakeRepository{order: testOrder(now, "FULFILLED"), transaction: store.PaymentTransaction{ID: uuid.New()}, approveRefundErr: approvalErr}
			gateway := &fakeGateway{provider: payment.ProviderAlipay}
			service := testService(t, repository, gateway, now)
			created, err := service.CreateRefund(context.Background(), CreateRefundRequest{ActorID: uuid.New(), OrderID: repository.order.ID, AmountMinor: 200, Reason: "user request"})
			if err != nil {
				t.Fatal(err)
			}
			_, err = service.ConfirmRefund(context.Background(), ConfirmRefundRequest{ActorID: uuid.New(), RefundID: created.Refund.ID, ExpectedVersion: 1})
			if !errors.Is(err, approvalErr) || gateway.refundCalls != 0 {
				t.Fatalf("approval error=%v provider_calls=%d, want %v/0", err, gateway.refundCalls, approvalErr)
			}
		}
	})
}

func TestPartialRefundWebhookUsesRefundAmount(t *testing.T) {
	now := time.Now().UTC()
	repository := &fakeRepository{order: testOrder(now, "REFUNDING")}
	repository.refund = store.Refund{ID: uuid.New(), OrderID: repository.order.ID, RefundNo: "RF-PARTIAL", AmountMinor: 200, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, webhookResult: payment.WebhookResult{Provider: payment.ProviderAlipay, EventID: "REFUND-EVENT", EventType: payment.WebhookRefund, MerchantOrderID: repository.order.OrderNo, MerchantRefundID: repository.refund.RefundNo, ProviderRefundID: "PROVIDER-REFUND", Status: payment.StatusRefunded, AmountMinor: repository.refund.AmountMinor, Currency: repository.order.Currency, OccurredAt: now}}
	service := testService(t, repository, gateway, now)

	ack, err := service.HandleWebhook(context.Background(), string(payment.ProviderAlipay), string(payment.FlowNative), nil, []byte("verified refund"))
	if err != nil || !ack.Accepted || repository.refund.Status != "SUCCEEDED" {
		t.Fatalf("partial refund ack=%+v refund=%+v err=%v", ack, repository.refund, err)
	}
}

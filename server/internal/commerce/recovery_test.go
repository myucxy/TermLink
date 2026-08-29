package commerce

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

type fakeRecoveryRepository struct {
	paymentCandidates     []store.CommercePendingPaymentCandidate
	fulfillmentCandidates []store.CommerceFulfillmentCandidate
	refundCandidates      []store.CommerceRefundCandidate
	paymentErr            error
	fulfillmentErr        error
	refundErr             error
	cutoffs               []time.Time
	limits                []int
}

func (repository *fakeRecoveryRepository) ListStalePendingPaymentCandidates(_ context.Context, cutoff time.Time, limit int) ([]store.CommercePendingPaymentCandidate, error) {
	repository.cutoffs = append(repository.cutoffs, cutoff)
	repository.limits = append(repository.limits, limit)
	return repository.paymentCandidates, repository.paymentErr
}

func (repository *fakeRecoveryRepository) ListPendingFulfillmentCandidates(_ context.Context, cutoff time.Time, limit int) ([]store.CommerceFulfillmentCandidate, error) {
	repository.cutoffs = append(repository.cutoffs, cutoff)
	repository.limits = append(repository.limits, limit)
	return repository.fulfillmentCandidates, repository.fulfillmentErr
}

func (repository *fakeRecoveryRepository) ListPendingRefundCandidates(_ context.Context, cutoff time.Time, limit int) ([]store.CommerceRefundCandidate, error) {
	repository.cutoffs = append(repository.cutoffs, cutoff)
	repository.limits = append(repository.limits, limit)
	return repository.refundCandidates, repository.refundErr
}

func pendingPaymentCandidate(order store.Order, updatedAt time.Time) store.CommercePendingPaymentCandidate {
	return store.CommercePendingPaymentCandidate{
		OrderID: order.ID, AccountID: order.AccountID, Provider: order.Provider,
		PaymentScene: order.PaymentScene, Status: "PENDING_PAYMENT",
		ExpiresAt: order.ExpiresAt, UpdatedAt: updatedAt,
	}
}

func TestRecoveryRunOnceIsIdempotentAcrossRepeatedCandidates(t *testing.T) {
	now := time.Date(2026, 8, 27, 3, 0, 0, 0, time.UTC)
	orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	orderRepository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: orderRepository.order.ID, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryResult: payment.QueryResult{
		Provider: payment.ProviderAlipay, MerchantOrderID: orderRepository.order.OrderNo,
		ProviderTransactionID: "RECOVERY-TRADE", Status: payment.StatusSucceeded,
		AmountMinor: orderRepository.order.AmountMinor, Currency: orderRepository.order.Currency, PaidAt: now,
	}}
	service := testService(t, orderRepository, gateway, now)
	recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
		pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
	}}
	runner := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }, DisableJitter: true})

	for run := range 2 {
		result, err := runner.RunOnce(context.Background())
		if err != nil || result.PaymentsFulfilled != 1 {
			t.Fatalf("run %d result=%+v err=%v", run, result, err)
		}
	}
	if gateway.queryCalls != 1 || orderRepository.markPaidCalls != 1 || orderRepository.fulfillCalls != 1 {
		t.Fatalf("repeated candidate repeated side effects: query=%d paid=%d fulfilled=%d", gateway.queryCalls, orderRepository.markPaidCalls, orderRepository.fulfillCalls)
	}
}

func TestRecoveryRejectsOutOfOrderCanceledAndProviderMismatch(t *testing.T) {
	now := time.Now().UTC()
	for _, status := range []string{"CLOSED", "REFUNDED"} {
		t.Run(strings.ToLower(status), func(t *testing.T) {
			orderRepository := &fakeRepository{order: testOrder(now, status)}
			gateway := &fakeGateway{provider: payment.ProviderAlipay}
			service := testService(t, orderRepository, gateway, now)
			recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
				pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
			}}
			result, err := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
			if !errors.Is(err, ErrPaymentStateNotProgressable) || result.Failed != 1 || gateway.queryCalls != 0 || orderRepository.fulfillCalls != 0 {
				t.Fatalf("terminal state advanced: result=%+v err=%v query=%d", result, err, gateway.queryCalls)
			}
		})
	}

	t.Run("provider_result_mismatch", func(t *testing.T) {
		orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
		orderRepository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: orderRepository.order.ID, Status: "PENDING"}
		gateway := &fakeGateway{provider: payment.ProviderAlipay, queryResult: payment.QueryResult{
			Provider: payment.ProviderWeChatPay, MerchantOrderID: orderRepository.order.OrderNo,
			ProviderTransactionID: "WRONG-PROVIDER", Status: payment.StatusSucceeded,
			AmountMinor: orderRepository.order.AmountMinor, Currency: orderRepository.order.Currency,
		}}
		service := testService(t, orderRepository, gateway, now)
		recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
			pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
		}}
		result, err := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
		if !errors.Is(err, ErrPaymentResultMismatch) || result.Failed != 1 || orderRepository.markPaidCalls != 0 || orderRepository.fulfillCalls != 0 {
			t.Fatalf("provider mismatch advanced: result=%+v err=%v", result, err)
		}
	})
}

func TestRecoveryPartialFailureDoesNotBlockOtherClasses(t *testing.T) {
	now := time.Now().UTC()
	listErr := errors.New("payment candidate query unavailable")
	orderRepository := &fakeRepository{order: testOrder(now, "PAID")}
	refund := store.Refund{ID: uuid.New(), OrderID: orderRepository.order.ID, RefundNo: "RF-PARTIAL-FAILURE", AmountMinor: 200, Status: "PENDING"}
	orderRepository.refund = refund
	gateway := &fakeGateway{provider: payment.ProviderAlipay, refundResult: payment.RefundResult{
		Provider: payment.ProviderAlipay, MerchantOrderID: orderRepository.order.OrderNo, MerchantRefundID: refund.RefundNo,
		Status: payment.StatusPending, AmountMinor: refund.AmountMinor, Currency: orderRepository.order.Currency,
	}}
	service := testService(t, orderRepository, gateway, now)
	recoveryRepository := &fakeRecoveryRepository{
		paymentErr: listErr,
		fulfillmentCandidates: []store.CommerceFulfillmentCandidate{{
			OrderID: orderRepository.order.ID, Status: "PAID", UpdatedAt: now.Add(-5 * time.Minute),
		}},
		refundCandidates: []store.CommerceRefundCandidate{{
			RefundID: refund.ID, OrderID: orderRepository.order.ID, Provider: orderRepository.order.Provider,
			Status: "PENDING", UpdatedAt: now.Add(-5 * time.Minute),
		}},
	}
	result, err := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
	if !errors.Is(err, listErr) || result.FulfillmentsCompleted != 1 || result.RefundsPending != 1 || orderRepository.fulfillCalls != 1 {
		t.Fatalf("independent recovery was blocked: result=%+v err=%v", result, err)
	}
}

func TestRecoveryLeavesAuthoritativePendingPaymentUnchanged(t *testing.T) {
	now := time.Now().UTC()
	orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	orderRepository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: orderRepository.order.ID, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryResult: payment.QueryResult{
		Provider: payment.ProviderAlipay, MerchantOrderID: orderRepository.order.OrderNo,
		Status: payment.StatusPending, AmountMinor: orderRepository.order.AmountMinor, Currency: orderRepository.order.Currency,
	}}
	service := testService(t, orderRepository, gateway, now)
	repository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
		pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
	}}
	result, err := NewRecoveryRunner(repository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
	if err != nil || result.PaymentsPending != 1 || result.PaymentsFulfilled != 0 || orderRepository.markPaidCalls != 0 || orderRepository.fulfillCalls != 0 || orderRepository.order.Status != "PENDING_PAYMENT" {
		t.Fatalf("pending provider result changed state: result=%+v order=%+v err=%v", result, orderRepository.order, err)
	}
}

func TestRecoveryQueriesRefundTerminalAndMismatchStates(t *testing.T) {
	now := time.Now().UTC()
	for _, test := range []struct {
		name           string
		providerStatus payment.PaymentStatus
		provider       payment.Provider
		wantStore      string
		wantPending    int
		wantCompleted  int
		wantFailed     int
		wantErr        error
	}{
		{name: "pending", providerStatus: payment.StatusRefunding, provider: payment.ProviderAlipay, wantStore: "PENDING", wantPending: 1},
		{name: "success", providerStatus: payment.StatusRefunded, provider: payment.ProviderAlipay, wantStore: "SUCCEEDED", wantCompleted: 1},
		{name: "failure", providerStatus: payment.StatusFailed, provider: payment.ProviderAlipay, wantStore: "FAILED", wantFailed: 1},
		{name: "provider_mismatch", providerStatus: payment.StatusRefunded, provider: payment.ProviderWeChatPay, wantStore: "PENDING", wantErr: ErrPaymentResultMismatch},
	} {
		t.Run(test.name, func(t *testing.T) {
			orderRepository := &fakeRepository{order: testOrder(now, "REFUNDING")}
			orderRepository.refund = store.Refund{ID: uuid.New(), OrderID: orderRepository.order.ID, RefundNo: "RF-RECOVERY-" + test.name, AmountMinor: 200, Status: "PENDING"}
			gateway := &fakeGateway{provider: payment.ProviderAlipay, refundResult: payment.RefundResult{
				Provider: test.provider, MerchantOrderID: orderRepository.order.OrderNo, MerchantRefundID: orderRepository.refund.RefundNo,
				ProviderRefundID: "PROVIDER-REFUND", Status: test.providerStatus, AmountMinor: orderRepository.refund.AmountMinor,
				Currency: orderRepository.order.Currency,
			}}
			service := testService(t, orderRepository, gateway, now)
			repository := &fakeRecoveryRepository{refundCandidates: []store.CommerceRefundCandidate{{
				RefundID: orderRepository.refund.ID, OrderID: orderRepository.order.ID, Provider: orderRepository.order.Provider,
				Status: "PENDING", UpdatedAt: now.Add(-5 * time.Minute),
			}}}
			result, err := NewRecoveryRunner(repository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
			if !errors.Is(err, test.wantErr) || orderRepository.refund.Status != test.wantStore || result.RefundsPending != test.wantPending || result.RefundsCompleted != test.wantCompleted || result.RefundsFailed != test.wantFailed {
				t.Fatalf("refund recovery result=%+v refund=%+v err=%v", result, orderRepository.refund, err)
			}
			wantCompleteCalls := 0
			if test.wantStore == "SUCCEEDED" || test.wantStore == "FAILED" {
				wantCompleteCalls = 1
			}
			if gateway.queryRefundCalls != 1 || orderRepository.completeRefundCalls != wantCompleteCalls {
				t.Fatalf("refund calls query=%d complete=%d", gateway.queryRefundCalls, orderRepository.completeRefundCalls)
			}
		})
	}
}

func TestRecoveryInvalidAndFreshCandidatesFailClosed(t *testing.T) {
	now := time.Now().UTC()
	orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	service := testService(t, orderRepository, &fakeGateway{provider: payment.ProviderAlipay}, now)
	recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
		{OrderID: uuid.New(), AccountID: uuid.New(), Provider: "UNKNOWN", PaymentScene: "NATIVE", Status: "PENDING_PAYMENT", UpdatedAt: now.Add(-5 * time.Minute)},
		pendingPaymentCandidate(orderRepository.order, now),
	}}
	result, err := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
	if err == nil || result.InvalidCandidates != 2 || result.PaymentsQueried != 0 {
		t.Fatalf("unsafe candidates were processed: result=%+v err=%v", result, err)
	}
}

func TestRecoveryCancellationStopsBlockingProvider(t *testing.T) {
	now := time.Now().UTC()
	orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	orderRepository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: orderRepository.order.ID, Status: "PENDING"}
	started := make(chan struct{})
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryFunc: func(ctx context.Context, _ string) (payment.QueryResult, error) {
		close(started)
		<-ctx.Done()
		return payment.QueryResult{}, ctx.Err()
	}}
	service := testService(t, orderRepository, gateway, now)
	recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
		pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
	}}
	runner := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }, ItemTimeout: time.Minute, PassTimeout: 2 * time.Minute})
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		_, err := runner.RunOnce(ctx)
		done <- err
	}()
	<-started
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("cancellation error=%v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("recovery did not stop on cancellation")
	}
	if len(recoveryRepository.cutoffs) != 1 {
		t.Fatalf("later candidate classes ran after cancellation: calls=%d", len(recoveryRepository.cutoffs))
	}
}

func TestRecoveryItemTimeoutIsBounded(t *testing.T) {
	now := time.Now().UTC()
	orderRepository := &fakeRepository{order: testOrder(now, "PENDING_PAYMENT")}
	orderRepository.transaction = store.PaymentTransaction{ID: uuid.New(), OrderID: orderRepository.order.ID, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, queryFunc: func(ctx context.Context, _ string) (payment.QueryResult, error) {
		<-ctx.Done()
		return payment.QueryResult{}, ctx.Err()
	}}
	service := testService(t, orderRepository, gateway, now)
	recoveryRepository := &fakeRecoveryRepository{paymentCandidates: []store.CommercePendingPaymentCandidate{
		pendingPaymentCandidate(orderRepository.order, now.Add(-5*time.Minute)),
	}}
	started := time.Now()
	result, err := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{Clock: func() time.Time { return now }, ItemTimeout: 20 * time.Millisecond}).RunOnce(context.Background())
	if !errors.Is(err, context.DeadlineExceeded) || result.Failed != 1 || time.Since(started) > time.Second {
		t.Fatalf("item timeout unbounded: elapsed=%s result=%+v err=%v", time.Since(started), result, err)
	}
	if len(recoveryRepository.cutoffs) != 3 {
		t.Fatalf("timeout blocked independent classes: calls=%d", len(recoveryRepository.cutoffs))
	}
}

func TestRecoveryUsesBoundedCutoffBatchAndLowCardinalityLogs(t *testing.T) {
	now := time.Date(2026, 8, 27, 3, 0, 0, 0, time.UTC)
	orderRepository := &fakeRepository{order: testOrder(now, "REFUNDING")}
	refundID := uuid.New()
	orderRepository.refund = store.Refund{ID: refundID, OrderID: orderRepository.order.ID, RefundNo: "RF-LOG", AmountMinor: 200, Status: "PENDING"}
	gateway := &fakeGateway{provider: payment.ProviderAlipay, refundResult: payment.RefundResult{
		Provider: payment.ProviderAlipay, MerchantOrderID: orderRepository.order.OrderNo, MerchantRefundID: orderRepository.refund.RefundNo,
		Status: payment.StatusPending, AmountMinor: orderRepository.refund.AmountMinor, Currency: orderRepository.order.Currency,
	}}
	service := testService(t, orderRepository, gateway, now)
	recoveryRepository := &fakeRecoveryRepository{refundCandidates: []store.CommerceRefundCandidate{{
		RefundID: refundID, OrderID: orderRepository.order.ID, Provider: string(payment.ProviderAlipay),
		Status: "CREATED", UpdatedAt: now.Add(-5 * time.Minute),
	}}}
	var logs bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&logs, nil))
	runner := NewRecoveryRunner(recoveryRepository, service, RecoveryOptions{
		Clock: func() time.Time { return now }, BatchSize: MaxRecoveryBatchSize + 1,
		StaleAfter: 3 * time.Minute, Logger: logger, RandomFloat64: func() float64 { return 2 },
	})
	runner.runAndLog(context.Background())
	if len(recoveryRepository.cutoffs) != 3 || len(recoveryRepository.limits) != 3 {
		t.Fatalf("candidate feeds not all called: %+v", recoveryRepository)
	}
	for index := range recoveryRepository.cutoffs {
		if !recoveryRepository.cutoffs[index].Equal(now.Add(-3*time.Minute)) || recoveryRepository.limits[index] != DefaultRecoveryBatchSize {
			t.Fatalf("unbounded feed call %d: cutoff=%s limit=%d", index, recoveryRepository.cutoffs[index], recoveryRepository.limits[index])
		}
	}
	if delay := runner.nextDelay(); delay != runner.interval {
		t.Fatalf("invalid jitter source was not neutralized: delay=%s interval=%s", delay, runner.interval)
	}
	logText := logs.String()
	if !strings.Contains(logText, `"refunds_queried":1`) || strings.Contains(logText, refundID.String()) || strings.Contains(logText, orderRepository.order.ID.String()) {
		t.Fatalf("recovery log is missing aggregate or leaks identifiers: %s", logText)
	}
}

func TestRecoveryDefensivelyBoundsOversizedCandidateFeed(t *testing.T) {
	now := time.Now().UTC()
	orderRepository := &fakeRepository{order: testOrder(now, "REFUNDING")}
	orderRepository.refund = store.Refund{ID: uuid.New(), OrderID: orderRepository.order.ID, RefundNo: "RF-OVERFLOW", AmountMinor: 200, Status: "PENDING"}
	service := testService(t, orderRepository, &fakeGateway{provider: payment.ProviderAlipay}, now)
	refunds := make([]store.CommerceRefundCandidate, DefaultRecoveryBatchSize+10)
	for index := range refunds {
		refunds[index] = store.CommerceRefundCandidate{
			RefundID: uuid.New(), OrderID: orderRepository.order.ID, Provider: string(payment.ProviderAlipay),
			Status: "PENDING", UpdatedAt: now.Add(-5 * time.Minute),
		}
	}
	repository := &fakeRecoveryRepository{refundCandidates: refunds}
	result, err := NewRecoveryRunner(repository, service, RecoveryOptions{Clock: func() time.Time { return now }}).RunOnce(context.Background())
	// Missing local refund rows fail closed, but at most the configured batch
	// is attempted even when a broken repository returns more.
	if !errors.Is(err, ErrRecoveryCandidateOverflow) || result.RefundsQueried != DefaultRecoveryBatchSize || result.CandidatesListed != DefaultRecoveryBatchSize {
		t.Fatalf("oversized feed was not bounded: result=%+v err=%v", result, err)
	}
}

type recoverySensitiveLogError struct {
	text  string
	calls int
}

func (e *recoverySensitiveLogError) Error() string {
	e.calls++
	return e.text
}

func TestRecoveryFailureLogDoesNotFormatError(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &recoverySensitiveLogError{text: "merchant_private_key=recovery-secret"}

	logRecoveryFailure(logger, RecoveryResult{CandidatesListed: 7, Failed: 2, InvalidCandidates: 1}, sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "recovery-secret") {
		t.Fatalf("recovery log formatted or exposed the error: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"commerce_recovery_partial_failure"`) ||
		!strings.Contains(logged, `"candidates":7`) || !strings.Contains(logged, `"failed":2`) {
		t.Fatalf("recovery log is missing stable fields: %s", logged)
	}
}

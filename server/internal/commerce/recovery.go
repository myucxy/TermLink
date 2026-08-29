package commerce

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"math/rand/v2"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultRecoveryInterval    = time.Minute
	DefaultRecoveryJitter      = 10 * time.Second
	DefaultRecoveryStaleAfter  = 2 * time.Minute
	DefaultRecoveryItemTimeout = 12 * time.Second
	DefaultRecoveryPassTimeout = 4 * time.Minute
	DefaultRecoveryBatchSize   = 50
	MaxRecoveryBatchSize       = 200
	MinRecoveryInterval        = 5 * time.Second
	MaxRecoveryItemTimeout     = time.Minute
	MaxRecoveryPassTimeout     = 15 * time.Minute
)

var ErrRecoveryCandidateOverflow = errors.New("commerce: recovery candidate feed exceeded requested limit")

// RecoveryRepository exposes bounded, read-only candidate feeds. State
// transitions remain behind Service and the transactional store methods.
type RecoveryRepository interface {
	ListStalePendingPaymentCandidates(context.Context, time.Time, int) ([]store.CommercePendingPaymentCandidate, error)
	ListPendingFulfillmentCandidates(context.Context, time.Time, int) ([]store.CommerceFulfillmentCandidate, error)
	ListPendingRefundCandidates(context.Context, time.Time, int) ([]store.CommerceRefundCandidate, error)
}

var _ RecoveryRepository = (*store.Postgres)(nil)

type RecoveryOptions struct {
	Interval      time.Duration
	Jitter        time.Duration
	DisableJitter bool
	StaleAfter    time.Duration
	ItemTimeout   time.Duration
	PassTimeout   time.Duration
	BatchSize     int
	Clock         func() time.Time
	RandomFloat64 func() float64
	Logger        *slog.Logger
}

type RecoveryResult struct {
	CandidatesListed      int
	PaymentsQueried       int
	PaymentsFulfilled     int
	PaymentsPending       int
	FulfillmentsRetried   int
	FulfillmentsCompleted int
	InvalidCandidates     int
	Failed                int
	RefundsQueried        int
	RefundsPending        int
	RefundsCompleted      int
	RefundsFailed         int
	PaymentBatchFull      bool
	FulfillmentBatchFull  bool
	RefundBatchFull       bool
}

// RecoveryRunner is an at-least-once recovery loop. Candidate feeds can
// overlap across replicas; all actual state transitions are idempotent and
// re-read canonical order state before mutation.
type RecoveryRunner struct {
	repository    RecoveryRepository
	service       *Service
	interval      time.Duration
	jitter        time.Duration
	staleAfter    time.Duration
	itemTimeout   time.Duration
	passTimeout   time.Duration
	batchSize     int
	clock         func() time.Time
	randomFloat64 func() float64
	logger        *slog.Logger
	newTimer      func(time.Duration) recoveryTimer
}

type recoveryTimer interface {
	Chan() <-chan time.Time
	Stop() bool
}

type realRecoveryTimer struct{ *time.Timer }

func (timer realRecoveryTimer) Chan() <-chan time.Time { return timer.C }

func NewRecoveryRunner(repository RecoveryRepository, service *Service, options RecoveryOptions) *RecoveryRunner {
	if options.Interval < MinRecoveryInterval {
		options.Interval = DefaultRecoveryInterval
	}
	if options.DisableJitter {
		options.Jitter = 0
	} else if options.Jitter <= 0 {
		options.Jitter = DefaultRecoveryJitter
	}
	if options.Jitter >= options.Interval {
		options.Jitter = options.Interval / 4
	}
	if options.StaleAfter <= 0 {
		options.StaleAfter = DefaultRecoveryStaleAfter
	}
	if options.ItemTimeout <= 0 || options.ItemTimeout > MaxRecoveryItemTimeout {
		options.ItemTimeout = DefaultRecoveryItemTimeout
	}
	if options.PassTimeout <= 0 || options.PassTimeout > MaxRecoveryPassTimeout || options.PassTimeout < options.ItemTimeout {
		options.PassTimeout = DefaultRecoveryPassTimeout
	}
	if options.BatchSize < 1 || options.BatchSize > MaxRecoveryBatchSize {
		options.BatchSize = DefaultRecoveryBatchSize
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.RandomFloat64 == nil {
		options.RandomFloat64 = rand.Float64
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &RecoveryRunner{
		repository: repository, service: service, interval: options.Interval, jitter: options.Jitter,
		staleAfter: options.StaleAfter, itemTimeout: options.ItemTimeout, passTimeout: options.PassTimeout,
		batchSize: options.BatchSize, clock: options.Clock, randomFloat64: options.RandomFloat64,
		logger: options.Logger, newTimer: func(delay time.Duration) recoveryTimer { return realRecoveryTimer{time.NewTimer(delay)} },
	}
}

// RunOnce processes all independent candidate classes even when listing or
// processing another class fails. Cancellation is the exception: it stops new
// provider work immediately.
func (runner *RecoveryRunner) RunOnce(ctx context.Context) (RecoveryResult, error) {
	if runner == nil || runner.repository == nil || runner.service == nil {
		return RecoveryResult{}, ErrInvalidRequest
	}
	passContext, cancel := context.WithTimeout(ctx, runner.passTimeout)
	defer cancel()
	cutoff := runner.clock().UTC().Add(-runner.staleAfter)
	var result RecoveryResult
	var recoveryErrors []error

	paymentCandidates, err := runner.repository.ListStalePendingPaymentCandidates(passContext, cutoff, runner.batchSize)
	if err != nil {
		recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending payments: %w", err))
	} else {
		if len(paymentCandidates) > runner.batchSize {
			paymentCandidates = paymentCandidates[:runner.batchSize]
			recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending payments: %w", ErrRecoveryCandidateOverflow))
		}
		result.CandidatesListed += len(paymentCandidates)
		result.PaymentBatchFull = len(paymentCandidates) == runner.batchSize
		for index, candidate := range paymentCandidates {
			if err := passContext.Err(); err != nil {
				recoveryErrors = append(recoveryErrors, err)
				return result, errors.Join(recoveryErrors...)
			}
			if !validPaymentRecoveryCandidate(candidate, cutoff) {
				result.InvalidCandidates++
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("pending payment candidate %d: %w", index, ErrPaymentResultMismatch))
				continue
			}
			result.PaymentsQueried++
			itemContext, itemCancel := context.WithTimeout(passContext, runner.itemTimeout)
			outcome, itemErr := runner.service.QueryAndFulfill(itemContext, candidate.AccountID, candidate.OrderID)
			itemCancel()
			if itemErr != nil {
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("pending payment candidate %d: %w", index, itemErr))
				continue
			}
			if outcome.Fulfilled {
				result.PaymentsFulfilled++
			} else {
				result.PaymentsPending++
			}
		}
	}

	if err := passContext.Err(); err != nil {
		recoveryErrors = append(recoveryErrors, err)
		return result, errors.Join(recoveryErrors...)
	}
	fulfillmentCandidates, err := runner.repository.ListPendingFulfillmentCandidates(passContext, cutoff, runner.batchSize)
	if err != nil {
		recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending fulfillment: %w", err))
	} else {
		if len(fulfillmentCandidates) > runner.batchSize {
			fulfillmentCandidates = fulfillmentCandidates[:runner.batchSize]
			recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending fulfillment: %w", ErrRecoveryCandidateOverflow))
		}
		result.CandidatesListed += len(fulfillmentCandidates)
		result.FulfillmentBatchFull = len(fulfillmentCandidates) == runner.batchSize
		for index, candidate := range fulfillmentCandidates {
			if err := passContext.Err(); err != nil {
				recoveryErrors = append(recoveryErrors, err)
				return result, errors.Join(recoveryErrors...)
			}
			if !validFulfillmentRecoveryCandidate(candidate, cutoff) {
				result.InvalidCandidates++
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("fulfillment candidate %d: %w", index, ErrPaymentResultMismatch))
				continue
			}
			result.FulfillmentsRetried++
			itemContext, itemCancel := context.WithTimeout(passContext, runner.itemTimeout)
			order, itemErr := runner.service.RecoverFulfillment(itemContext, candidate.OrderID)
			itemCancel()
			if itemErr != nil {
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("fulfillment candidate %d: %w", index, itemErr))
				continue
			}
			if order.Status == "FULFILLED" {
				result.FulfillmentsCompleted++
			}
		}
	}

	if err := passContext.Err(); err != nil {
		recoveryErrors = append(recoveryErrors, err)
		return result, errors.Join(recoveryErrors...)
	}
	refundCandidates, err := runner.repository.ListPendingRefundCandidates(passContext, cutoff, runner.batchSize)
	if err != nil {
		recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending refunds: %w", err))
	} else {
		if len(refundCandidates) > runner.batchSize {
			refundCandidates = refundCandidates[:runner.batchSize]
			recoveryErrors = append(recoveryErrors, fmt.Errorf("list pending refunds: %w", ErrRecoveryCandidateOverflow))
		}
		result.CandidatesListed += len(refundCandidates)
		result.RefundBatchFull = len(refundCandidates) == runner.batchSize
		for index, candidate := range refundCandidates {
			if err := passContext.Err(); err != nil {
				recoveryErrors = append(recoveryErrors, err)
				return result, errors.Join(recoveryErrors...)
			}
			if !validRefundRecoveryCandidate(candidate, cutoff) {
				result.InvalidCandidates++
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("refund candidate %d: %w", index, ErrPaymentResultMismatch))
				continue
			}
			result.RefundsQueried++
			itemContext, itemCancel := context.WithTimeout(passContext, runner.itemTimeout)
			outcome, itemErr := runner.service.QueryAndCompleteRefund(itemContext, candidate.RefundID)
			itemCancel()
			if itemErr != nil {
				result.Failed++
				recoveryErrors = append(recoveryErrors, fmt.Errorf("refund candidate %d: %w", index, itemErr))
				continue
			}
			switch outcome.ProviderStatus {
			case payment.StatusSucceeded, payment.StatusRefunded:
				result.RefundsCompleted++
			case payment.StatusFailed, payment.StatusClosed:
				result.RefundsFailed++
			default:
				result.RefundsPending++
			}
		}
	}

	return result, errors.Join(recoveryErrors...)
}

func validPaymentRecoveryCandidate(candidate store.CommercePendingPaymentCandidate, cutoff time.Time) bool {
	return candidate.OrderID != uuid.Nil && candidate.AccountID != uuid.Nil && candidate.Status == "PENDING_PAYMENT" &&
		supportedProviderScene(candidate.Provider, candidate.PaymentScene) && !candidate.UpdatedAt.IsZero() && !candidate.UpdatedAt.After(cutoff)
}

func validFulfillmentRecoveryCandidate(candidate store.CommerceFulfillmentCandidate, cutoff time.Time) bool {
	return candidate.OrderID != uuid.Nil && (candidate.Status == "PAID" || candidate.Status == "FULFILLING") &&
		!candidate.UpdatedAt.IsZero() && !candidate.UpdatedAt.After(cutoff)
}

func validRefundRecoveryCandidate(candidate store.CommerceRefundCandidate, cutoff time.Time) bool {
	return candidate.RefundID != uuid.Nil && candidate.OrderID != uuid.Nil && supportedProvider(candidate.Provider) &&
		(candidate.Status == "CREATED" || candidate.Status == "PENDING") && !candidate.UpdatedAt.IsZero() && !candidate.UpdatedAt.After(cutoff)
}

func (runner *RecoveryRunner) Run(ctx context.Context) {
	runner.runAndLog(ctx)
	for {
		timer := runner.newTimer(runner.nextDelay())
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.Chan():
			timer.Stop()
			runner.runAndLog(ctx)
		}
	}
}

func (runner *RecoveryRunner) nextDelay() time.Duration {
	if runner.jitter <= 0 {
		return runner.interval
	}
	randomValue := runner.randomFloat64()
	if math.IsNaN(randomValue) || randomValue < 0 || randomValue > 1 {
		randomValue = 0.5
	}
	offset := time.Duration((randomValue*2 - 1) * float64(runner.jitter))
	delay := runner.interval + offset
	if delay < MinRecoveryInterval {
		return MinRecoveryInterval
	}
	return delay
}

func (runner *RecoveryRunner) runAndLog(ctx context.Context) {
	result, err := runner.RunOnce(ctx)
	if err != nil {
		if ctx.Err() == nil {
			logRecoveryFailure(runner.logger, result, err)
		}
		return
	}
	if result.CandidatesListed > 0 {
		runner.logger.Info("commerce recovery pass completed",
			"candidates", result.CandidatesListed,
			"payments_queried", result.PaymentsQueried,
			"payments_fulfilled", result.PaymentsFulfilled,
			"payments_pending", result.PaymentsPending,
			"fulfillments_retried", result.FulfillmentsRetried,
			"fulfillments_completed", result.FulfillmentsCompleted,
			"refunds_queried", result.RefundsQueried,
			"refunds_pending", result.RefundsPending,
			"refunds_completed", result.RefundsCompleted,
			"refunds_failed", result.RefundsFailed)
	}
}

func logRecoveryFailure(logger *slog.Logger, result RecoveryResult, _ error) {
	logger.Error("commerce recovery pass partially failed",
		"error_class", "commerce_recovery_partial_failure",
		"candidates", result.CandidatesListed,
		"failed", result.Failed,
		"invalid_candidates", result.InvalidCandidates)
}

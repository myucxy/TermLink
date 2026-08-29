// Package commercecleanup runs bounded retention and expiry maintenance for
// the Server Web commercial control plane.
package commercecleanup

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"time"
)

const (
	DefaultInterval                     = 5 * time.Minute
	DefaultJitter                       = 30 * time.Second
	DefaultOutboxRetention              = 7 * 24 * time.Hour
	DefaultPaymentNotificationRetention = 30 * 24 * time.Hour
	DefaultBatchSize                    = 100
	MaxBatchSize                        = 200
	MinInterval                         = time.Second
)

// Store keeps all mutations in the PostgreSQL authority. In particular,
// ExpireEntitlementGrants must expire a bounded batch and recompute affected
// materialized entitlements in the same transaction.
type Store interface {
	CloseExpiredOrders(context.Context, int) (int64, error)
	ExpireEntitlementGrants(context.Context, time.Time, int) (int64, int, error)
	PurgeExpiredIdempotencyKeys(context.Context, time.Time, int) (int64, error)
	PurgeProcessedOutbox(context.Context, time.Time, int) (int64, error)
	RedactPaymentNotificationPayloads(context.Context, time.Time, int) (int64, error)
	CountExpiredAmbiguousInvitationReservations(context.Context, time.Time, int) (int, bool, error)
}

type Options struct {
	Interval                     time.Duration
	Jitter                       time.Duration
	DisableJitter                bool
	OutboxRetention              time.Duration
	PaymentNotificationRetention time.Duration
	BatchSize                    int
	Clock                        func() time.Time
	RandomFloat64                func() float64
	Logger                       *slog.Logger
}

type Result struct {
	OrdersClosed                    int64
	GrantsExpired                   int64
	EntitlementsRecomputed          int
	IdempotencyKeysPurged           int64
	ProcessedOutboxPurged           int64
	PaymentNotificationPayloadsGone int64
	AmbiguousInvitationReservations int
	AmbiguousInvitationMore         bool
}

type Runner struct {
	store                        Store
	interval                     time.Duration
	jitter                       time.Duration
	outboxRetention              time.Duration
	paymentNotificationRetention time.Duration
	batchSize                    int
	clock                        func() time.Time
	randomFloat64                func() float64
	logger                       *slog.Logger
	newTimer                     func(time.Duration) timer
}

type timer interface {
	Chan() <-chan time.Time
	Stop() bool
}

type realTimer struct{ *time.Timer }

func (t realTimer) Chan() <-chan time.Time { return t.C }

func New(repository Store, options Options) *Runner {
	if options.Interval < MinInterval {
		options.Interval = DefaultInterval
	}
	if options.DisableJitter {
		options.Jitter = 0
	} else if options.Jitter <= 0 {
		options.Jitter = DefaultJitter
	}
	if options.Jitter >= options.Interval {
		options.Jitter = options.Interval / 4
	}
	if options.OutboxRetention <= 0 {
		options.OutboxRetention = DefaultOutboxRetention
	}
	if options.PaymentNotificationRetention <= 0 {
		options.PaymentNotificationRetention = DefaultPaymentNotificationRetention
	}
	if options.BatchSize <= 0 || options.BatchSize > MaxBatchSize {
		options.BatchSize = DefaultBatchSize
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
	return &Runner{
		store: repository, interval: options.Interval, jitter: options.Jitter,
		outboxRetention:              options.OutboxRetention,
		paymentNotificationRetention: options.PaymentNotificationRetention,
		batchSize:                    options.BatchSize, clock: options.Clock,
		randomFloat64: options.RandomFloat64, logger: options.Logger,
		newTimer: func(delay time.Duration) timer { return realTimer{time.NewTimer(delay)} },
	}
}

// RunOnce attempts every independent maintenance operation even if another
// operation fails. This prevents, for example, a payment-provider data issue
// from indefinitely retaining expired idempotency records.
func (r *Runner) RunOnce(ctx context.Context) (Result, error) {
	now := r.clock().UTC()
	var result Result
	var operationErrors []error

	var err error
	result.OrdersClosed, err = r.store.CloseExpiredOrders(ctx, r.batchSize)
	operationErrors = collectError(err, operationErrors, "close expired orders")

	result.GrantsExpired, result.EntitlementsRecomputed, err = r.store.ExpireEntitlementGrants(ctx, now, r.batchSize)
	if err != nil {
		operationErrors = append(operationErrors, operationError("expire entitlement grants", err))
	}

	result.AmbiguousInvitationReservations, result.AmbiguousInvitationMore, err =
		r.store.CountExpiredAmbiguousInvitationReservations(ctx, now, r.batchSize)
	if err != nil {
		operationErrors = append(operationErrors, operationError("inspect ambiguous invitation reservations", err))
	}

	result.IdempotencyKeysPurged, err = r.store.PurgeExpiredIdempotencyKeys(ctx, now, r.batchSize)
	operationErrors = collectError(err, operationErrors, "purge expired idempotency keys")
	result.ProcessedOutboxPurged, err = r.store.PurgeProcessedOutbox(ctx, now.Add(-r.outboxRetention), r.batchSize)
	operationErrors = collectError(err, operationErrors, "purge processed outbox")
	result.PaymentNotificationPayloadsGone, err = r.store.RedactPaymentNotificationPayloads(ctx, now.Add(-r.paymentNotificationRetention), r.batchSize)
	operationErrors = collectError(err, operationErrors, "redact payment notification payloads")

	return result, errors.Join(operationErrors...)
}

func collectError(err error, current []error, operation string) []error {
	if err != nil {
		current = append(current, operationError(operation, err))
	}
	return current
}

func operationError(operation string, err error) error {
	return fmt.Errorf("%s: %w", operation, err)
}

// Run performs one initial bounded pass, then schedules later passes with
// centered jitter. Context cancellation stops the active timer immediately.
func (r *Runner) Run(ctx context.Context) {
	r.runAndLog(ctx)
	for {
		timer := r.newTimer(r.nextDelay())
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.Chan():
			timer.Stop()
			r.runAndLog(ctx)
		}
	}
}

func (r *Runner) nextDelay() time.Duration {
	if r.jitter <= 0 {
		return r.interval
	}
	offset := time.Duration((r.randomFloat64()*2 - 1) * float64(r.jitter))
	delay := r.interval + offset
	if delay < time.Second {
		return time.Second
	}
	return delay
}

func (r *Runner) runAndLog(ctx context.Context) {
	result, err := r.RunOnce(ctx)
	if result.AmbiguousInvitationReservations > 0 {
		r.logger.Warn("expired invitation reservations require reconciliation; no capacity was released",
			"reservations", result.AmbiguousInvitationReservations,
			"more", result.AmbiguousInvitationMore)
	}
	if err != nil {
		if ctx.Err() == nil {
			logMaintenanceFailure(r.logger, result, err)
		}
		return
	}
	if result.OrdersClosed > 0 || result.GrantsExpired > 0 || result.IdempotencyKeysPurged > 0 ||
		result.ProcessedOutboxPurged > 0 || result.PaymentNotificationPayloadsGone > 0 {
		r.logger.Info("commerce maintenance completed",
			"orders_closed", result.OrdersClosed,
			"grants_expired", result.GrantsExpired,
			"entitlements_recomputed", result.EntitlementsRecomputed,
			"idempotency_keys_purged", result.IdempotencyKeysPurged,
			"processed_outbox_purged", result.ProcessedOutboxPurged,
			"payment_notification_payloads_redacted", result.PaymentNotificationPayloadsGone)
	}
}

func logMaintenanceFailure(logger *slog.Logger, result Result, _ error) {
	logger.Error("commerce maintenance partially failed",
		"error_class", "commerce_maintenance_partial_failure",
		"orders_closed", result.OrdersClosed,
		"grants_expired", result.GrantsExpired,
		"entitlements_recomputed", result.EntitlementsRecomputed,
		"ambiguous_invitation_reservations", result.AmbiguousInvitationReservations,
		"idempotency_keys_purged", result.IdempotencyKeysPurged,
		"processed_outbox_purged", result.ProcessedOutboxPurged,
		"payment_notification_payloads_redacted", result.PaymentNotificationPayloadsGone)
}

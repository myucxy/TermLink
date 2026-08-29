package commercecleanup

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"strings"
	"testing"
	"time"
)

type sensitiveLogError struct {
	text  string
	calls int
}

func (e *sensitiveLogError) Error() string {
	e.calls++
	return e.text
}

type recordingStore struct {
	orderLimit, grantLimit, invitationLimit int
	idempotencyLimit, outboxLimit           int
	notificationLimit                       int
	grantNow, invitationNow, idempotencyNow time.Time
	outboxBefore, notificationBefore        time.Time
	orderCount, grantCount                  int64
	recomputed                              int
	idempotencyCount, outboxCount           int64
	notificationCount                       int64
	ambiguous                               int
	more                                    bool
	orderErr                                error
	grantErr                                error
	idempotencyErr                          error
}

func (s *recordingStore) CloseExpiredOrders(_ context.Context, limit int) (int64, error) {
	s.orderLimit = limit
	return s.orderCount, s.orderErr
}

func (s *recordingStore) ExpireEntitlementGrants(_ context.Context, now time.Time, limit int) (int64, int, error) {
	s.grantNow, s.grantLimit = now, limit
	return s.grantCount, s.recomputed, s.grantErr
}

func (s *recordingStore) PurgeExpiredIdempotencyKeys(_ context.Context, now time.Time, limit int) (int64, error) {
	s.idempotencyNow, s.idempotencyLimit = now, limit
	return s.idempotencyCount, s.idempotencyErr
}

func (s *recordingStore) PurgeProcessedOutbox(_ context.Context, before time.Time, limit int) (int64, error) {
	s.outboxBefore, s.outboxLimit = before, limit
	return s.outboxCount, nil
}

func (s *recordingStore) RedactPaymentNotificationPayloads(_ context.Context, before time.Time, limit int) (int64, error) {
	s.notificationBefore, s.notificationLimit = before, limit
	return s.notificationCount, nil
}

func (s *recordingStore) CountExpiredAmbiguousInvitationReservations(_ context.Context, now time.Time, limit int) (int, bool, error) {
	s.invitationNow, s.invitationLimit = now, limit
	return s.ambiguous, s.more, nil
}

func TestRunOnceUsesOneClockSampleAndBoundedCutoffs(t *testing.T) {
	now := time.Date(2026, 8, 27, 17, 20, 0, 0, time.FixedZone("test", 8*60*60))
	repository := &recordingStore{
		orderCount: 1, grantCount: 2, recomputed: 1, idempotencyCount: 3,
		outboxCount: 4, notificationCount: 5, ambiguous: 6, more: true,
	}
	runner := New(repository, Options{Clock: func() time.Time { return now }})
	result, err := runner.RunOnce(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if result.OrdersClosed != 1 || result.GrantsExpired != 2 || result.EntitlementsRecomputed != 1 ||
		result.IdempotencyKeysPurged != 3 || result.ProcessedOutboxPurged != 4 ||
		result.PaymentNotificationPayloadsGone != 5 || result.AmbiguousInvitationReservations != 6 ||
		!result.AmbiguousInvitationMore {
		t.Fatalf("unexpected result: %+v", result)
	}
	wantNow := now.UTC()
	if repository.grantNow != wantNow || repository.invitationNow != wantNow || repository.idempotencyNow != wantNow {
		t.Fatalf("operations used inconsistent clock samples: grant=%s invitation=%s idempotency=%s", repository.grantNow, repository.invitationNow, repository.idempotencyNow)
	}
	if repository.outboxBefore != wantNow.Add(-DefaultOutboxRetention) || repository.notificationBefore != wantNow.Add(-DefaultPaymentNotificationRetention) {
		t.Fatalf("retention cutoffs outbox=%s notification=%s", repository.outboxBefore, repository.notificationBefore)
	}
	if repository.orderLimit != DefaultBatchSize || repository.grantLimit != DefaultBatchSize ||
		repository.invitationLimit != DefaultBatchSize || repository.idempotencyLimit != DefaultBatchSize ||
		repository.outboxLimit != DefaultBatchSize || repository.notificationLimit != DefaultBatchSize {
		t.Fatalf("operations were not bounded: %+v", repository)
	}
}

func TestRunOnceAttemptsIndependentOperationsAfterFailure(t *testing.T) {
	first := errors.New("orders unavailable")
	second := errors.New("idempotency unavailable")
	repository := &recordingStore{orderErr: first, idempotencyErr: second, outboxCount: 2}
	result, err := New(repository, Options{}).RunOnce(context.Background())
	if !errors.Is(err, first) || !errors.Is(err, second) {
		t.Fatalf("RunOnce error=%v", err)
	}
	if result.ProcessedOutboxPurged != 2 || repository.notificationLimit == 0 {
		t.Fatalf("later independent operations were skipped: result=%+v repository=%+v", result, repository)
	}
}

func TestNewBoundsOptionsAndNextDelay(t *testing.T) {
	repository := &recordingStore{}
	runner := New(repository, Options{
		Interval:      10 * time.Second,
		Jitter:        20 * time.Second,
		BatchSize:     MaxBatchSize + 1,
		RandomFloat64: func() float64 { return 1 },
	})
	if runner.jitter != 2500*time.Millisecond {
		t.Fatalf("oversized jitter=%s, want %s", runner.jitter, 2500*time.Millisecond)
	}
	if runner.batchSize != DefaultBatchSize {
		t.Fatalf("oversized batch=%d", runner.batchSize)
	}
	if delay := runner.nextDelay(); delay != 12500*time.Millisecond {
		t.Fatalf("next delay=%s", delay)
	}
}

func TestNewRejectsHotPollingInterval(t *testing.T) {
	runner := New(&recordingStore{}, Options{Interval: time.Nanosecond, DisableJitter: true})
	if runner.interval != DefaultInterval || runner.nextDelay() != DefaultInterval {
		t.Fatalf("interval=%s delay=%s", runner.interval, runner.nextDelay())
	}
}

func TestExplicitNonDefaultIntervalMayDisableJitter(t *testing.T) {
	runner := New(&recordingStore{}, Options{Interval: 20 * time.Second, DisableJitter: true})
	if runner.jitter != 0 || runner.nextDelay() != 20*time.Second {
		t.Fatalf("jitter=%s delay=%s", runner.jitter, runner.nextDelay())
	}
}

type blockingTimer struct {
	channel chan time.Time
	stopped bool
}

func (t *blockingTimer) Chan() <-chan time.Time { return t.channel }
func (t *blockingTimer) Stop() bool {
	t.stopped = true
	return true
}

func TestRunStopsTimerOnContextCancellation(t *testing.T) {
	runner := New(&recordingStore{}, Options{DisableJitter: true})
	created := make(chan *blockingTimer, 1)
	runner.newTimer = func(time.Duration) timer {
		item := &blockingTimer{channel: make(chan time.Time)}
		created <- item
		return item
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		runner.Run(ctx)
		close(done)
	}()
	item := <-created
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("runner did not stop after context cancellation")
	}
	if !item.stopped {
		t.Fatal("active timer was not stopped")
	}
}

func TestMaintenanceFailureLogDoesNotFormatError(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &sensitiveLogError{text: "postgres://admin:secret@database/termlink"}

	logMaintenanceFailure(logger, Result{OrdersClosed: 2, GrantsExpired: 3}, sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "secret") || strings.Contains(logged, "postgres://") {
		t.Fatalf("maintenance log formatted or exposed the error: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"commerce_maintenance_partial_failure"`) ||
		!strings.Contains(logged, `"orders_closed":2`) || !strings.Contains(logged, `"grants_expired":3`) {
		t.Fatalf("maintenance log is missing stable fields: %s", logged)
	}
}

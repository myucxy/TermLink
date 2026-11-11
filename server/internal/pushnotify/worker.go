package pushnotify

import (
	"context"
	"time"

	"github.com/google/uuid"
)

const (
	batchLimit  = 10
	leasePeriod = 5 * time.Minute
	finishLimit = 5 * time.Second
	pollPeriod  = 5 * time.Second
)

type Worker struct {
	queue  Queue
	sender Sender
	codec  *Codec
	now    func() time.Time
}

func NewWorker(queue Queue, sender Sender, codec *Codec) (*Worker, error) {
	if queue == nil || sender == nil || codec == nil || codec.aead == nil {
		return nil, ErrConfiguration
	}
	return &Worker{queue: queue, sender: sender, codec: codec, now: time.Now}, nil
}

// Run keeps transient queue/provider failures retryable. It does not log raw
// repository errors, Delivery objects or provider errors. Cancellation drains
// only bounded Finish calls for already claimed IDs, then returns ctx.Err().
func (w *Worker) Run(ctx context.Context) error {
	if w == nil || ctx == nil {
		return ErrConfiguration
	}
	for {
		_ = w.RunOnce(ctx)
		if ctx.Err() != nil {
			return ctx.Err()
		}
		timer := time.NewTimer(pollPeriod)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
	}
}

func (w *Worker) RunOnce(ctx context.Context) error {
	if w == nil || ctx == nil {
		return ErrConfiguration
	}
	if ctx.Err() != nil {
		return ctx.Err()
	}
	lease := uuid.New()
	started := w.now()
	ids, err := w.queue.ClaimPushNotifications(ctx, lease, batchLimit, leasePeriod)
	if err != nil {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		return ErrQueue
	}
	// A repository bug must not turn one lease into unbounded work. Excess IDs
	// stay untouched and recover through lease expiry in the authoritative queue.
	if len(ids) > batchLimit {
		return ErrQueue
	}
	seen := make(map[uuid.UUID]bool, len(ids))
	failed := false
	deadline, cancel := context.WithDeadline(ctx, started.Add(leasePeriod))
	defer cancel()
	for _, id := range ids {
		if id == uuid.Nil || seen[id] {
			failed = true
			continue
		}
		seen[id] = true
		result := Result{Status: Retry, Code: "PUSH_CANCELLED", RetryAfter: time.Minute}
		if deadline.Err() == nil {
			var settle bool
			result, settle = w.process(deadline, id, lease)
			if !settle {
				continue // Prepare atomically canceled or superseded this event.
			}
		}
		// Even a canceled network call must release/retry its owned lease. A
		// failed Finish leaves the existing lease for expiration, never ACKs it.
		finish, stop := context.WithTimeout(context.WithoutCancel(ctx), finishLimit)
		if err := w.queue.FinishPushNotification(finish, id, lease, result); err != nil {
			failed = true
		}
		stop()
	}
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if failed {
		return ErrQueue
	}
	return nil
}

func (w *Worker) process(ctx context.Context, id, lease uuid.UUID) (Result, bool) {
	delivery, err := w.queue.PreparePushNotification(ctx, id, lease)
	if err != nil {
		return Result{Status: Retry, Code: "PUSH_PREPARE_UNAVAILABLE", RetryAfter: time.Minute}, true
	}
	if delivery == nil {
		return Result{}, false
	}
	if delivery.ID != id || delivery.RegistrationID == uuid.Nil || delivery.Generation == uuid.Nil {
		return Result{Status: Permanent, Code: "PUSH_DELIVERY_INVALID"}, true
	}
	if !delivery.ExpiresAt.After(w.now()) {
		return Result{Status: Permanent, Code: "PUSH_EXPIRED"}, true
	}
	if ctx.Err() != nil {
		return Result{Status: Retry, Code: "PUSH_CANCELLED", RetryAfter: retryBackoff(delivery.Attempt)}, true
	}
	token, err := w.codec.Open(delivery.TokenCipher, delivery.AccountID, delivery.DeviceID, delivery.Topic, delivery.Environment)
	if err != nil {
		return Result{Status: Permanent, Code: "PUSH_TOKEN_CIPHER_INVALID"}, true
	}
	sendContext, cancel := context.WithDeadline(ctx, delivery.ExpiresAt)
	defer cancel()
	result := safeResult(w.sender.Send(sendContext, Message{
		SoundEnabled: delivery.SoundEnabled, ID: id, PCID: delivery.PCID, SessionID: delivery.SessionID, Token: token,
		Topic: delivery.Topic, Environment: delivery.Environment, Kind: delivery.Kind, ExpiresAt: delivery.ExpiresAt,
	}))
	token = ""
	if result.Status == Retry {
		if !delivery.ExpiresAt.After(w.now()) {
			return Result{Status: Permanent, Code: "PUSH_EXPIRED"}, true
		}
		if backoff := retryBackoff(delivery.Attempt); result.RetryAfter < backoff {
			result.RetryAfter = backoff
		}
		if result.RetryAfter > maximumRetry {
			result.RetryAfter = maximumRetry
		}
	}
	return result, true
}

func retryBackoff(attempt int) time.Duration {
	if attempt < 1 {
		attempt = 1
	}
	if attempt > 9 {
		return time.Hour
	}
	delay := 15 * time.Second * time.Duration(1<<uint(attempt-1))
	if delay > time.Hour {
		return time.Hour
	}
	return delay
}

// Sender is an interface; keep queue diagnostics safe even with a buggy sender.
func safeResult(result Result) Result {
	if result.Status != Sent && result.Status != Retry && result.Status != InvalidToken && result.Status != Permanent {
		return Result{Status: Retry, Code: "APNS_RESPONSE_INVALID", RetryAfter: 15 * time.Minute}
	}
	switch result.Code {
	case "APNS_SENT", "APNS_UNAVAILABLE", "APNS_AUTH_UNAVAILABLE", "APNS_PROTOCOL_INVALID", "APNS_RESPONSE_INVALID",
		"APNS_SERVER_ERROR", "APNS_RATE_LIMITED", "APNS_UNREGISTERED", "APNS_BAD_DEVICE_TOKEN",
		"APNS_PROVIDER_TOKEN_EXPIRED", "APNS_REQUEST_REJECTED", "PUSH_CONFIGURATION_INVALID", "PUSH_CANCELLED",
		"PUSH_EXPIRED", "PUSH_MESSAGE_INVALID":
	default:
		return Result{Status: Retry, Code: "APNS_RESPONSE_INVALID", RetryAfter: 15 * time.Minute}
	}
	if result.Status != InvalidToken {
		result.InvalidatedAt = nil
	}
	return result
}

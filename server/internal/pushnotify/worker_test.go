package pushnotify

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

type recordedFinish struct {
	id, lease uuid.UUID
	result    Result
	canceled  bool
}
type queueFixture struct {
	ids                                   []uuid.UUID
	deliveries                            map[uuid.UUID]*Delivery
	lease                                 uuid.UUID
	limit                                 int
	duration                              time.Duration
	prepared                              []uuid.UUID
	finished                              []recordedFinish
	claimError, prepareError, finishError error
	onPrepare                             func(uuid.UUID)
	claimCalls                            int
}

func (q *queueFixture) ClaimPushNotifications(ctx context.Context, lease uuid.UUID, limit int, duration time.Duration) ([]uuid.UUID, error) {
	q.claimCalls++
	q.lease, q.limit, q.duration = lease, limit, duration
	return q.ids, q.claimError
}
func (q *queueFixture) PreparePushNotification(ctx context.Context, id, lease uuid.UUID) (*Delivery, error) {
	if lease != q.lease {
		return nil, errors.New("wrong lease")
	}
	q.prepared = append(q.prepared, id)
	if q.onPrepare != nil {
		q.onPrepare(id)
	}
	return q.deliveries[id], q.prepareError
}
func (q *queueFixture) FinishPushNotification(ctx context.Context, id, lease uuid.UUID, result Result) error {
	q.finished = append(q.finished, recordedFinish{id, lease, result, ctx.Err() != nil})
	return q.finishError
}

type senderFunc func(context.Context, Message) Result

func (s senderFunc) Send(ctx context.Context, message Message) Result { return s(ctx, message) }
func workerDelivery(t *testing.T, codec *Codec, now time.Time) *Delivery {
	t.Helper()
	id, account, device := uuid.New(), uuid.New(), uuid.New()
	sealed, err := codec.Seal(strings.Repeat("ab", 32), account, device, "dev.termlink.ios", EnvironmentSandbox)
	if err != nil {
		t.Fatal(err)
	}
	return &Delivery{ID: id, RegistrationID: uuid.New(), Generation: uuid.New(), AccountID: account, DeviceID: device, PCID: uuid.New(), SessionID: uuid.New(), TokenCipher: sealed, Topic: "dev.termlink.ios", Environment: EnvironmentSandbox, Kind: KindAINeedsInput, ExpiresAt: now.Add(time.Hour), Attempt: 1}
}

func TestWorkerPreparesAuthorityBeforeDecryptingAndFinishesItsLease(t *testing.T) {
	codec, _ := New(bytes.Repeat([]byte{1}, 32))
	now := time.Now()
	delivery := workerDelivery(t, codec, now)
	revoked := uuid.New()
	queue := &queueFixture{ids: []uuid.UUID{revoked, delivery.ID}, deliveries: map[uuid.UUID]*Delivery{delivery.ID: delivery}}
	calls := 0
	worker, err := NewWorker(queue, senderFunc(func(ctx context.Context, message Message) Result {
		calls++
		if len(queue.prepared) != 2 || queue.prepared[1] != delivery.ID {
			t.Fatal("send preceded authority preparation")
		}
		if message.Token != strings.Repeat("ab", 32) || message.ID != delivery.ID || message.PCID != delivery.PCID || message.SessionID != delivery.SessionID || message.Kind != delivery.Kind {
			t.Fatal("prepared message identity changed")
		}
		if deadline, ok := ctx.Deadline(); !ok || deadline.After(now.Add(leasePeriod)) {
			t.Fatal("send not bounded by lease/expiry")
		}
		return Result{Status: Sent, Code: "APNS_SENT"}
	}), codec)
	if err != nil {
		t.Fatal(err)
	}
	worker.now = func() time.Time { return now }
	if err := worker.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if calls != 1 || queue.lease == uuid.Nil || queue.limit != 10 || queue.duration != 5*time.Minute || len(queue.finished) != 1 || queue.finished[0].id != delivery.ID || queue.finished[0].lease != queue.lease || queue.finished[0].result.Status != Sent {
		t.Fatal("revoked/nil preparation sent/decrypted or settled another lease")
	}
}

func TestWorkerRejectsExpiredMismatchedOrCorruptCipherWithoutSend(t *testing.T) {
	codec, _ := New(make([]byte, 32))
	now := time.Now()
	for _, mode := range []string{"expired", "cipher", "aad-device", "aad-account", "topic", "environment", "event-id", "generation"} {
		t.Run(mode, func(t *testing.T) {
			delivery := workerDelivery(t, codec, now)
			id := delivery.ID
			switch mode {
			case "expired":
				delivery.ExpiresAt = now.Add(-time.Second)
			case "cipher":
				delivery.TokenCipher[len(delivery.TokenCipher)-1] ^= 1
			case "aad-device":
				delivery.DeviceID = uuid.New()
			case "aad-account":
				delivery.AccountID = uuid.New()
			case "topic":
				delivery.Topic = "dev.other"
			case "environment":
				delivery.Environment = EnvironmentProduction
			case "event-id":
				delivery.ID = uuid.New()
			case "generation":
				delivery.Generation = uuid.Nil
			}
			queue := &queueFixture{ids: []uuid.UUID{id}, deliveries: map[uuid.UUID]*Delivery{id: delivery}}
			worker, _ := NewWorker(queue, senderFunc(func(context.Context, Message) Result {
				t.Fatal("unauthorized/expired/corrupt token reached sender")
				return Result{}
			}), codec)
			worker.now = func() time.Time { return now }
			if err := worker.RunOnce(context.Background()); err != nil || len(queue.finished) != 1 || queue.finished[0].result.Status != Permanent {
				t.Fatalf("invalid delivery not settled: %v", err)
			}
		})
	}
}

func TestWorkerBackoffPreservesProviderDelayAndInvalidationTimestamp(t *testing.T) {
	codec, _ := New(make([]byte, 32))
	now := time.Now()
	invalidated := now.Add(-time.Hour)
	for _, test := range []struct {
		attempt int
		input   Result
		status  Status
		minimum time.Duration
	}{
		{1, Result{Status: Retry, Code: "APNS_UNAVAILABLE"}, Retry, 15 * time.Second},
		{5, Result{Status: Retry, Code: "APNS_UNAVAILABLE"}, Retry, 4 * time.Minute},
		{1000, Result{Status: Retry, Code: "APNS_UNAVAILABLE"}, Retry, time.Hour},
		{2, Result{Status: Retry, Code: "APNS_SERVER_ERROR", RetryAfter: 15 * time.Minute}, Retry, 15 * time.Minute},
		{2, Result{Status: Retry, Code: "APNS_RATE_LIMITED", RetryAfter: 2 * time.Hour}, Retry, 2 * time.Hour},
		{2, Result{Status: InvalidToken, Code: "APNS_UNREGISTERED", InvalidatedAt: &invalidated}, InvalidToken, 0},
		{2, Result{Status: "unknown", Code: "secret-token-https://url"}, Retry, 15 * time.Minute},
	} {
		delivery := workerDelivery(t, codec, now)
		delivery.Attempt = test.attempt
		queue := &queueFixture{ids: []uuid.UUID{delivery.ID}, deliveries: map[uuid.UUID]*Delivery{delivery.ID: delivery}}
		worker, _ := NewWorker(queue, senderFunc(func(context.Context, Message) Result { return test.input }), codec)
		if err := worker.RunOnce(context.Background()); err != nil {
			t.Fatal(err)
		}
		result := queue.finished[0].result
		if result.Status != test.status || result.RetryAfter < test.minimum || result.RetryAfter > maximumRetry || strings.Contains(result.Code, "secret") {
			t.Fatalf("retry policy incorrect: %+v", result)
		}
		if test.input.InvalidatedAt != nil && (result.InvalidatedAt == nil || !result.InvalidatedAt.Equal(invalidated)) {
			t.Fatal("410 timestamp lost before store generation check")
		}
	}
}

func TestWorkerCancellationSafelyReleasesClaimedLeases(t *testing.T) {
	for _, phase := range []string{"before-prepare", "during-send"} {
		t.Run(phase, func(t *testing.T) {
			codec, _ := New(make([]byte, 32))
			now := time.Now()
			delivery := workerDelivery(t, codec, now)
			second := workerDelivery(t, codec, now)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			queue := &queueFixture{ids: []uuid.UUID{delivery.ID, second.ID}, deliveries: map[uuid.UUID]*Delivery{delivery.ID: delivery, second.ID: second}}
			calls := 0
			if phase == "before-prepare" {
				queue.onPrepare = func(uuid.UUID) { cancel() }
			}
			worker, _ := NewWorker(queue, senderFunc(func(ctx context.Context, message Message) Result {
				calls++
				cancel()
				<-ctx.Done()
				return Result{Status: Retry, Code: "PUSH_CANCELLED"}
			}), codec)
			if err := worker.RunOnce(ctx); !errors.Is(err, context.Canceled) {
				t.Fatalf("canceled run error %v", err)
			}
			if len(queue.finished) != 2 {
				t.Fatal("cancellation stranded a claimed lease")
			}
			for _, finished := range queue.finished {
				if finished.canceled || finished.result.Status != Retry || finished.lease != queue.lease {
					t.Fatal("cancellation ACKed/lost a pending event or canceled Finish")
				}
			}
			if phase == "before-prepare" && calls != 0 || phase == "during-send" && calls != 1 {
				t.Fatal("cancellation did not stop remaining sends")
			}
		})
	}
	codec, _ := New(make([]byte, 32))
	queue := &queueFixture{}
	worker, _ := NewWorker(queue, senderFunc(func(context.Context, Message) Result { t.Fatal("cancelled Run called sender"); return Result{} }), codec)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := worker.Run(ctx); !errors.Is(err, context.Canceled) || queue.claimCalls != 0 {
		t.Fatal("Run ignored cancellation")
	}
}

func TestWorkerExpiryDuringSendAndQueueErrorsRemainBounded(t *testing.T) {
	codec, _ := New(make([]byte, 32))
	delivery := workerDelivery(t, codec, time.Now())
	delivery.ExpiresAt = time.Now().Add(30 * time.Millisecond)
	queue := &queueFixture{ids: []uuid.UUID{delivery.ID}, deliveries: map[uuid.UUID]*Delivery{delivery.ID: delivery}}
	worker, _ := NewWorker(queue, senderFunc(func(ctx context.Context, _ Message) Result {
		<-ctx.Done()
		return Result{Status: Retry, Code: "APNS_UNAVAILABLE"}
	}), codec)
	if err := worker.RunOnce(context.Background()); err != nil || queue.finished[0].result.Code != "PUSH_EXPIRED" {
		t.Fatal("expired event kept retrying")
	}
	for _, mode := range []string{"claim", "prepare", "finish"} {
		delivery := workerDelivery(t, codec, time.Now())
		queue := &queueFixture{ids: []uuid.UUID{delivery.ID}, deliveries: map[uuid.UUID]*Delivery{delivery.ID: delivery}}
		secret := errors.New("postgres://user:secret@db device-token")
		switch mode {
		case "claim":
			queue.claimError = secret
		case "prepare":
			queue.prepareError = secret
		case "finish":
			queue.finishError = secret
		}
		worker, _ := NewWorker(queue, senderFunc(func(context.Context, Message) Result { return Result{Status: Sent, Code: "APNS_SENT"} }), codec)
		err := worker.RunOnce(context.Background())
		if mode == "prepare" {
			if err != nil || queue.finished[0].result.Status != Retry || queue.finished[0].result.Code != "PUSH_PREPARE_UNAVAILABLE" {
				t.Fatal("Prepare failure did not retry")
			}
		} else if !errors.Is(err, ErrQueue) || strings.Contains(err.Error(), "secret") {
			t.Fatal("queue failure ignored/leaked")
		}
	}
}

func TestUnknownInvalidationCannotDisableRegistration(t *testing.T) {
	result := safeResult(Result{Status: InvalidToken, Code: "untrusted_raw_reason"})
	if result.Status != Retry || result.InvalidatedAt != nil {
		t.Fatal("unknown sender diagnostics invalidated a registration")
	}
}

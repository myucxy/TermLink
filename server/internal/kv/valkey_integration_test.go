package kv

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

func TestAcquireSharesLeaseAcrossDevicesAtomically(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey lease test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	holderID := uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", leaseKey+":participants").Err()
		_ = valkey.Close()
	})

	initial, err := valkey.Acquire(ctx, sessionID, holderID, 900*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(300 * time.Millisecond)
	resumeStarted := time.Now().UTC()
	resumed, err := valkey.Acquire(ctx, sessionID, holderID, 1200*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	if resumed.ID != initial.ID || resumed.Version != initial.Version {
		t.Fatalf("resumed lease (id=%s version=%d), want original (id=%s version=%d)", resumed.ID, resumed.Version, initial.ID, initial.Version)
	}
	if resumed.HolderDeviceID != holderID {
		t.Fatalf("resumed holder = %s, want %s", resumed.HolderDeviceID, holderID)
	}
	if resumed.ExpiresAt.Before(resumeStarted.Add(1100 * time.Millisecond)) {
		t.Fatalf("resumed expiry = %s, want approximately a full duration after %s", resumed.ExpiresAt, resumeStarted)
	}
	ttl, err := valkey.Client.PTTL(ctx, leaseKey).Result()
	if err != nil {
		t.Fatal(err)
	}
	if ttl < 1050*time.Millisecond || ttl > 1200*time.Millisecond {
		t.Fatalf("resumed TTL = %s, want full 1.2s duration", ttl)
	}

	secondHolderID := uuid.New()
	shared, err := valkey.Acquire(ctx, sessionID, secondHolderID, time.Second)
	if err != nil {
		t.Fatalf("second device join: %v", err)
	}
	if shared.ID != initial.ID || shared.Version != initial.Version || shared.HolderDeviceID != holderID {
		t.Fatalf("shared lease = %+v, want first holder and fence %+v", shared, initial)
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if exists, existsErr := valkey.Client.Exists(ctx, leaseKey).Result(); existsErr != nil {
			t.Fatal(existsErr)
		} else if exists == 0 {
			break
		}
		time.Sleep(25 * time.Millisecond)
	}
	if exists, err := valkey.Client.Exists(ctx, leaseKey).Result(); err != nil || exists != 0 {
		t.Fatalf("resumed lease did not expire: exists=%d err=%v", exists, err)
	}
	replacement, err := valkey.Acquire(ctx, sessionID, uuid.New(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if replacement.Version != initial.Version+1 {
		t.Fatalf("replacement version = %d, want %d", replacement.Version, initial.Version+1)
	}
	if replacement.ID == initial.ID {
		t.Fatal("replacement lease reused the expired lease ID")
	}
}

func TestAcquireConcurrentContendersCreateOneLease(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey concurrency test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", leaseKey+":participants").Err()
		_ = valkey.Close()
	})

	const contenderCount = 24
	start := make(chan struct{})
	results := make(chan struct {
		lease Lease
		err   error
	}, contenderCount)
	var waitGroup sync.WaitGroup
	for i := 0; i < contenderCount; i++ {
		waitGroup.Add(1)
		go func() {
			defer waitGroup.Done()
			<-start
			lease, acquireErr := valkey.Acquire(ctx, sessionID, uuid.New(), 5*time.Second)
			results <- struct {
				lease Lease
				err   error
			}{lease: lease, err: acquireErr}
		}()
	}
	close(start)
	waitGroup.Wait()
	close(results)

	var shared Lease
	successes := 0
	for result := range results {
		if result.err != nil {
			t.Fatalf("unexpected acquire error: %v", result.err)
		}
		if successes == 0 {
			shared = result.lease
		} else if result.lease.ID != shared.ID || result.lease.Version != shared.Version || result.lease.HolderDeviceID != shared.HolderDeviceID {
			t.Fatalf("concurrent acquire returned a different shared fence: first=%+v got=%+v", shared, result.lease)
		}
		successes++
	}
	if successes != contenderCount {
		t.Fatalf("concurrent successes=%d, want %d", successes, contenderCount)
	}
	if shared.Version != 1 {
		t.Fatalf("shared version = %d, want 1", shared.Version)
	}
	version, err := valkey.Client.Get(ctx, leaseKey+":version").Int64()
	if err != nil {
		t.Fatal(err)
	}
	if version != shared.Version {
		t.Fatalf("stored version counter = %d, want %d; joins must not consume versions", version, shared.Version)
	}
}

func TestReleaseForDeviceRetainsSharedLeaseUntilLastParticipant(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey shared release test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	firstDevice, secondDevice := uuid.New(), uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", leaseKey+":participants").Err()
		_ = valkey.Close()
	})

	lease, err := valkey.Acquire(ctx, sessionID, firstDevice, 5*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	joined, err := valkey.Acquire(ctx, sessionID, secondDevice, 5*time.Second)
	if err != nil || joined.ID != lease.ID || joined.Version != lease.Version {
		t.Fatalf("join lease=%+v error=%v, want fence %+v", joined, err, lease)
	}
	accepted, released, err := valkey.ReleaseForDevice(ctx, lease, firstDevice)
	if err != nil || !accepted || released {
		t.Fatalf("first release accepted/released/error=%v/%v/%v, want true/false/nil", accepted, released, err)
	}
	remaining, err := valkey.GetLease(ctx, sessionID)
	if err != nil || remaining.ID != lease.ID || remaining.Version != lease.Version {
		t.Fatalf("shared lease after first release=%+v error=%v", remaining, err)
	}
	if _, err := valkey.RenewForDevice(ctx, remaining, secondDevice, 5*time.Second); err != nil {
		t.Fatalf("remaining device renew: %v", err)
	}
	accepted, released, err = valkey.ReleaseForDevice(ctx, remaining, secondDevice)
	if err != nil || !accepted || !released {
		t.Fatalf("last release accepted/released/error=%v/%v/%v, want true/true/nil", accepted, released, err)
	}
	if _, err := valkey.GetLease(ctx, sessionID); !errors.Is(err, redis.Nil) {
		t.Fatalf("lease after final release error=%v, want redis.Nil", err)
	}
}

func TestReleaseForDevicePrunesExpiredParticipants(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey participant TTL test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	firstDevice, secondDevice := uuid.New(), uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", leaseKey+":participants").Err()
		_ = valkey.Close()
	})

	lease, err := valkey.Acquire(ctx, sessionID, firstDevice, 400*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := valkey.Acquire(ctx, sessionID, secondDevice, 2*time.Second); err != nil {
		t.Fatal(err)
	}
	time.Sleep(600 * time.Millisecond)
	accepted, released, err := valkey.ReleaseForDevice(ctx, lease, secondDevice)
	if err != nil || !accepted || !released {
		t.Fatalf("release after peer participant TTL accepted/released/error=%v/%v/%v", accepted, released, err)
	}
}

func TestAcquireMigratesLegacyLeaseParticipantsWithoutChangingFence(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey lease migration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	legacyHolder, joiningDevice, leaseID := uuid.New(), uuid.New(), uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", leaseKey+":participants").Err()
		_ = valkey.Close()
	})
	legacyValue := legacyHolder.String() + "|" + leaseID.String() + "|7"
	if err := valkey.Client.Set(ctx, leaseKey, legacyValue, 5*time.Second).Err(); err != nil {
		t.Fatal(err)
	}

	shared, err := valkey.Acquire(ctx, sessionID, joiningDevice, 5*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if shared.HolderDeviceID != legacyHolder || shared.ID != leaseID || shared.Version != 7 {
		t.Fatalf("legacy join changed fence: got %+v", shared)
	}
	accepted, released, err := valkey.ReleaseForDevice(ctx, shared, joiningDevice)
	if err != nil || !accepted || released {
		t.Fatalf("joining device release accepted/released/error=%v/%v/%v, want legacy holder retained", accepted, released, err)
	}
	accepted, released, err = valkey.ReleaseForDevice(ctx, shared, legacyHolder)
	if err != nil || !accepted || !released {
		t.Fatalf("legacy holder release accepted/released/error=%v/%v/%v", accepted, released, err)
	}
}

// TestReserveLiveSessionDoesNotApplyLegacyQuota verifies that the sorted-set
// lease registry accepts more subscriptions than the former plan limit. The
// optional legacy limit argument is deliberately ignored for compatibility.
func TestReserveLiveSessionDoesNotApplyLegacyQuota(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey quota test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	userID := uuid.New()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, "live-sessions:"+userID.String()).Err()
		_ = valkey.Close()
	})
	for i := 0; i < 5; i++ {
		reserved, reserveErr := valkey.ReserveLiveSession(ctx, userID, uuid.New(), uuid.New(), 1)
		if reserveErr != nil || !reserved {
			t.Fatalf("reservation %d = (%v, %v), want success beyond legacy limit", i+1, reserved, reserveErr)
		}
	}
}

func TestReserveLiveSessionsBatchesOneDeviceRegistryUpdate(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey batch reservation test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	userID, deviceID := uuid.New(), uuid.New()
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New(), uuid.New()}
	key := "live-sessions:" + userID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, key).Err()
		_ = valkey.Close()
	})

	if err := valkey.ReserveLiveSessions(ctx, userID, sessionIDs, deviceID); err != nil {
		t.Fatal(err)
	}
	if count, err := valkey.Client.ZCard(ctx, key).Result(); err != nil || count != int64(len(sessionIDs)) {
		t.Fatalf("batch registry count/error = %d/%v, want %d/nil", count, err, len(sessionIDs))
	}
	for _, sessionID := range sessionIDs {
		score, err := valkey.Client.ZScore(ctx, key, sessionID.String()+":"+deviceID.String()).Result()
		if err != nil || score <= float64(time.Now().UnixMilli()) {
			t.Fatalf("batch member %s score/error = %f/%v", sessionID, score, err)
		}
	}
}

func TestReserveLiveSessionsRejectsUnboundedOrInvalidBatchBeforeValkey(t *testing.T) {
	valkey := &Valkey{}
	tooMany := make([]uuid.UUID, maxLiveSessionReservationBatch+1)
	for index := range tooMany {
		tooMany[index] = uuid.New()
	}
	if err := valkey.ReserveLiveSessions(context.Background(), uuid.New(), tooMany, uuid.New()); err == nil {
		t.Fatal("unbounded batch was accepted")
	}
	if err := valkey.ReserveLiveSessions(context.Background(), uuid.New(), []uuid.UUID{uuid.Nil}, uuid.New()); err == nil {
		t.Fatal("nil session ID was accepted")
	}
}

// TestForceReleasePreservesReplacementLease uses a real Valkey instance. The
// hook makes the historical GET/PTTL/DEL implementation fail deterministically:
// after it has read the old lease, another actor releases that lease and
// acquires a replacement before the stale force-release is allowed to proceed.
// The production Lua implementation performs no client-visible GET and removes
// the old value atomically, so the replacement remains present.
func TestForceReleasePreservesReplacementLease(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the real Valkey race test")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}

	sessionID := uuid.New()
	leaseKey := "lease:" + sessionID.String()
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version").Err()
		_ = valkey.Close()
	})

	oldLease, err := valkey.Acquire(ctx, sessionID, uuid.New(), 30*time.Second)
	if err != nil {
		t.Fatal(err)
	}

	hook := newBlockingGetHook(leaseKey)
	valkey.Client.AddHook(hook)
	type forceResult struct {
		lease    Lease
		released bool
		err      error
	}
	resultChannel := make(chan forceResult, 1)
	go func() {
		lease, released, forceErr := valkey.ForceRelease(ctx, sessionID)
		resultChannel <- forceResult{lease: lease, released: released, err: forceErr}
	}()

	var replacement Lease
	var result forceResult
	select {
	case result = <-resultChannel:
		// The atomic Lua implementation completes without issuing a standalone
		// GET. Disarm the detector before verifying the replacement below.
		hook.disarm()
		replacement, err = valkey.Acquire(ctx, sessionID, uuid.New(), 30*time.Second)
		if err != nil {
			t.Fatal(err)
		}
	case <-hook.oldValueRead:
		// This branch is reached by the former split GET/PTTL/DEL algorithm.
		// Arrange the exact replacement race before allowing it to continue.
		released, releaseErr := valkey.Release(ctx, oldLease)
		if releaseErr != nil {
			t.Fatal(releaseErr)
		}
		if !released {
			t.Fatal("concurrent actor did not release the old lease")
		}
		replacement, err = valkey.Acquire(ctx, sessionID, uuid.New(), 30*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		hook.unblock()
		select {
		case result = <-resultChannel:
		case <-ctx.Done():
			t.Fatal("force release did not finish after the race was unblocked")
		}
	case <-ctx.Done():
		t.Fatal("force release neither completed nor read the old lease")
	}

	if result.err != nil {
		t.Fatal(result.err)
	}
	if !result.released {
		t.Fatal("force release did not report the old lease as released")
	}
	if result.lease.ID != oldLease.ID || result.lease.HolderDeviceID != oldLease.HolderDeviceID || result.lease.Version != oldLease.Version {
		t.Fatalf("force release returned lease %+v, want old lease %+v", result.lease, oldLease)
	}

	stored, err := valkey.GetLease(ctx, sessionID)
	if err != nil {
		t.Fatalf("replacement lease was removed: %v", err)
	}
	if stored.ID != replacement.ID || stored.HolderDeviceID != replacement.HolderDeviceID || stored.Version != replacement.Version {
		t.Fatalf("stored lease %+v, want replacement %+v", stored, replacement)
	}
}

type blockingGetHook struct {
	key          string
	armed        atomic.Bool
	triggered    atomic.Bool
	oldValueRead chan struct{}
	continueRead chan struct{}
}

func newBlockingGetHook(key string) *blockingGetHook {
	hook := &blockingGetHook{
		key:          key,
		oldValueRead: make(chan struct{}),
		continueRead: make(chan struct{}),
	}
	hook.armed.Store(true)
	return hook
}

func (h *blockingGetHook) disarm() { h.armed.Store(false) }

func (h *blockingGetHook) unblock() {
	h.disarm()
	close(h.continueRead)
}

func (h *blockingGetHook) DialHook(next redis.DialHook) redis.DialHook { return next }

func (h *blockingGetHook) ProcessHook(next redis.ProcessHook) redis.ProcessHook {
	return func(ctx context.Context, command redis.Cmder) error {
		err := next(ctx, command)
		if err != nil || !h.armed.Load() || command.Name() != "get" || len(command.Args()) < 2 || fmt.Sprint(command.Args()[1]) != h.key {
			return err
		}
		if !h.triggered.CompareAndSwap(false, true) {
			return err
		}
		close(h.oldValueRead)
		select {
		case <-h.continueRead:
			return err
		case <-ctx.Done():
			return ctx.Err()
		}
	}
}

func (h *blockingGetHook) ProcessPipelineHook(next redis.ProcessPipelineHook) redis.ProcessPipelineHook {
	return next
}

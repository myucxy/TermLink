package kv

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

type memoryVersionSource struct{ next atomic.Int64 }

func (s *memoryVersionSource) NextControlLeaseVersion(context.Context) (int64, error) {
	return s.next.Add(1), nil
}

func newMemoryCoordinatorForTest(t *testing.T) (*MemoryCoordinator, *memoryVersionSource, *time.Time) {
	t.Helper()
	versions := &memoryVersionSource{}
	versions.next.Store(281474976710656)
	coordinator, err := NewMemoryCoordinator(versions)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC().Truncate(time.Millisecond)
	coordinator.clock = func() time.Time { return now }
	t.Cleanup(func() { _ = coordinator.Close() })
	return coordinator, versions, &now
}

func TestMemoryCoordinatorSharedLeaseLifecycleAndExpiry(t *testing.T) {
	coordinator, _, now := newMemoryCoordinatorForTest(t)
	ctx := context.Background()
	sessionID, firstID, secondID := uuid.New(), uuid.New(), uuid.New()
	first, err := coordinator.Acquire(ctx, sessionID, firstID, 30*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	second, err := coordinator.Acquire(ctx, sessionID, secondID, time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	if first.ID != second.ID || first.Version != second.Version || second.HolderDeviceID != firstID {
		t.Fatalf("shared lease changed its fence: first=%+v second=%+v", first, second)
	}
	accepted, released, err := coordinator.ReleaseForDevice(ctx, second, firstID)
	if err != nil || !accepted || released {
		t.Fatalf("first release accepted=%t released=%t err=%v", accepted, released, err)
	}
	stored, err := coordinator.GetLease(ctx, sessionID)
	if err != nil || stored.ID != first.ID {
		t.Fatalf("remaining participant lease=%+v err=%v", stored, err)
	}
	*now = now.Add(61 * time.Second)
	if _, err := coordinator.GetLease(ctx, sessionID); !errors.Is(err, redis.Nil) {
		t.Fatalf("expired lease error=%v, want redis.Nil", err)
	}
}

func TestMemoryCoordinatorLeaseFencesSurviveCoordinatorRestart(t *testing.T) {
	versions := &memoryVersionSource{}
	versions.next.Store(281474976710656)
	firstCoordinator, _ := NewMemoryCoordinator(versions)
	first, err := firstCoordinator.Acquire(context.Background(), uuid.New(), uuid.New(), time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	_ = firstCoordinator.Close()
	secondCoordinator, _ := NewMemoryCoordinator(versions)
	defer secondCoordinator.Close()
	second, err := secondCoordinator.Acquire(context.Background(), uuid.New(), uuid.New(), time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	if second.Version <= first.Version {
		t.Fatalf("restart fence regressed: first=%d second=%d", first.Version, second.Version)
	}
}

func TestMemoryCoordinatorConcurrentAcquireUsesOneFence(t *testing.T) {
	coordinator, _, _ := newMemoryCoordinatorForTest(t)
	sessionID := uuid.New()
	const participants = 64
	leases := make(chan Lease, participants)
	errorsFound := make(chan error, participants)
	var start sync.WaitGroup
	start.Add(1)
	var workers sync.WaitGroup
	for range participants {
		workers.Add(1)
		go func() {
			defer workers.Done()
			start.Wait()
			lease, err := coordinator.Acquire(context.Background(), sessionID, uuid.New(), time.Minute)
			if err != nil {
				errorsFound <- err
				return
			}
			leases <- lease
		}()
	}
	start.Done()
	workers.Wait()
	close(leases)
	close(errorsFound)
	for err := range errorsFound {
		t.Fatalf("concurrent acquire failed: %v", err)
	}
	var expected Lease
	count := 0
	for lease := range leases {
		if count == 0 {
			expected = lease
		} else if lease.ID != expected.ID || lease.Version != expected.Version || lease.HolderDeviceID != expected.HolderDeviceID {
			t.Fatalf("concurrent acquire returned distinct fence: first=%+v current=%+v", expected, lease)
		}
		count++
	}
	if count != participants {
		t.Fatalf("leases=%d, want %d", count, participants)
	}
}

func TestMemoryCoordinatorForceReleaseOperationDoesNotDeleteReplacement(t *testing.T) {
	coordinator, _, _ := newMemoryCoordinatorForTest(t)
	ctx := context.Background()
	sessionID, userID := uuid.New(), uuid.New()
	original, _ := coordinator.Acquire(ctx, sessionID, uuid.New(), time.Minute)
	released, ok, err := coordinator.ForceReleaseForOperation(ctx, sessionID, userID, "operation-1")
	if err != nil || !ok || released.ID != original.ID {
		t.Fatalf("first release lease=%+v ok=%t err=%v", released, ok, err)
	}
	replacement, _ := coordinator.Acquire(ctx, sessionID, uuid.New(), time.Minute)
	replayed, ok, err := coordinator.ForceReleaseForOperation(ctx, sessionID, userID, "operation-1")
	if err != nil || !ok || replayed.ID != original.ID {
		t.Fatalf("replayed release lease=%+v ok=%t err=%v", replayed, ok, err)
	}
	stored, err := coordinator.GetLease(ctx, sessionID)
	if err != nil || stored.ID != replacement.ID {
		t.Fatalf("replacement was removed: stored=%+v err=%v", stored, err)
	}
}

func TestMemoryCoordinatorNonceRateAndAuthorityNotifications(t *testing.T) {
	coordinator, _, now := newMemoryCoordinatorForTest(t)
	ctx := context.Background()
	accepted, err := coordinator.Consume(ctx, "proof", "nonce", time.Minute)
	if err != nil || !accepted {
		t.Fatalf("first nonce accepted=%t err=%v", accepted, err)
	}
	accepted, err = coordinator.Consume(ctx, "proof", "nonce", time.Minute)
	if err != nil || accepted {
		t.Fatalf("replayed nonce accepted=%t err=%v", accepted, err)
	}
	for index := 0; index < 2; index++ {
		allowed, allowErr := coordinator.Allow(ctx, "registration", 2, time.Minute)
		if allowErr != nil || !allowed {
			t.Fatalf("rate request %d allowed=%t err=%v", index, allowed, allowErr)
		}
	}
	if allowed, allowErr := coordinator.Allow(ctx, "registration", 2, time.Minute); allowErr != nil || allowed {
		t.Fatalf("rate overflow allowed=%t err=%v", allowed, allowErr)
	}
	subscription, err := coordinator.Subscribe(ctx, "device-revoked")
	if err != nil {
		t.Fatal(err)
	}
	defer subscription.Close()
	id := uuid.New()
	if err := coordinator.Publish(ctx, "device-revoked", id); err != nil {
		t.Fatal(err)
	}
	receiveCtx, cancel := context.WithTimeout(ctx, time.Second)
	defer cancel()
	message, err := subscription.Receive(receiveCtx)
	if err != nil || message.Channel != "device-revoked" || message.ID != id {
		t.Fatalf("message=%+v err=%v", message, err)
	}
	*now = now.Add(time.Minute)
	if accepted, err = coordinator.Consume(ctx, "proof", "nonce", time.Minute); err != nil || !accepted {
		t.Fatalf("expired nonce accepted=%t err=%v", accepted, err)
	}
}

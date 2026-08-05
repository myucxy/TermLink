package kv

import (
	"context"
	"fmt"
	"os"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
)

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

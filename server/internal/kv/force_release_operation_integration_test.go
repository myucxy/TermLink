package kv

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestForceReleaseOperationRetryDoesNotDeleteReplacement(t *testing.T) {
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if redisURL == "" {
		t.Skip("TERMLINK_INTEGRATION_REDIS_URL is required for the operation retry test")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	valkey, err := Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	sessionID := uuid.New()
	userID := uuid.New()
	operationKey := "force-release-" + uuid.NewString()
	leaseKey := "lease:" + sessionID.String()
	tombstoneKey := forceReleaseOperationKey(sessionID, userID, operationKey)
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, leaseKey, leaseKey+":version", tombstoneKey).Err()
		_ = valkey.Close()
	})

	oldLease, err := valkey.Acquire(ctx, sessionID, uuid.New(), 30*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	first, released, err := valkey.ForceReleaseForOperation(ctx, sessionID, userID, operationKey)
	if err != nil {
		t.Fatal(err)
	}
	if !released || first.ID != oldLease.ID || first.Version != oldLease.Version {
		t.Fatalf("first release=%+v released=%v, want old lease=%+v", first, released, oldLease)
	}

	replacement, err := valkey.Acquire(ctx, sessionID, uuid.New(), 30*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	replayed, released, err := valkey.ForceReleaseForOperation(ctx, sessionID, userID, operationKey)
	if err != nil {
		t.Fatal(err)
	}
	if !released || replayed.ID != oldLease.ID || replayed.Version != oldLease.Version {
		t.Fatalf("replayed release=%+v released=%v, want old lease=%+v", replayed, released, oldLease)
	}
	stored, err := valkey.GetLease(ctx, sessionID)
	if err != nil {
		t.Fatalf("replacement lease was removed by retry: %v", err)
	}
	if stored.ID != replacement.ID || stored.Version != replacement.Version {
		t.Fatalf("stored lease=%+v, want replacement=%+v", stored, replacement)
	}
}

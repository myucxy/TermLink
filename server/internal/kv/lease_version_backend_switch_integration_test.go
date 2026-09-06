package kv_test

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestLeaseFenceIncreasesAcrossCoordinatorBackendSwitches(t *testing.T) {
	databaseURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL"))
	redisURL := strings.TrimSpace(os.Getenv("TERMLINK_INTEGRATION_REDIS_URL"))
	if databaseURL == "" || redisURL == "" {
		t.Skip("PostgreSQL and Valkey integration URLs are required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := store.Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	valkey, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	defer valkey.Close()
	valkey.LeaseVersions = repository

	acquireValkey := func() kv.Lease {
		sessionID := uuid.New()
		lease, acquireErr := valkey.Acquire(ctx, sessionID, uuid.New(), time.Minute)
		if acquireErr != nil {
			t.Fatal(acquireErr)
		}
		t.Cleanup(func() {
			cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
			defer cleanupCancel()
			key := "lease:" + sessionID.String()
			_ = valkey.Client.Del(cleanupCtx, key, key+":version", key+":participants").Err()
		})
		return lease
	}

	distributedBefore := acquireValkey()
	embedded, err := kv.NewMemoryCoordinator(repository)
	if err != nil {
		t.Fatal(err)
	}
	embeddedLease, err := embedded.Acquire(ctx, uuid.New(), uuid.New(), time.Minute)
	_ = embedded.Close()
	if err != nil {
		t.Fatal(err)
	}
	distributedAfter := acquireValkey()
	if distributedBefore.Version >= embeddedLease.Version || embeddedLease.Version >= distributedAfter.Version {
		t.Fatalf("lease fences did not increase across backend switches: %d, %d, %d",
			distributedBefore.Version, embeddedLease.Version, distributedAfter.Version)
	}
}

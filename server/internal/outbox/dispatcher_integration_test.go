package outbox

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestDispatcherDeliversAuthorityEvents(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("production PostgreSQL and Valkey integration environment is not configured")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := store.Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	valkey, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_ = valkey.Close()
		repository.Close()
	})

	dispatcher := Dispatcher{Store: repository, Valkey: valkey}
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	outboxIDs := make([]int64, 0, 4)
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		if len(outboxIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM outbox WHERE id=ANY($1::bigint[])`, outboxIDs)
		}
	})

	deviceID := uuid.New()
	subscription := valkey.Client.Subscribe(ctx, "device-authorization-changed")
	defer subscription.Close()
	if _, err := subscription.Receive(ctx); err != nil {
		t.Fatal(err)
	}
	grantEventID := insertOutboxFixture(t, repository, ctx, "grant.updated", &deviceID, `{}`)
	outboxIDs = append(outboxIDs, grantEventID)
	dispatcher.dispatchBatch(ctx, logger)

	for {
		select {
		case message := <-subscription.Channel():
			// Other integration packages can legitimately publish authority
			// changes against the same disposable Valkey instance. Wait for the
			// event owned by this fixture instead of making package scheduling a
			// source of flakes.
			if message != nil && message.Channel == "device-authorization-changed" && message.Payload == deviceID.String() {
				goto grantPublished
			}
		case <-ctx.Done():
			t.Fatal("grant authority event was not published")
		}
	}

grantPublished:
	assertOutboxState(t, repository, ctx, grantEventID, true, 1, "")

	refundID := uuid.New()
	refundSubscription := valkey.Client.Subscribe(ctx, "refund-approval-changed")
	defer refundSubscription.Close()
	if _, err := refundSubscription.Receive(ctx); err != nil {
		t.Fatal(err)
	}
	refundEventID := insertOutboxFixture(t, repository, ctx, "refund.approval.changed", &refundID, `{"refund_id":"`+refundID.String()+`","status":"CREATED","version":2}`)
	outboxIDs = append(outboxIDs, refundEventID)
	dispatcher.dispatchBatch(ctx, logger)
	for {
		select {
		case message := <-refundSubscription.Channel():
			if message != nil && message.Channel == "refund-approval-changed" && message.Payload == refundID.String() {
				goto refundPublished
			}
		case <-ctx.Done():
			t.Fatal("refund approval event was not published")
		}
	}

refundPublished:
	assertOutboxState(t, repository, ctx, refundEventID, true, 1, "")

	sessionID := uuid.New()
	holderID := uuid.New()
	lease, err := valkey.Acquire(ctx, sessionID, holderID, 30*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_ = valkey.Client.Del(cleanupContext, "lease:"+sessionID.String(), "lease:"+sessionID.String()+":version").Err()
	})
	leaseEventID := insertLeaseRevocationFixture(t, repository, ctx, lease)
	outboxIDs = append(outboxIDs, leaseEventID)
	dispatcher.dispatchBatch(ctx, logger)
	if _, err := valkey.GetLease(ctx, sessionID); !errors.Is(err, redis.Nil) {
		t.Fatalf("revoked lease lookup error=%v, want redis.Nil", err)
	}
	assertOutboxState(t, repository, ctx, leaseEventID, true, 1, "")

	invalidEventID := insertOutboxFixture(t, repository, ctx, "lease.revoked", &sessionID, `{}`)
	outboxIDs = append(outboxIDs, invalidEventID)
	dispatcher.dispatchBatch(ctx, logger)
	assertOutboxState(t, repository, ctx, invalidEventID, false, 1, "delivery_failed")
}

func insertOutboxFixture(t *testing.T, repository *store.Postgres, ctx context.Context, eventType string, aggregateID *uuid.UUID, payload string) int64 {
	t.Helper()
	var id int64
	if err := repository.Pool.QueryRow(ctx, `
		INSERT INTO outbox(event_type,aggregate_id,payload)
		VALUES($1,$2,$3::jsonb) RETURNING id`, eventType, aggregateID, payload).Scan(&id); err != nil {
		t.Fatal(err)
	}
	return id
}

func insertLeaseRevocationFixture(t *testing.T, repository *store.Postgres, ctx context.Context, lease kv.Lease) int64 {
	t.Helper()
	var id int64
	if err := repository.Pool.QueryRow(ctx, `
		INSERT INTO outbox(event_type,aggregate_id,payload)
		VALUES('lease.revoked',$1,jsonb_build_object(
			'session_id',$1::uuid,'holder_device_id',$2::uuid,
			'lease_id',$3::uuid,'version',$4::bigint))
		RETURNING id`, lease.SessionID, lease.HolderDeviceID, lease.ID, lease.Version).Scan(&id); err != nil {
		t.Fatal(err)
	}
	return id
}

func assertOutboxState(t *testing.T, repository *store.Postgres, ctx context.Context, id int64, processed bool, attempts int, lastError string) {
	t.Helper()
	var gotProcessed bool
	var gotAttempts int
	var gotLastError *string
	if err := repository.Pool.QueryRow(ctx, `
		SELECT processed_at IS NOT NULL,attempts,last_error FROM outbox WHERE id=$1`, id).
		Scan(&gotProcessed, &gotAttempts, &gotLastError); err != nil {
		t.Fatal(err)
	}
	if gotProcessed != processed || gotAttempts != attempts {
		t.Fatalf("outbox id=%d processed/attempts=%v/%d, want %v/%d", id, gotProcessed, gotAttempts, processed, attempts)
	}
	if lastError == "" {
		if gotLastError != nil {
			t.Fatalf("outbox id=%d last_error=%q, want NULL", id, *gotLastError)
		}
	} else if gotLastError == nil || *gotLastError != lastError {
		t.Fatalf("outbox id=%d last_error=%v, want %q", id, gotLastError, lastError)
	}
}

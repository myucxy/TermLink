package store

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestIdempotencyClaimRetriesServerFailureWithSameRequestOnly(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the idempotency retry test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()

	userID := uuid.New()
	subjectHash := sha256.Sum256([]byte("idempotency-retry-" + userID.String()))
	if _, err := repository.Pool.Exec(ctx,
		`INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, userID, subjectHash[:]); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cleanupContext, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupContext, `DELETE FROM users WHERE id=$1`, userID)
	})

	requestHash := sha256.Sum256([]byte("DELETE\n/v1/sessions/example/lease\n"))
	key := "retry-" + uuid.NewString()
	claim, err := repository.ClaimIdempotency(ctx, userID, key, requestHash)
	if err != nil || !claim.Claimed {
		t.Fatalf("initial claim=%+v err=%v", claim, err)
	}
	if err := repository.CompleteIdempotency(ctx, userID, key, requestHash, 500, json.RawMessage(`{"error":"transient"}`)); err != nil {
		t.Fatal(err)
	}
	retry, err := repository.ClaimIdempotency(ctx, userID, key, requestHash)
	if err != nil || !retry.Claimed || retry.Status != nil {
		t.Fatalf("same-request retry=%+v err=%v", retry, err)
	}
	if err := repository.CompleteIdempotency(ctx, userID, key, requestHash, 200, json.RawMessage(`{"released":true}`)); err != nil {
		t.Fatal(err)
	}
	replay, err := repository.ClaimIdempotency(ctx, userID, key, requestHash)
	if err != nil || replay.Claimed || replay.Status == nil || *replay.Status != 200 {
		t.Fatalf("successful replay=%+v err=%v", replay, err)
	}

	mismatchKey := "mismatch-" + uuid.NewString()
	if claim, err := repository.ClaimIdempotency(ctx, userID, mismatchKey, requestHash); err != nil || !claim.Claimed {
		t.Fatalf("mismatch fixture claim=%+v err=%v", claim, err)
	}
	if err := repository.CompleteIdempotency(ctx, userID, mismatchKey, requestHash, 503, json.RawMessage(`{"error":"transient"}`)); err != nil {
		t.Fatal(err)
	}
	differentHash := sha256.Sum256([]byte("DELETE\n/v1/sessions/different/lease\n"))
	mismatch, err := repository.ClaimIdempotency(ctx, userID, mismatchKey, differentHash)
	if err != nil {
		t.Fatal(err)
	}
	if mismatch.Claimed || mismatch.RequestHash != requestHash || mismatch.Status == nil || *mismatch.Status != 503 {
		t.Fatalf("mismatched request reused failed key: %+v", mismatch)
	}
}

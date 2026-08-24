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
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

type recordingHistoryDeleter struct {
	keys        []string
	failingKey  string
	failingOnce bool
}

func (d *recordingHistoryDeleter) Delete(_ context.Context, key string) error {
	d.keys = append(d.keys, key)
	if key == d.failingKey && d.failingOnce {
		d.failingOnce = false
		return errors.New("temporary object store failure")
	}
	return nil
}

func deleteAttempts(keys []string, target string) int {
	attempts := 0
	for _, key := range keys {
		if key == target {
			attempts++
		}
	}
	return attempts
}

func TestDispatcherRetriesHistoryObjectDeletionAtLeastOnce(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("production PostgreSQL integration environment is not configured")
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
	defer repository.Close()

	sessionID := uuid.New()
	objectKey := "accounts/11111111-1111-4111-8111-111111111111/sessions/22222222-2222-4222-8222-222222222222/transcript/" + uuid.NewString() + ".bin"
	eventID := insertOutboxFixture(t, repository, ctx, "history.object.delete", &sessionID, `{"object_key":"`+objectKey+`"}`)
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE id=$1`, eventID)
	})

	deleter := &recordingHistoryDeleter{failingKey: objectKey, failingOnce: true}
	dispatcher := Dispatcher{Store: repository, History: deleter}
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	dispatcher.dispatchBatch(ctx, logger)
	assertOutboxState(t, repository, ctx, eventID, false, 1, "delivery_failed")
	if attempts := deleteAttempts(deleter.keys, objectKey); attempts != 1 {
		t.Fatalf("first delete attempts=%v", deleter.keys)
	}

	if _, err := repository.Pool.Exec(ctx, `UPDATE outbox SET available_at=now() WHERE id=$1`, eventID); err != nil {
		t.Fatal(err)
	}
	dispatcher.dispatchBatch(ctx, logger)
	assertOutboxState(t, repository, ctx, eventID, true, 2, "")
	if attempts := deleteAttempts(deleter.keys, objectKey); attempts != 2 {
		t.Fatalf("at-least-once delete attempts=%v", deleter.keys)
	}
}

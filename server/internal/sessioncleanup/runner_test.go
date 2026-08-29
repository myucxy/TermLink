package sessioncleanup

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"
	"time"
)

type sensitiveLogError struct {
	text  string
	calls int
}

func (e *sensitiveLogError) Error() string {
	e.calls++
	return e.text
}

type recordingStore struct {
	endedBefore time.Time
	staleBefore time.Time
	batchSize   int
	calls       int
}

func (s *recordingStore) CleanupSessions(_ context.Context, endedBefore, staleBefore time.Time, batchSize int) (int, error) {
	s.endedBefore = endedBefore
	s.staleBefore = staleBefore
	s.batchSize = batchSize
	s.calls++
	return 3, nil
}

func TestRunOnceUsesSafeDefaultsAndOneClockSample(t *testing.T) {
	now := time.Date(2026, 8, 23, 12, 0, 0, 0, time.FixedZone("test", 8*60*60))
	store := &recordingStore{}
	runner := New(store, Options{Clock: func() time.Time { return now }})
	if runner.interval != DefaultInterval {
		t.Fatalf("interval=%s, want %s", runner.interval, DefaultInterval)
	}
	if runner.staleRetention != 15*time.Minute {
		t.Fatalf("stale retention=%s, want 15m", runner.staleRetention)
	}

	cleaned, err := runner.RunOnce(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if cleaned != 3 || store.calls != 1 {
		t.Fatalf("cleaned/calls=%d/%d, want 3/1", cleaned, store.calls)
	}
	utcNow := now.UTC()
	if want := utcNow.Add(-DefaultEndedRetention); !store.endedBefore.Equal(want) {
		t.Fatalf("ended cutoff=%s, want %s", store.endedBefore, want)
	}
	if want := utcNow.Add(-DefaultStaleRetention); !store.staleBefore.Equal(want) {
		t.Fatalf("stale cutoff=%s, want %s", store.staleBefore, want)
	}
	if store.batchSize != DefaultBatchSize {
		t.Fatalf("batch=%d, want %d", store.batchSize, DefaultBatchSize)
	}
}

func TestNewClampsOversizedBatch(t *testing.T) {
	store := &recordingStore{}
	runner := New(store, Options{BatchSize: DefaultBatchSize + 1})
	if _, err := runner.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if store.batchSize != DefaultBatchSize {
		t.Fatalf("batch=%d, want %d", store.batchSize, DefaultBatchSize)
	}
}

func TestCleanupFailureLogDoesNotFormatError(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &sensitiveLogError{text: "password=session-cleanup-secret"}

	logCleanupFailure(logger, 5, sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "session-cleanup-secret") {
		t.Fatalf("cleanup log formatted or exposed the error: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"session_cleanup_failed"`) || !strings.Contains(logged, `"sessions":5`) {
		t.Fatalf("cleanup log is missing stable fields: %s", logged)
	}
}

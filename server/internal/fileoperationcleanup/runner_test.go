package fileoperationcleanup

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"
	"time"

	"github.com/termlink/termlink/server/internal/store"
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
	now, terminalBefore time.Time
	limit               int
	result              store.FileOperationCleanupResult
	err                 error
}

func (s *recordingStore) CleanupFileOperations(_ context.Context, now, terminalBefore time.Time, limit int) (store.FileOperationCleanupResult, error) {
	s.now, s.terminalBefore, s.limit = now, terminalBefore, limit
	return s.result, s.err
}

func TestRunOnceUsesBoundedDefaultsAndOneClockSample(t *testing.T) {
	now := time.Date(2026, 8, 24, 16, 0, 0, 0, time.FixedZone("test", 8*60*60))
	repository := &recordingStore{result: store.FileOperationCleanupResult{Expired: 1, Deleted: 2}}
	runner := New(repository, Options{Clock: func() time.Time { return now }})
	if runner.interval != DefaultInterval || runner.terminalRetention != DefaultTerminalRetention || runner.batchSize != DefaultBatchSize {
		t.Fatalf("unexpected defaults interval=%s retention=%s batch=%d", runner.interval, runner.terminalRetention, runner.batchSize)
	}
	result, err := runner.RunOnce(context.Background())
	if err != nil || result != repository.result {
		t.Fatalf("RunOnce result=%+v err=%v", result, err)
	}
	if !repository.now.Equal(now.UTC()) || !repository.terminalBefore.Equal(now.UTC().Add(-DefaultTerminalRetention)) || repository.limit != DefaultBatchSize {
		t.Fatalf("cleanup input now=%s terminalBefore=%s limit=%d", repository.now, repository.terminalBefore, repository.limit)
	}
}

func TestNewRejectsOversizedBatch(t *testing.T) {
	repository := &recordingStore{}
	runner := New(repository, Options{BatchSize: store.MaxFileOperationCleanupBatch + 1})
	if runner.batchSize != DefaultBatchSize {
		t.Fatalf("oversized batch was retained: %d", runner.batchSize)
	}
}

func TestCleanupFailureLogDoesNotFormatError(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &sensitiveLogError{text: "access_key=secret-file-operation-key"}

	logCleanupFailure(logger, store.FileOperationCleanupResult{Expired: 4, Deleted: 2}, sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "secret-file-operation-key") {
		t.Fatalf("cleanup log formatted or exposed the error: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"file_operation_cleanup_failed"`) ||
		!strings.Contains(logged, `"expired":4`) || !strings.Contains(logged, `"deleted":2`) {
		t.Fatalf("cleanup log is missing stable fields: %s", logged)
	}
}

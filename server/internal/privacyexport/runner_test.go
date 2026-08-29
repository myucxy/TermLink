package privacyexport

import (
	"context"
	"errors"
	"testing"
	"time"
)

type fakeWorker struct {
	completed   int
	expired     int
	processErr  error
	cleanupErr  error
	processSize int
	cleanupSize int
}

func (w *fakeWorker) ProcessOnce(_ context.Context, limit int) (int, error) {
	w.processSize = limit
	return w.completed, w.processErr
}

func (w *fakeWorker) CleanupExpired(_ context.Context, limit int) (int, error) {
	w.cleanupSize = limit
	return w.expired, w.cleanupErr
}

func TestRunnerRunOnceProcessesAndCleansIndependently(t *testing.T) {
	processErr := errors.New("process failed")
	worker := &fakeWorker{completed: 2, expired: 3, processErr: processErr}
	runner := NewRunner(worker, RunnerOptions{Timeout: time.Second, BatchSize: 7, CleanupSize: 9, DisableJitter: true})
	result, err := runner.RunOnce(context.Background())
	if !errors.Is(err, processErr) || result.Completed != 2 || result.Expired != 3 {
		t.Fatalf("RunOnce result=%+v error=%v", result, err)
	}
	if worker.processSize != 7 || worker.cleanupSize != 9 {
		t.Fatalf("limits process=%d cleanup=%d", worker.processSize, worker.cleanupSize)
	}
}

func TestRunnerRejectsMissingWorkerAndBoundsOptions(t *testing.T) {
	if _, err := NewRunner(nil, RunnerOptions{}).RunOnce(context.Background()); !errors.Is(err, ErrRunnerUnavailable) {
		t.Fatalf("missing worker error=%v", err)
	}
	worker := &fakeWorker{}
	runner := NewRunner(worker, RunnerOptions{BatchSize: MaxRunnerBatchSize + 1, CleanupSize: MaxRunnerCleanupSize + 1, DisableJitter: true})
	if _, err := runner.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if worker.processSize != DefaultRunnerBatchSize || worker.cleanupSize != DefaultRunnerCleanupSize {
		t.Fatalf("default limits process=%d cleanup=%d", worker.processSize, worker.cleanupSize)
	}
}

func TestRunnerCleanupLimitMatchesPersistenceBoundary(t *testing.T) {
	if DefaultRunnerCleanupSize != MaxRunnerCleanupSize {
		t.Fatalf("default cleanup size=%d, persistence maximum=%d", DefaultRunnerCleanupSize, MaxRunnerCleanupSize)
	}
	worker := &fakeWorker{}
	runner := NewRunner(worker, RunnerOptions{CleanupSize: MaxRunnerCleanupSize, DisableJitter: true})
	if _, err := runner.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if worker.cleanupSize != MaxRunnerCleanupSize {
		t.Fatalf("cleanup size=%d, want %d", worker.cleanupSize, MaxRunnerCleanupSize)
	}
}

func TestRunnerDelayIsBounded(t *testing.T) {
	runner := NewRunner(&fakeWorker{}, RunnerOptions{Interval: 10 * time.Second, Jitter: 2 * time.Second, RandomFloat64: func() float64 { return 1 }})
	if delay := runner.nextDelay(); delay != 12*time.Second {
		t.Fatalf("delay=%v", delay)
	}
	runner.randomFloat64 = func() float64 { return 9 }
	if delay := runner.nextDelay(); delay != 10*time.Second {
		t.Fatalf("invalid random delay=%v", delay)
	}
}

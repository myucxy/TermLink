package accountdeletion

import (
	"context"
	"errors"
	"testing"
	"time"
)

type fakeRunnerWorker struct {
	completed int
	err       error
	limit     int
	wait      bool
}

func (w *fakeRunnerWorker) ProcessOnce(ctx context.Context, limit int) (int, error) {
	w.limit = limit
	if w.wait {
		<-ctx.Done()
		return 0, ctx.Err()
	}
	return w.completed, w.err
}

func TestAccountDeletionRunnerBoundsBatchAndTimeout(t *testing.T) {
	t.Parallel()
	worker := &fakeRunnerWorker{wait: true}
	runner := NewRunner(worker, RunnerOptions{
		Timeout: 20 * time.Millisecond, BatchSize: MaxRunnerBatchSize + 1, DisableJitter: true,
	})
	started := time.Now()
	_, err := runner.RunOnce(context.Background())
	if !errors.Is(err, context.DeadlineExceeded) || time.Since(started) > time.Second || worker.limit != DefaultRunnerBatchSize {
		t.Fatalf("RunOnce error=%v elapsed=%s limit=%d", err, time.Since(started), worker.limit)
	}
	if _, err := NewRunner(nil, RunnerOptions{}).RunOnce(context.Background()); !errors.Is(err, ErrRunnerUnavailable) {
		t.Fatalf("missing worker error=%v", err)
	}
}

func TestAccountDeletionRunnerBackoffAndJitterAreBounded(t *testing.T) {
	t.Parallel()
	runner := NewRunner(&fakeRunnerWorker{}, RunnerOptions{
		Interval: 10 * time.Second, MaxBackoff: 40 * time.Second, Jitter: 2 * time.Second,
		RandomFloat64: func() float64 { return 1 },
	})
	for failures, want := range map[int]time.Duration{
		0: 12 * time.Second, 1: 22 * time.Second, 2: 40 * time.Second, 9: 40 * time.Second,
	} {
		if got := runner.nextDelay(failures); got != want {
			t.Fatalf("failures=%d delay=%s, want %s", failures, got, want)
		}
	}
	runner.randomFloat64 = func() float64 { return 9 }
	if got := runner.nextDelay(0); got != 10*time.Second {
		t.Fatalf("invalid jitter value delay=%s", got)
	}
}

func TestAccountDeletionRunnerStopsWhenCanceled(t *testing.T) {
	t.Parallel()
	runner := NewRunner(&fakeRunnerWorker{}, RunnerOptions{Interval: time.Second, DisableJitter: true})
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		runner.Run(ctx)
		close(done)
	}()
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("runner did not stop after cancellation")
	}
}

package reconciliation

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

type recordingProcessor struct {
	mu      sync.Mutex
	results []ProcessResult
	errors  []error
	calls   []uuid.UUID
	wait    <-chan struct{}
	started chan<- struct{}
}

func (processor *recordingProcessor) Process(ctx context.Context, candidate store.CommerceReconciliationCandidate) (ProcessResult, error) {
	processor.mu.Lock()
	index := len(processor.calls)
	processor.calls = append(processor.calls, candidate.RunID)
	processor.mu.Unlock()
	if processor.started != nil {
		select {
		case processor.started <- struct{}{}:
		default:
		}
	}
	if processor.wait != nil {
		select {
		case <-ctx.Done():
			return ProcessResult{Acquired: true}, ctx.Err()
		case <-processor.wait:
		}
	}
	processor.mu.Lock()
	defer processor.mu.Unlock()
	var result ProcessResult
	if index < len(processor.results) {
		result = processor.results[index]
	}
	var err error
	if index < len(processor.errors) {
		err = processor.errors[index]
	}
	return result, err
}

func TestRunnerContinuesAfterItemFailureAndCountsOutcomes(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now.Add(-2 * time.Minute)),
	}}
	processor := &recordingProcessor{
		results: []ProcessResult{
			{Acquired: true, Summary: Summary{Result: "BALANCED"}},
			{Acquired: true, Summary: Summary{Result: "FAILED"}},
			{Acquired: true, Summary: Summary{Result: "DIFFERENCES"}},
		},
		errors: []error{nil, errors.New("provider unavailable"), nil},
	}
	runner := NewRunner(repository, processor, RunnerOptions{
		Clock: func() time.Time { return now }, DisableJitter: true, StaleAfter: time.Minute, BatchSize: 10,
	})
	result, err := runner.RunOnce(context.Background())
	if err == nil {
		t.Fatal("expected joined item failure")
	}
	if result.Candidates != 3 || result.Acquired != 3 || result.Balanced != 1 || result.Differences != 1 || result.Failed != 1 || len(processor.calls) != 3 {
		t.Fatalf("unexpected result: %+v calls=%d", result, len(processor.calls))
	}
}

func TestRunnerSkipsUnacquiredAndRejectsFutureCandidate(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now),
	}}
	processor := &recordingProcessor{results: []ProcessResult{{Acquired: false}}}
	runner := NewRunner(repository, processor, RunnerOptions{Clock: func() time.Time { return now }, DisableJitter: true, StaleAfter: time.Minute})
	result, err := runner.RunOnce(context.Background())
	if err == nil || result.Skipped != 1 || result.Invalid != 1 || result.Failed != 1 || len(processor.calls) != 1 {
		t.Fatalf("result=%+v err=%v calls=%d", result, err, len(processor.calls))
	}
}

func TestRunnerItemTimeoutStopsOnlyCurrentItem(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now.Add(-2 * time.Minute)),
	}}
	processor := &recordingProcessor{wait: make(chan struct{})}
	runner := NewRunner(repository, processor, RunnerOptions{
		Clock: func() time.Time { return now }, DisableJitter: true, StaleAfter: time.Minute,
		ItemTimeout: 10 * time.Millisecond, PassTimeout: time.Second,
	})
	result, err := runner.RunOnce(context.Background())
	if !errors.Is(err, context.DeadlineExceeded) || result.Failed != 2 || result.Acquired != 2 || len(processor.calls) != 2 {
		t.Fatalf("result=%+v err=%v calls=%d", result, err, len(processor.calls))
	}
}

func TestRunnerCancellationStopsStartingNewItems(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now.Add(-2 * time.Minute)),
	}}
	started := make(chan struct{}, 1)
	processor := &recordingProcessor{wait: make(chan struct{}), started: started}
	runner := NewRunner(repository, processor, RunnerOptions{Clock: func() time.Time { return now }, DisableJitter: true, StaleAfter: time.Minute})
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		_, err := runner.RunOnce(ctx)
		done <- err
	}()
	<-started
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatalf("error = %v", err)
	}
	if len(processor.calls) != 1 {
		t.Fatalf("started %d items after cancellation", len(processor.calls))
	}
}

func TestRunnerBoundsCandidateFeedAndJitter(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{
		runnerCandidate(now.Add(-2 * time.Minute)),
		runnerCandidate(now.Add(-2 * time.Minute)),
	}}
	processor := &recordingProcessor{results: []ProcessResult{{Acquired: false}}}
	runner := NewRunner(repository, processor, RunnerOptions{
		Clock: func() time.Time { return now }, Interval: time.Minute, Jitter: 10 * time.Second,
		RandomFloat64: func() float64 { return 1 }, StaleAfter: time.Minute, BatchSize: 1,
	})
	result, err := runner.RunOnce(context.Background())
	if !errors.Is(err, ErrCandidateOverflow) || result.Candidates != 1 || !result.BatchFull || len(processor.calls) != 1 {
		t.Fatalf("result=%+v err=%v", result, err)
	}
	if delay := runner.nextDelay(); delay != 70*time.Second {
		t.Fatalf("delay = %s", delay)
	}
}

func TestRunnerLogsOnlyLowCardinalityAggregates(t *testing.T) {
	now := time.Date(2026, 8, 27, 12, 0, 0, 0, time.UTC)
	candidate := runnerCandidate(now.Add(-2 * time.Minute))
	repository := &testRepository{candidates: []store.CommerceReconciliationCandidate{candidate}}
	processor := &recordingProcessor{results: []ProcessResult{{Acquired: true, Summary: Summary{Result: "DIFFERENCES"}}}}
	var output bytes.Buffer
	runner := NewRunner(repository, processor, RunnerOptions{
		Clock: func() time.Time { return now }, DisableJitter: true, StaleAfter: time.Minute,
		Logger: slog.New(slog.NewJSONHandler(&output, nil)),
	})
	runner.runAndLog(context.Background())
	logs := output.String()
	if !strings.Contains(logs, `"differences":1`) || strings.Contains(logs, candidate.RunID.String()) || strings.Contains(logs, "order") || strings.Contains(logs, "trade") {
		t.Fatalf("unsafe or incomplete log: %s", logs)
	}
}

func runnerCandidate(createdAt time.Time) store.CommerceReconciliationCandidate {
	start := time.Date(2026, 8, 26, 0, 0, 0, 0, time.UTC)
	return store.CommerceReconciliationCandidate{
		RunID: uuid.New(), Provider: "ALIPAY", PeriodStart: start, PeriodEnd: start.AddDate(0, 0, 1), CreatedAt: createdAt,
	}
}

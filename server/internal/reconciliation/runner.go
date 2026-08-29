package reconciliation

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"math/rand/v2"
	"time"

	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultRunnerInterval    = 15 * time.Minute
	DefaultRunnerJitter      = 2 * time.Minute
	DefaultRunnerStaleAfter  = time.Minute
	DefaultRunnerItemTimeout = 2 * time.Minute
	DefaultRunnerPassTimeout = 10 * time.Minute
	DefaultRunnerBatchSize   = 10
	MaxRunnerBatchSize       = 100
	MinRunnerInterval        = 10 * time.Second
	MaxRunnerItemTimeout     = 5 * time.Minute
	MaxRunnerPassTimeout     = 30 * time.Minute
)

var (
	ErrRunnerUnavailable = errors.New("reconciliation: runner unavailable")
	ErrCandidateOverflow = errors.New("reconciliation: candidate feed exceeded requested limit")
)

type CandidateProcessor interface {
	Process(context.Context, store.CommerceReconciliationCandidate) (ProcessResult, error)
}

type RunnerOptions struct {
	Interval      time.Duration
	Jitter        time.Duration
	DisableJitter bool
	StaleAfter    time.Duration
	ItemTimeout   time.Duration
	PassTimeout   time.Duration
	BatchSize     int
	Clock         func() time.Time
	RandomFloat64 func() float64
	Logger        *slog.Logger
}

type RunnerResult struct {
	Candidates  int
	Acquired    int
	Skipped     int
	Balanced    int
	Differences int
	Failed      int
	Invalid     int
	BatchFull   bool
}

type Runner struct {
	repository    Repository
	processor     CandidateProcessor
	interval      time.Duration
	jitter        time.Duration
	staleAfter    time.Duration
	itemTimeout   time.Duration
	passTimeout   time.Duration
	batchSize     int
	clock         func() time.Time
	randomFloat64 func() float64
	logger        *slog.Logger
	newTimer      func(time.Duration) runnerTimer
}

type runnerTimer interface {
	Chan() <-chan time.Time
	Stop() bool
}

type realRunnerTimer struct{ *time.Timer }

func (timer realRunnerTimer) Chan() <-chan time.Time { return timer.C }

func NewRunner(repository Repository, processor CandidateProcessor, options RunnerOptions) *Runner {
	if options.Interval < MinRunnerInterval {
		options.Interval = DefaultRunnerInterval
	}
	if options.DisableJitter {
		options.Jitter = 0
	} else if options.Jitter <= 0 {
		options.Jitter = DefaultRunnerJitter
	}
	if options.Jitter >= options.Interval {
		options.Jitter = options.Interval / 4
	}
	if options.StaleAfter <= 0 {
		options.StaleAfter = DefaultRunnerStaleAfter
	}
	if options.ItemTimeout <= 0 || options.ItemTimeout > MaxRunnerItemTimeout {
		options.ItemTimeout = DefaultRunnerItemTimeout
	}
	if options.PassTimeout <= 0 || options.PassTimeout > MaxRunnerPassTimeout || options.PassTimeout < options.ItemTimeout {
		options.PassTimeout = DefaultRunnerPassTimeout
	}
	if options.BatchSize < 1 || options.BatchSize > MaxRunnerBatchSize {
		options.BatchSize = DefaultRunnerBatchSize
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.RandomFloat64 == nil {
		options.RandomFloat64 = rand.Float64
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		repository: repository, processor: processor, interval: options.Interval, jitter: options.Jitter,
		staleAfter: options.StaleAfter, itemTimeout: options.ItemTimeout, passTimeout: options.PassTimeout,
		batchSize: options.BatchSize, clock: options.Clock, randomFloat64: options.RandomFloat64,
		logger: options.Logger, newTimer: func(delay time.Duration) runnerTimer { return realRunnerTimer{time.NewTimer(delay)} },
	}
}

func (runner *Runner) RunOnce(ctx context.Context) (RunnerResult, error) {
	if runner == nil || runner.repository == nil || runner.processor == nil {
		return RunnerResult{}, ErrRunnerUnavailable
	}
	passContext, cancel := context.WithTimeout(ctx, runner.passTimeout)
	defer cancel()
	cutoff := runner.clock().UTC().Add(-runner.staleAfter)
	candidates, err := runner.repository.ListPendingReconciliationCandidates(passContext, cutoff, runner.batchSize)
	if err != nil {
		return RunnerResult{}, fmt.Errorf("list reconciliation candidates: %w", err)
	}
	result := RunnerResult{Candidates: len(candidates), BatchFull: len(candidates) == runner.batchSize}
	var runErrors []error
	if len(candidates) > runner.batchSize {
		candidates = candidates[:runner.batchSize]
		result.Candidates = len(candidates)
		result.BatchFull = true
		runErrors = append(runErrors, ErrCandidateOverflow)
	}
	for index, candidate := range candidates {
		if err := passContext.Err(); err != nil {
			return result, errors.Join(append(runErrors, err)...)
		}
		if candidate.RunID == [16]byte{} || candidate.CreatedAt.IsZero() || candidate.CreatedAt.After(cutoff) {
			result.Invalid++
			result.Failed++
			runErrors = append(runErrors, fmt.Errorf("candidate %d: %w", index, ErrInvalidCandidate))
			continue
		}
		itemContext, itemCancel := context.WithTimeout(passContext, runner.itemTimeout)
		outcome, processErr := runner.processor.Process(itemContext, candidate)
		itemCancel()
		if outcome.Acquired {
			result.Acquired++
		} else if processErr == nil {
			result.Skipped++
		}
		if processErr != nil {
			result.Failed++
			if errors.Is(processErr, ErrInvalidCandidate) {
				result.Invalid++
			}
			runErrors = append(runErrors, fmt.Errorf("candidate %d: %w", index, processErr))
			continue
		}
		if !outcome.Acquired {
			continue
		}
		switch outcome.Summary.Result {
		case "BALANCED":
			result.Balanced++
		case "DIFFERENCES":
			result.Differences++
		default:
			result.Failed++
			runErrors = append(runErrors, fmt.Errorf("candidate %d: invalid processor outcome", index))
		}
	}
	return result, errors.Join(runErrors...)
}

func (runner *Runner) Run(ctx context.Context) {
	if runner == nil {
		return
	}
	runner.runAndLog(ctx)
	for {
		timer := runner.newTimer(runner.nextDelay())
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.Chan():
			timer.Stop()
			runner.runAndLog(ctx)
		}
	}
}

func (runner *Runner) nextDelay() time.Duration {
	if runner.jitter <= 0 {
		return runner.interval
	}
	randomValue := runner.randomFloat64()
	if math.IsNaN(randomValue) || randomValue < 0 || randomValue > 1 {
		randomValue = 0.5
	}
	delay := runner.interval + time.Duration((randomValue*2-1)*float64(runner.jitter))
	if delay < MinRunnerInterval {
		return MinRunnerInterval
	}
	return delay
}

func (runner *Runner) runAndLog(ctx context.Context) {
	result, err := runner.RunOnce(ctx)
	if err != nil {
		if ctx.Err() == nil {
			runner.logger.Error("payment reconciliation pass partially failed",
				"error_class", "partial_failure", "candidates", result.Candidates,
				"acquired", result.Acquired, "skipped", result.Skipped,
				"failed", result.Failed, "invalid", result.Invalid)
		}
		return
	}
	if result.Candidates > 0 {
		runner.logger.Info("payment reconciliation pass completed",
			"candidates", result.Candidates, "acquired", result.Acquired,
			"skipped", result.Skipped, "balanced", result.Balanced,
			"differences", result.Differences)
	}
}

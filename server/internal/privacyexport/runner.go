package privacyexport

import (
	"context"
	"errors"
	"log/slog"
	"math/rand/v2"
	"time"

	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultRunnerInterval    = time.Minute
	DefaultRunnerJitter      = 10 * time.Second
	DefaultRunnerTimeout     = 2 * time.Minute
	DefaultRunnerBatchSize   = 10
	DefaultRunnerCleanupSize = store.MaxUserPortalExportCleanupBatch
	MaxRunnerBatchSize       = 100
	MaxRunnerCleanupSize     = store.MaxUserPortalExportCleanupBatch
)

var ErrRunnerUnavailable = errors.New("privacy export runner unavailable")

type Worker interface {
	ProcessOnce(context.Context, int) (int, error)
	CleanupExpired(context.Context, int) (int, error)
}

type RunnerOptions struct {
	Interval      time.Duration
	Jitter        time.Duration
	DisableJitter bool
	Timeout       time.Duration
	BatchSize     int
	CleanupSize   int
	RandomFloat64 func() float64
	Logger        *slog.Logger
}

type RunnerResult struct {
	Completed int
	Expired   int
}

type Runner struct {
	worker        Worker
	interval      time.Duration
	jitter        time.Duration
	timeout       time.Duration
	batchSize     int
	cleanupSize   int
	randomFloat64 func() float64
	logger        *slog.Logger
}

func NewRunner(worker Worker, options RunnerOptions) *Runner {
	if options.Interval < time.Second {
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
	if options.Timeout <= 0 || options.Timeout > 10*time.Minute {
		options.Timeout = DefaultRunnerTimeout
	}
	if options.BatchSize < 1 || options.BatchSize > MaxRunnerBatchSize {
		options.BatchSize = DefaultRunnerBatchSize
	}
	if options.CleanupSize < 1 || options.CleanupSize > MaxRunnerCleanupSize {
		options.CleanupSize = DefaultRunnerCleanupSize
	}
	if options.RandomFloat64 == nil {
		options.RandomFloat64 = rand.Float64
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		worker: worker, interval: options.Interval, jitter: options.Jitter,
		timeout: options.Timeout, batchSize: options.BatchSize, cleanupSize: options.CleanupSize,
		randomFloat64: options.RandomFloat64, logger: options.Logger,
	}
}

func (r *Runner) RunOnce(ctx context.Context) (RunnerResult, error) {
	if r == nil || r.worker == nil {
		return RunnerResult{}, ErrRunnerUnavailable
	}
	passContext, cancel := context.WithTimeout(ctx, r.timeout)
	defer cancel()
	var result RunnerResult
	completed, processErr := r.worker.ProcessOnce(passContext, r.batchSize)
	result.Completed = completed
	expired, cleanupErr := r.worker.CleanupExpired(passContext, r.cleanupSize)
	result.Expired = expired
	return result, errors.Join(processErr, cleanupErr)
}

func (r *Runner) Run(ctx context.Context) {
	if r == nil {
		return
	}
	r.runAndLog(ctx)
	for {
		timer := time.NewTimer(r.nextDelay())
		select {
		case <-ctx.Done():
			if !timer.Stop() {
				select {
				case <-timer.C:
				default:
				}
			}
			return
		case <-timer.C:
			r.runAndLog(ctx)
		}
	}
}

func (r *Runner) runAndLog(ctx context.Context) {
	result, err := r.RunOnce(ctx)
	if err != nil {
		if ctx.Err() == nil {
			r.logger.Error("privacy export maintenance partially failed", "error_class", "PRIVACY_EXPORT_MAINTENANCE_FAILED", "completed", result.Completed, "expired", result.Expired)
		}
		return
	}
	if result.Completed > 0 || result.Expired > 0 {
		r.logger.Info("privacy export maintenance completed", "completed", result.Completed, "expired", result.Expired)
	}
}

func (r *Runner) nextDelay() time.Duration {
	if r.jitter <= 0 {
		return r.interval
	}
	randomValue := r.randomFloat64()
	if randomValue < 0 || randomValue > 1 {
		randomValue = 0.5
	}
	delay := r.interval + time.Duration((randomValue*2-1)*float64(r.jitter))
	if delay < time.Second {
		return time.Second
	}
	return delay
}

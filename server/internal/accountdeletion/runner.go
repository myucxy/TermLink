package accountdeletion

import (
	"context"
	"errors"
	"log/slog"
	"math/rand/v2"
	"time"
)

const (
	DefaultRunnerInterval   = 30 * time.Second
	DefaultRunnerMaxBackoff = 5 * time.Minute
	DefaultRunnerJitter     = 5 * time.Second
	DefaultRunnerTimeout    = 4 * time.Minute
	DefaultRunnerBatchSize  = 10
	MaxRunnerBatchSize      = 100
)

var ErrRunnerUnavailable = errors.New("account deletion runner unavailable")

type Worker interface {
	ProcessOnce(context.Context, int) (int, error)
}

type RunnerOptions struct {
	Interval      time.Duration
	MaxBackoff    time.Duration
	Jitter        time.Duration
	DisableJitter bool
	Timeout       time.Duration
	BatchSize     int
	RandomFloat64 func() float64
	Logger        *slog.Logger
}

type Runner struct {
	worker        Worker
	interval      time.Duration
	maxBackoff    time.Duration
	jitter        time.Duration
	timeout       time.Duration
	batchSize     int
	randomFloat64 func() float64
	logger        *slog.Logger
}

func NewRunner(worker Worker, options RunnerOptions) *Runner {
	if options.Interval < time.Second || options.Interval > 10*time.Minute {
		options.Interval = DefaultRunnerInterval
	}
	if options.MaxBackoff < options.Interval || options.MaxBackoff > time.Hour {
		options.MaxBackoff = DefaultRunnerMaxBackoff
		if options.MaxBackoff < options.Interval {
			options.MaxBackoff = options.Interval
		}
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
	if options.RandomFloat64 == nil {
		options.RandomFloat64 = rand.Float64
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		worker: worker, interval: options.Interval, maxBackoff: options.MaxBackoff,
		jitter: options.Jitter, timeout: options.Timeout, batchSize: options.BatchSize,
		randomFloat64: options.RandomFloat64, logger: options.Logger,
	}
}

func (r *Runner) RunOnce(ctx context.Context) (int, error) {
	if r == nil || r.worker == nil || ctx == nil {
		return 0, ErrRunnerUnavailable
	}
	passContext, cancel := context.WithTimeout(ctx, r.timeout)
	defer cancel()
	completed, err := r.worker.ProcessOnce(passContext, r.batchSize)
	if err != nil && passContext.Err() != nil {
		return completed, passContext.Err()
	}
	return completed, err
}

func (r *Runner) Run(ctx context.Context) {
	if r == nil || ctx == nil {
		return
	}
	failures := 0
	for {
		completed, err := r.RunOnce(ctx)
		if ctx.Err() != nil {
			return
		}
		if err != nil {
			failures++
			r.logger.Error("account deletion maintenance failed",
				"error_class", "ACCOUNT_DELETION_MAINTENANCE_FAILED", "completed", completed)
		} else {
			failures = 0
			if completed > 0 {
				r.logger.Info("account deletion maintenance completed", "completed", completed)
			}
		}
		timer := time.NewTimer(r.nextDelay(failures))
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
		}
	}
}

func (r *Runner) nextDelay(failures int) time.Duration {
	delay := r.interval
	for index := 0; index < failures && delay < r.maxBackoff; index++ {
		if delay > r.maxBackoff/2 {
			delay = r.maxBackoff
			break
		}
		delay *= 2
	}
	if r.jitter > 0 {
		randomValue := r.randomFloat64()
		if randomValue < 0 || randomValue > 1 {
			randomValue = 0.5
		}
		delay += time.Duration((randomValue*2 - 1) * float64(r.jitter))
	}
	if delay < time.Second {
		return time.Second
	}
	if delay > r.maxBackoff {
		return r.maxBackoff
	}
	return delay
}

var _ Worker = (*Service)(nil)

// Package sessioncleanup periodically tombstones expired server-side session
// metadata. It deliberately has no dependency on realtime or Windows control.
package sessioncleanup

import (
	"context"
	"log/slog"
	"time"
)

const (
	DefaultInterval       = time.Minute
	DefaultEndedRetention = 15 * time.Minute
	DefaultStaleRetention = 15 * time.Minute
	DefaultBatchSize      = 100
)

type Store interface {
	CleanupSessions(context.Context, time.Time, time.Time, int) (int, error)
}

type Options struct {
	Interval       time.Duration
	EndedRetention time.Duration
	StaleRetention time.Duration
	BatchSize      int
	Clock          func() time.Time
	Logger         *slog.Logger
}

type Runner struct {
	store          Store
	interval       time.Duration
	endedRetention time.Duration
	staleRetention time.Duration
	batchSize      int
	clock          func() time.Time
	logger         *slog.Logger
	newTicker      func(time.Duration) ticker
}

type ticker interface {
	Chan() <-chan time.Time
	Stop()
}

type realTicker struct{ *time.Ticker }

func (t realTicker) Chan() <-chan time.Time { return t.C }

func New(store Store, options Options) *Runner {
	if options.Interval <= 0 {
		options.Interval = DefaultInterval
	}
	if options.EndedRetention <= 0 {
		options.EndedRetention = DefaultEndedRetention
	}
	if options.StaleRetention <= 0 {
		options.StaleRetention = DefaultStaleRetention
	}
	if options.BatchSize <= 0 || options.BatchSize > DefaultBatchSize {
		options.BatchSize = DefaultBatchSize
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		store: store, interval: options.Interval, endedRetention: options.EndedRetention,
		staleRetention: options.StaleRetention, batchSize: options.BatchSize,
		clock: options.Clock, logger: options.Logger,
		newTicker: func(interval time.Duration) ticker { return realTicker{time.NewTicker(interval)} },
	}
}

// RunOnce applies one bounded cleanup batch. The cutoffs come from one clock
// sample so terminal and stale decisions are consistent within the pass.
func (r *Runner) RunOnce(ctx context.Context) (int, error) {
	now := r.clock().UTC()
	return r.store.CleanupSessions(
		ctx,
		now.Add(-r.endedRetention),
		now.Add(-r.staleRetention),
		r.batchSize,
	)
}

// Run performs an initial bounded pass, then one pass per interval until the
// server context is cancelled. Errors are logged and retried on the next tick.
func (r *Runner) Run(ctx context.Context) {
	r.runAndLog(ctx)
	ticker := r.newTicker(r.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.Chan():
			r.runAndLog(ctx)
		}
	}
}

func (r *Runner) runAndLog(ctx context.Context) {
	cleaned, err := r.RunOnce(ctx)
	if err != nil {
		if ctx.Err() == nil {
			r.logger.Error("automatic session cleanup failed", "error", err)
		}
		return
	}
	if cleaned > 0 {
		r.logger.Info("automatic session cleanup completed", "sessions", cleaned)
	}
}

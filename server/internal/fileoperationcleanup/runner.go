// Package fileoperationcleanup bounds retained PC file-operation metadata.
package fileoperationcleanup

import (
	"context"
	"log/slog"
	"time"

	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultInterval          = time.Minute
	DefaultTerminalRetention = 24 * time.Hour
	DefaultBatchSize         = 250
)

type Store interface {
	CleanupFileOperations(context.Context, time.Time, time.Time, int) (store.FileOperationCleanupResult, error)
}

type Options struct {
	Interval          time.Duration
	TerminalRetention time.Duration
	BatchSize         int
	Clock             func() time.Time
	Logger            *slog.Logger
}

type Runner struct {
	store             Store
	interval          time.Duration
	terminalRetention time.Duration
	batchSize         int
	clock             func() time.Time
	logger            *slog.Logger
	newTicker         func(time.Duration) ticker
}

type ticker interface {
	Chan() <-chan time.Time
	Stop()
}

type realTicker struct{ *time.Ticker }

func (t realTicker) Chan() <-chan time.Time { return t.C }

func New(repository Store, options Options) *Runner {
	if options.Interval <= 0 {
		options.Interval = DefaultInterval
	}
	if options.TerminalRetention <= 0 {
		options.TerminalRetention = DefaultTerminalRetention
	}
	if options.BatchSize <= 0 || options.BatchSize > store.MaxFileOperationCleanupBatch {
		options.BatchSize = DefaultBatchSize
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	if options.Logger == nil {
		options.Logger = slog.Default()
	}
	return &Runner{
		store: repository, interval: options.Interval,
		terminalRetention: options.TerminalRetention, batchSize: options.BatchSize,
		clock: options.Clock, logger: options.Logger,
		newTicker: func(interval time.Duration) ticker { return realTicker{time.NewTicker(interval)} },
	}
}

func (r *Runner) RunOnce(ctx context.Context) (store.FileOperationCleanupResult, error) {
	now := r.clock().UTC()
	return r.store.CleanupFileOperations(ctx, now, now.Add(-r.terminalRetention), r.batchSize)
}

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
	result, err := r.RunOnce(ctx)
	if err != nil {
		if ctx.Err() == nil {
			logCleanupFailure(r.logger, result, err)
		}
		return
	}
	if result.Expired > 0 || result.Deleted > 0 {
		r.logger.Info("file-operation cleanup completed", "expired", result.Expired, "deleted", result.Deleted)
	}
}

func logCleanupFailure(logger *slog.Logger, result store.FileOperationCleanupResult, _ error) {
	logger.Error("file-operation cleanup failed",
		"error_class", "file_operation_cleanup_failed",
		"expired", result.Expired,
		"deleted", result.Deleted)
}

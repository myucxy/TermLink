package api

import (
	"context"
	"log/slog"
)

// logAPIFailure emits only explicitly classified operational metadata. The
// error is deliberately accepted but never formatted or attached to the
// record: provider and storage errors may contain DSNs, credentials, or
// user-supplied data.
func logAPIFailure(ctx context.Context, level slog.Level, message, errorClass string, err error, resources ...slog.Attr) {
	logAPIFailureWith(slog.Default(), ctx, level, message, errorClass, err, resources...)
}

func logAPIFailureWith(logger *slog.Logger, ctx context.Context, level slog.Level, message, errorClass string, _ error, resources ...slog.Attr) {
	attrs := make([]slog.Attr, 0, 2+len(resources))
	attrs = append(attrs,
		slog.String("error_class", errorClass),
		slog.String("trace_id", traceID(ctx)),
	)
	attrs = append(attrs, resources...)
	logger.LogAttrs(ctx, level, message, attrs...)
}

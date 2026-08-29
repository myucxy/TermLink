package webportal

import (
	"context"
	"log/slog"
)

// logPortalFailure intentionally never formats err. OIDC, template, and store
// errors can carry tokens, provider responses, DSNs, or user-provided values.
func logPortalFailure(ctx context.Context, level slog.Level, message, errorClass, traceID string, err error, resources ...slog.Attr) {
	logPortalFailureWith(slog.Default(), ctx, level, message, errorClass, traceID, err, resources...)
}

func logPortalFailureWith(logger *slog.Logger, ctx context.Context, level slog.Level, message, errorClass, traceID string, _ error, resources ...slog.Attr) {
	attrs := make([]slog.Attr, 0, 2+len(resources))
	attrs = append(attrs,
		slog.String("error_class", errorClass),
		slog.String("trace_id", traceID),
	)
	attrs = append(attrs, resources...)
	logger.LogAttrs(ctx, level, message, attrs...)
}

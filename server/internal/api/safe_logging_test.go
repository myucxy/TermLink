package api

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"
)

type apiSensitiveError struct {
	text  string
	calls int
}

func (e *apiSensitiveError) Error() string {
	e.calls++
	return e.text
}

func TestLogAPIFailureDoesNotExposeErrorText(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	ctx := context.WithValue(context.Background(), traceKey{}, "trace-safe-1")
	secret := "postgres://admin:super-secret@database/termlink?sslmode=disable"
	sensitiveErr := &apiSensitiveError{text: secret}

	logAPIFailureWith(logger, ctx, slog.LevelError, "API storage operation failed",
		"storage_operation_failed", sensitiveErr, slog.String("session_id", "6d62c516-3163-413d-8725-e24f58d58977"))

	logged := output.String()
	if sensitiveErr.calls != 0 {
		t.Fatalf("error text was formatted %d times", sensitiveErr.calls)
	}
	if strings.Contains(logged, secret) || strings.Contains(logged, "super-secret") {
		t.Fatalf("sensitive error text reached the log: %s", logged)
	}
	for _, expected := range []string{`"error_class":"storage_operation_failed"`, `"trace_id":"trace-safe-1"`, `"session_id":"6d62c516-3163-413d-8725-e24f58d58977"`} {
		if !strings.Contains(logged, expected) {
			t.Fatalf("log does not contain %s: %s", expected, logged)
		}
	}
}

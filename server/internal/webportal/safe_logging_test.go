package webportal

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"
)

type portalSensitiveError struct {
	text  string
	calls int
}

func (e *portalSensitiveError) Error() string {
	e.calls++
	return e.text
}

func TestLogPortalFailureDoesNotExposeErrorText(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	secret := "client_secret=oidc-secret&code=authorization-code"
	sensitiveErr := &portalSensitiveError{text: secret}

	logPortalFailureWith(logger, context.Background(), slog.LevelWarn, "web OIDC callback rejected",
		"oidc_token_verification_failed", "login-trace-1", sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 {
		t.Fatalf("error text was formatted %d times", sensitiveErr.calls)
	}
	if strings.Contains(logged, secret) || strings.Contains(logged, "oidc-secret") || strings.Contains(logged, "authorization-code") {
		t.Fatalf("sensitive error text reached the log: %s", logged)
	}
	for _, expected := range []string{`"error_class":"oidc_token_verification_failed"`, `"trace_id":"login-trace-1"`} {
		if !strings.Contains(logged, expected) {
			t.Fatalf("log does not contain %s: %s", expected, logged)
		}
	}
}

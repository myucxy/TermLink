package main

import (
	"bytes"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

type sensitiveLogError struct {
	text  string
	calls int
}

func (e *sensitiveLogError) Error() string {
	e.calls++
	return e.text
}

func TestHandlerHealthEndpoints(t *testing.T) {
	handler := newHandler("termlink-test-token", t.TempDir())
	for _, path := range []string{"/healthz", "/readyz"} {
		t.Run(path, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodGet, path, nil)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != http.StatusOK {
				t.Fatalf("GET %s returned %d, want %d", path, response.Code, http.StatusOK)
			}
		})
	}
}

func TestLANServerFailureLogDoesNotFormatError(t *testing.T) {
	var output bytes.Buffer
	logger := slog.New(slog.NewJSONHandler(&output, nil))
	sensitiveErr := &sensitiveLogError{text: "TERMLINK_TEST_TOKEN=lan-secret"}

	logLANServerFailure(logger, "lan_test_server_failed", sensitiveErr)

	logged := output.String()
	if sensitiveErr.calls != 0 || strings.Contains(logged, "lan-secret") {
		t.Fatalf("LAN server log formatted or exposed the error: calls=%d log=%s", sensitiveErr.calls, logged)
	}
	if !strings.Contains(logged, `"error_class":"lan_test_server_failed"`) {
		t.Fatalf("LAN server log is missing stable error class: %s", logged)
	}
}

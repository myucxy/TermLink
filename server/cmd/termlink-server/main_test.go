package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestOptionalHistoryStoreDoesNotWrapTypedNil(t *testing.T) {
	if optionalHistoryStore(nil) != nil {
		t.Fatal("disabled history store became a non-nil interface")
	}
}

func TestLANTestHandlerHealthEndpoints(t *testing.T) {
	handler := newLANTestHandler("termlink-test-token", t.TempDir())
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

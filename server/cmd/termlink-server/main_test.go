package main

import (
	"bytes"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestOptionalHistoryStoreDoesNotWrapTypedNil(t *testing.T) {
	if optionalHistoryStore(nil) != nil {
		t.Fatal("disabled history store became a non-nil interface")
	}
}

func TestDeriveServerKeySeparatesSecurityDomains(t *testing.T) {
	master := "0123456789abcdef0123456789abcdef"
	subject := deriveServerKey(master, "oidc-subject-hash")
	token := deriveServerKey(master, "capability-token-signing")
	cookie := deriveServerKey(master, "web-cookie-master")
	if len(subject) != 32 || len(token) != 32 || len(cookie) != 32 {
		t.Fatalf("derived key lengths = %d/%d/%d", len(subject), len(token), len(cookie))
	}
	if bytes.Equal(subject, token) || bytes.Equal(subject, cookie) || bytes.Equal(token, cookie) {
		t.Fatal("distinct security domains produced the same derived key")
	}
	if !bytes.Equal(subject, deriveServerKey(master, "oidc-subject-hash")) {
		t.Fatal("server key derivation is not deterministic")
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

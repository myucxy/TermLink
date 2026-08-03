package history

import (
	"context"
	"crypto/sha256"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestHistoryObjectMetadataIsBounded(t *testing.T) {
	digest := sha256.Sum256([]byte("ciphertext"))
	if err := validateObject("accounts/a/sessions/s/chunks/c.bin", 10, digest[:]); err != nil {
		t.Fatalf("valid metadata rejected: %v", err)
	}
	for _, key := range []string{"sessions/s/c.bin", "accounts/../secret", `accounts/a\\secret`} {
		if err := validateObject(key, 10, digest[:]); err == nil {
			t.Fatalf("unsafe object key accepted: %q", key)
		}
	}
	if err := validateObject("accounts/a/c.bin", MaxObjectBytes+1, digest[:]); err == nil {
		t.Fatal("oversized object accepted")
	}
}

func TestOpenUsesPublicEndpointOnlyForPresignedRequests(t *testing.T) {
	internalRequests := make(chan struct{}, 1)
	internal := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			internalRequests <- struct{}{}
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer internal.Close()
	public := httptest.NewServer(http.NotFoundHandler())
	defer public.Close()

	store, err := Open(context.Background(), internal.URL, public.URL, "us-east-1", "termlink-history", "access-key", "secret-key")
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	presigned, err := store.PresignGet(context.Background(), "accounts/a/sessions/s/chunks/c.bin", time.Minute)
	if err != nil {
		t.Fatalf("PresignGet() error = %v", err)
	}
	if !strings.HasPrefix(presigned.URL, public.URL+"/termlink-history/accounts/") {
		t.Fatalf("presigned URL = %q, want public endpoint %q", presigned.URL, public.URL)
	}
	select {
	case <-internalRequests:
	case <-time.After(time.Second):
		t.Fatal("Open() did not use internal endpoint for bucket verification")
	}
}

func TestOpenDefaultsEmptyPublicEndpointToInternal(t *testing.T) {
	internal := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	defer internal.Close()

	store, err := Open(context.Background(), internal.URL, "", "us-east-1", "termlink-history", "access-key", "secret-key")
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	presigned, err := store.PresignGet(context.Background(), "accounts/a/sessions/s/chunks/c.bin", time.Minute)
	if err != nil {
		t.Fatalf("PresignGet() error = %v", err)
	}
	if !strings.HasPrefix(presigned.URL, internal.URL+"/termlink-history/accounts/") {
		t.Fatalf("presigned URL = %q, want internal endpoint %q", presigned.URL, internal.URL)
	}
}

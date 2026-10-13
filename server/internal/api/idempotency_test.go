package api

import (
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestResponseMayHaveBody(t *testing.T) {
	tests := []struct {
		method string
		status int
		want   bool
	}{
		{http.MethodPost, http.StatusOK, true},
		{http.MethodDelete, http.StatusNoContent, false},
		{http.MethodGet, http.StatusNotModified, false},
		{http.MethodHead, http.StatusOK, false},
		{http.MethodGet, http.StatusContinue, false},
	}
	for _, test := range tests {
		if got := responseMayHaveBody(test.method, test.status); got != test.want {
			t.Fatalf("responseMayHaveBody(%q, %d) = %t, want %t", test.method, test.status, got, test.want)
		}
	}
}

func TestIsCurrentPCUpsertRequest(t *testing.T) {
	const pcID = "11111111-1111-1111-1111-111111111111"
	tests := []struct {
		method string
		path   string
		want   bool
	}{
		{http.MethodPut, "/v1/pcs/" + pcID, true},
		{http.MethodGet, "/v1/pcs/" + pcID, false},
		{http.MethodPut, "/v1/pcs/" + pcID + "/session-snapshot", false},
		{http.MethodPut, "/v1/pcs/", false},
		{http.MethodPut, "/v1/pcs/" + pcID + "/", false},
		{http.MethodPut, "/v1/pcs/not-a-uuid", false},
		{http.MethodPut, "/v1/pcsx/" + pcID, false},
	}
	for _, test := range tests {
		t.Run(fmt.Sprintf("%s_%s", test.method, test.path), func(t *testing.T) {
			request := httptest.NewRequest(test.method, test.path, nil)
			if got := isCurrentPCUpsertRequest(request); got != test.want {
				t.Fatalf("isCurrentPCUpsertRequest(%s %s) = %t, want %t", test.method, test.path, got, test.want)
			}
		})
	}
}

func TestIdempotencyDoesNotReplayPCUpsertState(t *testing.T) {
	var calls int
	var observed []string
	handler := (&API{}).idempotency(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Fatalf("read forwarded body: %v", err)
		}
		observed = append(observed, string(body))
		if calls == 1 {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	bodies := []string{`{"state":"A"}`, `{"state":"B"}`, `{"state":"A"}`}
	for index, wantStatus := range []int{http.StatusBadRequest, http.StatusOK, http.StatusOK} {
		request := httptest.NewRequest(http.MethodPut, "/v1/pcs/11111111-1111-1111-1111-111111111111", strings.NewReader(bodies[index]))
		request.Header.Set("Idempotency-Key", "fixed-state-key")
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != wantStatus {
			t.Fatalf("request %d status = %d, want %d", index+1, response.Code, wantStatus)
		}
	}
	if calls != 3 {
		t.Fatalf("PC state handler calls = %d, want 3", calls)
	}
	if strings.Join(observed, "\x00") != strings.Join(bodies, "\x00") {
		t.Fatalf("PC state bodies = %#v, want %#v", observed, bodies)
	}
}

func TestIdempotencyStillRequiresKeyForPCUpsertState(t *testing.T) {
	called := false
	handler := (&API{}).idempotency(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		called = true
	}))
	request := httptest.NewRequest(http.MethodPut, "/v1/pcs/11111111-1111-1111-1111-111111111111", nil)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || called {
		t.Fatalf("missing key status=%d called=%t, want 400 and false", response.Code, called)
	}
}

func TestIdempotencyStillRejectsInvalidPCUpsertStateInputs(t *testing.T) {
	tests := []struct {
		name string
		key  string
		body string
	}{
		{name: "key too long", key: strings.Repeat("k", 129)},
		{name: "body too large", key: "valid-state-key", body: strings.Repeat("x", maxJSONBody+1)},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			called := false
			handler := (&API{}).idempotency(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
				called = true
			}))
			request := httptest.NewRequest(http.MethodPut, "/v1/pcs/11111111-1111-1111-1111-111111111111", strings.NewReader(test.body))
			request.Header.Set("Idempotency-Key", test.key)
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != http.StatusBadRequest || called {
				t.Fatalf("status=%d called=%t, want 400 and false", response.Code, called)
			}
		})
	}
}

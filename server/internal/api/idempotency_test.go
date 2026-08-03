package api

import (
	"net/http"
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

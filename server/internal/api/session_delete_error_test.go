package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/termlink/termlink/server/internal/store"
)

func TestWriteStoreErrorMapsActiveSessionToConflict(t *testing.T) {
	request := httptest.NewRequest(http.MethodDelete, "/v1/sessions/session-id", nil)
	response := httptest.NewRecorder()

	writeStoreError(response, request, store.ErrSessionActive)

	if response.Code != http.StatusConflict {
		t.Fatalf("status=%d, want %d", response.Code, http.StatusConflict)
	}
	var body struct {
		Error struct {
			Code string `json:"code"`
		} `json:"error"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	if body.Error.Code != "SESSION_ACTIVE" {
		t.Fatalf("error code=%q, want SESSION_ACTIVE", body.Error.Code)
	}
}

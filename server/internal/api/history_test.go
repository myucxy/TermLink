package api

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestCreateHistoryUploadReturnsUnavailableWhenObjectStoreIsDisabled(t *testing.T) {
	request := httptest.NewRequest(http.MethodPost, "/v1/sessions/session/history/uploads", strings.NewReader(`{}`))
	response := httptest.NewRecorder()

	(&API{}).createHistoryUpload(response, request)

	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want %d", response.Code, http.StatusServiceUnavailable)
	}
	if !strings.Contains(response.Body.String(), `"code":"HISTORY_UNAVAILABLE"`) {
		t.Fatalf("response did not contain stable history error: %s", response.Body.String())
	}
}

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

func TestRetiredTerminalHistoryRequiresHostHistoryUpgrade(t *testing.T) {
	for _, test := range []struct {
		method string
		path   string
	}{
		{method: http.MethodGet, path: "/v1/sessions/session/history"},
		{method: http.MethodPost, path: "/v1/sessions/session/history/uploads"},
		{method: http.MethodPost, path: "/v1/sessions/session/history/uploads/upload/commit"},
	} {
		request := httptest.NewRequest(test.method, test.path, nil)
		response := httptest.NewRecorder()

		retiredTerminalHistory(response, request)

		if response.Code != http.StatusUpgradeRequired {
			t.Fatalf("%s %s status = %d, want %d", test.method, test.path, response.Code, http.StatusUpgradeRequired)
		}
		if response.Header().Get("Upgrade") != "host_history_v1" {
			t.Fatalf("%s %s Upgrade = %q", test.method, test.path, response.Header().Get("Upgrade"))
		}
		if !strings.Contains(response.Body.String(), `"code":"CLIENT_UPGRADE_REQUIRED"`) {
			t.Fatalf("%s %s response did not contain the upgrade error: %s", test.method, test.path, response.Body.String())
		}
	}
}

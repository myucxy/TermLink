package api

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestConnectionFailureRetainsCodeTraceAndAddsSafeStage(t *testing.T) {
	for _, test := range []struct{ method, path, stage string }{
		{"GET", "/v1/client-config", "configuration"},
		{"POST", "/v1/devices/register", "device_registration"},
		{"GET", "/v1/devices/self", "device_identity"},
		{"PUT", "/v1/pcs/" + uuid.NewString(), "pc_metadata"},
		{"PUT", "/v1/pcs/" + uuid.NewString() + "/remote-routing", ""},
		{"PUT", "/v1/devices/self/push", "push_registration"},
	} {
		req := httptest.NewRequest(test.method, test.path, nil)
		res := httptest.NewRecorder()
		traceMiddleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { writeError(w, r, 400, "INVALID_REQUEST", nil) })).ServeHTTP(res, req)
		var data map[string]map[string]string
		if err := json.Unmarshal(res.Body.Bytes(), &data); err != nil {
			t.Fatal(err)
		}
		if data["error"]["code"] != "INVALID_REQUEST" || data["error"]["stage"] != test.stage || data["error"]["trace_id"] == "" {
			t.Fatalf("wrong error %s", res.Body.String())
		}
		if strings.Contains(res.Body.String(), test.path) {
			t.Fatal("path exposed")
		}
	}
}

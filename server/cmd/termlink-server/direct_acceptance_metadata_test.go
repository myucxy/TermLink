package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strconv"
	"strings"
	"testing"
)

const testDirectAcceptanceBuildSHA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

func TestDirectAcceptanceMetadataDisabledIsNotRegistered(t *testing.T) {
	tests := []struct {
		name string
		gate string
	}{
		{name: "default"},
		{name: "production false", gate: "false"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			t.Setenv(directAcceptanceMetadataEnableEnv, test.gate)
			t.Setenv(directAcceptanceMetadataBuildEnv, "not-a-digest")
			config, err := loadDirectAcceptanceMetadataConfig("https://production.example.invalid/path")
			if err != nil || config != nil {
				t.Fatalf("disabled config = %#v, %v; want nil, nil", config, err)
			}
			mux := http.NewServeMux()
			if err := registerDirectAcceptanceMetadataRoute(mux, config, true, true); err != nil {
				t.Fatal(err)
			}
			fallback := http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(http.StatusUnauthorized)
			})
			hidden := hideDirectAcceptanceMetadataWhenDisabled(config, fallback)
			for _, request := range []struct{ method, uri string }{
				{method: http.MethodGet, uri: directAcceptanceMetadataPath},
				{method: http.MethodPost, uri: directAcceptanceMetadataPath},
				{method: http.MethodGet, uri: directAcceptanceMetadataPath + "?probe=1"},
			} {
				response := serveMetadataRequest(hidden, request.method, request.uri)
				if response.Code != http.StatusNotFound {
					t.Fatalf("disabled endpoint %s %s status = %d, want 404", request.method, request.uri, response.Code)
				}
			}
			other := serveMetadataRequest(hidden, http.MethodGet, "/v1/other")
			if other.Code != http.StatusUnauthorized {
				t.Fatalf("unrelated fallback status = %d, want 401", other.Code)
			}
		})
	}
}

func TestDirectAcceptanceMetadataRejectsInvalidEnabledConfiguration(t *testing.T) {
	t.Setenv(directAcceptanceMetadataEnableEnv, "true")
	tests := []struct {
		name   string
		origin string
		digest string
	}{
		{name: "HTTP origin", origin: "http://acceptance.example.invalid", digest: testDirectAcceptanceBuildSHA},
		{name: "origin path", origin: "https://acceptance.example.invalid/", digest: testDirectAcceptanceBuildSHA},
		{name: "origin query", origin: "https://acceptance.example.invalid?mode=test", digest: testDirectAcceptanceBuildSHA},
		{name: "origin fragment", origin: "https://acceptance.example.invalid#fragment", digest: testDirectAcceptanceBuildSHA},
		{name: "origin user info", origin: "https://user@acceptance.example.invalid", digest: testDirectAcceptanceBuildSHA},
		{name: "short digest", origin: "https://acceptance.example.invalid", digest: strings.Repeat("a", 63)},
		{name: "uppercase digest", origin: "https://acceptance.example.invalid", digest: strings.Repeat("A", 64)},
		{name: "non hexadecimal digest", origin: "https://acceptance.example.invalid", digest: strings.Repeat("g", 64)},
		{name: "padded digest", origin: "https://acceptance.example.invalid", digest: " " + testDirectAcceptanceBuildSHA},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			t.Setenv(directAcceptanceMetadataBuildEnv, test.digest)
			if config, err := loadDirectAcceptanceMetadataConfig(test.origin); err == nil || config != nil {
				t.Fatalf("invalid enabled config = %#v, %v; want fail-closed error", config, err)
			}
		})
	}

	t.Setenv(directAcceptanceMetadataEnableEnv, "sometimes")
	t.Setenv(directAcceptanceMetadataBuildEnv, testDirectAcceptanceBuildSHA)
	if config, err := loadDirectAcceptanceMetadataConfig("https://acceptance.example.invalid"); err == nil || config != nil {
		t.Fatalf("invalid gate config = %#v, %v; want fail-closed error", config, err)
	}
}

func TestDirectAcceptanceMetadataExactResponseAndRouteContract(t *testing.T) {
	t.Setenv(directAcceptanceMetadataEnableEnv, "true")
	t.Setenv(directAcceptanceMetadataBuildEnv, testDirectAcceptanceBuildSHA)
	config, err := loadDirectAcceptanceMetadataConfig("https://termlink.example.invalid:9443")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	if err = registerDirectAcceptanceMetadataRoute(mux, config, true, true); err != nil {
		t.Fatal(err)
	}

	wantBody := `{"schema_version":1,"kind":"termlink.server-direct-acceptance-metadata","environment":"acceptance","origin":"https://termlink.example.invalid:9443","build_sha256":"` + testDirectAcceptanceBuildSHA + `","direct_transport_enabled":true,"direct_routing_permitted":true,"capabilities":["direct_transport_shadow_v1","direct_transport_shadow_multipair_v2","direct_transport_data_v1","direct_session_cutover_v1"]}`
	response := serveMetadataRequest(mux, http.MethodGet, directAcceptanceMetadataPath)
	if response.Code != http.StatusOK || response.Body.String() != wantBody {
		t.Fatalf("response = %d %q, want 200 %q", response.Code, response.Body.String(), wantBody)
	}
	if response.Header().Get("Content-Type") != "application/json" ||
		response.Header().Get("Cache-Control") != "no-store" ||
		response.Header().Get("Content-Length") != strconv.Itoa(len(wantBody)) {
		t.Fatalf("unsafe response headers: %#v", response.Header())
	}
	if response.Body.Len() >= directAcceptanceMetadataMaxBytes {
		t.Fatalf("response length = %d, want under %d", response.Body.Len(), directAcceptanceMetadataMaxBytes)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(response.Body.Bytes(), &fields); err != nil {
		t.Fatal(err)
	}
	wantFields := []string{
		"schema_version", "kind", "environment", "origin", "build_sha256",
		"direct_transport_enabled", "direct_routing_permitted", "capabilities",
	}
	if len(fields) != len(wantFields) {
		t.Fatalf("metadata fields = %v", fields)
	}
	for _, field := range wantFields {
		if _, ok := fields[field]; !ok {
			t.Fatalf("metadata omitted %q", field)
		}
	}
	for _, forbidden := range []string{"token", "account", "hostname", "ip_address", "path", "key", "secret", "error", "exception"} {
		if _, ok := fields[forbidden]; ok {
			t.Fatalf("metadata exposed forbidden field %q", forbidden)
		}
	}

	badRequests := []struct {
		name   string
		method string
		uri    string
		status int
	}{
		{name: "method", method: http.MethodPost, uri: directAcceptanceMetadataPath, status: http.StatusMethodNotAllowed},
		{name: "query", method: http.MethodGet, uri: directAcceptanceMetadataPath + "?debug=1", status: http.StatusNotFound},
		{name: "suffix", method: http.MethodGet, uri: directAcceptanceMetadataPath + "/extra", status: http.StatusNotFound},
		{name: "nearby path", method: http.MethodGet, uri: "/v1/acceptance/direct_metadata", status: http.StatusNotFound},
	}
	for _, test := range badRequests {
		t.Run(test.name, func(t *testing.T) {
			got := serveMetadataRequest(mux, test.method, test.uri)
			if got.Code != test.status {
				t.Fatalf("status = %d, want %d", got.Code, test.status)
			}
		})
	}
}

func TestDirectAcceptanceMetadataCapabilitiesTrackActualGates(t *testing.T) {
	tests := []struct {
		name         string
		transport    bool
		routing      bool
		wantRouting  bool
		capabilities []string
	}{
		{name: "transport unavailable", routing: true, capabilities: []string{}},
		{name: "shadow only", transport: true, capabilities: []string{"direct_transport_shadow_v1", "direct_transport_shadow_multipair_v2"}},
		{name: "routing bundle", transport: true, routing: true, wantRouting: true, capabilities: []string{"direct_transport_shadow_v1", "direct_transport_shadow_multipair_v2", "direct_transport_data_v1", "direct_session_cutover_v1"}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			mux := http.NewServeMux()
			config := &directAcceptanceMetadataConfig{origin: "https://acceptance.example.invalid", buildSHA256: testDirectAcceptanceBuildSHA}
			if err := registerDirectAcceptanceMetadataRoute(mux, config, test.transport, test.routing); err != nil {
				t.Fatal(err)
			}
			response := serveMetadataRequest(mux, http.MethodGet, directAcceptanceMetadataPath)
			var metadata directAcceptanceMetadata
			if err := json.Unmarshal(response.Body.Bytes(), &metadata); err != nil {
				t.Fatal(err)
			}
			if metadata.DirectTransportEnabled != test.transport || metadata.DirectRoutingPermitted != test.wantRouting ||
				!reflect.DeepEqual(metadata.Capabilities, test.capabilities) {
				t.Fatalf("metadata gates = transport:%t routing:%t capabilities:%v", metadata.DirectTransportEnabled, metadata.DirectRoutingPermitted, metadata.Capabilities)
			}
			hasData := containsMetadataCapability(metadata.Capabilities, "direct_transport_data_v1")
			hasCutover := containsMetadataCapability(metadata.Capabilities, "direct_session_cutover_v1")
			if hasData != hasCutover || hasData != metadata.DirectRoutingPermitted {
				t.Fatal("routing capability bundle is inconsistent")
			}
		})
	}
}

func serveMetadataRequest(handler http.Handler, method, uri string) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, uri, nil)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}

func containsMetadataCapability(capabilities []string, wanted string) bool {
	for _, capability := range capabilities {
		if capability == wanted {
			return true
		}
	}
	return false
}

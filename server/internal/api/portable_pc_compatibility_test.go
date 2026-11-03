package api

import (
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/presence"
	"github.com/termlink/termlink/server/internal/realtime"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPortablePCControlConnectionMetadata(t *testing.T) {
	for _, platform := range []string{store.PCPlatformMacOS, store.PCPlatformLinux} {
		for _, architecture := range []string{store.PCArchitectureArm64, store.PCArchitectureX64} {
			t.Run(platform+"/"+architecture, func(t *testing.T) {
				gotPlatform, gotArchitecture, _, capabilities, err := normalizePCAgentMetadata(
					store.DeviceTypePCAgent, platform, architecture, "portable validation",
					[]string{}, false, false, []string{}, []store.TerminalCommandCapability{},
				)
				if err != nil {
					t.Fatalf("control-only portable metadata rejected: %v", err)
				}
				if gotPlatform != platform || gotArchitecture != architecture || capabilities == nil || len(capabilities) != 0 {
					t.Fatalf("portable metadata changed platform/architecture or inferred capabilities: %q %q %#v", gotPlatform, gotArchitecture, capabilities)
				}
			})
		}
	}
}

func TestPortablePCAIWithoutTerminal(t *testing.T) {
	for _, platform := range []string{"macos", "linux"} {
		_, _, _, features, err := normalizePCAgentMetadata(store.DeviceTypePCAgent, platform, "arm64", "validation",
			[]string{"ai_agent_v1"}, false, false, []string{}, []store.TerminalCommandCapability{})
		if err != nil || !reflect.DeepEqual(features, []string{"ai_agent_v1"}) {
			t.Fatalf("AI-only host: %v %#v", err, features)
		}
		for _, extra := range []string{store.PCCapabilityFileOperationsV1, store.PCCapabilityTerminalSnapshotV1, "future"} {
			if _, _, _, _, err := normalizePCAgentMetadata(store.DeviceTypePCAgent, platform, "arm64", "validation",
				[]string{"ai_agent_v1", extra}, false, false, nil, nil); err == nil {
				t.Fatalf("accepted unsupported mix %q", extra)
			}
		}
	}
}

func TestPortablePCControlMetadataRequiresExplicitEmptyFeatures(t *testing.T) {
	for _, test := range []struct {
		name, deviceType, platform string
		capabilities               []string
		heartbeat, update          bool
		shells                     []string
		commands                   []store.TerminalCommandCapability
	}{
		{name: "omitted capabilities", deviceType: store.DeviceTypePCAgent, platform: "macos"},
		{name: "session heartbeat", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{}, heartbeat: true},
		{name: "remote update", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{}, update: true},
		{name: "shells", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{}, shells: []string{"bash"}},
		{name: "commands", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{}, commands: []store.TerminalCommandCapability{{ID: "bash"}}},
		{name: "file only", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{store.PCCapabilityFileOperationsV1}},
		{name: "snapshot only", deviceType: store.DeviceTypePCAgent, platform: "macos", capabilities: []string{store.PCCapabilityTerminalSnapshotV1}},
		{name: "windows contract", deviceType: store.DeviceTypePCAgent, platform: "windows", capabilities: []string{}},
		{name: "legacy contract", deviceType: store.DeviceTypeLegacyWindowsPC, platform: "windows", capabilities: []string{}},
		{name: "legacy cannot become Mac", deviceType: store.DeviceTypeLegacyWindowsPC, platform: "macos", capabilities: []string{}},
		{name: "unknown device", deviceType: "ANDROID", platform: "macos", capabilities: []string{}},
	} {
		t.Run(test.name, func(t *testing.T) {
			_, _, _, _, err := normalizePCAgentMetadata(test.deviceType, test.platform, "arm64", "validation",
				test.capabilities, test.heartbeat, test.update, test.shells, test.commands)
			if err == nil {
				t.Fatal("inconsistent control-only metadata was accepted")
			}
		})
	}
}

// The .NET connection fixture does not execute Go handlers. Exercise its exact
// Hello and ping against the real signed-request middleware and Relay here.
func TestPortablePCSignedRelayHandshakeAndLiveness(t *testing.T) {
	user, device := uuid.New(), uuid.New()
	now := time.Now().UTC()
	authentication, key := fileOperationTestAuth(t, user, device, store.DeviceTypePCAgent, now)
	hub := realtime.NewHub(nil, nil, nil)
	hub.SetPresence(presence.New(time.Minute))
	server := httptest.NewServer(authentication.Authenticate(hub.Handler()))
	defer server.Close()
	request := signedFileOperationRequest(t, key, device, now, http.MethodGet, "/v1/realtime", nil)
	dialer := websocket.Dialer{Subprotocols: []string{"termlink.realtime.v1"}, HandshakeTimeout: 3 * time.Second}
	socket, response, err := dialer.Dial("ws"+strings.TrimPrefix(server.URL, "http")+"/v1/realtime", request.Header)
	if response != nil && response.Body != nil {
		defer response.Body.Close()
	}
	if err != nil {
		t.Fatal(err)
	}
	defer socket.Close()
	if socket.Subprotocol() != "termlink.realtime.v1" {
		t.Fatal("portable PC subprotocol was not negotiated")
	}
	capabilities := []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1", "liveness_probe_v1"}
	if err := socket.WriteJSON(map[string]any{"role": "pc", "protocol_versions": []int{1}, "capabilities": capabilities}); err != nil {
		t.Fatal(err)
	}
	if err := socket.SetReadDeadline(time.Now().Add(3 * time.Second)); err != nil {
		t.Fatal(err)
	}
	var welcome struct {
		Type             string   `json:"type"`
		ProtocolVersion  int      `json:"protocol_version"`
		ConnectionID     string   `json:"connection_id"`
		HeartbeatSeconds int      `json:"heartbeat_seconds"`
		Capabilities     []string `json:"capabilities"`
	}
	if err := socket.ReadJSON(&welcome); err != nil {
		t.Fatal(err)
	}
	connection, err := uuid.Parse(welcome.ConnectionID)
	if err != nil || connection == uuid.Nil || welcome.Type != "welcome" || welcome.ProtocolVersion != 1 ||
		welcome.HeartbeatSeconds < 1 || welcome.HeartbeatSeconds > 120 || !reflect.DeepEqual(welcome.Capabilities, capabilities) {
		t.Fatalf("Welcome is incompatible with portable PC: %#v", welcome)
	}
	probe := uuid.NewString()
	if err := socket.WriteJSON(map[string]string{"type": "ping", "probe_id": probe}); err != nil {
		t.Fatal(err)
	}
	var pong map[string]string
	if err := socket.ReadJSON(&pong); err != nil || pong["type"] != "pong" || pong["probe_id"] != probe {
		t.Fatalf("portable PC liveness failed: %v %#v", err, pong)
	}
}

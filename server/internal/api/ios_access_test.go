package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/filetransfer"
	"github.com/termlink/termlink/server/internal/presence"
	"github.com/termlink/termlink/server/internal/realtime"
	"github.com/termlink/termlink/server/internal/store"
)

func TestClientConfigurationPublishesOptInIOSWithoutChangingLegacyFields(t *testing.T) {
	service := &API{ClientConfig: ClientConfiguration{
		OIDCIssuer: "https://service.test/realms/termlink", OIDCClientID: "android",
		RedirectURI: "dev.termlink:/oauth2redirect", WindowsClientID: "windows",
		WindowsRedirectURI: "http://127.0.0.1/oauth2/callback", WebClientID: "web", WebRedirectURI: "https://service.test/auth/callback",
	}}
	for _, enabled := range []bool{false, true} {
		if enabled {
			service.ClientConfig.IOSClientID = "ios"
			service.ClientConfig.IOSRedirectURI = "dev.termlink.ios:/oauth2redirect"
		}
		response := httptest.NewRecorder()
		service.getClientConfiguration(response, httptest.NewRequest(http.MethodGet, "/v1/client-config", nil))
		var config map[string]any
		if response.Code != http.StatusOK || json.Unmarshal(response.Body.Bytes(), &config) != nil {
			t.Fatal("client configuration is unavailable")
		}
		if config["oidc_client_id"] != "android" || config["windows_oidc_client_id"] != "windows" || config["web_oidc_client_id"] != "web" {
			t.Fatal("iOS enablement changed existing clients")
		}
		if config["ios_oidc_client_id"] != service.ClientConfig.IOSClientID || config["ios_redirect_uri"] != service.ClientConfig.IOSRedirectURI {
			t.Fatal("iOS public configuration does not match the configured opt-in identity")
		}
	}
}

// This uses the real signed-request middleware and WebSocket handlers with an
// in-memory identity/route fixture. Database registration is tested separately.
func TestIOSAuthenticatesToMobileRelayAndFileTransferWithoutPCRole(t *testing.T) {
	for _, channel := range []string{"realtime", "files"} {
		for _, role := range []string{"android", "pc"} {
			t.Run(channel+"/"+role, func(t *testing.T) {
				user, mobile, pc, transfer := uuid.New(), uuid.New(), uuid.New(), uuid.New()
				now := time.Now().UTC()
				authentication, key := fileOperationTestAuth(t, user, mobile, "IOS", now)
				var handler http.Handler
				path, protocol := "/v1/realtime", "termlink.realtime.v1"
				capabilities, version := []string{"cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1"}, 1
				if channel == "realtime" {
					hub := realtime.NewHub(nil, nil, nil)
					hub.SetPresence(presence.New(time.Minute))
					handler = hub.Handler()
				} else {
					path, protocol = "/v1/file-transfers", "termlink.file-transfer.v2"
					capabilities, version = []string{"e2ee", "resume_offset_v2"}, 2
					hub := filetransfer.NewHub(iosTransferRoute{user: user, route: store.FileTransferRouteAuthorization{
						TransferID: transfer, RequesterDevice: mobile, PCDevice: pc,
						Status: store.FileOperationPending, ExpiresAt: now.Add(time.Hour),
					}}, nil)
					handler = hub.Handler()
				}
				server := httptest.NewServer(authentication.Authenticate(handler))
				defer server.Close()
				request := signedFileOperationRequest(t, key, mobile, now, http.MethodGet, path, nil)
				dialer := websocket.Dialer{Subprotocols: []string{protocol}, HandshakeTimeout: 3 * time.Second}
				socket, response, err := dialer.Dial("ws"+strings.TrimPrefix(server.URL, "http")+path, request.Header)
				if err != nil {
					if response != nil {
						response.Body.Close()
					}
					t.Fatal(err)
				}
				defer socket.Close()
				if socket.Subprotocol() != protocol {
					t.Fatal("wrong negotiated subprotocol")
				}
				hello := map[string]any{"role": role, "protocol_versions": []int{version}, "capabilities": capabilities}
				if channel == "files" {
					hello["transfer_id"] = transfer.String()
				}
				if err = socket.WriteJSON(hello); err != nil {
					t.Fatal(err)
				}
				socket.SetReadDeadline(time.Now().Add(3 * time.Second))
				var welcome map[string]any
				err = socket.ReadJSON(&welcome)
				if role == "pc" {
					if !websocket.IsCloseError(err, websocket.ClosePolicyViolation) {
						t.Fatalf("iOS PC impersonation was not rejected: %v", err)
					}
					return
				}
				if err != nil || welcome["protocol_version"] != float64(version) {
					t.Fatalf("iOS mobile handshake failed: %v, %v", err, welcome)
				}
			})
		}
	}
}

type iosTransferRoute struct {
	user  uuid.UUID
	route store.FileTransferRouteAuthorization
}

func (s iosTransferRoute) FileTransferRoute(_ context.Context, user, transfer uuid.UUID) (store.FileTransferRouteAuthorization, error) {
	if s.user != user || s.route.TransferID != transfer {
		return store.FileTransferRouteAuthorization{}, store.ErrDenied
	}
	return s.route, nil
}

func TestIOSMobileHTTPRoutesAllowFilesAndUpdateStatus(t *testing.T) {
	user, mobile, pc := uuid.New(), uuid.New(), uuid.New()
	now := time.Now().UTC()
	authentication, key := fileOperationTestAuth(t, user, mobile, "IOS", now)
	files := &fakeFileOperationStore{record: store.FileOperationRecord{ID: uuid.New(), PCID: pc, Operation: store.FileOperationList}}
	updates := &fakeAgentUpdateStore{}
	service := &API{fileOperations: files, agentUpdates: updates, Clock: func() time.Time { return now }}
	path := "/v1/pcs/" + pc.String() + "/file-operations"
	request := signedFileOperationRequest(t, key, mobile, now, http.MethodPost, path, []byte(`{"operation":"LIST","path":"C:\\work"}`))
	request.SetPathValue("pcId", pc.String())
	response := httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(service.createFileOperation)).ServeHTTP(response, request)
	if response.Code != http.StatusCreated || files.createInput.Path != `C:\work` {
		t.Fatalf("iOS file browse rejected: %d %s", response.Code, response.Body.String())
	}
	path = "/v1/pcs/" + pc.String() + "/agent-updates/active"
	request = signedFileOperationRequest(t, key, mobile, now, http.MethodGet, path, nil)
	request.SetPathValue("pcId", pc.String())
	response = httptest.NewRecorder()
	authentication.Authenticate(http.HandlerFunc(service.getActiveAgentUpdate)).ServeHTTP(response, request)
	if response.Code != http.StatusNoContent || updates.activeCalls != 1 || updates.activeRequester != mobile {
		t.Fatalf("iOS update recovery rejected: %d %s", response.Code, response.Body.String())
	}
}

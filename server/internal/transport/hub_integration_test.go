package transport

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/store"
)

type fixedTransportVerifier struct{ identity auth.Identity }

func (v fixedTransportVerifier) Verify(context.Context, string) (auth.Identity, error) {
	return v.identity, nil
}

type transportAuthDevices struct{ devices map[uuid.UUID]auth.Device }

func (d transportAuthDevices) LoadDeviceForSubject(_ context.Context, _ [32]byte, id uuid.UUID) (auth.Device, error) {
	device, ok := d.devices[id]
	if !ok {
		return auth.Device{}, store.ErrNotFound
	}
	return device, nil
}

type acceptTransportNonces struct{}

func (acceptTransportNonces) Consume(context.Context, string, string, time.Duration) (bool, error) {
	return true, nil
}

func TestSignalWebSocketRoutesOpaqueFrameBetweenAuthenticatedPair(t *testing.T) {
	account, pc, android := uuid.New(), uuid.New(), uuid.New()
	_, pcPrivate, _ := ed25519.GenerateKey(rand.Reader)
	_, androidPrivate, _ := ed25519.GenerateKey(rand.Reader)
	pcDevice := auth.Device{ID: pc, UserID: account, Type: "PC_AGENT", TrustState: "TRUSTED", SigningPublicKey: pcPrivate.Public().(ed25519.PublicKey)}
	androidDevice := auth.Device{ID: android, UserID: account, Type: "ANDROID", TrustState: "TRUSTED", SigningPublicKey: androidPrivate.Public().(ed25519.PublicKey)}
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"}, android: {ID: android, Type: "ANDROID", TrustState: "TRUSTED"},
	}}}
	tickets := testTicketService(t, resolver)
	connection := uuid.New()
	if _, err := tickets.Issue(context.Background(), testPrincipal(account, pc, "PC_AGENT"), android, connection); err != nil {
		t.Fatal(err)
	}
	epoch := tickets.connections[connectionKey{accountID: account, connection: connection}].pathEpoch
	hub := NewHub(tickets)
	middleware := auth.Middleware{
		Tokens:  fixedTransportVerifier{identity: auth.Identity{Issuer: "test", Subject: "account", TokenID: "token"}},
		Devices: transportAuthDevices{devices: map[uuid.UUID]auth.Device{pc: pcDevice, android: androidDevice}},
		Nonces:  acceptTransportNonces{}, SubjectKey: make([]byte, 32),
	}
	mux := http.NewServeMux()
	mux.Handle("/v1/transport/signals", AuthenticateSignals(middleware, hub.Handler()))
	server := httptest.NewServer(mux)
	defer server.Close()
	pcSocket := dialSignalSocket(t, server.URL, pc, pcPrivate, "pc")
	defer pcSocket.Close()
	androidSocket := dialSignalSocket(t, server.URL, android, androidPrivate, "android")
	defer androidSocket.Close()

	frame := testSignalFrame(connection, pc, android, epoch, 1, SignalPathOffer, 64)
	if err := pcSocket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatal(err)
	}
	_ = androidSocket.SetReadDeadline(time.Now().Add(2 * time.Second))
	messageType, routed, err := androidSocket.ReadMessage()
	if err != nil {
		t.Fatalf("read routed signal: %v", err)
	}
	if messageType != websocket.BinaryMessage || string(routed) != string(frame) {
		t.Fatal("signal was not routed byte-identically")
	}
}

func TestSignalWebSocketWelcomeNegotiatesDirectDataOnlyWithAllPrerequisites(t *testing.T) {
	account, pc := uuid.New(), uuid.New()
	_, private, _ := ed25519.GenerateKey(rand.Reader)
	device := auth.Device{
		ID: pc, UserID: account, Type: "PC_AGENT", TrustState: "TRUSTED",
		SigningPublicKey: private.Public().(ed25519.PublicKey),
	}
	resolver := &memoryDeviceResolver{devices: map[uuid.UUID]map[uuid.UUID]store.DeviceRecord{account: {
		pc: {ID: pc, Type: "PC_AGENT", TrustState: "TRUSTED"},
	}}}
	middleware := auth.Middleware{
		Tokens:  fixedTransportVerifier{identity: auth.Identity{Issuer: "test", Subject: "account", TokenID: "token"}},
		Devices: transportAuthDevices{devices: map[uuid.UUID]auth.Device{pc: device}},
		Nonces:  acceptTransportNonces{}, SubjectKey: make([]byte, 32),
	}

	for _, test := range []struct {
		name               string
		serverPermitted    bool
		clientCapabilities []string
		wantDirectData     bool
	}{
		{
			name: "server_gate_disabled", serverPermitted: false,
			clientCapabilities: []string{"direct_transport_v1", "direct_transport_shadow_multipair_v2", "direct_transport_data_v1", "direct_session_cutover_v1"},
		},
		{
			name: "client_data_only_fails_closed", serverPermitted: true,
			clientCapabilities: []string{"direct_transport_v1", "direct_transport_data_v1"},
		},
		{
			name: "server_and_client_capabilities_present", serverPermitted: true,
			clientCapabilities: []string{"direct_transport_v1", "direct_transport_data_v1", "direct_session_cutover_v1"},
			wantDirectData:     true,
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			service, err := NewRouteTicketServiceWithRouting(
				resolver, make([]byte, ed25519.SeedSize), test.serverPermitted,
			)
			if err != nil {
				t.Fatal(err)
			}
			server := httptest.NewServer(AuthenticateSignals(middleware, NewHub(service).Handler()))
			defer server.Close()
			socket, welcomeCapabilities := dialSignalSocketWithCapabilities(
				t, server.URL, pc, private, "pc", test.clientCapabilities,
			)
			defer socket.Close()
			if got := slices.Contains(welcomeCapabilities, "direct_transport_data_v1"); got != test.wantDirectData {
				t.Fatalf("Welcome direct data=%t capabilities=%v, want %t", got, welcomeCapabilities, test.wantDirectData)
			}
			if got := slices.Contains(welcomeCapabilities, "direct_session_cutover_v1"); got != test.wantDirectData {
				t.Fatalf("Welcome session cutover=%t capabilities=%v, want %t", got, welcomeCapabilities, test.wantDirectData)
			}
		})
	}
}

func TestSignalWebSocketRejectsOrdinaryDeviceProofDomain(t *testing.T) {
	account, pc := uuid.New(), uuid.New()
	_, private, _ := ed25519.GenerateKey(rand.Reader)
	device := auth.Device{ID: pc, UserID: account, Type: "PC_AGENT", TrustState: "TRUSTED", SigningPublicKey: private.Public().(ed25519.PublicKey)}
	middleware := auth.Middleware{
		Tokens:  fixedTransportVerifier{identity: auth.Identity{Issuer: "test", Subject: "account", TokenID: "token"}},
		Devices: transportAuthDevices{devices: map[uuid.UUID]auth.Device{pc: device}},
		Nonces:  acceptTransportNonces{}, SubjectKey: make([]byte, 32),
	}
	server := httptest.NewServer(AuthenticateSignals(middleware, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusNoContent) })))
	defer server.Close()
	timestamp := time.Now().UnixMilli()
	nonceBytes := make([]byte, 16)
	_, _ = rand.Read(nonceBytes)
	nonce := base64.RawURLEncoding.EncodeToString(nonceBytes)
	bodyHash := sha256.Sum256(nil)
	signature := ed25519.Sign(private, auth.CanonicalRequest(http.MethodGet, "/v1/transport/signals", formatInt64(timestamp), nonce, bodyHash))
	request, _ := http.NewRequest(http.MethodGet, server.URL+"/v1/transport/signals", nil)
	request.Header.Set("Authorization", "Bearer integration-token")
	request.Header.Set("X-TermLink-Device", pc.String())
	request.Header.Set("X-TermLink-Timestamp", formatInt64(timestamp))
	request.Header.Set("X-TermLink-Nonce", nonce)
	request.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(signature))
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("ordinary proof domain status=%d, want 401", response.StatusCode)
	}
}

func dialSignalSocket(t *testing.T, serverURL string, deviceID uuid.UUID, private ed25519.PrivateKey, role string) *websocket.Conn {
	t.Helper()
	socket, _ := dialSignalSocketWithCapabilities(
		t, serverURL, deviceID, private, role, []string{"direct_transport_v1"},
	)
	return socket
}

func dialSignalSocketWithCapabilities(
	t *testing.T,
	serverURL string,
	deviceID uuid.UUID,
	private ed25519.PrivateKey,
	role string,
	capabilities []string,
) (*websocket.Conn, []string) {
	t.Helper()
	timestamp := time.Now().UnixMilli()
	nonceBytes := make([]byte, 16)
	_, _ = rand.Read(nonceBytes)
	nonce := base64.RawURLEncoding.EncodeToString(nonceBytes)
	bodyHash := sha256.Sum256(nil)
	signature := ed25519.Sign(private, SignalCanonicalRequest(formatInt64(timestamp), nonce, bodyHash))
	headers := http.Header{
		"Authorization":        []string{"Bearer integration-token"},
		"X-TermLink-Device":    []string{deviceID.String()},
		"X-TermLink-Timestamp": []string{formatInt64(timestamp)},
		"X-TermLink-Nonce":     []string{nonce},
		"X-TermLink-Signature": []string{base64.RawURLEncoding.EncodeToString(signature)},
	}
	dialer := websocket.Dialer{Subprotocols: []string{signalSubprotocol}}
	socket, response, err := dialer.Dial("ws"+strings.TrimPrefix(serverURL, "http")+"/v1/transport/signals", headers)
	if err != nil {
		if response != nil {
			t.Fatalf("dial signal websocket status=%d: %v", response.StatusCode, err)
		}
		t.Fatal(err)
	}
	hello := map[string]any{"type": "CLIENT_HELLO", "role": role, "protocol_versions": []int{1}, "capabilities": capabilities}
	if err := socket.WriteJSON(hello); err != nil {
		t.Fatal(err)
	}
	var welcome struct {
		Type         string   `json:"type"`
		Capabilities []string `json:"capabilities"`
	}
	if err := socket.ReadJSON(&welcome); err != nil {
		t.Fatal(err)
	}
	if welcome.Type != "SERVER_WELCOME" {
		t.Fatalf("unexpected welcome: %+v", welcome)
	}
	return socket, welcome.Capabilities
}

func formatInt64(value int64) string {
	return strconv.FormatInt(value, 10)
}

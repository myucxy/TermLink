package e2eserver

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/myucxy/TermLink/technical-validation/e2e/internal/realtimewire"
)

const testToken = "test-e2e-token"

type testIdentity struct {
	id       uuid.UUID
	kind     string
	name     string
	keyID    string
	public   ed25519.PublicKey
	private  ed25519.PrivateKey
	exchange []byte
}

func newTestIdentity(t *testing.T, kind, name string) testIdentity {
	t.Helper()
	public, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	exchange := make([]byte, 32)
	if _, err := rand.Read(exchange); err != nil {
		t.Fatal(err)
	}
	return testIdentity{
		id: uuid.New(), kind: kind, name: name, keyID: "test-key-01",
		public: public, private: private, exchange: exchange,
	}
}

func registrationRequest(t *testing.T, root string, identity testIdentity) (*http.Request, []byte) {
	t.Helper()
	timestamp := time.Now().UnixMilli()
	nonce := randomNonce(t)
	signing := base64.RawURLEncoding.EncodeToString(identity.public)
	exchange := base64.RawURLEncoding.EncodeToString(identity.exchange)
	canonical := strings.Join([]string{
		"TERMLINK-DEVICE-REGISTER-V1", identity.id.String(), identity.kind, identity.name,
		signing, exchange, identity.keyID, strconv.FormatInt(timestamp, 10), nonce,
	}, "\n")
	body, err := json.Marshal(map[string]any{
		"id": identity.id, "type": identity.kind, "name": identity.name,
		"signing_public_key": signing, "exchange_public_key": exchange, "key_id": identity.keyID,
		"timestamp_ms": timestamp, "nonce": nonce,
		"signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(identity.private, []byte(canonical))),
	})
	if err != nil {
		t.Fatal(err)
	}
	request, err := http.NewRequest(http.MethodPost, root+"/v1/devices/register", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+testToken)
	request.Header.Set("Content-Type", "application/json")
	return request, body
}

func registerIdentity(t *testing.T, client *http.Client, root string, identity testIdentity) {
	t.Helper()
	request, _ := registrationRequest(t, root, identity)
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("register %s status = %d, body = %s", identity.kind, response.StatusCode, readBody(t, response.Body))
	}
}

func signedRequest(t *testing.T, method, rawURL string, body []byte, identity testIdentity) *http.Request {
	t.Helper()
	request, err := http.NewRequest(method, rawURL, bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	timestamp := time.Now().UnixMilli()
	nonce := randomNonce(t)
	digest := sha256.Sum256(body)
	canonical := strings.Join([]string{
		"TERMLINK-DEVICE-PROOF-V1", strings.ToUpper(method), request.URL.RequestURI(),
		strconv.FormatInt(timestamp, 10), nonce, hex.EncodeToString(digest[:]),
	}, "\n")
	request.Header.Set("Authorization", "Bearer "+testToken)
	request.Header.Set("X-TermLink-Device", identity.id.String())
	request.Header.Set("X-TermLink-Timestamp", strconv.FormatInt(timestamp, 10))
	request.Header.Set("X-TermLink-Nonce", nonce)
	request.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(ed25519.Sign(identity.private, []byte(canonical))))
	if len(body) > 0 {
		request.Header.Set("Content-Type", "application/json")
	}
	return request
}

func randomNonce(t *testing.T) string {
	t.Helper()
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		t.Fatal(err)
	}
	return base64.RawURLEncoding.EncodeToString(value)
}

func doSigned(t *testing.T, client *http.Client, method, rawURL string, body []byte, identity testIdentity, want int) []byte {
	t.Helper()
	response, err := client.Do(signedRequest(t, method, rawURL, body, identity))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	result := []byte{}
	if response.StatusCode != http.StatusNoContent {
		result, err = io.ReadAll(response.Body)
		if err != nil {
			t.Fatal(err)
		}
	}
	if response.StatusCode != want {
		t.Fatalf("%s %s status = %d, want %d, body = %s", method, rawURL, response.StatusCode, want, result)
	}
	return result
}

func TestHealthReadinessAndDevelopmentIdentityMode(t *testing.T) {
	server := httptest.NewServer(New(Config{Token: testToken}).Handler())
	defer server.Close()
	for _, path := range []string{"/healthz", "/readyz"} {
		response, err := http.Get(server.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		body := readBody(t, response.Body)
		_ = response.Body.Close()
		if response.StatusCode != http.StatusOK || !strings.Contains(body, `"status"`) {
			t.Fatalf("GET %s = %d %s", path, response.StatusCode, body)
		}
	}
	response, err := http.Get(server.URL + "/v1/client-config")
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	var config map[string]any
	if err := json.NewDecoder(response.Body).Decode(&config); err != nil {
		t.Fatal(err)
	}
	if config["identity_mode"] != "development_shared_token" || config["persistent"] != false ||
		config["auth_mode"] != "development_login" || config["login_enabled"] != true {
		t.Fatalf("client config = %#v", config)
	}
}

func TestDevelopmentLoginIssueFailureAndExpiry(t *testing.T) {
	now := time.Date(2026, 8, 19, 12, 0, 0, 0, time.UTC)
	application := New(Config{
		Token:                   testToken,
		Now:                     func() time.Time { return now },
		DevelopmentUsername:     "alice",
		DevelopmentPassword:     "correct horse",
		DevelopmentAccountLabel: "Alice's local account",
		DevelopmentTokenTTL:     time.Minute,
	})
	server := httptest.NewServer(application.Handler())
	defer server.Close()

	login := func(username, password string) (*http.Response, map[string]any) {
		t.Helper()
		body, err := json.Marshal(map[string]string{"username": username, "password": password})
		if err != nil {
			t.Fatal(err)
		}
		request, err := http.NewRequest(http.MethodPost, server.URL+"/v1/auth/login", bytes.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("Content-Type", "application/json")
		response, err := server.Client().Do(request)
		if err != nil {
			t.Fatal(err)
		}
		var payload map[string]any
		if err := json.NewDecoder(response.Body).Decode(&payload); err != nil {
			response.Body.Close()
			t.Fatal(err)
		}
		response.Body.Close()
		return response, payload
	}

	failed, failure := login("alice", "wrong")
	if failed.StatusCode != http.StatusUnauthorized || failure["error"] == nil {
		t.Fatalf("invalid login = %d %#v", failed.StatusCode, failure)
	}
	if strings.Contains(fmt.Sprint(failure), "wrong") || strings.Contains(fmt.Sprint(failure), "correct horse") {
		t.Fatalf("login failure leaked a credential: %#v", failure)
	}

	response, payload := login(" alice ", "correct horse")
	if response.StatusCode != http.StatusOK {
		t.Fatalf("valid login = %d %#v", response.StatusCode, payload)
	}
	token, _ := payload["access_token"].(string)
	if token == "" || token == testToken || payload["token_type"] != "Bearer" || payload["expires_in"] != float64(60) {
		t.Fatalf("login payload = %#v", payload)
	}
	if payload["account_label"] != "Alice's local account" {
		t.Fatalf("login account label = %#v", payload)
	}
	account, ok := payload["account"].(map[string]any)
	if !ok || account["label"] != "Alice's local account" {
		t.Fatalf("login account = %#v", payload["account"])
	}
	if !application.validBearer("Bearer "+token) || !application.validBearer("Bearer "+testToken) {
		t.Fatal("issued and shared bearer tokens should both be accepted")
	}

	now = now.Add(time.Minute)
	if application.validBearer("Bearer " + token) {
		t.Fatal("expired development token was accepted")
	}
	// Legacy shared-token traffic also performs expiry cleanup; a busy Agent
	// must not keep stale interactive tokens resident indefinitely.
	if !application.validBearer("Bearer " + testToken) {
		t.Fatal("shared bearer token was unexpectedly rejected")
	}
	application.mu.RLock()
	remaining := len(application.issuedTokens)
	application.mu.RUnlock()
	if remaining != 0 {
		t.Fatalf("expired development tokens were not cleaned up: %d", remaining)
	}
}

func TestDeviceCatalogLeaseLifecycleAndNonceReplay(t *testing.T) {
	application := New(Config{Token: testToken})
	server := httptest.NewServer(application.Handler())
	defer server.Close()
	windows := newTestIdentity(t, "WINDOWS_PC", "E2E PC")
	android := newTestIdentity(t, "ANDROID", "E2E Android")

	windowsRegistration, _ := registrationRequest(t, server.URL, windows)
	response, err := server.Client().Do(windowsRegistration)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("first registration = %d, body = %s", response.StatusCode, readBody(t, response.Body))
	}
	_ = response.Body.Close()
	request, body := registrationRequest(t, server.URL, android)
	response, err = server.Client().Do(request)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("android registration = %d, body = %s", response.StatusCode, readBody(t, response.Body))
	}
	_ = response.Body.Close()
	replay, err := http.NewRequest(http.MethodPost, server.URL+"/v1/devices/register", bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	replay.Header = request.Header.Clone()
	response, err = server.Client().Do(replay)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusUnauthorized || !strings.Contains(readBody(t, response.Body), "REPLAYED_PROOF") {
		t.Fatalf("registration replay status = %d", response.StatusCode)
	}
	_ = response.Body.Close()
	self := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/devices/self", nil, android, http.StatusOK)
	if !bytes.Contains(self, []byte(`"device"`)) || !bytes.Contains(self, []byte(android.id.String())) ||
		!bytes.Contains(self, []byte(`"trust_state":"TRUSTED"`)) {
		t.Fatalf("device self response = %s", self)
	}

	pcID := uuid.New()
	pcBody := []byte(`{"name":"E2E PC","agent_version":"e2e-test","key_epoch":1}`)
	pcRequest := signedRequest(t, http.MethodPut, server.URL+"/v1/pcs/"+pcID.String(), pcBody, windows)
	replayHeaders := pcRequest.Header.Clone()
	response, err = server.Client().Do(pcRequest)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusOK {
		t.Fatalf("PC upsert = %d, body = %s", response.StatusCode, readBody(t, response.Body))
	}
	_ = response.Body.Close()
	pcReplay, _ := http.NewRequest(http.MethodPut, server.URL+"/v1/pcs/"+pcID.String(), bytes.NewReader(pcBody))
	pcReplay.Header = replayHeaders
	response, err = server.Client().Do(pcReplay)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusUnauthorized || !strings.Contains(readBody(t, response.Body), "REPLAYED_PROOF") {
		t.Fatalf("authenticated replay status = %d", response.StatusCode)
	}
	_ = response.Body.Close()
	grantsBody := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/pcs/"+pcID.String()+"/grants", nil, windows, http.StatusOK)
	var grants struct {
		PCID     uuid.UUID         `json:"pc_id"`
		KeyEpoch int64             `json:"key_epoch"`
		Items    []implicitPCGrant `json:"items"`
	}
	if err := json.Unmarshal(grantsBody, &grants); err != nil {
		t.Fatal(err)
	}
	if grants.PCID != pcID || grants.KeyEpoch != 1 || len(grants.Items) != 1 ||
		grants.Items[0].DeviceID != android.id || grants.Items[0].DeviceType != "ANDROID" ||
		grants.Items[0].Permission != "MANAGE" || !bytes.Equal(grants.Items[0].ExchangePublicKey, android.exchange) {
		t.Fatalf("implicit PC grants = %#v", grants)
	}

	sessionID := uuid.New()
	sessionBody := []byte(fmt.Sprintf(`{"pc_id":%q,"shell_type":"powershell","title_cipher":"","state":"RUNNING","columns":120,"rows":40,"last_output_seq":7}`, pcID))
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/sessions/"+sessionID.String(), sessionBody, windows, http.StatusOK)

	devices := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/devices", nil, android, http.StatusOK)
	pcs := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/pcs", nil, android, http.StatusOK)
	sessions := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/pcs/"+pcID.String()+"/sessions", nil, android, http.StatusOK)
	if !bytes.Contains(devices, []byte(windows.id.String())) || !bytes.Contains(devices, []byte(android.id.String())) ||
		!bytes.Contains(pcs, []byte(pcID.String())) || !bytes.Contains(sessions, []byte(sessionID.String())) {
		t.Fatalf("catalog missing resources: devices=%s pcs=%s sessions=%s", devices, pcs, sessions)
	}

	leaseBody := doSigned(t, server.Client(), http.MethodPost, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", []byte(`{}`), android, http.StatusCreated)
	var lease Lease
	if err := json.Unmarshal(leaseBody, &lease); err != nil {
		t.Fatal(err)
	}
	if lease.Version != 1 || lease.HolderDeviceID != android.id || !lease.ExpiresAt.After(time.Now()) {
		t.Fatalf("acquired lease = %#v", lease)
	}
	getLease := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", nil, windows, http.StatusOK)
	if !bytes.Contains(getLease, []byte(lease.ID.String())) {
		t.Fatalf("GET lease = %s", getLease)
	}
	renewedBody := doSigned(t, server.Client(), http.MethodPost, server.URL+"/v1/sessions/"+sessionID.String()+"/lease/"+lease.ID.String()+"/renew", []byte(`{}`), android, http.StatusOK)
	var renewed Lease
	if err := json.Unmarshal(renewedBody, &renewed); err != nil {
		t.Fatal(err)
	}
	if renewed.Version != 2 {
		t.Fatalf("renewed lease version = %d", renewed.Version)
	}
	doSigned(t, server.Client(), http.MethodDelete, server.URL+"/v1/sessions/"+sessionID.String()+"/lease/"+lease.ID.String(), nil, android, http.StatusNoContent)

	secondBody := doSigned(t, server.Client(), http.MethodPost, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", []byte(`{}`), android, http.StatusCreated)
	var second Lease
	if err := json.Unmarshal(secondBody, &second); err != nil {
		t.Fatal(err)
	}
	if second.Version <= renewed.Version {
		t.Fatalf("replacement lease did not advance fence: %#v", second)
	}
	force := doSigned(t, server.Client(), http.MethodDelete, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", nil, windows, http.StatusOK)
	if !bytes.Contains(force, []byte(`"released":true`)) {
		t.Fatalf("force release = %s", force)
	}
	doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", nil, windows, http.StatusNotFound)
}

func TestOpaqueRealtimeRelayAndLeaseACL(t *testing.T) {
	application := New(Config{Token: testToken})
	server := httptest.NewServer(application.Handler())
	defer server.Close()
	windows := newTestIdentity(t, "WINDOWS_PC", "Relay PC")
	android := newTestIdentity(t, "ANDROID", "Relay Android")
	registerIdentity(t, server.Client(), server.URL, windows)
	registerIdentity(t, server.Client(), server.URL, android)
	pcID, sessionID := uuid.New(), uuid.New()
	pcBody := []byte(`{"name":"Relay PC","agent_version":"relay-test","key_epoch":1}`)
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/pcs/"+pcID.String(), pcBody, windows, http.StatusOK)
	sessionBody := []byte(fmt.Sprintf(`{"pc_id":%q,"shell_type":"powershell","title_cipher":"","state":"RUNNING","columns":80,"rows":24,"last_output_seq":0}`, pcID))
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/sessions/"+sessionID.String(), sessionBody, windows, http.StatusOK)

	pcSocket := connectRealtime(t, server.URL, windows, "pc")
	defer pcSocket.Close()
	androidSocket := connectRealtime(t, server.URL, android, "android")
	defer androidSocket.Close()

	subscribe := testEnvelope(t, realtimewire.Subscribe, realtimewire.AndroidToPC, sessionID, android.id, windows.id, 1)
	writeEnvelope(t, androidSocket, subscribe)
	assertEnvelope(t, pcSocket, subscribe)

	output := testEnvelope(t, realtimewire.OutputChunk, realtimewire.PCToAndroid, sessionID, windows.id, android.id, 1)
	writeEnvelope(t, pcSocket, output)
	assertEnvelope(t, androidSocket, output)

	leaseBody := doSigned(t, server.Client(), http.MethodPost, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", []byte(`{}`), android, http.StatusCreated)
	var lease Lease
	if err := json.Unmarshal(leaseBody, &lease); err != nil {
		t.Fatal(err)
	}
	input := testEnvelope(t, realtimewire.InputEvent, realtimewire.AndroidToPC, sessionID, android.id, windows.id, 2)
	writeEnvelope(t, androidSocket, input)
	assertEnvelope(t, pcSocket, input)

	doSigned(t, server.Client(), http.MethodDelete, server.URL+"/v1/sessions/"+sessionID.String()+"/lease/"+lease.ID.String(), nil, android, http.StatusNoContent)
	unauthorized := testEnvelope(t, realtimewire.ResizeRequest, realtimewire.AndroidToPC, sessionID, android.id, windows.id, 3)
	writeEnvelope(t, androidSocket, unauthorized)
	_ = androidSocket.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, _, err := androidSocket.ReadMessage(); err == nil {
		t.Fatal("unauthorized resize did not close the Android realtime connection")
	}
}

func TestSameAndroidDeviceCanUseIndependentSessionPeers(t *testing.T) {
	application := New(Config{Token: testToken})
	server := httptest.NewServer(application.Handler())
	defer server.Close()
	windows := newTestIdentity(t, "WINDOWS_PC", "Multi-session PC")
	android := newTestIdentity(t, "ANDROID", "Multi-session Android")
	registerIdentity(t, server.Client(), server.URL, windows)
	registerIdentity(t, server.Client(), server.URL, android)
	pcID := uuid.New()
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/pcs/"+pcID.String(),
		[]byte(`{"name":"Multi-session PC","agent_version":"relay-test","key_epoch":1}`), windows, http.StatusOK)
	sessionIDs := []uuid.UUID{uuid.New(), uuid.New()}
	for _, sessionID := range sessionIDs {
		body := []byte(fmt.Sprintf(`{"pc_id":%q,"shell_type":"powershell","title_cipher":"","state":"RUNNING","columns":80,"rows":24,"last_output_seq":0}`, pcID))
		doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/sessions/"+sessionID.String(), body, windows, http.StatusOK)
	}

	pcSocket := connectRealtime(t, server.URL, windows, "pc")
	defer pcSocket.Close()
	firstAndroidSocket := connectRealtime(t, server.URL, android, "android")
	defer firstAndroidSocket.Close()
	secondAndroidSocket := connectRealtime(t, server.URL, android, "android")
	defer secondAndroidSocket.Close()

	firstSubscription := testEnvelope(t, realtimewire.Subscribe, realtimewire.AndroidToPC, sessionIDs[0], android.id, windows.id, 1)
	writeEnvelope(t, firstAndroidSocket, firstSubscription)
	assertEnvelope(t, pcSocket, firstSubscription)
	secondSubscription := testEnvelope(t, realtimewire.Subscribe, realtimewire.AndroidToPC, sessionIDs[1], android.id, windows.id, 1)
	writeEnvelope(t, secondAndroidSocket, secondSubscription)
	assertEnvelope(t, pcSocket, secondSubscription)

	firstOutput := testEnvelope(t, realtimewire.OutputChunk, realtimewire.PCToAndroid, sessionIDs[0], windows.id, android.id, 1)
	writeEnvelope(t, pcSocket, firstOutput)
	assertEnvelope(t, firstAndroidSocket, firstOutput)
	secondOutput := testEnvelope(t, realtimewire.OutputChunk, realtimewire.PCToAndroid, sessionIDs[1], windows.id, android.id, 1)
	writeEnvelope(t, pcSocket, secondOutput)
	assertEnvelope(t, secondAndroidSocket, secondOutput)
}

func TestDisconnectAndroidRealtimePreservesServerStateAndBlocksOnlyAndroid(t *testing.T) {
	var clockOffset atomic.Int64
	application := New(Config{
		Token: testToken,
		Now: func() time.Time {
			return time.Now().Add(time.Duration(clockOffset.Load()))
		},
	})
	server := httptest.NewServer(application.Handler())
	defer server.Close()
	windows := newTestIdentity(t, "WINDOWS_PC", "Disconnect PC")
	android := newTestIdentity(t, "ANDROID", "Disconnect Android")
	registerIdentity(t, server.Client(), server.URL, windows)
	registerIdentity(t, server.Client(), server.URL, android)
	pcID, sessionID := uuid.New(), uuid.New()
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/pcs/"+pcID.String(),
		[]byte(`{"name":"Disconnect PC","agent_version":"disconnect-test","key_epoch":1}`), windows, http.StatusOK)
	sessionBody := []byte(fmt.Sprintf(`{"pc_id":%q,"shell_type":"powershell","title_cipher":"","state":"RUNNING","columns":80,"rows":24,"last_output_seq":0}`, pcID))
	doSigned(t, server.Client(), http.MethodPut, server.URL+"/v1/sessions/"+sessionID.String(), sessionBody, windows, http.StatusOK)
	leaseBody := doSigned(t, server.Client(), http.MethodPost, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", []byte(`{}`), android, http.StatusCreated)
	var lease Lease
	if err := json.Unmarshal(leaseBody, &lease); err != nil {
		t.Fatal(err)
	}

	pcSocket := connectRealtime(t, server.URL, windows, "pc")
	defer pcSocket.Close()
	androidSocket := connectRealtime(t, server.URL, android, "android")
	defer androidSocket.Close()
	subscribe := testEnvelope(t, realtimewire.Subscribe, realtimewire.AndroidToPC, sessionID, android.id, windows.id, 1)
	writeEnvelope(t, androidSocket, subscribe)
	assertEnvelope(t, pcSocket, subscribe)

	response, err := http.Post(server.URL+"/v1/e2e/realtime/disconnect-android?hold_ms=5000", "", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	var result struct {
		DisconnectedAndroidPeers int   `json:"disconnected_android_peers"`
		HoldMillis               int64 `json:"hold_ms"`
	}
	if err := json.NewDecoder(response.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != http.StatusOK || result.DisconnectedAndroidPeers != 1 || result.HoldMillis != 5000 {
		t.Fatalf("disconnect response = %d %#v", response.StatusCode, result)
	}

	_ = androidSocket.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, _, err := androidSocket.ReadMessage(); err == nil {
		t.Fatal("disconnect endpoint left the Android realtime socket open")
	}

	application.mu.RLock()
	_, hasWindows := application.devices[windows.id]
	_, hasAndroid := application.devices[android.id]
	_, hasPC := application.pcs[pcID]
	_, hasSession := application.sessions[sessionID]
	preservedLease, hasLease := application.leases[sessionID]
	pcPeerCount := 0
	for peer := range application.peers[windows.id] {
		if peer.role == "pc" {
			pcPeerCount++
		}
	}
	application.mu.RUnlock()
	if !hasWindows || !hasAndroid || !hasPC || !hasSession || !hasLease || preservedLease.ID != lease.ID {
		t.Fatal("disconnect endpoint mutated the in-memory catalog, session, or lease")
	}
	if pcPeerCount != 1 {
		t.Fatalf("PC realtime peers after Android disconnect = %d, want 1", pcPeerCount)
	}

	blockedURL, err := url.Parse(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	blockedURL.Scheme = "ws"
	blockedURL.Path = "/v1/realtime"
	proof := signedRequest(t, http.MethodGet, blockedURL.String(), nil, android)
	dialer := websocket.Dialer{Subprotocols: []string{realtimeSubprotocol}, HandshakeTimeout: 5 * time.Second}
	blockedSocket, blockedResponse, err := dialer.Dial(blockedURL.String(), proof.Header)
	if blockedSocket != nil {
		_ = blockedSocket.Close()
	}
	if err == nil || blockedResponse == nil {
		t.Fatalf("Android realtime reconnect during hold unexpectedly succeeded: response=%v err=%v", blockedResponse, err)
	}
	blockedBody := readBody(t, blockedResponse.Body)
	_ = blockedResponse.Body.Close()
	if blockedResponse.StatusCode != http.StatusServiceUnavailable || blockedResponse.Header.Get("Retry-After") == "" ||
		!strings.Contains(blockedBody, "ANDROID_REALTIME_BLOCKED") {
		t.Fatalf("blocked Android reconnect = %d retry-after=%q body=%s", blockedResponse.StatusCode, blockedResponse.Header.Get("Retry-After"), blockedBody)
	}

	secondPCSocket := connectRealtime(t, server.URL, windows, "pc")
	_ = secondPCSocket.Close()

	clockOffset.Store(int64(5*time.Second + time.Millisecond))
	recoveredAndroidSocket := connectRealtime(t, server.URL, android, "android")
	defer recoveredAndroidSocket.Close()
	recoveredSubscribe := testEnvelope(t, realtimewire.Subscribe, realtimewire.AndroidToPC, sessionID, android.id, windows.id, 1)
	writeEnvelope(t, recoveredAndroidSocket, recoveredSubscribe)
	assertEnvelope(t, pcSocket, recoveredSubscribe)
	output := testEnvelope(t, realtimewire.OutputChunk, realtimewire.PCToAndroid, sessionID, windows.id, android.id, 1)
	writeEnvelope(t, pcSocket, output)
	assertEnvelope(t, recoveredAndroidSocket, output)

	devices := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/devices", nil, android, http.StatusOK)
	sessions := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/pcs/"+pcID.String()+"/sessions", nil, android, http.StatusOK)
	leaseAfterReconnect := doSigned(t, server.Client(), http.MethodGet, server.URL+"/v1/sessions/"+sessionID.String()+"/lease", nil, windows, http.StatusOK)
	if !bytes.Contains(devices, []byte(windows.id.String())) || !bytes.Contains(devices, []byte(android.id.String())) ||
		!bytes.Contains(sessions, []byte(sessionID.String())) || !bytes.Contains(leaseAfterReconnect, []byte(lease.ID.String())) {
		t.Fatalf("state after reconnect missing: devices=%s sessions=%s lease=%s", devices, sessions, leaseAfterReconnect)
	}
}

func TestDisconnectAndroidRealtimeRejectsInvalidHold(t *testing.T) {
	server := httptest.NewServer(New(Config{Token: testToken}).Handler())
	defer server.Close()
	for _, value := range []string{"-1", "30001", "not-a-number"} {
		response, err := http.Post(server.URL+"/v1/e2e/realtime/disconnect-android?hold_ms="+url.QueryEscape(value), "", nil)
		if err != nil {
			t.Fatal(err)
		}
		body := readBody(t, response.Body)
		_ = response.Body.Close()
		if response.StatusCode != http.StatusBadRequest || !strings.Contains(body, "INVALID_HOLD_MS") {
			t.Fatalf("hold_ms=%q response = %d %s", value, response.StatusCode, body)
		}
	}
}

func connectRealtime(t *testing.T, root string, identity testIdentity, role string) *websocket.Conn {
	t.Helper()
	parsed, err := url.Parse(root)
	if err != nil {
		t.Fatal(err)
	}
	parsed.Scheme = "ws"
	parsed.Path = "/v1/realtime"
	proof := signedRequest(t, http.MethodGet, parsed.String(), nil, identity)
	dialer := websocket.Dialer{Subprotocols: []string{realtimeSubprotocol}, HandshakeTimeout: 5 * time.Second}
	connection, response, err := dialer.Dial(parsed.String(), proof.Header)
	if err != nil {
		if response != nil {
			t.Fatalf("realtime dial failed: %v, status=%d, body=%s", err, response.StatusCode, readBody(t, response.Body))
		}
		t.Fatal(err)
	}
	hello := map[string]any{
		"role": role, "protocol_versions": []int{1},
		"capabilities": requiredCapabilities,
	}
	if err := connection.WriteJSON(hello); err != nil {
		t.Fatal(err)
	}
	_ = connection.SetReadDeadline(time.Now().Add(2 * time.Second))
	messageType, data, err := connection.ReadMessage()
	if err != nil {
		t.Fatal(err)
	}
	if messageType != websocket.TextMessage || !bytes.Contains(data, []byte(`"type":"welcome"`)) || connection.Subprotocol() != realtimeSubprotocol {
		t.Fatalf("welcome type=%d subprotocol=%q body=%s", messageType, connection.Subprotocol(), data)
	}
	var welcome serverWelcome
	if err := json.Unmarshal(data, &welcome); err != nil {
		t.Fatal(err)
	}
	if welcome.Type != "welcome" || welcome.ProtocolVersion != realtimewire.ProtocolVersion ||
		welcome.HeartbeatSeconds != int(pingInterval.Seconds()) {
		t.Fatalf("welcome contract = %#v", welcome)
	}
	if _, err := uuid.Parse(welcome.ConnectionID); err != nil {
		t.Fatalf("welcome connection ID %q: %v", welcome.ConnectionID, err)
	}
	_ = connection.SetReadDeadline(time.Time{})
	return connection
}

func testEnvelope(t *testing.T, message realtimewire.MessageType, direction realtimewire.Direction, sessionID, senderID, recipientID uuid.UUID, sequence uint64) realtimewire.Envelope {
	t.Helper()
	return realtimewire.Envelope{
		ProtocolVersion: realtimewire.ProtocolVersion, MessageType: message,
		SessionID: sessionID[:], SenderDeviceID: senderID[:], RecipientDeviceID: recipientID[:],
		Direction: direction, Sequence: sequence, SentAtMS: time.Now().UnixMilli(),
		KeyID: []byte("test-key-01"), Nonce: bytes.Repeat([]byte{0x11}, 12),
		Ciphertext: bytes.Repeat([]byte{0x42}, 32),
	}
}

func writeEnvelope(t *testing.T, connection *websocket.Conn, envelope realtimewire.Envelope) {
	t.Helper()
	frame, err := envelope.Marshal()
	if err != nil {
		t.Fatal(err)
	}
	if err := connection.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatal(err)
	}
}

func assertEnvelope(t *testing.T, connection *websocket.Conn, want realtimewire.Envelope) {
	t.Helper()
	_ = connection.SetReadDeadline(time.Now().Add(2 * time.Second))
	messageType, frame, err := connection.ReadMessage()
	if err != nil {
		t.Fatal(err)
	}
	if messageType != websocket.BinaryMessage {
		t.Fatalf("message type = %d", messageType)
	}
	got, err := realtimewire.Unmarshal(frame)
	if err != nil {
		t.Fatal(err)
	}
	if got.MessageType != want.MessageType || got.Sequence != want.Sequence || !bytes.Equal(got.Ciphertext, want.Ciphertext) {
		t.Fatalf("relayed envelope = %#v, want %#v", got, want)
	}
	_ = connection.SetReadDeadline(time.Time{})
}

func readBody(t *testing.T, reader io.Reader) string {
	t.Helper()
	value, err := io.ReadAll(reader)
	if err != nil {
		t.Fatal(err)
	}
	return string(value)
}

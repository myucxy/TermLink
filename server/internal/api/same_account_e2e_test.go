package api_test

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/realtime"
	"github.com/termlink/termlink/server/internal/realtimewire"
	"github.com/termlink/termlink/server/internal/store"
)

// TestSameAccountDirectUseE2EGate is the service-side acceptance gate for the
// no-trust/no-grant product path. It uses real PostgreSQL and Valkey authority,
// the production HTTP authentication middleware, and the production WebSocket
// Relay. It deliberately does not claim to exercise a real OIDC provider,
// Windows/Android binaries, TLS, or client-side encryption/decryption.
func TestSameAccountDirectUseE2EGate(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("same-account E2E requires TERMLINK_INTEGRATION_DATABASE_URL and TERMLINK_INTEGRATION_REDIS_URL")
	}

	ctx := context.Background()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := store.Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()
	if err := repository.VerifySchema(ctx); err != nil {
		t.Fatal(err)
	}
	// This is the production policy under test. Leaving the default false would
	// silently exercise the retired PENDING/grant compatibility path instead.
	repository.AutoTrustDevices = true

	valkey, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	defer valkey.Close()

	issuer := "https://same-account-e2e.issuer.test"
	sameSubject := "same-subject-" + uuid.NewString()
	crossSubject := "cross-subject-" + uuid.NewString()
	verifier := integrationVerifier{identities: map[string]auth.Identity{
		"same-account-windows": {
			Issuer: issuer, Subject: sameSubject, AuthorizedParty: "termlink-windows",
			TokenID: "same-account-windows-" + uuid.NewString(),
		},
		"same-account-windows-two": {
			Issuer: issuer, Subject: sameSubject, AuthorizedParty: "termlink-windows",
			TokenID: "same-account-windows-two-" + uuid.NewString(),
		},
		"same-account-android": {
			Issuer: issuer, Subject: sameSubject, AuthorizedParty: "termlink-android",
			TokenID: "same-account-android-" + uuid.NewString(), MFA: true, AuthenticatedAt: time.Now(),
		},
		"same-account-android-two": {
			Issuer: issuer, Subject: sameSubject, AuthorizedParty: "termlink-android",
			TokenID: "same-account-android-two-" + uuid.NewString(),
		},
		"cross-account-android": {
			Issuer: issuer, Subject: crossSubject, AuthorizedParty: "termlink-android",
			TokenID: "cross-account-android-" + uuid.NewString(),
		},
	}}
	subjectKey := []byte("01234567890123456789012345678901")
	authentication := auth.Middleware{
		Tokens: verifier, Devices: repository, Nonces: valkey, SubjectKey: subjectKey,
	}
	service := &api.API{
		Store: repository, Valkey: valkey, Tokens: verifier, Auth: authentication,
		SubjectKey: subjectKey, TokenKey: subjectKey,
		ClientConfig: api.ClientConfiguration{
			OIDCClientID: "termlink-android", WindowsClientID: "termlink-windows",
			WindowsRegistrationParties: []string{"termlink-windows"},
		},
	}
	hub := realtime.NewHub(repository, valkey, nil)
	defer hub.Close(context.Background())
	mux := http.NewServeMux()
	mux.Handle("/v1/realtime", authentication.Authenticate(hub.Handler()))
	mux.Handle("/", service.Handler())
	testServer := httptest.NewServer(mux)
	defer testServer.Close()

	windows := newDevice(t)
	windowsTwo := newDevice(t)
	android := newDevice(t)
	androidTwo := newDevice(t)
	crossAndroid := newDevice(t)
	windowsRegistration := sameAccountRegister(t, testServer.URL, windows, "WINDOWS_PC", "Same-account PC", "same-account-windows")
	windowsTwoRegistration := sameAccountRegister(t, testServer.URL, windowsTwo, "WINDOWS_PC", "Same-account PC two", "same-account-windows-two")
	androidRegistration := sameAccountRegister(t, testServer.URL, android, "ANDROID", "Same-account Android", "same-account-android")
	androidTwoRegistration := sameAccountRegister(t, testServer.URL, androidTwo, "ANDROID", "Same-account Android two", "same-account-android-two")
	crossRegistration := sameAccountRegister(t, testServer.URL, crossAndroid, "ANDROID", "Cross-account Android", "cross-account-android")

	sameUserID := mustResponseUUID(t, windowsRegistration, "user_id")
	windowsTwoUserID := mustResponseUUID(t, windowsTwoRegistration, "user_id")
	androidUserID := mustResponseUUID(t, androidRegistration, "user_id")
	androidTwoUserID := mustResponseUUID(t, androidTwoRegistration, "user_id")
	crossUserID := mustResponseUUID(t, crossRegistration, "user_id")
	if windowsTwoUserID != sameUserID || androidUserID != sameUserID || androidTwoUserID != sameUserID {
		t.Fatalf("same OIDC subject produced different accounts: windows=%s/%s androids=%s/%s", sameUserID, windowsTwoUserID, androidUserID, androidTwoUserID)
	}
	if crossUserID == sameUserID {
		t.Fatal("different OIDC subjects unexpectedly shared an account")
	}
	defer func() {
		_, _ = repository.Pool.Exec(context.Background(), `DELETE FROM users WHERE id=ANY($1)`, []uuid.UUID{sameUserID, crossUserID})
	}()
	if windowsRegistration["bootstrap"] != true || windowsTwoRegistration["bootstrap"] != false || androidRegistration["bootstrap"] != false || androidTwoRegistration["bootstrap"] != false {
		t.Fatalf("unexpected bootstrap flags: windows=%v/%v androids=%v/%v", windowsRegistration["bootstrap"], windowsTwoRegistration["bootstrap"], androidRegistration["bootstrap"], androidTwoRegistration["bootstrap"])
	}
	assertDeviceTrusted(t, repository, windows.id)
	assertDeviceTrusted(t, repository, windowsTwo.id)
	assertDeviceTrusted(t, repository, android.id)
	assertDeviceTrusted(t, repository, androidTwo.id)
	assertDeviceTrusted(t, repository, crossAndroid.id)
	assertNoLegacyAuthorizationRows(t, repository, sameUserID)

	pcID := uuid.New()
	sessionID := uuid.New()
	sameAccountSignedJSON(t, testServer.URL, windows, "same-account-windows", http.MethodPut, "/v1/pcs/"+pcID.String(), map[string]any{
		"name": "Same-account PC", "agent_version": "e2e", "key_epoch": 1,
	}, "same-account-upsert-pc", http.StatusOK)
	collaborativePolicy := sameAccountSignedJSONWithHeaders(t, testServer.URL, android, "same-account-android", http.MethodPut, "/v1/account/pcs/"+pcID.String()+"/control-policy", map[string]any{
		"remote_routing_enabled": true, "mode": "COLLABORATIVE", "local_priority": true, "expected_version": 1,
	}, "same-account-collaborative-policy", map[string]string{
		"X-TermLink-Operation-Reason": "verify same-account collaborative control",
	}, http.StatusOK)
	sameAccountSignedJSON(t, testServer.URL, windows, "same-account-windows", http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "pwsh", "title_cipher": "", "state": "RUNNING",
		"columns": 120, "rows": 40, "last_output_seq": 0,
	}, "same-account-upsert-session", http.StatusOK)

	pcs := sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodGet, "/v1/pcs", nil, "", http.StatusOK)
	assertSingleItemID(t, pcs, pcID)
	sessions := sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodGet, "/v1/pcs/"+pcID.String()+"/sessions", nil, "", http.StatusOK)
	assertSingleItemID(t, sessions, sessionID)
	subscription := sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/subscribe", map[string]any{}, "same-account-rest-subscribe", http.StatusCreated)
	if subscription["permission"] != "MANAGE" || strings.TrimSpace(fmt.Sprint(subscription["token"])) == "" {
		t.Fatalf("same-account subscription did not receive direct MANAGE authority: %v", subscription)
	}
	secondSubscription := sameAccountSignedJSON(t, testServer.URL, androidTwo, "same-account-android-two", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/subscribe", map[string]any{}, "same-account-rest-subscribe-two", http.StatusCreated)
	if secondSubscription["permission"] != "MANAGE" || strings.TrimSpace(fmt.Sprint(secondSubscription["token"])) == "" {
		t.Fatalf("second same-account subscription did not receive direct MANAGE authority: %v", secondSubscription)
	}

	lease := sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "same-account-lease", http.StatusCreated)
	if fmt.Sprint(lease["holder_device_id"]) != android.id.String() {
		t.Fatalf("lease holder=%v, want %s", lease["holder_device_id"], android.id)
	}
	leaseID, err := uuid.Parse(fmt.Sprint(lease["lease_id"]))
	if err != nil {
		t.Fatalf("invalid lease response: %v", lease)
	}
	sharedLease := sameAccountSignedJSON(t, testServer.URL, androidTwo, "same-account-android-two", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "same-account-shared-lease", http.StatusCreated)
	if sharedLease["lease_id"] != lease["lease_id"] || sharedLease["version"] != lease["version"] || sharedLease["holder_device_id"] != lease["holder_device_id"] {
		t.Fatalf("second Android received a different shared lease: first=%v second=%v", lease, sharedLease)
	}
	assertDirectInputLeaseAuthorizationHTTP(
		t, testServer.URL, service, authentication, repository, valkey, pcID, sessionID, windows, windowsTwo, android, androidTwo,
		crossAndroid, mustNestedResponseInt64(t, collaborativePolicy, "policy", "policy_version"),
	)

	pcSocket := sameAccountDialRealtime(t, testServer.URL, windows, "same-account-windows", "pc")
	defer pcSocket.Close()
	androidSocket := sameAccountDialRealtime(t, testServer.URL, android, "same-account-android", "android")
	defer androidSocket.Close()
	androidTwoSocket := sameAccountDialRealtime(t, testServer.URL, androidTwo, "same-account-android-two", "android")
	defer androidTwoSocket.Close()

	subscribeFrame := testEnvelope(android, windows, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 1)
	writeEnvelope(t, androidSocket, subscribeFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, subscribeFrame) {
		t.Fatal("Relay changed the same-account SUBSCRIBE frame")
	}
	secondSubscribeFrame := testEnvelope(androidTwo, windows, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 1)
	writeEnvelope(t, androidTwoSocket, secondSubscribeFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, secondSubscribeFrame) {
		t.Fatal("Relay changed the second same-account SUBSCRIBE frame")
	}
	outputFrame := testEnvelope(windows, android, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 1)
	writeEnvelope(t, pcSocket, outputFrame)
	if got := readEnvelope(t, androidSocket); !bytes.Equal(got, outputFrame) {
		t.Fatal("Relay changed or misdirected the PC output frame")
	}
	inputFrame := testEnvelope(android, windows, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, 2)
	writeEnvelope(t, androidSocket, inputFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, inputFrame) {
		t.Fatal("Relay changed or misdirected the leased Android input frame")
	}
	secondInputFrame := testEnvelope(androidTwo, windows, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, 2)
	writeEnvelope(t, androidTwoSocket, secondInputFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, secondInputFrame) {
		t.Fatal("Relay rejected or changed the second Android's shared-lease input frame")
	}

	sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodDelete, "/v1/sessions/"+sessionID.String()+"/lease/"+leaseID.String(), nil, "same-account-release-first-participant", http.StatusNoContent)
	_ = androidSocket.Close()
	remainingInputFrame := testEnvelope(androidTwo, windows, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, 3)
	writeEnvelope(t, androidTwoSocket, remainingInputFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, remainingInputFrame) {
		t.Fatal("first Android release/disconnect revoked the remaining Android's shared control")
	}
	rejoinedLease := sameAccountSignedJSON(t, testServer.URL, android, "same-account-android", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "same-account-rejoin-shared-lease", http.StatusCreated)
	if rejoinedLease["lease_id"] != lease["lease_id"] || rejoinedLease["version"] != lease["version"] {
		t.Fatalf("first Android did not rejoin the existing shared lease: original=%v rejoined=%v", lease, rejoinedLease)
	}
	sameAccountSignedJSON(t, testServer.URL, windows, "same-account-windows", http.MethodPost, "/v1/devices/"+android.id.String()+"/revoke", map[string]any{}, "same-account-revoke-first-android", http.StatusOK)
	postRevokeInputFrame := testEnvelope(androidTwo, windows, sessionID, realtimewire.InputEvent, realtimewire.AndroidToPC, 4)
	writeEnvelope(t, androidTwoSocket, postRevokeInputFrame)
	if got := readEnvelope(t, pcSocket); !bytes.Equal(got, postRevokeInputFrame) {
		t.Fatal("first Android authority revocation revoked the remaining Android's shared control")
	}

	crossPCs := sameAccountSignedJSON(t, testServer.URL, crossAndroid, "cross-account-android", http.MethodGet, "/v1/pcs", nil, "", http.StatusOK)
	if items, ok := crossPCs["items"].([]any); !ok || len(items) != 0 {
		t.Fatalf("cross-account PC discovery leaked items: %v", crossPCs)
	}
	sameAccountSignedJSON(t, testServer.URL, crossAndroid, "cross-account-android", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/subscribe", map[string]any{}, "cross-account-rest-subscribe", http.StatusForbidden)
	sameAccountSignedJSON(t, testServer.URL, crossAndroid, "cross-account-android", http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "cross-account-lease", http.StatusForbidden)

	crossSocket := sameAccountDialRealtime(t, testServer.URL, crossAndroid, "cross-account-android", "android")
	crossSubscribe := testEnvelope(crossAndroid, windows, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 1)
	writeEnvelope(t, crossSocket, crossSubscribe)
	assertSameAccountRouteDeniedAndSocketAlive(t, pcSocket, crossSocket)
	_ = crossSocket.Close()

	sameAccountSignedJSON(t, testServer.URL, androidTwo, "same-account-android-two", http.MethodDelete, "/v1/sessions/"+sessionID.String()+"/lease/"+leaseID.String(), nil, "same-account-release-last-participant", http.StatusNoContent)
	noLeaseDirectPath := "/v1/sessions/" + sessionID.String() + "/lease?sender_device_id=" + androidTwo.id.String()
	nonOwningWithoutLease := sameAccountSignedJSON(
		t, testServer.URL, windowsTwo, "same-account-windows-two", http.MethodGet,
		noLeaseDirectPath, nil, "", http.StatusForbidden,
	)
	assertDirectInputErrorPayload(t, nonOwningWithoutLease, "CONTROL_NOT_GRANTED", "errors.control_not_granted")
	sameAccountSignedJSON(t, testServer.URL, windows, "same-account-windows", http.MethodGet, "/v1/sessions/"+sessionID.String()+"/lease", nil, "", http.StatusNotFound)
	var durableLeaseCount int
	if err := repository.Pool.QueryRow(context.Background(), `SELECT count(*) FROM control_leases WHERE session_id=$1`, sessionID).Scan(&durableLeaseCount); err != nil {
		t.Fatal(err)
	}
	if durableLeaseCount != 0 {
		t.Fatalf("last shared-lease participant left %d durable fences, want 0", durableLeaseCount)
	}
	assertExpiredDirectInputLeaseDenied(t, testServer.URL, valkey, windows, androidTwo, sessionID)
	assertNoLegacyAuthorizationRows(t, repository, sameUserID)
	t.Log("covered: same-subject Windows+two Android registration; shared lease acquisition; simultaneous Relay input; participant release/disconnect and active authority-revocation isolation; last-participant live/durable cleanup; cross-subject isolation")
	t.Log("boundary: service integration only; real OIDC, packaged clients, TLS, and client cryptography remain outside this gate")
}

func sameAccountRegister(t *testing.T, root string, device testDevice, deviceType, name, bearer string) map[string]any {
	t.Helper()
	timestamp := time.Now().UnixMilli()
	nonce := randomNonce(t)
	signing := base64.RawURLEncoding.EncodeToString(device.public)
	exchange := base64.RawURLEncoding.EncodeToString(device.exchange)
	keyID := "device-key-" + device.id.String()
	canonical := strings.Join([]string{
		"TERMLINK-DEVICE-REGISTER-V1", device.id.String(), deviceType, name,
		signing, exchange, keyID, strconv.FormatInt(timestamp, 10), nonce,
	}, "\n")
	body := map[string]any{
		"id": device.id, "type": deviceType, "name": name,
		"signing_public_key": signing, "exchange_public_key": exchange, "key_id": keyID,
		"timestamp_ms": timestamp, "nonce": nonce,
		"signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical))),
	}
	data, err := json.Marshal(body)
	if err != nil {
		t.Fatal(err)
	}
	request, err := http.NewRequest(http.MethodPost, root+"/v1/devices/register", bytes.NewReader(data))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer "+bearer)
	request.Header.Set("Content-Type", "application/json")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	raw, _ := io.ReadAll(response.Body)
	if response.StatusCode != http.StatusCreated {
		t.Fatalf("register %s status=%d body=%s", deviceType, response.StatusCode, raw)
	}
	var result map[string]any
	if err := json.Unmarshal(raw, &result); err != nil {
		t.Fatal(err)
	}
	return result
}

func sameAccountSignedJSON(t *testing.T, root string, device testDevice, bearer, method, path string, body any, idempotency string, wantStatus int) map[string]any {
	t.Helper()
	return sameAccountSignedJSONWithHeaders(t, root, device, bearer, method, path, body, idempotency, nil, wantStatus)
}

func sameAccountSignedJSONWithHeaders(t *testing.T, root string, device testDevice, bearer, method, path string, body any, idempotency string, headers map[string]string, wantStatus int) map[string]any {
	t.Helper()
	var data []byte
	var err error
	if body != nil {
		data, err = json.Marshal(body)
		if err != nil {
			t.Fatal(err)
		}
	}
	request := newSignedRequest(t, root, device, method, path, data, idempotency)
	request.Header.Set("Authorization", "Bearer "+bearer)
	for name, value := range headers {
		request.Header.Set(name, value)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	raw, _ := io.ReadAll(response.Body)
	if response.StatusCode != wantStatus {
		t.Fatalf("%s %s status=%d body=%s", method, path, response.StatusCode, raw)
	}
	if len(raw) == 0 {
		return map[string]any{}
	}
	var result map[string]any
	if err := json.Unmarshal(raw, &result); err != nil {
		t.Fatal(err)
	}
	return result
}

func sameAccountDialRealtime(t *testing.T, root string, device testDevice, bearer, role string) *websocket.Conn {
	t.Helper()
	path := "/v1/realtime"
	request := newSignedRequest(t, "", device, http.MethodGet, path, nil, "")
	request.Header.Set("Authorization", "Bearer "+bearer)
	dialer := websocket.Dialer{Subprotocols: []string{"termlink.realtime.v1"}}
	socket, response, err := dialer.Dial("ws"+strings.TrimPrefix(root, "http")+path, request.Header)
	if err != nil {
		if response != nil {
			t.Fatalf("realtime status=%d error=%v", response.StatusCode, err)
		}
		t.Fatal(err)
	}
	hello := fmt.Sprintf(`{"role":%q,"protocol_versions":[1],"capabilities":["cumulative_ack","e2ee_x25519_chacha20poly1305","flow_control_v1","lease_v1","liveness_probe_v1"]}`, role)
	if err := socket.WriteMessage(websocket.TextMessage, []byte(hello)); err != nil {
		t.Fatal(err)
	}
	messageType, _, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage {
		t.Fatalf("welcome type=%d error=%v", messageType, err)
	}
	return socket
}

func mustResponseUUID(t *testing.T, response map[string]any, key string) uuid.UUID {
	t.Helper()
	value, err := uuid.Parse(fmt.Sprint(response[key]))
	if err != nil {
		t.Fatalf("response %q is not a UUID: %v", key, response[key])
	}
	return value
}

func assertDeviceTrusted(t *testing.T, repository *store.Postgres, deviceID uuid.UUID) {
	t.Helper()
	var trustState string
	if err := repository.Pool.QueryRow(context.Background(), `SELECT trust_state FROM devices WHERE id=$1`, deviceID).Scan(&trustState); err != nil {
		t.Fatal(err)
	}
	if trustState != "TRUSTED" {
		t.Fatalf("device %s trust state=%q, want TRUSTED immediately after registration", deviceID, trustState)
	}
}

func assertNoLegacyAuthorizationRows(t *testing.T, repository *store.Postgres, userID uuid.UUID) {
	t.Helper()
	var pairingChallenges, deviceGrants, selfTrustTokens int
	err := repository.Pool.QueryRow(context.Background(), `
		SELECT
			(SELECT count(*) FROM pairing_challenges WHERE user_id=$1),
			(SELECT count(*) FROM device_grants g JOIN pcs p ON p.id=g.pc_id WHERE p.owner_user_id=$1),
			(SELECT count(*) FROM device_self_trust_tokens WHERE user_id=$1)`, userID).
		Scan(&pairingChallenges, &deviceGrants, &selfTrustTokens)
	if err != nil {
		t.Fatal(err)
	}
	if pairingChallenges != 0 || deviceGrants != 0 || selfTrustTokens != 0 {
		t.Fatalf("legacy authorization rows pairing/grants/self-trust=%d/%d/%d, want 0/0/0", pairingChallenges, deviceGrants, selfTrustTokens)
	}
}

func assertSingleItemID(t *testing.T, response map[string]any, want uuid.UUID) {
	t.Helper()
	items, ok := response["items"].([]any)
	if !ok || len(items) != 1 {
		t.Fatalf("items=%v, want exactly one item", response["items"])
	}
	item, ok := items[0].(map[string]any)
	if !ok || fmt.Sprint(item["id"]) != want.String() {
		t.Fatalf("item=%v, want id=%s", items[0], want)
	}
}

func assertSameAccountRouteDeniedAndSocketAlive(t *testing.T, pcSocket, androidSocket *websocket.Conn) {
	t.Helper()
	probeID := uuid.NewString()
	ping := []byte(fmt.Sprintf(`{"type":"ping","probe_id":%q}`, probeID))
	_ = androidSocket.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if err := androidSocket.WriteMessage(websocket.TextMessage, ping); err != nil {
		t.Fatalf("cross-account Realtime liveness write: %v", err)
	}
	_ = androidSocket.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, raw, err := androidSocket.ReadMessage()
	if err != nil {
		t.Fatalf("cross-account Realtime connection closed after denied frame: %v", err)
	}
	var pong map[string]string
	if messageType != websocket.TextMessage || json.Unmarshal(raw, &pong) != nil ||
		pong["type"] != "pong" || pong["probe_id"] != probeID {
		t.Fatalf("cross-account Realtime liveness response=%q", raw)
	}
	_ = pcSocket.SetReadDeadline(time.Now().Add(250 * time.Millisecond))
	if _, _, err := pcSocket.ReadMessage(); err == nil {
		t.Fatal("cross-account SUBSCRIBE was forwarded to the PC")
	}
}

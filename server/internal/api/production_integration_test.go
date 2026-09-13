package api_test

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
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

type integrationVerifier struct{ identities map[string]auth.Identity }

func (v integrationVerifier) Verify(_ context.Context, token string) (auth.Identity, error) {
	identity, ok := v.identities[token]
	if !ok {
		return auth.Identity{}, errors.New("unknown integration token")
	}
	return identity, nil
}

type integrationRouteObserver struct {
	events      chan string
	connections chan string
}

func (integrationRouteObserver) ConnectionOpened(string) {}
func (o integrationRouteObserver) ConnectionClosed(role string) {
	select {
	case o.connections <- role:
	default:
	}
}
func (integrationRouteObserver) SubscriptionsChanged(int)  {}
func (integrationRouteObserver) SlowConsumerDisconnected() {}
func (o integrationRouteObserver) EnvelopeRouted(direction, messageType, result string, _ int, _ time.Duration) {
	select {
	case o.events <- direction + ":" + messageType + ":" + result:
	default:
	}
}

type testDevice struct {
	id       uuid.UUID
	private  ed25519.PrivateKey
	public   ed25519.PublicKey
	exchange []byte
}

func TestProductionAccountRelayAndRevocation(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("production infrastructure integration environment is not configured")
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
	assertProductionMigrations(t, repository)
	valkey, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	defer valkey.Close()
	identity := auth.Identity{
		Issuer:          "https://integration.issuer.test",
		Subject:         "subject-" + uuid.NewString(),
		AuthorizedParty: "termlink-android",
		TokenID:         "integration-access-token-" + uuid.NewString(),
	}
	windowsIdentity := identity
	windowsIdentity.AuthorizedParty = "termlink-windows"
	windowsIdentity.TokenID = "integration-windows-access-token-" + uuid.NewString()
	verifier := integrationVerifier{identities: map[string]auth.Identity{
		"integration":         identity,
		"integration-windows": windowsIdentity,
	}}
	subjectKey := []byte("01234567890123456789012345678901")
	authentication := auth.Middleware{Tokens: verifier, Devices: repository, Nonces: valkey, SubjectKey: subjectKey}
	service := &api.API{
		Store: repository, Valkey: valkey, Tokens: verifier, Auth: authentication,
		SubjectKey: subjectKey, TokenKey: subjectKey,
		ClientConfig: api.ClientConfiguration{
			OIDCClientID: "termlink-android", WindowsClientID: "termlink-windows",
			WindowsRegistrationParties: []string{"termlink-windows"},
		},
	}
	routeEvents := make(chan string, 128)
	connectionEvents := make(chan string, 16)
	authorityEvents := make(chan string, 16)
	hub := realtime.NewHubWithObserver(repository, valkey, nil, integrationRouteObserver{events: routeEvents, connections: connectionEvents})
	authorityContext, stopAuthorityChanges := context.WithCancel(ctx)
	authoritySubscription := valkey.Client.Subscribe(authorityContext, "device-revoked", "device-authorization-changed", "account-status-changed")
	if _, err := authoritySubscription.Receive(authorityContext); err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			select {
			case <-authorityContext.Done():
				return
			case message, ok := <-authoritySubscription.Channel():
				if !ok {
					return
				}
				id, err := uuid.Parse(message.Payload)
				if err != nil {
					continue
				}
				switch message.Channel {
				case "device-revoked":
					hub.Revoke(id)
				case "device-authorization-changed":
					hub.RefreshDevice(id)
				case "account-status-changed":
					hub.DisableAccount(id)
				}
				select {
				case authorityEvents <- message.Channel:
				default:
				}
			}
		}
	}()
	defer func() {
		stopAuthorityChanges()
		_ = authoritySubscription.Close()
	}()
	mux := http.NewServeMux()
	mux.Handle("/v1/realtime", authentication.Authenticate(hub.Handler()))
	mux.Handle("/", service.Handler())
	server := httptest.NewServer(mux)
	defer server.Close()
	defer hub.Close(context.Background())
	pc := newDevice(t)
	android := newDevice(t)
	register(t, server.URL, pc, "WINDOWS_PC", "Integration PC")
	registerResponse := register(t, server.URL, android, "ANDROID", "Integration Android")
	if registerResponse["bootstrap"] != false {
		t.Fatal("Android unexpectedly bootstrapped")
	}
	accountUserID, err := uuid.Parse(fmt.Sprint(registerResponse["user_id"]))
	if err != nil {
		t.Fatalf("registered account id=%v", registerResponse["user_id"])
	}
	defer func() {
		_, _ = repository.Pool.Exec(context.Background(), `DELETE FROM users WHERE id=$1`, accountUserID)
	}()
	selfTrustedAndroid := newDevice(t)
	selfTrustRegistration := register(t, server.URL, selfTrustedAndroid, "ANDROID", "Self-trusted Android")
	if selfTrustRegistration["bootstrap"] != false {
		t.Fatal("self-trust Android unexpectedly bootstrapped")
	}
	selfTrust := signedJSON(t, server.URL, selfTrustedAndroid, http.MethodPost, "/v1/devices/self/trust", map[string]any{}, "self-trust-android", http.StatusOK)
	if selfTrust["status"] != "TRUSTED" || selfTrust["changed"] != true {
		t.Fatalf("self-trust response=%v", selfTrust)
	}
	repeatedSelfTrust := signedJSON(t, server.URL, selfTrustedAndroid, http.MethodPost, "/v1/devices/self/trust", map[string]any{}, "self-trust-android-repeat", http.StatusOK)
	if repeatedSelfTrust["changed"] != false {
		t.Fatalf("repeated self-trust response=%v", repeatedSelfTrust)
	}
	var selfTrustGrants, selfTrustAudits int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM device_grants WHERE device_id=$1`, selfTrustedAndroid.id).Scan(&selfTrustGrants); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM audit_events WHERE user_id=$1 AND actor_device_id=$2 AND action='device.self_trust'`, accountUserID, selfTrustedAndroid.id).Scan(&selfTrustAudits); err != nil {
		t.Fatal(err)
	}
	if selfTrustGrants != 0 || selfTrustAudits != 1 {
		t.Fatalf("self-trust grants/audits=%d/%d, want 0/1", selfTrustGrants, selfTrustAudits)
	}
	pcID := uuid.New()
	sessionID := uuid.New()
	historySessionID := uuid.New()
	signedJSON(t, server.URL, pc, http.MethodPut, "/v1/pcs/"+pcID.String(), map[string]any{"name": "Integration PC", "agent_version": "0.1.0", "key_epoch": 1}, "upsert-pc", http.StatusOK)
	pairingBody := map[string]any{
		"target_device_id": android.id, "pc_id": pcID, "permission": "CONTROL",
	}
	pairing := signedJSON(t, server.URL, pc, http.MethodPost, "/v1/pairing/challenges", pairingBody, "create-pairing", http.StatusCreated)
	signedJSON(t, server.URL, android, http.MethodPost, "/v1/pairing/challenges/"+pairing["challenge_id"].(string)+"/complete", map[string]any{
		"code": pairing["code"], "challenge_nonce": pairing["challenge_nonce"],
	}, "complete-pairing", http.StatusOK)
	grants := signedJSON(t, server.URL, pc, http.MethodGet, "/v1/pcs/"+pcID.String()+"/grants", nil, "", http.StatusOK)
	if grants["key_epoch"] != float64(1) || len(grants["items"].([]any)) != 1 {
		t.Fatalf("authorized PC Grant snapshot=%v", grants)
	}
	grant := grants["items"].([]any)[0].(map[string]any)
	if grant["signing_public_key"] != base64.StdEncoding.EncodeToString(android.public) ||
		grant["exchange_public_key"] != base64.StdEncoding.EncodeToString(android.exchange) {
		t.Fatalf("authorized PC Grant public keys=%v, want Android signing and exchange keys", grant)
	}
	signedJSON(t, server.URL, android, http.MethodGet, "/v1/pcs/"+pcID.String()+"/grants", nil, "", http.StatusForbidden)
	signedJSON(t, server.URL, pc, http.MethodPut, "/v1/sessions/"+sessionID.String(), map[string]any{"pc_id": pcID, "shell_type": "pwsh", "title_cipher": "", "state": "RUNNING", "columns": 100, "rows": 30, "last_output_seq": 0}, "upsert-session", http.StatusOK)
	signedJSON(t, server.URL, pc, http.MethodPut, "/v1/sessions/"+historySessionID.String(), map[string]any{"pc_id": pcID, "shell_type": "pwsh", "title_cipher": "", "state": "RUNNING", "columns": 100, "rows": 30, "last_output_seq": 0}, "upsert-history-session", http.StatusOK)
	signedJSON(t, server.URL, pc, http.MethodDelete, "/v1/sessions/"+historySessionID.String(), nil, "reject-active-history-session-delete", http.StatusConflict)
	signedJSON(t, server.URL, pc, http.MethodPut, "/v1/sessions/"+historySessionID.String(), map[string]any{
		"pc_id": pcID, "shell_type": "pwsh", "title_cipher": "", "state": "EXITED",
		"columns": 100, "rows": 30, "last_output_seq": 0, "ended_at": time.Now().UTC(),
	}, "exit-history-session", http.StatusOK)
	testRetiredHistoryEndpoints(t, server.URL, pc, historySessionID)
	leaseOne := signedJSON(t, server.URL, android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "lease-repeat", http.StatusCreated)
	leaseTwo := signedJSON(t, server.URL, android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "lease-repeat", http.StatusCreated)
	if leaseOne["lease_id"] != leaseTwo["lease_id"] {
		t.Fatal("idempotent lease response changed")
	}
	// CONTROL is not device-management authority. A controller must not be
	// able to promote its own Grant or revoke the owning PC.
	signedJSON(t, server.URL, android, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "MANAGE",
	}, "controller-self-promotion", http.StatusForbidden)
	signedJSON(t, server.URL, android, http.MethodPost, "/v1/devices/"+pc.id.String()+"/revoke", map[string]any{}, "controller-revoke-owner", http.StatusForbidden)

	// Lease renewal must re-check the current Grant and release a stale lease
	// after a downgrade, rather than letting it block another controller.
	signedJSON(t, server.URL, pc, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "VIEW",
	}, "downgrade-before-renew", http.StatusOK)
	signedJSON(t, server.URL, android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease/"+fmt.Sprint(leaseOne["lease_id"])+"/renew", map[string]any{}, "renew-after-downgrade", http.StatusForbidden)
	signedJSON(t, server.URL, pc, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "CONTROL",
	}, "restore-control", http.StatusOK)
	signedJSON(t, server.URL, android, http.MethodPost, "/v1/sessions/"+sessionID.String()+"/lease", map[string]any{}, "lease-after-restore", http.StatusCreated)
	directInputLeasePath := "/v1/sessions/" + sessionID.String() + "/lease?sender_device_id=" + android.id.String()
	assertDirectInputAuthorizationResponse(
		t,
		signedJSON(t, server.URL, pc, http.MethodGet, directInputLeasePath, nil, "", http.StatusOK),
		android.id,
		"SINGLE",
		1,
	)
	pcSocket := dialRealtime(t, server.URL, pc, "pc")
	defer pcSocket.Close()
	androidSocket := dialRealtime(t, server.URL, android, "android")
	defer androidSocket.Close()
	subscribe := testEnvelope(android, pc, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 1)
	writeEnvelope(t, androidSocket, subscribe)
	readEnvelope(t, pcSocket)
	output := testEnvelope(pc, android, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 1)
	writeEnvelope(t, pcSocket, output)
	received := readEnvelope(t, androidSocket)
	if !bytes.Equal(received, output) {
		t.Fatal("Relay changed opaque production frame")
	}

	// UNSUBSCRIBE is forwarded for Agent cleanup, but a late PC output for the
	// removed subscriber is an idempotent drop and must not close the PC socket.
	unsubscribe := testEnvelope(android, pc, sessionID, realtimewire.Unsubscribe, realtimewire.AndroidToPC, 2)
	writeEnvelope(t, androidSocket, unsubscribe)
	readEnvelope(t, pcSocket)
	drainRouteEvents(routeEvents)
	lateOutput := testEnvelope(pc, android, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 2)
	writeEnvelope(t, pcSocket, lateOutput)
	waitRouteEvent(t, routeEvents, "pc_to_android", realtimewire.OutputChunk, "accepted")
	resubscribe := testEnvelopeWithACK(android, pc, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 3, 1)
	writeEnvelope(t, androidSocket, resubscribe)
	readEnvelope(t, pcSocket)
	writeEnvelope(t, pcSocket, lateOutput)
	if got := readEnvelope(t, androidSocket); !bytes.Equal(got, lateOutput) {
		t.Fatal("PC connection did not survive stale post-unsubscribe output")
	}

	// A finite Grant carries its authoritative expiry into the subscription
	// cache. Expiry therefore invalidates the subscription before the next
	// ciphertext without a PostgreSQL query on every terminal-output frame.
	// Direct database edits are used only to arrange the fixture before the
	// subscription; production Grant changes go through the API and publish an
	// immediate invalidation event.
	expiryUnsubscribe := testEnvelope(android, pc, sessionID, realtimewire.Unsubscribe, realtimewire.AndroidToPC, 4)
	writeEnvelope(t, androidSocket, expiryUnsubscribe)
	readEnvelope(t, pcSocket)
	if _, err := repository.Pool.Exec(ctx, `UPDATE device_grants SET expires_at=now()+interval '500 milliseconds' WHERE device_id=$1 AND pc_id=$2`, android.id, pcID); err != nil {
		t.Fatal(err)
	}
	expiringSubscribe := testEnvelopeWithACK(android, pc, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 5, 2)
	writeEnvelope(t, androidSocket, expiringSubscribe)
	readEnvelope(t, pcSocket)
	time.Sleep(750 * time.Millisecond)
	expiredDirectInput := signedJSON(t, server.URL, pc, http.MethodGet, directInputLeasePath, nil, "", http.StatusForbidden)
	assertDirectInputErrorPayload(t, expiredDirectInput, "CONTROL_NOT_GRANTED", "errors.control_not_granted")
	drainRouteEvents(routeEvents)
	expiredOutput := testEnvelope(pc, android, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 3)
	writeEnvelope(t, pcSocket, expiredOutput)
	waitRouteEvent(t, routeEvents, "pc_to_android", realtimewire.OutputChunk, "denied")
	signedJSON(t, server.URL, pc, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "CONTROL",
	}, "restore-expired-grant", http.StatusOK)
	assertDirectInputAuthorizationResponse(
		t,
		signedJSON(t, server.URL, pc, http.MethodGet, directInputLeasePath, nil, "", http.StatusOK),
		android.id,
		"SINGLE",
		1,
	)
	assertWebSocketClosed(t, androidSocket, websocket.ClosePolicyViolation)
	waitAuthorityEvent(t, authorityEvents, "device-authorization-changed")
	restoredAndroidSocket := dialRealtime(t, server.URL, android, "android")
	defer restoredAndroidSocket.Close()
	restoredSubscribe := testEnvelopeWithACK(android, pc, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 4, 2)
	writeEnvelope(t, restoredAndroidSocket, restoredSubscribe)
	readEnvelope(t, pcSocket)
	writeEnvelope(t, pcSocket, expiredOutput)
	if got := readEnvelope(t, restoredAndroidSocket); !bytes.Equal(got, expiredOutput) {
		t.Fatal("PC connection did not survive expired subscription cleanup")
	}

	// A physical Android disconnect has no UNSUBSCRIBE frame. Wait until the
	// Relay removes it, send one late PC output, then reconnect and prove the
	// original PC WebSocket is still usable.
	if err := restoredAndroidSocket.Close(); err != nil {
		t.Fatal(err)
	}
	waitConnectionClosed(t, connectionEvents, "android")
	drainRouteEvents(routeEvents)
	disconnectedOutput := testEnvelope(pc, android, sessionID, realtimewire.OutputChunk, realtimewire.PCToAndroid, 4)
	writeEnvelope(t, pcSocket, disconnectedOutput)
	waitRouteEvent(t, routeEvents, "pc_to_android", realtimewire.OutputChunk, "accepted")
	reconnectedAndroidSocket := dialRealtime(t, server.URL, android, "android")
	defer reconnectedAndroidSocket.Close()
	reconnectSubscribe := testEnvelopeWithACK(android, pc, sessionID, realtimewire.Subscribe, realtimewire.AndroidToPC, 5, 3)
	writeEnvelope(t, reconnectedAndroidSocket, reconnectSubscribe)
	readEnvelope(t, pcSocket)
	writeEnvelope(t, pcSocket, disconnectedOutput)
	if got := readEnvelope(t, reconnectedAndroidSocket); !bytes.Equal(got, disconnectedOutput) {
		t.Fatal("PC connection did not survive physical Android disconnect")
	}

	// Realtime heartbeat, not only the initial REST upsert, owns PC Presence.
	// Gorilla processes control frames while a reader is active, as production
	// Agents are; keep a reader running so the client answers the Relay ping.
	_ = pcSocket.SetReadDeadline(time.Time{})
	go func() {
		for {
			if _, _, err := pcSocket.ReadMessage(); err != nil {
				return
			}
		}
	}()
	if _, err := repository.Pool.Exec(ctx, `UPDATE pcs SET last_seen_at=now()-interval '5 minutes' WHERE id=$1`, pcID); err != nil {
		t.Fatal(err)
	}
	presenceDeadline := time.Now().Add(20 * time.Second)
	for {
		var pcLastSeen, deviceLastSeen time.Time
		if err := repository.Pool.QueryRow(ctx, `SELECT p.last_seen_at,d.last_seen_at FROM pcs p JOIN devices d ON d.id=p.device_id WHERE p.id=$1`, pcID).Scan(&pcLastSeen, &deviceLastSeen); err != nil {
			t.Fatal(err)
		}
		if pcLastSeen.After(time.Now().Add(-time.Minute)) && deviceLastSeen.After(time.Now().Add(-time.Minute)) {
			break
		}
		if time.Now().After(presenceDeadline) {
			t.Fatal("PC Presence was not refreshed by the production Realtime heartbeat")
		}
		time.Sleep(250 * time.Millisecond)
	}
	signedJSON(t, server.URL, pc, http.MethodDelete, "/v1/pcs/"+pcID.String()+"/grants/"+android.id.String(), nil, "delete-single-grant", http.StatusNoContent)
	assertWebSocketClosed(t, reconnectedAndroidSocket, websocket.ClosePolicyViolation)
	waitAuthorityEvent(t, authorityEvents, "device-authorization-changed")
	deletedGrantSnapshot := signedJSON(t, server.URL, pc, http.MethodGet, "/v1/pcs/"+pcID.String()+"/grants", nil, "", http.StatusOK)
	if deletedGrantSnapshot["key_epoch"] != float64(2) || len(deletedGrantSnapshot["items"].([]any)) != 0 {
		t.Fatalf("deleted PC Grant snapshot=%v", deletedGrantSnapshot)
	}
	signedJSON(t, server.URL, pc, http.MethodPost, "/v1/pcs/"+pcID.String()+"/grants", map[string]any{
		"device_id": android.id, "permission": "CONTROL",
	}, "restore-after-single-grant-delete", http.StatusOK)
	waitAuthorityEvent(t, authorityEvents, "device-authorization-changed")
	finalAndroidSocket := dialRealtime(t, server.URL, android, "android")
	defer finalAndroidSocket.Close()
	signedJSON(t, server.URL, pc, http.MethodPost, "/v1/devices/"+android.id.String()+"/revoke", map[string]any{}, "revoke-device", http.StatusOK)
	assertWebSocketClosed(t, finalAndroidSocket, websocket.ClosePolicyViolation)
	rotatedGrants := signedJSON(t, server.URL, pc, http.MethodGet, "/v1/pcs/"+pcID.String()+"/grants", nil, "", http.StatusOK)
	if rotatedGrants["key_epoch"] != float64(3) || len(rotatedGrants["items"].([]any)) != 0 {
		t.Fatalf("revoked PC Grant snapshot=%v", rotatedGrants)
	}
	request := newSignedRequest(t, server.URL, android, http.MethodGet, "/v1/devices", nil, "")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Fatalf("revoked device status=%d", response.StatusCode)
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE users SET status='LOCKED' WHERE id=$1`, accountUserID); err != nil {
		t.Fatal(err)
	}
	if err := valkey.Client.Publish(ctx, "account-status-changed", accountUserID.String()).Err(); err != nil {
		t.Fatal(err)
	}
	waitAuthorityEvent(t, authorityEvents, "account-status-changed")
	waitConnectionClosed(t, connectionEvents, "pc")
}

func assertProductionMigrations(t *testing.T, repository *store.Postgres) {
	t.Helper()
	rows, err := repository.Pool.Query(context.Background(), `SELECT version,name FROM schema_migrations WHERE version BETWEEN 1 AND 15 ORDER BY version`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	expected := []struct {
		version int64
		name    string
	}{
		{1, "000001_production_core.up.sql"},
		{2, "000002_pairing_grants.up.sql"},
		{3, "000003_history_uploads.up.sql"},
		{4, "000004_pc_key_epoch.up.sql"},
		{5, "000005_billing_entitlements.up.sql"},
		{6, "000006_account_roles.up.sql"},
		{7, "000007_android_self_trust.up.sql"},
		{8, "000008_windows_bootstrap_fence.up.sql"},
		{9, "000009_unlimited_account_resources.up.sql"},
		{10, "000010_same_account_direct_access.up.sql"},
		{11, "000011_history_paging.up.sql"},
		{12, "000012_history_recipients.up.sql"},
		{13, "000013_history_recipient_backfill_hardening.up.sql"},
		{14, "000014_transcript_history.up.sql"},
		{15, "000015_session_automatic_cleanup.up.sql"},
	}
	index := 0
	for rows.Next() {
		var version int64
		var name string
		if err := rows.Scan(&version, &name); err != nil {
			t.Fatal(err)
		}
		if index >= len(expected) || version != expected[index].version || name != expected[index].name {
			t.Fatalf("unexpected migration at index %d: version=%d name=%q", index, version, name)
		}
		index++
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if index != len(expected) {
		t.Fatalf("applied migrations=%d, want %d", index, len(expected))
	}
}

func testRetiredHistoryEndpoints(t *testing.T, root string, pc testDevice, sessionID uuid.UUID) {
	t.Helper()
	for _, request := range []struct {
		method, path, key string
		body              any
	}{
		{method: http.MethodGet, path: "/v1/sessions/" + sessionID.String() + "/history"},
		{method: http.MethodPost, path: "/v1/sessions/" + sessionID.String() + "/history/uploads", key: "retired-history-upload", body: map[string]any{}},
		{method: http.MethodPost, path: "/v1/sessions/" + sessionID.String() + "/history/uploads/" + uuid.NewString() + "/commit", key: "retired-history-commit"},
	} {
		response := signedJSON(t, root, pc, request.method, request.path, request.body, request.key, http.StatusUpgradeRequired)
		errorBody, ok := response["error"].(map[string]any)
		if !ok || errorBody["code"] != "CLIENT_UPGRADE_REQUIRED" {
			t.Fatalf("retired history response=%v", response)
		}
	}
}

func assertWebSocketClosed(t *testing.T, socket *websocket.Conn, wantCode int) {
	t.Helper()
	_ = socket.SetReadDeadline(time.Now().Add(5 * time.Second))
	_, _, err := socket.ReadMessage()
	if err == nil {
		t.Fatal("revoked WebSocket remained open")
	}
	var closeErr *websocket.CloseError
	if !errors.As(err, &closeErr) {
		t.Fatalf("revoked WebSocket error=%v", err)
	}
	if closeErr.Code != wantCode {
		t.Fatalf("revoked WebSocket close code=%d, want %d", closeErr.Code, wantCode)
	}
}

func drainRouteEvents(events <-chan string) {
	for {
		select {
		case <-events:
		default:
			return
		}
	}
}

func waitAuthorityEvent(t *testing.T, events <-chan string, expected string) {
	t.Helper()
	deadline := time.NewTimer(5 * time.Second)
	defer deadline.Stop()
	for {
		select {
		case event := <-events:
			if event == expected {
				return
			}
		case <-deadline.C:
			t.Fatalf("timed out waiting for authority event %q", expected)
		}
	}
}

func waitRouteEvent(t *testing.T, events <-chan string, direction string, messageType realtimewire.MessageType, result string) {
	t.Helper()
	want := direction + ":" + strconv.FormatUint(uint64(messageType), 10) + ":" + result
	seen := make([]string, 0, 8)
	timer := time.NewTimer(5 * time.Second)
	defer timer.Stop()
	for {
		select {
		case event := <-events:
			if event == want {
				return
			}
			if len(seen) < cap(seen) {
				seen = append(seen, event)
			}
		case <-timer.C:
			t.Fatalf("production route event %q was not observed; saw %v", want, seen)
		}
	}
}

func waitConnectionClosed(t *testing.T, events <-chan string, role string) {
	t.Helper()
	timer := time.NewTimer(5 * time.Second)
	defer timer.Stop()
	for {
		select {
		case got := <-events:
			if got == role {
				return
			}
		case <-timer.C:
			t.Fatalf("%s Realtime connection did not close", role)
		}
	}
}

func envOrDefault(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}

func newDevice(t *testing.T) testDevice {
	t.Helper()
	public, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	exchange := make([]byte, 32)
	if _, err := rand.Read(exchange); err != nil {
		t.Fatal(err)
	}
	return testDevice{id: uuid.New(), private: private, public: public, exchange: exchange}
}

func register(t *testing.T, root string, device testDevice, deviceType, name string) map[string]any {
	t.Helper()
	timestamp := time.Now().UnixMilli()
	nonce := randomNonce(t)
	signing := base64.RawURLEncoding.EncodeToString(device.public)
	exchange := base64.RawURLEncoding.EncodeToString(device.exchange)
	keyID := "device-key-" + device.id.String()
	canonical := strings.Join([]string{"TERMLINK-DEVICE-REGISTER-V1", device.id.String(), deviceType, name, signing, exchange, keyID, strconv.FormatInt(timestamp, 10), nonce}, "\n")
	body := map[string]any{"id": device.id, "type": deviceType, "name": name, "signing_public_key": signing, "exchange_public_key": exchange, "key_id": keyID, "timestamp_ms": timestamp, "nonce": nonce, "signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(device.private, []byte(canonical)))}
	data, _ := json.Marshal(body)
	request, _ := http.NewRequest(http.MethodPost, root+"/v1/devices/register", bytes.NewReader(data))
	token := "integration"
	if deviceType == "WINDOWS_PC" {
		token = "integration-windows"
	}
	request.Header.Set("Authorization", "Bearer "+token)
	request.Header.Set("Content-Type", "application/json")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		failure, _ := io.ReadAll(response.Body)
		t.Fatalf("register status=%d body=%s", response.StatusCode, failure)
	}
	var result map[string]any
	if err := json.NewDecoder(response.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	return result
}

func signedJSON(t *testing.T, root string, device testDevice, method, path string, body any, idempotency string, wantStatus int) map[string]any {
	t.Helper()
	var data []byte
	if body != nil {
		data, _ = json.Marshal(body)
	}
	request := newSignedRequest(t, root, device, method, path, data, idempotency)
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

func newSignedRequest(t *testing.T, root string, device testDevice, method, path string, body []byte, idempotency string) *http.Request {
	t.Helper()
	request, err := http.NewRequest(method, root+path, bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	timestamp := strconv.FormatInt(time.Now().UnixMilli(), 10)
	nonce := randomNonce(t)
	hash := sha256.Sum256(body)
	signature := ed25519.Sign(device.private, auth.CanonicalRequest(method, path, timestamp, nonce, hash))
	request.Header.Set("Authorization", "Bearer integration")
	request.Header.Set("X-TermLink-Device", device.id.String())
	request.Header.Set("X-TermLink-Timestamp", timestamp)
	request.Header.Set("X-TermLink-Nonce", nonce)
	request.Header.Set("X-TermLink-Signature", base64.RawURLEncoding.EncodeToString(signature))
	if len(body) > 0 {
		request.Header.Set("Content-Type", "application/json")
	}
	if idempotency != "" {
		request.Header.Set("Idempotency-Key", idempotency)
	}
	return request
}

func dialRealtime(t *testing.T, root string, device testDevice, role string) *websocket.Conn {
	t.Helper()
	path := "/v1/realtime"
	request := newSignedRequest(t, "", device, http.MethodGet, path, nil, "")
	header := request.Header
	dialer := websocket.Dialer{Subprotocols: []string{"termlink.realtime.v1"}}
	socket, response, err := dialer.Dial("ws"+strings.TrimPrefix(root, "http")+path, header)
	if err != nil {
		if response != nil {
			t.Fatalf("realtime status=%d error=%v", response.StatusCode, err)
		}
		t.Fatal(err)
	}
	hello := fmt.Sprintf(`{"role":%q,"protocol_versions":[1],"capabilities":["cumulative_ack","e2ee_x25519_chacha20poly1305","flow_control_v1","lease_v1"]}`, role)
	if err := socket.WriteMessage(websocket.TextMessage, []byte(hello)); err != nil {
		t.Fatal(err)
	}
	messageType, _, err := socket.ReadMessage()
	if err != nil || messageType != websocket.TextMessage {
		t.Fatalf("welcome type=%d error=%v", messageType, err)
	}
	return socket
}

func testEnvelope(sender, recipient testDevice, sessionID uuid.UUID, messageType realtimewire.MessageType, direction realtimewire.Direction, sequence uint64) []byte {
	return testEnvelopeWithACK(sender, recipient, sessionID, messageType, direction, sequence, 0)
}

func testEnvelopeWithACK(sender, recipient testDevice, sessionID uuid.UUID, messageType realtimewire.MessageType, direction realtimewire.Direction, sequence, ack uint64) []byte {
	envelope := realtimewire.Envelope{ProtocolVersion: 1, MessageType: messageType, SessionID: sessionID[:], SenderDeviceID: sender.id[:], RecipientDeviceID: recipient.id[:], Direction: direction, Sequence: sequence, ACK: ack, SentAtMS: time.Now().UnixMilli(), KeyID: []byte("epoch-01"), Nonce: bytes.Repeat([]byte{byte(sequence)}, 12), Ciphertext: bytes.Repeat([]byte{0x44}, 32)}
	encoded, err := envelope.Marshal()
	if err != nil {
		panic(err)
	}
	return encoded
}
func writeEnvelope(t *testing.T, socket *websocket.Conn, frame []byte) {
	t.Helper()
	if err := socket.WriteMessage(websocket.BinaryMessage, frame); err != nil {
		t.Fatal(err)
	}
}
func readEnvelope(t *testing.T, socket *websocket.Conn) []byte {
	t.Helper()
	_ = socket.SetReadDeadline(time.Now().Add(5 * time.Second))
	messageType, frame, err := socket.ReadMessage()
	if err != nil {
		t.Fatal(err)
	}
	if messageType != websocket.BinaryMessage {
		t.Fatalf("message type=%d", messageType)
	}
	return frame
}
func randomNonce(t *testing.T) string {
	t.Helper()
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		t.Fatal(err)
	}
	return base64.RawURLEncoding.EncodeToString(value)
}

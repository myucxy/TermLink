package api_test

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/realtime"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPCMachineIdentityRebindPreservesPCAndRevokesOldRealtime(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("PC machine identity API integration requires PostgreSQL and Valkey")
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
	repository.AutoTrustDevices = true
	valkey, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	defer valkey.Close()

	issuer := "https://stable-machine.issuer.test"
	subject := "stable-machine-" + uuid.NewString()
	crossSubject := "stable-machine-cross-" + uuid.NewString()
	verifier := integrationVerifier{identities: map[string]auth.Identity{
		"stable-machine-first": {
			Issuer: issuer, Subject: subject, AuthorizedParty: "termlink-windows",
			TokenID: "stable-machine-first-" + uuid.NewString(),
		},
		"stable-machine-second": {
			Issuer: issuer, Subject: subject, AuthorizedParty: "termlink-windows",
			TokenID: "stable-machine-second-" + uuid.NewString(),
		},
		"stable-machine-cross": {
			Issuer: issuer, Subject: crossSubject, AuthorizedParty: "termlink-windows",
			TokenID: "stable-machine-cross-" + uuid.NewString(),
		},
	}}
	subjectKey := []byte("stable-machine-subject-key-32byte")
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
	authorityContext, stopAuthority := context.WithCancel(ctx)
	defer stopAuthority()
	authoritySubscription := valkey.Client.Subscribe(authorityContext, "device-revoked")
	if _, err := authoritySubscription.Receive(authorityContext); err != nil {
		t.Fatal(err)
	}
	defer authoritySubscription.Close()
	go func() {
		for message := range authoritySubscription.Channel() {
			if deviceID, parseErr := uuid.Parse(message.Payload); parseErr == nil {
				hub.Revoke(deviceID)
			}
		}
	}()
	mux := http.NewServeMux()
	mux.Handle("/v1/realtime", authentication.Authenticate(hub.Handler()))
	mux.Handle("/", service.Handler())
	testServer := httptest.NewServer(mux)
	defer testServer.Close()

	first := newDevice(t)
	second := newDevice(t)
	firstRegistration := sameAccountRegister(
		t, testServer.URL, first, "WINDOWS_PC", "Stable machine", "stable-machine-first")
	userID := mustResponseUUID(t, firstRegistration, "user_id")
	defer func() {
		_, _ = repository.Pool.Exec(context.Background(), `DELETE FROM users WHERE id=$1`, userID)
	}()
	pcID := uuid.New()
	sessionID := uuid.New()
	machineDigest := sha256.Sum256([]byte("stable-machine-api:" + uuid.NewString()))
	machineID := base64.RawURLEncoding.EncodeToString(machineDigest[:])
	firstUpsert := sameAccountSignedJSON(
		t, testServer.URL, first, "stable-machine-first", http.MethodPut,
		"/v1/pcs/"+pcID.String(),
		map[string]any{
			"name": "Stable machine", "agent_version": "integration", "key_epoch": 3,
			"machine_id": machineID,
		},
		"stable-machine-first-upsert", http.StatusOK)
	if fmt.Sprint(firstUpsert["id"]) != pcID.String() || firstUpsert["key_epoch"] != float64(3) {
		t.Fatalf("first PC upsert returned id=%v, want %s", firstUpsert["id"], pcID)
	}
	sameAccountSignedJSON(
		t, testServer.URL, first, "stable-machine-first", http.MethodPut,
		"/v1/sessions/"+sessionID.String(),
		map[string]any{
			"pc_id": pcID, "shell_type": "pwsh", "title_cipher": "", "state": "RUNNING",
			"columns": 120, "rows": 40, "last_output_seq": 0,
		},
		"stable-machine-session", http.StatusOK)

	resolved := resolveMachine(t, testServer.URL, "stable-machine-second", machineID, http.StatusOK)
	if resolved["found"] != true || fmt.Sprint(resolved["pc_id"]) != pcID.String() ||
		resolved["pc_name"] != "Stable machine" || resolved["key_epoch"] != float64(3) {
		t.Fatalf("same-account machine resolution=%v", resolved)
	}
	crossResolved := resolveMachine(t, testServer.URL, "stable-machine-cross", machineID, http.StatusOK)
	if crossResolved["found"] != false {
		t.Fatalf("cross-account machine resolution leaked a PC: %v", crossResolved)
	}

	oldSocket := sameAccountDialRealtime(t, testServer.URL, first, "stable-machine-first", "pc")
	defer oldSocket.Close()
	secondRegistration := sameAccountRegister(
		t, testServer.URL, second, "WINDOWS_PC", "Stable machine", "stable-machine-second")
	if mustResponseUUID(t, secondRegistration, "user_id") != userID {
		t.Fatal("replacement Windows device was registered to a different account")
	}
	secondUpsert := sameAccountSignedJSON(
		t, testServer.URL, second, "stable-machine-second", http.MethodPut,
		"/v1/pcs/"+pcID.String(),
		map[string]any{
			"name": "Stable machine", "agent_version": "integration-rotated", "key_epoch": 3,
			"machine_id": machineID,
		},
		"stable-machine-second-upsert", http.StatusOK)
	if fmt.Sprint(secondUpsert["id"]) != pcID.String() || secondUpsert["key_epoch"] != float64(4) {
		t.Fatalf("rotated PC upsert returned id=%v, want %s", secondUpsert["id"], pcID)
	}
	assertWebSocketClosed(t, oldSocket, websocket.ClosePolicyViolation)

	sessions := sameAccountSignedJSON(
		t, testServer.URL, second, "stable-machine-second", http.MethodGet,
		"/v1/pcs/"+pcID.String()+"/sessions", nil, "", http.StatusOK)
	assertSingleItemID(t, sessions, sessionID)
	var pcCount, sessionCount int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT
			(SELECT count(*) FROM pcs WHERE owner_user_id=$1),
			(SELECT count(*) FROM sessions WHERE pc_id=$2)`, userID, pcID).
		Scan(&pcCount, &sessionCount); err != nil {
		t.Fatal(err)
	}
	if pcCount != 1 || sessionCount != 1 {
		t.Fatalf("stable rebind PC/session counts=%d/%d, want 1/1", pcCount, sessionCount)
	}
	assertDeviceTrustState(t, repository, first.id, "REVOKED")
	assertDeviceTrustState(t, repository, second.id, "TRUSTED")
}

func resolveMachine(t *testing.T, root, bearer, machineID string, wantStatus int) map[string]any {
	t.Helper()
	body, err := json.Marshal(map[string]string{"machine_id": machineID})
	if err != nil {
		t.Fatal(err)
	}
	request, err := http.NewRequest(http.MethodPost, root+"/v1/pcs/resolve-machine", bytes.NewReader(body))
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
	raw, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	if response.StatusCode != wantStatus {
		t.Fatalf("resolve machine status=%d body=%s", response.StatusCode, raw)
	}
	var result map[string]any
	if err := json.Unmarshal(raw, &result); err != nil {
		t.Fatal(err)
	}
	return result
}

func assertDeviceTrustState(t *testing.T, repository *store.Postgres, deviceID uuid.UUID, want string) {
	t.Helper()
	var got string
	if err := repository.Pool.QueryRow(
		context.Background(), `SELECT trust_state FROM devices WHERE id=$1`, deviceID).Scan(&got); err != nil {
		t.Fatal(err)
	}
	if got != want {
		t.Fatalf("device %s trust state=%q, want %q", deviceID, got, want)
	}
}

package api_test

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

func TestPCUpsertCurrentStateReplaysLatestBodyWithoutEpochRotation(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	redisURL := os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("PC upsert idempotency integration requires PostgreSQL and Valkey")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
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

	const bearer = "pc-upsert-idempotency"
	issuer := "https://pc-upsert-idempotency.issuer.test"
	subject := "pc-upsert-idempotency-" + uuid.NewString()
	verifier := integrationVerifier{identities: map[string]auth.Identity{
		bearer: {
			Issuer: issuer, Subject: subject, AuthorizedParty: "termlink-windows",
			TokenID: bearer + "-" + uuid.NewString(),
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
	mux := http.NewServeMux()
	mux.Handle("/", service.Handler())
	testServer := httptest.NewServer(mux)
	defer testServer.Close()

	windows := newDevice(t)
	registration := sameAccountRegister(t, testServer.URL, windows, "WINDOWS_PC", "State A", bearer)
	userID := mustResponseUUID(t, registration, "user_id")
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, userID)
	}()

	pcID := uuid.New()
	bodies := []map[string]any{
		{"name": "State A", "agent_version": "agent-a", "key_epoch": 3},
		{"name": "State B", "agent_version": "agent-b", "key_epoch": 3},
		{"name": "State A", "agent_version": "agent-a", "key_epoch": 3},
	}
	serializedFirst, err := json.Marshal(bodies[0])
	if err != nil {
		t.Fatal(err)
	}
	digestMaterial := []byte(http.MethodPut + "\n" + "/v1/pcs/" + pcID.String() + "\n")
	digestMaterial = append(digestMaterial, serializedFirst...)
	requestHash := sha256.Sum256(digestMaterial)
	const stateKey = "pc-state-refresh"
	claim, err := repository.ClaimIdempotency(ctx, userID, stateKey, requestHash)
	if err != nil {
		t.Fatal(err)
	}
	if !claim.Claimed {
		t.Fatal("failed to seed stale PC upsert idempotency record")
	}
	if err := repository.CompleteIdempotency(ctx, userID, stateKey, requestHash, http.StatusBadRequest, json.RawMessage(`{"code":"OLD_CACHED_ERROR"}`)); err != nil {
		t.Fatal(err)
	}
	for index, body := range bodies {
		response := sameAccountSignedJSON(
			t, testServer.URL, windows, bearer, http.MethodPut,
			"/v1/pcs/"+pcID.String(), body, stateKey, http.StatusOK)
		if got := fmt.Sprint(response["key_epoch"]); got != "3" {
			t.Fatalf("upsert %d returned key_epoch=%v, want 3", index+1, response["key_epoch"])
		}
	}

	var name, agentVersion string
	var keyEpoch int64
	if err := repository.Pool.QueryRow(ctx,
		`SELECT name,agent_version,key_epoch FROM pcs WHERE id=$1`, pcID).
		Scan(&name, &agentVersion, &keyEpoch); err != nil {
		t.Fatal(err)
	}
	if name != "State A" || agentVersion != "agent-a" || keyEpoch != 3 {
		t.Fatalf("stored PC state=%q/%q/%d, want State A/agent-a/3", name, agentVersion, keyEpoch)
	}
}

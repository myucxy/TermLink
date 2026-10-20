package api_test

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/api"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/store"
)

// The Windows recovery path relies on registration creating only absent IDs,
// followed by a normal authenticated self request with the original signing key.
func TestRetainedDeviceRecoveryCannotOverwriteExistingIdentity(t *testing.T) {
	databaseURL, redisURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL"), os.Getenv("TERMLINK_INTEGRATION_REDIS_URL")
	if databaseURL == "" || redisURL == "" {
		t.Skip("retained device recovery integration requires PostgreSQL and Redis/Valkey")
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
	coordinator, err := kv.Open(ctx, redisURL)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	verifier := integrationVerifier{identities: map[string]auth.Identity{
		"recovery": {Issuer: "https://recovery.test", Subject: uuid.NewString(), AuthorizedParty: "termlink-windows"},
		"other":    {Issuer: "https://recovery.test", Subject: uuid.NewString(), AuthorizedParty: "termlink-windows"},
	}}
	key := []byte("retained-recovery-subject-key-32-bytes")
	service := &api.API{
		Store: repository, Valkey: coordinator, Tokens: verifier, SubjectKey: key, TokenKey: key,
		Auth:         auth.Middleware{Tokens: verifier, Devices: repository, Nonces: coordinator, SubjectKey: key},
		ClientConfig: api.ClientConfiguration{WindowsRegistrationParties: []string{"termlink-windows"}},
	}
	server := httptest.NewServer(service.Handler())
	defer server.Close()
	device := newDevice(t)
	var userID uuid.UUID
	defer func() {
		if userID != uuid.Nil {
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM audit_events WHERE user_id=$1`, userID)
			_, _ = repository.Pool.Exec(ctx, `DELETE FROM users WHERE id=$1`, userID)
		}
	}()
	register := func(candidate testDevice, bearer string, wantStatus int) map[string]any {
		t.Helper()
		timestamp, nonce := time.Now().UnixMilli(), randomNonce(t)
		signing, exchange := base64.RawURLEncoding.EncodeToString(candidate.public), base64.RawURLEncoding.EncodeToString(candidate.exchange)
		keyID := "device-key-" + candidate.id.String()
		canonical := strings.Join([]string{"TERMLINK-DEVICE-REGISTER-V1", candidate.id.String(), "PC_AGENT", "Retained PC",
			signing, exchange, keyID, strconv.FormatInt(timestamp, 10), nonce}, "\n")
		return sameAccountSignedJSON(t, server.URL, candidate, bearer, http.MethodPost, "/v1/devices/register", map[string]any{
			"id": candidate.id, "type": "PC_AGENT", "name": "Retained PC", "signing_public_key": signing,
			"exchange_public_key": exchange, "key_id": keyID, "timestamp_ms": timestamp, "nonce": nonce,
			"signature": base64.RawURLEncoding.EncodeToString(ed25519.Sign(candidate.private, []byte(canonical))),
		}, "", wantStatus)
	}
	self := func(candidate testDevice, bearer string, wantStatus int, wantCode string) {
		t.Helper()
		result := sameAccountSignedJSON(t, server.URL, candidate, bearer, http.MethodGet, "/v1/devices/self", nil, "", wantStatus)
		if wantCode != "" {
			failure, ok := result["error"].(map[string]any)
			if !ok || failure["code"] != wantCode {
				t.Fatalf("device self result=%v, want %s", result, wantCode)
			}
		}
	}

	self(device, "recovery", http.StatusUnauthorized, "UNAUTHENTICATED")
	userID = mustResponseUUID(t, register(device, "recovery", http.StatusCreated), "user_id")
	self(device, "recovery", http.StatusOK, "")
	register(device, "recovery", http.StatusConflict)

	imposter := newDevice(t)
	imposter.id = device.id
	register(imposter, "recovery", http.StatusConflict)
	self(imposter, "recovery", http.StatusUnauthorized, "UNAUTHENTICATED")
	self(device, "recovery", http.StatusOK, "")
	register(device, "other", http.StatusConflict)
	self(device, "other", http.StatusUnauthorized, "UNAUTHENTICATED")
	register(device, "invalid-token", http.StatusUnauthorized)

	if _, err := repository.Pool.Exec(ctx, `UPDATE devices SET disabled_at=now(),disabled_reason='recovery-test' WHERE id=$1`, device.id); err != nil {
		t.Fatal(err)
	}
	self(device, "recovery", http.StatusUnauthorized, "DEVICE_DISABLED")
	register(device, "recovery", http.StatusConflict)
	var disabled bool
	var signingKey []byte
	if err := repository.Pool.QueryRow(ctx, `SELECT disabled_at IS NOT NULL,signing_public_key FROM devices WHERE id=$1`, device.id).
		Scan(&disabled, &signingKey); err != nil || !disabled || !bytes.Equal(signingKey, device.public) {
		t.Fatalf("registration altered disabled identity: disabled=%v, error=%v", disabled, err)
	}
	if _, err := repository.Pool.Exec(ctx, `UPDATE devices SET disabled_at=NULL,trust_state='REVOKED',revoked_at=now() WHERE id=$1`, device.id); err != nil {
		t.Fatal(err)
	}
	self(device, "recovery", http.StatusUnauthorized, "DEVICE_REVOKED")
	register(device, "recovery", http.StatusConflict)
	assertDeviceTrustState(t, repository, device.id, "REVOKED")

	if _, err := repository.Pool.Exec(ctx, `DELETE FROM devices WHERE id=$1`, device.id); err != nil {
		t.Fatal(err)
	}
	self(device, "recovery", http.StatusUnauthorized, "UNAUTHENTICATED")
	if restoredUser := mustResponseUUID(t, register(device, "recovery", http.StatusCreated), "user_id"); restoredUser != userID {
		t.Fatal("recovered device changed account")
	}
	self(device, "recovery", http.StatusOK, "")
}

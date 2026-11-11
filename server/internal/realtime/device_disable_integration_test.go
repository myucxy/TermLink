package realtime

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/websocket"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/auth"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/migrate"
	"github.com/termlink/termlink/server/internal/outbox"
	"github.com/termlink/termlink/server/internal/store"
)

// Exercises actual PostgreSQL disable/outbox, the single-node coordinator,
// signed HTTP authentication and live WebSockets. OIDC is a local fixture;
// no account, device or transport belonging to a real deployment is used.
func TestPortalDisableOutboxClosesCurrentRelayAndDeniesReconnect(t *testing.T) {
	for _, deviceType := range []string{"IOS", "ANDROID", store.DeviceTypePCAgent} {
		t.Run(deviceType, func(t *testing.T) {
			ctx, repository := disableRelayDatabase(t)
			repository.AutoTrustDevices = true
			coordinator, err := kv.NewMemoryCoordinator(repository)
			if err != nil {
				t.Fatal(err)
			}
			defer coordinator.Close()
			logger := slog.New(slog.NewTextHandler(io.Discard, nil))
			hub := NewHub(repository, coordinator, logger)
			defer hub.Close(context.Background())
			user, target, other := uuid.New(), uuid.New(), uuid.New()
			identity := auth.Identity{Issuer: "https://fixture.invalid", Subject: "disable-fixture", AuthorizedParty: "fixture", TokenID: "fixture"}
			subjectKey := sha256.Sum256([]byte("local test-only subject hashing key"))
			subjectHash := auth.SubjectHash(subjectKey[:], identity)
			if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2)`, user, subjectHash[:]); err != nil {
				t.Fatal(err)
			}
			keys := map[uuid.UUID]ed25519.PrivateKey{}
			for _, device := range []struct {
				id   uuid.UUID
				kind string
			}{{target, deviceType}, {other, "IOS"}} {
				public, private, err := ed25519.GenerateKey(rand.Reader)
				if err != nil {
					t.Fatal(err)
				}
				keys[device.id] = private
				exchange := sha256.Sum256([]byte(device.id.String()))
				if _, err := repository.Pool.Exec(ctx, `INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state) VALUES($1,$2,$3,'disable fixture',$4,$5,$6,'TRUSTED')`, device.id, user, device.kind, public, exchange[:], device.id.String()); err != nil {
					t.Fatal(err)
				}
			}
			if deviceType == store.DeviceTypePCAgent {
				if _, err := repository.Pool.Exec(ctx, `INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version,platform,architecture,platform_version) VALUES($1,$2,$3,'disable owner fixture','0.3.16.146','macos','arm64','fixture')`, uuid.New(), user, target); err != nil {
					t.Fatal(err)
				}
			}
			authentication := auth.Middleware{Tokens: disableRelayToken{identity}, Devices: repository, Nonces: coordinator, SubjectKey: subjectKey[:]}
			server := httptest.NewServer(authentication.Authenticate(hub.Handler()))
			defer server.Close()
			endpoint := "ws" + strings.TrimPrefix(server.URL, "http") + "/v1/realtime"
			dialer := websocket.Dialer{Subprotocols: []string{subprotocol}, HandshakeTimeout: 3 * time.Second}
			connect := func(id uuid.UUID, role string) *websocket.Conn {
				t.Helper()
				socket, response, err := dialer.Dial(endpoint, disableRelayHeaders(id, keys[id]))
				if err != nil {
					if response != nil {
						response.Body.Close()
					}
					t.Fatal(err)
				}
				if err := socket.WriteJSON(map[string]any{"role": role, "protocol_versions": []int{1}, "capabilities": requiredCapabilities}); err != nil {
					socket.Close()
					t.Fatal(err)
				}
				if err := socket.SetReadDeadline(time.Now().Add(3 * time.Second)); err != nil {
					t.Fatal(err)
				}
				var welcome serverWelcome
				if err := socket.ReadJSON(&welcome); err != nil || welcome.ProtocolVersion != 1 {
					socket.Close()
					t.Fatalf("signed Relay handshake failed: %v", err)
				}
				return socket
			}
			role := "android"
			if deviceType == store.DeviceTypePCAgent {
				role = "pc"
			}
			targetSocket := connect(target, role)
			defer targetSocket.Close()
			otherSocket := connect(other, "android")
			defer otherSocket.Close()
			watch, err := coordinator.Subscribe(ctx, "device-revoked")
			if err != nil {
				t.Fatal(err)
			}
			defer watch.Close()
			if result, err := repository.DisableUserPortalDevice(ctx, user, target, "disable-connected-fixture", "test-disable-relay"); err != nil || !result.Changed {
				t.Fatalf("disable result=%+v err=%v", result, err)
			}
			workerCtx, stop := context.WithCancel(ctx)
			workerDone := make(chan struct{})
			go func() {
				defer close(workerDone)
				(outbox.Dispatcher{Store: repository, Valkey: coordinator, Logger: logger}).Run(workerCtx)
			}()
			defer func() {
				stop()
				select {
				case <-workerDone:
				case <-time.After(3 * time.Second):
					t.Error("outbox worker did not stop")
				}
			}()
			message, err := watch.Receive(ctx)
			if err != nil || message.Channel != "device-revoked" || message.ID != target {
				t.Fatalf("disable revocation message=%+v err=%v", message, err)
			}
			// This is the existing production device-revoked dispatch hook.
			hub.Revoke(message.ID)
			if _, _, err := targetSocket.ReadMessage(); !websocket.IsCloseError(err, websocket.ClosePolicyViolation) {
				t.Fatalf("disabled live socket was not policy-closed: %v", err)
			}
			hub.mu.RLock()
			unaffected := hub.peers[other]
			alive := unaffected != nil && unaffected.open()
			hub.mu.RUnlock()
			if !alive {
				t.Fatal("disabling one device closed another account device")
			}
			if err := otherSocket.WriteControl(websocket.PingMessage, []byte("fixture"), time.Now().Add(time.Second)); err != nil {
				t.Fatal("unaffected Relay socket failed", err)
			}
			reconnected, response, err := dialer.Dial(endpoint, disableRelayHeaders(target, keys[target]))
			if reconnected != nil {
				reconnected.Close()
				t.Fatal("disabled device reconnected")
			}
			if response == nil || response.StatusCode != http.StatusUnauthorized || err == nil {
				t.Fatalf("disabled reconnect status/error was not unauthorized: %v", err)
			}
			response.Body.Close()
		})
	}
}

type disableRelayToken struct{ identity auth.Identity }

func (f disableRelayToken) Verify(_ context.Context, token string) (auth.Identity, error) {
	if token != "fixture-token" {
		return auth.Identity{}, errors.New("invalid fixture token")
	}
	return f.identity, nil
}
func disableRelayHeaders(id uuid.UUID, key ed25519.PrivateKey) http.Header {
	nonceBytes := make([]byte, 16)
	if _, err := rand.Read(nonceBytes); err != nil {
		panic(err)
	}
	nonce := base64.RawURLEncoding.EncodeToString(nonceBytes)
	timestamp := strconv.FormatInt(time.Now().UnixMilli(), 10)
	digest := sha256.Sum256(nil)
	return http.Header{"Authorization": {"Bearer fixture-token"}, "X-TermLink-Device": {id.String()}, "X-TermLink-Timestamp": {timestamp}, "X-TermLink-Nonce": {nonce}, "X-TermLink-Signature": {base64.RawURLEncoding.EncodeToString(ed25519.Sign(key, auth.CanonicalRequest(http.MethodGet, "/v1/realtime", timestamp, nonce, digest)))}}
}
func disableRelayDatabase(t *testing.T) (context.Context, *store.Postgres) {
	t.Helper()
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		cancel()
		t.Fatal(err)
	}
	name := "termlink_disable_" + uuid.NewString()[:8]
	quoted := pgx.Identifier{name}.Sanitize()
	if _, err := admin.Exec(ctx, `CREATE DATABASE `+quoted); err != nil {
		admin.Close()
		cancel()
		t.Fatal(err)
	}
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.Database = name
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		pool.Close()
		cleanup, done := context.WithTimeout(context.Background(), 5*time.Second)
		defer done()
		if _, err := admin.Exec(cleanup, `DROP DATABASE `+quoted+` WITH (FORCE)`); err != nil {
			t.Error(fmt.Errorf("clean fixture database: %w", err))
		}
		admin.Close()
		cancel()
	})
	if err := migrate.UpPool(ctx, pool); err != nil {
		t.Fatal(err)
	}
	return ctx, &store.Postgres{Pool: pool}
}

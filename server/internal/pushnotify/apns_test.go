package pushnotify

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/google/uuid"
)

func testConfig(t *testing.T) (Config, *ecdsa.PrivateKey) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	encoded, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	return Config{TeamID: "ABCDEFGHIJ", KeyID: "KLMNOPQRST", Topic: "dev.termlink.ios", Environment: EnvironmentSandbox,
		PrivateKey: pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: encoded})}, key
}
func testMessage(now time.Time) Message {
	return Message{ID: uuid.New(), PCID: uuid.New(), SessionID: uuid.New(), Token: strings.Repeat("ab", 32),
		Topic: "dev.termlink.ios", Environment: EnvironmentSandbox, Kind: KindAINeedsInput, ExpiresAt: now.Add(time.Hour)}
}

func TestNotificationSoundRequiresExplicitPreference(t *testing.T) {
	message := testMessage(time.Now())
	for _, enabled := range []bool{false, true} {
		message.SoundEnabled = enabled
		body, err := notificationPayload(message)
		if err != nil {
			t.Fatal(err)
		}
		var payload map[string]json.RawMessage
		if err = json.Unmarshal(body, &payload); err != nil {
			t.Fatal(err)
		}
		var aps map[string]any
		if err = json.Unmarshal(payload["aps"], &aps); err != nil {
			t.Fatal(err)
		}
		_, present := aps["sound"]
		if present != enabled {
			t.Fatalf("sound=%v presence=%v", enabled, present)
		}
	}
}
func tlsProvider(t *testing.T, config Config, handler http.HandlerFunc, now func() time.Time) *APNs {
	t.Helper()
	server := httptest.NewUnstartedServer(handler)
	server.EnableHTTP2 = true
	server.StartTLS()
	t.Cleanup(server.Close)
	provider, err := newAPNs(config, server.Client(), server.URL, now)
	if err != nil {
		t.Fatal(err)
	}
	return provider
}
func verifyJWT(t *testing.T, authorization string, key *ecdsa.PrivateKey, config Config, now time.Time) string {
	t.Helper()
	if !strings.HasPrefix(authorization, "bearer ") {
		t.Error("missing provider JWT")
		return ""
	}
	jwt := strings.TrimPrefix(authorization, "bearer ")
	parts := strings.Split(jwt, ".")
	if len(parts) != 3 {
		t.Error("malformed JWT")
		return ""
	}
	decode := func(value string) []byte {
		data, err := base64.RawURLEncoding.DecodeString(value)
		if err != nil {
			t.Error("invalid JWT encoding")
		}
		return data
	}
	var header map[string]string
	var claims map[string]any
	if json.Unmarshal(decode(parts[0]), &header) != nil || json.Unmarshal(decode(parts[1]), &claims) != nil {
		t.Error("invalid JWT JSON")
	}
	if header["alg"] != "ES256" || header["kid"] != config.KeyID || claims["iss"] != config.TeamID || claims["iat"] != float64(now.Unix()) {
		t.Error("JWT claims differ")
	}
	signature := decode(parts[2])
	hash := sha256.Sum256([]byte(parts[0] + "." + parts[1]))
	if len(signature) != 64 || !ecdsa.Verify(&key.PublicKey, hash[:], new(big.Int).SetBytes(signature[:32]), new(big.Int).SetBytes(signature[32:])) {
		t.Error("JWT ES256 signature is invalid")
	}
	return jwt
}

func TestAPNsHTTP2JWTCacheAndPayloadAllowlist(t *testing.T) {
	config, key := testConfig(t)
	now := time.Now().UTC().Truncate(time.Second)
	issued := now
	var tokens []string
	var payloads []map[string]any
	var collapses []string
	var mu sync.Mutex
	provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) {
		if r.ProtoMajor != 2 || r.TLS == nil || r.TLS.Version < tls.VersionTLS12 || r.Method != http.MethodPost {
			t.Error("push did not use POST + HTTP/2 TLS")
		}
		if r.URL.Path != "/3/device/"+strings.Repeat("ab", 32) || r.Header.Get("apns-topic") != config.Topic ||
			r.Header.Get("apns-push-type") != "alert" || r.Header.Get("apns-priority") != "10" {
			t.Error("unexpected APNs routing/header")
		}
		if _, err := uuid.Parse(r.Header.Get("apns-id")); err != nil {
			t.Error("invalid apns-id")
		}
		if r.Header.Get("apns-expiration") != fmt.Sprint(now.Add(time.Hour).Unix()) {
			t.Error("expiry not carried to APNs")
		}
		var payload map[string]any
		if json.NewDecoder(r.Body).Decode(&payload) != nil {
			t.Error("invalid payload")
		}
		jwt := verifyJWT(t, r.Header.Get("authorization"), key, config, issued)
		mu.Lock()
		tokens = append(tokens, jwt)
		payloads = append(payloads, payload)
		collapses = append(collapses, r.Header.Get("apns-collapse-id"))
		mu.Unlock()
		w.WriteHeader(http.StatusOK)
	}, func() time.Time { return now })
	message := testMessage(now)
	message.SoundEnabled = true
	for i := 0; i < 3; i++ {
		if i == 1 {
			now = now.Add(29 * time.Minute)
		}
		if i == 2 {
			now = now.Add(time.Minute)
			issued = now
		}
		message.ExpiresAt = now.Add(time.Hour)
		result := provider.Send(context.Background(), message)
		if result.Status != Sent || result.Code != "APNS_SENT" {
			t.Fatalf("successful HTTP2 push: %+v", result)
		}
	}
	if tokens[0] != tokens[1] || tokens[1] == tokens[2] {
		t.Fatal("provider JWT was not cached 30 minutes and then rotated")
	}
	for _, payload := range payloads {
		keys := make([]string, 0, len(payload))
		for k := range payload {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		if !reflect.DeepEqual(keys, []string{"aps", "event_id", "kind", "pc_id", "session_id"}) || payload["event_id"] != message.ID.String() || payload["pc_id"] != message.PCID.String() || payload["session_id"] != message.SessionID.String() || payload["kind"] != KindAINeedsInput {
			t.Fatal("payload identity/allowlist changed")
		}
		aps := payload["aps"].(map[string]any)
		if aps["alert"].(map[string]any)["body"] != "AI 会话需要确认" || len(aps) != 2 {
			t.Fatal("payload contained custom actions/content")
		}
		encoded, _ := json.Marshal(payload)
		if bytes.Contains(encoded, []byte(message.Token)) || len(encoded) >= 4096 {
			t.Fatal("token leak/oversized payload")
		}
	}
	if len(collapses[0]) > 64 || collapses[0] == "" || collapses[0] != collapses[1] {
		t.Fatal("collapse ID not bounded/stable")
	}
	for _, kind := range []string{KindAICompleted, KindUpgradeSucceeded, KindUpgradeFailed} {
		copy := message
		copy.Kind = kind
		copy.SessionID = uuid.Nil
		payload, err := notificationPayload(copy)
		if err != nil || strings.Contains(string(payload), "session_id") || strings.Contains(string(payload), "category") {
			t.Fatal("unexpected upgrade/AI payload field")
		}
	}
}

func TestAPNsConfigurationAndFixedRouting(t *testing.T) {
	config, _ := testConfig(t)
	if err := ValidateConfig(config); err != nil {
		t.Fatal(err)
	}
	for _, env := range []string{EnvironmentProduction, EnvironmentSandbox} {
		copy := config
		copy.Environment = env
		provider, err := NewAPNs(copy)
		if err != nil {
			t.Fatal(err)
		}
		want := productionEndpoint
		if env == EnvironmentSandbox {
			want = sandboxEndpoint
		}
		transport := provider.client.Transport.(*http.Transport)
		if provider.endpoint != want || transport.Proxy != nil || !transport.ForceAttemptHTTP2 || transport.TLSClientConfig.MinVersion < tls.VersionTLS12 || provider.client.Timeout > providerTimeout {
			t.Fatal("public provider transport policy weakened")
		}
		if !errors.Is(provider.client.CheckRedirect(nil, nil), http.ErrUseLastResponse) {
			t.Fatal("redirect enabled")
		}
	}
	p384, _ := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	invalidCurve, _ := x509.MarshalPKCS8PrivateKey(p384)
	for _, change := range []func(*Config){
		func(c *Config) { c.TeamID = "bad" }, func(c *Config) { c.KeyID = "bad" },
		func(c *Config) { c.Environment = "https://evil.invalid" }, func(c *Config) { c.Topic = "a\r\nb" },
		func(c *Config) { c.PrivateKey = []byte("not a private key") },
		func(c *Config) {
			c.PrivateKey = pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: invalidCurve})
		},
		func(c *Config) { c.PrivateKey = append(c.PrivateKey, []byte("extra")...) },
	} {
		copy := config
		change(&copy)
		if !errors.Is(ValidateConfig(copy), ErrConfiguration) {
			t.Fatal("invalid config accepted")
		}
		if _, err := NewAPNs(copy); !errors.Is(err, ErrConfiguration) {
			t.Fatal("invalid public provider accepted")
		}
	}
	var requests atomic.Int32
	provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) { requests.Add(1); w.WriteHeader(200) }, time.Now)
	for _, change := range []func(*Message){
		func(m *Message) { m.Topic = "dev.evil" }, func(m *Message) { m.Environment = EnvironmentProduction },
		func(m *Message) { m.Token = "invalid" }, func(m *Message) { m.ID = uuid.Nil },
		func(m *Message) { m.PCID = uuid.Nil }, func(m *Message) { m.SessionID = uuid.Nil },
		func(m *Message) { m.Kind = "approve-everything" }, func(m *Message) { m.ExpiresAt = time.Now().Add(-time.Second) },
	} {
		m := testMessage(time.Now())
		change(&m)
		if result := provider.Send(context.Background(), m); result.Status != Permanent {
			t.Fatalf("invalid message reached sender: %+v", result)
		}
	}
	if requests.Load() != 0 {
		t.Fatal("per-message topic/environment/identity bypass sent a push")
	}
}

func TestAPNsResponsesRetryAndInvalidation(t *testing.T) {
	config, _ := testConfig(t)
	now := time.Now().UTC().Truncate(time.Second)
	for _, test := range []struct {
		name        string
		status      int
		body, retry string
		want        Status
		code        string
		minimum     time.Duration
		invalidated bool
	}{
		{"unregistered", 410, fmt.Sprintf(`{"reason":"Unregistered","timestamp":%d}`, now.Add(-time.Minute).UnixMilli()), "", InvalidToken, "APNS_UNREGISTERED", 0, true},
		{"410 missing timestamp", 410, `{"reason":"Unregistered"}`, "", Retry, "APNS_RESPONSE_INVALID", 15 * time.Minute, false},
		{"400 device", 400, `{"reason":"BadDeviceToken"}`, "", InvalidToken, "APNS_BAD_DEVICE_TOKEN", 0, false},
		{"400 topic token", 400, `{"reason":"DeviceTokenNotForTopic"}`, "", InvalidToken, "APNS_BAD_DEVICE_TOKEN", 0, false},
		{"500", 500, `{"reason":"InternalServerError"}`, "", Retry, "APNS_SERVER_ERROR", 15 * time.Minute, false},
		{"503 short retry", 503, `{"reason":"ServiceUnavailable"}`, "10", Retry, "APNS_SERVER_ERROR", 15 * time.Minute, false},
		{"503 long retry", 503, `{"reason":"ServiceUnavailable"}`, "1800", Retry, "APNS_SERVER_ERROR", 30 * time.Minute, false},
		{"429 date", 429, `{"reason":"TooManyRequests"}`, now.Add(2 * time.Hour).Format(http.TimeFormat), Retry, "APNS_RATE_LIMITED", 2 * time.Hour, false},
		{"429 malformed", 429, `{"reason":"TooManyProviderTokenUpdates"}`, "invalid", Retry, "APNS_RATE_LIMITED", time.Minute, false},
		{"provider expired", 403, `{"reason":"ExpiredProviderToken"}`, "", Retry, "APNS_PROVIDER_TOKEN_EXPIRED", 20 * time.Minute, false},
		{"bad auth", 403, `{"reason":"InvalidProviderToken"}`, "", Permanent, "APNS_REQUEST_REJECTED", 0, false},
		{"redirect", 302, `{"reason":"private-data-must-not-leak"}`, "", Permanent, "APNS_REQUEST_REJECTED", 0, false},
		{"unknown body", 400, `{"reason":"token-secret-must-not-leak"}`, "", Permanent, "APNS_REQUEST_REJECTED", 0, false},
		{"large body", 400, strings.Repeat("secret", 1000), "", Retry, "APNS_RESPONSE_INVALID", 15 * time.Minute, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Retry-After", test.retry)
				w.WriteHeader(test.status)
				_, _ = io.WriteString(w, test.body)
			}, func() time.Time { return now })
			result := provider.Send(context.Background(), testMessage(now))
			if result.Status != test.want || result.Code != test.code || result.RetryAfter < test.minimum || (result.InvalidatedAt != nil) != test.invalidated {
				t.Fatalf("response mapping: %+v", result)
			}
			if result.InvalidatedAt != nil && !result.InvalidatedAt.Equal(now.Add(-time.Minute)) {
				t.Fatal("Apple millisecond invalidation timestamp changed")
			}
			if strings.Contains(fmt.Sprint(result), "secret") {
				t.Fatal("provider error leaked raw data")
			}
		})
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestAPNsRefusesRedirectHTTP1AndMasksNetworkErrors(t *testing.T) {
	config, _ := testConfig(t)
	now := time.Now()
	var requests atomic.Int32
	provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) { requests.Add(1); http.Redirect(w, r, "/redirected", 302) }, func() time.Time { return now })
	if result := provider.Send(context.Background(), testMessage(now)); result.Status != Permanent || requests.Load() != 1 {
		t.Fatal("APNs redirect was followed")
	}
	http1 := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(200) }))
	defer http1.Close()
	provider, _ = newAPNs(config, http1.Client(), http1.URL, func() time.Time { return now })
	if result := provider.Send(context.Background(), testMessage(now)); result.Code != "APNS_PROTOCOL_INVALID" || result.Status != Retry {
		t.Fatal("HTTP1 accepted")
	}
	secret := testMessage(now).Token
	provider, _ = newAPNs(config, &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
		return nil, &url.Error{Op: "Post", URL: r.URL.String(), Err: fmt.Errorf("credential %s", secret)}
	})}, sandboxEndpoint, func() time.Time { return now })
	result := provider.Send(context.Background(), testMessage(now))
	if result.Status != Retry || strings.Contains(fmt.Sprint(result), secret) || strings.Contains(fmt.Sprint(result), "https:") {
		t.Fatal("network error leaked token/URL")
	}
}

func TestAPNsConcurrentRequestsReuseOneProviderJWT(t *testing.T) {
	config, _ := testConfig(t)
	now := time.Now()
	var mu sync.Mutex
	tokens := map[string]bool{}
	provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		tokens[r.Header.Get("authorization")] = true
		mu.Unlock()
		w.WriteHeader(200)
	}, func() time.Time { return now })
	var group sync.WaitGroup
	for i := 0; i < 16; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			if result := provider.Send(context.Background(), testMessage(now)); result.Status != Sent {
				t.Error("concurrent push failed")
			}
		}()
	}
	group.Wait()
	if len(tokens) != 1 {
		t.Fatal("concurrent sends minted repeated provider JWTs")
	}
}

func TestAPNsCanceledContextAndExpiryBoundNetworkWait(t *testing.T) {
	config, _ := testConfig(t)
	now := time.Now()
	var calls atomic.Int32
	provider := tlsProvider(t, config, func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		select {
		case <-r.Context().Done():
		case <-time.After(time.Second):
		}
	}, time.Now)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if result := provider.Send(ctx, testMessage(now)); result.Status != Retry || calls.Load() != 0 {
		t.Fatal("cancelled push made a request")
	}
	message := testMessage(now)
	message.ExpiresAt = time.Now().Add(100 * time.Millisecond)
	started := time.Now()
	result := provider.Send(context.Background(), message)
	if result.Status != Retry || time.Since(started) > time.Second {
		t.Fatal("event deadline did not bound APNs request")
	}
	if got := retryAfter("999999999", now, time.Minute); got != maximumRetry {
		t.Fatal("server retry interval is unbounded")
	}
}

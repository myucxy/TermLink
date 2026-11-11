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
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"io"
	"net"
	"net/http"
	"regexp"
	"strconv"
	"sync"
	"time"

	"github.com/google/uuid"
)

const (
	productionEndpoint = "https://api.push.apple.com"
	sandboxEndpoint    = "https://api.sandbox.push.apple.com"
	providerTimeout    = 20 * time.Second
	jwtLifetime        = 30 * time.Minute
	maximumResponse    = 4096
	maximumRetry       = 24 * time.Hour
)

var appleIdentifier = regexp.MustCompile(`^[A-Z0-9]{10}$`)

type APNs struct {
	teamID, keyID, topic, environment, endpoint string
	key                                         *ecdsa.PrivateKey
	client                                      *http.Client
	now                                         func() time.Time
	mu                                          sync.Mutex
	jwt                                         string
	jwtIssued                                   time.Time
}

func NewAPNs(config Config) (*APNs, error) {
	transport := &http.Transport{
		Proxy: nil, ForceAttemptHTTP2: true,
		DialContext:         (&net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSClientConfig:     &tls.Config{MinVersion: tls.VersionTLS12},
		TLSHandshakeTimeout: 5 * time.Second, ResponseHeaderTimeout: 15 * time.Second,
		MaxConnsPerHost: 2, MaxIdleConnsPerHost: 2, MaxIdleConns: 4,
		IdleConnTimeout: 60 * time.Second, MaxResponseHeaderBytes: 16 * 1024,
	}
	endpoint := productionEndpoint
	if config.Environment == EnvironmentSandbox {
		endpoint = sandboxEndpoint
	}
	return newAPNs(config, &http.Client{Transport: transport, Timeout: providerTimeout}, endpoint, time.Now)
}

// ValidateConfig verifies fixed identity/topic/environment and parses only the
// supplied P8 key. It performs no network operation and retains no key material.
func ValidateConfig(config Config) error {
	_, err := parseConfigKey(config)
	return err
}

func parseConfigKey(config Config) (*ecdsa.PrivateKey, error) {
	if !appleIdentifier.MatchString(config.TeamID) || !appleIdentifier.MatchString(config.KeyID) || !validTopic(config.Topic) ||
		!validEnvironment(config.Environment) || len(config.PrivateKey) == 0 || len(config.PrivateKey) > 16*1024 {
		return nil, ErrConfiguration
	}
	block, rest := pem.Decode(config.PrivateKey)
	if block == nil || block.Type != "PRIVATE KEY" || len(bytes.TrimSpace(rest)) != 0 {
		return nil, ErrConfiguration
	}
	parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
	if err != nil {
		return nil, ErrConfiguration
	}
	key, ok := parsed.(*ecdsa.PrivateKey)
	if !ok || key.Curve != elliptic.P256() {
		return nil, ErrConfiguration
	}
	return key, nil
}

// Dependency injection is package-private and used only by offline HTTP/2 TLS
// fixtures. The public constructor cannot redirect pushes to arbitrary hosts.
func newAPNs(config Config, client *http.Client, endpoint string, now func() time.Time) (*APNs, error) {
	key, err := parseConfigKey(config)
	if err != nil || client == nil || now == nil {
		return nil, ErrConfiguration
	}
	isolated := *client
	isolated.Jar = nil
	isolated.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	if isolated.Timeout <= 0 || isolated.Timeout > providerTimeout {
		isolated.Timeout = providerTimeout
	}
	return &APNs{teamID: config.TeamID, keyID: config.KeyID, topic: config.Topic, environment: config.Environment,
		endpoint: endpoint, key: key, client: &isolated, now: now}, nil
}

func (a *APNs) Send(ctx context.Context, message Message) Result {
	if a == nil || a.now == nil || a.client == nil || a.key == nil || ctx == nil {
		return Result{Status: Permanent, Code: "PUSH_CONFIGURATION_INVALID"}
	}
	if ctx.Err() != nil {
		return Result{Status: Retry, Code: "PUSH_CANCELLED", RetryAfter: time.Minute}
	}
	now := a.now()
	if !message.ExpiresAt.After(now) {
		return Result{Status: Permanent, Code: "PUSH_EXPIRED"}
	}
	token, err := normalizeToken(message.Token)
	if err != nil || message.Topic != a.topic || message.Environment != a.environment || message.ID == uuid.Nil || message.PCID == uuid.Nil ||
		((message.Kind == KindAINeedsInput || message.Kind == KindAICompleted) && message.SessionID == uuid.Nil) {
		return Result{Status: Permanent, Code: "PUSH_MESSAGE_INVALID"}
	}
	body, err := notificationPayload(message)
	if err != nil {
		return Result{Status: Permanent, Code: "PUSH_MESSAGE_INVALID"}
	}
	jwt, err := a.providerToken(now)
	if err != nil {
		return Result{Status: Retry, Code: "APNS_AUTH_UNAVAILABLE", RetryAfter: 15 * time.Minute}
	}
	deadline, cancel := context.WithDeadline(ctx, message.ExpiresAt)
	defer cancel()
	request, err := http.NewRequestWithContext(deadline, http.MethodPost, a.endpoint+"/3/device/"+token, bytes.NewReader(body))
	if err != nil {
		return Result{Status: Permanent, Code: "PUSH_MESSAGE_INVALID"}
	}
	request.Header.Set("authorization", "bearer "+jwt)
	request.Header.Set("content-type", "application/json")
	request.Header.Set("apns-topic", a.topic)
	request.Header.Set("apns-push-type", "alert")
	request.Header.Set("apns-priority", "10")
	request.Header.Set("apns-id", message.ID.String())
	request.Header.Set("apns-collapse-id", collapseID(message))
	request.Header.Set("apns-expiration", strconv.FormatInt(message.ExpiresAt.Unix(), 10))
	response, err := a.client.Do(request)
	if err != nil {
		// net/http errors include the token-bearing URL. Never return/log them.
		return Result{Status: Retry, Code: "APNS_UNAVAILABLE", RetryAfter: time.Minute}
	}
	defer response.Body.Close()
	if response.ProtoMajor != 2 {
		return Result{Status: Retry, Code: "APNS_PROTOCOL_INVALID", RetryAfter: 15 * time.Minute}
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, maximumResponse+1))
	if err != nil || len(data) > maximumResponse {
		return Result{Status: Retry, Code: "APNS_RESPONSE_INVALID", RetryAfter: 15 * time.Minute}
	}
	if response.StatusCode == http.StatusOK {
		return Result{Status: Sent, Code: "APNS_SENT"}
	}
	var failure struct {
		Reason    string `json:"reason"`
		Timestamp int64  `json:"timestamp"`
	}
	if len(data) != 0 {
		decoder := json.NewDecoder(bytes.NewReader(data))
		if err := decoder.Decode(&failure); err != nil {
			failure.Reason = ""
		}
	}
	if response.StatusCode >= 500 && response.StatusCode <= 599 {
		return Result{Status: Retry, Code: "APNS_SERVER_ERROR", RetryAfter: retryAfter(response.Header.Get("Retry-After"), now, 15*time.Minute)}
	}
	if response.StatusCode == http.StatusTooManyRequests {
		return Result{Status: Retry, Code: "APNS_RATE_LIMITED", RetryAfter: retryAfter(response.Header.Get("Retry-After"), now, time.Minute)}
	}
	if response.StatusCode == http.StatusGone && failure.Reason == "Unregistered" {
		invalidated := time.UnixMilli(failure.Timestamp).UTC()
		if failure.Timestamp <= 0 || invalidated.After(now.Add(5*time.Minute)) {
			return Result{Status: Retry, Code: "APNS_RESPONSE_INVALID", RetryAfter: 15 * time.Minute}
		}
		return Result{Status: InvalidToken, Code: "APNS_UNREGISTERED", InvalidatedAt: &invalidated}
	}
	if response.StatusCode == http.StatusBadRequest && (failure.Reason == "BadDeviceToken" || failure.Reason == "DeviceTokenNotForTopic") {
		return Result{Status: InvalidToken, Code: "APNS_BAD_DEVICE_TOKEN"}
	}
	if response.StatusCode == http.StatusForbidden && failure.Reason == "ExpiredProviderToken" {
		a.mu.Lock()
		// Do not discard a newer token created by another concurrent send.
		delay := time.Minute
		if a.jwt == jwt {
			if age := now.Sub(a.jwtIssued); age >= 20*time.Minute {
				a.jwt = ""
			} else {
				delay = 20*time.Minute - age
			}
		}
		a.mu.Unlock()
		return Result{Status: Retry, Code: "APNS_PROVIDER_TOKEN_EXPIRED", RetryAfter: delay}
	}
	if response.StatusCode == http.StatusRequestTimeout || response.StatusCode == http.StatusServiceUnavailable {
		return Result{Status: Retry, Code: "APNS_UNAVAILABLE", RetryAfter: 15 * time.Minute}
	}
	// Never propagate the raw Apple reason, response body, headers or URL.
	return Result{Status: Permanent, Code: "APNS_REQUEST_REJECTED"}
}

func (a *APNs) providerToken(now time.Time) (string, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.jwt != "" && !now.Before(a.jwtIssued) && now.Sub(a.jwtIssued) < jwtLifetime {
		return a.jwt, nil
	}
	header, _ := json.Marshal(map[string]string{"alg": "ES256", "kid": a.keyID})
	claims, _ := json.Marshal(map[string]any{"iss": a.teamID, "iat": now.Unix()})
	encode := base64.RawURLEncoding.EncodeToString
	unsigned := encode(header) + "." + encode(claims)
	digest := sha256.Sum256([]byte(unsigned))
	r, s, err := ecdsa.Sign(rand.Reader, a.key, digest[:])
	if err != nil {
		return "", err
	}
	signature := make([]byte, 64)
	r.FillBytes(signature[:32])
	s.FillBytes(signature[32:])
	a.jwt, a.jwtIssued = unsigned+"."+encode(signature), now
	return a.jwt, nil
}

func notificationPayload(message Message) ([]byte, error) {
	text := ""
	switch message.Kind {
	case KindAINeedsInput:
		text = "AI 会话需要确认"
	case KindAICompleted:
		text = "AI 会话已完成回复"
	case KindUpgradeSucceeded:
		text = "客户端升级已完成"
	case KindUpgradeFailed:
		text = "客户端升级失败"
	default:
		return nil, ErrConfiguration
	}
	payload := map[string]any{
		"aps":      map[string]any{"alert": map[string]string{"title": "TermLink", "body": text}},
		"event_id": message.ID.String(), "pc_id": message.PCID.String(), "kind": message.Kind,
	}
	if message.SoundEnabled {
		payload["aps"].(map[string]any)["sound"] = "default"
	}
	if message.SessionID != uuid.Nil {
		payload["session_id"] = message.SessionID.String()
	}
	return json.Marshal(payload)
}

func collapseID(message Message) string {
	value := boundFields(message.PCID.String(), message.SessionID.String(), message.Kind)
	digest := sha256.Sum256(value)
	return hex.EncodeToString(digest[:])
}

func retryAfter(value string, now time.Time, minimum time.Duration) time.Duration {
	delay := minimum
	if seconds, err := strconv.ParseInt(value, 10, 32); err == nil && seconds > 0 {
		if duration := time.Duration(seconds) * time.Second; duration > delay {
			delay = duration
		}
	} else if date, err := http.ParseTime(value); err == nil && date.Sub(now) > delay {
		delay = date.Sub(now)
	}
	if delay > maximumRetry {
		delay = maximumRetry
	}
	return delay
}

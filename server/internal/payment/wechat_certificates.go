package payment

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	defaultWeChatCertificateGracePeriod = 24 * time.Hour
	defaultWeChatCertificateWarning     = 14 * 24 * time.Hour
	defaultWeChatCertificateRefresh     = 12 * time.Hour
	maxWeChatPlatformCertificates       = 16
)

var errUnknownWeChatCertificate = errors.New("payment: unknown wechat platform certificate")

// WeChatCertificateHealth is deliberately low-cardinality. It is suitable for
// health endpoints and metrics without exposing certificate serial numbers.
type WeChatCertificateHealth struct {
	Status               string    `json:"status"`
	ActiveCertificates   int       `json:"active_certificates"`
	RetiringCertificates int       `json:"retiring_certificates"`
	RemainingDays        int       `json:"remaining_days"`
	EarliestExpiryAt     time.Time `json:"earliest_expiry_at,omitempty"`
	LastRefreshAt        time.Time `json:"last_refresh_at,omitempty"`
}

type weChatCertificateManagerConfig struct {
	MchID              string
	MerchantSerial     string
	MerchantPrivateKey *rsa.PrivateKey
	APIv3Key           []byte
	InitialKeys        map[string]*rsa.PublicKey
	BaseURL            *url.URL
	HTTPClient         HTTPClient
	Clock              Clock
	ReplayWindow       time.Duration
	ResponseLimit      int64
	GracePeriod        time.Duration
	ExpiryWarning      time.Duration
}

type weChatPlatformCertificate struct {
	publicKey   *rsa.PublicKey
	notBefore   time.Time
	notAfter    time.Time
	retireAfter time.Time
}

type WeChatCertificateManager struct {
	mchID              string
	merchantSerial     string
	merchantPrivateKey *rsa.PrivateKey
	apiV3Key           []byte
	base               *url.URL
	client             HTTPClient
	now                Clock
	replayWindow       time.Duration
	responseLimit      int64
	gracePeriod        time.Duration
	expiryWarning      time.Duration

	refreshMu sync.Mutex
	mu        sync.RWMutex
	entries   map[string]weChatPlatformCertificate
	updatedAt time.Time
}

func newWeChatCertificateManager(config weChatCertificateManagerConfig) (*WeChatCertificateManager, error) {
	if config.BaseURL == nil || config.HTTPClient == nil || config.Clock == nil || config.MchID == "" ||
		config.MerchantSerial == "" || !validRSAPrivateKey(config.MerchantPrivateKey) ||
		len(config.APIv3Key) != 32 || len(config.InitialKeys) == 0 || len(config.InitialKeys) > maxWeChatPlatformCertificates {
		return nil, ErrInvalidConfig
	}
	if config.ReplayWindow <= 0 {
		config.ReplayWindow = defaultReplayWindow
	}
	if config.ResponseLimit <= 0 {
		config.ResponseLimit = defaultResponseLimit
	}
	if config.GracePeriod <= 0 {
		config.GracePeriod = defaultWeChatCertificateGracePeriod
	}
	if config.ExpiryWarning <= 0 {
		config.ExpiryWarning = defaultWeChatCertificateWarning
	}
	entries := make(map[string]weChatPlatformCertificate, len(config.InitialKeys))
	for serial, key := range config.InitialKeys {
		serial = normalizeWeChatCertificateSerial(serial)
		if serial == "" || !safeHeaderValue(serial) || !validRSAPublicKey(key) {
			return nil, ErrInvalidConfig
		}
		if _, duplicate := entries[serial]; duplicate {
			return nil, ErrInvalidConfig
		}
		entries[serial] = weChatPlatformCertificate{publicKey: key}
	}
	base := *config.BaseURL
	return &WeChatCertificateManager{
		mchID: config.MchID, merchantSerial: config.MerchantSerial,
		merchantPrivateKey: config.MerchantPrivateKey, apiV3Key: append([]byte(nil), config.APIv3Key...),
		base: &base, client: config.HTTPClient, now: config.Clock, replayWindow: config.ReplayWindow,
		responseLimit: config.ResponseLimit, gracePeriod: config.GracePeriod,
		expiryWarning: config.ExpiryWarning, entries: entries,
	}, nil
}

// Refresh downloads the current WeChat Pay platform certificates. The
// response is authenticated by the existing trust set before any encrypted
// certificate is parsed. The live snapshot changes only after every item has
// passed signature, AEAD, X.509, validity, serial, and key checks.
func (m *WeChatCertificateManager) Refresh(ctx context.Context) (WeChatCertificateHealth, error) {
	m.refreshMu.Lock()
	defer m.refreshMu.Unlock()
	return m.refreshLocked(ctx)
}

// refreshUnknown coalesces concurrent refresh attempts for an unknown serial.
// A waiter rechecks the trust snapshot after acquiring the refresh lock, so a
// successful refresh by another request does not cause another download.
func (m *WeChatCertificateManager) refreshUnknown(ctx context.Context, headers http.Header) error {
	serialValue, err := headerValue(headers, "Wechatpay-Serial")
	if err != nil {
		return err
	}
	serial := normalizeWeChatCertificateSerial(serialValue)
	if serial == "" {
		return ErrMalformedPayload
	}
	m.refreshMu.Lock()
	defer m.refreshMu.Unlock()
	if m.hasUsable(serial, m.now().UTC()) {
		return nil
	}
	_, err = m.refreshLocked(ctx)
	return err
}

func (m *WeChatCertificateManager) refreshLocked(ctx context.Context) (WeChatCertificateHealth, error) {
	endpoint := *m.base
	endpoint.Path = strings.TrimRight(endpoint.Path, "/") + "/v3/certificates"
	endpoint.RawQuery = ""
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return m.Health(), fmt.Errorf("create wechat certificate request: %w", err)
	}
	request.Header.Set("Accept", "application/json")
	if err := m.authorize(request); err != nil {
		return m.Health(), err
	}
	response, err := m.client.Do(request)
	if err != nil {
		return m.Health(), fmt.Errorf("download wechat certificates: %w", err)
	}
	if response == nil || response.Body == nil {
		return m.Health(), ErrMalformedPayload
	}
	defer response.Body.Close()
	body, err := readBounded(response.Body, m.responseLimit)
	if err != nil {
		return m.Health(), err
	}
	if _, err := m.verify(response.Header, body); err != nil {
		if errors.Is(err, errUnknownWeChatCertificate) {
			return m.Health(), ErrInvalidSignature
		}
		return m.Health(), err
	}
	if response.StatusCode < http.StatusOK || response.StatusCode >= http.StatusMultipleChoices {
		return m.Health(), ErrProviderRejected
	}
	now := m.now().UTC()
	downloaded, err := m.decodeCertificates(body, now)
	if err != nil {
		return m.Health(), err
	}

	m.mu.Lock()
	next := make(map[string]weChatPlatformCertificate, len(downloaded)+len(m.entries))
	for serial, certificate := range downloaded {
		next[serial] = certificate
	}
	for serial, existing := range m.entries {
		if _, current := next[serial]; current || !certificateUsableAt(existing, now) {
			continue
		}
		if existing.retireAfter.IsZero() {
			existing.retireAfter = now.Add(m.gracePeriod)
			if !existing.notAfter.IsZero() && existing.notAfter.Before(existing.retireAfter) {
				existing.retireAfter = existing.notAfter
			}
		}
		if existing.retireAfter.After(now) {
			next[serial] = existing
		}
	}
	if len(next) > maxWeChatPlatformCertificates {
		m.mu.Unlock()
		return m.Health(), ErrMalformedPayload
	}
	m.entries = next
	m.updatedAt = now
	m.mu.Unlock()
	return m.Health(), nil
}

// Run refreshes immediately and then periodically. Errors leave the last
// authenticated snapshot in place; callers obtain health through Health.
func (m *WeChatCertificateManager) Run(ctx context.Context, interval time.Duration) {
	if interval <= 0 {
		interval = defaultWeChatCertificateRefresh
	}
	_, _ = m.Refresh(ctx)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			_, _ = m.Refresh(ctx)
		}
	}
}

func (m *WeChatCertificateManager) Health() WeChatCertificateHealth {
	now := m.now().UTC()
	m.mu.RLock()
	defer m.mu.RUnlock()
	health := WeChatCertificateHealth{Status: "UNAVAILABLE", RemainingDays: -1, LastRefreshAt: m.updatedAt}
	var earliest time.Time
	knownExpiry := false
	for _, entry := range m.entries {
		if !certificateUsableAt(entry, now) {
			continue
		}
		if entry.retireAfter.IsZero() {
			health.ActiveCertificates++
		} else {
			health.RetiringCertificates++
		}
		if entry.retireAfter.IsZero() && !entry.notAfter.IsZero() && (earliest.IsZero() || entry.notAfter.Before(earliest)) {
			earliest = entry.notAfter
			knownExpiry = true
		}
	}
	if health.ActiveCertificates == 0 {
		return health
	}
	health.Status = "UNKNOWN"
	if knownExpiry {
		remaining := earliest.Sub(now)
		health.EarliestExpiryAt = earliest
		health.RemainingDays = int(remaining / (24 * time.Hour))
		if remaining <= m.expiryWarning {
			health.Status = "EXPIRING"
		} else {
			health.Status = "HEALTHY"
		}
	}
	return health
}

func (m *WeChatCertificateManager) verify(headers http.Header, body []byte) (time.Time, error) {
	timestampValue, err := headerValue(headers, "Wechatpay-Timestamp")
	if err != nil {
		return time.Time{}, err
	}
	nonce, err := headerValue(headers, "Wechatpay-Nonce")
	if err != nil {
		return time.Time{}, err
	}
	signature, err := headerValue(headers, "Wechatpay-Signature")
	if err != nil {
		return time.Time{}, err
	}
	serial, err := headerValue(headers, "Wechatpay-Serial")
	if err != nil {
		return time.Time{}, err
	}
	serial = normalizeWeChatCertificateSerial(serial)
	timestamp, err := strconv.ParseInt(timestampValue, 10, 64)
	if err != nil {
		return time.Time{}, ErrMalformedPayload
	}
	messageTime := time.Unix(timestamp, 0)
	now := m.now()
	if err := checkReplay(now, messageTime, m.replayWindow); err != nil {
		return time.Time{}, err
	}
	m.mu.RLock()
	entry, exists := m.entries[serial]
	usable := exists && certificateUsableAt(entry, now.UTC())
	m.mu.RUnlock()
	if !usable {
		return time.Time{}, errUnknownWeChatCertificate
	}
	message := timestampValue + "\n" + nonce + "\n" + string(body) + "\n"
	if err := rsaVerify(entry.publicKey, []byte(message), signature); err != nil {
		return time.Time{}, err
	}
	return messageTime, nil
}

func (m *WeChatCertificateManager) hasUsable(serial string, now time.Time) bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	entry, exists := m.entries[serial]
	return exists && certificateUsableAt(entry, now)
}

func (m *WeChatCertificateManager) authorize(request *http.Request) error {
	timestamp := strconv.FormatInt(m.now().Unix(), 10)
	nonceBytes := make([]byte, 16)
	if _, err := io.ReadFull(rand.Reader, nonceBytes); err != nil {
		return fmt.Errorf("generate wechat certificate nonce: %w", err)
	}
	nonce := hex.EncodeToString(nonceBytes)
	message := request.Method + "\n" + request.URL.RequestURI() + "\n" + timestamp + "\n" + nonce + "\n\n"
	signature, err := rsaSign(m.merchantPrivateKey, []byte(message))
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", fmt.Sprintf(`WECHATPAY2-SHA256-RSA2048 mchid="%s",nonce_str="%s",signature="%s",timestamp="%s",serial_no="%s"`, m.mchID, nonce, signature, timestamp, m.merchantSerial))
	return nil
}

func (m *WeChatCertificateManager) decodeCertificates(body []byte, now time.Time) (map[string]weChatPlatformCertificate, error) {
	var response struct {
		Data []struct {
			SerialNo      string `json:"serial_no"`
			EffectiveTime string `json:"effective_time"`
			ExpireTime    string `json:"expire_time"`
			Encrypt       struct {
				Algorithm      string `json:"algorithm"`
				Nonce          string `json:"nonce"`
				AssociatedData string `json:"associated_data"`
				Ciphertext     string `json:"ciphertext"`
			} `json:"encrypt_certificate"`
		} `json:"data"`
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&response); err != nil || len(response.Data) == 0 || len(response.Data) > maxWeChatPlatformCertificates {
		return nil, ErrMalformedPayload
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return nil, ErrMalformedPayload
	}
	result := make(map[string]weChatPlatformCertificate, len(response.Data))
	for _, item := range response.Data {
		serial := normalizeWeChatCertificateSerial(item.SerialNo)
		effective, effectiveErr := time.Parse(time.RFC3339, item.EffectiveTime)
		expires, expiresErr := time.Parse(time.RFC3339, item.ExpireTime)
		if serial == "" || effectiveErr != nil || expiresErr != nil || effective.After(now) || !expires.After(now) ||
			item.Encrypt.Algorithm != "AEAD_AES_256_GCM" {
			return nil, ErrMalformedPayload
		}
		plaintext, err := decryptWeChatResource(m.apiV3Key, item.Encrypt.Nonce, item.Encrypt.AssociatedData, item.Encrypt.Ciphertext)
		if err != nil {
			return nil, err
		}
		certificate, err := parseWeChatPlatformCertificate(plaintext)
		clear(plaintext)
		if err != nil || normalizeWeChatCertificateSerial(certificate.SerialNumber.Text(16)) != serial ||
			certificate.NotBefore.After(now) || !certificate.NotAfter.After(now) ||
			!certificate.NotBefore.Equal(effective) || !certificate.NotAfter.Equal(expires) {
			return nil, ErrMalformedPayload
		}
		publicKey, ok := certificate.PublicKey.(*rsa.PublicKey)
		if !ok || !validRSAPublicKey(publicKey) || certificate.KeyUsage != 0 && certificate.KeyUsage&x509.KeyUsageDigitalSignature == 0 {
			return nil, ErrMalformedPayload
		}
		if _, duplicate := result[serial]; duplicate {
			return nil, ErrMalformedPayload
		}
		result[serial] = weChatPlatformCertificate{publicKey: publicKey, notBefore: certificate.NotBefore, notAfter: certificate.NotAfter}
	}
	return result, nil
}

func parseWeChatPlatformCertificate(data []byte) (*x509.Certificate, error) {
	block, rest := pem.Decode(data)
	if block == nil || block.Type != "CERTIFICATE" || len(bytes.TrimSpace(rest)) != 0 {
		return nil, ErrMalformedPayload
	}
	certificate, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, ErrMalformedPayload
	}
	return certificate, nil
}

func certificateUsableAt(entry weChatPlatformCertificate, now time.Time) bool {
	if entry.publicKey == nil || !entry.notBefore.IsZero() && now.Before(entry.notBefore) ||
		!entry.notAfter.IsZero() && !now.Before(entry.notAfter) ||
		!entry.retireAfter.IsZero() && !now.Before(entry.retireAfter) {
		return false
	}
	return true
}

func normalizeWeChatCertificateSerial(serial string) string {
	return strings.ToUpper(strings.TrimSpace(serial))
}

package payment

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"math/big"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type weChatTestCertificate struct {
	key         *rsa.PrivateKey
	certificate *x509.Certificate
	pem         []byte
	serial      string
}

type synchronizedClock struct {
	mu  sync.RWMutex
	now time.Time
}

func (c *synchronizedClock) Now() time.Time {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.now
}

func (c *synchronizedClock) Set(now time.Time) {
	c.mu.Lock()
	c.now = now
	c.mu.Unlock()
}

func newWeChatTestCertificate(t *testing.T, serial int64, notBefore, notAfter time.Time) weChatTestCertificate {
	t.Helper()
	key := generateRSAKey(t)
	template := &x509.Certificate{
		SerialNumber:          big.NewInt(serial),
		Subject:               pkix.Name{CommonName: "WeChat Pay Platform Test"},
		NotBefore:             notBefore,
		NotAfter:              notAfter,
		KeyUsage:              x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	return weChatTestCertificate{
		key:         key,
		certificate: certificate,
		pem:         pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		serial:      normalizeWeChatCertificateSerial(certificate.SerialNumber.Text(16)),
	}
}

func weChatCertificateResponseBody(t *testing.T, apiKey []byte, certificates ...weChatTestCertificate) []byte {
	t.Helper()
	data := make([]map[string]any, 0, len(certificates))
	for index, certificate := range certificates {
		nonce := "certnonce" + string(rune('a'+index)) + "00"
		aad := "certificate"
		data = append(data, map[string]any{
			"serial_no":      certificate.serial,
			"effective_time": certificate.certificate.NotBefore.Format(time.RFC3339),
			"expire_time":    certificate.certificate.NotAfter.Format(time.RFC3339),
			"encrypt_certificate": map[string]string{
				"algorithm":       "AEAD_AES_256_GCM",
				"nonce":           nonce,
				"associated_data": aad,
				"ciphertext":      encryptWeChatTestResource(t, apiKey, nonce, aad, certificate.pem),
			},
		})
	}
	body, err := json.Marshal(map[string]any{"data": data})
	if err != nil {
		t.Fatal(err)
	}
	return body
}

func signedWeChatHeadersForSerial(t *testing.T, key *rsa.PrivateKey, serial string, timestamp time.Time, nonce string, body []byte) http.Header {
	t.Helper()
	headers := signedWeChatHeaders(t, key, timestamp, nonce, body)
	headers.Set("Wechatpay-Serial", serial)
	return headers
}

func newCertificateTestGateway(t *testing.T, clock *synchronizedClock, initial weChatTestCertificate, client HTTPClient, grace time.Duration) (*WeChatGateway, []byte) {
	t.Helper()
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	gateway, err := NewWeChat(WeChatConfig{
		AppID: "wx-app-1", MchID: "merchant-1", MerchantSerial: "merchant-serial", MerchantPrivateKey: generateRSAKey(t),
		APIv3Key: apiKey, PlatformPublicKeys: map[string]*rsa.PublicKey{initial.serial: &initial.key.PublicKey},
		NotifyURL: "https://api.example/pay/wechat/notify", BaseURL: "https://wechat.example", HTTPClient: client,
		Clock: clock.Now, ReplayWindow: 5 * time.Minute, CertificateGracePeriod: grace,
	})
	if err != nil {
		t.Fatal(err)
	}
	return gateway, apiKey
}

func certificateHTTPResponse(body []byte, headers http.Header) *http.Response {
	return &http.Response{StatusCode: http.StatusOK, Header: headers, Body: io.NopCloser(strings.NewReader(string(body)))}
}

func TestWeChatPlatformCertificateRotationAndHealth(t *testing.T) {
	now := time.Date(2026, 8, 27, 3, 0, 0, 0, time.UTC)
	clock := &synchronizedClock{now: now}
	oldCertificate := newWeChatTestCertificate(t, 0x1001, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	newCertificate := newWeChatTestCertificate(t, 0x1002, now.Add(-time.Hour), now.Add(45*24*time.Hour))
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	body := weChatCertificateResponseBody(t, apiKey, newCertificate)
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if request.Method != http.MethodGet || request.URL.Path != "/v3/certificates" || request.Header.Get("Authorization") == "" {
			t.Errorf("unexpected certificate request: %s %s", request.Method, request.URL.String())
		}
		headers := signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, clock.Now(), "rotation-response", body)
		return certificateHTTPResponse(body, headers), nil
	})
	gateway, _ := newCertificateTestGateway(t, clock, oldCertificate, client, 2*time.Hour)

	health, err := gateway.RefreshPlatformCertificates(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if health.Status != "HEALTHY" || health.ActiveCertificates != 1 || health.RetiringCertificates != 1 ||
		health.RemainingDays != 45 || !health.EarliestExpiryAt.Equal(newCertificate.certificate.NotAfter) {
		t.Fatalf("health = %#v", health)
	}
	payload := []byte(`{"rotation":"accepted"}`)
	headers := signedWeChatHeadersForSerial(t, newCertificate.key, newCertificate.serial, now, "new-certificate", payload)
	if _, err := gateway.verifySignature(context.Background(), headers, payload, false); err != nil {
		t.Fatalf("new platform certificate was not trusted: %v", err)
	}
	encoded, err := json.Marshal(health)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(encoded), oldCertificate.serial) || strings.Contains(string(encoded), newCertificate.serial) {
		t.Fatalf("health leaked certificate serial: %s", encoded)
	}
}

func TestWeChatRetiredCertificateGraceDoesNotExceedExpiry(t *testing.T) {
	now := time.Date(2026, 8, 27, 3, 30, 0, 0, time.UTC)
	clock := &synchronizedClock{now: now}
	oldCertificate := newWeChatTestCertificate(t, 0x2001, now.Add(-time.Hour), now.Add(30*time.Minute))
	newCertificate := newWeChatTestCertificate(t, 0x2002, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	firstBody := weChatCertificateResponseBody(t, apiKey, oldCertificate, newCertificate)
	secondBody := weChatCertificateResponseBody(t, apiKey, newCertificate)
	var requests atomic.Int32
	client := roundTripFunc(func(*http.Request) (*http.Response, error) {
		if requests.Add(1) == 1 {
			headers := signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, clock.Now(), "first-response", firstBody)
			return certificateHTTPResponse(firstBody, headers), nil
		}
		headers := signedWeChatHeadersForSerial(t, newCertificate.key, newCertificate.serial, clock.Now(), "second-response", secondBody)
		return certificateHTTPResponse(secondBody, headers), nil
	})
	gateway, _ := newCertificateTestGateway(t, clock, oldCertificate, client, 2*time.Hour)
	if _, err := gateway.RefreshPlatformCertificates(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := gateway.RefreshPlatformCertificates(context.Background()); err != nil {
		t.Fatal(err)
	}
	gateway.certificates.mu.RLock()
	retired := gateway.certificates.entries[oldCertificate.serial]
	gateway.certificates.mu.RUnlock()
	if !retired.retireAfter.Equal(oldCertificate.certificate.NotAfter) {
		t.Fatalf("retireAfter = %s, want certificate expiry %s", retired.retireAfter, oldCertificate.certificate.NotAfter)
	}
	payload := []byte(`{"callback":"old-certificate"}`)
	headers := signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, clock.Now(), "old-within-grace", payload)
	if _, err := gateway.verifySignature(context.Background(), headers, payload, false); err != nil {
		t.Fatalf("old certificate was rejected during grace: %v", err)
	}
	clock.Set(now.Add(31 * time.Minute))
	headers = signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, clock.Now(), "old-after-expiry", payload)
	if _, err := gateway.verifySignature(context.Background(), headers, payload, false); !errors.Is(err, ErrInvalidSignature) {
		t.Fatalf("expired retired certificate error = %v", err)
	}
}

func TestWeChatForgedCertificateResponsePreservesTrust(t *testing.T) {
	now := time.Date(2026, 8, 27, 4, 0, 0, 0, time.UTC)
	clock := &synchronizedClock{now: now}
	oldCertificate := newWeChatTestCertificate(t, 0x3001, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	newCertificate := newWeChatTestCertificate(t, 0x3002, now.Add(-time.Hour), now.Add(60*24*time.Hour))
	attacker := generateRSAKey(t)
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	body := weChatCertificateResponseBody(t, apiKey, newCertificate)
	client := roundTripFunc(func(*http.Request) (*http.Response, error) {
		headers := signedWeChatHeadersForSerial(t, attacker, "DEADBEEF", clock.Now(), "forged-response", body)
		return certificateHTTPResponse(body, headers), nil
	})
	gateway, _ := newCertificateTestGateway(t, clock, oldCertificate, client, time.Hour)
	if _, err := gateway.RefreshPlatformCertificates(context.Background()); !errors.Is(err, ErrInvalidSignature) {
		t.Fatalf("forged response error = %v", err)
	}
	payload := []byte(`{"trust":"unchanged"}`)
	oldHeaders := signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, now, "old-remains", payload)
	if _, err := gateway.verifySignature(context.Background(), oldHeaders, payload, false); err != nil {
		t.Fatalf("original trust was lost: %v", err)
	}
	newHeaders := signedWeChatHeadersForSerial(t, newCertificate.key, newCertificate.serial, now, "new-not-installed", payload)
	if _, err := gateway.verifySignature(context.Background(), newHeaders, payload, false); !errors.Is(err, ErrInvalidSignature) {
		t.Fatalf("untrusted new certificate error = %v", err)
	}
}

func TestWeChatInvalidDownloadedCertificateIsAtomic(t *testing.T) {
	now := time.Date(2026, 8, 27, 4, 30, 0, 0, time.UTC)
	valid := newWeChatTestCertificate(t, 0x4001, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	expired := newWeChatTestCertificate(t, 0x4002, now.Add(-2*time.Hour), now.Add(-time.Hour))
	future := newWeChatTestCertificate(t, 0x4003, now.Add(time.Hour), now.Add(30*24*time.Hour))
	serialMismatch := newWeChatTestCertificate(t, 0x4004, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	apiKey := []byte("0123456789abcdef0123456789abcdef")

	validBody := weChatCertificateResponseBody(t, apiKey, serialMismatch)
	var mismatched map[string]any
	if err := json.Unmarshal(validBody, &mismatched); err != nil {
		t.Fatal(err)
	}
	mismatched["data"].([]any)[0].(map[string]any)["serial_no"] = "FFFF"
	mismatchBody, err := json.Marshal(mismatched)
	if err != nil {
		t.Fatal(err)
	}

	badCipherBody := weChatCertificateResponseBody(t, apiKey, serialMismatch)
	var badCipher map[string]any
	if err := json.Unmarshal(badCipherBody, &badCipher); err != nil {
		t.Fatal(err)
	}
	badCipher["data"].([]any)[0].(map[string]any)["encrypt_certificate"].(map[string]any)["ciphertext"] = "not-base64"
	badCipherBody, err = json.Marshal(badCipher)
	if err != nil {
		t.Fatal(err)
	}

	tests := []struct {
		name string
		body []byte
	}{
		{name: "expired", body: weChatCertificateResponseBody(t, apiKey, expired)},
		{name: "not-yet-effective", body: weChatCertificateResponseBody(t, apiKey, future)},
		{name: "serial-mismatch", body: mismatchBody},
		{name: "bad-ciphertext", body: badCipherBody},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			clock := &synchronizedClock{now: now}
			client := roundTripFunc(func(*http.Request) (*http.Response, error) {
				headers := signedWeChatHeadersForSerial(t, valid.key, valid.serial, clock.Now(), "invalid-response", test.body)
				return certificateHTTPResponse(test.body, headers), nil
			})
			gateway, _ := newCertificateTestGateway(t, clock, valid, client, time.Hour)
			if _, err := gateway.RefreshPlatformCertificates(context.Background()); err == nil {
				t.Fatal("invalid certificate download was accepted")
			}
			payload := []byte(`{"atomic":true}`)
			headers := signedWeChatHeadersForSerial(t, valid.key, valid.serial, now, "original-trust", payload)
			if _, err := gateway.verifySignature(context.Background(), headers, payload, false); err != nil {
				t.Fatalf("original snapshot changed after failure: %v", err)
			}
		})
	}
}

func TestWeChatUnknownSerialConcurrentRefreshIsCoalesced(t *testing.T) {
	now := time.Date(2026, 8, 27, 5, 0, 0, 0, time.UTC)
	clock := &synchronizedClock{now: now}
	oldCertificate := newWeChatTestCertificate(t, 0x5001, now.Add(-time.Hour), now.Add(30*24*time.Hour))
	newCertificate := newWeChatTestCertificate(t, 0x5002, now.Add(-time.Hour), now.Add(60*24*time.Hour))
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	body := weChatCertificateResponseBody(t, apiKey, newCertificate)
	var downloads atomic.Int32
	client := roundTripFunc(func(*http.Request) (*http.Response, error) {
		downloads.Add(1)
		headers := signedWeChatHeadersForSerial(t, oldCertificate.key, oldCertificate.serial, clock.Now(), "coalesced-response", body)
		return certificateHTTPResponse(body, headers), nil
	})
	gateway, _ := newCertificateTestGateway(t, clock, oldCertificate, client, time.Hour)
	payload := []byte(`{"serial":"new"}`)
	headers := signedWeChatHeadersForSerial(t, newCertificate.key, newCertificate.serial, now, "unknown-serial", payload)

	const goroutines = 32
	start := make(chan struct{})
	errorsFound := make(chan error, goroutines)
	var group sync.WaitGroup
	for range goroutines {
		group.Add(1)
		go func() {
			defer group.Done()
			<-start
			_, err := gateway.verifySignature(context.Background(), headers, payload, true)
			errorsFound <- err
		}()
	}
	close(start)
	group.Wait()
	close(errorsFound)
	for err := range errorsFound {
		if err != nil {
			t.Fatalf("concurrent verification failed: %v", err)
		}
	}
	if got := downloads.Load(); got != 1 {
		t.Fatalf("certificate downloads = %d, want 1", got)
	}
}

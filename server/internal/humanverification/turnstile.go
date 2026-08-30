// Package humanverification verifies bounded, server-side anti-automation
// challenges without exposing provider secrets to browsers or application
// clients.
package humanverification

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"time"
)

const (
	ProviderTurnstile     = "turnstile"
	RegistrationAction    = "registration"
	TurnstileScriptOrigin = "https://challenges.cloudflare.com"
	turnstileVerifyURL    = TurnstileScriptOrigin + "/turnstile/v0/siteverify"
	maximumResponseBytes  = 64 << 10
	maximumTokenLength    = 2048
	defaultTimeout        = 8 * time.Second
)

var (
	ErrRequired    = errors.New("human verification is required")
	ErrRejected    = errors.New("human verification was rejected")
	ErrUnavailable = errors.New("human verification is unavailable")
)

type TurnstileConfig struct {
	SiteKey          string
	Secret           string
	ExpectedHostname string
	VerifyURL        string
	HTTPClient       *http.Client
	Timeout          time.Duration
}

// Turnstile is a fail-closed verifier for Cloudflare's Siteverify endpoint.
type Turnstile struct {
	siteKey          string
	secret           string
	expectedHostname string
	verifyURL        string
	http             *http.Client
	timeout          time.Duration
}

func NewTurnstile(config TurnstileConfig) (*Turnstile, error) {
	siteKey := strings.TrimSpace(config.SiteKey)
	secret := strings.TrimSpace(config.Secret)
	hostname := strings.ToLower(strings.TrimSpace(config.ExpectedHostname))
	if siteKey == "" || len(siteKey) > 256 || containsControlOrSpace(siteKey) ||
		secret == "" || len(secret) > 512 || strings.ContainsAny(secret, "\r\n") ||
		hostname == "" || len(hostname) > 253 || strings.ContainsAny(hostname, " /\\\t\r\n:") {
		return nil, errors.New("Turnstile configuration is invalid")
	}
	timeout := config.Timeout
	if timeout == 0 {
		timeout = defaultTimeout
	}
	if timeout < 100*time.Millisecond || timeout > 30*time.Second {
		return nil, errors.New("Turnstile timeout is invalid")
	}
	verifyURL := strings.TrimSpace(config.VerifyURL)
	if verifyURL == "" {
		verifyURL = turnstileVerifyURL
	}
	parsed, err := url.Parse(verifyURL)
	if err != nil || parsed.Scheme != "https" || parsed.Host == "" || parsed.User != nil || parsed.RawQuery != "" || parsed.Fragment != "" {
		return nil, errors.New("Turnstile verification URL is invalid")
	}
	client := config.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: timeout}
	}
	isolated := *client
	isolated.CheckRedirect = func(_ *http.Request, _ []*http.Request) error { return http.ErrUseLastResponse }
	return &Turnstile{
		siteKey: siteKey, secret: secret, expectedHostname: hostname,
		verifyURL: parsed.String(), http: &isolated, timeout: timeout,
	}, nil
}

func (t *Turnstile) SiteKey() string {
	if t == nil {
		return ""
	}
	return t.siteKey
}

func (t *Turnstile) Verify(ctx context.Context, token, remoteIP string) error {
	if t == nil {
		return ErrUnavailable
	}
	token = strings.TrimSpace(token)
	if token == "" {
		return ErrRequired
	}
	if len(token) > maximumTokenLength || strings.ContainsAny(token, "\r\n") {
		return ErrRejected
	}
	form := url.Values{"secret": {t.secret}, "response": {token}}
	if address, err := netip.ParseAddr(strings.TrimSpace(remoteIP)); err == nil {
		form.Set("remoteip", address.Unmap().String())
	}
	requestCtx, cancel := context.WithTimeout(ctx, t.timeout)
	defer cancel()
	request, err := http.NewRequestWithContext(requestCtx, http.MethodPost, t.verifyURL, strings.NewReader(form.Encode()))
	if err != nil {
		return ErrUnavailable
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "application/json")
	response, err := t.http.Do(request)
	if err != nil {
		return ErrUnavailable
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return ErrUnavailable
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, maximumResponseBytes+1))
	if err != nil || len(body) > maximumResponseBytes {
		clear(body)
		return ErrUnavailable
	}
	defer clear(body)
	var result struct {
		Success  bool     `json:"success"`
		Hostname string   `json:"hostname"`
		Action   string   `json:"action"`
		Errors   []string `json:"error-codes"`
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	if err := decoder.Decode(&result); err != nil {
		return ErrUnavailable
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return ErrUnavailable
	}
	if !result.Success || !strings.EqualFold(strings.TrimSpace(result.Hostname), t.expectedHostname) ||
		strings.TrimSpace(result.Action) != RegistrationAction {
		return ErrRejected
	}
	return nil
}

func containsControlOrSpace(value string) bool {
	return strings.IndexFunc(value, func(r rune) bool { return r <= ' ' || r == 0x7f }) >= 0
}

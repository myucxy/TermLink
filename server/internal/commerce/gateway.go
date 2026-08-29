package commerce

import (
	"bytes"
	"context"
	"crypto/rsa"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	maxPublicConfigBytes = 64 << 10
	maxSecretRefsBytes   = 16 << 10
)

// GatewayResolver returns a validated, ready-to-use provider gateway for a
// specific provider and payment scene.
type GatewayResolver interface {
	Resolve(ctx context.Context, provider, scene string) (payment.Gateway, error)
}

// GatewayValidator performs local configuration, key, and certificate
// validation. It must not call a provider or create a payment.
type GatewayValidator interface {
	ValidateVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error
}

type GatewayProber interface {
	ProbeVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error
}

type cachedCertificateHealthSource interface {
	CachedPaymentCertificateHealth(provider, scene string) (payment.WeChatCertificateHealth, bool)
}

type GatewayResolverConfig struct {
	Repository  ChannelRepository
	SecretRoot  string
	SecretLimit int64
	HTTPClient  payment.HTTPClient
	Clock       payment.Clock
}

type ChannelGatewayResolver struct {
	repository ChannelRepository
	secrets    *SecretLoader
	client     payment.HTTPClient
	clock      payment.Clock
	cacheMu    sync.RWMutex
	cache      map[string]cachedGateway
}

type cachedGateway struct {
	versionID uuid.UUID
	gateway   payment.Gateway
}

// CachedPaymentCertificateHealth is a read-only, no-I/O snapshot used by the
// admin console. It never resolves a new gateway or reads a Secret, so opening
// the page cannot become a Secret-existence timing oracle.
func (r *ChannelGatewayResolver) CachedPaymentCertificateHealth(provider, scene string) (payment.WeChatCertificateHealth, bool) {
	cacheKey := strings.ToUpper(strings.TrimSpace(provider)) + "\x00" + strings.ToUpper(strings.TrimSpace(scene))
	r.cacheMu.RLock()
	cached, ok := r.cache[cacheKey]
	r.cacheMu.RUnlock()
	if !ok {
		return payment.WeChatCertificateHealth{}, false
	}
	weChat, ok := cached.gateway.(*payment.WeChatGateway)
	if !ok {
		return payment.WeChatCertificateHealth{}, false
	}
	return weChat.PlatformCertificateHealth(), true
}

func NewChannelGatewayResolver(config GatewayResolverConfig) (*ChannelGatewayResolver, error) {
	if config.Repository == nil {
		return nil, fmt.Errorf("%w: repository is required", ErrInvalidChannelConfig)
	}
	secrets, err := NewSecretLoader(config.SecretRoot, config.SecretLimit)
	if err != nil {
		return nil, err
	}
	if config.Clock == nil {
		config.Clock = time.Now
	}
	return &ChannelGatewayResolver{repository: config.Repository, secrets: secrets, client: config.HTTPClient, clock: config.Clock, cache: make(map[string]cachedGateway)}, nil
}

func (r *ChannelGatewayResolver) Resolve(ctx context.Context, provider, scene string) (payment.Gateway, error) {
	provider = strings.ToUpper(strings.TrimSpace(provider))
	scene = strings.ToUpper(strings.TrimSpace(scene))
	if !supportedProviderScene(provider, scene) {
		return nil, ErrPaymentChannelUnavailable
	}
	channel, version, err := r.repository.GetActivePaymentConfiguration(ctx, provider, scene)
	if err != nil {
		return nil, fmt.Errorf("%w: load active configuration", ErrPaymentChannelUnavailable)
	}
	if !validActiveConfiguration(channel, version, provider, scene) {
		return nil, ErrPaymentChannelUnavailable
	}
	cacheKey := provider + "\x00" + scene
	r.cacheMu.RLock()
	cached, ok := r.cache[cacheKey]
	r.cacheMu.RUnlock()
	if ok && cached.versionID == version.ID {
		return cached.gateway, nil
	}
	var gateway payment.Gateway
	switch provider {
	case string(payment.ProviderAlipay):
		gateway, err = r.buildAlipay(version)
	case string(payment.ProviderWeChatPay):
		gateway, err = r.buildWeChat(version)
	default:
		return nil, ErrPaymentChannelUnavailable
	}
	if err != nil {
		return nil, err
	}
	r.cacheMu.Lock()
	if current, exists := r.cache[cacheKey]; exists && current.versionID == version.ID {
		gateway = current.gateway
	} else {
		r.cache[cacheKey] = cachedGateway{versionID: version.ID, gateway: gateway}
	}
	r.cacheMu.Unlock()
	return gateway, nil
}

// ValidateVersion constructs a gateway from an unactivated draft using only
// local configuration and Docker secrets. Successful validation does not imply
// provider connectivity and never creates a charge.
func (r *ChannelGatewayResolver) ValidateVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if channel.ID == uuid.Nil || version.ID == uuid.Nil || version.ChannelID != channel.ID || version.Status != "DRAFT" ||
		!supportedProviderScene(channel.Provider, channel.Scene) || (version.Environment != "PRODUCTION" && version.Environment != "SANDBOX") {
		return ErrInvalidChannelConfig
	}
	var err error
	switch channel.Provider {
	case string(payment.ProviderAlipay):
		_, err = r.buildAlipay(version)
	case string(payment.ProviderWeChatPay):
		_, err = r.buildWeChat(version)
	default:
		err = ErrInvalidChannelConfig
	}
	if err != nil {
		return err
	}
	return ctx.Err()
}

// ProbeVersion performs one signed, non-mutating provider query with a random,
// reserved health-check order identifier. A successful probe requires either
// a valid query response or the provider's signed not-found response.
func (r *ChannelGatewayResolver) ProbeVersion(ctx context.Context, channel store.PaymentChannel, version store.PaymentChannelVersion) error {
	if err := r.ValidateVersion(ctx, channel, version); err != nil {
		return err
	}
	var gateway payment.Gateway
	var err error
	switch channel.Provider {
	case string(payment.ProviderAlipay):
		gateway, err = r.buildAlipay(version)
	case string(payment.ProviderWeChatPay):
		gateway, err = r.buildWeChat(version)
	default:
		return ErrInvalidChannelConfig
	}
	if err != nil {
		return err
	}
	return gateway.HealthCheck(ctx)
}

func validActiveConfiguration(channel store.PaymentChannel, version store.PaymentChannelVersion, provider, scene string) bool {
	return channel.Provider == provider && channel.Scene == scene && channel.Enabled && channel.HealthStatus == "HEALTHY" &&
		channel.ActiveVersionID != nil && *channel.ActiveVersionID == version.ID && version.ChannelID == channel.ID && version.Status == "VALIDATED" &&
		(version.Environment == "PRODUCTION" || version.Environment == "SANDBOX")
}

type alipayPublicConfig struct {
	AppID      string `json:"app_id"`
	SellerID   string `json:"seller_id"`
	GatewayURL string `json:"gateway_url,omitempty"`
	NotifyURL  string `json:"notify_url"`
	ReturnURL  string `json:"return_url,omitempty"`
}

type alipaySecretRefs struct {
	MerchantPrivateKey SecretRef `json:"merchant_private_key"`
	AlipayPublicKey    SecretRef `json:"alipay_public_key"`
}

func (r *ChannelGatewayResolver) buildAlipay(version store.PaymentChannelVersion) (payment.Gateway, error) {
	var public alipayPublicConfig
	var refs alipaySecretRefs
	if err := decodeStrictObject(version.PublicConfig, maxPublicConfigBytes, &public); err != nil {
		return nil, err
	}
	if err := decodeStrictObject(version.SecretRefs, maxSecretRefsBytes, &refs); err != nil {
		return nil, err
	}
	if strings.TrimSpace(public.AppID) == "" || strings.TrimSpace(public.SellerID) == "" || strings.TrimSpace(public.NotifyURL) == "" || refs.MerchantPrivateKey == "" || refs.AlipayPublicKey == "" {
		return nil, ErrInvalidChannelConfig
	}
	if version.Environment == "PRODUCTION" && public.GatewayURL != "" && public.GatewayURL != "https://openapi.alipay.com/gateway.do" {
		return nil, ErrInvalidChannelConfig
	}
	privatePEM, err := r.secrets.Read(refs.MerchantPrivateKey)
	if err != nil {
		return nil, err
	}
	privateKey, err := parseRSAPrivateKey(privatePEM)
	clear(privatePEM)
	if err != nil {
		return nil, err
	}
	publicPEM, err := r.secrets.Read(refs.AlipayPublicKey)
	if err != nil {
		return nil, err
	}
	publicKey, err := parseRSAPublicKey(publicPEM)
	clear(publicPEM)
	if err != nil {
		return nil, err
	}
	gateway, err := payment.NewAlipay(payment.AlipayConfig{
		AppID: public.AppID, SellerID: public.SellerID, GatewayURL: public.GatewayURL, NotifyURL: public.NotifyURL, ReturnURL: public.ReturnURL,
		PrivateKey: privateKey, AlipayPublicKey: publicKey, HTTPClient: r.client, Clock: r.clock,
	})
	if err != nil {
		return nil, fmt.Errorf("%w: alipay configuration", ErrInvalidChannelConfig)
	}
	return gateway, nil
}

type weChatPublicConfig struct {
	AppID          string `json:"app_id"`
	MchID          string `json:"mch_id"`
	MerchantSerial string `json:"merchant_serial"`
	NotifyURL      string `json:"notify_url"`
	BaseURL        string `json:"base_url,omitempty"`
}

type weChatSecretRefs struct {
	MerchantPrivateKey SecretRef            `json:"merchant_private_key"`
	APIv3Key           SecretRef            `json:"api_v3_key"`
	PlatformPublicKeys map[string]SecretRef `json:"platform_public_keys"`
}

func (r *ChannelGatewayResolver) buildWeChat(version store.PaymentChannelVersion) (payment.Gateway, error) {
	var public weChatPublicConfig
	var refs weChatSecretRefs
	if err := decodeStrictObject(version.PublicConfig, maxPublicConfigBytes, &public); err != nil {
		return nil, err
	}
	if err := decodeStrictObject(version.SecretRefs, maxSecretRefsBytes, &refs); err != nil {
		return nil, err
	}
	if strings.TrimSpace(public.AppID) == "" || strings.TrimSpace(public.MchID) == "" || strings.TrimSpace(public.MerchantSerial) == "" || strings.TrimSpace(public.NotifyURL) == "" || refs.MerchantPrivateKey == "" || refs.APIv3Key == "" || len(refs.PlatformPublicKeys) == 0 || len(refs.PlatformPublicKeys) > 16 {
		return nil, ErrInvalidChannelConfig
	}
	if version.Environment == "PRODUCTION" && public.BaseURL != "" && public.BaseURL != "https://api.mch.weixin.qq.com" {
		return nil, ErrInvalidChannelConfig
	}
	privatePEM, err := r.secrets.Read(refs.MerchantPrivateKey)
	if err != nil {
		return nil, err
	}
	privateKey, err := parseRSAPrivateKey(privatePEM)
	clear(privatePEM)
	if err != nil {
		return nil, err
	}
	apiKey, err := r.secrets.Read(refs.APIv3Key)
	if err != nil {
		return nil, err
	}
	apiKey = bytes.TrimRight(apiKey, "\r\n")
	if len(apiKey) != 32 {
		clear(apiKey)
		return nil, ErrInvalidSecret
	}
	platformKeys := make(map[string]*rsa.PublicKey, len(refs.PlatformPublicKeys))
	for serial, ref := range refs.PlatformPublicKeys {
		serial = strings.TrimSpace(serial)
		if serial == "" || len(serial) > 128 || ref == "" {
			clear(apiKey)
			return nil, ErrInvalidChannelConfig
		}
		keyPEM, readErr := r.secrets.Read(ref)
		if readErr != nil {
			clear(apiKey)
			return nil, readErr
		}
		key, parseErr := parseRSAPublicKey(keyPEM)
		clear(keyPEM)
		if parseErr != nil {
			clear(apiKey)
			return nil, parseErr
		}
		platformKeys[serial] = key
	}
	gateway, err := payment.NewWeChat(payment.WeChatConfig{
		AppID: public.AppID, MchID: public.MchID, MerchantSerial: public.MerchantSerial, MerchantPrivateKey: privateKey,
		APIv3Key: apiKey, PlatformPublicKeys: platformKeys, NotifyURL: public.NotifyURL, BaseURL: public.BaseURL, HTTPClient: r.client, Clock: r.clock,
	})
	clear(apiKey)
	if err != nil {
		return nil, fmt.Errorf("%w: wechat configuration", ErrInvalidChannelConfig)
	}
	return gateway, nil
}

func decodeStrictObject(data json.RawMessage, limit int, target any) error {
	trimmed := bytes.TrimSpace(data)
	if len(trimmed) == 0 || len(data) > limit || !json.Valid(trimmed) || trimmed[0] != '{' {
		return ErrInvalidChannelConfig
	}
	if err := rejectDuplicateJSONKeys(trimmed); err != nil {
		return ErrInvalidChannelConfig
	}
	decoder := json.NewDecoder(bytes.NewReader(trimmed))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return ErrInvalidChannelConfig
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		return ErrInvalidChannelConfig
	}
	return nil
}

func rejectDuplicateJSONKeys(data []byte) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := consumeJSONValue(decoder); err != nil {
		return err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return ErrInvalidChannelConfig
	}
	return nil
}

func consumeJSONValue(decoder *json.Decoder) error {
	token, err := decoder.Token()
	if err != nil {
		return err
	}
	delimiter, ok := token.(json.Delim)
	if !ok {
		return nil
	}
	switch delimiter {
	case '{':
		seen := make(map[string]struct{})
		for decoder.More() {
			keyToken, err := decoder.Token()
			if err != nil {
				return err
			}
			key, ok := keyToken.(string)
			if !ok {
				return ErrInvalidChannelConfig
			}
			if _, exists := seen[key]; exists {
				return ErrInvalidChannelConfig
			}
			seen[key] = struct{}{}
			if err := consumeJSONValue(decoder); err != nil {
				return err
			}
		}
		closing, err := decoder.Token()
		if err != nil || closing != json.Delim('}') {
			return ErrInvalidChannelConfig
		}
		return nil
	case '[':
		for decoder.More() {
			if err := consumeJSONValue(decoder); err != nil {
				return err
			}
		}
		closing, err := decoder.Token()
		if err != nil || closing != json.Delim(']') {
			return ErrInvalidChannelConfig
		}
		return nil
	default:
		return ErrInvalidChannelConfig
	}
}

func supportedProvider(provider string) bool {
	return provider == string(payment.ProviderAlipay) || provider == string(payment.ProviderWeChatPay)
}

func supportedProviderScene(provider, scene string) bool {
	switch provider {
	case string(payment.ProviderAlipay):
		return scene == string(payment.FlowNative) || scene == string(payment.FlowWeb)
	case string(payment.ProviderWeChatPay):
		return scene == string(payment.FlowNative)
	default:
		return false
	}
}

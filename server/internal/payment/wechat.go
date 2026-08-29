package payment

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const defaultWeChatBaseURL = "https://api.mch.weixin.qq.com"

type WeChatConfig struct {
	AppID                    string
	MchID                    string
	MerchantSerial           string
	MerchantPrivateKey       *rsa.PrivateKey
	APIv3Key                 []byte
	PlatformPublicKeys       map[string]*rsa.PublicKey
	NotifyURL                string
	BaseURL                  string
	HTTPClient               HTTPClient
	Clock                    Clock
	ResponseLimit            int64
	WebhookLimit             int
	ReplayWindow             time.Duration
	CertificateGracePeriod   time.Duration
	CertificateExpiryWarning time.Duration
}

type WeChatGateway struct {
	config       WeChatConfig
	client       HTTPClient
	now          Clock
	base         *url.URL
	certificates *WeChatCertificateManager
}

func NewWeChat(config WeChatConfig) (*WeChatGateway, error) {
	config.AppID = strings.TrimSpace(config.AppID)
	config.MchID = strings.TrimSpace(config.MchID)
	config.MerchantSerial = strings.TrimSpace(config.MerchantSerial)
	config.NotifyURL = strings.TrimSpace(config.NotifyURL)
	if config.BaseURL == "" {
		config.BaseURL = defaultWeChatBaseURL
	}
	base, err := url.Parse(config.BaseURL)
	if err != nil || base.Scheme != "https" || base.Host == "" || base.User != nil || base.RawQuery != "" || base.Fragment != "" || config.AppID == "" || config.MchID == "" || config.MerchantSerial == "" || !validHTTPSURL(config.NotifyURL) || !validRSAPrivateKey(config.MerchantPrivateKey) || len(config.APIv3Key) != 32 || len(config.PlatformPublicKeys) == 0 {
		return nil, ErrInvalidConfig
	}
	if !safeHeaderValue(config.MchID) || !safeHeaderValue(config.MerchantSerial) {
		return nil, ErrInvalidConfig
	}
	keys := make(map[string]*rsa.PublicKey, len(config.PlatformPublicKeys))
	for serial, key := range config.PlatformPublicKeys {
		serial = strings.TrimSpace(serial)
		if serial == "" || !safeHeaderValue(serial) || !validRSAPublicKey(key) {
			return nil, ErrInvalidConfig
		}
		keys[serial] = key
	}
	config.PlatformPublicKeys = keys
	config.APIv3Key = append([]byte(nil), config.APIv3Key...)
	client := config.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	now := config.Clock
	if now == nil {
		now = time.Now
	}
	if config.ResponseLimit <= 0 {
		config.ResponseLimit = defaultResponseLimit
	}
	if config.WebhookLimit <= 0 {
		config.WebhookLimit = defaultWebhookLimit
	}
	if config.ReplayWindow <= 0 {
		config.ReplayWindow = defaultReplayWindow
	}
	certificates, err := newWeChatCertificateManager(weChatCertificateManagerConfig{
		MchID: config.MchID, MerchantSerial: config.MerchantSerial, MerchantPrivateKey: config.MerchantPrivateKey,
		APIv3Key: config.APIv3Key, InitialKeys: config.PlatformPublicKeys, BaseURL: base,
		HTTPClient: client, Clock: now, ReplayWindow: config.ReplayWindow, ResponseLimit: config.ResponseLimit,
		GracePeriod: config.CertificateGracePeriod, ExpiryWarning: config.CertificateExpiryWarning,
	})
	if err != nil {
		return nil, err
	}
	return &WeChatGateway{config: config, client: client, now: now, base: base, certificates: certificates}, nil
}

func (g *WeChatGateway) RefreshPlatformCertificates(ctx context.Context) (WeChatCertificateHealth, error) {
	return g.certificates.Refresh(ctx)
}

func (g *WeChatGateway) PlatformCertificateHealth() WeChatCertificateHealth {
	return g.certificates.Health()
}

func (g *WeChatGateway) RunPlatformCertificateRefresh(ctx context.Context, interval time.Duration) {
	g.certificates.Run(ctx, interval)
}

func (*WeChatGateway) Provider() Provider { return ProviderWeChatPay }

func (g *WeChatGateway) CreatePayment(ctx context.Context, request OrderRequest) (PaymentInstruction, error) {
	request, err := validateOrder(request)
	if err != nil {
		return PaymentInstruction{}, err
	}
	if request.Flow != FlowNative {
		return PaymentInstruction{}, ErrUnsupported
	}
	payload := struct {
		AppID       string `json:"appid"`
		MchID       string `json:"mchid"`
		Description string `json:"description"`
		OutTradeNo  string `json:"out_trade_no"`
		NotifyURL   string `json:"notify_url"`
		TimeExpire  string `json:"time_expire,omitempty"`
		Amount      struct {
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}{AppID: g.config.AppID, MchID: g.config.MchID, Description: request.Subject, OutTradeNo: request.MerchantOrderID, NotifyURL: g.config.NotifyURL}
	payload.Amount.Total = request.AmountMinor
	payload.Amount.Currency = request.Currency
	if !request.ExpiresAt.IsZero() {
		if !request.ExpiresAt.After(g.now()) {
			return PaymentInstruction{}, ErrInvalidRequest
		}
		payload.TimeExpire = request.ExpiresAt.Format(time.RFC3339)
	}
	body, err := g.call(ctx, http.MethodPost, "/v3/pay/transactions/native", nil, payload)
	if err != nil {
		return PaymentInstruction{}, err
	}
	var response struct {
		CodeURL string `json:"code_url"`
	}
	if err := json.Unmarshal(body, &response); err != nil || strings.TrimSpace(response.CodeURL) == "" {
		return PaymentInstruction{}, ErrMalformedPayload
	}
	return PaymentInstruction{Provider: ProviderWeChatPay, MerchantOrderID: request.MerchantOrderID, Kind: InstructionQRCode, QRCodeURL: response.CodeURL, ExpiresAt: request.ExpiresAt}, nil
}

func (g *WeChatGateway) QueryPayment(ctx context.Context, merchantOrderID string) (QueryResult, error) {
	merchantOrderID = strings.TrimSpace(merchantOrderID)
	if !validMerchantIdentifier(merchantOrderID) {
		return QueryResult{}, ErrInvalidRequest
	}
	query := url.Values{"mchid": {g.config.MchID}}
	body, err := g.call(ctx, http.MethodGet, "/v3/pay/transactions/out-trade-no/"+url.PathEscape(merchantOrderID), query, nil)
	if err != nil {
		return QueryResult{}, err
	}
	var response struct {
		AppID         string `json:"appid"`
		MchID         string `json:"mchid"`
		OutTradeNo    string `json:"out_trade_no"`
		TransactionID string `json:"transaction_id"`
		TradeState    string `json:"trade_state"`
		SuccessTime   string `json:"success_time"`
		Amount        struct {
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}
	if err := json.Unmarshal(body, &response); err != nil || !secureEqualString(response.AppID, g.config.AppID) || !secureEqualString(response.MchID, g.config.MchID) || response.OutTradeNo != merchantOrderID || response.Amount.Total <= 0 || response.Amount.Currency != CurrencyCNY {
		return QueryResult{}, ErrMalformedPayload
	}
	result := QueryResult{Provider: ProviderWeChatPay, MerchantOrderID: response.OutTradeNo, ProviderTransactionID: response.TransactionID, Status: wechatTradeStatus(response.TradeState), AmountMinor: response.Amount.Total, Currency: response.Amount.Currency}
	if response.SuccessTime != "" {
		result.PaidAt, err = time.Parse(time.RFC3339, response.SuccessTime)
		if err != nil {
			return QueryResult{}, ErrMalformedPayload
		}
	}
	return result, nil
}

func (g *WeChatGateway) HealthCheck(ctx context.Context) error {
	merchantOrderID, err := randomHealthMerchantID()
	if err != nil {
		return err
	}
	_, err = g.QueryPayment(ctx, merchantOrderID)
	if err == nil || providerErrorHasCode(err, "ORDER_NOT_EXIST") {
		return nil
	}
	return err
}

func (g *WeChatGateway) Refund(ctx context.Context, request RefundRequest) (RefundResult, error) {
	request, err := validateRefund(request)
	if err != nil {
		return RefundResult{}, err
	}
	payload := struct {
		OutTradeNo  string `json:"out_trade_no"`
		OutRefundNo string `json:"out_refund_no"`
		Reason      string `json:"reason,omitempty"`
		Amount      struct {
			Refund   int64  `json:"refund"`
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}{OutTradeNo: request.MerchantOrderID, OutRefundNo: request.MerchantRefundID, Reason: request.Reason}
	payload.Amount.Refund = request.AmountMinor
	payload.Amount.Total = request.TotalAmountMinor
	payload.Amount.Currency = request.Currency
	body, err := g.call(ctx, http.MethodPost, "/v3/refund/domestic/refunds", nil, payload)
	if err != nil {
		return RefundResult{}, err
	}
	var response struct {
		RefundID    string `json:"refund_id"`
		OutRefundNo string `json:"out_refund_no"`
		OutTradeNo  string `json:"out_trade_no"`
		Status      string `json:"status"`
		Amount      struct {
			Refund   int64  `json:"refund"`
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}
	if err := json.Unmarshal(body, &response); err != nil || response.OutTradeNo != request.MerchantOrderID || response.OutRefundNo != request.MerchantRefundID || response.RefundID == "" || response.Amount.Refund != request.AmountMinor || response.Amount.Total != request.TotalAmountMinor || response.Amount.Currency != CurrencyCNY {
		return RefundResult{}, ErrMalformedPayload
	}
	return RefundResult{Provider: ProviderWeChatPay, MerchantOrderID: response.OutTradeNo, MerchantRefundID: response.OutRefundNo, ProviderRefundID: response.RefundID, Status: wechatRefundStatus(response.Status), AmountMinor: response.Amount.Refund, Currency: response.Amount.Currency}, nil
}

func (g *WeChatGateway) QueryRefund(ctx context.Context, request RefundRequest) (RefundResult, error) {
	request, err := validateRefund(request)
	if err != nil {
		return RefundResult{}, err
	}
	body, err := g.call(ctx, http.MethodGet, "/v3/refund/domestic/refunds/"+url.PathEscape(request.MerchantRefundID), nil, nil)
	if err != nil {
		return RefundResult{}, err
	}
	var response struct {
		RefundID    string `json:"refund_id"`
		OutRefundNo string `json:"out_refund_no"`
		OutTradeNo  string `json:"out_trade_no"`
		Status      string `json:"status"`
		Amount      struct {
			Refund   int64  `json:"refund"`
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}
	if err := json.Unmarshal(body, &response); err != nil || response.OutTradeNo != request.MerchantOrderID ||
		response.OutRefundNo != request.MerchantRefundID || response.RefundID == "" || response.Amount.Refund != request.AmountMinor ||
		response.Amount.Total != request.TotalAmountMinor || response.Amount.Currency != request.Currency {
		return RefundResult{}, ErrMalformedPayload
	}
	return RefundResult{Provider: ProviderWeChatPay, MerchantOrderID: response.OutTradeNo, MerchantRefundID: response.OutRefundNo,
		ProviderRefundID: response.RefundID, Status: wechatRefundStatus(response.Status), AmountMinor: response.Amount.Refund, Currency: response.Amount.Currency}, nil
}

func (g *WeChatGateway) VerifyWebhook(ctx context.Context, headers http.Header, body []byte) (WebhookResult, error) {
	if len(body) == 0 {
		return WebhookResult{}, ErrMalformedPayload
	}
	if len(body) > g.config.WebhookLimit {
		return WebhookResult{}, ErrPayloadTooLarge
	}
	headerTime, err := g.verifySignature(ctx, headers, body, true)
	if err != nil {
		return WebhookResult{}, err
	}
	var envelope struct {
		ID           string `json:"id"`
		CreateTime   string `json:"create_time"`
		EventType    string `json:"event_type"`
		ResourceType string `json:"resource_type"`
		Resource     struct {
			Algorithm      string `json:"algorithm"`
			Ciphertext     string `json:"ciphertext"`
			AssociatedData string `json:"associated_data"`
			Nonce          string `json:"nonce"`
		} `json:"resource"`
	}
	if err := json.Unmarshal(body, &envelope); err != nil || envelope.ID == "" || envelope.ResourceType != "encrypt-resource" || envelope.Resource.Algorithm != "AEAD_AES_256_GCM" {
		return WebhookResult{}, ErrMalformedPayload
	}
	created, err := time.Parse(time.RFC3339, envelope.CreateTime)
	if err != nil || checkReplay(g.now(), created, g.config.ReplayWindow) != nil {
		if err != nil {
			return WebhookResult{}, ErrMalformedPayload
		}
		return WebhookResult{}, ErrReplay
	}
	plaintext, err := decryptWeChatResource(g.config.APIv3Key, envelope.Resource.Nonce, envelope.Resource.AssociatedData, envelope.Resource.Ciphertext)
	if err != nil {
		return WebhookResult{}, err
	}
	if strings.HasPrefix(envelope.EventType, "TRANSACTION.") {
		return g.parseTransactionWebhook(envelope.ID, headerTime, plaintext)
	}
	if strings.HasPrefix(envelope.EventType, "REFUND.") {
		return g.parseRefundWebhook(envelope.ID, headerTime, plaintext)
	}
	return WebhookResult{}, ErrUnsupported
}

func (g *WeChatGateway) parseTransactionWebhook(eventID string, occurredAt time.Time, plaintext []byte) (WebhookResult, error) {
	var transaction struct {
		AppID         string `json:"appid"`
		MchID         string `json:"mchid"`
		OutTradeNo    string `json:"out_trade_no"`
		TransactionID string `json:"transaction_id"`
		TradeState    string `json:"trade_state"`
		SuccessTime   string `json:"success_time"`
		Amount        struct {
			Total    int64  `json:"total"`
			Currency string `json:"currency"`
		} `json:"amount"`
	}
	if err := json.Unmarshal(plaintext, &transaction); err != nil || !secureEqualString(transaction.AppID, g.config.AppID) || !secureEqualString(transaction.MchID, g.config.MchID) || transaction.OutTradeNo == "" || transaction.TransactionID == "" || transaction.Amount.Total <= 0 || transaction.Amount.Currency != CurrencyCNY {
		return WebhookResult{}, ErrMalformedPayload
	}
	if transaction.SuccessTime != "" {
		parsed, err := time.Parse(time.RFC3339, transaction.SuccessTime)
		if err != nil {
			return WebhookResult{}, ErrMalformedPayload
		}
		occurredAt = parsed
	}
	return WebhookResult{Provider: ProviderWeChatPay, EventID: eventID, EventType: WebhookPayment, MerchantOrderID: transaction.OutTradeNo, ProviderTransactionID: transaction.TransactionID, Status: wechatTradeStatus(transaction.TradeState), AmountMinor: transaction.Amount.Total, Currency: transaction.Amount.Currency, OccurredAt: occurredAt}, nil
}

func (g *WeChatGateway) parseRefundWebhook(eventID string, occurredAt time.Time, plaintext []byte) (WebhookResult, error) {
	var refund struct {
		MchID         string `json:"mchid"`
		OutTradeNo    string `json:"out_trade_no"`
		TransactionID string `json:"transaction_id"`
		OutRefundNo   string `json:"out_refund_no"`
		RefundID      string `json:"refund_id"`
		RefundStatus  string `json:"refund_status"`
		SuccessTime   string `json:"success_time"`
		Amount        struct {
			Refund int64 `json:"refund"`
		} `json:"amount"`
	}
	if err := json.Unmarshal(plaintext, &refund); err != nil || !secureEqualString(refund.MchID, g.config.MchID) || refund.OutTradeNo == "" || refund.TransactionID == "" || refund.OutRefundNo == "" || refund.RefundID == "" || refund.Amount.Refund <= 0 {
		return WebhookResult{}, ErrMalformedPayload
	}
	if refund.SuccessTime != "" {
		parsed, err := time.Parse(time.RFC3339, refund.SuccessTime)
		if err != nil {
			return WebhookResult{}, ErrMalformedPayload
		}
		occurredAt = parsed
	}
	return WebhookResult{Provider: ProviderWeChatPay, EventID: eventID, EventType: WebhookRefund, MerchantOrderID: refund.OutTradeNo, ProviderTransactionID: refund.TransactionID, MerchantRefundID: refund.OutRefundNo, ProviderRefundID: refund.RefundID, Status: wechatRefundStatus(refund.RefundStatus), AmountMinor: refund.Amount.Refund, Currency: CurrencyCNY, OccurredAt: occurredAt}, nil
}

func (g *WeChatGateway) call(ctx context.Context, method, path string, query url.Values, payload any) ([]byte, error) {
	endpoint := *g.base
	endpoint.Path = strings.TrimRight(endpoint.Path, "/") + path
	endpoint.RawQuery = query.Encode()
	var body []byte
	var err error
	if payload != nil {
		body, err = json.Marshal(payload)
		if err != nil {
			return nil, fmt.Errorf("marshal wechat request: %w", err)
		}
	}
	request, err := http.NewRequestWithContext(ctx, method, endpoint.String(), bytes.NewReader(body))
	if err != nil {
		return nil, fmt.Errorf("create wechat request: %w", err)
	}
	request.Header.Set("Accept", "application/json")
	if payload != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	if err := g.authorize(request, body); err != nil {
		return nil, err
	}
	response, err := g.client.Do(request)
	if err != nil {
		return nil, fmt.Errorf("call wechat: %w", err)
	}
	if response == nil || response.Body == nil {
		return nil, ErrMalformedPayload
	}
	defer response.Body.Close()
	responseBody, err := readBounded(response.Body, g.config.ResponseLimit)
	if err != nil {
		return nil, err
	}
	if _, err := g.verifySignature(ctx, response.Header, responseBody, true); err != nil {
		return nil, err
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		var providerError struct {
			Code string `json:"code"`
		}
		if json.Unmarshal(responseBody, &providerError) != nil {
			return nil, ErrProviderRejected
		}
		return nil, providerRejected(providerError.Code)
	}
	return responseBody, nil
}

func (g *WeChatGateway) authorize(request *http.Request, body []byte) error {
	timestamp := strconv.FormatInt(g.now().Unix(), 10)
	nonceBytes := make([]byte, 16)
	if _, err := io.ReadFull(rand.Reader, nonceBytes); err != nil {
		return fmt.Errorf("generate wechat nonce: %w", err)
	}
	nonce := hex.EncodeToString(nonceBytes)
	message := request.Method + "\n" + request.URL.RequestURI() + "\n" + timestamp + "\n" + nonce + "\n" + string(body) + "\n"
	signature, err := rsaSign(g.config.MerchantPrivateKey, []byte(message))
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", fmt.Sprintf(`WECHATPAY2-SHA256-RSA2048 mchid="%s",nonce_str="%s",signature="%s",timestamp="%s",serial_no="%s"`, g.config.MchID, nonce, signature, timestamp, g.config.MerchantSerial))
	return nil
}

func (g *WeChatGateway) verifySignature(ctx context.Context, headers http.Header, body []byte, refreshUnknown bool) (time.Time, error) {
	messageTime, err := g.certificates.verify(headers, body)
	if !errors.Is(err, errUnknownWeChatCertificate) || !refreshUnknown {
		if errors.Is(err, errUnknownWeChatCertificate) {
			return time.Time{}, ErrInvalidSignature
		}
		return messageTime, err
	}
	if refreshErr := g.certificates.refreshUnknown(ctx, headers); refreshErr != nil {
		return time.Time{}, ErrInvalidSignature
	}
	messageTime, err = g.certificates.verify(headers, body)
	if errors.Is(err, errUnknownWeChatCertificate) {
		return time.Time{}, ErrInvalidSignature
	}
	return messageTime, err
}

func decryptWeChatResource(apiKey []byte, nonce, associatedData, ciphertext string) ([]byte, error) {
	if len(apiKey) != 32 || len(nonce) != 12 || ciphertext == "" {
		return nil, ErrMalformedPayload
	}
	decoded, err := base64.StdEncoding.DecodeString(ciphertext)
	if err != nil {
		return nil, ErrMalformedPayload
	}
	block, err := aes.NewCipher(apiKey)
	if err != nil {
		return nil, ErrMalformedPayload
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, ErrMalformedPayload
	}
	plaintext, err := gcm.Open(nil, []byte(nonce), decoded, []byte(associatedData))
	if err != nil {
		return nil, ErrInvalidSignature
	}
	return plaintext, nil
}

func safeHeaderValue(value string) bool {
	return value != "" && !strings.ContainsAny(value, "\r\n\"")
}

func wechatTradeStatus(status string) PaymentStatus {
	switch strings.ToUpper(strings.TrimSpace(status)) {
	case "SUCCESS":
		return StatusSucceeded
	case "NOTPAY", "USERPAYING":
		return StatusPending
	case "CLOSED", "REVOKED", "PAYERROR":
		return StatusClosed
	case "REFUND":
		return StatusRefunded
	default:
		return StatusUnknown
	}
}

func wechatRefundStatus(status string) PaymentStatus {
	switch strings.ToUpper(strings.TrimSpace(status)) {
	case "SUCCESS":
		return StatusRefunded
	case "PROCESSING":
		return StatusRefunding
	case "CLOSED", "ABNORMAL":
		return StatusFailed
	default:
		return StatusUnknown
	}
}

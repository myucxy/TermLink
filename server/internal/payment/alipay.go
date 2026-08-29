package payment

import (
	"bytes"
	"context"
	"crypto/rsa"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"
)

const defaultAlipayGateway = "https://openapi.alipay.com/gateway.do"

type AlipayConfig struct {
	AppID           string
	SellerID        string
	GatewayURL      string
	NotifyURL       string
	ReturnURL       string
	PrivateKey      *rsa.PrivateKey
	AlipayPublicKey *rsa.PublicKey
	HTTPClient      HTTPClient
	Clock           Clock
	Location        *time.Location
	ResponseLimit   int64
	WebhookLimit    int
	ReplayWindow    time.Duration
}

type AlipayGateway struct {
	config AlipayConfig
	client HTTPClient
	now    Clock
}

func NewAlipay(config AlipayConfig) (*AlipayGateway, error) {
	config.AppID = strings.TrimSpace(config.AppID)
	config.SellerID = strings.TrimSpace(config.SellerID)
	config.NotifyURL = strings.TrimSpace(config.NotifyURL)
	config.ReturnURL = strings.TrimSpace(config.ReturnURL)
	if config.GatewayURL == "" {
		config.GatewayURL = defaultAlipayGateway
	}
	gatewayURL, err := url.Parse(config.GatewayURL)
	if err != nil || gatewayURL.Scheme != "https" || gatewayURL.Host == "" || gatewayURL.User != nil || gatewayURL.RawQuery != "" || gatewayURL.Fragment != "" || config.AppID == "" || config.SellerID == "" || !validHTTPSURL(config.NotifyURL) || (config.ReturnURL != "" && !validHTTPSURL(config.ReturnURL)) || !validRSAPrivateKey(config.PrivateKey) || !validRSAPublicKey(config.AlipayPublicKey) {
		return nil, ErrInvalidConfig
	}
	if config.Location == nil {
		config.Location = time.FixedZone("CST", 8*60*60)
	}
	client := config.HTTPClient
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	now := config.Clock
	if now == nil {
		now = time.Now
	}
	if config.WebhookLimit <= 0 {
		config.WebhookLimit = defaultWebhookLimit
	}
	if config.ResponseLimit <= 0 {
		config.ResponseLimit = defaultResponseLimit
	}
	if config.ReplayWindow <= 0 {
		config.ReplayWindow = defaultReplayWindow
	}
	return &AlipayGateway{config: config, client: client, now: now}, nil
}

func (*AlipayGateway) Provider() Provider { return ProviderAlipay }

func (g *AlipayGateway) CreatePayment(ctx context.Context, request OrderRequest) (PaymentInstruction, error) {
	request, err := validateOrder(request)
	if err != nil {
		return PaymentInstruction{}, err
	}
	if request.Flow != FlowNative && request.Flow != FlowWeb {
		return PaymentInstruction{}, ErrUnsupported
	}
	biz := map[string]any{
		"out_trade_no": request.MerchantOrderID,
		"total_amount": amountString(request.AmountMinor),
		"subject":      request.Subject,
		"product_code": "FAST_INSTANT_TRADE_PAY",
	}
	if !request.ExpiresAt.IsZero() {
		remaining := request.ExpiresAt.Sub(g.now())
		if remaining <= 0 {
			return PaymentInstruction{}, ErrInvalidRequest
		}
		minutes := int64(remaining.Round(time.Minute) / time.Minute)
		if minutes < 1 {
			minutes = 1
		}
		biz["timeout_express"] = fmt.Sprintf("%dm", minutes)
	}
	if request.Flow == FlowWeb {
		biz["product_code"] = "FAST_INSTANT_TRADE_PAY"
		values, err := g.signedValues("alipay.trade.page.pay", biz)
		if err != nil {
			return PaymentInstruction{}, err
		}
		return PaymentInstruction{
			Provider:        ProviderAlipay,
			MerchantOrderID: request.MerchantOrderID,
			Kind:            InstructionRedirect,
			RedirectURL:     g.config.GatewayURL + "?" + values.Encode(),
			ExpiresAt:       request.ExpiresAt,
		}, nil
	}
	delete(biz, "product_code")
	response, err := g.call(ctx, "alipay.trade.precreate", "alipay_trade_precreate_response", biz)
	if err != nil {
		return PaymentInstruction{}, err
	}
	var parsed struct {
		Code       string `json:"code"`
		SubCode    string `json:"sub_code"`
		OutTradeNo string `json:"out_trade_no"`
		QRCode     string `json:"qr_code"`
	}
	if err := json.Unmarshal(response, &parsed); err != nil || parsed.Code == "" {
		return PaymentInstruction{}, ErrMalformedPayload
	}
	if parsed.Code != "10000" {
		return PaymentInstruction{}, providerRejected(parsed.SubCode)
	}
	if parsed.OutTradeNo != request.MerchantOrderID || strings.TrimSpace(parsed.QRCode) == "" {
		return PaymentInstruction{}, ErrMalformedPayload
	}
	return PaymentInstruction{Provider: ProviderAlipay, MerchantOrderID: request.MerchantOrderID, Kind: InstructionQRCode, QRCodeURL: parsed.QRCode, ExpiresAt: request.ExpiresAt}, nil
}

func (g *AlipayGateway) QueryPayment(ctx context.Context, merchantOrderID string) (QueryResult, error) {
	merchantOrderID = strings.TrimSpace(merchantOrderID)
	if !validMerchantIdentifier(merchantOrderID) {
		return QueryResult{}, ErrInvalidRequest
	}
	response, err := g.call(ctx, "alipay.trade.query", "alipay_trade_query_response", map[string]any{"out_trade_no": merchantOrderID})
	if err != nil {
		return QueryResult{}, err
	}
	var parsed struct {
		Code        string `json:"code"`
		SubCode     string `json:"sub_code"`
		OutTradeNo  string `json:"out_trade_no"`
		TradeNo     string `json:"trade_no"`
		TradeStatus string `json:"trade_status"`
		TotalAmount string `json:"total_amount"`
		SendPayDate string `json:"send_pay_date"`
	}
	if err := json.Unmarshal(response, &parsed); err != nil || parsed.Code == "" {
		return QueryResult{}, ErrMalformedPayload
	}
	if parsed.Code != "10000" {
		return QueryResult{}, providerRejected(parsed.SubCode)
	}
	if parsed.OutTradeNo != merchantOrderID {
		return QueryResult{}, ErrMalformedPayload
	}
	amount, err := parseAmountMinor(parsed.TotalAmount)
	if err != nil {
		return QueryResult{}, err
	}
	result := QueryResult{Provider: ProviderAlipay, MerchantOrderID: merchantOrderID, ProviderTransactionID: parsed.TradeNo, Status: alipayStatus(parsed.TradeStatus), AmountMinor: amount, Currency: CurrencyCNY}
	if parsed.SendPayDate != "" {
		result.PaidAt, err = time.ParseInLocation("2006-01-02 15:04:05", parsed.SendPayDate, g.config.Location)
		if err != nil {
			return QueryResult{}, ErrMalformedPayload
		}
	}
	return result, nil
}

func (g *AlipayGateway) HealthCheck(ctx context.Context) error {
	merchantOrderID, err := randomHealthMerchantID()
	if err != nil {
		return err
	}
	_, err = g.QueryPayment(ctx, merchantOrderID)
	if err == nil || providerErrorHasCode(err, "ACQ.TRADE_NOT_EXIST", "TRADE_NOT_EXIST") {
		return nil
	}
	return err
}

func (g *AlipayGateway) Refund(ctx context.Context, request RefundRequest) (RefundResult, error) {
	request, err := validateRefund(request)
	if err != nil {
		return RefundResult{}, err
	}
	biz := map[string]any{
		"out_trade_no":   request.MerchantOrderID,
		"out_request_no": request.MerchantRefundID,
		"refund_amount":  amountString(request.AmountMinor),
	}
	if request.Reason != "" {
		biz["refund_reason"] = request.Reason
	}
	response, err := g.call(ctx, "alipay.trade.refund", "alipay_trade_refund_response", biz)
	if err != nil {
		return RefundResult{}, err
	}
	var parsed struct {
		Code       string `json:"code"`
		SubCode    string `json:"sub_code"`
		OutTradeNo string `json:"out_trade_no"`
		RefundFee  string `json:"refund_fee"`
		FundChange string `json:"fund_change"`
	}
	if err := json.Unmarshal(response, &parsed); err != nil || parsed.Code == "" {
		return RefundResult{}, ErrMalformedPayload
	}
	if parsed.Code != "10000" {
		return RefundResult{}, providerRejected(parsed.SubCode)
	}
	if parsed.OutTradeNo != request.MerchantOrderID {
		return RefundResult{}, ErrMalformedPayload
	}
	amount, err := parseAmountMinor(parsed.RefundFee)
	if err != nil || amount != request.AmountMinor {
		return RefundResult{}, ErrMalformedPayload
	}
	status := StatusSucceeded
	if strings.EqualFold(parsed.FundChange, "N") {
		status = StatusPending
	}
	return RefundResult{Provider: ProviderAlipay, MerchantOrderID: request.MerchantOrderID, MerchantRefundID: request.MerchantRefundID, Status: status, AmountMinor: amount, Currency: CurrencyCNY}, nil
}

func (g *AlipayGateway) QueryRefund(ctx context.Context, request RefundRequest) (RefundResult, error) {
	request, err := validateRefund(request)
	if err != nil {
		return RefundResult{}, err
	}
	response, err := g.call(ctx, "alipay.trade.fastpay.refund.query", "alipay_trade_fastpay_refund_query_response", map[string]any{
		"out_trade_no": request.MerchantOrderID, "out_request_no": request.MerchantRefundID,
	})
	if err != nil {
		return RefundResult{}, err
	}
	var parsed struct {
		Code         string `json:"code"`
		SubCode      string `json:"sub_code"`
		OutTradeNo   string `json:"out_trade_no"`
		OutRequestNo string `json:"out_request_no"`
		RefundAmount string `json:"refund_amount"`
		RefundStatus string `json:"refund_status"`
	}
	if err := json.Unmarshal(response, &parsed); err != nil || parsed.Code == "" {
		return RefundResult{}, ErrMalformedPayload
	}
	if parsed.Code != "10000" {
		return RefundResult{}, providerRejected(parsed.SubCode)
	}
	amount, err := parseAmountMinor(parsed.RefundAmount)
	if err != nil || parsed.OutTradeNo != request.MerchantOrderID || parsed.OutRequestNo != request.MerchantRefundID || amount != request.AmountMinor {
		return RefundResult{}, ErrMalformedPayload
	}
	status := StatusPending
	if parsed.RefundStatus == "REFUND_SUCCESS" {
		status = StatusSucceeded
	}
	return RefundResult{Provider: ProviderAlipay, MerchantOrderID: request.MerchantOrderID, MerchantRefundID: request.MerchantRefundID, Status: status, AmountMinor: amount, Currency: CurrencyCNY}, nil
}

func (g *AlipayGateway) VerifyWebhook(_ context.Context, _ http.Header, body []byte) (WebhookResult, error) {
	if len(body) == 0 {
		return WebhookResult{}, ErrMalformedPayload
	}
	if len(body) > g.config.WebhookLimit {
		return WebhookResult{}, ErrPayloadTooLarge
	}
	values, err := url.ParseQuery(string(body))
	if err != nil {
		return WebhookResult{}, ErrMalformedPayload
	}
	for _, entries := range values {
		if len(entries) != 1 {
			return WebhookResult{}, ErrMalformedPayload
		}
	}
	if !secureEqualString(values.Get("sign_type"), "RSA2") {
		return WebhookResult{}, ErrInvalidSignature
	}
	signature := values.Get("sign")
	if signature == "" {
		return WebhookResult{}, ErrInvalidSignature
	}
	delete(values, "sign")
	delete(values, "sign_type")
	if err := rsaVerify(g.config.AlipayPublicKey, []byte(canonicalValues(values)), signature); err != nil {
		return WebhookResult{}, err
	}
	if !secureEqualString(values.Get("app_id"), g.config.AppID) || (g.config.SellerID != "" && !secureEqualString(values.Get("seller_id"), g.config.SellerID)) {
		return WebhookResult{}, ErrMalformedPayload
	}
	notifyTime, err := time.ParseInLocation("2006-01-02 15:04:05", values.Get("notify_time"), g.config.Location)
	if err != nil || checkReplay(g.now(), notifyTime, g.config.ReplayWindow) != nil {
		if err != nil {
			return WebhookResult{}, ErrMalformedPayload
		}
		return WebhookResult{}, ErrReplay
	}
	amount, err := parseAmountMinor(values.Get("total_amount"))
	if err != nil || amount <= 0 || values.Get("out_trade_no") == "" || values.Get("trade_no") == "" || values.Get("notify_id") == "" {
		return WebhookResult{}, ErrMalformedPayload
	}
	return WebhookResult{
		Provider: ProviderAlipay, EventID: values.Get("notify_id"), EventType: WebhookPayment, MerchantOrderID: values.Get("out_trade_no"), ProviderTransactionID: values.Get("trade_no"), Status: alipayStatus(values.Get("trade_status")), AmountMinor: amount, Currency: CurrencyCNY, OccurredAt: notifyTime,
	}, nil
}

func (g *AlipayGateway) signedValues(method string, biz any) (url.Values, error) {
	bizJSON, err := json.Marshal(biz)
	if err != nil {
		return nil, fmt.Errorf("marshal alipay request: %w", err)
	}
	values := url.Values{
		"app_id":      {g.config.AppID},
		"method":      {method},
		"format":      {"JSON"},
		"charset":     {"UTF-8"},
		"sign_type":   {"RSA2"},
		"timestamp":   {g.now().In(g.config.Location).Format("2006-01-02 15:04:05")},
		"version":     {"1.0"},
		"biz_content": {string(bizJSON)},
	}
	if g.config.NotifyURL != "" {
		values.Set("notify_url", g.config.NotifyURL)
	}
	if method == "alipay.trade.page.pay" && g.config.ReturnURL != "" {
		values.Set("return_url", g.config.ReturnURL)
	}
	signature, err := rsaSign(g.config.PrivateKey, []byte(canonicalValues(values)))
	if err != nil {
		return nil, err
	}
	values.Set("sign", signature)
	return values, nil
}

func (g *AlipayGateway) call(ctx context.Context, method, responseKey string, biz any) (json.RawMessage, error) {
	values, err := g.signedValues(method, biz)
	if err != nil {
		return nil, err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, g.config.GatewayURL, strings.NewReader(values.Encode()))
	if err != nil {
		return nil, fmt.Errorf("create alipay request: %w", err)
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "application/json")
	response, err := g.client.Do(request)
	if err != nil {
		return nil, fmt.Errorf("call alipay: %w", err)
	}
	if response == nil || response.Body == nil {
		return nil, ErrMalformedPayload
	}
	defer response.Body.Close()
	body, err := readBounded(response.Body, g.config.ResponseLimit)
	if err != nil {
		return nil, err
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return nil, fmt.Errorf("%w: HTTP %d", ErrProviderRejected, response.StatusCode)
	}
	var envelope map[string]json.RawMessage
	if err := json.Unmarshal(body, &envelope); err != nil {
		return nil, ErrMalformedPayload
	}
	raw, ok := envelope[responseKey]
	if !ok || len(raw) == 0 {
		return nil, ErrMalformedPayload
	}
	var signature string
	if err := json.Unmarshal(envelope["sign"], &signature); err != nil || signature == "" {
		return nil, ErrInvalidSignature
	}
	if err := rsaVerify(g.config.AlipayPublicKey, bytes.TrimSpace(raw), signature); err != nil {
		return nil, err
	}
	return raw, nil
}

func canonicalValues(values url.Values) string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		if len(values[key]) != 0 && values[key][0] != "" {
			parts = append(parts, key+"="+values[key][0])
		}
	}
	return strings.Join(parts, "&")
}

func alipayStatus(status string) PaymentStatus {
	switch strings.ToUpper(strings.TrimSpace(status)) {
	case "TRADE_SUCCESS", "TRADE_FINISHED":
		return StatusSucceeded
	case "WAIT_BUYER_PAY":
		return StatusPending
	case "TRADE_CLOSED":
		return StatusClosed
	default:
		return StatusUnknown
	}
}

type providerError struct{ code string }

func (e providerError) Error() string { return ErrProviderRejected.Error() + ": " + e.code }
func (e providerError) Unwrap() error { return ErrProviderRejected }

func providerErrorHasCode(err error, codes ...string) bool {
	var providerErr providerError
	if !errors.As(err, &providerErr) {
		return false
	}
	for _, code := range codes {
		if providerErr.code == code {
			return true
		}
	}
	return false
}

func providerRejected(code string) error {
	code = strings.TrimSpace(code)
	if code == "" {
		return ErrProviderRejected
	}
	var sanitized strings.Builder
	for _, char := range code {
		if sanitized.Len() >= 64 {
			break
		}
		if (char >= 'a' && char <= 'z') || (char >= 'A' && char <= 'Z') || (char >= '0' && char <= '9') || char == '_' || char == '-' || char == '.' {
			sanitized.WriteRune(char)
		} else {
			sanitized.WriteByte('_')
		}
	}
	return providerError{code: sanitized.String()}
}

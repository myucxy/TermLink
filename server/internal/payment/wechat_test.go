package payment

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"testing"
	"time"
)

func newTestWeChat(t *testing.T, now time.Time, client HTTPClient) (*WeChatGateway, *rsa.PrivateKey, *rsa.PrivateKey, []byte) {
	t.Helper()
	merchantKey := generateRSAKey(t)
	platformKey := generateRSAKey(t)
	apiKey := []byte("0123456789abcdef0123456789abcdef")
	gateway, err := NewWeChat(WeChatConfig{
		AppID: "wx-app-1", MchID: "merchant-1", MerchantSerial: "merchant-serial", MerchantPrivateKey: merchantKey,
		APIv3Key: apiKey, PlatformPublicKeys: map[string]*rsa.PublicKey{"platform-serial": &platformKey.PublicKey}, NotifyURL: "https://api.example/pay/wechat/notify",
		BaseURL: "https://wechat.example", HTTPClient: client, Clock: func() time.Time { return now }, ReplayWindow: 5 * time.Minute, WebhookLimit: 4096,
	})
	if err != nil {
		t.Fatal(err)
	}
	return gateway, merchantKey, platformKey, apiKey
}

func TestWeChatRequestSigningAndSignedCreateResponse(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	var merchantKey *rsa.PrivateKey
	var platformKey *rsa.PrivateKey
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		body, err := io.ReadAll(request.Body)
		if err != nil {
			t.Fatal(err)
		}
		fields := parseWeChatAuthorization(t, request.Header.Get("Authorization"))
		message := request.Method + "\n" + request.URL.RequestURI() + "\n" + fields["timestamp"] + "\n" + fields["nonce_str"] + "\n" + string(body) + "\n"
		if fields["mchid"] != "merchant-1" || fields["serial_no"] != "merchant-serial" {
			t.Fatalf("authorization fields = %v", fields)
		}
		if err := rsaVerify(&merchantKey.PublicKey, []byte(message), fields["signature"]); err != nil {
			t.Fatalf("request signature: %v", err)
		}
		responseBody := []byte(`{"code_url":"weixin://wxpay/bizpayurl?pr=test"}`)
		responseHeaders := signedWeChatHeaders(t, platformKey, now, "response-nonce", responseBody)
		return &http.Response{StatusCode: http.StatusOK, Header: responseHeaders, Body: io.NopCloser(strings.NewReader(string(responseBody)))}, nil
	})
	gateway, generatedMerchantKey, generatedPlatformKey, _ := newTestWeChat(t, now, client)
	merchantKey = generatedMerchantKey
	platformKey = generatedPlatformKey
	instruction, err := gateway.CreatePayment(context.Background(), OrderRequest{MerchantOrderID: "order_123", Subject: "TermLink Pro", AmountMinor: 1299, Currency: CurrencyCNY, Flow: FlowNative})
	if err != nil {
		t.Fatal(err)
	}
	if instruction.Kind != InstructionQRCode || !strings.HasPrefix(instruction.QRCodeURL, "weixin://") {
		t.Fatalf("instruction = %#v", instruction)
	}
}

func TestWeChatWebhookSignatureAndAESGCMDecryption(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	gateway, _, platformKey, apiKey := newTestWeChat(t, now, nil)
	plaintext := []byte(`{"appid":"wx-app-1","mchid":"merchant-1","out_trade_no":"order_123","transaction_id":"wx-transaction-1","trade_state":"SUCCESS","success_time":"2026-08-27T10:29:30+08:00","amount":{"total":1299,"currency":"CNY"}}`)
	nonce := "123456789012"
	aad := "transaction"
	ciphertext := encryptWeChatTestResource(t, apiKey, nonce, aad, plaintext)
	envelope := map[string]any{
		"id": "event-1", "create_time": now.Add(-time.Minute).Format(time.RFC3339), "event_type": "TRANSACTION.SUCCESS", "resource_type": "encrypt-resource",
		"resource": map[string]string{"algorithm": "AEAD_AES_256_GCM", "ciphertext": ciphertext, "associated_data": aad, "nonce": nonce},
	}
	body, err := json.Marshal(envelope)
	if err != nil {
		t.Fatal(err)
	}
	headers := signedWeChatHeaders(t, platformKey, now.Add(-time.Minute), "callback-nonce", body)
	result, err := gateway.VerifyWebhook(context.Background(), headers, body)
	if err != nil {
		t.Fatal(err)
	}
	if result.Status != StatusSucceeded || result.AmountMinor != 1299 || result.EventID != "event-1" || result.ProviderTransactionID != "wx-transaction-1" {
		t.Fatalf("result = %#v", result)
	}
	refundPlaintext := []byte(`{"mchid":"merchant-1","out_trade_no":"order_123","transaction_id":"wx-transaction-1","out_refund_no":"refund_1","refund_id":"wx-refund-1","refund_status":"SUCCESS","success_time":"2026-08-27T10:29:45+08:00","amount":{"total":1299,"refund":500}}`)
	envelope["id"] = "refund-event-1"
	envelope["event_type"] = "REFUND.SUCCESS"
	envelope["resource"] = map[string]string{"algorithm": "AEAD_AES_256_GCM", "ciphertext": encryptWeChatTestResource(t, apiKey, nonce, aad, refundPlaintext), "associated_data": aad, "nonce": nonce}
	refundBody, err := json.Marshal(envelope)
	if err != nil {
		t.Fatal(err)
	}
	refundHeaders := signedWeChatHeaders(t, platformKey, now.Add(-time.Minute), "refund-callback-nonce", refundBody)
	refundResult, err := gateway.VerifyWebhook(context.Background(), refundHeaders, refundBody)
	if err != nil {
		t.Fatal(err)
	}
	if refundResult.EventType != WebhookRefund || refundResult.Status != StatusRefunded || refundResult.MerchantRefundID != "refund_1" || refundResult.ProviderRefundID != "wx-refund-1" || refundResult.Currency != CurrencyCNY {
		t.Fatalf("refund result = %#v", refundResult)
	}

	tamperedHeaders := headers.Clone()
	tamperedHeaders.Set("Wechatpay-Signature", base64.StdEncoding.EncodeToString(make([]byte, 256)))
	if _, err := gateway.VerifyWebhook(context.Background(), tamperedHeaders, body); !errors.Is(err, ErrInvalidSignature) {
		t.Fatalf("tampered signature error = %v", err)
	}
	oldHeaders := signedWeChatHeaders(t, platformKey, now.Add(-10*time.Minute), "old-nonce", body)
	if _, err := gateway.VerifyWebhook(context.Background(), oldHeaders, body); !errors.Is(err, ErrReplay) {
		t.Fatalf("old webhook error = %v", err)
	}
	if _, err := gateway.VerifyWebhook(context.Background(), headers, []byte(strings.Repeat("x", 4097))); !errors.Is(err, ErrPayloadTooLarge) {
		t.Fatalf("oversized webhook error = %v", err)
	}
}

func TestWeChatMalformedEncryptedPayloadFailsClosed(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	gateway, _, platformKey, _ := newTestWeChat(t, now, nil)
	body := []byte(fmt.Sprintf(`{"id":"event-1","create_time":%q,"event_type":"TRANSACTION.SUCCESS","resource_type":"encrypt-resource","resource":{"algorithm":"AEAD_AES_256_GCM","ciphertext":"not-base64","associated_data":"transaction","nonce":"123456789012"}}`, now.Format(time.RFC3339)))
	headers := signedWeChatHeaders(t, platformKey, now, "nonce", body)
	if _, err := gateway.VerifyWebhook(context.Background(), headers, body); !errors.Is(err, ErrMalformedPayload) {
		t.Fatalf("malformed ciphertext error = %v", err)
	}
}

func TestWeChatRejectsUnsignedOversizedProviderResponse(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	client := roundTripFunc(func(*http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(strings.Repeat("x", 65)))}, nil
	})
	gateway, _, _, _ := newTestWeChat(t, now, client)
	gateway.config.ResponseLimit = 64
	_, err := gateway.CreatePayment(context.Background(), OrderRequest{MerchantOrderID: "order_123", Subject: "Pro", AmountMinor: 100, Flow: FlowNative})
	if !errors.Is(err, ErrPayloadTooLarge) {
		t.Fatalf("error = %v", err)
	}
}

func TestWeChatQueryAndRefundUseSignedProviderValues(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	var platformKey *rsa.PrivateKey
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		var body []byte
		switch {
		case request.Method == http.MethodGet && strings.Contains(request.URL.Path, "/transactions/out-trade-no/order_123"):
			if request.URL.Query().Get("mchid") != "merchant-1" {
				t.Fatalf("query = %s", request.URL.RawQuery)
			}
			body = []byte(`{"appid":"wx-app-1","mchid":"merchant-1","out_trade_no":"order_123","transaction_id":"wx-transaction-1","trade_state":"SUCCESS","success_time":"2026-08-27T10:29:00+08:00","amount":{"total":1299,"currency":"CNY"}}`)
		case request.Method == http.MethodPost && request.URL.Path == "/v3/refund/domestic/refunds":
			body = []byte(`{"refund_id":"wx-refund-1","out_refund_no":"refund_1","out_trade_no":"order_123","status":"PROCESSING","amount":{"refund":500,"total":1299,"currency":"CNY"}}`)
		case request.Method == http.MethodGet && request.URL.Path == "/v3/refund/domestic/refunds/refund_1":
			body = []byte(`{"refund_id":"wx-refund-1","out_refund_no":"refund_1","out_trade_no":"order_123","status":"SUCCESS","amount":{"refund":500,"total":1299,"currency":"CNY"}}`)
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.String())
		}
		headers := signedWeChatHeaders(t, platformKey, now, "response-nonce", body)
		return &http.Response{StatusCode: http.StatusOK, Header: headers, Body: io.NopCloser(strings.NewReader(string(body)))}, nil
	})
	gateway, _, generatedPlatformKey, _ := newTestWeChat(t, now, client)
	platformKey = generatedPlatformKey
	query, err := gateway.QueryPayment(context.Background(), "order_123")
	if err != nil {
		t.Fatal(err)
	}
	if query.Status != StatusSucceeded || query.AmountMinor != 1299 || query.ProviderTransactionID != "wx-transaction-1" {
		t.Fatalf("query = %#v", query)
	}
	refund, err := gateway.Refund(context.Background(), RefundRequest{MerchantOrderID: "order_123", MerchantRefundID: "refund_1", AmountMinor: 500, TotalAmountMinor: 1299, Currency: CurrencyCNY})
	if err != nil {
		t.Fatal(err)
	}
	if refund.Status != StatusRefunding || refund.ProviderRefundID != "wx-refund-1" || refund.AmountMinor != 500 {
		t.Fatalf("refund = %#v", refund)
	}
	queriedRefund, err := gateway.QueryRefund(context.Background(), RefundRequest{MerchantOrderID: "order_123", MerchantRefundID: "refund_1", AmountMinor: 500, TotalAmountMinor: 1299, Currency: CurrencyCNY})
	if err != nil || queriedRefund.Status != StatusRefunded || queriedRefund.ProviderRefundID != "wx-refund-1" {
		t.Fatalf("queried refund = %#v error=%v", queriedRefund, err)
	}
}

func encryptWeChatTestResource(t *testing.T, key []byte, nonce, aad string, plaintext []byte) string {
	t.Helper()
	block, err := aes.NewCipher(key)
	if err != nil {
		t.Fatal(err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		t.Fatal(err)
	}
	return base64.StdEncoding.EncodeToString(gcm.Seal(nil, []byte(nonce), plaintext, []byte(aad)))
}

func signedWeChatHeaders(t *testing.T, key *rsa.PrivateKey, timestamp time.Time, nonce string, body []byte) http.Header {
	t.Helper()
	timestampValue := strconv.FormatInt(timestamp.Unix(), 10)
	signature, err := rsaSign(key, []byte(timestampValue+"\n"+nonce+"\n"+string(body)+"\n"))
	if err != nil {
		t.Fatal(err)
	}
	return http.Header{
		"Wechatpay-Timestamp": {timestampValue}, "Wechatpay-Nonce": {nonce},
		"Wechatpay-Signature": {signature}, "Wechatpay-Serial": {"platform-serial"},
	}
}

func parseWeChatAuthorization(t *testing.T, header string) map[string]string {
	t.Helper()
	const prefix = "WECHATPAY2-SHA256-RSA2048 "
	if !strings.HasPrefix(header, prefix) {
		t.Fatalf("authorization = %q", header)
	}
	result := make(map[string]string)
	for _, entry := range strings.Split(strings.TrimPrefix(header, prefix), ",") {
		key, value, ok := strings.Cut(strings.TrimSpace(entry), "=")
		if !ok {
			t.Fatalf("authorization entry = %q", entry)
		}
		result[key] = strings.Trim(value, `"`)
	}
	return result
}

func TestWeChatQueryRequestTargetIsCanonical(t *testing.T) {
	values := url.Values{"mchid": {"merchant-1"}}
	if got := values.Encode(); got != "mchid=merchant-1" {
		t.Fatalf("query = %s", got)
	}
}

package payment

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"
)

type roundTripFunc func(*http.Request) (*http.Response, error)

func (function roundTripFunc) Do(request *http.Request) (*http.Response, error) {
	return function(request)
}

func generateRSAKey(t *testing.T) *rsa.PrivateKey {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	return key
}

func TestAlipayCreateNativeVerifiesCanonicalRequestAndResponse(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	now := time.Date(2026, 8, 27, 10, 30, 0, 0, time.FixedZone("CST", 8*60*60))
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if err := request.ParseForm(); err != nil {
			t.Fatal(err)
		}
		signature := request.Form.Get("sign")
		request.Form.Del("sign")
		if request.Form.Get("method") != "alipay.trade.precreate" || request.Form.Get("sign_type") != "RSA2" {
			t.Fatalf("unexpected request fields: %v", request.Form)
		}
		if err := rsaVerify(&merchantKey.PublicKey, []byte(canonicalValues(request.Form)), signature); err != nil {
			t.Fatalf("request RSA2 signature: %v", err)
		}
		raw := []byte(`{"code":"10000","out_trade_no":"order_123","qr_code":"https://qr.example/123"}`)
		responseSignature, err := rsaSign(providerKey, raw)
		if err != nil {
			t.Fatal(err)
		}
		body := fmt.Sprintf(`{"alipay_trade_precreate_response":%s,"sign":%q}`, raw, responseSignature)
		return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(body))}, nil
	})
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify",
		PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey, HTTPClient: client, Clock: func() time.Time { return now }, Location: now.Location(),
	})
	if err != nil {
		t.Fatal(err)
	}
	instruction, err := gateway.CreatePayment(context.Background(), OrderRequest{MerchantOrderID: "order_123", Subject: "TermLink Pro", AmountMinor: 1299, Currency: CurrencyCNY, Flow: FlowNative})
	if err != nil {
		t.Fatal(err)
	}
	if instruction.Kind != InstructionQRCode || instruction.QRCodeURL != "https://qr.example/123" {
		t.Fatalf("instruction = %#v", instruction)
	}
}

func TestAlipayWebRedirectUsesRSA2CanonicalSignature(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	now := time.Date(2026, 8, 27, 10, 30, 0, 0, time.FixedZone("CST", 8*60*60))
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify", ReturnURL: "https://app.example/orders/order_123",
		PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey, Clock: func() time.Time { return now }, Location: now.Location(),
	})
	if err != nil {
		t.Fatal(err)
	}
	instruction, err := gateway.CreatePayment(context.Background(), OrderRequest{MerchantOrderID: "order_123", Subject: "TermLink Pro", AmountMinor: 19900, Flow: FlowWeb})
	if err != nil {
		t.Fatal(err)
	}
	redirect, err := url.Parse(instruction.RedirectURL)
	if err != nil {
		t.Fatal(err)
	}
	values := redirect.Query()
	signature := values.Get("sign")
	values.Del("sign")
	if values.Get("method") != "alipay.trade.page.pay" || values.Get("return_url") == "" {
		t.Fatalf("redirect values = %v", values)
	}
	if err := rsaVerify(&merchantKey.PublicKey, []byte(canonicalValues(values)), signature); err != nil {
		t.Fatalf("redirect RSA2 signature: %v", err)
	}
}

func TestAlipayWebhookVerificationAndFailures(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	location := time.FixedZone("CST", 8*60*60)
	now := time.Date(2026, 8, 27, 10, 30, 0, 0, location)
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify", PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey,
		Clock: func() time.Time { return now }, Location: location, WebhookLimit: 4096, ReplayWindow: 5 * time.Minute,
	})
	if err != nil {
		t.Fatal(err)
	}
	values := url.Values{
		"app_id": {"app-1"}, "seller_id": {"seller-1"}, "notify_id": {"event-1"},
		"notify_time": {now.Add(-time.Minute).Format("2006-01-02 15:04:05")}, "out_trade_no": {"order_123"},
		"trade_no": {"provider-456"}, "trade_status": {"TRADE_SUCCESS"}, "total_amount": {"12.99"}, "sign_type": {"RSA2"},
	}
	signedValues := cloneValues(values)
	signedValues.Del("sign_type")
	signature, err := rsaSign(providerKey, []byte(canonicalValues(signedValues)))
	if err != nil {
		t.Fatal(err)
	}
	values.Set("sign", signature)
	body := []byte(values.Encode())
	result, err := gateway.VerifyWebhook(context.Background(), nil, body)
	if err != nil {
		t.Fatal(err)
	}
	if result.Status != StatusSucceeded || result.AmountMinor != 1299 || result.EventID != "event-1" || result.MerchantOrderID != "order_123" {
		t.Fatalf("result = %#v", result)
	}

	tampered := append([]byte(nil), body...)
	tampered = []byte(strings.Replace(string(tampered), "12.99", "99.99", 1))
	if _, err := gateway.VerifyWebhook(context.Background(), nil, tampered); !errors.Is(err, ErrInvalidSignature) {
		t.Fatalf("tampered error = %v", err)
	}
	if _, err := gateway.VerifyWebhook(context.Background(), nil, []byte(strings.Repeat("x", 4097))); !errors.Is(err, ErrPayloadTooLarge) {
		t.Fatalf("oversized error = %v", err)
	}
	values.Set("notify_time", now.Add(-10*time.Minute).Format("2006-01-02 15:04:05"))
	values.Del("sign")
	signedValues = cloneValues(values)
	signedValues.Del("sign_type")
	signature, _ = rsaSign(providerKey, []byte(canonicalValues(signedValues)))
	values.Set("sign", signature)
	if _, err := gateway.VerifyWebhook(context.Background(), nil, []byte(values.Encode())); !errors.Is(err, ErrReplay) {
		t.Fatalf("old webhook error = %v", err)
	}
}

func cloneValues(values url.Values) url.Values {
	cloned := make(url.Values, len(values))
	for key, entries := range values {
		cloned[key] = append([]string(nil), entries...)
	}
	return cloned
}

func TestAlipayProviderResponseLimitFailsClosed(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify", PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey, ResponseLimit: 16,
		HTTPClient: roundTripFunc(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(strings.Repeat("x", 17)))}, nil
		}),
	})
	if err != nil {
		t.Fatal(err)
	}
	_, err = gateway.CreatePayment(context.Background(), OrderRequest{MerchantOrderID: "order_123", Subject: "Pro", AmountMinor: 100, Flow: FlowNative})
	if !errors.Is(err, ErrPayloadTooLarge) {
		t.Fatalf("error = %v", err)
	}
}

func TestAlipayQueryAndRefundUseProviderAuthoritativeValues(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	now := time.Date(2026, 8, 27, 10, 30, 0, 0, time.FixedZone("CST", 8*60*60))
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if err := request.ParseForm(); err != nil {
			t.Fatal(err)
		}
		var key string
		var raw []byte
		switch request.Form.Get("method") {
		case "alipay.trade.query":
			key = "alipay_trade_query_response"
			raw = []byte(`{"code":"10000","out_trade_no":"order_123","trade_no":"trade-1","trade_status":"TRADE_SUCCESS","total_amount":"12.99","send_pay_date":"2026-08-27 10:29:00"}`)
		case "alipay.trade.refund":
			key = "alipay_trade_refund_response"
			raw = []byte(`{"code":"10000","out_trade_no":"order_123","refund_fee":"5.00","fund_change":"Y"}`)
		case "alipay.trade.fastpay.refund.query":
			key = "alipay_trade_fastpay_refund_query_response"
			raw = []byte(`{"code":"10000","out_trade_no":"order_123","out_request_no":"refund_1","refund_amount":"5.00","refund_status":"REFUND_SUCCESS"}`)
		default:
			t.Fatalf("unexpected method %q", request.Form.Get("method"))
		}
		signature, err := rsaSign(providerKey, raw)
		if err != nil {
			t.Fatal(err)
		}
		body := fmt.Sprintf(`{%q:%s,"sign":%q}`, key, raw, signature)
		return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(body))}, nil
	})
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify",
		PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey, HTTPClient: client, Clock: func() time.Time { return now }, Location: now.Location(),
	})
	if err != nil {
		t.Fatal(err)
	}
	query, err := gateway.QueryPayment(context.Background(), "order_123")
	if err != nil {
		t.Fatal(err)
	}
	if query.Status != StatusSucceeded || query.AmountMinor != 1299 || query.ProviderTransactionID != "trade-1" {
		t.Fatalf("query = %#v", query)
	}
	refund, err := gateway.Refund(context.Background(), RefundRequest{MerchantOrderID: "order_123", MerchantRefundID: "refund_1", AmountMinor: 500, TotalAmountMinor: 1299, Currency: CurrencyCNY})
	if err != nil {
		t.Fatal(err)
	}
	if refund.Status != StatusSucceeded || refund.AmountMinor != 500 || refund.MerchantRefundID != "refund_1" {
		t.Fatalf("refund = %#v", refund)
	}
	queriedRefund, err := gateway.QueryRefund(context.Background(), RefundRequest{MerchantOrderID: "order_123", MerchantRefundID: "refund_1", AmountMinor: 500, TotalAmountMinor: 1299, Currency: CurrencyCNY})
	if err != nil || queriedRefund.Status != StatusSucceeded || queriedRefund.AmountMinor != 500 {
		t.Fatalf("queried refund = %#v error=%v", queriedRefund, err)
	}
}

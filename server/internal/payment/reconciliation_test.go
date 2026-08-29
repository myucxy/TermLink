package payment

import (
	"archive/zip"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/csv"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"golang.org/x/text/encoding/simplifiedchinese"
	"golang.org/x/text/transform"
)

var statementTestLocation = time.FixedZone("CST", 8*60*60)

type statementTransportFunc func(*http.Request) (*http.Response, error)

func (function statementTransportFunc) RoundTrip(request *http.Request) (*http.Response, error) {
	return function(request)
}

func TestParseAlipayStatementPaymentsAndRefunds(t *testing.T) {
	body := statementCSV(t, [][]string{
		{"支付宝交易明细"},
		{"支付宝交易号", "商户订单号", "业务类型", "完成时间", "订单金额（元）", "退款金额（元）", "退款批次号/请求号", "支付宝退款号", "交易状态"},
		{"ali-trade-1", "ORDER_1", "交易", "2026-08-26 10:11:12", "12.34", "", "", "", "交易成功"},
		{"ali-trade-2", "ORDER_2", "退款", "2026-08-26 11:12:13", "20.00", "5.67", "REFUND_2", "ali-refund-2", "退款成功"},
		{"总交易单数：2"},
	})
	records := parseStatementForTest(t, StatementDownload{
		Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body)), Size: int64(len(body)),
	}, StatementLimits{})
	if len(records) != 2 {
		t.Fatalf("records = %d, want 2", len(records))
	}
	if records[0].Kind != StatementPayment || records[0].MerchantOrderID != "ORDER_1" || records[0].AmountMinor != 1234 || records[0].Status != StatusSucceeded {
		t.Fatalf("unexpected payment record: %+v", records[0])
	}
	if records[1].Kind != StatementRefund || records[1].MerchantRefundID != "REFUND_2" || records[1].ProviderRefundID != "ali-refund-2" || records[1].AmountMinor != 567 || records[1].Status != StatusRefunded {
		t.Fatalf("unexpected refund record: %+v", records[1])
	}
}

func TestParseWeChatStatementRequiresIntegrityAndDoesNotDuplicateRefundAsPayment(t *testing.T) {
	body := statementCSV(t, [][]string{
		{"微信支付账单"},
		{"交易时间", "微信订单号", "商户订单号", "业务类型", "交易状态", "货币种类", "应结订单金额", "微信退款单号", "商户退款单号", "退款金额", "退款状态", "退款成功时间"},
		{"2026-08-26 09:00:00", "wx-trade-1", "ORDER_1", "交易", "SUCCESS", "CNY", "9.99", "", "", "", "", ""},
		{"2026-08-26 09:10:00", "wx-trade-2", "ORDER_2", "退款", "REFUND", "CNY", "20.00", "wx-refund-2", "REFUND_2", "3.21", "SUCCESS", "2026-08-26 12:00:00"},
	})
	hash := sha256.Sum256(body)
	download := StatementDownload{
		Provider: ProviderWeChatPay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body)),
		Size: int64(len(body)), HashType: "SHA256", HashHex: hex.EncodeToString(hash[:]),
	}
	records := parseStatementForTest(t, download, StatementLimits{})
	if len(records) != 2 {
		t.Fatalf("records = %d, want one payment and one refund", len(records))
	}
	if records[0].Kind != StatementPayment || records[1].Kind != StatementRefund || records[1].ProviderRefundID != "wx-refund-2" {
		t.Fatalf("unexpected records: %+v", records)
	}

	missingHash := download
	missingHash.Body = io.NopCloser(bytes.NewReader(body))
	missingHash.HashType = ""
	missingHash.HashHex = ""
	if err := ParseStatement(missingHash, StatementLimits{}, func(StatementRecord) error { return nil }); !errors.Is(err, ErrStatementIntegrity) {
		t.Fatalf("missing WeChat hash error = %v", err)
	}
}

func TestParseStatementCompressedFormats(t *testing.T) {
	body := alipayPaymentStatement(t, "ORDER_1", "ali-trade-1", "1.23")
	var compressed bytes.Buffer
	gzipWriter := gzip.NewWriter(&compressed)
	if _, err := gzipWriter.Write(body); err != nil {
		t.Fatal(err)
	}
	if err := gzipWriter.Close(); err != nil {
		t.Fatal(err)
	}

	zipBody := statementZIP(t, map[string][]byte{"detail.csv": body, "summary.txt": []byte("summary")})
	tests := []struct {
		name string
		body []byte
	}{
		{name: "gzip_auto", body: compressed.Bytes()},
		{name: "zip_auto", body: zipBody},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			records := parseStatementForTest(t, StatementDownload{
				Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatAuto,
				Body: io.NopCloser(bytes.NewReader(test.body)), Size: int64(len(test.body)),
			}, StatementLimits{})
			if len(records) != 1 || records[0].AmountMinor != 123 {
				t.Fatalf("unexpected records: %+v", records)
			}
		})
	}
}

func TestParseStatementAcceptsProviderGB18030AndStopsAtSummary(t *testing.T) {
	utf8Body := statementCSV(t, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.23"},
		{"总交易单数", "总交易额"},
		{"1", "1.23"},
	})
	body, _, err := transform.Bytes(simplifiedchinese.GB18030.NewEncoder(), utf8Body)
	if err != nil {
		t.Fatal(err)
	}
	records := parseStatementForTest(t, StatementDownload{
		Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV,
		Body: io.NopCloser(bytes.NewReader(body)), Size: int64(len(body)),
	}, StatementLimits{})
	if len(records) != 1 || records[0].MerchantOrderID != "ORDER_1" {
		t.Fatalf("unexpected GB18030 records: %+v", records)
	}
}

func TestParseStatementExactUncompressedLimitSucceeds(t *testing.T) {
	body := alipayPaymentStatement(t, "ORDER_1", "trade-1", "1.23")
	records := parseStatementForTest(t, StatementDownload{
		Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV,
		Body: io.NopCloser(bytes.NewReader(body)), Size: int64(len(body)),
	}, StatementLimits{MaxDownloadBytes: int64(len(body)), MaxUncompressedBytes: int64(len(body))})
	if len(records) != 1 {
		t.Fatalf("records = %d", len(records))
	}
}

func TestParseStatementRejectsLimitsAndAmbiguousArchives(t *testing.T) {
	body := alipayPaymentStatement(t, "ORDER_1", "ali-trade-1", "1.23")
	twoRows := statementCSV(t, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"ali-trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.23"},
		{"ali-trade-2", "ORDER_2", "2026-08-26 10:00:01", "2.00"},
	})
	tests := []struct {
		name     string
		download StatementDownload
		limits   StatementLimits
		want     error
	}{
		{
			name: "declared download size", download: StatementDownload{Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body)), Size: 200},
			limits: StatementLimits{MaxDownloadBytes: 100}, want: ErrStatementLimit,
		},
		{
			name: "download bytes", download: StatementDownload{Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body))},
			limits: StatementLimits{MaxDownloadBytes: int64(len(body) - 1)}, want: ErrStatementLimit,
		},
		{
			name: "rows", download: StatementDownload{Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(twoRows))},
			limits: StatementLimits{MaxRows: 1}, want: ErrStatementLimit,
		},
		{
			name: "field", download: StatementDownload{Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body))},
			limits: StatementLimits{MaxFieldBytes: 8}, want: ErrStatementLimit,
		},
		{
			name: "multiple csv", download: StatementDownload{Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatZIP, Body: io.NopCloser(bytes.NewReader(statementZIP(t, map[string][]byte{"one.csv": body, "two.csv": body})))},
			limits: StatementLimits{}, want: ErrStatementMalformed,
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if err := ParseStatement(test.download, test.limits, func(StatementRecord) error { return nil }); !errors.Is(err, test.want) {
				t.Fatalf("error = %v, want %v", err, test.want)
			}
		})
	}
}

func TestParseStatementRejectsInvalidProviderFieldsAndHash(t *testing.T) {
	tests := []struct {
		name     string
		provider Provider
		rows     [][]string
	}{
		{
			name: "amount", provider: ProviderAlipay,
			rows: [][]string{{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"}, {"trade", "ORDER_1", "2026-08-26 10:00:00", "1.234"}},
		},
		{
			name: "date", provider: ProviderAlipay,
			rows: [][]string{{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"}, {"trade", "ORDER_1", "not-a-date", "1.23"}},
		},
		{
			name: "currency", provider: ProviderWeChatPay,
			rows: [][]string{{"交易时间", "微信订单号", "商户订单号", "交易状态", "货币种类", "应结订单金额"}, {"2026-08-26 10:00:00", "trade", "ORDER_1", "SUCCESS", "US", "1.23"}},
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			body := statementCSV(t, test.rows)
			download := StatementDownload{Provider: test.provider, Date: testStatementDate(), Format: StatementFormatCSV, Body: io.NopCloser(bytes.NewReader(body)), Size: int64(len(body))}
			if test.provider == ProviderWeChatPay {
				hash := sha256.Sum256(body)
				download.HashType, download.HashHex = "SHA256", hex.EncodeToString(hash[:])
			}
			if err := ParseStatement(download, StatementLimits{}, func(StatementRecord) error { return nil }); !errors.Is(err, ErrStatementMalformed) {
				t.Fatalf("error = %v", err)
			}
		})
	}

	body := alipayPaymentStatement(t, "ORDER_1", "trade", "1.23")
	if err := ParseStatement(StatementDownload{
		Provider: ProviderAlipay, Date: testStatementDate(), Format: StatementFormatCSV,
		Body: io.NopCloser(bytes.NewReader(body)), HashType: "SHA256", HashHex: strings.Repeat("0", 64),
	}, StatementLimits{}, func(StatementRecord) error { return nil }); !errors.Is(err, ErrStatementIntegrity) {
		t.Fatalf("hash mismatch error = %v", err)
	}
}

func TestDownloadStatementValidatesTransport(t *testing.T) {
	date := testStatementDate()
	if _, err := downloadStatement(context.Background(), nil, ProviderAlipay, date, "https://bill.example/file", "", ""); !errors.Is(err, ErrStatementUnavailable) {
		t.Fatalf("nil client error = %v", err)
	}
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: http.StatusBadGateway, Body: io.NopCloser(strings.NewReader("upstream")), Header: make(http.Header), Request: request}, nil
	})
	if _, err := downloadStatement(context.Background(), client, ProviderAlipay, date, "http://bill.example/file", "", ""); !errors.Is(err, ErrStatementMalformed) {
		t.Fatalf("insecure URL error = %v", err)
	}
	if _, err := downloadStatement(context.Background(), client, ProviderAlipay, date, "https://bill.example/file", "", ""); !errors.Is(err, ErrStatementUnavailable) {
		t.Fatalf("non-2xx error = %v", err)
	}

	largeClient := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: http.StatusOK, ContentLength: DefaultStatementDownloadLimit + 1, Body: io.NopCloser(strings.NewReader("")), Header: make(http.Header), Request: request}, nil
	})
	if _, err := downloadStatement(context.Background(), largeClient, ProviderAlipay, date, "https://bill.example/file", "", ""); !errors.Is(err, ErrStatementLimit) {
		t.Fatalf("content-length error = %v", err)
	}

	requests := 0
	redirectClient := &http.Client{Transport: statementTransportFunc(func(request *http.Request) (*http.Response, error) {
		requests++
		response := &http.Response{
			StatusCode: http.StatusFound, Header: make(http.Header), Body: io.NopCloser(strings.NewReader("")), Request: request,
		}
		response.Header.Set("Location", "http://private.example/statement")
		return response, nil
	})}
	if _, err := downloadStatement(context.Background(), redirectClient, ProviderAlipay, date, "https://bill.example/file", "", ""); !errors.Is(err, ErrStatementMalformed) {
		t.Fatalf("insecure redirect error = %v", err)
	}
	if requests != 1 {
		t.Fatalf("insecure redirect issued %d requests", requests)
	}
}

func TestAlipayDownloadStatementUsesSignedBillURL(t *testing.T) {
	merchantKey := generateRSAKey(t)
	providerKey := generateRSAKey(t)
	statement := alipayPaymentStatement(t, "ORDER_1", "trade-1", "1.23")
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if request.URL.Host == "bill.example" {
			return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), ContentLength: int64(len(statement)), Body: io.NopCloser(bytes.NewReader(statement)), Request: request}, nil
		}
		if err := request.ParseForm(); err != nil {
			t.Fatal(err)
		}
		if request.Form.Get("method") != "alipay.data.dataservice.bill.downloadurl.query" || request.Form.Get("biz_content") != `{"bill_date":"20260826","bill_type":"trade"}` {
			t.Fatalf("unexpected bill query: %v", request.Form)
		}
		raw := []byte(`{"code":"10000","bill_download_url":"https://bill.example/alipay.csv"}`)
		signature, err := rsaSign(providerKey, raw)
		if err != nil {
			t.Fatal(err)
		}
		body := fmt.Sprintf(`{"alipay_data_dataservice_bill_downloadurl_query_response":%s,"sign":%q}`, raw, signature)
		return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(body)), Request: request}, nil
	})
	gateway, err := NewAlipay(AlipayConfig{
		AppID: "app-1", SellerID: "seller-1", GatewayURL: "https://alipay.example/gateway.do", NotifyURL: "https://api.example/pay/alipay/notify",
		PrivateKey: merchantKey, AlipayPublicKey: &providerKey.PublicKey, HTTPClient: client, Location: statementTestLocation,
	})
	if err != nil {
		t.Fatal(err)
	}
	download, err := gateway.DownloadStatement(context.Background(), StatementRequest{Date: testStatementDate()})
	if err != nil {
		t.Fatal(err)
	}
	records := parseStatementForTest(t, download, StatementLimits{})
	if len(records) != 1 || records[0].MerchantOrderID != "ORDER_1" {
		t.Fatalf("records = %+v", records)
	}
}

func TestWeChatDownloadStatementUsesSignedURLAndHash(t *testing.T) {
	now := time.Date(2026, 8, 27, 2, 30, 0, 0, time.UTC)
	statement := statementCSV(t, [][]string{
		{"交易时间", "微信订单号", "商户订单号", "交易状态", "货币种类", "应结订单金额"},
		{"2026-08-26 10:00:00", "wx-trade-1", "ORDER_1", "SUCCESS", "CNY", "1.23"},
	})
	hash := sha256.Sum256(statement)
	signingKey := generateRSAKey(t)
	client := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if request.URL.Host == "bill.example" {
			return &http.Response{StatusCode: http.StatusOK, Header: make(http.Header), ContentLength: int64(len(statement)), Body: io.NopCloser(bytes.NewReader(statement)), Request: request}, nil
		}
		if request.URL.Path != "/v3/bill/tradebill" || request.URL.Query().Get("bill_date") != "2026-08-26" || request.URL.Query().Get("bill_type") != "ALL" {
			t.Fatalf("unexpected WeChat bill request: %s", request.URL.String())
		}
		body := []byte(fmt.Sprintf(`{"hash_type":"SHA256","hash_value":%q,"download_url":"https://bill.example/wechat.csv"}`, hex.EncodeToString(hash[:])))
		headers := signedWeChatHeaders(t, signingKey, now, "statement-response", body)
		return &http.Response{StatusCode: http.StatusOK, Header: headers, Body: io.NopCloser(bytes.NewReader(body)), Request: request}, nil
	})
	merchantKey := generateRSAKey(t)
	gateway, err := NewWeChat(WeChatConfig{
		AppID: "wx-app-1", MchID: "merchant-1", MerchantSerial: "merchant-serial", MerchantPrivateKey: merchantKey,
		APIv3Key: []byte("0123456789abcdef0123456789abcdef"), PlatformPublicKeys: map[string]*rsa.PublicKey{"platform-serial": &signingKey.PublicKey},
		NotifyURL: "https://api.example/pay/wechat/notify", BaseURL: "https://wechat.example", HTTPClient: client, Clock: func() time.Time { return now },
	})
	if err != nil {
		t.Fatal(err)
	}
	download, err := gateway.DownloadStatement(context.Background(), StatementRequest{Date: testStatementDate()})
	if err != nil {
		t.Fatal(err)
	}
	records := parseStatementForTest(t, download, StatementLimits{})
	if len(records) != 1 || records[0].ProviderTransactionID != "wx-trade-1" {
		t.Fatalf("records = %+v", records)
	}
}

func parseStatementForTest(t *testing.T, download StatementDownload, limits StatementLimits) []StatementRecord {
	t.Helper()
	var records []StatementRecord
	if err := ParseStatement(download, limits, func(record StatementRecord) error {
		records = append(records, record)
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return records
}

func testStatementDate() time.Time {
	return time.Date(2026, 8, 26, 0, 0, 0, 0, statementTestLocation)
}

func alipayPaymentStatement(t *testing.T, order, trade, amount string) []byte {
	t.Helper()
	return statementCSV(t, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{trade, order, "2026-08-26 10:00:00", amount},
	})
}

func statementCSV(t *testing.T, rows [][]string) []byte {
	t.Helper()
	var buffer bytes.Buffer
	writer := csv.NewWriter(&buffer)
	if err := writer.WriteAll(rows); err != nil {
		t.Fatal(err)
	}
	writer.Flush()
	if err := writer.Error(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

func statementZIP(t *testing.T, files map[string][]byte) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	for name, body := range files {
		file, err := archive.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := file.Write(body); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

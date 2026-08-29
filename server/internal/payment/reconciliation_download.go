package payment

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

var (
	_ StatementDownloader = (*AlipayGateway)(nil)
	_ StatementDownloader = (*WeChatGateway)(nil)
)

func (g *AlipayGateway) DownloadStatement(ctx context.Context, request StatementRequest) (StatementDownload, error) {
	date, err := statementDate(request)
	if err != nil {
		return StatementDownload{}, err
	}
	response, err := g.call(ctx, "alipay.data.dataservice.bill.downloadurl.query", "alipay_data_dataservice_bill_downloadurl_query_response", map[string]any{
		"bill_type": "trade", "bill_date": strings.ReplaceAll(date, "-", ""),
	})
	if err != nil {
		return StatementDownload{}, err
	}
	var parsed struct {
		Code            string `json:"code"`
		SubCode         string `json:"sub_code"`
		BillDownloadURL string `json:"bill_download_url"`
	}
	if err := json.Unmarshal(response, &parsed); err != nil || parsed.Code == "" {
		return StatementDownload{}, ErrStatementMalformed
	}
	if parsed.Code != "10000" {
		return StatementDownload{}, providerRejected(parsed.SubCode)
	}
	return downloadStatement(ctx, g.client, ProviderAlipay, request.Date, parsed.BillDownloadURL, "", "")
}

func (g *WeChatGateway) DownloadStatement(ctx context.Context, request StatementRequest) (StatementDownload, error) {
	date, err := statementDate(request)
	if err != nil {
		return StatementDownload{}, err
	}
	query := url.Values{"bill_date": {date}, "bill_type": {"ALL"}}
	body, err := g.call(ctx, http.MethodGet, "/v3/bill/tradebill", query, nil)
	if err != nil {
		return StatementDownload{}, err
	}
	var parsed struct {
		HashType    string `json:"hash_type"`
		HashValue   string `json:"hash_value"`
		DownloadURL string `json:"download_url"`
	}
	if err := json.Unmarshal(body, &parsed); err != nil || parsed.HashType == "" || parsed.HashValue == "" {
		return StatementDownload{}, ErrStatementMalformed
	}
	return downloadStatement(ctx, g.client, ProviderWeChatPay, request.Date, parsed.DownloadURL, parsed.HashType, parsed.HashValue)
}

func downloadStatement(ctx context.Context, client HTTPClient, provider Provider, date time.Time, rawURL, hashType, hashValue string) (StatementDownload, error) {
	if client == nil {
		return StatementDownload{}, ErrStatementUnavailable
	}
	parsed, err := url.Parse(strings.TrimSpace(rawURL))
	if err != nil || parsed.Scheme != "https" || parsed.Host == "" || parsed.User != nil || parsed.Fragment != "" {
		return StatementDownload{}, ErrStatementMalformed
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, parsed.String(), nil)
	if err != nil {
		return StatementDownload{}, ErrStatementMalformed
	}
	request.Header.Set("Accept", "application/octet-stream,text/csv,application/zip,application/gzip")
	response, err := statementDownloadClient(client).Do(request)
	if err != nil {
		if errors.Is(err, ErrStatementMalformed) {
			return StatementDownload{}, ErrStatementMalformed
		}
		return StatementDownload{}, fmt.Errorf("%w: provider download", ErrStatementUnavailable)
	}
	if response == nil || response.Body == nil {
		return StatementDownload{}, ErrStatementUnavailable
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		response.Body.Close()
		return StatementDownload{}, fmt.Errorf("%w: HTTP %d", ErrStatementUnavailable, response.StatusCode)
	}
	if response.Request != nil && response.Request.URL != nil && response.Request.URL.Scheme != "https" {
		response.Body.Close()
		return StatementDownload{}, ErrStatementMalformed
	}
	size := response.ContentLength
	if size < 0 {
		if value := response.Header.Get("Content-Length"); value != "" {
			size, _ = strconv.ParseInt(value, 10, 64)
		}
	}
	if size > DefaultStatementDownloadLimit {
		response.Body.Close()
		return StatementDownload{}, ErrStatementLimit
	}
	return StatementDownload{
		Provider: provider, Date: date.UTC(), Format: StatementFormatAuto, Body: response.Body, Size: size,
		HashType: hashType, HashHex: hashValue,
	}, nil
}

func statementDownloadClient(client HTTPClient) HTTPClient {
	httpClient, ok := client.(*http.Client)
	if !ok {
		return client
	}
	clone := *httpClient
	previousCheck := clone.CheckRedirect
	clone.CheckRedirect = func(request *http.Request, via []*http.Request) error {
		if request == nil || request.URL == nil || request.URL.Scheme != "https" || request.URL.Host == "" || request.URL.User != nil || request.URL.Fragment != "" {
			return ErrStatementMalformed
		}
		if previousCheck != nil {
			return previousCheck(request, via)
		}
		if len(via) >= 10 {
			return ErrStatementUnavailable
		}
		return nil
	}
	return &clone
}

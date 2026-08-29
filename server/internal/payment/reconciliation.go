package payment

import (
	"archive/zip"
	"bufio"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha1"
	"crypto/sha256"
	"encoding/csv"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"golang.org/x/text/encoding/simplifiedchinese"
	"golang.org/x/text/transform"
)

const (
	DefaultStatementDownloadLimit     = int64(32 << 20)
	DefaultStatementUncompressedLimit = int64(64 << 20)
	DefaultStatementRowLimit          = 200_000
	DefaultStatementFieldLimit        = 16 << 10
	MaxStatementPeriodDays            = 31
)

var (
	ErrStatementUnavailable = errors.New("payment: provider statement unavailable")
	ErrStatementMalformed   = errors.New("payment: malformed provider statement")
	ErrStatementLimit       = errors.New("payment: provider statement exceeds limit")
	ErrStatementIntegrity   = errors.New("payment: provider statement integrity check failed")
)

type StatementFormat string

const (
	StatementFormatAuto StatementFormat = "AUTO"
	StatementFormatCSV  StatementFormat = "CSV"
	StatementFormatGZIP StatementFormat = "GZIP"
	StatementFormatZIP  StatementFormat = "ZIP"
)

type StatementKind string

const (
	StatementPayment StatementKind = "PAYMENT"
	StatementRefund  StatementKind = "REFUND"
)

type StatementRequest struct {
	Date time.Time
}

type StatementDownload struct {
	Provider Provider
	Date     time.Time
	Format   StatementFormat
	Body     io.ReadCloser
	Size     int64
	HashType string
	HashHex  string
}

type StatementRecord struct {
	Kind                  StatementKind
	MerchantOrderID       string
	MerchantRefundID      string
	ProviderTransactionID string
	ProviderRefundID      string
	AmountMinor           int64
	Currency              string
	Status                PaymentStatus
	OccurredAt            time.Time
}

type StatementLimits struct {
	MaxDownloadBytes     int64
	MaxUncompressedBytes int64
	MaxRows              int
	MaxFieldBytes        int
}

type StatementDownloader interface {
	Provider() Provider
	DownloadStatement(context.Context, StatementRequest) (StatementDownload, error)
}

func normalizeStatementLimits(limits StatementLimits) StatementLimits {
	if limits.MaxDownloadBytes <= 0 || limits.MaxDownloadBytes > DefaultStatementDownloadLimit {
		limits.MaxDownloadBytes = DefaultStatementDownloadLimit
	}
	if limits.MaxUncompressedBytes <= 0 || limits.MaxUncompressedBytes > DefaultStatementUncompressedLimit {
		limits.MaxUncompressedBytes = DefaultStatementUncompressedLimit
	}
	if limits.MaxRows <= 0 || limits.MaxRows > DefaultStatementRowLimit {
		limits.MaxRows = DefaultStatementRowLimit
	}
	if limits.MaxFieldBytes <= 0 || limits.MaxFieldBytes > DefaultStatementFieldLimit {
		limits.MaxFieldBytes = DefaultStatementFieldLimit
	}
	return limits
}

// ParseStatement streams CSV records from a bounded provider download. ZIP
// input is first bounded by compressed and uncompressed limits, then each CSV
// row is processed sequentially. Raw statement bytes are never retained by
// the reconciliation layer.
func ParseStatement(download StatementDownload, limits StatementLimits, emit func(StatementRecord) error) error {
	if download.Body == nil || emit == nil || (download.Provider != ProviderAlipay && download.Provider != ProviderWeChatPay) {
		return ErrStatementMalformed
	}
	defer download.Body.Close()
	limits = normalizeStatementLimits(limits)
	if download.Size > limits.MaxDownloadBytes {
		return ErrStatementLimit
	}
	if download.Provider == ProviderWeChatPay && (strings.TrimSpace(download.HashType) == "" || strings.TrimSpace(download.HashHex) == "") {
		return ErrStatementIntegrity
	}
	compressed, err := readStatementBounded(download.Body, limits.MaxDownloadBytes)
	if err != nil {
		return err
	}
	if err := verifyStatementHash(compressed, download.HashType, download.HashHex); err != nil {
		return err
	}
	var reader io.Reader
	var closer io.Closer
	switch download.Format {
	case "", StatementFormatAuto:
		download.Format = detectStatementFormat(compressed)
	}
	switch download.Format {
	case StatementFormatCSV:
		reader = bytes.NewReader(compressed)
	case StatementFormatGZIP:
		gzipReader, openErr := gzip.NewReader(bytes.NewReader(compressed))
		if openErr != nil {
			return ErrStatementMalformed
		}
		reader, closer = gzipReader, gzipReader
	case StatementFormatZIP:
		return parseZIPStatement(compressed, download.Provider, limits, emit)
	default:
		return ErrStatementMalformed
	}
	if closer != nil {
		defer closer.Close()
	}
	return parseStatementCSV(io.LimitReader(reader, limits.MaxUncompressedBytes+1), download.Provider, limits, emit)
}

func verifyStatementHash(data []byte, hashType, expected string) error {
	hashType = strings.ToUpper(strings.TrimSpace(hashType))
	expected = strings.ToLower(strings.TrimSpace(expected))
	if hashType == "" && expected == "" {
		return nil
	}
	if expected == "" {
		return ErrStatementIntegrity
	}
	var actual string
	switch hashType {
	case "SHA1":
		digest := sha1.Sum(data) // #nosec G505 -- required provider statement integrity format.
		actual = hex.EncodeToString(digest[:])
	case "SHA256":
		digest := sha256.Sum256(data)
		actual = hex.EncodeToString(digest[:])
	default:
		return ErrStatementIntegrity
	}
	if !strings.EqualFold(actual, expected) {
		return ErrStatementIntegrity
	}
	return nil
}

func parseZIPStatement(compressed []byte, provider Provider, limits StatementLimits, emit func(StatementRecord) error) error {
	archive, err := zip.NewReader(bytes.NewReader(compressed), int64(len(compressed)))
	if err != nil || len(archive.File) == 0 || len(archive.File) > 8 {
		return ErrStatementMalformed
	}
	parsed := 0
	for _, file := range archive.File {
		name := strings.ToLower(file.Name)
		if file.FileInfo().IsDir() || !strings.HasSuffix(name, ".csv") || strings.Contains(file.Name, "汇总") || strings.Contains(name, "summary") {
			continue
		}
		if file.UncompressedSize64 > uint64(limits.MaxUncompressedBytes) {
			return ErrStatementLimit
		}
		body, openErr := file.Open()
		if openErr != nil {
			return ErrStatementMalformed
		}
		parseErr := parseStatementCSV(io.LimitReader(body, limits.MaxUncompressedBytes+1), provider, limits, emit)
		closeErr := body.Close()
		if parseErr != nil {
			return parseErr
		}
		if closeErr != nil {
			return ErrStatementMalformed
		}
		parsed++
	}
	if parsed != 1 {
		return ErrStatementMalformed
	}
	return nil
}

func parseStatementCSV(reader io.Reader, provider Provider, limits StatementLimits, emit func(StatementRecord) error) error {
	counter := &statementCountingReader{reader: reader, remaining: limits.MaxUncompressedBytes}
	textReader, err := statementTextReader(counter)
	if err != nil {
		return err
	}
	csvReader := csv.NewReader(textReader)
	csvReader.FieldsPerRecord = -1
	csvReader.ReuseRecord = true
	csvReader.TrimLeadingSpace = true
	var header []string
	rowCount := 0
	footerSeen := false
	for {
		row, err := csvReader.Read()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			if errors.Is(counter.err, ErrStatementLimit) {
				return ErrStatementLimit
			}
			return ErrStatementMalformed
		}
		if err := validateStatementFields(row, limits.MaxFieldBytes); err != nil {
			return err
		}
		if header == nil {
			if isProviderHeader(provider, row) {
				header = append([]string(nil), row...)
			}
			continue
		}
		if emptyStatementRow(row) {
			continue
		}
		if footerSeen {
			continue
		}
		if statementFooter(row) {
			footerSeen = true
			continue
		}
		rowCount++
		if rowCount > limits.MaxRows {
			return ErrStatementLimit
		}
		records, parseErr := parseProviderStatementRow(provider, header, row)
		if parseErr != nil {
			return parseErr
		}
		for _, record := range records {
			if emitErr := emit(record); emitErr != nil {
				return emitErr
			}
		}
	}
	if header == nil {
		return ErrStatementMalformed
	}
	return nil
}

func statementTextReader(reader io.Reader) (io.Reader, error) {
	buffered := bufio.NewReaderSize(reader, 64<<10)
	sample, err := buffered.Peek(64 << 10)
	if err != nil && !errors.Is(err, bufio.ErrBufferFull) && !errors.Is(err, io.EOF) {
		return nil, ErrStatementMalformed
	}
	sample = trimIncompleteUTF8Suffix(sample)
	if utf8.Valid(sample) {
		return buffered, nil
	}
	return transform.NewReader(buffered, simplifiedchinese.GB18030.NewDecoder()), nil
}

func trimIncompleteUTF8Suffix(value []byte) []byte {
	for trim := 0; trim < utf8.UTFMax && trim < len(value); trim++ {
		candidate := value[:len(value)-trim]
		if utf8.Valid(candidate) {
			return candidate
		}
	}
	return value
}

type statementCountingReader struct {
	reader    io.Reader
	remaining int64
	err       error
}

func (reader *statementCountingReader) Read(buffer []byte) (int, error) {
	if reader.remaining <= 0 {
		var probe [1]byte
		read, err := reader.reader.Read(probe[:])
		if read > 0 {
			reader.err = ErrStatementLimit
			return 0, reader.err
		}
		return 0, err
	}
	if int64(len(buffer)) > reader.remaining {
		buffer = buffer[:reader.remaining]
	}
	read, err := reader.reader.Read(buffer)
	reader.remaining -= int64(read)
	if reader.remaining < 0 {
		reader.err = ErrStatementLimit
		return read, reader.err
	}
	return read, err
}

func readStatementBounded(reader io.Reader, limit int64) ([]byte, error) {
	data, err := io.ReadAll(io.LimitReader(reader, limit+1))
	if err != nil {
		return nil, ErrStatementUnavailable
	}
	if int64(len(data)) > limit {
		return nil, ErrStatementLimit
	}
	return data, nil
}

func detectStatementFormat(data []byte) StatementFormat {
	if len(data) >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4 {
		return StatementFormatZIP
	}
	if len(data) >= 2 && data[0] == 0x1f && data[1] == 0x8b {
		return StatementFormatGZIP
	}
	return StatementFormatCSV
}

func validateStatementFields(row []string, max int) error {
	if len(row) == 0 || len(row) > 128 {
		return ErrStatementMalformed
	}
	for _, field := range row {
		if !utf8.ValidString(field) {
			return ErrStatementMalformed
		}
		if len(field) > max {
			return ErrStatementLimit
		}
	}
	return nil
}

func isProviderHeader(provider Provider, row []string) bool {
	index := statementHeaderIndex(row)
	switch provider {
	case ProviderAlipay:
		return index.has("商户订单号") && index.has("支付宝交易号")
	case ProviderWeChatPay:
		return index.has("商户订单号") && index.has("微信订单号")
	default:
		return false
	}
}

func parseProviderStatementRow(provider Provider, header, row []string) ([]StatementRecord, error) {
	values := statementRowValues(header, row)
	switch provider {
	case ProviderAlipay:
		return parseAlipayStatementRow(values)
	case ProviderWeChatPay:
		return parseWeChatStatementRow(values)
	default:
		return nil, ErrStatementMalformed
	}
}

func parseAlipayStatementRow(values statementValues) ([]StatementRecord, error) {
	orderNo := values.get("商户订单号")
	providerNo := values.get("支付宝交易号")
	if !validMerchantIdentifier(orderNo) || providerNo == "" || len(providerNo) > 128 {
		return nil, ErrStatementMalformed
	}
	refundNo := values.getAny("退款批次号/请求号", "商户退款单号")
	businessType := strings.ToUpper(values.getAny("业务类型", "收支类型"))
	isRefund := refundNo != "" || strings.Contains(businessType, "退款") || strings.Contains(businessType, "REFUND")
	if isRefund {
		if !validMerchantIdentifier(refundNo) {
			return nil, ErrStatementMalformed
		}
		if status := strings.ToUpper(values.getAny("退款状态", "交易状态")); status != "" && !successfulStatementStatus(status) && !refundedStatementStatus(status) {
			return nil, nil
		}
		occurred, err := parseStatementTime(values.getAny("退款成功时间", "退款时间", "完成时间", "交易时间"))
		if err != nil {
			return nil, err
		}
		amount, err := parsePositiveStatementAmount(values.getAny("退款金额（元）", "退款金额(元)", "退款金额", "商家实收（元）", "商家实收(元)"), true)
		if err != nil {
			return nil, err
		}
		providerRefundNo := values.getAny("支付宝退款号", "退款交易号")
		if providerRefundNo != "" && len(providerRefundNo) > 128 {
			return nil, ErrStatementMalformed
		}
		return []StatementRecord{{Kind: StatementRefund, MerchantOrderID: orderNo, MerchantRefundID: refundNo, ProviderTransactionID: providerNo, ProviderRefundID: providerRefundNo, AmountMinor: amount, Currency: CurrencyCNY, Status: StatusRefunded, OccurredAt: occurred}}, nil
	}
	if status := strings.ToUpper(values.get("交易状态")); status != "" && !successfulStatementStatus(status) {
		return nil, nil
	}
	occurred, err := parseStatementTime(values.getAny("完成时间", "交易时间"))
	if err != nil {
		return nil, err
	}
	amount, err := parsePositiveStatementAmount(values.getAny("订单金额（元）", "订单金额(元)", "订单金额"), false)
	if err != nil {
		return nil, err
	}
	return []StatementRecord{{Kind: StatementPayment, MerchantOrderID: orderNo, ProviderTransactionID: providerNo, AmountMinor: amount, Currency: CurrencyCNY, Status: StatusSucceeded, OccurredAt: occurred}}, nil
}

func parseWeChatStatementRow(values statementValues) ([]StatementRecord, error) {
	orderNo := values.get("商户订单号")
	providerNo := values.get("微信订单号")
	currency := strings.ToUpper(values.getAny("货币种类", "币种"))
	if !validMerchantIdentifier(orderNo) || providerNo == "" || len(providerNo) > 128 || !validStatementCurrency(currency) {
		return nil, ErrStatementMalformed
	}
	refundNo := values.get("商户退款单号")
	businessType := strings.ToUpper(values.get("业务类型"))
	tradeStatus := strings.ToUpper(values.get("交易状态"))
	isRefund := refundNo != "" || strings.Contains(businessType, "退款") || strings.Contains(businessType, "REFUND") || refundedStatementStatus(tradeStatus)
	if isRefund {
		if !validMerchantIdentifier(refundNo) {
			return nil, ErrStatementMalformed
		}
		if status := strings.ToUpper(values.get("退款状态")); status != "" && !successfulStatementStatus(status) && !refundedStatementStatus(status) {
			return nil, nil
		}
		occurred, err := parseStatementTime(values.getAny("退款成功时间", "退款时间", "交易时间", "完成时间"))
		if err != nil {
			return nil, err
		}
		refundCurrency := strings.ToUpper(values.getAny("退款币种", "货币种类", "币种"))
		if !validStatementCurrency(refundCurrency) {
			return nil, ErrStatementMalformed
		}
		refundAmount, refundErr := parsePositiveStatementAmount(values.getAny("退款金额", "退款金额（元）", "退款金额(元)", "申请退款金额", "申请退款金额（元）", "申请退款金额(元)"), false)
		if refundErr != nil {
			return nil, refundErr
		}
		providerRefundNo := values.get("微信退款单号")
		if providerRefundNo != "" && len(providerRefundNo) > 128 {
			return nil, ErrStatementMalformed
		}
		return []StatementRecord{{Kind: StatementRefund, MerchantOrderID: orderNo, MerchantRefundID: refundNo, ProviderTransactionID: providerNo, ProviderRefundID: providerRefundNo, AmountMinor: refundAmount, Currency: refundCurrency, Status: StatusRefunded, OccurredAt: occurred}}, nil
	}
	if tradeStatus != "" && !successfulStatementStatus(tradeStatus) {
		return nil, nil
	}
	occurred, err := parseStatementTime(values.getAny("交易时间", "完成时间"))
	if err != nil {
		return nil, err
	}
	amount, err := parsePositiveStatementAmount(values.getAny("应结订单金额", "订单金额（元）", "订单金额(元)", "订单金额"), false)
	if err != nil {
		return nil, err
	}
	return []StatementRecord{{Kind: StatementPayment, MerchantOrderID: orderNo, ProviderTransactionID: providerNo, AmountMinor: amount, Currency: currency, Status: StatusSucceeded, OccurredAt: occurred}}, nil
}

func validStatementCurrency(value string) bool {
	if len(value) != 3 {
		return false
	}
	for _, character := range value {
		if character < 'A' || character > 'Z' {
			return false
		}
	}
	return true
}

func successfulStatementStatus(value string) bool {
	value = strings.ToUpper(strings.TrimSpace(value))
	return value == "SUCCESS" || value == "SUCCEEDED" || value == "支付成功" || value == "交易成功" || value == "退款成功"
}

func refundedStatementStatus(value string) bool {
	value = strings.ToUpper(strings.TrimSpace(value))
	return value == "REFUND" || value == "REFUNDED" || value == "转入退款" || value == "退款"
}

type statementValues map[string]string

type statementIndex map[string]int

func statementHeaderIndex(header []string) statementIndex {
	result := make(statementIndex, len(header))
	for index, value := range header {
		result[normalizeStatementValue(value)] = index
	}
	return result
}

func (index statementIndex) has(key string) bool {
	_, ok := index[key]
	return ok
}

func statementRowValues(header, row []string) statementValues {
	result := make(statementValues, len(header))
	for index, key := range header {
		if index < len(row) {
			result[normalizeStatementValue(key)] = normalizeStatementValue(row[index])
		}
	}
	return result
}

func (values statementValues) get(key string) string { return values[key] }

func (values statementValues) getAny(keys ...string) string {
	for _, key := range keys {
		if value := values[key]; value != "" {
			return value
		}
	}
	return ""
}

func normalizeStatementValue(value string) string {
	value = strings.TrimPrefix(value, "\ufeff")
	value = strings.TrimSpace(value)
	value = strings.TrimPrefix(value, "`")
	return strings.TrimSpace(value)
}

func parsePositiveStatementAmount(value string, absolute bool) (int64, error) {
	value = normalizeStatementValue(value)
	if absolute {
		value = strings.TrimPrefix(value, "-")
	}
	amount, err := parseAmountMinor(value)
	if err != nil || amount <= 0 {
		return 0, ErrStatementMalformed
	}
	return amount, nil
}

func parseStatementTime(value string) (time.Time, error) {
	value = normalizeStatementValue(value)
	location := time.FixedZone("CST", 8*60*60)
	for _, layout := range []string{"2006-01-02 15:04:05", time.RFC3339} {
		parsed, err := time.ParseInLocation(layout, value, location)
		if err == nil {
			return parsed.UTC(), nil
		}
	}
	return time.Time{}, ErrStatementMalformed
}

func emptyStatementRow(row []string) bool {
	for _, value := range row {
		if normalizeStatementValue(value) != "" {
			return false
		}
	}
	return true
}

func statementFooter(row []string) bool {
	if len(row) == 0 {
		return false
	}
	first := normalizeStatementValue(row[0])
	return strings.HasPrefix(first, "总交易") || strings.HasPrefix(first, "合计") || strings.HasPrefix(first, "#")
}

func statementDate(request StatementRequest) (string, error) {
	if request.Date.IsZero() {
		return "", ErrInvalidRequest
	}
	location := time.FixedZone("CST", 8*60*60)
	return request.Date.In(location).Format("2006-01-02"), nil
}

// ValidateStatementPeriod verifies that a reconciliation period is non-empty
// and small enough to be processed with the bounded statement downloader.
func ValidateStatementPeriod(start, end time.Time) error {
	if start.IsZero() || end.IsZero() || !end.After(start) || end.Sub(start) > MaxStatementPeriodDays*24*time.Hour {
		return ErrInvalidRequest
	}
	return nil
}

func parseStatementInteger(value string) (int64, error) {
	parsed, err := strconv.ParseInt(normalizeStatementValue(value), 10, 64)
	if err != nil {
		return 0, fmt.Errorf("%w: integer", ErrStatementMalformed)
	}
	return parsed, nil
}

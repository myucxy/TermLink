package reconciliation

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/csv"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

var (
	reconciliationTestLocation = time.FixedZone("CST", 8*60*60)
	reconciliationTestKey      = []byte("0123456789abcdef0123456789abcdef")
)

type testLease struct {
	mu       sync.Mutex
	released bool
}

func (lease *testLease) Release() {
	lease.mu.Lock()
	lease.released = true
	lease.mu.Unlock()
}

type testRepository struct {
	mu sync.Mutex

	candidates    []store.CommerceReconciliationCandidate
	pages         []LocalPage
	listErr       error
	acquireErr    error
	acquired      bool
	lease         *testLease
	completeErr   error
	completed     int
	success       bool
	summary       []byte
	discrepancies []Discrepancy
	cursors       []LocalCursor
	periodStarts  []time.Time
	periodEnds    []time.Time
}

func (repository *testRepository) ListPendingReconciliationCandidates(_ context.Context, _ time.Time, limit int) ([]store.CommerceReconciliationCandidate, error) {
	if repository.listErr != nil {
		return nil, repository.listErr
	}
	result := append([]store.CommerceReconciliationCandidate(nil), repository.candidates...)
	return result, nil
}

func (repository *testRepository) TryAcquireReconciliationRun(context.Context, uuid.UUID) (RunLease, bool, error) {
	if repository.acquireErr != nil {
		return nil, false, repository.acquireErr
	}
	if !repository.acquired {
		return nil, false, nil
	}
	if repository.lease == nil {
		repository.lease = &testLease{}
	}
	return repository.lease, true, nil
}

func (repository *testRepository) ListReconciliationLocalEntries(_ context.Context, _ string, start, end time.Time, cursor LocalCursor, _ int) (LocalPage, error) {
	repository.mu.Lock()
	defer repository.mu.Unlock()
	repository.cursors = append(repository.cursors, cursor)
	repository.periodStarts = append(repository.periodStarts, start)
	repository.periodEnds = append(repository.periodEnds, end)
	if len(repository.pages) == 0 {
		return LocalPage{}, nil
	}
	page := repository.pages[0]
	repository.pages = repository.pages[1:]
	return page, nil
}

func (repository *testRepository) CompleteReconciliationRun(_ context.Context, _ uuid.UUID, success bool, summary []byte, discrepancies []Discrepancy) (store.ReconciliationRun, error) {
	repository.mu.Lock()
	defer repository.mu.Unlock()
	repository.completed++
	repository.success = success
	repository.summary = append([]byte(nil), summary...)
	repository.discrepancies = append([]Discrepancy(nil), discrepancies...)
	return store.ReconciliationRun{}, repository.completeErr
}

type testResolver struct {
	gateway payment.Gateway
	err     error
}

func (resolver testResolver) Resolve(context.Context, string, string) (payment.Gateway, error) {
	return resolver.gateway, resolver.err
}

type testGateway struct {
	provider  payment.Provider
	downloads map[string]payment.StatementDownload
	err       error
	calls     int
}

func (gateway *testGateway) Provider() payment.Provider { return gateway.provider }

func (gateway *testGateway) DownloadStatement(_ context.Context, request payment.StatementRequest) (payment.StatementDownload, error) {
	gateway.calls++
	if gateway.err != nil {
		return payment.StatementDownload{}, gateway.err
	}
	key := request.Date.In(reconciliationTestLocation).Format("2006-01-02")
	download, ok := gateway.downloads[key]
	if !ok {
		return payment.StatementDownload{}, payment.ErrStatementUnavailable
	}
	copy := download
	if body, ok := download.Body.(*reusableBody); ok {
		copy.Body = io.NopCloser(bytes.NewReader(body.data))
	}
	return copy, nil
}

func (*testGateway) CreatePayment(context.Context, payment.OrderRequest) (payment.PaymentInstruction, error) {
	return payment.PaymentInstruction{}, payment.ErrUnsupported
}

func (*testGateway) QueryPayment(context.Context, string) (payment.QueryResult, error) {
	return payment.QueryResult{}, payment.ErrUnsupported
}

func (*testGateway) HealthCheck(context.Context) error { return nil }

func (*testGateway) Refund(context.Context, payment.RefundRequest) (payment.RefundResult, error) {
	return payment.RefundResult{}, payment.ErrUnsupported
}

func (*testGateway) QueryRefund(context.Context, payment.RefundRequest) (payment.RefundResult, error) {
	return payment.RefundResult{}, payment.ErrUnsupported
}

func (*testGateway) VerifyWebhook(context.Context, http.Header, []byte) (payment.WebhookResult, error) {
	return payment.WebhookResult{}, payment.ErrUnsupported
}

type reusableBody struct{ data []byte }

func (body *reusableBody) Read(buffer []byte) (int, error) {
	return 0, errors.New("reusable test body must be cloned before reading")
}
func (*reusableBody) Close() error { return nil }

func TestProcessorBalancedMixedPaymentAndRefund(t *testing.T) {
	fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "业务类型", "完成时间", "订单金额（元）", "退款金额（元）", "退款批次号/请求号", "支付宝退款号", "交易状态"},
		{"trade-1", "ORDER_1", "交易", "2026-08-26 10:00:00", "12.34", "", "", "", "交易成功"},
		{"trade-2", "ORDER_2", "退款", "2026-08-26 11:00:00", "20.00", "5.67", "REFUND_2", "provider-refund-2", "退款成功"},
	})
	fixture.repository.pages = []LocalPage{{Entries: []LocalEntry{
		localPayment("ORDER_1", "trade-1", 1234, fixture.candidate.PeriodStart.Add(10*time.Hour)),
		localRefund("ORDER_2", "REFUND_2", "provider-refund-2", 567, fixture.candidate.PeriodStart.Add(11*time.Hour)),
	}}}

	result, err := fixture.processor.Process(context.Background(), fixture.candidate)
	if err != nil {
		t.Fatal(err)
	}
	if !result.Acquired || result.Summary.Result != "BALANCED" || !result.Summary.Balanced || result.Summary.MatchedEntries != 2 {
		t.Fatalf("unexpected result: %+v", result)
	}
	if fixture.repository.completed != 1 || !fixture.repository.success || fixture.gateway.calls != 1 {
		t.Fatalf("completion=%d success=%v calls=%d", fixture.repository.completed, fixture.repository.success, fixture.gateway.calls)
	}
	wantStart := time.Date(2026, 8, 25, 16, 0, 0, 0, time.UTC)
	wantEnd := time.Date(2026, 8, 26, 16, 0, 0, 0, time.UTC)
	if len(fixture.repository.periodStarts) != 1 || !fixture.repository.periodStarts[0].Equal(wantStart) || !fixture.repository.periodEnds[0].Equal(wantEnd) {
		t.Fatalf("local query window = [%v,%v), want [%v,%v)", fixture.repository.periodStarts, fixture.repository.periodEnds, wantStart, wantEnd)
	}
	if len(fixture.repository.discrepancies) != 0 {
		t.Fatalf("balanced run persisted discrepancies: %+v", fixture.repository.discrepancies)
	}
}

func TestProcessorReportsRedactedDifferenceSummary(t *testing.T) {
	fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"provider-secret-trade", "ORDER_AMOUNT_SECRET", "2026-08-26 10:00:00", "12.34"},
		{"provider-only-trade", "ORDER_PROVIDER_ONLY", "2026-08-26 11:00:00", "2.00"},
	})
	amountMismatch := localPayment("ORDER_AMOUNT_SECRET", "other-trade", 999, fixture.candidate.PeriodStart.Add(10*time.Hour))
	amountMismatch.Currency = "USD"
	fixture.repository.pages = []LocalPage{{Entries: []LocalEntry{
		amountMismatch,
		localPayment("ORDER_LOCAL_ONLY", "local-only-trade", 300, fixture.candidate.PeriodStart.Add(12*time.Hour)),
	}}}

	result, err := fixture.processor.Process(context.Background(), fixture.candidate)
	if err != nil {
		t.Fatal(err)
	}
	want := DifferenceCounts{ProviderOnly: 1, LocalOnly: 1, AmountMismatch: 1, CurrencyMismatch: 1, TransactionMismatch: 1}
	if result.Summary.Result != "DIFFERENCES" || result.Summary.Differences != want {
		t.Fatalf("differences = %+v, want %+v", result.Summary.Differences, want)
	}
	if len(fixture.repository.discrepancies) != want.Total() {
		t.Fatalf("persisted discrepancies = %d, want %d", len(fixture.repository.discrepancies), want.Total())
	}
	for _, discrepancy := range fixture.repository.discrepancies {
		if discrepancy.Key == [sha256.Size]byte{} || len(discrepancy.ExternalReferenceHMAC) != sha256.Size || discrepancy.Provider != string(payment.ProviderAlipay) {
			t.Fatalf("invalid actionable discrepancy: %+v", discrepancy)
		}
	}
	for _, secret := range []string{"ORDER_AMOUNT_SECRET", "ORDER_PROVIDER_ONLY", "ORDER_LOCAL_ONLY", "provider-secret-trade", "local-only-trade"} {
		if bytes.Contains(fixture.repository.summary, []byte(secret)) {
			t.Fatalf("persisted summary contains sensitive identifier %q: %s", secret, fixture.repository.summary)
		}
	}
	var parsed map[string]any
	if err := json.Unmarshal(fixture.repository.summary, &parsed); err != nil {
		t.Fatal(err)
	}
}

func TestProcessorPaginationAndExactDuplicates(t *testing.T) {
	fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
	})
	entry := localPayment("ORDER_1", "trade-1", 100, fixture.candidate.PeriodStart.Add(10*time.Hour))
	entry2 := entry
	entry2.RowID = uuid.New()
	next := LocalCursor{OccurredAt: entry.OccurredAt, Kind: entry.Kind, RowID: entry.RowID, Valid: true}
	fixture.processor.localPageSize = 1
	fixture.repository.pages = []LocalPage{{Entries: []LocalEntry{entry}, Next: next}, {Entries: []LocalEntry{entry2}}}

	result, err := fixture.processor.Process(context.Background(), fixture.candidate)
	if err != nil {
		t.Fatal(err)
	}
	if result.Summary.Differences.DuplicateProvider != 1 || result.Summary.Differences.DuplicateLocal != 1 || len(fixture.repository.cursors) != 2 || fixture.repository.cursors[1] != next {
		t.Fatalf("summary=%+v cursors=%+v", result.Summary, fixture.repository.cursors)
	}
}

func TestProcessorFailsClosedForProviderAndStatementFailures(t *testing.T) {
	tests := []struct {
		name        string
		mutate      func(*processorFixture)
		failureCode string
	}{
		{
			name: "no gateway", mutate: func(fixture *processorFixture) {
				fixture.processor.gateways = testResolver{err: errors.New("not configured")}
			},
			failureCode: "CHANNEL_UNAVAILABLE",
		},
		{
			name: "download", mutate: func(fixture *processorFixture) { fixture.gateway.err = payment.ErrStatementUnavailable },
			failureCode: "STATEMENT_UNAVAILABLE",
		},
		{
			name: "provider mismatch", mutate: func(fixture *processorFixture) {
				download := fixture.gateway.downloads["2026-08-26"]
				download.Provider = payment.ProviderWeChatPay
				fixture.gateway.downloads["2026-08-26"] = download
			}, failureCode: "PROVIDER_MISMATCH",
		},
		{
			name: "date mismatch", mutate: func(fixture *processorFixture) {
				download := fixture.gateway.downloads["2026-08-26"]
				download.Date = download.Date.AddDate(0, 0, 1)
				fixture.gateway.downloads["2026-08-26"] = download
			}, failureCode: "STATEMENT_DATE_MISMATCH",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
				{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
				{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
			})
			test.mutate(fixture)
			result, err := fixture.processor.Process(context.Background(), fixture.candidate)
			if err == nil || result.Summary.Result != "FAILED" || result.Summary.FailureCode != test.failureCode {
				t.Fatalf("result=%+v err=%v", result, err)
			}
			if fixture.repository.completed != 1 || fixture.repository.success {
				t.Fatalf("failed task was not persisted fail-closed")
			}
		})
	}
}

func TestProcessorLockAndConflictingDuplicates(t *testing.T) {
	fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
	})
	fixture.repository.acquired = false
	result, err := fixture.processor.Process(context.Background(), fixture.candidate)
	if err != nil || result.Acquired || fixture.gateway.calls != 0 || fixture.repository.completed != 0 {
		t.Fatalf("lock skip result=%+v err=%v", result, err)
	}

	fixture = newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
		{"trade-2", "ORDER_1", "2026-08-26 10:00:00", "2.00"},
	})
	result, err = fixture.processor.Process(context.Background(), fixture.candidate)
	if !errors.Is(err, ErrProviderEntryConflict) || result.Summary.FailureCode != "STATEMENT_INVALID" || fixture.repository.success {
		t.Fatalf("conflict result=%+v err=%v", result, err)
	}
}

func TestProcessorFailsClosedWhenActionableDifferenceLimitIsExceeded(t *testing.T) {
	fixture := newProcessorFixture(t, payment.ProviderAlipay, [][]string{
		{"支付宝交易号", "商户订单号", "完成时间", "订单金额（元）"},
		{"trade-1", "ORDER_1", "2026-08-26 10:00:00", "1.00"},
		{"trade-2", "ORDER_2", "2026-08-26 11:00:00", "2.00"},
	})
	fixture.processor.maxDiscrepancies = 1
	result, err := fixture.processor.Process(context.Background(), fixture.candidate)
	if !errors.Is(err, ErrEntryLimit) || result.Summary.Result != "FAILED" || result.Summary.FailureCode != "ENTRY_LIMIT_EXCEEDED" {
		t.Fatalf("result=%+v err=%v", result, err)
	}
	if fixture.repository.success || len(fixture.repository.discrepancies) != 0 {
		t.Fatalf("overflow persisted partial discrepancies: %+v", fixture.repository.discrepancies)
	}
}

type processorFixture struct {
	candidate  store.CommerceReconciliationCandidate
	repository *testRepository
	gateway    *testGateway
	processor  *Processor
}

func newProcessorFixture(t *testing.T, provider payment.Provider, rows [][]string) *processorFixture {
	t.Helper()
	start := time.Date(2026, 8, 26, 0, 0, 0, 0, reconciliationTestLocation)
	periodStart := time.Date(2026, 8, 26, 0, 0, 0, 0, time.UTC)
	body := reconciliationCSV(t, rows)
	download := payment.StatementDownload{
		Provider: provider, Date: start, Format: payment.StatementFormatCSV,
		Body: &reusableBody{data: body}, Size: int64(len(body)),
	}
	if provider == payment.ProviderWeChatPay {
		hash := sha256.Sum256(body)
		download.HashType = "SHA256"
		download.HashHex = stringHex(hash[:])
	}
	candidate := store.CommerceReconciliationCandidate{
		RunID: uuid.New(), Provider: string(provider), PeriodStart: periodStart, PeriodEnd: periodStart.AddDate(0, 0, 1), CreatedAt: periodStart.AddDate(0, 0, 2),
	}
	repository := &testRepository{acquired: true, pages: []LocalPage{{}}}
	gateway := &testGateway{provider: provider, downloads: map[string]payment.StatementDownload{"2026-08-26": download}}
	processor, err := NewProcessor(repository, testResolver{gateway: gateway}, ProcessorOptions{DigestKey: reconciliationTestKey, Clock: func() time.Time { return candidate.CreatedAt }})
	if err != nil {
		t.Fatal(err)
	}
	return &processorFixture{candidate: candidate, repository: repository, gateway: gateway, processor: processor}
}

func localPayment(order, providerTrade string, amount int64, occurred time.Time) LocalEntry {
	orderID, transactionID := uuid.New(), uuid.New()
	return LocalEntry{
		RowID: transactionID, OrderID: orderID, TransactionID: transactionID, Kind: payment.StatementPayment, OrderNo: order,
		ReferenceHash: reconciliationDigest("provider-transaction", providerTrade), AmountMinor: amount,
		Currency: payment.CurrencyCNY, Status: payment.StatusSucceeded, OccurredAt: occurred.UTC(),
	}
}

func localRefund(order, refund, providerRefund string, amount int64, occurred time.Time) LocalEntry {
	orderID, transactionID := uuid.New(), uuid.New()
	return LocalEntry{
		RowID: uuid.New(), OrderID: orderID, TransactionID: transactionID, Kind: payment.StatementRefund, OrderNo: order, RefundNo: refund,
		ReferenceHash: reconciliationDigest("provider-refund", providerRefund), AmountMinor: amount,
		Currency: payment.CurrencyCNY, Status: payment.StatusRefunded, OccurredAt: occurred.UTC(),
	}
}

func reconciliationDigest(purpose, value string) []byte {
	hash := hmac.New(sha256.New, reconciliationTestKey)
	_, _ = hash.Write([]byte("termlink-commerce-v1\x00" + purpose + "\x00" + value))
	return hash.Sum(nil)
}

func reconciliationCSV(t *testing.T, rows [][]string) []byte {
	t.Helper()
	var buffer bytes.Buffer
	writer := csv.NewWriter(&buffer)
	writer.WriteAll(rows)
	writer.Flush()
	if err := writer.Error(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}

func stringHex(value []byte) string {
	const alphabet = "0123456789abcdef"
	var builder strings.Builder
	builder.Grow(len(value) * 2)
	for _, item := range value {
		builder.WriteByte(alphabet[item>>4])
		builder.WriteByte(alphabet[item&0x0f])
	}
	return builder.String()
}

// Package reconciliation downloads authoritative provider statements and
// compares them with bounded local payment/refund snapshots. Only redacted
// aggregate differences are persisted.
package reconciliation

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/json"
	"errors"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

const (
	DefaultLocalPageSize        = 500
	DefaultMaxEntries           = 200_000
	DefaultMaxDiscrepancies     = 50_000
	reconciliationSummarySchema = 1
)

var (
	ErrInvalidCandidate      = errors.New("reconciliation: invalid candidate")
	ErrGatewayUnavailable    = errors.New("reconciliation: payment gateway unavailable")
	ErrEntryLimit            = errors.New("reconciliation: entry limit exceeded")
	ErrLocalEntryMalformed   = errors.New("reconciliation: malformed local entry")
	ErrProviderEntryConflict = errors.New("reconciliation: conflicting provider entry")
)

type GatewayResolver interface {
	Resolve(context.Context, string, string) (payment.Gateway, error)
}

type ProcessorOptions struct {
	DigestKey        []byte
	StatementLimits  payment.StatementLimits
	LocalPageSize    int
	MaxEntries       int
	MaxDiscrepancies int
	Clock            func() time.Time
}

type Processor struct {
	repository       Repository
	gateways         GatewayResolver
	digestKey        []byte
	statementLimits  payment.StatementLimits
	localPageSize    int
	maxEntries       int
	maxDiscrepancies int
	clock            func() time.Time
}

func NewProcessor(repository Repository, gateways GatewayResolver, options ProcessorOptions) (*Processor, error) {
	if repository == nil || gateways == nil || len(options.DigestKey) < 32 {
		return nil, errors.New("reconciliation: repository, gateway resolver, and digest key are required")
	}
	if options.LocalPageSize < 1 || options.LocalPageSize > 1000 {
		options.LocalPageSize = DefaultLocalPageSize
	}
	if options.MaxEntries < 1 || options.MaxEntries > DefaultMaxEntries {
		options.MaxEntries = DefaultMaxEntries
	}
	if options.MaxDiscrepancies < 1 || options.MaxDiscrepancies > DefaultMaxEntries {
		options.MaxDiscrepancies = DefaultMaxDiscrepancies
	}
	if options.Clock == nil {
		options.Clock = time.Now
	}
	return &Processor{
		repository: repository, gateways: gateways, digestKey: append([]byte(nil), options.DigestKey...),
		statementLimits: options.StatementLimits, localPageSize: options.LocalPageSize,
		maxEntries: options.MaxEntries, maxDiscrepancies: options.MaxDiscrepancies, clock: options.Clock,
	}, nil
}

type DifferenceCounts struct {
	ProviderOnly        int `json:"provider_only"`
	LocalOnly           int `json:"local_only"`
	AmountMismatch      int `json:"amount_mismatch"`
	CurrencyMismatch    int `json:"currency_mismatch"`
	StatusMismatch      int `json:"status_mismatch"`
	TransactionMismatch int `json:"transaction_mismatch"`
	DuplicateProvider   int `json:"duplicate_provider"`
	DuplicateLocal      int `json:"duplicate_local"`
}

func (counts DifferenceCounts) Total() int {
	return counts.ProviderOnly + counts.LocalOnly + counts.AmountMismatch + counts.CurrencyMismatch +
		counts.StatusMismatch + counts.TransactionMismatch + counts.DuplicateProvider + counts.DuplicateLocal
}

type Summary struct {
	SchemaVersion   int              `json:"schema_version"`
	Provider        string           `json:"provider"`
	PeriodStart     time.Time        `json:"period_start"`
	PeriodEnd       time.Time        `json:"period_end"`
	Result          string           `json:"result"`
	Balanced        bool             `json:"balanced"`
	ProviderEntries int              `json:"provider_entries"`
	LocalEntries    int              `json:"local_entries"`
	MatchedEntries  int              `json:"matched_entries"`
	Differences     DifferenceCounts `json:"differences"`
	CompletedAt     time.Time        `json:"completed_at"`
	FailureCode     string           `json:"failure_code,omitempty"`
}

type ProcessResult struct {
	Acquired bool
	Summary  Summary
}

func (processor *Processor) Process(ctx context.Context, candidate store.CommerceReconciliationCandidate) (ProcessResult, error) {
	if candidate.RunID == [16]byte{} {
		return ProcessResult{}, ErrInvalidCandidate
	}
	lease, acquired, err := processor.repository.TryAcquireReconciliationRun(ctx, candidate.RunID)
	if err != nil || !acquired {
		return ProcessResult{Acquired: acquired}, err
	}
	if lease == nil {
		return ProcessResult{Acquired: true}, ErrInvalidCandidate
	}
	defer lease.Release()

	summary := Summary{
		SchemaVersion: reconciliationSummarySchema, Provider: candidate.Provider,
		PeriodStart: candidate.PeriodStart.UTC(), PeriodEnd: candidate.PeriodEnd.UTC(), CompletedAt: processor.clock().UTC(),
	}
	if err := validateCandidate(candidate); err != nil {
		return processor.failRun(ctx, candidate, summary, "INVALID_CANDIDATE", err)
	}
	periodStart, periodEnd := statementPeriodBounds(candidate)
	statementDownloader, err := processor.resolveDownloader(ctx, candidate.Provider)
	if err != nil {
		return processor.failRun(ctx, candidate, summary, "CHANNEL_UNAVAILABLE", err)
	}
	localEntries, duplicateLocals, err := processor.loadLocalEntries(ctx, candidate, periodStart, periodEnd)
	if err != nil {
		return processor.failRun(ctx, candidate, summary, failureCode(err), err)
	}
	summary.LocalEntries = len(localEntries)
	summary.Differences.DuplicateLocal = len(duplicateLocals)
	providerEntries := make(map[string]payment.StatementRecord)
	var duplicateProviders []payment.StatementRecord
	providerSeen := 0
	location := periodStart.Location()
	for day := periodStart; day.Before(periodEnd); day = day.AddDate(0, 0, 1) {
		download, downloadErr := statementDownloader.DownloadStatement(ctx, payment.StatementRequest{Date: day})
		if downloadErr != nil {
			return processor.failRun(ctx, candidate, summary, failureCode(downloadErr), downloadErr)
		}
		if download.Provider != payment.Provider(candidate.Provider) {
			if download.Body != nil {
				download.Body.Close()
			}
			return processor.failRun(ctx, candidate, summary, "PROVIDER_MISMATCH", ErrProviderEntryConflict)
		}
		if download.Date.IsZero() || !midnight(download.Date.In(location)).Equal(day) {
			if download.Body != nil {
				download.Body.Close()
			}
			return processor.failRun(ctx, candidate, summary, "STATEMENT_DATE_MISMATCH", payment.ErrStatementIntegrity)
		}
		parseErr := payment.ParseStatement(download, processor.statementLimits, func(entry payment.StatementRecord) error {
			providerSeen++
			if providerSeen > processor.maxEntries {
				return ErrEntryLimit
			}
			entryLocalDate := midnight(entry.OccurredAt.In(location))
			if !entryLocalDate.Equal(day) {
				return payment.ErrStatementIntegrity
			}
			if entry.OccurredAt.Before(periodStart) || !entry.OccurredAt.Before(periodEnd) {
				return nil
			}
			if err := validateProviderEntry(candidate.Provider, entry); err != nil {
				return err
			}
			key := reconciliationKey(entry.Kind, entry.MerchantOrderID, entry.MerchantRefundID)
			if existing, exists := providerEntries[key]; exists {
				if !sameProviderEntry(existing, entry) {
					return ErrProviderEntryConflict
				}
				duplicateProviders = append(duplicateProviders, entry)
				return nil
			}
			providerEntries[key] = entry
			return nil
		})
		if parseErr != nil {
			return processor.failRun(ctx, candidate, summary, failureCode(parseErr), parseErr)
		}
	}

	summary.ProviderEntries = len(providerEntries)
	summary.Differences.DuplicateProvider = len(duplicateProviders)
	discrepancies, err := processor.compare(candidate.Provider, localEntries, providerEntries, duplicateLocals, duplicateProviders, &summary)
	if err != nil {
		return processor.failRun(ctx, candidate, summary, failureCode(err), err)
	}
	summary.Balanced = summary.Differences.Total() == 0
	if summary.Balanced {
		summary.Result = "BALANCED"
	} else {
		summary.Result = "DIFFERENCES"
	}
	payload, err := json.Marshal(summary)
	if err != nil {
		return ProcessResult{Acquired: true, Summary: summary}, err
	}
	if _, err := processor.repository.CompleteReconciliationRun(ctx, candidate.RunID, true, payload, discrepancies); err != nil {
		return ProcessResult{Acquired: true, Summary: summary}, err
	}
	return ProcessResult{Acquired: true, Summary: summary}, nil
}

func (processor *Processor) loadLocalEntries(ctx context.Context, candidate store.CommerceReconciliationCandidate, periodStart, periodEnd time.Time) (map[string]LocalEntry, []LocalEntry, error) {
	entries := make(map[string]LocalEntry)
	cursor := LocalCursor{}
	var duplicates []LocalEntry
	seen := 0
	for {
		page, err := processor.repository.ListReconciliationLocalEntries(ctx, candidate.Provider, periodStart, periodEnd, cursor, processor.localPageSize)
		if err != nil {
			return nil, duplicates, err
		}
		if len(page.Entries) > processor.localPageSize {
			return nil, duplicates, ErrEntryLimit
		}
		for _, entry := range page.Entries {
			seen++
			if seen > processor.maxEntries {
				return nil, duplicates, ErrEntryLimit
			}
			if err := validateLocalEntry(candidate.Provider, periodStart, periodEnd, entry); err != nil {
				return nil, duplicates, err
			}
			key := reconciliationKey(entry.Kind, entry.OrderNo, entry.RefundNo)
			if existing, exists := entries[key]; exists {
				if !sameLocalEntry(existing, entry) {
					return nil, duplicates, ErrLocalEntryMalformed
				}
				duplicates = append(duplicates, entry)
				continue
			}
			entries[key] = entry
		}
		if !page.Next.Valid {
			break
		}
		if len(page.Entries) == 0 || (cursor.Valid && !cursorBefore(cursor, page.Next)) {
			return nil, duplicates, ErrLocalEntryMalformed
		}
		cursor = page.Next
	}
	return entries, duplicates, nil
}

func (processor *Processor) compare(providerName string, local map[string]LocalEntry, provider map[string]payment.StatementRecord, duplicateLocals []LocalEntry, duplicateProviders []payment.StatementRecord, summary *Summary) ([]Discrepancy, error) {
	capacity := summary.Differences.Total() + len(local) + len(provider)
	if capacity > processor.maxDiscrepancies {
		capacity = processor.maxDiscrepancies
	}
	discrepancies := make([]Discrepancy, 0, capacity)
	appendDiscrepancy := func(discrepancy Discrepancy) error {
		if len(discrepancies) >= processor.maxDiscrepancies {
			return ErrEntryLimit
		}
		discrepancies = append(discrepancies, discrepancy)
		return nil
	}
	for index := range duplicateLocals {
		if err := appendDiscrepancy(processor.discrepancy(providerName, "DUPLICATE_LOCAL_REFERENCE", "DUPLICATE_LOCAL_REFERENCE", &duplicateLocals[index], nil)); err != nil {
			return nil, err
		}
	}
	for index := range duplicateProviders {
		if err := appendDiscrepancy(processor.discrepancy(providerName, "DUPLICATE_PROVIDER_REFERENCE", "DUPLICATE_PROVIDER_REFERENCE", nil, &duplicateProviders[index])); err != nil {
			return nil, err
		}
	}
	for key, providerEntry := range provider {
		localEntry, exists := local[key]
		if !exists {
			summary.Differences.ProviderOnly++
			if err := appendDiscrepancy(processor.discrepancy(providerName, "MISSING_LOCAL", "PROVIDER_ENTRY_WITHOUT_LOCAL_RECORD", nil, &providerEntry)); err != nil {
				return nil, err
			}
			continue
		}
		delete(local, key)
		summary.MatchedEntries++
		if providerEntry.AmountMinor != localEntry.AmountMinor {
			summary.Differences.AmountMismatch++
			if err := appendDiscrepancy(processor.discrepancy(providerName, "AMOUNT_MISMATCH", "AMOUNT_MISMATCH", &localEntry, &providerEntry)); err != nil {
				return nil, err
			}
		}
		if providerEntry.Currency != localEntry.Currency {
			summary.Differences.CurrencyMismatch++
			if err := appendDiscrepancy(processor.discrepancy(providerName, "CURRENCY_MISMATCH", "CURRENCY_MISMATCH", &localEntry, &providerEntry)); err != nil {
				return nil, err
			}
		}
		if providerEntry.Status != localEntry.Status {
			summary.Differences.StatusMismatch++
			if err := appendDiscrepancy(processor.discrepancy(providerName, "STATUS_MISMATCH", "STATUS_MISMATCH", &localEntry, &providerEntry)); err != nil {
				return nil, err
			}
		}
		if providerEntry.Kind == payment.StatementPayment {
			digest := processor.providerReferenceDigest("provider-transaction", providerEntry.ProviderTransactionID)
			if subtle.ConstantTimeCompare(localEntry.ReferenceHash, digest[:]) != 1 {
				summary.Differences.TransactionMismatch++
				if err := appendDiscrepancy(processor.discrepancy(providerName, "OTHER", "PAYMENT_REFERENCE_MISMATCH", &localEntry, &providerEntry)); err != nil {
					return nil, err
				}
			}
		} else {
			providerReference := providerEntry.ProviderRefundID
			if providerReference == "" {
				providerReference = providerEntry.MerchantRefundID
			}
			digest := processor.providerReferenceDigest("provider-refund", providerReference)
			if subtle.ConstantTimeCompare(localEntry.ReferenceHash, digest[:]) != 1 {
				summary.Differences.TransactionMismatch++
				if err := appendDiscrepancy(processor.discrepancy(providerName, "REFUND_MISMATCH", "REFUND_REFERENCE_MISMATCH", &localEntry, &providerEntry)); err != nil {
					return nil, err
				}
			}
		}
	}
	for _, localEntry := range local {
		summary.Differences.LocalOnly++
		if err := appendDiscrepancy(processor.discrepancy(providerName, "MISSING_PROVIDER", "LOCAL_RECORD_WITHOUT_PROVIDER_ENTRY", &localEntry, nil)); err != nil {
			return nil, err
		}
	}
	return discrepancies, nil
}

func (processor *Processor) failRun(ctx context.Context, candidate store.CommerceReconciliationCandidate, summary Summary, code string, cause error) (ProcessResult, error) {
	summary.Result = "FAILED"
	summary.Balanced = false
	summary.FailureCode = code
	summary.CompletedAt = processor.clock().UTC()
	payload, marshalErr := json.Marshal(summary)
	if marshalErr != nil {
		return ProcessResult{Acquired: true, Summary: summary}, errors.Join(cause, marshalErr)
	}
	_, completeErr := processor.repository.CompleteReconciliationRun(ctx, candidate.RunID, false, payload, nil)
	return ProcessResult{Acquired: true, Summary: summary}, errors.Join(cause, completeErr)
}

func (processor *Processor) resolveDownloader(ctx context.Context, provider string) (payment.StatementDownloader, error) {
	scenes := []string{string(payment.FlowNative)}
	if provider == string(payment.ProviderAlipay) {
		scenes = append(scenes, string(payment.FlowWeb))
	}
	for _, scene := range scenes {
		gateway, err := processor.gateways.Resolve(ctx, provider, scene)
		if err != nil {
			continue
		}
		downloader, ok := gateway.(payment.StatementDownloader)
		if ok && downloader.Provider() == payment.Provider(provider) {
			return downloader, nil
		}
	}
	return nil, ErrGatewayUnavailable
}

func (processor *Processor) providerReferenceDigest(purpose, value string) [32]byte {
	hash := hmac.New(sha256.New, processor.digestKey)
	_, _ = hash.Write([]byte("termlink-commerce-v1\x00" + purpose + "\x00" + value))
	var digest [32]byte
	copy(digest[:], hash.Sum(nil))
	return digest
}

func (processor *Processor) discrepancy(provider, category, summaryCode string, local *LocalEntry, remote *payment.StatementRecord) Discrepancy {
	result := Discrepancy{Provider: provider, Category: category, SummaryCode: summaryCode}
	var kind payment.StatementKind
	if local != nil {
		kind = local.Kind
		orderID, transactionID := local.OrderID, local.TransactionID
		if orderID != uuid.Nil {
			result.InternalOrderID = &orderID
		}
		if transactionID != uuid.Nil {
			result.InternalTransactionID = &transactionID
		}
		result.ExternalReferenceHMAC = append([]byte(nil), local.ReferenceHash...)
		status, currency, amount := string(local.Status), local.Currency, local.AmountMinor
		result.LocalStatus, result.LocalCurrency, result.LocalAmountMinor = &status, &currency, &amount
	}
	if remote != nil {
		kind = remote.Kind
		result.ExternalReferenceHMAC = processor.statementReferenceHMAC(*remote)
		status, currency, amount := string(remote.Status), remote.Currency, remote.AmountMinor
		result.ProviderStatus, result.ProviderCurrency, result.ProviderAmountMinor = &status, &currency, &amount
	}
	key := hmac.New(sha256.New, processor.digestKey)
	writeDiscrepancyField(key, "termlink-reconciliation-v1")
	writeDiscrepancyField(key, provider)
	writeDiscrepancyField(key, category)
	writeDiscrepancyField(key, string(kind))
	writeDiscrepancyField(key, summaryCode)
	if result.InternalOrderID != nil {
		writeDiscrepancyBytes(key, result.InternalOrderID[:])
	} else {
		writeDiscrepancyBytes(key, nil)
	}
	if result.InternalTransactionID != nil {
		writeDiscrepancyBytes(key, result.InternalTransactionID[:])
	} else {
		writeDiscrepancyBytes(key, nil)
	}
	writeDiscrepancyBytes(key, result.ExternalReferenceHMAC)
	copy(result.Key[:], key.Sum(nil))
	return result
}

func (processor *Processor) statementReferenceHMAC(entry payment.StatementRecord) []byte {
	purpose, reference := "provider-transaction", entry.ProviderTransactionID
	if entry.Kind == payment.StatementRefund {
		purpose, reference = "provider-refund", entry.ProviderRefundID
		if reference == "" {
			reference = entry.MerchantRefundID
		}
	}
	digest := processor.providerReferenceDigest(purpose, reference)
	return append([]byte(nil), digest[:]...)
}

func writeDiscrepancyField(hash interface{ Write([]byte) (int, error) }, value string) {
	writeDiscrepancyBytes(hash, []byte(value))
}

func writeDiscrepancyBytes(hash interface{ Write([]byte) (int, error) }, value []byte) {
	var size [4]byte
	binary.BigEndian.PutUint32(size[:], uint32(len(value)))
	_, _ = hash.Write(size[:])
	_, _ = hash.Write(value)
}

func validateCandidate(candidate store.CommerceReconciliationCandidate) error {
	start, end := statementPeriodBounds(candidate)
	if candidate.RunID == [16]byte{} || (candidate.Provider != string(payment.ProviderAlipay) && candidate.Provider != string(payment.ProviderWeChatPay)) ||
		payment.ValidateStatementPeriod(start, end) != nil {
		return ErrInvalidCandidate
	}
	return nil
}

func statementPeriodBounds(candidate store.CommerceReconciliationCandidate) (time.Time, time.Time) {
	location := time.FixedZone("CST", 8*60*60)
	startYear, startMonth, startDay := candidate.PeriodStart.Date()
	endYear, endMonth, endDay := candidate.PeriodEnd.Date()
	return time.Date(startYear, startMonth, startDay, 0, 0, 0, 0, location),
		time.Date(endYear, endMonth, endDay, 0, 0, 0, 0, location)
}

func validateProviderEntry(provider string, entry payment.StatementRecord) error {
	if !validCurrency(entry.Currency) || entry.AmountMinor <= 0 || entry.MerchantOrderID == "" || entry.ProviderTransactionID == "" ||
		(entry.Kind != payment.StatementPayment && entry.Kind != payment.StatementRefund) || entry.OccurredAt.IsZero() {
		return payment.ErrStatementMalformed
	}
	if entry.Kind == payment.StatementPayment && entry.Status != payment.StatusSucceeded {
		return payment.ErrStatementMalformed
	}
	if entry.Kind == payment.StatementRefund && (entry.MerchantRefundID == "" || entry.Status != payment.StatusRefunded) {
		return payment.ErrStatementMalformed
	}
	_ = provider
	return nil
}

func validateLocalEntry(provider string, start, end time.Time, entry LocalEntry) error {
	if entry.RowID == [16]byte{} || entry.OrderID == [16]byte{} || entry.TransactionID == [16]byte{} || entry.OrderNo == "" || entry.AmountMinor <= 0 || !validCurrency(entry.Currency) ||
		entry.OccurredAt.Before(start) || !entry.OccurredAt.Before(end) || len(entry.ReferenceHash) != sha256.Size {
		return ErrLocalEntryMalformed
	}
	switch entry.Kind {
	case payment.StatementPayment:
		if entry.Status != payment.StatusSucceeded || len(entry.ReferenceHash) != sha256.Size {
			return ErrLocalEntryMalformed
		}
	case payment.StatementRefund:
		if entry.RefundNo == "" || entry.Status != payment.StatusRefunded {
			return ErrLocalEntryMalformed
		}
	default:
		return ErrLocalEntryMalformed
	}
	_ = provider
	return nil
}

func validCurrency(value string) bool {
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

func reconciliationKey(kind payment.StatementKind, orderNo, refundNo string) string {
	return string(kind) + "\x00" + orderNo + "\x00" + refundNo
}

func sameProviderEntry(left, right payment.StatementRecord) bool {
	return left.Kind == right.Kind && left.MerchantOrderID == right.MerchantOrderID && left.MerchantRefundID == right.MerchantRefundID &&
		left.ProviderTransactionID == right.ProviderTransactionID && left.ProviderRefundID == right.ProviderRefundID &&
		left.AmountMinor == right.AmountMinor && left.Currency == right.Currency && left.Status == right.Status && left.OccurredAt.Equal(right.OccurredAt)
}

func sameLocalEntry(left, right LocalEntry) bool {
	return left.Kind == right.Kind && left.OrderNo == right.OrderNo && left.RefundNo == right.RefundNo &&
		subtle.ConstantTimeCompare(left.ReferenceHash, right.ReferenceHash) == 1 && left.AmountMinor == right.AmountMinor &&
		left.Currency == right.Currency && left.Status == right.Status && left.OccurredAt.Equal(right.OccurredAt)
}

func cursorBefore(left, right LocalCursor) bool {
	if left.OccurredAt.Before(right.OccurredAt) {
		return true
	}
	if left.OccurredAt.After(right.OccurredAt) {
		return false
	}
	if left.Kind < right.Kind {
		return true
	}
	if left.Kind > right.Kind {
		return false
	}
	return string(left.RowID[:]) < string(right.RowID[:])
}

func midnight(value time.Time) time.Time {
	year, month, day := value.Date()
	return time.Date(year, month, day, 0, 0, 0, 0, value.Location())
}

func failureCode(err error) string {
	switch {
	case errors.Is(err, payment.ErrStatementUnavailable):
		return "STATEMENT_UNAVAILABLE"
	case errors.Is(err, payment.ErrStatementLimit), errors.Is(err, ErrEntryLimit):
		return "ENTRY_LIMIT_EXCEEDED"
	case errors.Is(err, payment.ErrStatementIntegrity):
		return "STATEMENT_INTEGRITY_FAILED"
	case errors.Is(err, payment.ErrStatementMalformed), errors.Is(err, ErrProviderEntryConflict), errors.Is(err, ErrLocalEntryMalformed):
		return "STATEMENT_INVALID"
	default:
		return "RECONCILIATION_FAILED"
	}
}

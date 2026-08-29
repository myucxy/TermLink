package reconciliation

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/json"
	"errors"
	"sort"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/internal/payment"
	"github.com/termlink/termlink/server/internal/store"
)

type LocalEntry struct {
	RowID         uuid.UUID
	OrderID       uuid.UUID
	TransactionID uuid.UUID
	Kind          payment.StatementKind
	OrderNo       string
	RefundNo      string
	ReferenceHash []byte
	AmountMinor   int64
	Currency      string
	Status        payment.PaymentStatus
	OccurredAt    time.Time
}

type LocalCursor struct {
	OccurredAt time.Time
	Kind       payment.StatementKind
	RowID      uuid.UUID
	Valid      bool
}

type LocalPage struct {
	Entries []LocalEntry
	Next    LocalCursor
}

type Discrepancy struct {
	Key                   [sha256.Size]byte
	Provider              string
	Category              string
	InternalOrderID       *uuid.UUID
	InternalTransactionID *uuid.UUID
	ExternalReferenceHMAC []byte
	LocalStatus           *string
	ProviderStatus        *string
	SummaryCode           string
	LocalAmountMinor      *int64
	ProviderAmountMinor   *int64
	LocalCurrency         *string
	ProviderCurrency      *string
}

type RunLease interface {
	Release()
}

type Repository interface {
	ListPendingReconciliationCandidates(context.Context, time.Time, int) ([]store.CommerceReconciliationCandidate, error)
	TryAcquireReconciliationRun(context.Context, uuid.UUID) (RunLease, bool, error)
	ListReconciliationLocalEntries(context.Context, string, time.Time, time.Time, LocalCursor, int) (LocalPage, error)
	CompleteReconciliationRun(context.Context, uuid.UUID, bool, []byte, []Discrepancy) (store.ReconciliationRun, error)
}

const maxPersistedDiscrepancies = DefaultMaxEntries

// PostgresRepository adapts the existing store without expanding its shared
// commercial mutation file. Session advisory locks provide crash-safe,
// multi-instance exclusion while leaving PENDING state retryable if a worker
// exits before completion.
type PostgresRepository struct {
	store *store.Postgres
	pool  *pgxpool.Pool
}

func NewPostgresRepository(repository *store.Postgres) (*PostgresRepository, error) {
	if repository == nil || repository.Pool == nil {
		return nil, errors.New("reconciliation: postgres repository unavailable")
	}
	return &PostgresRepository{store: repository, pool: repository.Pool}, nil
}

func (repository *PostgresRepository) ListPendingReconciliationCandidates(ctx context.Context, before time.Time, limit int) ([]store.CommerceReconciliationCandidate, error) {
	return repository.store.ListPendingReconciliationCandidates(ctx, before, limit)
}

type postgresRunLease struct {
	connection *pgxpool.Conn
	lockKey    int64
}

func (lease *postgresRunLease) Release() {
	if lease == nil || lease.connection == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	_, _ = lease.connection.Exec(ctx, `SELECT pg_advisory_unlock($1)`, lease.lockKey)
	lease.connection.Release()
	lease.connection = nil
}

func (repository *PostgresRepository) TryAcquireReconciliationRun(ctx context.Context, runID uuid.UUID) (RunLease, bool, error) {
	if runID == uuid.Nil {
		return nil, false, errors.New("reconciliation: invalid run")
	}
	connection, err := repository.pool.Acquire(ctx)
	if err != nil {
		return nil, false, err
	}
	lockKey := reconciliationLockKey(runID)
	lease := &postgresRunLease{connection: connection, lockKey: lockKey}
	var acquired bool
	if err := connection.QueryRow(ctx, `SELECT pg_try_advisory_lock($1)`, lockKey).Scan(&acquired); err != nil {
		connection.Release()
		return nil, false, err
	}
	if !acquired {
		connection.Release()
		return nil, false, nil
	}
	var status string
	if err := connection.QueryRow(ctx, `SELECT status FROM reconciliation_runs WHERE id=$1`, runID).Scan(&status); err != nil {
		lease.Release()
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, false, store.ErrNotFound
		}
		return nil, false, err
	}
	if status != "PENDING" {
		lease.Release()
		return nil, false, nil
	}
	return lease, true, nil
}

func reconciliationLockKey(runID uuid.UUID) int64 {
	digest := sha256.Sum256(append([]byte("termlink-reconciliation-lock-v1\x00"), runID[:]...))
	return int64(binary.BigEndian.Uint64(digest[:8]))
}

const localEntriesSQL = `
SELECT row_id,order_id,transaction_id,kind,order_no,refund_no,reference_hash,amount_minor,currency,status,occurred_at
FROM (
	SELECT t.id AS row_id,o.id AS order_id,t.id AS transaction_id,'PAYMENT'::text AS kind,o.order_no,''::text AS refund_no,
         t.provider_trade_no_hash AS reference_hash,t.amount_minor,t.currency,
         'SUCCEEDED'::text AS status,t.paid_at AS occurred_at
  FROM payment_transactions t
  JOIN orders o ON o.id=t.order_id
  WHERE t.provider=$1 AND t.status IN ('SUCCEEDED','REFUNDED')
    AND t.paid_at >= $2 AND t.paid_at < $3
  UNION ALL
	SELECT r.id AS row_id,o.id AS order_id,r.transaction_id,'REFUND'::text AS kind,o.order_no,r.refund_no,
         r.provider_ref_hash AS reference_hash,r.amount_minor,o.currency,
         'REFUNDED'::text AS status,r.completed_at AS occurred_at
  FROM refunds r
  JOIN orders o ON o.id=r.order_id
  WHERE o.provider=$1 AND r.status='SUCCEEDED'
    AND r.completed_at >= $2 AND r.completed_at < $3
) entries
WHERE NOT $4 OR (occurred_at,kind,row_id) > ($5,$6,$7)
ORDER BY occurred_at,kind,row_id
LIMIT $8`

func (repository *PostgresRepository) ListReconciliationLocalEntries(ctx context.Context, provider string, start, end time.Time, cursor LocalCursor, limit int) (LocalPage, error) {
	if limit < 1 || limit > 1000 {
		limit = 500
	}
	if cursor.RowID == uuid.Nil {
		cursor.RowID = uuid.Nil
	}
	rows, err := repository.pool.Query(ctx, localEntriesSQL,
		provider, start.UTC(), end.UTC(), cursor.Valid, cursor.OccurredAt.UTC(), string(cursor.Kind), cursor.RowID, limit+1)
	if err != nil {
		return LocalPage{}, err
	}
	defer rows.Close()
	entries := make([]LocalEntry, 0, limit+1)
	for rows.Next() {
		var entry LocalEntry
		if err := rows.Scan(&entry.RowID, &entry.OrderID, &entry.TransactionID, &entry.Kind, &entry.OrderNo, &entry.RefundNo, &entry.ReferenceHash,
			&entry.AmountMinor, &entry.Currency, &entry.Status, &entry.OccurredAt); err != nil {
			return LocalPage{}, err
		}
		entries = append(entries, entry)
	}
	if err := rows.Err(); err != nil {
		return LocalPage{}, err
	}
	page := LocalPage{Entries: entries}
	if len(entries) > limit {
		page.Entries = entries[:limit]
		last := page.Entries[len(page.Entries)-1]
		page.Next = LocalCursor{OccurredAt: last.OccurredAt, Kind: last.Kind, RowID: last.RowID, Valid: true}
	}
	return page, nil
}

func (repository *PostgresRepository) CompleteReconciliationRun(ctx context.Context, runID uuid.UUID, success bool, summary []byte, discrepancies []Discrepancy) (store.ReconciliationRun, error) {
	trimmedSummary := strings.TrimSpace(string(summary))
	if runID == uuid.Nil || !json.Valid(summary) || !strings.HasPrefix(trimmedSummary, "{") || !strings.HasSuffix(trimmedSummary, "}") ||
		len(summary) > 256<<10 || len(discrepancies) > maxPersistedDiscrepancies || (!success && len(discrepancies) != 0) {
		return store.ReconciliationRun{}, store.ErrDenied
	}
	for _, discrepancy := range discrepancies {
		if !validDiscrepancy(discrepancy) {
			return store.ReconciliationRun{}, store.ErrDenied
		}
	}
	discrepancies = append([]Discrepancy(nil), discrepancies...)
	sort.Slice(discrepancies, func(left, right int) bool {
		return bytes.Compare(discrepancies[left].Key[:], discrepancies[right].Key[:]) < 0
	})
	tx, err := repository.pool.Begin(ctx)
	if err != nil {
		return store.ReconciliationRun{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	status := "SUCCEEDED"
	if !success {
		status = "FAILED"
	}
	var result store.ReconciliationRun
	err = tx.QueryRow(ctx, `
UPDATE reconciliation_runs
SET status=$2,result_summary=$3::jsonb,started_at=coalesce(started_at,now()),completed_at=now()
WHERE id=$1 AND status IN('PENDING','RUNNING')
RETURNING id,provider,period_start,period_end,status,result_summary,requested_by,started_at,completed_at,created_at`,
		runID, status, json.RawMessage(summary)).Scan(
		&result.ID, &result.Provider, &result.PeriodStart, &result.PeriodEnd, &result.Status,
		&result.ResultSummary, &result.RequestedBy, &result.StartedAt, &result.CompletedAt, &result.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return store.ReconciliationRun{}, store.ErrConflict
	}
	if err != nil {
		return store.ReconciliationRun{}, err
	}
	for _, discrepancy := range discrepancies {
		_, err = tx.Exec(ctx, `
INSERT INTO reconciliation_discrepancies(
  id,run_id,discrepancy_key,provider,category,internal_order_id,internal_transaction_id,
  external_reference_hmac,local_status,provider_status,summary_code,
  local_amount_minor,provider_amount_minor,local_currency,provider_currency,status)
VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,'OPEN')
ON CONFLICT(discrepancy_key) DO UPDATE SET
  run_id=excluded.run_id,provider=excluded.provider,category=excluded.category,
  internal_order_id=excluded.internal_order_id,internal_transaction_id=excluded.internal_transaction_id,
  external_reference_hmac=excluded.external_reference_hmac,
  local_status=excluded.local_status,provider_status=excluded.provider_status,summary_code=excluded.summary_code,
  local_amount_minor=excluded.local_amount_minor,provider_amount_minor=excluded.provider_amount_minor,
  local_currency=excluded.local_currency,provider_currency=excluded.provider_currency,
  status=CASE WHEN reconciliation_discrepancies.status IN('RESOLVED','IGNORED') THEN 'OPEN' ELSE reconciliation_discrepancies.status END,
  acknowledged_at=CASE WHEN reconciliation_discrepancies.status IN('RESOLVED','IGNORED') THEN NULL ELSE reconciliation_discrepancies.acknowledged_at END,
  resolved_at=NULL,resolved_by=NULL,resolution_code=NULL,last_seen_at=now(),updated_at=now(),
  version=reconciliation_discrepancies.version+1`,
			uuid.New(), runID, discrepancy.Key[:], discrepancy.Provider, discrepancy.Category,
			discrepancy.InternalOrderID, discrepancy.InternalTransactionID, discrepancy.ExternalReferenceHMAC,
			discrepancy.LocalStatus, discrepancy.ProviderStatus, discrepancy.SummaryCode,
			discrepancy.LocalAmountMinor, discrepancy.ProviderAmountMinor,
			discrepancy.LocalCurrency, discrepancy.ProviderCurrency)
		if err != nil {
			return store.ReconciliationRun{}, err
		}
	}
	return result, tx.Commit(ctx)
}

func validDiscrepancy(discrepancy Discrepancy) bool {
	if discrepancy.Key == [sha256.Size]byte{} || (discrepancy.Provider != string(payment.ProviderAlipay) && discrepancy.Provider != string(payment.ProviderWeChatPay)) ||
		len(discrepancy.ExternalReferenceHMAC) != sha256.Size || len(discrepancy.SummaryCode) < 1 || len(discrepancy.SummaryCode) > 64 {
		return false
	}
	switch discrepancy.Category {
	case "MISSING_LOCAL", "MISSING_PROVIDER", "AMOUNT_MISMATCH", "CURRENCY_MISMATCH", "STATUS_MISMATCH", "REFUND_MISMATCH", "DUPLICATE_PROVIDER_REFERENCE", "DUPLICATE_LOCAL_REFERENCE", "OTHER":
	default:
		return false
	}
	for _, value := range []*string{discrepancy.LocalStatus, discrepancy.ProviderStatus} {
		if value != nil && !validUpperCode(*value, 32) {
			return false
		}
	}
	for _, value := range []*string{discrepancy.LocalCurrency, discrepancy.ProviderCurrency} {
		if value != nil && !validCurrency(*value) {
			return false
		}
	}
	return (discrepancy.LocalAmountMinor == nil || *discrepancy.LocalAmountMinor >= 0) &&
		(discrepancy.ProviderAmountMinor == nil || *discrepancy.ProviderAmountMinor >= 0) && validUpperCode(discrepancy.SummaryCode, 64)
}

func validUpperCode(value string, max int) bool {
	if len(value) < 1 || len(value) > max || value[0] < 'A' || value[0] > 'Z' {
		return false
	}
	for _, character := range value[1:] {
		if (character < 'A' || character > 'Z') && (character < '0' || character > '9') && character != '_' {
			return false
		}
	}
	return true
}

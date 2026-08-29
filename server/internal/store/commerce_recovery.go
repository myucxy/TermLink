package store

import (
	"context"
	"time"

	"github.com/google/uuid"
)

const (
	commerceRecoveryDefaultLimit = 50
	commerceRecoveryMaxLimit     = 200
)

// CommercePendingPaymentCandidate identifies an old payment attempt for an
// authoritative provider query. The merchant order number remains behind the
// existing GetOrder boundary and is not duplicated into the candidate feed.
type CommercePendingPaymentCandidate struct {
	OrderID      uuid.UUID
	AccountID    uuid.UUID
	Provider     string
	PaymentScene string
	Status       string
	ExpiresAt    time.Time
	UpdatedAt    time.Time
}

// CommerceFulfillmentCandidate can be passed directly to FulfillOrder. That
// method supplies the serializable, idempotent mutation boundary.
type CommerceFulfillmentCandidate struct {
	OrderID   uuid.UUID
	Status    string
	UpdatedAt time.Time
}

// CommerceRefundCandidate is observational only. The current payment Gateway
// has no QueryRefund operation, so discovering one of these rows must not be
// interpreted as evidence for any refund state transition.
type CommerceRefundCandidate struct {
	RefundID  uuid.UUID
	OrderID   uuid.UUID
	Provider  string
	Status    string
	UpdatedAt time.Time
}

type CommerceReconciliationCandidate struct {
	RunID       uuid.UUID
	Provider    string
	PeriodStart time.Time
	PeriodEnd   time.Time
	CreatedAt   time.Time
}

// CommerceConsistencyCounts reports only clear relational contradictions. It
// contains no provider correlation values or row payloads.
type CommerceConsistencyCounts struct {
	OrdersWithoutCompatibleTransaction    int64
	SettledTransactionsWithUnsettledOrder int64
	CompletedOrdersWithoutGrant           int64
	RefundedOrdersWithLiveGrant           int64
}

const stalePendingPaymentCandidatesSQL = `
	SELECT orders.id,orders.account_id,orders.provider,orders.payment_scene,
	       orders.status,orders.expires_at,orders.updated_at
	FROM orders
	WHERE orders.status='PENDING_PAYMENT' AND orders.updated_at<=$1
	ORDER BY orders.updated_at,orders.id
	LIMIT $2`

const pendingFulfillmentCandidatesSQL = `
	SELECT orders.id,orders.status,orders.updated_at
	FROM orders
	WHERE orders.status IN ('PAID','FULFILLING') AND orders.updated_at<=$1
	ORDER BY orders.updated_at,orders.id
	LIMIT $2`

const pendingRefundCandidatesSQL = `
	SELECT refunds.id,refunds.order_id,orders.provider,refunds.status,refunds.updated_at
	FROM refunds
	JOIN orders ON orders.id=refunds.order_id
	WHERE refunds.status IN ('CREATED','PENDING') AND refunds.updated_at<=$1
	ORDER BY refunds.updated_at,refunds.id
	LIMIT $2`

const pendingReconciliationCandidatesSQL = `
	SELECT reconciliation_runs.id,reconciliation_runs.provider,
	       reconciliation_runs.period_start,reconciliation_runs.period_end,
	       reconciliation_runs.created_at
	FROM reconciliation_runs
	WHERE reconciliation_runs.status='PENDING' AND reconciliation_runs.created_at<=$1
	ORDER BY reconciliation_runs.created_at,reconciliation_runs.id
	LIMIT $2`

const commerceConsistencyCountsSQL = `
	SELECT
		(SELECT count(*)
		 FROM orders
		 WHERE (orders.status IN ('PAID','FULFILLING','FULFILLED','REFUNDING')
		        AND NOT EXISTS (
		            SELECT 1 FROM payment_transactions
		            WHERE payment_transactions.order_id=orders.id
		              AND payment_transactions.status='SUCCEEDED'
		        ))
		    OR (orders.status='REFUNDED'
		        AND NOT EXISTS (
		            SELECT 1 FROM payment_transactions
		            WHERE payment_transactions.order_id=orders.id
		              AND payment_transactions.status='REFUNDED'
		        ))),
		(SELECT count(*)
		 FROM orders
		 WHERE EXISTS (
		       SELECT 1 FROM payment_transactions
		       WHERE payment_transactions.order_id=orders.id
		         AND ((payment_transactions.status='SUCCEEDED'
		               AND orders.status NOT IN ('PAID','FULFILLING','FULFILLED','REFUNDING'))
		           OR (payment_transactions.status='REFUNDED' AND orders.status<>'REFUNDED'))
		   )),
		(SELECT count(*)
		 FROM orders
		 WHERE orders.status IN ('FULFILLED','REFUNDING','REFUNDED')
		   AND NOT EXISTS (
		       SELECT 1 FROM entitlement_grants
		       WHERE entitlement_grants.source_id=orders.id
		         AND entitlement_grants.account_id=orders.account_id
		         AND entitlement_grants.source_type=orders.provider
		         AND entitlement_grants.plan_key=orders.plan_key
		         AND entitlement_grants.catalog_version=orders.catalog_version
		   )),
		(SELECT count(*)
		 FROM orders
		 WHERE orders.status='REFUNDED'
		   AND EXISTS (
		       SELECT 1 FROM entitlement_grants
		       WHERE entitlement_grants.source_id=orders.id
		         AND entitlement_grants.account_id=orders.account_id
		         AND entitlement_grants.source_type=orders.provider
		         AND entitlement_grants.status IN ('PENDING','ACTIVE')
		   ))`

func normalizeCommerceRecoveryLimit(limit int) int {
	if limit < 1 {
		return commerceRecoveryDefaultLimit
	}
	if limit > commerceRecoveryMaxLimit {
		return commerceRecoveryMaxLimit
	}
	return limit
}

// ListStalePendingPaymentCandidates is intentionally a read-only, at-least-
// once candidate feed. It holds no database lock while a provider is queried;
// the existing settlement methods recheck state transactionally.
func (s *Postgres) ListStalePendingPaymentCandidates(ctx context.Context, before time.Time, limit int) ([]CommercePendingPaymentCandidate, error) {
	limit = normalizeCommerceRecoveryLimit(limit)
	rows, err := s.Pool.Query(ctx, stalePendingPaymentCandidatesSQL, before.UTC(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]CommercePendingPaymentCandidate, 0, limit)
	for rows.Next() {
		var item CommercePendingPaymentCandidate
		if err := rows.Scan(&item.OrderID, &item.AccountID, &item.Provider,
			&item.PaymentScene, &item.Status, &item.ExpiresAt, &item.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

// ListPendingFulfillmentCandidates is read-only. Concurrent workers may see
// the same order; FulfillOrder provides the idempotent transactional fence.
func (s *Postgres) ListPendingFulfillmentCandidates(ctx context.Context, before time.Time, limit int) ([]CommerceFulfillmentCandidate, error) {
	limit = normalizeCommerceRecoveryLimit(limit)
	rows, err := s.Pool.Query(ctx, pendingFulfillmentCandidatesSQL, before.UTC(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]CommerceFulfillmentCandidate, 0, limit)
	for rows.Next() {
		var item CommerceFulfillmentCandidate
		if err := rows.Scan(&item.OrderID, &item.Status, &item.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListPendingRefundCandidates(ctx context.Context, before time.Time, limit int) ([]CommerceRefundCandidate, error) {
	limit = normalizeCommerceRecoveryLimit(limit)
	rows, err := s.Pool.Query(ctx, pendingRefundCandidatesSQL, before.UTC(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]CommerceRefundCandidate, 0, limit)
	for rows.Next() {
		var item CommerceRefundCandidate
		if err := rows.Scan(&item.RefundID, &item.OrderID, &item.Provider,
			&item.Status, &item.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) ListPendingReconciliationCandidates(ctx context.Context, before time.Time, limit int) ([]CommerceReconciliationCandidate, error) {
	limit = normalizeCommerceRecoveryLimit(limit)
	rows, err := s.Pool.Query(ctx, pendingReconciliationCandidatesSQL, before.UTC(), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]CommerceReconciliationCandidate, 0, limit)
	for rows.Next() {
		var item CommerceReconciliationCandidate
		if err := rows.Scan(&item.RunID, &item.Provider, &item.PeriodStart,
			&item.PeriodEnd, &item.CreatedAt); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func (s *Postgres) CountCommerceRecoveryInconsistencies(ctx context.Context) (CommerceConsistencyCounts, error) {
	var result CommerceConsistencyCounts
	err := s.Pool.QueryRow(ctx, commerceConsistencyCountsSQL).Scan(
		&result.OrdersWithoutCompatibleTransaction,
		&result.SettledTransactionsWithUnsettledOrder,
		&result.CompletedOrdersWithoutGrant,
		&result.RefundedOrdersWithLiveGrant,
	)
	return result, err
}

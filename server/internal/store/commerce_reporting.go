package store

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

const (
	commerceReportDefaultLimit   = 50
	commerceReportMaxLimit       = 200
	commerceReportMaxRange       = 93 * 24 * time.Hour
	commerceExportDefaultLimit   = 500
	commerceExportMaxLimit       = 1000
	commerceExportMaxRange       = 31 * 24 * time.Hour
	commerceConsistencyMaxRange  = 31 * 24 * time.Hour
	commerceCatalogDraftMaxPlans = 500
)

// CommerceOrderReportFilter is an exact-match operational filter. CreatedFrom
// is inclusive and CreatedTo is exclusive. A cursor is valid only when both
// cursor fields are supplied; it identifies the last row returned by the
// preceding page.
type CommerceOrderReportFilter struct {
	OrderNo                string
	AccountID              *uuid.UUID
	Provider               string
	Status                 string
	CreatedFrom, CreatedTo *time.Time
	AfterCreatedAt         *time.Time
	AfterID                *uuid.UUID
	Limit                  int
}

type CommerceOrderReportRow struct {
	OrderID                       uuid.UUID
	OrderNo                       string
	AccountID                     uuid.UUID
	PlanKey                       string
	CatalogVersion                int64
	Provider                      string
	PaymentScene                  string
	AmountMinor                   int64
	Currency                      string
	Status                        string
	ExpiresAt                     time.Time
	PaidAt, FulfilledAt, ClosedAt *time.Time
	CreatedAt, UpdatedAt          time.Time
}

type CommerceOrderReportCursor struct {
	CreatedAt time.Time
	OrderID   uuid.UUID
}

type CommerceOrderReportPage struct {
	Rows       []CommerceOrderReportRow
	NextCursor *CommerceOrderReportCursor
}

// CommerceOrderExportFilter intentionally has no cursor. Every export is a
// bounded snapshot request and reports truncation explicitly.
type CommerceOrderExportFilter struct {
	OrderNo                string
	AccountID              *uuid.UUID
	Provider               string
	Status                 string
	CreatedFrom, CreatedTo *time.Time
	Limit                  int
}

// CommerceOrderExportRow contains only ledger facts needed by operations.
// It deliberately omits internal row IDs, idempotency keys, provider
// references, payment payloads, notification content and identity attributes.
type CommerceOrderExportRow struct {
	OrderNo        string
	AccountID      uuid.UUID
	PlanKey        string
	CatalogVersion int64
	Provider       string
	PaymentScene   string
	AmountMinor    int64
	Currency       string
	Status         string
	PaidAt         *time.Time
	CreatedAt      time.Time
}

type CommerceOrderExport struct {
	Rows      []CommerceOrderExportRow
	Truncated bool
}

type CommerceConsistencyFilter struct {
	AccountID              *uuid.UUID
	Provider               string
	Category               string
	CreatedFrom, CreatedTo *time.Time
	After                  *CommerceConsistencyCursor
	Limit                  int
}

type CommerceConsistencyCursor struct {
	OccurredAt time.Time
	EntityID   uuid.UUID
	Category   string
}

// CommerceConsistencyDetail is an observational summary. RecommendedAction
// names a separately authorized workflow; reading this row never mutates an
// order or treats local state as proof of payment.
type CommerceConsistencyDetail struct {
	Category          string
	Severity          string
	RecommendedAction string
	EntityID          uuid.UUID
	OrderID           *uuid.UUID
	OrderNo           string
	AccountID         uuid.UUID
	Provider          string
	OrderStatus       string
	TransactionStates string
	TransactionCount  int64
	GrantStates       string
	GrantCount        int64
	MaterializedPlan  string
	ExpectedPlan      string
	OccurredAt        time.Time
}

type CommerceConsistencyPage struct {
	Rows       []CommerceConsistencyDetail
	NextCursor *CommerceConsistencyCursor
}

type CommerceCatalogDraft struct {
	ID         uuid.UUID
	Version    int64
	CreatedAt  time.Time
	Plans      []PlanDraft
	Validation CommerceCatalogValidationSummary
}

type CommerceCatalogValidationSummary struct {
	Valid     bool
	PlanCount int
	Issues    []CommerceCatalogValidationIssue
}

// CommerceCatalogValidationIssue uses stable codes so Web/API layers can
// localize the text without exposing database or configuration details.
type CommerceCatalogValidationIssue struct {
	Code    string
	PlanID  *uuid.UUID
	PlanKey string
	Field   string
}

const (
	CommerceCatalogIssueFreePlanRequired        = "FREE_PLAN_REQUIRED"
	CommerceCatalogIssueDuplicatePlanKey        = "DUPLICATE_PLAN_KEY"
	CommerceCatalogIssueCurrencyMustBeCNY       = "CURRENCY_MUST_BE_CNY"
	CommerceCatalogIssueFreePriceMustBeZero     = "FREE_PRICE_MUST_BE_ZERO"
	CommerceCatalogIssuePaidPriceMustBePositive = "PAID_PRICE_MUST_BE_POSITIVE"
	CommerceCatalogIssueFallbackPlanNotFound    = "FALLBACK_PLAN_NOT_FOUND"
	CommerceCatalogIssueSaleWindowInvalid       = "SALE_WINDOW_INVALID"
)

type normalizedCommerceOrderFilter struct {
	orderNo, provider, status string
	accountID                 uuid.UUID
	from, to                  time.Time
	afterAt                   time.Time
	afterID                   uuid.UUID
	hasCursor                 bool
	limit                     int
}

var commerceOrderStatuses = map[string]struct{}{
	"CREATED": {}, "PENDING_PAYMENT": {}, "PAID": {}, "FULFILLING": {},
	"FULFILLED": {}, "CLOSED": {}, "REFUNDING": {}, "REFUNDED": {}, "FAILED": {},
}

var commerceConsistencyCategories = map[string]struct{}{
	"ORDER_WITHOUT_COMPATIBLE_TRANSACTION": {},
	"SETTLED_TRANSACTION_ORDER_MISMATCH":   {},
	"COMPLETED_ORDER_WITHOUT_GRANT":        {},
	"REFUNDED_ORDER_WITH_LIVE_GRANT":       {},
	"MATERIALIZED_ENTITLEMENT_MISMATCH":    {},
}

func normalizeCommerceOrderReportFilter(input CommerceOrderReportFilter, now time.Time) (normalizedCommerceOrderFilter, error) {
	var result normalizedCommerceOrderFilter
	result.orderNo = strings.TrimSpace(input.OrderNo)
	result.provider = strings.ToUpper(strings.TrimSpace(input.Provider))
	result.status = strings.ToUpper(strings.TrimSpace(input.Status))
	if result.orderNo != "" && !validCommerceOrderNumber(result.orderNo) {
		return result, ErrDenied
	}
	if result.provider != "" && !validProvider(result.provider) {
		return result, ErrDenied
	}
	if result.status != "" {
		if _, ok := commerceOrderStatuses[result.status]; !ok {
			return result, ErrDenied
		}
	}
	if input.AccountID != nil {
		if *input.AccountID == uuid.Nil {
			return result, ErrDenied
		}
		result.accountID = *input.AccountID
	}
	if (input.CreatedFrom == nil) != (input.CreatedTo == nil) {
		return result, ErrDenied
	}
	now = now.UTC()
	if input.CreatedFrom == nil {
		result.from, result.to = now.Add(-30*24*time.Hour), now
	} else {
		result.from, result.to = input.CreatedFrom.UTC(), input.CreatedTo.UTC()
	}
	if !result.to.After(result.from) || result.to.Sub(result.from) > commerceReportMaxRange || result.to.After(now.Add(5*time.Minute)) {
		return result, ErrDenied
	}
	if (input.AfterCreatedAt == nil) != (input.AfterID == nil) {
		return result, ErrDenied
	}
	if input.AfterCreatedAt != nil {
		result.afterAt, result.afterID, result.hasCursor = input.AfterCreatedAt.UTC(), *input.AfterID, true
		if result.afterID == uuid.Nil || result.afterAt.Before(result.from) || !result.afterAt.Before(result.to) {
			return result, ErrDenied
		}
	}
	result.limit = input.Limit
	if result.limit == 0 {
		result.limit = commerceReportDefaultLimit
	}
	if result.limit < 1 || result.limit > commerceReportMaxLimit {
		return result, ErrDenied
	}
	return result, nil
}

func validCommerceOrderNumber(value string) bool {
	if len(value) < 16 || len(value) > 64 {
		return false
	}
	for _, char := range value {
		if (char < 'a' || char > 'z') && (char < 'A' || char > 'Z') &&
			(char < '0' || char > '9') && char != '-' && char != '_' {
			return false
		}
	}
	return true
}

func commerceReportingRole(ctx context.Context, tx pgx.Tx, actorID uuid.UUID) error {
	if actorID == uuid.Nil {
		return ErrDenied
	}
	_, err := commerceRole(ctx, tx, actorID, false, false)
	return err
}

func (s *Postgres) commerceReportingTx(ctx context.Context, actorID uuid.UUID) (pgx.Tx, error) {
	tx, err := s.Pool.BeginTx(ctx, commerceTxOptions(false))
	if err != nil {
		return nil, err
	}
	if err := commerceReportingRole(ctx, tx, actorID); err != nil {
		_ = tx.Rollback(ctx)
		return nil, err
	}
	return tx, nil
}

const commerceCatalogDraftSQL = `
	SELECT id,version,created_at
	FROM catalog_versions
	WHERE status='DRAFT'
	ORDER BY version DESC,id DESC
	LIMIT 1`

const commerceCatalogDraftPlansSQL = `
	SELECT id,$2::bigint,plan_key,display_name,description,billing_period,
	       max_remote_pcs,max_live_sessions,feature_flags,channel_products,sort_order,
	       visible_platforms,available_from,available_until,
	       COALESCE(fallback_plan_key,''),price_minor,currency
	FROM plans
	WHERE catalog_version_id=$1
	ORDER BY sort_order,plan_key,id
	LIMIT $3`

func commerceCatalogDraftTxOptions() pgx.TxOptions {
	return pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly}
}

// GetCommerceCatalogDraft returns a repeatable-read snapshot of the newest
// DRAFT catalog. A nil draft with nil error means no draft exists. It never
// creates or publishes a catalog.
func (s *Postgres) GetCommerceCatalogDraft(ctx context.Context, actorID uuid.UUID) (*CommerceCatalogDraft, error) {
	tx, err := s.Pool.BeginTx(ctx, commerceCatalogDraftTxOptions())
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if err := commerceReportingRole(ctx, tx, actorID); err != nil {
		return nil, err
	}
	var result CommerceCatalogDraft
	if err := tx.QueryRow(ctx, commerceCatalogDraftSQL).Scan(&result.ID, &result.Version, &result.CreatedAt); errors.Is(err, pgx.ErrNoRows) {
		if err := tx.Commit(ctx); err != nil {
			return nil, err
		}
		return nil, nil
	} else if err != nil {
		return nil, err
	}
	rows, err := tx.Query(ctx, commerceCatalogDraftPlansSQL, result.ID, result.Version, commerceCatalogDraftMaxPlans+1)
	if err != nil {
		return nil, err
	}
	result.Plans = make([]PlanDraft, 0)
	for rows.Next() {
		if len(result.Plans) == commerceCatalogDraftMaxPlans {
			rows.Close()
			return nil, ErrConflict
		}
		item, err := scanPlanDraft(rows)
		if err != nil {
			rows.Close()
			return nil, err
		}
		result.Plans = append(result.Plans, item)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return nil, err
	}
	rows.Close()
	result.Validation = validateCommerceCatalogDraft(result.Plans)
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return &result, nil
}

func validateCommerceCatalogDraft(plans []PlanDraft) CommerceCatalogValidationSummary {
	result := CommerceCatalogValidationSummary{PlanCount: len(plans), Issues: make([]CommerceCatalogValidationIssue, 0)}
	keys := make(map[string]int, len(plans))
	for _, plan := range plans {
		keys[plan.Key]++
	}
	if keys["free"] == 0 {
		result.Issues = append(result.Issues, CommerceCatalogValidationIssue{
			Code: CommerceCatalogIssueFreePlanRequired, Field: "plan_key",
		})
	}
	for _, plan := range plans {
		planID := plan.ID
		issue := func(code, field string) {
			result.Issues = append(result.Issues, CommerceCatalogValidationIssue{
				Code: code, PlanID: &planID, PlanKey: plan.Key, Field: field,
			})
		}
		if keys[plan.Key] > 1 {
			issue(CommerceCatalogIssueDuplicatePlanKey, "plan_key")
		}
		if plan.Currency != "CNY" {
			issue(CommerceCatalogIssueCurrencyMustBeCNY, "currency")
		}
		if (plan.Key == "free" || plan.BillingPeriod == "FREE") && plan.PriceMinor != 0 {
			issue(CommerceCatalogIssueFreePriceMustBeZero, "price_minor")
		}
		switch plan.BillingPeriod {
		case "MONTHLY", "YEARLY":
			if plan.PriceMinor <= 0 {
				issue(CommerceCatalogIssuePaidPriceMustBePositive, "price_minor")
			}
		}
		if plan.FallbackPlanKey != "" && keys[plan.FallbackPlanKey] == 0 {
			issue(CommerceCatalogIssueFallbackPlanNotFound, "fallback_plan_key")
		}
		if plan.AvailableFrom != nil && plan.AvailableUntil != nil && !plan.AvailableUntil.After(*plan.AvailableFrom) {
			issue(CommerceCatalogIssueSaleWindowInvalid, "available_until")
		}
	}
	result.Valid = len(result.Issues) == 0
	return result
}

const commerceOrderReportSQL = `
	SELECT id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,
	       amount_minor,currency,status,expires_at,paid_at,fulfilled_at,closed_at,
	       created_at,updated_at
	FROM orders
	WHERE created_at >= $1 AND created_at < $2
	  AND ($3 = '' OR order_no = $3)
	  AND ($4::uuid = '00000000-0000-0000-0000-000000000000'::uuid OR account_id = $4)
	  AND ($5 = '' OR provider = $5)
	  AND ($6 = '' OR status = $6)
	  AND (NOT $7 OR (created_at,id) < ($8,$9))
	ORDER BY created_at DESC,id DESC
	LIMIT $10`

func (s *Postgres) ListCommerceOrderReport(ctx context.Context, actorID uuid.UUID, input CommerceOrderReportFilter) (CommerceOrderReportPage, error) {
	filter, err := normalizeCommerceOrderReportFilter(input, time.Now())
	if err != nil {
		return CommerceOrderReportPage{}, err
	}
	tx, err := s.commerceReportingTx(ctx, actorID)
	if err != nil {
		return CommerceOrderReportPage{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, commerceOrderReportSQL, filter.from, filter.to, filter.orderNo,
		filter.accountID, filter.provider, filter.status, filter.hasCursor, filter.afterAt,
		filter.afterID, filter.limit+1)
	if err != nil {
		return CommerceOrderReportPage{}, err
	}
	result := CommerceOrderReportPage{Rows: make([]CommerceOrderReportRow, 0, filter.limit)}
	for rows.Next() {
		var item CommerceOrderReportRow
		if err := rows.Scan(&item.OrderID, &item.OrderNo, &item.AccountID, &item.PlanKey,
			&item.CatalogVersion, &item.Provider, &item.PaymentScene, &item.AmountMinor,
			&item.Currency, &item.Status, &item.ExpiresAt, &item.PaidAt, &item.FulfilledAt,
			&item.ClosedAt, &item.CreatedAt, &item.UpdatedAt); err != nil {
			rows.Close()
			return CommerceOrderReportPage{}, err
		}
		result.Rows = append(result.Rows, item)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return CommerceOrderReportPage{}, err
	}
	rows.Close()
	if len(result.Rows) > filter.limit {
		result.Rows = result.Rows[:filter.limit]
		last := result.Rows[len(result.Rows)-1]
		result.NextCursor = &CommerceOrderReportCursor{CreatedAt: last.CreatedAt, OrderID: last.OrderID}
	}
	if err := tx.Commit(ctx); err != nil {
		return CommerceOrderReportPage{}, err
	}
	return result, nil
}

const commerceOrderExportSQL = `
	SELECT order_no,account_id,plan_key,catalog_version,provider,payment_scene,
	       amount_minor,currency,status,paid_at,created_at
	FROM orders
	WHERE created_at >= $1 AND created_at < $2
	  AND ($3 = '' OR order_no = $3)
	  AND ($4::uuid = '00000000-0000-0000-0000-000000000000'::uuid OR account_id = $4)
	  AND ($5 = '' OR provider = $5)
	  AND ($6 = '' OR status = $6)
	ORDER BY created_at DESC,id DESC
	LIMIT $7`

func normalizeCommerceOrderExportFilter(input CommerceOrderExportFilter, now time.Time) (normalizedCommerceOrderFilter, error) {
	if input.CreatedFrom == nil || input.CreatedTo == nil {
		return normalizedCommerceOrderFilter{}, ErrDenied
	}
	limit := input.Limit
	if limit == 0 {
		limit = commerceExportDefaultLimit
	}
	if limit < 1 || limit > commerceExportMaxLimit {
		return normalizedCommerceOrderFilter{}, ErrDenied
	}
	filter, err := normalizeCommerceOrderReportFilter(CommerceOrderReportFilter{
		OrderNo: input.OrderNo, AccountID: input.AccountID, Provider: input.Provider,
		Status: input.Status, CreatedFrom: input.CreatedFrom, CreatedTo: input.CreatedTo,
		Limit: commerceReportDefaultLimit,
	}, now)
	if err != nil || filter.to.Sub(filter.from) > commerceExportMaxRange {
		return normalizedCommerceOrderFilter{}, ErrDenied
	}
	filter.limit = limit
	return filter, nil
}

func (s *Postgres) ExportCommerceOrders(ctx context.Context, actorID uuid.UUID, input CommerceOrderExportFilter) (CommerceOrderExport, error) {
	filter, err := normalizeCommerceOrderExportFilter(input, time.Now())
	if err != nil {
		return CommerceOrderExport{}, err
	}
	tx, err := s.commerceReportingTx(ctx, actorID)
	if err != nil {
		return CommerceOrderExport{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, commerceOrderExportSQL, filter.from, filter.to, filter.orderNo,
		filter.accountID, filter.provider, filter.status, filter.limit+1)
	if err != nil {
		return CommerceOrderExport{}, err
	}
	result := CommerceOrderExport{Rows: make([]CommerceOrderExportRow, 0, filter.limit)}
	for rows.Next() {
		var item CommerceOrderExportRow
		if err := rows.Scan(&item.OrderNo, &item.AccountID, &item.PlanKey, &item.CatalogVersion,
			&item.Provider, &item.PaymentScene, &item.AmountMinor, &item.Currency, &item.Status,
			&item.PaidAt, &item.CreatedAt); err != nil {
			rows.Close()
			return CommerceOrderExport{}, err
		}
		result.Rows = append(result.Rows, item)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return CommerceOrderExport{}, err
	}
	rows.Close()
	if len(result.Rows) > filter.limit {
		result.Rows = result.Rows[:filter.limit]
		result.Truncated = true
	}
	if err := tx.Commit(ctx); err != nil {
		return CommerceOrderExport{}, err
	}
	return result, nil
}

const commerceConsistencyDetailsSQL = `
	WITH scoped_orders AS (
		SELECT id,order_no,account_id,plan_key,catalog_version,provider,status,created_at,updated_at
		FROM orders
		WHERE created_at >= $1 AND created_at < $2
		  AND ($3::uuid = '00000000-0000-0000-0000-000000000000'::uuid OR account_id = $3)
		  AND ($4 = '' OR provider = $4)
	), transaction_summary AS (
		SELECT payment_transactions.order_id,
		       count(*) AS transaction_count,
		       string_agg(DISTINCT payment_transactions.status, ',' ORDER BY payment_transactions.status) AS transaction_states,
		       bool_or(payment_transactions.status='SUCCEEDED') AS has_succeeded,
		       bool_or(payment_transactions.status='REFUNDED') AS has_refunded
		FROM payment_transactions
		JOIN scoped_orders ON scoped_orders.id=payment_transactions.order_id
		GROUP BY payment_transactions.order_id
	), grant_summary AS (
		SELECT entitlement_grants.source_id AS order_id,
		       count(*) AS grant_count,
		       string_agg(DISTINCT entitlement_grants.status, ',' ORDER BY entitlement_grants.status) AS grant_states,
		       bool_or(entitlement_grants.status IN ('PENDING','ACTIVE')) AS has_live
		FROM entitlement_grants
		JOIN scoped_orders ON scoped_orders.id=entitlement_grants.source_id
		 AND scoped_orders.account_id=entitlement_grants.account_id
		 AND scoped_orders.provider=entitlement_grants.source_type
		GROUP BY entitlement_grants.source_id
	), order_issues AS (
		SELECT 'ORDER_WITHOUT_COMPATIBLE_TRANSACTION'::text AS category,
		       'ERROR'::text AS severity,
		       'QUERY_PROVIDER_THEN_REPLAY_VERIFIED_SETTLEMENT'::text AS recommended_action,
		       scoped_orders.id AS entity_id,scoped_orders.id AS order_id,scoped_orders.order_no,
		       scoped_orders.account_id,scoped_orders.provider,scoped_orders.status AS order_status,
		       coalesce(transaction_summary.transaction_states,'') AS transaction_states,
		       coalesce(transaction_summary.transaction_count,0) AS transaction_count,
		       coalesce(grant_summary.grant_states,'') AS grant_states,
		       coalesce(grant_summary.grant_count,0) AS grant_count,
		       coalesce(entitlements.plan_key,'') AS materialized_plan,
		       scoped_orders.plan_key AS expected_plan,scoped_orders.updated_at AS occurred_at
		FROM scoped_orders
		LEFT JOIN transaction_summary ON transaction_summary.order_id=scoped_orders.id
		LEFT JOIN grant_summary ON grant_summary.order_id=scoped_orders.id
		LEFT JOIN entitlements ON entitlements.account_id=scoped_orders.account_id
		WHERE (scoped_orders.status IN ('PAID','FULFILLING','FULFILLED','REFUNDING')
		       AND NOT coalesce(transaction_summary.has_succeeded,false))
		   OR (scoped_orders.status='REFUNDED' AND NOT coalesce(transaction_summary.has_refunded,false))
		UNION ALL
		SELECT 'SETTLED_TRANSACTION_ORDER_MISMATCH','ERROR',
		       'QUERY_PROVIDER_AND_REPLAY_VERIFIED_SETTLEMENT',
		       scoped_orders.id,scoped_orders.id,scoped_orders.order_no,scoped_orders.account_id,
		       scoped_orders.provider,scoped_orders.status,
		       transaction_summary.transaction_states,transaction_summary.transaction_count,
		       coalesce(grant_summary.grant_states,''),coalesce(grant_summary.grant_count,0),
		       coalesce(entitlements.plan_key,''),scoped_orders.plan_key,scoped_orders.updated_at
		FROM scoped_orders
		JOIN transaction_summary ON transaction_summary.order_id=scoped_orders.id
		LEFT JOIN grant_summary ON grant_summary.order_id=scoped_orders.id
		LEFT JOIN entitlements ON entitlements.account_id=scoped_orders.account_id
		WHERE (transaction_summary.has_succeeded
		       AND scoped_orders.status NOT IN ('PAID','FULFILLING','FULFILLED','REFUNDING'))
		   OR (transaction_summary.has_refunded AND scoped_orders.status<>'REFUNDED')
		UNION ALL
		SELECT 'COMPLETED_ORDER_WITHOUT_GRANT','ERROR','RETRY_IDEMPOTENT_FULFILLMENT',
		       scoped_orders.id,scoped_orders.id,scoped_orders.order_no,scoped_orders.account_id,
		       scoped_orders.provider,scoped_orders.status,
		       coalesce(transaction_summary.transaction_states,''),coalesce(transaction_summary.transaction_count,0),
		       '',0,coalesce(entitlements.plan_key,''),scoped_orders.plan_key,scoped_orders.updated_at
		FROM scoped_orders
		LEFT JOIN transaction_summary ON transaction_summary.order_id=scoped_orders.id
		LEFT JOIN grant_summary ON grant_summary.order_id=scoped_orders.id
		LEFT JOIN entitlements ON entitlements.account_id=scoped_orders.account_id
		WHERE scoped_orders.status IN ('FULFILLED','REFUNDING','REFUNDED')
		  AND grant_summary.order_id IS NULL
		UNION ALL
		SELECT 'REFUNDED_ORDER_WITH_LIVE_GRANT','ERROR','REVIEW_REFUND_AND_REVOKE_ORDER_GRANT',
		       scoped_orders.id,scoped_orders.id,scoped_orders.order_no,scoped_orders.account_id,
		       scoped_orders.provider,scoped_orders.status,
		       coalesce(transaction_summary.transaction_states,''),coalesce(transaction_summary.transaction_count,0),
		       grant_summary.grant_states,grant_summary.grant_count,
		       coalesce(entitlements.plan_key,''),scoped_orders.plan_key,scoped_orders.updated_at
		FROM scoped_orders
		LEFT JOIN transaction_summary ON transaction_summary.order_id=scoped_orders.id
		JOIN grant_summary ON grant_summary.order_id=scoped_orders.id AND grant_summary.has_live
		LEFT JOIN entitlements ON entitlements.account_id=scoped_orders.account_id
		WHERE scoped_orders.status='REFUNDED'
	), affected_accounts AS (
		SELECT DISTINCT account_id FROM scoped_orders
	), entitlement_issues AS (
		SELECT 'MATERIALIZED_ENTITLEMENT_MISMATCH'::text AS category,
		       'WARNING'::text AS severity,
		       'RECOMPUTE_ENTITLEMENT_FROM_VERIFIED_GRANTS'::text AS recommended_action,
		       affected_accounts.account_id AS entity_id,NULL::uuid AS order_id,''::text AS order_no,
		       affected_accounts.account_id,''::text AS provider,''::text AS order_status,
		       ''::text AS transaction_states,0::bigint AS transaction_count,
		       coalesce(winner.status,'') AS grant_states,
		       CASE WHEN winner.id IS NULL THEN 0 ELSE 1 END::bigint AS grant_count,
		       entitlements.plan_key AS materialized_plan,coalesce(winner.plan_key,'free') AS expected_plan,
		       entitlements.updated_at AS occurred_at
		FROM affected_accounts
		JOIN entitlements ON entitlements.account_id=affected_accounts.account_id
		LEFT JOIN LATERAL (
			SELECT entitlement_grants.id,entitlement_grants.plan_key,
			       entitlement_grants.catalog_version,entitlement_grants.source_type,
			       entitlement_grants.ends_at,entitlement_grants.grace_until,entitlement_grants.status
			FROM entitlement_grants
			JOIN catalog_versions ON catalog_versions.version=entitlement_grants.catalog_version
			JOIN plans ON plans.catalog_version_id=catalog_versions.id
			 AND plans.plan_key=entitlement_grants.plan_key
			WHERE entitlement_grants.account_id=affected_accounts.account_id
			  AND entitlement_grants.status='ACTIVE'
			  AND entitlement_grants.starts_at<=CURRENT_TIMESTAMP
			  AND (entitlement_grants.ends_at IS NULL OR entitlement_grants.ends_at>CURRENT_TIMESTAMP)
			ORDER BY plans.price_minor DESC,plans.sort_order DESC,
			         (entitlement_grants.source_type='DEFAULT') ASC,
			         entitlement_grants.ends_at DESC NULLS FIRST,entitlement_grants.created_at DESC
			LIMIT 1
		) winner ON true
		WHERE (winner.id IS NULL AND (entitlements.plan_key<>'free' OR entitlements.source<>'DEFAULT'))
		   OR (winner.id IS NOT NULL AND (
		       entitlements.plan_key IS DISTINCT FROM winner.plan_key
		       OR entitlements.catalog_version IS DISTINCT FROM winner.catalog_version
		       OR entitlements.source IS DISTINCT FROM winner.source_type
		       OR entitlements.expires_at IS DISTINCT FROM winner.ends_at
		       OR entitlements.grace_until IS DISTINCT FROM winner.grace_until))
	), issues AS (
		SELECT * FROM order_issues
		UNION ALL
		SELECT * FROM entitlement_issues
	)
	SELECT category,severity,recommended_action,entity_id,order_id,order_no,account_id,
	       provider,order_status,transaction_states,transaction_count,grant_states,grant_count,
	       materialized_plan,expected_plan,occurred_at
	FROM issues
	WHERE ($5 = '' OR category=$5)
	  AND (NOT $6 OR (occurred_at,entity_id,category) < ($7,$8,$9))
	ORDER BY occurred_at DESC,entity_id DESC,category DESC
	LIMIT $10`

type normalizedCommerceConsistencyFilter struct {
	accountID          uuid.UUID
	provider, category string
	from, to, afterAt  time.Time
	afterEntity        uuid.UUID
	afterCategory      string
	hasCursor          bool
	limit              int
}

func normalizeCommerceConsistencyFilter(input CommerceConsistencyFilter, now time.Time) (normalizedCommerceConsistencyFilter, error) {
	var result normalizedCommerceConsistencyFilter
	result.provider = strings.ToUpper(strings.TrimSpace(input.Provider))
	result.category = strings.ToUpper(strings.TrimSpace(input.Category))
	if result.provider != "" && !validProvider(result.provider) {
		return result, ErrDenied
	}
	if result.category != "" {
		if _, ok := commerceConsistencyCategories[result.category]; !ok {
			return result, ErrDenied
		}
	}
	if input.AccountID != nil {
		if *input.AccountID == uuid.Nil {
			return result, ErrDenied
		}
		result.accountID = *input.AccountID
	}
	if (input.CreatedFrom == nil) != (input.CreatedTo == nil) {
		return result, ErrDenied
	}
	now = now.UTC()
	if input.CreatedFrom == nil {
		result.from, result.to = now.Add(-24*time.Hour), now
	} else {
		result.from, result.to = input.CreatedFrom.UTC(), input.CreatedTo.UTC()
	}
	if !result.to.After(result.from) || result.to.Sub(result.from) > commerceConsistencyMaxRange || result.to.After(now.Add(5*time.Minute)) {
		return result, ErrDenied
	}
	if input.After != nil {
		result.afterAt = input.After.OccurredAt.UTC()
		result.afterEntity = input.After.EntityID
		result.afterCategory = strings.ToUpper(strings.TrimSpace(input.After.Category))
		result.hasCursor = true
		if result.afterEntity == uuid.Nil || result.afterAt.IsZero() || result.afterCategory == "" {
			return result, ErrDenied
		}
		if _, ok := commerceConsistencyCategories[result.afterCategory]; !ok {
			return result, ErrDenied
		}
	}
	result.limit = input.Limit
	if result.limit == 0 {
		result.limit = commerceReportDefaultLimit
	}
	if result.limit < 1 || result.limit > commerceReportMaxLimit {
		return result, ErrDenied
	}
	return result, nil
}

func (s *Postgres) ListCommerceConsistencyDetails(ctx context.Context, actorID uuid.UUID, input CommerceConsistencyFilter) (CommerceConsistencyPage, error) {
	filter, err := normalizeCommerceConsistencyFilter(input, time.Now())
	if err != nil {
		return CommerceConsistencyPage{}, err
	}
	tx, err := s.commerceReportingTx(ctx, actorID)
	if err != nil {
		return CommerceConsistencyPage{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx, commerceConsistencyDetailsSQL, filter.from, filter.to,
		filter.accountID, filter.provider, filter.category, filter.hasCursor, filter.afterAt,
		filter.afterEntity, filter.afterCategory, filter.limit+1)
	if err != nil {
		return CommerceConsistencyPage{}, err
	}
	result := CommerceConsistencyPage{Rows: make([]CommerceConsistencyDetail, 0, filter.limit)}
	for rows.Next() {
		var item CommerceConsistencyDetail
		if err := rows.Scan(&item.Category, &item.Severity, &item.RecommendedAction,
			&item.EntityID, &item.OrderID, &item.OrderNo, &item.AccountID, &item.Provider,
			&item.OrderStatus, &item.TransactionStates, &item.TransactionCount,
			&item.GrantStates, &item.GrantCount, &item.MaterializedPlan, &item.ExpectedPlan,
			&item.OccurredAt); err != nil {
			rows.Close()
			return CommerceConsistencyPage{}, err
		}
		result.Rows = append(result.Rows, item)
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return CommerceConsistencyPage{}, err
	}
	rows.Close()
	if len(result.Rows) > filter.limit {
		result.Rows = result.Rows[:filter.limit]
		last := result.Rows[len(result.Rows)-1]
		result.NextCursor = &CommerceConsistencyCursor{
			OccurredAt: last.OccurredAt, EntityID: last.EntityID, Category: last.Category,
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return CommerceConsistencyPage{}, err
	}
	return result, nil
}

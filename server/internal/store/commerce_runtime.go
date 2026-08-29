package store

// Runtime lookups used by the payment orchestrator. These methods return only
// the canonical state needed to drive an existing state machine; mutations
// remain in commerce.go where their transactional invariants are enforced.

import (
	"context"
	"errors"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

// GetActivePaymentConfiguration resolves the one validated configuration that
// is currently enabled for a provider and scene. SecretRefs contains external
// references only; secret bytes are never stored in PostgreSQL.
func (s *Postgres) GetActivePaymentConfiguration(ctx context.Context, provider, scene string) (PaymentChannel, PaymentChannelVersion, error) {
	var channel PaymentChannel
	var version PaymentChannelVersion
	err := s.Pool.QueryRow(ctx, `
		SELECT c.id,c.provider,c.scene,c.enabled,c.active_version_id,c.health_status,
		       c.last_webhook_at,coalesce(c.last_error_code,''),c.created_at,c.updated_at,
		       v.id,v.channel_id,v.version,v.environment,v.public_config,v.secret_refs,
		       v.status,v.validated_at,v.validation_summary,v.created_by,v.created_at
		FROM payment_channels c
		JOIN payment_channel_versions v ON v.id=c.active_version_id AND v.channel_id=c.id
		WHERE c.provider=$1 AND c.scene=$2 AND c.enabled
		  AND c.health_status='HEALTHY' AND v.status='VALIDATED'`, provider, scene).Scan(
		&channel.ID, &channel.Provider, &channel.Scene, &channel.Enabled,
		&channel.ActiveVersionID, &channel.HealthStatus, &channel.LastWebhookAt,
		&channel.LastErrorCode, &channel.CreatedAt, &channel.UpdatedAt,
		&version.ID, &version.ChannelID, &version.Version, &version.Environment,
		&version.PublicConfig, &version.SecretRefs, &version.Status,
		&version.ValidatedAt, &version.ValidationSummary, &version.CreatedBy,
		&version.CreatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return PaymentChannel{}, PaymentChannelVersion{}, ErrPaymentChannelUnavailable
	}
	return channel, version, err
}

func (s *Postgres) GetOrder(ctx context.Context, accountID, orderID uuid.UUID) (Order, error) {
	item, err := scanOrder(s.Pool.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE id=$1 AND account_id=$2`, orderID, accountID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) GetOrderByID(ctx context.Context, orderID uuid.UUID) (Order, error) {
	item, err := scanOrder(s.Pool.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE id=$1`, orderID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) GetOrderByNumber(ctx context.Context, orderNo string) (Order, error) {
	item, err := scanOrder(s.Pool.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE order_no=$1`, orderNo))
	if errors.Is(err, pgx.ErrNoRows) {
		return Order{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) GetPendingPaymentTransaction(ctx context.Context, orderID uuid.UUID) (PaymentTransaction, error) {
	item, err := scanPaymentTransaction(s.Pool.QueryRow(ctx, `
		SELECT id,order_id,provider,provider_trade_no_hash,status,amount_minor,
		       currency,payment_payload,failure_code,requested_at,paid_at,updated_at
		FROM payment_transactions
		WHERE order_id=$1 AND status='PENDING'
		ORDER BY requested_at DESC,id DESC LIMIT 1`, orderID))
	if errors.Is(err, pgx.ErrNoRows) {
		return PaymentTransaction{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) GetRefundByNumber(ctx context.Context, refundNo string) (Refund, error) {
	item, err := scanRefund(s.Pool.QueryRow(ctx, `SELECT `+refundColumns+`
		FROM refunds WHERE refund_no=$1`, refundNo))
	if errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, ErrNotFound
	}
	return item, err
}

func (s *Postgres) GetRefundByID(ctx context.Context, refundID uuid.UUID) (Refund, error) {
	item, err := scanRefund(s.Pool.QueryRow(ctx, `SELECT `+refundColumns+`
		FROM refunds WHERE id=$1`, refundID))
	if errors.Is(err, pgx.ErrNoRows) {
		return Refund{}, ErrNotFound
	}
	return item, err
}

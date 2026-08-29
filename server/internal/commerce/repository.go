package commerce

import (
	"context"
	"encoding/json"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

// Repository is the persistence boundary used by Service. It intentionally
// exposes state-machine operations instead of a database handle so commerce
// orchestration can be tested without PostgreSQL and cannot bypass store
// invariants.
type Repository interface {
	GetActivePaymentConfiguration(ctx context.Context, provider, scene string) (store.PaymentChannel, store.PaymentChannelVersion, error)

	CreateOrder(ctx context.Context, accountID uuid.UUID, input store.CreateOrderInput) (store.Order, error)
	GetOrder(ctx context.Context, accountID, orderID uuid.UUID) (store.Order, error)
	GetOrderByID(ctx context.Context, orderID uuid.UUID) (store.Order, error)
	GetOrderByNumber(ctx context.Context, orderNo string) (store.Order, error)
	GetPendingPaymentTransaction(ctx context.Context, orderID uuid.UUID) (store.PaymentTransaction, error)
	PersistPaymentInitiation(ctx context.Context, accountID, orderID, transactionID uuid.UUID, payload json.RawMessage) (store.PaymentTransaction, error)
	MarkOrderPaid(ctx context.Context, orderNo, provider string, transactionID uuid.UUID, tradeHash [32]byte, amount int64, currency string, paidAt time.Time) (store.Order, error)
	FulfillOrder(ctx context.Context, orderID uuid.UUID) (store.Order, store.Entitlement, error)

	RecordPaymentNotification(ctx context.Context, input store.PaymentNotificationInput) (store.PaymentNotification, bool, error)
	CompletePaymentNotification(ctx context.Context, notificationID uuid.UUID, success bool, failureCode string) error

	CreateRefund(ctx context.Context, actorID, orderID uuid.UUID, amount int64, reason, traceID string, approvalExpiresAt time.Time) (store.Refund, error)
	ApproveRefund(ctx context.Context, actorID, refundID uuid.UUID, expectedVersion int64, traceID string) (store.Refund, bool, error)
	GetRefundByID(ctx context.Context, refundID uuid.UUID) (store.Refund, error)
	GetRefundByNumber(ctx context.Context, refundNo string) (store.Refund, error)
	CompleteRefund(ctx context.Context, refundID uuid.UUID, providerRefHash [32]byte, success bool, failureCode string) (store.Refund, error)
}

var _ Repository = (*store.Postgres)(nil)

// ChannelRepository is the smaller boundary needed by the default gateway
// resolver. Keeping it separate makes configuration validation independently
// testable.
type ChannelRepository interface {
	GetActivePaymentConfiguration(ctx context.Context, provider, scene string) (store.PaymentChannel, store.PaymentChannelVersion, error)
}

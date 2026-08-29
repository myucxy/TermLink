// Package payment implements the provider boundary for server-side Web
// payments. Callers must grant entitlements only from provider-authoritative
// QueryResult or WebhookResult values, never from a browser redirect.
package payment

import (
	"context"
	"errors"
	"net/http"
	"time"
)

type Provider string

const (
	ProviderAlipay    Provider = "ALIPAY"
	ProviderWeChatPay Provider = "WECHAT_PAY"
	CurrencyCNY                = "CNY"
)

type PaymentFlow string

const (
	FlowNative PaymentFlow = "NATIVE"
	FlowWeb    PaymentFlow = "WEB"
)

type InstructionKind string

const (
	InstructionQRCode   InstructionKind = "QR_CODE"
	InstructionRedirect InstructionKind = "REDIRECT"
)

type PaymentStatus string

const (
	StatusPending   PaymentStatus = "PENDING"
	StatusSucceeded PaymentStatus = "SUCCEEDED"
	StatusClosed    PaymentStatus = "CLOSED"
	StatusFailed    PaymentStatus = "FAILED"
	StatusRefunding PaymentStatus = "REFUNDING"
	StatusRefunded  PaymentStatus = "REFUNDED"
	StatusUnknown   PaymentStatus = "UNKNOWN"
)

type WebhookEventType string

const (
	WebhookPayment WebhookEventType = "PAYMENT"
	WebhookRefund  WebhookEventType = "REFUND"
)

var (
	ErrInvalidConfig    = errors.New("payment: invalid gateway configuration")
	ErrInvalidRequest   = errors.New("payment: invalid request")
	ErrInvalidSignature = errors.New("payment: invalid provider signature")
	ErrReplay           = errors.New("payment: message outside replay window")
	ErrPayloadTooLarge  = errors.New("payment: payload too large")
	ErrMalformedPayload = errors.New("payment: malformed provider payload")
	ErrProviderRejected = errors.New("payment: provider rejected request")
	ErrUnsupported      = errors.New("payment: unsupported operation")
)

type OrderRequest struct {
	MerchantOrderID string
	Subject         string
	AmountMinor     int64
	Currency        string
	Flow            PaymentFlow
	ExpiresAt       time.Time
}

type PaymentInstruction struct {
	Provider              Provider
	MerchantOrderID       string
	Kind                  InstructionKind
	QRCodeURL             string
	RedirectURL           string
	ProviderTransactionID string
	ExpiresAt             time.Time
}

type QueryResult struct {
	Provider              Provider
	MerchantOrderID       string
	ProviderTransactionID string
	Status                PaymentStatus
	AmountMinor           int64
	Currency              string
	PaidAt                time.Time
	FailureCode           string
}

type RefundRequest struct {
	MerchantOrderID  string
	MerchantRefundID string
	AmountMinor      int64
	TotalAmountMinor int64
	Currency         string
	Reason           string
}

type RefundResult struct {
	Provider         Provider
	MerchantOrderID  string
	MerchantRefundID string
	ProviderRefundID string
	Status           PaymentStatus
	AmountMinor      int64
	Currency         string
}

type WebhookResult struct {
	Provider              Provider
	EventID               string
	EventType             WebhookEventType
	MerchantOrderID       string
	ProviderTransactionID string
	MerchantRefundID      string
	ProviderRefundID      string
	Status                PaymentStatus
	AmountMinor           int64
	Currency              string
	OccurredAt            time.Time
}

// Gateway is implemented by each payment provider. VerifyWebhook must be
// called with the original body bytes and headers. A successful browser return
// is deliberately absent from this interface because it is not proof of
// payment.
type Gateway interface {
	Provider() Provider
	CreatePayment(context.Context, OrderRequest) (PaymentInstruction, error)
	QueryPayment(context.Context, string) (QueryResult, error)
	HealthCheck(context.Context) error
	Refund(context.Context, RefundRequest) (RefundResult, error)
	QueryRefund(context.Context, RefundRequest) (RefundResult, error)
	VerifyWebhook(context.Context, http.Header, []byte) (WebhookResult, error)
}

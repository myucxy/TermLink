package store

import (
	"context"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"regexp"
	"strings"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

var (
	ErrAdminOrderNotClosable         = errors.New("order is not safely closable")
	ErrAdminCloseIdempotencyConflict = errors.New("admin close idempotency conflict")
)

var adminOrderCloseReasonCode = regexp.MustCompile(`^[A-Z][A-Z0-9_]{2,63}$`)

func ValidAdminOrderCloseReasonCode(value string) bool {
	return adminOrderCloseReasonCode.MatchString(strings.ToUpper(strings.TrimSpace(value)))
}

type AdminCloseOrderInput struct {
	ActorID        uuid.UUID
	ActorDeviceID  uuid.UUID
	OrderID        uuid.UUID
	ReasonCode     string
	IdempotencyKey string
	RequestHash    [32]byte
	TraceID        string
}

type AdminCloseOrderResult struct {
	Order    Order
	Replayed bool
}

type adminCloseReceipt struct {
	OrderID uuid.UUID `json:"order_id"`
	Status  string    `json:"status"`
}

const adminCloseOrderSQL = `UPDATE orders target
	SET status='CLOSED',closed_at=coalesce(closed_at,now()),updated_at=now()
	WHERE target.id=$1
	  AND target.status IN ('CREATED','PENDING_PAYMENT')
	  AND NOT EXISTS (
		SELECT 1 FROM payment_transactions transaction
		WHERE transaction.order_id=target.id
		  AND transaction.status IN ('SUCCEEDED','REFUNDED')
	  )
	RETURNING ` + orderColumns

const adminCloseTransactionsSQL = `UPDATE payment_transactions
	SET status='CLOSED',failure_code='ADMIN_ORDER_CLOSED',updated_at=now()
	WHERE order_id=$1 AND status IN ('INITIATED','PENDING')`

const adminCloseOutboxSQL = `INSERT INTO outbox(event_type,aggregate_id,payload)
	VALUES('commerce.order.closed',$1,jsonb_build_object(
		'order_id',$1::uuid::text,'account_id',$2::uuid::text,'status','CLOSED'))`

// AdminCloseUnpaidOrder is an administrator-only, operation-idempotent state
// transition. It intentionally does not call account-scoped CancelOrder: the
// administrator may operate across accounts, while the database status
// predicate remains the authority that closes the payment race fail-closed.
func (s *Postgres) AdminCloseUnpaidOrder(ctx context.Context, input AdminCloseOrderInput) (AdminCloseOrderResult, error) {
	return s.adminCloseUnpaidOrder(ctx, input, false)
}

// AdminCloseUnpaidOrderFromWeb is the browser-console capability. The Web
// adapter has already enforced an authenticated server-side session, CSRF,
// recent MFA, reason, and its own idempotency fence. Unlike the device API a
// browser session has no TermLink device UUID, so its audit actor_device_id is
// deliberately NULL and the metadata records the WEB actor channel.
func (s *Postgres) AdminCloseUnpaidOrderFromWeb(ctx context.Context, input AdminCloseOrderInput) (AdminCloseOrderResult, error) {
	return s.adminCloseUnpaidOrder(ctx, input, true)
}

func (s *Postgres) adminCloseUnpaidOrder(ctx context.Context, input AdminCloseOrderInput, allowDeviceLessWeb bool) (AdminCloseOrderResult, error) {
	input.ReasonCode = strings.ToUpper(strings.TrimSpace(input.ReasonCode))
	input.IdempotencyKey = strings.TrimSpace(input.IdempotencyKey)
	input.TraceID = strings.TrimSpace(input.TraceID)
	if !validAdminCloseInput(input, !allowDeviceLessWeb) {
		return AdminCloseOrderResult{}, ErrDenied
	}
	tx, err := s.commerceTx(ctx, input.ActorID, true, false)
	if err != nil {
		return AdminCloseOrderResult{}, err
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if input.ActorDeviceID != uuid.Nil {
		var trustedDevice bool
		if err = tx.QueryRow(ctx, `SELECT true FROM devices
			WHERE id=$1 AND user_id=$2 AND trust_state='TRUSTED'`, input.ActorDeviceID, input.ActorID).Scan(&trustedDevice); errors.Is(err, pgx.ErrNoRows) {
			return AdminCloseOrderResult{}, ErrDenied
		} else if err != nil {
			return AdminCloseOrderResult{}, err
		}
	}

	if _, err = tx.Exec(ctx, `DELETE FROM idempotency_keys
		WHERE user_id=$1 AND idempotency_key=$2 AND expires_at<now()`, input.ActorID, input.IdempotencyKey); err != nil {
		return AdminCloseOrderResult{}, err
	}
	var storedHash []byte
	var storedStatus *int
	var storedBody json.RawMessage
	err = tx.QueryRow(ctx, `SELECT request_hash,response_status,response_body
		FROM idempotency_keys WHERE user_id=$1 AND idempotency_key=$2 FOR UPDATE`, input.ActorID, input.IdempotencyKey).
		Scan(&storedHash, &storedStatus, &storedBody)
	switch {
	case err == nil:
		if len(storedHash) != len(input.RequestHash) || subtle.ConstantTimeCompare(storedHash, input.RequestHash[:]) != 1 || storedStatus == nil || *storedStatus != 200 {
			return AdminCloseOrderResult{}, ErrAdminCloseIdempotencyConflict
		}
		var receipt adminCloseReceipt
		if json.Unmarshal(storedBody, &receipt) != nil || receipt.OrderID != input.OrderID || receipt.Status != "CLOSED" {
			return AdminCloseOrderResult{}, ErrAdminCloseIdempotencyConflict
		}
		order, loadErr := scanOrder(tx.QueryRow(ctx, `SELECT `+orderColumns+` FROM orders WHERE id=$1`, input.OrderID))
		if loadErr != nil || order.Status != "CLOSED" {
			if loadErr != nil {
				return AdminCloseOrderResult{}, loadErr
			}
			return AdminCloseOrderResult{}, ErrAdminCloseIdempotencyConflict
		}
		return AdminCloseOrderResult{Order: order, Replayed: true}, tx.Commit(ctx)
	case !errors.Is(err, pgx.ErrNoRows):
		return AdminCloseOrderResult{}, err
	}
	if _, err = tx.Exec(ctx, `INSERT INTO idempotency_keys(
		user_id,idempotency_key,request_hash,expires_at)
		VALUES($1,$2,$3,now()+interval '24 hours')`, input.ActorID, input.IdempotencyKey, input.RequestHash[:]); err != nil {
		return AdminCloseOrderResult{}, err
	}

	order, err := scanOrder(tx.QueryRow(ctx, adminCloseOrderSQL, input.OrderID))
	if errors.Is(err, pgx.ErrNoRows) {
		var exists bool
		if lookupErr := tx.QueryRow(ctx, `SELECT true FROM orders WHERE id=$1`, input.OrderID).Scan(&exists); errors.Is(lookupErr, pgx.ErrNoRows) {
			return AdminCloseOrderResult{}, ErrNotFound
		} else if lookupErr != nil {
			return AdminCloseOrderResult{}, lookupErr
		}
		return AdminCloseOrderResult{}, ErrAdminOrderNotClosable
	}
	if err != nil {
		return AdminCloseOrderResult{}, err
	}
	if _, err = tx.Exec(ctx, adminCloseTransactionsSQL, order.ID); err != nil {
		return AdminCloseOrderResult{}, err
	}
	actorChannel := "DEVICE"
	var actorDeviceID any = input.ActorDeviceID
	if input.ActorDeviceID == uuid.Nil {
		actorChannel = "WEB"
		actorDeviceID = nil
	}
	metadata, _ := json.Marshal(map[string]string{
		"from": "UNPAID", "to": "CLOSED", "reason_code": input.ReasonCode,
		"request_hash": hex.EncodeToString(input.RequestHash[:]), "actor_channel": actorChannel,
	})
	if _, err = tx.Exec(ctx, `INSERT INTO audit_events(
		user_id,actor_device_id,action,target_type,target_id,result,trace_id,metadata)
		VALUES($1,$2,'payment.order.admin_close','order',$3,'SUCCEEDED',$4,$5::jsonb)`,
		input.ActorID, actorDeviceID, order.ID, input.TraceID, metadata); err != nil {
		return AdminCloseOrderResult{}, err
	}
	if _, err = tx.Exec(ctx, adminCloseOutboxSQL, order.ID, order.AccountID); err != nil {
		return AdminCloseOrderResult{}, err
	}
	receipt, err := json.Marshal(adminCloseReceipt{OrderID: order.ID, Status: order.Status})
	if err != nil {
		return AdminCloseOrderResult{}, err
	}
	command, err := tx.Exec(ctx, `UPDATE idempotency_keys
		SET response_status=200,response_body=$4::jsonb
		WHERE user_id=$1 AND idempotency_key=$2 AND request_hash=$3`,
		input.ActorID, input.IdempotencyKey, input.RequestHash[:], receipt)
	if err != nil {
		return AdminCloseOrderResult{}, err
	}
	if command.RowsAffected() != 1 {
		return AdminCloseOrderResult{}, ErrAdminCloseIdempotencyConflict
	}
	if err = tx.Commit(ctx); err != nil {
		return AdminCloseOrderResult{}, err
	}
	return AdminCloseOrderResult{Order: order}, nil
}

func validAdminCloseInput(input AdminCloseOrderInput, requireDevice bool) bool {
	return input.ActorID != uuid.Nil && (!requireDevice || input.ActorDeviceID != uuid.Nil) && input.OrderID != uuid.Nil &&
		ValidAdminOrderCloseReasonCode(input.ReasonCode) &&
		validAdminCloseIdempotencyKey(input.IdempotencyKey) && input.TraceID != "" && len(input.TraceID) <= 256
}

func validAdminCloseIdempotencyKey(value string) bool {
	if len(value) < 8 || len(value) > 128 {
		return false
	}
	for _, character := range value {
		if character < 0x21 || character > 0x7e {
			return false
		}
	}
	return true
}

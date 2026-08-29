package store

import (
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestAdminCloseUnpaidOrderAtomicIdempotentAndPaymentRace(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the admin order close test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	if err := migrate.Up(ctx, databaseURL); err != nil {
		t.Fatal(err)
	}
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()

	adminID, adminDeviceID, accountID := uuid.New(), uuid.New(), uuid.New()
	orderIDs := make([]uuid.UUID, 0, 3)
	transactionIDs := make([]uuid.UUID, 0, 3)
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		if len(orderIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, orderIDs)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE target_type='order' AND target_id=ANY($1::uuid[])`, orderIDs)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM idempotency_keys WHERE user_id=$1`, adminID)
		}
		if len(transactionIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM payment_transactions WHERE id=ANY($1::uuid[])`, transactionIDs)
		}
		if len(orderIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM orders WHERE id=ANY($1::uuid[])`, orderIDs)
		}
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM devices WHERE id=$1`, adminDeviceID)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=ANY($1::uuid[])`, []uuid.UUID{adminID, accountID})
	}()
	if _, err = repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,is_admin,role) VALUES
		($1,$2,true,'ADMIN'),($3,$4,false,'USER')`, adminID, bytes.Repeat([]byte{0x51}, 32), accountID, bytes.Repeat([]byte{0x52}, 32)); err != nil {
		t.Fatal(err)
	}
	if _, err = repository.Pool.Exec(ctx, `INSERT INTO devices(
		id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
		VALUES($1,$2,'ANDROID','Admin close integration device',$3,$4,$5,'TRUSTED')`,
		adminDeviceID, adminID, bytes.Repeat([]byte{0x61}, 32), bytes.Repeat([]byte{0x62}, 32), "admin-close-"+uuid.NewString()); err != nil {
		t.Fatal(err)
	}

	createFixture := func(status string) (Order, PaymentTransaction) {
		t.Helper()
		orderID, transactionID := uuid.New(), uuid.New()
		orderNo := "TL" + uuid.NewString()
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at)
			VALUES($1,$2,$3,'pro',1,'ALIPAY','NATIVE',990,'CNY',$4,$5,now()+interval '1 hour')`,
			orderID, orderNo, accountID, status, "admin-close-fixture-"+uuid.NewString()); err != nil {
			t.Fatal(err)
		}
		transactionStatus := "PENDING"
		if status == "CREATED" {
			transactionStatus = "INITIATED"
		}
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency)
			VALUES($1,$2,'ALIPAY',$3,990,'CNY')`, transactionID, orderID, transactionStatus); err != nil {
			t.Fatal(err)
		}
		orderIDs = append(orderIDs, orderID)
		transactionIDs = append(transactionIDs, transactionID)
		return Order{ID: orderID, OrderNo: orderNo, AccountID: accountID, Provider: "ALIPAY", PaymentScene: "NATIVE", AmountMinor: 990, Currency: "CNY", Status: status}, PaymentTransaction{ID: transactionID, OrderID: orderID, Status: transactionStatus}
	}

	order, transaction := createFixture("PENDING_PAYMENT")
	requestHash := sha256.Sum256([]byte("admin close request"))
	input := AdminCloseOrderInput{ActorID: adminID, ActorDeviceID: adminDeviceID, OrderID: order.ID, ReasonCode: "CUSTOMER_REQUEST", IdempotencyKey: "admin-close-success-01", RequestHash: requestHash, TraceID: uuid.NewString()}
	result, err := repository.AdminCloseUnpaidOrder(ctx, input)
	if err != nil || result.Replayed || result.Order.Status != "CLOSED" || result.Order.AccountID != accountID {
		t.Fatalf("admin close result=%+v error=%v", result, err)
	}
	var transactionStatus string
	if err = repository.Pool.QueryRow(ctx, `SELECT status FROM payment_transactions WHERE id=$1`, transaction.ID).Scan(&transactionStatus); err != nil || transactionStatus != "CLOSED" {
		t.Fatalf("pending transaction status=%q error=%v", transactionStatus, err)
	}
	assertAdminClosePersistence(t, ctx, repository, order.ID, adminDeviceID, 1, 1)
	replay, err := repository.AdminCloseUnpaidOrder(ctx, input)
	if err != nil || !replay.Replayed || replay.Order.Status != "CLOSED" {
		t.Fatalf("admin close replay=%+v error=%v", replay, err)
	}
	assertAdminClosePersistence(t, ctx, repository, order.ID, adminDeviceID, 1, 1)
	conflict := input
	conflict.RequestHash = sha256.Sum256([]byte("different request"))
	if _, err = repository.AdminCloseUnpaidOrder(ctx, conflict); !errors.Is(err, ErrAdminCloseIdempotencyConflict) {
		t.Fatalf("idempotency mismatch error=%v", err)
	}
	createdOrder, createdTransaction := createFixture("CREATED")
	createdInput := AdminCloseOrderInput{
		ActorID: adminID, ActorDeviceID: adminDeviceID, OrderID: createdOrder.ID, ReasonCode: "PAYMENT_ABANDONED",
		IdempotencyKey: "admin-close-created-01", RequestHash: sha256.Sum256([]byte("created close")), TraceID: uuid.NewString(),
	}
	if result, closeErr := repository.AdminCloseUnpaidOrder(ctx, createdInput); closeErr != nil || result.Order.Status != "CLOSED" {
		t.Fatalf("created order close result=%+v error=%v", result, closeErr)
	}
	if err = repository.Pool.QueryRow(ctx, `SELECT status FROM payment_transactions WHERE id=$1`, createdTransaction.ID).Scan(&transactionStatus); err != nil || transactionStatus != "CLOSED" {
		t.Fatalf("initiated transaction status=%q error=%v", transactionStatus, err)
	}

	for _, terminalStatus := range []string{"PAID", "FULFILLING", "FULFILLED", "REFUNDING", "REFUNDED"} {
		terminalOrderID, terminalTransactionID := uuid.New(), uuid.New()
		orderIDs = append(orderIDs, terminalOrderID)
		transactionIDs = append(transactionIDs, terminalTransactionID)
		createdAt := time.Now().UTC().Add(-2 * time.Minute)
		paidAt := createdAt.Add(time.Minute)
		var fulfilledAt *time.Time
		if terminalStatus == "FULFILLED" || terminalStatus == "REFUNDING" || terminalStatus == "REFUNDED" {
			value := time.Now().UTC()
			fulfilledAt = &value
		}
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at,paid_at,fulfilled_at,created_at)
			VALUES($1,$2,$3,'pro',1,'ALIPAY','NATIVE',990,'CNY',$4,$5,now()+interval '1 hour',$6,$7,$8)`,
			terminalOrderID, "TL"+uuid.NewString(), accountID, terminalStatus, "terminal-order-"+uuid.NewString(), paidAt, fulfilledAt, createdAt); err != nil {
			t.Fatal(err)
		}
		paymentStatus := "SUCCEEDED"
		if terminalStatus == "REFUNDED" {
			paymentStatus = "REFUNDED"
		}
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency,paid_at)
			VALUES($1,$2,'ALIPAY',$3,990,'CNY',now())`, terminalTransactionID, terminalOrderID, paymentStatus); err != nil {
			t.Fatal(err)
		}
		terminalInput := AdminCloseOrderInput{
			ActorID: adminID, ActorDeviceID: adminDeviceID, OrderID: terminalOrderID, ReasonCode: "PAYMENT_ABANDONED",
			IdempotencyKey: "terminal-close-" + terminalStatus, RequestHash: sha256.Sum256([]byte(terminalStatus)), TraceID: uuid.NewString(),
		}
		if _, closeErr := repository.AdminCloseUnpaidOrder(ctx, terminalInput); !errors.Is(closeErr, ErrAdminOrderNotClosable) {
			t.Fatalf("terminal order %s close error=%v", terminalStatus, closeErr)
		}
		var retainedStatus string
		if err = repository.Pool.QueryRow(ctx, `SELECT status FROM orders WHERE id=$1`, terminalOrderID).Scan(&retainedStatus); err != nil || retainedStatus != terminalStatus {
			t.Fatalf("terminal order %s changed to %q error=%v", terminalStatus, retainedStatus, err)
		}
	}

	raceOrder, raceTransaction := createFixture("PENDING_PAYMENT")
	tradeHash := sha256.Sum256([]byte("provider trade reference"))
	closeInput := AdminCloseOrderInput{
		ActorID: adminID, ActorDeviceID: adminDeviceID, OrderID: raceOrder.ID, ReasonCode: "PAYMENT_ABANDONED",
		IdempotencyKey: "admin-close-race-01", RequestHash: sha256.Sum256([]byte("race close")), TraceID: uuid.NewString(),
	}
	start := make(chan struct{})
	var closeErr, paymentErr error
	var closeResult AdminCloseOrderResult
	var paymentResult Order
	var wait sync.WaitGroup
	wait.Add(2)
	go func() {
		defer wait.Done()
		<-start
		closeResult, closeErr = repository.AdminCloseUnpaidOrder(ctx, closeInput)
	}()
	go func() {
		defer wait.Done()
		<-start
		paymentResult, paymentErr = repository.MarkOrderPaid(ctx, raceOrder.OrderNo, "ALIPAY", raceTransaction.ID, tradeHash, 990, "CNY", time.Now().UTC())
	}()
	close(start)
	wait.Wait()
	if (closeErr == nil) == (paymentErr == nil) {
		t.Fatalf("close/payment success must be exclusive: close=%+v/%v payment=%+v/%v", closeResult, closeErr, paymentResult, paymentErr)
	}
	var finalStatus string
	if err = repository.Pool.QueryRow(ctx, `SELECT status FROM orders WHERE id=$1`, raceOrder.ID).Scan(&finalStatus); err != nil {
		t.Fatal(err)
	}
	if closeErr == nil && finalStatus != "CLOSED" || paymentErr == nil && finalStatus != "PAID" {
		t.Fatalf("race final status=%q close_error=%v payment_error=%v", finalStatus, closeErr, paymentErr)
	}
}

func assertAdminClosePersistence(t *testing.T, ctx context.Context, repository *Postgres, orderID, actorDeviceID uuid.UUID, wantAudit, wantOutbox int) {
	t.Helper()
	var audits, events int
	var leaked bool
	if err := repository.Pool.QueryRow(ctx, `SELECT
		(SELECT count(*) FROM audit_events WHERE target_id=$1 AND actor_device_id=$2 AND action='payment.order.admin_close'),
		(SELECT count(*) FROM outbox WHERE aggregate_id=$1 AND event_type='commerce.order.closed'),
		EXISTS(SELECT 1 FROM outbox WHERE aggregate_id=$1 AND event_type='commerce.order.closed'
			AND (payload ? 'provider' OR payload ? 'reason' OR payload ? 'idempotency_key' OR payload ? 'payment_payload'))`, orderID, actorDeviceID).
		Scan(&audits, &events, &leaked); err != nil {
		t.Fatal(err)
	}
	if audits != wantAudit || events != wantOutbox || leaked {
		t.Fatalf("admin close audit/outbox/leaked=%d/%d/%t want=%d/%d/false", audits, events, leaked, wantAudit, wantOutbox)
	}
}

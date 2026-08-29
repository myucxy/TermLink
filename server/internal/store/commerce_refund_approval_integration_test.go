package store

import (
	"bytes"
	"context"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/migrate"
)

func TestRefundApprovalStoreStateMachineAndOutbox(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the refund approval store test")
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
	actorID := uuid.New()
	orderIDs := []uuid.UUID{uuid.New(), uuid.New()}
	transactionIDs := []uuid.UUID{uuid.New(), uuid.New()}
	refundIDs := make([]uuid.UUID, 0, 2)
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		if len(refundIDs) > 0 {
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, refundIDs)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM audit_events WHERE target_type='refund' AND target_id=ANY($1::uuid[])`, refundIDs)
			_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM refunds WHERE id=ANY($1::uuid[])`, refundIDs)
		}
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM payment_transactions WHERE id=ANY($1::uuid[])`, transactionIDs)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM orders WHERE id=ANY($1::uuid[])`, orderIDs)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=$1`, actorID)
	}()
	if _, err = repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,is_admin,role) VALUES($1,$2,true,'ADMIN')`, actorID, bytes.Repeat([]byte{0x43}, 32)); err != nil {
		t.Fatal(err)
	}
	for index := range orderIDs {
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at,paid_at,fulfilled_at)
			VALUES($1,$2,$3,'pro',1,'ALIPAY','NATIVE',990,'CNY','FULFILLED',$4,now()+interval '1 hour',now(),now())`,
			orderIDs[index], "TL"+uuid.NewString(), actorID, "refund-approval-"+uuid.NewString()); err != nil {
			t.Fatal(err)
		}
		if _, err = repository.Pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency,paid_at)
			VALUES($1,$2,'ALIPAY','SUCCEEDED',990,'CNY',now())`, transactionIDs[index], orderIDs[index]); err != nil {
			t.Fatal(err)
		}
	}

	requested, err := repository.CreateRefund(ctx, actorID, orderIDs[0], 990, "customer requested cancellation", "refund-request-trace", time.Now().UTC().Add(15*time.Minute))
	if err != nil {
		t.Fatal(err)
	}
	refundIDs = append(refundIDs, requested.ID)
	if requested.Status != "APPROVAL_PENDING" || requested.Version != 1 || requested.ApprovedBy != nil || requested.ApprovedAt != nil || requested.ApprovalExpiresAt == nil {
		t.Fatalf("requested refund = %+v", requested)
	}
	assertRefundOrderStatus(t, ctx, repository, orderIDs[0], "FULFILLED")
	assertRefundApprovalPersistence(t, ctx, repository, requested.ID, 1, 1)

	approved, transitioned, err := repository.ApproveRefund(ctx, actorID, requested.ID, 1, "refund-confirm-trace")
	if err != nil || !transitioned {
		t.Fatalf("approve refund transitioned=%t error=%v", transitioned, err)
	}
	if approved.Status != "CREATED" || approved.Version != 2 || approved.ApprovedBy == nil || *approved.ApprovedBy != actorID || approved.ApprovedAt == nil {
		t.Fatalf("approved refund = %+v", approved)
	}
	assertRefundOrderStatus(t, ctx, repository, orderIDs[0], "REFUNDING")
	assertRefundApprovalPersistence(t, ctx, repository, requested.ID, 2, 2)

	replayed, transitioned, err := repository.ApproveRefund(ctx, actorID, requested.ID, 1, "refund-confirm-replay")
	if err != nil || transitioned || replayed.ID != requested.ID || replayed.Version != 2 {
		t.Fatalf("approval replay = %+v transitioned=%t error=%v", replayed, transitioned, err)
	}
	assertRefundApprovalPersistence(t, ctx, repository, requested.ID, 2, 2)
	if _, _, err = repository.ApproveRefund(ctx, actorID, requested.ID, 2, "wrong-version"); !errors.Is(err, ErrRefundVersionConflict) {
		t.Fatalf("wrong approval version error=%v", err)
	}

	expired, err := repository.CreateRefund(ctx, actorID, orderIDs[1], 990, "expired approval fixture", "expired-request", time.Now().UTC().Add(15*time.Minute))
	if err != nil {
		t.Fatal(err)
	}
	refundIDs = append(refundIDs, expired.ID)
	if _, err = repository.Pool.Exec(ctx, `UPDATE refunds SET requested_at=now()-interval '2 minutes',approval_expires_at=now()-interval '1 minute' WHERE id=$1`, expired.ID); err != nil {
		t.Fatal(err)
	}
	if _, _, err = repository.ApproveRefund(ctx, actorID, expired.ID, 1, "expired-confirm"); !errors.Is(err, ErrRefundApprovalExpired) {
		t.Fatalf("expired approval error=%v", err)
	}
	assertRefundOrderStatus(t, ctx, repository, orderIDs[1], "FULFILLED")
}

func assertRefundOrderStatus(t *testing.T, ctx context.Context, repository *Postgres, orderID uuid.UUID, want string) {
	t.Helper()
	var status string
	if err := repository.Pool.QueryRow(ctx, `SELECT status FROM orders WHERE id=$1`, orderID).Scan(&status); err != nil || status != want {
		t.Fatalf("order %s status=%q error=%v, want %q", orderID, status, err, want)
	}
}

func assertRefundApprovalPersistence(t *testing.T, ctx context.Context, repository *Postgres, refundID uuid.UUID, wantAudits, wantOutbox int) {
	t.Helper()
	var audits, events int
	var hasSensitivePayload bool
	if err := repository.Pool.QueryRow(ctx, `SELECT
		(SELECT count(*) FROM audit_events WHERE target_type='refund' AND target_id=$1),
		(SELECT count(*) FROM outbox WHERE event_type='refund.approval.changed' AND aggregate_id=$1),
		EXISTS(SELECT 1 FROM outbox WHERE event_type='refund.approval.changed' AND aggregate_id=$1
			AND (payload ? 'reason' OR payload ? 'provider' OR payload ? 'approved_by' OR payload ? 'requested_by'))`, refundID).
		Scan(&audits, &events, &hasSensitivePayload); err != nil {
		t.Fatal(err)
	}
	if audits != wantAudits || events != wantOutbox || hasSensitivePayload {
		t.Fatalf("refund %s audit/outbox/sensitive=%d/%d/%t, want %d/%d/false", refundID, audits, events, hasSensitivePayload, wantAudits, wantOutbox)
	}
}

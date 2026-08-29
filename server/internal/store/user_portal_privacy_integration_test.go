package store

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestUserPortalPrivacyStateMachinesIntegration(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the user-portal privacy integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()

	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "user_portal_privacy_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err := admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupCtx, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop isolated user-portal privacy schema: %v", cleanupErr)
		}
	}()

	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	config.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	defer pool.Close()
	applyUserPortalPrivacyMigrations(t, ctx, pool)

	repository := &Postgres{Pool: pool}
	accountA, accountB, pcA := seedUserPortalPrivacyAccounts(t, ctx, pool)
	legalRecords := seedUserPortalAccountDeletionLegalRecords(t, ctx, pool, accountA)
	testUserPortalDataExportStateMachine(t, ctx, repository, pool, accountA, accountB)
	testUserPortalPCPolicyStateMachine(t, ctx, repository, accountA, accountB, pcA)
	testUserPortalAccountDeletionStateMachine(t, ctx, repository, pool, accountA, accountB, legalRecords)
}

func applyUserPortalPrivacyMigrations(t *testing.T, ctx context.Context, pool *pgxpool.Pool) {
	t.Helper()
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	for version := 1; version <= int(migrations.LatestVersion); version++ {
		prefix := fmt.Sprintf("%06d_", version)
		name := ""
		for _, entry := range entries {
			if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), ".up.sql") {
				name = entry.Name()
				break
			}
		}
		if name == "" {
			t.Fatalf("embedded migration %d was not found", version)
		}
		script, err := migrations.Files.ReadFile(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := pool.Exec(ctx, string(script)); err != nil {
			t.Fatalf("execute %s: %v", name, err)
		}
	}
}

func seedUserPortalPrivacyAccounts(t *testing.T, ctx context.Context, pool *pgxpool.Pool) (uuid.UUID, uuid.UUID, uuid.UUID) {
	t.Helper()
	accountA, accountB := uuid.New(), uuid.New()
	pcA := uuid.New()
	deviceA, deviceB := uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `
		INSERT INTO users(id,oidc_subject_hash) VALUES($1,$2),($3,$4)`,
		accountA, privacyRepeatedByte(0x11), accountB, privacyRepeatedByte(0x12)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO devices(
			id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state
		) VALUES
			($1,$2,'WINDOWS_PC','Account A PC',$3,$4,'privacy-a','TRUSTED'),
			($5,$6,'WINDOWS_PC','Account B PC',$7,$8,'privacy-b','TRUSTED')`,
		deviceA, accountA, privacyRepeatedByte(0x21), privacyRepeatedByte(0x31),
		deviceB, accountB, privacyRepeatedByte(0x22), privacyRepeatedByte(0x32)); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO pcs(id,owner_user_id,device_id,name,agent_version)
		VALUES($1,$2,$3,'Account A PC','integration')`, pcA, accountA, deviceA); err != nil {
		t.Fatal(err)
	}
	return accountA, accountB, pcA
}

type userPortalAccountDeletionLegalRecords struct {
	orderID       uuid.UUID
	transactionID uuid.UUID
	refundID      uuid.UUID
	orderNumber   string
}

func seedUserPortalAccountDeletionLegalRecords(t *testing.T, ctx context.Context, pool *pgxpool.Pool, accountID uuid.UUID) userPortalAccountDeletionLegalRecords {
	t.Helper()
	fixture := userPortalAccountDeletionLegalRecords{
		orderID: uuid.New(), transactionID: uuid.New(), refundID: uuid.New(),
		orderNumber: "TL-DELETE-LEGAL-0001",
	}
	created := time.Now().UTC().Add(-time.Hour).Truncate(time.Microsecond)
	if _, err := pool.Exec(ctx, `
		INSERT INTO orders(
			id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,
			amount_minor,currency,status,idempotency_key,expires_at,closed_at,created_at,updated_at
		) VALUES($1,$2,$3,'free',1,'ALIPAY','NATIVE',100,'CNY','CLOSED',
		         'customer-sensitive-key',$4,$5,$5,$5)`,
		fixture.orderID, fixture.orderNumber, accountID, created.Add(30*time.Minute), created); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO payment_transactions(
			id,order_id,provider,status,amount_minor,currency,payment_payload,failure_code,requested_at,updated_at
		) VALUES($1,$2,'ALIPAY','FAILED',100,'CNY','{"private":"remove"}','DECLINED',$3,$3)`,
		fixture.transactionID, fixture.orderID, created); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO payment_notifications(
			id,provider,notification_id_hash,order_no,signature_valid,processing_state,
			payload_ciphertext,payload_digest,received_at,processed_at
		) VALUES($1,'ALIPAY',$2,$3,true,'PROCESSED',$4,$5,$6,$6)`,
		uuid.New(), privacyRepeatedByte(0x51), fixture.orderNumber, []byte("private-payment-payload"), privacyRepeatedByte(0x52), created); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO refunds(
			id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by,
			requested_at,completed_at,updated_at,approved_by,approved_at
		) VALUES($1,'RF-DELETE-LEGAL-0001',$2,$3,50,'customer private reason','CLOSED',
		         $4,$5,$5,$5,$4,$5)`,
		fixture.refundID, fixture.orderID, fixture.transactionID, accountID, created); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `
		INSERT INTO audit_events(user_id,action,target_type,target_id,result,trace_id)
		VALUES($1,'account.legal.retention.fixture','account',$1,'SUCCEEDED','trace-legal-fixture')`, accountID); err != nil {
		t.Fatal(err)
	}
	return fixture
}

func testUserPortalDataExportStateMachine(t *testing.T, ctx context.Context, repository *Postgres, pool *pgxpool.Pool, accountA, accountB uuid.UUID) {
	t.Helper()
	requested, err := repository.RequestUserPortalDataExport(ctx, accountA, "export-request-1", "trace-export-request")
	if err != nil {
		t.Fatal(err)
	}
	replayed, err := repository.RequestUserPortalDataExport(ctx, accountA, "export-request-1", "trace-export-request-retry")
	if err != nil || replayed.ID != requested.ID {
		t.Fatalf("idempotent export request=(%s,%v), want %s", replayed.ID, err, requested.ID)
	}
	if _, err := repository.GetUserPortalDataExport(ctx, accountB, requested.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account export lookup error=%v, want ErrDenied", err)
	}

	processorA, processorB := uuid.New(), uuid.New()
	first, err := repository.ClaimUserPortalDataExports(ctx, processorA, time.Minute, 1)
	if err != nil || len(first) != 1 || first[0].ID != requested.ID || first[0].ProcessingFence != 1 {
		t.Fatalf("first export claim=%+v error=%v", first, err)
	}
	if _, err := pool.Exec(ctx, `UPDATE data_export_requests SET processor_lease_until=now()-interval '1 second' WHERE id=$1`, requested.ID); err != nil {
		t.Fatal(err)
	}
	second, err := repository.ClaimUserPortalDataExports(ctx, processorB, time.Minute, 1)
	if err != nil || len(second) != 1 || second[0].ProcessingFence != first[0].ProcessingFence+1 {
		t.Fatalf("reclaimed export=%+v error=%v", second, err)
	}
	completion := UserPortalDataExportCompletion{
		ArtifactObjectKey: fmt.Sprintf("accounts/%s/privacy-exports/%s/%d.bin", accountA, requested.ID, second[0].ProcessingFence),
		ArtifactSHA256:    privacyRepeatedByte(0x41),
		ArtifactSizeBytes: 512,
		ExpiresAt:         time.Now().UTC().Add(24 * time.Hour).Truncate(time.Microsecond),
	}
	if err := repository.CompleteUserPortalDataExport(ctx, processorA, requested.ID, first[0].ProcessingFence, completion, "trace-stale-export"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale export processor error=%v, want ErrConflict", err)
	}
	if err := repository.CompleteUserPortalDataExport(ctx, processorB, requested.ID, second[0].ProcessingFence, completion, "trace-export-ready"); err != nil {
		t.Fatal(err)
	}
	if err := repository.CompleteUserPortalDataExport(ctx, processorB, requested.ID, second[0].ProcessingFence, completion, "trace-export-ready-retry"); err != nil {
		t.Fatalf("idempotent export completion: %v", err)
	}
	var auditCount, outboxCount int
	if err := pool.QueryRow(ctx, `
		SELECT
			(SELECT count(*) FROM audit_events WHERE user_id=$1 AND target_id=$2
			 AND action IN ('user_portal.data_export.request','user_portal.data_export.ready')),
			(SELECT count(*) FROM outbox WHERE aggregate_id=$2
			 AND event_type IN ('privacy.data_export.requested','privacy.data_export.ready'))`,
		accountA, requested.ID).Scan(&auditCount, &outboxCount); err != nil || auditCount != 2 || outboxCount != 2 {
		t.Fatalf("export audit/outbox=%d/%d error=%v, want 2/2", auditCount, outboxCount, err)
	}

	failedRequest, err := repository.RequestUserPortalDataExport(ctx, accountB, "export-request-b1", "trace-export-request-b")
	if err != nil {
		t.Fatal(err)
	}
	processorC := uuid.New()
	failedJobs, err := repository.ClaimUserPortalDataExports(ctx, processorC, time.Minute, 1)
	if err != nil || len(failedJobs) != 1 || failedJobs[0].ID != failedRequest.ID {
		t.Fatalf("failed export claim=%+v error=%v", failedJobs, err)
	}
	if err := repository.FailUserPortalDataExport(ctx, processorC, failedRequest.ID, failedJobs[0].ProcessingFence, "OBJECT_UNAVAILABLE", "trace-export-failed"); err != nil {
		t.Fatal(err)
	}
	if err := repository.FailUserPortalDataExport(ctx, processorC, failedRequest.ID, failedJobs[0].ProcessingFence, "OBJECT_UNAVAILABLE", "trace-export-failed-retry"); err != nil {
		t.Fatalf("idempotent export failure: %v", err)
	}
	failedState, err := repository.GetUserPortalDataExport(ctx, accountB, failedRequest.ID)
	if err != nil || failedState.Status != "FAILED" {
		t.Fatalf("failed export state=%+v error=%v", failedState, err)
	}
}

func testUserPortalPCPolicyStateMachine(t *testing.T, ctx context.Context, repository *Postgres, accountA, accountB, pcA uuid.UUID) {
	t.Helper()
	input := UserPortalPCControlPolicyInput{
		RemoteRoutingEnabled: true,
		Mode:                 ControlModeCollaborative,
		LocalPriority:        false,
		ExpectedVersion:      1,
	}
	policy, err := repository.SetUserPortalPCControlPolicy(ctx, accountA, pcA, input, "pc-policy-change-1", "trace-pc-policy")
	if err != nil || policy.Mode != ControlModeCollaborative || policy.LocalPriority || policy.PolicyVersion != 2 {
		t.Fatalf("updated PC policy=%+v error=%v", policy, err)
	}
	stale := input
	stale.Mode = ControlModeTakeover
	if _, err := repository.SetUserPortalPCControlPolicy(ctx, accountA, pcA, stale, "pc-policy-stale-1", "trace-pc-stale"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale PC policy error=%v, want ErrConflict", err)
	}
	stale.ExpectedVersion = 2
	if _, err := repository.SetUserPortalPCControlPolicy(ctx, accountB, pcA, stale, "pc-policy-cross-1", "trace-pc-cross"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account PC policy error=%v, want ErrDenied", err)
	}
}

func testUserPortalAccountDeletionStateMachine(t *testing.T, ctx context.Context, repository *Postgres, pool *pgxpool.Pool, accountA, accountB uuid.UUID, legal userPortalAccountDeletionLegalRecords) {
	t.Helper()
	requested, err := repository.RequestUserPortalAccountDeletionState(ctx, accountA, "delete-request-1", "trace-delete-request")
	if err != nil {
		t.Fatal(err)
	}
	scheduled := time.Now().UTC().Add(time.Second).Truncate(time.Microsecond)
	retention := scheduled.Add(30 * 24 * time.Hour)
	if _, err := repository.ConfirmUserPortalAccountDeletion(ctx, accountA, requested.ID, scheduled, retention, "delete-unbound-confirm-1", "trace-delete-unbound-confirm"); !errors.Is(err, ErrConflict) {
		t.Fatalf("unbound deletion confirmation error=%v, want ErrConflict", err)
	}
	identityCipher := privacyRepeatedBytes(0x61, userPortalIdentityHandleCipherSize)
	replacementCipher := privacyRepeatedBytes(0x62, userPortalIdentityHandleCipherSize)
	if _, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountB, requested.ID, identityCipher, 7, "delete-cross-bind-1", "trace-delete-cross-bind"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account identity binding error=%v, want ErrDenied", err)
	}
	bound, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountA, requested.ID, identityCipher, 7, "delete-bind-1", "trace-delete-bind")
	if err != nil || bound.ID != requested.ID {
		t.Fatalf("identity binding=%+v error=%v", bound, err)
	}
	// A retry may regenerate AES-GCM ciphertext after the successful response
	// is lost. The same idempotency key must replay the stored result rather
	// than treating the randomized nonce as a different operation.
	boundReplay, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountA, requested.ID, replacementCipher, 7, "delete-bind-1", "trace-delete-bind-retry")
	if err != nil || boundReplay.ID != bound.ID || boundReplay.Version != bound.Version {
		t.Fatalf("lost-response identity binding replay=%+v error=%v", boundReplay, err)
	}
	if _, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountA, requested.ID, identityCipher, 7, "delete-bind-same-2", "trace-delete-bind-same"); err != nil {
		t.Fatalf("same identity handle with a new idempotency key: %v", err)
	}
	if _, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountA, requested.ID, replacementCipher, 7, "delete-bind-replace-1", "trace-delete-bind-replace"); !errors.Is(err, ErrConflict) {
		t.Fatalf("identity handle replacement error=%v, want ErrConflict", err)
	}
	confirmed, err := repository.ConfirmUserPortalAccountDeletion(ctx, accountA, requested.ID, scheduled, retention, "delete-confirm-1", "trace-delete-confirm")
	if err != nil || confirmed.Status != "CONFIRMED" {
		t.Fatalf("confirmed deletion=%+v error=%v", confirmed, err)
	}
	confirmedReplay, err := repository.ConfirmUserPortalAccountDeletion(ctx, accountA, requested.ID, scheduled, retention, "delete-confirm-1", "trace-delete-confirm-retry")
	if err != nil || confirmedReplay.ID != confirmed.ID || confirmedReplay.Version != confirmed.Version {
		t.Fatalf("idempotent deletion confirmation=%+v error=%v", confirmedReplay, err)
	}
	if _, err := repository.GetUserPortalAccountDeletion(ctx, accountB, requested.ID); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account deletion lookup error=%v, want ErrDenied", err)
	}
	if _, err := repository.CancelUserPortalAccountDeletion(ctx, accountB, requested.ID, "delete-cross-cancel-1", "trace-delete-cross"); !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-account deletion cancel error=%v, want ErrDenied", err)
	}
	canceled, err := repository.CancelUserPortalAccountDeletion(ctx, accountA, requested.ID, "delete-cancel-1", "trace-delete-cancel")
	if err != nil || canceled.Status != "CANCELED" {
		t.Fatalf("canceled deletion=%+v error=%v", canceled, err)
	}
	canceledReplay, err := repository.CancelUserPortalAccountDeletion(ctx, accountA, requested.ID, "delete-cancel-1", "trace-delete-cancel-retry")
	if err != nil || canceledReplay.ID != canceled.ID || canceledReplay.Version != canceled.Version {
		t.Fatalf("idempotent deletion cancel=%+v error=%v", canceledReplay, err)
	}

	finalRequest, err := repository.RequestUserPortalAccountDeletionState(ctx, accountA, "delete-request-2", "trace-delete-request-2")
	if err != nil || finalRequest.ID == requested.ID {
		t.Fatalf("second deletion request=%+v error=%v", finalRequest, err)
	}
	finalSchedule := time.Now().UTC().Add(50 * time.Millisecond).Truncate(time.Microsecond)
	finalRetention := finalSchedule.Add(30 * 24 * time.Hour)
	finalIdentityCipher := privacyRepeatedBytes(0x71, userPortalIdentityHandleCipherSize)
	if _, err := repository.BindUserPortalAccountDeletionIdentityHandle(ctx, accountA, finalRequest.ID, finalIdentityCipher, 8, "delete-bind-final-1", "trace-delete-bind-final"); err != nil {
		t.Fatal(err)
	}
	if _, err := repository.ConfirmUserPortalAccountDeletion(ctx, accountA, finalRequest.ID, finalSchedule, finalRetention, "delete-confirm-2", "trace-delete-confirm-2"); err != nil {
		t.Fatal(err)
	}
	waitForPrivacySchedule(t, ctx, pool, finalSchedule)
	processorA, processorB := uuid.New(), uuid.New()
	first, err := repository.ClaimUserPortalAccountDeletions(ctx, processorA, time.Minute, 1)
	if err != nil || len(first) != 1 || first[0].ID != finalRequest.ID || first[0].ProcessingFence != 1 ||
		first[0].IdentityHandleKeyVersion != 8 || !bytes.Equal(first[0].IdentityHandle, finalIdentityCipher) {
		t.Fatalf("first deletion claim=%+v error=%v", first, err)
	}
	if _, err := pool.Exec(ctx, `UPDATE account_deletion_requests SET processor_lease_until=now()-interval '1 second' WHERE id=$1`, finalRequest.ID); err != nil {
		t.Fatal(err)
	}
	second, err := repository.ClaimUserPortalAccountDeletions(ctx, processorB, time.Minute, 1)
	if err != nil || len(second) != 1 || second[0].ProcessingFence != first[0].ProcessingFence+1 {
		t.Fatalf("reclaimed deletion=%+v error=%v", second, err)
	}
	if err := repository.CompleteUserPortalAccountDeletion(ctx, processorA, finalRequest.ID, first[0].ProcessingFence, "trace-stale-delete"); !errors.Is(err, ErrConflict) {
		t.Fatalf("stale deletion processor error=%v, want ErrConflict", err)
	}
	if _, err := repository.PrepareUserPortalAccountDeletion(ctx, processorB, finalRequest.ID, second[0].ProcessingFence); err != nil {
		t.Fatal(err)
	}
	if err := repository.FailUserPortalAccountDeletion(ctx, processorB, finalRequest.ID, second[0].ProcessingFence, "RETENTION_BACKEND_UNAVAILABLE", "trace-delete-fail"); err != nil {
		t.Fatal(err)
	}
	if err := repository.FailUserPortalAccountDeletion(ctx, processorB, finalRequest.ID, second[0].ProcessingFence, "RETENTION_BACKEND_UNAVAILABLE", "trace-delete-fail-retry"); err != nil {
		t.Fatalf("idempotent deletion failure: %v", err)
	}
	if _, err := repository.CancelUserPortalAccountDeletion(ctx, accountA, finalRequest.ID, "delete-cancel-locked", "trace-delete-cancel-locked"); !errors.Is(err, ErrDenied) {
		t.Fatalf("locked partially processed deletion cancel error=%v, want ErrDenied", err)
	}
	processorC := uuid.New()
	third, err := repository.ClaimUserPortalAccountDeletions(ctx, processorC, time.Minute, 1)
	if err != nil || len(third) != 1 || third[0].ID != finalRequest.ID || third[0].ProcessingFence != second[0].ProcessingFence+1 {
		t.Fatalf("failed deletion retry claim=%+v error=%v", third, err)
	}
	if err := repository.CompleteUserPortalAccountDeletion(ctx, processorB, finalRequest.ID, second[0].ProcessingFence, "trace-stale-delete-after-fail"); !errors.Is(err, ErrConflict) {
		t.Fatalf("superseded failed processor error=%v, want ErrConflict", err)
	}
	deletedObjects, err := repository.PrepareUserPortalAccountDeletion(ctx, processorC, finalRequest.ID, third[0].ProcessingFence)
	if err != nil || len(deletedObjects) == 0 {
		t.Fatalf("final deletion preparation objects=%d error=%v", len(deletedObjects), err)
	}
	if err := repository.FinalizeUserPortalAccountDeletion(ctx, processorC, finalRequest.ID, third[0].ProcessingFence, deletedObjects, "trace-delete-complete"); err != nil {
		t.Fatal(err)
	}
	if err := repository.FinalizeUserPortalAccountDeletion(ctx, processorC, finalRequest.ID, third[0].ProcessingFence, deletedObjects, "trace-delete-complete-retry"); err != nil {
		t.Fatalf("idempotent deletion completion: %v", err)
	}
	var accountStatus, requestStatus string
	if err := pool.QueryRow(ctx, `
		SELECT account.status,request.status FROM users account
		JOIN account_deletion_requests request ON request.account_id=account.id
		WHERE account.id=$1 AND request.id=$2`, accountA, finalRequest.ID).Scan(&accountStatus, &requestStatus); err != nil {
		t.Fatal(err)
	}
	if accountStatus != "DELETED" || requestStatus != "COMPLETED" {
		t.Fatalf("final deletion states=%s/%s, want DELETED/COMPLETED", accountStatus, requestStatus)
	}
	assertUserPortalAccountDeletionRetention(t, ctx, pool, accountA, finalRequest.ID, legal)
}

func assertUserPortalAccountDeletionRetention(t *testing.T, ctx context.Context, pool *pgxpool.Pool, accountID, requestID uuid.UUID, legal userPortalAccountDeletionLegalRecords) {
	t.Helper()
	tombstone := userPortalDeletedIdentityDigest(accountID, requestID)
	var orderRetained, orderAnonymized, paymentRedacted, notificationRedacted bool
	var refundRedacted, auditRetained, identityAnonymized, privateRowsGone bool
	err := pool.QueryRow(ctx, `
		SELECT
			EXISTS(SELECT 1 FROM orders WHERE id=$2 AND account_id=$1),
			EXISTS(SELECT 1 FROM orders WHERE id=$2 AND idempotency_key='deleted-' || id::text),
			EXISTS(SELECT 1 FROM payment_transactions WHERE id=$3 AND payment_payload='{}'::jsonb),
			EXISTS(SELECT 1 FROM payment_notifications WHERE order_no=$5 AND payload_ciphertext IS NULL),
			EXISTS(SELECT 1 FROM refunds WHERE id=$4 AND reason='ACCOUNT_DELETED'),
			EXISTS(SELECT 1 FROM audit_events WHERE user_id=$1 AND action='account.legal.retention.fixture'),
			EXISTS(SELECT 1 FROM users WHERE id=$1 AND oidc_subject_hash=$6 AND status='DELETED'),
			NOT EXISTS(SELECT 1 FROM pcs WHERE owner_user_id=$1)
			AND NOT EXISTS(SELECT 1 FROM devices WHERE user_id=$1)
			AND NOT EXISTS(SELECT 1 FROM data_export_requests WHERE account_id=$1)
			AND NOT EXISTS(SELECT 1 FROM subscriptions WHERE account_id=$1)
			AND NOT EXISTS(SELECT 1 FROM entitlements WHERE account_id=$1)`,
		accountID, legal.orderID, legal.transactionID, legal.refundID, legal.orderNumber, tombstone[:],
	).Scan(
		&orderRetained, &orderAnonymized, &paymentRedacted, &notificationRedacted,
		&refundRedacted, &auditRetained, &identityAnonymized, &privateRowsGone,
	)
	if err != nil {
		t.Fatal(err)
	}
	if !orderRetained || !orderAnonymized || !paymentRedacted || !notificationRedacted ||
		!refundRedacted || !auditRetained || !identityAnonymized || !privateRowsGone {
		t.Fatalf("retention/anonymization failed order=%t/%t payment=%t notification=%t refund=%t audit=%t identity=%t privateGone=%t",
			orderRetained, orderAnonymized, paymentRedacted, notificationRedacted,
			refundRedacted, auditRetained, identityAnonymized, privateRowsGone)
	}
}

func waitForPrivacySchedule(t *testing.T, ctx context.Context, pool *pgxpool.Pool, scheduled time.Time) {
	t.Helper()
	for {
		var due bool
		if err := pool.QueryRow(ctx, `SELECT now()>=$1`, scheduled).Scan(&due); err != nil {
			t.Fatal(err)
		}
		if due {
			return
		}
		select {
		case <-ctx.Done():
			t.Fatal(ctx.Err())
		case <-time.After(10 * time.Millisecond):
		}
	}
}

func privacyRepeatedByte(value byte) []byte {
	return privacyRepeatedBytes(value, 32)
}

func privacyRepeatedBytes(value byte, size int) []byte {
	result := make([]byte, size)
	for index := range result {
		result[index] = value
	}
	return result
}

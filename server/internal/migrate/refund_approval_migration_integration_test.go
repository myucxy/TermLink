package migrate

import (
	"context"
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

func TestRefundApprovalMigrationUpDownFailClosed(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the refund approval migration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "refund_approval_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		_, _ = admin.Exec(cleanupCtx, "DROP SCHEMA "+quotedSchema+" CASCADE")
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
	for version := 1; version <= 25; version++ {
		name := findMigrationName(t, version, ".up.sql")
		execMigrationFile(t, ctx, pool, name)
	}

	actorID, orderID, transactionID, legacyRefundID := uuid.New(), uuid.New(), uuid.New(), uuid.New()
	if _, err = pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,is_admin) VALUES($1,decode(repeat('42',32),'hex'),true)`, actorID); err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at,paid_at,fulfilled_at)
		VALUES($1,$2,$3,'pro',1,'ALIPAY','NATIVE',990,'CNY','FULFILLED','refund-migration-order',now()+interval '1 hour',now(),now())`,
		orderID, "TL"+strings.ReplaceAll(orderID.String(), "-", ""), actorID); err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency,paid_at)
		VALUES($1,$2,'ALIPAY','SUCCEEDED',990,'CNY',now())`, transactionID, orderID); err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by)
		VALUES($1,$2,$3,$4,990,'legacy approved request','CREATED',$5)`, legacyRefundID, "RF"+strings.ReplaceAll(legacyRefundID.String(), "-", ""), orderID, transactionID, actorID); err != nil {
		t.Fatal(err)
	}

	execMigrationFile(t, ctx, pool, "000026_refund_approval.up.sql")
	var version int64
	var approvedBy uuid.UUID
	var approvedAt time.Time
	if err = pool.QueryRow(ctx, `SELECT version,approved_by,approved_at FROM refunds WHERE id=$1`, legacyRefundID).Scan(&version, &approvedBy, &approvedAt); err != nil {
		t.Fatal(err)
	}
	if version != 1 || approvedBy != actorID || approvedAt.IsZero() {
		t.Fatalf("legacy refund approval backfill = version %d actor %s at %s", version, approvedBy, approvedAt)
	}
	execMigrationFile(t, ctx, pool, "000026_refund_approval.down.sql")
	if _, err = pool.Exec(ctx, `SELECT version FROM refunds LIMIT 1`); err == nil {
		t.Fatal("successful rollback retained refund approval columns")
	}

	execMigrationFile(t, ctx, pool, "000026_refund_approval.up.sql")
	pendingID := uuid.New()
	if _, err = pool.Exec(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by,approval_expires_at)
		VALUES($1,$2,$3,$4,990,'pending approval','APPROVAL_PENDING',$5,now()+interval '15 minutes')`, pendingID, "RF"+strings.ReplaceAll(pendingID.String(), "-", ""), orderID, transactionID, actorID); err != nil {
		t.Fatal(err)
	}
	down, err := migrations.Files.ReadFile("000026_refund_approval.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, string(down)); err == nil || !strings.Contains(strings.ToLower(err.Error()), "pending approval requests exist") {
		t.Fatalf("rollback did not fail closed with a pending request: %v", err)
	}
	if err = pool.QueryRow(ctx, `SELECT version FROM refunds WHERE id=$1`, pendingID).Scan(&version); err != nil || version != 1 {
		t.Fatalf("failed rollback changed pending request: version=%d error=%v", version, err)
	}
	if _, err = pool.Exec(ctx, `DELETE FROM refunds WHERE id=$1`, pendingID); err != nil {
		t.Fatal(err)
	}
	execMigrationFile(t, ctx, pool, "000026_refund_approval.down.sql")
}

func findMigrationName(t *testing.T, version int, suffix string) string {
	t.Helper()
	entries, err := migrations.Files.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	prefix := fmt.Sprintf("%06d_", version)
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), suffix) {
			return entry.Name()
		}
	}
	t.Fatalf("migration %d%s not found", version, suffix)
	return ""
}

func execMigrationFile(t *testing.T, ctx context.Context, pool *pgxpool.Pool, name string) {
	t.Helper()
	contents, err := migrations.Files.ReadFile(name)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = pool.Exec(ctx, string(contents)); err != nil {
		t.Fatalf("execute %s: %v", name, err)
	}
}

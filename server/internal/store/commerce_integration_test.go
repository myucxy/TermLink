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
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/termlink/termlink/server/migrations"
)

func TestCommerceMigrationUpDownAndIntegrity(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required for the commerce migration integration test")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()

	admin, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer admin.Close()
	schema := "commerce_migration_" + strings.ReplaceAll(uuid.NewString(), "-", "")
	quotedSchema := pgx.Identifier{schema}.Sanitize()
	if _, err = admin.Exec(ctx, "CREATE SCHEMA "+quotedSchema); err != nil {
		t.Fatal(err)
	}
	defer func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cleanupCancel()
		if _, cleanupErr := admin.Exec(cleanupCtx, "DROP SCHEMA "+quotedSchema+" CASCADE"); cleanupErr != nil {
			t.Errorf("drop isolated commerce schema: %v", cleanupErr)
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

	applyCommerceFixtureSchema23(t, ctx, pool)
	fixture := seedCommerceMigrationFixture(t, ctx, pool)
	execEmbeddedMigration(t, ctx, pool, "000024_server_web_commerce.up.sql")
	assertCommerceGrantBackfill(t, ctx, pool, fixture)
	assertCommerceDatabaseIntegrity(t, ctx, pool, fixture)
	seedCommerceRollbackCollision(t, ctx, pool, fixture)
	execEmbeddedMigration(t, ctx, pool, "000024_server_web_commerce.down.sql")
	assertCommerceRollback(t, ctx, pool, fixture)
}

type commerceMigrationFixture struct {
	adminID          uuid.UUID
	defaultAccountID uuid.UUID
	googleAccountID  uuid.UUID
	webAccountID     uuid.UUID
	legacyAccountID  uuid.UUID
	googleSubID      uuid.UUID
	webSubID         uuid.UUID
	extraWebSubID    uuid.UUID
	webRenewsAt      time.Time
	alipaySubID      uuid.UUID
}

func applyCommerceFixtureSchema23(t *testing.T, ctx context.Context, pool *pgxpool.Pool) {
	t.Helper()
	for version := 1; version <= 23; version++ {
		name := ""
		entries, err := migrations.Files.ReadDir(".")
		if err != nil {
			t.Fatal(err)
		}
		prefix := fmt.Sprintf("%06d_", version)
		for _, entry := range entries {
			if strings.HasPrefix(entry.Name(), prefix) && strings.HasSuffix(entry.Name(), ".up.sql") {
				name = entry.Name()
				break
			}
		}
		if name == "" {
			t.Fatalf("embedded migration %d was not found", version)
		}
		execEmbeddedMigration(t, ctx, pool, name)
	}
}

func execEmbeddedMigration(t *testing.T, ctx context.Context, pool *pgxpool.Pool, name string) {
	t.Helper()
	sql, err := migrations.Files.ReadFile(name)
	if err != nil {
		t.Fatal(err)
	}
	tx, err := pool.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	if _, err = tx.Exec(ctx, string(sql)); err != nil {
		t.Fatalf("execute %s: %v", name, err)
	}
	if err = tx.Commit(ctx); err != nil {
		t.Fatalf("commit %s: %v", name, err)
	}
}

func seedCommerceMigrationFixture(t *testing.T, ctx context.Context, pool *pgxpool.Pool) commerceMigrationFixture {
	t.Helper()
	f := commerceMigrationFixture{
		adminID:          uuid.New(),
		defaultAccountID: uuid.New(),
		googleAccountID:  uuid.New(),
		webAccountID:     uuid.New(),
		legacyAccountID:  uuid.New(),
		googleSubID:      uuid.New(),
		webSubID:         uuid.New(),
		extraWebSubID:    uuid.New(),
		webRenewsAt:      time.Now().UTC().Add(14 * 24 * time.Hour).Truncate(time.Microsecond),
		alipaySubID:      uuid.MustParse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
	}
	ids := []uuid.UUID{f.adminID, f.defaultAccountID, f.googleAccountID, f.webAccountID, f.legacyAccountID}
	for i, id := range ids {
		digest := make([]byte, 32)
		for j := range digest {
			digest[j] = byte(i + 1)
		}
		role := "USER"
		if id == f.adminID {
			role = "SUPER_ADMIN"
		}
		if _, err := pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,role,is_admin) VALUES($1,$2,$3,$4)`, id, digest, role, role == "SUPER_ADMIN"); err != nil {
			t.Fatal(err)
		}
	}
	googleHash := repeatedByte(0x21)
	webHash := repeatedByte(0x22)
	extraWebHash := repeatedByte(0x23)
	if _, err := pool.Exec(ctx, `
		INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status,renews_at)
		VALUES($1,$2,'GOOGLE_PLAY','google.plus',$3,'free',1,'ACTIVE',NULL),
		      ($4,$5,'WEB','legacy.web.plus',$6,'free',1,'CANCELED',$7),
		      ($8,$2,'WEB','second.active.source',$9,'free',1,'ACTIVE',NULL)`,
		f.googleSubID, f.googleAccountID, googleHash, f.webSubID, f.webAccountID, webHash, f.webRenewsAt,
		f.extraWebSubID, extraWebHash); err != nil {
		t.Fatal(err)
	}
	effective := time.Now().UTC().Add(-24 * time.Hour).Truncate(time.Microsecond)
	invalidLegacyEnd := effective.Add(-time.Hour)
	if _, err := pool.Exec(ctx, `
		INSERT INTO entitlements(
			account_id,plan_key,catalog_version,max_remote_pcs,max_live_sessions,
			feature_flags,version,effective_at,expires_at,grace_until,source,provider_reference_hash
		) VALUES
			($1,'free',1,-1,-1,'{}',1,$5,NULL,NULL,'DEFAULT',NULL),
			($2,'free',1,-1,-1,'{}',1,$5,NULL,NULL,'GOOGLE_PLAY',$6),
			($3,'free',1,-1,-1,'{}',1,$5,NULL,NULL,'WEB',$7),
			($4,'free',1,-1,-1,'{}',1,$5,$8,$8,'MANUAL',NULL)`,
		f.defaultAccountID, f.googleAccountID, f.webAccountID, f.legacyAccountID,
		effective, googleHash, webHash, invalidLegacyEnd); err != nil {
		t.Fatal(err)
	}
	return f
}

func assertCommerceGrantBackfill(t *testing.T, ctx context.Context, pool *pgxpool.Pool, f commerceMigrationFixture) {
	t.Helper()
	type grant struct {
		source   string
		sourceID *uuid.UUID
		endsAt   *time.Time
		grace    *time.Time
		status   string
	}
	var count int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM entitlement_grants`).Scan(&count); err != nil || count != 5 {
		t.Fatalf("backfilled grants=(%d,%v), want 5", count, err)
	}
	load := func(where string, value any) grant {
		t.Helper()
		var item grant
		if err := pool.QueryRow(ctx, `SELECT source_type,source_id,ends_at,grace_until,status FROM entitlement_grants WHERE `+where, value).Scan(&item.source, &item.sourceID, &item.endsAt, &item.grace, &item.status); err != nil {
			t.Fatal(err)
		}
		return item
	}
	if got := load(`account_id=$1 AND source_type='DEFAULT'`, f.defaultAccountID); got.source != "DEFAULT" || got.sourceID != nil || got.status != "ACTIVE" {
		t.Fatalf("DEFAULT grant=%+v", got)
	}
	if got := load(`source_id=$1`, f.googleSubID); got.source != "GOOGLE_PLAY" || got.sourceID == nil || *got.sourceID != f.googleSubID || got.endsAt != nil || got.status != "ACTIVE" {
		t.Fatalf("GOOGLE_PLAY grant=%+v", got)
	}
	if got := load(`source_id=$1`, f.webSubID); got.source != "WEB" || got.sourceID == nil || *got.sourceID != f.webSubID || got.endsAt == nil || !got.endsAt.Equal(f.webRenewsAt) || got.status != "ACTIVE" {
		t.Fatalf("WEB canceled grant=%+v, renews_at=%s", got, f.webRenewsAt)
	}
	if got := load(`source_id=$1`, f.extraWebSubID); got.source != "WEB" || got.sourceID == nil || *got.sourceID != f.extraWebSubID || got.endsAt != nil || got.status != "ACTIVE" {
		t.Fatalf("second active WEB source grant=%+v", got)
	}
	if got := load(`account_id=$1 AND source_type='MANUAL'`, f.legacyAccountID); got.source != "MANUAL" || got.endsAt != nil || got.grace != nil || got.status != "EXPIRED" {
		t.Fatalf("malformed legacy grant=%+v", got)
	}
	var deterministic bool
	if err := pool.QueryRow(ctx, `SELECT id=md5(account_id::text || ':termlink:entitlement-grant:migration:24')::uuid FROM entitlement_grants WHERE account_id=$1 AND source_type='DEFAULT'`, f.defaultAccountID).Scan(&deterministic); err != nil || !deterministic {
		t.Fatalf("deterministic grant identity=(%t,%v)", deterministic, err)
	}
}

func assertCommerceDatabaseIntegrity(t *testing.T, ctx context.Context, pool *pgxpool.Pool, f commerceMigrationFixture) {
	t.Helper()
	batchID, code1, code2 := uuid.New(), uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_batches(id,name,created_by) VALUES($1,'audit',$2)`, batchID, f.adminID); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_codes(id,batch_id,code_hash,code_prefix,max_uses) VALUES($1,$2,$3,'TLI-AAAA',1),($4,$2,$5,'TLI-BBBB',1)`, code1, batchID, repeatedByte(0x31), code2, repeatedByte(0x32)); err != nil {
		t.Fatal(err)
	}
	emailHash := repeatedByte(0x41)
	if _, err := pool.Exec(ctx, `INSERT INTO invitation_redemptions(id,code_id,registration_attempt_id,email_hash,state,reserved_until,provider_accepted_at) VALUES($1,$2,$3,$4,'PROVIDER_ACCEPTED',now()+interval '10 minutes',now())`, uuid.New(), code1, uuid.New(), emailHash); err != nil {
		t.Fatal(err)
	}
	_, err := pool.Exec(ctx, `INSERT INTO invitation_redemptions(id,code_id,registration_attempt_id,email_hash,state,reserved_until,provider_accepted_at) VALUES($1,$2,$3,$4,'PROVIDER_ACCEPTED',now()+interval '10 minutes',now())`, uuid.New(), code2, uuid.New(), emailHash)
	assertPostgresCode(t, err, "23505")
	_, err = pool.Exec(ctx, `INSERT INTO invitation_redemptions(id,code_id,registration_attempt_id,email_hash,state,reserved_until) VALUES($1,$2,$3,$4,'PROVIDER_ACCEPTED',now()+interval '10 minutes')`, uuid.New(), code2, uuid.New(), repeatedByte(0x42))
	assertPostgresCode(t, err, "23514")

	channel1, channel2, version2 := uuid.New(), uuid.New(), uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO payment_channels(id,provider,scene) VALUES($1,'ALIPAY','WEB'),($2,'WECHAT_PAY','WEB')`, channel1, channel2); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO payment_channel_versions(id,channel_id,version,environment,status,validated_at,created_by) VALUES($1,$2,1,'SANDBOX','VALIDATED',now(),$3)`, version2, channel2, f.adminID); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `UPDATE payment_channels SET active_version_id=$2,enabled=true WHERE id=$1`, channel1, version2)
	assertPostgresCode(t, err, "23503")
	_, err = pool.Exec(ctx, `UPDATE payment_channels SET enabled=true WHERE id=$1`, channel1)
	assertPostgresCode(t, err, "23514")
	_, err = pool.Exec(ctx, `INSERT INTO payment_channel_versions(id,channel_id,version,environment,public_config,created_by) VALUES($1,$2,2,'SANDBOX','[]',$3)`, uuid.New(), channel2, f.adminID)
	assertPostgresCode(t, err, "23514")

	order1, order2 := uuid.New(), uuid.New()
	created := time.Now().UTC().Add(-time.Minute)
	expires := created.Add(time.Hour)
	for i, orderID := range []uuid.UUID{order1, order2} {
		if _, err := pool.Exec(ctx, `INSERT INTO orders(id,order_no,account_id,plan_key,catalog_version,provider,payment_scene,amount_minor,currency,status,idempotency_key,expires_at,created_at) VALUES($1,$2,$3,'free',1,'ALIPAY','NATIVE',100,'CNY','CREATED',$4,$5,$6)`, orderID, fmt.Sprintf("TL%032d", i+1), f.defaultAccountID, fmt.Sprintf("order-key-%d", i+1), expires, created); err != nil {
			t.Fatal(err)
		}
	}
	transaction1, transaction2, pendingRetry := uuid.New(), uuid.New(), uuid.New()
	_, err = pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency) VALUES($1,$2,'WECHAT_PAY','PENDING',100,'CNY')`, uuid.New(), order1)
	assertPostgresCode(t, err, "23503")
	_, err = pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency) VALUES($1,$2,'ALIPAY','PENDING',101,'CNY')`, uuid.New(), order1)
	assertPostgresCode(t, err, "23503")
	if _, err := pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency) VALUES($1,$2,'ALIPAY','PENDING',100,'CNY'),($3,$2,'ALIPAY','PENDING',100,'CNY')`, transaction1, order1, pendingRetry); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency) VALUES($1,$2,'ALIPAY','SUCCEEDED',100,'CNY')`, uuid.New(), order2); err == nil {
		t.Fatal("SUCCEEDED transaction without paid_at was accepted")
	}
	if _, err := pool.Exec(ctx, `INSERT INTO payment_transactions(id,order_id,provider,status,amount_minor,currency,paid_at) VALUES($1,$2,'ALIPAY','SUCCEEDED',100,'CNY',now())`, transaction2, order2); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE payment_transactions SET status='SUCCEEDED',paid_at=now() WHERE id=$1`, transaction1); err != nil {
		t.Fatal(err)
	}
	_, err = pool.Exec(ctx, `UPDATE payment_transactions SET status='SUCCEEDED',paid_at=now() WHERE id=$1`, pendingRetry)
	assertPostgresCode(t, err, "23505")
	_, err = pool.Exec(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by) VALUES($1,$2,$3,$4,50,'mismatch','CREATED',$5)`, uuid.New(), "RF000000000000000000000000000001", order1, transaction2, f.adminID)
	assertPostgresCode(t, err, "23503")
	_, err = pool.Exec(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,requested_by) VALUES($1,$2,$3,$4,50,'missing completion','SUCCEEDED',$5)`, uuid.New(), "RF000000000000000000000000000002", order1, transaction1, f.adminID)
	assertPostgresCode(t, err, "23514")
	refundHash := repeatedByte(0x71)
	for i := 0; i < 2; i++ {
		_, err = pool.Exec(ctx, `INSERT INTO refunds(id,refund_no,order_id,transaction_id,amount_minor,reason,status,provider_ref_hash,requested_by,completed_at) VALUES($1,$2,$3,$4,50,'provider replay','SUCCEEDED',$5,$6,now())`, uuid.New(), fmt.Sprintf("RF%030d", i+10), order1, transaction1, refundHash, f.adminID)
		if i == 0 && err != nil {
			t.Fatal(err)
		}
		if i == 1 {
			assertPostgresCode(t, err, "23505")
		}
	}
}

func seedCommerceRollbackCollision(t *testing.T, ctx context.Context, pool *pgxpool.Pool, f commerceMigrationFixture) {
	t.Helper()
	alipayHash := repeatedByte(0x51)
	collisionHash := append(append([]byte{}, f.alipaySubID[:]...), alipayHash[:16]...)
	manualID := uuid.New()
	if _, err := pool.Exec(ctx, `INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status) VALUES($1,$2,'MANUAL','manual',$3,'free',1,'ACTIVE'),($4,$5,'ALIPAY','alipay',$6,'free',1,'ACTIVE')`, manualID, f.defaultAccountID, collisionHash, f.alipaySubID, f.legacyAccountID, alipayHash); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, `UPDATE entitlements SET source='ALIPAY',provider_reference_hash=$2 WHERE account_id=$1`, f.legacyAccountID, alipayHash); err != nil {
		t.Fatal(err)
	}
}

func assertCommerceRollback(t *testing.T, ctx context.Context, pool *pgxpool.Pool, f commerceMigrationFixture) {
	t.Helper()
	var commerceTable *string
	if err := pool.QueryRow(ctx, `SELECT to_regclass('entitlement_grants')::text`).Scan(&commerceTable); err != nil {
		t.Fatal(err)
	}
	if commerceTable != nil {
		t.Fatalf("entitlement_grants still exists after down: %s", *commerceTable)
	}
	var priceColumnCount int
	if err := pool.QueryRow(ctx, `SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='plans' AND column_name IN ('price_minor','currency')`).Scan(&priceColumnCount); err != nil || priceColumnCount != 0 {
		t.Fatalf("v24 plan columns after down=(%d,%v)", priceColumnCount, err)
	}
	for accountID, expectedSource := range map[uuid.UUID]string{
		f.defaultAccountID: "DEFAULT",
		f.googleAccountID:  "GOOGLE_PLAY",
		f.webAccountID:     "WEB",
		f.legacyAccountID:  "MANUAL",
	} {
		var source string
		if err := pool.QueryRow(ctx, `SELECT source FROM entitlements WHERE account_id=$1`, accountID).Scan(&source); err != nil || source != expectedSource {
			t.Errorf("rolled-back entitlement %s source=(%q,%v), want %s", accountID, source, err, expectedSource)
		}
	}
	var rollbackEntitlementHash, rollbackSubscriptionHash []byte
	if err := pool.QueryRow(ctx, `
		SELECT e.provider_reference_hash,s.provider_ref_hash
		FROM entitlements e
		JOIN subscriptions s ON s.account_id=e.account_id AND s.id=$2
		WHERE e.account_id=$1`, f.legacyAccountID, f.alipaySubID).Scan(&rollbackEntitlementHash, &rollbackSubscriptionHash); err != nil || !bytes.Equal(rollbackEntitlementHash, rollbackSubscriptionHash) {
		t.Errorf("rolled-back ALIPAY identity mismatch entitlement/subscription=(%x/%x err=%v)", rollbackEntitlementHash, rollbackSubscriptionHash, err)
	}
	var convertedCount, distinctCount int
	if err := pool.QueryRow(ctx, `SELECT count(*),count(DISTINCT provider_ref_hash) FROM subscriptions WHERE provider='MANUAL'`).Scan(&convertedCount, &distinctCount); err != nil {
		t.Fatal(err)
	}
	if convertedCount < 2 || convertedCount != distinctCount {
		t.Fatalf("MANUAL rollback identities count/distinct=%d/%d", convertedCount, distinctCount)
	}
	for subscriptionID, expectedProvider := range map[uuid.UUID]string{
		f.googleSubID:   "GOOGLE_PLAY",
		f.webSubID:      "WEB",
		f.extraWebSubID: "WEB",
		f.alipaySubID:   "MANUAL",
	} {
		var provider string
		var hashLength int
		if err := pool.QueryRow(ctx, `SELECT provider,octet_length(provider_ref_hash) FROM subscriptions WHERE id=$1`, subscriptionID).Scan(&provider, &hashLength); err != nil || provider != expectedProvider || hashLength != 32 {
			t.Errorf("rolled-back subscription %s=(provider=%q hash=%d err=%v), want %s/32", subscriptionID, provider, hashLength, err, expectedProvider)
		}
	}
	_, err := pool.Exec(ctx, `INSERT INTO subscriptions(id,account_id,provider,product_id,provider_ref_hash,plan_key,catalog_version,status) VALUES($1,$2,'ALIPAY','invalid',$3,'free',1,'ACTIVE')`, uuid.New(), f.defaultAccountID, repeatedByte(0x61))
	assertPostgresCode(t, err, "23514")
	for _, constraint := range []string{"subscriptions_provider_check", "entitlements_source_check"} {
		var found bool
		if err := pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE connamespace=current_schema()::regnamespace AND conname=$1)`, constraint).Scan(&found); err != nil || !found {
			t.Errorf("restored constraint %s=(%t,%v)", constraint, found, err)
		}
	}
}

func repeatedByte(value byte) []byte {
	result := make([]byte, 32)
	for i := range result {
		result[i] = value
	}
	return result
}

func assertPostgresCode(t *testing.T, err error, code string) {
	t.Helper()
	if err == nil {
		t.Fatalf("operation succeeded, want PostgreSQL error %s", code)
	}
	var postgresError *pgconn.PgError
	if !errors.As(err, &postgresError) || postgresError.Code != code {
		t.Fatalf("PostgreSQL error=%v, want SQLSTATE %s", err, code)
	}
}

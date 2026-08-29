package store

import (
	"context"
	"crypto/sha256"
	"errors"
	"os"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestManualEntitlementLimitAndInputValidation(t *testing.T) {
	t.Parallel()
	for _, test := range []struct{ input, want int }{{0, 50}, {-1, 50}, {1, 1}, {200, 200}, {201, 200}} {
		if got := normalizeManualEntitlementLimit(test.input); got != test.want {
			t.Fatalf("normalizeManualEntitlementLimit(%d)=%d, want %d", test.input, got, test.want)
		}
	}
	for _, reason := range []string{"valid operational reason", "人工调整权益"} {
		if !validateManualEntitlementReason(reason) {
			t.Fatalf("valid reason rejected: %q", reason)
		}
	}
	for _, reason := range []string{"", "ab", " padded reason ", "line\nbreak", strings.Repeat("界", 501)} {
		if validateManualEntitlementReason(reason) {
			t.Fatalf("invalid reason accepted: %q", reason)
		}
	}
	if !validateManualEntitlementKey("manual-grant-key") || validateManualEntitlementKey(" bad-key") {
		t.Fatal("idempotency key validation mismatch")
	}
}

func TestManualEntitlementDTOIsMinimal(t *testing.T) {
	t.Parallel()
	typeOf := reflect.TypeOf(ManualEntitlementGrant{})
	expected := []string{"ID", "AccountID", "PlanKey", "CatalogVersion", "StartsAt", "EndsAt", "Status", "CreatedAt", "UpdatedAt"}
	if typeOf.NumField() != len(expected) {
		t.Fatalf("grant fields=%d, want %d", typeOf.NumField(), len(expected))
	}
	for index, field := range expected {
		if got := typeOf.Field(index).Name; got != field {
			t.Fatalf("field %d=%s, want %s", index, got, field)
		}
	}
}

func TestManualEntitlementSQLContracts(t *testing.T) {
	t.Parallel()
	list := strings.ToLower(manualEntitlementListSQL)
	if !strings.Contains(list, "source_type='manual'") || !strings.Contains(list, "account_id=$1") ||
		!strings.Contains(list, "order by manual_grant.created_at desc,manual_grant.id desc") || !strings.Contains(list, "limit $2") {
		t.Fatalf("manual grant list is not scoped/bounded/stable: %s", list)
	}
	insert := strings.ToLower(manualEntitlementInsertSQL)
	if !strings.Contains(insert, "'manual'") || !strings.Contains(insert, "starts_at,ends_at,status") || !strings.Contains(insert, "'active'") {
		t.Fatalf("manual insert contract incomplete: %s", insert)
	}
	revoke := strings.ToLower(manualEntitlementRevokeSQL)
	for _, required := range []string{"source_type='manual'", "status='active'", "account_id=$2", "status='revoked'"} {
		if !strings.Contains(revoke, required) {
			t.Fatalf("revoke SQL lacks %q: %s", required, revoke)
		}
	}
	for _, query := range []string{manualEntitlementListSQL, manualEntitlementLookupSQL, manualEntitlementLockSQL, manualEntitlementRevokeSQL} {
		query = strings.ToLower(query)
		for _, forbidden := range []string{
			"provider_reference", "provider_trade", "payment_payload", "secret_refs",
			"title_cipher", "last_output_seq", "object_key", "ciphertext",
		} {
			if strings.Contains(query, forbidden) {
				t.Fatalf("query exposes forbidden field %q", forbidden)
			}
		}
	}
	insertIdempotency := strings.ToLower(manualEntitlementIdempotencyInsertSQL)
	if !strings.Contains(insertIdempotency, "idempotency_keys(user_id,idempotency_key,request_hash") ||
		!strings.Contains(insertIdempotency, "values($1,$2,$3") ||
		!strings.Contains(insertIdempotency, "on conflict(user_id,idempotency_key)") {
		t.Fatalf("idempotency insert is not actor/key scoped: %s", insertIdempotency)
	}
	for _, query := range []string{manualEntitlementIdempotencyReadSQL, manualEntitlementIdempotencyCompleteSQL} {
		query = strings.ToLower(query)
		if !strings.Contains(query, "user_id=$1") || !strings.Contains(query, "idempotency_key=$2") {
			t.Fatalf("idempotency SQL lacks actor/key scope: %s", query)
		}
	}
	if !strings.Contains(strings.ToLower(manualEntitlementEnsureOutboxSQL), "entitlement.changed") {
		t.Fatal("entitlement.changed outbox contract missing")
	}
	audit := strings.ToLower(manualEntitlementAuditSQL)
	for _, required := range []string{"user_id", "actor_device_id", "target_id", "trace_id", "metadata"} {
		if !strings.Contains(audit, required) {
			t.Fatalf("manual entitlement audit lacks %q", required)
		}
	}
}

func TestManualEntitlementRequestHashBindsReasonAndOperation(t *testing.T) {
	t.Parallel()
	actorID := uuid.New()
	first := manualEntitlementRequestHash("create", actorID, "reason one")
	second := manualEntitlementRequestHash("create", actorID, "reason two")
	third := manualEntitlementRequestHash("revoke", actorID, "reason one")
	if first == second || first == third {
		t.Fatal("idempotency request hash did not bind reason/operation")
	}
}

func TestManualEntitlementPostgresTransactions(t *testing.T) {
	databaseURL := os.Getenv("TERMLINK_INTEGRATION_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("TERMLINK_INTEGRATION_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	repository, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer repository.Close()

	adminID, auditorID, targetID := uuid.New(), uuid.New(), uuid.New()
	adminDeviceID, auditorDeviceID := uuid.New(), uuid.New()
	ids := []uuid.UUID{adminID, auditorID, targetID}
	deviceIDs := []uuid.UUID{adminDeviceID, auditorDeviceID}
	grantIDs := make([]uuid.UUID, 0, 2)
	t.Cleanup(func() {
		cleanupCtx, cleanupCancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cleanupCancel()
		_, _ = repository.Pool.Exec(cleanupCtx, `
			DELETE FROM audit_events
			WHERE user_id=ANY($1::uuid[]) OR actor_device_id=ANY($2::uuid[])
			   OR target_id=ANY($3::uuid[])`, ids, deviceIDs, grantIDs)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM outbox WHERE aggregate_id=ANY($1::uuid[])`, ids)
		_, _ = repository.Pool.Exec(cleanupCtx, `DELETE FROM users WHERE id=ANY($1::uuid[])`, ids)
	})

	for index, fixture := range []struct {
		id   uuid.UUID
		role Role
	}{{adminID, RoleAdmin}, {auditorID, RoleAuditor}, {targetID, RoleUser}} {
		subject := sha256.Sum256([]byte("manual-entitlement-test:" + fixture.id.String()))
		if _, err := repository.Pool.Exec(ctx, `INSERT INTO users(id,oidc_subject_hash,role) VALUES($1,$2,$3)`, fixture.id, subject[:], fixture.role); err != nil {
			t.Fatal(err)
		}
		_ = index
	}
	for _, fixture := range []struct{ id, userID uuid.UUID }{{adminDeviceID, adminID}, {auditorDeviceID, auditorID}} {
		if _, err := repository.Pool.Exec(ctx, `
			INSERT INTO devices(id,user_id,device_type,name,signing_public_key,exchange_public_key,key_id,trust_state)
			VALUES($1,$2,'ANDROID','manual grant test',decode(repeat('31',32),'hex'),
			       decode(repeat('32',32),'hex'),$3,'TRUSTED')`, fixture.id, fixture.userID, "manual-"+fixture.id.String()); err != nil {
			t.Fatal(err)
		}
	}
	var planKey string
	if err := repository.Pool.QueryRow(ctx, `
		SELECT plan.plan_key FROM plans plan JOIN catalog_versions catalog ON catalog.id=plan.catalog_version_id
		WHERE catalog.status='PUBLISHED' ORDER BY plan.sort_order DESC,plan.plan_key LIMIT 1`).Scan(&planKey); err != nil {
		t.Fatal(err)
	}

	input := CreateManualEntitlementGrantInput{
		AccountID: targetID, PlanKey: planKey, DurationDays: 30,
		Reason: "Grant temporary support entitlement", IdempotencyKey: "manual-create-integration", TraceID: "manual-create-test",
	}
	created, err := repository.CreateManualEntitlementGrant(ctx, adminID, adminDeviceID, input)
	if err != nil {
		t.Fatal(err)
	}
	grantIDs = append(grantIDs, created.ID)
	replayed, err := repository.CreateManualEntitlementGrant(ctx, adminID, adminDeviceID, input)
	if err != nil || replayed.ID != created.ID {
		t.Fatalf("create replay=%+v err=%v, want grant %s", replayed, err, created.ID)
	}
	var grantCount, createAudits, outboxEvents int
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM entitlement_grants WHERE id=$1 AND source_type='MANUAL'`, created.ID).Scan(&grantCount); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND target_id=$2 AND actor_device_id=$3
		  AND action='admin.entitlement.manual.create'`, adminID, created.ID, adminDeviceID).Scan(&createAudits); err != nil {
		t.Fatal(err)
	}
	if err := repository.Pool.QueryRow(ctx, `SELECT count(*) FROM outbox WHERE aggregate_id=$1 AND event_type='entitlement.changed'`, targetID).Scan(&outboxEvents); err != nil {
		t.Fatal(err)
	}
	if grantCount != 1 || createAudits != 1 || outboxEvents < 1 {
		t.Fatalf("grant/audit/outbox=%d/%d/%d", grantCount, createAudits, outboxEvents)
	}

	auditorInput := input
	auditorInput.IdempotencyKey = "manual-auditor-denied"
	if _, err := repository.CreateManualEntitlementGrant(ctx, auditorID, auditorDeviceID, auditorInput); !errors.Is(err, ErrDenied) {
		t.Fatalf("auditor write err=%v, want ErrDenied", err)
	}
	listed, err := repository.ListManualEntitlementGrants(ctx, auditorID, ManualEntitlementGrantFilter{AccountID: targetID, Limit: 10})
	if err != nil || len(listed) != 1 || listed[0].ID != created.ID {
		t.Fatalf("auditor list=%+v err=%v", listed, err)
	}

	revoke := RevokeManualEntitlementGrantInput{
		GrantID: created.ID, Reason: "Revoke completed support entitlement",
		IdempotencyKey: "manual-revoke-integration", TraceID: "manual-revoke-test",
	}
	revoked, err := repository.RevokeManualEntitlementGrant(ctx, adminID, adminDeviceID, revoke)
	if err != nil || revoked.Status != "REVOKED" {
		t.Fatalf("revoke=%+v err=%v", revoked, err)
	}
	revokedReplay, err := repository.RevokeManualEntitlementGrant(ctx, adminID, adminDeviceID, revoke)
	if err != nil || revokedReplay.ID != revoked.ID || revokedReplay.Status != "REVOKED" {
		t.Fatalf("revoke replay=%+v err=%v", revokedReplay, err)
	}
	var revokeAudits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND target_id=$2 AND actor_device_id=$3
		  AND action='admin.entitlement.manual.revoke'`, adminID, revoked.ID, adminDeviceID).Scan(&revokeAudits); err != nil {
		t.Fatal(err)
	}
	if revokeAudits != 1 {
		t.Fatalf("revoke audits=%d, want 1", revokeAudits)
	}

	webInput := input
	webInput.Reason = "Grant entitlement from secured Web console"
	webInput.IdempotencyKey = "manual-web-create-integration"
	webInput.TraceID = "manual-web-create-test"
	webCreated, err := repository.CreateManualEntitlementGrantFromWeb(ctx, adminID, webInput)
	if err != nil {
		t.Fatal(err)
	}
	grantIDs = append(grantIDs, webCreated.ID)
	var webCreateAudits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND target_id=$2 AND actor_device_id IS NULL
		  AND action='admin.entitlement.manual.create'
		  AND metadata->>'actor_channel'='WEB'`, adminID, webCreated.ID).Scan(&webCreateAudits); err != nil {
		t.Fatal(err)
	}
	if webCreateAudits != 1 {
		t.Fatalf("Web create audits=%d, want 1", webCreateAudits)
	}
	webRevoked, err := repository.RevokeManualEntitlementGrantFromWeb(ctx, adminID, RevokeManualEntitlementGrantInput{
		GrantID: webCreated.ID, Reason: "Revoke entitlement from secured Web console",
		IdempotencyKey: "manual-web-revoke-integration", TraceID: "manual-web-revoke-test",
	})
	if err != nil || webRevoked.Status != "REVOKED" {
		t.Fatalf("Web revoke=%+v err=%v", webRevoked, err)
	}
	var webRevokeAudits int
	if err := repository.Pool.QueryRow(ctx, `
		SELECT count(*) FROM audit_events
		WHERE user_id=$1 AND target_id=$2 AND actor_device_id IS NULL
		  AND action='admin.entitlement.manual.revoke'
		  AND metadata->>'actor_channel'='WEB'`, adminID, webCreated.ID).Scan(&webRevokeAudits); err != nil {
		t.Fatal(err)
	}
	if webRevokeAudits != 1 {
		t.Fatalf("Web revoke audits=%d, want 1", webRevokeAudits)
	}
	deviceOnlyInput := webInput
	deviceOnlyInput.IdempotencyKey = "manual-device-missing-integration"
	if _, err := repository.CreateManualEntitlementGrant(ctx, adminID, uuid.Nil, deviceOnlyInput); !errors.Is(err, ErrDenied) {
		t.Fatalf("device API without trusted device err=%v, want ErrDenied", err)
	}

	var catalogVersion int64
	if err := repository.Pool.QueryRow(ctx, `SELECT version FROM catalog_versions WHERE status='PUBLISHED'`).Scan(&catalogVersion); err != nil {
		t.Fatal(err)
	}
	nonManualID := uuid.New()
	grantIDs = append(grantIDs, nonManualID)
	if _, err := repository.Pool.Exec(ctx, `
		INSERT INTO entitlement_grants(id,account_id,source_type,plan_key,catalog_version,starts_at,status)
		VALUES($1,$2,'DEFAULT',$3,$4,now(),'ACTIVE')`, nonManualID, targetID, planKey, catalogVersion); err != nil {
		t.Fatal(err)
	}
	_, err = repository.RevokeManualEntitlementGrant(ctx, adminID, adminDeviceID, RevokeManualEntitlementGrantInput{
		GrantID: nonManualID, Reason: "Attempt prohibited cross-source revoke",
		IdempotencyKey: "manual-cross-source", TraceID: "manual-cross-source-test",
	})
	if !errors.Is(err, ErrDenied) {
		t.Fatalf("cross-source revoke err=%v, want ErrDenied", err)
	}
}

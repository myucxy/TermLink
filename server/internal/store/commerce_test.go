package store

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

type commerceRoleRow struct {
	role   Role
	status string
	err    error
}

func (r commerceRoleRow) Scan(dest ...any) error {
	if r.err != nil {
		return r.err
	}
	*(dest[0].(*Role)) = r.role
	*(dest[1].(*string)) = r.status
	return nil
}

type commerceRoleTxMock struct {
	row      commerceRoleRow
	query    string
	actorArg any
}

func (m *commerceRoleTxMock) Begin(context.Context) (pgx.Tx, error) { panic("unexpected Begin") }
func (m *commerceRoleTxMock) Commit(context.Context) error          { panic("unexpected Commit") }
func (m *commerceRoleTxMock) Rollback(context.Context) error        { panic("unexpected Rollback") }
func (m *commerceRoleTxMock) CopyFrom(context.Context, pgx.Identifier, []string, pgx.CopyFromSource) (int64, error) {
	panic("unexpected CopyFrom")
}
func (m *commerceRoleTxMock) SendBatch(context.Context, *pgx.Batch) pgx.BatchResults {
	panic("unexpected SendBatch")
}
func (m *commerceRoleTxMock) LargeObjects() pgx.LargeObjects { panic("unexpected LargeObjects") }
func (m *commerceRoleTxMock) Prepare(context.Context, string, string) (*pgconn.StatementDescription, error) {
	panic("unexpected Prepare")
}
func (m *commerceRoleTxMock) Exec(context.Context, string, ...any) (pgconn.CommandTag, error) {
	panic("unexpected Exec")
}
func (m *commerceRoleTxMock) Query(context.Context, string, ...any) (pgx.Rows, error) {
	panic("unexpected Query")
}
func (m *commerceRoleTxMock) QueryRow(_ context.Context, query string, args ...any) pgx.Row {
	m.query = query
	if len(args) > 0 {
		m.actorArg = args[0]
	}
	return m.row
}
func (m *commerceRoleTxMock) Conn() *pgx.Conn { return nil }

type entitlementRecomputeTxMock struct {
	execCount int
	rowErr    error
}

func (m *entitlementRecomputeTxMock) Begin(context.Context) (pgx.Tx, error) {
	panic("unexpected Begin")
}
func (m *entitlementRecomputeTxMock) Commit(context.Context) error   { panic("unexpected Commit") }
func (m *entitlementRecomputeTxMock) Rollback(context.Context) error { panic("unexpected Rollback") }
func (m *entitlementRecomputeTxMock) CopyFrom(context.Context, pgx.Identifier, []string, pgx.CopyFromSource) (int64, error) {
	panic("unexpected CopyFrom")
}
func (m *entitlementRecomputeTxMock) SendBatch(context.Context, *pgx.Batch) pgx.BatchResults {
	panic("unexpected SendBatch")
}
func (m *entitlementRecomputeTxMock) LargeObjects() pgx.LargeObjects {
	panic("unexpected LargeObjects")
}
func (m *entitlementRecomputeTxMock) Prepare(context.Context, string, string) (*pgconn.StatementDescription, error) {
	panic("unexpected Prepare")
}
func (m *entitlementRecomputeTxMock) Exec(context.Context, string, ...any) (pgconn.CommandTag, error) {
	m.execCount++
	return pgconn.NewCommandTag("UPDATE 0"), nil
}
func (m *entitlementRecomputeTxMock) Query(context.Context, string, ...any) (pgx.Rows, error) {
	panic("unexpected Query")
}
func (m *entitlementRecomputeTxMock) QueryRow(context.Context, string, ...any) pgx.Row {
	return commerceRoleRow{err: m.rowErr}
}
func (m *entitlementRecomputeTxMock) Conn() *pgx.Conn { return nil }

func TestValidateInvitationBatch(t *testing.T) {
	now := time.Now().UTC()
	later := now.Add(time.Hour)
	tests := []struct {
		name string
		in   InvitationBatchInput
		ok   bool
	}{
		{"basic", InvitationBatchInput{Name: "beta", Status: "ACTIVE", DefaultMaxUses: 1}, true},
		{"email digest", InvitationBatchInput{Name: "domain", Status: "ACTIVE", DefaultMaxUses: 2, EmailHash: &[32]byte{1}}, true},
		{"gift pair", InvitationBatchInput{Name: "gift", Status: "ACTIVE", DefaultMaxUses: 1, GiftPlanKey: "pro", GiftDurationDays: 30}, true},
		{"missing gift duration", InvitationBatchInput{Name: "gift", Status: "ACTIVE", DefaultMaxUses: 1, GiftPlanKey: "pro"}, false},
		{"reversed window", InvitationBatchInput{Name: "bad", Status: "ACTIVE", DefaultMaxUses: 1, ValidFrom: &later, ValidUntil: &now}, false},
		{"email address is not domain", InvitationBatchInput{Name: "bad", Status: "ACTIVE", DefaultMaxUses: 1, EmailDomain: "a@example.com"}, false},
		{"unknown state", InvitationBatchInput{Name: "bad", Status: "DELETED", DefaultMaxUses: 1}, false},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := validateInvitationBatch(test.in)
			if (err == nil) != test.ok {
				t.Fatalf("validateInvitationBatch() error = %v, ok = %t", err, test.ok)
			}
		})
	}
}

func TestValidateRedemptionBatch(t *testing.T) {
	now := time.Now().UTC()
	valid := RedemptionBatchInput{
		Name: "campaign", PlanKey: "pro", CatalogVersion: 2,
		DurationDays: 30, MaxClaimsPerAccount: 1,
		StackingRule: "EXTEND_SAME_OR_UPGRADE", Status: "ACTIVE",
	}
	if err := validateRedemptionBatch(valid); err != nil {
		t.Fatalf("valid batch rejected: %v", err)
	}
	both := valid
	both.FixedExpiresAt = &now
	if err := validateRedemptionBatch(both); err == nil {
		t.Fatal("duration and fixed expiry were both accepted")
	}
	missing := valid
	missing.DurationDays = 0
	if err := validateRedemptionBatch(missing); err == nil {
		t.Fatal("batch without expiry strategy was accepted")
	}
	badRule := valid
	badRule.StackingRule = "DOWNGRADE"
	if err := validateRedemptionBatch(badRule); err == nil {
		t.Fatal("unknown stacking rule was accepted")
	}
}

func TestComputeGrantEnd(t *testing.T) {
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	current := now.Add(48 * time.Hour)
	end, err := computeGrantEnd(now, 10, nil, &current, true)
	if err != nil {
		t.Fatal(err)
	}
	want := current.Add(10 * 24 * time.Hour)
	if end == nil || !end.Equal(want) {
		t.Fatalf("same-plan extension = %v, want %v", end, want)
	}
	upgradeEnd, err := computeGrantEnd(now, 10, nil, &current, false)
	if err != nil {
		t.Fatal(err)
	}
	if upgradeEnd == nil || !upgradeEnd.Equal(now.Add(10*24*time.Hour)) {
		t.Fatalf("upgrade expiry = %v", upgradeEnd)
	}
	expired := now.Add(-time.Second)
	if _, err := computeGrantEnd(now, 0, &expired, nil, false); !errors.Is(err, ErrRedemptionExpired) {
		t.Fatalf("expired fixed date error = %v", err)
	}
}

func TestPaymentChannelConfigurationRejectsSecrets(t *testing.T) {
	valid := PaymentChannelVersionInput{
		Environment:  "PRODUCTION",
		PublicConfig: json.RawMessage(`{"app_id":"example","notify_url":"https://example.test/pay"}`),
		SecretRefs:   json.RawMessage(`{"merchant_private_key":"docker-secret://alipay-private-key","api_v3_key":"/run/secrets/wechat-api-v3"}`),
	}
	if err := validatePaymentChannelVersionInput(valid); err != nil {
		t.Fatalf("reference-only configuration rejected: %v", err)
	}

	tests := []PaymentChannelVersionInput{
		{Environment: "PRODUCTION", PublicConfig: json.RawMessage(`{"private_key":"raw"}`), SecretRefs: json.RawMessage(`{"key":"secret://key"}`)},
		{Environment: "PRODUCTION", PublicConfig: json.RawMessage(`{"app_id":"x"}`), SecretRefs: json.RawMessage(`{"key":"raw-secret-value"}`)},
		{Environment: "PRODUCTION", PublicConfig: json.RawMessage(`{"app_id":"x"}`), SecretRefs: json.RawMessage(`{"key":"-----BEGIN PRIVATE KEY-----"}`)},
		{Environment: "TEST", PublicConfig: json.RawMessage(`{"app_id":"x"}`), SecretRefs: json.RawMessage(`{"key":"secret://key"}`)},
	}
	for i, input := range tests {
		if err := validatePaymentChannelVersionInput(input); err == nil {
			t.Fatalf("unsafe configuration %d was accepted", i)
		}
	}
}

func TestCommercePrimitiveValidation(t *testing.T) {
	if !validProvider("ALIPAY") || !validProvider("WECHAT_PAY") || validProvider("WEB") {
		t.Fatal("provider allowlist is incorrect")
	}
	for _, scene := range []string{"NATIVE", "WEB", "H5", "JSAPI"} {
		if !validScene(scene) {
			t.Fatalf("valid scene %q rejected", scene)
		}
	}
	if validScene("APP") || validCodePrefix("abc") || validCodePrefix("abcdefghijklmnopq") {
		t.Fatal("invalid primitive accepted")
	}
	if normalizePage(0) != 50 || normalizePage(201) != 50 || normalizePage(200) != 200 {
		t.Fatal("page bounds are incorrect")
	}
}

func TestRefundPolicyDefaultsToOneFullRefund(t *testing.T) {
	if !validFullRefundAmount(1000, 1000, 0) {
		t.Fatal("single full refund was rejected")
	}
	for _, values := range [][3]int64{{1000, 999, 0}, {1000, 1000, 1}, {0, 0, 0}, {1000, 1001, 0}} {
		if validFullRefundAmount(values[0], values[1], values[2]) {
			t.Fatalf("unsafe refund state was accepted: order=%d requested=%d prior=%d", values[0], values[1], values[2])
		}
	}
}

func TestRefundApprovalReplayAndExpiryGuards(t *testing.T) {
	now := time.Now().UTC()
	actor := uuid.New()
	approvedAt := now.Add(-time.Minute)
	expiresAt := now.Add(time.Minute)
	approved := Refund{Status: "CREATED", Version: 2, ApprovedBy: &actor, ApprovedAt: &approvedAt}
	if !refundApprovalIsReplay(approved, 1) {
		t.Fatal("the immediately consumed approval version was not treated as an idempotent replay")
	}
	for _, item := range []Refund{
		{Status: "APPROVAL_PENDING", Version: 1},
		{Status: "CREATED", Version: 2, ApprovedAt: &approvedAt},
		{Status: "CREATED", Version: 1, ApprovedBy: &actor, ApprovedAt: &approvedAt},
	} {
		if refundApprovalIsReplay(item, 1) {
			t.Fatalf("unsafe approval state accepted as replay: %+v", item)
		}
	}
	if !refundApprovalIsLive(now, &expiresAt) || refundApprovalIsLive(now, nil) {
		t.Fatal("approval expiry guard rejected a live window or accepted a missing window")
	}
	expiresAt = now
	if refundApprovalIsLive(now, &expiresAt) {
		t.Fatal("approval window remained live at its exclusive expiry boundary")
	}
}

func TestRefundApprovalOutboxIsTransactionalAndNonSensitive(t *testing.T) {
	for name, query := range map[string]string{
		"requested": refundApprovalRequestedOutboxSQL,
		"confirmed": refundApprovalConfirmedOutboxSQL,
	} {
		lower := strings.ToLower(query)
		for _, required := range []string{"insert into outbox", "refund.approval.changed", "aggregate_id", "refund_id", "order_id", "status", "version"} {
			if !strings.Contains(lower, required) {
				t.Errorf("%s refund approval outbox is missing %q", name, required)
			}
		}
		for _, forbidden := range []string{"reason", "provider", "requested_by", "approved_by", "trace_id"} {
			if strings.Contains(lower, forbidden) {
				t.Errorf("%s refund approval outbox exposes %q", name, forbidden)
			}
		}
	}
}

func TestCommerceReadRBACSQLAndTransactionAreNonLocking(t *testing.T) {
	if strings.Contains(strings.ToUpper(commerceRoleReadSQL), "FOR UPDATE") {
		t.Fatalf("read role query unexpectedly locks actor: %s", commerceRoleReadSQL)
	}
	if !strings.Contains(strings.ToUpper(commerceRoleWriteSQL), "FOR UPDATE") {
		t.Fatalf("write role query does not lock actor: %s", commerceRoleWriteSQL)
	}
	read := commerceTxOptions(false)
	if read.IsoLevel != pgx.ReadCommitted || read.AccessMode != pgx.ReadOnly {
		t.Fatalf("read options = %#v, want READ COMMITTED READ ONLY", read)
	}
	write := commerceTxOptions(true)
	if write.IsoLevel != pgx.Serializable || write.AccessMode != pgx.ReadWrite {
		t.Fatalf("write options = %#v, want SERIALIZABLE READ WRITE", write)
	}

	actorID := uuid.New()
	readMock := &commerceRoleTxMock{row: commerceRoleRow{role: RoleAuditor, status: "ACTIVE"}}
	role, err := commerceRole(context.Background(), readMock, actorID, false, false)
	if err != nil || role != RoleAuditor {
		t.Fatalf("auditor read role = (%s,%v)", role, err)
	}
	if readMock.query != commerceRoleReadSQL || readMock.actorArg != actorID {
		t.Fatalf("read role query = %q actor=%v", readMock.query, readMock.actorArg)
	}

	writeMock := &commerceRoleTxMock{row: commerceRoleRow{role: RoleAdmin, status: "ACTIVE"}}
	role, err = commerceRole(context.Background(), writeMock, actorID, true, false)
	if err != nil || role != RoleAdmin {
		t.Fatalf("admin write role = (%s,%v)", role, err)
	}
	if writeMock.query != commerceRoleWriteSQL {
		t.Fatalf("write role query = %q", writeMock.query)
	}

	deniedMock := &commerceRoleTxMock{row: commerceRoleRow{role: RoleAuditor, status: "ACTIVE"}}
	if _, err = commerceRole(context.Background(), deniedMock, actorID, true, false); !errors.Is(err, ErrDenied) {
		t.Fatalf("auditor write error = %v, want ErrDenied", err)
	}
}

func TestInvitationReleaseStateMachine(t *testing.T) {
	mutate, err := invitationReleaseNeedsMutation("RESERVED")
	if err != nil || !mutate {
		t.Fatalf("RESERVED = (%t,%v), want mutation", mutate, err)
	}
	mutate, err = invitationReleaseNeedsMutation("RELEASED")
	if err != nil || mutate {
		t.Fatalf("RELEASED = (%t,%v), want idempotent no-op", mutate, err)
	}
	for _, state := range []string{"PROVIDER_ACCEPTED", "REDEEMED", ""} {
		if mutate, err = invitationReleaseNeedsMutation(state); mutate || !errors.Is(err, ErrConflict) {
			t.Fatalf("%s = (%t,%v), want ErrConflict", state, mutate, err)
		}
	}
}

func TestPendingInvitationRequiresUniqueUnclaimedCandidate(t *testing.T) {
	countSQL := strings.ToUpper(pendingInvitationCandidateCountSQL)
	lockSQL := strings.ToUpper(pendingInvitationCandidateLockSQL)
	if !strings.Contains(countSQL, "USER_ID IS NULL") || strings.Contains(countSQL, "FOR UPDATE") {
		t.Fatalf("candidate count SQL must be unclaimed and non-locking: %s", pendingInvitationCandidateCountSQL)
	}
	if !strings.Contains(lockSQL, "USER_ID IS NULL") || !strings.Contains(lockSQL, "FOR UPDATE OF R") || strings.Contains(lockSQL, "LIMIT 1") {
		t.Fatalf("candidate lock SQL must lock the already-proven unique unclaimed row: %s", pendingInvitationCandidateLockSQL)
	}
	if err := validatePendingInvitationCandidateCount(0); !errors.Is(err, ErrNotFound) {
		t.Fatalf("zero candidates error = %v", err)
	}
	if err := validatePendingInvitationCandidateCount(1); err != nil {
		t.Fatalf("one candidate error = %v", err)
	}
	for _, count := range []int{-1, 2, 10} {
		if err := validatePendingInvitationCandidateCount(count); !errors.Is(err, ErrConflict) {
			t.Fatalf("%d candidates error = %v, want ErrConflict", count, err)
		}
	}
}

func TestEntitlementMaterializedStateEquality(t *testing.T) {
	effective := time.Date(2026, 8, 27, 8, 0, 0, 0, time.UTC)
	expires := effective.Add(30 * 24 * time.Hour)
	base := Entitlement{
		PlanKey: "pro", CatalogVersion: 8,
		MaxRemotePCs: 3, MaxLiveSessions: 5,
		FeatureFlags: json.RawMessage(`{"collaboration":true}`),
		Version:      11, EffectiveAt: effective, ExpiresAt: &expires,
		Source: "REDEMPTION",
	}
	same := base
	same.Version = 999 // Version is output state, not an equality input.
	if !entitlementStateEqual(base, same) {
		fatalEntitlementDifference(t, "version-only", base, same)
	}

	changes := []struct {
		name string
		edit func(*Entitlement)
	}{
		{"plan", func(v *Entitlement) { v.PlanKey = "plus" }},
		{"catalog", func(v *Entitlement) { v.CatalogVersion++ }},
		{"pc quota", func(v *Entitlement) { v.MaxRemotePCs++ }},
		{"session quota", func(v *Entitlement) { v.MaxLiveSessions++ }},
		{"features", func(v *Entitlement) { v.FeatureFlags = json.RawMessage(`{"collaboration":false}`) }},
		{"effective", func(v *Entitlement) { v.EffectiveAt = v.EffectiveAt.Add(time.Second) }},
		{"expiry", func(v *Entitlement) { x := expires.Add(time.Hour); v.ExpiresAt = &x }},
		{"grace", func(v *Entitlement) { x := expires.Add(time.Hour); v.GraceUntil = &x }},
		{"source", func(v *Entitlement) { v.Source = "ALIPAY" }},
		{"provider digest", func(v *Entitlement) { v.ProviderRefHash = []byte{1} }},
	}
	for _, change := range changes {
		t.Run(change.name, func(t *testing.T) {
			changed := base
			change.edit(&changed)
			if entitlementStateEqual(base, changed) {
				fatalEntitlementDifference(t, change.name, base, changed)
			}
		})
	}

	changedEffective := base
	changedEffective.EffectiveAt = effective.Add(time.Hour)
	if !entitlementStateEqualExceptEffective(base, changedEffective) {
		t.Fatal("effective-only change was not ignored by default-state comparison")
	}
	api := entitlementForAPI(base)
	if api.MaxRemotePCs != UnlimitedQuota || api.MaxLiveSessions != UnlimitedQuota {
		t.Fatalf("API quotas = (%d,%d), want unlimited", api.MaxRemotePCs, api.MaxLiveSessions)
	}
	if base.MaxRemotePCs != 3 || base.MaxLiveSessions != 5 {
		t.Fatal("API compatibility conversion mutated stored quota values")
	}
}

func TestBoundedCleanupRecomputeDoesNotExpireAdditionalGrantSources(t *testing.T) {
	sentinel := errors.New("stop after materialization query")
	bounded := &entitlementRecomputeTxMock{rowErr: sentinel}
	if _, err := recomputeEntitlementFromMaterializedGrants(context.Background(), bounded, uuid.New()); !errors.Is(err, sentinel) {
		t.Fatalf("bounded recompute error = %v, want sentinel", err)
	}
	if bounded.execCount != 0 {
		t.Fatalf("bounded recompute executed %d source updates, want 0", bounded.execCount)
	}

	regular := &entitlementRecomputeTxMock{rowErr: sentinel}
	if _, err := recomputeEntitlement(context.Background(), regular, uuid.New()); !errors.Is(err, sentinel) {
		t.Fatalf("regular recompute error = %v, want sentinel", err)
	}
	if regular.execCount != 1 {
		t.Fatalf("regular recompute executed %d source updates, want 1", regular.execCount)
	}
}

func TestCommerceCleanupSQLContracts(t *testing.T) {
	boundedMutations := map[string]string{
		"idempotency purge": purgeExpiredIdempotencySQL,
		"outbox purge":      purgeProcessedOutboxSQL,
		"payload redaction": redactTerminalPaymentNotificationsSQL,
	}
	for name, query := range boundedMutations {
		t.Run(name, func(t *testing.T) {
			normalized := strings.ToUpper(strings.Join(strings.Fields(query), " "))
			if !strings.Contains(normalized, "WITH DUE AS") || !strings.Contains(normalized, "LIMIT $2") || !strings.Contains(normalized, "FOR UPDATE SKIP LOCKED") {
				t.Fatalf("cleanup mutation is not a bounded locked CTE: %s", query)
			}
		})
	}

	redaction := strings.ToUpper(strings.Join(strings.Fields(redactTerminalPaymentNotificationsSQL), " "))
	if !strings.Contains(redaction, "PROCESSING_STATE IN('PROCESSED','REJECTED')") ||
		!strings.Contains(redaction, "PROCESSED_AT<$1") ||
		!strings.Contains(redaction, "PAYLOAD_CIPHERTEXT IS NOT NULL") ||
		!strings.Contains(redaction, "SET PAYLOAD_CIPHERTEXT=NULL") {
		t.Fatalf("payment payload redaction is not restricted to retained terminal rows: %s", redactTerminalPaymentNotificationsSQL)
	}

	ambiguous := strings.ToUpper(strings.Join(strings.Fields(countAmbiguousInvitationReservationsSQL), " "))
	if !strings.HasPrefix(ambiguous, "SELECT COUNT(*)") ||
		!strings.Contains(ambiguous, "STATE='RESERVED'") ||
		!strings.Contains(ambiguous, "RESERVED_UNTIL<=$1") ||
		!strings.Contains(ambiguous, "LIMIT $2") ||
		strings.Contains(ambiguous, " UPDATE ") || strings.Contains(ambiguous, " DELETE ") {
		t.Fatalf("ambiguous invitation maintenance must be bounded and read-only: %s", countAmbiguousInvitationReservationsSQL)
	}

	candidates := strings.ToUpper(strings.Join(strings.Fields(expiredGrantCandidatesSQL), " "))
	expiry := strings.ToUpper(strings.Join(strings.Fields(expireSelectedGrantsSQL), " "))
	if !strings.Contains(candidates, "LIMIT $2") || !strings.Contains(expiry, "WHERE ID=ANY($1)") ||
		!strings.Contains(expiry, "STATUS='ACTIVE'") || !strings.Contains(expiry, "ENDS_AT<=$2") {
		t.Fatalf("grant expiration is not constrained to the selected batch: candidates=%s mutation=%s", expiredGrantCandidatesSQL, expireSelectedGrantsSQL)
	}
}

func fatalEntitlementDifference(t *testing.T, name string, left, right Entitlement) {
	t.Helper()
	t.Fatalf("%s was incorrectly considered equal: left=%+v right=%+v", name, left, right)
}

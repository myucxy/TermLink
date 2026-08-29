package store

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
)

func TestNormalizeCommerceOrderReportFilter(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	from, to := now.Add(-24*time.Hour), now
	accountID, afterID := uuid.New(), uuid.New()
	afterAt := now.Add(-time.Hour)
	filter, err := normalizeCommerceOrderReportFilter(CommerceOrderReportFilter{
		OrderNo: "TL0123456789abcdef", AccountID: &accountID, Provider: " alipay ",
		Status: " fulfilled ", CreatedFrom: &from, CreatedTo: &to,
		AfterCreatedAt: &afterAt, AfterID: &afterID, Limit: 75,
	}, now)
	if err != nil {
		t.Fatal(err)
	}
	if filter.provider != "ALIPAY" || filter.status != "FULFILLED" || filter.accountID != accountID ||
		!filter.hasCursor || filter.afterID != afterID || filter.limit != 75 {
		t.Fatalf("unexpected normalized filter: %+v", filter)
	}

	defaults, err := normalizeCommerceOrderReportFilter(CommerceOrderReportFilter{}, now)
	if err != nil || defaults.limit != commerceReportDefaultLimit || defaults.to != now ||
		defaults.from != now.Add(-30*24*time.Hour) {
		t.Fatalf("unexpected defaults: %+v error=%v", defaults, err)
	}
}

func TestCommerceOrderReportRejectsInvalidAndUnboundedFilters(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	from, to := now.Add(-time.Hour), now
	tooOld := now.Add(-commerceReportMaxRange - time.Second)
	future := now.Add(10 * time.Minute)
	nilID := uuid.Nil
	after := now.Add(-30 * time.Minute)
	tests := []CommerceOrderReportFilter{
		{OrderNo: "%' OR true--", CreatedFrom: &from, CreatedTo: &to},
		{Provider: "STRIPE", CreatedFrom: &from, CreatedTo: &to},
		{Status: "SUCCESS", CreatedFrom: &from, CreatedTo: &to},
		{AccountID: &nilID, CreatedFrom: &from, CreatedTo: &to},
		{CreatedFrom: &from},
		{CreatedFrom: &tooOld, CreatedTo: &to},
		{CreatedFrom: &from, CreatedTo: &future},
		{CreatedFrom: &from, CreatedTo: &to, AfterCreatedAt: &after},
		{CreatedFrom: &from, CreatedTo: &to, AfterCreatedAt: &after, AfterID: &nilID},
		{CreatedFrom: &from, CreatedTo: &to, Limit: commerceReportMaxLimit + 1},
	}
	for index, input := range tests {
		if _, err := normalizeCommerceOrderReportFilter(input, now); !errors.Is(err, ErrDenied) {
			t.Fatalf("unsafe filter %d error=%v", index, err)
		}
	}
}

func TestCommerceExportRequiresShortExplicitWindowAndBoundedRows(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	from, to := now.Add(-commerceExportMaxRange), now
	filter, err := normalizeCommerceOrderExportFilter(CommerceOrderExportFilter{
		CreatedFrom: &from, CreatedTo: &to,
	}, now)
	if err != nil || filter.limit != commerceExportDefaultLimit {
		t.Fatalf("valid export filter=%+v error=%v", filter, err)
	}
	tooOld := from.Add(-time.Second)
	for index, input := range []CommerceOrderExportFilter{
		{},
		{CreatedFrom: &from},
		{CreatedFrom: &tooOld, CreatedTo: &to},
		{CreatedFrom: &from, CreatedTo: &to, Limit: commerceExportMaxLimit + 1},
	} {
		if _, err := normalizeCommerceOrderExportFilter(input, now); !errors.Is(err, ErrDenied) {
			t.Fatalf("unsafe export filter %d error=%v", index, err)
		}
	}
}

func TestCommerceConsistencyFilterRejectsUnknownCategoriesAndLargeRanges(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	from, to := now.Add(-time.Hour), now
	valid, err := normalizeCommerceConsistencyFilter(CommerceConsistencyFilter{
		Provider: "wechat_pay", Category: "completed_order_without_grant",
		CreatedFrom: &from, CreatedTo: &to, Limit: 20,
	}, now)
	if err != nil || valid.provider != "WECHAT_PAY" || valid.category != "COMPLETED_ORDER_WITHOUT_GRANT" {
		t.Fatalf("valid consistency filter=%+v error=%v", valid, err)
	}
	tooOld := now.Add(-commerceConsistencyMaxRange - time.Second)
	badCursor := CommerceConsistencyCursor{OccurredAt: now, EntityID: uuid.New(), Category: "UNKNOWN"}
	for index, input := range []CommerceConsistencyFilter{
		{Category: "PAYMENT_FAILED", CreatedFrom: &from, CreatedTo: &to},
		{Provider: "CARD", CreatedFrom: &from, CreatedTo: &to},
		{CreatedFrom: &tooOld, CreatedTo: &to},
		{CreatedFrom: &from, CreatedTo: &to, After: &badCursor},
		{CreatedFrom: &from, CreatedTo: &to, Limit: commerceReportMaxLimit + 1},
	} {
		if _, err := normalizeCommerceConsistencyFilter(input, now); !errors.Is(err, ErrDenied) {
			t.Fatalf("unsafe consistency filter %d error=%v", index, err)
		}
	}
}

func TestCommerceReportingRoleRequiresActiveConsoleRole(t *testing.T) {
	t.Parallel()
	actorID := uuid.New()
	for _, test := range []struct {
		name   string
		role   Role
		status string
		ok     bool
	}{
		{name: "super admin", role: RoleSuperAdmin, status: "ACTIVE", ok: true},
		{name: "admin", role: RoleAdmin, status: "ACTIVE", ok: true},
		{name: "auditor", role: RoleAuditor, status: "ACTIVE", ok: true},
		{name: "ordinary user", role: RoleUser, status: "ACTIVE"},
		{name: "disabled admin", role: RoleAdmin, status: "DISABLED"},
	} {
		t.Run(test.name, func(t *testing.T) {
			tx := &commerceRoleTxMock{row: commerceRoleRow{role: test.role, status: test.status}}
			err := commerceReportingRole(context.Background(), tx, actorID)
			if (err == nil) != test.ok {
				t.Fatalf("commerceReportingRole error=%v ok=%t", err, test.ok)
			}
			if tx.query != commerceRoleReadSQL || strings.Contains(strings.ToUpper(tx.query), "FOR UPDATE") {
				t.Fatalf("report authorization query is not safe read-only SQL: %s", tx.query)
			}
		})
	}
	if err := commerceReportingRole(context.Background(), &commerceRoleTxMock{}, uuid.Nil); !errors.Is(err, ErrDenied) {
		t.Fatalf("nil actor error=%v", err)
	}
}

func TestCommerceReportingSQLContracts(t *testing.T) {
	t.Parallel()
	queries := map[string]string{
		"orders":      commerceOrderReportSQL,
		"export":      commerceOrderExportSQL,
		"consistency": commerceConsistencyDetailsSQL,
	}
	for name, query := range queries {
		t.Run(name, func(t *testing.T) {
			lower := strings.ToLower(query)
			for _, forbidden := range []string{
				" for update", "skip locked", "insert into", "update orders", "delete from",
				"idempotency_key", "payment_payload", "payload_ciphertext", "provider_trade_no",
				"provider_ref_hash", "notification_id_hash", "payload_digest", "email_hash",
				"secret_refs", "public_config", "result_summary", "refunds.reason",
			} {
				if strings.Contains(lower, forbidden) {
					t.Fatalf("%s query contains forbidden sensitive/mutating token %q", name, forbidden)
				}
			}
			if !strings.Contains(lower, "limit $") || !strings.Contains(lower, "order by") ||
				!strings.Contains(lower, "created_at >= $1") || !strings.Contains(lower, "created_at < $2") {
				t.Fatalf("%s query is not time-bounded and stably paginated: %s", name, query)
			}
		})
	}
	for _, required := range []string{
		"payment_transactions", "entitlement_grants", "entitlements", "catalog_versions", "plans",
		"ORDER_WITHOUT_COMPATIBLE_TRANSACTION", "SETTLED_TRANSACTION_ORDER_MISMATCH",
		"COMPLETED_ORDER_WITHOUT_GRANT", "REFUNDED_ORDER_WITH_LIVE_GRANT",
		"MATERIALIZED_ENTITLEMENT_MISMATCH",
	} {
		if !strings.Contains(commerceConsistencyDetailsSQL, required) {
			t.Fatalf("consistency query lacks %q", required)
		}
	}
}

func TestCommerceReportingDTOsExcludeSensitiveFields(t *testing.T) {
	t.Parallel()
	values := []any{
		CommerceOrderReportRow{}, CommerceOrderExportRow{}, CommerceConsistencyDetail{},
	}
	for _, value := range values {
		typeOf := reflect.TypeOf(value)
		for index := 0; index < typeOf.NumField(); index++ {
			name := strings.ToLower(typeOf.Field(index).Name)
			for _, forbidden := range []string{
				"idempotency", "payload", "cipher", "trade", "reference", "hash", "secret",
				"email", "reason", "notification", "metadata", "username", "subject",
			} {
				if strings.Contains(name, forbidden) {
					t.Fatalf("%s exposes forbidden field %s", typeOf.Name(), typeOf.Field(index).Name)
				}
			}
		}
	}
}

func TestValidateCommerceCatalogDraftAcceptsPublishableDraft(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	later := now.Add(30 * 24 * time.Hour)
	plans := []PlanDraft{
		{ID: uuid.New(), Key: "free", BillingPeriod: "FREE", PriceMinor: 0, Currency: "CNY", FallbackPlanKey: "free"},
		{ID: uuid.New(), Key: "pro", BillingPeriod: "MONTHLY", PriceMinor: 1990, Currency: "CNY", FallbackPlanKey: "free", AvailableFrom: &now, AvailableUntil: &later},
	}
	summary := validateCommerceCatalogDraft(plans)
	if !summary.Valid || summary.PlanCount != len(plans) || len(summary.Issues) != 0 {
		t.Fatalf("valid draft summary=%+v", summary)
	}
}

func TestValidateCommerceCatalogDraftReportsEveryRequiredProblem(t *testing.T) {
	t.Parallel()
	now := time.Date(2026, time.August, 27, 12, 0, 0, 0, time.UTC)
	earlier := now.Add(-time.Hour)
	duplicateID := uuid.New()
	plans := []PlanDraft{
		{ID: duplicateID, Key: "pro", BillingPeriod: "MONTHLY", PriceMinor: 0, Currency: "USD", FallbackPlanKey: "missing", AvailableFrom: &now, AvailableUntil: &earlier},
		{ID: uuid.New(), Key: "pro", BillingPeriod: "FREE", PriceMinor: 1, Currency: "CNY"},
	}
	summary := validateCommerceCatalogDraft(plans)
	if summary.Valid || summary.PlanCount != len(plans) {
		t.Fatalf("invalid draft summary=%+v", summary)
	}
	want := map[string]int{
		CommerceCatalogIssueFreePlanRequired:        1,
		CommerceCatalogIssueDuplicatePlanKey:        2,
		CommerceCatalogIssueCurrencyMustBeCNY:       1,
		CommerceCatalogIssueFreePriceMustBeZero:     1,
		CommerceCatalogIssuePaidPriceMustBePositive: 1,
		CommerceCatalogIssueFallbackPlanNotFound:    1,
		CommerceCatalogIssueSaleWindowInvalid:       1,
	}
	got := make(map[string]int)
	for _, issue := range summary.Issues {
		got[issue.Code]++
		if issue.Code != CommerceCatalogIssueFreePlanRequired && (issue.PlanID == nil || issue.PlanKey == "") {
			t.Fatalf("plan-scoped issue lacks safe locator: %+v", issue)
		}
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("issue counts=%v, want %v", got, want)
	}
}

func TestValidateCommerceCatalogDraftFreeKeyAlwaysHasZeroPrice(t *testing.T) {
	t.Parallel()
	summary := validateCommerceCatalogDraft([]PlanDraft{{
		ID: uuid.New(), Key: "free", BillingPeriod: "MONTHLY", PriceMinor: 100, Currency: "CNY",
	}})
	if !hasCommerceCatalogIssue(summary, CommerceCatalogIssueFreePriceMustBeZero) {
		t.Fatalf("free plan with nonzero price was not rejected: %+v", summary)
	}
}

func hasCommerceCatalogIssue(summary CommerceCatalogValidationSummary, code string) bool {
	for _, issue := range summary.Issues {
		if issue.Code == code {
			return true
		}
	}
	return false
}

func TestCommerceCatalogDraftQueriesAreReadOnlyBoundedAndSafe(t *testing.T) {
	t.Parallel()
	options := commerceCatalogDraftTxOptions()
	if options.IsoLevel != pgx.RepeatableRead || options.AccessMode != pgx.ReadOnly {
		t.Fatalf("catalog read options=%#v", options)
	}
	for name, query := range map[string]string{
		"catalog": commerceCatalogDraftSQL,
		"plans":   commerceCatalogDraftPlansSQL,
	} {
		t.Run(name, func(t *testing.T) {
			lower := strings.ToLower(query)
			for _, required := range []string{"order by", "limit "} {
				if !strings.Contains(lower, required) {
					t.Fatalf("%s query lacks bounded stable clause %q: %s", name, required, query)
				}
			}
			for _, forbidden := range []string{
				" for update", "insert into", "update ", "delete from", "payment_payload",
				"payload_ciphertext", "provider_trade", "provider_ref", "notification",
				"idempotency", "secret", "email_hash",
			} {
				if strings.Contains(lower, forbidden) {
					t.Fatalf("%s query contains forbidden token %q", name, forbidden)
				}
			}
		})
	}
	if !strings.Contains(commerceCatalogDraftSQL, "status='DRAFT'") ||
		!strings.Contains(commerceCatalogDraftPlansSQL, "catalog_version_id=$1") ||
		!strings.Contains(commerceCatalogDraftPlansSQL, "LIMIT $3") {
		t.Fatal("catalog queries do not scope the newest draft and bounded full plan set")
	}
}

func TestCommerceCatalogValidationDTOIsSafe(t *testing.T) {
	t.Parallel()
	for _, value := range []any{CommerceCatalogDraft{}, CommerceCatalogValidationSummary{}, CommerceCatalogValidationIssue{}} {
		typeOf := reflect.TypeOf(value)
		for index := 0; index < typeOf.NumField(); index++ {
			name := strings.ToLower(typeOf.Field(index).Name)
			for _, forbidden := range []string{"secret", "payload", "cipher", "trade", "reference", "hash", "email", "idempotency"} {
				if strings.Contains(name, forbidden) {
					t.Fatalf("%s exposes forbidden field %s", typeOf.Name(), typeOf.Field(index).Name)
				}
			}
		}
	}
}

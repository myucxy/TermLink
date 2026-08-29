package migrate

import (
	"regexp"
	"strings"
	"testing"

	"github.com/termlink/termlink/server/migrations"
)

func TestCommerceMigrationSecurityContract(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000024_server_web_commerce.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000024_server_web_commerce.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	up := compactCommerceSQL(string(upBytes))
	down := compactCommerceSQL(string(downBytes))

	upClauses := []string{
		"create unique index invitation_redemptions_one_provider_accepted_email on invitation_redemptions(email_hash) where state = 'provider_accepted' and user_id is null",
		"create unique index entitlement_grants_source on entitlement_grants(source_type, source_id) where source_id is not null",
		"when s.status = 'canceled' then s.renews_at",
		"foreign key (active_version_id, id) references payment_channel_versions(id, channel_id)",
		"constraint payment_transactions_order_terms_fk foreign key (order_id, provider, amount_minor, currency) references orders(id, provider, amount_minor, currency)",
		"create unique index payment_transactions_one_settled_per_order on payment_transactions(order_id) where status in ('succeeded', 'refunded')",
		"constraint refunds_transaction_order_fk foreign key (transaction_id, order_id) references payment_transactions(id, order_id)",
		"create unique index refunds_provider_reference on refunds(provider_ref_hash) where provider_ref_hash is not null",
		"drop constraint if exists subscriptions_provider_check, add constraint subscriptions_provider_check",
		"drop constraint if exists entitlements_source_check, add constraint entitlements_source_check",
	}
	for _, clause := range upClauses {
		if !strings.Contains(up, clause) {
			t.Errorf("commerce up migration is missing security contract %q", clause)
		}
	}
	downClauses := []string{
		"drop constraint if exists payment_channels_active_version_fk",
		"lock table subscriptions in share row exclusive mode",
		"where provider in ('alipay', 'wechat_pay')",
		"add constraint subscriptions_provider_check check (provider in ('google_play', 'web', 'manual'))",
		"add constraint entitlements_source_check check (source in ('default', 'google_play', 'web', 'manual'))",
	}
	for _, clause := range downClauses {
		if !strings.Contains(down, clause) {
			t.Errorf("commerce down migration is missing rollback contract %q", clause)
		}
	}
}

func TestCommerceMigrationDownCoversCreatedTables(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000024_server_web_commerce.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000024_server_web_commerce.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	createdTable := regexp.MustCompile(`(?im)^create\s+table\s+([a-z_]+)\s*\(`)
	down := strings.ToLower(string(downBytes))
	matches := createdTable.FindAllStringSubmatch(string(upBytes), -1)
	if len(matches) == 0 {
		t.Fatal("commerce migration did not create any tables")
	}
	for _, match := range matches {
		if !strings.Contains(down, "drop table if exists "+strings.ToLower(match[1])) {
			t.Errorf("down migration does not drop created table %s", match[1])
		}
	}
}

func TestRefundApprovalMigrationContract(t *testing.T) {
	upBytes, err := migrations.Files.ReadFile("000026_refund_approval.up.sql")
	if err != nil {
		t.Fatal(err)
	}
	downBytes, err := migrations.Files.ReadFile("000026_refund_approval.down.sql")
	if err != nil {
		t.Fatal(err)
	}
	up := compactCommerceSQL(string(upBytes))
	down := compactCommerceSQL(string(downBytes))
	for _, clause := range []string{
		"add column version bigint not null default 1 check (version > 0)",
		"add column approved_by uuid references users(id) on delete restrict",
		"add column approved_at timestamptz",
		"add column approval_expires_at timestamptz",
		"update refunds set approved_by = requested_by, approved_at = requested_at where status <> 'approval_pending'",
		"status in ('approval_pending', 'created', 'pending', 'succeeded', 'failed', 'closed')",
		"status = 'approval_pending' and approved_by is null and approved_at is null and approval_expires_at is not null",
		"status <> 'approval_pending' and approved_by is not null and approved_at is not null and approved_at >= requested_at",
	} {
		if !strings.Contains(up, clause) {
			t.Errorf("refund approval migration is missing %q", clause)
		}
	}
	for _, clause := range []string{
		"if exists (select 1 from refunds where status = 'approval_pending') then raise exception",
		"drop column approval_expires_at",
		"status in ('created', 'pending', 'succeeded', 'failed', 'closed')",
	} {
		if !strings.Contains(down, clause) {
			t.Errorf("refund approval rollback is missing %q", clause)
		}
	}
}

func compactCommerceSQL(value string) string {
	return strings.Join(strings.Fields(strings.ToLower(value)), " ")
}

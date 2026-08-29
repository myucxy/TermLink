package store

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/google/uuid"
)

func TestListAccountRefundsRejectsMissingAccountBeforeDatabaseAccess(t *testing.T) {
	_, err := (&Postgres{}).ListAccountRefunds(context.Background(), uuid.Nil, 20)
	if !errors.Is(err, ErrDenied) {
		t.Fatalf("ListAccountRefunds(nil account) error = %v, want ErrDenied", err)
	}
}

func TestAccountRefundQueryScopesOwnershipInFirstStatement(t *testing.T) {
	query := strings.ToLower(strings.Join(strings.Fields(`SELECT `+accountRefundColumns+`
		FROM refunds r JOIN orders o ON o.id=r.order_id
		WHERE o.account_id=$1
		ORDER BY r.requested_at DESC,r.id DESC LIMIT $2`), " "))
	for _, required := range []string{
		"from refunds r join orders o on o.id=r.order_id",
		"where o.account_id=$1",
		"order by r.requested_at desc,r.id desc limit $2",
	} {
		if !strings.Contains(query, required) {
			t.Fatalf("account refund query omitted %q: %s", required, query)
		}
	}
	if strings.Contains(accountRefundColumns, "metadata") {
		t.Fatalf("account refund columns unexpectedly expose metadata: %s", accountRefundColumns)
	}
}

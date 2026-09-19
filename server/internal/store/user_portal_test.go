package store

import (
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
)

func TestNormalizeUserPortalLimit(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name string
		in   int
		want int
	}{
		{name: "zero uses default", in: 0, want: userPortalDefaultLimit},
		{name: "negative uses default", in: -1, want: userPortalDefaultLimit},
		{name: "one retained", in: 1, want: 1},
		{name: "maximum retained", in: userPortalMaxLimit, want: userPortalMaxLimit},
		{name: "over maximum clamps", in: userPortalMaxLimit + 1, want: userPortalMaxLimit},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := normalizeUserPortalLimit(test.in); got != test.want {
				t.Fatalf("normalizeUserPortalLimit(%d)=%d, want %d", test.in, got, test.want)
			}
		})
	}
}

func TestNormalizeUserPortalPageRejectsPartialCursor(t *testing.T) {
	t.Parallel()
	valid := UserPortalUUIDCursor{CreatedAt: time.Now().UTC(), ID: uuid.New()}
	if _, _, _, err := normalizeUserPortalPage(UserPortalPageRequest{Before: &valid}); err != nil {
		t.Fatalf("valid cursor: %v", err)
	}
	for _, cursor := range []UserPortalUUIDCursor{
		{ID: uuid.New()},
		{CreatedAt: time.Now().UTC()},
	} {
		cursor := cursor
		if _, _, _, err := normalizeUserPortalPage(UserPortalPageRequest{Before: &cursor}); err != ErrInvalidUserPortalPage {
			t.Fatalf("partial cursor err=%v, want ErrInvalidUserPortalPage", err)
		}
	}
}

func TestUserPortalDTOsExposeOnlyContractFields(t *testing.T) {
	t.Parallel()
	assertFields(t, UserPortalAccountOverview{}, "ID", "Role", "Status", "CreatedAt", "AndroidDevices", "PCDevices", "PCs", "OnlinePCs", "Sessions", "ActiveSessions")
	assertFields(t, UserPortalDevice{}, "ID", "Type", "Name", "TrustState", "Online", "LastSeenAt", "CreatedAt", "RevokedAt")
	assertFields(t, UserPortalPC{}, "ID", "DeviceID", "Name", "AgentVersion", "Platform", "Architecture", "PlatformVersion", "RemoteRoutingEnabled", "Online", "LastSeenAt", "CreatedAt")
	assertFields(t, UserPortalSession{}, "ID", "PCID", "PCName", "State", "Online", "CreatedAt", "EndedAt")
}

func TestUserPortalSQLIsAccountScopedAndOmitsSensitiveColumns(t *testing.T) {
	t.Parallel()
	queries := map[string]string{
		"overview": userPortalAccountOverviewSQL,
		"devices":  userPortalDevicesSQL,
		"pcs":      userPortalPCsSQL,
		"sessions": userPortalSessionsSQL,
	}
	for name, query := range queries {
		query := strings.ToLower(query)
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(query, "$1") {
				t.Fatal("query lacks account parameter")
			}
			if !strings.Contains(query, "account.status='active'") {
				t.Fatal("query lacks active-account fence")
			}
			if !strings.Contains(query, "account.id=$1") {
				t.Fatal("query lacks account identity fence")
			}
			for _, forbidden := range []string{
				"title_cipher", "last_output_seq", "event_chunks", "snapshots",
				"object_key", "ciphertext", "payment_payload", "secret_refs",
				"provider_reference", "provider_trade", "order_no", "refund_no",
				"audit.metadata", "trace_id", "ip_hash", "terminal_commands",
			} {
				if strings.Contains(query, forbidden) {
					t.Fatalf("query contains forbidden data source %q", forbidden)
				}
			}
		})
	}
}

func TestUserPortalListSQLUsesBoundedStableKeysetOrdering(t *testing.T) {
	t.Parallel()
	for name, query := range map[string]string{
		"devices":  userPortalDevicesSQL,
		"pcs":      userPortalPCsSQL,
		"sessions": userPortalSessionsSQL,
	} {
		query := strings.ToLower(query)
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(query, "created_at,") || !strings.Contains(query, "order by") || !strings.Contains(query, "limit $5") {
				t.Fatalf("query is not a bounded created_at/UUID keyset query: %s", query)
			}
		})
	}
}

func assertFields(t *testing.T, value any, expected ...string) {
	t.Helper()
	typeOf := reflect.TypeOf(value)
	if typeOf.NumField() != len(expected) {
		t.Fatalf("%s has %d exported fields, want %d", typeOf.Name(), typeOf.NumField(), len(expected))
	}
	for index, name := range expected {
		if got := typeOf.Field(index).Name; got != name {
			t.Fatalf("%s field %d=%s, want %s", typeOf.Name(), index, got, name)
		}
	}
}

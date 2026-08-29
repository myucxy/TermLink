package store

import (
	"reflect"
	"strings"
	"testing"
)

func TestRegistrationRecoveryCandidateQueryIsBoundedAndRedacted(t *testing.T) {
	query := strings.ToLower(strings.Join(strings.Fields(listRegistrationRecoveryCandidatesSQL), " "))
	for _, required := range []string{
		"state in ('reserved','released')", "user_id is null", "limit $2",
		"registration.recovery.orphan_deleted", "order by case",
	} {
		if !strings.Contains(query, required) {
			t.Errorf("candidate query lacks %q: %s", required, query)
		}
	}
	for _, forbidden := range []string{"email_hash", "code_hash", "provider_ref", "metadata", "username", "password"} {
		if strings.Contains(query, forbidden) {
			t.Errorf("candidate query exposes %q", forbidden)
		}
	}
}

func TestRegistrationRecoveryCandidateDTOIsMinimal(t *testing.T) {
	typeOf := reflect.TypeOf(RegistrationRecoveryCandidate{})
	want := []string{"AttemptID", "State", "ReservedUntil", "ReleasedAt", "UpdatedAt"}
	if typeOf.NumField() != len(want) {
		t.Fatalf("candidate has %d fields, want %d", typeOf.NumField(), len(want))
	}
	for i, name := range want {
		if typeOf.Field(i).Name != name {
			t.Errorf("field %d=%s, want %s", i, typeOf.Field(i).Name, name)
		}
	}
}

func TestNormalizeRegistrationRecoveryLimit(t *testing.T) {
	for input, want := range map[int]int{-1: 50, 0: 50, 1: 1, 200: 200, 201: 200} {
		if got := normalizeRegistrationRecoveryLimit(input); got != want {
			t.Errorf("normalize(%d)=%d, want %d", input, got, want)
		}
	}
}

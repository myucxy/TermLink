package store

import (
	"testing"

	"github.com/google/uuid"
)

func TestManagedBootstrapRebindRequiresAllRecoveryGuards(t *testing.T) {
	oldID := uuid.MustParse("11111111-1111-4111-8111-111111111111")
	eligible := []portalBootstrapCandidate{{ID: oldID, BootstrapManaged: true}}
	tests := []struct {
		name       string
		assertion  PortalIdentityAssertion
		candidates []portalBootstrapCandidate
		allowed    bool
	}{
		{name: "managed bootstrap", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}, candidates: eligible, allowed: true},
		{name: "generic super admin claim", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin}, candidates: eligible},
		{name: "managed marker without super admin", assertion: PortalIdentityAssertion{Role: RoleUser, BootstrapManaged: true}, candidates: eligible},
		{name: "no previous super admin", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}},
		{name: "multiple super admins", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}, candidates: append(eligible, portalBootstrapCandidate{ID: uuid.New(), BootstrapManaged: true})},
		{name: "manually managed previous account", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}, candidates: []portalBootstrapCandidate{{ID: oldID}}},
		{name: "previous account owns devices", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}, candidates: []portalBootstrapCandidate{{ID: oldID, BootstrapManaged: true, DeviceCount: 1}}},
		{name: "invalid previous account", assertion: PortalIdentityAssertion{Role: RoleSuperAdmin, BootstrapManaged: true}, candidates: []portalBootstrapCandidate{{BootstrapManaged: true}}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, ok := managedBootstrapRebindCandidate(test.assertion, test.candidates)
			if ok != test.allowed {
				t.Fatalf("allowed=%t, want %t", ok, test.allowed)
			}
			if test.allowed && got != oldID {
				t.Fatalf("candidate=%s, want %s", got, oldID)
			}
			if !test.allowed && got != uuid.Nil {
				t.Fatalf("rejected candidate=%s, want nil", got)
			}
		})
	}
}

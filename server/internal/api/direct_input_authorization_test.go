package api

import (
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/kv"
	"github.com/termlink/termlink/server/internal/store"
)

func TestDirectInputLeaseAuthorizationMatchesControlPolicy(t *testing.T) {
	sender := uuid.New()
	other := uuid.New()
	basePolicy := store.SessionControlPolicy{
		Mode:          store.ControlModeSingle,
		LocalPriority: true,
		PolicyVersion: 3,
		PCID:          uuid.New(),
		PCDeviceID:    uuid.New(),
	}
	baseLease := kv.Lease{
		SessionID:      uuid.New(),
		HolderDeviceID: sender,
		ID:             uuid.New(),
		Version:        7,
		ExpiresAt:      time.Now().Add(time.Minute),
	}
	tests := []struct {
		name              string
		permission        string
		policy            store.SessionControlPolicy
		lease             kv.Lease
		sender            uuid.UUID
		participantActive bool
		want              bool
	}{
		{name: "single holder", permission: "CONTROL", policy: basePolicy, lease: baseLease, sender: sender, participantActive: true, want: true},
		{name: "manage holder", permission: "MANAGE", policy: basePolicy, lease: baseLease, sender: sender, participantActive: true, want: true},
		{name: "single non holder", permission: "CONTROL", policy: basePolicy, lease: baseLease, sender: other, participantActive: true},
		{name: "takeover non holder", permission: "CONTROL", policy: withControlMode(basePolicy, store.ControlModeTakeover), lease: baseLease, sender: other, participantActive: true},
		{name: "collaborative participant", permission: "CONTROL", policy: withControlMode(basePolicy, store.ControlModeCollaborative), lease: baseLease, sender: other, participantActive: true, want: true},
		{name: "collaborative non participant", permission: "CONTROL", policy: withControlMode(basePolicy, store.ControlModeCollaborative), lease: baseLease, sender: other},
		{name: "view denied", permission: "VIEW", policy: withControlMode(basePolicy, store.ControlModeCollaborative), lease: baseLease, sender: other, participantActive: true},
		{name: "expired denied", permission: "CONTROL", policy: basePolicy, lease: withLeaseExpiry(baseLease, time.Now().Add(-time.Second)), sender: sender, participantActive: true},
		{name: "empty sender denied", permission: "CONTROL", policy: basePolicy, lease: baseLease, sender: uuid.Nil, participantActive: true},
		{name: "invalid policy denied", permission: "CONTROL", policy: store.SessionControlPolicy{}, lease: baseLease, sender: sender, participantActive: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := directInputLeaseAuthorized(test.permission, test.policy, test.lease, test.sender, test.participantActive); got != test.want {
				t.Fatalf("directInputLeaseAuthorized() = %t, want %t", got, test.want)
			}
		})
	}
}

func withControlMode(policy store.SessionControlPolicy, mode store.ControlMode) store.SessionControlPolicy {
	policy.Mode = mode
	return policy
}

func withLeaseExpiry(lease kv.Lease, expiresAt time.Time) kv.Lease {
	lease.ExpiresAt = expiresAt
	return lease
}

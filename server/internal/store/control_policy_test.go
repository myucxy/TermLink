package store

import (
	"testing"

	"github.com/google/uuid"
)

func TestSessionControlPolicyValidation(t *testing.T) {
	base := SessionControlPolicy{Mode: ControlModeSingle, LocalPriority: true, PolicyVersion: 1, PCID: uuid.New(), PCDeviceID: uuid.New()}
	if !base.Valid() {
		t.Fatal("valid SINGLE control policy was rejected")
	}
	for _, mode := range []ControlMode{ControlModeSingle, ControlModeTakeover, ControlModeCollaborative} {
		policy := base
		policy.Mode = mode
		if !policy.Valid() {
			t.Fatalf("valid %s control policy was rejected", mode)
		}
	}
	invalid := base
	invalid.Mode = "UNSAFE"
	if invalid.Valid() {
		t.Fatal("unknown control mode was accepted")
	}
	invalid = base
	invalid.PolicyVersion = 0
	if invalid.Valid() {
		t.Fatal("zero policy version was accepted")
	}
}

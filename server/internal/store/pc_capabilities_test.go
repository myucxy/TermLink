package store

import (
	"reflect"
	"testing"
)

func TestApplyLegacyPCDefaultsDoesNotInferSnapshotFromHeartbeat(t *testing.T) {
	item := PCRecord{SessionHeartbeatCapable: true}

	applyLegacyPCDefaults(&item)

	want := []string{PCCapabilityRealtimeTerminalV1, PCCapabilityFileOperationsV1}
	if !reflect.DeepEqual(item.Capabilities, want) {
		t.Fatalf("capabilities = %#v, want %#v", item.Capabilities, want)
	}
	if HasPCCapability(item.Capabilities, PCCapabilityTerminalSnapshotV1) {
		t.Fatalf("heartbeat incorrectly inferred snapshot capability: %#v", item.Capabilities)
	}
}

func TestApplyLegacyPCDefaultsPreservesExplicitCapabilities(t *testing.T) {
	want := []string{
		PCCapabilityRealtimeTerminalV1,
		PCCapabilityTerminalSnapshotV1,
		"future_terminal_presentation_v2",
	}
	item := PCRecord{
		SessionHeartbeatCapable: true,
		Capabilities:            append([]string(nil), want...),
	}

	applyLegacyPCDefaults(&item)

	if !reflect.DeepEqual(item.Capabilities, want) {
		t.Fatalf("explicit capabilities = %#v, want %#v", item.Capabilities, want)
	}
}

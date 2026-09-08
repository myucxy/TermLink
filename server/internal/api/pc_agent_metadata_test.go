package api

import (
	"reflect"
	"testing"

	"github.com/google/uuid"
	"github.com/termlink/termlink/server/internal/store"
)

func TestDeviceListV1ProjectsPCAgentForReleasedAndroidEnums(t *testing.T) {
	items := []store.DeviceRecord{
		{ID: uuid.New(), Type: store.DeviceTypePCAgent},
		{ID: uuid.New(), Type: "ANDROID"},
	}
	projectDeviceTypesForV1(items)
	if items[0].Type != store.DeviceTypeLegacyWindowsPC || items[1].Type != "ANDROID" {
		t.Fatalf("unexpected V1 device projection: %#v", items)
	}
}

func TestNormalizePCAgentMetadataRequiresBoundedCapabilities(t *testing.T) {
	platform, architecture, version, capabilities, err := normalizePCAgentMetadata(
		store.DeviceTypePCAgent,
		"windows",
		"x64",
		"Windows 11 24H2",
		[]string{
			store.PCCapabilityRealtimeTerminalV1,
			store.PCCapabilityFileOperationsV1,
			store.PCCapabilityTerminalCommandsV1,
		},
		true,
		false,
		[]string{"powershell-7"},
		[]store.TerminalCommandCapability{{
			ID: "powershell-7", DisplayName: "PowerShell 7", ShellType: "powershell-7", Source: "detected",
		}},
	)
	if err != nil {
		t.Fatal(err)
	}
	if platform != "windows" || architecture != "x64" || version != "Windows 11 24H2" ||
		!store.HasPCCapability(capabilities, store.PCCapabilityFileOperationsV1) {
		t.Fatalf("unexpected normalized PC Agent metadata: %q %q %q %#v", platform, architecture, version, capabilities)
	}

	_, _, _, _, err = normalizePCAgentMetadata(
		store.DeviceTypePCAgent, "linux", "arm64", "Ubuntu", []string{store.PCCapabilityRealtimeTerminalV1},
		true, false, []string{"powershell-7"}, nil,
	)
	if err == nil {
		t.Fatal("terminal commands were accepted without terminal_commands_v1")
	}
}

func TestNormalizeLegacyWindowsMetadataSynthesizesCompatibilityCapabilities(t *testing.T) {
	platform, architecture, version, capabilities, err := normalizePCAgentMetadata(
		store.DeviceTypeLegacyWindowsPC, "", "", "", nil,
		true, true, []string{"command-prompt"}, nil,
	)
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{
		store.PCCapabilityRealtimeTerminalV1,
		store.PCCapabilityFileOperationsV1,
		store.PCCapabilityTerminalCommandsV1,
		store.PCCapabilityRemoteAgentUpdateV1,
	} {
		if !store.HasPCCapability(capabilities, expected) {
			t.Fatalf("legacy compatibility capability %q is missing from %#v", expected, capabilities)
		}
	}
	if platform != "windows" || architecture != "unknown" || version != "legacy" {
		t.Fatalf("unexpected legacy defaults: %q %q %q", platform, architecture, version)
	}
	if store.HasPCCapability(capabilities, store.PCCapabilityTerminalSnapshotV1) {
		t.Fatalf("legacy heartbeat incorrectly synthesized snapshot capability: %#v", capabilities)
	}
}

func TestNormalizePCAgentMetadataPreservesOnlyExplicitSnapshotCapability(t *testing.T) {
	const futureCapability = "future_terminal_presentation_v2"
	want := []string{
		store.PCCapabilityRealtimeTerminalV1,
		store.PCCapabilityTerminalSnapshotV1,
		futureCapability,
	}
	_, _, _, capabilities, err := normalizePCAgentMetadata(
		store.DeviceTypeLegacyWindowsPC, "", "", "", append([]string(nil), want...),
		true, false, nil, nil,
	)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(capabilities, want) {
		t.Fatalf("explicit and unknown capabilities = %#v, want %#v", capabilities, want)
	}
}

func TestNormalizePCAgentMetadataRejectsDuplicateCapabilities(t *testing.T) {
	_, _, _, _, err := normalizePCAgentMetadata(
		store.DeviceTypePCAgent, "windows", "x64", "Windows 11", []string{
			store.PCCapabilityRealtimeTerminalV1,
			store.PCCapabilityTerminalSnapshotV1,
			store.PCCapabilityTerminalSnapshotV1,
		},
		false, false, nil, nil,
	)
	if err == nil {
		t.Fatal("duplicate PC capabilities were accepted")
	}
}

func TestProjectPCServerCapabilitiesRequiresAgentAndServerSupport(t *testing.T) {
	items := []store.PCRecord{
		{Capabilities: []string{store.PCCapabilityFileOperationsV1, store.PCCapabilityFileNameFilterV1}},
		{Capabilities: []string{store.PCCapabilityFileOperationsV1}},
	}
	projectPCServerCapabilities(items)
	if !store.HasPCCapability(items[0].Capabilities, store.PCCapabilityFileSearchV1) {
		t.Fatal("server did not expose search for a capable Agent")
	}
	if store.HasPCCapability(items[1].Capabilities, store.PCCapabilityFileSearchV1) {
		t.Fatal("server exposed search for an incapable Agent")
	}
}

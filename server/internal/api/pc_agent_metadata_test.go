package api

import (
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
		store.PCCapabilityTerminalSnapshotV1,
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
}

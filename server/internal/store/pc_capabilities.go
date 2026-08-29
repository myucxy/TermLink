package store

const (
	DeviceTypeLegacyWindowsPC = "WINDOWS_PC"
	DeviceTypePCAgent         = "PC_AGENT"

	PCPlatformWindows = "windows"
	PCPlatformLinux   = "linux"
	PCPlatformMacOS   = "macos"

	PCArchitectureX64     = "x64"
	PCArchitectureArm64   = "arm64"
	PCArchitectureUnknown = "unknown"

	PCCapabilityRealtimeTerminalV1  = "realtime_terminal_v1"
	PCCapabilityTerminalSnapshotV1  = "terminal_snapshot_v1"
	PCCapabilityTerminalSnapshotV2  = "terminal_snapshot_v2"
	PCCapabilityFileOperationsV1    = "file_operations_v1"
	PCCapabilityTerminalCommandsV1  = "terminal_commands_v1"
	PCCapabilityRemoteAgentUpdateV1 = "remote_agent_update_v1"

	MaxPCCapabilities = 64
)

func IsPCAgentDeviceType(deviceType string) bool {
	return deviceType == DeviceTypeLegacyWindowsPC || deviceType == DeviceTypePCAgent
}

func HasPCCapability(capabilities []string, expected string) bool {
	for _, capability := range capabilities {
		if capability == expected {
			return true
		}
	}
	return false
}

func applyLegacyPCDefaults(item *PCRecord) {
	if item.Platform == "" {
		item.Platform = PCPlatformWindows
	}
	if item.Architecture == "" {
		item.Architecture = PCArchitectureUnknown
	}
	if item.PlatformVersion == "" {
		item.PlatformVersion = "legacy"
	}
	if item.Capabilities != nil {
		return
	}
	// Heartbeat support only describes liveness reporting. Snapshot restore is
	// fail-closed and must be explicitly advertised by the Agent.
	item.Capabilities = []string{PCCapabilityRealtimeTerminalV1, PCCapabilityFileOperationsV1}
	if len(item.SupportedShells) > 0 || len(item.TerminalCommands) > 0 {
		item.Capabilities = append(item.Capabilities, PCCapabilityTerminalCommandsV1)
	}
	if item.RemoteAgentUpdateCapable {
		item.Capabilities = append(item.Capabilities, PCCapabilityRemoteAgentUpdateV1)
	}
}

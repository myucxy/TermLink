using System.Collections.ObjectModel;

namespace TermLink.Pc.Contracts;

public enum PcPlatform
{
    Unknown = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 3,
}
public enum PcArchitecture
{
    Unknown = 0,
    X64 = 1,
    Arm64 = 2,
}

public enum AgentConnectionState
{
    NotConfigured = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    ReauthenticationRequired = 4,
    Offline = 5,
    Faulted = 6,
}

public enum ManagedSessionState
{
    Starting = 0,
    Connected = 1,
    Disconnected = 2,
    Ended = 3,
    Interrupted = 4,
    Faulted = 5,
}

public static class PcCapabilityIds
{
    public const string RealtimeTerminalV1 = "realtime_terminal_v1";
    public const string TerminalSnapshotV1 = "terminal_snapshot_v1";
    public const string FileOperationsV1 = "file_operations_v1";
    public const string TerminalCommandsV1 = "terminal_commands_v1";
    public const string RemoteAgentUpdateV1 = "remote_agent_update_v1";
}

public static class PcContractLimits
{
    public const int MaximumCapabilities = 64;
    public const int MaximumDevices = 256;
    public const int MaximumSessions = 512;
    public const int MaximumTerminalCommands = 24;
    public const int MaximumIdentifierBytes = 64;
    public const int MaximumDisplayNameBytes = 160;
    public const int MaximumDetailBytes = 1024;
}

public sealed record PcAgentIdentity(
    Guid PcId,
    string Name,
    PcPlatform Platform,
    PcArchitecture Architecture,
    string PlatformVersion,
    string AgentVersion);

public sealed record TerminalCommandCapability(
    string Id,
    string DisplayName,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool Detected,
    bool RunAsAdministrator = true,
    bool Enabled = true);

public sealed record ManagedSessionSnapshot(
    Guid SessionId,
    string Title,
    string ShellType,
    ManagedSessionState State,
    int ViewerCount,
    DateTimeOffset LastActivityAt,
    ulong LastSequence);

public sealed record AccountDeviceSnapshot(
    Guid DeviceId,
    string Name,
    string DeviceType,
    string TrustState,
    bool Online);

public sealed record AgentStatusSnapshot(
    long Revision,
    DateTimeOffset ObservedAt,
    PcAgentIdentity Identity,
    AgentConnectionState ConnectionState,
    bool AccountLinked,
    string AccountDisplayName,
    string ServerOrigin,
    bool RemoteAccessEnabled,
    string? LastErrorCode,
    string? LastErrorDetail,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<TerminalCommandCapability> TerminalCommands,
    IReadOnlyList<ManagedSessionSnapshot> Sessions,
    IReadOnlyList<AccountDeviceSnapshot> Devices)
{
    public static AgentStatusSnapshot Disconnected(PcAgentIdentity identity, string code, string detail) =>
        new(
            0,
            DateTimeOffset.UtcNow,
            identity,
            AgentConnectionState.Offline,
            false,
            string.Empty,
            string.Empty,
            false,
            code,
            detail,
            ReadOnlySet<string>.Empty,
            Array.Empty<TerminalCommandCapability>(),
            Array.Empty<ManagedSessionSnapshot>(),
            Array.Empty<AccountDeviceSnapshot>());
}

public static class PcContractValidator
{
    public static void Validate(AgentStatusSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateIdentity(value.Identity);
        ValidateUtf8(value.AccountDisplayName, PcContractLimits.MaximumDisplayNameBytes, nameof(value.AccountDisplayName), true);
        ValidateUtf8(value.ServerOrigin, PcContractLimits.MaximumDetailBytes, nameof(value.ServerOrigin), true);
        ValidateUtf8(value.LastErrorCode, PcContractLimits.MaximumIdentifierBytes, nameof(value.LastErrorCode), true);
        ValidateUtf8(value.LastErrorDetail, PcContractLimits.MaximumDetailBytes, nameof(value.LastErrorDetail), true);
        if (value.Capabilities.Count > PcContractLimits.MaximumCapabilities) throw new InvalidDataException("Too many PC capabilities.");
        if (value.TerminalCommands.Count > PcContractLimits.MaximumTerminalCommands) throw new InvalidDataException("Too many terminal commands.");
        if (value.Sessions.Count > PcContractLimits.MaximumSessions) throw new InvalidDataException("Too many sessions.");
        if (value.Devices.Count > PcContractLimits.MaximumDevices) throw new InvalidDataException("Too many devices.");
        foreach (var capability in value.Capabilities)
            ValidateUtf8(capability, PcContractLimits.MaximumIdentifierBytes, "capability", false);
        foreach (var command in value.TerminalCommands) ValidateCommand(command);
        foreach (var session in value.Sessions)
        {
            if (session.SessionId == Guid.Empty) throw new InvalidDataException("A session ID is empty.");
            ValidateUtf8(session.Title, PcContractLimits.MaximumDisplayNameBytes, "session title", true);
            ValidateUtf8(session.ShellType, PcContractLimits.MaximumIdentifierBytes, "shell type", false);
        }
    }

    private static void ValidateIdentity(PcAgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.PcId == Guid.Empty) throw new InvalidDataException("The PC ID is empty.");
        if (identity.Platform == PcPlatform.Unknown) throw new InvalidDataException("The PC platform is unknown.");
        if (identity.Architecture == PcArchitecture.Unknown) throw new InvalidDataException("The PC architecture is unknown.");
        ValidateUtf8(identity.Name, PcContractLimits.MaximumDisplayNameBytes, "PC name", false);
        ValidateUtf8(identity.PlatformVersion, PcContractLimits.MaximumDisplayNameBytes, "platform version", false);
        ValidateUtf8(identity.AgentVersion, PcContractLimits.MaximumIdentifierBytes, "Agent version", false);
    }

    private static void ValidateCommand(TerminalCommandCapability command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUtf8(command.Id, PcContractLimits.MaximumIdentifierBytes, "terminal command ID", false);
        ValidateUtf8(command.DisplayName, PcContractLimits.MaximumDisplayNameBytes, "terminal command name", false);
        ValidateUtf8(command.Executable, PcContractLimits.MaximumDetailBytes, "terminal command executable", false);
        if (command.Arguments.Count > 32) throw new InvalidDataException("A terminal command contains too many arguments.");
        foreach (var argument in command.Arguments)
            ValidateUtf8(argument, PcContractLimits.MaximumDetailBytes, "terminal command argument", true);
    }

    private static void ValidateUtf8(string? value, int maximumBytes, string name, bool allowEmpty)
    {
        value ??= string.Empty;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.IndexOf('\0') >= 0 ||
            System.Text.Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw new InvalidDataException($"The {name} is invalid.");
        }
    }
}

internal static class ReadOnlySet<T> where T : notnull
{
    public static IReadOnlySet<T> Empty { get; } = new HashSet<T>();
}

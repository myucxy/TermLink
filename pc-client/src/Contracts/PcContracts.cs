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

/// <summary>
/// The data-plane carrier currently used by a managed session.
/// Relay is deliberately the zero/default value so older Agent IPC responses
/// remain safe while the optional carrier field is rolled out.
/// </summary>
public enum SessionDataPlaneCarrier : byte
{
    Relay = 0,
    Negotiating = 1,
    LanDirect = 2,
    NatDirect = 3,
}

/// <summary>
/// Compatibility seam for the optional Agent IPC carrier field. The desktop
/// client may be newer than the Agent, so absence or an unrecognised value
/// must never be interpreted as a direct path.
/// </summary>
public static class SessionDataPlaneCarrierContract
{
    private static readonly string[] CarrierPropertyNames =
    [
        "Carrier",
        "DataPlaneCarrier",
        "TransportCarrier",
        "ConnectionCarrier",
    ];

    public static SessionDataPlaneCarrier ReadFromAgentSummary(object? summary)
    {
        if (summary is null) return SessionDataPlaneCarrier.Relay;

        var type = summary.GetType();
        foreach (var name in CarrierPropertyNames)
        {
            var property = type.GetProperty(name, System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
            if (property is null || property.GetIndexParameters().Length != 0) continue;
            try
            {
                return Parse(property.GetValue(summary));
            }
            catch (Exception error) when (error is MemberAccessException or System.Reflection.TargetInvocationException)
            {
                return SessionDataPlaneCarrier.Relay;
            }
        }

        return SessionDataPlaneCarrier.Relay;
    }

    public static SessionDataPlaneCarrier Normalize(SessionDataPlaneCarrier value) =>
        Enum.IsDefined(value) ? value : SessionDataPlaneCarrier.Relay;

    public static SessionDataPlaneCarrier Parse(object? value)
    {
        if (value is SessionDataPlaneCarrier carrier) return Normalize(carrier);
        if (value is Enum enumValue)
            return ParseName(Enum.GetName(enumValue.GetType(), enumValue));
        if (value is string text) return ParseName(text);

        // Numeric values are accepted only for the frozen 0..3 wire mapping.
        // Any other value is intentionally treated as Relay.
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
        {
            try
            {
                var numeric = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                return numeric switch
                {
                    0 => SessionDataPlaneCarrier.Relay,
                    1 => SessionDataPlaneCarrier.Negotiating,
                    2 => SessionDataPlaneCarrier.LanDirect,
                    3 => SessionDataPlaneCarrier.NatDirect,
                    _ => SessionDataPlaneCarrier.Relay,
                };
            }
            catch (OverflowException)
            {
                return SessionDataPlaneCarrier.Relay;
            }
        }

        return SessionDataPlaneCarrier.Relay;
    }

    private static SessionDataPlaneCarrier ParseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SessionDataPlaneCarrier.Relay;
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();
        return normalized switch
        {
            "RELAY" or "RELAYONLY" or "SERVERRELAY" or "WEBSOCKETRELAY" => SessionDataPlaneCarrier.Relay,
            "NEGOTIATING" or "NEGOTIATINGDIRECT" or "DIRECTNEGOTIATING" or "DIRECTPENDING" => SessionDataPlaneCarrier.Negotiating,
            "LANDIRECT" or "LAN" or "LOCALDIRECT" or "LOCALNETWORKDIRECT" or
                "LOCALAREANETWORKDIRECT" => SessionDataPlaneCarrier.LanDirect,
            "NATDIRECT" or "NAT" or "UDPDIRECT" or "QUICDIRECT" or "DIRECTNAT" or
                "NATTRAVERSALDIRECT" => SessionDataPlaneCarrier.NatDirect,
            _ => SessionDataPlaneCarrier.Relay,
        };
    }
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

public sealed record AiAgentCapability(string Id, string Name, string Path, string Version, string Status, string Strategy = "native")
{
    public string Detail => Strategy == "desktop_remote" ? (Status == "available" ? "桌面远控 · 已配置地址" : "桌面远控 · 请重新保存地址") : $"{Version} · " + (Status switch {
        "available" => "协议可用 · 认证尚未验证",
        "missing" => "未检测到安装",
        "runtime_missing" => "缺少运行时",
        "desktop_only" => "桌面版已安装，未发现编码接口；请安装对应 CLI",
        "runtime_blocked" => "已检测到引擎，但系统阻止启动；请修复安装或使用 CLI",
        "incompatible" => "结构化协议未支持",
        _ => "检测失败，可重试"
    });
}

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
    ulong LastSequence,
    SessionDataPlaneCarrier Carrier = SessionDataPlaneCarrier.Relay)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TerminalDiagnostic { get; init; }
    public override string ToString() => $"ManagedSessionSnapshot {{ SessionId = {SessionId}, State = {State} }}";
}

public sealed record AiSessionSummary(string Id, string Title, string Agent, string Directory, string State);

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
    string AccountUsername,
    string AccountNickname,
    string ServerOrigin,
    bool RemoteAccessEnabled,
    string? LastErrorCode,
    string? LastErrorDetail,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<TerminalCommandCapability> TerminalCommands,
    IReadOnlyList<ManagedSessionSnapshot> Sessions,
    IReadOnlyList<AccountDeviceSnapshot> Devices)
{
    public bool StatusKnown { get; init; } = true;
    public IReadOnlyList<AiSessionSummary> AiSessions { get; init; } = Array.Empty<AiSessionSummary>();
    public int ActiveAiSessions { get; init; }
    public bool SessionDetailsAvailable { get; init; }
    public bool AiSessionsTruncated { get; init; }
    public static AgentStatusSnapshot Disconnected(PcAgentIdentity identity, string code, string detail) =>
        new(
            0,
            DateTimeOffset.UtcNow,
            identity,
            AgentConnectionState.Offline,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            code,
            detail,
            ReadOnlySet<string>.Empty,
            Array.Empty<TerminalCommandCapability>(),
            Array.Empty<ManagedSessionSnapshot>(),
            Array.Empty<AccountDeviceSnapshot>()) { StatusKnown = false };
}

public static class PcContractValidator
{
    public static void Validate(AgentStatusSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateIdentity(value.Identity);
        ValidateUtf8(value.AccountUsername, PcContractLimits.MaximumDisplayNameBytes, nameof(value.AccountUsername), true);
        ValidateUtf8(value.AccountNickname, PcContractLimits.MaximumDisplayNameBytes, nameof(value.AccountNickname), true);
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

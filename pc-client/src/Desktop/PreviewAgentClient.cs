using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Desktop;

public sealed class PreviewAgentClient : IAgentClient
{
    private readonly PlatformDescriptor descriptor;
    private readonly bool fixtureEnabled;
    private long revision = 1;
    private bool remoteAccessEnabled;

    public PreviewAgentClient(PlatformDescriptor descriptor, bool fixtureEnabled)
    {
        this.descriptor = descriptor;
        this.fixtureEnabled = fixtureEnabled;
        remoteAccessEnabled = fixtureEnabled;
    }

    public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateSnapshot());
    }

    public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        remoteAccessEnabled = enabled;
        Interlocked.Increment(ref revision);
        return ValueTask.CompletedTask;
    }

    public ValueTask RetryConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref revision);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Unsupported(cancellationToken);

    public ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken) =>
        Unsupported(cancellationToken);

    public ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        Unsupported(cancellationToken);

    public ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken) =>
        Unsupported(cancellationToken);

    public ValueTask RegisterAsync(
        string username,
        string email,
        string password,
        string passwordConfirmation,
        string pcName,
        CancellationToken cancellationToken) => Unsupported(cancellationToken);

    public ValueTask SignOutAsync(CancellationToken cancellationToken) => Unsupported(cancellationToken);

    public ValueTask SaveTerminalCommandsAsync(
        IReadOnlyList<TerminalCommandCapability> commands,
        CancellationToken cancellationToken) => Unsupported(cancellationToken);

    private static ValueTask Unsupported(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new NotSupportedException("The preview fixture does not mutate a production Agent."));
    }

    private AgentStatusSnapshot CreateSnapshot()
    {
        var identity = new PcAgentIdentity(
            Guid.Parse("27bfa40c-684a-4a2f-8c3c-a9095d969ed9"),
            fixtureEnabled ? "TermLink Avalonia Preview" : Environment.MachineName,
            descriptor.Platform,
            descriptor.Architecture,
            descriptor.PlatformVersion,
            "0.2.0-preview.1");
        if (!fixtureEnabled)
        {
            return AgentStatusSnapshot.Disconnected(
                identity,
                "PC-PREVIEW-NO_AGENT",
                "P2 预览模式不会连接或接管当前生产 Agent；使用 --preview-fixture 查看示例数据。") with
            {
                Revision = Volatile.Read(ref revision),
            };
        }

        var observedAt = DateTimeOffset.UtcNow;
        return new AgentStatusSnapshot(
            Volatile.Read(ref revision),
            observedAt,
            identity,
            AgentConnectionState.Connected,
            true,
            "Preview Account",
            "https://preview.invalid",
            remoteAccessEnabled,
            null,
            null,
            new HashSet<string>
            {
                PcCapabilityIds.RealtimeTerminalV1,
                PcCapabilityIds.TerminalSnapshotV1,
                PcCapabilityIds.FileOperationsV1,
                PcCapabilityIds.TerminalCommandsV1,
            },
            new[]
            {
                new TerminalCommandCapability("pwsh", "PowerShell", "pwsh", Array.Empty<string>(), true),
                new TerminalCommandCapability("wsl", "WSL", "wsl", Array.Empty<string>(), true),
            },
            new[]
            {
                new ManagedSessionSnapshot(
                    Guid.Parse("72a67cd4-ee16-499b-b479-ce00ecae6694"),
                    "PowerShell · Avalonia preview",
                    "pwsh",
                    ManagedSessionState.Connected,
                    1,
                    observedAt,
                    42),
            },
            Array.Empty<AccountDeviceSnapshot>());
    }
}

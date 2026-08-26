using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;

namespace TermLink.Agent.Host;

public sealed class PortableAgentRuntime : ILocalAgentCommandHandler
{
    private const string IdentitySecretName = "pc-identity-v1";
    private readonly IPlatformServices platform;
    private readonly PcAgentIdentity identity;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private IReadOnlyList<TerminalCommandCapability> commands;
    private long revision = 1;

    private PortableAgentRuntime(
        IPlatformServices platform,
        PcAgentIdentity identity,
        IReadOnlyList<TerminalCommandCapability> commands)
    {
        this.platform = platform;
        this.identity = identity;
        this.commands = commands;
    }

    public static async ValueTask<PortableAgentRuntime> CreateAsync(
        IPlatformServices platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var idBytes = await platform.SecretStore.RetrieveAsync(IdentitySecretName, cancellationToken).ConfigureAwait(false);
        Guid id;
        if (idBytes is null)
        {
            id = Guid.NewGuid();
            idBytes = id.ToByteArray();
            await platform.SecretStore.StoreAsync(IdentitySecretName, idBytes, cancellationToken).ConfigureAwait(false);
        }
        else if (idBytes.Length == 16) id = new Guid(idBytes);
        else
        {
            CryptographicOperations.ZeroMemory(idBytes);
            throw new CryptographicException("The portable Agent identity is invalid.");
        }
        CryptographicOperations.ZeroMemory(idBytes);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var identity = new PcAgentIdentity(
            id,
            Environment.MachineName,
            platform.Descriptor.Platform,
            platform.Descriptor.Architecture,
            platform.Descriptor.PlatformVersion,
            version);
        var commands = await platform.TerminalCommands.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return new PortableAgentRuntime(platform, identity, commands);
    }

    public async ValueTask<LocalAgentCommandResult> HandleAsync(
        LocalAgentCommand command,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case LocalAgentCommand.GetStatus:
                return LocalAgentCommandResult.Ok(CreateStatus());
            case LocalAgentCommand.SaveTerminalCommands:
            {
                var value = Deserialize<TerminalCommandsPayload>(payload);
                if (value.Commands.Count > PcContractLimits.MaximumTerminalCommands)
                    return LocalAgentCommandResult.Reject("TOO_MANY_TERMINAL_COMMANDS");
                await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    commands = value.Commands.ToArray();
                    revision++;
                }
                finally { stateGate.Release(); }
                return LocalAgentCommandResult.Ok();
            }
            case LocalAgentCommand.StopSession:
            {
                var value = Deserialize<SessionPayload>(payload);
                await platform.TerminalHost.StopAsync(value.SessionId, cancellationToken).ConfigureAwait(false);
                revision++;
                return LocalAgentCommandResult.Ok();
            }
            case LocalAgentCommand.SetRemoteAccess:
            case LocalAgentCommand.RetryConnection:
            case LocalAgentCommand.GrantDevice:
            case LocalAgentCommand.RevokeDevice:
            case LocalAgentCommand.Login:
            case LocalAgentCommand.Register:
            case LocalAgentCommand.SignOut:
                // The process and authenticated IPC are production code, but
                // server Relay enrollment is deliberately fail-closed until
                // the portable Relay worker passes real Linux/macOS E2E.
                return LocalAgentCommandResult.Reject("PORTABLE_RELAY_NOT_CONFIGURED");
            default:
                return LocalAgentCommandResult.Reject("LOCAL_AGENT_COMMAND_UNSUPPORTED");
        }
    }

    private AgentStatusSnapshot CreateStatus() => new(
        Interlocked.Read(ref revision),
        DateTimeOffset.UtcNow,
        identity,
        AgentConnectionState.NotConfigured,
        false,
        string.Empty,
        string.Empty,
        false,
        "PORTABLE_RELAY_NOT_CONFIGURED",
        "The portable Agent is running locally; Relay enrollment has not been completed.",
        new HashSet<string>
        {
            PcCapabilityIds.RealtimeTerminalV1,
            PcCapabilityIds.TerminalSnapshotV1,
            PcCapabilityIds.FileOperationsV1,
            PcCapabilityIds.TerminalCommandsV1,
        },
        commands,
        Array.Empty<ManagedSessionSnapshot>(),
        Array.Empty<AccountDeviceSnapshot>());

    private static T Deserialize<T>(JsonElement? payload) where T : class =>
        payload?.Deserialize<T>(LocalAgentProtocol.JsonOptions) ??
        throw new InvalidDataException("LOCAL_AGENT_PAYLOAD_INVALID");
}

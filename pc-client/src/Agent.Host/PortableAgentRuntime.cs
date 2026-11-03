using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Agent.Connection;
using TermLink.Agent.AI;
using TermLink.Ai;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Agent.Host;

public sealed class PortableAgentRuntime : ILocalAgentCommandHandler, IAsyncDisposable
{
    private const string IdentitySecretName = "pc-identity-v1";
    private readonly IPlatformServices platform;
    private readonly RelayConnectionService connection;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private IReadOnlyList<TerminalCommandCapability> commands;
    private long revision = 1;
    private readonly object shutdownSync = new();
    private Task? stopping;

    private PortableAgentRuntime(
        IPlatformServices platform,
        RelayConnectionService connection,
        IReadOnlyList<TerminalCommandCapability> commands)
    {
        this.platform = platform;
        this.connection = connection;
        this.commands = commands;
    }

    public static async ValueTask<PortableAgentRuntime> CreateAsync(
        IPlatformServices platform,
        CancellationToken cancellationToken,
        IRelayAuthorization? authorization = null)
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
        var connection = await RelayConnectionService.CreateAsync(platform.SecretStore, identity, cancellationToken,
            authorization: authorization,
            aiHostFactory: platform is TermLink.Platform.MacOS.MacPlatformServices ? scope => new PortableAiHost(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support",
                TermLink.Platform.MacOS.MacPlatformOptions.IsDevelopmentBuild ? "TermLink DEV" : "TermLink", "ai", scope)) : null).ConfigureAwait(false);
        return new PortableAgentRuntime(platform, connection, commands);
    }

    public async ValueTask<LocalAgentCommandResult> HandleAsync(
        LocalAgentCommand command,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (shutdownSync)
            {
                if (stopping is not null && command != LocalAgentCommand.Shutdown)
                    return LocalAgentCommandResult.Reject("PC_AGENT_EXITING");
            }
            switch (command)
            {
                case LocalAgentCommand.Shutdown:
                    await DisposeAsync().ConfigureAwait(false);
                    return LocalAgentCommandResult.Ok(new AgentShutdownReceipt(Environment.ProcessId));
                case LocalAgentCommand.GetStatus:
                    if (payload is { } statusPayload && statusPayload.TryGetProperty("force_refresh", out var refresh) && refresh.ValueKind == JsonValueKind.True)
                    {
                        var detected = await platform.TerminalCommands.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try { commands = detected; }
                        finally { stateGate.Release(); }
                    }
                    return LocalAgentCommandResult.Ok(CreateStatus());
                case LocalAgentCommand.DetectAiAgents:
                {
                    var agents = (await new PosixAiAgentDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false)).ToList();
                    if (OperatingSystem.IsMacOS()) {
                        var zcode = await ZcodeDesktopRuntime.DetectMacAsync(cancellationToken).ConfigureAwait(false);
                        agents.RemoveAll(a => a.Id == "zcode");
                        agents.Add(new AiAgentCapability(zcode.Id, zcode.Name, zcode.Path, zcode.Version, zcode.Status, "native"));
                    }
                    return LocalAgentCommandResult.Ok(agents);
                }
                case LocalAgentCommand.CancelAccountAuthorization:
                    await connection.CancelPendingAuthorizationAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.SaveTerminalCommands:
                    return LocalAgentCommandResult.Reject("PORTABLE_COMMAND_EDITING_UNAVAILABLE");
                case LocalAgentCommand.StopSession:
                {
                    var value = Deserialize<SessionPayload>(payload);
                    await platform.TerminalHost.StopAsync(value.SessionId, cancellationToken).ConfigureAwait(false);
                    revision++;
                    return LocalAgentCommandResult.Ok();
                }
                case LocalAgentCommand.SetRemoteAccess:
                    await connection.SetRemoteAccessAsync(Deserialize<SetRemoteAccessPayload>(payload).Enabled, cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.RetryConnection:
                    await connection.RetryAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.ConfigureServer:
                    await connection.ConfigureServerAsync(Deserialize<ConfigureServerPayload>(payload).ServerUrl, cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.BeginAccountAuthorization:
                    await connection.AuthorizeAsync(Deserialize<AccountAuthorizationPayload>(payload).PcName, cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.SignOut:
                    await connection.SignOutAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case LocalAgentCommand.GrantDevice:
                case LocalAgentCommand.RevokeDevice:
                    return LocalAgentCommandResult.Reject("PORTABLE_DEVICE_MANAGEMENT_UNAVAILABLE");
                case LocalAgentCommand.Login:
                case LocalAgentCommand.Register:
                    return LocalAgentCommandResult.Reject("ACCOUNT_BROWSER_AUTHORIZATION_REQUIRED");
                default:
                    return LocalAgentCommandResult.Reject("LOCAL_AGENT_COMMAND_UNSUPPORTED");
            }
            Interlocked.Increment(ref revision);
            return LocalAgentCommandResult.Ok();
        }
        catch (RelayOperationException error) { return LocalAgentCommandResult.Reject(error.Code); }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Net.Sockets.SocketException or
            JsonException or CryptographicException or InvalidOperationException or KeyNotFoundException)
        {
            // Never return credential-bearing HTTP bodies or callback URLs.
            return LocalAgentCommandResult.Reject("ACCOUNT_CONNECTION_FAILED");
        }
    }

    private AgentStatusSnapshot CreateStatus()
    {
        var status = connection.Snapshot;
        return new(
            Interlocked.Increment(ref revision),
            DateTimeOffset.UtcNow,
            status.Identity,
            status.State,
            status.AccountLinked,
            status.Username,
            status.Nickname,
            status.ServerOrigin,
            status.RemoteAccessEnabled,
            status.ErrorCode,
            status.ErrorCode is null ? null : "The portable PC connection needs attention.",
            connection.SupportsAi ? new HashSet<string> { "ai_agent_v1" } : new HashSet<string>(),
            commands,
            Array.Empty<ManagedSessionSnapshot>(),
            status.Devices);
    }

    public ValueTask DisposeAsync()
    {
        lock (shutdownSync)
        {
            // Keep new work fenced after a failed stop, but allow the user to
            // retry cleanup instead of permanently replaying the first failure.
            if (stopping is null || stopping.IsFaulted || stopping.IsCanceled) stopping = StopAsync();
            return new ValueTask(stopping);
        }
    }

    private async Task StopAsync()
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (platform.TerminalHost is IAsyncDisposable terminalHost)
                await terminalHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static T Deserialize<T>(JsonElement? payload) where T : class =>
        payload?.Deserialize<T>(LocalAgentProtocol.JsonOptions) ??
        throw new InvalidDataException("LOCAL_AGENT_PAYLOAD_INVALID");
}

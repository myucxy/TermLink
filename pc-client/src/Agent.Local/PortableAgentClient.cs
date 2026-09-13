using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Agent.Local;

public sealed record PortableAgentClientOptions(
    string Endpoint,
    TimeSpan ConnectTimeout,
    string SecretName = "local-ipc-authentication-v1");

public sealed class PortableAgentClient : IAgentClient, IAsyncDisposable
{
    private readonly PortableAgentClientOptions options;
    private readonly ISecretStore secretStore;
    private readonly ILocalIpcTransport transport;
    private readonly IProcessSupervisor supervisor;
    private readonly SemaphoreSlim keyGate = new(1, 1);
    private byte[]? key;
    private int disposed;

    public PortableAgentClient(
        PortableAgentClientOptions options,
        ISecretStore secretStore,
        ILocalIpcTransport transport,
        IProcessSupervisor supervisor)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        if (!Path.IsPathRooted(options.Endpoint) || options.ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Portable Agent connection options are invalid.", nameof(options));
    }

    public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken) =>
        SendAsync<AgentStatusSnapshot>(LocalAgentCommand.GetStatus, new { force_refresh = forceRefresh }, cancellationToken);

    public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.SetRemoteAccess, new SetRemoteAccessPayload(enabled), cancellationToken);

    public ValueTask ConfigureServerAsync(string serverUrl, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException(
            "Server URL configuration is currently available through the Windows Agent."));

    public ValueTask RetryConnectionAsync(CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.RetryConnection, null, cancellationToken);

    public ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.StopSession, new SessionPayload(sessionId), cancellationToken);

    public ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.GrantDevice, new DevicePayload(deviceId, permission), cancellationToken);

    public ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.RevokeDevice, new DevicePayload(deviceId), cancellationToken);

    public ValueTask BeginAccountAuthorizationAsync(string pcName, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException(
            "Browser account authorization is currently available through the Windows Agent."));

    public ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.Login, new LoginPayload(username, password, pcName), cancellationToken);

    public ValueTask RegisterAsync(
        string username,
        string nickname,
        string email,
        string password,
        string passwordConfirmation,
        string pcName,
        CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.Register,
            new RegisterPayload(username, nickname, email, password, passwordConfirmation, pcName), cancellationToken);

    public ValueTask SignOutAsync(CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.SignOut, null, cancellationToken);

    public ValueTask SaveTerminalCommandsAsync(
        IReadOnlyList<TerminalCommandCapability> commands,
        CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.SaveTerminalCommands, new TerminalCommandsPayload(commands), cancellationToken);

    private async ValueTask SendEmptyAsync(
        LocalAgentCommand command,
        object? payload,
        CancellationToken cancellationToken) =>
        _ = await SendAsync<JsonElement?>(command, payload, cancellationToken).ConfigureAwait(false);

    private async ValueTask<T> SendAsync<T>(
        LocalAgentCommand command,
        object? payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var authenticationKey = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        await supervisor.EnsureAgentRunningAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = await transport.ConnectAsync(
            options.Endpoint, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        var request = LocalAgentProtocol.CreateRequest(command, payload, authenticationKey, DateTimeOffset.UtcNow);
        await LocalAgentProtocol.WriteAsync(stream, request, cancellationToken).ConfigureAwait(false);
        var response = await LocalAgentProtocol.ReadAsync<LocalAgentResponse>(stream, cancellationToken).ConfigureAwait(false);
        LocalAgentProtocol.AuthenticateResponse(response, request, authenticationKey, DateTimeOffset.UtcNow);
        if (!response.Success) throw new InvalidOperationException(response.Code);
        if (response.Payload is null)
        {
            if (default(T) is null) return default!;
            throw new InvalidDataException("LOCAL_AGENT_RESPONSE_PAYLOAD_MISSING");
        }
        try
        {
            return response.Payload.Value.Deserialize<T>(LocalAgentProtocol.JsonOptions) ??
                throw new InvalidDataException("LOCAL_AGENT_RESPONSE_PAYLOAD_INVALID");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("LOCAL_AGENT_RESPONSE_PAYLOAD_INVALID", error);
        }
    }

    private async ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        if (key is not null) return key;
        await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key is not null) return key;
            if (!secretStore.IsAvailable)
                throw new CryptographicException("A secure local Agent credential store is required.");
            var stored = await secretStore.RetrieveAsync(options.SecretName, cancellationToken).ConfigureAwait(false);
            if (stored is not null && stored.Length != LocalAgentProtocol.AuthenticationKeyBytes)
            {
                CryptographicOperations.ZeroMemory(stored);
                throw new CryptographicException("The stored local Agent key is invalid.");
            }
            if (stored is null)
            {
                stored = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes);
                await secretStore.StoreAsync(options.SecretName, stored, cancellationToken).ConfigureAwait(false);
            }
            key = stored;
            return stored;
        }
        finally { keyGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
            key = null;
        }
        keyGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

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
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim shutdownGate = new(1, 1);
    private CancellationTokenSource requests = new();
    private byte[]? key;
    private int disposed;
    private int fullShutdownRequested;
    private bool shutdownComplete;

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

    public bool SupportsCompleteShutdown => supervisor is IAgentExitMonitor;
    public bool SupportsAiAgentDetection => true;
    public bool SupportsTerminalCommandEditing => false;
    public bool SupportsRemoteSessions => false;
    public bool SupportsBrowserAuthorizationCancellation => true;

    public async ValueTask ResumeAfterUpdateFailureAsync()
    {
        await shutdownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref fullShutdownRequested) == 0) return;
                shutdownComplete = false;
                requests = new CancellationTokenSource();
                Volatile.Write(ref fullShutdownRequested, 0);
            }
            finally { lifecycle.Release(); }
        }
        finally { shutdownGate.Release(); }
    }

    public ValueTask<IReadOnlyList<AiAgentCapability>> DetectAiAgentsAsync(CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<AiAgentCapability>>(LocalAgentCommand.DetectAiAgents, null, cancellationToken);

    public ValueTask ControlBrowserAuthorizationAsync(bool cancel, CancellationToken cancellationToken) => cancel
        ? SendEmptyAsync(LocalAgentCommand.CancelAccountAuthorization, null, cancellationToken)
        : ValueTask.FromException(new PlatformNotSupportedException());

    public async ValueTask ShutdownCompletelyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (supervisor is not IAgentExitMonitor monitor) throw new PlatformNotSupportedException("Agent exit monitoring is unavailable.");
        await shutdownGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (shutdownComplete) return;
            // Fence startup before waiting on an in-flight launch. Existing
            // reads/authorization calls are cancelled so polling cannot respawn
            // the Agent while its authenticated shutdown is in progress.
            Volatile.Write(ref fullShutdownRequested, 1);
            requests.Cancel();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            AgentShutdownReceipt receipt;
            try
            {
                receipt = await SendCoreAsync<AgentShutdownReceipt>(LocalAgentCommand.Shutdown, null, false, deadline.Token).ConfigureAwait(false);
            }
            catch (System.Net.Sockets.SocketException error) when (error.SocketErrorCode is
                System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.AddressNotAvailable or
                System.Net.Sockets.SocketError.NotConnected)
            {
                if (await supervisor.IsAgentRunningAsync(deadline.Token).ConfigureAwait(false)) throw;
                shutdownComplete = true;
                return; // No owned Agent is running; never launch one just to exit.
            }
            if (receipt.ProcessId <= 0) throw new InvalidDataException("LOCAL_AGENT_SHUTDOWN_RECEIPT_INVALID");
            await monitor.WaitForExitAsync(receipt.ProcessId, TimeSpan.FromSeconds(15), deadline.Token).ConfigureAwait(false);
            shutdownComplete = true;
        }
        catch
        {
            await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                requests = new CancellationTokenSource();
                Volatile.Write(ref fullShutdownRequested, 0);
            }
            finally { lifecycle.Release(); }
            throw;
        }
        finally { shutdownGate.Release(); }
    }

    public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.SetRemoteAccess, new SetRemoteAccessPayload(enabled), cancellationToken);

    public ValueTask ConfigureServerAsync(string serverUrl, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.ConfigureServer, new ConfigureServerPayload(serverUrl), cancellationToken);

    public ValueTask RetryConnectionAsync(CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.RetryConnection, null, cancellationToken);

    public ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.StopSession, new SessionPayload(sessionId), cancellationToken);

    public ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.GrantDevice, new DevicePayload(deviceId, permission), cancellationToken);

    public ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.RevokeDevice, new DevicePayload(deviceId), cancellationToken);

    public ValueTask BeginAccountAuthorizationAsync(string pcName, CancellationToken cancellationToken) =>
        SendEmptyAsync(LocalAgentCommand.BeginAccountAuthorization, new AccountAuthorizationPayload(pcName), cancellationToken);

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
        ValueTask.FromException(new AgentClientOperationException("PORTABLE_COMMAND_EDITING_UNAVAILABLE"));

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
        if (Volatile.Read(ref fullShutdownRequested) != 0) throw new AgentClientOperationException("PC_AGENT_EXITING");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requests.Token);
        try { return await SendCoreAsync<T>(command, payload, true, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Volatile.Read(ref fullShutdownRequested) != 0)
        {
            throw new AgentClientOperationException("PC_AGENT_EXITING");
        }
    }

    private async ValueTask<T> SendCoreAsync<T>(
        LocalAgentCommand command, object? payload, bool startAgent, CancellationToken cancellationToken)
    {
        byte[] authenticationKey;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (startAgent && Volatile.Read(ref fullShutdownRequested) != 0) throw new AgentClientOperationException("PC_AGENT_EXITING");
            authenticationKey = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
            if (startAgent) await supervisor.EnsureAgentRunningAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
        await using var stream = await transport.ConnectAsync(
            options.Endpoint, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        var request = LocalAgentProtocol.CreateRequest(command, payload, authenticationKey, DateTimeOffset.UtcNow);
        await LocalAgentProtocol.WriteAsync(stream, request, cancellationToken).ConfigureAwait(false);
        var response = await LocalAgentProtocol.ReadAsync<LocalAgentResponse>(stream, cancellationToken).ConfigureAwait(false);
        LocalAgentProtocol.AuthenticateResponse(response, request, authenticationKey, DateTimeOffset.UtcNow);
        if (!response.Success) throw new AgentClientOperationException(response.Code);
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
        requests.Cancel();
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
            key = null;
        }
        keyGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

using System.Security.Cryptography;
using TermLink.Platform;

namespace TermLink.Agent.Local;

public sealed record LocalAgentCommandResult(bool Success, string Code, object? Payload = null)
{
    public static LocalAgentCommandResult Ok(object? payload = null) => new(true, "OK", payload);
    public static LocalAgentCommandResult Reject(string code) => new(false, code);
}

public interface ILocalAgentCommandHandler
{
    ValueTask<LocalAgentCommandResult> HandleAsync(
        LocalAgentCommand command,
        System.Text.Json.JsonElement? payload,
        CancellationToken cancellationToken);
}

public sealed class LocalAgentServer : IAsyncDisposable
{
    private readonly ILocalIpcTransport transport;
    private readonly string endpoint;
    private readonly byte[] key;
    private readonly ILocalAgentCommandHandler handler;
    private readonly BoundedLocalAgentNonceStore nonces = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> clients = [];
    private readonly object clientsSync = new();
    private ILocalIpcListener? listener;
    private int disposed;

    public LocalAgentServer(
        ILocalIpcTransport transport,
        string endpoint,
        ReadOnlySpan<byte> key,
        ILocalAgentCommandHandler handler)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.endpoint = endpoint;
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (key.Length != LocalAgentProtocol.AuthenticationKeyBytes)
            throw new CryptographicException("The local Agent key is invalid.");
        this.key = key.ToArray();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        listener = await transport.ListenAsync(endpoint, linked.Token).ConfigureAwait(false);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                Stream stream;
                try { stream = await listener.AcceptAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                var client = HandleClientAsync(stream, linked.Token);
                lock (clientsSync) clients.Add(client);
                _ = client.ContinueWith(
                    completed => { lock (clientsSync) clients.Remove(completed); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            listener = null;
            Task[] active;
            lock (clientsSync) active = clients.ToArray();
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            LocalAgentEnvelope? request = null;
            try
            {
                request = await LocalAgentProtocol.ReadAsync<LocalAgentEnvelope>(stream, cancellationToken).ConfigureAwait(false);
                try { LocalAgentProtocol.AuthenticateRequest(request, key, DateTimeOffset.UtcNow, nonces); }
                catch (Exception error) when (error is InvalidDataException or CryptographicException or ArgumentException)
                {
                    // Do not turn an authentication failure into a keyed
                    // response oracle. The caller receives an authenticated
                    // error only after it has proven possession of the key.
                    return;
                }
                LocalAgentCommandResult result;
                try
                {
                    result = await handler.HandleAsync(request.Command, request.Payload, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    var code = error.Message.StartsWith("LOCAL_AGENT_", StringComparison.Ordinal)
                        ? error.Message
                        : "LOCAL_AGENT_REQUEST_REJECTED";
                    result = LocalAgentCommandResult.Reject(code);
                }
                var response = LocalAgentProtocol.CreateResponse(
                    request, result.Success, result.Code, result.Payload, key, DateTimeOffset.UtcNow);
                await LocalAgentProtocol.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or CryptographicException or ArgumentException)
            {
                // Malformed framing/serialization is unauthenticated and must
                // be closed without a signed response.
                _ = error;
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(key);
    }
}

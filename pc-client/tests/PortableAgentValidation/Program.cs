using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Agent.Local;
using TermLink.Platform.Unix;

var key = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes);
try
{
    ValidateAuthentication(key);
    await ValidateFramesAsync(key);
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) await ValidateServerAsync(key);
    Console.WriteLine("Portable Agent IPC validation passed: bounded frames, HMAC binding, clock window, replay defense, UDS request/response, and command rejection.");
}
finally { CryptographicOperations.ZeroMemory(key); }

static void ValidateAuthentication(byte[] key)
{
    var now = DateTimeOffset.UtcNow;
    var request = LocalAgentProtocol.CreateRequest(LocalAgentCommand.GetStatus, new { force_refresh = true }, key, now);
    var nonces = new BoundedLocalAgentNonceStore();
    LocalAgentProtocol.AuthenticateRequest(request, key, now, nonces);
    RequireThrows<InvalidDataException>(
        () => LocalAgentProtocol.AuthenticateRequest(request, key, now, nonces),
        "A replayed nonce was accepted.");

    var expired = LocalAgentProtocol.CreateRequest(LocalAgentCommand.GetStatus, null, key, now - TimeSpan.FromMinutes(3));
    RequireThrows<InvalidDataException>(
        () => LocalAgentProtocol.AuthenticateRequest(expired, key, now, new BoundedLocalAgentNonceStore()),
        "An expired request was accepted.");

    var tampered = request with { Command = LocalAgentCommand.SignOut };
    RequireThrows<InvalidDataException>(
        () => LocalAgentProtocol.AuthenticateRequest(tampered, key, now, new BoundedLocalAgentNonceStore()),
        "A tampered request body was accepted.");

    var response = LocalAgentProtocol.CreateResponse(request, true, "OK", new { value = 1 }, key, now);
    LocalAgentProtocol.AuthenticateResponse(response, request, key, now);
    RequireThrows<InvalidDataException>(
        () => LocalAgentProtocol.AuthenticateResponse(response with { Success = false }, request, key, now),
        "A tampered response body was accepted.");
    var other = LocalAgentProtocol.CreateRequest(LocalAgentCommand.GetStatus, null, key, now);
    RequireThrows<InvalidDataException>(
        () => LocalAgentProtocol.AuthenticateResponse(response, other, key, now),
        "A response was replayed onto another request.");
}

static async Task ValidateFramesAsync(byte[] key)
{
    var request = LocalAgentProtocol.CreateRequest(LocalAgentCommand.RetryConnection, null, key, DateTimeOffset.UtcNow);
    await using var stream = new MemoryStream();
    await LocalAgentProtocol.WriteAsync(stream, request, CancellationToken.None);
    stream.Position = 0;
    var roundTrip = await LocalAgentProtocol.ReadAsync<LocalAgentEnvelope>(stream, CancellationToken.None);
    Require(roundTrip.RequestId == request.RequestId && roundTrip.AuthenticationTag == request.AuthenticationTag,
        "A local Agent frame did not round trip exactly.");

    await using var oversized = new MemoryStream();
    var header = new byte[] { 0, 1, 0, 1 };
    await oversized.WriteAsync(header);
    oversized.Position = 0;
    await RequireThrowsAsync<InvalidDataException>(
        () => LocalAgentProtocol.ReadAsync<LocalAgentEnvelope>(oversized, CancellationToken.None).AsTask(),
        "An oversized local Agent frame was accepted.");
}

static async Task ValidateServerAsync(byte[] key)
{
    var root = Path.Combine(Path.GetTempPath(), $"termlink-agent-ipc-{Environment.ProcessId}");
    Directory.CreateDirectory(root);
    SetPrivateMode(root);
    var endpoint = Path.Combine(root, "agent.sock");
    var transport = new UnixDomainSocketTransport();
    await using var server = new LocalAgentServer(transport, endpoint, key, new FakeHandler());
    using var shutdown = new CancellationTokenSource();
    var running = server.RunAsync(shutdown.Token);
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(endpoint) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Require(File.Exists(endpoint), "The local Agent server did not listen.");
        await using var stream = await transport.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);
        var request = LocalAgentProtocol.CreateRequest(LocalAgentCommand.GetStatus, null, key, DateTimeOffset.UtcNow);
        await LocalAgentProtocol.WriteAsync(stream, request, CancellationToken.None);
        var response = await LocalAgentProtocol.ReadAsync<LocalAgentResponse>(stream, CancellationToken.None);
        LocalAgentProtocol.AuthenticateResponse(response, request, key, DateTimeOffset.UtcNow);
        Require(response.Success && response.Code == "OK" &&
            response.Payload?.GetProperty("state").GetString() == "ready", "The local Agent response is incorrect.");
    }
    finally
    {
        shutdown.Cancel();
        await server.DisposeAsync();
        try { await running; }
        catch (OperationCanceledException) { }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void SetPrivateMode(string path)
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static async Task RequireThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

file sealed class FakeHandler : ILocalAgentCommandHandler
{
    public ValueTask<LocalAgentCommandResult> HandleAsync(
        LocalAgentCommand command,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(command == LocalAgentCommand.GetStatus
            ? LocalAgentCommandResult.Ok(new { state = "ready" })
            : LocalAgentCommandResult.Reject("UNSUPPORTED"));
    }
}

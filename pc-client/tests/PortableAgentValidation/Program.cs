using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.MacOS;
using TermLink.Platform.Unix;
using TermLink.Presentation;

// Isolated real child process for the shutdown integration check. The random
// authentication key is passed only through this test process's environment.
if (args.Length == 3 && args[0] == "--agent" && args[1] == "--ipc")
{
    var childKey = Convert.FromBase64String(Environment.GetEnvironmentVariable("TERMLINK_TEST_IPC_KEY")!);
    try
    {
        await using var terminal = new PosixTerminalSessionHost();
        var launched = await terminal.LaunchAsync(new TerminalLaunchRequest("sh", Path.GetTempPath(), 80, 24,
            new Dictionary<string, string>()), CancellationToken.None);
        await using var childServer = new LocalAgentServer(new UnixDomainSocketTransport(), args[2], childKey,
            new ShutdownHandler(terminal));
        var running = childServer.RunAsync(CancellationToken.None);
        Console.WriteLine(launched.ProcessId);
        await running;
    }
    finally { CryptographicOperations.ZeroMemory(childKey); }
    return;
}

var key = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes);
try
{
    ValidateAuthentication(key);
    await ValidateFramesAsync(key);
    await PortableStatusValidation.RunAsync(key);
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        await ValidateServerAsync(key);
        await ValidateShutdownAsync(key);
        if (OperatingSystem.IsMacOS()) await ValidateRealProcessShutdownAsync(key);
    }
    Console.WriteLine("Portable Agent IPC/shutdown validation passed: authenticated shutdown, rejection, startup fence, UI-only exit, process identity, PTY cleanup and actual child exit.");
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

static async Task ValidateShutdownAsync(byte[] key)
{
    var root = Path.Combine(Path.GetTempPath(), $"tl-shutdown-{Environment.ProcessId}");
    Directory.CreateDirectory(root);
    SetPrivateMode(root);
    var endpoint = Path.Combine(root, "agent.sock");
    var transport = new UnixDomainSocketTransport();
    var handler = new ShutdownHandler();
    var supervisor = new ShutdownSupervisor();
    var secrets = new TestSecrets(key);
    await using var server = new LocalAgentServer(transport, endpoint, key, handler);
    var running = server.RunAsync(CancellationToken.None);
    try
    {
        // A forged shutdown must neither stop the Host nor receive signed proof.
        await using (var stream = await transport.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            var forged = LocalAgentProtocol.CreateRequest(LocalAgentCommand.Shutdown, null, RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow);
            await LocalAgentProtocol.WriteAsync(stream, forged, CancellationToken.None);
            var byteBuffer = new byte[1];
            Require(await stream.ReadAsync(byteBuffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "Forged shutdown received a response.");
        }
        Require(!running.IsCompleted && handler.ShutdownCount == 0, "Forged shutdown stopped the server.");

        await using (var uiOnly = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)), secrets, transport, supervisor))
        {
            // Disposing the control client alone must leave the Agent listening.
        }
        Require(!running.IsCompleted, "UI-only exit stopped the Agent.");
        await using var client = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)), secrets, transport, supervisor);
        Require(client.SupportsAiAgentDetection && client.SupportsBrowserAuthorizationCancellation && !client.SupportsTerminalCommandEditing,
            "Portable client feature support is inaccurate.");
        Require((await client.DetectAiAgentsAsync(CancellationToken.None)).Single().Id == "codex", "AI detection did not cross authenticated IPC.");
        await client.ControlBrowserAuthorizationAsync(true, CancellationToken.None);
        Require(handler.CancelCount == 1, "Browser cancellation did not cross authenticated IPC.");
        handler.Reject = true;
        await RequireThrowsAsync<AgentClientOperationException>(() => client.ShutdownCompletelyAsync(CancellationToken.None).AsTask(), "Rejected shutdown was reported as success.");
        Require(!running.IsCompleted, "Rejected shutdown closed the server.");
        handler.Reject = false;
        supervisor.BlockStartup = true;
        var refresh = client.GetStatusAsync(false, CancellationToken.None).AsTask();
        await supervisor.Starting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = client.ShutdownCompletelyAsync(CancellationToken.None).AsTask();
        await RequireThrowsAsync<AgentClientOperationException>(() => refresh, "Shutdown did not cancel an in-flight startup/status call.");
        await supervisor.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var starts = supervisor.StartCount;
        await RequireThrowsAsync<AgentClientOperationException>(() => client.GetStatusAsync(false, CancellationToken.None).AsTask(), "Status polling was allowed to restart the Agent.");
        Require(supervisor.StartCount == starts && !stop.IsCompleted, "UI exit did not wait for actual process exit.");
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        supervisor.Exited.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        await client.ShutdownCompletelyAsync(CancellationToken.None);
        Require(handler.ShutdownCount == 1 && supervisor.StartCount == starts, "Shutdown spawned an Agent or sent duplicate stop commands.");
        await using var absent = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)), secrets, transport, supervisor);
        await absent.ShutdownCompletelyAsync(CancellationToken.None);
        Require(supervisor.StartCount == starts, "Full exit started an already-stopped Agent.");
        // A cancelled/failed update can reopen IPC and remove the startup fence,
        // without replacing login material or requiring a new control client.
        supervisor.BlockStartup = false;
        await using var resumedServer = new LocalAgentServer(transport, endpoint, key, new ShutdownHandler());
        using var resumedLifetime = new CancellationTokenSource();
        var resumedRunning = resumedServer.RunAsync(resumedLifetime.Token);
        try
        {
            await client.ResumeAfterUpdateFailureAsync();
            await client.ResumeAfterUpdateFailureAsync();
            Require((await client.DetectAiAgentsAsync(CancellationToken.None)).Single().Id == "codex" &&
                supervisor.StartCount == starts + 1, "Update failure recovery did not restore authenticated IPC and Agent startup.");
        }
        finally
        {
            resumedLifetime.Cancel();
            await resumedRunning.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Console.WriteLine("PASS IPC shutdown authentication, failure, polling fence, wait and UI-only isolation");
    }
    finally
    {
        await server.DisposeAsync();
        try { await running; } catch (OperationCanceledException) { }
        Directory.Delete(root, true);
    }
}

static async Task ValidateRealProcessShutdownAsync(byte[] key)
{
    var root = Path.Combine(Path.GetTempPath(), $"tl-process-{Environment.ProcessId}");
    Directory.CreateDirectory(root);
    SetPrivateMode(root);
    var endpoint = Path.Combine(root, "agent.sock");
    var pidPath = Path.Combine(root, "agent.pid");
    var executable = Environment.ProcessPath!;
    var info = new ProcessStartInfo(executable)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("--agent"); info.ArgumentList.Add("--ipc"); info.ArgumentList.Add(endpoint);
    info.Environment["TERMLINK_TEST_IPC_KEY"] = Convert.ToBase64String(key);
    using var process = Process.Start(info)!;
    var stderr = process.StandardError.ReadToEndAsync();
    try
    {
        var shellPid = int.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
        await File.WriteAllTextAsync(pidPath, process.Id.ToString());
        var supervisor = new MacProcessSupervisor(executable, endpoint, pidPath);
        var mismatch = new MacProcessSupervisor("/bin/false", endpoint, pidPath);
        await RequireThrowsAsync<InvalidDataException>(() => mismatch.WaitForExitAsync(process.Id, TimeSpan.FromSeconds(1), CancellationToken.None).AsTask(), "A foreign executable was accepted.");
        await RequireThrowsAsync<TimeoutException>(() => supervisor.WaitForExitAsync(process.Id, TimeSpan.FromMilliseconds(50), CancellationToken.None).AsTask(), "An alive Agent was reported as stopped.");
        Require(!process.HasExited, "Monitoring killed the test process.");
        await using var client = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)), new TestSecrets(key), new UnixDomainSocketTransport(), supervisor);
        await client.ShutdownCompletelyAsync(CancellationToken.None);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(process.ExitCode == 0 && !File.Exists(pidPath), "Full exit left a process or stale PID file.");
        var shellAlive = false;
        try { using var shell = Process.GetProcessById(shellPid); shellAlive = !shell.HasExited; }
        catch (ArgumentException) { }
        Require(!shellAlive, "Full exit left a managed PTY child running.");
        Require(!File.Exists(endpoint), "Agent socket remained after full exit.");
        Console.WriteLine("PASS actual Agent process exits 0, PTY child stops, socket/PID removed, foreign PID protected");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        var error = await stderr;
        if (error.Length > 0) Console.Error.WriteLine(error);
        Directory.Delete(root, true);
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

file sealed class ShutdownHandler(IAsyncDisposable? terminal = null) : ILocalAgentCommandHandler
{
    public int CancelCount { get; private set; }
    public bool Reject { get; set; }
    public int ShutdownCount { get; private set; }
    public async ValueTask<LocalAgentCommandResult> HandleAsync(LocalAgentCommand command, JsonElement? payload, CancellationToken token)
    {
        if (command == LocalAgentCommand.DetectAiAgents)
            return LocalAgentCommandResult.Ok(new[] { new AiAgentCapability("codex", "Codex", "/test/codex", "1", "detected", "local_cli") });
        if (command == LocalAgentCommand.CancelAccountAuthorization) { CancelCount++; return LocalAgentCommandResult.Ok(); }
        if (command != LocalAgentCommand.Shutdown || Reject) return LocalAgentCommandResult.Reject("SHUTDOWN_REJECTED");
        if (terminal is not null) await terminal.DisposeAsync();
        ShutdownCount++;
        return LocalAgentCommandResult.Ok(new AgentShutdownReceipt(Environment.ProcessId));
    }
}

file sealed class TestSecrets(byte[] key) : ISecretStore
{
    public bool IsAvailable => true;
    public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token) => ValueTask.FromResult<byte[]?>(key.ToArray());
    public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken token) => throw new InvalidOperationException("Shutdown must not change stored login.");
    public ValueTask DeleteAsync(string name, CancellationToken token) => throw new InvalidOperationException("Shutdown must not delete stored login.");
}

file sealed class ShutdownSupervisor : IProcessSupervisor, IAgentExitMonitor
{
    public int StartCount { get; private set; }
    public bool BlockStartup { get; set; }
    public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask EnsureAgentRunningAsync(CancellationToken token)
    {
        StartCount++;
        Starting.TrySetResult();
        if (BlockStartup) await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
    public ValueTask<bool> IsAgentRunningAsync(CancellationToken token) => ValueTask.FromResult(false);
    public async ValueTask WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken token)
    { Waiting.TrySetResult(); await Exited.Task.WaitAsync(timeout, token); }
}

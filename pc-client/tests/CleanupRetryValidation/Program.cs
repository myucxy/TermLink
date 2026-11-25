// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Agent.Connection;
using TermLink.Agent.Host;
using TermLink.Agent.Local;
using TermLink.Agent.Terminal;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;
using TermLink.Protocol.Realtime.V1;

if (args.Length != 1) throw new ArgumentException("Pass the verified packaged native library directory.");
PortableTerminalHost.LoadNative(args[0]);
var root = Path.Combine(Path.GetTempPath(), "tl-cleanup-" + Guid.NewGuid().ToString("N")[..12]);
Directory.CreateDirectory(root);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
try
{
    await ConnectionFailuresAsync();
    await RetirementFailuresAsync();
    await WorkerFailuresAsync();
    await PendingWorkerAsync();
    await TerminalFailuresAsync(new IOException("Owned stop failure"), "io");
    await TerminalFailuresAsync(new OperationCanceledException("Owned stop cancellation"), "cancel");
    await TerminalNormalAndConcurrentAsync();
    await FailedReaderValidation.RunAsync(root);
    await RuntimeReceiptAsync();
    Console.WriteLine("Cleanup retry validation passed: connection owners, every resource, worker drain, cancellation, actual native core, signed receipts and normal/repeated exit.");
}
finally { Directory.Delete(root, recursive: true); }

async Task<RelayConnectionService> CreateConnectionAsync(NoNetwork network)
{
    // No account is restored; the handler rejects any accidental HTTP request.
    return await RelayConnectionService.CreateAsync(new NoSecrets(),
        new PcAgentIdentity(Guid.NewGuid(), "owned-fixture", PcPlatform.Linux, PcArchitecture.Arm64, "fixture", "0.3.17"),
        CancellationToken.None, http: new HttpClient(network));
}

async Task ConnectionFailuresAsync()
{
    var network = new NoNetwork(); var service = await CreateConnectionAsync(network);
    var terminal = new FaultTerminal([new IOException("Owned terminal failure")]);
    var file = new FaultFile([new OperationCanceledException("Owned file cancellation")]);
    var ai = new FaultAi([new InvalidOperationException("Owned AI failure")]);
    Set(service, "terminalHost", terminal); Set(service, "fileHost", file); Set(service, "aiHost", ai);
    var first = await FailureAsync<IOException>(() => service.DisposeAsync().AsTask());
    Require(first.InnerException is AggregateException { InnerExceptions.Count: 3 }, "Every independent owner failure must be reported.");
    Require(terminal.Attempts == 1 && file.Attempts == 1 && ai.Attempts == 1 && terminal.Open && file.Open && ai.Open,
        "A failed owner was lost or prevented another resource from attempting cleanup.");
    Require(ReferenceEquals(Get(service, "terminalHost"), terminal) && ReferenceEquals(Get(service, "fileHost"), file) && ReferenceEquals(Get(service, "aiHost"), ai),
        "A failed owner must remain reachable for retry.");
    await FailureAsync<ObjectDisposedException>(() => service.ConfigureServerAsync("https://never-requested.invalid", CancellationToken.None));
    await FailureAsync<ObjectDisposedException>(() => service.RetryAsync(CancellationToken.None));
    await service.DisposeAsync(); await service.DisposeAsync();
    Require(terminal.Attempts == 2 && file.Attempts == 2 && ai.Attempts == 2 && !terminal.Open && !file.Open && !ai.Open &&
        Get(service, "terminalHost") is null && Get(service, "fileHost") is null && Get(service, "aiHost") is null && network.Calls == 0,
        "Retry success skipped an owner or repeated completed cleanup.");
    Console.WriteLine("PASS connection cleanup: all three owners attempted, failures retained, new work fenced, cancelled owner retried and repeated success idempotent");
}

async Task RetirementFailuresAsync()
{
    var network = new NoNetwork(); var service = await CreateConnectionAsync(network);
    foreach (var (field, method, owner) in new (string, string, FaultOwner)[] {
        ("terminalHost", "RetireTerminalHostAsync", new FaultTerminal([new IOException("Owned terminal failure")])),
        ("fileHost", "RetireFileHostAsync", new FaultFile([new IOException("Owned file failure")])),
        ("aiHost", "RetireAiHost", new FaultAi([new IOException("Owned AI failure")])) })
    {
        Set(service, field, owner);
        await FailureAsync<IOException>(() => InvokeRetireAsync(service, method));
        Require(ReferenceEquals(Get(service, field), owner) && owner.Open, "Failed retirement lost its owner.");
        await InvokeRetireAsync(service, method); await InvokeRetireAsync(service, method);
        Require(Get(service, field) is null && !owner.Open && owner.Attempts == 2, "Retirement retry did not release exactly the failed owner.");
    }
    await service.DisposeAsync(); Require(network.Calls == 0, "Retirement made a network request.");
    Console.WriteLine("PASS individual host retirement: terminal/file/AI owner is cleared only after disposal succeeds");
}

async Task WorkerFailuresAsync()
{
    foreach (var error in new Exception[] { new IOException("Owned worker failure"), new OperationCanceledException("Owned worker cancellation") })
    {
        var network = new NoNetwork(); var service = await CreateConnectionAsync(network);
        var terminal = new FaultTerminal([]); var file = new FaultFile([]);
        Set(service, "terminalHost", terminal); Set(service, "fileHost", file);
        Set(service, "workerCancellation", new CancellationTokenSource()); Set(service, "worker", Task.FromException(error));
        await FailureAsync<IOException>(() => service.DisposeAsync().AsTask());
        Require(Get(service, "worker") is null && !terminal.Open && !file.Open && terminal.Attempts == 1 && file.Attempts == 1,
            "A completed faulted worker prevented later owners from being cleaned.");
        await service.DisposeAsync(); await service.DisposeAsync();
        Require(terminal.Attempts == 1 && file.Attempts == 1 && network.Calls == 0, "Worker failure remained permanently latched or repeated completed owners.");
    }
    Console.WriteLine("PASS worker cleanup: completed faults/cancellation are reported once, worker references released, later owners cleaned, retry completes");
}

async Task PendingWorkerAsync()
{
    var network = new NoNetwork(); var service = await CreateConnectionAsync(network);
    var terminal = new FaultTerminal([]); var file = new FaultFile([]);
    Set(service, "terminalHost", terminal); Set(service, "fileHost", file);
    var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellation = new CancellationTokenSource(); Set(service, "workerCancellation", cancellation); Set(service, "worker", worker.Task);
    var cleanup = service.DisposeAsync().AsTask();
    Require(cancellation.IsCancellationRequested && !cleanup.IsCompleted && terminal.Attempts == 0 && file.Attempts == 0,
        "Dispose retired resources before the actual worker completed.");
    using var caller = new CancellationTokenSource(); caller.Cancel();
    await FailureAsync<OperationCanceledException>(() => cleanup.WaitAsync(caller.Token));
    Require(!cleanup.IsCompleted && Get(service, "worker") is not null, "A cancelled caller falsely completed background cleanup.");
    worker.SetResult(); await cleanup.WaitAsync(TimeSpan.FromSeconds(3)); await service.DisposeAsync();
    Require(!terminal.Open && !file.Open && terminal.Attempts == 1 && file.Attempts == 1 && network.Calls == 0, "Drained worker did not finish owned cleanup.");
    Console.WriteLine("PASS pending worker: cancellation fences new work but owned cleanup waits for real completion before retiring resources");
}

async Task TerminalFailuresAsync(Exception failure, string name)
{
    var pty = new FaultPty(); var key = RandomNumberGenerator.GetBytes(32);
    var host = new PortableTerminalHost(pty, Commands(), Path.Combine(root, "native-" + name), key);
    var first = await host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None);
    var second = await host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None);
    pty.Failures[first] = failure;
    var sessions = Sessions(host); var firstCore = Get(sessions[first], "Core"); var secondCore = Get(sessions[second], "Core");
    await FailureAsync<IOException>(() => host.DisposeAsync().AsTask());
    Require(host.Sessions.Count == 1 && host.Sessions[0].Id == first && pty.Open[first] && !pty.Open[second] &&
        pty.Stops[first] == 1 && pty.Stops[second] == 1, "One session stop failure prevented another session cleanup or dropped the failing session.");
    Require((bool)Get(secondCore!, "disposed")! && !(bool)Get(firstCore!, "disposed")!, "Native core was leaked for a stopped session or freed for a still-open PTY.");
    Require(((CancellationTokenSource)Get(host, "lifetime")!).IsCancellationRequested && ((Task)Get(host, "clock")!).IsCompleted &&
        !AllZero((byte[])Get(host, "stateKey")!), "Clock was not drained or state key was destroyed before retry could persist a failed session.");
    await FailureAsync<ObjectDisposedException>(() => host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None));
    await host.DisposeAsync(); await host.DisposeAsync();
    Require(host.Sessions.Count == 0 && !pty.Open[first] && pty.Stops[first] == 2 && pty.Stops[second] == 1 &&
        (bool)Get(firstCore!, "disposed")! && AllZero((byte[])Get(host, "stateKey")!), "Retry did not close the failed PTY/core/key or repeated another session.");
    CryptographicOperations.ZeroMemory(key);
    Console.WriteLine("PASS native terminal " + name + ": reader/clock cancelled, successful session freed, failed session/key retained, retry closes it and repeat is idempotent");
}

async Task TerminalNormalAndConcurrentAsync()
{
    var pty = new FaultPty(); var host = new PortableTerminalHost(pty, Commands());
    var id = await host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None);
    pty.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = host.DisposeAsync().AsTask(); await pty.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second = host.DisposeAsync().AsTask();
    Require(!first.IsCompleted && !second.IsCompleted && pty.Stops[id] == 1 && pty.Open[id], "Concurrent disposal returned before PTY cleanup.");
    pty.StopGate.SetResult(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
    Require(!pty.Open[id] && pty.Stops[id] == 1 && host.Sessions.Count == 0, "Normal concurrent disposal repeated a successful PTY stop.");
    Console.WriteLine("PASS normal terminal exit: concurrent callers wait for the same PTY cleanup and completed resources are released once");
}

async Task RuntimeReceiptAsync()
{
    var network = new NoNetwork(); var connection = await CreateConnectionAsync(network);
    var terminal = new FaultTerminal([new IOException("Owned connection terminal failure")]); var file = new FaultFile([]);
    Set(connection, "terminalHost", terminal); Set(connection, "fileHost", file);
    var platform = new NoPlatform();
    var runtime = (PortableAgentRuntime)typeof(PortableAgentRuntime).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
        .Invoke([platform, connection, Array.Empty<TerminalCommandCapability>()]);
    var key = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes); var endpoint = Path.Combine(root, "ipc.sock");
    await using var server = new LocalAgentServer(new UnixDomainSocketTransport(), endpoint, key, runtime);
    using var cancellation = new CancellationTokenSource(); var serving = server.RunAsync(cancellation.Token);
    try
    {
        var first = await RequestAsync(LocalAgentCommand.Shutdown);
        Require(!first.Success && first.Code == "PC_AGENT_CLEANUP_INCOMPLETE" && first.Payload is null && terminal.Open && terminal.Attempts == 1 && file.Attempts == 1 && !serving.IsCompleted,
            "First failing owned cleanup published a successful shutdown receipt or skipped later resources.");
        var status = await RequestAsync(LocalAgentCommand.GetStatus);
        Require(!status.Success && status.Code == "PC_AGENT_EXITING", "Failed shutdown reopened the Host work fence.");
        terminal.BlockNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = RequestAsync(LocalAgentCommand.Shutdown); await terminal.AttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(!retry.IsCompleted, "Shutdown receipt was sent before the retry completed owned cleanup.");
        terminal.BlockNext.SetResult(); var completed = await retry.WaitAsync(TimeSpan.FromSeconds(3));
        Require(completed.Success && completed.Payload?.GetProperty("process_id").GetInt32() == Environment.ProcessId && !terminal.Open && terminal.Attempts == 2 && file.Attempts == 1,
            "Shutdown retry did not wait for the connection-owned resource and return its authenticated receipt.");
        await serving.WaitAsync(TimeSpan.FromSeconds(3)); await runtime.DisposeAsync();
        Require(terminal.Attempts == 2 && network.Calls == 0, "Final runtime disposal repeated a completed connection owner or used network.");
        Console.WriteLine("PASS signed IPC/runtime exit: failing connection owner prevents receipt, status stays fenced, retry waits for cleanup then returns authenticated success");
    }
    finally
    {
        cancellation.Cancel(); await server.DisposeAsync();
        try { await serving; } catch (OperationCanceledException) { }
        CryptographicOperations.ZeroMemory(key);
    }
    async Task<LocalAgentResponse> RequestAsync(LocalAgentCommand command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = await new UnixDomainSocketTransport().ConnectAsync(endpoint, TimeSpan.FromSeconds(2), deadline.Token);
        var request = LocalAgentProtocol.CreateRequest(command, null, key, DateTimeOffset.UtcNow);
        await LocalAgentProtocol.WriteAsync(stream, request, deadline.Token);
        var response = await LocalAgentProtocol.ReadAsync<LocalAgentResponse>(stream, deadline.Token);
        LocalAgentProtocol.AuthenticateResponse(response, request, key, DateTimeOffset.UtcNow); return response;
    }
}

static IReadOnlyList<TerminalCommandCapability> Commands() => [new("sh", "Owned fake PTY", "/bin/sh", [], true, false)];
static Dictionary<Guid, object> Sessions(PortableTerminalHost host) => ((System.Collections.IEnumerable)Get(host, "sessions")!).Cast<object>()
    .ToDictionary(item => (Guid)item.GetType().GetProperty("Key")!.GetValue(item)!, item => item.GetType().GetProperty("Value")!.GetValue(item)!);
static object? Get(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(value);
static void Set(object value, string name, object? data) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, data);
static bool AllZero(byte[] bytes) => bytes.All(value => value == 0);
static async Task InvokeRetireAsync(object owner, string method)
{
    try { if (owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null) is Task task) await task; }
    catch (TargetInvocationException error) when (error.InnerException is IOException io) { throw io; }
}
static async Task<T> FailureAsync<T>(Func<Task> operation) where T : Exception
{ try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

abstract class FaultOwner(Exception[] failures)
{
    public int Attempts; public bool Open = true; private readonly Queue<Exception> failures = new(failures);
    protected void Attempt() { Attempts++; if (failures.TryDequeue(out var failure)) throw failure; Open = false; }
}
sealed class FaultAi(Exception[] failures) : FaultOwner(failures), IRelayAiHost
{
    public void Dispose() => Attempt();
    public Task<string> ExecuteAsync(Guid c, string r, CancellationToken t, Func<bool> a) => throw new NotSupportedException();
}
sealed class FaultFile(Exception[] failures) : FaultOwner(failures), IRelayFileHost
{
    public ValueTask DisposeAsync() { Attempt(); return ValueTask.CompletedTask; }
    public Task<JsonElement> ListAsync(string p, long o, string q, CancellationToken t) => throw new NotSupportedException();
    public Task<RelayFileArtifact> PrepareAsync(Guid id, string p, CancellationToken t) => throw new NotSupportedException();
    public Task CleanupAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
}
sealed class FaultTerminal(Exception[] failures) : FaultOwner(failures), IRelayTerminalHost
{
    public TaskCompletionSource? BlockNext;
    public TaskCompletionSource AttemptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<TerminalCommandCapability> Commands => [];
    public IReadOnlyList<RelayTerminalSession> Sessions => [];
    public async ValueTask DisposeAsync() { if (BlockNext is { } gate) { AttemptStarted.TrySetResult(); await gate.Task; } Attempt(); }
    public Task<Guid> StartAsync(string c, string d, Guid o, CancellationToken t) => throw new NotSupportedException();
    public Task StopAsync(Guid i, CancellationToken t) => throw new NotSupportedException();
    public Task RecordInputResultAsync(Guid i, Guid r, InputResultPayload p, CancellationToken t) => throw new NotSupportedException();
    public Task<RelayTerminalEvent?> RecoverAsync(Guid i, Guid r, ulong a, CancellationToken t) => throw new NotSupportedException();
    public Task<RelayTerminalCheckpoint> CaptureAsync(Guid i, CancellationToken t) => throw new NotSupportedException();
    public Task<IReadOnlyList<RelayTerminalEvent>?> ReadAsync(Guid i, ulong a, CancellationToken t) => throw new NotSupportedException();
    public Task<IReadOnlyList<RelayTerminalEvent>?> ReadHistoryAsync(Guid i, ulong a, CancellationToken t) => throw new NotSupportedException();
    public Task<string?> InputAsync(Guid i, Guid s, InputEventPayload p, Func<Task<bool>> a, CancellationToken t) => throw new NotSupportedException();
    public Task<string?> ResizeAsync(Guid i, ResizePayload p, Func<Task<bool>> a, CancellationToken t) => throw new NotSupportedException();
}
sealed class FaultPty : ITerminalSessionHost
{
    public Dictionary<Guid, bool> Open = []; public Dictionary<Guid, int> Stops = [];
    public Dictionary<Guid, Exception> Failures = []; private readonly Dictionary<Guid, bool> readersFinished = [];
    public TaskCompletionSource? StopGate; public TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken token)
    { var id = Guid.NewGuid(); Open[id] = true; readersFinished[id] = false; Stops[id] = 0; return ValueTask.FromResult(new TerminalLaunchResult(id, 0, request.CommandId)); }
    public async ValueTask<int> ReadOutputAsync(Guid id, Memory<byte> buffer, CancellationToken token)
    { try { await Task.Delay(Timeout.Infinite, token); return 0; } finally { await Task.Delay(10); readersFinished[id] = true; } }
    public async ValueTask StopAsync(Guid id, CancellationToken token)
    {
        if (!readersFinished[id]) throw new InvalidOperationException("Reader must be drained before PTY stop.");
        Stops[id]++; if (StopGate is { } gate) { Stopping.TrySetResult(); await gate.Task; }
        if (Failures.Remove(id, out var error)) throw error; Open[id] = false;
    }
    public ValueTask ResizeAsync(Guid i, int c, int r, CancellationToken t) => throw new NotSupportedException();
    public ValueTask SendInputAsync(Guid i, ReadOnlyMemory<byte> b, CancellationToken t) => throw new NotSupportedException();
    public ValueTask SignalAsync(Guid i, TerminalSignal s, CancellationToken t) => throw new NotSupportedException();
    public ValueTask<int> WaitForExitAsync(Guid i, CancellationToken t) => throw new NotSupportedException();
}
sealed class NoNetwork : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Calls++; throw new InvalidOperationException("No network allowed."); }
}
sealed class NoSecrets : ISecretStore
{
    public bool IsAvailable => true;
    public ValueTask<byte[]?> RetrieveAsync(string n, CancellationToken t) => ValueTask.FromResult<byte[]?>(null);
    public ValueTask StoreAsync(string n, ReadOnlyMemory<byte> b, CancellationToken t) => throw new NotSupportedException();
    public ValueTask DeleteAsync(string n, CancellationToken t) => throw new NotSupportedException();
}
sealed class NoPlatform : IPlatformServices
{
    public PlatformDescriptor Descriptor => new(PcPlatform.Linux, PcArchitecture.Arm64, "owned-fixture", "linux-arm64", false);
    public ISecretStore SecretStore => new NoSecrets();
    public ILocalIpcTransport LocalIpc => new UnixDomainSocketTransport();
    public ITerminalSessionHost TerminalHost => new PlatformTerminal();
    public IFileSystemOperations FileSystem => throw new NotSupportedException();
    public ITerminalCommandDiscovery TerminalCommands => throw new NotSupportedException();
    public IAutostartManager Autostart => throw new NotSupportedException();
    public IUpdateInstaller Updates => throw new NotSupportedException();
    public IProcessSupervisor Processes => throw new NotSupportedException();
    public IPlatformNotification Notifications => throw new NotSupportedException();
    private sealed class PlatformTerminal : ITerminalSessionHost, IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest r, CancellationToken t) => throw new NotSupportedException();
        public ValueTask ResizeAsync(Guid i, int c, int r, CancellationToken t) => throw new NotSupportedException();
        public ValueTask SendInputAsync(Guid i, ReadOnlyMemory<byte> b, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<int> ReadOutputAsync(Guid i, Memory<byte> b, CancellationToken t) => throw new NotSupportedException();
        public ValueTask SignalAsync(Guid i, TerminalSignal s, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<int> WaitForExitAsync(Guid i, CancellationToken t) => throw new NotSupportedException();
        public ValueTask StopAsync(Guid i, CancellationToken t) => throw new NotSupportedException();
    }
}

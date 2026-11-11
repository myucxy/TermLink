using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Threading;
using TermLink.Agent.Local;
using TermLink.Desktop;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Desktop.HeadlessTests;

internal static class StartupAuthorizationValidation
{
    public static async Task RunAsync()
    {
        await StartupStaysResponsiveAsync();
        await DeniedAuthorizationCanRetryAsync();
        await DisposeDrainsNativeReadAsync(startPolling: true);
        await DisposeDrainsNativeReadAsync(startPolling: true, deny: true);
        await DisposeDrainsNativeReadAsync(startPolling: false);
        Console.WriteLine("PASS pending synchronous credential read leaves startup/window/dispatcher responsive; status refresh is single-flight; denial stays unknown and explicit retry recovers; cancelled callers and disposal never pretend native authorization finished");
    }

    private static async Task StartupStaysResponsiveAsync()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var snapshot = Snapshot();
        using var secrets = new BlockingSecrets();
        await using var client = Client(secrets, snapshot);
        var state = new AgentStateStore(client, TimeSpan.FromMinutes(5));
        await using var model = new MainWindowViewModel(state, client, new AvaloniaUiDispatcher(),
            new PlatformDescriptor(PcPlatform.MacOS, PcArchitecture.Arm64, "fixture", "osx-arm64", false));
        var wrongThread = false;
        model.PropertyChanged += (_, _) => wrongThread |= !Dispatcher.UIThread.CheckAccess();
        var window = new MainWindow { DataContext = model };
        try
        {
            // Same ordering as App.OnFrameworkInitializationCompleted: Start
            // happens before the framework shows its assigned main window.
            var started = Stopwatch.StartNew();
            model.Start();
            Require(started.Elapsed < TimeSpan.FromSeconds(1), "Startup synchronously waited for secure-store authorization.");
            Require(secrets.Entered.Wait(TimeSpan.FromSeconds(2)), "Credential fixture did not enter.");
            Require(secrets.ThreadId != uiThread, "Credential read still ran on the UI thread.");
            window.Show();
            var pumped = false;
            Dispatcher.UIThread.Post(() => pumped = true);
            Dispatcher.UIThread.RunJobs();
            Require(pumped && window.IsVisible && model.IsAccountStateLoading && !model.IsAccountStateKnown,
                "Pending authorization blocked window dispatch or guessed account state.");
            Require(window.FindControl<TextBlock>("MacAuthorizationStartupGuidance") is { IsVisible: true, Text.Length: > 20 },
                "The visible startup window has no Mac authorization guidance.");
            Require(state.Current is null, "Pending request published a status.");
            model.Navigate("settings");
            Require(model.CurrentPage.Key == "settings", "Pending authorization blocked navigation.");
            var shared = Enumerable.Range(0, 8).Select(_ => state.RefreshAsync(true)).ToArray();
            using var cancelled = new CancellationTokenSource();
            var waiter = state.RefreshAsync(true, cancelled.Token);
            cancelled.Cancel();
            try { await waiter; throw new Exception("Cancelled caller kept waiting."); }
            catch (OperationCanceledException) { }
            Require(shared.All(t => !t.IsCompleted) && secrets.Reads == 1,
                "Refresh/cancel launched a second authorization or completed the owned native read.");
            secrets.Release.Set();
            await Task.WhenAll(shared).WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => model.IsAccountStateKnown);
            Require(!wrongThread && model.IsConnected && secrets.Reads == 1,
                "Refreshed account state was not published through the UI dispatcher.");
            Require(!window.FindControl<Border>("SidebarAccountLoading")!.IsVisible,
                "Authorization guidance remained after authenticated status arrived.");
        }
        finally { secrets.Release.Set(); window.Close(); }
    }

    private static async Task DisposeDrainsNativeReadAsync(bool startPolling, bool deny = false)
    {
        using var secrets = new BlockingSecrets { Deny = deny };
        await using var client = Client(secrets, Snapshot());
        var state = new AgentStateStore(client, TimeSpan.FromMinutes(5));
        var changed = 0; state.Changed += (_, _) => changed++;
        Task? refresh = null;
        if (startPolling) state.Start(); else refresh = state.RefreshAsync();
        Require(secrets.Entered.Wait(TimeSpan.FromSeconds(2)), "Dispose fixture did not enter native read.");
        var disposal = state.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(80);
            Require(!disposal.IsCompleted && secrets.Reads == 1,
                "Disposal cancelled its waiter and abandoned the pending native authorization.");
            Require(state.Current is null, "State lock is held while the native API waits.");
        }
        finally { secrets.Release.Set(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        if (refresh is not null) { try { await refresh; } catch (OperationCanceledException) { } }
        Require(changed == 0 && state.Current is null, "A completed native read published after disposal.");
    }

    private static async Task DeniedAuthorizationCanRetryAsync()
    {
        using var secrets = new BlockingSecrets { Deny = true };
        await using var client = Client(secrets, Snapshot());
        var state = new AgentStateStore(client, TimeSpan.FromMinutes(5));
        await using var model = new MainWindowViewModel(state, client, new AvaloniaUiDispatcher(),
            new PlatformDescriptor(PcPlatform.MacOS, PcArchitecture.Arm64, "fixture", "osx-arm64", false));
        var wrongThread = false;
        model.PropertyChanged += (_, _) => wrongThread |= !Dispatcher.UIThread.CheckAccess();
        model.Start();
        Require(secrets.Entered.Wait(TimeSpan.FromSeconds(2)), "Declined fixture never entered.");
        secrets.Release.Set();
        await Until(() => model.AgentUnavailable);
        Require(!model.IsAccountStateKnown && !model.IsAccountLinked && !model.IsAccountSignedOut && secrets.Reads == 1 && !wrongThread,
            "Declined authorization guessed account status, retried concurrently or bypassed the dispatcher.");
        secrets.Deny = false;
        await state.RefreshAsync(true);
        await Until(() => model.IsAccountStateKnown && !model.AgentUnavailable);
        Require(model.IsConnected && secrets.Reads == 2 && !wrongThread, "Explicit retry did not recover after authorization.");
    }

    private static async Task Until(Func<bool> condition)
    {
        var stop = Stopwatch.StartNew();
        while (!condition()) { if (stop.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("UI status did not arrive."); await Task.Delay(10); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static AgentStatusSnapshot Snapshot() => new(1, DateTimeOffset.UtcNow,
        new PcAgentIdentity(Guid.NewGuid(), "startup-fixture", PcPlatform.MacOS, PcArchitecture.Arm64, "fixture", "0.3.16.146"),
        AgentConnectionState.Connected, true, "fixture-user", "Fixture", "https://example.invalid", true, null, null,
        new HashSet<string>(), [], [], []);
    private static PortableAgentClient Client(BlockingSecrets secret, AgentStatusSnapshot snapshot) => new(
        new PortableAgentClientOptions(Path.Combine(Path.GetTempPath(), "unused-startup-fixture.sock"), TimeSpan.FromSeconds(1)),
        secret, new MemoryTransport(secret.Key, snapshot), new NoProcessSupervisor());

    private sealed class BlockingSecrets : ISecretStore, IDisposable
    {
        public readonly byte[] Key = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes);
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public int Reads, ThreadId;
        public bool Deny;
        public bool IsAvailable => true;
        public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reads); ThreadId=Environment.CurrentManagedThreadId;
            Entered.Set();
            // Deliberately ignores cancellation, as a native ACL dialog does.
            // The bound prevents a broken test from hanging the test process.
            if (!Release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Credential fixture was not released.");
            if (Deny) throw new CryptographicException("Fixture native authorization declined.");
            return ValueTask.FromResult<byte[]?>(Key.ToArray());
        }
        public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> value, CancellationToken token) => throw new Exception("No key writes are expected.");
        public ValueTask DeleteAsync(string name, CancellationToken token) => throw new Exception("No key deletion is expected.");
        public void Dispose() { Release.Set(); CryptographicOperations.ZeroMemory(Key); Entered.Dispose(); Release.Dispose(); }
    }
    private sealed class NoProcessSupervisor : IProcessSupervisor
    {
        public ValueTask EnsureAgentRunningAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<bool> IsAgentRunningAsync(CancellationToken token) => ValueTask.FromResult(true);
    }
    private sealed class MemoryTransport(byte[] key, AgentStatusSnapshot snapshot) : ILocalIpcTransport
    {
        public ValueTask<Stream> ConnectAsync(string endpoint, TimeSpan timeout, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new ResponseStream(key, snapshot)); }
        public ValueTask<ILocalIpcListener> ListenAsync(string endpoint, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class ResponseStream(byte[] key, AgentStatusSnapshot snapshot) : Stream
    {
        private readonly MemoryStream request = new(), response = new();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task FlushAsync(CancellationToken token)
        {
            request.Position=0;
            var envelope = await LocalAgentProtocol.ReadAsync<LocalAgentEnvelope>(request, token);
            LocalAgentProtocol.AuthenticateRequest(envelope, key, DateTimeOffset.UtcNow, new BoundedLocalAgentNonceStore());
            Require(envelope.Command == LocalAgentCommand.GetStatus, "Unexpected fixture command.");
            await LocalAgentProtocol.WriteAsync(response, LocalAgentProtocol.CreateResponse(envelope, true, "", snapshot, key, DateTimeOffset.UtcNow), token);
            response.Position=0;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => response.ReadAsync(buffer,token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => request.WriteAsync(buffer,token);
        public override int Read(byte[] buffer,int offset,int count) => response.Read(buffer,offset,count);
        public override void Write(byte[] buffer,int offset,int count) => request.Write(buffer,offset,count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing){request.Dispose();response.Dispose();}base.Dispose(disposing); }
    }
}

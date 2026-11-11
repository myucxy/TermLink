using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Google.Protobuf;
using TermLink.Agent.Connection;
using TermLink.Agent.Files;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Protocol;
using TermLink.Protocol.FileTransfer.V2;
using TermLink.Protocol.Realtime.V1;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Parameters;

if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Actual Mac required");
var root = Path.Combine(Path.GetTempPath(), "termlink-file-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--main-relay-reconnect", StringComparer.Ordinal)) await MainRelayReconnectAsync();
    else
    {
        await NativeFilesAsync(); await EncryptedRelayAsync();
        await MainRelayReconnectAsync(); await MainRelayReconnectAsync(retry: true);
        await FileTokenRefreshAsync(); await FileTokenRefreshAsync(reject: true);
        foreach (var action in new[] { "disable", "sign-out", "account-change" }) await FileAuthorityStopAsync(action);
        await SignOutDuringRefreshAsync();
    }
}
finally { Directory.Delete(root, true); }

async Task NativeFilesAsync()
{
    var source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
    var cache = Path.Combine(root, "native-cache");
    await using var host = new PosixRemoteFileHost(cache, 1024 * 1024);
    var data = RandomNumberGenerator.GetBytes(512 * 1024);
    await File.WriteAllBytesAsync(Path.Combine(source, "中文.bin"), data);
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    await File.WriteAllTextAsync(Path.Combine(source, "nested", "hello.txt"), "hello中文");
    await File.WriteAllBytesAsync(Path.Combine(source, "empty"), []);
    File.CreateSymbolicLink(Path.Combine(source, "linked"), Path.Combine(source, "中文.bin"));
    Directory.CreateSymbolicLink(Path.Combine(source, "cycle"), source);
    File.CreateSymbolicLink(Path.Combine(source, "escape"), "/etc/passwd");
    var page = await host.ListAsync(source, 0, "", CancellationToken.None);
    Require(page.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "中文.bin"), "Unicode listing");
    Require(!page.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "cycle"), "unsupported symlink hidden from mobile V1 listing");
    Require(page.GetProperty("entries").EnumerateArray().All(e => e.GetProperty("kind").GetString() is "file" or "directory"), "mobile V1 kinds");
    var filtered = await host.ListAsync(source, 0, "HELLO", CancellationToken.None);
    Require(filtered.GetProperty("entries").GetArrayLength() == 0, "filter should not recurse");
    filtered = await host.ListAsync(Path.Combine(source, "nested"), 0, "HELLO", CancellationToken.None);
    Require(filtered.GetProperty("entries").GetArrayLength() == 1, "name filter");
    var id = Guid.NewGuid();
    byte[] hash;
    await using (var artifact = await host.PrepareAsync(id, Path.Combine(source, "中文.bin"), CancellationToken.None))
    {
        hash = artifact.Sha256;
        Require(hash.AsSpan().SequenceEqual(SHA256.HashData(data)), "file hash");
        Require(artifact.SizeBytes == data.Length && !artifact.DirectoryZip, "descriptor");
        await File.WriteAllTextAsync(Path.Combine(source, "中文.bin"), "changed");
        using var memory = new MemoryStream(); await artifact.Content.CopyToAsync(memory);
        Require(memory.ToArray().AsSpan().SequenceEqual(data), "snapshot changed with source");
    }
    await using (var reloadedHost = new PosixRemoteFileHost(cache, 1024 * 1024))
    await using (var artifact = await reloadedHost.PrepareAsync(id, Path.Combine(source, "中文.bin"), CancellationToken.None))
        Require(artifact.Sha256.AsSpan().SequenceEqual(hash) && artifact.SizeBytes == data.Length, "restart immutable artifact");
    await Throws<RelayFileException>(() => host.PrepareAsync(id, Path.Combine(source, "empty"), CancellationToken.None), "cross-source operation reuse");
    await using (var artifact = await host.PrepareAsync(Guid.NewGuid(), source, CancellationToken.None))
    {
        Require(artifact.DirectoryZip && artifact.Filename.EndsWith(".zip"), "directory ZIP descriptor");
        using var zip = new ZipArchive(artifact.Content, ZipArchiveMode.Read, leaveOpen: true);
        Require(zip.GetEntry("nested/hello.txt") is not null && zip.GetEntry("empty") is not null, "ZIP entries");
        Require(!zip.Entries.Any(e => e.FullName.Contains("escape") || e.FullName.Contains("cycle") || e.FullName.Contains("linked") || e.FullName.Contains("..")), "ZIP followed link");
    }
    foreach (var name in new[] { "escape", "linked", "cycle/empty" })
        await Throws<RelayFileException>(() => host.PrepareAsync(Guid.NewGuid(), Path.Combine(source, name), CancellationToken.None), "no-follow path");
    await using (var artifact = await host.PrepareAsync(Guid.NewGuid(), Path.Combine(source, "empty"), CancellationToken.None))
        Require(artifact.SizeBytes == 0 && artifact.Sha256.AsSpan().SequenceEqual(SHA256.HashData([])), "empty file");
    var tooBig = Path.Combine(source, "large"); using (var file = File.Create(tooBig)) file.SetLength(1024 * 1024 + 1);
    await Throws<RelayFileException>(() => host.PrepareAsync(Guid.NewGuid(), tooBig, CancellationToken.None), "size limit");
    File.Delete(tooBig);
    using (var cancelled = new CancellationTokenSource())
    { cancelled.Cancel(); await Throws<OperationCanceledException>(() => host.PrepareAsync(Guid.NewGuid(), source, cancelled.Token), "cancel preparation"); }
    var pageDirectory = Path.Combine(root, "pages"); Directory.CreateDirectory(pageDirectory);
    for (var i = 0; i < 505; i++) File.WriteAllText(Path.Combine(pageDirectory, $"file{i:D3}"), "");
    var first = await host.ListAsync(pageDirectory, 0, "", CancellationToken.None);
    var next = await host.ListAsync(pageDirectory, 500, "", CancellationToken.None);
    Require(first.GetProperty("entries").GetArrayLength() == 500 && first.GetProperty("next_offset").GetInt64() == 500 && next.GetProperty("entries").GetArrayLength() == 5 && next.GetProperty("next_offset").ValueKind == JsonValueKind.Null, "pagination");
    var payload = Path.Combine(cache, id.ToString("N"), "payload");
    await File.WriteAllTextAsync(payload, "tamper");
    await Throws<RelayFileException>(() => host.PrepareAsync(id, Path.Combine(source, "中文.bin"), CancellationToken.None), "tamper artifact");
    await host.CleanupAsync(id, CancellationToken.None); Require(!Directory.Exists(Path.GetDirectoryName(payload)), "cleanup");
    var enclosing = Path.Combine(root, "enclosing"); Directory.CreateDirectory(enclosing);
    await File.WriteAllTextAsync(Path.Combine(enclosing, "kept.txt"), "must remain");
    var nestedCache = Path.Combine(enclosing, "inside-cache");
    await using (var nestedHost = new PosixRemoteFileHost(nestedCache, 1024 * 1024))
    {
        await using var artifact = await nestedHost.PrepareAsync(Guid.NewGuid(), enclosing, CancellationToken.None);
        using var zip = new ZipArchive(artifact.Content, ZipArchiveMode.Read, leaveOpen: true);
        Require(zip.GetEntry("kept.txt") is not null && !zip.Entries.Any(e => e.FullName.Contains("inside-cache")), "ZIP recursively included its own live cache");
        await Throws<RelayFileException>(() => nestedHost.PrepareAsync(Guid.NewGuid(), nestedCache, CancellationToken.None), "cache root download");
    }
    Console.WriteLine("PASS actual macOS handle-based listing/filter/pagination, immutable file+restart, ZIP, empty file, symlink/cycle/escape rejection, size limit, cancellation, cache tamper, cleanup and self-containing ZIP cache exclusion");
}

async Task EncryptedRelayAsync()
{
    var source = Path.Combine(root, "relay-source"); Directory.CreateDirectory(source);
    var bytes = RandomNumberGenerator.GetBytes(900000); var path = Path.Combine(source, "数据.bin"); await File.WriteAllBytesAsync(path, bytes);
    await File.WriteAllTextAsync(Path.Combine(source, "second.txt"), "zip sample");
    await using var server = new FileFixture();
    var jobs = new[] { server.Enqueue("LIST", source), server.Enqueue("DOWNLOAD", path), server.Enqueue("DOWNLOAD", source) };
    var cache = Path.Combine(root, "relay-cache");
    await using var service = await RelayConnectionService.CreateAsync(new MemorySecrets(), new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "0.3.16.146"), CancellationToken.None,
        authorization: new FixtureAuthorization(server), fileHostFactory: _ => new PosixRemoteFileHost(cache));
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("File fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Wait(() => jobs.All(j => j.Status == "SUCCEEDED"), server);
    Require(jobs[0].Result.GetProperty("entries").GetArrayLength() == 2, "LIST completion");
    Require(jobs[1].Received.ToArray().AsSpan().SequenceEqual(bytes), "encrypted bytes mismatch");
    Require(jobs[1].Connections >= 2 && jobs[1].ResumeSeen > 0, "resume did not use committed prefix");
    using (var zip = new ZipArchive(new MemoryStream(jobs[2].Received.ToArray()), ZipArchiveMode.Read)) Require(zip.GetEntry("second.txt") is not null && zip.GetEntry("数据.bin") is not null, "E2EE ZIP");
    Require(server.ProofsRejected == 0 && server.FileMetadata && server.Pings > 0, "proof/capability/heartbeat");
    Require(server.ClaimKeys.Take(4).Distinct().Count() == 1 && server.ClaimKeys.ElementAt(4) != server.ClaimKeys.First(), "stuck claim idempotency key did not rotate after bounded retries");
    var failed = server.Enqueue("DOWNLOAD", Path.Combine(source, "does-not-exist.bin")); failed.ExpectFailure = true;
    await Wait(() => failed.Status == "FAILED", server);
    Require(failed.Result.ValueKind == JsonValueKind.Undefined, "failed operation included a result payload");
    var cancelled = server.Enqueue("DOWNLOAD", path); cancelled.CancelOnOffer = true;
    await Wait(() => cancelled.CancelObserved && server.ActiveFileSockets == 0 && !Directory.Exists(Path.Combine(cache, cancelled.Id.ToString("N"))), server);
    Require(cancelled.Status != "SUCCEEDED" && !Directory.Exists(Path.Combine(cache, cancelled.Id.ToString("N"))), "cancel completed or leaked artifact");
    var revoked = server.Enqueue("DOWNLOAD", path); revoked.WaitOnOffer = true;
    await Wait(() => revoked.OfferSeen, server); server.RevokeMobile = true;
    await Wait(() => server.ActiveFileSockets == 0 && !Directory.Exists(Path.Combine(cache, revoked.Id.ToString("N"))), server);
    Require(revoked.Status != "SUCCEEDED", "revoked transfer completed");
    await service.SetRemoteAccessAsync(false, CancellationToken.None);
    Console.WriteLine("PASS signed operation leases + actual E2EE file/ZIP WebSockets, mid-file disconnect and committed-offset resume, completion, cancellation, dynamic revocation, bounded concurrent jobs and independent Relay heartbeat");
}

async Task MainRelayReconnectAsync(bool retry = false)
{
    var path = Path.Combine(root, retry ? "manual-retry.bin" : "main-reconnect.bin");
    var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
    await File.WriteAllBytesAsync(path, bytes);
    var cache = Path.Combine(root, retry ? "manual-retry-cache" : "main-reconnect-cache");
    await using var server = new FileFixture { InProgressClaims = 0 };
    var job = server.Enqueue("DOWNLOAD", path); job.HoldFirstOffer = true;
    await using var service = await RelayConnectionService.CreateAsync(new MemorySecrets(), new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "0.3.16.146"), CancellationToken.None,
        authorization: new FixtureAuthorization(server), fileHostFactory: _ => new PosixRemoteFileHost(cache));
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("Main reconnect fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Wait(() => job.OfferSeen, server);
    var before = server.Connections;
    if (retry) await service.RetryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    else server.DropCurrent = true;
    try
    {
        await Wait(() => server.Connections > before && service.Snapshot.State == AgentConnectionState.Connected, server);
        job.HoldFirstOffer = false;
        await Wait(() => job.Status == "SUCCEEDED", server, 6);
        Require(job.Received.ToArray().AsSpan().SequenceEqual(bytes), "main Relay reconnect changed file bytes");
        Require(job.Claims == 1, "main Relay reconnect reclaimed an unexpired file operation");
        Require(server.ProofsRejected == 0 && !Directory.Exists(Path.Combine(cache, job.Id.ToString("N"))), "main Relay reconnect proof or cleanup");
        Console.WriteLine(retry ? "PASS manual Relay retry preserves active file ownership and completes without reclaim" : "PASS actual control WebSocket interruption keeps the same file lease and completes immediately after main Relay reconnect");
    }
    catch
    {
        Console.WriteLine($"RECONNECT_STATE status={job.Status} claims={job.Claims} file_connections={job.Connections} main_connections={server.Connections} artifact_exists={Directory.Exists(Path.Combine(cache, job.Id.ToString("N")))}");
        throw;
    }
    finally { job.HoldFirstOffer = false; }
}

async Task FileTokenRefreshAsync(bool reject = false)
{
    var name = reject ? "rejected-refresh" : "file-token-refresh";
    var path = Path.Combine(root, name + ".bin"); var cache = Path.Combine(root, name + "-cache");
    var bytes = RandomNumberGenerator.GetBytes(64 * 1024); await File.WriteAllBytesAsync(path, bytes);
    await using var server = new FileFixture { InProgressClaims = 0, DenyRefresh = reject };
    var job = server.Enqueue("DOWNLOAD", path); job.HoldFirstOffer = true; job.DropFirstOffer = !reject;
    await using var service = await RelayConnectionService.CreateAsync(new MemorySecrets(), new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "0.3.16.146"), CancellationToken.None,
        authorization: new FixtureAuthorization(server) { ExpiresIn = 34 }, fileHostFactory: _ => new PosixRemoteFileHost(cache));
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("Token refresh fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Wait(() => job.OfferSeen, server);
    try
    {
        await Wait(() => server.Refreshes > 0, server, 10);
        if (reject)
        {
            await Wait(() => service.Snapshot.State == AgentConnectionState.ReauthenticationRequired, server, 8);
            Require(service.Snapshot.ErrorCode == "ACCOUNT_REAUTHENTICATION_REQUIRED", "rejected refresh lost reauthentication state");
            Require(!Directory.Exists(Path.Combine(cache, job.Id.ToString("N"))), "rejected refresh retained download artifact");
            job.HoldFirstOffer = false;
            await Wait(() => server.ActiveFileSockets == 0, server, 5);
            Require(job.Status != "SUCCEEDED", "rejected refresh completed a download");
            Console.WriteLine("PASS failed token refresh stops the file worker, removes prepared data and requires account authorization");
        }
        else
        {
            await Wait(() => server.Connections >= 2 && service.Snapshot.State == AgentConnectionState.Connected, server, 8);
            job.HoldFirstOffer = false;
            await Wait(() => job.Status == "SUCCEEDED", server, 8);
            Require(server.Refreshes == 1 && server.FreshFileAuthentications > 0, "file socket did not use the one refreshed bearer");
            Require(job.Claims == 1 && job.Connections >= 2 && job.Received.ToArray().AsSpan().SequenceEqual(bytes), "refresh lost lease or file content");
            Require(server.ProofsRejected == 0, "refresh sent invalid signed proof");
            Console.WriteLine("PASS token rotation preserves the active file lease and a forced file WebSocket reconnect uses the newly refreshed token");
        }
    }
    finally { job.HoldFirstOffer = false; }
}

async Task FileAuthorityStopAsync(string action)
{
    var path = Path.Combine(root, action + ".bin"); var cache = Path.Combine(root, action + "-cache");
    await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(64 * 1024));
    await using var server = new FileFixture { InProgressClaims = 0 };
    var job = server.Enqueue("DOWNLOAD", path); job.HoldFirstOffer = true;
    var authorization = new FixtureAuthorization(server);
    await using var service = await RelayConnectionService.CreateAsync(new MemorySecrets(), new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "0.3.16.146"), CancellationToken.None,
        authorization: authorization, fileHostFactory: _ => new PosixRemoteFileHost(cache));
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("Authority stop fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Wait(() => job.OfferSeen, server);
    try
    {
        switch (action)
        {
            case "disable": await service.SetRemoteAccessAsync(false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); break;
            case "sign-out": await service.SignOutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); break;
            case "account-change":
                authorization.Subject = "B";
                await service.AuthorizeAsync("Account B", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Require(service.Snapshot.Username == "user-B", "account change did not activate account B"); break;
        }
        Require(!service.Snapshot.RemoteAccessEnabled && !Directory.Exists(Path.Combine(cache, job.Id.ToString("N"))), action + " retained download authority or cache");
        job.HoldFirstOffer = false;
        await Wait(() => server.ActiveFileSockets == 0, server, 5);
        var claims = server.ClaimKeys.Count;
        await Task.Delay(900);
        Require(server.ClaimKeys.Count == claims && job.Status != "SUCCEEDED", action + " worker kept running");
        Console.WriteLine("PASS " + action + " stops active file sockets and claim polling, removes prepared bytes and cannot complete the old operation");
    }
    finally { job.HoldFirstOffer = false; }
}

async Task SignOutDuringRefreshAsync()
{
    var path = Path.Combine(root, "pending-refresh.bin"); var cache = Path.Combine(root, "pending-refresh-cache");
    await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(64 * 1024));
    await using var server = new FileFixture { InProgressClaims = 0, HoldRefresh = true };
    var job = server.Enqueue("DOWNLOAD", path); job.HoldFirstOffer = true;
    await using var service = await RelayConnectionService.CreateAsync(new MemorySecrets(), new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "0.3.16.146"), CancellationToken.None,
        authorization: new FixtureAuthorization(server) { ExpiresIn = 34 }, fileHostFactory: _ => new PosixRemoteFileHost(cache));
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("Pending refresh fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Wait(() => job.OfferSeen && server.Refreshes > 0, server, 10);
    try
    {
        await service.SignOutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Require(!service.Snapshot.AccountLinked && !Directory.Exists(Path.Combine(cache, job.Id.ToString("N"))), "pending refresh survived sign-out");
        Console.WriteLine("PASS sign-out cancels an in-flight refresh and file worker without waiting on the UI operations gate");
    }
    finally { server.HoldRefresh = false; job.HoldFirstOffer = false; }
}

static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected failure: " + name); }
static async Task Wait(Func<bool> condition, FileFixture server, int seconds = 25)
{
    var end = DateTimeOffset.UtcNow.AddSeconds(seconds);
    while (!condition()) { if (server.Failure is { } e) throw e; if (DateTimeOffset.UtcNow > end) throw new TimeoutException("file fixture timeout"); await Task.Delay(50); }
}

file sealed class MemorySecrets : ISecretStore
{
    public ConcurrentDictionary<string, byte[]> Values { get; } = new();
    public bool IsAvailable => true;
    public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Values[name] = secret.ToArray(); return ValueTask.CompletedTask; }
    public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Values.TryGetValue(name, out var b) ? b.ToArray() : null); }
    public ValueTask DeleteAsync(string name, CancellationToken token)
    { Values.TryRemove(name, out _); return ValueTask.CompletedTask; }
}

file sealed class FixtureAuthorization(FileFixture server) : IRelayAuthorization
{
    public string Subject { get; set; } = "A";
    public int ExpiresIn { get; set; } = 3600;
    public Task<RelayTokens> AuthorizeAsync(RelayOidcConfiguration config, HttpClient http, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new RelayTokens(server.TokenFor(Subject), "refresh-" + Subject, ExpiresIn));
    }
}

file sealed class FileFixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<Guid, Device> devices = new();
    private readonly ConcurrentDictionary<string, bool> nonces = new();
    private readonly ConcurrentDictionary<string, string> tokens = new();
    private readonly ConcurrentDictionary<string, string> issuedTokens = new();
    private int connections, pings, upserts, invalidProofs, conflicts, refreshes;
    private Guid metadataPCId;
    public int ProofsRejected => InvalidProofs;
    public bool FileMetadata;
    public int ActiveFileSockets;
    public ConcurrentQueue<string> ClaimKeys = new();
    public Exception? Failure;
    private readonly ConcurrentQueue<Job> pending = new();
    private readonly ConcurrentDictionary<Guid, Job> operations = new();
    public Job Enqueue(string kind, string path) { var job = new Job(kind, path); operations[job.Id] = job; pending.Enqueue(job); return job; }
    public sealed class Job(string kind, string path) {
        public readonly Guid Id = Guid.NewGuid(); public readonly string Kind = kind, Path = path;
        public string Status = "PENDING"; public JsonElement Result;
        public MemoryStream Received = new(); public string Filename = ""; public long Size; public byte[] Hash = [];
        public bool ExpectFailure;
        public bool Zip, CancelOnOffer, WaitOnOffer, OfferSeen, CancelObserved; public volatile bool HoldFirstOffer; public bool DropFirstOffer; public int Connections, Claims; public long ResumeSeen;
    }
    public bool EnableAi { get; init; }
    public bool RevokeMobile { get; set; }
    public bool AiMetadata { get; private set; }
    public Guid MobileId { get; } = Guid.NewGuid();
    private readonly DeviceIdentity mobile = DeviceIdentity.Generate();
    public TaskCompletionSource<string> AiReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Origin { get; }
    public string Issuer => Origin + "/realms/desktop";
    public int Connections => Volatile.Read(ref connections);
    public int Pings => Volatile.Read(ref pings);
    public int Upserts => Volatile.Read(ref upserts);
    public int InvalidProofs => Volatile.Read(ref invalidProofs);
    public int Conflicts => Volatile.Read(ref conflicts);
    public int Refreshes => Volatile.Read(ref refreshes);
    public bool DropNext { get; set; }
    public volatile bool DropCurrent;
    public int InProgressClaims { get; init; } = 4;
    public bool DenyRefresh { get; init; }
    public volatile bool HoldRefresh;
    public int FreshFileAuthentications;
    public bool WrongSelfKey { get; set; }
    public bool DenySelf { get; set; }
    public bool WrongDiscoveryEndpoint { get; set; }
    public bool InvalidWelcome { get; set; }
    public bool EmptyProductCapabilities { get; private set; }
    public Guid LastDeviceId { get; private set; }
    public string RefreshSubject { get; set; } = "A";
    private sealed record Device(string Signing, string Exchange, string Name, string Subject);

    public FileFixture()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Origin = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(Origin + "/");
        listener.Start();
        loop = Task.Run(async () =>
        {
            try { while (!shutdown.IsCancellationRequested) clients.Add(Handle(await listener.GetContextAsync().WaitAsync(shutdown.Token))); }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        });
    }

    public string TokenFor(string subject) => tokens.GetOrAdd(subject, MintToken);
    private string MintToken(string subject)
    {
        var token = "eyJhbGciOiJub25lIn0." + RelaySecurity.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        { iss = Issuer, azp = "desktop", sub = subject, preferred_username = "user-" + subject, name = "User " + subject,
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), jti = Guid.NewGuid().ToString("N") })) + ".fixture-signature";
        issuedTokens[token] = subject;
        return token;
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/v1/client-config") { await Json(context, new { oidc_issuer = Issuer, windows_oidc_client_id = "desktop", windows_redirect_uri = "http://127.0.0.1/oauth2/callback" }); return; }
            if (path == "/realms/desktop/.well-known/openid-configuration")
            { await Json(context, new { issuer = Issuer, authorization_endpoint = Origin + "/authorize", token_endpoint = WrongDiscoveryEndpoint ? "https://foreign.invalid/token" : Origin + "/token" }); return; }
            if (path == "/token")
            {
                Interlocked.Increment(ref refreshes);
                while (HoldRefresh) await Task.Delay(20, shutdown.Token);
                if (DenyRefresh) { await Json(context, new { error = "invalid_grant" }, 400); return; }
                var refreshed = MintToken(RefreshSubject); tokens[RefreshSubject] = refreshed;
                await Json(context, new { access_token = refreshed, refresh_token = "rotated-refresh-" + RefreshSubject, expires_in = 3600 }); return;
            }
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, shutdown.Token);
            var bytes = body.ToArray();
            var bearer = context.Request.Headers["Authorization"] ?? "";
            var subject = issuedTokens.TryGetValue(bearer.Replace("Bearer ", "", StringComparison.Ordinal), out var tokenSubject) ? tokenSubject : "unknown";
            if (path == "/v1/devices/register")
            {
                using var json = JsonDocument.Parse(bytes);
                var value = json.RootElement;
                var id = value.GetProperty("id").GetGuid();
                var signing = value.GetProperty("signing_public_key").GetString()!;
                var exchange = value.GetProperty("exchange_public_key").GetString()!;
                var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", id.ToString("D"), "PC_AGENT", value.GetProperty("name").GetString(),
                    signing, exchange, value.GetProperty("key_id").GetString(), value.GetProperty("timestamp_ms").GetInt64().ToString(), value.GetProperty("nonce").GetString());
                if (value.GetProperty("type").GetString() != "PC_AGENT" || !Verify(signing, canonical, value.GetProperty("signature").GetString()!))
                { Interlocked.Increment(ref invalidProofs); context.Response.StatusCode = 401; context.Response.Close(); return; }
                var record = new Device(signing, exchange, value.GetProperty("name").GetString()!, subject);
                var added = devices.TryAdd(id, record);
                LastDeviceId = id;
                if (!added) Interlocked.Increment(ref conflicts);
                await Json(context, new { }, added ? 201 : 409);
                return;
            }
            if (!Guid.TryParse(context.Request.Headers["X-TermLink-Device"], out var deviceId) || !devices.TryGetValue(deviceId, out var device) ||
                !ValidateProof(context.Request, bytes, device))
            { Interlocked.Increment(ref invalidProofs); context.Response.StatusCode = 401; context.Response.Close(); return; }
            if (path == "/v1/devices/self")
            { await Json(context, new { device = new { id = deviceId, type = "PC_AGENT", trust_state = DenySelf ? "REVOKED" : "TRUSTED", signing_public_key = WrongSelfKey ? "wrong" : Convert.ToBase64String(Decode(device.Signing)), exchange_public_key = Convert.ToBase64String(Decode(device.Exchange)) } }); return; }
            if (path == "/v1/devices")
            { await Json(context, new { items = new[] { new { id = Guid.NewGuid(), name = "iPhone", type = "ANDROID", trust_state = "TRUSTED", online = true } } }); return; }
            if (path.EndsWith("/grants", StringComparison.Ordinal) && path.StartsWith("/v1/pcs/", StringComparison.Ordinal)) {
                var items = RevokeMobile ? Array.Empty<object>() : new object[] { new { device_id = MobileId, device_type = "IOS", permission = "MANAGE",
                    exchange_public_key = Convert.ToBase64String(mobile.Exchange.PublicKey), signing_public_key = Convert.ToBase64String(mobile.SigningPublicKey) } };
                await Json(context, new { key_epoch = 1, items }); return;
            }
            if (path.Contains("/file-operations", StringComparison.Ordinal))
            {
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                {
                    ClaimKeys.Enqueue(context.Request.Headers["Idempotency-Key"] ?? "");
                    if (ClaimKeys.Count <= InProgressClaims) { await Json(context, new { error = new { code = "IDEMPOTENCY_IN_PROGRESS" } }, 409); return; }
                    if (!pending.TryDequeue(out var next)) { context.Response.StatusCode=204;context.Response.Close();return; }
                    next.Status="PROCESSING"; next.Claims++;
                    await Json(context, new { file_operation = new { id=next.Id, operation=next.Kind, path=next.Path, requester_device_id=MobileId, status=next.Status }, lease_token=RelaySecurity.Base64Url(SHA256.HashData(next.Id.ToByteArray())) });return;
                }
                var segments=path.Split('/');var job=operations[Guid.Parse(segments[5])];
                using var json=JsonDocument.Parse(bytes);var request=json.RootElement;
                if (request.GetProperty("lease_token").GetString()!=RelaySecurity.Base64Url(SHA256.HashData(job.Id.ToByteArray()))) throw new Exception("wrong operation lease");
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                {
                    if (job.CancelObserved || RevokeMobile) { await Json(context,new {},403);return; }
                    await Json(context,new { file_operation=new { id=job.Id,lease_expires_at=DateTimeOffset.UtcNow.AddHours(1) } });return;
                }
                if (path.EndsWith("/upload", StringComparison.Ordinal))
                {
                    job.Filename=request.GetProperty("filename").GetString()!;job.Size=request.GetProperty("size_bytes").GetInt64();
                    job.Hash=Decode(request.GetProperty("sha256").GetString()!);job.Zip=request.GetProperty("directory_zip").GetBoolean();
                    await Json(context,new { transfer=new { protocol="REALTIME_V2",transfer_id=job.Id,requester_device_id=MobileId } });return;
                }
                if (path.EndsWith("/complete", StringComparison.Ordinal))
                {
                    var status=request.GetProperty("status").GetString()!;
                    var hasResult=request.TryGetProperty("result",out var result);
                    var hasError=request.TryGetProperty("error",out var error);
                    // Match the real Go API: RawMessage distinguishes an omitted
                    // result from explicit JSON null. DOWNLOAD and FAILED must omit it.
                    if ((status=="SUCCEEDED" && job.Kind=="DOWNLOAD" && hasResult) ||
                        (status=="FAILED" && (hasResult || !hasError || error.ValueKind!=JsonValueKind.String)) ||
                        (status=="SUCCEEDED" && hasError && error.ValueKind!=JsonValueKind.Null))
                        throw new Exception("completion JSON violates the production omitted-result contract");
                    job.Status=status;
                    if(hasResult)job.Result=result.Clone();
                    if(job.Status=="FAILED" && !job.ExpectFailure)Failure=new Exception("operation failed: "+request.GetProperty("error").GetString());
                    await Json(context,new {});return;
                }
            }
            if (path=="/v1/file-transfers")
            {
                if (Refreshes > 0)
                {
                    if (bearer != "Bearer " + TokenFor(device.Subject)) throw new Exception("file reconnect used a stale bearer");
                    Interlocked.Increment(ref FreshFileAuthentications);
                }
                await FileSocket(context,deviceId,device);return;
            }
            if (path.StartsWith("/v1/pcs/", StringComparison.Ordinal))
            {
                metadataPCId = Guid.Parse(path.Split('/')[3]);
                using var metadata = JsonDocument.Parse(bytes);
                var value = metadata.RootElement;
                FileMetadata = value.GetProperty("capabilities").EnumerateArray().Any(v => v.GetString() == "file_operations_v1");
                AiMetadata = value.GetProperty("capabilities").EnumerateArray().Any(v => v.GetString() == "ai_agent_v1");
                EmptyProductCapabilities = value.GetProperty("platform").GetString() == "macos" &&
                    value.GetProperty("architecture").GetString() == "arm64" && value.GetProperty("capabilities").GetArrayLength() == 0 &&
                    value.GetProperty("supported_shells").GetArrayLength() == 0 && !value.GetProperty("remote_agent_update_capable").GetBoolean();
                Interlocked.Increment(ref upserts);
                await Json(context, new { }); return;
            }
            if (path == "/v1/realtime")
            {
                var accepted = await context.AcceptWebSocketAsync("termlink.realtime.v1");
                using var socket = accepted.WebSocket;
                using var hello = await Receive(socket);
                if (hello.RootElement.GetProperty("role").GetString() != "pc") throw new InvalidOperationException("Wrong role.");
                var capabilities = hello.RootElement.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()).ToArray();
                Interlocked.Increment(ref connections);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                { type = "welcome", protocol_version = InvalidWelcome ? 9 : 1, connection_id = Guid.NewGuid().ToString("D"), heartbeat_seconds = 1, capabilities }), WebSocketMessageType.Text, true, shutdown.Token);
                if (DropNext) { DropNext = false; await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "fixture reconnect", shutdown.Token); return; }
                var sentAi = false;
                while (!shutdown.IsCancellationRequested)
                {
                    var data = new byte[1024 * 1024];
                    var received = await socket.ReceiveAsync(data.AsMemory(), shutdown.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType == WebSocketMessageType.Binary && EnableAi) {
                        var reply = Envelope.Parser.ParseFrom(data, 0, received.Count);
                        var pc = new Guid(Convert.ToHexString(reply.SessionId.Span));
                        var pairwise = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(device.Exchange), deviceId, MobileId, 1);
                        var key = SessionCrypto.DeriveSessionKey(pairwise, pc, CryptoDirection.PcToAndroid, reply.KeyId.ToByteArray());
                        var decrypted = SessionCrypto.Decrypt(key, reply.Nonce.ToByteArray(), SessionCrypto.AssociatedData(reply), reply.Ciphertext.ToByteArray());
                        try { AiReply.TrySetResult(AiAgentResponsePayload.Parser.ParseFrom(decrypted).Json); }
                        finally { CryptographicOperations.ZeroMemory(decrypted); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pairwise); }
                        continue;
                    }
                    if (DropCurrent) { DropCurrent = false; await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "active main Relay interruption", shutdown.Token); return; }
                    using var ping = JsonDocument.Parse(data.AsMemory(0, received.Count));
                    Interlocked.Increment(ref pings);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "pong", probe_id = ping.RootElement.GetProperty("probe_id").GetString() }), WebSocketMessageType.Text, true, shutdown.Token);
                    if (EnableAi && !sentAi) {
                        sentAi = true;
                        var stored = devices[deviceId];
                        var pcPath = metadataPCId;
                        var pairwise = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(stored.Exchange), deviceId, MobileId, 1);
                        var keyId = Encoding.UTF8.GetBytes("epoch-00000001");
                        var key = SessionCrypto.DeriveSessionKey(pairwise, pcPath, CryptoDirection.AndroidToPc, keyId);
                        var request = new Envelope { ProtocolVersion = 1, MessageType = MessageType.AiAgentRequest, Direction = Direction.AndroidToPc,
                            SessionId = ByteString.CopyFrom(Convert.FromHexString(pcPath.ToString("N"))), SenderDeviceId = ByteString.CopyFrom(Convert.FromHexString(MobileId.ToString("N"))),
                            RecipientDeviceId = ByteString.CopyFrom(Convert.FromHexString(deviceId.ToString("N"))), Seq = 1, KeyId = ByteString.CopyFrom(keyId),
                            Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)), SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        var payload = new AiAgentRequestPayload { RequestId = ByteString.CopyFrom(Convert.FromHexString(Guid.NewGuid().ToString("N"))), Json = "{\"action\":\"catalog\"}" }.ToByteArray();
                        request.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, request.Nonce.ToByteArray(), SessionCrypto.AssociatedData(request), payload));
                        var corrupt = request.Clone(); var bad = corrupt.Ciphertext.ToByteArray(); bad[^1] ^= 1; corrupt.Ciphertext = ByteString.CopyFrom(bad);
                        await socket.SendAsync(corrupt.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        var wrongRecipient = request.Clone(); wrongRecipient.RecipientDeviceId = ByteString.CopyFrom(new byte[16]);
                        await socket.SendAsync(wrongRecipient.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        await socket.SendAsync(request.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pairwise);
                    }
                }
                return;
            }
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
        { context.Response.Abort(); }
        catch (Exception error) { Failure=error; context.Response.Abort(); }
    }

    private async Task FileSocket(HttpListenerContext context, Guid pc, Device device)
    {
        var accepted=await context.AcceptWebSocketAsync("termlink.file-transfer.v2");using var socket=accepted.WebSocket;
        Interlocked.Increment(ref ActiveFileSockets);
        try
        {
            using var hello=await Receive(socket);var id=hello.RootElement.GetProperty("transfer_id").GetGuid();var job=operations[id];
            if(hello.RootElement.GetProperty("role").GetString()!="pc"||hello.RootElement.GetProperty("protocol_versions")[0].GetInt32()!=2)throw new Exception("file hello");
            job.Connections++;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new {type="welcome",protocol_version=2,heartbeat_seconds=15,capabilities=new[]{"e2ee","resume_offset_v2"}}),WebSocketMessageType.Text,true,shutdown.Token);
            var pair=SessionCrypto.DerivePairwiseKey(mobile.Exchange,Decode(device.Exchange),pc,MobileId,1);
            var keyId=Encoding.ASCII.GetBytes("file-transfer-v2:"+id.ToString("N")+":1");
            var incoming=SessionCrypto.DeriveFileTransferKey(pair,id,CryptoDirection.PcToAndroid,1,keyId);
            var outgoing=SessionCrypto.DeriveFileTransferKey(pair,id,CryptoDirection.AndroidToPc,1,keyId);
            ulong inSeq=0,outSeq=0;
            try
            {
                while(!shutdown.IsCancellationRequested)
                {
                    var buffer=new byte[320*1024];var count=0;ValueWebSocketReceiveResult part;
                    do { part=await socket.ReceiveAsync(buffer.AsMemory(count),shutdown.Token); if(part.MessageType==WebSocketMessageType.Close)return; count+=part.Count; } while(!part.EndOfMessage);
                    var envelope=FileTransferEnvelopeV2.Parser.ParseFrom(buffer,0,count);
                    if(envelope.ProtocolVersion!=2||!envelope.TransferId.Equals(Id(id))||!envelope.SenderDeviceId.Equals(Id(pc))||!envelope.RecipientDeviceId.Equals(Id(MobileId))||
                        envelope.Sequence<=inSeq||envelope.KeyEpoch!=1||!envelope.KeyId.Span.SequenceEqual(keyId))throw new Exception("file envelope");
                    inSeq=envelope.Sequence;
                    var plain=SessionCrypto.Decrypt(incoming,envelope.Nonce.ToByteArray(),AAD(envelope),envelope.Ciphertext.ToByteArray());
                    var payload=FileTransferPayloadV2.Parser.ParseFrom(plain);CryptographicOperations.ZeroMemory(plain);
                    switch(envelope.MessageType)
                    {
                        case FileTransferMessageTypeV2.Offer:
                            var offer=payload.Offer;job.OfferSeen=true;
                            while(job.HoldFirstOffer && job.Connections == 1) await Task.Delay(20,shutdown.Token);
                            if(job.DropFirstOffer && job.Connections == 1) { await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable,"file reconnect after token refresh",shutdown.Token);return; }
                            if(offer is null||offer.SizeBytes!=(ulong)job.Size||offer.Filename!=job.Filename||offer.DirectoryZip!=job.Zip||!offer.Sha256.Span.SequenceEqual(job.Hash))throw new Exception("file offer");
                            if(job.CancelOnOffer){job.CancelObserved=true;await Reply(FileTransferMessageTypeV2.Cancel,new FileTransferPayloadV2{Cancel=new(){ReasonCode="USER_CANCELLED"}});return;}
                            if(job.WaitOnOffer){while(!RevokeMobile)await Task.Delay(50,shutdown.Token);await Task.Delay(5000,shutdown.Token);return;}
                            if(job.Received.Length>0)job.ResumeSeen=job.Received.Length;
                            await Reply(FileTransferMessageTypeV2.Resume,new FileTransferPayloadV2{Resume=new(){CommittedOffset=(ulong)job.Received.Length}});break;
                        case FileTransferMessageTypeV2.Chunk:
                            var chunk=payload.Chunk;if(chunk is null||chunk.Offset!=(ulong)job.Received.Length||chunk.Data.Length>256*1024)throw new Exception("file chunk");
                            job.Received.Write(chunk.Data.Span);
                            if(!job.Zip&&job.Connections==1&&job.Size>300000){await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable,"isolated resume test",shutdown.Token);return;}
                            await Reply(FileTransferMessageTypeV2.Ack,new FileTransferPayloadV2{Ack=new(){CommittedOffset=(ulong)job.Received.Length}});break;
                        case FileTransferMessageTypeV2.Complete:
                            if(job.Received.Length!=job.Size||!SHA256.HashData(job.Received.ToArray()).AsSpan().SequenceEqual(job.Hash))throw new Exception("file final digest");
                            await Reply(FileTransferMessageTypeV2.CompleteAck,new FileTransferPayloadV2{CompleteAck=new(){CommittedOffset=(ulong)job.Size,Sha256=ByteString.CopyFrom(job.Hash)}});return;
                        default:throw new Exception("unexpected sender message");
                    }
                }
            }
            finally{CryptographicOperations.ZeroMemory(pair);CryptographicOperations.ZeroMemory(incoming);CryptographicOperations.ZeroMemory(outgoing);}
            async Task Reply(FileTransferMessageTypeV2 type,FileTransferPayloadV2 payload)
            {
                var envelope=new FileTransferEnvelopeV2{ProtocolVersion=2,TransferId=Id(id),SenderDeviceId=Id(MobileId),RecipientDeviceId=Id(pc),MessageType=type,Sequence=++outSeq,KeyEpoch=1,KeyId=ByteString.CopyFrom(keyId),Nonce=ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12))};
                envelope.Ciphertext=ByteString.CopyFrom(SessionCrypto.Encrypt(outgoing,envelope.Nonce.ToByteArray(),AAD(envelope),payload.ToByteArray()));
                await socket.SendAsync(envelope.ToByteArray(),WebSocketMessageType.Binary,true,shutdown.Token);
            }
        }
        finally{Interlocked.Decrement(ref ActiveFileSockets);}
    }
    private static byte[] AAD(FileTransferEnvelopeV2 value){var copy=value.Clone();copy.Ciphertext=ByteString.Empty;return copy.ToByteArray();}
    private static ByteString Id(Guid id)=>ByteString.CopyFrom(Convert.FromHexString(id.ToString("N")));
    private bool ValidateProof(HttpListenerRequest request, byte[] body, Device device)
    {
        var bearer = request.Headers["Authorization"] ?? "";
        if (!bearer.StartsWith("Bearer ", StringComparison.Ordinal) || !issuedTokens.TryGetValue(bearer[7..], out var subject) || subject != device.Subject) return false;
        var headers = request.Headers;
        var nonce = headers["X-TermLink-Nonce"] ?? "";
        var timestamp = headers["X-TermLink-Timestamp"];
        if (!long.TryParse(timestamp, out var milliseconds) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - milliseconds) > 120000 ||
            nonce.Length < 16 || !nonces.TryAdd(nonce, true)) return false;
        var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", request.HttpMethod, request.RawUrl, timestamp, nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        return Verify(device.Signing, canonical, headers["X-TermLink-Signature"] ?? "");
    }

    private static bool Verify(string key, string message, string signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(Decode(key), 0));
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.VerifySignature(Decode(signature));
    }
    private static byte[] Decode(string value)
    { var text = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '=')); }
    private async Task<JsonDocument> Receive(WebSocket socket)
    {
        var bytes = new byte[16384];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), shutdown.Token);
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Closed fixture.");
        return JsonDocument.Parse(bytes.AsMemory(0, result.Count));
    }
    private async Task Json(HttpListenerContext context, object value, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), shutdown.Token);
        context.Response.Close();
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); listener.Close();
        await loop;
        await Task.WhenAll(clients);
        shutdown.Dispose(); mobile.Dispose();
    }
}

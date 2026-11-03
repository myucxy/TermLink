using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Agent.Connection;
using TermLink.Agent.Host;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

var passed = 0;
await Check("AI encrypted route, invalid proof, revocation and disable", ValidateAiTransportAsync);
await Check("origin, token and callback validation", ValidateSecurityAsync);
await Check("browser PKCE and loopback callback", ValidateBrowserAsync);
await Check("enrollment, signed Relay, presence and retry", ValidateConnectionAsync);
await Check("account-isolated retained identity and token deletion", ValidateAccountsAsync);
await Check("registration conflict requires matching signed identity", ValidateConflictAsync);
await Check("sign-out cancels browser authorization", ValidateCancellationAsync);
await Check("refresh rotation persists and rejects changed subject", ValidateRefreshAsync);
await Check("bounded HTTP responses", ValidateBoundsAsync);
await Check("portable Host commands expose live Relay status", ValidateHostAsync);
await Check("revoked device cannot re-enroll or silently rotate keys", ValidateDeniedAsync);
await Check("untrusted discovery endpoint cannot receive OAuth code", ValidateDiscoveryAsync);
await Check("invalid Relay welcome never becomes connected", ValidateWelcomeAsync);
await Check("full exit stops Relay and PTY, retaining login", ValidateFullExitAsync);
await Check("full exit cancels pending browser authorization", ValidateFullExitAuthorizationAsync);
await Check("full exit retries failed terminal cleanup", ValidateFullExitRetryAsync);
await Check("portable authorization cancel and read-only command refresh", ValidatePortableControlsAsync);
Console.WriteLine($"Relay connection validation passed: {passed} scenarios.");

async Task Check(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine("PASS " + name);
}

static async Task ValidateAiTransportAsync()
{
    await using var server = new RelayFixture { EnableAi = true };
    var secrets = new MemorySecrets();
    var hosts = new List<RecordingAiHost>();
    await using var service = await RelayConnectionService.CreateAsync(secrets, new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS validation", "0.3.16.146"), CancellationToken.None,
        authorization: new FixtureAuthorization(server), aiHostFactory: _ => { var host = new RecordingAiHost(); hosts.Add(host); return host; });
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    await service.AuthorizeAsync("Mac AI fixture", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    var reply = await server.AiReply.Task.WaitAsync(TimeSpan.FromSeconds(12));
    Require(reply.Contains("catalog") && hosts.Sum(h => h.Calls) == 1 && hosts[0].Caller == server.MobileId,
        "Authenticated request/reply did not reach the AI host exactly once; tampered data was admitted.");
    Require(server.AiMetadata && server.Pings > 0 && server.InvalidProofs == 0, "AI capability or heartbeat did not match.");
    server.RevokeMobile = true;
    await Until(() => hosts[0].Disposed);
    await service.SetRemoteAccessAsync(false, CancellationToken.None);
    Require(hosts.All(h => h.Disposed) && !service.Snapshot.RemoteAccessEnabled, "Disabling remote access left an AI owner alive.");
    await service.SignOutAsync(CancellationToken.None);
}

static Task ValidateSecurityAsync()
{
    Require(RelaySecurity.NormalizeOrigin("https://example.com/") == "https://example.com", "Origin normalization.");
    foreach (var value in new[] { "http://example.com", "http://192.168.1.2", "https://user:secret@example.com", "https://example.com/path", "https://example.com/?secret=1", "https://example.com/#fragment", "file:///tmp/test" })
        Throws<RelayOperationException>(() => RelaySecurity.NormalizeOrigin(value));
    var issuer = "https://identity.example/realms/main";
    var token = Token(issuer, "A");
    Require(RelaySecurity.ReadClaims(token, issuer, "desktop").Subject == "A", "Token claims.");
    Throws<RelayOperationException>(() => RelaySecurity.ReadClaims(token, issuer + "-other", "desktop"));
    Throws<RelayOperationException>(() => RelaySecurity.ReadClaims(token, issuer, "another"));
    Throws<RelayOperationException>(() => RelaySecurity.ReadClaims(Token(issuer, "A", -30), issuer, "desktop"));
    var redirect = new Uri("http://127.0.0.1:12345/oauth2/callback");
    string Request(string query, string method = "GET", string host = "127.0.0.1:12345") =>
        $"{method} /oauth2/callback?{query} HTTP/1.1\r\nHost: {host}\r\n\r\n";
    Require(BrowserAuthorization.ParseCallback(Request("code=abc&state=expected"), redirect, "expected") == "abc", "Callback.");
    foreach (var request in new[] { Request("code=x&state=bad"), Request("code=x&state=expected&state=expected"),
        Request("code=x&state=expected", "POST"), Request("code=x&state=expected", host: "evil.example"),
        Request("code=x&state=expected").Replace("\r\n\r\n", "\r\nHost: evil\r\n\r\n"), Request("code=%0Asecret&state=expected") })
        Require(BrowserAuthorization.ParseCallback(request, redirect, "expected") is null, "Unsafe callback accepted.");
    Require(BrowserAuthorization.ParseCallback(Request("error=access_denied&state=expected"), redirect, "expected") == "", "Denial callback.");
    return Task.CompletedTask;
}

static async Task ValidateBrowserAsync()
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Task? browser = null;
    var handler = new TokenHandler();
    using var http = new HttpClient(handler);
    var flow = new BrowserAuthorization(uri =>
    {
        handler.Authorization = uri;
        browser = Task.Run(async () =>
        {
            var query = Query(uri.Query);
            using var browserHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var invalid = await browserHttp.GetAsync(query["redirect_uri"] + "?state=wrong&code=attack", deadline.Token);
            Require(invalid.StatusCode == HttpStatusCode.BadRequest, "Wrong state callback.");
            using var accepted = await browserHttp.GetAsync(query["redirect_uri"] + "?state=" + query["state"] + "&code=verified-code", deadline.Token);
            Require(accepted.IsSuccessStatusCode, "Valid callback rejected.");
        }, deadline.Token);
    });
    var tokens = await flow.AuthorizeAsync(new RelayOidcConfiguration("https://issuer.invalid", "desktop",
        new Uri("https://issuer.invalid/authorize"), new Uri("https://issuer.invalid/token")), http, deadline.Token);
    await browser!;
    Require(tokens.AccessToken == "test-access" && handler.Verified, "PKCE token exchange.");
}

static async Task ValidateConnectionAsync()
{
    await using var server = new RelayFixture();
    var secrets = new MemorySecrets();
    var authorization = new FixtureAuthorization(server);
    await using var service = await Create(server, secrets, authorization);
    Require(service.Snapshot.State == AgentConnectionState.NotConfigured && !service.Snapshot.AccountLinked, "Initial state.");
    await service.AuthorizeAsync("Mac validation", CancellationToken.None);
    Require(service.Snapshot.AccountLinked && !service.Snapshot.RemoteAccessEnabled, "Login must not enable remote access.");
    Require(server.Upserts > 0 && server.InvalidProofs == 0, "Signed enrollment.");
    server.DropNext = true;
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.Connected && server.Connections >= 2 &&
        service.Snapshot.Devices.Count > 0);
    Require(server.InvalidProofs == 0 && server.Pings > 0, "Signed WebSocket/liveness.");
    Require(server.EmptyProductCapabilities, "Premature terminal/file/AI capability advertisement.");
    await service.RetryAsync(CancellationToken.None);
    await Until(() => server.Connections >= 3 && service.Snapshot.State == AgentConnectionState.Connected);
    await service.SetRemoteAccessAsync(false, CancellationToken.None);
    Require(service.Snapshot.State == AgentConnectionState.Offline && !service.Snapshot.RemoteAccessEnabled, "Disable remote access.");
    var count = server.Connections;
    await Task.Delay(200);
    Require(server.Connections == count, "Disabled connection restarted.");
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.Connected);
    await service.DisposeAsync();
    await using var restored = await Create(server, secrets, authorization);
    await Until(() => restored.Snapshot.State == AgentConnectionState.Connected);
    Require(restored.Snapshot.AccountLinked, "Persisted account did not reconnect.");
}

static async Task ValidateAccountsAsync()
{
    await using var server = new RelayFixture();
    var secrets = new MemorySecrets();
    var authorization = new FixtureAuthorization(server);
    await using var service = await Create(server, secrets, authorization);
    await service.AuthorizeAsync("Mac A", CancellationToken.None);
    var pcA = service.Snapshot.Identity.PcId;
    var deviceA = server.LastDeviceId;
    await service.SignOutAsync(CancellationToken.None);
    Require(!secrets.Values.ContainsKey("relay-active-account-v1") &&
        secrets.Values.All(p => !Encoding.UTF8.GetString(p.Value).Contains("refresh-A", StringComparison.Ordinal)), "Sign-out left tokens.");
    authorization.Subject = "B";
    await service.AuthorizeAsync("Mac B", CancellationToken.None);
    Require(service.Snapshot.Identity.PcId != pcA && server.LastDeviceId != deviceA, "Cross-account identity reuse.");
    await ThrowsAsync<RelayOperationException>(() => service.ConfigureServerAsync("https://other.invalid", CancellationToken.None));
    await service.SignOutAsync(CancellationToken.None);
    authorization.Subject = "A";
    await service.AuthorizeAsync("Mac A", CancellationToken.None);
    Require(service.Snapshot.Identity.PcId == pcA && server.LastDeviceId == deviceA, "Same-account identity was replaced.");
    await service.SignOutAsync(CancellationToken.None);
    await service.ConfigureServerAsync("https://other.invalid", CancellationToken.None);
    Require(service.Snapshot.ServerOrigin == "https://other.invalid", "Server change after sign-out.");
}

static async Task ValidateConflictAsync()
{
    await using var server = new RelayFixture { WrongSelfKey = true };
    var secrets = new MemorySecrets();
    await using var service = await Create(server, secrets, new FixtureAuthorization(server));
    await ThrowsAsync<RelayOperationException>(() => service.AuthorizeAsync("Mac", CancellationToken.None));
    Require(!service.Snapshot.AccountLinked && !secrets.Values.ContainsKey("relay-active-account-v1"), "Bad identity committed tokens.");
    server.WrongSelfKey = false;
    await service.AuthorizeAsync("Mac", CancellationToken.None);
    Require(service.Snapshot.AccountLinked && server.Conflicts > 0, "Matching conflict recovery.");
}

static async Task ValidateCancellationAsync()
{
    await using var server = new RelayFixture();
    var auth = new PendingAuthorization();
    var secrets = new MemorySecrets();
    await using var service = await Create(server, secrets, auth);
    var login = service.AuthorizeAsync("Mac", CancellationToken.None);
    await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await service.SignOutAsync(CancellationToken.None);
    await ThrowsAsync<RelayOperationException>(() => login);
    Require(!service.Snapshot.AccountLinked && !secrets.Values.ContainsKey("relay-active-account-v1"), "Stale browser committed after sign-out.");
}

static async Task ValidateRefreshAsync()
{
    await using var server = new RelayFixture();
    var auth = new FixtureAuthorization(server) { ExpiresIn = 1 };
    var secrets = new MemorySecrets();
    await using var service = await Create(server, secrets, auth);
    await service.AuthorizeAsync("Mac", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.Connected && server.Refreshes == 1);
    var persisted = Encoding.UTF8.GetString(secrets.Values["relay-active-account-v1"]);
    Require(persisted.Contains("rotated-refresh-A", StringComparison.Ordinal), "Rotated refresh token not saved.");
    await service.SignOutAsync(CancellationToken.None);
    server.RefreshSubject = "B";
    await service.AuthorizeAsync("Mac", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.ReauthenticationRequired);
    Require(service.Snapshot.ErrorCode == "ACCOUNT_REFRESH_IDENTITY_CHANGED", "Changed refresh subject accepted.");
    Require(!Encoding.UTF8.GetString(secrets.Values["relay-active-account-v1"]).Contains("rotated-refresh-B", StringComparison.Ordinal), "Changed subject persisted.");
}

static async Task ValidateBoundsAsync()
{
    using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) };
    await ThrowsAsync<RelayOperationException>(() => RelayHttp.ReadJsonAsync(response, CancellationToken.None));
}

static async Task ValidateHostAsync()
{
    await using var server = new RelayFixture();
    var platform = new FixturePlatform();
    await using var runtime = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None, new FixtureAuthorization(server));
    async Task<LocalAgentCommandResult> Command(LocalAgentCommand command, object? payload = null) =>
        await runtime.HandleAsync(command, payload is null ? null : JsonSerializer.SerializeToElement(payload, LocalAgentProtocol.JsonOptions), CancellationToken.None);
    AgentStatusSnapshot Status() => (AgentStatusSnapshot)runtime.HandleAsync(LocalAgentCommand.GetStatus, null, CancellationToken.None)
        .AsTask().GetAwaiter().GetResult().Payload!;
    Require((await Command(LocalAgentCommand.ConfigureServer, new ConfigureServerPayload(server.Origin))).Success, "Host server configuration.");
    Require((await Command(LocalAgentCommand.BeginAccountAuthorization, new AccountAuthorizationPayload("Mac Host"))).Success, "Host authorization.");
    Require((await Command(LocalAgentCommand.SetRemoteAccess, new SetRemoteAccessPayload(true))).Success, "Host remote enable.");
    await Until(() => Status().ConnectionState == AgentConnectionState.Connected);
    var status = Status();
    PcContractValidator.Validate(status);
    Require(status.AccountLinked && status.Identity.Platform == PcPlatform.MacOS && status.Capabilities.Count == 0, "Host snapshot capability truth.");
    Require((await Command(LocalAgentCommand.Login, new LoginPayload("user", "password", "Mac"))).Code == "ACCOUNT_BROWSER_AUTHORIZATION_REQUIRED", "Password login enabled.");
    Require((await Command(LocalAgentCommand.SignOut)).Success && !Status().AccountLinked, "Host sign-out.");
}

static async Task ValidatePortableControlsAsync()
{
    await using var server = new RelayFixture();
    var platform = new FixturePlatform();
    var authorization = new PendingAuthorization();
    await using var runtime = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None, authorization);
    var configured = JsonSerializer.SerializeToElement(new ConfigureServerPayload(server.Origin), LocalAgentProtocol.JsonOptions);
    Require((await runtime.HandleAsync(LocalAgentCommand.ConfigureServer, configured, CancellationToken.None)).Success, "Cancel setup failed.");
    var login = runtime.HandleAsync(LocalAgentCommand.BeginAccountAuthorization,
        JsonSerializer.SerializeToElement(new AccountAuthorizationPayload("Mac"), LocalAgentProtocol.JsonOptions), CancellationToken.None).AsTask();
    await authorization.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require((await runtime.HandleAsync(LocalAgentCommand.CancelAccountAuthorization, null, CancellationToken.None)).Success,
        "Portable cancel control failed.");
    Require((await login.WaitAsync(TimeSpan.FromSeconds(5))).Code == "ACCOUNT_AUTHORIZATION_CANCELLED", "Cancel did not stop pending login.");
    Require(!((MemorySecrets)platform.SecretStore).Values.ContainsKey("relay-active-account-v1"), "Cancel wrote credentials.");
    Require((await runtime.HandleAsync(LocalAgentCommand.SaveTerminalCommands, null, CancellationToken.None)).Code == "PORTABLE_COMMAND_EDITING_UNAVAILABLE",
        "Portable Host accepted a command edit it cannot persist or launch.");
    platform.DiscoveredCommands = [new("zsh", "Z shell", "/bin/zsh", ["-l"], true, false)];
    var refreshed = await runtime.HandleAsync(LocalAgentCommand.GetStatus,
        JsonSerializer.SerializeToElement(new { force_refresh = true }), CancellationToken.None);
    Require(refreshed.Payload is AgentStatusSnapshot { TerminalCommands.Count: 1 } status && status.TerminalCommands[0].Id == "zsh",
        "Force-refresh did not re-detect installed shells.");
}

static async Task ValidateFullExitRetryAsync()
{
    var terminal = new RetryCleanupTerminalHost();
    var platform = new FixturePlatform { TerminalHost = terminal };
    await using var runtime = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None);
    var first = await runtime.HandleAsync(LocalAgentCommand.Shutdown, null, CancellationToken.None);
    Require(!first.Success && terminal.Attempts == 1, "Failed cleanup was reported as a successful exit.");
    Require((await runtime.HandleAsync(LocalAgentCommand.GetStatus, null, CancellationToken.None)).Code == "PC_AGENT_EXITING",
        "Failed cleanup allowed new work.");
    var retry = await runtime.HandleAsync(LocalAgentCommand.Shutdown, null, CancellationToken.None);
    Require(retry.Success && retry.Payload is AgentShutdownReceipt && terminal.Attempts == 2,
        "Complete exit did not retry failed terminal cleanup.");
    await runtime.DisposeAsync();
    Require(terminal.Attempts == 2, "Successful cleanup was repeated.");
}

static async Task ValidateFullExitAsync()
{
    await using var server = new RelayFixture();
    var platform = new FixturePlatform();
    await using var runtime = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None, new FixtureAuthorization(server));
    async Task<LocalAgentCommandResult> Command(LocalAgentCommand command, object? payload = null) =>
        await runtime.HandleAsync(command, payload is null ? null : JsonSerializer.SerializeToElement(payload, LocalAgentProtocol.JsonOptions), CancellationToken.None);
    Require((await Command(LocalAgentCommand.ConfigureServer, new ConfigureServerPayload(server.Origin))).Success, "Full exit config.");
    Require((await Command(LocalAgentCommand.BeginAccountAuthorization, new AccountAuthorizationPayload("Mac shutdown"))).Success, "Full exit login.");
    Require((await Command(LocalAgentCommand.SetRemoteAccess, new SetRemoteAccessPayload(true))).Success, "Full exit enable.");
    await Until(() => ((AgentStatusSnapshot)runtime.HandleAsync(LocalAgentCommand.GetStatus, null, CancellationToken.None)
        .AsTask().GetAwaiter().GetResult().Payload!).ConnectionState == AgentConnectionState.Connected);
    var secrets = (MemorySecrets)platform.SecretStore;
    var savedAccount = secrets.Values["relay-active-account-v1"].ToArray();
    var terminal = await platform.TerminalHost.LaunchAsync(new TerminalLaunchRequest("sh", Path.GetTempPath(), 80, 24,
        new Dictionary<string, string>()), CancellationToken.None);
    var result = await Command(LocalAgentCommand.Shutdown);
    Require(result.Success && result.Payload is AgentShutdownReceipt receipt && receipt.ProcessId == Environment.ProcessId,
        "Full exit receipt missing.");
    Require((await Command(LocalAgentCommand.GetStatus)).Code == "PC_AGENT_EXITING", "Host accepted work during shutdown.");
    Require(savedAccount.SequenceEqual(secrets.Values["relay-active-account-v1"]), "Full exit cleared login.");
    await ThrowsAsync<ObjectDisposedException>(() => platform.TerminalHost.SendInputAsync(terminal.SessionId, "pwd\n"u8.ToArray(), CancellationToken.None).AsTask());
    var connections = server.Connections;
    await Task.Delay(150);
    Require(connections == server.Connections, "Full exit restarted Relay.");
    Require((await Command(LocalAgentCommand.Shutdown)).Success, "Repeated shutdown is not idempotent.");
}

static async Task ValidateFullExitAuthorizationAsync()
{
    await using var server = new RelayFixture();
    var platform = new FixturePlatform();
    var authorization = new PendingAuthorization();
    await using var runtime = await PortableAgentRuntime.CreateAsync(platform, CancellationToken.None, authorization);
    var configured = JsonSerializer.SerializeToElement(new ConfigureServerPayload(server.Origin), LocalAgentProtocol.JsonOptions);
    Require((await runtime.HandleAsync(LocalAgentCommand.ConfigureServer, configured, CancellationToken.None)).Success, "Pending authorization config.");
    var login = runtime.HandleAsync(LocalAgentCommand.BeginAccountAuthorization,
        JsonSerializer.SerializeToElement(new AccountAuthorizationPayload("Mac"), LocalAgentProtocol.JsonOptions), CancellationToken.None).AsTask();
    await authorization.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require((await runtime.HandleAsync(LocalAgentCommand.Shutdown, null, CancellationToken.None)).Success, "Shutdown during authorization failed.");
    Require((await login.WaitAsync(TimeSpan.FromSeconds(5))).Code == "ACCOUNT_AUTHORIZATION_CANCELLED", "Browser authorization survived full exit.");
    Require(!((MemorySecrets)platform.SecretStore).Values.ContainsKey("relay-active-account-v1"), "Cancelled authorization persisted credentials.");
}

static async Task ValidateDeniedAsync()
{
    await using var server = new RelayFixture();
    var secrets = new MemorySecrets();
    var auth = new FixtureAuthorization(server);
    await using var service = await Create(server, secrets, auth);
    await service.AuthorizeAsync("Mac", CancellationToken.None);
    var device = server.LastDeviceId;
    server.DenySelf = true;
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.ReauthenticationRequired);
    var attempts = server.Conflicts;
    await Task.Delay(200);
    Require(server.Conflicts == attempts, "Denied device kept retrying.");
    await service.SignOutAsync(CancellationToken.None);
    await ThrowsAsync<RelayOperationException>(() => service.AuthorizeAsync("Mac", CancellationToken.None));
    Require(device == server.LastDeviceId && !service.Snapshot.AccountLinked, "Denied device key rotated or login accepted.");
}

static async Task ValidateDiscoveryAsync()
{
    await using var server = new RelayFixture { WrongDiscoveryEndpoint = true };
    var secrets = new MemorySecrets();
    var authorization = new PendingAuthorization();
    await using var service = await Create(server, secrets, authorization);
    await ThrowsAsync<RelayOperationException>(() => service.AuthorizeAsync("Mac", CancellationToken.None));
    Require(!authorization.Started.Task.IsCompleted && !secrets.Values.ContainsKey("relay-active-account-v1"), "Discovery accepted foreign token endpoint.");
}

static async Task ValidateWelcomeAsync()
{
    await using var server = new RelayFixture { InvalidWelcome = true };
    var secrets = new MemorySecrets();
    await using var service = await Create(server, secrets, new FixtureAuthorization(server));
    await service.AuthorizeAsync("Mac", CancellationToken.None);
    await service.SetRemoteAccessAsync(true, CancellationToken.None);
    await Until(() => service.Snapshot.State == AgentConnectionState.Reconnecting && service.Snapshot.ErrorCode == "RELAY_WELCOME_INVALID");
    Require(server.Pings == 0, "Invalid welcome started presence traffic.");
    await service.SetRemoteAccessAsync(false, CancellationToken.None);
}

static async Task<RelayConnectionService> Create(RelayFixture server, MemorySecrets secrets, IRelayAuthorization auth)
{
    var identity = new PcAgentIdentity(Guid.NewGuid(), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS validation", "0.3.16.146");
    var service = await RelayConnectionService.CreateAsync(secrets, identity, CancellationToken.None, authorization: auth);
    await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
    return service;
}

static string Token(string issuer, string subject, int seconds = 3600) =>
    "eyJhbGciOiJub25lIn0." + RelaySecurity.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    { iss = issuer, azp = "desktop", sub = subject, preferred_username = "user-" + subject, name = "User " + subject,
        exp = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds() })) + ".fixture-signature";

static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

static async Task Until(Func<bool> condition)
{
    var end = DateTime.UtcNow.AddSeconds(12);
    while (!condition())
    {
        if (DateTime.UtcNow >= end) throw new TimeoutException("Connection validation timed out.");
        await Task.Delay(25);
    }
}

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
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

file sealed class FixturePlatform : IPlatformServices, ITerminalCommandDiscovery
{
    public IReadOnlyList<TerminalCommandCapability> DiscoveredCommands { get; set; } = [];
    public PlatformDescriptor Descriptor { get; } = new(PcPlatform.MacOS, PcArchitecture.Arm64, "macOS test", "osx-arm64", false);
    public ISecretStore SecretStore { get; } = new MemorySecrets();
    public ITerminalCommandDiscovery TerminalCommands => this;
    public ILocalIpcTransport LocalIpc => throw new NotSupportedException();
    public ITerminalSessionHost TerminalHost { get; init; } = new PosixTerminalSessionHost();
    public IFileSystemOperations FileSystem => throw new NotSupportedException();
    public IAutostartManager Autostart => throw new NotSupportedException();
    public IUpdateInstaller Updates => throw new NotSupportedException();
    public IProcessSupervisor Processes => throw new NotSupportedException();
    public IPlatformNotification Notifications => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<TerminalCommandCapability>> DiscoverAsync(CancellationToken token) =>
        ValueTask.FromResult(DiscoveredCommands);
}

file sealed class RetryCleanupTerminalHost : ITerminalSessionHost, IAsyncDisposable
{
    public int Attempts { get; private set; }
    public ValueTask DisposeAsync() => ++Attempts == 1
        ? ValueTask.FromException(new IOException("Simulated cleanup failure."))
        : ValueTask.CompletedTask;
    public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken token) => throw new NotSupportedException();
    public ValueTask ResizeAsync(Guid id, int columns, int rows, CancellationToken token) => throw new NotSupportedException();
    public ValueTask SendInputAsync(Guid id, ReadOnlyMemory<byte> input, CancellationToken token) => throw new NotSupportedException();
    public ValueTask<int> ReadOutputAsync(Guid id, Memory<byte> output, CancellationToken token) => throw new NotSupportedException();
    public ValueTask SignalAsync(Guid id, TerminalSignal signal, CancellationToken token) => throw new NotSupportedException();
    public ValueTask<int> WaitForExitAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
    public ValueTask StopAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
}

file sealed class FixtureAuthorization(RelayFixture server) : IRelayAuthorization
{
    public string Subject { get; set; } = "A";
    public int ExpiresIn { get; set; } = 3600;
    public Task<RelayTokens> AuthorizeAsync(RelayOidcConfiguration config, HttpClient http, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new RelayTokens(server.TokenFor(Subject), "refresh-" + Subject, ExpiresIn));
    }
}

file sealed class PendingAuthorization : IRelayAuthorization
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<RelayTokens> AuthorizeAsync(RelayOidcConfiguration config, HttpClient http, CancellationToken token)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException();
    }
}

file sealed class TokenHandler : HttpMessageHandler
{
    public Uri? Authorization { get; set; }
    public bool Verified { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var query = Authorization!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var form = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Verified = request.RequestUri!.AbsoluteUri == "https://issuer.invalid/token" && form["code"] == "verified-code" &&
            form["redirect_uri"] == query["redirect_uri"] && form["client_id"] == "desktop" &&
            RelaySecurity.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))) == query["code_challenge"];
        if (!Verified) throw new InvalidOperationException("PKCE invalid.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600}") };
    }
}

file sealed class RecordingAiHost : IRelayAiHost
{
    public int Calls;
    public Guid Caller;
    public bool Disposed;
    public Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized) {
        if (!authorized()) throw new InvalidOperationException("Invalid authority");
        Interlocked.Increment(ref Calls); Caller = caller;
        return Task.FromResult("{\"hostId\":\"fixture\",\"action\":\"catalog\"}");
    }
    public void Dispose() => Disposed = true;
}

file sealed class RelayFixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<Guid, Device> devices = new();
    private readonly ConcurrentDictionary<string, bool> nonces = new();
    private readonly ConcurrentDictionary<string, string> tokens = new();
    private int connections, pings, upserts, invalidProofs, conflicts, refreshes;
    private Guid metadataPCId;
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
    public bool WrongSelfKey { get; set; }
    public bool DenySelf { get; set; }
    public bool WrongDiscoveryEndpoint { get; set; }
    public bool InvalidWelcome { get; set; }
    public bool EmptyProductCapabilities { get; private set; }
    public Guid LastDeviceId { get; private set; }
    public string RefreshSubject { get; set; } = "A";
    private sealed record Device(string Signing, string Exchange, string Name, string Subject);

    public RelayFixture()
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

    public string TokenFor(string subject) => tokens.GetOrAdd(subject, key => "eyJhbGciOiJub25lIn0." + RelaySecurity.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    { iss = Issuer, azp = "desktop", sub = subject, preferred_username = "user-" + subject, name = "User " + subject,
        exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })) + ".fixture-signature");

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/v1/client-config") { await Json(context, new { oidc_issuer = Issuer, windows_oidc_client_id = "desktop", windows_redirect_uri = "http://127.0.0.1/oauth2/callback" }); return; }
            if (path == "/realms/desktop/.well-known/openid-configuration")
            { await Json(context, new { issuer = Issuer, authorization_endpoint = Origin + "/authorize", token_endpoint = WrongDiscoveryEndpoint ? "https://foreign.invalid/token" : Origin + "/token" }); return; }
            if (path == "/token")
            { Interlocked.Increment(ref refreshes); await Json(context, new { access_token = TokenFor(RefreshSubject), refresh_token = "rotated-refresh-" + RefreshSubject, expires_in = 3600 }); return; }
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, shutdown.Token);
            var bytes = body.ToArray();
            var bearer = context.Request.Headers["Authorization"] ?? "";
            var subject = bearer == "Bearer " + TokenFor("A") ? "A" : "B";
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
            if (path.StartsWith("/v1/pcs/", StringComparison.Ordinal))
            {
                metadataPCId = Guid.Parse(path.Split('/')[3]);
                using var metadata = JsonDocument.Parse(bytes);
                var value = metadata.RootElement;
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
    }

    private bool ValidateProof(HttpListenerRequest request, byte[] body, Device device)
    {
        if (request.Headers["Authorization"] != "Bearer " + TokenFor(device.Subject)) return false;
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

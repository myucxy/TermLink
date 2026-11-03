using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Protocol;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent.Connection;

public sealed record RelayConnectionSnapshot(
    PcAgentIdentity Identity, AgentConnectionState State, bool AccountLinked, string Username, string Nickname,
    string ServerOrigin, bool RemoteAccessEnabled, string? ErrorCode, IReadOnlyList<AccountDeviceSnapshot> Devices);

/// <summary>
/// Portable account and Relay owner. Only the installed AI owner is advertised;
/// terminal, file and update data planes remain unavailable.
/// </summary>
public sealed partial class RelayConnectionService : IAsyncDisposable
{
    public const string DefaultOrigin = "https://termlink.kget.cc:8910";
    private const string SettingsKey = "relay-server-origin-v1";
    private const string ActiveKey = "relay-active-account-v1";
    private static readonly string[] TransportCapabilities =
        ["cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1", "liveness_probe_v1"];
    private readonly ISecretStore secrets;
    private readonly PcAgentIdentity localIdentity;
    private readonly HttpClient http;
    private readonly IRelayAuthorization authorization;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private string origin = DefaultOrigin;
    private Account? account;
    private RelayConnectionSnapshot snapshot;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private CancellationTokenSource? authorizationCancellation;
    private long generation;
    private int disposed;
    private readonly Func<string, IRelayAiHost>? aiHostFactory;
    private IRelayAiHost? aiHost;

    private sealed record RetainedIdentity(Guid DeviceId, Guid PcId, string MachineId, string PrivateMaterial, long KeyEpoch);
    private sealed record Account(
        string Origin, string Issuer, string ClientId, string Subject, string Username, string Nickname, string PcName,
        RetainedIdentity Identity, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, bool RemoteAccessEnabled);

    private RelayConnectionService(ISecretStore secrets, PcAgentIdentity identity, HttpClient? http, IRelayAuthorization? authorization,
        Func<string, IRelayAiHost>? aiHostFactory)
    {
        this.secrets = secrets;
        localIdentity = identity;
        ownsHttp = http is null;
        this.http = http ?? RelayHttp.CreateClient();
        this.authorization = authorization ?? new BrowserAuthorization();
        this.aiHostFactory = aiHostFactory;
        snapshot = new RelayConnectionSnapshot(identity, AgentConnectionState.NotConfigured, false, "", "", origin, false, null, []);
    }

    public static async Task<RelayConnectionService> CreateAsync(
        ISecretStore secrets, PcAgentIdentity identity, CancellationToken token,
        HttpClient? http = null, IRelayAuthorization? authorization = null, Func<string, IRelayAiHost>? aiHostFactory = null)
    {
        if (!secrets.IsAvailable) throw new CryptographicException("A secure credential store is required.");
        if (identity.Platform is not (PcPlatform.MacOS or PcPlatform.Linux) ||
            identity.Architecture is not (PcArchitecture.Arm64 or PcArchitecture.X64))
            throw new PlatformNotSupportedException("The portable Relay Host requires macOS/Linux on arm64/x64.");
        var service = new RelayConnectionService(secrets, identity, http, authorization, aiHostFactory);
        try
        {
            var configured = await service.LoadAsync<string>(SettingsKey, token).ConfigureAwait(false);
            if (configured is not null) service.origin = RelaySecurity.NormalizeOrigin(configured);
            var stored = await service.LoadAsync<Account>(ActiveKey, token).ConfigureAwait(false);
            if (stored is not null)
            {
                if (stored.Origin != service.origin || RelaySecurity.NormalizeOrigin(stored.Origin) != stored.Origin ||
                    !Uri.TryCreate(stored.Issuer, UriKind.Absolute, out var issuer) || !RelaySecurity.IsSecureEndpoint(issuer) ||
                    stored.Subject.Length is < 1 or > 512 || stored.ClientId.Length is < 1 or > 256)
                    throw new RelayOperationException("ACCOUNT_STORED_CREDENTIALS_INVALID");
                using var identityCheck = OpenIdentity(stored.Identity);
                service.account = stored;
            }
            service.Publish(service.account is null ? AgentConnectionState.NotConfigured : AgentConnectionState.Offline);
            service.StartWorker();
            return service;
        }
        catch
        {
            await service.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public RelayConnectionSnapshot Snapshot { get { lock (sync) return snapshot; } }
    public bool SupportsAi => aiHostFactory is not null;

    public async Task ConfigureServerAsync(string value, CancellationToken token)
    {
        var normalized = RelaySecurity.NormalizeOrigin(value);
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (account is not null && normalized != origin) throw new RelayOperationException("ACCOUNT_SIGN_OUT_REQUIRED");
            CancelAuthorization();
            await SaveAsync(SettingsKey, normalized, token).ConfigureAwait(false);
            origin = normalized;
            Publish(account is null ? AgentConnectionState.NotConfigured : Snapshot.State);
        }
        finally { operations.Release(); }
    }

    public async Task AuthorizeAsync(string pcName, CancellationToken token)
    {
        pcName = pcName.Trim();
        if (Encoding.UTF8.GetByteCount(pcName) is < 1 or > 128 || pcName.Any(char.IsControl))
            throw new RelayOperationException("ACCOUNT_PC_NAME_INVALID");
        CancellationTokenSource attempt;
        string selectedOrigin;
        long epoch;
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelAuthorization();
            attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            authorizationCancellation = attempt;
            selectedOrigin = origin;
            epoch = generation;
        }
        finally { operations.Release(); }
        try
        {
            var oidc = await DiscoverAsync(selectedOrigin, attempt.Token).ConfigureAwait(false);
            var tokens = await authorization.AuthorizeAsync(oidc, http, attempt.Token).ConfigureAwait(false);
            var claims = RelaySecurity.ReadClaims(tokens.AccessToken, oidc.Issuer, oidc.ClientId);
            var scope = RelaySecurity.AccountScope(selectedOrigin, oidc.Issuer, claims.Subject);
            await operations.WaitAsync(attempt.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (epoch != generation || origin != selectedOrigin) throw new OperationCanceledException(attempt.Token);
                await StopWorkerAsync().ConfigureAwait(false);
                // Retained identities contain no OAuth credentials. Never reuse
                // another account's key, even on the same computer.
                var retained = await LoadAsync<RetainedIdentity>("relay-identity-" + scope, attempt.Token).ConfigureAwait(false);
                if (retained is null)
                {
                    using var identity = DeviceIdentity.Generate();
                    var material = identity.ExportPrivateMaterial();
                    try
                    {
                        retained = new RetainedIdentity(Guid.NewGuid(), Guid.NewGuid(),
                            RelaySecurity.Base64Url(RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(material), 1);
                    }
                    finally { CryptographicOperations.ZeroMemory(material); }
                    // Persist the token-free key before enrollment, so an
                    // interrupted first login cannot leave an orphan identity.
                    await SaveAsync("relay-identity-" + scope, retained, attempt.Token).ConfigureAwait(false);
                }
                using var device = OpenIdentity(retained);
                var candidate = new Account(selectedOrigin, oidc.Issuer, oidc.ClientId, claims.Subject,
                    claims.Username, claims.Nickname, pcName, retained, tokens.AccessToken, tokens.RefreshToken,
                    Min(claims.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn)), false);
                await RegisterAndVerifyAsync(candidate, device, attempt.Token).ConfigureAwait(false);
                await UpsertAsync(candidate, device, attempt.Token).ConfigureAwait(false);
                await SaveAsync(SettingsKey, selectedOrigin, attempt.Token).ConfigureAwait(false);
                await SaveAsync(ActiveKey, candidate, attempt.Token).ConfigureAwait(false);
                RetireAiHost();
                account = candidate;
                Publish(AgentConnectionState.Offline);
            }
            finally
            {
                StartWorker(); // Restore the old account's worker if enrollment failed.
                operations.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw new RelayOperationException("ACCOUNT_AUTHORIZATION_CANCELLED");
        }
        finally
        {
            // Cancellation sources are disposed by their owning authorization
            // attempt, after any configuration/sign-out cancellation completes.
            await operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { if (ReferenceEquals(authorizationCancellation, attempt)) authorizationCancellation = null; }
            finally { operations.Release(); attempt.Dispose(); }
        }
    }

    public async Task SetRemoteAccessAsync(bool enabled, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (account is null) throw new RelayOperationException("ACCOUNT_LOGIN_REQUIRED");
            await StopWorkerAsync().ConfigureAwait(false);
            if (!enabled) RetireAiHost();
            var updated = account with { RemoteAccessEnabled = enabled };
            await SaveAsync(ActiveKey, updated, token).ConfigureAwait(false);
            account = updated;
            Publish(AgentConnectionState.Offline);
        }
        finally { StartWorker(); operations.Release(); }
    }

    public async Task RetryAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (account is null) throw new RelayOperationException("ACCOUNT_LOGIN_REQUIRED");
            await StopWorkerAsync().ConfigureAwait(false);
            StartWorker();
        }
        finally { operations.Release(); }
    }

    public async Task SignOutAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelAuthorization();
            await StopWorkerAsync().ConfigureAwait(false);
            // Once sign-out starts, clear the active credentials even if the UI
            // disconnects. Device keys remain in their account-scoped record.
            await secrets.DeleteAsync(ActiveKey, CancellationToken.None).ConfigureAwait(false);
            RetireAiHost();
            account = null;
            Publish(AgentConnectionState.NotConfigured);
        }
        finally { operations.Release(); }
    }

    private async Task<RelayOidcConfiguration> DiscoverAsync(string selectedOrigin, CancellationToken token)
    {
        using var response = await http.GetAsync(selectedOrigin + "/v1/client-config", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        RelayHttp.RequireSuccess(response);
        using var config = await RelayHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        var issuer = config.RootElement.GetProperty("oidc_issuer").GetString()?.TrimEnd('/') ?? "";
        var client = config.RootElement.GetProperty("windows_oidc_client_id").GetString() ?? "";
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || !RelaySecurity.IsSecureEndpoint(issuerUri) ||
            issuerUri.Query.Length != 0 || client.Length is < 1 or > 256 || client.Any(char.IsControl) ||
            config.RootElement.GetProperty("windows_redirect_uri").GetString() != "http://127.0.0.1/oauth2/callback")
            throw new RelayOperationException("ACCOUNT_CLIENT_CONFIG_INVALID");
        using var discoveryResponse = await http.GetAsync(issuer + "/.well-known/openid-configuration", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        RelayHttp.RequireSuccess(discoveryResponse);
        using var discovery = await RelayHttp.ReadJsonAsync(discoveryResponse, token).ConfigureAwait(false);
        if (discovery.RootElement.GetProperty("issuer").GetString()?.TrimEnd('/') != issuer)
            throw new RelayOperationException("ACCOUNT_ISSUER_MISMATCH");
        Uri Endpoint(string name)
        {
            if (!Uri.TryCreate(discovery.RootElement.GetProperty(name).GetString(), UriKind.Absolute, out var endpoint) ||
                !RelaySecurity.IsSecureEndpoint(endpoint) || endpoint.Query.Length != 0 ||
                endpoint.Scheme != issuerUri.Scheme || endpoint.Authority != issuerUri.Authority)
                throw new RelayOperationException("ACCOUNT_OIDC_ENDPOINT_INVALID");
            return endpoint;
        }
        return new RelayOidcConfiguration(issuer, client, Endpoint("authorization_endpoint"), Endpoint("token_endpoint"));
    }

    private async Task RegisterAndVerifyAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        using var response = await SendAsync(current, device, HttpMethod.Post, "/v1/devices/register",
            RelaySecurity.Registration(current.Identity.DeviceId, current.PcName, device), false, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.Conflict)
            RelayHttp.RequireSuccess(response);
        // A 409 is never evidence of possession. Require a fresh signed self
        // request and exact identity/key matches before saving any tokens.
        using var self = await SendAsync(current, device, HttpMethod.Get, "/v1/devices/self", [], true, token).ConfigureAwait(false);
        RelayHttp.RequireSuccess(self);
        using var json = await RelayHttp.ReadJsonAsync(self, token).ConfigureAwait(false);
        var value = json.RootElement.GetProperty("device");
        if (value.GetProperty("id").GetGuid() != current.Identity.DeviceId || value.GetProperty("type").GetString() != "PC_AGENT" ||
            value.GetProperty("trust_state").GetString() != "TRUSTED" ||
            value.GetProperty("signing_public_key").GetString() != Convert.ToBase64String(device.SigningPublicKey) ||
            value.GetProperty("exchange_public_key").GetString() != Convert.ToBase64String(device.Exchange.PublicKey))
            throw new RelayOperationException("ACCOUNT_DEVICE_IDENTITY_REJECTED");
    }

    private async Task UpsertAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = current.PcName, agent_version = localIdentity.AgentVersion, key_epoch = current.Identity.KeyEpoch,
            platform = localIdentity.Platform == PcPlatform.MacOS ? "macos" : "linux",
            architecture = localIdentity.Architecture == PcArchitecture.Arm64 ? "arm64" : "x64",
            platform_version = localIdentity.PlatformVersion, machine_id = current.Identity.MachineId,
            capabilities = aiHostFactory is null ? Array.Empty<string>() : new[] { "ai_agent_v1" }, session_heartbeat_capable = false, remote_agent_update_capable = false,
            supported_shells = Array.Empty<string>(), terminal_commands = Array.Empty<object>(),
        });
        using var response = await SendAsync(current, device, HttpMethod.Put, "/v1/pcs/" + current.Identity.PcId.ToString("D"), body, true, token).ConfigureAwait(false);
        RelayHttp.RequireSuccess(response);

    }

    private async Task<HttpResponseMessage> SendAsync(
        Account current, DeviceIdentity device, HttpMethod method, string path, byte[] body, bool proof, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, current.Origin + path);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (proof)
            foreach (var item in RelaySecurity.Proof(method, path, body, current.Identity.DeviceId, device, current.AccessToken))
                request.Headers.TryAddWithoutValidation(item.Key, item.Value);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        if (proof && method != HttpMethod.Get) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private void StartWorker()
    {
        if (account is null || !account.RemoteAccessEnabled || worker is { IsCompleted: false } || disposed != 0) return;
        workerCancellation = new CancellationTokenSource();
        worker = RunAsync(workerCancellation.Token);
    }

    private async Task StopWorkerAsync()
    {
        workerCancellation?.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
        workerCancellation?.Dispose();
        workerCancellation = null;
        worker = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Publish(failures == 0 ? AgentConnectionState.Connecting : AgentConnectionState.Reconnecting);
                await RefreshIfNeededAsync(token).ConfigureAwait(false);
                var current = account!;
                using var device = OpenIdentity(current.Identity);
                aiHost ??= aiHostFactory?.Invoke(RelaySecurity.AccountScope(current.Origin, current.Issuer, current.Subject));
                await RegisterAndVerifyAsync(current, device, token).ConfigureAwait(false);
                await UpsertAsync(current, device, token).ConfigureAwait(false);
                await PumpAsync(current, device, token).ConfigureAwait(false);
                failures = 0; // Normal token rotation reconnects with fresh proof.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                RetireAiHost();
                var code = error is RelayOperationException operation ? operation.Code :
                    error is OperationCanceledException ? "RELAY_TIMEOUT" : "RELAY_CONNECTION_FAILED";
                if (code is "ACCOUNT_REAUTHENTICATION_REQUIRED" or "ACCOUNT_TOKEN_INVALID" or
                    "ACCOUNT_REFRESH_IDENTITY_CHANGED" or "ACCOUNT_DEVICE_IDENTITY_REJECTED")
                {
                    Publish(AgentConnectionState.ReauthenticationRequired, code);
                    return;
                }
                Publish(AgentConnectionState.Reconnecting, code);
                failures = Math.Min(failures + 1, 6);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << failures)) + TimeSpan.FromMilliseconds(Random.Shared.Next(500)), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task RefreshIfNeededAsync(CancellationToken token)
    {
        var current = account!;
        if (current.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return;
        if (current.RefreshToken.Length == 0) throw new RelayOperationException("ACCOUNT_REAUTHENTICATION_REQUIRED");
        var oidc = await DiscoverAsync(current.Origin, token).ConfigureAwait(false);
        if (oidc.Issuer != current.Issuer || oidc.ClientId != current.ClientId)
            throw new RelayOperationException("ACCOUNT_REFRESH_IDENTITY_CHANGED");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = current.ClientId, ["refresh_token"] = current.RefreshToken,
        });
        using var response = await http.PostAsync(oidc.TokenEndpoint, content, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new RelayOperationException("ACCOUNT_REAUTHENTICATION_REQUIRED");
        RelayHttp.RequireSuccess(response);
        using var json = await RelayHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        var tokens = BrowserAuthorization.ReadTokens(json.RootElement);
        var claims = RelaySecurity.ReadClaims(tokens.AccessToken, current.Issuer, current.ClientId);
        if (claims.Subject != current.Subject) throw new RelayOperationException("ACCOUNT_REFRESH_IDENTITY_CHANGED");
        var updated = current with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken.Length == 0 ? current.RefreshToken : tokens.RefreshToken,
            ExpiresAt = Min(claims.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn)),
        };
        await SaveAsync(ActiveKey, updated, token).ConfigureAwait(false);
        account = updated;
    }

    private async Task PumpAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("termlink.realtime.v1");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        foreach (var item in RelaySecurity.Proof(HttpMethod.Get, "/v1/realtime", [], current.Identity.DeviceId, device, current.AccessToken))
            socket.Options.SetRequestHeader(item.Key, item.Value);
        var uri = new UriBuilder(current.Origin) { Scheme = new Uri(current.Origin).Scheme == "https" ? "wss" : "ws", Path = "/v1/realtime" }.Uri;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(uri, handshake.Token).ConfigureAwait(false);
        var expectedCapabilities = aiHost is null ? TransportCapabilities : TransportCapabilities.Append("ai_agent_v1").ToArray();
        await SendControlAsync(socket, new { role = "pc", protocol_versions = new[] { 1 }, capabilities = expectedCapabilities }, handshake.Token).ConfigureAwait(false);
        using var welcome = await ReceiveControlAsync(socket, handshake.Token).ConfigureAwait(false);
        var root = welcome.RootElement;
        var capabilities = root.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal);
        if (root.GetProperty("type").GetString() != "welcome" || root.GetProperty("protocol_version").GetInt32() != 1 ||
            !Guid.TryParse(root.GetProperty("connection_id").GetString(), out var connectionId) || connectionId == Guid.Empty ||
            capabilities.Count > 64 || !expectedCapabilities.All(capabilities.Contains) || capabilities.Any(c => c is null || !expectedCapabilities.Contains(c)))
            throw new RelayOperationException("RELAY_WELCOME_INVALID");
        var heartbeat = root.GetProperty("heartbeat_seconds").GetInt32();
        if (heartbeat is < 1 or > 120) throw new RelayOperationException("RELAY_WELCOME_INVALID");
        Publish(AgentConnectionState.Connected);
        if (aiHost is not null) {
            await PumpAiAsync(socket, account ?? current, device, heartbeat, token).ConfigureAwait(false);
            return;
        }
        while (!token.IsCancellationRequested)
        {
            // Application pings prove the Relay round trip, including networks
            // that silently retain a dead TCP connection. One receive at a time.
            var probe = Guid.NewGuid().ToString("D");
            using var liveness = CancellationTokenSource.CreateLinkedTokenSource(token);
            liveness.CancelAfter(TimeSpan.FromSeconds(10));
            await SendControlAsync(socket, new { type = "ping", probe_id = probe }, liveness.Token).ConfigureAwait(false);
            using var pong = await ReceiveControlAsync(socket, liveness.Token).ConfigureAwait(false);
            if (pong.RootElement.GetProperty("type").GetString() != "pong" || pong.RootElement.GetProperty("probe_id").GetString() != probe)
                throw new RelayOperationException("RELAY_LIVENESS_INVALID");
            using var devices = await SendAsync(current, device, HttpMethod.Get, "/v1/devices", [], true, token).ConfigureAwait(false);
            RelayHttp.RequireSuccess(devices);
            using var list = await RelayHttp.ReadJsonAsync(devices, token).ConfigureAwait(false);
            var entries = list.RootElement.GetProperty("items").EnumerateArray().Select(value => new AccountDeviceSnapshot(
                value.GetProperty("id").GetGuid(), value.GetProperty("name").GetString() ?? "",
                value.GetProperty("type").GetString() ?? "", value.GetProperty("trust_state").GetString() ?? "",
                value.TryGetProperty("online", out var online) && online.GetBoolean())).Take(PcContractLimits.MaximumDevices).ToArray();
            if (entries.Any(e => Encoding.UTF8.GetByteCount(e.Name) > 160 || e.Name.Any(char.IsControl) || e.DeviceId == Guid.Empty))
                throw new RelayOperationException("SERVER_DEVICE_LIST_INVALID");
            Publish(AgentConnectionState.Connected, devices: entries);
            await UpsertAsync(current, device, token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, heartbeat)), token).ConfigureAwait(false);
            if (current.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            {
                await RefreshIfNeededAsync(token).ConfigureAwait(false);
                // Reconnect with the new bearer and proof; never assume the
                // old WebSocket's authorization changes after token refresh.
                return;
            }
        }
    }

    private static Task SendControlAsync(ClientWebSocket socket, object value, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, token);

    private static async Task<JsonDocument> ReceiveControlAsync(ClientWebSocket socket, CancellationToken token)
    {
        var bytes = new byte[16 * 1024];
        var count = 0;
        while (true)
        {
            if (count == bytes.Length) throw new RelayOperationException("RELAY_CONTROL_TOO_LARGE");
            var result = await socket.ReceiveAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new RelayOperationException("RELAY_DISCONNECTED");
            if (result.MessageType != WebSocketMessageType.Text) throw new RelayOperationException("RELAY_DATA_PLANE_UNAVAILABLE");
            count += result.Count;
            if (result.EndOfMessage) return JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
        }
    }

    private void Publish(AgentConnectionState state, string? error = null, IReadOnlyList<AccountDeviceSnapshot>? devices = null)
    {
        var current = account;
        lock (sync)
        {
            snapshot = new RelayConnectionSnapshot(current is null ? localIdentity : localIdentity with
            { PcId = current.Identity.PcId, Name = current.PcName }, state, current is not null,
                current?.Username ?? "", current?.Nickname ?? "", origin, current?.RemoteAccessEnabled ?? false, error,
                devices ?? (state == AgentConnectionState.Connected ? snapshot.Devices : []));
        }
    }

    public async Task CancelPendingAuthorizationAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (authorizationCancellation is null) throw new RelayOperationException("ACCOUNT_AUTHORIZATION_NOT_WAITING");
            CancelAuthorization();
        }
        finally { operations.Release(); }
    }

    private void CancelAuthorization()
    {
        generation++;
        authorizationCancellation?.Cancel();
    }

    private static DeviceIdentity OpenIdentity(RetainedIdentity identity)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(identity.PrivateMaterial); }
        catch (FormatException) { throw new RelayOperationException("ACCOUNT_STORED_CREDENTIALS_INVALID"); }
        try
        {
            if (bytes.Length != 64 || identity.DeviceId == Guid.Empty || identity.PcId == Guid.Empty ||
                identity.KeyEpoch < 1 || identity.MachineId.Length != 43)
                throw new RelayOperationException("ACCOUNT_STORED_CREDENTIALS_INVALID");
            var signing = bytes[..32];
            var exchange = bytes[32..];
            try { return new DeviceIdentity(signing, exchange); }
            finally
            {
                CryptographicOperations.ZeroMemory(signing);
                CryptographicOperations.ZeroMemory(exchange);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<T?> LoadAsync<T>(string key, CancellationToken token)
    {
        var bytes = await secrets.RetrieveAsync(key, token).ConfigureAwait(false);
        if (bytes is null) return default;
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new RelayOperationException("ACCOUNT_STORED_CREDENTIALS_INVALID"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (bytes.Length > 64 * 1024) throw new RelayOperationException("ACCOUNT_STORED_CREDENTIALS_INVALID");
            await secrets.StoreAsync(key, bytes, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            CancelAuthorization();
            await StopWorkerAsync().ConfigureAwait(false);
            RetireAiHost();
            if (ownsHttp) http.Dispose();
        }
        finally { operations.Release(); }
    }
}

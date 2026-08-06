using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed class ProductionReauthenticationRequiredException : Exception
    {
        public ProductionReauthenticationRequiredException() : base("TermLink account authorization must be renewed.") { }
    }

    internal sealed class ProductionServerResponseException : HttpRequestException
    {
        public string ErrorCode { get; }

        public ProductionServerResponseException(HttpStatusCode statusCode, string errorCode)
            : base("TermLink server rejected the request.", null, statusCode)
        {
            ErrorCode = errorCode ?? string.Empty;
        }
    }

    internal sealed class ProductionAgentRuntime : IDisposable
    {
        private static readonly TimeSpan SubscriberIdleTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan AuthorityCacheDuration = TimeSpan.FromSeconds(30);
        private const int DefaultFlowWindowEvents = 128;
        private const ulong DefaultFlowWindowWireBytes = 3UL << 20;
        private const int MaxFlowWindowEvents = 4096;
        private const ulong MaxFlowWindowWireBytes = 8UL << 20;
        private sealed class CommittedEvent { public ManagedShellType ShellType; public LocalFrame Frame; }
        private sealed class PendingSession { public ManagedShellType ShellType; }
        private sealed class Subscriber
        {
            public readonly object Sync = new object();
            public Guid DeviceId;
            public byte[] ExchangePublicKey;
            public byte[] PairwiseKey;
            public long KeyEpoch;
            public DateTime LastActivityUtc;
            public ulong HighestAck;
            public ulong LastSentSequence;
            public bool FlowPaused;
            public int FlowWindowEvents = DefaultFlowWindowEvents;
            public ulong FlowWindowWireBytes = DefaultFlowWindowWireBytes;
            public ulong FlowControlEpoch;
            public ulong InFlightWireBytes;
            public readonly SortedDictionary<ulong, byte[]> SentFrames = new SortedDictionary<ulong, byte[]>();
            public bool DrainRequested;
            public bool Removed;
        }
        private sealed class LeaseStatus
        {
            public Guid HolderDeviceId;
            public Guid LeaseId;
            public long Version;
            public DateTime ExpiresAtUtc;
            public DateTime ValidatedAtUtc;
        }
        private readonly ProductionAgentConfig config;
        private readonly ProductionCredentials credentials;
        private readonly HttpClient http;
        // Frames are durably committed to each SessionAgent DPAPI buffer before
        // OnCommitted is invoked. Keep only bounded session metadata here and
        // let the publisher replay from that durable source, so a slow Relay
        // cannot block ConPTY and cannot silently drop a committed frame.
        private readonly ConcurrentDictionary<Guid, PendingSession> pendingSessions = new ConcurrentDictionary<Guid, PendingSession>();
        private readonly ConcurrentDictionary<Guid, ulong> publishedSequences = new ConcurrentDictionary<Guid, ulong>();
        private readonly SemaphoreSlim eventSignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim socketSendGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>> subscribers = new ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>>();
        private readonly ConcurrentDictionary<Guid, Subscriber> historyRecipients = new ConcurrentDictionary<Guid, Subscriber>();
        private readonly ConcurrentDictionary<Guid, byte> pendingHistorySessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte> terminalHistorySessions = new ConcurrentDictionary<Guid, byte>();
        private readonly SemaphoreSlim historySignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim historyRecipientGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim statusRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim tokenRefreshGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<Guid, LeaseStatus> leaseStatuses = new ConcurrentDictionary<Guid, LeaseStatus>();
        private readonly ConcurrentDictionary<Guid, ulong> localInputEpochs = new ConcurrentDictionary<Guid, ulong>();
        private readonly ConcurrentDictionary<string, ulong> inputSequences = new ConcurrentDictionary<string, ulong>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object lifecycleSync = new object();
        private readonly object statusSync = new object();
        private SessionRegistry registry;
        private Task worker;
        private Task historyWorker;
        private ClientWebSocket socket;
        private ProductionHistoryUploader historyUploader;
        private DateTime authorityExpiresUtc;
        private DateTime statusExpiresUtc;
        private ProductionRelayState relayState = ProductionRelayState.Connecting;
        private string relayErrorCode = string.Empty;
        private DateTime? lastConnectedAtUtc;
        private IList<ProductionDeviceSummary> productionDevices = new List<ProductionDeviceSummary>();
        private PairingChallengeSummary pairingChallenge;
        private Task statusRefreshWorker;
        private string tokenEndpoint;
        private int disposed;

        private ProductionAgentRuntime(ProductionAgentConfig config, ProductionCredentials credentials)
        {
            this.config = config; this.credentials = credentials;
            http = ProductionTls.CreateHttpClient(config.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
        }

        public static ProductionAgentRuntime TryCreate()
        {
            var config = ProductionAgentConfig.TryLoad(); if (config == null) return null;
            var credentials = ProductionCredentials.TryLoad(); if (credentials == null) return null;
            return new ProductionAgentRuntime(config, credentials);
        }

        public void Attach(SessionRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            lock (lifecycleSync)
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("ProductionAgentRuntime");
                if (this.registry != null) throw new InvalidOperationException("Production runtime is already attached.");
                this.registry = registry;
                QueueRegisteredSessionsForReplay();
                historyUploader = new ProductionHistoryUploader(http, SendAsync, credentials.DeviceId);
                historyWorker = Task.Run(HistoryUploadLoopAsync);
                worker = Task.Run(RunAsync);
            }
        }

        private void QueueRegisteredSessionsForReplay()
        {
            if (registry == null) return;
            var queued = false;
            foreach (var session in registry.GetSummaries())
            {
                pendingSessions.AddOrUpdate(
                    session.SessionId,
                    _ => new PendingSession { ShellType = session.ShellType },
                    (_, existing) => existing);
                queued = true;
            }
            if (!queued) return;
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void OnCommitted(ManagedShellType shellType, LocalFrame frame)
        {
            if (Volatile.Read(ref disposed) != 0 || frame == null) return;
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                localInputEpochs[frame.SessionId] = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
            }
            pendingSessions.AddOrUpdate(
                frame.SessionId,
                _ =>
                {
                    RemovePublishedSequence(frame.SessionId);
                    return new PendingSession { ShellType = shellType };
                },
                (_, existing) =>
                {
                    // Session IDs are unique. A replay snapshot can observe a
                    // freshly appended frame before this callback runs and may
                    // already have advanced the publication cursor to the same
                    // sequence. That is normal concurrency, not a reset.
                    if (frame.Sequence == 1)
                    {
                        RemovePublishedSequence(frame.SessionId);
                        return new PendingSession { ShellType = shellType };
                    }
                    return existing;
                });
            // Upload from the durable buffer immediately, but only mark a
            // terminal buffer releasable after PublishLoop has sent its final
            // lifecycle frame to current subscribers.
            QueueHistoryUpload(frame.SessionId, false);
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public bool DrainTerminalPublications(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (Volatile.Read(ref disposed) != 0) return pendingSessions.IsEmpty;
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { return pendingSessions.IsEmpty; }
            var deadline = DateTime.UtcNow.Add(timeout);
            while (!pendingSessions.IsEmpty && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            return pendingSessions.IsEmpty;
        }

        public ProductionStatus GetStatus(bool forceRefresh)
        {
            if (forceRefresh)
            {
                RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            else if (DateTime.UtcNow >= statusExpiresUtc)
            {
                lock (lifecycleSync)
                {
                    if (Volatile.Read(ref disposed) == 0 &&
                        (statusRefreshWorker == null || statusRefreshWorker.IsCompleted))
                    {
                        statusRefreshWorker = Task.Run(async () =>
                        {
                            try { await RefreshStatusAsync(cancellation.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                            catch (HttpRequestException) { }
                            catch (JsonException) { }
                        });
                    }
                }
            }
            return SnapshotStatus();
        }

        public PairingChallengeSummary CreatePairingChallenge(Guid targetDeviceId, string permission)
        {
            if (targetDeviceId == Guid.Empty || targetDeviceId == credentials.DeviceId) throw new ArgumentException("A different target device is required.", nameof(targetDeviceId));
            permission = (permission ?? string.Empty).Trim().ToUpperInvariant();
            if (permission != "VIEW" && permission != "CONTROL" && permission != "MANAGE")
                throw new ArgumentException("A supported permission is required.", nameof(permission));
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                target_device_id = targetDeviceId,
                pc_id = config.PcId,
                permission
            });
            try
            {
                using var response = SendAsync(HttpMethod.Post, "/v1/pairing/challenges", body, true, Guid.NewGuid().ToString("D"), cancellation.Token).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(response.Content.ReadAsByteArrayAsync(cancellation.Token).GetAwaiter().GetResult());
                var root = document.RootElement;
                var challenge = new PairingChallengeSummary
                {
                    ChallengeId = root.GetProperty("challenge_id").GetGuid(),
                    TargetDeviceId = targetDeviceId,
                    TargetName = root.GetProperty("target").GetProperty("name").GetString(),
                    TargetFingerprint = root.GetProperty("target").GetProperty("public_key_fingerprint").GetString(),
                    Permission = root.GetProperty("grant").GetProperty("permission").GetString(),
                    Code = root.GetProperty("code").GetString(),
                    ChallengeNonce = root.GetProperty("challenge_nonce").GetString(),
                    ExpiresAtUtc = root.GetProperty("expires_at").GetDateTime().ToUniversalTime()
                };
                lock (statusSync) pairingChallenge = challenge;
                statusExpiresUtc = DateTime.MinValue;
                return challenge;
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }

        /// <summary>
        /// Grants a permission to an Android device that has already completed
        /// account-level self trust.  This is intentionally separate from the
        /// short-lived pairing challenge flow used by pending devices.
        /// </summary>
        public void GrantDevice(Guid targetDeviceId, string permission)
        {
            if (targetDeviceId == Guid.Empty || targetDeviceId == credentials.DeviceId)
                throw new ArgumentException("A different target device is required.", nameof(targetDeviceId));
            permission = ProductionDeviceGrantContract.NormalizePermission(permission);
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                device_id = targetDeviceId,
                permission
            });
            // Scope idempotency to this click/operation. A key derived only
            // from the grant body would replay an old success for 24 hours if
            // the same permission were revoked and then deliberately granted
            // again. SendAsync still reuses this key for its authentication
            // retry, which is the retry boundary idempotency is intended for.
            var idempotencyKey = ProductionDeviceGrantContract.IdempotencyKey(
                config.PcId, targetDeviceId, permission, Guid.NewGuid());
            try
            {
                using var response = SendAsync(
                    HttpMethod.Post,
                    ProductionDeviceGrantContract.GrantsPath(config.PcId),
                    body,
                    true,
                    idempotencyKey,
                    cancellation.Token).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                statusExpiresUtc = DateTime.MinValue;
                RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }

        public void RevokeDevice(Guid targetDeviceId)
        {
            if (targetDeviceId == Guid.Empty || targetDeviceId == credentials.DeviceId) throw new ArgumentException("The local PC device cannot revoke itself.", nameof(targetDeviceId));
            using var response = SendAsync(HttpMethod.Post, "/v1/devices/" + targetDeviceId.ToString("D") + "/revoke", Array.Empty<byte>(), true, Guid.NewGuid().ToString("D"), cancellation.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            foreach (var session in subscribers.Values)
            {
                if (session.TryRemove(targetDeviceId, out var removed) && removed.PairwiseKey != null)
                    CryptographicOperations.ZeroMemory(removed.PairwiseKey);
            }
            if (historyRecipients.TryRemove(targetDeviceId, out var historyRecipient))
            {
                if (historyRecipient.ExchangePublicKey != null) CryptographicOperations.ZeroMemory(historyRecipient.ExchangePublicKey);
                if (historyRecipient.PairwiseKey != null) CryptographicOperations.ZeroMemory(historyRecipient.PairwiseKey);
            }
            lock (statusSync)
            {
                if (pairingChallenge != null && pairingChallenge.TargetDeviceId == targetDeviceId) pairingChallenge = null;
            }
            statusExpiresUtc = DateTime.MinValue;
            RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
        }

        private async Task RunAsync()
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    SetRelayState(ProductionRelayState.Connecting, string.Empty);
                    await EnsureRegisteredAsync(cancellation.Token).ConfigureAwait(false);
                    await ConnectAndPumpAsync(cancellation.Token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (ProductionReauthenticationRequiredException)
                {
                    SetRelayState(ProductionRelayState.Failed, "ACCOUNT_REAUTH_REQUIRED");
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    SetRelayState(ProductionRelayState.Reconnecting, ConnectionFailureCode(error));
                    await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
        }

        private static string ConnectionFailureCode(Exception error)
        {
            if (error is ProductionServerResponseException response && !string.IsNullOrWhiteSpace(response.ErrorCode))
            {
                return "HTTP_" + ((int)response.StatusCode.Value).ToString() + "_" + response.ErrorCode;
            }
            if (error is HttpRequestException request)
            {
                return request.StatusCode.HasValue
                    ? "HTTP_" + ((int)request.StatusCode.Value).ToString()
                    : "HTTP_TRANSPORT_FAILED";
            }
            if (error is WebSocketException) return "WEBSOCKET_CONNECTION_FAILED";
            if (error is InvalidDataException) return "RELAY_PROTOCOL_INVALID";
            if (error is JsonException) return "SERVER_RESPONSE_INVALID";
            if (error is CryptographicException) return "CRYPTOGRAPHIC_OPERATION_FAILED";
            return "RELAY_CONNECTION_FAILED";
        }

        private async Task EnsureRegisteredAsync(CancellationToken token)
        {
            var registration = new Dictionary<string, object>
            {
                ["id"] = credentials.DeviceId, ["type"] = "WINDOWS_PC", ["name"] = config.PcName,
                ["signing_public_key"] = Base64Url(credentials.Identity.SigningPublicKey), ["exchange_public_key"] = Base64Url(credentials.Identity.Exchange.PublicKey),
                ["key_id"] = "device-key-" + credentials.DeviceId.ToString("D")
            };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var nonce = RandomNonce();
            var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", credentials.DeviceId.ToString("D"), "WINDOWS_PC", config.PcName,
                registration["signing_public_key"], registration["exchange_public_key"], registration["key_id"], now, nonce);
            registration["timestamp_ms"] = now; registration["nonce"] = nonce; registration["signature"] = Base64Url(credentials.Identity.Sign(Encoding.UTF8.GetBytes(canonical)));
            var response = await SendAsync(HttpMethod.Post, "/v1/devices/register", JsonSerializer.SerializeToUtf8Bytes(registration), false, null, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.Conflict)
                await EnsureServerSuccessAsync(response, "DEVICE_REGISTER", token).ConfigureAwait(false);
            response.Dispose();
            var pcBody = JsonSerializer.SerializeToUtf8Bytes(new { name = config.PcName, agent_version = config.AgentVersion, key_epoch = config.KeyEpoch });
            using var pc = await SendAsync(HttpMethod.Put, "/v1/pcs/" + config.PcId.ToString("D"), pcBody, true, "pc-upsert-" + config.PcId.ToString("N"), token).ConfigureAwait(false);
            await EnsureServerSuccessAsync(pc, "PC_UPSERT", token).ConfigureAwait(false);
        }

        private static async Task EnsureServerSuccessAsync(HttpResponseMessage response, string operation, CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            var code = string.Empty;
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (bytes.Length <= 64 * 1024)
                {
                    using var document = JsonDocument.Parse(bytes);
                    if (document.RootElement.TryGetProperty("error", out var error) &&
                        error.TryGetProperty("code", out var value))
                    {
                        var candidate = value.GetString() ?? string.Empty;
                        if (candidate.Length <= 64)
                        {
                            var valid = true;
                            foreach (var character in candidate)
                            {
                                if ((character < 'A' || character > 'Z') &&
                                    (character < '0' || character > '9') && character != '_')
                                {
                                    valid = false;
                                    break;
                                }
                            }
                            if (valid) code = candidate;
                        }
                    }
                }
                CryptographicOperations.ZeroMemory(bytes);
            }
            catch (JsonException)
            {
            }
            throw new ProductionServerResponseException(
                response.StatusCode,
                string.IsNullOrWhiteSpace(code) ? operation : operation + "_" + code);
        }

        private async Task ConnectAndPumpAsync(CancellationToken token)
        {
            var active = new ClientWebSocket();
            lock (lifecycleSync)
            {
                if (Volatile.Read(ref disposed) != 0) throw new OperationCanceledException(token);
                socket = active;
            }
            ClearSubscribers();

            try
            {
                active.Options.AddSubProtocol("termlink.realtime.v1");
                ProductionTls.ConfigureWebSocket(active.Options, config.ServerUrl);
                var accessToken = await GetAccessTokenAsync(false, token).ConfigureAwait(false);
                AddProofHeaders(active.Options, HttpMethod.Get, "/v1/realtime", Array.Empty<byte>(), accessToken);
                var wsUri = new Uri(config.ServerUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase) + "/v1/realtime");
                await active.ConnectAsync(wsUri, token).ConfigureAwait(false);
                var hello = Encoding.UTF8.GetBytes("{\"role\":\"pc\",\"protocol_versions\":[1],\"capabilities\":[\"cumulative_ack\",\"e2ee_x25519_chacha20poly1305\",\"flow_control_v1\",\"lease_v1\",\"bounded_event_replay_v1\"]}");
                try { await SendWebSocketAsync(active, hello, WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(hello); }
                var welcome = new byte[16 * 1024];
                var result = await active.ReceiveAsync(welcome, token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Production Relay welcome is missing.");
                SetRelayState(ProductionRelayState.Connected, string.Empty);

                using (var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var receiver = ReceiveLoopAsync(active, pumpCancellation.Token);
                    var sender = PublishLoopAsync(active, pumpCancellation.Token);
                    try
                    {
                        await Task.WhenAny(receiver, sender).ConfigureAwait(false);
                    }
                    finally
                    {
                        pumpCancellation.Cancel();
                        try { await Task.WhenAll(receiver, sender).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested || pumpCancellation.IsCancellationRequested) { }
                    }
                }
            }
            finally
            {
                try
                {
                    using (var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    {
                        if (active.State == WebSocketState.Open)
                        {
                            await CloseWebSocketAsync(active, closeCancellation.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
                lock (lifecycleSync)
                {
                    if (ReferenceEquals(socket, active)) socket = null;
                }
                active.Dispose();
                ClearSubscribers();
                if (!token.IsCancellationRequested) SetRelayState(ProductionRelayState.Reconnecting, "RELAY_DISCONNECTED");
            }
        }

        private async Task RefreshStatusAsync(CancellationToken token)
        {
            await statusRefreshGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var response = await SendAsync(HttpMethod.Get, "/v1/devices", Array.Empty<byte>(), true, null, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                var devices = new List<ProductionDeviceSummary>();
                foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
                {
                    devices.Add(new ProductionDeviceSummary
                    {
                        DeviceId = item.GetProperty("id").GetGuid(),
                        Type = item.GetProperty("type").GetString(),
                        Name = item.GetProperty("name").GetString(),
                        TrustState = item.GetProperty("trust_state").GetString()
                    });
                }
                var activeSessions = registry == null ? new List<SessionSummary>() : new List<SessionSummary>(registry.GetSummaries());
                var retainedLeases = new HashSet<Guid>();
                foreach (var session in activeSessions)
                {
                    using var lease = await SendAsync(HttpMethod.Get, "/v1/sessions/" + session.SessionId.ToString("D") + "/lease", Array.Empty<byte>(), true, null, token).ConfigureAwait(false);
                    // A recovered local SessionHost can outlive a clean Relay
                    // deployment. Until its next event republishes the Session,
                    // the server deliberately hides it as either missing or
                    // forbidden. Neither response represents an active lease and
                    // must not prevent the PC from refreshing pending devices.
                    if (lease.StatusCode == HttpStatusCode.NotFound ||
                        lease.StatusCode == HttpStatusCode.Forbidden) continue;
                    lease.EnsureSuccessStatusCode();
                    using var leaseDocument = JsonDocument.Parse(await lease.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    leaseStatuses[session.SessionId] = new LeaseStatus
                    {
                        HolderDeviceId = leaseDocument.RootElement.GetProperty("holder_device_id").GetGuid(),
                        LeaseId = leaseDocument.RootElement.GetProperty("lease_id").GetGuid(),
                        Version = leaseDocument.RootElement.GetProperty("version").GetInt64(),
                        ExpiresAtUtc = leaseDocument.RootElement.GetProperty("expires_at").GetDateTime().ToUniversalTime(),
                        ValidatedAtUtc = DateTime.UtcNow
                    };
                    retainedLeases.Add(session.SessionId);
                }
                foreach (var lease in leaseStatuses)
                {
                    if (!retainedLeases.Contains(lease.Key)) leaseStatuses.TryRemove(lease.Key, out var ignored);
                }
                lock (statusSync)
                {
                    productionDevices = devices;
                    if (pairingChallenge != null && pairingChallenge.ExpiresAtUtc <= DateTime.UtcNow) pairingChallenge = null;
                }
                statusExpiresUtc = DateTime.UtcNow.AddSeconds(5);
            }
            finally { statusRefreshGate.Release(); }
        }

        private ProductionStatus SnapshotStatus()
        {
            PruneSubscribers();
            var sessions = new List<ProductionSessionSummary>();
            var registrySessions = registry == null ? new List<SessionSummary>() : new List<SessionSummary>(registry.GetSummaries());
            foreach (var session in registrySessions)
            {
                subscribers.TryGetValue(session.SessionId, out var viewers);
                leaseStatuses.TryGetValue(session.SessionId, out var lease);
                var activeLease = lease != null && lease.ExpiresAtUtc > DateTime.UtcNow ? lease : null;
                sessions.Add(new ProductionSessionSummary
                {
                    SessionId = session.SessionId,
                    ViewerCount = viewers == null ? 0 : viewers.Count,
                    ControllerDeviceId = activeLease == null ? (Guid?)null : activeLease.HolderDeviceId,
                    LeaseExpiresAtUtc = activeLease == null ? (DateTime?)null : activeLease.ExpiresAtUtc
                });
            }
            lock (statusSync)
            {
                var devices = new List<ProductionDeviceSummary>();
                foreach (var device in productionDevices)
                {
                    devices.Add(new ProductionDeviceSummary
                    {
                        DeviceId = device.DeviceId,
                        Type = device.Type,
                        Name = device.Name,
                        TrustState = device.TrustState
                    });
                }
                return new ProductionStatus
                {
                    Configured = true,
                    AccountLinked = true,
                    ServerHost = new Uri(config.ServerUrl).Host,
                    PcName = config.PcName,
                    RelayState = relayState,
                    LastErrorCode = relayErrorCode,
                    LastConnectedAtUtc = lastConnectedAtUtc,
                    Devices = devices,
                    Sessions = sessions,
                    PairingChallenge = pairingChallenge == null ? null : new PairingChallengeSummary
                    {
                        ChallengeId = pairingChallenge.ChallengeId,
                        TargetDeviceId = pairingChallenge.TargetDeviceId,
                        TargetName = pairingChallenge.TargetName,
                        TargetFingerprint = pairingChallenge.TargetFingerprint,
                        Permission = pairingChallenge.Permission,
                        Code = pairingChallenge.Code,
                        ChallengeNonce = pairingChallenge.ChallengeNonce,
                        ExpiresAtUtc = pairingChallenge.ExpiresAtUtc
                    }
                };
            }
        }

        private void SetRelayState(ProductionRelayState state, string errorCode)
        {
            lock (statusSync)
            {
                relayState = state;
                relayErrorCode = errorCode ?? string.Empty;
                if (state == ProductionRelayState.Connected) lastConnectedAtUtc = DateTime.UtcNow;
            }
        }

        private async Task PublishLoopAsync(ClientWebSocket active, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!await eventSignal.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
                {
                    PruneSubscribers();
                    PruneCompletedSessions();
                    continue;
                }
                PruneSubscribers();
                // Keep a small coalescing window for terminal bursts without
                // adding a quarter-second round trip to every ordered input
                // result. This keeps normal typing responsive while retaining
                // durable-buffer batching and Relay backpressure behavior.
                await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
                foreach (var pending in pendingSessions)
                {
                    var terminalPublished = false;
                    ulong resumeFrom;
                    publishedSequences.TryGetValue(pending.Key, out resumeFrom);
                    IList<LocalFrame> frames;
                    ulong earliest;
                    ulong latest;
                    if (registry == null || !registry.TryReadBufferedEvents(
                        pending.Key,
                        resumeFrom,
                        out frames,
                        out earliest,
                        out latest))
                    {
                        if (TryRemovePending(pending.Key, pending.Value))
                        {
                            RemovePublishedSequence(pending.Key);
                        }
                        continue;
                    }
                    foreach (var frame in frames)
                    {
                        if (publishedSequences.TryGetValue(pending.Key, out var alreadyPublished) &&
                            frame.Sequence <= alreadyPublished)
                        {
                            continue;
                        }
                        var delivered = await PublishAsync(active, new CommittedEvent { ShellType = pending.Value.ShellType, Frame = frame }, token).ConfigureAwait(false);
                        AdvancePublishedSequence(pending.Key, frame.Sequence);
                        if (IsTerminalFrame(frame))
                        {
                            if (delivered && TryRemovePending(pending.Key, pending.Value))
                            {
                                RemovePublishedSequence(pending.Key);
                            }
                            terminalPublished = true;
                            break;
                        }
                    }
                    if (frames.Count > 0) QueueHistoryUpload(pending.Key, terminalPublished);
                    if (frames.Count == 0 && latest > resumeFrom)
                    {
                        // No retained frame remains. The next Subscribe carries
                        // its own resume cursor and will receive an explicit Gap.
                        AdvancePublishedSequence(pending.Key, latest);
                    }
                }
            }
        }

        private void PruneCompletedSessions()
        {
            if (registry == null) return;
            foreach (var pending in pendingSessions)
            {
                if (registry.ContainsSession(pending.Key)) continue;
                if (TryRemovePending(pending.Key, pending.Value))
                {
                    RemovePublishedSequence(pending.Key);
                }
            }
        }

        private static bool IsTerminalFrame(LocalFrame frame)
        {
            if (frame == null || frame.Type != LocalFrameType.Lifecycle) return false;
            int exitCode;
            var state = LocalPayloadCodec.ParseLifecycle(frame.Payload, out exitCode);
            return state == LocalLifecycleState.Ended || state == LocalLifecycleState.Interrupted;
        }

        private void QueueHistoryUpload(Guid sessionId, bool terminal)
        {
            if (sessionId == Guid.Empty || Volatile.Read(ref disposed) != 0) return;
            if (terminal) terminalHistorySessions[sessionId] = 0;
            pendingHistorySessions[sessionId] = 0;
            try { historySignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HistoryUploadLoopAsync()
        {
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                await historySignal.WaitAsync(token).ConfigureAwait(false);
                var retry = false;
                foreach (var pending in pendingHistorySessions)
                {
                    if (!pendingHistorySessions.TryRemove(pending.Key, out var ignored)) continue;
                    try
                    {
                        IList<LocalFrame> frames;
                        ulong earliest;
                        ulong latest;
                        var durable = false;
                        if (registry != null && registry.TryReadBufferedEvents(
                            pending.Key,
                            0,
                            out frames,
                            out earliest,
                            out latest))
                        {
                            durable = true;
                            var events = new List<HistoryEventV1>(frames.Count);
                            foreach (var frame in frames)
                            {
                                var historyEvent = BuildHistoryEvent(frame);
                                if (historyEvent != null) events.Add(historyEvent);
                            }
                            if (events.Count > 0)
                            {
                                durable = await UploadHistoryAsync(pending.Key, events, token).ConfigureAwait(false);
                            }
                        }
                        if (terminalHistorySessions.ContainsKey(pending.Key) && !durable)
                        {
                            pendingHistorySessions[pending.Key] = 0;
                            retry = true;
                            continue;
                        }
                        if (terminalHistorySessions.TryRemove(pending.Key, out ignored) && registry != null)
                        {
                            registry.ReleaseBufferedSession(pending.Key);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch
                    {
                        pendingHistorySessions[pending.Key] = 0;
                        retry = true;
                    }
                }
                if (retry)
                {
                    // History may be disabled by entitlement for an extended
                    // period. Retain the DPAPI buffer and retry without a hot
                    // loop; transport failures still reach this same bounded
                    // retry path.
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    try { historySignal.Release(); }
                    catch (SemaphoreFullException) { }
                }
            }
        }

        private bool TryRemovePending(Guid sessionId, PendingSession expected)
        {
            return ((ICollection<KeyValuePair<Guid, PendingSession>>)pendingSessions).Remove(
                new KeyValuePair<Guid, PendingSession>(sessionId, expected));
        }

        private void RemovePublishedSequence(Guid sessionId)
        {
            ulong ignored;
            publishedSequences.TryRemove(sessionId, out ignored);
        }

        private void AdvancePublishedSequence(Guid sessionId, ulong sequence)
        {
            if (sequence == 0) return;
            publishedSequences.AddOrUpdate(
                sessionId,
                sequence,
                (_, current) => Math.Max(current, sequence));
        }

        private async Task<bool> PublishAsync(ClientWebSocket active, CommittedEvent item, CancellationToken token)
        {
            if (publishedSequences.TryGetValue(item.Frame.SessionId, out var alreadyPublished) &&
                item.Frame.Sequence <= alreadyPublished)
            {
                return true;
            }
            if (item.Frame.Type == LocalFrameType.LocalInputObserved)
            {
                using var released = await SendAsync(
                    HttpMethod.Delete,
                    "/v1/sessions/" + item.Frame.SessionId.ToString("D") + "/lease",
                    Array.Empty<byte>(),
                    true,
                    LocalInputLeaseIdempotencyKey(item.Frame.SessionId, item.Frame.Sequence),
                    token).ConfigureAwait(false);
                released.EnsureSuccessStatusCode();
            }
            if (item.Frame.Type == LocalFrameType.Lifecycle || item.Frame.Type == LocalFrameType.Resize)
            {
                await UpsertSessionAsync(item, token).ConfigureAwait(false);
            }
            PruneSubscribers();
            if (!subscribers.TryGetValue(item.Frame.SessionId, out var sessionSubscribers)) return true;
            var delivered = true;
            foreach (var subscriber in sessionSubscribers.Values)
            {
                var messageType = MapMessageType(item.Frame);
                if (messageType == MessageType.Unspecified) continue;
                IMessage payload = BuildPayload(item.Frame);
                var payloadBytes = payload.ToByteArray();
                try
                {
                    if (!await SendEncryptedAsync(active, item.Frame.SessionId, subscriber, messageType, Direction.PcToAndroid, item.Frame.Sequence, payloadBytes, token).ConfigureAwait(false))
                        delivered = false;
                }
                finally { CryptographicOperations.ZeroMemory(payloadBytes); }
            }
            return delivered;
        }

        private async Task UpsertSessionAsync(CommittedEvent item, CancellationToken token)
        {
            string state = "RUNNING"; int columns = 80, rows = 24; int? exitCode = null;
            if (item.Frame.Type == LocalFrameType.Resize) { LocalPayloadCodec.ParseResize(item.Frame.Payload, out var c, out var r); columns = c; rows = r; }
            if (item.Frame.Type == LocalFrameType.Lifecycle) { var lifecycle = LocalPayloadCodec.ParseLifecycle(item.Frame.Payload, out var exit); if (lifecycle == LocalLifecycleState.Ended) { state = "EXITED"; exitCode = exit; } else if (lifecycle == LocalLifecycleState.Interrupted) state = "INTERRUPTED"; }
            var endedAt = state == "RUNNING" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow;
            var body = JsonSerializer.SerializeToUtf8Bytes(new { pc_id = config.PcId, shell_type = item.ShellType.ToString().ToLowerInvariant(), title_cipher = "", state, columns, rows, last_output_seq = item.Frame.Sequence, ended_at = endedAt });
            using var response = await SendAsync(HttpMethod.Put, "/v1/sessions/" + item.Frame.SessionId.ToString("D"), body, true, "session-" + item.Frame.SessionId.ToString("N") + "-" + item.Frame.Sequence, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        private async Task ReceiveLoopAsync(ClientWebSocket active, CancellationToken token)
        {
            var buffer = new byte[1024 * 1024];
            while (active.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream(); WebSocketReceiveResult part;
                do { part = await active.ReceiveAsync(buffer, token).ConfigureAwait(false); if (part.MessageType == WebSocketMessageType.Close) return; stream.Write(buffer, 0, part.Count); if (stream.Length > buffer.Length) throw new InvalidDataException("Relay frame exceeds limit."); } while (!part.EndOfMessage);
                if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Binary Envelope required.");
                var envelope = Envelope.Parser.ParseFrom(stream.ToArray());
                await HandleIncomingAsync(active, envelope, token).ConfigureAwait(false);
            }
        }

        private async Task HandleIncomingAsync(ClientWebSocket active, Envelope envelope, CancellationToken token)
        {
            if (envelope.ProtocolVersion != 1 || envelope.Direction != Direction.AndroidToPc ||
                envelope.Seq == 0 || envelope.Nonce.Length != 12 || envelope.Ciphertext.Length < 16)
            {
                throw new InvalidDataException("Envelope metadata is invalid.");
            }
            if (!GuidFromBytes(envelope.RecipientDeviceId.Span).Equals(credentials.DeviceId)) throw new InvalidDataException("Envelope recipient mismatch.");
            var senderId = GuidFromBytes(envelope.SenderDeviceId.Span); var sessionId = GuidFromBytes(envelope.SessionId.Span);
            Subscriber subscriber;
            if (envelope.MessageType == MessageType.Subscribe)
            {
                subscriber = await GetSubscriberAsync(sessionId, senderId, token, true).ConfigureAwait(false);
            }
            else if (!subscribers.TryGetValue(sessionId, out var knownSubscribers) ||
                !knownSubscribers.TryGetValue(senderId, out subscriber))
            {
                if (envelope.MessageType == MessageType.Unsubscribe)
                {
                    try { subscriber = await GetSubscriberAsync(sessionId, senderId, token).ConfigureAwait(false); }
                    catch (InvalidDataException) { return; }
                    catch (HttpRequestException) { return; }
                }
                else
                {
                if (envelope.MessageType != MessageType.InputEvent &&
                    envelope.MessageType != MessageType.ResizeRequest) return;
                subscriber = await TryRestoreSubscriberAsync(sessionId, senderId, token).ConfigureAwait(false);
                if (subscriber == null) return;
                }
            }
            long subscriberEpoch;
            var pairwiseKey = SnapshotPairwiseKey(subscriber, out subscriberEpoch);
            if (pairwiseKey == null) return;
            var expectedKeyId = EpochKeyId(subscriberEpoch);
            if (!CryptographicOperations.FixedTimeEquals(expectedKeyId, envelope.KeyId.Span))
            {
                CryptographicOperations.ZeroMemory(pairwiseKey);
                CryptographicOperations.ZeroMemory(expectedKeyId);
                throw new InvalidDataException("Envelope key epoch is stale or invalid.");
            }
            var sessionKey = SessionCrypto.DeriveSessionKey(pairwiseKey, sessionId, CryptoDirection.AndroidToPc, expectedKeyId);
            CryptographicOperations.ZeroMemory(pairwiseKey);
            CryptographicOperations.ZeroMemory(expectedKeyId);
            byte[] plaintext = null;
            try
            {
                plaintext = SessionCrypto.Decrypt(sessionKey, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
                TouchSubscriber(subscriber);
                if (envelope.MessageType != MessageType.Subscribe &&
                    envelope.MessageType != MessageType.OutputAck &&
                    envelope.MessageType != MessageType.FlowControl &&
                    envelope.Ack != 0)
                {
                    throw new InvalidDataException("This message type must use ack=0.");
                }
                if (envelope.MessageType == MessageType.Subscribe)
                {
                    var subscribe = SubscribePayload.Parser.ParseFrom(plaintext);
                    if (subscribe.ResumeFromSeq != envelope.Ack ||
                        (subscribe.SubscriberExchangePublicKey.Length > 0 &&
                         !MatchesExchangePublicKey(subscriber, subscribe.SubscriberExchangePublicKey.Span)))
                    {
                        throw new InvalidDataException("Subscribe cursor or exchange key is invalid.");
                    }
                    await ReplaySubscriberAsync(
                        active,
                        sessionId,
                        subscriber,
                        subscribe.ResumeFromSeq,
                        token).ConfigureAwait(false);
                    return;
                }
                if (envelope.MessageType == MessageType.Unsubscribe)
                {
                    if (plaintext.Length != 0) throw new InvalidDataException("Unsubscribe payload must be empty.");
                    if (!RemoveSubscriber(sessionId, senderId, subscriber)) RetireSubscriber(subscriber);
                    return;
                }
                if (envelope.MessageType == MessageType.OutputAck)
                {
                    var acknowledgement = OutputAckPayload.Parser.ParseFrom(plaintext);
                    if (acknowledgement.HighestContiguousSeq != envelope.Ack ||
                        acknowledgement.Missing.Count != 0 ||
                        !TryAdvanceSubscriberAck(subscriber, acknowledgement.HighestContiguousSeq))
                    {
                        throw new InvalidDataException("Output acknowledgement is invalid.");
                    }
                    if (TakeSubscriberDrainRequest(subscriber))
                    {
                        await ReplaySubscriberAsync(active, sessionId, subscriber, acknowledgement.HighestContiguousSeq, token).ConfigureAwait(false);
                    }
                    return;
                }
                if (envelope.MessageType == MessageType.FlowControl)
                {
                    var flow = FlowControlPayload.Parser.ParseFrom(plaintext);
                    if (flow.HighestContiguousSeq != envelope.Ack ||
                        flow.ControlEpoch == 0 ||
                        flow.MaxUnackedEvents > (uint)MaxFlowWindowEvents ||
                        flow.MaxUnackedWireBytes > MaxFlowWindowWireBytes ||
                        !TryApplyFlowControl(subscriber, flow))
                    {
                        throw new InvalidDataException("Flow control payload is invalid.");
                    }
                    if (TakeSubscriberDrainRequest(subscriber))
                    {
                        await ReplaySubscriberAsync(active, sessionId, subscriber, flow.HighestContiguousSeq, token).ConfigureAwait(false);
                    }
                    return;
                }
                if (envelope.MessageType == MessageType.ResizeRequest)
                {
                    var resize = ResizePayload.Parser.ParseFrom(plaintext);
                    if (resize.Columns < 1 || resize.Columns > short.MaxValue ||
                        resize.Rows < 1 || resize.Rows > short.MaxValue ||
                        resize.LeaseId.Length != 16)
                    {
                        return;
                    }
                    var resizeLeaseId = GuidFromBytes(resize.LeaseId.Span);
                    if (!await LeaseMatchesAsync(sessionId, senderId, resizeLeaseId, resize.LeaseVersion, token).ConfigureAwait(false)) return;
                    registry.TrySendResize(sessionId, (short)resize.Columns, (short)resize.Rows);
                    return;
                }
                if (envelope.MessageType != MessageType.InputEvent) return;
                var input = InputEventPayload.Parser.ParseFrom(plaintext);
                if (input.InputId.Length != 16 || input.LeaseId.Length != 16 || input.InputSeq == 0 || input.Input.Length == 0) return;
                var inputId = GuidFromBytes(input.InputId.Span); var leaseId = GuidFromBytes(input.LeaseId.Span);
                var sequenceKey = sessionId.ToString("N") + senderId.ToString("N");
                var previous = inputSequences.GetOrAdd(sequenceKey, 0);
                if (input.InputSeq <= previous)
                {
                    registry.TrySendRemoteInputRejection(sessionId, inputId, LocalRemoteInputResult.InputSequenceRejected);
                    return;
                }
                if (!await LeaseMatchesAsync(sessionId, senderId, leaseId, input.LeaseVersion, token).ConfigureAwait(false))
                {
                    registry.TrySendRemoteInputRejection(sessionId, inputId, LocalRemoteInputResult.ControlLeaseRejected);
                    return;
                }
                var inputBytes = input.Input.ToByteArray();
                try
                {
                    if (registry.TrySendRemoteInput(sessionId, inputId, input.ObservedLocalInputEpoch, inputBytes))
                    {
                        inputSequences[sequenceKey] = input.InputSeq;
                    }
                    else
                    {
                        registry.TrySendRemoteInputRejection(sessionId, inputId, LocalRemoteInputResult.SessionInputUnavailable);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(inputBytes); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private Subscriber RememberSubscriber(Guid sessionId, Subscriber candidate)
        {
            var sessionSubscribers = subscribers.GetOrAdd(
                sessionId,
                _ => new ConcurrentDictionary<Guid, Subscriber>());
            while (true)
            {
                if (sessionSubscribers.TryGetValue(candidate.DeviceId, out var retained))
                {
                    // A periodic SUBSCRIBE may resolve to the Subscriber that is
                    // already published for this session. Never retire that same
                    // instance: retirement zeroizes the live pairwise key.
                    if (ReferenceEquals(retained, candidate)) return retained;
                    lock (retained.Sync)
                    {
                        if (!retained.Removed)
                        {
                            RetireSubscriber(candidate);
                            return retained;
                        }
                    }
                    ((ICollection<KeyValuePair<Guid, Subscriber>>)sessionSubscribers).Remove(
                        new KeyValuePair<Guid, Subscriber>(candidate.DeviceId, retained));
                    continue;
                }
                if (sessionSubscribers.TryAdd(candidate.DeviceId, candidate)) return candidate;
            }
        }

        private static string LocalInputLeaseIdempotencyKey(Guid sessionId, ulong frameSequence)
        {
            // A recovered durable LocalInputObserved frame must replay the same
            // DELETE operation. Coupling the key to its immutable local frame
            // identity prevents an old replay from fencing a later lease.
            return "local-input-priority-" + sessionId.ToString("N") + "-" + frameSequence.ToString("X16");
        }

        private async Task<bool> LeaseMatchesAsync(
            Guid sessionId,
            Guid senderId,
            Guid leaseId,
            long leaseVersion,
            CancellationToken token)
        {
            var now = DateTime.UtcNow;
            if (leaseStatuses.TryGetValue(sessionId, out var cached) &&
                cached.HolderDeviceId == senderId &&
                cached.LeaseId == leaseId &&
                cached.Version == leaseVersion &&
                cached.ExpiresAtUtc > now &&
                cached.ValidatedAtUtc >= now.AddSeconds(-1))
            {
                return true;
            }
            try
            {
                using var response = await SendAsync(
                    HttpMethod.Get,
                    "/v1/sessions/" + sessionId.ToString("D") + "/lease",
                    Array.Empty<byte>(),
                    true,
                    null,
                    token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                var lease = document.RootElement;
                if (!lease.TryGetProperty("holder_device_id", out var holder) ||
                    !lease.TryGetProperty("lease_id", out var id) ||
                    !lease.TryGetProperty("version", out var version) ||
                    !lease.TryGetProperty("expires_at", out var expires) ||
                    !holder.TryGetGuid(out var parsedHolder) ||
                    !id.TryGetGuid(out var parsedLease) ||
                    !version.TryGetInt64(out var parsedVersion) ||
                    !expires.TryGetDateTime(out var parsedExpires))
                {
                    return false;
                }
                var validatedAt = DateTime.UtcNow;
                var status = new LeaseStatus
                {
                    HolderDeviceId = parsedHolder,
                    LeaseId = parsedLease,
                    Version = parsedVersion,
                    ExpiresAtUtc = parsedExpires.ToUniversalTime(),
                    ValidatedAtUtc = validatedAt
                };
                leaseStatuses[sessionId] = status;
                return status.ExpiresAtUtc > validatedAt &&
                    parsedHolder == senderId &&
                    parsedLease == leaseId &&
                    parsedVersion == leaseVersion;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return false; }
            catch (JsonException) { return false; }
        }

        private async Task<Subscriber> GetSubscriberAsync(
            Guid sessionId,
            Guid deviceId,
            CancellationToken token,
            bool forceAuthorityRefresh = false)
        {
            if (forceAuthorityRefresh)
                await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
            if (subscribers.TryGetValue(sessionId, out var known) && known.TryGetValue(deviceId, out var existing))
            {
                lock (existing.Sync)
                {
                    if (!existing.Removed && existing.KeyEpoch == config.KeyEpoch) return existing;
                }
                RemoveSubscriber(sessionId, deviceId, existing);
            }

            await RefreshAuthorizedRecipientsAsync(false, token).ConfigureAwait(false);
            if (!historyRecipients.TryGetValue(deviceId, out var authorized))
            {
                await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
                if (!historyRecipients.TryGetValue(deviceId, out authorized))
                    throw new InvalidDataException("Subscriber does not have an active PC grant.");
            }
            byte[] publicKey;
            long keyEpoch;
            lock (authorized.Sync)
            {
                if (authorized.Removed || authorized.ExchangePublicKey == null || authorized.KeyEpoch < 1)
                    throw new InvalidDataException("Subscriber authority is unavailable.");
                publicKey = (byte[])authorized.ExchangePublicKey.Clone();
                keyEpoch = authorized.KeyEpoch;
            }
            return new Subscriber
            {
                DeviceId = deviceId,
                ExchangePublicKey = publicKey,
                PairwiseKey = SessionCrypto.DerivePairwiseKey(
                    credentials.Identity.Exchange,
                    publicKey,
                    credentials.DeviceId,
                    deviceId,
                    keyEpoch),
                KeyEpoch = keyEpoch,
                LastActivityUtc = DateTime.UtcNow
            };
        }

        private async Task<Subscriber> TryRestoreSubscriberAsync(
            Guid sessionId,
            Guid deviceId,
            CancellationToken token)
        {
            try
            {
                return RememberSubscriber(
                    sessionId,
                    await GetSubscriberAsync(sessionId, deviceId, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return null; }
            catch (HttpRequestException) { return null; }
            catch (JsonException) { return null; }
            catch (FormatException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        private async Task<bool> UploadHistoryAsync(Guid sessionId, IList<HistoryEventV1> events, CancellationToken token)
        {
            if (historyUploader == null) return false;
            IList<LocalFrame> retainedFrames;
            ulong ignoredEarliest;
            ulong ignoredLatest;
            if (registry != null && registry.TryReadBufferedEvents(sessionId, 0, out retainedFrames, out ignoredEarliest, out ignoredLatest))
            {
                var retainedEvents = new List<HistoryEventV1>();
                foreach (var frame in retainedFrames)
                {
                    var historyEvent = BuildHistoryEvent(frame);
                    if (historyEvent != null) retainedEvents.Add(historyEvent);
                }
                if (retainedEvents.Count > 0) events = retainedEvents;
            }
            // History objects must never be produced for a stale or revoked
            // recipient. Fetch a fresh PC-scoped Grant snapshot for every
            // archival attempt; the response also carries the authoritative
            // key epoch in the same database snapshot.
            await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
            var hasRecipient = false;
            var durable = true;
            foreach (var recipient in historyRecipients.Values)
            {
                long keyEpoch;
                var pairwiseKey = SnapshotPairwiseKey(recipient, out keyEpoch);
                if (pairwiseKey == null) continue;
                hasRecipient = true;
                try
                {
                    var outcome = await historyUploader.UploadAsync(
                        sessionId,
                        new ProductionHistoryRecipient
                        {
                            DeviceId = recipient.DeviceId,
                            PairwiseKey = pairwiseKey,
                            KeyEpoch = keyEpoch
                        },
                        events,
                        token).ConfigureAwait(false);
                    if (outcome != ProductionHistoryUploadOutcome.Durable) durable = false;
                }
                finally { CryptographicOperations.ZeroMemory(pairwiseKey); }
            }
            return hasRecipient && durable;
        }

        private async Task RefreshAuthorizedRecipientsAsync(bool force, CancellationToken token)
        {
            if (!force && DateTime.UtcNow < authorityExpiresUtc) return;
            await historyRecipientGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!force && DateTime.UtcNow < authorityExpiresUtc) return;
                using var response = await SendAsync(
                    HttpMethod.Get,
                    "/v1/pcs/" + config.PcId.ToString("D") + "/grants",
                    Array.Empty<byte>(),
                    true,
                    null,
                    token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("key_epoch", out var epochElement) ||
                    !epochElement.TryGetInt64(out var keyEpoch) || keyEpoch < 1 || keyEpoch < config.KeyEpoch)
                {
                    throw new InvalidDataException("PC Grant key epoch is invalid.");
                }
                var authorizedKeys = new Dictionary<Guid, byte[]>();
                var retained = new HashSet<Guid>();
                try
                {
                    foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
                    {
                        if (!string.Equals(item.GetProperty("device_type").GetString(), "ANDROID", StringComparison.Ordinal)) continue;
                        var deviceId = item.GetProperty("device_id").GetGuid();
                        var publicKey = Convert.FromBase64String(item.GetProperty("exchange_public_key").GetString());
                        if (publicKey.Length != 32 || deviceId == Guid.Empty)
                        {
                            CryptographicOperations.ZeroMemory(publicKey);
                            throw new InvalidDataException("PC Grant exchange key is invalid.");
                        }
                        if (!authorizedKeys.TryAdd(deviceId, publicKey))
                        {
                            CryptographicOperations.ZeroMemory(publicKey);
                            throw new InvalidDataException("PC Grant snapshot contains a duplicate device.");
                        }
                    }

                    if (keyEpoch > config.KeyEpoch)
                    {
                        var previousEpoch = config.KeyEpoch;
                        config.KeyEpoch = keyEpoch;
                        try { ProductionAgentConfig.Save(config); }
                        catch
                        {
                            config.KeyEpoch = previousEpoch;
                            throw;
                        }
                        ClearSubscribers();
                        foreach (var previous in historyRecipients.Values) RetireSubscriber(previous);
                        historyRecipients.Clear();
                    }

                    foreach (var item in authorizedKeys)
                    {
                        retained.Add(item.Key);
                        var publicKey = (byte[])item.Value.Clone();
                        var candidate = new Subscriber
                        {
                            DeviceId = item.Key,
                            ExchangePublicKey = publicKey,
                            PairwiseKey = SessionCrypto.DerivePairwiseKey(
                                credentials.Identity.Exchange,
                                publicKey,
                                credentials.DeviceId,
                                item.Key,
                                keyEpoch),
                            KeyEpoch = keyEpoch,
                            LastActivityUtc = DateTime.UtcNow
                        };
                        if (historyRecipients.TryGetValue(item.Key, out var existing) &&
                            SubscriberAuthorityMatches(existing, candidate))
                        {
                            RetireSubscriber(candidate);
                            continue;
                        }
                        historyRecipients[item.Key] = candidate;
                        if (existing != null) RetireSubscriber(existing);
                    }
                    foreach (var item in historyRecipients)
                    {
                        if (retained.Contains(item.Key)) continue;
                        if (historyRecipients.TryRemove(item.Key, out var removed)) RetireSubscriber(removed);
                    }
                    authorityExpiresUtc = DateTime.UtcNow.Add(AuthorityCacheDuration);
                }
                finally
                {
                    foreach (var publicKey in authorizedKeys.Values) CryptographicOperations.ZeroMemory(publicKey);
                }
            }
            finally { historyRecipientGate.Release(); }
        }

        private async Task ReplaySubscriberAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ulong resumeFromSequence,
            CancellationToken token)
        {
            // Do not publish the subscriber until the replay lock is held. A
            // concurrent live publisher can then never overtake this replay.
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                IList<LocalFrame> frames;
                ulong earliestRetainedSequence;
                ulong latestSequence;
                if (registry == null || !registry.TryReadBufferedEvents(
                    sessionId,
                    resumeFromSequence,
                    out frames,
                    out earliestRetainedSequence,
                    out latestSequence))
                {
                    throw new InvalidDataException("Subscribed session is no longer registered.");
                }
                if (resumeFromSequence > latestSequence)
                    throw new InvalidDataException("Subscribe cursor exceeds the session sequence.");
                // The publication cursor is global only as a batching hint;
                // never advance it from a single subscriber replay. Doing so
                // can make another subscriber skip durable frames that have
                // not yet reached its socket.
                subscriber = RememberSubscriber(sessionId, subscriber);
                if (!ResetSubscriberCursor(subscriber, resumeFromSequence))
                    throw new InvalidDataException("Subscribe cursor regressed below the durable acknowledgement.");

                var gapStart = resumeFromSequence == ulong.MaxValue
                    ? 0
                    : resumeFromSequence + 1;
                var gapEnd = 0UL;
                if (gapStart > 0 && earliestRetainedSequence > gapStart)
                {
                    gapEnd = earliestRetainedSequence - 1;
                }
                else if (gapStart > 0 && frames.Count == 0 && latestSequence >= gapStart)
                {
                    gapEnd = latestSequence;
                }
                if (gapEnd >= gapStart && gapStart > 0)
                {
                    var gapPayload = new GapPayload
                    {
                        Start = gapStart,
                        End = gapEnd,
                        ReasonCode = "LOCAL_BUFFER_GAP"
                    };
                    var gapBytes = gapPayload.ToByteArray();
                    try
                    {
                        if (gapEnd == ulong.MaxValue) throw new InvalidDataException("Gap sequence is exhausted.");
                        await SendEncryptedCoreAsync(
                            active,
                            sessionId,
                            subscriber,
                            MessageType.Gap,
                            Direction.PcToAndroid,
                            gapEnd + 1,
                            gapBytes,
                            token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(gapBytes); }

                    RemoveSubscriber(sessionId, subscriber.DeviceId, subscriber);

                    // Without a portable Snapshot, Android cannot safely apply
                    // the retained suffix after a permanent VT byte gap. Stop
                    // this replay after the explicit Gap instead of flooding a
                    // client that must remain in HistoryGap until a future
                    // snapshot-based recovery is available.
                    return;
                }

                foreach (var frame in frames)
                {
                    var messageType = MapMessageType(frame);
                    if (messageType == MessageType.Unspecified) continue;
                    var payload = BuildPayload(frame).ToByteArray();
                    try
                    {
                        var sent = await SendEncryptedCoreAsync(
                            active,
                            sessionId,
                            subscriber,
                            messageType,
                            Direction.PcToAndroid,
                            frame.Sequence,
                            payload,
                            token).ConfigureAwait(false);
                        if (!sent) break;
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }
            }
            finally { socketSendGate.Release(); }
            QueueHistoryUpload(sessionId, false);
        }

        private static byte[] GetCachedSubscriberFrameOrState(
            Subscriber subscriber,
            ulong sequence,
            out bool alreadyDelivered,
            out bool canEncrypt)
        {
            alreadyDelivered = false;
            canEncrypt = false;
            if (subscriber == null) return null;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed) return null;
                PruneSubscriberFramesLocked(subscriber);
                if (sequence <= subscriber.HighestAck)
                {
                    alreadyDelivered = true;
                    return null;
                }
                if (subscriber.FlowPaused ||
                    subscriber.SentFrames.Count >= subscriber.FlowWindowEvents ||
                    subscriber.InFlightWireBytes >= subscriber.FlowWindowWireBytes)
                {
                    // A frame that would exceed the absolute window remains
                    // in the durable Registry buffer for a later drain.
                    subscriber.DrainRequested = true;
                    alreadyDelivered = false;
                    return null;
                }
                if (subscriber.SentFrames.TryGetValue(sequence, out var existing))
                    return (byte[])existing.Clone();
                canEncrypt = true;
                return null;
            }
        }

        private static byte[] PrepareNewSubscriberFrame(Subscriber subscriber, ulong sequence, byte[] serialized)
        {
            if (subscriber == null || serialized == null || serialized.Length == 0) return null;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed) return null;
                PruneSubscriberFramesLocked(subscriber);
                if (sequence <= subscriber.HighestAck) return null;
                if (subscriber.FlowPaused ||
                    subscriber.SentFrames.Count >= subscriber.FlowWindowEvents ||
                    subscriber.InFlightWireBytes + (ulong)serialized.Length > subscriber.FlowWindowWireBytes)
                {
                    subscriber.DrainRequested = true;
                    return null;
                }
                if (subscriber.SentFrames.TryGetValue(sequence, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing, serialized))
                        throw new InvalidDataException("same sequence produced different ciphertext");
                    return (byte[])existing.Clone();
                }
                var retained = (byte[])serialized.Clone();
                subscriber.SentFrames.Add(sequence, retained);
                subscriber.InFlightWireBytes += (ulong)retained.Length;
                return (byte[])serialized.Clone();
            }
        }

        private static void PruneSubscriberFramesLocked(Subscriber subscriber)
        {
            while (subscriber.SentFrames.Count > 0)
            {
                var first = subscriber.SentFrames.First();
                if (first.Key > subscriber.HighestAck) break;
                subscriber.SentFrames.Remove(first.Key);
                subscriber.InFlightWireBytes = subscriber.InFlightWireBytes >= (ulong)first.Value.Length
                    ? subscriber.InFlightWireBytes - (ulong)first.Value.Length
                    : 0;
                CryptographicOperations.ZeroMemory(first.Value);
            }
        }

        private async Task<bool> SendEncryptedAsync(ClientWebSocket active, Guid sessionId, Subscriber subscriber, MessageType type, Direction direction, ulong sequence, byte[] plaintext, CancellationToken token)
        {
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await SendEncryptedCoreAsync(
                    active,
                    sessionId,
                    subscriber,
                    type,
                    direction,
                    sequence,
                    plaintext,
                    token).ConfigureAwait(false);
            }
            finally { socketSendGate.Release(); }
        }

        private async Task<bool> SendEncryptedCoreAsync(ClientWebSocket active, Guid sessionId, Subscriber subscriber, MessageType type, Direction direction, ulong sequence, byte[] plaintext, CancellationToken token)
        {
            var cached = GetCachedSubscriberFrameOrState(subscriber, sequence, out var alreadyDelivered, out var canEncrypt);
            if (alreadyDelivered) return true;
            if (!canEncrypt && cached == null) return false;
            if (cached != null)
            {
                try
                {
                    await active.SendAsync(cached, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                    RecordSubscriberSend(subscriber, sequence);
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(cached); }
            }

            long keyEpoch;
            var pairwiseKey = SnapshotPairwiseKey(subscriber, out keyEpoch);
            if (pairwiseKey == null) return false;
            var keyId = EpochKeyId(keyEpoch); var cryptoDirection = direction == Direction.PcToAndroid ? CryptoDirection.PcToAndroid : CryptoDirection.AndroidToPc;
            var key = SessionCrypto.DeriveSessionKey(pairwiseKey, sessionId, cryptoDirection, keyId);
            CryptographicOperations.ZeroMemory(pairwiseKey);
            var nonce = NewEnvelopeNonce();
            try
            {
                // The first serialized Envelope for a logical sequence is
                // retained per subscriber until ACK. Replays therefore use
                // byte-identical ciphertext, allowing the Relay to reject a
                // same-sequence/different-ciphertext substitution.
                var envelope = new Envelope { ProtocolVersion = 1, MessageType = type, SessionId = ByteString.CopyFrom(GuidBytes(sessionId)), SenderDeviceId = ByteString.CopyFrom(GuidBytes(credentials.DeviceId)), RecipientDeviceId = ByteString.CopyFrom(GuidBytes(subscriber.DeviceId)), Direction = direction, Seq = sequence, SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), KeyId = ByteString.CopyFrom(keyId), Nonce = ByteString.CopyFrom(nonce) };
                var ciphertext = SessionCrypto.Encrypt(key, nonce, SessionCrypto.AssociatedData(envelope), plaintext);
                try
                {
                    envelope.Ciphertext = ByteString.CopyFrom(ciphertext);
                }
                finally { CryptographicOperations.ZeroMemory(ciphertext); }
                var serialized = envelope.ToByteArray();
                try
                {
                    var sendBytes = PrepareNewSubscriberFrame(subscriber, sequence, serialized);
                    if (sendBytes == null) return false;
                    try
                    {
                        await active.SendAsync(sendBytes, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(sendBytes); }
                    RecordSubscriberSend(subscriber, sequence);
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(serialized); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(keyId);
            }
        }

        private static byte[] NewEnvelopeNonce()
        {
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            return nonce;
        }

        private static byte[] EpochKeyId(long keyEpoch)
        {
            if (keyEpoch < 1) throw new InvalidDataException("Key epoch is invalid.");
            return Encoding.UTF8.GetBytes("epoch-" + keyEpoch.ToString("D8"));
        }

        private async Task SendWebSocketAsync(ClientWebSocket active, byte[] payload, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token)
        {
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try { await active.SendAsync(payload, messageType, endOfMessage, token).ConfigureAwait(false); }
            finally { socketSendGate.Release(); }
        }

        private async Task CloseWebSocketAsync(ClientWebSocket active, CancellationToken token)
        {
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try { await active.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent reconnect", token).ConfigureAwait(false); }
            finally { socketSendGate.Release(); }
        }

        private static MessageType MapMessageType(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Output) return MessageType.OutputChunk;
            if (frame.Type == LocalFrameType.Resize) return MessageType.ResizeApplied;
            if (frame.Type == LocalFrameType.Gap) return MessageType.Gap;
            if (frame.Type == LocalFrameType.Lifecycle)
            {
                var lifecycle = LocalPayloadCodec.ParseLifecycle(frame.Payload, out var ignoredExitCode);
                if (lifecycle == LocalLifecycleState.Running) return MessageType.SessionStarted;
                if (lifecycle == LocalLifecycleState.Interrupted) return MessageType.SessionInterrupted;
                if (lifecycle == LocalLifecycleState.Ended) return MessageType.SessionEnded;
                return MessageType.SessionUpsert;
            }
            if (frame.Type == LocalFrameType.LocalInputObserved) return MessageType.LeaseRevoked;
            if (frame.Type == LocalFrameType.RemoteInputResult)
            {
                LocalRemoteInputResult result;
                LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out result);
                return result == LocalRemoteInputResult.Accepted
                    ? MessageType.InputAccepted
                    : MessageType.InputRejected;
            }
            return MessageType.Unspecified;
        }

        private static IMessage BuildPayload(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Output) return new OutputChunkPayload { Output = ByteString.CopyFrom(frame.Payload) };
            if (frame.Type == LocalFrameType.Resize)
            {
                LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                return new ResizePayload { Columns = (uint)columns, Rows = (uint)rows };
            }
            if (frame.Type == LocalFrameType.Gap)
            {
                LocalPayloadCodec.ParseGap(frame.Payload, out var start, out var end);
                return new GapPayload { Start = start, End = end, ReasonCode = "LOCAL_BUFFER_GAP" };
            }
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                return new LeasePayload
                {
                    ReasonCode = "LOCAL_INPUT_PRIORITY",
                    LocalInputEpoch = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload)
                };
            }
            if (frame.Type == LocalFrameType.RemoteInputResult)
            {
                LocalRemoteInputResult result;
                var inputId = LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out result);
                return new InputResultPayload
                {
                    InputId = ByteString.CopyFrom(GuidBytes(inputId)),
                    Accepted = result == LocalRemoteInputResult.Accepted,
                    ReasonCode = InputResultReason(result)
                };
            }
            if (frame.Type == LocalFrameType.Lifecycle)
            {
                var lifecycle = LocalPayloadCodec.ParseLifecycle(frame.Payload, out var exitCode);
                var state = lifecycle == LocalLifecycleState.Ended ? "ENDED" :
                    lifecycle == LocalLifecycleState.Interrupted ? "INTERRUPTED" : "RUNNING";
                var payload = new SessionUpsertPayload { Lifecycle = state };
                if (lifecycle == LocalLifecycleState.Ended) payload.ExitCode = exitCode;
                return payload;
            }
            return new SessionUpsertPayload { Lifecycle = "changed" };
        }

        private static HistoryEventV1 BuildHistoryEvent(LocalFrame frame)
        {
            var type = MapMessageType(frame);
            if (type == MessageType.Unspecified) return null;
            var payload = BuildPayload(frame).ToByteArray();
            try
            {
                return new HistoryEventV1 { Seq = frame.Sequence, MessageType = type, Payload = ByteString.CopyFrom(payload) };
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }

        private static string InputResultReason(LocalRemoteInputResult result)
        {
            if (result == LocalRemoteInputResult.Accepted) return string.Empty;
            if (result == LocalRemoteInputResult.LocalInputSuperseded) return "LOCAL_INPUT_PRIORITY";
            if (result == LocalRemoteInputResult.ConPtyClosed) return "SESSION_INPUT_CLOSED";
            if (result == LocalRemoteInputResult.InputSequenceRejected) return "INPUT_SEQUENCE_REJECTED";
            if (result == LocalRemoteInputResult.ControlLeaseRejected) return "CONTROL_LEASE_REJECTED";
            if (result == LocalRemoteInputResult.SessionInputUnavailable) return "SESSION_INPUT_UNAVAILABLE";
            return "CONPTY_WRITE_FAILED";
        }

        private static byte[] SnapshotPairwiseKey(Subscriber subscriber, out long keyEpoch)
        {
            keyEpoch = 0;
            if (subscriber == null) return null;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || subscriber.PairwiseKey == null || subscriber.KeyEpoch < 1) return null;
                keyEpoch = subscriber.KeyEpoch;
                return (byte[])subscriber.PairwiseKey.Clone();
            }
        }

        private static bool MatchesExchangePublicKey(Subscriber subscriber, ReadOnlySpan<byte> publicKey)
        {
            lock (subscriber.Sync)
            {
                return !subscriber.Removed && subscriber.ExchangePublicKey != null &&
                    CryptographicOperations.FixedTimeEquals(subscriber.ExchangePublicKey, publicKey);
            }
        }

        private static bool SubscriberAuthorityMatches(Subscriber left, Subscriber right)
        {
            lock (left.Sync)
            lock (right.Sync)
            {
                return !left.Removed && !right.Removed && left.KeyEpoch == right.KeyEpoch &&
                    left.ExchangePublicKey != null && right.ExchangePublicKey != null &&
                    CryptographicOperations.FixedTimeEquals(left.ExchangePublicKey, right.ExchangePublicKey);
            }
        }

        private static void TouchSubscriber(Subscriber subscriber)
        {
            lock (subscriber.Sync)
            {
                if (!subscriber.Removed) subscriber.LastActivityUtc = DateTime.UtcNow;
            }
        }

        private static bool ResetSubscriberCursor(Subscriber subscriber, ulong resumeFromSequence)
        {
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || resumeFromSequence < subscriber.HighestAck) return false;
                subscriber.HighestAck = resumeFromSequence;
                PruneSubscriberFramesLocked(subscriber);
                if (subscriber.LastSentSequence < resumeFromSequence)
                    subscriber.LastSentSequence = resumeFromSequence;
                subscriber.LastActivityUtc = DateTime.UtcNow;
                return true;
            }
        }

        private static void RecordSubscriberSend(Subscriber subscriber, ulong sequence)
        {
            lock (subscriber.Sync)
            {
                if (subscriber.Removed) return;
                if (sequence > subscriber.LastSentSequence) subscriber.LastSentSequence = sequence;
            }
        }

        private static bool TryAdvanceSubscriberAck(Subscriber subscriber, ulong highestContiguousSequence)
        {
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || highestContiguousSequence < subscriber.HighestAck ||
                    highestContiguousSequence > subscriber.LastSentSequence) return false;
                subscriber.HighestAck = highestContiguousSequence;
                PruneSubscriberFramesLocked(subscriber);
                subscriber.LastActivityUtc = DateTime.UtcNow;
                return true;
            }
        }

        private static bool TryApplyFlowControl(Subscriber subscriber, FlowControlPayload flow)
        {
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || flow.HighestContiguousSeq < subscriber.HighestAck ||
                    flow.HighestContiguousSeq > subscriber.LastSentSequence ||
                    flow.ControlEpoch < subscriber.FlowControlEpoch)
                    return false;
                var paused = flow.Paused || flow.MaxUnackedEvents == 0 || flow.MaxUnackedWireBytes == 0;
                if (flow.ControlEpoch == subscriber.FlowControlEpoch)
                {
                    return subscriber.HighestAck == flow.HighestContiguousSeq &&
                        subscriber.FlowPaused == paused &&
                        subscriber.FlowWindowEvents == (int)flow.MaxUnackedEvents &&
                        subscriber.FlowWindowWireBytes == flow.MaxUnackedWireBytes;
                }
                subscriber.HighestAck = flow.HighestContiguousSeq;
                subscriber.FlowControlEpoch = flow.ControlEpoch;
                subscriber.FlowPaused = paused;
                subscriber.FlowWindowEvents = (int)flow.MaxUnackedEvents;
                subscriber.FlowWindowWireBytes = flow.MaxUnackedWireBytes;
                PruneSubscriberFramesLocked(subscriber);
                subscriber.LastActivityUtc = DateTime.UtcNow;
                return true;
            }
        }

        private static bool TakeSubscriberDrainRequest(Subscriber subscriber)
        {
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || subscriber.FlowPaused || !subscriber.DrainRequested ||
                    subscriber.SentFrames.Count >= subscriber.FlowWindowEvents ||
                    subscriber.InFlightWireBytes >= subscriber.FlowWindowWireBytes)
                    return false;
                subscriber.DrainRequested = false;
                return true;
            }
        }

        private bool RemoveSubscriber(Guid sessionId, Guid deviceId, Subscriber expected)
        {
            if (!subscribers.TryGetValue(sessionId, out var sessionSubscribers)) return false;
            var removed = ((ICollection<KeyValuePair<Guid, Subscriber>>)sessionSubscribers).Remove(
                new KeyValuePair<Guid, Subscriber>(deviceId, expected));
            if (!removed) return false;
            RetireSubscriber(expected);
            if (sessionSubscribers.IsEmpty)
            {
                ((ICollection<KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>>)subscribers).Remove(
                    new KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>(sessionId, sessionSubscribers));
            }
            return true;
        }

        private static void RetireSubscriber(Subscriber subscriber)
        {
            if (subscriber == null) return;
            byte[] exchangePublicKey;
            byte[] pairwiseKey;
            List<byte[]> sentFrames;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed) return;
                subscriber.Removed = true;
                exchangePublicKey = subscriber.ExchangePublicKey;
                pairwiseKey = subscriber.PairwiseKey;
                subscriber.ExchangePublicKey = null;
                subscriber.PairwiseKey = null;
                sentFrames = subscriber.SentFrames.Values.ToList();
                subscriber.SentFrames.Clear();
                subscriber.InFlightWireBytes = 0;
            }
            if (exchangePublicKey != null) CryptographicOperations.ZeroMemory(exchangePublicKey);
            if (pairwiseKey != null) CryptographicOperations.ZeroMemory(pairwiseKey);
            foreach (var frame in sentFrames) CryptographicOperations.ZeroMemory(frame);
        }

        private void PruneSubscribers()
        {
            var cutoff = DateTime.UtcNow.Subtract(SubscriberIdleTimeout);
            foreach (var session in subscribers)
            {
                foreach (var item in session.Value)
                {
                    var expired = false;
                    lock (item.Value.Sync)
                    {
                        expired = item.Value.Removed || item.Value.LastActivityUtc <= cutoff;
                    }
                    if (expired) RemoveSubscriber(session.Key, item.Key, item.Value);
                }
            }
        }

        private void ClearSubscribers()
        {
            foreach (var session in subscribers.Values)
            {
                foreach (var subscriber in session.Values)
                {
                    RetireSubscriber(subscriber);
                }
                session.Clear();
            }
            subscribers.Clear();
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, bool deviceProof, string idempotencyKey, CancellationToken token)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var accessToken = await GetAccessTokenAsync(attempt > 0, token).ConfigureAwait(false);
                using (var request = new HttpRequestMessage(method, config.ServerUrl + path))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    if (idempotencyKey != null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                    if (deviceProof) AddProofHeaders(request.Headers, method, path, body);
                    if (body.Length > 0)
                    {
                        request.Content = new ByteArrayContent(body);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    }
                    var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                    {
                        return response;
                    }
                    response.Dispose();
                }
            }
            throw new ProductionReauthenticationRequiredException();
        }

        private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken token)
        {
            if (!forceRefresh && !credentials.ShouldRefresh(DateTime.UtcNow, TimeSpan.FromMinutes(1)))
                return credentials.GetAccessToken();
            if (!credentials.CanRefresh || string.IsNullOrWhiteSpace(config.OIDCIssuer) || string.IsNullOrWhiteSpace(config.OIDCClientId))
            {
                if (forceRefresh) throw new ProductionReauthenticationRequiredException();
                return credentials.GetAccessToken();
            }
            await tokenRefreshGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && !credentials.ShouldRefresh(DateTime.UtcNow, TimeSpan.FromMinutes(1)))
                    return credentials.GetAccessToken();
                if (string.IsNullOrWhiteSpace(tokenEndpoint))
                {
                    using var discovery = await http.GetAsync(
                        config.OIDCIssuer.TrimEnd('/') + "/.well-known/openid-configuration",
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);
                    discovery.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(
                        await discovery.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    var endpoint = document.RootElement.GetProperty("token_endpoint").GetString();
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException("OIDC token endpoint is invalid.");
                    tokenEndpoint = endpoint;
                }
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = config.OIDCClientId,
                        ["refresh_token"] = credentials.GetRefreshToken()
                    })
                };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new ProductionReauthenticationRequiredException();
                response.EnsureSuccessStatusCode();
                using var tokens = JsonDocument.Parse(
                    await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                var accessToken = tokens.RootElement.GetProperty("access_token").GetString();
                var expiresIn = tokens.RootElement.GetProperty("expires_in").GetInt32();
                var refreshToken = tokens.RootElement.TryGetProperty("refresh_token", out var refreshed)
                    ? refreshed.GetString()
                    : credentials.GetRefreshToken();
                if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken) || expiresIn < 30)
                    throw new InvalidDataException("OIDC refresh response is incomplete.");
                credentials.UpdateTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn));
                return accessToken;
            }
            finally { tokenRefreshGate.Release(); }
        }

        private void AddProofHeaders(HttpRequestHeaders headers, HttpMethod method, string target, byte[] body)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(); var nonce = RandomNonce(); var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(); var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", method.Method.ToUpperInvariant(), target, timestamp, nonce, hash); headers.TryAddWithoutValidation("X-TermLink-Device", credentials.DeviceId.ToString("D")); headers.TryAddWithoutValidation("X-TermLink-Timestamp", timestamp); headers.TryAddWithoutValidation("X-TermLink-Nonce", nonce); headers.TryAddWithoutValidation("X-TermLink-Signature", Base64Url(credentials.Identity.Sign(Encoding.UTF8.GetBytes(canonical))));
        }
        private void AddProofHeaders(ClientWebSocketOptions options, HttpMethod method, string target, byte[] body, string accessToken) { var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(); var nonce = RandomNonce(); var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(); var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", method.Method.ToUpperInvariant(), target, timestamp, nonce, hash); options.SetRequestHeader("Authorization", "Bearer " + accessToken); options.SetRequestHeader("X-TermLink-Device", credentials.DeviceId.ToString("D")); options.SetRequestHeader("X-TermLink-Timestamp", timestamp); options.SetRequestHeader("X-TermLink-Nonce", nonce); options.SetRequestHeader("X-TermLink-Signature", Base64Url(credentials.Identity.Sign(Encoding.UTF8.GetBytes(canonical)))); }
        private static string Base64Url(byte[] value) { return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        private static string RandomNonce() { var value = new byte[16]; RandomNumberGenerator.Fill(value); return Base64Url(value); }
        private static byte[] GuidBytes(Guid value) { return Convert.FromHexString(value.ToString("N")); }
        private static Guid GuidFromBytes(ReadOnlySpan<byte> value) { if (value.Length != 16) throw new InvalidDataException("UUID must contain 16 bytes."); return Guid.ParseExact(Convert.ToHexString(value), "N"); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            SetRelayState(ProductionRelayState.Stopped, string.Empty);
            cancellation.Cancel();
            pendingSessions.Clear();

            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            if (active != null) active.Dispose();

            Task activeWorker;
            Task activeHistoryWorker;
            Task activeStatusRefresh;
            lock (lifecycleSync)
            {
                activeWorker = worker;
                activeHistoryWorker = historyWorker;
                activeStatusRefresh = statusRefreshWorker;
            }
            if (activeWorker != null)
            {
                try { activeWorker.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeHistoryWorker != null)
            {
                try { activeHistoryWorker.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeStatusRefresh != null)
            {
                try { activeStatusRefresh.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { }
                catch (ObjectDisposedException) { }
            }

            http.Dispose();
            credentials.Dispose();
            if (historyUploader != null) historyUploader.Dispose();
            ClearSubscribers();
            foreach (var recipient in historyRecipients.Values)
            {
                RetireSubscriber(recipient);
            }
            historyRecipients.Clear();
            pendingHistorySessions.Clear();
            terminalHistorySessions.Clear();
            publishedSequences.Clear();
            localInputEpochs.Clear();
            inputSequences.Clear();
            eventSignal.Dispose();
            historySignal.Dispose();
            socketSendGate.Dispose();
            historyRecipientGate.Dispose();
            statusRefreshGate.Dispose();
            tokenRefreshGate.Dispose();
            cancellation.Dispose();
        }
    }
}

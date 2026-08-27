using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.UpdateCore;
using CoreTerminalInput = TermLink.TerminalCore.TerminalInput;
using CoreTerminalInputModifiers = TermLink.TerminalCore.TerminalInputModifiers;
using CoreTerminalKey = TermLink.TerminalCore.TerminalKey;

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
        // Android accepts a durable Snapshot only while its realtime suffix is
        // no larger than one normal flow-control window.  Keep the REST
        // directory cursor inside that same bound while a newer Snapshot is
        // being sealed and committed.
        private const ulong MaxTailFirstSnapshotSuffixEvents = DefaultFlowWindowEvents;
        private const ulong DefaultFlowWindowWireBytes = 3UL << 20;
        private const int MaxFlowWindowEvents = 4096;
        private const ulong MaxFlowWindowWireBytes = 8UL << 20;
        private const int MaxRealtimeEnvelopeBytes = 1024 * 1024;
        private static readonly TimeSpan MetadataUpsertCadence = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SessionMetadataHeartbeatInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PCHeartbeatInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RealtimeHandshakeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RealtimeSendTimeout = TimeSpan.FromSeconds(15);
        private const int MaxRememberedTerminationRequests = 4096;
        private const string TerminalSnapshotEnvironment = "TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V1";
        private const string TypedInputEnvironment = "TERMLINK_ENABLE_TYPED_INPUT_V1";
        private sealed class CommittedEvent { public ManagedShellType ShellType; public LocalFrame Frame; }
        private sealed class PendingSession { public ManagedShellType ShellType; }
        private sealed class SessionPublicationState
        {
            public readonly object Sync = new object();
            public readonly SemaphoreSlim RestUpsertGate = new SemaphoreSlim(1, 1);
            public ManagedShellType ShellType;
            public bool HasSize;
            public int Columns;
            public int Rows;
            public string Title = string.Empty;
            public LocalLifecycleState Lifecycle = LocalLifecycleState.Running;
            public int? ExitCode;
            public ulong LastOutputSequence;
            public ulong LastEventSequence;
            public DateTimeOffset? EndedAt;
            public bool RestDirty;
            public ulong RestRevision;
            public DateTime LastRestAttemptUtc;
            public DateTime LastRestConfirmedUtc;
            public ulong LastRestConfirmedOutputSequence;
            public ulong TailFirstSnapshotPublicationSequence;
            public string SnapshotStatus;
            public string SnapshotReasonCode;
        }
        private sealed class SessionPublicationSnapshot
        {
            public Guid SessionId;
            public ManagedShellType ShellType;
            public bool HasSize;
            public int Columns;
            public int Rows;
            public string Title;
            public byte[] TitleCipher;
            public ulong RestRevision;
            public LocalLifecycleState Lifecycle;
            public int? ExitCode;
            public ulong LastOutputSequence;
            public ulong LastEventSequence;
            public DateTimeOffset? EndedAt;
            public string SnapshotStatus;
            public string SnapshotReasonCode;
        }
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
            public bool ReplayInProgress;
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
        private readonly ProductionReconnectPreferences reconnectPreferences;
        private readonly HttpClient http;
        private readonly HttpClient fileUploadHttp;
        // Frames are durably committed to each SessionAgent DPAPI buffer before
        // OnCommitted is invoked. Keep only bounded session metadata here and
        // let the publisher replay from that durable source, so a slow Relay
        // cannot block ConPTY and cannot silently drop a committed frame.
        private readonly ConcurrentDictionary<Guid, PendingSession> pendingSessions = new ConcurrentDictionary<Guid, PendingSession>();
        private readonly ConcurrentDictionary<Guid, ulong> publishedSequences = new ConcurrentDictionary<Guid, ulong>();
        private readonly ConcurrentDictionary<Guid, SessionPublicationState> publicationStates = new ConcurrentDictionary<Guid, SessionPublicationState>();
        private readonly SemaphoreSlim eventSignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim metadataSignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim socketSendGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>> subscribers = new ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Subscriber>>();
        // Route state survives active subscription teardown for the lifetime
        // of one PC Relay connection. This preserves byte-identical cached
        // ciphertext for an unacknowledged sequence after idle/unsubscribe.
        private readonly ConcurrentDictionary<string, Subscriber> connectionSubscriberRoutes = new ConcurrentDictionary<string, Subscriber>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, Subscriber> authorizedRecipients = new ConcurrentDictionary<Guid, Subscriber>();
        private readonly ConcurrentDictionary<Guid, byte> pendingHistorySessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte> terminalHistorySessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<LocalFrame>> pendingHistoryFrames = new ConcurrentDictionary<Guid, ConcurrentQueue<LocalFrame>>();
        private readonly ConcurrentDictionary<Guid, byte> historyReconcileSessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, string> terminalHistoryPersistenceFailures = new ConcurrentDictionary<Guid, string>();
        private readonly SemaphoreSlim historySignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim authorizedRecipientGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim statusRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim tokenRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim reconnectSignal = new SemaphoreSlim(0, 1);
        private readonly ConcurrentDictionary<Guid, LeaseStatus> leaseStatuses = new ConcurrentDictionary<Guid, LeaseStatus>();
        private readonly ConcurrentDictionary<Guid, ulong> localInputEpochs = new ConcurrentDictionary<Guid, ulong>();
        private readonly ConcurrentDictionary<string, ulong> inputSequences = new ConcurrentDictionary<string, ulong>();
        private readonly ConcurrentDictionary<Guid, Guid> terminationRequests = new ConcurrentDictionary<Guid, Guid>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ProductionTerminalHistoryStore terminalHistoryStore;
        private readonly ProductionTerminalHistoryProducer terminalHistoryProducer;
        private readonly bool terminalSnapshotEnabled;
        private readonly bool typedInputEnabled;
        private static readonly TermLink.TerminalCore.VtTerminalInputEncoder TypedInputEncoder =
            new TermLink.TerminalCore.VtTerminalInputEncoder();
        private readonly object lifecycleSync = new object();
        private readonly object statusSync = new object();
        private SessionRegistry registry;
        private Task worker;
        private Task historyWorker;
        private Task metadataWorker;
        private ClientWebSocket socket;
        private ProductionHistoryUploader historyUploader;
        private ProductionFileOperationWorker fileOperationWorker;
        private ProductionAgentUpdateWorker agentUpdateWorker;
        private DateTime authorityExpiresUtc;
        private DateTime lastPCUpsertAttemptUtc;
        private DateTime statusExpiresUtc;
        private ProductionRelayState relayState = ProductionRelayState.Connecting;
        private string relayErrorCode = string.Empty;
        private DateTime? lastConnectedAtUtc;
        private IList<ProductionDeviceSummary> productionDevices = new List<ProductionDeviceSummary>();
        private PairingChallengeSummary pairingChallenge;
        private Task statusRefreshWorker;
        private string tokenEndpoint;
        private int disposed;
        private int reauthenticationRequired;
        private long successfulConnectionGeneration;

        private ProductionAgentRuntime(ProductionAgentConfig config, ProductionCredentials credentials)
            : this(config, credentials, new ProductionReconnectPreferences())
        {
        }

        private ProductionAgentRuntime(
            ProductionAgentConfig config,
            ProductionCredentials credentials,
            ProductionReconnectPreferences reconnectPreferences)
        {
            this.config = config;
            this.credentials = credentials;
            this.reconnectPreferences = reconnectPreferences ??
                throw new ArgumentNullException(nameof(reconnectPreferences));
            this.reconnectPreferences.Validate();
            http = ProductionTls.CreateHttpClient(config.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
            fileUploadHttp = ProductionTls.CreateHttpClient(config.ServerUrl);
            // A selected artifact can be several GiB. Its presigned upload is
            // governed by cancellation and the server expiry instead of the
            // short REST metadata timeout.
            fileUploadHttp.Timeout = Timeout.InfiniteTimeSpan;
            terminalSnapshotEnabled = IsTerminalSnapshotRuntimeEnabled(
                Environment.GetEnvironmentVariable(TerminalSnapshotEnvironment));
            typedInputEnabled = IsTypedInputRuntimeEnabled(
                Environment.GetEnvironmentVariable(TypedInputEnvironment));
            terminalHistoryStore = new ProductionTerminalHistoryStore(
                ProductionTerminalHistoryRoot(config.PcId),
                credentials.DeviceId);
            terminalHistoryProducer = new ProductionTerminalHistoryProducer(
                terminalHistoryStore,
                credentials.DeviceId);
        }

        private static string ProductionTerminalHistoryRoot(Guid pcId)
        {
            if (pcId == Guid.Empty) throw new ArgumentException("PC ID is required.", nameof(pcId));
            return Path.Combine(
                Path.GetDirectoryName(ProductionAgentConfig.DefaultPath),
                "production-history",
                pcId.ToString("N"));
        }

        // Tail-first history is part of the installed product contract. Keep a
        // narrowly scoped emergency rollback for diagnostics, but do not make
        // normal Tray/installer startup depend on a machine environment value.
        internal static bool IsTerminalSnapshotRuntimeEnabled(string value) =>
            !string.Equals(value, "0", StringComparison.Ordinal);

        internal static bool IsTypedInputRuntimeEnabled(string value) =>
            !string.Equals(value, "0", StringComparison.Ordinal);

        internal static string NormalizeServerOrigin(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ||
                (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidDataException("Production Server origin is invalid.");
            }
            var origin = new UriBuilder(
                server.Scheme.ToLowerInvariant(),
                server.IdnHost,
                server.IsDefaultPort ? -1 : server.Port).Uri;
            return origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/');
        }

        internal static byte[] BuildHelloPayload(bool terminalSnapshotEnabled) =>
            BuildHelloPayloadWithFeatures(terminalSnapshotEnabled, true);

        internal static byte[] BuildHelloPayloadWithFeatures(bool terminalSnapshotEnabled, bool typedInputEnabled)
        {
            var capabilities = new List<string>
            {
                "cumulative_ack",
                "e2ee_x25519_chacha20poly1305",
                "flow_control_v1",
                "lease_v1",
                "bounded_event_replay_v1",
                "terminal_presentation_metadata_v1",
                "remote_agent_update_v1"
            };
            if (terminalSnapshotEnabled) capabilities.Add("terminal_snapshot_v1");
            if (typedInputEnabled) capabilities.Add("terminal_typed_input_v1");
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                role = "pc",
                protocol_versions = new[] { 1 },
                capabilities
            });
        }

        internal static void ValidateRealtimeEnvelopeSize(int serializedLength)
        {
            if (serializedLength < 1 || serializedLength >= MaxRealtimeEnvelopeBytes)
                throw new InvalidDataException("Realtime Envelope must be smaller than 1 MiB.");
        }

        internal static ProductionTerminalHistoryProductionResult ProcessTerminalHistoryIfEnabled(
            bool terminalSnapshotEnabled,
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            if (!terminalSnapshotEnabled) return null;
            if (producer == null) throw new ArgumentNullException(nameof(producer));
            return producer.ProcessSession(sessionId);
        }

        public static ProductionAgentRuntime TryCreate()
        {
            var config = ProductionAgentConfig.TryLoad(); if (config == null) return null;
            var runtimeVersion = typeof(ProductionAgentRuntime).Assembly
                .GetName().Version?.ToString() ?? "unknown";
            if (!string.Equals(config.AgentVersion, runtimeVersion, StringComparison.Ordinal))
            {
                // Runtime metadata must reflect the binary actually serving the
                // account, not the version captured at first enrollment. Keep
                // this in memory: local developer builds must not rewrite a
                // user's durable account file merely by being executed.
                config.AgentVersion = runtimeVersion;
            }
            var credentials = ProductionCredentials.TryLoad(); if (credentials == null) return null;
            return new ProductionAgentRuntime(
                config,
                credentials,
                ProductionReconnectPreferences.Load());
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
                QueuePersistedHistoryForReplay();
                historyUploader = new ProductionHistoryUploader(
                    http,
                    SendAsync,
                    credentials.DeviceId,
                    terminalHistoryStore,
                    terminalSnapshotEnabled);
                fileOperationWorker = new ProductionFileOperationWorker(
                    new ProductionFileOperationApi(config.PcId, fileUploadHttp, SendAsync),
                    new DirectoryListingService(),
                    new FileTransferPreparer(),
                    new ManagedTerminalLauncher(),
                    cancellation.Token,
                    () => registry.RemoteAccessEnabled,
                    sessionId => registry.ContainsSession(sessionId),
                    sessionId => registry.TerminateSession(sessionId));
                fileOperationWorker.Start();
                agentUpdateWorker = new ProductionAgentUpdateWorker(
                    new ProductionAgentUpdateApi(config.PcId, SendAsync),
                    config.PcId,
                    config.AgentVersion,
                    WindowsUpdateVersion.GetBuild(typeof(ProductionAgentRuntime).Assembly.GetName().Version),
                    cancellation.Token,
                    () => registry.GetSummaries().Select(value => value.SessionId).ToList(),
                    sessionId => registry.TerminateSession(sessionId),
                    () =>
                    {
                        lock (statusSync) return relayState == ProductionRelayState.Connected;
                    });
                agentUpdateWorker.Start();
                historyWorker = Task.Run(HistoryUploadLoopAsync);
                metadataWorker = Task.Run(MetadataUpsertLoopAsync);
                worker = Task.Run(RunAsync);
            }
        }

        private void QueuePersistedHistoryForReplay()
        {
            foreach (var sessionId in terminalHistoryStore.ListSessions())
            {
                historyReconcileSessions[sessionId] = 0;
                pendingHistorySessions[sessionId] = 0;
            }
            if (registry != null)
            {
                foreach (var session in registry.GetSummaries())
                {
                    historyReconcileSessions[session.SessionId] = 0;
                    pendingHistorySessions[session.SessionId] = 0;
                }
            }
            if (pendingHistorySessions.IsEmpty) return;
            try { historySignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
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
            // SessionAgent has already durably committed this frame before the
            // callback. Copy it into an in-memory handoff so Production WAL IO
            // and snapshot work can never delay Host ACK or realtime enqueue.
            var historyFrame = new LocalFrame(
                frame.Type,
                frame.SessionId,
                frame.Sequence,
                frame.Payload == null ? Array.Empty<byte>() : (byte[])frame.Payload.Clone());
            pendingHistoryFrames.GetOrAdd(
                frame.SessionId,
                _ => new ConcurrentQueue<LocalFrame>()).Enqueue(historyFrame);
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                localInputEpochs[frame.SessionId] = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
            }
            var publicationState = publicationStates.GetOrAdd(
                frame.SessionId,
                _ => new SessionPublicationState());
            ApplyPublicationFrame(
                publicationState,
                new CommittedEvent { ShellType = shellType, Frame = frame },
                DateTimeOffset.UtcNow);
            if (terminalSnapshotEnabled && frame.Type == LocalFrameType.Output)
            {
                StageTailFirstSnapshotPublication(publicationState, frame.Sequence);
            }
            try { metadataSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
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
            if (forceRefresh && !IsReauthenticationRequired)
            {
                try
                {
                    RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
                }
                catch (ProductionReauthenticationRequiredException)
                {
                    MarkReauthenticationRequired();
                }
            }
            else if (!IsReauthenticationRequired && DateTime.UtcNow >= statusExpiresUtc)
            {
                lock (lifecycleSync)
                {
                    if (Volatile.Read(ref disposed) == 0 && !IsReauthenticationRequired &&
                        (statusRefreshWorker == null || statusRefreshWorker.IsCompleted))
                    {
                        statusRefreshWorker = Task.Run(async () =>
                        {
                            try { await RefreshStatusAsync(cancellation.Token).ConfigureAwait(false); }
                            catch (ProductionReauthenticationRequiredException) { }
                            catch (OperationCanceledException) { }
                            catch (HttpRequestException) { }
                            catch (JsonException) { }
                        });
                    }
                }
            }
            return SnapshotStatus();
        }

        /// <summary>
        /// Interrupts the current socket attempt or retry delay without
        /// restarting the registry or disturbing any local SessionHost.
        /// </summary>
        public void RequestReconnect()
        {
            if (Volatile.Read(ref disposed) != 0 || IsReauthenticationRequired) return;
            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            try { active?.Abort(); }
            catch (ObjectDisposedException) { }
            try { reconnectSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
            SetRelayState(ProductionRelayState.Connecting, string.Empty);
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
        /// Compatibility operation for deployments that still expose explicit
        /// per-PC permissions. Production automatic-account authorization does
        /// not require this operation before an Android device can subscribe.
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
            ClearSubscriberRoutesForDevice(targetDeviceId);
            if (authorizedRecipients.TryRemove(targetDeviceId, out var authorizedRecipient))
                RetireSubscriber(authorizedRecipient);
            lock (statusSync)
            {
                if (pairingChallenge != null && pairingChallenge.TargetDeviceId == targetDeviceId) pairingChallenge = null;
            }
            statusExpiresUtc = DateTime.MinValue;
            RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
        }

        private async Task RunAsync()
        {
            var consecutiveFailures = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var connectionGenerationBeforeAttempt =
                    Interlocked.Read(ref successfulConnectionGeneration);
                try
                {
                    SetRelayState(ProductionRelayState.Connecting, string.Empty);
                    await EnsureRegisteredAsync(cancellation.Token).ConfigureAwait(false);
                    await ConnectAndPumpAsync(cancellation.Token).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (ProductionReauthenticationRequiredException)
                {
                    MarkReauthenticationRequired();
                    // An invalid/expired refresh credential cannot recover through
                    // periodic socket retries. Keep the actionable authentication
                    // state stable until the user explicitly retries or the login
                    // workflow replaces the runtime with fresh credentials.
                    await reconnectSignal.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (Exception error)
                {
                    if (Interlocked.Read(ref successfulConnectionGeneration) !=
                        connectionGenerationBeforeAttempt)
                    {
                        // A healthy socket generation resets the backoff even if
                        // it later disconnects by throwing from the pump.
                        consecutiveFailures = 0;
                    }
                    consecutiveFailures = checked(consecutiveFailures + 1);
                    SetRelayState(ProductionRelayState.Reconnecting, ConnectionFailureCode(error));
                    var jitterMilliseconds = RandomNumberGenerator.GetInt32(0, 500);
                    var manuallyRetried = await WaitForRetryOrReconnectAsync(
                        reconnectPreferences.DelayAfterFailure(consecutiveFailures) +
                            TimeSpan.FromMilliseconds(jitterMilliseconds),
                        cancellation.Token).ConfigureAwait(false);
                    if (manuallyRetried) consecutiveFailures = 0;
                }
            }
        }

        private async Task<bool> WaitForRetryOrReconnectAsync(TimeSpan delay, CancellationToken token)
        {
            var deadline = DateTime.UtcNow.Add(delay);
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                var slice = remaining > TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : remaining;
                if (await reconnectSignal.WaitAsync(slice, token).ConfigureAwait(false)) return true;
            }
        }

        private static string ConnectionFailureCode(Exception error)
        {
            if (error is ProductionServerResponseException response && !string.IsNullOrWhiteSpace(response.ErrorCode))
            {
                return "HTTP_" + ((int)response.StatusCode.Value).ToString() + "_" + response.ErrorCode;
            }
            var socket = FindInner<SocketException>(error);
            if (socket != null)
            {
                if (socket.SocketErrorCode == SocketError.HostNotFound ||
                    socket.SocketErrorCode == SocketError.NoData ||
                    socket.SocketErrorCode == SocketError.TryAgain)
                    return "DNS_RESOLUTION_FAILED";
                if (socket.SocketErrorCode == SocketError.ConnectionRefused)
                    return "CONNECTION_REFUSED";
                if (socket.SocketErrorCode == SocketError.TimedOut)
                    return "CONNECTION_TIMEOUT";
                return "NETWORK_CONNECTION_FAILED";
            }
            if (FindInner<AuthenticationException>(error) != null)
                return "TLS_CERTIFICATE_FAILED";
            if (error is TimeoutException || error is OperationCanceledException)
                return "CONNECTION_TIMEOUT";
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

        private static T FindInner<T>(Exception error) where T : Exception
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is T typed) return typed;
            }
            return null;
        }

        private async Task EnsureRegisteredAsync(CancellationToken token)
        {
            var response = await RegisterDeviceAsync("PC_AGENT", token).ConfigureAwait(false);
            var errorCode = response.IsSuccessStatusCode
                ? string.Empty
                : await ReadServerErrorCodeAsync(response, token).ConfigureAwait(false);
            if ((response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Forbidden) &&
                (errorCode == "INVALID_DEVICE_TYPE" || errorCode == "DEVICE_TYPE_NOT_ALLOWED"))
            {
                response.Dispose();
                response = await RegisterDeviceAsync("WINDOWS_PC", token).ConfigureAwait(false);
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw ReauthenticationRequired();
                }
                if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.Conflict)
                    await EnsureServerSuccessAsync(response, "DEVICE_REGISTER", token).ConfigureAwait(false);
            }
            lock (statusSync) lastPCUpsertAttemptUtc = DateTime.UtcNow;
            await UpsertPCAsync(token).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> RegisterDeviceAsync(string deviceType, CancellationToken token)
        {
            var registration = new Dictionary<string, object>
            {
                ["id"] = credentials.DeviceId, ["type"] = deviceType, ["name"] = config.PcName,
                ["signing_public_key"] = Base64Url(credentials.Identity.SigningPublicKey), ["exchange_public_key"] = Base64Url(credentials.Identity.Exchange.PublicKey),
                ["key_id"] = "device-key-" + credentials.DeviceId.ToString("D")
            };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var nonce = RandomNonce();
            var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", credentials.DeviceId.ToString("D"), deviceType, config.PcName,
                registration["signing_public_key"], registration["exchange_public_key"], registration["key_id"], now, nonce);
            registration["timestamp_ms"] = now; registration["nonce"] = nonce; registration["signature"] = Base64Url(credentials.Identity.Sign(Encoding.UTF8.GetBytes(canonical)));
            return await SendAsync(HttpMethod.Post, "/v1/devices/register", JsonSerializer.SerializeToUtf8Bytes(registration), false, null, token).ConfigureAwait(false);
        }

        internal static byte[] BuildPCUpsertBody(string pcName, string agentVersion, long keyEpoch)
        {
            if (string.IsNullOrWhiteSpace(pcName)) throw new ArgumentException("A PC name is required.", nameof(pcName));
            if (string.IsNullOrWhiteSpace(agentVersion)) throw new ArgumentException("An Agent version is required.", nameof(agentVersion));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = pcName,
                agent_version = agentVersion,
                key_epoch = keyEpoch,
                session_heartbeat_capable = true
            });
        }

        internal static byte[] BuildPCUpsertBodyWithCapabilities(
            string pcName,
            string agentVersion,
            long keyEpoch,
            string[] supportedShells)
        {
            if (string.IsNullOrWhiteSpace(pcName)) throw new ArgumentException("A PC name is required.", nameof(pcName));
            if (string.IsNullOrWhiteSpace(agentVersion)) throw new ArgumentException("An Agent version is required.", nameof(agentVersion));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            if (supportedShells == null) throw new ArgumentNullException(nameof(supportedShells));

            var normalizedShells = supportedShells
                .Select(value => value ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var shell in normalizedShells)
                ManagedTerminalLauncher.ValidateShellType(shell);

            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = pcName,
                agent_version = agentVersion,
                key_epoch = keyEpoch,
                session_heartbeat_capable = true,
                supported_shells = normalizedShells,
                remote_agent_update_capable = true
            });
        }

        internal static byte[] BuildPCUpsertBodyWithTerminalCommands(
            string pcName,
            string agentVersion,
            long keyEpoch,
            string[] supportedShells,
            TerminalCommandCapability[] terminalCommands)
        {
            if (terminalCommands == null) throw new ArgumentNullException(nameof(terminalCommands));
            _ = BuildPCUpsertBodyWithCapabilities(pcName, agentVersion, keyEpoch, supportedShells);
            var normalizedShells = supportedShells
                .Select(value => value ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (terminalCommands.Length > 24 ||
                terminalCommands.Select(command => command?.Id).Distinct(StringComparer.Ordinal).Count() != terminalCommands.Length)
                throw new ArgumentException("Terminal command capabilities must be unique and bounded.", nameof(terminalCommands));
            var normalizedCommands = terminalCommands.Select(command =>
            {
                if (command == null || string.IsNullOrWhiteSpace(command.Id) ||
                    string.IsNullOrWhiteSpace(command.DisplayName) ||
                    string.IsNullOrWhiteSpace(command.ShellType) ||
                    (command.Source != "detected" && command.Source != "manual"))
                {
                    throw new ArgumentException("A terminal command capability is invalid.", nameof(terminalCommands));
                }
                return new
                {
                    id = command.Id,
                    display_name = command.DisplayName,
                    shell_type = command.ShellType,
                    source = command.Source
                };
            }).ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = pcName,
                agent_version = agentVersion,
                key_epoch = keyEpoch,
                session_heartbeat_capable = true,
                supported_shells = normalizedShells,
                terminal_commands = normalizedCommands,
                remote_agent_update_capable = true
            });
        }

        internal static byte[] BuildPCAgentUpsertBodyWithTerminalCommands(
            string pcName,
            string agentVersion,
            long keyEpoch,
            string platformVersion,
            string architecture,
            bool terminalSnapshotCapable,
            string[] supportedShells,
            TerminalCommandCapability[] terminalCommands)
        {
            if (string.IsNullOrWhiteSpace(platformVersion) || Encoding.UTF8.GetByteCount(platformVersion) > 160)
                throw new ArgumentException("A bounded platform version is required.", nameof(platformVersion));
            if (architecture != "x64" && architecture != "arm64")
                throw new ArgumentException("A supported PC architecture is required.", nameof(architecture));
            var validationBody = BuildPCUpsertBodyWithTerminalCommands(
                pcName, agentVersion, keyEpoch, supportedShells, terminalCommands);
            CryptographicOperations.ZeroMemory(validationBody);
            var capabilities = new List<string>
            {
                "realtime_terminal_v1",
                "file_operations_v1",
                "terminal_commands_v1",
                "remote_agent_update_v1"
            };
            if (terminalSnapshotCapable) capabilities.Insert(1, "terminal_snapshot_v1");
            var normalizedCommands = terminalCommands.Select(command => new
            {
                id = command.Id,
                display_name = command.DisplayName,
                shell_type = command.ShellType,
                source = command.Source
            }).ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                name = pcName,
                agent_version = agentVersion,
                key_epoch = keyEpoch,
                platform = "windows",
                architecture,
                platform_version = platformVersion,
                capabilities,
                session_heartbeat_capable = true,
                supported_shells = supportedShells.Distinct(StringComparer.Ordinal).ToArray(),
                terminal_commands = normalizedCommands,
                remote_agent_update_capable = true
            });
        }

        private async Task UpsertPCAsync(CancellationToken token)
        {
            var legacyBody = BuildPCUpsertBodyWithTerminalCommands(
                config.PcName,
                config.AgentVersion,
                config.KeyEpoch,
                ManagedTerminalLauncher.DetectSupportedShellTypes(),
                ManagedTerminalLauncher.DetectTerminalCommandCapabilities());
            var body = BuildPCAgentUpsertBodyWithTerminalCommands(
                config.PcName,
                config.AgentVersion,
                config.KeyEpoch,
                RuntimeInformation.OSDescription,
                PCAgentArchitecture(RuntimeInformation.OSArchitecture),
                terminalSnapshotEnabled,
                ManagedTerminalLauncher.DetectSupportedShellTypes(),
                ManagedTerminalLauncher.DetectTerminalCommandCapabilities());
            try
            {
                var response = await SendAsync(
                    HttpMethod.Put,
                    "/v1/pcs/" + config.PcId.ToString("D"),
                    body,
                    true,
                    PCUpsertIdempotencyKey(config.PcId, body),
                    token).ConfigureAwait(false);
                var errorCode = response.StatusCode == HttpStatusCode.BadRequest
                    ? await ReadServerErrorCodeAsync(response, token).ConfigureAwait(false)
                    : string.Empty;
                if (response.StatusCode == HttpStatusCode.BadRequest && errorCode == "INVALID_REQUEST")
                {
                    response.Dispose();
                    response = await SendAsync(
                        HttpMethod.Put,
                        "/v1/pcs/" + config.PcId.ToString("D"),
                        legacyBody,
                        true,
                        PCUpsertIdempotencyKey(config.PcId, legacyBody),
                        token).ConfigureAwait(false);
                }
                using (response)
                    await EnsureServerSuccessAsync(response, "PC_UPSERT", token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
                CryptographicOperations.ZeroMemory(legacyBody);
            }
        }

        private static string PCAgentArchitecture(Architecture architecture)
        {
            return architecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException("TermLink PC Agent requires x64 or arm64.")
            };
        }

        internal static bool IsPCHeartbeatDue(
            bool relayConnected,
            DateTime lastAttemptUtc,
            DateTime nowUtc)
        {
            return relayConnected &&
                (lastAttemptUtc == default || nowUtc - lastAttemptUtc >= PCHeartbeatInterval);
        }

        private static string PCUpsertIdempotencyKey(Guid pcId, byte[] body)
        {
            if (pcId == Guid.Empty) throw new ArgumentException("A PC ID is required.", nameof(pcId));
            if (body == null || body.Length == 0) throw new ArgumentException("PC metadata is required.", nameof(body));
            var digest = SHA256.HashData(body);
            try
            {
                // The upsert body can legitimately change after an Agent update,
                // PC rename, or key rotation. Keep retries of the same body stable
                // without replaying a prior 24-hour response for different metadata.
                // V2 deliberately escapes any cached V1 response. In
                // particular, deployments may still retain a 24-hour
                // INVALID_REQUEST result for the pre-heartbeat body contract.
                return "pc-upsert-v2-" + pcId.ToString("N") + "-" +
                    Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }

        private static async Task EnsureServerSuccessAsync(HttpResponseMessage response, string operation, CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            var code = await ReadServerErrorCodeAsync(response, token).ConfigureAwait(false);
            throw new ProductionServerResponseException(
                response.StatusCode,
                string.IsNullOrWhiteSpace(code) ? operation : operation + "_" + code);
        }

        private static async Task<string> ReadServerErrorCodeAsync(HttpResponseMessage response, CancellationToken token)
        {
            var code = string.Empty;
            byte[] bytes = null;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
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
            }
            catch (JsonException)
            {
            }
            finally
            {
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
            }
            return code;
        }

        private async Task ConnectAndPumpAsync(CancellationToken token)
        {
            var active = new ClientWebSocket();
            lock (lifecycleSync)
            {
                if (Volatile.Read(ref disposed) != 0) throw new OperationCanceledException(token);
                socket = active;
            }
            // A realtime connection is an authorization boundary. Never let a
            // lease observation from the prior connection satisfy the first
            // input on a reconnected Android route; fetch the current fence.
            ClearRealtimeConnectionState();

            try
            {
                active.Options.AddSubProtocol("termlink.realtime.v1");
                ProductionTls.ConfigureWebSocket(active.Options, config.ServerUrl);
                var accessToken = await GetAccessTokenAsync(false, token).ConfigureAwait(false);
                AddProofHeaders(active.Options, HttpMethod.Get, "/v1/realtime", Array.Empty<byte>(), accessToken);
                var wsUri = new Uri(config.ServerUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase) + "/v1/realtime");
                using (var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshakeCancellation.CancelAfter(RealtimeHandshakeTimeout);
                    try
                    {
                        await active.ConnectAsync(wsUri, handshakeCancellation.Token).ConfigureAwait(false);
                        var hello = BuildHelloPayloadWithFeatures(terminalSnapshotEnabled, typedInputEnabled);
                        try
                        {
                            await SendWebSocketAsync(
                                active,
                                hello,
                                WebSocketMessageType.Text,
                                true,
                                handshakeCancellation.Token).ConfigureAwait(false);
                        }
                        finally { CryptographicOperations.ZeroMemory(hello); }
                        var welcome = new byte[16 * 1024];
                        var result = await active.ReceiveAsync(welcome, handshakeCancellation.Token).ConfigureAwait(false);
                        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                            throw new InvalidDataException("Production Relay welcome is missing or exceeds its frame limit.");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new WebSocketException(
                            WebSocketError.ConnectionClosedPrematurely,
                            "Production Relay connection or welcome timed out.");
                    }
                }
                SetRelayState(ProductionRelayState.Connected, string.Empty);
                // A coalesced signal may have been consumed by the previous
                // sender just before its socket failed. Re-enumerate every local
                // session on each successful connection so reconnect cannot
                // starve a quiet terminal indefinitely.
                QueueRegisteredSessionsForReplay();

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
                if (!token.IsCancellationRequested)
                {
                    // A clean Close frame immediately after welcome is still a
                    // failed realtime attempt. Route it through RunAsync's
                    // bounded exponential delay instead of reconnecting hot.
                    throw new WebSocketException(
                        WebSocketError.ConnectionClosedPrematurely,
                        "Production Relay disconnected.");
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
                ClearRealtimeConnectionState();
                if (!token.IsCancellationRequested) SetRelayState(ProductionRelayState.Reconnecting, "RELAY_DISCONNECTED");
            }
        }

        private async Task RefreshStatusAsync(CancellationToken token)
        {
            await statusRefreshGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var response = await SendAsync(HttpMethod.Get, "/v1/devices", Array.Empty<byte>(), true, null, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw ReauthenticationRequired();
                }
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
                var accountUsername = config.AccountUsername;
                var accountEmail = config.AccountEmail;
                if (string.IsNullOrWhiteSpace(accountUsername) || string.IsNullOrWhiteSpace(accountEmail))
                {
                    var account = ProductionCredentials.ReadAccountClaims(credentials.GetAccessToken());
                    if (string.IsNullOrWhiteSpace(accountUsername)) accountUsername = account.Username;
                    if (string.IsNullOrWhiteSpace(accountEmail)) accountEmail = account.Email;
                }
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
                    PcId = config.PcId,
                    AgentVersion = config.AgentVersion,
                    Configured = true,
                    AccountLinked = true,
                    AccountUsername = accountUsername,
                    AccountEmail = accountEmail,
                    ServerHost = new Uri(config.ServerUrl).Host,
                    ServerOrigin = NormalizeServerOrigin(config.ServerUrl),
                    TerminalSnapshotEnabled = terminalSnapshotEnabled,
                    PcName = config.PcName,
                    RelayState = relayState,
                    LastErrorCode = relayErrorCode,
                    LastConnectedAtUtc = lastConnectedAtUtc,
                    Devices = devices,
                    Sessions = sessions,
                    TerminalCommands = ManagedTerminalLauncher.LoadAvailableTerminalCommandProfiles()
                        .Select(profile => new ProductionTerminalCommandSummary
                        {
                            Id = profile.Id,
                            DisplayName = profile.DisplayName,
                            Executable = profile.ExecutablePath,
                            Arguments = profile.Arguments,
                            ShellType = profile.ManagedShell,
                            Detected = string.Equals(profile.Source, "detected", StringComparison.Ordinal),
                            Enabled = profile.Enabled,
                            RunAsAdministrator = profile.RunAsAdministrator
                        })
                        .ToList(),
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
            if (IsReauthenticationRequired && state != ProductionRelayState.Stopped)
            {
                state = ProductionRelayState.Failed;
                errorCode = "ACCOUNT_REAUTH_REQUIRED";
            }
            var connected = false;
            lock (statusSync)
            {
                connected = state == ProductionRelayState.Connected && relayState != ProductionRelayState.Connected;
                relayState = state;
                relayErrorCode = errorCode ?? string.Empty;
                if (state == ProductionRelayState.Connected) lastConnectedAtUtc = DateTime.UtcNow;
            }
            if (!connected) return;
            Interlocked.Increment(ref successfulConnectionGeneration);
            // Re-publish every encrypted directory title after a successful
            // Relay generation. The Server row is durable, but this heals
            // cleanup/failover races and ensures a quiet session does not need
            // a new terminal frame before Android can rediscover its title.
            QueueSessionTitleDirectoryRepublish();
        }

        private async Task PublishLoopAsync(ClientWebSocket active, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!await eventSignal.WaitAsync(MetadataUpsertCadence, token).ConfigureAwait(false))
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
                    bool hasMore;
                    if (registry == null || !registry.TryReadBufferedEvents(
                        pending.Key,
                        resumeFrom,
                        DefaultFlowWindowEvents,
                        checked((long)DefaultFlowWindowWireBytes),
                        out frames,
                        out earliest,
                        out latest,
                        out hasMore))
                    {
                        if (TryRemovePending(pending.Key, pending.Value))
                        {
                            RemovePublishedSequence(pending.Key);
                        }
                        continue;
                    }
                    var madeProgress = false;
                    foreach (var frame in frames)
                    {
                        if (publishedSequences.TryGetValue(pending.Key, out var alreadyPublished) &&
                            frame.Sequence <= alreadyPublished)
                        {
                            continue;
                        }
                        var delivered = await PublishAsync(active, new CommittedEvent { ShellType = pending.Value.ShellType, Frame = frame }, token).ConfigureAwait(false);
                        var frameIsTerminal = IsTerminalFrame(frame);
                        var deliveredTerminal = RecordPublicationOutcome(
                            publishedSequences,
                            pending.Key,
                            frame.Sequence,
                            delivered,
                            frameIsTerminal);
                        if (!delivered) break;
                        madeProgress = true;
                        if (frameIsTerminal)
                        {
                            if (TryRemovePending(pending.Key, pending.Value))
                            {
                                RemovePublishedSequence(pending.Key);
                            }
                            terminalPublished = deliveredTerminal;
                            break;
                        }
                    }
                    if (terminalPublished) QueueHistoryUpload(pending.Key, true);
                    if (frames.Count == 0 && latest > resumeFrom)
                    {
                        // No retained frame remains. The next Subscribe carries
                        // its own resume cursor and will receive an explicit Gap.
                        AdvancePublishedSequence(pending.Key, latest);
                    }
                    if (hasMore && madeProgress)
                    {
                        try { eventSignal.Release(); }
                        catch (SemaphoreFullException) { }
                        catch (ObjectDisposedException) { }
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
            if (frame == null || frame.Type != LocalFrameType.SessionRemoved) return false;
            LocalPayloadCodec.ParseSessionRemoved(frame.Payload);
            return true;
        }

        private void QueueHistoryUpload(Guid sessionId, bool terminal)
        {
            if (sessionId == Guid.Empty || Volatile.Read(ref disposed) != 0) return;
            if (terminal)
            {
                terminalHistorySessions[sessionId] = 0;
                // A terminal pass is also the recovery barrier before the
                // SessionAgent DPAPI buffer may be released.
                historyReconcileSessions[sessionId] = 0;
            }
            pendingHistorySessions[sessionId] = 0;
            try { historySignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HistoryUploadLoopAsync()
        {
            var token = cancellation.Token;
            var historyPassReady = false;
            while (!token.IsCancellationRequested)
            {
                if (!historyPassReady)
                    await historySignal.WaitAsync(token).ConfigureAwait(false);
                historyPassReady = false;
                // Merge high-frequency output callbacks into one WAL/snapshot
                // pass. Terminal delivery still remains bounded by this short
                // delay and is reconciled before buffer release.
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                var retry = false;
                foreach (var pending in pendingHistorySessions)
                {
                    if (!pendingHistorySessions.TryRemove(pending.Key, out var ignored)) continue;
                    try
                    {
                        var queuedFrames = DrainPendingHistoryFrames(pending.Key);
                        var reconcile = historyReconcileSessions.TryRemove(pending.Key, out ignored) ||
                            terminalHistoryPersistenceFailures.ContainsKey(pending.Key);
                        IList<LocalFrame> uploadFrames;
                        if (reconcile)
                        {
                            IList<LocalFrame> bufferedFrames;
                            ulong earliest;
                            ulong latest;
                            if (registry != null && registry.TryReadBufferedEvents(
                                pending.Key,
                                0,
                                out bufferedFrames,
                                out earliest,
                                out latest))
                            {
                                foreach (var frame in bufferedFrames)
                                    terminalHistoryStore.AppendCommittedFrame(frame);
                            }
                            foreach (var frame in queuedFrames)
                                terminalHistoryStore.AppendCommittedFrame(frame);
                            uploadFrames = terminalHistoryStore.ReadFrames(pending.Key);
                        }
                        else
                        {
                            foreach (var frame in queuedFrames)
                                terminalHistoryStore.AppendCommittedFrame(frame);
                            uploadFrames = queuedFrames;
                        }
                        terminalHistoryPersistenceFailures.TryRemove(pending.Key, out _);
                        var production = terminalSnapshotEnabled
                            ? terminalHistoryProducer.ProcessSession(pending.Key, uploadFrames)
                            : null;
                        ObserveTerminalSnapshotProduction(pending.Key, production);
                        var events = new List<HistoryEventV1>(uploadFrames.Count);
                        foreach (var frame in uploadFrames.OrderBy(value => value.Sequence))
                        {
                            var historyEvent = BuildHistoryEvent(frame);
                            if (historyEvent != null) events.Add(historyEvent);
                        }
                        var durable = !terminalHistoryPersistenceFailures.ContainsKey(pending.Key) &&
                            events.Count > 0 &&
                            await UploadHistoryAsync(pending.Key, events, production, token).ConfigureAwait(false);
                        if (!durable)
                        {
                            historyReconcileSessions[pending.Key] = 0;
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
                    catch (Exception error)
                    {
                        terminalHistoryPersistenceFailures[pending.Key] = error.GetType().Name;
                        ObserveTerminalSnapshotFailure(pending.Key);
                        historyReconcileSessions[pending.Key] = 0;
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
                    await WaitForHistoryRetryOrSignalAsync(
                        historySignal,
                        TimeSpan.FromSeconds(30),
                        token).ConfigureAwait(false);
                    // Either the timeout or the consumed signal authorizes the
                    // next snapshot pass; do not wait for a second signal.
                    historyPassReady = true;
                }
            }
        }

        internal static async Task<bool> WaitForHistoryRetryOrSignalAsync(
            SemaphoreSlim signal,
            TimeSpan retryDelay,
            CancellationToken token)
        {
            return await signal.WaitAsync(retryDelay, token).ConfigureAwait(false);
        }

        private IList<LocalFrame> DrainPendingHistoryFrames(Guid sessionId)
        {
            var result = new List<LocalFrame>();
            if (!pendingHistoryFrames.TryGetValue(sessionId, out var queue)) return result;
            while (queue.TryDequeue(out var frame)) result.Add(frame);
            return result;
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

        private static SessionPublicationSnapshot ApplyPublicationFrame(
            SessionPublicationState state,
            CommittedEvent item,
            DateTimeOffset observedAt)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (item == null || item.Frame == null) throw new ArgumentNullException(nameof(item));
            lock (state.Sync)
            {
                if (item.Frame.Sequence <= state.LastEventSequence)
                    return CapturePublicationSnapshot(item.Frame.SessionId, state);
                state.ShellType = item.ShellType;
                switch (item.Frame.Type)
                {
                    case LocalFrameType.Output:
                        state.LastOutputSequence = item.Frame.Sequence;
                        MarkRestDirtyLocked(state);
                        break;
                    case LocalFrameType.Resize:
                        LocalPayloadCodec.ParseResize(item.Frame.Payload, out var columns, out var rows);
                        state.Columns = columns;
                        state.Rows = rows;
                        state.HasSize = true;
                        MarkRestDirtyLocked(state);
                        break;
                    case LocalFrameType.Title:
                        state.Title = LocalPayloadCodec.ParseTitle(item.Frame.Payload);
                        MarkRestDirtyLocked(state);
                        break;
                    case LocalFrameType.Lifecycle:
                        state.Lifecycle = LocalPayloadCodec.ParseLifecycle(item.Frame.Payload, out var exitCode);
                        state.ExitCode = state.Lifecycle == LocalLifecycleState.Ended ? (int?)exitCode : null;
                        state.EndedAt = state.Lifecycle == LocalLifecycleState.Running
                            ? (DateTimeOffset?)null
                            : observedAt;
                        MarkRestDirtyLocked(state);
                        break;
                    case LocalFrameType.SessionRemoved:
                        LocalPayloadCodec.ParseSessionRemoved(item.Frame.Payload);
                        break;
                }
                state.LastEventSequence = item.Frame.Sequence;
                return CapturePublicationSnapshot(item.Frame.SessionId, state);
            }
        }

        private static SessionPublicationSnapshot CapturePublicationSnapshot(
            Guid sessionId,
            SessionPublicationState state)
        {
            return new SessionPublicationSnapshot
            {
                SessionId = sessionId,
                ShellType = state.ShellType,
                HasSize = state.HasSize,
                Columns = state.Columns,
                Rows = state.Rows,
                Title = state.Title,
                RestRevision = state.RestRevision,
                Lifecycle = state.Lifecycle,
                ExitCode = state.ExitCode,
                LastOutputSequence = state.LastOutputSequence,
                LastEventSequence = state.LastEventSequence,
                EndedAt = state.EndedAt,
                SnapshotStatus = state.SnapshotStatus,
                SnapshotReasonCode = state.SnapshotReasonCode
            };
        }

        private static void MarkRestDirtyLocked(SessionPublicationState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.RestRevision == ulong.MaxValue)
                throw new InvalidOperationException("Session publication revision exhausted.");
            state.RestRevision++;
            state.RestDirty = true;
        }

        private static void StageTailFirstSnapshotPublication(
            SessionPublicationState state,
            ulong outputSequence)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (outputSequence == 0) return;
            lock (state.Sync)
            {
                // The history worker will replace this first-output anchor with
                // the exact checkpoint it produces.  Installing it immediately
                // prevents the independently scheduled REST worker from racing
                // hundreds of output events ahead during that first pass.
                if (state.TailFirstSnapshotPublicationSequence == 0)
                    state.TailFirstSnapshotPublicationSequence = outputSequence;
            }
        }

        private void ObserveTerminalSnapshotProduction(
            Guid sessionId,
            ProductionTerminalHistoryProductionResult production)
        {
            if (!terminalSnapshotEnabled || sessionId == Guid.Empty || production == null ||
                production.LastSequence == 0 ||
                !publicationStates.TryGetValue(sessionId, out var state))
            {
                return;
            }
            var publicationSequence = production.SnapshotEligible
                ? production.LastSequence
                : 0UL;
            var snapshotStatus = production.SnapshotEligible
                ? "READY"
                : production.PermanentlyIneligible ? "UNSUPPORTED" : "PENDING";
            var snapshotReasonCode = production.SnapshotEligible
                ? null
                : NormalizeSnapshotReasonCode(production.IneligibilityCode);
            var changed = false;
            lock (state.Sync)
            {
                if (state.TailFirstSnapshotPublicationSequence != publicationSequence ||
                    !string.Equals(state.SnapshotStatus, snapshotStatus, StringComparison.Ordinal) ||
                    !string.Equals(state.SnapshotReasonCode, snapshotReasonCode, StringComparison.Ordinal))
                {
                    state.TailFirstSnapshotPublicationSequence = publicationSequence;
                    state.SnapshotStatus = snapshotStatus;
                    state.SnapshotReasonCode = snapshotReasonCode;
                    MarkRestDirtyLocked(state);
                    changed = true;
                }
            }
            if (!changed) return;
            try { metadataSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        internal static string NormalizeSnapshotReasonCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return "UNKNOWN";
            for (var index = 0; index < value.Length; index++)
            {
                var item = value[index];
                if ((item < 'A' || item > 'Z') && (item < '0' || item > '9') && item != '_')
                    return "UNKNOWN";
            }
            return value;
        }

        private void ObserveTerminalSnapshotFailure(Guid sessionId)
        {
            if (!terminalSnapshotEnabled || sessionId == Guid.Empty ||
                !publicationStates.TryGetValue(sessionId, out var state)) return;
            var changed = false;
            lock (state.Sync)
            {
                if (!string.Equals(state.SnapshotStatus, "FAILED", StringComparison.Ordinal) ||
                    !string.Equals(state.SnapshotReasonCode, "HISTORY_PRODUCTION_FAILED", StringComparison.Ordinal))
                {
                    state.SnapshotStatus = "FAILED";
                    state.SnapshotReasonCode = "HISTORY_PRODUCTION_FAILED";
                    MarkRestDirtyLocked(state);
                    changed = true;
                }
            }
            if (!changed) return;
            try { metadataSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private void LimitSessionOutputToTailFirstSnapshotLocked(
            SessionPublicationState state,
            SessionPublicationSnapshot snapshot)
        {
            if (!terminalSnapshotEnabled || state == null || snapshot == null) return;
            var anchor = state.TailFirstSnapshotPublicationSequence;
            if (anchor == 0) return;
            var maximum = anchor > ulong.MaxValue - MaxTailFirstSnapshotSuffixEvents
                ? ulong.MaxValue
                : anchor + MaxTailFirstSnapshotSuffixEvents;
            var limited = Math.Min(snapshot.LastOutputSequence, maximum);
            // PostgreSQL keeps last_output_seq monotonic.  Never pretend that a
            // cursor already confirmed by this process can be moved backward;
            // a newer checkpoint must catch up to it instead.
            snapshot.LastOutputSequence = Math.Max(
                state.LastRestConfirmedOutputSequence,
                limited);
        }

        private static bool ConfirmSessionUpsert(
            SessionPublicationState state,
            SessionPublicationSnapshot snapshot,
            DateTime confirmedAtUtc)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (state.Sync)
            {
                state.LastRestConfirmedUtc = confirmedAtUtc;
                state.LastRestConfirmedOutputSequence = Math.Max(
                    state.LastRestConfirmedOutputSequence,
                    snapshot.LastOutputSequence);
                if (state.RestRevision != snapshot.RestRevision) return false;
                state.RestDirty = false;
                return true;
            }
        }

        private static string LifecycleText(LocalLifecycleState lifecycle)
        {
            if (lifecycle == LocalLifecycleState.Ended) return "EXITED";
            if (lifecycle == LocalLifecycleState.Interrupted) return "INTERRUPTED";
            return "RUNNING";
        }

        private static string ShellTypeText(ManagedShellType shellType)
        {
            return shellType.ToString().ToLowerInvariant();
        }

        internal static bool RecordPublicationOutcome(
            ConcurrentDictionary<Guid, ulong> sequences,
            Guid sessionId,
            ulong sequence,
            bool delivered,
            bool terminal)
        {
            if (sequences == null) throw new ArgumentNullException(nameof(sequences));
            if (!delivered) return false;
            if (sessionId == Guid.Empty || sequence == 0)
                throw new ArgumentException("Delivered publication identity is invalid.");
            sequences.AddOrUpdate(sessionId, sequence, (_, current) => Math.Max(current, sequence));
            return terminal;
        }

        private async Task<bool> PublishAsync(ClientWebSocket active, CommittedEvent item, CancellationToken token)
        {
            if (publishedSequences.TryGetValue(item.Frame.SessionId, out var alreadyPublished) &&
                item.Frame.Sequence <= alreadyPublished)
            {
                return true;
            }
            var state = publicationStates.GetOrAdd(item.Frame.SessionId, _ => new SessionPublicationState());
            var metadata = ApplyPublicationFrame(state, item, DateTimeOffset.UtcNow);
            if (item.Frame.Type == LocalFrameType.LocalInputObserved)
            {
                using var released = await SendAsync(
                    HttpMethod.Delete,
                    "/v1/sessions/" + item.Frame.SessionId.ToString("D") + "/lease",
                    Array.Empty<byte>(),
                    true,
                    LocalInputLeaseIdempotencyKey(item.Frame.SessionId, item.Frame.Sequence),
                    token).ConfigureAwait(false);
                try
                {
                    await EnsureServerSuccessAsync(
                        released,
                        "LOCAL_INPUT_LEASE_RELEASE",
                        token).ConfigureAwait(false);
                }
                catch (ProductionServerResponseException error) when (
                    IsStaleLocalInputLeaseReleaseDenial(error))
                {
                    // A recovered LocalInputObserved frame can outlive the
                    // session row or this PC's session authority. In either
                    // case the PC cannot still own a releasable server lease,
                    // so keep publishing the durable terminal event. Device
                    // trust/authentication failures use distinct error codes
                    // and deliberately continue to fault the Relay pump.
                }
            }
            PruneSubscribers();
            if (!subscribers.TryGetValue(item.Frame.SessionId, out var sessionSubscribers)) return true;
            var delivered = true;
            foreach (var subscriber in sessionSubscribers.Values)
            {
                var messageType = MapMessageType(item.Frame);
                if (messageType == MessageType.Unspecified) continue;
                IMessage payload = BuildRealtimePayload(item.Frame, item.ShellType, metadata);
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

        private static bool IsStaleLocalInputLeaseReleaseDenial(
            ProductionServerResponseException error)
        {
            if (error == null || error.StatusCode != HttpStatusCode.Forbidden) return false;
            return string.Equals(
                    error.ErrorCode,
                    "LOCAL_INPUT_LEASE_RELEASE_FORBIDDEN",
                    StringComparison.Ordinal) ||
                string.Equals(
                    error.ErrorCode,
                    "LOCAL_INPUT_LEASE_RELEASE_MANAGE_NOT_GRANTED",
                    StringComparison.Ordinal);
        }

        private static bool RequiresImmediateMetadataUpsert(LocalFrame frame)
        {
            return frame != null &&
                (frame.Type == LocalFrameType.Lifecycle ||
                 frame.Type == LocalFrameType.Resize ||
                 frame.Type == LocalFrameType.Title);
        }

        private async Task FlushPendingMetadataUpsertsAsync(CancellationToken token)
        {
            var entries = publicationStates.ToArray();
            var now = DateTime.UtcNow;
            const int concurrency = 4;
            for (var offset = 0; offset < entries.Length; offset += concurrency)
            {
                var count = Math.Min(concurrency, entries.Length - offset);
                var writes = new Task[count];
                for (var index = 0; index < count; index++)
                {
                    var item = entries[offset + index];
                    SessionPublicationSnapshot snapshot;
                    lock (item.Value.Sync)
                    {
                        if (item.Value.HasSize && item.Value.Lifecycle != LocalLifecycleState.Ended &&
                            now - item.Value.LastRestConfirmedUtc >= SessionMetadataHeartbeatInterval)
                        {
                            MarkRestDirtyLocked(item.Value);
                        }
                        snapshot = CapturePublicationSnapshot(item.Key, item.Value);
                    }
                    writes[index] = UpsertSessionAsync(item.Value, snapshot, false, token);
                }
                await Task.WhenAll(writes).ConfigureAwait(false);
            }
        }

        private async Task MetadataUpsertLoopAsync()
        {
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                await metadataSignal.WaitAsync(PCHeartbeatWait(DateTime.UtcNow), token).ConfigureAwait(false);
                try
                {
                    await TryHeartbeatPCAsync(token).ConfigureAwait(false);
                    await FlushPendingMetadataUpsertsAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (
                    error is HttpRequestException ||
                    error is IOException ||
                    error is JsonException)
                {
                    // Session discovery is independent from the WebSocket pump.
                    // Retain RestDirty and retry without tearing down a healthy
                    // realtime connection.
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
            }
        }

        private TimeSpan PCHeartbeatWait(DateTime nowUtc)
        {
            lock (statusSync)
            {
                if (relayState != ProductionRelayState.Connected || lastPCUpsertAttemptUtc == default)
                    return PCHeartbeatInterval;
                var remaining = PCHeartbeatInterval - (nowUtc - lastPCUpsertAttemptUtc);
                if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
                return remaining < PCHeartbeatInterval ? remaining : PCHeartbeatInterval;
            }
        }

        private async Task TryHeartbeatPCAsync(CancellationToken token)
        {
            var now = DateTime.UtcNow;
            lock (statusSync)
            {
                if (!IsPCHeartbeatDue(
                    relayState == ProductionRelayState.Connected,
                    lastPCUpsertAttemptUtc,
                    now))
                {
                    return;
                }
                // Record starts, rather than completions, so slow or failed
                // HTTP attempts retry on the next fixed cadence without a hot
                // loop and without coupling Relay health to REST availability.
                lastPCUpsertAttemptUtc = now;
            }
            try
            {
                await UpsertPCAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
            catch (Exception error) when (
                error is HttpRequestException ||
                error is IOException ||
                error is JsonException)
            {
                // Server-authoritative presence is best effort while the
                // realtime socket is healthy. Retry at the next cadence.
            }
        }

        private async Task UpsertSessionAsync(
            SessionPublicationState state,
            SessionPublicationSnapshot candidate,
            bool force,
            CancellationToken token)
        {
            await state.RestUpsertGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                SessionPublicationSnapshot snapshot;
                var attemptedAt = DateTime.UtcNow;
                lock (state.Sync)
                {
                    if (!state.RestDirty || !state.HasSize) return;
                    if (!force && attemptedAt - state.LastRestAttemptUtc < MetadataUpsertCadence) return;
                    state.LastRestAttemptUtc = attemptedAt;
                    snapshot = candidate != null &&
                        candidate.LastEventSequence == state.LastEventSequence &&
                        candidate.RestRevision == state.RestRevision
                        ? candidate
                        : CapturePublicationSnapshot(candidate == null ? Guid.Empty : candidate.SessionId, state);
                }
                if (snapshot.SessionId == Guid.Empty) return;
                var titleAuthorityAvailable = true;
                try
                {
                    await RefreshAuthorizedRecipientsAsync(false, token).ConfigureAwait(false);
                }
                catch (Exception error) when (
                    error is HttpRequestException ||
                    error is IOException ||
                    error is JsonException ||
                    error is ProductionServerResponseException serverError &&
                        serverError.StatusCode.HasValue && IsTransientStatus(serverError.StatusCode.Value))
                {
                    // Session presence and terminal dimensions remain useful
                    // while the recipient directory is temporarily unavailable.
                    // Retain the last encrypted title on the Server by omitting
                    // title_cipher from this heartbeat.
                    titleAuthorityAvailable = false;
                }
                byte[] titleCipher = null;
                byte[] body = null;
                try
                {
                    // The authority refresh may have changed the recipient
                    // generation or local terminal frames may have advanced
                    // while REST was in flight. Capture one current metadata
                    // revision before sealing, so an older completion can never
                    // clear a newer title/directory/reconnect publication.
                    lock (state.Sync)
                    {
                        snapshot = CapturePublicationSnapshot(snapshot.SessionId, state);
                        LimitSessionOutputToTailFirstSnapshotLocked(state, snapshot);
                    }
                    titleCipher = titleAuthorityAvailable
                        ? await BuildSessionTitleCipherAsync(snapshot, token).ConfigureAwait(false)
                        : Array.Empty<byte>();
                    snapshot.TitleCipher = titleCipher;
                    body = BuildSessionUpsertBody(config.PcId, snapshot);
                }
                finally
                {
                    snapshot.TitleCipher = null;
                    if (titleCipher != null) CryptographicOperations.ZeroMemory(titleCipher);
                }
                try
                {
                    var path = "/v1/sessions/" + snapshot.SessionId.ToString("D");
                    var idempotencyKey = SessionUpsertIdempotencyKey(
                        snapshot.SessionId,
                        snapshot.LastEventSequence,
                        body);
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            using var response = await SendAsync(
                                HttpMethod.Put,
                                path,
                                body,
                                true,
                                idempotencyKey,
                                token).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode) break;
                            if (attempt >= 2 || !IsTransientStatus(response.StatusCode))
                                await EnsureServerSuccessAsync(response, "SESSION_UPSERT", token).ConfigureAwait(false);
                        }
                        catch (HttpRequestException) when (attempt < 2)
                        {
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), token).ConfigureAwait(false);
                    }
                    ConfirmSessionUpsert(state, snapshot, DateTime.UtcNow);
                }
                finally
                {
                    if (body != null) CryptographicOperations.ZeroMemory(body);
                }
            }
            finally { state.RestUpsertGate.Release(); }
        }

        internal static string SessionUpsertIdempotencyKey(
            Guid sessionId,
            ulong lastEventSequence,
            byte[] body)
        {
            if (sessionId == Guid.Empty || lastEventSequence == 0)
                throw new ArgumentException("Session upsert identity is invalid.");
            if (body == null || body.Length == 0)
                throw new ArgumentException("Session upsert body is required.", nameof(body));
            var digest = SHA256.HashData(body);
            try
            {
                // Full SHA-256 still fits the Server's 128-character bound:
                // 8-byte namespace + UUID + max UInt64 + separators + digest.
                return "session-" + sessionId.ToString("N") + "-" +
                    lastEventSequence.ToString(CultureInfo.InvariantCulture) + "-" +
                    Convert.ToHexString(digest).ToLowerInvariant();
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }

        private static byte[] BuildSessionUpsertBody(Guid pcId, SessionPublicationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                pc_id = pcId,
                shell_type = ShellTypeText(snapshot.ShellType),
                // This is a versioned, multi-recipient E2EE envelope. The
                // server persists and distributes it without learning the
                // terminal title. An empty value means "no title update" and
                // therefore preserves the last successfully sealed title.
                title_cipher = snapshot.TitleCipher == null || snapshot.TitleCipher.Length == 0
                    ? string.Empty
                    : Base64Url(snapshot.TitleCipher),
                state = LifecycleText(snapshot.Lifecycle),
                columns = snapshot.Columns,
                rows = snapshot.Rows,
                last_output_seq = snapshot.LastOutputSequence,
                ended_at = snapshot.EndedAt,
                snapshot_status = snapshot.SnapshotStatus,
                snapshot_reason_code = snapshot.SnapshotReasonCode,
                session_heartbeat_capable = true
            });
        }

        private byte[] BuildSessionTitleCipher(SessionPublicationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var recipients = new List<SessionTitleRecipient>();
            try
            {
                foreach (var item in authorizedRecipients.OrderBy(value => value.Key))
                {
                    byte[] pairwiseKey = null;
                    long keyEpoch = 0;
                    lock (item.Value.Sync)
                    {
                        if (!item.Value.Removed && item.Value.PairwiseKey != null &&
                            item.Value.PairwiseKey.Length == 32 && item.Value.KeyEpoch > 0)
                        {
                            pairwiseKey = (byte[])item.Value.PairwiseKey.Clone();
                            keyEpoch = item.Value.KeyEpoch;
                        }
                    }
                    if (pairwiseKey != null)
                    {
                        recipients.Add(new SessionTitleRecipient(item.Key, keyEpoch, pairwiseKey));
                    }
                }
                if (recipients.Count == 0) return Array.Empty<byte>();
                return SessionTitleCrypto.Seal(
                    snapshot.Title ?? string.Empty,
                    snapshot.SessionId,
                    credentials.DeviceId,
                    recipients);
            }
            finally
            {
                foreach (var recipient in recipients)
                {
                    if (recipient.PairwiseKey != null) CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
                }
            }
        }

        private async Task<byte[]> BuildSessionTitleCipherAsync(
            SessionPublicationSnapshot snapshot,
            CancellationToken token)
        {
            await authorizedRecipientGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Refresh swaps and retires recipient keys under this same
                // gate. Cloning one complete generation avoids envelopes that
                // mix entries from before and after an epoch/authority change.
                return BuildSessionTitleCipher(snapshot);
            }
            finally { authorizedRecipientGate.Release(); }
        }

        private async Task<bool> EnsureSessionMetadataCoversSnapshotAsync(
            Guid sessionId,
            ulong snapshotSequence,
            CancellationToken token)
        {
            if (sessionId == Guid.Empty || snapshotSequence == 0) return false;
            if (!publicationStates.TryGetValue(sessionId, out var state)) return false;
            SessionPublicationSnapshot snapshot;
            lock (state.Sync)
            {
                if (!state.HasSize || state.LastOutputSequence < snapshotSequence) return false;
                if (state.LastRestConfirmedOutputSequence >= snapshotSequence) return true;
                MarkRestDirtyLocked(state);
                snapshot = CapturePublicationSnapshot(sessionId, state);
            }
            await UpsertSessionAsync(state, snapshot, true, token).ConfigureAwait(false);
            lock (state.Sync)
            {
                return state.LastRestConfirmedOutputSequence >= snapshotSequence;
            }
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout ||
                statusCode == (HttpStatusCode)429 ||
                code == 500 || code == 502 || code == 503 || code == 504;
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
                    envelope.MessageType != MessageType.ResizeRequest &&
                    envelope.MessageType != MessageType.SessionTerminateRequest) return;
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
                    RemoveSubscriber(sessionId, senderId, subscriber);
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
                        resize.Rows < 1 || resize.Rows > short.MaxValue)
                    {
                        return;
                    }
                    // The PC terminal viewport is authoritative.  Android may
                    // send an older ResizeRequest, but it can never resize the
                    // local ConPTY or mutate the published PC dimensions.
                    return;
                }
                if (envelope.MessageType == MessageType.SessionTerminateRequest)
                {
                    Guid requestId;
                    if (!TryParseSessionTerminateRequest(plaintext, out requestId)) return;
                    Guid previousSessionId;
                    if (terminationRequests.TryGetValue(requestId, out previousSessionId))
                    {
                        // Exact retransmission is already complete. Reuse of a
                        // request ID for another Session is rejected without
                        // affecting either local process tree.
                        return;
                    }
                    if (!terminationRequests.TryAdd(requestId, sessionId)) return;
                    if (!registry.TerminateSession(sessionId))
                    {
                        terminationRequests.TryRemove(requestId, out _);
                        return;
                    }
                    PruneTerminationRequests(requestId);
                    return;
                }
                if (envelope.MessageType != MessageType.InputEvent) return;
                var input = InputEventPayload.Parser.ParseFrom(plaintext);
                if (input.InputId.Length != 16 || input.LeaseId.Length != 16 || input.InputSeq == 0 || input.Input.Length == 0) return;
                var inputId = GuidFromBytes(input.InputId.Span); var leaseId = GuidFromBytes(input.LeaseId.Span);
                byte[] canonicalInput = null;
                if (typedInputEnabled && input.TypedInput != null)
                {
                    if (!TryEncodeTypedInput(input.TypedInput, out canonicalInput) ||
                        canonicalInput.Length != input.Input.Length ||
                        !CryptographicOperations.FixedTimeEquals(canonicalInput, input.Input.Span))
                    {
                        if (canonicalInput != null) CryptographicOperations.ZeroMemory(canonicalInput);
                        registry.TrySendRemoteInputRejection(sessionId, inputId, LocalRemoteInputResult.InputPayloadRejected);
                        return;
                    }
                }
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
                var inputBytes = canonicalInput ?? input.Input.ToByteArray();
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

        internal static bool TryParseSessionTerminateRequest(byte[] plaintext, out Guid requestId)
        {
            requestId = Guid.Empty;
            if (plaintext == null) return false;
            SessionTerminateRequestPayload request;
            try { request = SessionTerminateRequestPayload.Parser.ParseFrom(plaintext); }
            catch (InvalidProtocolBufferException) { return false; }
            if (request.RequestId.Length != 16 ||
                !string.Equals(request.ReasonCode, "USER_REQUESTED", StringComparison.Ordinal))
            {
                return false;
            }
            requestId = GuidFromBytes(request.RequestId.Span);
            return true;
        }

        private void PruneTerminationRequests(Guid preserveRequestId)
        {
            if (terminationRequests.Count <= MaxRememberedTerminationRequests) return;
            foreach (var requestId in terminationRequests.Keys)
            {
                if (requestId == preserveRequestId) continue;
                terminationRequests.TryRemove(requestId, out _);
                if (terminationRequests.Count <= MaxRememberedTerminationRequests / 2) break;
            }
        }

        private Subscriber RememberSubscriber(Guid sessionId, Subscriber candidate)
        {
            candidate = RememberConnectionRoute(sessionId, candidate);
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

        private Subscriber RememberConnectionRoute(Guid sessionId, Subscriber candidate)
        {
            var routeKey = SubscriberRouteKey(sessionId, candidate.DeviceId);
            while (true)
            {
                if (connectionSubscriberRoutes.TryGetValue(routeKey, out var retained))
                {
                    if (ReferenceEquals(retained, candidate)) return retained;
                    if (SubscriberAuthorityMatches(retained, candidate))
                    {
                        RetireSubscriber(candidate);
                        return retained;
                    }
                    if (((ICollection<KeyValuePair<string, Subscriber>>)connectionSubscriberRoutes).Remove(
                        new KeyValuePair<string, Subscriber>(routeKey, retained)))
                    {
                        DetachSubscriber(sessionId, retained.DeviceId, retained);
                        RetireSubscriber(retained);
                    }
                    continue;
                }
                if (connectionSubscriberRoutes.TryAdd(routeKey, candidate)) return candidate;
            }
        }

        private static string SubscriberRouteKey(Guid sessionId, Guid deviceId) =>
            sessionId.ToString("N") + ":" + deviceId.ToString("N");

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
            if (sessionId == Guid.Empty || senderId == Guid.Empty ||
                leaseId == Guid.Empty || leaseVersion < 1)
            {
                return false;
            }
            if (!await IsAuthorizedInputSenderAsync(senderId, token).ConfigureAwait(false))
                return false;

            var now = DateTime.UtcNow;
            if (leaseStatuses.TryGetValue(sessionId, out var cached) &&
                cached.HolderDeviceId != Guid.Empty &&
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
                    parsedHolder != Guid.Empty &&
                    parsedLease == leaseId &&
                    parsedVersion == leaseVersion;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return false; }
            catch (JsonException) { return false; }
        }

        private async Task<bool> IsAuthorizedInputSenderAsync(
            Guid senderId,
            CancellationToken token)
        {
            try
            {
                // This is the Server's PC-scoped same-account recipient
                // snapshot. The holder field on a control lease identifies
                // who acquired/renews its fence; it is not the account-wide
                // input authorization decision.
                await RefreshAuthorizedRecipientsAsync(false, token).ConfigureAwait(false);
                if (IsAuthorizedInputSender(senderId)) return true;

                // A newly paired/reconnected Android may not be present in a
                // still-live negative cache. Refresh once before rejecting it.
                await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
                return IsAuthorizedInputSender(senderId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return false; }
            catch (HttpRequestException) { return false; }
            catch (JsonException) { return false; }
            catch (FormatException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        private bool IsAuthorizedInputSender(Guid senderId)
        {
            if (senderId == Guid.Empty ||
                !authorizedRecipients.TryGetValue(senderId, out var authorized))
            {
                return false;
            }
            lock (authorized.Sync)
            {
                return !authorized.Removed &&
                    authorized.DeviceId == senderId &&
                    authorized.KeyEpoch == config.KeyEpoch &&
                    authorized.ExchangePublicKey != null &&
                    authorized.ExchangePublicKey.Length == 32 &&
                    authorized.PairwiseKey != null &&
                    authorized.PairwiseKey.Length == 32;
            }
        }

        private async Task<Subscriber> GetSubscriberAsync(
            Guid sessionId,
            Guid deviceId,
            CancellationToken token,
            bool forceAuthorityRefresh = false)
        {
            if (subscribers.TryGetValue(sessionId, out var known) && known.TryGetValue(deviceId, out var existing))
            {
                lock (existing.Sync)
                {
                    if (!existing.Removed && existing.KeyEpoch == config.KeyEpoch) return existing;
                }
                RemoveSubscriber(sessionId, deviceId, existing);
            }
            var routeKey = SubscriberRouteKey(sessionId, deviceId);
            if (connectionSubscriberRoutes.TryGetValue(routeKey, out var routed))
            {
                lock (routed.Sync)
                {
                    if (!routed.Removed && routed.KeyEpoch == config.KeyEpoch) return routed;
                }
                if (((ICollection<KeyValuePair<string, Subscriber>>)connectionSubscriberRoutes).Remove(
                    new KeyValuePair<string, Subscriber>(routeKey, routed)))
                {
                    RetireSubscriber(routed);
                }
            }

            // Periodic Android SUBSCRIBE frames start a new flow-control
            // generation, but do not by themselves invalidate a route that was
            // already authorized for this connection and key epoch. Refreshing
            // before these fast paths turns every 15-second SUBSCRIBE into a
            // REST dependency and lets a transient refresh failure strand a
            // still-valid subscriber.
            await RefreshAuthorizedRecipientsAsync(forceAuthorityRefresh, token).ConfigureAwait(false);
            if (!authorizedRecipients.TryGetValue(deviceId, out var authorized))
            {
                if (!forceAuthorityRefresh)
                    await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
                if (!authorizedRecipients.TryGetValue(deviceId, out authorized))
                    throw new InvalidDataException("Subscriber is not authorized for this PC.");
            }
            byte[] publicKey;
            long keyEpoch;
            lock (authorized.Sync)
            {
                if (authorized.Removed || authorized.ExchangePublicKey == null || authorized.KeyEpoch < 1)
                    throw new InvalidDataException("Subscriber authorization key is unavailable.");
                publicKey = (byte[])authorized.ExchangePublicKey.Clone();
                keyEpoch = authorized.KeyEpoch;
            }
            try
            {
                return RememberConnectionRoute(
                    sessionId,
                    CreateAuthorizedSubscriber(
                        credentials.Identity.Exchange,
                        credentials.DeviceId,
                        deviceId,
                        publicKey,
                        keyEpoch));
            }
            finally { CryptographicOperations.ZeroMemory(publicKey); }
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

        private async Task<bool> UploadHistoryAsync(
            Guid sessionId,
            IList<HistoryEventV1> events,
            ProductionTerminalHistoryProductionResult production,
            CancellationToken token)
        {
            if (historyUploader == null) return false;
            // Stage the exact checkpoint before the recipient lookup or object
            // upload performs network IO.  REST metadata is published on a
            // separate worker and must not run unboundedly ahead while output
            // continues at high frequency.
            ObserveTerminalSnapshotProduction(sessionId, production);
            // History objects must never be produced for a stale or revoked
            // recipient. Fetch a fresh PC-scoped authorization snapshot for
            // every archival attempt; in automatic account mode this includes
            // trusted same-account Android devices without a separate grant.
            // The response carries the authoritative key epoch in the same
            // database snapshot.
            await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
            var recipients = new List<ProductionHistoryRecipient>();
            foreach (var recipient in authorizedRecipients.Values)
            {
                long keyEpoch;
                var pairwiseKey = SnapshotPairwiseKey(recipient, out keyEpoch);
                if (pairwiseKey != null)
                {
                    recipients.Add(new ProductionHistoryRecipient
                    {
                        DeviceId = recipient.DeviceId,
                        PairwiseKey = pairwiseKey,
                        KeyEpoch = keyEpoch
                    });
                }
            }
            try
            {
                var recipientEpochs = recipients.Select(value =>
                    (value.DeviceId, value.KeyEpoch)).ToList();
                ProductionTerminalHistoryOutbox terminalOutbox = null;
                if (terminalSnapshotEnabled)
                {
                    terminalHistoryProducer.RetainDeliveries(sessionId, recipientEpochs);
                    terminalOutbox = terminalHistoryProducer.LoadOutbox(sessionId);
                }
                var hasRecipient = recipients.Count > 0;
                var durable = true;
                if (terminalOutbox != null && recipients.Any(value =>
                    !HasUploadedTerminalSnapshot(terminalOutbox, value.DeviceId, value.KeyEpoch)))
                {
                    // Tail usability is account-wide. Publish this anchor to
                    // one fixed authorization batch before any recipient can
                    // start CHUNK or Transcript backfill.
                    if (!await EnsureSessionMetadataCoversSnapshotAsync(
                        sessionId,
                        terminalOutbox.SnapshotSequence,
                        token).ConfigureAwait(false))
                    {
                        return false;
                    }
                    foreach (var recipient in recipients)
                    {
                        if (HasUploadedTerminalSnapshot(
                            terminalOutbox,
                            recipient.DeviceId,
                            recipient.KeyEpoch))
                        {
                            continue;
                        }
                        var snapshot = terminalHistoryProducer.PrepareNextUpload(
                            sessionId,
                            recipient);
                        if (snapshot == null ||
                            snapshot.Artifact.Kind != ProductionTerminalHistoryArtifactKind.Snapshot)
                            throw new InvalidDataException("Terminal outbox did not prioritize its snapshot anchor.");
                        try
                        {
                            var snapshotOutcome = await historyUploader.UploadTerminalArtifactAsync(
                                sessionId,
                                recipient,
                                snapshot,
                                token).ConfigureAwait(false);
                            if (snapshotOutcome != ProductionHistoryUploadOutcome.Durable)
                            {
                                durable = false;
                                continue;
                            }
                            terminalHistoryProducer.MarkUploaded(snapshot);
                        }
                        finally { CryptographicOperations.ZeroMemory(snapshot.SealedObject); }
                    }
                    terminalOutbox = terminalHistoryProducer.LoadOutbox(sessionId);
                    // This is a strict global barrier. A transient failure for
                    // one recipient must not let another recipient's large
                    // transcript backlog delay that missing Snapshot again.
                    if (terminalOutbox == null || recipients.Any(value =>
                        !HasUploadedTerminalSnapshot(terminalOutbox, value.DeviceId, value.KeyEpoch)))
                    {
                        return false;
                    }
                }

                // Output that arrived while the anchor was being published
                // authorizes an immediate checkpoint pass. Do not begin bulk
                // history for the now-stale anchor.
                if (pendingHistorySessions.ContainsKey(sessionId)) return false;

                foreach (var recipient in recipients)
                {
                    if (terminalOutbox != null && !HasUploadedTerminalSnapshot(
                        terminalOutbox,
                        recipient.DeviceId,
                        recipient.KeyEpoch))
                    {
                        durable = false;
                        continue;
                    }
                    else if (terminalOutbox == null &&
                        production != null && production.SnapshotEligible)
                    {
                        // Never let the generic CHUNK path overtake a snapshot
                        // that should exist but was not durably materialized.
                        durable = false;
                        continue;
                    }

                    var outcome = await historyUploader.UploadAsync(
                        sessionId,
                        recipient,
                        events,
                        token,
                        () => pendingHistorySessions.ContainsKey(sessionId)).ConfigureAwait(false);
                    if (outcome != ProductionHistoryUploadOutcome.Durable)
                    {
                        durable = false;
                        continue;
                    }
                    if (pendingHistorySessions.ContainsKey(sessionId)) return false;
                    if (terminalOutbox != null)
                    {
                        while (true)
                        {
                            if (pendingHistorySessions.ContainsKey(sessionId)) return false;
                            var pending = terminalHistoryProducer.PrepareNextUpload(
                                sessionId,
                                recipient);
                            if (pending == null) break;
                            try
                            {
                                if (pending.Artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                                    throw new InvalidDataException("Terminal snapshot was not committed before background history.");
                                var terminalOutcome = await historyUploader.UploadTerminalArtifactAsync(
                                    sessionId,
                                    recipient,
                                    pending,
                                    token).ConfigureAwait(false);
                                if (terminalOutcome != ProductionHistoryUploadOutcome.Durable)
                                {
                                    durable = false;
                                    break;
                                }
                                terminalHistoryProducer.MarkUploaded(pending);
                            }
                            finally { CryptographicOperations.ZeroMemory(pending.SealedObject); }
                        }
                        if (!terminalHistoryProducer.AreAllUploaded(
                            sessionId,
                            terminalOutbox.SnapshotSequence,
                            recipient))
                        {
                            durable = false;
                        }
                        if (production != null && production.SnapshotEligible &&
                            terminalOutbox.SnapshotSequence != production.LastSequence)
                            durable = false;
                    }
                }
                return hasRecipient && durable;
            }
            finally
            {
                foreach (var recipient in recipients)
                {
                    if (recipient.PairwiseKey != null)
                        CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
                }
            }
        }

        internal static bool HasUploadedTerminalSnapshot(
            ProductionTerminalHistoryOutbox outbox,
            Guid recipientDeviceId,
            long keyEpoch)
        {
            if (outbox == null || recipientDeviceId == Guid.Empty || keyEpoch < 1) return false;
            var snapshot = outbox.Artifacts?.SingleOrDefault(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
            if (snapshot == null) return false;
            var delivery = outbox.Deliveries?.Values.SingleOrDefault(
                value => value.RecipientDeviceId == recipientDeviceId && value.KeyEpoch == keyEpoch);
            return delivery != null && delivery.UploadedArtifactIds.Contains(snapshot.ArtifactId);
        }

        private async Task RefreshAuthorizedRecipientsAsync(bool force, CancellationToken token)
        {
            if (!force && DateTime.UtcNow < authorityExpiresUtc) return;
            await authorizedRecipientGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!force && DateTime.UtcNow < authorityExpiresUtc) return;
                var historyAuthorityChanged = false;
                using var response = await SendAsync(
                    HttpMethod.Get,
                    ProductionDeviceGrantContract.GrantsPath(config.PcId),
                    Array.Empty<byte>(),
                    true,
                    null,
                    token).ConfigureAwait(false);
                await EnsureServerSuccessAsync(
                    response,
                    "RECIPIENT_AUTHORIZATION_REFRESH",
                    token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                var authorizedKeys = ParseAuthorizedRecipientKeys(
                    document.RootElement,
                    config.KeyEpoch,
                    out var keyEpoch);
                var retained = new HashSet<Guid>();
                try
                {
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
                        foreach (var previous in authorizedRecipients.Values) RetireSubscriber(previous);
                        authorizedRecipients.Clear();
                        historyAuthorityChanged = true;
                    }

                    foreach (var item in authorizedKeys)
                    {
                        retained.Add(item.Key);
                        var candidate = CreateAuthorizedSubscriber(
                            credentials.Identity.Exchange,
                            credentials.DeviceId,
                            item.Key,
                            item.Value,
                            keyEpoch);
                        if (authorizedRecipients.TryGetValue(item.Key, out var existing) &&
                            SubscriberAuthorityMatches(existing, candidate))
                        {
                            RetireSubscriber(candidate);
                            continue;
                        }
                        ClearSubscriberRoutesForDevice(item.Key);
                        authorizedRecipients[item.Key] = candidate;
                        historyAuthorityChanged = true;
                        if (existing != null) RetireSubscriber(existing);
                    }
                    foreach (var item in authorizedRecipients)
                    {
                        if (retained.Contains(item.Key)) continue;
                        if (authorizedRecipients.TryRemove(item.Key, out var removed))
                        {
                            ClearSubscriberRoutesForDevice(item.Key);
                            RetireSubscriber(removed);
                            historyAuthorityChanged = true;
                        }
                    }
                    authorityExpiresUtc = DateTime.UtcNow.Add(AuthorityCacheDuration);
                }
                finally
                {
                    foreach (var publicKey in authorizedKeys.Values) CryptographicOperations.ZeroMemory(publicKey);
                }
                if (historyAuthorityChanged)
                {
                    QueuePersistedHistoryForReplay();
                    QueueSessionTitleDirectoryRepublish();
                }
            }
            finally { authorizedRecipientGate.Release(); }
        }

        private void QueueSessionTitleDirectoryRepublish()
        {
            foreach (var item in publicationStates)
            {
                lock (item.Value.Sync)
                {
                    if (item.Value.HasSize) MarkRestDirtyLocked(item.Value);
                }
            }
            try { metadataSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private static Dictionary<Guid, byte[]> ParseAuthorizedRecipientKeys(
            JsonElement root,
            long minimumKeyEpoch,
            out long keyEpoch)
        {
            if (minimumKeyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(minimumKeyEpoch));
            if (!root.TryGetProperty("key_epoch", out var epochElement) ||
                !epochElement.TryGetInt64(out keyEpoch) || keyEpoch < minimumKeyEpoch)
            {
                throw new InvalidDataException("PC recipient key epoch is invalid.");
            }
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("PC recipient snapshot is invalid.");

            // The endpoint name remains /grants for wire compatibility, but in
            // production automatic-account mode these entries are the Server's
            // complete authorized recipient set. Permission/grant provenance is
            // deliberately not a client-side subscription gate.
            var authorizedKeys = new Dictionary<Guid, byte[]>();
            try
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!string.Equals(item.GetProperty("device_type").GetString(), "ANDROID", StringComparison.Ordinal)) continue;
                    var deviceId = item.GetProperty("device_id").GetGuid();
                    byte[] publicKey;
                    try
                    {
                        publicKey = Convert.FromBase64String(item.GetProperty("exchange_public_key").GetString());
                    }
                    catch (Exception error) when (error is FormatException || error is ArgumentNullException)
                    {
                        throw new InvalidDataException("PC recipient exchange key is invalid.", error);
                    }
                    if (publicKey.Length != 32 || deviceId == Guid.Empty)
                    {
                        CryptographicOperations.ZeroMemory(publicKey);
                        throw new InvalidDataException("PC recipient exchange key is invalid.");
                    }
                    if (!authorizedKeys.TryAdd(deviceId, publicKey))
                    {
                        CryptographicOperations.ZeroMemory(publicKey);
                        throw new InvalidDataException("PC recipient snapshot contains a duplicate device.");
                    }
                }
                return authorizedKeys;
            }
            catch
            {
                foreach (var publicKey in authorizedKeys.Values) CryptographicOperations.ZeroMemory(publicKey);
                throw;
            }
        }

        private static Subscriber CreateAuthorizedSubscriber(
            DeviceExchangeKeyPair localExchange,
            Guid pcDeviceId,
            Guid recipientDeviceId,
            byte[] exchangePublicKey,
            long keyEpoch)
        {
            if (localExchange == null) throw new ArgumentNullException(nameof(localExchange));
            if (pcDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty || pcDeviceId == recipientDeviceId)
                throw new InvalidDataException("PC recipient identity is invalid.");
            if (exchangePublicKey == null || exchangePublicKey.Length != 32 || keyEpoch < 1)
                throw new InvalidDataException("PC recipient authorization key is invalid.");

            var retainedPublicKey = (byte[])exchangePublicKey.Clone();
            try
            {
                return new Subscriber
                {
                    DeviceId = recipientDeviceId,
                    ExchangePublicKey = retainedPublicKey,
                    PairwiseKey = SessionCrypto.DerivePairwiseKey(
                        localExchange,
                        retainedPublicKey,
                        pcDeviceId,
                        recipientDeviceId,
                        keyEpoch),
                    KeyEpoch = keyEpoch,
                    LastActivityUtc = DateTime.UtcNow
                };
            }
            catch
            {
                CryptographicOperations.ZeroMemory(retainedPublicKey);
                throw;
            }
        }

        private async Task ReplaySubscriberAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ulong resumeFromSequence,
            CancellationToken token)
        {
            // Publish a lightweight replay barrier while holding the socket
            // gate, then perform bounded DPAPI/file recovery without that
            // global gate. Live publication observes ReplayInProgress and
            // stops at this subscriber until the recovered page is ready.
            int maximumEvents;
            long maximumStoredBytes;
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                subscriber = RememberSubscriber(sessionId, subscriber);
                if (!ResetSubscriberCursor(subscriber, resumeFromSequence))
                    throw new InvalidDataException("Subscribe cursor regressed below the durable acknowledgement.");
                lock (subscriber.Sync)
                {
                    if (subscriber.Removed) throw new InvalidDataException("Subscriber is no longer active.");
                    subscriber.ReplayInProgress = true;
                    var remainingEvents = subscriber.FlowWindowEvents - subscriber.SentFrames.Count;
                    var remainingBytes = subscriber.FlowWindowWireBytes > subscriber.InFlightWireBytes
                        ? subscriber.FlowWindowWireBytes - subscriber.InFlightWireBytes
                        : 0;
                    maximumEvents = Math.Max(1, remainingEvents);
                    maximumStoredBytes = checked((long)Math.Max(1UL, remainingBytes));
                }
            }
            finally { socketSendGate.Release(); }

            IList<LocalFrame> frames;
            ulong earliestRetainedSequence;
            ulong latestSequence;
            bool hasMore;
            try
            {
                if (registry == null || !registry.TryReadBufferedEvents(
                    sessionId,
                    resumeFromSequence,
                    maximumEvents,
                    maximumStoredBytes,
                    out frames,
                    out earliestRetainedSequence,
                    out latestSequence,
                    out hasMore))
                {
                    throw new InvalidDataException("Subscribed session is no longer registered.");
                }
                if (resumeFromSequence > latestSequence)
                    throw new InvalidDataException("Subscribe cursor exceeds the session sequence.");
            }
            catch
            {
                lock (subscriber.Sync) subscriber.ReplayInProgress = false;
                RemoveSubscriber(sessionId, subscriber.DeviceId, subscriber);
                throw;
            }

            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (subscriber.Sync)
                {
                    if (subscriber.Removed) return;
                    subscriber.ReplayInProgress = false;
                    if (hasMore) subscriber.DrainRequested = true;
                }

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

                var replayShellType = ManagedShellType.Custom;
                SessionPublicationSnapshot replayMetadata = null;
                if (pendingSessions.TryGetValue(sessionId, out var pendingSession))
                    replayShellType = pendingSession.ShellType;
                if (publicationStates.TryGetValue(sessionId, out var replayState))
                {
                    lock (replayState.Sync)
                    {
                        replayMetadata = CapturePublicationSnapshot(sessionId, replayState);
                        replayShellType = replayMetadata.ShellType;
                    }
                }
                foreach (var frame in frames)
                {
                    var messageType = MapMessageType(frame);
                    if (messageType == MessageType.Unspecified) continue;
                    var payload = BuildRealtimePayload(frame, replayShellType, replayMetadata).ToByteArray();
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
            finally
            {
                lock (subscriber.Sync) subscriber.ReplayInProgress = false;
                socketSendGate.Release();
            }
            // A flow-control drain may have delivered a frame that the global
            // publisher deliberately left uncommitted. Wake it so every active
            // subscriber is rechecked before the publication cursor advances.
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
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
                if (subscriber.ReplayInProgress || subscriber.FlowPaused ||
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
                    await SendWebSocketFrameWithDeadlineAsync(
                        active, cached, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
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
                    ValidateRealtimeEnvelopeSize(serialized.Length);
                    var sendBytes = PrepareNewSubscriberFrame(subscriber, sequence, serialized);
                    if (sendBytes == null) return false;
                    try
                    {
                        await SendWebSocketFrameWithDeadlineAsync(
                            active, sendBytes, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
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
            try
            {
                await SendWebSocketFrameWithDeadlineAsync(
                    active, payload, messageType, endOfMessage, token).ConfigureAwait(false);
            }
            finally { socketSendGate.Release(); }
        }

        private static async Task SendWebSocketFrameWithDeadlineAsync(
            ClientWebSocket active,
            byte[] payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken token)
        {
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            sendCancellation.CancelAfter(RealtimeSendTimeout);
            try
            {
                await active.SendAsync(
                    payload,
                    messageType,
                    endOfMessage,
                    sendCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    "Production Relay send timed out.");
            }
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
            if (frame.Type == LocalFrameType.Title) return MessageType.SessionUpsert;
            if (frame.Type == LocalFrameType.SessionRemoved) return MessageType.SessionRemoved;
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
            return BuildRealtimePayload(frame, ManagedShellType.Custom, null);
        }

        private static IMessage BuildRealtimePayload(
            LocalFrame frame,
            ManagedShellType shellType,
            SessionPublicationSnapshot metadata)
        {
            if (frame.Type == LocalFrameType.Output)
            {
                return new OutputChunkPayload
                {
                    Output = ByteString.CopyFrom(frame.Payload),
                    SourceEventSeq = frame.Sequence,
                    PresentationRevision = frame.Sequence
                };
            }
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
                var state = lifecycle == LocalLifecycleState.Ended ? "ENDED" : LifecycleText(lifecycle);
                var payload = BuildSessionMetadataPayload(shellType, state, metadata);
                if (lifecycle == LocalLifecycleState.Ended) payload.ExitCode = exitCode;
                return payload;
            }
            if (frame.Type == LocalFrameType.Title)
            {
                var payload = BuildSessionMetadataPayload(shellType, "CHANGED", metadata);
                // Assignment preserves proto3 optional presence even when the
                // normalized title is empty, which explicitly clears clients.
                payload.Title = LocalPayloadCodec.ParseTitle(frame.Payload);
                return payload;
            }
            if (frame.Type == LocalFrameType.SessionRemoved)
            {
                LocalPayloadCodec.ParseSessionRemoved(frame.Payload);
                return new Google.Protobuf.WellKnownTypes.Empty();
            }
            return new SessionUpsertPayload { Lifecycle = "CHANGED" };
        }

        private static SessionUpsertPayload BuildSessionMetadataPayload(
            ManagedShellType shellType,
            string lifecycle,
            SessionPublicationSnapshot metadata)
        {
            var payload = new SessionUpsertPayload
            {
                ShellType = ShellTypeText(shellType),
                Lifecycle = lifecycle,
                // The outer Envelope provides realtime E2EE, but this field's
                // contract still says ciphertext. Leave it empty until a
                // versioned recipient-bound title envelope is defined.
                TitleCipher = ByteString.Empty
            };
            if (metadata != null && metadata.HasSize)
            {
                payload.Columns = checked((uint)metadata.Columns);
                payload.Rows = checked((uint)metadata.Rows);
            }
            return payload;
        }

        private static HistoryEventV1 BuildHistoryEvent(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.SessionRemoved) return null;
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
            if (result == LocalRemoteInputResult.InputPayloadRejected) return "INPUT_PAYLOAD_REJECTED";
            return "CONPTY_WRITE_FAILED";
        }

        internal static bool TryEncodeTypedInput(TerminalInputV1 wire, out byte[] encoded)
        {
            encoded = null;
            if (wire == null || wire.FormatVersion != 1) return false;
            try
            {
                CoreTerminalInput input;
                var modifiers = CoreTerminalInputModifiers.None;
                if (wire.Modifiers != null)
                {
                    if (wire.Modifiers.Control) modifiers |= CoreTerminalInputModifiers.Control;
                    if (wire.Modifiers.Alt) modifiers |= CoreTerminalInputModifiers.Alt;
                    if (wire.Modifiers.Shift) modifiers |= CoreTerminalInputModifiers.Shift;
                    if (wire.Modifiers.Meta) modifiers |= CoreTerminalInputModifiers.Meta;
                }
                switch ((int)wire.Kind)
                {
                    case 1:
                        if (wire.ValueCase != TerminalInputV1.ValueOneofCase.Text) return false;
                        input = CoreTerminalInput.FromText(wire.Text, modifiers);
                        break;
                    case 2:
                        if (wire.ValueCase != TerminalInputV1.ValueOneofCase.Key ||
                            !Enum.IsDefined(typeof(CoreTerminalKey), (int)wire.Key)) return false;
                        input = CoreTerminalInput.FromKey((CoreTerminalKey)(int)wire.Key, modifiers);
                        break;
                    case 3:
                        if (wire.ValueCase != TerminalInputV1.ValueOneofCase.Paste ||
                            modifiers != CoreTerminalInputModifiers.None) return false;
                        input = CoreTerminalInput.FromPaste(wire.Paste.Memory);
                        break;
                    case 4:
                        if (wire.ValueCase != TerminalInputV1.ValueOneofCase.RawBytes ||
                            modifiers != CoreTerminalInputModifiers.None) return false;
                        input = CoreTerminalInput.FromRawBytes(wire.RawBytes.Memory);
                        break;
                    default:
                        return false;
                }
                encoded = TypedInputEncoder.Encode(input);
                return true;
            }
            catch (ArgumentException)
            {
                if (encoded != null) CryptographicOperations.ZeroMemory(encoded);
                encoded = null;
                return false;
            }
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
                // FLOW_CONTROL epochs are scoped to one authenticated
                // SUBSCRIBE generation. Keep the retained pause/window state
                // until the new generation explicitly supersedes it, while
                // allowing that generation to restart at epoch 1.
                subscriber.FlowControlEpoch = 0;
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
            return DetachSubscriber(sessionId, deviceId, expected);
        }

        private bool DetachSubscriber(Guid sessionId, Guid deviceId, Subscriber expected)
        {
            if (!subscribers.TryGetValue(sessionId, out var sessionSubscribers)) return false;
            var removed = ((ICollection<KeyValuePair<Guid, Subscriber>>)sessionSubscribers).Remove(
                new KeyValuePair<Guid, Subscriber>(deviceId, expected));
            if (!removed) return false;
            if (sessionSubscribers.IsEmpty)
            {
                ((ICollection<KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>>)subscribers).Remove(
                    new KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>(sessionId, sessionSubscribers));
            }
            return true;
        }

        private void ClearSubscriberRoutesForDevice(Guid deviceId)
        {
            foreach (var route in connectionSubscriberRoutes)
            {
                if (route.Value.DeviceId != deviceId) continue;
                if (!((ICollection<KeyValuePair<string, Subscriber>>)connectionSubscriberRoutes).Remove(route))
                    continue;
                foreach (var session in subscribers)
                    DetachSubscriber(session.Key, deviceId, route.Value);
                RetireSubscriber(route.Value);
            }
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
            var retained = new HashSet<Subscriber>();
            foreach (var session in subscribers.Values)
            {
                foreach (var subscriber in session.Values)
                {
                    retained.Add(subscriber);
                }
                session.Clear();
            }
            subscribers.Clear();
            foreach (var route in connectionSubscriberRoutes.Values) retained.Add(route);
            connectionSubscriberRoutes.Clear();
            foreach (var subscriber in retained) RetireSubscriber(subscriber);
        }

        private void ClearRealtimeConnectionState()
        {
            ClearSubscribers();
            leaseStatuses.Clear();
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
                    if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
                    response.Dispose();
                    if (attempt > 0) throw ReauthenticationRequired();
                }
            }
            throw ReauthenticationRequired();
        }

        private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken token)
        {
            if (IsReauthenticationRequired) throw new ProductionReauthenticationRequiredException();
            if (!forceRefresh && !credentials.ShouldRefresh(DateTime.UtcNow, TimeSpan.FromMinutes(1)))
                return credentials.GetAccessToken();
            if (!credentials.CanRefresh || string.IsNullOrWhiteSpace(config.OIDCIssuer) || string.IsNullOrWhiteSpace(config.OIDCClientId))
            {
                if (forceRefresh) throw ReauthenticationRequired();
                return credentials.GetAccessToken();
            }
            await tokenRefreshGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsReauthenticationRequired) throw new ProductionReauthenticationRequiredException();
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
                    throw ReauthenticationRequired();
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

        private bool IsReauthenticationRequired => Volatile.Read(ref reauthenticationRequired) != 0;

        private ProductionReauthenticationRequiredException ReauthenticationRequired()
        {
            MarkReauthenticationRequired();
            return new ProductionReauthenticationRequiredException();
        }

        private void MarkReauthenticationRequired()
        {
            Interlocked.Exchange(ref reauthenticationRequired, 1);
            statusExpiresUtc = DateTime.MaxValue;
            SetRelayState(ProductionRelayState.Failed, "ACCOUNT_REAUTH_REQUIRED");
            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            try { active?.Abort(); }
            catch (ObjectDisposedException) { }
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
            Task activeMetadataWorker;
            Task activeStatusRefresh;
            lock (lifecycleSync)
            {
                activeWorker = worker;
                activeHistoryWorker = historyWorker;
                activeMetadataWorker = metadataWorker;
                activeStatusRefresh = statusRefreshWorker;
            }
            if (activeWorker != null)
            {
                try { activeWorker.GetAwaiter().GetResult(); }
                catch (AggregateException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeHistoryWorker != null)
            {
                try { activeHistoryWorker.GetAwaiter().GetResult(); }
                catch (AggregateException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeMetadataWorker != null)
            {
                try { activeMetadataWorker.GetAwaiter().GetResult(); }
                catch (AggregateException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeStatusRefresh != null)
            {
                try { activeStatusRefresh.GetAwaiter().GetResult(); }
                catch (AggregateException) { }
                catch (ProductionReauthenticationRequiredException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

            if (agentUpdateWorker != null) agentUpdateWorker.Dispose();
            if (fileOperationWorker != null) fileOperationWorker.Dispose();
            if (historyUploader != null) historyUploader.Dispose();
            fileUploadHttp.Dispose();
            http.Dispose();
            credentials.Dispose();
            ClearSubscribers();
            foreach (var recipient in authorizedRecipients.Values)
            {
                RetireSubscriber(recipient);
            }
            authorizedRecipients.Clear();
            pendingHistorySessions.Clear();
            terminalHistorySessions.Clear();
            pendingHistoryFrames.Clear();
            historyReconcileSessions.Clear();
            terminalHistoryPersistenceFailures.Clear();
            publishedSequences.Clear();
            foreach (var state in publicationStates.Values) state.RestUpsertGate.Dispose();
            publicationStates.Clear();
            localInputEpochs.Clear();
            inputSequences.Clear();
            terminationRequests.Clear();
            eventSignal.Dispose();
            metadataSignal.Dispose();
            historySignal.Dispose();
            socketSendGate.Dispose();
            authorizedRecipientGate.Dispose();
            statusRefreshGate.Dispose();
            tokenRefreshGate.Dispose();
            reconnectSignal.Dispose();
            cancellation.Dispose();
        }
    }
}

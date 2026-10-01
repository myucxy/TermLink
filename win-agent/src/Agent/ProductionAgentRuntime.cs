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
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
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

    /// <summary>
    /// Tracks the application cursor independently from transport receipt.
    /// Later control envelopes may finish synchronously while an earlier input
    /// is still awaiting its durable SessionHost result; the cursor must not
    /// cross that pending input.
    /// </summary>
    internal sealed class ProductionDirectIngressApplyTracker
    {
        private readonly object sync = new object();
        private readonly HashSet<ulong> observed = new HashSet<ulong>();
        private readonly HashSet<ulong> applied = new HashSet<ulong>();
        private bool initialized;
        private ulong highestContiguous;
        private ulong duplicateApplyCount;

        internal ulong HighestContiguous
        {
            get { lock (sync) return highestContiguous; }
        }

        internal ulong DuplicateApplyCount
        {
            get { lock (sync) return duplicateApplyCount; }
        }

        internal void Observe(ulong sequence)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            lock (sync)
            {
                InitializeLocked(sequence);
                if (sequence > highestContiguous) observed.Add(sequence);
            }
        }

        internal ulong MarkApplied(ulong sequence)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            lock (sync)
            {
                InitializeLocked(sequence);
                if (sequence <= highestContiguous || applied.Contains(sequence))
                {
                    if (duplicateApplyCount < ulong.MaxValue) duplicateApplyCount++;
                    return 0;
                }
                observed.Add(sequence);
                applied.Add(sequence);
                var advanced = 0UL;
                while (highestContiguous < ulong.MaxValue)
                {
                    var next = highestContiguous + 1;
                    if (!observed.Contains(next) || !applied.Contains(next)) break;
                    observed.Remove(next);
                    applied.Remove(next);
                    highestContiguous = next;
                    advanced = next;
                }
                return advanced;
            }
        }

        private void InitializeLocked(ulong sequence)
        {
            if (initialized) return;
            highestContiguous = sequence - 1;
            initialized = true;
        }
    }

    // The Relay and future Direct carriers share this owned ingress value. The
    // parsed protobuf is used for routing/decryption, while RawBytes remains
    // the exact wire message for replay identity and byte-level arbitration.
    // The owner must dispose the value only after all consumers finish.
    internal sealed class ProductionInboundEnvelope : IDisposable
    {
        private byte[] rawBytes;

        private ProductionInboundEnvelope(Envelope envelope, byte[] rawBytes)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            this.rawBytes = rawBytes ?? throw new ArgumentNullException(nameof(rawBytes));
        }

        internal Envelope Envelope { get; }

        internal ReadOnlyMemory<byte> RawBytes
        {
            get
            {
                var value = Volatile.Read(ref rawBytes);
                if (value == null) throw new ObjectDisposedException(nameof(ProductionInboundEnvelope));
                return value;
            }
        }

        internal static ProductionInboundEnvelope Parse(byte[] rawBytes)
        {
            if (rawBytes == null) throw new ArgumentNullException(nameof(rawBytes));
            try
            {
                var envelope = Envelope.Parser.ParseFrom(rawBytes);
                return new ProductionInboundEnvelope(envelope, rawBytes);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(rawBytes);
                throw;
            }
        }

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref rawBytes, null);
            if (value != null) CryptographicOperations.ZeroMemory(value);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Small, testable ownership seam shared by first-send and cached replay.
    /// A Direct failure never performs a second managed fallback: native owns
    /// that decision, and the durable subscriber cache remains the retry source.
    /// </summary>
    internal static class ProductionAgentExactEnvelopeRouter
    {
        internal static bool AcceptOwnedRelayEnvelope(
            ProductionRelayExactOutbox outbox,
            ProductionDirectDataRelayEnvelope envelope)
        {
            if (outbox == null) throw new ArgumentNullException(nameof(outbox));
            var outcome = outbox.TryEnqueue(envelope);
            if (outcome == ProductionRelayExactEnqueueOutcome.RejectedNotOwned)
            {
                throw new InvalidOperationException(
                    "Relay 重试队列已满，Envelope 仍由调用方持有。/ " +
                    "The Relay retry outbox is full; the caller still owns the Envelope.");
            }
            return outcome == ProductionRelayExactEnqueueOutcome.OwnedSent;
        }

        internal static async Task<bool> RouteAsync(
            ProductionDirectDataRuntime directRuntime,
            ProductionRelayExactOutbox relayOutbox,
            Guid peerDeviceId,
            Guid sessionId,
            ulong sequence,
            byte[] exactEnvelope,
            bool directEligible,
            Action<Guid, string> observeDirectFault,
            CancellationToken token)
        {
            if (relayOutbox == null) throw new ArgumentNullException(nameof(relayOutbox));
            if (exactEnvelope == null || exactEnvelope.Length == 0)
                throw new ArgumentException("An exact Envelope is required.", nameof(exactEnvelope));
            // Native recovery is allowed to return this exact Envelope to
            // Relay synchronously. Refuse new Direct work while its bounded
            // recovery owner has no room. CanAccept is advisory, so the
            // enqueue path still handles a concurrent capacity race.
            if (!relayOutbox.CanAccept(new ProductionDirectDataRelayEnvelope
                {
                    PeerDeviceId = peerDeviceId,
                    SessionId = sessionId,
                    EnvelopeSequence = sequence,
                    ExactEnvelope = exactEnvelope
                }))
                return false;
            if (directRuntime != null)
            {
                try
                {
                    var outcome = await directRuntime.RouteEnvelopeAsync(
                        peerDeviceId,
                        sessionId,
                        sequence,
                        exactEnvelope,
                        directEligible,
                        token).ConfigureAwait(false);
                    if (outcome != ProductionDirectDataRouteOutcome.Dropped) return true;
                    // Native no longer owns the attempted transfer and Relay
                    // did not accept it. Keep the subscriber's exact bytes as
                    // authority and wake the publisher immediately; a quiet
                    // session must not wait for another terminal frame.
                    try { observeDirectFault?.Invoke(peerDeviceId, "DIRECT_ROUTE_DROPPED"); }
                    catch (Exception callbackError) when (callbackError is not OutOfMemoryException) { }
                    return false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException &&
                    error is not StackOverflowException && error is not AccessViolationException)
                {
                    // Do not issue a second fallback here. The native actor
                    // either transferred the exact Envelope to Relay already,
                    // or the subscriber cache still owns the retry source.
                    try { observeDirectFault?.Invoke(peerDeviceId, NormalizeFault(error)); }
                    catch (Exception callbackError) when (callbackError is not OutOfMemoryException) { }
                    return false;
                }
            }

            var owned = exactEnvelope.ToArray();
            try
            {
                AcceptOwnedRelayEnvelope(relayOutbox,
                    new ProductionDirectDataRelayEnvelope
                    {
                        PeerDeviceId = peerDeviceId,
                        SessionId = sessionId,
                        EnvelopeSequence = sequence,
                        ExactEnvelope = owned
                    });
                owned = null;
                // OwnedDeferred is successful publication: the long-lived
                // Relay worker now owns retry across socket generations.
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException &&
                error is not StackOverflowException && error is not AccessViolationException)
            {
                return false;
            }
            finally
            {
                if (owned != null) CryptographicOperations.ZeroMemory(owned);
            }
        }

        private static string NormalizeFault(Exception error)
        {
            var value = error?.Message?.Trim();
            if (string.IsNullOrEmpty(value)) return "DIRECT_ROUTE_FAILED";
            var result = new StringBuilder(Math.Min(64, value.Length));
            foreach (var character in value.ToUpperInvariant())
            {
                if (result.Length >= 64) break;
                result.Append(char.IsAsciiLetterOrDigit(character) || character == '_'
                    ? character
                    : '_');
            }
            return result.Length == 0 ? "DIRECT_ROUTE_FAILED" : result.ToString();
        }
    }

    internal sealed partial class ProductionAgentRuntime : IDisposable
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
        private const int HostHistoryResponseChunkBytes = 512 * 1024;
        private static readonly bool HostHistoryDataPlaneEnabled = true;
        private static readonly TimeSpan MetadataUpsertCadence = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SessionMetadataHeartbeatInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PCHeartbeatInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DirectRoutingPolicyRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RealtimeHandshakeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RealtimeSendTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HostHistoryChunkAckTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PersistedHistoryIdleDelay = TimeSpan.FromSeconds(5);
        private const int MaxRememberedTerminationRequests = 4096;
        private const int MaxRememberedInputRequests = 4096;
        private const string TerminalSnapshotEnvironment = "TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V2";
        private const string LegacyTerminalSnapshotEnvironment = "TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V1";
        private const string TypedInputEnvironment = "TERMLINK_ENABLE_TYPED_INPUT_V1";
        private const string TerminalCoreShadowEnvironment = "TERMLINK_ENABLE_TERMINAL_CORE_SHADOW";
        private const string DirectTransportShadowEnvironment =
            "TERMLINK_ENABLE_DIRECT_TRANSPORT_SHADOW";
        // Release and isolated DirectAcceptance builds contain the data plane.
        // Server capability and the portal-owned routing policy remain the
        // runtime authority; no process environment variable can bypass them.
#if TERMLINK_DIRECT_DATA_PLANE
        internal const bool DirectTransportDataPlaneBuildEnabled = true;
#else
        internal const bool DirectTransportDataPlaneBuildEnabled = false;
#endif
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
            public byte[] SigningPublicKey;
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
            // SentFrames is the subscriber-scoped byte-identical ciphertext
            // cache. GenerationInFlightSequences is the independently bounded
            // set advertised to the current SUBSCRIBE generation. A reconnect
            // must retain the former while resetting the latter, otherwise a
            // completely full old window can never retransmit its first frame.
            public readonly HashSet<ulong> GenerationInFlightSequences = new HashSet<ulong>();
            public readonly SortedDictionary<ulong, byte[]> SentFrames = new SortedDictionary<ulong, byte[]>();
            public bool DrainRequested;
            public bool ReplayInProgress;
            public bool Removed;
            public ulong LastResizeRequestSequence;
        }
        private sealed class DirectCutoverReservation
        {
            public Guid SessionId;
            public Guid PeerDeviceId;
            public Subscriber Subscriber;
            public long KeyEpoch;
            public ulong FirstDirectSequence;
        }
        private sealed class InputReplayRecord
        {
            public readonly object Sync = new object();
            public Guid SessionId;
            public Guid SenderDeviceId;
            public byte[] EnvelopeSha256;
            public ulong EnvelopeSequence;
            public LocalRemoteInputResult? Result;
            public ulong ResultSequence;
        }
        private sealed class AuthorizedRecipientKeyMaterial : IDisposable
        {
            public AuthorizedRecipientKeyMaterial(byte[] exchangePublicKey, byte[] signingPublicKey)
            {
                ExchangePublicKey = exchangePublicKey ??
                    throw new ArgumentNullException(nameof(exchangePublicKey));
                SigningPublicKey = signingPublicKey ??
                    throw new ArgumentNullException(nameof(signingPublicKey));
            }

            public byte[] ExchangePublicKey { get; private set; }
            public byte[] SigningPublicKey { get; private set; }

            public void Dispose()
            {
                if (ExchangePublicKey != null)
                    CryptographicOperations.ZeroMemory(ExchangePublicKey);
                if (SigningPublicKey != null)
                    CryptographicOperations.ZeroMemory(SigningPublicKey);
                ExchangePublicKey = null;
                SigningPublicKey = null;
            }
        }
        private sealed class LeaseStatus
        {
            public Guid HolderDeviceId;
            public Guid AuthorizedSenderDeviceId;
            public Guid LeaseId;
            public long Version;
            public long PolicyVersion;
            public string ControlMode;
            public DateTime ExpiresAtUtc;
            public DateTime ValidatedAtUtc;
        }
        private readonly ProductionAgentConfig config;
        private readonly ProductionCredentials credentials;
        private readonly ProductionRealtimeConnectionSupervisor connectionSupervisor;
        private readonly ProductionRealtimeCapabilityState realtimeCapabilityState =
            new ProductionRealtimeCapabilityState();
        private readonly IProductionDirectPeerRouteRegistry directPeerRoutes;
        private readonly ProductionDirectTransportSupervisor directTransportSupervisor;
        private readonly ProductionRelayExactOutbox relayExactOutbox;
        private readonly ProductionDirectDataPlaneAdmission directDataAdmission;
        private readonly ProductionDirectTransportSignalingClient directSignaling;
        private readonly ProductionDirectDataRuntime directDataRuntime;
        private readonly ProductionDirectMultipairShadowRuntime directShadowRuntime;
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
        private readonly ConcurrentDictionary<Guid, long> pendingHistorySessions = new ConcurrentDictionary<Guid, long>();
        private readonly ConcurrentDictionary<Guid, byte> terminalHistorySessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentQueue<Guid> persistedHistoryBacklog = new ConcurrentQueue<Guid>();
        private readonly ConcurrentDictionary<Guid, byte> persistedHistoryBacklogSessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte> persistedHistoryWorkSessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte> activeHistoryWorkSessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<LocalFrame>> pendingHistoryFrames = new ConcurrentDictionary<Guid, ConcurrentQueue<LocalFrame>>();
        private readonly ConcurrentDictionary<Guid, byte> historyReconcileSessions = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, string> terminalHistoryPersistenceFailures = new ConcurrentDictionary<Guid, string>();
        private readonly SemaphoreSlim historySignal = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim authorizedRecipientGate = new SemaphoreSlim(1, 1);
        // Subscriber publication and Direct authority retirement form one
        // synchronous transaction. REST refresh remains serialized by
        // authorizedRecipientGate; this lock prevents a late replay/cutover
        // continuation from republishing an old Subscriber while that refresh
        // retires its key generation.
        private readonly object subscriberAuthoritySync = new object();
        private long directAuthorityKeyEpoch;
        private int directAuthorityTransitioning;
        private readonly SemaphoreSlim statusRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim tokenRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim directConfigRefreshGate = new SemaphoreSlim(1, 1);
        private readonly object persistedHistoryCancellationSync = new object();
        private readonly ConcurrentDictionary<Guid, LeaseStatus> leaseStatuses = new ConcurrentDictionary<Guid, LeaseStatus>();
        // Direct input authorization is sender-bound. Keep it separate from
        // the one-per-session status snapshot so two collaborative Android
        // controllers do not continually evict each other's one-second cache.
        private readonly ConcurrentDictionary<string, LeaseStatus> inputLeaseAuthorizations =
            new ConcurrentDictionary<string, LeaseStatus>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, ulong> localInputEpochs = new ConcurrentDictionary<Guid, ulong>();
        private readonly ConcurrentDictionary<string, ulong> inputSequences = new ConcurrentDictionary<string, ulong>();
        private readonly ConcurrentDictionary<Guid, InputReplayRecord> inputReplayRecords =
            new ConcurrentDictionary<Guid, InputReplayRecord>();
        private readonly ConcurrentQueue<Guid> inputReplayOrder = new ConcurrentQueue<Guid>();
        private readonly ConcurrentDictionary<Guid, byte> pendingPcToAndroidCutovers =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<string, ProductionDirectIngressApplyTracker>
            directIngressApplyStates =
                new ConcurrentDictionary<string, ProductionDirectIngressApplyTracker>(
                StringComparer.Ordinal);
        // Admission and eviction are one transaction. ConcurrentDictionary by
        // itself cannot enforce a hard bound when every retained request is
        // still awaiting its durable SessionHost result.
        private readonly object inputReplayAdmissionSync = new object();
        private readonly ConcurrentDictionary<Guid, Guid> terminationRequests = new ConcurrentDictionary<Guid, Guid>();
        private readonly ConcurrentDictionary<Guid, byte> resizeRequests = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, ProductionHostHistoryResponseRequest> hostHistoryRequests =
            new ConcurrentDictionary<Guid, ProductionHostHistoryResponseRequest>();
        private readonly ConcurrentDictionary<Guid, byte> destroyedHistorySessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, Task> historyDeletionTasks =
            new ConcurrentDictionary<Guid, Task>();
        private readonly SemaphoreSlim hostHistoryResponseGate = new SemaphoreSlim(2, 2);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Channel<ProductionDirectDataIngressDelivery> commonIngressQueue;
        private readonly ProductionTerminalHistoryStore terminalHistoryStore;
        private readonly ProductionTerminalHistoryProducer terminalHistoryProducer;
        private readonly ProductionHostHistoryService hostHistoryService;
        private readonly bool terminalSnapshotEnabled;
        private readonly bool typedInputEnabled;
        private readonly bool terminalCoreShadowEnabled;
        private readonly bool directTransportShadowEnabled;
        private readonly string registryControlPipeName;
        private readonly ConcurrentDictionary<Guid, string> terminalCoreShadowStates =
            new ConcurrentDictionary<Guid, string>();
        private static readonly TermLink.TerminalCore.VtTerminalInputEncoder TypedInputEncoder =
            new TermLink.TerminalCore.VtTerminalInputEncoder();
        private readonly object lifecycleSync = new object();
        private readonly object statusSync = new object();
        private SessionRegistry registry;
        private Task worker;
        private Task historyWorker;
        private Task priorityHistoryWorker;
        private Task metadataWorker;
        private Task commonIngressWorker;
        private Task directRoutingPolicyWorker;
        private ClientWebSocket socket;
        private ProductionHistoryUploader historyUploader;
        private ProductionFileOperationWorker fileOperationWorker;
        private ProductionAgentUpdateWorker agentUpdateWorker;
        private PostRegistrationWorkerStartGate postRegistrationWorkerStartGate;
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
        private int cleanupStarted;
        private int reauthenticationRequired;
        private NetworkAvailabilityChangedEventHandler networkAvailabilityHandler;
        private NetworkAddressChangedEventHandler networkAddressHandler;
        private Task directShadowCleanupTask;
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
        private readonly ConcurrentDictionary<Guid, ProductionDirectDataPeerSnapshot>
            directAcceptancePeerSnapshots =
                new ConcurrentDictionary<Guid, ProductionDirectDataPeerSnapshot>();
        private readonly ProductionDirectAcceptanceDiagnosticsServer directAcceptanceDiagnostics;
#endif
        private long lastLiveHistoryActivityTick = Environment.TickCount64;
        private CancellationTokenSource persistedHistoryCancellation;
        private Guid persistedHistoryCancellationSessionId;
        private long nextHistoryWorkTicket;

        private ProductionAgentRuntime(ProductionAgentConfig config, ProductionCredentials credentials)
            : this(config, credentials, new ProductionReconnectPreferences(), null)
        {
        }

        private ProductionAgentRuntime(
            ProductionAgentConfig config,
            ProductionCredentials credentials,
            ProductionReconnectPreferences reconnectPreferences,
            string registryControlPipeName)
        {
            this.config = config;
            this.credentials = credentials;
            directAuthorityKeyEpoch = config.KeyEpoch;
            connectionSupervisor = new ProductionRealtimeConnectionSupervisor(
                reconnectPreferences ?? throw new ArgumentNullException(nameof(reconnectPreferences)));
            directPeerRoutes = new ProductionDirectPeerRouteRegistry();
            directTransportSupervisor = new ProductionDirectTransportSupervisor();
            relayExactOutbox = new ProductionRelayExactOutbox();
            directDataAdmission = new ProductionDirectDataPlaneAdmission(
                DirectTransportDataPlaneBuildEnabled);
            commonIngressQueue = Channel.CreateBounded<ProductionDirectDataIngressDelivery>(
                new BoundedChannelOptions(128)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            this.registryControlPipeName = string.IsNullOrWhiteSpace(registryControlPipeName)
                ? TermLinkControlPipe.DefaultName()
                : registryControlPipeName.Trim();
            http = ProductionTls.CreateHttpClient(config.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
            fileUploadHttp = ProductionTls.CreateHttpClient(config.ServerUrl);
            // A selected artifact can be several GiB. Its presigned upload is
            // governed by cancellation and the server expiry instead of the
            // short REST metadata timeout.
            fileUploadHttp.Timeout = Timeout.InfiniteTimeSpan;
            terminalSnapshotEnabled = IsTerminalSnapshotRuntimeEnabled(
                Environment.GetEnvironmentVariable(TerminalSnapshotEnvironment) ??
                Environment.GetEnvironmentVariable(LegacyTerminalSnapshotEnvironment));
            typedInputEnabled = IsTypedInputRuntimeEnabled(
                Environment.GetEnvironmentVariable(TypedInputEnvironment));
            terminalCoreShadowEnabled = IsTerminalCoreShadowRuntimeEnabled(
                Environment.GetEnvironmentVariable(TerminalCoreShadowEnvironment));
            directTransportShadowEnabled = IsDirectTransportShadowRuntimeEnabled(
                Environment.GetEnvironmentVariable(DirectTransportShadowEnvironment));
            ProductionDirectTransportSignalingClient createdSignaling = null;
            ProductionDirectDataRuntime createdDataRuntime = null;
            ProductionDirectMultipairShadowRuntime createdShadowRuntime = null;
            if (directTransportShadowEnabled || DirectTransportDataPlaneBuildEnabled)
            {
                try
                {
                    var serverOrigin = NormalizeServerOrigin(config.ServerUrl);
                    createdSignaling = new ProductionDirectTransportSignalingClient(
                        serverOrigin,
                        credentials.DeviceId,
                        credentials.Identity,
                        GetDirectShadowAccessTokenAsync,
                        enabled: true,
                        multipairV2Enabled: true,
                        directDataV1Enabled: DirectTransportDataPlaneBuildEnabled);
                    createdSignaling.StateChanged += ObserveDirectSignalingState;
                    createdDataRuntime = new ProductionDirectDataRuntime(
                        credentials.DeviceId,
                        createdSignaling,
                        new ProductionDirectDataRuntimeOptions
                        {
                            EnqueueRelayEnvelope = envelope =>
                                ProductionAgentExactEnvelopeRouter.AcceptOwnedRelayEnvelope(
                                    relayExactOutbox, envelope),
                            EnqueueIngress = EnqueueCommonIngress,
                            EnqueueNativeAction = DisposeNativeAction,
                            HandlePrePromotionSignalAsync = DispatchPrePromotionSignalAsync,
                            ParseIngressMetadata = ParseDirectIngressMetadata,
                            PeerStateChanged = ObserveDirectDataPeerState,
                            SignalingReaderFaulted = ObserveDirectSignalingReaderFault,
                            RenewTicketAsync = async (peerDeviceId, connectionId, currentTicket,
                                    cancellationToken) =>
                            {
                                var renewal = await createdSignaling.RenewTicketAsync(
                                        peerDeviceId, connectionId, currentTicket, cancellationToken)
                                    .ConfigureAwait(false);
                                return renewal.Status == ProductionDirectSignalingCallStatus.Success
                                    ? renewal.Value
                                    : null;
                            }
                        });
                    createdShadowRuntime = new ProductionDirectMultipairShadowRuntime(
                        enabled: true,
                        localDeviceId: credentials.DeviceId,
                        signalKeyId: DeviceKeyId(credentials.DeviceId),
                        identity: credentials.Identity,
                        supervisor: directTransportSupervisor,
                        signalingFactory: () => createdSignaling,
                        peerProvider: SnapshotDirectPeerAuthorizationsAsync,
                        diagnostics: ProductionDirectTransportDiagnostics.CreateForProcess(),
                        sharedSignaling: createdSignaling,
                        // Preserve V4 shadow evidence while the production V5
                        // data plane remains build-gated. Enabling the build
                        // gate switches this exact seam to one-shot handoff.
                        promoteToDataRuntimeAsync: DirectTransportDataPlaneBuildEnabled
                            ? PromoteDirectPeerAsync
                            : null);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Direct is optional shadow evidence. Initialization failure
                    // must never prevent the authoritative Relay runtime from starting.
                    if (createdSignaling != null)
                        createdSignaling.StateChanged -= ObserveDirectSignalingState;
                    try { createdShadowRuntime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { }
                    try { createdDataRuntime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { }
                    if (createdDataRuntime == null && createdSignaling != null)
                    {
                        try { createdSignaling.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                        catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { }
                    }
                    createdSignaling = null;
                    createdDataRuntime = null;
                    createdShadowRuntime = null;
                }
            }
            directSignaling = createdSignaling;
            directDataRuntime = createdDataRuntime;
            directShadowRuntime = createdShadowRuntime;
            var historyRoot = ProductionTerminalHistoryRoot(config);
            MigrateLegacyTerminalHistory(config.PcId, historyRoot);
            terminalHistoryStore = new ProductionTerminalHistoryStore(historyRoot, credentials.DeviceId);
            terminalHistoryStore.FrameObserved += ObserveCatalogFrame;
            terminalHistoryProducer = new ProductionTerminalHistoryProducer(
                terminalHistoryStore,
                credentials.DeviceId,
                terminalCoreShadowEnabled,
                terminalSnapshotEnabled,
                ObserveTerminalCoreShadow);
            hostHistoryService = new ProductionHostHistoryService(
                terminalHistoryStore,
                terminalHistoryProducer,
                credentials.DeviceId,
                sessionId => destroyedHistorySessions.ContainsKey(sessionId) ||
                    terminalHistoryStore.IsSessionDeleted(sessionId),
                WriteHostHistoryDiagnostic);
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
            directAcceptanceDiagnostics =
                ProductionDirectAcceptanceDiagnosticsServer.TryCreateFromEnvironment(
                    CaptureDirectAcceptanceDiagnostics);
#endif
        }

        // Temporary host-history diagnostics: bounded structural metadata only
        // (session/kind/status), never terminal text or key material.
        private static void WriteHostHistoryDiagnostic(string message)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink", "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "TermLink-background-runtime.log"),
                    DateTime.UtcNow.ToString("O") + " host-history-diag " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never become a request-path dependency.
            }
        }

        private static string ProductionTerminalHistoryRoot(ProductionAgentConfig config)
        {
            if (config == null || config.PcId == Guid.Empty)
                throw new ArgumentException("PC configuration is required.", nameof(config));
            var machineIdentity = string.IsNullOrWhiteSpace(config.MachineId)
                ? config.PcId.ToString("N")
                : config.MachineId;
            if (machineIdentity.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("PC machine identity cannot be used as a history path.");
            return Path.Combine(
                Path.GetDirectoryName(ProductionAgentConfig.DefaultPath),
                "Data",
                "History",
                machineIdentity);
        }

        private static void MigrateLegacyTerminalHistory(Guid pcId, string destination)
        {
            var legacy = Path.Combine(
                Path.GetDirectoryName(ProductionAgentConfig.DefaultPath),
                "production-history",
                pcId.ToString("N"));
            if (Directory.Exists(destination) || !Directory.Exists(legacy)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try { Directory.Move(legacy, destination); }
            catch (IOException)
            {
                // A concurrent runtime may already own either path. Never copy
                // mutable WAL files behind its back; the next clean start can retry.
            }
        }

        // Canonical terminal-core Snapshot V2 is the production authority.
        // Operators retain an explicit local rollback switch; an unset value
        // enables the production path.
        internal static bool IsTerminalSnapshotRuntimeEnabled(string value) =>
            !string.Equals(value, "0", StringComparison.Ordinal) &&
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        internal static bool IsTypedInputRuntimeEnabled(string value) =>
            !string.Equals(value, "0", StringComparison.Ordinal);

        // Shadowing cannot change user-visible terminal state and is enabled by default so every
        // packaged Windows build contributes migration evidence.  Operators retain an immediate
        // local rollback that does not affect the existing Portable producer.
        internal static bool IsTerminalCoreShadowRuntimeEnabled(string value)
        {
            var normalized = value?.Trim();
            return !string.Equals(normalized, "0", StringComparison.Ordinal) &&
                !string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDirectTransportShadowRuntimeEnabled(string value)
        {
            var normalized = value?.Trim();
            return string.Equals(normalized, "1", StringComparison.Ordinal) ||
                string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase);
        }

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
            return BuildHelloPayloadWithFeatures(
                terminalSnapshotEnabled,
                typedInputEnabled,
                directTransportDataEnabled: false);
        }

        internal static byte[] BuildHelloPayloadWithFeatures(
            bool terminalSnapshotEnabled,
            bool typedInputEnabled,
            bool directTransportDataEnabled)
        {
            var capabilities = ProductionRealtimeCapabilities.BuildAdvertisedCapabilities(
                terminalSnapshotEnabled,
                typedInputEnabled,
                directTransportDataEnabled);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                role = "pc",
                protocol_versions = new[] { 1 },
                capabilities
            });
        }

        internal ProductionRealtimeCapabilitySnapshot SnapshotRealtimeCapabilities() =>
            realtimeCapabilityState.Snapshot();

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

        public static ProductionAgentRuntime TryCreate(string registryControlPipeName = null)
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
            TryRetainIdentity(config, credentials);
            return new ProductionAgentRuntime(
                config,
                credentials,
                ProductionReconnectPreferences.Load(),
                registryControlPipeName);
        }

        private static void TryRetainIdentity(
            ProductionAgentConfig config,
            ProductionCredentials credentials)
        {
            if (string.IsNullOrWhiteSpace(config?.MachineId) || credentials == null) return;
            // Token-free retention is a recovery aid. A local storage failure
            // must not prevent the authenticated runtime from serving sessions;
            // server-side machine resolution remains available.
            ProductionIdentityStore.TrySave(config, credentials);
        }

        public void Attach(SessionRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            lock (lifecycleSync)
            {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("ProductionAgentRuntime");
                if (this.registry != null) throw new InvalidOperationException("Production runtime is already attached.");
                this.registry = registry;
                registry.RestoreDeletedSessions(terminalHistoryStore.DeletedSessionIds());
                QueueRegisteredSessionsForReplay();
                QueuePersistedHistoryForReplay();
                historyUploader = new ProductionHistoryUploader(
                    http,
                    SendAsync,
                    credentials.DeviceId,
                    terminalHistoryStore,
                    terminalSnapshotEnabled);
                var fileTransferSender = new ProductionFileTransferSender(
                    config.ServerUrl,
                    credentials.DeviceId,
                    async (options, path, token) =>
                    {
                        var accessToken = await GetAccessTokenAsync(false, token).ConfigureAwait(false);
                        AddProofHeaders(options, HttpMethod.Get, path, Array.Empty<byte>(), accessToken);
                    },
                    ResolveFileTransferPeerAsync);
                fileOperationWorker = new ProductionFileOperationWorker(
                    new ProductionFileOperationApi(config.PcId, fileUploadHttp, SendAsync, fileTransferSender),
                    new DirectoryListingService(),
                    new FileTransferPreparer(),
                    new ManagedTerminalLauncher(registryControlPipeName),
                    cancellation.Token,
                    () => registry.RemoteAccessEnabled,
                    sessionId => registry.ContainsSession(sessionId),
                    sessionId => registry.TerminateSession(sessionId),
                    diagnostic: ObserveFileOperationWorkerDiagnostic);
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
                postRegistrationWorkerStartGate = new PostRegistrationWorkerStartGate(
                    fileOperationWorker.Start,
                    agentUpdateWorker.Start);
                historyWorker = Task.Run(() => HistoryUploadLoopAsync(allowPersistedBacklog: true));
                // A persisted frame can contain a very large output payload and
                // is only cooperatively preemptible between terminal parser
                // events. Keep one independent realtime lane so that even an
                // in-progress legacy replay can never delay a new Snapshot.
                priorityHistoryWorker = Task.Run(() => HistoryUploadLoopAsync(allowPersistedBacklog: false));
                metadataWorker = Task.Run(MetadataUpsertLoopAsync);
                commonIngressWorker = Task.Run(CommonIngressLoopAsync);
                networkAvailabilityHandler = (_, network) =>
                {
                    DispatchNetworkAvailabilityChange(
                        network.IsAvailable,
                        connectionSupervisor.NotifyNetworkAvailable,
                        NotifyDirectNetworkChangedSafely);
                };
                networkAddressHandler = (_, _) => NotifyDirectNetworkChangedSafely();
                NetworkChange.NetworkAvailabilityChanged += networkAvailabilityHandler;
                NetworkChange.NetworkAddressChanged += networkAddressHandler;
                worker = Task.Run(RunAsync);
                StartDirectRuntimesSafely();
                if (DirectTransportDataPlaneBuildEnabled && directSignaling != null)
                    directRoutingPolicyWorker = Task.Run(DirectRoutingPolicyLoopAsync);
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
                directAcceptanceDiagnostics?.Start();
#endif
            }
        }

        /// <summary>
        /// Closes the callback hand-off window used when a live SessionRegistry
        /// replaces its account runtime. The registry callback cannot target
        /// this instance until SessionRegistryService publishes it; a second
        /// authoritative enumeration after publication covers every frame that
        /// committed between the initial Attach enumeration and that write.
        /// </summary>
        internal void ReconcileAttachedRegistryAfterPublication()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (registry == null)
                throw new InvalidOperationException("Production runtime is not attached.");
            QueueRegisteredSessionsForReplay();
            QueuePersistedHistoryForReplay();
        }

        private void StartDirectRuntimesSafely()
        {
            if (IsReauthenticationRequired || Volatile.Read(ref disposed) != 0) return;
            if (directDataRuntime == null || directShadowRuntime == null) return;
            TryStartSharedDirectRuntimes(
                directDataRuntime.Start,
                directShadowRuntime.Start);
        }

        internal static bool TryStartSharedDirectRuntimes(
            Func<bool> startSignalingReaderOwner,
            Func<bool> startShadow)
        {
            if (startSignalingReaderOwner == null || startShadow == null) return false;
            var sharedSignalingReaderStarted = false;
            try { sharedSignalingReaderStarted = startSignalingReaderOwner(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            // The V5 runtime is the sole reader/owner of shared signalling.
            // Starting the V4 shadow without that reader leaves it alive but
            // permanently starved of offers, so fail this optional lane closed
            // while Relay continues independently.
            if (!sharedSignalingReaderStarted) return false;
            try
            {
                return startShadow();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            return false;
        }

        private void ObserveDirectRelayConnectedSafely(
            bool connected,
            ClientWebSocket active = null)
        {
            var generation = connectionSupervisor.Snapshot().PhysicalGeneration;
            try
            {
                if (connected && active != null)
                    relayExactOutbox.ObserveRelayConnected(
                        generation,
                        (request, token) => SendRelayOutboxEnvelopeAsync(
                            active, request, token));
                else if (generation > 0)
                    relayExactOutbox.ObserveRelayDisconnected(generation);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try
            {
                directDataAdmission.ObserveRelay(connected
                    ? realtimeCapabilityState.Snapshot()
                    : new ProductionRealtimeCapabilitySnapshot(0, Array.Empty<string>()));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try { directPeerRoutes.ObserveRelay(connected, generation); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            if (directShadowRuntime == null) return;
            try
            {
                directShadowRuntime.ObserveRelayConnected(
                    connected,
                    generation);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private void NotifyDirectNetworkChangedSafely()
        {
            try { if (directShadowRuntime?.IsCurrentNetworkUnchanged() == true) return; }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try { directDataRuntime?.NotifyNetworkChanged(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try { directPeerRoutes.RetireAllDirect("NETWORK_CHANGED"); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            if (directShadowRuntime == null) return;
            try { directShadowRuntime.NotifyNetworkChanged(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        internal IReadOnlyList<ProductionDirectPeerRouteSnapshot> SnapshotDirectPeerRoutes() =>
            directPeerRoutes.Snapshot();

        internal bool TryBeginDirectPeerRoute(
            Guid androidDeviceId,
            ProductionDirectPeerPathFence fence,
            ProductionDirectAuthenticatedPathKind pathKind)
        {
            return directPeerRoutes.TryGetOrCreate(androidDeviceId, out var actor) &&
                actor.TryBeginDirect(fence, pathKind);
        }

        internal bool TryMarkDirectPeerDirectionActive(
            Guid androidDeviceId,
            ProductionDirectPeerPathFence fence,
            ProductionDirectPeerDirection direction)
        {
            return directPeerRoutes.TryGet(androidDeviceId, out var actor) &&
                actor.TryMarkDirectionActive(fence, direction);
        }

        internal bool TryRetireDirectPeerRoute(
            Guid androidDeviceId,
            ProductionDirectPeerPathFence fence,
            string reasonCode) =>
            directPeerRoutes.TryRetirePeer(androidDeviceId, fence, reasonCode);

        internal static void DispatchNetworkAvailabilityChange(
            bool isAvailable,
            Action notifyRelayNetworkAvailable,
            Action notifyDirectNetworkChanged)
        {
            if (notifyRelayNetworkAvailable == null)
                throw new ArgumentNullException(nameof(notifyRelayNetworkAvailable));
            if (notifyDirectNetworkChanged == null)
                throw new ArgumentNullException(nameof(notifyDirectNetworkChanged));

            // Relay remains the authoritative, always-available recovery lane.
            // Wake it before optional Direct work, which may be slow or fail.
            if (isAvailable) notifyRelayNetworkAvailable();
            try { notifyDirectNetworkChanged(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private async Task DisposeDirectRuntimesSafelyAsync()
        {
            await DisposeSharedDirectRuntimesInOrderAsync(
                directShadowRuntime == null
                    ? null
                    : () => directShadowRuntime.DisposeAsync(),
                directSignaling == null
                    ? null
                    : () => directSignaling.StateChanged -= ObserveDirectSignalingState,
                directDataRuntime == null
                    ? null
                    : () => directDataRuntime.DisposeAsync()).ConfigureAwait(false);
        }

        private void BeginDirectRuntimeCleanup()
        {
            lock (lifecycleSync)
                directShadowCleanupTask ??= Task.Run(DisposeDirectRuntimesSafelyAsync);
        }

        internal static async Task DisposeSharedDirectRuntimesInOrderAsync(
            Func<ValueTask> disposeShadow,
            Action detachSignalingObserver,
            Func<ValueTask> disposeSignalingReaderOwner)
        {
            if (disposeShadow != null)
            {
                try { await disposeShadow().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            try { detachSignalingObserver?.Invoke(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            if (disposeSignalingReaderOwner != null)
            {
                try { await disposeSignalingReaderOwner().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }

        private void ObserveDirectSignalingState(ProductionDirectSignalingSnapshot snapshot)
        {
            if (snapshot == null || Volatile.Read(ref disposed) != 0) return;
            try
            {
                var relayGeneration = realtimeCapabilityState.Snapshot().PhysicalGeneration;
                directDataAdmission.ObserveSignalling(snapshot, relayGeneration);
                directDataRuntime?.ObserveSignalingState(
                    snapshot.State == ProductionDirectSignalingLaneState.Connected,
                    Math.Max(0, snapshot.Generation),
                    snapshot.DirectDataPlaneV1Negotiated);
                if (snapshot.State == ProductionDirectSignalingLaneState.Connected)
                    _ = RefreshDirectServerConfigAsync(snapshot.Generation, cancellation.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private async Task DirectRoutingPolicyLoopAsync()
        {
            while (!cancellation.IsCancellationRequested && !IsReauthenticationRequired)
            {
                await Task.Delay(DirectRoutingPolicyRefreshInterval, cancellation.Token)
                    .ConfigureAwait(false);
                if (IsReauthenticationRequired) return;
                var snapshot = directSignaling?.Snapshot;
                if (snapshot?.State == ProductionDirectSignalingLaneState.Connected)
                    await RefreshDirectServerConfigAsync(snapshot.Generation, cancellation.Token)
                        .ConfigureAwait(false);
            }
        }

        private async Task RefreshDirectServerConfigAsync(
            long signalingGeneration,
            CancellationToken token)
        {
            if (directSignaling == null || signalingGeneration < 1) return;
            var gateEntered = false;
            try
            {
                await directConfigRefreshGate.WaitAsync(token).ConfigureAwait(false);
                gateEntered = true;
                var result = await directSignaling.FetchConfigAsync(token).ConfigureAwait(false);
                if (result.Status != ProductionDirectSignalingCallStatus.Success ||
                    result.Value == null || directSignaling.Snapshot.Generation != signalingGeneration)
                {
                    directDataAdmission.ObserveServerConfigUnavailable();
                    return;
                }
                directDataAdmission.ObserveServerConfig(result.Value);
                if (!result.Value.DirectRoutingPermitted)
                    await RetireDirectAuthorityPeersAsync(
                        SnapshotActiveDirectPeerIds(),
                        "DIRECT_ROUTING_DISABLED").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                directDataAdmission.ObserveServerConfigUnavailable();
            }
            finally
            {
                if (gateEntered) directConfigRefreshGate.Release();
            }
        }

        private async Task<bool> PromoteDirectPeerAsync(
            ProductionDirectDataPromotionHandoff handoff,
            CancellationToken token)
        {
            if (handoff == null || directDataRuntime == null ||
                !DirectHandoffAuthorityMatches(handoff) ||
                !directDataAdmission.TryCapture(out var admission))
                return false;

            byte[] pairwiseKey = null;
            byte[] signalKeyId = null;
            ProductionDirectCommitRequestV4 nomination = null;
            byte[] ticketWireBytes = null;
            ProductionDirectPathFenceV5 promotionPath = null;
            try
            {
                promotionPath = handoff.Path;
                pairwiseKey = handoff.CopyPairwiseKey();
                signalKeyId = handoff.CopySignalKeyId();
                nomination = handoff.CopyNomination();
                ticketWireBytes = handoff.CopyTicketWireBytes();
                using var admitted = new ProductionDirectDataPromotionHandoff(
                    handoff.PeerDeviceId,
                    handoff.ConnectionId,
                    handoff.KeyEpoch,
                    // ProductionDirectDataRuntime fences cutover control
                    // messages against the signalling generation.
                    admission.SignallingGeneration,
                    handoff.LocalSignalSequence,
                    handoff.RemoteSignalSequence,
                    handoff.RemoteSignalAcknowledgement,
                    pairwiseKey,
                    signalKeyId,
                    handoff.Native,
                    nomination,
                    handoff.SignNativeTranscript,
                    handoff.NominationCompleted,
                    handoff.NominationFailed,
                    ticketWireBytes);
                var promoted = await directDataRuntime.PromotePeerAsync(admitted, token)
                    .ConfigureAwait(false);
                if (promoted && (!directDataAdmission.IsCurrent(admission) ||
                    !DirectHandoffAuthorityMatches(handoff)))
                {
                    await directDataRuntime.RetirePeerAsync(
                        handoff.PeerDeviceId,
                        promotionPath,
                        "DIRECT_ADMISSION_STALE").ConfigureAwait(false);
                    return false;
                }
                return promoted;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ObserveDirectDataPathFault(
                    handoff.PeerDeviceId,
                    promotionPath,
                    "DIRECT_PROMOTION_FAILED");
                return false;
            }
            finally
            {
                if (pairwiseKey != null) CryptographicOperations.ZeroMemory(pairwiseKey);
                if (signalKeyId != null) CryptographicOperations.ZeroMemory(signalKeyId);
                if (ticketWireBytes != null)
                    CryptographicOperations.ZeroMemory(ticketWireBytes);
                ProductionDirectDataPromotionHandoff.ZeroNomination(nomination);
            }
        }

        private bool DirectHandoffAuthorityMatches(
            ProductionDirectDataPromotionHandoff handoff)
        {
            if (handoff == null || handoff.PeerDeviceId == Guid.Empty) return false;
            byte[] pairwiseKey = null;
            try
            {
                pairwiseKey = handoff.CopyPairwiseKey();
                lock (subscriberAuthoritySync)
                {
                    if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                        handoff.KeyEpoch != Volatile.Read(ref directAuthorityKeyEpoch) ||
                        !authorizedRecipients.TryGetValue(
                            handoff.PeerDeviceId, out var authorized))
                        return false;
                    lock (authorized.Sync)
                    {
                        return !authorized.Removed &&
                            authorized.KeyEpoch == handoff.KeyEpoch &&
                            authorized.PairwiseKey?.Length == 32 &&
                            CryptographicOperations.FixedTimeEquals(
                                authorized.PairwiseKey,
                                pairwiseKey);
                    }
                }
            }
            catch (ObjectDisposedException) { return false; }
            finally
            {
                if (pairwiseKey != null) CryptographicOperations.ZeroMemory(pairwiseKey);
            }
        }

        private Task DispatchPrePromotionSignalAsync(
            TermLink.Protocol.Transport.V1.TransportSignalEnvelopeV1 signal,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            directShadowRuntime?.TryDispatchPrePromotionSignal(signal);
            return Task.CompletedTask;
        }

        private static ProductionDirectDataIngressMetadata ParseDirectIngressMetadata(
            byte[] exactEnvelope)
        {
            if (exactEnvelope == null || exactEnvelope.Length == 0) return null;
            try
            {
                var envelope = Envelope.Parser.ParseFrom(exactEnvelope);
                var sessionId = GuidFromBytes(envelope.SessionId.Span);
                if (sessionId == Guid.Empty || envelope.Seq == 0) return null;
                return new ProductionDirectDataIngressMetadata
                {
                    SessionId = sessionId,
                    EnvelopeSequence = envelope.Seq
                };
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return null;
            }
        }

        private void EnqueueCommonIngress(ProductionDirectDataIngressDelivery delivery)
        {
            if (delivery == null || delivery.ExactEnvelope == null ||
                delivery.ExactEnvelope.Length == 0)
                throw new ArgumentException(
                    "输入 Envelope 无效。/ The ingress Envelope is invalid.",
                    nameof(delivery));
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ProductionAgentRuntime));
            // This callback is the synchronous ownership boundary required by
            // ProductionDirectDataRuntime. Waiting here provides bounded
            // backpressure without creating a second application executor.
            commonIngressQueue.Writer.WriteAsync(delivery, cancellation.Token)
                .AsTask().GetAwaiter().GetResult();
        }

        private async Task CommonIngressLoopAsync()
        {
            var token = cancellation.Token;
            try
            {
                while (await commonIngressQueue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (commonIngressQueue.Reader.TryRead(out var delivery))
                    {
                        byte[] owned = delivery.ExactEnvelope;
                        try
                        {
                            using var ingress = ProductionInboundEnvelope.Parse(owned);
                            owned = null;
                            ClientWebSocket active;
                            lock (lifecycleSync) active = socket;
                            await HandleIncomingAsync(
                                active,
                                ingress,
                                delivery.Source,
                                delivery.PeerDeviceId,
                                delivery.Path,
                                token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception error) when (error is not OutOfMemoryException &&
                            error is not StackOverflowException && error is not AccessViolationException)
                        {
                            if (delivery.Source == ProductionDirectIngressSourceV5.Direct)
                                ObserveDirectDataPathFault(
                                    delivery.PeerDeviceId,
                                    delivery.Path,
                                    "DIRECT_INGRESS_FAILED");
                            else
                                connectionSupervisor.ObserveSessionSoftFailure();
                        }
                        finally
                        {
                            if (owned != null) CryptographicOperations.ZeroMemory(owned);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                while (commonIngressQueue.Reader.TryRead(out var pending))
                    if (pending.ExactEnvelope != null)
                        CryptographicOperations.ZeroMemory(pending.ExactEnvelope);
            }
        }

        private static void DisposeNativeAction(ProductionDirectDataNativeAction value)
        {
            if (value?.PairwiseKey != null)
                CryptographicOperations.ZeroMemory(value.PairwiseKey);
            if (value?.Action?.Payload != null && value.Action.Payload.Length > 0)
                CryptographicOperations.ZeroMemory(value.Action.Payload);
        }

        private void ObserveDirectDataPeerState(ProductionDirectDataPeerSnapshot value)
        {
            if (value == null || value.PeerDeviceId == Guid.Empty || value.Path == null) return;
            if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                value.KeyEpoch != Volatile.Read(ref directAuthorityKeyEpoch))
            {
                if (directDataRuntime != null && !value.Retired)
                    _ = directDataRuntime.RetirePeerAsync(
                        value.PeerDeviceId,
                        value.Path,
                        "PEER_AUTHORITY_STALE");
                return;
            }
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
            directAcceptancePeerSnapshots.AddOrUpdate(
                value.PeerDeviceId,
                value,
                (_, retained) => retained != null &&
                    (retained.KeyEpoch > value.KeyEpoch ||
                     retained.KeyEpoch == value.KeyEpoch && retained.Path != null &&
                     retained.Path.PathEpoch > value.Path.PathEpoch)
                        ? retained
                        : value);
#endif
            try
            {
                var fence = new ProductionDirectPeerPathFence
                {
                    PathEpoch = value.Path.PathEpoch,
                    ConnectionId = value.Path.ConnectionId,
                    AttemptId = value.Path.AttemptId
                };
                if (value.Retired)
                {
                    directPeerRoutes.TryRetirePeer(
                        value.PeerDeviceId, fence, value.LastFault);
                    directShadowRuntime?.NotifyPromotedPathRetired(value.PeerDeviceId, value.Path, value.LastFault);
                    return;
                }
                var pathKind = value.PcToAndroidCarrier == ProductionDirectCarrierV5.LanDirect ||
                    value.AndroidToPcCarrier == ProductionDirectCarrierV5.LanDirect
                    ? ProductionDirectAuthenticatedPathKind.LocalAreaNetwork
                    : value.PcToAndroidCarrier == ProductionDirectCarrierV5.NatDirect ||
                        value.AndroidToPcCarrier == ProductionDirectCarrierV5.NatDirect
                        ? ProductionDirectAuthenticatedPathKind.NatTraversal
                        : ProductionDirectAuthenticatedPathKind.Unknown;
                if (!directPeerRoutes.TryGetOrCreate(value.PeerDeviceId, out var actor)) return;
                var pcActive = value.PcToAndroidCarrier is ProductionDirectCarrierV5.LanDirect or
                    ProductionDirectCarrierV5.NatDirect;
                var androidActive = value.AndroidToPcCarrier is ProductionDirectCarrierV5.LanDirect or
                    ProductionDirectCarrierV5.NatDirect;
                var mixedPathKinds = pcActive && androidActive &&
                    value.PcToAndroidCarrier != value.AndroidToPcCarrier;
                var snapshot = actor.Snapshot;
                if (mixedPathKinds || !pcActive && !androidActive ||
                    pathKind == ProductionDirectAuthenticatedPathKind.Unknown)
                {
                    if (snapshot.Fence != null && snapshot.Fence.Equals(fence))
                        actor.TryRetireDirect(fence, mixedPathKinds
                            ? "DIRECT_CARRIER_MISMATCH"
                            : "DIRECT_RELAY_FALLBACK");
                    return;
                }
                if (snapshot.Fence != null && snapshot.Fence.Equals(fence) &&
                    (snapshot.PathKind != pathKind ||
                     snapshot.PcToAndroidActive != pcActive ||
                     snapshot.AndroidToPcActive != androidActive))
                {
                    actor.TryRetireDirect(fence, "DIRECT_CARRIER_CHANGED");
                    snapshot = actor.Snapshot;
                }
                if (snapshot.Fence == null || !snapshot.Fence.Equals(fence))
                {
                    if (!actor.TryBeginDirect(fence, pathKind)) return;
                }
                if (pcActive)
                    actor.TryMarkDirectionActive(fence, ProductionDirectPeerDirection.PcToAndroid);
                if (androidActive)
                    actor.TryMarkDirectionActive(fence, ProductionDirectPeerDirection.AndroidToPc);
                if (value.ReadyForSessionCutover)
                    SchedulePcToAndroidCutover(value.PeerDeviceId);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private void SchedulePcToAndroidCutover(Guid peerDeviceId)
        {
            if (directDataRuntime == null || peerDeviceId == Guid.Empty ||
                Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                Volatile.Read(ref disposed) != 0 ||
                !RequestPcToAndroidCutoverPass(pendingPcToAndroidCutovers, peerDeviceId))
                return;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await TryStartPcToAndroidCutoverAsync(
                            peerDeviceId, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception error) when (error is not OutOfMemoryException &&
                        error is not StackOverflowException && error is not AccessViolationException)
                    {
                        ObserveDirectDataPeerFault(peerDeviceId, "DIRECT_CUTOVER_START_FAILED");
                    }
                    if (!CompletePcToAndroidCutoverPass(
                            pendingPcToAndroidCutovers, peerDeviceId)) break;
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Coalesces actor-owned cutover passes without losing a late session.
        /// Value 0 means one pass is running; value 1 records that a callback or
        /// SUBSCRIBE arrived during that pass and requires another snapshot.
        /// </summary>
        internal static bool RequestPcToAndroidCutoverPass(
            ConcurrentDictionary<Guid, byte> pending,
            Guid peerDeviceId)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (peerDeviceId == Guid.Empty) return false;
            if (pending.TryAdd(peerDeviceId, 0)) return true;
            pending.AddOrUpdate(peerDeviceId, 1, (_, _) => 1);
            return false;
        }

        internal static bool CompletePcToAndroidCutoverPass(
            ConcurrentDictionary<Guid, byte> pending,
            Guid peerDeviceId)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            while (pending.TryGetValue(peerDeviceId, out var state))
            {
                if (state != 0)
                {
                    if (pending.TryUpdate(peerDeviceId, 0, state)) return true;
                    continue;
                }
                if (((ICollection<KeyValuePair<Guid, byte>>)pending).Remove(
                        new KeyValuePair<Guid, byte>(peerDeviceId, 0)))
                    return false;
            }
            return false;
        }

        private async Task<bool> TryStartPcToAndroidCutoverAsync(
            Guid peerDeviceId,
            CancellationToken token)
        {
            await socketSendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var peer = directDataRuntime?.Snapshot()
                    .FirstOrDefault(value => value.PeerDeviceId == peerDeviceId);
                var authorityEpoch = Volatile.Read(ref directAuthorityKeyEpoch);
                if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                    peer?.ReadyForSessionCutover != true || peer.Retired ||
                    peer.KeyEpoch != authorityEpoch) return false;
                var reservations = new List<DirectCutoverReservation>();
                foreach (var session in subscribers.OrderBy(value => value.Key))
                {
                    if (reservations.Count >= ProductionDirectCutoverCodec.MaximumSessionBarriers)
                        break;
                    if (peer.PcToAndroidSessionCarriers.TryGetValue(
                            session.Key, out var committedCarrier) &&
                        committedCarrier is ProductionDirectCarrierV5.LanDirect or
                            ProductionDirectCarrierV5.NatDirect)
                        continue;
                    if (!session.Value.TryGetValue(peerDeviceId, out var subscriber)) continue;
                    lock (subscriber.Sync)
                    {
                        if (subscriber.Removed || subscriber.ReplayInProgress ||
                            !session.Value.TryGetValue(peerDeviceId, out var current) ||
                            !ReferenceEquals(current, subscriber))
                            continue;
                        var lastOrderedSequence = Math.Max(
                            subscriber.LastSentSequence,
                            subscriber.HighestAck);
                        if (lastOrderedSequence >= long.MaxValue) continue;
                        if (subscriber.KeyEpoch != authorityEpoch) continue;
                        reservations.Add(new DirectCutoverReservation
                        {
                            SessionId = session.Key,
                            PeerDeviceId = peerDeviceId,
                            Subscriber = subscriber,
                            KeyEpoch = subscriber.KeyEpoch,
                            FirstDirectSequence = lastOrderedSequence + 1
                        });
                    }
                }
                if (reservations.Count == 0) return false;
                // Holding the common send gate makes every earlier Relay send
                // visible in LastSentSequence and prevents a later send from
                // crossing the barrier until the proposal is accepted by the
                // actor and handed to the independent signalling lane.
                var started = await directDataRuntime.StartPcToAndroidCutoverAsync(
                    peerDeviceId,
                    reservations.Select(value => new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = value.SessionId,
                        FirstDirectSequence = value.FirstDirectSequence
                    }).ToArray(),
                    token).ConfigureAwait(false);
                if (!started) return false;
                if (reservations.All(DirectCutoverReservationIsCurrent)) return true;
                foreach (var reservation in reservations)
                    DeregisterDirectSession(reservation.PeerDeviceId, reservation.SessionId);
                return false;
            }
            finally { socketSendGate.Release(); }
        }

        private bool DirectCutoverReservationIsCurrent(
            DirectCutoverReservation reservation)
        {
            if (reservation == null || reservation.Subscriber == null) return false;
            lock (subscriberAuthoritySync)
            {
                if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                    reservation.KeyEpoch != Volatile.Read(ref directAuthorityKeyEpoch) ||
                    !subscribers.TryGetValue(
                        reservation.SessionId, out var sessionSubscribers) ||
                    !sessionSubscribers.TryGetValue(
                        reservation.PeerDeviceId, out var current) ||
                    !ReferenceEquals(current, reservation.Subscriber))
                    return false;
                lock (reservation.Subscriber.Sync)
                {
                    return !reservation.Subscriber.Removed &&
                        reservation.Subscriber.KeyEpoch == reservation.KeyEpoch;
                }
            }
        }

        private void ObserveDirectDataPeerFault(Guid peerDeviceId, string reasonCode)
        {
            if (peerDeviceId == Guid.Empty) return;
            connectionSupervisor.ObserveSessionSoftFailure();
            // A frame that stayed in Subscriber.SentFrames after a Direct
            // fault must be reconsidered immediately. The native actor owns
            // path-scoped retirement and publishes its exact fence; a callback
            // carrying only a peer ID must not retire a newer successor path.
            SignalPublisherSafely();
        }

        private void SignalPublisherSafely()
        {
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        internal static bool NotifyRelayCapacityReleased(
            int removedEnvelopeCount,
            Action signalPublisher)
        {
            if (removedEnvelopeCount <= 0 || signalPublisher == null) return false;
            try
            {
                signalPublisher();
                return true;
            }
            catch (SemaphoreFullException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        private void ObserveDirectDataPathFault(
            Guid peerDeviceId,
            ProductionDirectPathFenceV5 path,
            string reasonCode)
        {
            if (peerDeviceId != Guid.Empty && path != null)
            {
                var normalizedReason = string.IsNullOrWhiteSpace(reasonCode)
                    ? "DIRECT_DATA_FAILED"
                    : reasonCode;
                directPeerRoutes.TryRetirePeer(
                    peerDeviceId,
                    new ProductionDirectPeerPathFence
                    {
                        PathEpoch = path.PathEpoch,
                        ConnectionId = path.ConnectionId,
                        AttemptId = path.AttemptId
                    },
                    normalizedReason);
                // RetirePeerAsync removes the exact actor synchronously before
                // its first await. Start that retirement before waking the
                // publisher so no retry can be posted to the failed actor.
                if (directDataRuntime != null)
                    _ = directDataRuntime.RetirePeerAsync(
                        peerDeviceId,
                        path,
                        normalizedReason);
            }
            ObserveDirectDataPeerFault(peerDeviceId, reasonCode);
        }

        private async Task UpdateDirectAppliedCursorSafelyAsync(
            Guid peerDeviceId,
            ProductionDirectDirectionV5 direction,
            Guid sessionId,
            ulong highestContiguous,
            CancellationToken token)
        {
            if (directDataRuntime == null || peerDeviceId == Guid.Empty ||
                sessionId == Guid.Empty || highestContiguous == 0)
                return;
            try
            {
                await directDataRuntime.UpdateAppliedCursorAsync(
                    peerDeviceId,
                    direction,
                    sessionId,
                    highestContiguous,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException &&
                error is not StackOverflowException && error is not AccessViolationException)
            {
                ObserveDirectDataPeerFault(peerDeviceId, "DIRECT_CURSOR_UPDATE_FAILED");
            }
        }

        private void ObserveAndroidEnvelope(
            Guid peerDeviceId,
            Guid sessionId,
            ulong sequence)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty || sequence == 0) return;
            var state = directIngressApplyStates.GetOrAdd(
                DirectIngressApplyKey(peerDeviceId, sessionId),
                _ => new ProductionDirectIngressApplyTracker());
            state.Observe(sequence);
        }

        private async Task MarkAndroidEnvelopeAppliedAsync(
            Guid peerDeviceId,
            Guid sessionId,
            ulong sequence,
            CancellationToken token)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty || sequence == 0) return;
            var state = directIngressApplyStates.GetOrAdd(
                DirectIngressApplyKey(peerDeviceId, sessionId),
                _ => new ProductionDirectIngressApplyTracker());
            var advanced = state.MarkApplied(sequence);
            if (advanced > 0)
                await UpdateDirectAppliedCursorSafelyAsync(
                    peerDeviceId,
                    ProductionDirectDirectionV5.AndroidToPc,
                    sessionId,
                    advanced,
                    token).ConfigureAwait(false);
        }

        private static string DirectIngressApplyKey(Guid peerDeviceId, Guid sessionId) =>
            peerDeviceId.ToString("N") + ":" + sessionId.ToString("N");

        private void DeregisterDirectSession(Guid peerDeviceId, Guid sessionId)
        {
            if (directDataRuntime == null || peerDeviceId == Guid.Empty ||
                sessionId == Guid.Empty) return;
            // Posting both commands is synchronous up to the actor mailbox,
            // so a replacement SUBSCRIBE can only enqueue its later proposal
            // after this session-scoped retirement request.
            var pcToAndroid = directDataRuntime.DeregisterSessionAsync(
                peerDeviceId,
                ProductionDirectDirectionV5.PcToAndroid,
                sessionId,
                cancellation.Token);
            var androidToPc = directDataRuntime.DeregisterSessionAsync(
                peerDeviceId,
                ProductionDirectDirectionV5.AndroidToPc,
                sessionId,
                cancellation.Token);
            _ = ObserveDirectSessionDeregistrationAsync(
                peerDeviceId, pcToAndroid, androidToPc);
        }

        private async Task ObserveDirectSessionDeregistrationAsync(
            Guid peerDeviceId,
            Task<bool> pcToAndroid,
            Task<bool> androidToPc)
        {
            try { await Task.WhenAll(pcToAndroid, androidToPc).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException &&
                error is not StackOverflowException && error is not AccessViolationException)
            {
                ObserveDirectDataPeerFault(peerDeviceId, "DIRECT_SESSION_DEREGISTER_FAILED");
            }
        }

        private void ObserveDirectSignalingReaderFault(Exception error)
        {
            // Signalling is independent from Relay. Diagnostics are counted as
            // a soft failure and must not advance/clear the physical generation.
            connectionSupervisor.ObserveSessionSoftFailure();
        }

        private async Task<bool> SendRelayOutboxEnvelopeAsync(
            ClientWebSocket active,
            ProductionRelayExactSendRequest request,
            CancellationToken token)
        {
            if (active == null || request == null || request.RelayGeneration < 1)
                return false;
            var before = connectionSupervisor.Snapshot();
            lock (lifecycleSync)
            {
                if (!ReferenceEquals(socket, active) ||
                    before.PhysicalGeneration != request.RelayGeneration ||
                    !before.PhysicalConnected || active.State != WebSocketState.Open)
                    return false;
            }
            var exact = request.ExactEnvelope.ToArray();
            try
            {
                await SendWebSocketAsync(
                    active,
                    exact,
                    WebSocketMessageType.Binary,
                    true,
                    token).ConfigureAwait(false);
                var after = connectionSupervisor.Snapshot();
                lock (lifecycleSync)
                {
                    return ReferenceEquals(socket, active) && after.PhysicalConnected &&
                        after.PhysicalGeneration == request.RelayGeneration &&
                        active.State == WebSocketState.Open;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception error) when (error is not OutOfMemoryException &&
                error is not StackOverflowException && error is not AccessViolationException)
            {
                try { active.Abort(); }
                catch (ObjectDisposedException) { }
                return false;
            }
            finally { CryptographicOperations.ZeroMemory(exact); }
        }

        private Task<string> GetDirectShadowAccessTokenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (IsReauthenticationRequired)
                throw new ProductionReauthenticationRequiredException();
            // Optional Direct activity must not initiate shared refresh-token
            // failure handling, because that path is allowed to abort Relay.
            // The Relay lane remains the authority for credential renewal;
            // Direct takes a read-only snapshot of the current access token.
            return Task.FromResult(credentials.GetAccessToken());
        }

        private void QueuePersistedHistoryForReplay()
        {
            foreach (var sessionId in terminalHistoryStore.ListSessions())
            {
                QueuePersistedHistoryBacklog(sessionId);
            }
            if (registry != null)
            {
                foreach (var session in registry.GetSummaries())
                {
                    persistedHistoryBacklogSessions.TryRemove(session.SessionId, out _);
                    historyReconcileSessions[session.SessionId] = 0;
                    QueueNewHistoryWork(session.SessionId);
                }
            }
            if (pendingHistorySessions.IsEmpty && persistedHistoryBacklogSessions.IsEmpty) return;
            try { historySignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private void QueuePersistedHistoryBacklog(Guid sessionId)
        {
            if (sessionId == Guid.Empty ||
                destroyedHistorySessions.ContainsKey(sessionId) ||
                (registry != null && registry.ContainsActiveSession(sessionId)) ||
                pendingHistorySessions.ContainsKey(sessionId) ||
                persistedHistoryWorkSessions.ContainsKey(sessionId) ||
                !persistedHistoryBacklogSessions.TryAdd(sessionId, 0))
            {
                return;
            }
            persistedHistoryBacklog.Enqueue(sessionId);
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
            var historyDestroyed = destroyedHistorySessions.ContainsKey(frame.SessionId);
            if (!historyDestroyed)
            {
                var historyFrame = new LocalFrame(
                    frame.Type,
                    frame.SessionId,
                    frame.Sequence,
                    frame.Payload == null ? Array.Empty<byte>() : (byte[])frame.Payload.Clone());
                pendingHistoryFrames.GetOrAdd(
                    frame.SessionId,
                    _ => new ConcurrentQueue<LocalFrame>()).Enqueue(historyFrame);
            }
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                localInputEpochs[frame.SessionId] = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
            }
            else if (frame.Type == LocalFrameType.RemoteInputResult)
            {
                LocalRemoteInputResult inputResult;
                var completedInputId = LocalPayloadCodec.ParseRemoteInputResult(
                    frame.Payload,
                    out inputResult);
                if (inputReplayRecords.TryGetValue(completedInputId, out var replay) &&
                    replay.SessionId == frame.SessionId)
                {
                    Guid replaySender;
                    ulong replayEnvelopeSequence;
                    lock (replay.Sync)
                    {
                        replay.Result = inputResult;
                        replay.ResultSequence = frame.Sequence;
                        replaySender = replay.SenderDeviceId;
                        replayEnvelopeSequence = replay.EnvelopeSequence;
                    }
                    if (replaySender != Guid.Empty && replayEnvelopeSequence > 0)
                        _ = MarkAndroidEnvelopeAppliedAsync(
                            replaySender,
                            frame.SessionId,
                            replayEnvelopeSequence,
                            cancellation.Token);
                }
            }
            var publicationState = publicationStates.GetOrAdd(
                frame.SessionId,
                _ => new SessionPublicationState());
            ApplyPublicationFrame(
                publicationState,
                new CommittedEvent { ShellType = shellType, Frame = frame },
                DateTimeOffset.UtcNow);
            ObserveCatalogFrame(frame);
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
            if (!historyDestroyed) QueueHistoryUpload(frame.SessionId, false);
            try { eventSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        internal void DestroySessionHistory(Guid sessionId)
        {
            TryEnsureDurableSessionHistoryDeletion(sessionId, out _);
        }

        internal bool TryEnsureDurableSessionHistoryDeletion(Guid sessionId, out string errorCode)
        {
            errorCode = null;
            if (sessionId == Guid.Empty)
            {
                errorCode = "INVALID_SESSION_ID";
                return false;
            }
            try
            {
                // The marker is the acknowledgement boundary. It is written
                // through before any in-memory tombstone is exposed, so an
                // accepted close cannot resurrect history after Agent restart.
                terminalHistoryStore.MarkSessionDeleted(sessionId);
            }
            catch (Exception error) when (error is IOException ||
                error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)
            {
                terminalHistoryPersistenceFailures[sessionId] =
                    "DELETE_MARKER_" + error.GetType().Name;
                errorCode = "HISTORY_DELETE_NOT_DURABLE";
                return false;
            }

            destroyedHistorySessions.TryAdd(sessionId, 0);
            terminalHistoryPersistenceFailures.TryRemove(sessionId, out _);
            ApplyDestroyedSessionHistoryState(sessionId);
            ScheduleSessionHistoryDeletion(sessionId);
            return true;
        }

        private void ApplyDestroyedSessionHistoryState(Guid sessionId)
        {
            CancelHostHistoryRequests(sessionId, Guid.Empty);

            pendingHistorySessions.TryRemove(sessionId, out _);
            historyReconcileSessions.TryRemove(sessionId, out _);
            terminalHistorySessions.TryRemove(sessionId, out _);
            persistedHistoryBacklogSessions.TryRemove(sessionId, out _);
            if (pendingHistoryFrames.TryRemove(sessionId, out var queued))
            {
                while (queued.TryDequeue(out var frame))
                {
                    if (frame.Payload != null) CryptographicOperations.ZeroMemory(frame.Payload);
                }
            }
            lock (persistedHistoryCancellationSync)
            {
                if (persistedHistoryCancellationSessionId == sessionId)
                {
                    try { persistedHistoryCancellation?.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }

        }

        private void ScheduleSessionHistoryDeletion(Guid sessionId)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!historyDeletionTasks.TryAdd(sessionId, completion.Task)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    terminalHistoryProducer.ReleaseSession(sessionId);
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (activeHistoryWorkSessions.ContainsKey(sessionId) &&
                        DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                    terminalHistoryStore.DeleteSession(sessionId);
                }
                catch (Exception error) when (error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is InvalidOperationException)
                {
                    // The tombstone keeps the session inaccessible. A later
                    // runtime start sees no active registration and retries
                    // cleanup through the ordinary local-store maintenance.
                }
                finally
                {
                    try { registry?.ReleaseBufferedSession(sessionId); }
                    catch (Exception error) when (error is IOException ||
                        error is UnauthorizedAccessException ||
                        error is InvalidOperationException) { }
                    completion.TrySetResult(true);
                    ((ICollection<KeyValuePair<Guid, Task>>)historyDeletionTasks).Remove(
                        new KeyValuePair<Guid, Task>(sessionId, completion.Task));
                }
            });
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
            connectionSupervisor.RequestManualRetry();
            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            try { active?.Abort(); }
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
            authorizedRecipientGate.Wait(cancellation.Token);
            try
            {
                BeginDirectAuthorityTransition();
                try
                {
                    RetireDirectAuthorityPeersAsync(
                        new[] { targetDeviceId },
                        "PEER_AUTHORITY_REVOKED").GetAwaiter().GetResult();
                    lock (subscriberAuthoritySync)
                    {
                        ClearSubscriberRoutesForDevice(targetDeviceId);
                        if (authorizedRecipients.TryRemove(
                                targetDeviceId, out var authorizedRecipient))
                            RetireSubscriber(authorizedRecipient);
                        authorityExpiresUtc = default;
                    }
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
                    directAcceptancePeerSnapshots.TryRemove(targetDeviceId, out _);
#endif
                }
                finally { EndDirectAuthorityTransition(); }
            }
            finally { authorizedRecipientGate.Release(); }
            lock (statusSync)
            {
                if (pairingChallenge != null && pairingChallenge.TargetDeviceId == targetDeviceId) pairingChallenge = null;
            }
            statusExpiresUtc = DateTime.MinValue;
            RefreshStatusAsync(cancellation.Token).GetAwaiter().GetResult();
        }

        private async Task RunAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    SetRelayState(ProductionRelayState.Connecting, string.Empty);
                    await postRegistrationWorkerStartGate.EnsureRegisteredThenStartAsync(
                        EnsureRegisteredAsync,
                        cancellation.Token).ConfigureAwait(false);
                    await ConnectAndPumpAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (ProductionReauthenticationRequiredException)
                {
                    MarkReauthenticationRequired();
                    // An invalid/expired refresh credential cannot recover through
                    // periodic socket retries. Keep the actionable authentication
                    // state stable until the user explicitly retries or the login
                    // workflow replaces the runtime with fresh credentials.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    SetRelayState(ProductionRelayState.Reconnecting, ConnectionFailureCode(error));
                    var reconnect = connectionSupervisor.SchedulePhysicalFailure();
                    await connectionSupervisor.WaitForRetryOrResumeAsync(
                        reconnect.Delay,
                        cancellation.Token).ConfigureAwait(false);
                }
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
                ["key_id"] = DeviceKeyId(credentials.DeviceId)
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
            return BuildStablePCAgentUpsertBodyWithTerminalCommands(
                pcName,
                agentVersion,
                keyEpoch,
                platformVersion,
                architecture,
                terminalSnapshotCapable,
                supportedShells,
                terminalCommands,
                null);
        }

        internal static byte[] BuildStablePCAgentUpsertBodyWithTerminalCommands(
            string pcName,
            string agentVersion,
            long keyEpoch,
            string platformVersion,
            string architecture,
            bool terminalSnapshotCapable,
            string[] supportedShells,
            TerminalCommandCapability[] terminalCommands,
            string machineId)
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
                "file_operation_name_filter_v1",
                "terminal_commands_v1",
                "remote_agent_update_v1",
                "host_history_v1",
                "host_session_catalog_v1",
                "host_session_cleanup_v1",
                "terminal_window_resize_v1"
            };
            if (terminalSnapshotCapable) capabilities.Insert(1, "terminal_snapshot_v2");
            var normalizedCommands = terminalCommands.Select(command => new
            {
                id = command.Id,
                display_name = command.DisplayName,
                shell_type = command.ShellType,
                source = command.Source
            }).ToArray();
            if (!string.IsNullOrWhiteSpace(machineId) && !ProductionMachineIdentity.IsValid(machineId))
                throw new ArgumentException("The PC machine identity is invalid.", nameof(machineId));
            if (!string.IsNullOrWhiteSpace(machineId))
            {
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
                    remote_agent_update_capable = true,
                    machine_id = machineId
                });
            }
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
            var body = BuildStablePCAgentUpsertBodyWithTerminalCommands(
                config.PcName,
                config.AgentVersion,
                config.KeyEpoch,
                RuntimeInformation.OSDescription,
                PCAgentArchitecture(RuntimeInformation.OSArchitecture),
                terminalSnapshotEnabled,
                ManagedTerminalLauncher.DetectSupportedShellTypes(),
                ManagedTerminalLauncher.DetectTerminalCommandCapabilities(),
                config.MachineId);
            try
            {
                var response = await SendAsync(
                    HttpMethod.Put,
                    "/v1/pcs/" + config.PcId.ToString("D"),
                    body,
                    true,
                    DeviceScopedPCUpsertIdempotencyKey(config.PcId, credentials.DeviceId, body),
                    token).ConfigureAwait(false);
                var errorCode = response.StatusCode == HttpStatusCode.BadRequest
                    ? await ReadServerErrorCodeAsync(response, token).ConfigureAwait(false)
                    : string.Empty;
                if (response.StatusCode == HttpStatusCode.BadRequest && errorCode == "INVALID_REQUEST" &&
                    string.IsNullOrWhiteSpace(config.MachineId))
                {
                    response.Dispose();
                    response = await SendAsync(
                        HttpMethod.Put,
                        "/v1/pcs/" + config.PcId.ToString("D"),
                        legacyBody,
                        true,
                        DeviceScopedPCUpsertIdempotencyKey(config.PcId, credentials.DeviceId, legacyBody),
                        token).ConfigureAwait(false);
                }
                using (response)
                {
                    await EnsureServerSuccessAsync(response, "PC_UPSERT", token).ConfigureAwait(false);
                    await ApplyPCUpsertKeyEpochAsync(response, token).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
                CryptographicOperations.ZeroMemory(legacyBody);
            }
        }

        private async Task ApplyPCUpsertKeyEpochAsync(
            HttpResponseMessage response,
            CancellationToken token)
        {
            if (response?.Content == null) return;
            try
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("key_epoch", out var value) ||
                    !value.TryGetInt64(out var keyEpoch) ||
                    keyEpoch <= Volatile.Read(ref directAuthorityKeyEpoch))
                    return;
                await authorizedRecipientGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (keyEpoch <= Volatile.Read(ref directAuthorityKeyEpoch)) return;
                    BeginDirectAuthorityTransition();
                    try
                    {
                        var peers = SnapshotAllDirectAuthorityPeerIds();
                        await RetireDirectAuthorityPeersAsync(
                            peers,
                            "KEY_EPOCH_ADVANCED").ConfigureAwait(false);
                        var previousEpoch = config.KeyEpoch;
                        config.KeyEpoch = keyEpoch;
                        try { ProductionAgentConfig.Save(config); }
                        catch
                        {
                            config.KeyEpoch = previousEpoch;
                            throw;
                        }
                        lock (subscriberAuthoritySync)
                        {
                            foreach (var peer in peers) relayExactOutbox.PrunePeer(peer);
                            ClearSubscribers();
                            foreach (var previous in authorizedRecipients.Values)
                                RetireSubscriber(previous);
                            authorizedRecipients.Clear();
                            Volatile.Write(ref directAuthorityKeyEpoch, keyEpoch);
                            authorityExpiresUtc = default;
                        }
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
                        directAcceptancePeerSnapshots.Clear();
#endif
                        TryRetainIdentity(config, credentials);
                        QueuePersistedHistoryForReplay();
                        QueueSessionTitleDirectoryRepublish();
                    }
                    finally { EndDirectAuthorityTransition(); }
                }
                finally { authorizedRecipientGate.Release(); }
            }
            catch (JsonException)
            {
                // Older compatible Servers returned an empty success body.
            }
        }

        private void BeginDirectAuthorityTransition()
        {
            lock (subscriberAuthoritySync)
            {
                if (Volatile.Read(ref directAuthorityTransitioning) != 0)
                    throw new InvalidOperationException(
                        "A Direct authority transition is already active.");
                Volatile.Write(ref directAuthorityTransitioning, 1);
                // Fence every promotion completion captured before this
                // transaction. Committed peers are removed below without
                // touching the Relay generation or SessionHost lifecycle.
                directDataAdmission.Invalidate();
            }
        }

        private void EndDirectAuthorityTransition()
        {
            lock (subscriberAuthoritySync)
                Volatile.Write(ref directAuthorityTransitioning, 0);
        }

        private HashSet<Guid> SnapshotAllDirectAuthorityPeerIds()
        {
            var peers = new HashSet<Guid>();
            lock (subscriberAuthoritySync)
            {
                foreach (var peer in authorizedRecipients.Keys) peers.Add(peer);
                foreach (var session in subscribers.Values)
                foreach (var peer in session.Keys)
                    peers.Add(peer);
                foreach (var route in connectionSubscriberRoutes.Values)
                    if (route != null && route.DeviceId != Guid.Empty)
                        peers.Add(route.DeviceId);
            }
            foreach (var peer in directPeerRoutes.Snapshot())
                if (peer.AndroidDeviceId != Guid.Empty) peers.Add(peer.AndroidDeviceId);
            if (directDataRuntime != null)
            {
                foreach (var peer in directDataRuntime.Snapshot())
                    if (peer.PeerDeviceId != Guid.Empty) peers.Add(peer.PeerDeviceId);
            }
            foreach (var peer in relayExactOutbox.Snapshot())
                if (peer.PeerDeviceId != Guid.Empty) peers.Add(peer.PeerDeviceId);
            return peers;
        }

        private HashSet<Guid> SnapshotActiveDirectPeerIds()
        {
            var peers = new HashSet<Guid>();
            foreach (var route in directPeerRoutes.Snapshot())
                if (route.AndroidDeviceId != Guid.Empty) peers.Add(route.AndroidDeviceId);
            if (directDataRuntime != null)
                foreach (var peer in directDataRuntime.Snapshot())
                    if (peer.PeerDeviceId != Guid.Empty) peers.Add(peer.PeerDeviceId);
            return peers;
        }

        private async Task RetireDirectAuthorityPeersAsync(
            IEnumerable<Guid> peerDeviceIds,
            string reasonCode)
        {
            var peers = (peerDeviceIds ?? Array.Empty<Guid>())
                .Where(value => value != Guid.Empty)
                .Distinct()
                .ToArray();
            var retirements = new List<Task<bool>>(peers.Length);
            foreach (var peer in peers)
            {
                // RetirePeerAsync removes the actor from its routing dictionary
                // synchronously before its first await. A new Subscriber is
                // still blocked by directAuthorityTransitioning until all
                // actor cleanup completions have joined.
                if (directDataRuntime != null)
                    retirements.Add(directDataRuntime.RetirePeerAsync(peer, reasonCode));
                directPeerRoutes.Remove(peer);
            }
            if (retirements.Count > 0)
                await Task.WhenAll(retirements).ConfigureAwait(false);
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

        internal static string DeviceScopedPCUpsertIdempotencyKey(
            Guid pcId,
            Guid deviceId,
            byte[] body)
        {
            if (pcId == Guid.Empty) throw new ArgumentException("A PC ID is required.", nameof(pcId));
            if (deviceId == Guid.Empty) throw new ArgumentException("A device ID is required.", nameof(deviceId));
            if (body == null || body.Length == 0) throw new ArgumentException("PC metadata is required.", nameof(body));
            var digest = SHA256.HashData(body);
            try
            {
                // A reinstalled Agent can legitimately bind a new device key
                // to the same PC and send identical metadata. Include the
                // authenticated device identity so the account-scoped Server
                // cache cannot replay the old device's successful upsert.
                return "pc-upsert-v3-" + pcId.ToString("N") + "-" +
                    deviceId.ToString("N") + "-" +
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
            ProductionRealtimeWelcome negotiatedWelcome = null;
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
                // Plain-HTTP local development servers speak ws:// for the
                // relay; only https:// origins upgrade to wss://.
                var realtimeRoot = new Uri(config.ServerUrl);
                var realtimeScheme = realtimeRoot.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
                var wsUri = new UriBuilder(realtimeRoot) { Scheme = realtimeScheme, Path = "/v1/realtime" }.Uri;
                using (var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshakeCancellation.CancelAfter(RealtimeHandshakeTimeout);
                    try
                    {
                        await active.ConnectAsync(wsUri, handshakeCancellation.Token).ConfigureAwait(false);
                        var advertisedCapabilities =
                            ProductionRealtimeCapabilities.BuildAdvertisedCapabilities(
                                terminalSnapshotEnabled,
                                typedInputEnabled,
                                DirectTransportDataPlaneBuildEnabled);
                        var hello = BuildHelloPayloadWithFeatures(
                            terminalSnapshotEnabled,
                            typedInputEnabled,
                            DirectTransportDataPlaneBuildEnabled);
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
                        var welcome = await ReceiveRealtimeWelcomeAsync(
                            active,
                            handshakeCancellation.Token).ConfigureAwait(false);
                        try
                        {
                            negotiatedWelcome = ProductionRealtimeCapabilities.ParseWelcome(
                                welcome,
                                advertisedCapabilities);
                        }
                        finally { CryptographicOperations.ZeroMemory(welcome); }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new WebSocketException(
                            WebSocketError.ConnectionClosedPrematurely,
                            "Production Relay connection or welcome timed out.");
                    }
                }
                var connectionGeneration = connectionSupervisor.MarkPhysicalConnected();
                if (!realtimeCapabilityState.TryMarkConnected(
                        connectionGeneration,
                        negotiatedWelcome))
                {
                    throw new InvalidOperationException(
                        "Production Relay capability generation is stale.");
                }
                ObserveDirectRelayConnectedSafely(true, active);
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
                    var stableConnection = MarkConnectionStableAfterDelayAsync(
                        connectionGeneration,
                        pumpCancellation.Token);
                    try
                    {
                        await Task.WhenAny(receiver, sender).ConfigureAwait(false);
                    }
                    finally
                    {
                        pumpCancellation.Cancel();
                        try { await Task.WhenAll(receiver, sender, stableConnection).ConfigureAwait(false); }
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
                connectionSupervisor.MarkPhysicalDisconnected();
                ObserveDirectRelayConnectedSafely(false);
                ClearRealtimeConnectionState();
                if (!token.IsCancellationRequested) SetRelayState(ProductionRelayState.Reconnecting, "RELAY_DISCONNECTED");
            }
        }

        private static async Task<byte[]> ReceiveRealtimeWelcomeAsync(
            ClientWebSocket active,
            CancellationToken token)
        {
            var buffer = new byte[ProductionRealtimeCapabilities.MaximumWelcomeBytes + 1];
            var received = 0;
            try
            {
                while (true)
                {
                    var result = await active.ReceiveAsync(
                        new ArraySegment<byte>(buffer, received, buffer.Length - received),
                        token).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidDataException("Production Relay welcome must be text.");
                    received = checked(received + result.Count);
                    if (received > ProductionRealtimeCapabilities.MaximumWelcomeBytes)
                        throw new InvalidDataException("Production Relay welcome exceeds its size limit.");
                    if (result.EndOfMessage) break;
                }
                if (received < 1)
                    throw new InvalidDataException("Production Relay welcome is missing.");
                var welcome = new byte[received];
                Buffer.BlockCopy(buffer, 0, welcome, 0, received);
                return welcome;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private async Task MarkConnectionStableAfterDelayAsync(
            long connectionGeneration,
            CancellationToken token)
        {
            await Task.Delay(
                ProductionRealtimeConnectionSupervisor.DefaultStableConnectionDuration,
                token).ConfigureAwait(false);
            connectionSupervisor.TryMarkPhysicalGenerationStable(connectionGeneration);
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
                    if (!retainedLeases.Contains(lease.Key))
                    {
                        leaseStatuses.TryRemove(lease.Key, out var ignored);
                        ClearDirectInputAuthorizations(lease.Key);
                    }
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
                    LeaseExpiresAtUtc = activeLease == null ? (DateTime?)null : activeLease.ExpiresAtUtc,
                    Carrier = ResolveSessionCarrier(session.SessionId, viewers)
                });
            }
            lock (statusSync)
            {
                var account = ProductionCredentials.ReadAccountClaims(credentials.GetAccessToken());
                var accountUsername = string.IsNullOrWhiteSpace(account.Username)
                    ? config.AccountUsername
                    : account.Username;
                var accountNickname = string.IsNullOrWhiteSpace(account.Nickname)
                    ? config.AccountNickname
                    : account.Nickname;
                if (string.IsNullOrWhiteSpace(accountNickname)) accountNickname = accountUsername;
                var accountEmail = string.IsNullOrWhiteSpace(account.Email)
                    ? config.AccountEmail
                    : account.Email;
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
                    AccountNickname = accountNickname,
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

        private string ResolveSessionCarrier(
            Guid sessionId,
            ConcurrentDictionary<Guid, Subscriber> viewers)
        {
            return ResolveSessionCarrier(
                sessionId,
                viewers?.Keys,
                directDataRuntime?.Snapshot());
        }

        // The session summary is shared by every current viewer. A control
        // lease holder is not a data-plane authority: collaborative viewers
        // can use different paths at the same time. Report Direct only when
        // every viewer has both directions committed to the same carrier.
        // Any partial, missing, retired or mixed state is represented by the
        // authoritative Relay fallback instead of overstating connectivity.
        internal static string ResolveSessionCarrier(
            Guid sessionId,
            IEnumerable<Guid> viewerDeviceIds,
            IEnumerable<ProductionDirectDataPeerSnapshot> peerSnapshots)
        {
            if (sessionId == Guid.Empty || viewerDeviceIds == null ||
                peerSnapshots == null)
                return ProductionSessionCarrier.Relay;
            var viewers = new HashSet<Guid>(
                viewerDeviceIds.Where(value => value != Guid.Empty));
            if (viewers.Count == 0) return ProductionSessionCarrier.Relay;

            var peers = new Dictionary<Guid, ProductionDirectDataPeerSnapshot>();
            foreach (var peer in peerSnapshots)
            {
                if (peer == null || peer.PeerDeviceId == Guid.Empty || peer.Retired ||
                    !peers.TryAdd(peer.PeerDeviceId, peer))
                    continue;
            }

            ProductionDirectCarrierV5? aggregate = null;
            foreach (var viewer in viewers)
            {
                if (!peers.TryGetValue(viewer, out var peer) ||
                    !peer.PcToAndroidSessionCarriers.TryGetValue(
                        sessionId, out var pcCarrier) ||
                    !peer.AndroidToPcSessionCarriers.TryGetValue(
                        sessionId, out var androidCarrier) ||
                    !IsDirectCarrier(pcCarrier) ||
                    !IsDirectCarrier(androidCarrier) ||
                    pcCarrier != androidCarrier ||
                    aggregate.HasValue && aggregate.Value != pcCarrier)
                    return ProductionSessionCarrier.Relay;
                aggregate = pcCarrier;
            }
            return aggregate.HasValue
                ? FormatSessionCarrier(aggregate.Value)
                : ProductionSessionCarrier.Relay;
        }

        private static bool IsDirectCarrier(ProductionDirectCarrierV5 carrier) =>
            carrier is ProductionDirectCarrierV5.LanDirect or ProductionDirectCarrierV5.NatDirect;

        private static string FormatSessionCarrier(ProductionDirectCarrierV5 carrier) => carrier switch
        {
            ProductionDirectCarrierV5.LanDirect => ProductionSessionCarrier.LanDirect,
            ProductionDirectCarrierV5.NatDirect => ProductionSessionCarrier.NatDirect,
            _ => ProductionSessionCarrier.Relay
        };

        internal static string FormatSessionCarrier(ProductionDirectPeerCarrier carrier) => carrier switch
        {
            ProductionDirectPeerCarrier.NegotiatingDirect =>
                ProductionSessionCarrier.NegotiatingDirect,
            ProductionDirectPeerCarrier.LocalAreaNetworkDirect =>
                ProductionSessionCarrier.LanDirect,
            ProductionDirectPeerCarrier.NatTraversalDirect =>
                ProductionSessionCarrier.NatDirect,
            _ => ProductionSessionCarrier.Relay
        };

        private void SetRelayState(ProductionRelayState state, string errorCode)
        {
            if (IsReauthenticationRequired && state != ProductionRelayState.Stopped)
            {
                state = ProductionRelayState.Failed;
                errorCode = "ACCOUNT_REAUTH_REQUIRED";
            }
            lock (statusSync)
            {
                relayState = state;
                relayErrorCode = errorCode ?? string.Empty;
                if (state == ProductionRelayState.Connected) lastConnectedAtUtc = DateTime.UtcNow;
            }
            if (state != ProductionRelayState.Connected) return;
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
                    try
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
                    catch (Exception error) when (
                        ProductionSessionRouteFaultBoundary.IsSessionLocal(error, token))
                    {
                        HandleSessionPublicationFault(pending.Key, pending.Value);
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

        private static bool HasValidFinalRecoveredTerminalMarker(
            Guid sessionId,
            IList<LocalFrame> frames)
        {
            if (sessionId == Guid.Empty || frames == null || frames.Count == 0) return false;
            ulong previousSequence = 0;
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame == null || frame.SessionId != sessionId || frame.Sequence == 0 ||
                    frame.Sequence <= previousSequence)
                    return false;
                previousSequence = frame.Sequence;
            }

            var final = frames[frames.Count - 1];
            try
            {
                if (final.Type == LocalFrameType.SessionRemoved)
                {
                    LocalPayloadCodec.ParseSessionRemoved(final.Payload);
                    return true;
                }
                if (final.Type != LocalFrameType.Lifecycle) return false;
                return LocalPayloadCodec.ParseLifecycle(final.Payload, out var ignoredExitCode) ==
                    LocalLifecycleState.Ended;
            }
            catch
            {
                // Recovery classification is a release gate. Malformed terminal metadata must
                // remain active/fail closed and be handled by the ordinary history retry path.
                return false;
            }
        }

        private void QueueHistoryUpload(Guid sessionId, bool terminal)
        {
            if (sessionId == Guid.Empty || Volatile.Read(ref disposed) != 0 ||
                destroyedHistorySessions.ContainsKey(sessionId)) return;
            RecordLiveHistoryActivity();
            persistedHistoryBacklogSessions.TryRemove(sessionId, out _);
            if (terminal)
            {
                terminalHistorySessions[sessionId] = 0;
                // A terminal pass is also the recovery barrier before the
                // SessionAgent DPAPI buffer may be released.
                historyReconcileSessions[sessionId] = 0;
            }
            QueueNewHistoryWork(sessionId);
            try { historySignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private void RecordLiveHistoryActivity()
        {
            Interlocked.Exchange(ref lastLiveHistoryActivityTick, Environment.TickCount64);
            CancellationTokenSource active;
            lock (persistedHistoryCancellationSync) active = persistedHistoryCancellation;
            if (active == null) return;
            try { active.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task HistoryUploadLoopAsync(bool allowPersistedBacklog)
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
                var retryImmediately = false;
                if (allowPersistedBacklog && pendingHistorySessions.IsEmpty &&
                    persistedHistoryWorkSessions.IsEmpty)
                    TryPromotePersistedHistoryBacklog(Environment.TickCount64);
                var attemptedHistorySessions = new HashSet<Guid>();
                while (TryTakeNextPendingHistorySession(
                    attemptedHistorySessions,
                    allowPersistedBacklog,
                    out var pending))
                {
                    var terminalPass = false;
                    var reconciledThisPass = false;
                    var persistedBacklogPass = persistedHistoryWorkSessions.ContainsKey(pending.Key);
                    var workCancellation = persistedBacklogPass
                        ? BeginPersistedHistoryWork(pending.Key, token)
                        : null;
                    var workToken = workCancellation?.Token ?? token;
                    try
                    {
                        var queuedFrames = DrainPendingHistoryFrames(pending.Key);
                        var reconcile = historyReconcileSessions.TryRemove(pending.Key, out _) ||
                            terminalHistoryPersistenceFailures.ContainsKey(pending.Key);
                        reconciledThisPass = reconcile;
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
                            uploadFrames = terminalHistoryStore.ReadFrames(
                                pending.Key,
                                () => workToken.IsCancellationRequested ||
                                    ShouldYieldHistoryUpload(pending.Key));
                            if (uploadFrames == null)
                            {
                                retryImmediately |= HasSupersedingHistoryWork(
                                    pending.Key,
                                    workToken,
                                    token);
                                RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                                retry = true;
                                continue;
                            }
                        }
                        else
                        {
                            foreach (var frame in queuedFrames)
                                terminalHistoryStore.AppendCommittedFrame(frame);
                            uploadFrames = queuedFrames;
                        }
                        terminalPass = terminalHistorySessions.ContainsKey(pending.Key) ||
                            (reconcile && HasValidFinalRecoveredTerminalMarker(
                                pending.Key,
                                uploadFrames));
                        terminalHistoryPersistenceFailures.TryRemove(pending.Key, out _);
                        var evaluated = terminalSnapshotEnabled
                            ? ReusePersistedTerminalProduction(pending.Key, uploadFrames)
                            : null;
                        if (evaluated == null && (terminalSnapshotEnabled || terminalCoreShadowEnabled))
                        {
                            evaluated = terminalHistoryProducer.ProcessSession(
                                pending.Key,
                                uploadFrames,
                                persistLegacyArtifacts: terminalSnapshotEnabled,
                                shouldYield: () => workToken.IsCancellationRequested ||
                                    ShouldYieldHistoryUpload(pending.Key));
                            if (evaluated == null)
                            {
                                retryImmediately |= HasSupersedingHistoryWork(
                                    pending.Key,
                                    workToken,
                                    token);
                                RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                                retry = true;
                                continue;
                            }
                        }
                        if (terminalPass)
                            terminalHistoryProducer.RetireShadowSession(pending.Key);
                        var production = terminalSnapshotEnabled ? evaluated : null;
                        ObserveTerminalSnapshotProduction(pending.Key, production);
                        var events = new List<HistoryEventV1>(uploadFrames.Count);
                        foreach (var frame in uploadFrames.OrderBy(value => value.Sequence))
                        {
                            var historyEvent = BuildHistoryEvent(frame);
                            if (historyEvent != null) events.Add(historyEvent);
                        }
                        var durable = !terminalHistoryPersistenceFailures.ContainsKey(pending.Key) &&
                            events.Count > 0 &&
                            await UploadHistoryAsync(pending.Key, events, production, workToken).ConfigureAwait(false);
                        if (!durable)
                        {
                            retryImmediately |= HasSupersedingHistoryWork(
                                pending.Key,
                                workToken,
                                token);
                            RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                            retry = true;
                            continue;
                        }
                        persistedHistoryWorkSessions.TryRemove(pending.Key, out _);
                        if (terminalPass)
                        {
                            terminalHistorySessions.TryRemove(pending.Key, out _);
                            registry?.ReleaseBufferedSession(pending.Key);
                            terminalHistoryProducer.ReleaseSession(pending.Key);
                            terminalCoreShadowStates.TryRemove(pending.Key, out _);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (OperationCanceledException) when (
                        persistedBacklogPass && workCancellation?.IsCancellationRequested == true)
                    {
                        retryImmediately = true;
                        RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                        retry = true;
                    }
                    catch (ProductionTerminalHistoryReconcileRequiredException)
                    {
                        // A live callback can expose a later durable frame before
                        // the independent production WAL has copied its prefix.
                        // Reconcile from the SessionAgent authority immediately;
                        // never persist this transient scheduling boundary as a
                        // native terminal capability failure.
                        // The first detection upgrades incremental work to an
                        // immediate full reconciliation. If that authoritative
                        // pass still cannot fill the same prefix (for example a
                        // locally trimmed buffer), use the ordinary bounded
                        // retry delay instead of decrypting a long WAL in a hot
                        // loop forever.
                        retryImmediately |= !reconciledThisPass;
                        RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                        retry = true;
                    }
                    catch (Exception error)
                    {
                        if (terminalPass || terminalHistorySessions.ContainsKey(pending.Key))
                            terminalHistoryProducer.RetireShadowSession(pending.Key);
                        terminalHistoryPersistenceFailures[pending.Key] = error.GetType().Name;
                        ObserveTerminalSnapshotFailure(pending.Key);
                        retryImmediately |= HasSupersedingHistoryWork(
                            pending.Key,
                            workToken,
                            token);
                        RequeueHistoryWork(pending.Key, persistedBacklogPass, pending.Value);
                        retry = true;
                    }
                    finally
                    {
                        if (workCancellation != null)
                            EndPersistedHistoryWork(pending.Key, workCancellation);
                        activeHistoryWorkSessions.TryRemove(pending.Key, out _);
                        // The capacity-one signal for genuinely newer work may
                        // have been consumed by the sibling worker while this
                        // session was active. A monotonic ticket distinguishes
                        // that callback from this pass's own bounded requeue.
                        if (pendingHistorySessions.TryGetValue(pending.Key, out var queuedTicket) &&
                            HasSupersedingHistoryTicket(pending.Value, queuedTicket))
                        {
                            retry = true;
                            retryImmediately = true;
                        }
                    }
                }
                if (retry || !persistedHistoryBacklogSessions.IsEmpty)
                {
                    // History may be disabled by entitlement for an extended
                    // period. Retain the DPAPI buffer and retry without a hot
                    // loop; transport failures still reach this same bounded
                    // retry path.
                    await WaitForHistoryRetryOrSignalAsync(
                        historySignal,
                        retry ? TimeSpan.FromSeconds(30) : PersistedHistoryIdleDelay,
                        retryImmediately,
                        token).ConfigureAwait(false);
                    // Either the timeout or the consumed signal authorizes the
                    // next snapshot pass; do not wait for a second signal.
                    historyPassReady = true;
                }
            }
        }

        private bool TryTakeNextPendingHistorySession(
            ISet<Guid> attempted,
            bool allowPersistedBacklog,
            out KeyValuePair<Guid, long> pending)
        {
            var ordered = OrderHistoryUploadCandidates(
                pendingHistorySessions.Keys,
                HistoryUploadPriority);
            // A live terminal can append again while its first coalesced batch
            // is uploading. Do not bury that new batch behind every persisted
            // session remaining in this pass. End the pass so the ordinary
            // 100 ms coalescing boundary runs again, then select live work first.
            foreach (var sessionId in ordered.Where(value =>
                HistoryUploadPriority(value) == 0 && !attempted.Contains(value) &&
                !activeHistoryWorkSessions.ContainsKey(value)))
            {
                attempted.Add(sessionId);
                if (destroyedHistorySessions.ContainsKey(sessionId))
                {
                    pendingHistorySessions.TryRemove(sessionId, out _);
                    continue;
                }
                if (!activeHistoryWorkSessions.TryAdd(sessionId, 0)) continue;
                if (!pendingHistorySessions.TryRemove(sessionId, out var value))
                {
                    activeHistoryWorkSessions.TryRemove(sessionId, out _);
                    continue;
                }
                pending = new KeyValuePair<Guid, long>(sessionId, value);
                return true;
            }
            if (ordered.Any(value => HistoryUploadPriority(value) == 0))
            {
                pending = default;
                return false;
            }
            foreach (var sessionId in ordered)
            {
                if (!allowPersistedBacklog && HistoryUploadPriority(sessionId) >= 2) continue;
                if (!attempted.Add(sessionId)) continue;
                if (destroyedHistorySessions.ContainsKey(sessionId))
                {
                    pendingHistorySessions.TryRemove(sessionId, out _);
                    continue;
                }
                if (!activeHistoryWorkSessions.TryAdd(sessionId, 0)) continue;
                if (!pendingHistorySessions.TryRemove(sessionId, out var value))
                {
                    activeHistoryWorkSessions.TryRemove(sessionId, out _);
                    continue;
                }
                pending = new KeyValuePair<Guid, long>(sessionId, value);
                return true;
            }
            pending = default;
            return false;
        }

        private bool TryPromotePersistedHistoryBacklog(long nowTick)
        {
            if (!CanPromotePersistedHistoryBacklog(
                !persistedHistoryBacklogSessions.IsEmpty,
                registry != null && registry.Count > 0,
                !pendingHistorySessions.IsEmpty || !pendingHistoryFrames.IsEmpty ||
                    !terminalHistorySessions.IsEmpty,
                Interlocked.Read(ref lastLiveHistoryActivityTick),
                nowTick,
                checked((long)PersistedHistoryIdleDelay.TotalMilliseconds)))
            {
                return false;
            }

            while (persistedHistoryBacklog.TryDequeue(out var sessionId))
            {
                if (!persistedHistoryBacklogSessions.TryRemove(sessionId, out _)) continue;
                if (destroyedHistorySessions.ContainsKey(sessionId)) continue;
                if (registry != null && registry.ContainsActiveSession(sessionId)) continue;
                historyReconcileSessions[sessionId] = 0;
                persistedHistoryWorkSessions[sessionId] = 0;
                QueueNewHistoryWork(sessionId);
                return true;
            }
            return false;
        }

        internal static bool CanPromotePersistedHistoryBacklog(
            bool hasBacklog,
            bool hasActiveSession,
            bool hasRealtimeHistoryWork,
            long lastLiveActivityTick,
            long nowTick,
            long idleDelayMilliseconds)
        {
            if (lastLiveActivityTick < 0) throw new ArgumentOutOfRangeException(nameof(lastLiveActivityTick));
            if (nowTick < 0) throw new ArgumentOutOfRangeException(nameof(nowTick));
            if (idleDelayMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(idleDelayMilliseconds));
            return hasBacklog && !hasActiveSession && !hasRealtimeHistoryWork &&
                nowTick >= lastLiveActivityTick &&
                nowTick - lastLiveActivityTick >= idleDelayMilliseconds;
        }

        private CancellationTokenSource BeginPersistedHistoryWork(
            Guid sessionId,
            CancellationToken token)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (persistedHistoryCancellationSync)
            {
                if (persistedHistoryCancellation != null)
                    throw new InvalidOperationException("Persisted history work is already active.");
                persistedHistoryCancellation = linked;
                persistedHistoryCancellationSessionId = sessionId;
            }
            if (ShouldYieldHistoryUpload(sessionId)) linked.Cancel();
            return linked;
        }

        private void EndPersistedHistoryWork(Guid sessionId, CancellationTokenSource expected)
        {
            lock (persistedHistoryCancellationSync)
            {
                if (ReferenceEquals(persistedHistoryCancellation, expected) &&
                    persistedHistoryCancellationSessionId == sessionId)
                {
                    persistedHistoryCancellation = null;
                    persistedHistoryCancellationSessionId = Guid.Empty;
                }
            }
            expected.Dispose();
        }

        private void RequeueHistoryWork(
            Guid sessionId,
            bool persistedBacklogPass,
            long workTicket)
        {
            if (workTicket <= 0) throw new ArgumentOutOfRangeException(nameof(workTicket));
            if (destroyedHistorySessions.ContainsKey(sessionId)) return;
            historyReconcileSessions[sessionId] = 0;
            if (!persistedBacklogPass) RequeueHistoryTicket(sessionId, workTicket);
            else
            {
                persistedHistoryWorkSessions.TryRemove(sessionId, out _);
                if ((registry != null && registry.ContainsActiveSession(sessionId)) ||
                    terminalHistorySessions.ContainsKey(sessionId))
                {
                    RequeueHistoryTicket(sessionId, workTicket);
                }
                else
                {
                    QueuePersistedHistoryBacklog(sessionId);
                }
            }
        }

        private void QueueNewHistoryWork(Guid sessionId)
        {
            if (sessionId == Guid.Empty || destroyedHistorySessions.ContainsKey(sessionId)) return;
            var ticket = Interlocked.Increment(ref nextHistoryWorkTicket);
            if (ticket <= 0) throw new InvalidOperationException("History work ticket exhausted.");
            pendingHistorySessions.AddOrUpdate(
                sessionId,
                ticket,
                (_, current) => Math.Max(current, ticket));
        }

        private void RequeueHistoryTicket(Guid sessionId, long workTicket)
        {
            if (destroyedHistorySessions.ContainsKey(sessionId)) return;
            pendingHistorySessions.AddOrUpdate(
                sessionId,
                workTicket,
                (_, current) => Math.Max(current, workTicket));
        }

        private int HistoryUploadPriority(Guid sessionId)
        {
            if (registry != null && registry.ContainsActiveSession(sessionId)) return 0;
            if (terminalHistorySessions.ContainsKey(sessionId)) return 1;
            return 2;
        }

        private bool ShouldYieldHistoryUpload(Guid sessionId)
        {
            var currentPriority = HistoryUploadPriority(sessionId);
            if (currentPriority == 0) return false;
            return pendingHistorySessions.Keys.Any(candidate =>
                candidate != sessionId && HistoryUploadPriority(candidate) < currentPriority);
        }

        private bool HasSupersedingHistoryWork(
            Guid sessionId,
            CancellationToken workToken,
            CancellationToken runtimeToken)
        {
            return !runtimeToken.IsCancellationRequested &&
                (workToken.IsCancellationRequested ||
                    pendingHistorySessions.ContainsKey(sessionId) ||
                    ShouldYieldHistoryUpload(sessionId));
        }

        internal static IList<Guid> OrderHistoryUploadCandidates(
            IEnumerable<Guid> sessionIds,
            Func<Guid, int> priority)
        {
            if (sessionIds == null) throw new ArgumentNullException(nameof(sessionIds));
            if (priority == null) throw new ArgumentNullException(nameof(priority));
            return sessionIds
                .Where(value => value != Guid.Empty)
                .Distinct()
                .OrderBy(priority)
                .ThenBy(value => value)
                .ToList();
        }

        private ProductionTerminalHistoryProductionResult ReusePersistedTerminalProduction(
            Guid sessionId,
            IList<LocalFrame> frames)
        {
            var checkpoint = terminalHistoryProducer.LoadCheckpoint(sessionId);
            var outbox = terminalHistoryProducer.LoadOutbox(sessionId);
            if (checkpoint == null || outbox == null || !checkpoint.SnapshotEligible ||
                checkpoint.AuthorityVersion < 2 ||
                checkpoint.LastSequence == 0 || outbox.SnapshotSequence != checkpoint.LastSequence)
            {
                return null;
            }
            var latestOutput = frames
                .Where(value => value != null && value.Type == LocalFrameType.Output)
                .Select(value => value.Sequence)
                .DefaultIfEmpty(0UL)
                .Max();
            if (latestOutput > checkpoint.LastSequence) return null;
            var snapshots = outbox.Artifacts
                .Where(value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                .ToList();
            if (snapshots.Count != 1 || snapshots[0].SnapshotSequence != checkpoint.LastSequence ||
                snapshots[0].FormatName != SnapshotCrypto.TerminalCoreSnapshotFormatName ||
                snapshots[0].FormatVersion != SnapshotCrypto.TerminalCoreSnapshotFormatVersion ||
                !CryptographicOperations.FixedTimeEquals(
                    checkpoint.SnapshotPlaintext,
                    snapshots[0].Plaintext))
                return null;
            return new ProductionTerminalHistoryProductionResult
            {
                SessionId = sessionId,
                LastSequence = checkpoint.LastSequence,
                SnapshotEligible = true,
                TranscriptPageCount = outbox.Artifacts.Count(
                    value => value.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
            };
        }

        internal static async Task<bool> WaitForHistoryRetryOrSignalAsync(
            SemaphoreSlim signal,
            TimeSpan retryDelay,
            bool retryImmediately,
            CancellationToken token)
        {
            // The shared capacity-one signal may have been consumed by the
            // other history worker while this worker was still committing a
            // batch. Queue state is authoritative: known superseding work must
            // start a fresh pass without falling into the transport backoff.
            if (retryImmediately) return true;
            return await signal.WaitAsync(retryDelay, token).ConfigureAwait(false);
        }

        internal static bool HasSupersedingHistoryTicket(
            long completedWorkTicket,
            long queuedWorkTicket)
        {
            if (completedWorkTicket <= 0) throw new ArgumentOutOfRangeException(nameof(completedWorkTicket));
            if (queuedWorkTicket <= 0) throw new ArgumentOutOfRangeException(nameof(queuedWorkTicket));
            return queuedWorkTicket > completedWorkTicket;
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

        private void ObserveTerminalCoreShadow(
            Guid sessionId,
            ProductionTerminalCoreShadowResult result)
        {
            if (!terminalCoreShadowEnabled || sessionId == Guid.Empty || result == null ||
                Volatile.Read(ref disposed) != 0)
                return;
            var state = result.Status + "|" + result.ReasonCode;
            if (terminalCoreShadowStates.TryGetValue(sessionId, out var previous) &&
                string.Equals(previous, state, StringComparison.Ordinal))
                return;
            terminalCoreShadowStates[sessionId] = state;
            if (string.Equals(result.Status, "SHADOW_RETIRED", StringComparison.Ordinal))
            {
                // terminal-core is a session renderer concern. Recording it as
                // a soft failure must never mutate the physical Relay lifetime.
                connectionSupervisor.ObserveSessionSoftFailure();
            }
            try
            {
                var root = Path.Combine(
                    ProductionAccountStorage.ResolveLocalApplicationDataRoot(),
                    "TermLink",
                    "logs");
                Directory.CreateDirectory(root);
                var line = JsonSerializer.Serialize(new
                {
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    component = "windows-terminal-core-shadow",
                    session_id = sessionId.ToString("D"),
                    status = result.Status,
                    reason_code = result.ReasonCode,
                    last_applied_sequence = result.LastAppliedSequence,
                    next_source_sequence = result.NextSourceSequence,
                    presentation_revision = result.PresentationRevision,
                    snapshot_sha256 = result.SnapshotSha256
                });
                File.AppendAllText(
                    Path.Combine(root, "TermLink-terminal-core-shadow.jsonl"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // A diagnostic path must never become part of terminal or history authority.
            }
        }

        private static void ObserveFileOperationWorkerDiagnostic(
            FileOperationWorkerDiagnostic diagnostic)
        {
            if (diagnostic == null) return;
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink",
                    "logs");
                Directory.CreateDirectory(root);
                var error = diagnostic.Error;
                var line = JsonSerializer.Serialize(new
                {
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    component = "windows-file-operation-worker",
                    event_code = diagnostic.Code,
                    message_zh = diagnostic.MessageZh,
                    message_en = diagnostic.MessageEn,
                    failure_type = error?.GetType().FullName,
                    failure_site = error?.TargetSite == null
                        ? null
                        : (error.TargetSite.DeclaringType?.FullName ?? "unknown") + "." + error.TargetSite.Name,
                    restart_count = diagnostic.RestartCount
                });
                File.AppendAllText(
                    Path.Combine(root, "TermLink-file-operations.jsonl"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Bounded local diagnostics must never affect task delivery.
            }
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
                catch (ProductionReauthenticationRequiredException)
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
                await PublishSessionSnapshotAsync(token).ConfigureAwait(false);
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
                await metadataSnapshotGate.WaitAsync(token).ConfigureAwait(false);
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
                finally { metadataSnapshotGate.Release(); }
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

        private static void ClearMemoryStream(MemoryStream stream)
        {
            if (stream == null || !stream.TryGetBuffer(out var segment) || segment.Array == null)
                return;
            CryptographicOperations.ZeroMemory(
                new Span<byte>(segment.Array, segment.Offset, segment.Count));
        }

        internal static byte[] ComputeExactEnvelopeSha256(ReadOnlySpan<byte> rawBytes)
        {
            if (rawBytes.Length == 0)
                throw new ArgumentException("An inbound Envelope cannot be empty.", nameof(rawBytes));
            return SHA256.HashData(rawBytes);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket active, CancellationToken token)
        {
            // The physical socket pump owns only WebSocket I/O. A malformed,
            // oversized or non-binary application frame is drained and retired
            // locally; it must not replace an otherwise healthy Relay
            // generation. Keeping the fragment buffer small also prevents one
            // rejected frame from reserving the full envelope limit forever.
            var buffer = new byte[64 * 1024];
            try
            {
                while (active.State == WebSocketState.Open)
                {
                    using var stream = new MemoryStream();
                    try
                    {
                        WebSocketReceiveResult part;
                        WebSocketMessageType? messageType = null;
                        var rejected = false;
                        do
                        {
                            // Exceptions and Close frames here are physical Relay
                            // failures. Payload validation starts only after this read.
                            part = await active.ReceiveAsync(buffer, token).ConfigureAwait(false);
                            if (part.MessageType == WebSocketMessageType.Close) return;
                            messageType ??= part.MessageType;
                            if (part.MessageType != messageType || part.MessageType != WebSocketMessageType.Binary)
                            {
                                rejected = true;
                            }
                            if (!rejected && stream.Length + part.Count <= MaxRealtimeEnvelopeBytes)
                            {
                                stream.Write(buffer, 0, part.Count);
                            }
                            else
                            {
                                rejected = true;
                            }
                        }
                        while (!part.EndOfMessage);

                        if (rejected || stream.Length == 0)
                        {
                            connectionSupervisor.ObserveSessionSoftFailure();
                            continue;
                        }
                        byte[] rawBytes = null;
                        try
                        {
                            // MemoryStream is only the frame assembler. Transfer
                            // ownership of the exact assembled bytes to the
                            // carrier before parsing; no protobuf reserialization
                            // is allowed to define replay identity.
                            rawBytes = stream.ToArray();
                            using var ingress = ProductionInboundEnvelope.Parse(rawBytes);
                            rawBytes = null;
                            await ArbitrateRelayIngressAsync(active, ingress, token)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            if (rawBytes != null) CryptographicOperations.ZeroMemory(rawBytes);
                        }
                    }
                    catch (Exception error) when (
                        ProductionSessionRouteFaultBoundary.IsSessionLocal(error, token))
                    {
                        // The protobuf may be too damaged to locate a session. It is
                        // still application data, not evidence that WSS I/O failed.
                        connectionSupervisor.ObserveSessionSoftFailure();
                    }
                    finally
                    {
                        ClearMemoryStream(stream);
                        CryptographicOperations.ZeroMemory(buffer);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private async Task ArbitrateRelayIngressAsync(
            ClientWebSocket active,
            ProductionInboundEnvelope ingress,
            CancellationToken token)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            var senderId = GuidFromBytes(ingress.Envelope.SenderDeviceId.Span);
            if (senderId == Guid.Empty)
                throw new InvalidDataException("Envelope sender is invalid.");
            if (directDataRuntime == null ||
                !IsRelayIngressDirectArbitrationEligible(ingress.Envelope, senderId))
            {
                await HandleIncomingAsync(
                    active,
                    ingress,
                    ProductionDirectIngressSourceV5.Relay,
                    senderId,
                    null,
                    token).ConfigureAwait(false);
                return;
            }

            var exact = ingress.RawBytes.ToArray();
            try
            {
                if (!await directDataRuntime.ArbitrateIngressAsync(
                        senderId,
                        ProductionDirectIngressSourceV5.Relay,
                        exact,
                        token).ConfigureAwait(false))
                {
                    // Relay input must never be lost if native arbitration is
                    // unavailable. DirectDataRuntime normally performs this
                    // same fallback before returning false.
                    await HandleIncomingAsync(
                        active,
                        ingress,
                        ProductionDirectIngressSourceV5.Relay,
                        senderId,
                        null,
                        token).ConfigureAwait(false);
                }
            }
            finally { CryptographicOperations.ZeroMemory(exact); }
        }

        internal static bool IsRelayIngressDirectArbitrationEligible(
            Envelope envelope,
            Guid authenticatedSenderDeviceId)
        {
            return authenticatedSenderDeviceId != Guid.Empty &&
                ProductionDirectRoutePolicy.Evaluate(envelope, out var directPeerDeviceId) ==
                    ProductionDirectRouteEligibility.DirectEligible &&
                directPeerDeviceId == authenticatedSenderDeviceId;
        }

        private async Task HandleIncomingAsync(
            ClientWebSocket active,
            ProductionInboundEnvelope ingress,
            ProductionDirectIngressSourceV5 source,
            Guid carrierPeerDeviceId,
            ProductionDirectPathFenceV5 carrierPath,
            CancellationToken token)
        {
            if (IsReauthenticationRequired || Volatile.Read(ref disposed) != 0) return;
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            var envelope = ingress.Envelope;
            var senderId = GuidFromBytes(envelope.SenderDeviceId.Span); var sessionId = GuidFromBytes(envelope.SessionId.Span);
            if (senderId == Guid.Empty || sessionId == Guid.Empty)
                throw new InvalidDataException("Envelope route identity is invalid.");
            Subscriber subscriber = null;
            await ProductionSessionRouteFaultBoundary.ExecuteAsync(async () =>
            {
            if (source == ProductionDirectIngressSourceV5.Direct &&
                (carrierPeerDeviceId == Guid.Empty || carrierPath == null ||
                 carrierPath.PathEpoch == 0 || carrierPath.ConnectionId == Guid.Empty ||
                 carrierPath.AttemptId == Guid.Empty ||
                 carrierPeerDeviceId != senderId ||
                 ProductionDirectRoutePolicy.Evaluate(envelope, out var directPeer) !=
                    ProductionDirectRouteEligibility.DirectEligible ||
                 directPeer != senderId))
                throw new InvalidDataException("Direct ingress route is not eligible.");
            if (envelope.ProtocolVersion != 1 || envelope.Direction != Direction.AndroidToPc)
                throw new InvalidDataException("Envelope metadata is invalid.");
            if (!GuidFromBytes(envelope.RecipientDeviceId.Span).Equals(credentials.DeviceId))
                throw new InvalidDataException("Envelope recipient mismatch.");
            if (envelope.Seq == 0 || envelope.Nonce.Length != 12 || envelope.Ciphertext.Length < 16)
                throw new InvalidDataException("Located session Envelope metadata is invalid.");
            if (envelope.MessageType == MessageType.SessionCatalogRequest || envelope.MessageType == MessageType.SessionCleanupRequest)
            {
                if (sessionId != config.PcId || envelope.Ack != 0 || registry == null || !registry.RemoteAccessEnabled)
                    throw new InvalidDataException("Catalog route is invalid.");
                subscriber = await GetSubscriberAsync(sessionId, senderId, token, true).ConfigureAwait(false);
            }
            else if (envelope.MessageType == MessageType.Subscribe)
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
                    envelope.MessageType != MessageType.SessionTerminateRequest &&
                    envelope.MessageType != MessageType.HostHistoryRequest &&
                    envelope.MessageType != MessageType.HostHistoryControl) return;
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
            var deviceRequest = envelope.MessageType == MessageType.SessionCatalogRequest || envelope.MessageType == MessageType.SessionCleanupRequest;
            var ingressApplied = !deviceRequest;
            try
            {
                plaintext = SessionCrypto.Decrypt(sessionKey, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
                if (!deviceRequest) ObserveAndroidEnvelope(senderId, sessionId, envelope.Seq);
                TouchSubscriber(subscriber);
                if (envelope.MessageType == MessageType.SessionCatalogRequest || envelope.MessageType == MessageType.SessionCleanupRequest)
                {
                    if (envelope.MessageType == MessageType.SessionCleanupRequest)
                    {
                        var cleanup = await CleanupSessionsAsync(SessionCleanupRequestPayload.Parser.ParseFrom(plaintext), token).ConfigureAwait(false);
                        var cleanupBytes = cleanup.ToByteArray();
                        try
                        {
                            await SendSessionControlEnvelopeAsync(active, sessionId, subscriber, envelope.Seq,
                                MessageType.SessionCleanupResponse, cleanupBytes, token).ConfigureAwait(false);
                        }
                        finally { CryptographicOperations.ZeroMemory(cleanupBytes); }
                        return;
                    }
                    var request = SessionCatalogRequestPayload.Parser.ParseFrom(plaintext);
                    if (GuidFromBytes(request.RequestId.Span) == Guid.Empty)
                        throw new InvalidDataException("Catalog request ID is invalid.");
                    var responseBytes = BuildSessionCatalog(request.RequestId).ToByteArray();
                    try
                    {
                        await SendSessionControlEnvelopeAsync(active, sessionId, subscriber,
                            envelope.Seq, MessageType.SessionCatalogResponse, responseBytes, token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(responseBytes); }
                    return;
                }
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
                        beginSubscribeGeneration: true,
                        token).ConfigureAwait(false);
                    SchedulePcToAndroidCutover(senderId);
                    return;
                }
                if (envelope.MessageType == MessageType.Unsubscribe)
                {
                    if (plaintext.Length != 0) throw new InvalidDataException("Unsubscribe payload must be empty.");
                    CancelHostHistoryRequests(sessionId, senderId);
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
                    NotifyRelayCapacityReleased(
                        relayExactOutbox.PruneDurablyAcknowledged(
                            senderId,
                            sessionId,
                            acknowledgement.HighestContiguousSeq),
                        SignalPublisherSafely);
                    await UpdateDirectAppliedCursorSafelyAsync(
                        senderId,
                        ProductionDirectDirectionV5.PcToAndroid,
                        sessionId,
                        acknowledgement.HighestContiguousSeq,
                        token).ConfigureAwait(false);
                    if (TakeSubscriberDrainRequest(subscriber))
                    {
                        await ReplaySubscriberAsync(
                            active,
                            sessionId,
                            subscriber,
                            acknowledgement.HighestContiguousSeq,
                            beginSubscribeGeneration: false,
                            token).ConfigureAwait(false);
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
                    NotifyRelayCapacityReleased(
                        relayExactOutbox.PruneDurablyAcknowledged(
                            senderId,
                            sessionId,
                            flow.HighestContiguousSeq),
                        SignalPublisherSafely);
                    await UpdateDirectAppliedCursorSafelyAsync(
                        senderId,
                        ProductionDirectDirectionV5.PcToAndroid,
                        sessionId,
                        flow.HighestContiguousSeq,
                        token).ConfigureAwait(false);
                    if (TakeSubscriberDrainRequest(subscriber))
                    {
                        await ReplaySubscriberAsync(
                            active,
                            sessionId,
                            subscriber,
                            flow.HighestContiguousSeq,
                            beginSubscribeGeneration: false,
                            token).ConfigureAwait(false);
                    }
                    return;
                }
                if (envelope.MessageType == MessageType.ResizeRequest)
                {
                    var resize = ResizePayload.Parser.ParseFrom(plaintext);
                    // Legacy automatic viewport notifications remain inert. Only
                    // an explicit, leased, bounded one-shot request is executable.
                    if (resize.RequestId.Length != 16 || GuidFromBytes(resize.RequestId.Span) == Guid.Empty) return;
                    lock (subscriber.Sync)
                    {
                        if (envelope.Seq <= subscriber.LastResizeRequestSequence) return;
                        subscriber.LastResizeRequestSequence = envelope.Seq;
                    }
                    var requestSequence = envelope.Seq;
                    _ = HandleWindowResizeAsync(active, sessionId, senderId, subscriber, requestSequence, resize, token);
                    return;
                }
                if (envelope.MessageType == MessageType.HostHistoryRequest)
                {
                    HostHistoryRequestPayload historyRequest;
                    try { historyRequest = HostHistoryRequestPayload.Parser.ParseFrom(plaintext); }
                    catch (InvalidProtocolBufferException) { return; }
                    WriteHostHistoryDiagnostic("request_received session=" + sessionId.ToString("N") +
                        " kind=" + historyRequest.Kind + " sender=" + senderId.ToString("N") +
                        " snapshot_seq=" + historyRequest.SnapshotSeq +
                        " before_line_id=" + historyRequest.BeforeLineId +
                        " after_seq=" + historyRequest.AfterSeq +
                        " through_seq=" + historyRequest.ThroughSeq);
                    if (historyRequest.RequestId.Length != 16 ||
                        GuidFromBytes(historyRequest.RequestId.Span) == Guid.Empty)
                    {
                        return;
                    }
                    var requestId = GuidFromBytes(historyRequest.RequestId.Span);
                    var responseRequest = new ProductionHostHistoryResponseRequest(sessionId, senderId);
                    if (!hostHistoryRequests.TryAdd(requestId, responseRequest))
                    {
                        responseRequest.Dispose();
                        return;
                    }
                    var retainedRequest = historyRequest.Clone();
                    var retainedSubscriber = subscriber;
                    var requestSequence = envelope.Seq;
                    _ = Task.Run(
                        () => ServeHostHistoryRequestAsync(
                            active,
                            sessionId,
                            retainedSubscriber,
                            retainedRequest,
                            requestSequence,
                            requestId,
                            responseRequest,
                            token),
                        CancellationToken.None);
                    return;
                }
                if (envelope.MessageType == MessageType.HostHistoryControl)
                {
                    HostHistoryControlPayload control;
                    try { control = HostHistoryControlPayload.Parser.ParseFrom(plaintext); }
                    catch (InvalidProtocolBufferException) { return; }
                    var requestId = GuidFromBytes(control.RequestId.Span);
                    if (requestId == Guid.Empty || !hostHistoryRequests.TryGetValue(requestId, out var pending) ||
                        pending.SessionId != sessionId || pending.DeviceId != senderId) return;
                    if (control.Action == HostHistoryControlActionV1.Cancel)
                    {
                        pending.Cancel();
                        return;
                    }
                    if (control.Action != HostHistoryControlActionV1.ChunkAck || control.ObjectId.Length != 16)
                        return;
                    var objectId = GuidFromBytes(control.ObjectId.Span);
                    if (objectId != Guid.Empty) pending.Acknowledge(objectId, control.ChunkIndex);
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
                        if (previousSessionId == sessionId)
                        {
                            var deletionDurable = terminalHistoryStore.IsSessionDeleted(sessionId);
                            await SendSessionTerminateResponseAsync(
                                active, sessionId, subscriber, envelope.Seq, requestId,
                                deletionDurable,
                                deletionDurable ? null : "HISTORY_DELETE_NOT_DURABLE",
                                token)
                                .ConfigureAwait(false);
                        }
                        return;
                    }
                    if (!terminationRequests.TryAdd(requestId, sessionId)) return;
                    var activeSession = registry.ContainsActiveSession(sessionId);
                    var knownSession = registry.ContainsSession(sessionId);
                    var alreadyDeleted = terminalHistoryStore.IsSessionDeleted(sessionId);
                    var retainedHistory = terminalHistoryStore.ContainsSession(sessionId);
                    var terminated = !activeSession ||
                        registry.TerminateSession(sessionId, explicitUserClose: false);
                    if (activeSession && !terminated)
                    {
                        terminationRequests.TryRemove(requestId, out _);
                        await SendSessionTerminateResponseAsync(
                            active, sessionId, subscriber, envelope.Seq, requestId, false,
                            "SESSION_TERMINATION_FAILED", token).ConfigureAwait(false);
                        return;
                    }
                    if (!knownSession && !retainedHistory && !alreadyDeleted)
                    {
                        terminationRequests.TryRemove(requestId, out _);
                        await SendSessionTerminateResponseAsync(
                            active, sessionId, subscriber, envelope.Seq, requestId, false,
                            "SESSION_NOT_FOUND", token).ConfigureAwait(false);
                        return;
                    }
                    if (!TryEnsureDurableSessionHistoryDeletion(sessionId, out var deletionError))
                    {
                        terminationRequests.TryRemove(requestId, out _);
                        await SendSessionTerminateResponseAsync(
                            active, sessionId, subscriber, envelope.Seq, requestId, false,
                            deletionError, token).ConfigureAwait(false);
                        return;
                    }
                    PruneTerminationRequests(requestId);
                    await SendSessionTerminateResponseAsync(
                        active, sessionId, subscriber, envelope.Seq, requestId, true, null, token)
                        .ConfigureAwait(false);
                    return;
                }
                if (envelope.MessageType != MessageType.InputEvent) return;
                var input = InputEventPayload.Parser.ParseFrom(plaintext);
                if (input.InputId.Length != 16 || input.LeaseId.Length != 16 || input.InputSeq == 0 || input.Input.Length == 0) return;
                // A valid input becomes applied only after SessionHost has
                // durably committed its accepted/rejected result. Later
                // control messages may complete first, but the contiguous
                // tracker below cannot advance across this pending sequence.
                ingressApplied = false;
                var inputId = GuidFromBytes(input.InputId.Span); var leaseId = GuidFromBytes(input.LeaseId.Span);
                var inputEnvelopeSha256 = ComputeExactEnvelopeSha256(ingress.RawBytes.Span);
                try
                {
                    // The SessionAgent owns the durable at-most-once fence. On
                    // runtime restart the volatile map is empty, so recover
                    // that identity before deciding whether this is a new
                    // command. A missing identity is the only new-input path;
                    // an old count-only fence fails closed below.
                    var persistedSessionId = Guid.Empty;
                    var persistedSenderDeviceId = Guid.Empty;
                    byte[] persistedEnvelopeSha256 = null;
                    var persistedIdentity = registry != null &&
                        registry.TryFindRemoteInputIdentity(
                            inputId,
                            out persistedSessionId,
                            out persistedSenderDeviceId,
                            out persistedEnvelopeSha256);
                    try
                    {
                        if (persistedIdentity &&
                            (persistedSessionId != sessionId ||
                             persistedSenderDeviceId == Guid.Empty ||
                             persistedSenderDeviceId != senderId ||
                             persistedEnvelopeSha256 == null ||
                             !CryptographicOperations.FixedTimeEquals(
                                 persistedEnvelopeSha256,
                                 inputEnvelopeSha256)))
                        {
                            throw new InvalidDataException(
                                "An input ID was reused for a different authenticated Envelope.");
                        }
                    }
                    finally
                    {
                        if (persistedEnvelopeSha256 != null)
                            CryptographicOperations.ZeroMemory(persistedEnvelopeSha256);
                    }
                    if (!TryRememberInputRequest(
                            inputId,
                            sessionId,
                            senderId,
                            inputEnvelopeSha256,
                            out var duplicateInput,
                            out var inputReplayCapacityExceeded))
                    {
                        // Do not turn bounded replay pressure into a physical
                        // connection fault. No durable fence was admitted, so
                        // the sender may retry after completed records retire.
                        if (inputReplayCapacityExceeded) return;
                        throw new InvalidDataException(
                            "An input ID was reused for a different authenticated Envelope.");
                    }
                    if (inputReplayRecords.TryGetValue(inputId, out var sequencedReplay))
                    {
                        lock (sequencedReplay.Sync)
                        {
                            if (sequencedReplay.EnvelopeSequence == 0)
                                sequencedReplay.EnvelopeSequence = envelope.Seq;
                            else if (sequencedReplay.EnvelopeSequence != envelope.Seq)
                                throw new InvalidDataException(
                                    "An input ID was reused for a different Envelope sequence.");
                        }
                    }
                    if (duplicateInput || persistedIdentity)
                    {
                        TryHydrateInputReplayResult(inputId, sessionId);
                        if (inputReplayRecords.TryGetValue(inputId, out var durableReplay))
                        {
                            var hasDurableResult = false;
                            lock (durableReplay.Sync)
                                hasDurableResult = durableReplay.Result.HasValue &&
                                    durableReplay.ResultSequence > 0;
                            if (hasDurableResult)
                                ingressApplied = true;
                        }
                        await ReplayRemoteInputResultAsync(
                            active,
                            sessionId,
                            senderId,
                            subscriber,
                            inputId,
                            inputReplayRecords.TryGetValue(inputId, out var remembered)
                                ? remembered.ResultSequence
                                : 0,
                            token).ConfigureAwait(false);
                        return;
                    }

                    byte[] canonicalInput = null;
                    if (typedInputEnabled && input.TypedInput != null)
                    {
                        if (!TryEncodeTypedInput(input.TypedInput, out canonicalInput) ||
                            canonicalInput.Length != input.Input.Length ||
                            !CryptographicOperations.FixedTimeEquals(canonicalInput, input.Input.Span))
                        {
                            if (canonicalInput != null) CryptographicOperations.ZeroMemory(canonicalInput);
                            ingressApplied = TryPersistRemoteInputRejection(
                                sessionId,
                                inputId,
                                LocalRemoteInputResult.InputPayloadRejected,
                                senderId,
                                inputEnvelopeSha256);
                            return;
                        }
                    }
                    var sequenceKey = sessionId.ToString("N") + senderId.ToString("N");
                    var previous = inputSequences.GetOrAdd(sequenceKey, 0);
                    if (input.InputSeq <= previous)
                    {
                        ingressApplied = TryPersistRemoteInputRejection(
                            sessionId,
                            inputId,
                            LocalRemoteInputResult.InputSequenceRejected,
                            senderId,
                            inputEnvelopeSha256);
                        return;
                    }
                    if (!await LeaseMatchesAsync(sessionId, senderId, leaseId, input.LeaseVersion, token).ConfigureAwait(false))
                    {
                        ingressApplied = TryPersistRemoteInputRejection(
                            sessionId,
                            inputId,
                            LocalRemoteInputResult.ControlLeaseRejected,
                            senderId,
                            inputEnvelopeSha256);
                        return;
                    }
                    var inputBytes = canonicalInput ?? input.Input.ToByteArray();
                    try
                    {
                        var dispatch = registry.DispatchRemoteInput(
                            sessionId,
                            inputId,
                            input.ObservedLocalInputEpoch,
                            inputBytes,
                            senderId,
                            inputEnvelopeSha256);
                        if (dispatch == RemoteInputDispatchOutcome.Enqueued)
                        {
                            inputSequences[sequenceKey] = input.InputSeq;
                        }
                        else if (dispatch == RemoteInputDispatchOutcome.Unavailable)
                        {
                            ingressApplied = TryPersistRemoteInputRejection(
                                sessionId,
                                inputId,
                                LocalRemoteInputResult.SessionInputUnavailable,
                                senderId,
                                inputEnvelopeSha256);
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(inputBytes); }
                }
                finally { CryptographicOperations.ZeroMemory(inputEnvelopeSha256); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ingressApplied)
                    await MarkAndroidEnvelopeAppliedAsync(
                        senderId,
                        sessionId,
                        envelope.Seq,
                        token).ConfigureAwait(false);
            }
            }, error =>
            {
                if (source == ProductionDirectIngressSourceV5.Direct)
                    ObserveDirectDataPathFault(
                        carrierPeerDeviceId,
                        carrierPath,
                        "DIRECT_INGRESS_REJECTED");
                else
                    HandleLocatedSessionFault(sessionId, senderId, subscriber);
            }, token)
                .ConfigureAwait(false);
        }

        private bool TryRememberInputRequest(
            Guid inputId,
            Guid sessionId,
            Guid senderDeviceId,
            byte[] envelopeSha256,
            out bool duplicate,
            out bool capacityExceeded)
        {
            duplicate = false;
            capacityExceeded = false;
            if (inputId == Guid.Empty || sessionId == Guid.Empty || senderDeviceId == Guid.Empty ||
                envelopeSha256 == null || envelopeSha256.Length != 32)
            {
                return false;
            }
            lock (inputReplayAdmissionSync)
            {
                if (inputReplayRecords.TryGetValue(inputId, out var retained))
                {
                    lock (retained.Sync)
                    {
                        duplicate = retained.SessionId == sessionId &&
                            retained.SenderDeviceId == senderDeviceId &&
                            retained.EnvelopeSha256 != null &&
                            CryptographicOperations.FixedTimeEquals(
                                retained.EnvelopeSha256,
                                envelopeSha256);
                    }
                    return duplicate;
                }

                if (!TryMakeInputReplayCapacity())
                {
                    capacityExceeded = true;
                    return false;
                }
                var candidate = new InputReplayRecord
                {
                    SessionId = sessionId,
                    SenderDeviceId = senderDeviceId,
                    EnvelopeSha256 = (byte[])envelopeSha256.Clone()
                };
                if (!inputReplayRecords.TryAdd(inputId, candidate))
                {
                    CryptographicOperations.ZeroMemory(candidate.EnvelopeSha256);
                    return false;
                }
                inputReplayOrder.Enqueue(inputId);
                return true;
            }
        }

        /// <summary>
        /// Makes room without ever evicting an input whose application result
        /// is unknown. Returning false is a fail-closed admission refusal; it
        /// is preferable to unbounded memory or losing the at-most-once fence.
        /// Caller owns inputReplayAdmissionSync.
        /// </summary>
        private bool TryMakeInputReplayCapacity()
        {
            var inspect = inputReplayOrder.Count;
            while (inputReplayRecords.Count >= MaxRememberedInputRequests &&
                inspect-- > 0 && inputReplayOrder.TryDequeue(out var oldestInputId))
            {
                if (!inputReplayRecords.TryGetValue(oldestInputId, out var candidate)) continue;
                lock (candidate.Sync)
                {
                    // A pending fence must survive pressure. It is the only
                    // thing preventing a retry from writing SessionHost twice.
                    if (!candidate.Result.HasValue || candidate.ResultSequence == 0)
                    {
                        inputReplayOrder.Enqueue(oldestInputId);
                        continue;
                    }
                }
                if (inputReplayRecords.TryRemove(oldestInputId, out var removed))
                {
                    if (removed.EnvelopeSha256 != null)
                        CryptographicOperations.ZeroMemory(removed.EnvelopeSha256);
                }
            }
            return inputReplayRecords.Count < MaxRememberedInputRequests;
        }

        /// <summary>
        /// A rejection is an application result too: SessionHost must commit
        /// it to the durable event buffer before it can satisfy later carrier
        /// duplicates. If the command could not be durably fenced at all,
        /// release only the volatile reservation so a retry is not stranded.
        /// </summary>
        private bool TryPersistRemoteInputRejection(
            Guid sessionId,
            Guid inputId,
            LocalRemoteInputResult result,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            if (registry != null && registry.TrySendRemoteInputRejection(
                    sessionId,
                    inputId,
                    result,
                    senderDeviceId,
                    envelopeSha256))
            {
                return true;
            }

            var durableFenceExists = false;
            byte[] persistedDigest = null;
            try
            {
                durableFenceExists = registry != null &&
                    registry.TryReadRemoteInputIdentity(
                        sessionId,
                        inputId,
                        out var persistedSender,
                        out persistedDigest) &&
                    (persistedSender == Guid.Empty ||
                     persistedDigest == null ||
                     (persistedSender == senderDeviceId &&
                      CryptographicOperations.FixedTimeEquals(
                          persistedDigest,
                          envelopeSha256)));
            }
            finally
            {
                if (persistedDigest != null)
                    CryptographicOperations.ZeroMemory(persistedDigest);
            }
            if (durableFenceExists) return false;

            lock (inputReplayAdmissionSync)
            {
                if (!inputReplayRecords.TryGetValue(inputId, out var pending)) return false;
                lock (pending.Sync)
                {
                    if (pending.Result.HasValue || pending.ResultSequence != 0 ||
                        pending.SessionId != sessionId ||
                        pending.SenderDeviceId != senderDeviceId ||
                        pending.EnvelopeSha256 == null ||
                        !CryptographicOperations.FixedTimeEquals(
                            pending.EnvelopeSha256,
                            envelopeSha256))
                    {
                        return false;
                    }
                }
                if (inputReplayRecords.TryRemove(inputId, out var removed) &&
                    removed.EnvelopeSha256 != null)
                {
                    CryptographicOperations.ZeroMemory(removed.EnvelopeSha256);
                }
            }
            return false;
        }

        private bool TryHydrateInputReplayResult(Guid inputId, Guid sessionId)
        {
            if (registry == null || inputId == Guid.Empty || sessionId == Guid.Empty ||
                !inputReplayRecords.TryGetValue(inputId, out var replay))
            {
                return false;
            }
            lock (replay.Sync)
            {
                if (replay.SessionId != sessionId) return false;
                if (replay.Result.HasValue && replay.ResultSequence != 0) return true;
            }
            if (!registry.TryReadRemoteInputResult(sessionId, inputId, out var frame))
                return false;
            try
            {
                if (frame == null || frame.SessionId != sessionId ||
                    frame.Type != LocalFrameType.RemoteInputResult || frame.Sequence == 0)
                {
                    return false;
                }
                var resultInputId = LocalPayloadCodec.ParseRemoteInputResult(
                    frame.Payload,
                    out var result);
                if (resultInputId != inputId) return false;
                lock (replay.Sync)
                {
                    if (replay.SessionId != sessionId) return false;
                    replay.Result = result;
                    replay.ResultSequence = frame.Sequence;
                }
                return true;
            }
            finally
            {
                if (frame?.Payload != null)
                    CryptographicOperations.ZeroMemory(frame.Payload);
            }
        }

        private async Task ReplayRemoteInputResultAsync(
            ClientWebSocket active,
            Guid sessionId,
            Guid senderDeviceId,
            Subscriber subscriber,
            Guid inputId,
            ulong ignoredResultSequence,
            CancellationToken token)
        {
            if (subscriber == null || inputId == Guid.Empty ||
                !inputReplayRecords.TryGetValue(inputId, out var replay)) return;
            ulong resultSequence;
            lock (replay.Sync)
            {
                if (replay.SessionId != sessionId ||
                    replay.SenderDeviceId != senderDeviceId ||
                    !replay.Result.HasValue || replay.ResultSequence == 0) return;
                resultSequence = replay.ResultSequence;
            }
            if (ignoredResultSequence != 0 && ignoredResultSequence != resultSequence) return;
            if (registry == null || !registry.TryReadRemoteInputResult(
                sessionId,
                inputId,
                out var resultFrame)) return;
            byte[] payload = null;
            try
            {
                if (resultFrame == null || resultFrame.Sequence != resultSequence ||
                    resultFrame.Type != LocalFrameType.RemoteInputResult)
                    return;
                LocalPayloadCodec.ParseRemoteInputResult(
                    resultFrame.Payload,
                    out var result);
                lock (replay.Sync)
                {
                    if (!replay.Result.HasValue || replay.Result.Value != result) return;
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
                payload = BuildRealtimePayload(
                    resultFrame,
                    replayShellType,
                    replayMetadata).ToByteArray();
                await SendEncryptedAsync(
                    active,
                    sessionId,
                    subscriber,
                    MapMessageType(resultFrame),
                    Direction.PcToAndroid,
                    resultFrame.Sequence,
                    payload,
                    token).ConfigureAwait(false);
            }
            finally
            {
                if (payload != null) CryptographicOperations.ZeroMemory(payload);
                if (resultFrame?.Payload != null)
                    CryptographicOperations.ZeroMemory(resultFrame.Payload);
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

        private async Task ServeHostHistoryRequestAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            HostHistoryRequestPayload request,
            ulong requestSequence,
            Guid requestId,
            ProductionHostHistoryResponseRequest responseRequest,
            CancellationToken token)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                responseRequest.CancellationToken);
            var requestToken = linkedCancellation.Token;
            ProductionHostHistoryGateLease gateLease = null;
            try
            {
                gateLease = await ProductionHostHistoryGateLease.AcquireAsync(
                    hostHistoryResponseGate,
                    requestToken).ConfigureAwait(false);
                long keyEpoch;
                var pairwiseKey = SnapshotPairwiseKey(subscriber, out keyEpoch);
                if (pairwiseKey == null) return;
                var recipient = new ProductionHistoryRecipient
                {
                    DeviceId = subscriber.DeviceId,
                    PairwiseKey = pairwiseKey,
                    KeyEpoch = keyEpoch
                };
                try
                {
                    using var selection = hostHistoryService.Select(sessionId, request, recipient);
                    WriteHostHistoryDiagnostic("selection session=" + sessionId.ToString("N") +
                        " status=" + selection.Status + " errorCode=" + selection.ErrorCode +
                        " hasObject=" + (selection.Object != null));
                    await SendHostHistorySelectionAsync(
                        active,
                        sessionId,
                        subscriber,
                        request.RequestId,
                        requestSequence,
                        selection,
                        responseRequest,
                        requestToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(pairwiseKey); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException ||
                error is InvalidDataException ||
                error is CryptographicException ||
                error is WebSocketException ||
                error is ObjectDisposedException)
            {
                connectionSupervisor.ObserveSessionSoftFailure();
            }
            finally
            {
                gateLease?.Dispose();
                ((ICollection<KeyValuePair<Guid, ProductionHostHistoryResponseRequest>>)hostHistoryRequests)
                    .Remove(new KeyValuePair<Guid, ProductionHostHistoryResponseRequest>(
                        requestId,
                        responseRequest));
                responseRequest.Dispose();
            }
        }

        private async Task SendHostHistorySelectionAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ByteString requestId,
            ulong requestSequence,
            ProductionHostHistorySelection selection,
            ProductionHostHistoryResponseRequest responseRequest,
            CancellationToken token)
        {
            if (destroyedHistorySessions.ContainsKey(sessionId)) return;
            var historyObject = selection.Object;
            var objectBytes = historyObject?.SealedObject?.LongLength ?? 0;
            var sendCount = historyObject == null
                ? 1
                : checked((int)Math.Max(
                    1,
                    (objectBytes + HostHistoryResponseChunkBytes - 1) /
                        HostHistoryResponseChunkBytes));
            var chunkCount = historyObject == null ? 0 : sendCount;
            var digest = historyObject == null
                ? Array.Empty<byte>()
                : SHA256.HashData(historyObject.SealedObject);
            try
            {
                for (var index = 0; index < sendCount; index++)
                {
                    if (destroyedHistorySessions.ContainsKey(sessionId)) return;
                    var offset = checked(index * HostHistoryResponseChunkBytes);
                    var count = historyObject == null
                        ? 0
                        : Math.Min(HostHistoryResponseChunkBytes, historyObject.SealedObject.Length - offset);
                    var response = ProductionHostHistoryResponseBuilder.Build(
                        requestId,
                        selection,
                        index,
                        chunkCount,
                        digest,
                        offset,
                        count);
                    Task chunkAcknowledgement = null;
                    if (historyObject != null)
                    {
                        chunkAcknowledgement = responseRequest.PrepareChunkAcknowledgement(
                            historyObject.ObjectId,
                            checked((uint)index));
                    }
                    var plaintext = response.ToByteArray();
                    try
                    {
                        await SendSessionControlEnvelopeAsync(
                            active,
                            sessionId,
                            subscriber,
                            requestSequence,
                            MessageType.HostHistoryResponse,
                            plaintext,
                            token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                    if (chunkAcknowledgement != null)
                    {
                        var timeout = Task.Delay(HostHistoryChunkAckTimeout, token);
                        var completed = await Task.WhenAny(chunkAcknowledgement, timeout).ConfigureAwait(false);
                        if (completed != chunkAcknowledgement)
                            throw new OperationCanceledException("Host history chunk acknowledgement timed out.");
                        await chunkAcknowledgement.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (digest.Length > 0) CryptographicOperations.ZeroMemory(digest);
            }
        }

        private async Task HandleWindowResizeAsync(ClientWebSocket active, Guid sessionId, Guid senderId,
            Subscriber subscriber, ulong requestSequence, ResizePayload resize, CancellationToken token)
        {
            var response = new ResizeResponsePayload { RequestId = resize.RequestId };
            var admitted = resizeRequests.TryAdd(sessionId, 0);
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!admitted) response.ErrorCode = "RESIZE_BUSY";
                else if ((!resize.Restore && (resize.Columns < 2 || resize.Columns > 1000 || resize.Rows < 2 || resize.Rows > 1000)) ||
                    resize.CellWidthPx > 1024 || resize.CellHeightPx > 1024 || resize.LeaseId.Length != 16 ||
                    resize.ExpiresAtMs <= now || resize.ExpiresAtMs > now + 15000)
                    response.ErrorCode = "RESIZE_EXPIRED_OR_INVALID";
                else if (!await LeaseMatchesAsync(sessionId, senderId, GuidFromBytes(resize.LeaseId.Span), resize.LeaseVersion, token).ConfigureAwait(false))
                    response.ErrorCode = "CONTROL_LEASE_REJECTED";
                else
                {
                    var result = await registry.RequestResizeAsync(sessionId, new LocalResizeRequest {
                        Id = GuidFromBytes(resize.RequestId.Span), LocalInputEpoch = resize.ObservedLocalInputEpoch,
                        ExpiresAtMs = resize.ExpiresAtMs, Restore = resize.Restore,
                        Columns = resize.Restore ? (short)0 : (short)resize.Columns,
                        Rows = resize.Restore ? (short)0 : (short)resize.Rows
                    }).ConfigureAwait(false);
                    response.Columns = (uint)Math.Max(0, (int)result.Columns);
                    response.Rows = (uint)Math.Max(0, (int)result.Rows);
                    response.ErrorCode = result.Error == LocalResizeError.OK ? string.Empty : result.Error.ToString();
                }
                var bytes = response.ToByteArray();
                try { await SendSessionControlEnvelopeAsync(active, sessionId, subscriber, requestSequence,
                    MessageType.ResizeResponse, bytes, token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Connection loss leaves an explicitly unknown result at the caller. */ }
            finally { if (admitted) resizeRequests.TryRemove(sessionId, out _); }
        }

        private async Task SendSessionTerminateResponseAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ulong requestSequence,
            Guid requestId,
            bool accepted,
            string errorCode,
            CancellationToken token)
        {
            var response = new SessionTerminateResponsePayload
            {
                RequestId = ByteString.CopyFrom(GuidBytes(requestId)),
                Accepted = accepted,
                ErrorCode = errorCode ?? string.Empty
            };
            var plaintext = response.ToByteArray();
            try
            {
                await SendSessionControlEnvelopeAsync(
                    active,
                    sessionId,
                    subscriber,
                    requestSequence,
                    MessageType.SessionTerminateResponse,
                    plaintext,
                    token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private async Task SendSessionControlEnvelopeAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ulong requestSequence,
            MessageType messageType,
            byte[] plaintext,
            CancellationToken token)
        {
            long keyEpoch;
            var pairwiseKey = SnapshotPairwiseKey(subscriber, out keyEpoch);
            if (pairwiseKey == null) return;
            var keyId = EpochKeyId(keyEpoch);
            var key = SessionCrypto.DeriveSessionKey(
                pairwiseKey,
                sessionId,
                CryptoDirection.PcToAndroid,
                keyId);
            var nonce = NewEnvelopeNonce();
            CryptographicOperations.ZeroMemory(pairwiseKey);
            try
            {
                var envelope = new Envelope
                {
                    ProtocolVersion = 1,
                    MessageType = messageType,
                    SessionId = ByteString.CopyFrom(GuidBytes(sessionId)),
                    SenderDeviceId = ByteString.CopyFrom(GuidBytes(credentials.DeviceId)),
                    RecipientDeviceId = ByteString.CopyFrom(GuidBytes(subscriber.DeviceId)),
                    Direction = Direction.PcToAndroid,
                    Seq = requestSequence,
                    SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    KeyId = ByteString.CopyFrom(keyId),
                    Nonce = ByteString.CopyFrom(nonce)
                };
                var ciphertext = SessionCrypto.Encrypt(
                    key,
                    nonce,
                    SessionCrypto.AssociatedData(envelope),
                    plaintext);
                try { envelope.Ciphertext = ByteString.CopyFrom(ciphertext); }
                finally { CryptographicOperations.ZeroMemory(ciphertext); }
                var serialized = envelope.ToByteArray();
                try
                {
                    ValidateRealtimeEnvelopeSize(serialized.Length);
                    await SendWebSocketAsync(
                        active,
                        serialized,
                        WebSocketMessageType.Binary,
                        true,
                        token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(serialized); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyId);
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(nonce);
            }
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
            lock (subscriberAuthoritySync)
            {
                EnsureCurrentSubscriberAuthorityLocked(candidate);
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
        }

        private Subscriber RememberConnectionRoute(Guid sessionId, Subscriber candidate)
        {
            lock (subscriberAuthoritySync)
            {
                EnsureCurrentSubscriberAuthorityLocked(candidate);
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
        }

        private void EnsureCurrentSubscriberAuthorityLocked(Subscriber candidate)
        {
            if (candidate == null ||
                Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                candidate.KeyEpoch != Volatile.Read(ref directAuthorityKeyEpoch) ||
                !authorizedRecipients.TryGetValue(candidate.DeviceId, out var authorized) ||
                !SubscriberRouteAuthorityMatches(authorized, candidate))
            {
                RetireSubscriber(candidate);
                throw new InvalidDataException(
                    "Subscriber authority changed while the session route was being installed.");
            }
        }

        private static bool SubscriberRouteAuthorityMatches(
            Subscriber authorized,
            Subscriber route)
        {
            if (authorized == null || route == null) return false;
            lock (authorized.Sync)
            lock (route.Sync)
            {
                return !authorized.Removed && !route.Removed &&
                    authorized.DeviceId == route.DeviceId &&
                    authorized.KeyEpoch == route.KeyEpoch &&
                    authorized.ExchangePublicKey?.Length == 32 &&
                    route.ExchangePublicKey?.Length == 32 &&
                    authorized.PairwiseKey?.Length == 32 &&
                    route.PairwiseKey?.Length == 32 &&
                    CryptographicOperations.FixedTimeEquals(
                        authorized.ExchangePublicKey,
                        route.ExchangePublicKey) &&
                    CryptographicOperations.FixedTimeEquals(
                        authorized.PairwiseKey,
                        route.PairwiseKey);
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
            var authorizationKey = DirectInputAuthorizationKey(sessionId, senderId);
            if (inputLeaseAuthorizations.TryGetValue(authorizationKey, out var cached) &&
                cached.AuthorizedSenderDeviceId == senderId &&
                cached.LeaseId == leaseId &&
                cached.Version == leaseVersion &&
                cached.PolicyVersion > 0 &&
                IsDirectInputSenderAllowed(
                    cached.ControlMode,
                    cached.HolderDeviceId,
                    senderId) &&
                cached.ExpiresAtUtc > now &&
                cached.ValidatedAtUtc >= now.AddSeconds(-1))
            {
                return true;
            }
            try
            {
                using var response = await SendAsync(
                    HttpMethod.Get,
                    "/v1/sessions/" + sessionId.ToString("D") + "/lease?sender_device_id=" +
                        senderId.ToString("D"),
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
                    !lease.TryGetProperty("authorized_sender_device_id", out var authorizedSender) ||
                    !lease.TryGetProperty("control_mode", out var controlMode) ||
                    !lease.TryGetProperty("policy_version", out var policyVersion) ||
                    !holder.TryGetGuid(out var parsedHolder) ||
                    !id.TryGetGuid(out var parsedLease) ||
                    !version.TryGetInt64(out var parsedVersion) ||
                    !expires.TryGetDateTime(out var parsedExpires) ||
                    !authorizedSender.TryGetGuid(out var parsedAuthorizedSender) ||
                    parsedAuthorizedSender != senderId ||
                    controlMode.ValueKind != JsonValueKind.String ||
                    !policyVersion.TryGetInt64(out var parsedPolicyVersion) ||
                    parsedPolicyVersion < 1)
                {
                    return false;
                }
                var parsedControlMode = controlMode.GetString();
                if (!IsDirectInputSenderAllowed(
                        parsedControlMode,
                        parsedHolder,
                        senderId)) return false;
                var validatedAt = DateTime.UtcNow;
                var status = new LeaseStatus
                {
                    HolderDeviceId = parsedHolder,
                    AuthorizedSenderDeviceId = parsedAuthorizedSender,
                    LeaseId = parsedLease,
                    Version = parsedVersion,
                    PolicyVersion = parsedPolicyVersion,
                    ControlMode = parsedControlMode,
                    ExpiresAtUtc = parsedExpires.ToUniversalTime(),
                    ValidatedAtUtc = validatedAt
                };
                leaseStatuses[sessionId] = status;
                inputLeaseAuthorizations[authorizationKey] = status;
                return status.ExpiresAtUtc > validatedAt &&
                    parsedHolder != Guid.Empty &&
                    parsedLease == leaseId &&
                    parsedVersion == leaseVersion;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return false; }
            catch (JsonException) { return false; }
        }

        private static bool IsDirectInputSenderAllowed(
            string controlMode,
            Guid holderDeviceId,
            Guid senderDeviceId)
        {
            if (holderDeviceId == Guid.Empty || senderDeviceId == Guid.Empty) return false;
            if (string.Equals(controlMode, "COLLABORATIVE", StringComparison.Ordinal)) return true;
            return (string.Equals(controlMode, "SINGLE", StringComparison.Ordinal) ||
                    string.Equals(controlMode, "TAKEOVER", StringComparison.Ordinal)) &&
                holderDeviceId == senderDeviceId;
        }

        private static string DirectInputAuthorizationKey(Guid sessionId, Guid senderDeviceId) =>
            sessionId.ToString("N") + ":" + senderDeviceId.ToString("N");

        private void ClearDirectInputAuthorizations(Guid sessionId)
        {
            if (sessionId == Guid.Empty) return;
            var prefix = sessionId.ToString("N") + ":";
            foreach (var item in inputLeaseAuthorizations)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                    inputLeaseAuthorizations.TryRemove(item.Key, out _);
            }
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
            if (senderId == Guid.Empty)
            {
                return false;
            }
            lock (subscriberAuthoritySync)
            {
                if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                    !authorizedRecipients.TryGetValue(senderId, out var authorized))
                    return false;
                lock (authorized.Sync)
                {
                    return !authorized.Removed &&
                        authorized.DeviceId == senderId &&
                        authorized.KeyEpoch == Volatile.Read(ref directAuthorityKeyEpoch) &&
                        authorized.ExchangePublicKey != null &&
                        authorized.ExchangePublicKey.Length == 32 &&
                        authorized.PairwiseKey != null &&
                        authorized.PairwiseKey.Length == 32;
                }
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
                if (IsCurrentSubscriberRoute(existing)) return existing;
                RemoveSubscriber(sessionId, deviceId, existing);
            }
            var routeKey = SubscriberRouteKey(sessionId, deviceId);
            if (connectionSubscriberRoutes.TryGetValue(routeKey, out var routed))
            {
                if (IsCurrentSubscriberRoute(routed)) return routed;
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

        private bool IsCurrentSubscriberRoute(Subscriber subscriber)
        {
            if (subscriber == null) return false;
            lock (subscriberAuthoritySync)
            {
                if (Volatile.Read(ref directAuthorityTransitioning) != 0 ||
                    !authorizedRecipients.TryGetValue(
                        subscriber.DeviceId, out var authorized))
                    return false;
                return subscriber.KeyEpoch == Volatile.Read(ref directAuthorityKeyEpoch) &&
                    SubscriberRouteAuthorityMatches(authorized, subscriber);
            }
        }

        private async Task<FileTransferPeerKeyMaterial> ResolveFileTransferPeerAsync(
            Guid requesterDeviceId,
            CancellationToken token)
        {
            if (requesterDeviceId == Guid.Empty)
                throw new FileOperationRemoteException(
                    "File-transfer requester is invalid / 文件传输请求设备无效。",
                    false);
            await RefreshAuthorizedRecipientsAsync(false, token).ConfigureAwait(false);
            if (!authorizedRecipients.TryGetValue(requesterDeviceId, out var authorized))
            {
                await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
                if (!authorizedRecipients.TryGetValue(requesterDeviceId, out authorized))
                {
                    throw new FileOperationRemoteException(
                        "File-transfer requester is no longer authorized / 文件传输请求设备已无权限。",
                        false);
                }
            }
            lock (authorized.Sync)
            {
                if (authorized.Removed || authorized.PairwiseKey == null ||
                    authorized.PairwiseKey.Length != 32 || authorized.KeyEpoch < 1)
                {
                    throw new FileOperationRemoteException(
                        "File-transfer encryption key is unavailable / 文件传输加密密钥不可用。",
                        false);
                }
                return new FileTransferPeerKeyMaterial(
                    requesterDeviceId,
                    authorized.KeyEpoch,
                    authorized.PairwiseKey);
            }
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
            // Stage the exact checkpoint before the recipient lookup or object
            // upload performs network IO.  REST metadata is published on a
            // separate worker and must not run unboundedly ahead while output
            // continues at high frequency.
            ObserveTerminalSnapshotProduction(sessionId, production);
            if (HostHistoryDataPlaneEnabled)
            {
                // WAL/checkpoint/outbox are already durable on the PC. Android
                // requests recipient-bound objects over the active E2EE route;
                // no terminal bytes or ciphertext are uploaded to object storage.
                return production != null || (events != null && events.Count > 0);
            }
            if (historyUploader == null) return false;
            // History objects must never be produced for a stale or revoked
            // recipient. Fetch a fresh PC-scoped authorization snapshot for
            // every archival attempt; in automatic account mode this includes
            // trusted same-account Android devices without a separate grant.
            // The response carries the authoritative key epoch in the same
            // database snapshot.
            await RefreshAuthorizedRecipientsAsync(true, token).ConfigureAwait(false);
            if (ShouldYieldHistoryUpload(sessionId)) return false;
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
                    if (ShouldYieldHistoryUpload(sessionId)) return false;
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
                        if (ShouldYieldHistoryUpload(sessionId)) return false;
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
                    if (ShouldYieldHistoryUpload(sessionId)) return false;
                    if (terminalOutbox != null && !HasUploadedTerminalSnapshot(
                        terminalOutbox,
                        recipient.DeviceId,
                        recipient.KeyEpoch))
                    {
                        durable = false;
                        continue;
                    }
                    else if (terminalOutbox == null && terminalSnapshotEnabled &&
                        (production == null || !production.PermanentlyIneligible))
                    {
                        // Snapshot mode starts before the first Output frame.
                        // Keep the generic CHUNK path behind that pending
                        // anchor as well as behind an eligible anchor whose
                        // outbox was not durably materialized. Only a proven
                        // permanent incompatibility may use CHUNK fallback.
                        durable = false;
                        continue;
                    }

                    var outcome = await historyUploader.UploadAsync(
                        sessionId,
                        recipient,
                        events,
                        token,
                        () => pendingHistorySessions.ContainsKey(sessionId) ||
                            ShouldYieldHistoryUpload(sessionId)).ConfigureAwait(false);
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
                            if (ShouldYieldHistoryUpload(sessionId)) return false;
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
                    Volatile.Read(ref directAuthorityKeyEpoch),
                    out var keyEpoch);
                var candidates = new Dictionary<Guid, Subscriber>();
                var transferred = new HashSet<Guid>();
                try
                {
                    foreach (var item in authorizedKeys)
                    {
                        candidates.Add(item.Key, CreateAuthorizedSubscriberWithSigningKey(
                            credentials.Identity.Exchange,
                            credentials.DeviceId,
                            item.Key,
                            item.Value.ExchangePublicKey,
                            item.Value.SigningPublicKey,
                            keyEpoch));
                    }

                    var epochAdvanced = keyEpoch >
                        Volatile.Read(ref directAuthorityKeyEpoch);
                    var changedPeers = new HashSet<Guid>();
                    lock (subscriberAuthoritySync)
                    {
                        foreach (var item in candidates)
                        {
                            if (!authorizedRecipients.TryGetValue(
                                    item.Key, out var existing) ||
                                !SubscriberAuthorityMatches(existing, item.Value))
                                changedPeers.Add(item.Key);
                        }
                        foreach (var existing in authorizedRecipients.Keys)
                            if (!candidates.ContainsKey(existing)) changedPeers.Add(existing);
                    }
                    if (epochAdvanced)
                        changedPeers.UnionWith(SnapshotAllDirectAuthorityPeerIds());

                    var transitioning = epochAdvanced || changedPeers.Count > 0;
                    if (transitioning) BeginDirectAuthorityTransition();
                    try
                    {
                        if (transitioning)
                        {
                            await RetireDirectAuthorityPeersAsync(
                                changedPeers,
                                epochAdvanced
                                    ? "KEY_EPOCH_ADVANCED"
                                    : "PEER_AUTHORITY_REPLACED").ConfigureAwait(false);
                        }

                        if (epochAdvanced)
                        {
                            var previousEpoch = config.KeyEpoch;
                            config.KeyEpoch = keyEpoch;
                            try { ProductionAgentConfig.Save(config); }
                            catch
                            {
                                config.KeyEpoch = previousEpoch;
                                throw;
                            }
                            TryRetainIdentity(config, credentials);
                        }

                        lock (subscriberAuthoritySync)
                        {
                            if (epochAdvanced)
                            {
                                foreach (var peer in changedPeers)
                                    relayExactOutbox.PrunePeer(peer);
                                ClearSubscribers();
                                foreach (var previous in authorizedRecipients.Values)
                                    RetireSubscriber(previous);
                                authorizedRecipients.Clear();
                                Volatile.Write(ref directAuthorityKeyEpoch, keyEpoch);
                                historyAuthorityChanged = true;
                            }

                            foreach (var item in candidates)
                            {
                                if (authorizedRecipients.TryGetValue(
                                        item.Key, out var existing) &&
                                    SubscriberAuthorityMatches(existing, item.Value))
                                    continue;
                                ClearSubscriberRoutesForDevice(item.Key);
                                authorizedRecipients[item.Key] = item.Value;
                                transferred.Add(item.Key);
                                historyAuthorityChanged = true;
                                if (existing != null) RetireSubscriber(existing);
                            }
                            foreach (var item in authorizedRecipients.ToArray())
                            {
                                if (candidates.ContainsKey(item.Key)) continue;
                                if (authorizedRecipients.TryRemove(item.Key, out var removed))
                                {
                                    ClearSubscriberRoutesForDevice(item.Key);
                                    RetireSubscriber(removed);
                                    historyAuthorityChanged = true;
                                }
                            }
                            authorityExpiresUtc = DateTime.UtcNow.Add(AuthorityCacheDuration);
                        }
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
                        if (epochAdvanced)
                            directAcceptancePeerSnapshots.Clear();
                        else
                            foreach (var peer in changedPeers)
                                directAcceptancePeerSnapshots.TryRemove(peer, out _);
#endif
                    }
                    finally
                    {
                        if (transitioning) EndDirectAuthorityTransition();
                    }
                }
                finally
                {
                    foreach (var candidate in candidates)
                    {
                        if (!transferred.Contains(candidate.Key))
                            RetireSubscriber(candidate.Value);
                    }
                    foreach (var keyMaterial in authorizedKeys.Values)
                        keyMaterial.Dispose();
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

        private static Dictionary<Guid, AuthorizedRecipientKeyMaterial> ParseAuthorizedRecipientKeys(
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
            var authorizedKeys = new Dictionary<Guid, AuthorizedRecipientKeyMaterial>();
            try
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!string.Equals(item.GetProperty("device_type").GetString(), "ANDROID", StringComparison.Ordinal)) continue;
                    var deviceId = item.GetProperty("device_id").GetGuid();
                    byte[] exchangePublicKey = null;
                    byte[] signingPublicKey = null;
                    try
                    {
                        exchangePublicKey = Convert.FromBase64String(
                            item.GetProperty("exchange_public_key").GetString());
                        signingPublicKey = Convert.FromBase64String(
                            item.GetProperty("signing_public_key").GetString());
                    }
                    catch (Exception error) when (
                        error is FormatException or ArgumentNullException or
                            KeyNotFoundException or InvalidOperationException)
                    {
                        if (exchangePublicKey != null)
                            CryptographicOperations.ZeroMemory(exchangePublicKey);
                        if (signingPublicKey != null)
                            CryptographicOperations.ZeroMemory(signingPublicKey);
                        throw new InvalidDataException("PC recipient authorization keys are invalid.", error);
                    }
                    if (exchangePublicKey.Length != 32 || signingPublicKey.Length != 32 ||
                        deviceId == Guid.Empty)
                    {
                        CryptographicOperations.ZeroMemory(exchangePublicKey);
                        CryptographicOperations.ZeroMemory(signingPublicKey);
                        throw new InvalidDataException("PC recipient authorization keys are invalid.");
                    }
                    var keyMaterial = new AuthorizedRecipientKeyMaterial(
                        exchangePublicKey,
                        signingPublicKey);
                    if (!authorizedKeys.TryAdd(deviceId, keyMaterial))
                    {
                        keyMaterial.Dispose();
                        throw new InvalidDataException("PC recipient snapshot contains a duplicate device.");
                    }
                }
                return authorizedKeys;
            }
            catch
            {
                foreach (var keyMaterial in authorizedKeys.Values) keyMaterial.Dispose();
                throw;
            }
        }

        private Task<IReadOnlyList<ProductionDirectPeerAuthorization>>
            SnapshotDirectPeerAuthorizationsAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<IReadOnlyList<ProductionDirectPeerAuthorization>>(token);
            var result = new List<ProductionDirectPeerAuthorization>();
            try
            {
                lock (subscriberAuthoritySync)
                {
                    if (Volatile.Read(ref directAuthorityTransitioning) != 0)
                        return Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(
                            result.AsReadOnly());
                    var authorityEpoch = Volatile.Read(ref directAuthorityKeyEpoch);
                    foreach (var item in authorizedRecipients.OrderBy(value => value.Key))
                    {
                        lock (item.Value.Sync)
                        {
                            if (item.Value.Removed || item.Value.PairwiseKey?.Length != 32 ||
                                item.Value.SigningPublicKey?.Length != 32 ||
                                item.Value.KeyEpoch != authorityEpoch)
                            {
                                continue;
                            }
                            result.Add(new ProductionDirectPeerAuthorization(
                                item.Value.DeviceId,
                                item.Value.PairwiseKey,
                                item.Value.SigningPublicKey,
                                item.Value.KeyEpoch));
                        }
                    }
                }
                return Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(
                    result.AsReadOnly());
            }
            catch
            {
                foreach (var value in result) value.Dispose();
                throw;
            }
        }

        internal static string DeviceKeyId(Guid deviceId)
        {
            if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
            return "device-key-" + deviceId.ToString("D");
        }

        private static Subscriber CreateAuthorizedSubscriber(
            DeviceExchangeKeyPair localExchange,
            Guid pcDeviceId,
            Guid recipientDeviceId,
            byte[] exchangePublicKey,
            long keyEpoch)
        {
            return CreateAuthorizedSubscriberWithSigningKey(
                localExchange,
                pcDeviceId,
                recipientDeviceId,
                exchangePublicKey,
                signingPublicKey: null,
                keyEpoch);
        }

        private static Subscriber CreateAuthorizedSubscriberWithSigningKey(
            DeviceExchangeKeyPair localExchange,
            Guid pcDeviceId,
            Guid recipientDeviceId,
            byte[] exchangePublicKey,
            byte[] signingPublicKey,
            long keyEpoch)
        {
            if (localExchange == null) throw new ArgumentNullException(nameof(localExchange));
            if (pcDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty || pcDeviceId == recipientDeviceId)
                throw new InvalidDataException("PC recipient identity is invalid.");
            if (exchangePublicKey == null || exchangePublicKey.Length != 32 ||
                (signingPublicKey != null && signingPublicKey.Length != 32) || keyEpoch < 1)
                throw new InvalidDataException("PC recipient authorization key is invalid.");

            var retainedPublicKey = (byte[])exchangePublicKey.Clone();
            var retainedSigningPublicKey = signingPublicKey == null
                ? null
                : (byte[])signingPublicKey.Clone();
            try
            {
                return new Subscriber
                {
                    DeviceId = recipientDeviceId,
                    ExchangePublicKey = retainedPublicKey,
                    SigningPublicKey = retainedSigningPublicKey,
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
                if (retainedSigningPublicKey != null)
                    CryptographicOperations.ZeroMemory(retainedSigningPublicKey);
                throw;
            }
        }

        private async Task ReplaySubscriberAsync(
            ClientWebSocket active,
            Guid sessionId,
            Subscriber subscriber,
            ulong resumeFromSequence,
            bool beginSubscribeGeneration,
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
                if (!TryPrepareSubscriberReplay(
                    subscriber,
                    resumeFromSequence,
                    beginSubscribeGeneration,
                    out maximumEvents,
                    out maximumStoredBytes))
                {
                    throw new InvalidDataException(
                        beginSubscribeGeneration
                            ? "Subscribe cursor regressed below the durable acknowledgement."
                            : "Subscriber replay cursor does not match the current acknowledgement.");
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
            return GetSubscriberFrameOrState(
                subscriber,
                sequence,
                reserveForCurrentGeneration: false,
                out alreadyDelivered,
                out canEncrypt);
        }

        private static byte[] PrepareCachedSubscriberFrameForSend(
            Subscriber subscriber,
            ulong sequence,
            out bool alreadyDelivered,
            out bool canEncrypt)
        {
            return GetSubscriberFrameOrState(
                subscriber,
                sequence,
                reserveForCurrentGeneration: true,
                out alreadyDelivered,
                out canEncrypt);
        }

        private static byte[] GetSubscriberFrameOrState(
            Subscriber subscriber,
            ulong sequence,
            bool reserveForCurrentGeneration,
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
                if (!reserveForCurrentGeneration &&
                    subscriber.SentFrames.TryGetValue(sequence, out var inspected))
                {
                    return (byte[])inspected.Clone();
                }
                if (subscriber.ReplayInProgress || subscriber.FlowPaused)
                {
                    subscriber.DrainRequested = true;
                    alreadyDelivered = false;
                    return null;
                }
                if (subscriber.SentFrames.TryGetValue(sequence, out var existing))
                {
                    if (!TryReserveSubscriberGenerationFrameLocked(
                        subscriber,
                        sequence,
                        checked((ulong)existing.Length)))
                    {
                        subscriber.DrainRequested = true;
                        return null;
                    }
                    return (byte[])existing.Clone();
                }
                if (!SubscriberGenerationHasCapacityLocked(subscriber, 0))
                {
                    // A new frame that would exceed the current generation's
                    // absolute window remains in the durable Registry buffer.
                    subscriber.DrainRequested = true;
                    alreadyDelivered = false;
                    return null;
                }
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
                if (subscriber.FlowPaused)
                {
                    subscriber.DrainRequested = true;
                    return null;
                }
                if (subscriber.SentFrames.TryGetValue(sequence, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing, serialized))
                        throw new InvalidDataException("same sequence produced different ciphertext");
                    if (!TryReserveSubscriberGenerationFrameLocked(
                        subscriber,
                        sequence,
                        checked((ulong)existing.Length)))
                    {
                        subscriber.DrainRequested = true;
                        return null;
                    }
                    return (byte[])existing.Clone();
                }
                if (!TryReserveSubscriberGenerationFrameLocked(
                    subscriber,
                    sequence,
                    checked((ulong)serialized.Length)))
                {
                    subscriber.DrainRequested = true;
                    return null;
                }
                var retained = (byte[])serialized.Clone();
                subscriber.SentFrames.Add(sequence, retained);
                return (byte[])serialized.Clone();
            }
        }

        private static bool SubscriberGenerationHasCapacityLocked(
            Subscriber subscriber,
            ulong additionalWireBytes)
        {
            if (subscriber.GenerationInFlightSequences.Count >= subscriber.FlowWindowEvents ||
                subscriber.InFlightWireBytes >= subscriber.FlowWindowWireBytes)
            {
                return false;
            }
            return additionalWireBytes <=
                subscriber.FlowWindowWireBytes - subscriber.InFlightWireBytes;
        }

        private static bool TryReserveSubscriberGenerationFrameLocked(
            Subscriber subscriber,
            ulong sequence,
            ulong wireBytes)
        {
            if (subscriber.GenerationInFlightSequences.Contains(sequence)) return true;
            if (!SubscriberGenerationHasCapacityLocked(subscriber, wireBytes)) return false;
            subscriber.GenerationInFlightSequences.Add(sequence);
            subscriber.InFlightWireBytes = checked(subscriber.InFlightWireBytes + wireBytes);
            return true;
        }

        private static void PruneSubscriberFramesLocked(Subscriber subscriber)
        {
            while (subscriber.SentFrames.Count > 0)
            {
                var first = subscriber.SentFrames.First();
                if (first.Key > subscriber.HighestAck) break;
                subscriber.SentFrames.Remove(first.Key);
                if (subscriber.GenerationInFlightSequences.Remove(first.Key))
                {
                    subscriber.InFlightWireBytes = subscriber.InFlightWireBytes >= (ulong)first.Value.Length
                        ? subscriber.InFlightWireBytes - (ulong)first.Value.Length
                        : 0;
                }
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
            var cached = PrepareCachedSubscriberFrameForSend(
                subscriber,
                sequence,
                out var alreadyDelivered,
                out var canEncrypt);
            if (alreadyDelivered) return true;
            if (!canEncrypt && cached == null) return false;
            if (cached != null)
            {
                try
                {
                    var directEligible = ProductionDirectRoutePolicy.Evaluate(type, direction) ==
                        ProductionDirectRouteEligibility.DirectEligible;
                    if (!await ProductionAgentExactEnvelopeRouter.RouteAsync(
                            directDataRuntime,
                            relayExactOutbox,
                            subscriber.DeviceId,
                            sessionId,
                            sequence,
                            cached,
                            directEligible,
                            ObserveDirectDataPeerFault,
                            token).ConfigureAwait(false))
                        return false;
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
                        var directEligible = ProductionDirectRoutePolicy.Evaluate(type, direction) ==
                            ProductionDirectRouteEligibility.DirectEligible;
                        if (!await ProductionAgentExactEnvelopeRouter.RouteAsync(
                                directDataRuntime,
                                relayExactOutbox,
                                subscriber.DeviceId,
                                sessionId,
                                sequence,
                                sendBytes,
                                directEligible,
                                ObserveDirectDataPeerFault,
                                token).ConfigureAwait(false))
                            return false;
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
                throw new ProductionPhysicalRealtimeException(
                    "Production Relay send timed out.",
                    new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (
                error is not ProductionPhysicalRealtimeException &&
                error is not OutOfMemoryException &&
                error is not StackOverflowException &&
                error is not AccessViolationException)
            {
                throw new ProductionPhysicalRealtimeException(
                    "Production Relay send failed.",
                    error);
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

        internal static HistoryEventV1 BuildHistoryEvent(LocalFrame frame)
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
                    CryptographicOperations.FixedTimeEquals(
                        left.ExchangePublicKey,
                        right.ExchangePublicKey) &&
                    ((left.SigningPublicKey == null && right.SigningPublicKey == null) ||
                        (left.SigningPublicKey != null && right.SigningPublicKey != null &&
                            CryptographicOperations.FixedTimeEquals(
                                left.SigningPublicKey,
                                right.SigningPublicKey)));
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
                return ResetSubscriberCursorLocked(subscriber, resumeFromSequence);
            }
        }

        private static bool ResetSubscriberCursorLocked(
            Subscriber subscriber,
            ulong resumeFromSequence)
        {
            if (subscriber.Removed || resumeFromSequence < subscriber.HighestAck) return false;
            subscriber.HighestAck = resumeFromSequence;
            PruneSubscriberFramesLocked(subscriber);
            if (subscriber.LastSentSequence < resumeFromSequence)
                subscriber.LastSentSequence = resumeFromSequence;
            // FLOW_CONTROL epochs are scoped to one authenticated SUBSCRIBE
            // generation. Retain byte-identical ciphertext until ACK, but reset
            // the old generation's delivery budget and stop sends until a fresh
            // FLOW_CONTROL explicitly opens a window.
            subscriber.GenerationInFlightSequences.Clear();
            subscriber.InFlightWireBytes = 0;
            subscriber.FlowPaused = true;
            subscriber.FlowWindowEvents = DefaultFlowWindowEvents;
            subscriber.FlowWindowWireBytes = DefaultFlowWindowWireBytes;
            subscriber.FlowControlEpoch = 0;
            subscriber.LastActivityUtc = DateTime.UtcNow;
            return true;
        }

        private static bool TryPrepareSubscriberReplay(
            Subscriber subscriber,
            ulong resumeFromSequence,
            bool beginSubscribeGeneration,
            out int maximumEvents,
            out long maximumStoredBytes)
        {
            maximumEvents = 0;
            maximumStoredBytes = 0;
            if (subscriber == null) return false;
            lock (subscriber.Sync)
            {
                if (beginSubscribeGeneration)
                {
                    if (!ResetSubscriberCursorLocked(subscriber, resumeFromSequence)) return false;
                }
                else if (subscriber.Removed || resumeFromSequence != subscriber.HighestAck)
                {
                    return false;
                }
                subscriber.ReplayInProgress = true;
                var remainingEvents = subscriber.FlowWindowEvents -
                    subscriber.GenerationInFlightSequences.Count;
                var remainingBytes = subscriber.FlowWindowWireBytes > subscriber.InFlightWireBytes
                    ? subscriber.FlowWindowWireBytes - subscriber.InFlightWireBytes
                    : 0;
                maximumEvents = Math.Max(1, remainingEvents);
                maximumStoredBytes = checked((long)Math.Max(1UL, remainingBytes));
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
                    subscriber.GenerationInFlightSequences.Count >= subscriber.FlowWindowEvents ||
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
            CancelHostHistoryRequests(sessionId, deviceId);
            DeregisterDirectSession(deviceId, sessionId);
            if (sessionSubscribers.IsEmpty)
            {
                ((ICollection<KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>>)subscribers).Remove(
                    new KeyValuePair<Guid, ConcurrentDictionary<Guid, Subscriber>>(sessionId, sessionSubscribers));
            }
            return true;
        }

        private void CancelHostHistoryRequests(Guid sessionId, Guid deviceId)
        {
            foreach (var item in hostHistoryRequests)
            {
                if (item.Value.SessionId != sessionId ||
                    (deviceId != Guid.Empty && item.Value.DeviceId != deviceId)) continue;
                item.Value.Cancel();
            }
        }

        private void CancelAllHostHistoryRequests()
        {
            foreach (var request in hostHistoryRequests.Values) request.Cancel();
        }

        private void WaitForHostHistoryRequests()
        {
            while (!hostHistoryRequests.IsEmpty)
            {
                var requests = hostHistoryRequests.Values.ToArray();
                foreach (var request in requests) request.Cancel();
                try { Task.WhenAll(requests.Select(value => value.Completion)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void WaitForSessionHistoryDeletions()
        {
            while (!historyDeletionTasks.IsEmpty)
            {
                var deletions = historyDeletionTasks.Values.ToArray();
                try { Task.WhenAll(deletions).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void HandleLocatedSessionFault(
            Guid sessionId,
            Guid deviceId,
            Subscriber expected)
        {
            connectionSupervisor.ObserveSessionSoftFailure();
            RetireLocatedSessionRoute(sessionId, deviceId, expected);
        }

        private void RetireLocatedSessionRoute(
            Guid sessionId,
            Guid deviceId,
            Subscriber expected)
        {
            var routeKey = SubscriberRouteKey(sessionId, deviceId);
            var target = expected;
            if (target == null && connectionSubscriberRoutes.TryGetValue(routeKey, out var routed))
                target = routed;
            if (target == null && subscribers.TryGetValue(sessionId, out var sessionSubscribers))
                sessionSubscribers.TryGetValue(deviceId, out target);
            if (target == null) return;

            // Retire only the active subscription. The authorized route owns
            // byte-identical ciphertext until a durable ACK; removing and
            // rebuilding it here would encrypt a different nonce for the same
            // (peer, session, seq), which the Relay outbox must fail closed.
            DetachSubscriber(sessionId, deviceId, target);
            lock (target.Sync)
            {
                if (target.Removed) return;
                target.GenerationInFlightSequences.Clear();
                target.InFlightWireBytes = 0;
                target.FlowPaused = true;
                target.FlowWindowEvents = DefaultFlowWindowEvents;
                target.FlowWindowWireBytes = DefaultFlowWindowWireBytes;
                target.FlowControlEpoch = 0;
                target.ReplayInProgress = false;
                target.DrainRequested = true;
            }
        }

        private void HandleSessionPublicationFault(Guid sessionId, PendingSession expected)
        {
            connectionSupervisor.ObserveSessionSoftFailure();
            if (TryRemovePending(sessionId, expected)) RemovePublishedSequence(sessionId);
            RetireSessionRoutes(sessionId);
        }

        private void RetireSessionRoutes(Guid sessionId)
        {
            foreach (var peer in relayExactOutbox.Snapshot())
                relayExactOutbox.PruneSession(peer.PeerDeviceId, sessionId);
            var retained = new HashSet<Subscriber>();
            if (subscribers.TryRemove(sessionId, out var sessionSubscribers))
            {
                foreach (var subscriber in sessionSubscribers.Values)
                    retained.Add(subscriber);
                sessionSubscribers.Clear();
            }
            var routePrefix = sessionId.ToString("N") + ":";
            foreach (var route in connectionSubscriberRoutes)
            {
                if (!route.Key.StartsWith(routePrefix, StringComparison.Ordinal) ||
                    !((ICollection<KeyValuePair<string, Subscriber>>)connectionSubscriberRoutes)
                        .Remove(route))
                    continue;
                retained.Add(route.Value);
            }
            foreach (var subscriber in retained)
            {
                DeregisterDirectSession(subscriber.DeviceId, sessionId);
                RetireSubscriber(subscriber);
            }
            var suffix = ":" + sessionId.ToString("N");
            foreach (var state in directIngressApplyStates)
                if (state.Key.EndsWith(suffix, StringComparison.Ordinal))
                    directIngressApplyStates.TryRemove(state.Key, out _);
        }

        private void ClearSubscriberRoutesForDevice(Guid deviceId)
        {
            relayExactOutbox.PrunePeer(deviceId);
            directPeerRoutes.Remove(deviceId);
            if (directDataRuntime != null)
                _ = directDataRuntime.RetirePeerAsync(deviceId, "PEER_AUTHORITY_RETIRED");
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
            byte[] signingPublicKey;
            byte[] pairwiseKey;
            List<byte[]> sentFrames;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed) return;
                subscriber.Removed = true;
                exchangePublicKey = subscriber.ExchangePublicKey;
                signingPublicKey = subscriber.SigningPublicKey;
                pairwiseKey = subscriber.PairwiseKey;
                subscriber.ExchangePublicKey = null;
                subscriber.SigningPublicKey = null;
                subscriber.PairwiseKey = null;
                sentFrames = subscriber.SentFrames.Values.ToList();
                subscriber.SentFrames.Clear();
                subscriber.GenerationInFlightSequences.Clear();
                subscriber.InFlightWireBytes = 0;
            }
            if (exchangePublicKey != null) CryptographicOperations.ZeroMemory(exchangePublicKey);
            if (signingPublicKey != null) CryptographicOperations.ZeroMemory(signingPublicKey);
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
            if (realtimeCapabilityState != null) realtimeCapabilityState.Clear();
            // Relay reconnect is not a durable ACK. Keep byte-identical
            // ciphertext and subscriber authority across physical socket
            // generations, while resetting only generation-scoped flow state.
            var retained = new HashSet<Subscriber>();
            foreach (var session in subscribers.Values)
            foreach (var subscriber in session.Values)
                retained.Add(subscriber);
            foreach (var subscriber in connectionSubscriberRoutes.Values)
                retained.Add(subscriber);
            foreach (var subscriber in retained)
            {
                lock (subscriber.Sync)
                {
                    if (subscriber.Removed) continue;
                    subscriber.GenerationInFlightSequences.Clear();
                    subscriber.InFlightWireBytes = 0;
                    subscriber.FlowPaused = true;
                    subscriber.FlowWindowEvents = DefaultFlowWindowEvents;
                    subscriber.FlowWindowWireBytes = DefaultFlowWindowWireBytes;
                    subscriber.FlowControlEpoch = 0;
                    subscriber.ReplayInProgress = false;
                    subscriber.DrainRequested = true;
                }
            }
            leaseStatuses.Clear();
            inputLeaseAuthorizations.Clear();
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
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || !ProductionServerOriginPolicy.IsAllowedOrigin(endpointUri))
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
                {
                    // Only a credential rejection is terminal; proxy/configuration errors may recover.
                    try
                    {
                        using var rejection = JsonDocument.Parse(
                            await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                        if (rejection.RootElement.ValueKind == JsonValueKind.Object &&
                            rejection.RootElement.TryGetProperty("error", out var reason) &&
                            reason.ValueKind == JsonValueKind.String &&
                            reason.GetString() is "invalid_grant" or "interaction_required" or
                                "login_required" or "account_setup_required" or "user_inactive")
                            throw ReauthenticationRequired();
                    }
                    catch (JsonException) { }
                }
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
            if (Interlocked.Exchange(ref reauthenticationRequired, 1) != 0) return;
            statusExpiresUtc = DateTime.MaxValue;
            SetRelayState(ProductionRelayState.Failed, "ACCOUNT_REAUTH_REQUIRED");
            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            try { active?.Abort(); }
            catch (ObjectDisposedException) { }
            // Signalling also owns a server presence entry. Retire every remote lane,
            // while leaving the local session registry and terminal processes intact.
            try { directPeerRoutes?.RetireAllDirect("ACCOUNT_REAUTH_REQUIRED"); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            BeginDirectRuntimeCleanup();
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

#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
        private ProductionDirectAcceptanceDiagnosticsSnapshot
            CaptureDirectAcceptanceDiagnostics(
                Guid? requestedSessionId,
                Guid? requestedPeerDeviceId)
        {
            if (!TryCaptureProcessIdentity(
                    Environment.ProcessId,
                    out var agentStartTime,
                    out var agentImageSha256))
            {
                return new ProductionDirectAcceptanceDiagnosticsSnapshot
                {
                    BlockedReason = "WINDOWS_DIAGNOSTICS_AGENT_IDENTITY_UNAVAILABLE"
                };
            }

            var baseline = new ProductionDirectAcceptanceDiagnosticsSnapshot
            {
                AgentPid = Environment.ProcessId,
                AgentStartTime = agentStartTime,
                ProcessImageSha256 = agentImageSha256,
                Carrier = ProductionSessionCarrier.Relay,
                FallbackReason = "NONE",
                RelayGeneration = realtimeCapabilityState.Snapshot().PhysicalGeneration,
                SignallingGeneration = directSignaling?.Snapshot.Generation ?? 0,
                QueueDepth = commonIngressQueue.Reader.CanCount
                    ? commonIngressQueue.Reader.Count
                    : null
            };
            // Probe proves only that the current-user read-only interface is
            // bound to this live Agent. It must not select an arbitrary session.
            if (!requestedSessionId.HasValue && !requestedPeerDeviceId.HasValue)
                return baseline;
            if (!requestedSessionId.HasValue || requestedSessionId == Guid.Empty ||
                !requestedPeerDeviceId.HasValue || requestedPeerDeviceId == Guid.Empty)
                return AcceptanceBlocked(
                    baseline,
                    "WINDOWS_DIAGNOSTICS_SELECTOR_INVALID");

            var sessionId = requestedSessionId.Value;
            var peerDeviceId = requestedPeerDeviceId.Value;
            var summaries = registry?.GetSummaries()
                .Where(value => value.SessionId == sessionId)
                .ToArray() ?? Array.Empty<SessionSummary>();
            if (summaries.Length != 1)
                return AcceptanceBlocked(
                    baseline,
                    summaries.Length == 0
                        ? "WINDOWS_DIAGNOSTICS_SESSION_NOT_FOUND"
                        : "WINDOWS_DIAGNOSTICS_SESSION_AMBIGUOUS");

            Subscriber subscriber;
            lock (subscriberAuthoritySync)
            {
                if (Volatile.Read(ref directAuthorityTransitioning) != 0)
                    return AcceptanceBlocked(
                        baseline,
                        "WINDOWS_DIAGNOSTICS_AUTHORITY_TRANSITION");
                if (!subscribers.TryGetValue(
                        sessionId, out var sessionSubscribers) ||
                    !sessionSubscribers.TryGetValue(peerDeviceId, out subscriber))
                    return AcceptanceBlocked(
                        baseline,
                        "WINDOWS_DIAGNOSTICS_VIEWER_NOT_FOUND");
                if (!IsCurrentSubscriberRoute(subscriber))
                    return AcceptanceBlocked(
                        baseline,
                        "WINDOWS_DIAGNOSTICS_VIEWER_IDENTITY_MISMATCH");
            }

            var session = summaries[0];
            if (session.HostProcessId <= 0 ||
                !TryCaptureProcessIdentity(
                    session.HostProcessId,
                    out var sessionHostStartTime,
                    out var sessionHostImageSha256))
                return AcceptanceBlocked(
                    baseline,
                    "WINDOWS_DIAGNOSTICS_SESSION_HOST_IDENTITY_UNAVAILABLE");

            ulong durableAck;
            lock (subscriber.Sync)
            {
                if (subscriber.Removed || subscriber.DeviceId != peerDeviceId)
                    return AcceptanceBlocked(
                        baseline,
                        "WINDOWS_DIAGNOSTICS_VIEWER_IDENTITY_MISMATCH");
                durableAck = subscriber.HighestAck;
            }

            var activePeers = directDataRuntime?.Snapshot() ??
                Array.Empty<ProductionDirectDataPeerSnapshot>();
            var activePeer = activePeers.SingleOrDefault(value =>
                value.PeerDeviceId == peerDeviceId &&
                value.KeyEpoch == Volatile.Read(ref directAuthorityKeyEpoch));
            var observedPeer = activePeer;
            if (observedPeer == null)
                directAcceptancePeerSnapshots.TryGetValue(peerDeviceId, out observedPeer);
            directIngressApplyStates.TryGetValue(
                DirectIngressApplyKey(peerDeviceId, sessionId),
                out var applyTracker);
            var outboxDepth = relayExactOutbox.Snapshot()
                .Where(value => value.PeerDeviceId == peerDeviceId)
                .Sum(value => (long)value.PendingFrames);
            var carrier = ResolveSessionCarrier(
                sessionId,
                new[] { peerDeviceId },
                activePeers);
            return new ProductionDirectAcceptanceDiagnosticsSnapshot
            {
                AgentPid = baseline.AgentPid,
                AgentStartTime = baseline.AgentStartTime,
                ProcessImageSha256 = baseline.ProcessImageSha256,
                Carrier = carrier,
                ContiguousAppliedSequence = applyTracker?.HighestContiguous ?? 0,
                DigestConflictCount = observedPeer?.IngressDigestConflictCount,
                DirectFrameReceivedCount = observedPeer?.DirectFramesReceived ?? 0,
                DirectFrameSentCount = observedPeer?.DirectFramesSent ?? 0,
                DirectReceiptCount = observedPeer?.TransportReceiptsReceived ?? 0,
                DuplicateApplyCount = applyTracker?.DuplicateApplyCount ?? 0,
                DuplicateDropCount = observedPeer?.IngressDuplicateDropCount,
                DurableAck = durableAck,
                FallbackReason = NormalizeAcceptanceReason(
                    observedPeer?.LastFallbackReasonCode),
                GapCount = observedPeer?.IngressGapCount,
                LastReceivedSequence = observedPeer?.LastReceivedEnvelopeFrameSequence ?? 0,
                OutboxDepth = outboxDepth,
                PathEpoch = observedPeer?.Path?.PathEpoch ?? 0,
                QueueDepth = baseline.QueueDepth,
                RelayGeneration = baseline.RelayGeneration,
                RelayReplayBytes = observedPeer?.RelayReplayBytes ?? 0,
                RelayReplayCount = observedPeer?.RelayReplayEnvelopeCount ?? 0,
                SessionHostPid = session.HostProcessId,
                SessionHostStartTime = sessionHostStartTime,
                SessionHostImageSha256 = sessionHostImageSha256,
                SignallingGeneration = baseline.SignallingGeneration,
                TransportReceiptHighWatermark =
                    observedPeer?.TransportReceiptHighWatermark ?? 0
            };
        }

        private static ProductionDirectAcceptanceDiagnosticsSnapshot AcceptanceBlocked(
            ProductionDirectAcceptanceDiagnosticsSnapshot baseline,
            string reasonCode) => new()
            {
                AgentPid = baseline.AgentPid,
                AgentStartTime = baseline.AgentStartTime,
                ProcessImageSha256 = baseline.ProcessImageSha256,
                Carrier = baseline.Carrier,
                FallbackReason = baseline.FallbackReason,
                QueueDepth = baseline.QueueDepth,
                RelayGeneration = baseline.RelayGeneration,
                SignallingGeneration = baseline.SignallingGeneration,
                BlockedReason = reasonCode
            };

        private static string NormalizeAcceptanceReason(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "NONE";
            value = value.Trim();
            return value.Length <= 96 && value.All(character =>
                    character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                ? value
                : "UNCLASSIFIED";
        }

        private static bool TryCaptureProcessIdentity(
            int processId,
            out string startTime,
            out string imageSha256)
        {
            startTime = null;
            imageSha256 = null;
            if (processId <= 0) return false;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited) return false;
                var imagePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    return false;
                var observedStart = process.StartTime.ToUniversalTime();
                using var image = new FileStream(
                    imagePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var digest = SHA256.HashData(image);
                try
                {
                    startTime = observedStart.ToString(
                        "O", CultureInfo.InvariantCulture);
                    imageSha256 = Convert.ToHexString(digest).ToLowerInvariant();
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(digest); }
            }
            catch (Exception error) when (
                error is InvalidOperationException or ArgumentException or
                    FileNotFoundException or IOException or
                    UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
#endif

        /// <summary>
        /// Immediately prevents this runtime from publishing any more account data.
        /// The potentially slow worker joins remain in Dispose so account switching
        /// can acknowledge the new local state without waiting on network teardown.
        /// </summary>
        internal void StopAccountSynchronization()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
            // Close the read-only acceptance surface before any account-owned
            // runtime state begins teardown.
            directAcceptanceDiagnostics?.Stop();
#endif
            postRegistrationWorkerStartGate?.Retire();
            var availabilityHandler = Interlocked.Exchange(ref networkAvailabilityHandler, null);
            if (availabilityHandler != null)
                NetworkChange.NetworkAvailabilityChanged -= availabilityHandler;
            var addressHandler = Interlocked.Exchange(ref networkAddressHandler, null);
            if (addressHandler != null)
                NetworkChange.NetworkAddressChanged -= addressHandler;
            SetRelayState(ProductionRelayState.Stopped, string.Empty);
            cancellation.Cancel();
            CancelAllHostHistoryRequests();
            connectionSupervisor.MarkPhysicalDisconnected();
            ObserveDirectRelayConnectedSafely(false);
            try { directPeerRoutes.RetireAllDirect("ACCOUNT_RUNTIME_STOPPED"); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            BeginDirectRuntimeCleanup();
            commonIngressQueue.Writer.TryComplete();
            pendingSessions.Clear();

            ClientWebSocket active;
            lock (lifecycleSync) active = socket;
            if (active != null)
            {
                try { active.Abort(); }
                catch (ObjectDisposedException) { }
                active.Dispose();
            }
        }

        public void Dispose()
        {
            StopAccountSynchronization();
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0) return;

            Task activeWorker;
            Task activeHistoryWorker;
            Task activePriorityHistoryWorker;
            Task activeMetadataWorker;
            Task activeCommonIngressWorker;
            Task activeDirectRoutingPolicyWorker;
            Task activeStatusRefresh;
            Task activeDirectShadowCleanup;
            lock (lifecycleSync)
            {
                activeWorker = worker;
                activeHistoryWorker = historyWorker;
                activePriorityHistoryWorker = priorityHistoryWorker;
                activeMetadataWorker = metadataWorker;
                activeCommonIngressWorker = commonIngressWorker;
                activeDirectRoutingPolicyWorker = directRoutingPolicyWorker;
                activeStatusRefresh = statusRefreshWorker;
                activeDirectShadowCleanup = directShadowCleanupTask;
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
            if (activePriorityHistoryWorker != null)
            {
                try { activePriorityHistoryWorker.GetAwaiter().GetResult(); }
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
            if (activeCommonIngressWorker != null)
            {
                try { activeCommonIngressWorker.GetAwaiter().GetResult(); }
                catch (AggregateException) { }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            if (activeDirectRoutingPolicyWorker != null)
            {
                try { activeDirectRoutingPolicyWorker.GetAwaiter().GetResult(); }
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
            if (activeDirectShadowCleanup != null)
            {
                try { activeDirectShadowCleanup.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
            directAcceptanceDiagnostics?.Dispose();
            directAcceptancePeerSnapshots.Clear();
#endif

            try { relayExactOutbox.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }

            // Host History tasks are intentionally independent from the Relay
            // receive worker. They alone dispose their request CTS after leaving
            // any ACK wait and gate lease, so cleanup must join them before
            // disposing shared crypto/store/semaphore dependencies.
            WaitForHostHistoryRequests();
            WaitForSessionHistoryDeletions();

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
            while (persistedHistoryBacklog.TryDequeue(out _)) { }
            persistedHistoryBacklogSessions.Clear();
            persistedHistoryWorkSessions.Clear();
            activeHistoryWorkSessions.Clear();
            pendingHistoryFrames.Clear();
            historyReconcileSessions.Clear();
            terminalHistoryPersistenceFailures.Clear();
            terminalCoreShadowStates.Clear();
            terminalHistoryProducer.Dispose();
            publishedSequences.Clear();
            foreach (var state in publicationStates.Values) state.RestUpsertGate.Dispose();
            publicationStates.Clear();
            localInputEpochs.Clear();
            inputSequences.Clear();
            inputLeaseAuthorizations.Clear();
            foreach (var replay in inputReplayRecords.Values)
            {
                if (replay.EnvelopeSha256 != null)
                    CryptographicOperations.ZeroMemory(replay.EnvelopeSha256);
            }
            inputReplayRecords.Clear();
            while (inputReplayOrder.TryDequeue(out _)) { }
            terminationRequests.Clear();
            eventSignal.Dispose();
            metadataSignal.Dispose();
            historySignal.Dispose();
            socketSendGate.Dispose();
            authorizedRecipientGate.Dispose();
            statusRefreshGate.Dispose();
            tokenRefreshGate.Dispose();
            directConfigRefreshGate.Dispose();
            hostHistoryResponseGate.Dispose();
            directPeerRoutes.Dispose();
            connectionSupervisor.Dispose();
            cancellation.Dispose();
        }
    }
}

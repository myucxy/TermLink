using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectDataRouteOutcome
    {
        DirectAccepted,
        RelaySent,
        RelayDeferred,
        Dropped
    }

    internal sealed class ProductionDirectDataIngressMetadata
    {
        internal Guid SessionId { get; init; }
        internal ulong EnvelopeSequence { get; init; }
    }

    internal sealed class ProductionDirectDataRelayEnvelope
    {
        internal Guid PeerDeviceId { get; init; }
        internal Guid SessionId { get; init; }
        internal ulong EnvelopeSequence { get; init; }
        internal byte[] ExactEnvelope { get; init; } = Array.Empty<byte>();
    }

    internal sealed class ProductionDirectDataIngressDelivery
    {
        internal Guid PeerDeviceId { get; init; }
        internal ProductionDirectIngressSourceV5 Source { get; init; }
        /// <summary>
        /// Identifies the exact actor path that produced this delivery. Relay
        /// fallback without a registered actor has no Direct path and leaves
        /// this value null.
        /// </summary>
        internal ProductionDirectPathFenceV5 Path { get; init; }
        internal byte[] ExactEnvelope { get; init; } = Array.Empty<byte>();
    }

    internal sealed class ProductionDirectDataNativeAction
    {
        internal Guid PeerDeviceId { get; init; }
        internal ProductionDirectRouteActionV5 Action { get; init; }
        internal byte[] PairwiseKey { get; init; } = Array.Empty<byte>();
    }

    internal sealed class ProductionDirectDataPeerSnapshot
    {
        internal Guid PeerDeviceId { get; init; }
        internal ProductionDirectPathFenceV5 Path { get; init; }
        internal long KeyEpoch { get; init; }
        internal long AdmissionGeneration { get; init; }
        internal ProductionDirectCarrierV5 PcToAndroidCarrier { get; init; }
        internal ProductionDirectCarrierV5 AndroidToPcCarrier { get; init; }
        internal bool ReadyForSessionCutover { get; init; }
        internal IReadOnlyDictionary<Guid, ProductionDirectCarrierV5>
            PcToAndroidSessionCarriers { get; init; } =
                new Dictionary<Guid, ProductionDirectCarrierV5>();
        internal IReadOnlyDictionary<Guid, ProductionDirectCarrierV5>
            AndroidToPcSessionCarriers { get; init; } =
                new Dictionary<Guid, ProductionDirectCarrierV5>();
        internal bool CommittedDirect { get; init; }
        internal int ActiveCutoverCount { get; init; }
        internal int CommittedCutoverCount { get; init; }
        /// <summary>
        /// Data frames accepted by the QUIC send loop. This is sourced from
        /// native ENVELOPE_QUEUED events, which are emitted only after the
        /// framed transport send completes.
        /// </summary>
        internal ulong DirectFramesSent { get; init; }
        /// <summary>
        /// Direct realtime Envelope frames admitted by the native receive
        /// loop. Control frames are deliberately excluded.
        /// </summary>
        internal ulong DirectFramesReceived { get; init; }
        internal ulong TransportReceiptsSent { get; init; }
        internal ulong TransportReceiptsReceived { get; init; }
        /// <summary>Highest native Direct frame acknowledged by the peer.</summary>
        internal ulong TransportReceiptHighWatermark { get; init; }
        /// <summary>
        /// Highest received Direct realtime Envelope frame exposed by ABI v5.
        /// The receipt frame's own wire sequence is not exposed by this ABI.
        /// </summary>
        internal ulong LastReceivedEnvelopeFrameSequence { get; init; }
        internal ulong IngressHoldCount { get; init; }
        /// <summary>
        /// Total native ingress drops. ABI v5 does not distinguish an exact
        /// duplicate from a retired-session/late-Direct drop.
        /// </summary>
        internal ulong IngressDropCount { get; init; }
        /// <summary>
        /// Null means the frozen ABI cannot classify the corresponding total
        /// without guessing. Consumers must not render null as a measured 0.
        /// </summary>
        internal ulong? IngressDuplicateDropCount { get; init; }
        internal ulong? IngressDigestConflictCount { get; init; }
        internal ulong? IngressGapCount { get; init; }
        internal ulong RelayReplayEnvelopeCount { get; init; }
        internal ulong RelayReplayBytes { get; init; }
        internal ulong FallbackCount { get; init; }
        internal string LastFallbackReasonCode { get; init; } = string.Empty;
        internal ulong NativeQueueLimitCount { get; init; }
        internal ulong PeerMailboxBytes { get; init; }
        internal bool SensitiveStateCleared { get; init; }
        internal bool OwnedCommandDataCleared { get; init; }
        internal bool Retired { get; init; }
        internal string LastFault { get; init; } = string.Empty;
    }

    internal sealed class ProductionDirectDataRuntimeOptions
    {
        internal int PeerMailboxCapacity { get; init; } = 64;
        internal long PeerMailboxCapacityBytes { get; init; } = 8L * 1024 * 1024;
        internal uint DuplicateGraceMilliseconds { get; init; } = 1_000;
        internal Func<long> ClockMilliseconds { get; init; } =
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        /// <summary>
        /// Synchronous, non-blocking ownership transfer into the Relay-owned
        /// bounded/retryable outbox. The receiver always owns ExactEnvelope
        /// after return and must zero it after delivery or disposal. The return
        /// value reports only whether Relay sent immediately (true) or retained
        /// the item for retry (false); false never rejects ownership.
        /// </summary>
        internal Func<ProductionDirectDataRelayEnvelope, bool> EnqueueRelayEnvelope { get; init; }
        /// <summary>
        /// Synchronous, non-blocking ownership transfer into the common
        /// business-ingress queue. That queue is the sole delivery owner and
        /// advances durable cursors only after the business commit completes.
        /// </summary>
        internal Action<ProductionDirectDataIngressDelivery> EnqueueIngress { get; init; }
        /// <summary>
        /// Synchronous ownership transfer to the cutover/E2EE control queue.
        /// The receiver owns PairwiseKey and must zero it after processing.
        /// </summary>
        internal Action<ProductionDirectDataNativeAction> EnqueueNativeAction { get; init; }
        /// <summary>
        /// Creates one fresh V5 owner around the exact promoted path. Tests may
        /// replace this factory; production uses the pinned native ABI V5 DLL.
        /// </summary>
        internal Func<ProductionDirectPathFenceV5, IProductionDirectDataNativePeer>
            CreateNativePeer { get; init; }
        internal Func<Guid, TransportSignalEnvelopeV1, byte[], CancellationToken, Task>
            HandleSignalAsync { get; init; }
        internal Func<TransportSignalEnvelopeV1, CancellationToken, Task>
            HandlePrePromotionSignalAsync { get; init; }
        internal Func<byte[], ProductionDirectDataIngressMetadata> ParseIngressMetadata { get; init; }
        internal Action<ProductionDirectDataPeerSnapshot> PeerStateChanged { get; init; }
        internal Action<Exception> SignalingReaderFaulted { get; init; }
        /// <summary>
        /// Exchanges the peer's current ticket for a server-renewed one
        /// (transport audit #2). Returns the renewed ticket wire bytes, or
        /// null when the server refused or the endpoint was unreachable; the
        /// actor retries with backoff and the native hard stop remains the
        /// final fallback. Null disables renewal (shadow/test runtimes).
        /// </summary>
        internal Func<Guid, Guid, byte[], CancellationToken, Task<byte[]>> RenewTicketAsync
        { get; init; }
    }

    internal interface IProductionDirectDataNativePeer : IDisposable
    {
        bool NativeAvailable { get; }
        ProductionDirectNativeResultV5 Promote(
            IProductionDirectPromotableNativeV4 v4,
            ProductionDirectPromoteRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        (ProductionDirectNativeResultV5 Status, ProductionDirectPathInfoV5 Info)
            PathInfo(ProductionDirectPathFenceV5 path) =>
            (ProductionDirectNativeResultV5.NotReady, null);
        ProductionDirectNativeResultV5 StartProposal(ProductionDirectProposalRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 ReceiveProposal(ProductionDirectProposalRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 ReceiveAccept(ProductionDirectTransitionRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 ReceiveCommit(ProductionDirectTransitionRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 CommitEchoSent(ProductionDirectTransitionRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 ReceiveFallback(ProductionDirectTransitionRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 Tick(ProductionDirectTransitionRequestV5 request) =>
            ProductionDirectNativeResultV5.InvalidArgument;
        ProductionDirectNativeResultV5 RouteEnvelope(
            ProductionDirectRouteEnvelopeRequestV5 request,
            byte[] exactEnvelope);
        ProductionDirectNativeResultV5 ArbitrateIngress(
            ProductionDirectIngressRequestV5 request,
            byte[] exactEnvelope);
        ProductionDirectNativeResultV5 TickIngress(
            ProductionDirectPathFenceV5 path,
            ulong nowMilliseconds);
        ProductionDirectSigningPayloadV5 ReceiveSigningPayload(ProductionDirectPathFenceV5 path);
        ProductionDirectNativeResultV5 SubmitSignature(
            ProductionDirectPathFenceV5 path,
            ulong requestId,
            byte[] signature);
        (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
            PollRouteAction(ProductionDirectPathFenceV5 path);
        ProductionDirectNativeReceiveV5 ReceiveEnvelope(ProductionDirectPathFenceV5 path);
        IReadOnlyList<ProductionDirectNativeEventV5> Poll(int maximumEvents = 32);
        ProductionDirectNativeResultV5 UpdateAppliedCursor(ProductionDirectCursorUpdateV5 request);
        ProductionDirectNativeResultV5 DeregisterSession(
            ProductionDirectSessionDeregisterV5 request);
        ProductionDirectNativeResultV5 NetworkChanged(ProductionDirectPathFenceV5 path);
        ProductionDirectNativeResultV5 Cancel(ProductionDirectPathFenceV5 path);

        /// <summary>
        /// Applies a server-renewed path ticket. Default-disabled so test
        /// fakes that predate renewals keep compiling; the production peer
        /// forwards to the boundary's renew_apply export.
        /// </summary>
        ProductionDirectNativeResultV5 RenewApplyTicket(
            ProductionDirectPathFenceV5 path, byte[] ticket) =>
            ProductionDirectNativeResultV5.InvalidArgument;
    }

    internal sealed class ProductionDirectDataNativePeer : IProductionDirectDataNativePeer
    {
        private readonly ProductionDirectNativeBoundaryV5 boundary;

        internal ProductionDirectDataNativePeer(ProductionDirectNativeBoundaryV5 boundary) =>
            this.boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));

        public bool NativeAvailable => boundary.NativeAvailable;
        public ProductionDirectNativeResultV5 Promote(
            IProductionDirectPromotableNativeV4 v4,
            ProductionDirectPromoteRequestV5 request) => boundary.Promote(v4, request);
        public (ProductionDirectNativeResultV5 Status, ProductionDirectPathInfoV5 Info)
            PathInfo(ProductionDirectPathFenceV5 path) => boundary.PathInfo(path);
        public ProductionDirectNativeResultV5 StartProposal(ProductionDirectProposalRequestV5 request) =>
            boundary.StartProposal(request);
        public ProductionDirectNativeResultV5 ReceiveProposal(ProductionDirectProposalRequestV5 request) =>
            boundary.ReceiveProposal(request);
        public ProductionDirectNativeResultV5 ReceiveAccept(ProductionDirectTransitionRequestV5 request) =>
            boundary.ReceiveAccept(request);
        public ProductionDirectNativeResultV5 ReceiveCommit(ProductionDirectTransitionRequestV5 request) =>
            boundary.ReceiveCommit(request);
        public ProductionDirectNativeResultV5 CommitEchoSent(ProductionDirectTransitionRequestV5 request) =>
            boundary.CommitEchoSent(request);
        public ProductionDirectNativeResultV5 ReceiveFallback(ProductionDirectTransitionRequestV5 request) =>
            boundary.ReceiveFallback(request);
        public ProductionDirectNativeResultV5 Tick(ProductionDirectTransitionRequestV5 request) =>
            boundary.Tick(request);
        public ProductionDirectNativeResultV5 RouteEnvelope(
            ProductionDirectRouteEnvelopeRequestV5 request,
            byte[] exactEnvelope) => boundary.RouteEnvelope(request, exactEnvelope);
        public ProductionDirectNativeResultV5 ArbitrateIngress(
            ProductionDirectIngressRequestV5 request,
            byte[] exactEnvelope) => boundary.ArbitrateIngress(request, exactEnvelope);
        public ProductionDirectNativeResultV5 TickIngress(
            ProductionDirectPathFenceV5 path,
            ulong nowMilliseconds) => boundary.TickIngress(path, nowMilliseconds);
        public ProductionDirectSigningPayloadV5 ReceiveSigningPayload(ProductionDirectPathFenceV5 path) =>
            boundary.ReceiveSigningPayload(path);
        public ProductionDirectNativeResultV5 SubmitSignature(
            ProductionDirectPathFenceV5 path,
            ulong requestId,
            byte[] signature) => boundary.SubmitSignature(path, requestId, signature);
        public (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
            PollRouteAction(ProductionDirectPathFenceV5 path) => boundary.PollRouteAction(path);
        public ProductionDirectNativeReceiveV5 ReceiveEnvelope(ProductionDirectPathFenceV5 path) =>
            boundary.ReceiveEnvelope(path);
        public IReadOnlyList<ProductionDirectNativeEventV5> Poll(int maximumEvents = 32) =>
            boundary.Poll(maximumEvents);
        public ProductionDirectNativeResultV5 UpdateAppliedCursor(
            ProductionDirectCursorUpdateV5 request) => boundary.UpdateAppliedCursor(request);
        public ProductionDirectNativeResultV5 DeregisterSession(
            ProductionDirectSessionDeregisterV5 request) => boundary.DeregisterSession(request);
        public ProductionDirectNativeResultV5 NetworkChanged(ProductionDirectPathFenceV5 path) =>
            boundary.NetworkChanged(path);
        public ProductionDirectNativeResultV5 Cancel(ProductionDirectPathFenceV5 path) =>
            boundary.Cancel(path);
        public ProductionDirectNativeResultV5 RenewApplyTicket(
            ProductionDirectPathFenceV5 path, byte[] ticket) =>
            boundary.RenewApplyTicket(path, ticket);
        public void Dispose() => boundary.Dispose();
    }

    internal sealed class ProductionDirectDataPeerRegistration
    {
        internal Guid PeerDeviceId { get; init; }
        internal ProductionDirectPathFenceV5 Path { get; init; }
        internal long KeyEpoch { get; init; }
        internal long AdmissionGeneration { get; init; }
        internal ulong InitialLocalSignalSequence { get; init; }
        internal ulong InitialRemoteSignalSequence { get; init; }
        internal ulong InitialRemoteSignalAcknowledgement { get; init; }
        internal byte[] PairwiseKey { get; init; }
        internal byte[] SignalKeyId { get; init; }
        /// <summary>
        /// The validated path ticket wire bytes at promotion time. The actor
        /// keeps exactly one current generation: it is the renewal proof for
        /// the server and is replaced after each accepted renew_apply.
        /// </summary>
        internal byte[] TicketWireBytes { get; init; }
        internal Func<byte[], byte[]> SignNativeTranscript { get; init; }
        internal IProductionDirectDataNativePeer Native { get; init; }
        internal ProductionDirectCommitRequestV4 PromotedNomination { get; init; }
        internal Action<byte[]> NominationCompleted { get; init; }
        internal Action<string> NominationFailed { get; init; }
    }

    /// <summary>
    /// Production Direct data coordinator. It is the sole reader of the shared
    /// signaling lane and dispatches all peer/native work to one bounded,
    /// single-reader actor per Android device. Relay remains an independent
    /// authority and is never owned, restarted, or stopped by this runtime.
    /// </summary>
    internal sealed class ProductionDirectDataRuntime : IAsyncDisposable
    {
        private readonly Guid localDeviceId;
        private readonly IProductionDirectShadowSignaling signaling;
        private readonly ProductionDirectDataRuntimeOptions options;
        private readonly ConcurrentDictionary<Guid, PeerActor> peers = new();
        private readonly object peerRegistrySync = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly object signalStateSync = new();
        private Task reader;
        private long signalGeneration;
        private bool signalConnected;
        private bool directDataNegotiated;
        private long networkGeneration;
        private int started;
        private int disposed;

        internal ProductionDirectDataRuntime(
            Guid localDeviceId,
            IProductionDirectShadowSignaling signaling,
            ProductionDirectDataRuntimeOptions options)
        {
            if (localDeviceId == Guid.Empty)
                throw new ArgumentException("A local device ID is required.", nameof(localDeviceId));
            this.localDeviceId = localDeviceId;
            this.signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (options.PeerMailboxCapacity is < 4 or > 1_024)
                throw new ArgumentOutOfRangeException(nameof(options.PeerMailboxCapacity));
            if (options.PeerMailboxCapacityBytes is < 1 or > 8L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(options.PeerMailboxCapacityBytes));
            if (options.DuplicateGraceMilliseconds > 5_000)
                throw new ArgumentOutOfRangeException(nameof(options.DuplicateGraceMilliseconds));
            _ = options.ClockMilliseconds ?? throw new ArgumentNullException(nameof(options.ClockMilliseconds));
            _ = options.EnqueueRelayEnvelope ??
                throw new ArgumentNullException(nameof(options.EnqueueRelayEnvelope));
            _ = options.EnqueueIngress ??
                throw new ArgumentNullException(nameof(options.EnqueueIngress));
            _ = options.ParseIngressMetadata ??
                throw new ArgumentNullException(nameof(options.ParseIngressMetadata));
        }

        /// <summary>
        /// Performs the one-shot V4-to-V5 native socket transfer and installs
        /// the resulting actor atomically. The caller may dispose the handoff
        /// immediately after this task returns; every actor-owned value is
        /// copied before registration.
        /// </summary>
        internal async Task<bool> PromotePeerAsync(
            ProductionDirectDataPromotionHandoff handoff,
            CancellationToken cancellationToken = default)
        {
            if (handoff == null) throw new ArgumentNullException(nameof(handoff));
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref started) == 0) return false;
            long promotionNetworkGeneration;
            lock (peerRegistrySync) promotionNetworkGeneration = networkGeneration;
            var path = CopyPath(handoff.Path);
            IProductionDirectDataNativePeer native = null;
            ProductionDirectCommitRequestV4 nomination = null;
            byte[] registrationKey = null;
            byte[] registrationKeyId = null;
            byte[] registrationTicket = null;
            ProductionDirectCommitRequestV4 registrationNomination = null;
            try
            {
                native = options.CreateNativePeer?.Invoke(path) ??
                    new ProductionDirectDataNativePeer(new ProductionDirectNativeBoundaryV5(
                        enabled: true,
                        initialPath: path));
                if (native?.NativeAvailable != true) return false;
                nomination = handoff.CopyNomination();
                var promoted = native.Promote(handoff.Native, new ProductionDirectPromoteRequestV5
                {
                    Path = path,
                    Nomination = nomination
                });
                if (promoted != ProductionDirectNativeResultV5.Accepted) return false;

                registrationKey = handoff.CopyPairwiseKey();
                registrationKeyId = handoff.CopySignalKeyId();
                registrationTicket = handoff.CopyTicketWireBytes();
                registrationNomination = ProductionDirectDataPromotionHandoff.CopyNomination(
                    nomination);
                var registration = new ProductionDirectDataPeerRegistration
                {
                    PeerDeviceId = handoff.PeerDeviceId,
                    Path = path,
                    KeyEpoch = handoff.KeyEpoch,
                    AdmissionGeneration = handoff.AdmissionGeneration,
                    InitialLocalSignalSequence = handoff.LocalSignalSequence,
                    InitialRemoteSignalSequence = handoff.RemoteSignalSequence,
                    InitialRemoteSignalAcknowledgement = handoff.RemoteSignalAcknowledgement,
                    PairwiseKey = registrationKey,
                    SignalKeyId = registrationKeyId,
                    TicketWireBytes = registrationTicket,
                    SignNativeTranscript = handoff.SignNativeTranscript,
                    Native = native,
                    PromotedNomination = registrationNomination,
                    NominationCompleted = handoff.NominationCompleted,
                    NominationFailed = handoff.NominationFailed
                };
                if (!await RegisterPeerAsync(
                        registration,
                        cancellationToken,
                        promotionNetworkGeneration).ConfigureAwait(false))
                    return false;
                native = null;
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return false;
            }
            finally
            {
                ProductionDirectDataPromotionHandoff.ZeroNomination(nomination);
                ProductionDirectDataPromotionHandoff.ZeroNomination(registrationNomination);
                if (registrationKey != null) CryptographicOperations.ZeroMemory(registrationKey);
                if (registrationKeyId != null) CryptographicOperations.ZeroMemory(registrationKeyId);
                if (registrationTicket != null)
                    CryptographicOperations.ZeroMemory(registrationTicket);
                try { native?.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }

        internal bool Start()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;
            if (!signaling.Start())
            {
                Interlocked.Exchange(ref started, 0);
                return false;
            }
            reader = ReadSignalsAsync(lifetime.Token);
            return true;
        }

        /// <summary>
        /// Updates admission for new cutovers only. A Relay/signaling
        /// generation change deliberately does not retire an already committed
        /// Direct carrier.
        /// </summary>
        internal void ObserveSignalingState(bool connected, long generation, bool dataNegotiated)
        {
            if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
            lock (signalStateSync)
            {
                if (generation < signalGeneration) return;
                signalGeneration = generation;
                signalConnected = connected;
                directDataNegotiated = connected && dataNegotiated;
            }
        }

        internal async Task<bool> RegisterPeerAsync(
            ProductionDirectDataPeerRegistration registration,
            CancellationToken cancellationToken = default) =>
            await RegisterPeerAsync(registration, cancellationToken, null).ConfigureAwait(false);

        private async Task<bool> RegisterPeerAsync(
            ProductionDirectDataPeerRegistration registration,
            CancellationToken cancellationToken,
            long? expectedNetworkGeneration)
        {
            ValidateRegistration(registration);
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            PeerActor actor;
            PeerActor replaced = null;
            lock (peerRegistrySync)
            {
                ThrowIfDisposed();
                if (expectedNetworkGeneration.HasValue &&
                    expectedNetworkGeneration.Value != networkGeneration)
                    return false;
                if (peers.TryGetValue(registration.PeerDeviceId, out var current) &&
                    registration.Path.PathEpoch <= current.PathEpoch)
                    return false;
                actor = new PeerActor(
                    localDeviceId,
                    signaling,
                    CaptureSignalGate,
                    registration,
                    options,
                    RemoveRetiredPeer);
                if (current != null) replaced = current;
                peers[registration.PeerDeviceId] = actor;
                // Start while the install lock still owns the dictionary slot,
                // so a concurrent successor cannot retire an unstarted actor.
                actor.Start();
            }
            if (replaced != null) await replaced.DisposeAsync("PEER_REPLACED").ConfigureAwait(false);
            return true;
        }

        internal async Task<ProductionDirectDataRouteOutcome> RouteEnvelopeAsync(
            Guid peerDeviceId,
            Guid sessionId,
            ulong envelopeSequence,
            byte[] exactEnvelope,
            bool routeEligible,
            CancellationToken cancellationToken = default)
        {
            ValidateEnvelopeIdentity(peerDeviceId, sessionId, envelopeSequence, exactEnvelope);
            ValidateExactEgressEnvelope(
                localDeviceId,
                peerDeviceId,
                sessionId,
                envelopeSequence,
                exactEnvelope,
                routeEligible);
            ThrowIfDisposed();
            if (!peers.TryGetValue(peerDeviceId, out var actor))
                return await SendRelayCloneAsync(peerDeviceId, sessionId, envelopeSequence,
                    exactEnvelope, cancellationToken).ConfigureAwait(false);
            var command = new RouteEnvelopeCommand(
                sessionId, envelopeSequence, exactEnvelope, routeEligible);
            if (!actor.TryPost(command))
            {
                command.Zero();
                actor.ObserveCommandCleanup(command);
                try
                {
                    return await SendRelayCloneAsync(peerDeviceId, sessionId, envelopeSequence,
                        exactEnvelope, cancellationToken).ConfigureAwait(false);
                }
                finally { _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL"); }
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> ArbitrateIngressAsync(
            Guid peerDeviceId,
            ProductionDirectIngressSourceV5 source,
            byte[] exactEnvelope,
            CancellationToken cancellationToken = default)
        {
            if (peerDeviceId == Guid.Empty || exactEnvelope?.Length is < 1 or
                >= 1024 * 1024 || source is not
                    (ProductionDirectIngressSourceV5.Relay or ProductionDirectIngressSourceV5.Direct))
                throw new ArgumentException("Direct ingress identity is invalid.");
            var metadata = options.ParseIngressMetadata(exactEnvelope) ??
                throw new InvalidOperationException("Direct ingress metadata was rejected.");
            if (metadata.SessionId == Guid.Empty || metadata.EnvelopeSequence == 0)
                throw new InvalidOperationException("Direct ingress metadata is invalid.");
            ThrowIfDisposed();
            if (!peers.TryGetValue(peerDeviceId, out var actor))
            {
                if (source == ProductionDirectIngressSourceV5.Relay)
                    return await DeliverIngressCloneAsync(
                        peerDeviceId, source, path: null, exactEnvelope, cancellationToken)
                        .ConfigureAwait(false);
                return false;
            }
            var command = new IngressCommand(source, metadata, exactEnvelope);
            if (!actor.TryPost(command))
            {
                command.Complete(false);
                actor.ObserveCommandCleanup(command);
                if (source == ProductionDirectIngressSourceV5.Relay)
                {
                    try
                    {
                        return await DeliverIngressCloneAsync(
                            peerDeviceId, source, actor.Path, exactEnvelope, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally { _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL"); }
                }
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> UpdateAppliedCursorAsync(
            Guid peerDeviceId,
            ProductionDirectDirectionV5 direction,
            Guid sessionId,
            ulong highestContiguous,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!peers.TryGetValue(peerDeviceId, out var actor)) return false;
            var command = new CursorCommand(direction, sessionId, highestContiguous);
            if (!actor.TryPost(command))
            {
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> DeregisterSessionAsync(
            Guid peerDeviceId,
            ProductionDirectDirectionV5 direction,
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty ||
                direction is not ProductionDirectDirectionV5.PcToAndroid and
                    not ProductionDirectDirectionV5.AndroidToPc ||
                !peers.TryGetValue(peerDeviceId, out var actor))
                return false;
            var command = new DeregisterSessionCommand(direction, sessionId);
            if (!actor.TryPost(command))
            {
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> StartCutoverAsync(
            Guid peerDeviceId,
            ProductionDirectCutoverProposal proposal,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (peerDeviceId == Guid.Empty || proposal == null) return false;
            if (!peers.TryGetValue(peerDeviceId, out var actor)) return false;
            var command = new StartCutoverCommand(CopyProposal(proposal));
            if (!actor.TryPost(command))
            {
                command.Completion.TrySetResult(false);
                command.ReleaseOwnedData();
                actor.ObserveCommandCleanup(command);
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates and starts a PC-to-Android proposal inside the peer actor so
        /// the Direct path binding never crosses into the product runtime.
        /// The caller owns only the session sequence barriers.
        /// </summary>
        internal async Task<bool> StartPcToAndroidCutoverAsync(
            Guid peerDeviceId,
            IReadOnlyList<ProductionDirectSessionCutoverBarrier> sessionBarriers,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (peerDeviceId == Guid.Empty || sessionBarriers == null ||
                sessionBarriers.Count is < 1 or > ProductionDirectCutoverCodec.MaximumSessionBarriers ||
                sessionBarriers.Any(value => value == null || value.SessionId == Guid.Empty ||
                    value.FirstDirectSequence is 0 or > long.MaxValue) ||
                sessionBarriers.Select(value => value.SessionId).Distinct().Count() !=
                    sessionBarriers.Count ||
                !peers.TryGetValue(peerDeviceId, out var actor))
                return false;
            var command = new StartOwnedCutoverCommand(sessionBarriers.Select(value =>
                new ProductionDirectSessionCutoverBarrier
                {
                    SessionId = value.SessionId,
                    FirstDirectSequence = value.FirstDirectSequence
                }).ToArray());
            if (!actor.TryPost(command))
            {
                command.Completion.TrySetResult(false);
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<bool> SendSignalAsync(
            Guid peerDeviceId,
            TransportSignalEnvelopeV1 signal,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!peers.TryGetValue(peerDeviceId, out var actor) || signal == null) return false;
            var gate = CaptureSignalGate();
            var command = new OutgoingSignalCommand(signal.Clone(), gate);
            if (!actor.TryPost(command))
            {
                command.Completion.TrySetResult(false);
                _ = RetireActorAsync(actor, "PEER_MAILBOX_FULL");
                return false;
            }
            return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void NotifyNetworkChanged()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            PeerActor[] current;
            lock (peerRegistrySync)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                networkGeneration = checked(networkGeneration + 1);
                current = peers.Values.ToArray();
            }
            foreach (var actor in current)
            {
                // A network transition fences the socket behind this actor.
                // If its bounded mailbox cannot accept the serialized native
                // notification, retire that exact actor instead of allowing a
                // stale Direct path to survive or evicting a replacement.
                if (!actor.TryPost(new NetworkChangedCommand()))
                    _ = RetireActorAsync(actor, "NETWORK_CHANGE_MAILBOX_FULL");
            }
        }

        /// <summary>
        /// Retires only the currently installed actor when all three path
        /// fence fields match. Use this overload for asynchronous Direct path
        /// callbacks, where an older actor may race with a successor.
        /// </summary>
        internal async Task<bool> RetirePeerAsync(
            Guid peerDeviceId,
            ProductionDirectPathFenceV5 expectedPath,
            string reasonCode)
        {
            if (peerDeviceId == Guid.Empty || expectedPath == null ||
                expectedPath.PathEpoch == 0 || expectedPath.ConnectionId == Guid.Empty ||
                expectedPath.AttemptId == Guid.Empty ||
                !peers.TryGetValue(peerDeviceId, out var actor) ||
                !actor.MatchesPath(expectedPath) ||
                !((ICollection<KeyValuePair<Guid, PeerActor>>)peers).Remove(
                    new KeyValuePair<Guid, PeerActor>(peerDeviceId, actor)))
                return false;
            await actor.DisposeAsync(reasonCode).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Unconditionally retires the current actor for authority teardown.
        /// Path-scoped failure callbacks must use the fenced overload above.
        /// </summary>
        internal async Task<bool> RetirePeerAsync(Guid peerDeviceId, string reasonCode)
        {
            if (!peers.TryRemove(peerDeviceId, out var actor)) return false;
            await actor.DisposeAsync(reasonCode).ConfigureAwait(false);
            return true;
        }

        private async Task RetireActorAsync(PeerActor actor, string reasonCode)
        {
            if (actor == null) return;
            ((ICollection<KeyValuePair<Guid, PeerActor>>)peers).Remove(
                new KeyValuePair<Guid, PeerActor>(actor.PeerDeviceId, actor));
            await actor.DisposeAsync(reasonCode).ConfigureAwait(false);
        }

        internal IReadOnlyList<ProductionDirectDataPeerSnapshot> Snapshot() =>
            peers.Values.Select(value => value.Snapshot)
                .OrderBy(value => value.PeerDeviceId).ToArray();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            if (reader != null)
            {
                try { await reader.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            KeyValuePair<Guid, PeerActor>[] current;
            lock (peerRegistrySync)
            {
                current = peers.ToArray();
                peers.Clear();
            }
            await Task.WhenAll(current.Select(item => item.Value.DisposeAsync("RUNTIME_STOPPED")))
                .ConfigureAwait(false);
            await signaling.DisposeAsync().ConfigureAwait(false);
            lifetime.Dispose();
        }

        private async Task ReadSignalsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var signal in signaling.ReadSignalsAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!TryReadPeer(signal, localDeviceId, out var peerDeviceId)) continue;
                    if (!peers.TryGetValue(peerDeviceId, out var actor))
                    {
                        if ((IsPrePromotionSignal(signal.SignalType) ||
                                IsCleanupSignal(signal.SignalType)) &&
                            options.HandlePrePromotionSignalAsync != null)
                        {
                            try
                            {
                                await options.HandlePrePromotionSignalAsync(
                                    signal.Clone(), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is not OutOfMemoryException)
                            {
                                try { options.SignalingReaderFaulted?.Invoke(error); }
                                catch (Exception) { }
                            }
                        }
                        continue;
                    }
                    var gate = CaptureSignalGate();
                    // A FALLBACK is cleanup, not authority to start or advance
                    // a cutover.  Keep accepting it on the current connected
                    // signalling generation after the data capability is
                    // withdrawn so an already committed direction can still
                    // converge to Relay.  The actor decrypts the payload and
                    // distinguishes a committed barrier from a stale
                    // negotiating barrier before mutating native state.
                    if (IsCutoverSignal(signal.SignalType) &&
                        (signal.SignalType == TransportSignalTypeV1.Fallback
                            ? !gate.Connected || gate.Generation <= 0
                            : !gate.DataAvailable))
                        continue;
                    if (!actor.TryPost(new IncomingSignalCommand(signal.Clone(), gate)))
                        _ = RetireActorAsync(actor, "SIGNAL_MAILBOX_FULL");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A shared signaling-lane fault is diagnostic only. Healthy,
                // committed Direct paths and the independent Relay survive.
                try { options.SignalingReaderFaulted?.Invoke(error); }
                catch (Exception) { }
            }
        }

        private SignalGate CaptureSignalGate()
        {
            lock (signalStateSync)
                return new SignalGate(signalConnected, directDataNegotiated, signalGeneration);
        }

        private Task<ProductionDirectDataRouteOutcome> SendRelayCloneAsync(
            Guid peerDeviceId,
            Guid sessionId,
            ulong sequence,
            byte[] exactEnvelope,
            CancellationToken token)
        {
            return Task.FromResult(TransferRelay(
                options, peerDeviceId, sessionId, sequence, exactEnvelope));
        }

        private Task<bool> DeliverIngressCloneAsync(
            Guid peerDeviceId,
            ProductionDirectIngressSourceV5 source,
            ProductionDirectPathFenceV5 path,
            byte[] exactEnvelope,
            CancellationToken token)
        {
            TransferIngress(options, peerDeviceId, source, path, exactEnvelope);
            return Task.FromResult(true);
        }

        private static ProductionDirectDataRouteOutcome TransferRelay(
            ProductionDirectDataRuntimeOptions runtimeOptions,
            Guid peerDeviceId,
            Guid sessionId,
            ulong sequence,
            byte[] exactEnvelope)
        {
            if (exactEnvelope == null || exactEnvelope.Length == 0)
                throw new InvalidOperationException("Relay transfer has no exact Envelope.");
            var owned = exactEnvelope.ToArray();
            try
            {
                bool sent;
                try
                {
                    sent = runtimeOptions.EnqueueRelayEnvelope(
                        new ProductionDirectDataRelayEnvelope
                        {
                            PeerDeviceId = peerDeviceId,
                            SessionId = sessionId,
                            EnvelopeSequence = sequence,
                            ExactEnvelope = owned
                        });
                }
                catch (Exception error) when (error is not OutOfMemoryException &&
                    error is not StackOverflowException && error is not AccessViolationException)
                {
                    // The Relay callback rejected before ownership crossed.
                    // Native's action payload is about to be destroyed, so the
                    // current durable publisher must receive Dropped and retry
                    // from its independent exact-byte cache.
                    throw new RelayOwnershipTransferException(
                        peerDeviceId, sessionId, sequence, error);
                }
                // Ownership crossed into the Relay outbox even when it reports
                // that network delivery is deferred.
                owned = null;
                return sent
                    ? ProductionDirectDataRouteOutcome.RelaySent
                    : ProductionDirectDataRouteOutcome.RelayDeferred;
            }
            finally
            {
                if (owned != null) CryptographicOperations.ZeroMemory(owned);
            }
        }

        private static void TransferIngress(
            ProductionDirectDataRuntimeOptions runtimeOptions,
            Guid peerDeviceId,
            ProductionDirectIngressSourceV5 source,
            ProductionDirectPathFenceV5 path,
            byte[] exactEnvelope)
        {
            if (exactEnvelope == null || exactEnvelope.Length == 0)
                throw new InvalidOperationException("Ingress transfer has no exact Envelope.");
            var owned = exactEnvelope.ToArray();
            try
            {
                runtimeOptions.EnqueueIngress(new ProductionDirectDataIngressDelivery
                    {
                        PeerDeviceId = peerDeviceId,
                        Source = source,
                        Path = CopyPath(path),
                        ExactEnvelope = owned
                    });
                owned = null;
            }
            finally
            {
                if (owned != null) CryptographicOperations.ZeroMemory(owned);
            }
        }

        private void RemoveRetiredPeer(PeerActor actor)
        {
            ((ICollection<KeyValuePair<Guid, PeerActor>>)peers).Remove(
                new KeyValuePair<Guid, PeerActor>(actor.PeerDeviceId, actor));
        }

        private static bool TryReadPeer(
            TransportSignalEnvelopeV1 signal,
            Guid localDeviceId,
            out Guid peerDeviceId)
        {
            peerDeviceId = Guid.Empty;
            if (signal?.SenderDeviceId.Length != 16 || signal.RecipientDeviceId.Length != 16 ||
                signal.ConnectionId.Length != 16 || signal.PathEpoch == 0) return false;
            try
            {
                peerDeviceId = Guid.ParseExact(Convert.ToHexString(signal.SenderDeviceId.Span), "N");
                var recipient = Guid.ParseExact(
                    Convert.ToHexString(signal.RecipientDeviceId.Span), "N");
                if (recipient != localDeviceId) return false;
            }
            catch (FormatException) { return false; }
            return peerDeviceId != Guid.Empty;
        }

        private static bool IsCutoverSignal(TransportSignalTypeV1 type) =>
            (int)type is >= 6 and <= 9;
        private static bool IsCleanupSignal(TransportSignalTypeV1 type) =>
            type == TransportSignalTypeV1.PathClose;
        private static bool IsPrePromotionSignal(TransportSignalTypeV1 type) =>
            (int)type is >= 1 and <= 5 or >= 11 and <= 14;

        private static void ValidateEnvelopeIdentity(
            Guid peerDeviceId, Guid sessionId, ulong sequence, byte[] exactEnvelope)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty || sequence == 0 ||
                exactEnvelope?.Length is < 1 or >= 1024 * 1024)
                throw new ArgumentException("Direct Envelope identity is invalid.");
        }

        private static void ValidateRegistration(ProductionDirectDataPeerRegistration value)
        {
            if (value == null || value.PeerDeviceId == Guid.Empty || value.Path == null ||
                value.Path.PathEpoch == 0 || value.Path.ConnectionId == Guid.Empty ||
                value.Path.AttemptId == Guid.Empty || value.KeyEpoch < 1 ||
                value.AdmissionGeneration < 1 || value.PairwiseKey?.Length != 32 ||
                value.PairwiseKey.All(item => item == 0) || value.Native?.NativeAvailable != true ||
                value.SignalKeyId?.Length is < 1 or > 64 ||
                value.SignalKeyId.All(item => item == 0) ||
                value.InitialLocalSignalSequence > long.MaxValue ||
                value.InitialRemoteSignalSequence > long.MaxValue ||
                value.InitialRemoteSignalAcknowledgement > value.InitialLocalSignalSequence ||
                value.SignNativeTranscript == null)
                throw new ArgumentException("Direct peer registration is invalid.", nameof(value));
            if (value.PromotedNomination != null &&
                (value.PromotedNomination.Epoch != value.Path.PathEpoch ||
                    value.PromotedNomination.AttemptId != value.Path.AttemptId ||
                    value.PromotedNomination.CheckRoundId == Guid.Empty ||
                    value.PromotedNomination.NominationId == Guid.Empty ||
                    value.PromotedNomination.WinningPair == null ||
                    value.NominationCompleted == null || value.NominationFailed == null))
                throw new ArgumentException("Promoted Direct nomination is invalid.", nameof(value));
        }

        private static void ValidateExactEgressEnvelope(
            Guid localDeviceId,
            Guid peerDeviceId,
            Guid sessionId,
            ulong envelopeSequence,
            byte[] exactEnvelope,
            bool routeEligible)
        {
            Envelope envelope;
            try { envelope = Envelope.Parser.ParseFrom(exactEnvelope); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new ArgumentException("Exact Direct Envelope cannot be parsed.",
                    nameof(exactEnvelope), error);
            }
            if (envelope.ProtocolVersion != 1 ||
                envelope.Direction != Direction.PcToAndroid ||
                !TryReadEnvelopeGuid(envelope.SessionId.Span, out var encodedSession) ||
                encodedSession != sessionId || envelope.Seq != envelopeSequence ||
                !TryReadEnvelopeGuid(envelope.SenderDeviceId.Span, out var sender) ||
                sender != localDeviceId ||
                !TryReadEnvelopeGuid(envelope.RecipientDeviceId.Span, out var recipient) ||
                recipient != peerDeviceId ||
                routeEligible != (ProductionDirectRoutePolicy.Evaluate(
                    envelope.MessageType,
                    envelope.Direction) == ProductionDirectRouteEligibility.DirectEligible))
                throw new ArgumentException("Exact Direct Envelope metadata does not match its route.",
                    nameof(exactEnvelope));
        }

        private static bool TryReadEnvelopeGuid(ReadOnlySpan<byte> value, out Guid result)
        {
            result = Guid.Empty;
            if (value.Length != 16 || value.IndexOfAnyExcept((byte)0) < 0) return false;
            try { result = Guid.ParseExact(Convert.ToHexString(value), "N"); }
            catch (FormatException) { return false; }
            return result != Guid.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ProductionDirectDataRuntime));
        }

        private readonly struct SignalGate
        {
            internal SignalGate(bool connected, bool negotiated, long generation)
            {
                Connected = connected;
                Negotiated = negotiated;
                Generation = generation;
            }
            internal bool Connected { get; }
            internal bool Negotiated { get; }
            internal long Generation { get; }
            internal bool DataAvailable => Connected && Negotiated && Generation > 0;
        }

        private abstract class PeerCommand
        {
            internal abstract int OwnedBytes { get; }
            internal virtual bool OwnedDataCleared => true;
            internal abstract Task ExecuteAsync(PeerActor actor, CancellationToken token);
            internal virtual Task FailAsync(PeerActor actor, CancellationToken token) => Task.CompletedTask;
            internal virtual void ReleaseOwnedData() { }
        }

        private sealed class RouteEnvelopeCommand : PeerCommand
        {
            private byte[] envelope;
            private readonly int ownedBytes;
            private int nativeOwned;
            internal RouteEnvelopeCommand(Guid sessionId, ulong sequence, byte[] value, bool eligible)
            {
                SessionId = sessionId;
                Sequence = sequence;
                envelope = value.ToArray();
                ownedBytes = envelope.Length;
                Eligible = eligible;
            }
            internal override int OwnedBytes => ownedBytes;
            internal Guid SessionId { get; }
            internal ulong Sequence { get; }
            internal bool Eligible { get; }
            internal byte[] Envelope => envelope;
            internal override bool OwnedDataCleared => envelope == null;
            internal TaskCompletionSource<ProductionDirectDataRouteOutcome> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.RouteAsync(this, token);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                // A successful native route call is the exact ownership
                // boundary. Native cancellation/recovery must release that
                // copy; issuing a second managed Relay fallback here would
                // duplicate the same Envelope.
                if (Volatile.Read(ref nativeOwned) != 0)
                {
                    Complete(ProductionDirectDataRouteOutcome.RelayDeferred);
                    return Task.CompletedTask;
                }
                return actor.FallbackRouteAsync(this, token);
            }
            internal void MarkNativeOwned() => Interlocked.Exchange(ref nativeOwned, 1);
            internal void Complete(ProductionDirectDataRouteOutcome outcome)
            {
                Completion.TrySetResult(outcome);
                Zero();
            }
            internal void Zero()
            {
                if (envelope == null) return;
                CryptographicOperations.ZeroMemory(envelope);
                envelope = null;
            }
        }

        private sealed class IngressCommand : PeerCommand
        {
            private byte[] envelope;
            private readonly int ownedBytes;
            internal IngressCommand(
                ProductionDirectIngressSourceV5 source,
                ProductionDirectDataIngressMetadata metadata,
                byte[] value)
            {
                Source = source;
                Metadata = metadata;
                envelope = value.ToArray();
                ownedBytes = envelope.Length;
            }
            internal override int OwnedBytes => ownedBytes;
            internal ProductionDirectIngressSourceV5 Source { get; }
            internal ProductionDirectDataIngressMetadata Metadata { get; }
            internal byte[] Envelope => envelope;
            internal override bool OwnedDataCleared => envelope == null;
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.IngressAsync(this, token);
            internal override Task FailAsync(PeerActor actor, CancellationToken token) =>
                actor.FallbackIngressAsync(this, token);
            internal void Complete(bool value)
            {
                Completion.TrySetResult(value);
                if (envelope != null) CryptographicOperations.ZeroMemory(envelope);
                envelope = null;
            }
        }

        private sealed class CursorCommand : PeerCommand
        {
            internal CursorCommand(ProductionDirectDirectionV5 direction, Guid sessionId, ulong cursor)
            { Direction = direction; SessionId = sessionId; Cursor = cursor; }
            internal ProductionDirectDirectionV5 Direction { get; }
            internal Guid SessionId { get; }
            internal ulong Cursor { get; }
            internal override int OwnedBytes => 0;
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.UpdateCursorAsync(this);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                Completion.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private sealed class DeregisterSessionCommand : PeerCommand
        {
            internal DeregisterSessionCommand(
                ProductionDirectDirectionV5 direction,
                Guid sessionId)
            { Direction = direction; SessionId = sessionId; }
            internal ProductionDirectDirectionV5 Direction { get; }
            internal Guid SessionId { get; }
            internal override int OwnedBytes => 0;
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.DeregisterSessionAsync(this);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                Completion.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private sealed class IncomingSignalCommand : PeerCommand
        {
            internal IncomingSignalCommand(TransportSignalEnvelopeV1 signal, SignalGate gate)
            { Signal = signal; Gate = gate; }
            internal TransportSignalEnvelopeV1 Signal { get; }
            internal SignalGate Gate { get; }
            internal override int OwnedBytes => Signal.CalculateSize();
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.ReceiveSignalAsync(this, token);
        }

        private sealed class OutgoingSignalCommand : PeerCommand
        {
            internal OutgoingSignalCommand(TransportSignalEnvelopeV1 signal, SignalGate gate)
            { Signal = signal; Gate = gate; }
            internal TransportSignalEnvelopeV1 Signal { get; }
            internal SignalGate Gate { get; }
            internal override int OwnedBytes => Signal.CalculateSize();
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.SendSignalAsync(this);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                Completion.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private sealed class NetworkChangedCommand : PeerCommand
        {
            internal override int OwnedBytes => 0;
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.NetworkChangedAsync();
        }

        private sealed class StartCutoverCommand : PeerCommand
        {
            internal StartCutoverCommand(ProductionDirectCutoverProposal proposal) =>
                Proposal = proposal;
            internal ProductionDirectCutoverProposal Proposal { get; }
            internal override int OwnedBytes => Proposal.WireBytes.Length;
            internal override bool OwnedDataCleared => ProposalSecretsCleared(Proposal);
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.StartCutoverAsync(this);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                Completion.TrySetResult(false);
                return Task.CompletedTask;
            }
            internal override void ReleaseOwnedData() => ZeroProposal(Proposal);
        }

        private sealed class StartOwnedCutoverCommand : PeerCommand
        {
            internal StartOwnedCutoverCommand(
                IReadOnlyList<ProductionDirectSessionCutoverBarrier> sessionBarriers) =>
                SessionBarriers = sessionBarriers;
            internal IReadOnlyList<ProductionDirectSessionCutoverBarrier> SessionBarriers { get; }
            internal override int OwnedBytes => SessionBarriers.Count * 24;
            internal TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal override Task ExecuteAsync(PeerActor actor, CancellationToken token) =>
                actor.StartOwnedCutoverAsync(this);
            internal override Task FailAsync(PeerActor actor, CancellationToken token)
            {
                Completion.TrySetResult(false);
                return Task.CompletedTask;
            }
        }

        private sealed class PeerActor
        {
            private readonly ProductionDirectDataRuntimeOptions options;
            private readonly Action<PeerActor> onRetired;
            private readonly IProductionDirectDataNativePeer native;
            private readonly IProductionDirectShadowSignaling signaling;
            private readonly Func<byte[], byte[]> signNativeTranscript;
            private readonly Func<SignalGate> currentSignalGate;
            private readonly Channel<PeerCommand> mailbox;
            private readonly CancellationTokenSource lifetime = new();
            private readonly ProductionDirectPathFenceV5 path;
            private readonly Guid localDeviceId;
            private readonly byte[] signalKeyId;
            private ProductionDirectCommitRequestV4 nomination;
            private Action<byte[]> nominationCompleted;
            private Action<string> nominationFailed;
            private readonly Dictionary<Guid, CutoverBarrierState> cutovers = new();
            // A committed direction no longer owns an active cutover barrier,
            // but its compact authenticated fallback state must survive for
            // the lifetime of that Direct carrier. Without it, a later native
            // or peer fallback could not map the opaque barrier ID back to its
            // independently committed direction.
            private readonly Dictionary<Guid, CutoverBarrierState> committedCutovers = new();
            private readonly Dictionary<Guid, ProductionDirectCarrierV5>
                pcToAndroidSessionCarriers = new();
            private readonly Dictionary<Guid, ProductionDirectCarrierV5>
                androidToPcSessionCarriers = new();
            private byte[] pairwiseKey;
            private byte[] localPathBinding;
            private byte[] peerPathBinding;
            // The current ticket generation is the renewal proof for the
            // server. Guarded by ticketSync; replaced only after the native
            // peer accepts a renewed ticket whose expiry slides forward.
            private readonly object ticketSync = new();
            private byte[] currentTicket;
            private long renewalInFlight;
            private Task worker;
            private ulong localSignalSequence;
            private ulong remoteSignalSequence;
            private ulong remoteSignalAcknowledgement;
            private ProductionDirectCarrierV5 pcToAndroidCarrier = ProductionDirectCarrierV5.Relay;
            private ProductionDirectCarrierV5 androidToPcCarrier = ProductionDirectCarrierV5.Relay;
            private string lastFault = string.Empty;
            private long mailboxBytes;
            private bool nominationAckSent;
            private bool nominationResultSent;
            private bool nominationCompletionNotified;
            private bool nominationFailureNotified;
            private int activeCutoverCount;
            private int committedCutoverCount;
            private int ownedCommandCleanupFailed;
            private int retired;
            private long directFramesSent;
            private long directFramesReceived;
            private long transportReceiptsSent;
            private long transportReceiptsReceived;
            private long transportReceiptHighWatermark;
            private long lastReceivedEnvelopeFrameSequence;
            private long ingressHoldCount;
            private long ingressDropCount;
            private long relayReplayEnvelopeCount;
            private long relayReplayBytes;
            private long fallbackCount;
            private string lastFallbackReasonCode = string.Empty;
            private long nativeQueueLimitCount;
            // Mutable carrier/cutover state is actor-confined. External
            // diagnostics read this immutable publication rather than
            // enumerating actor-owned dictionaries concurrently.
            private ProductionDirectDataPeerSnapshot publishedSnapshot;

            internal PeerActor(
                Guid localDeviceId,
                IProductionDirectShadowSignaling signaling,
                Func<SignalGate> currentSignalGate,
                ProductionDirectDataPeerRegistration registration,
                ProductionDirectDataRuntimeOptions options,
                Action<PeerActor> onRetired)
            {
                this.localDeviceId = localDeviceId;
                this.signaling = signaling;
                this.currentSignalGate = currentSignalGate;
                PeerDeviceId = registration.PeerDeviceId;
                path = CopyPath(registration.Path);
                KeyEpoch = registration.KeyEpoch;
                AdmissionGeneration = registration.AdmissionGeneration;
                localSignalSequence = registration.InitialLocalSignalSequence;
                remoteSignalSequence = registration.InitialRemoteSignalSequence;
                remoteSignalAcknowledgement = registration.InitialRemoteSignalAcknowledgement;
                pairwiseKey = registration.PairwiseKey.ToArray();
                signalKeyId = registration.SignalKeyId.ToArray();
                currentTicket = registration.TicketWireBytes?.ToArray();
                native = registration.Native;
                signNativeTranscript = registration.SignNativeTranscript;
                nomination = ProductionDirectDataPromotionHandoff.CopyNomination(
                    registration.PromotedNomination);
                nominationCompleted = registration.NominationCompleted;
                nominationFailed = registration.NominationFailed;
                this.options = options;
                this.onRetired = onRetired;
                mailbox = Channel.CreateBounded<PeerCommand>(new BoundedChannelOptions(
                    options.PeerMailboxCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                publishedSnapshot = BuildStateSnapshot();
            }

            internal Guid PeerDeviceId { get; }
            internal ulong PathEpoch => path.PathEpoch;
            internal ProductionDirectPathFenceV5 Path => CopyPath(path);
            internal bool MatchesPath(ProductionDirectPathFenceV5 expectedPath) =>
                expectedPath != null && path.Equals(expectedPath);
            internal long KeyEpoch { get; }
            internal long AdmissionGeneration { get; }
            internal ProductionDirectDataPeerSnapshot Snapshot =>
                WithLiveCounters(Volatile.Read(ref publishedSnapshot));

            private ProductionDirectDataPeerSnapshot BuildStateSnapshot() => new()
            {
                PeerDeviceId = PeerDeviceId,
                Path = CopyPath(path),
                KeyEpoch = KeyEpoch,
                AdmissionGeneration = AdmissionGeneration,
                PcToAndroidCarrier = pcToAndroidCarrier,
                AndroidToPcCarrier = androidToPcCarrier,
                ReadyForSessionCutover = nominationCompletionNotified &&
                    localPathBinding?.Length == 32,
                PcToAndroidSessionCarriers = new ReadOnlyDictionary<Guid, ProductionDirectCarrierV5>(
                    new Dictionary<Guid, ProductionDirectCarrierV5>(pcToAndroidSessionCarriers)),
                AndroidToPcSessionCarriers = new ReadOnlyDictionary<Guid, ProductionDirectCarrierV5>(
                    new Dictionary<Guid, ProductionDirectCarrierV5>(androidToPcSessionCarriers)),
                CommittedDirect = pcToAndroidCarrier != ProductionDirectCarrierV5.Relay ||
                    androidToPcCarrier != ProductionDirectCarrierV5.Relay,
                ActiveCutoverCount = Volatile.Read(ref activeCutoverCount),
                CommittedCutoverCount = Volatile.Read(ref committedCutoverCount),
                DirectFramesSent = ReadCounter(ref directFramesSent),
                DirectFramesReceived = ReadCounter(ref directFramesReceived),
                TransportReceiptsSent = ReadCounter(ref transportReceiptsSent),
                TransportReceiptsReceived = ReadCounter(ref transportReceiptsReceived),
                TransportReceiptHighWatermark = ReadCounter(
                    ref transportReceiptHighWatermark),
                LastReceivedEnvelopeFrameSequence = ReadCounter(
                    ref lastReceivedEnvelopeFrameSequence),
                IngressHoldCount = ReadCounter(ref ingressHoldCount),
                IngressDropCount = ReadCounter(ref ingressDropCount),
                // The frozen v5 action record reports only HOLD/DROP and the
                // envelope identity. It carries no duplicate/conflict/gap
                // discriminator, so publishing zero for these would be a
                // false measurement.
                IngressDuplicateDropCount = null,
                IngressDigestConflictCount = null,
                IngressGapCount = null,
                RelayReplayEnvelopeCount = ReadCounter(ref relayReplayEnvelopeCount),
                RelayReplayBytes = ReadCounter(ref relayReplayBytes),
                FallbackCount = ReadCounter(ref fallbackCount),
                LastFallbackReasonCode = Volatile.Read(ref lastFallbackReasonCode) ?? string.Empty,
                NativeQueueLimitCount = ReadCounter(ref nativeQueueLimitCount),
                PeerMailboxBytes = ReadCounter(ref mailboxBytes),
                SensitiveStateCleared = SensitiveStateIsCleared(),
                OwnedCommandDataCleared = Volatile.Read(ref ownedCommandCleanupFailed) == 0,
                Retired = Volatile.Read(ref retired) != 0,
                LastFault = lastFault
            };

            private ProductionDirectDataPeerSnapshot WithLiveCounters(
                ProductionDirectDataPeerSnapshot baseline) => new()
            {
                PeerDeviceId = baseline.PeerDeviceId,
                Path = baseline.Path,
                KeyEpoch = baseline.KeyEpoch,
                AdmissionGeneration = baseline.AdmissionGeneration,
                PcToAndroidCarrier = baseline.PcToAndroidCarrier,
                AndroidToPcCarrier = baseline.AndroidToPcCarrier,
                ReadyForSessionCutover = baseline.ReadyForSessionCutover,
                PcToAndroidSessionCarriers = baseline.PcToAndroidSessionCarriers,
                AndroidToPcSessionCarriers = baseline.AndroidToPcSessionCarriers,
                CommittedDirect = baseline.CommittedDirect,
                ActiveCutoverCount = Volatile.Read(ref activeCutoverCount),
                CommittedCutoverCount = Volatile.Read(ref committedCutoverCount),
                DirectFramesSent = ReadCounter(ref directFramesSent),
                DirectFramesReceived = ReadCounter(ref directFramesReceived),
                TransportReceiptsSent = ReadCounter(ref transportReceiptsSent),
                TransportReceiptsReceived = ReadCounter(ref transportReceiptsReceived),
                TransportReceiptHighWatermark = ReadCounter(
                    ref transportReceiptHighWatermark),
                LastReceivedEnvelopeFrameSequence = ReadCounter(
                    ref lastReceivedEnvelopeFrameSequence),
                IngressHoldCount = ReadCounter(ref ingressHoldCount),
                IngressDropCount = ReadCounter(ref ingressDropCount),
                IngressDuplicateDropCount = null,
                IngressDigestConflictCount = null,
                IngressGapCount = null,
                RelayReplayEnvelopeCount = ReadCounter(ref relayReplayEnvelopeCount),
                RelayReplayBytes = ReadCounter(ref relayReplayBytes),
                FallbackCount = ReadCounter(ref fallbackCount),
                LastFallbackReasonCode = Volatile.Read(ref lastFallbackReasonCode) ?? string.Empty,
                NativeQueueLimitCount = ReadCounter(ref nativeQueueLimitCount),
                PeerMailboxBytes = ReadCounter(ref mailboxBytes),
                SensitiveStateCleared = baseline.SensitiveStateCleared,
                OwnedCommandDataCleared = Volatile.Read(ref ownedCommandCleanupFailed) == 0,
                Retired = Volatile.Read(ref retired) != 0,
                LastFault = Volatile.Read(ref lastFault) ?? string.Empty
            };

            internal void Start() => worker = RunAsync(lifetime.Token);
            internal bool TryPost(PeerCommand command)
            {
                if (command == null || Volatile.Read(ref retired) != 0 ||
                    !TryReserveBytes(command.OwnedBytes)) return false;
                if (Volatile.Read(ref retired) == 0 && mailbox.Writer.TryWrite(command)) return true;
                ReleaseBytes(command.OwnedBytes);
                return false;
            }

            internal void ObserveCommandCleanup(PeerCommand command)
            {
                if (command?.OwnedDataCleared != true)
                    Interlocked.Exchange(ref ownedCommandCleanupFailed, 1);
            }

            internal async Task DisposeAsync(string reason)
            {
                if (Interlocked.Exchange(ref retired, 1) != 0)
                {
                    if (worker != null) await worker.ConfigureAwait(false);
                    return;
                }
                lastFault = NormalizeReason(reason);
                mailbox.Writer.TryComplete();
                // A self-retiring worker can finish and dispose its token at
                // the same boundary as an authority teardown. Disposal is
                // idempotent even when that finalization wins the race.
                try { lifetime.Cancel(); }
                catch (ObjectDisposedException) { }
                if (worker != null)
                {
                    try { await worker.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }

            internal async Task RouteAsync(RouteEnvelopeCommand command, CancellationToken token)
            {
                ProductionDirectNativeResultV5 result;
                try
                {
                    result = native.RouteEnvelope(new ProductionDirectRouteEnvelopeRequestV5
                    {
                        Path = path,
                        Direction = ProductionDirectDirectionV5.PcToAndroid,
                        RouteEligible = command.Eligible,
                        SessionId = command.SessionId,
                        EnvelopeSequence = command.Sequence,
                        NowMilliseconds = checked((ulong)Math.Max(0, options.ClockMilliseconds()))
                    }, command.Envelope);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    await FallbackRouteAsync(command, token).ConfigureAwait(false);
                    throw;
                }
                if (result != ProductionDirectNativeResultV5.Accepted)
                {
                    await FallbackRouteAsync(command, token).ConfigureAwait(false);
                    throw new PeerRetiredException("DIRECT_ROUTE_REJECTED");
                }
                command.MarkNativeOwned();
                ProductionDirectDataRouteOutcome? routedBy;
                try
                {
                    routedBy = await DrainNativeAsync(
                        command.SessionId,
                        command.Sequence).ConfigureAwait(false);
                }
                catch (RelayOwnershipTransferException error) when (error.Matches(
                    PeerDeviceId, command.SessionId, command.Sequence))
                {
                    // Native accepted the exact Envelope but its Relay action
                    // lost the capacity race before Relay accepted ownership.
                    // Never resend the action payload (it is zeroed by the
                    // action boundary); let the durable caller retry instead.
                    command.Complete(ProductionDirectDataRouteOutcome.Dropped);
                    throw;
                }
                // Do not release this managed safety copy until the first
                // native action/event drain succeeds. If the boundary throws,
                // FailAsync still has the exact current Envelope for Relay.
                command.Complete(routedBy ?? ProductionDirectDataRouteOutcome.DirectAccepted);
            }

            internal Task FallbackRouteAsync(RouteEnvelopeCommand command, CancellationToken token)
            {
                if (command.Envelope == null) return Task.CompletedTask;
                var outcome = ProductionDirectDataRouteOutcome.Dropped;
                try
                {
                    outcome = TransferRelay(
                        options,
                        PeerDeviceId,
                        command.SessionId,
                        command.Sequence,
                        command.Envelope);
                }
                finally
                {
                    command.Complete(outcome);
                }
                return Task.CompletedTask;
            }

            internal async Task IngressAsync(IngressCommand command, CancellationToken token)
            {
                ProductionDirectNativeResultV5 result;
                try
                {
                    result = native.ArbitrateIngress(new ProductionDirectIngressRequestV5
                    {
                        Path = path,
                        Direction = ProductionDirectDirectionV5.AndroidToPc,
                        Source = command.Source,
                        SessionId = command.Metadata.SessionId,
                        EnvelopeSequence = command.Metadata.EnvelopeSequence,
                        NowMilliseconds = checked((ulong)Math.Max(0, options.ClockMilliseconds())),
                        DuplicateGraceMilliseconds = options.DuplicateGraceMilliseconds
                    }, command.Envelope);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    await FallbackIngressAsync(command, token).ConfigureAwait(false);
                    throw;
                }
                if (result != ProductionDirectNativeResultV5.Accepted)
                {
                    await FallbackIngressAsync(command, token).ConfigureAwait(false);
                    throw new PeerRetiredException("DIRECT_INGRESS_REJECTED");
                }
                await DrainNativeAsync().ConfigureAwait(false);
                command.Complete(true);
            }

            internal Task FallbackIngressAsync(IngressCommand command, CancellationToken token)
            {
                if (command.Envelope == null) return Task.CompletedTask;
                if (command.Source != ProductionDirectIngressSourceV5.Relay)
                {
                    command.Complete(false);
                    return Task.CompletedTask;
                }
                try
                {
                    TransferIngress(
                        options,
                        PeerDeviceId,
                        command.Source,
                        path,
                        command.Envelope);
                    command.Complete(true);
                }
                catch
                {
                    command.Complete(false);
                    throw;
                }
                return Task.CompletedTask;
            }

            internal async Task UpdateCursorAsync(CursorCommand command)
            {
                var valid = command.Direction is ProductionDirectDirectionV5.PcToAndroid or
                    ProductionDirectDirectionV5.AndroidToPc && command.SessionId != Guid.Empty;
                var result = valid && native.UpdateAppliedCursor(new ProductionDirectCursorUpdateV5
                {
                    Path = path,
                    Direction = command.Direction,
                    SessionId = command.SessionId,
                    HighestContiguous = command.Cursor
                }) == ProductionDirectNativeResultV5.Accepted;
                if (!result)
                {
                    command.Completion.TrySetResult(false);
                    return;
                }
                // Cursor and carrier actions share one native owner. Drain
                // before acknowledging the serialized command so a successful
                // caller observes the committed carrier and released barrier.
                await DrainNativeAsync().ConfigureAwait(false);
                command.Completion.TrySetResult(true);
            }

            internal async Task DeregisterSessionAsync(DeregisterSessionCommand command)
            {
                var result = native.DeregisterSession(new ProductionDirectSessionDeregisterV5
                {
                    Path = path,
                    Direction = command.Direction,
                    SessionId = command.SessionId
                });
                if (result != ProductionDirectNativeResultV5.Accepted)
                {
                    command.Completion.TrySetResult(false);
                    return;
                }
                await DrainNativeAsync().ConfigureAwait(false);
                ForgetCommittedSession(command.Direction, command.SessionId);
                command.Completion.TrySetResult(true);
            }

            internal Task StartCutoverAsync(StartCutoverCommand command)
            {
                try
                {
                    var supplied = command.Proposal;
                    if (!ProductionDirectCutoverCodec.TryParseProposal(
                            supplied.WireBytes,
                            path.ConnectionId,
                            path.PathEpoch,
                            Direction.PcToAndroid,
                            out var proposal) ||
                        supplied.BarrierId != proposal.BarrierId ||
                        supplied.BarrierSha256?.Length != 32 ||
                        !CryptographicOperations.FixedTimeEquals(
                            supplied.BarrierSha256, proposal.BarrierSha256))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    if (!nominationCompletionNotified || localPathBinding?.Length != 32 ||
                        proposal.Direction != Direction.PcToAndroid ||
                        !CryptographicOperations.FixedTimeEquals(
                            proposal.DirectPathBinding, localPathBinding) ||
                        cutovers.Values.Any(value =>
                            value.Direction == ProductionDirectDirectionV5.PcToAndroid))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    var state = CutoverBarrierState.FromProposal(
                        proposal,
                        ProductionDirectDirectionV5.PcToAndroid,
                        locallyProposed: true);
                    if (!cutovers.TryAdd(state.BarrierId, state))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    Interlocked.Increment(ref activeCutoverCount);
                    var result = native.StartProposal(ProposalRequest(proposal, state.Direction));
                    if (IsBatchLocalTransitionRefusal(result))
                    {
                        RemoveCutover(state.BarrierId);
                        command.Completion.TrySetResult(false);
                        Publish();
                        return Task.CompletedTask;
                    }
                    if (result != ProductionDirectNativeResultV5.Accepted)
                        throw new PeerRetiredException("DIRECT_START_PROPOSAL_REJECTED");
                    var plaintext = ProductionDirectCutoverSignalCodec.CreateProposal(proposal);
                    try
                    {
                        // The native negotiation remains authoritative until
                        // its normal timeout when signalling is temporarily
                        // unavailable.  Keeping the actor alive preserves
                        // already committed sessions and lets the timeout
                        // return only this batch to Relay.
                        if (!TrySendCutoverEncrypted(
                                TransportSignalTypeV1.CutoverProposal, plaintext))
                        {
                            command.Completion.TrySetResult(false);
                            Publish();
                            return Task.CompletedTask;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                    command.Completion.TrySetResult(true);
                    return Task.CompletedTask;
                }
                catch
                {
                    command.Completion.TrySetResult(false);
                    throw;
                }
            }

            internal Task StartOwnedCutoverAsync(StartOwnedCutoverCommand command)
            {
                ProductionDirectCutoverProposal proposal = null;
                try
                {
                    if (!nominationCompletionNotified || localPathBinding?.Length != 32 ||
                        cutovers.Values.Any(value =>
                            value.Direction == ProductionDirectDirectionV5.PcToAndroid))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    proposal = ProductionDirectCutoverCodec.CreateProposal(
                        path.ConnectionId,
                        path.PathEpoch,
                        Direction.PcToAndroid,
                        Guid.NewGuid(),
                        localPathBinding,
                        command.SessionBarriers,
                        options.DuplicateGraceMilliseconds);
                    var state = CutoverBarrierState.FromProposal(
                        proposal,
                        ProductionDirectDirectionV5.PcToAndroid,
                        locallyProposed: true);
                    if (!cutovers.TryAdd(state.BarrierId, state))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    Interlocked.Increment(ref activeCutoverCount);
                    var startResult = native.StartProposal(
                        ProposalRequest(proposal, state.Direction));
                    if (IsBatchLocalTransitionRefusal(startResult))
                    {
                        RemoveCutover(state.BarrierId);
                        command.Completion.TrySetResult(false);
                        Publish();
                        return Task.CompletedTask;
                    }
                    if (startResult != ProductionDirectNativeResultV5.Accepted)
                        throw new PeerRetiredException("DIRECT_START_PROPOSAL_REJECTED");
                    var plaintext = ProductionDirectCutoverSignalCodec.CreateProposal(proposal);
                    try
                    {
                        if (!TrySendCutoverEncrypted(
                                TransportSignalTypeV1.CutoverProposal, plaintext))
                        {
                            command.Completion.TrySetResult(false);
                            Publish();
                            return Task.CompletedTask;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                    command.Completion.TrySetResult(true);
                    return Task.CompletedTask;
                }
                catch
                {
                    command.Completion.TrySetResult(false);
                    throw;
                }
                finally { ZeroProposal(proposal); }
            }

            internal async Task ReceiveSignalAsync(IncomingSignalCommand command, CancellationToken token)
            {
                var signal = command.Signal;
                ValidateSignalFence(signal, inbound: true, command.Gate);
                if (IsCleanupSignal(signal.SignalType))
                {
                    remoteSignalSequence = signal.SignalSeq;
                    remoteSignalAcknowledgement = signal.SignalAck;
                    if (options.HandleSignalAsync != null)
                        await ObserveSignalAsync(signal, token).ConfigureAwait(false);
                    throw new PeerRetiredException("PEER_PATH_CLOSED");
                }

                if (nomination != null &&
                    signal.SignalType == TransportSignalTypeV1.NominationResultV2)
                    HandleNominationResult(signal);
                else if (IsCutoverSignal(signal.SignalType) && options.HandleSignalAsync == null)
                    HandleCutoverSignal(signal, command.Gate);
                else if (options.HandleSignalAsync != null)
                    await ObserveSignalAsync(signal, token).ConfigureAwait(false);
                else
                    throw new PeerRetiredException("SIGNAL_TYPE_UNEXPECTED");

                remoteSignalSequence = signal.SignalSeq;
                remoteSignalAcknowledgement = signal.SignalAck;
                await DrainNativeAsync().ConfigureAwait(false);
            }

            internal Task SendSignalAsync(OutgoingSignalCommand command)
            {
                var signal = command.Signal;
                try
                {
                    ValidateSignalFence(signal, inbound: false, command.Gate);
                    if (!signaling.TrySend(signal))
                    {
                        command.Completion.TrySetResult(false);
                        return Task.CompletedTask;
                    }
                    localSignalSequence = signal.SignalSeq;
                    command.Completion.TrySetResult(true);
                }
                catch
                {
                    command.Completion.TrySetResult(false);
                    throw;
                }
                return Task.CompletedTask;
            }

            private async Task ObserveSignalAsync(
                TransportSignalEnvelopeV1 signal,
                CancellationToken token)
            {
                var key = pairwiseKey.ToArray();
                try
                {
                    await options.HandleSignalAsync(PeerDeviceId, signal, key, token)
                        .ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }

            private void HandleNominationResult(TransportSignalEnvelopeV1 signal)
            {
                var plaintext = ProductionDirectTransportCrypto.DecryptSignal(pairwiseKey, signal);
                try
                {
                    if (!ProductionDirectCutoverSignalCodec.TryParseNominationResult(
                            plaintext, nomination, out var binding))
                        throw new PeerRetiredException("DIRECT_NOMINATION_RESULT_INVALID");
                    if (peerPathBinding != null)
                    {
                        CryptographicOperations.ZeroMemory(binding);
                        throw new PeerRetiredException("DIRECT_NOMINATION_RESULT_DUPLICATE");
                    }
                    peerPathBinding = binding;
                    TryCompleteNomination();
                    Publish();
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            private void HandleCutoverSignal(
                TransportSignalEnvelopeV1 signal,
                SignalGate gate)
            {
                var plaintext = ProductionDirectTransportCrypto.DecryptSignal(pairwiseKey, signal);
                try
                {
                    switch (signal.SignalType)
                    {
                        case TransportSignalTypeV1.CutoverProposal:
                            ReceiveCutoverProposal(plaintext);
                            break;
                        case TransportSignalTypeV1.CutoverAccept:
                            ReceiveCutoverAccept(plaintext);
                            break;
                        case TransportSignalTypeV1.CutoverCommit:
                            ReceiveCutoverCommit(plaintext);
                            break;
                        case TransportSignalTypeV1.Fallback:
                            ReceiveFallback(plaintext, gate);
                            break;
                        default:
                            throw new PeerRetiredException("DIRECT_CUTOVER_SIGNAL_INVALID");
                    }
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            private void ReceiveCutoverProposal(ReadOnlySpan<byte> plaintext)
            {
                if (!nominationCompletionNotified || localPathBinding?.Length != 32 ||
                    !ProductionDirectCutoverSignalCodec.TryParseProposal(
                        plaintext,
                        path.ConnectionId,
                        path.PathEpoch,
                        Direction.AndroidToPc,
                        out var proposal))
                    throw new PeerRetiredException("DIRECT_CUTOVER_PROPOSAL_INVALID");
                var state = CutoverBarrierState.FromProposal(
                    proposal,
                    ProductionDirectDirectionV5.AndroidToPc,
                    locallyProposed: false);
                if (!cutovers.TryAdd(state.BarrierId, state))
                    throw new PeerRetiredException("DIRECT_CUTOVER_BARRIER_DUPLICATE");
                Interlocked.Increment(ref activeCutoverCount);
                var result = native.ReceiveProposal(ProposalRequest(proposal, state.Direction));
                if (IsBatchLocalTransitionRefusal(result))
                {
                    // A valid late/incremental proposal can race another
                    // batch or overlap a still-active session.  Native leaves
                    // the established direction untouched.  Drop only this
                    // managed proposal and let the peer time it out rather
                    // than retiring healthy Direct sessions.
                    RemoveCutover(state.BarrierId);
                    Publish();
                    return;
                }
                if (result != ProductionDirectNativeResultV5.Accepted)
                    throw new PeerRetiredException("DIRECT_RECEIVE_PROPOSAL_REJECTED");
            }

            private void ReceiveCutoverAccept(ReadOnlySpan<byte> plaintext)
            {
                if (!ProductionDirectCutoverSignalCodec.TryParseAccept(plaintext, out var value) ||
                    !TryCutover(value.BarrierId, value.BarrierSha256, out var state) ||
                    !state.LocallyProposed ||
                    state.Direction != ProductionDirectDirectionV5.PcToAndroid)
                    throw new PeerRetiredException("DIRECT_CUTOVER_ACCEPT_INVALID");
                var result = native.ReceiveAccept(TransitionRequest(
                    state,
                    accepted: value.Accepted,
                    isEcho: false));
                if (result != ProductionDirectNativeResultV5.Accepted)
                    throw new PeerRetiredException("DIRECT_RECEIVE_ACCEPT_REJECTED");
                if (!value.Accepted) RemoveCutover(state.BarrierId);
            }

            private void ReceiveCutoverCommit(ReadOnlySpan<byte> plaintext)
            {
                if (!ProductionDirectCutoverSignalCodec.TryParseCommit(plaintext, out var value) ||
                    !TryCutover(value.BarrierId, value.BarrierSha256, out var state))
                    throw new PeerRetiredException("DIRECT_CUTOVER_COMMIT_INVALID");
                var result = native.ReceiveCommit(TransitionRequest(
                    state,
                    accepted: false,
                    isEcho: state.LocallyProposed));
                if (result != ProductionDirectNativeResultV5.Accepted)
                    throw new PeerRetiredException("DIRECT_RECEIVE_COMMIT_REJECTED");
            }

            private void ReceiveFallback(ReadOnlySpan<byte> plaintext, SignalGate gate)
            {
                if (!ProductionDirectCutoverSignalCodec.TryParseFallback(plaintext, out var value))
                    throw new PeerRetiredException("DIRECT_FALLBACK_INVALID");
                var committedFallback = committedCutovers.TryGetValue(
                    value.BarrierId, out var state);
                if (!committedFallback && !cutovers.TryGetValue(value.BarrierId, out state))
                {
                    // An authenticated cleanup from an older capability
                    // generation can legitimately refer to a batch already
                    // retired locally.  It owns no routing authority and is
                    // therefore safe to consume as an idempotent no-op.
                    if (gate.Generation != AdmissionGeneration) return;
                    throw new PeerRetiredException("DIRECT_FALLBACK_INVALID");
                }
                if (!committedFallback &&
                    (gate.Generation != AdmissionGeneration || !gate.DataAvailable))
                {
                    // A negotiating barrier still depends on the exact
                    // generation/capability admission that created it.  A
                    // newer signalling lane cannot cancel that batch.
                    return;
                }
                var result = native.ReceiveFallback(TransitionRequest(
                    state,
                    accepted: false,
                    isEcho: false));
                if (IsBatchLocalTransitionRefusal(result))
                {
                    if (!committedFallback) RemoveCutover(state.BarrierId);
                    Publish();
                    return;
                }
                if (result != ProductionDirectNativeResultV5.Accepted)
                    throw new PeerRetiredException("DIRECT_RECEIVE_FALLBACK_REJECTED");
                RecordFallback(value.ReasonCode);
                RemoveCutover(state.BarrierId);
                if (committedFallback)
                {
                    foreach (var committed in committedCutovers.Values
                        .Where(value => value.Direction == state.Direction).ToArray())
                        RemoveCommittedCutover(committed.BarrierId);
                }
                else
                {
                    RemoveCommittedCutover(state.BarrierId);
                }
                Publish();
            }

            internal Task NetworkChangedAsync()
            {
                var result = native.NetworkChanged(path);
                if (result != ProductionDirectNativeResultV5.Accepted &&
                    result != ProductionDirectNativeResultV5.Cancelled)
                    throw new InvalidOperationException("DIRECT_NETWORK_CHANGE_FAILED");
                throw new PeerRetiredException("NETWORK_CHANGED");
            }

            private async Task RunAsync(CancellationToken token)
            {
                try
                {
                    if (nomination != null)
                    {
                        SendNominationAck();
                        await DrainNativeAsync().ConfigureAwait(false);
                        TryObservePathReady();
                    }
                    while (!token.IsCancellationRequested)
                    {
                        while (mailbox.Reader.TryRead(out var command))
                        {
                            try
                            {
                                await command.ExecuteAsync(this, token).ConfigureAwait(false);
                            }
                            catch (PeerRetiredException error)
                            {
                                lastFault = NormalizeReason(error.Message);
                                await command.FailAsync(this, CancellationToken.None).ConfigureAwait(false);
                                return;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                await command.FailAsync(this, CancellationToken.None).ConfigureAwait(false);
                                return;
                            }
                            catch (Exception error) when (error is not OutOfMemoryException)
                            {
                                lastFault = NormalizeReason(error.Message);
                                try { await command.FailAsync(this, CancellationToken.None).ConfigureAwait(false); }
                                catch (Exception fallbackError) when (fallbackError is not OutOfMemoryException) { }
                                return;
                            }
                            finally
                            {
                                command.ReleaseOwnedData();
                                ObserveCommandCleanup(command);
                                ReleaseBytes(command.OwnedBytes);
                            }
                        }

                        var waiting = mailbox.Reader.WaitToReadAsync(token).AsTask();
                        var tick = Task.Delay(TimeSpan.FromMilliseconds(50), token);
                        var completed = await Task.WhenAny(waiting, tick).ConfigureAwait(false);
                        if (completed == waiting)
                        {
                            if (!await waiting.ConfigureAwait(false)) break;
                            continue;
                        }

                        var now = checked((ulong)Math.Max(0, options.ClockMilliseconds()));
                        TickCutovers(now);
                        var tickResult = native.TickIngress(path, now);
                        if (tickResult is not ProductionDirectNativeResultV5.Accepted and
                            not ProductionDirectNativeResultV5.NoData and
                            not ProductionDirectNativeResultV5.NotReady)
                            throw new PeerRetiredException("DIRECT_TICK_FAILED");
                        await DrainNativeAsync().ConfigureAwait(false);
                        if (nomination != null && !nominationCompletionNotified)
                            TryObservePathReady();
                    }
                }
                catch (PeerRetiredException error)
                {
                    lastFault = NormalizeReason(error.Message);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lastFault = NormalizeReason(error.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref retired, 1);
                    mailbox.Writer.TryComplete();
                    while (mailbox.Reader.TryRead(out var pending))
                    {
                        try { await pending.FailAsync(this, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                        finally
                        {
                            pending.ReleaseOwnedData();
                            ObserveCommandCleanup(pending);
                            ReleaseBytes(pending.OwnedBytes);
                        }
                    }
                    try { native.Cancel(path); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    try { DrainRecoveryActions(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    try { native.Dispose(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    if (pairwiseKey != null) CryptographicOperations.ZeroMemory(pairwiseKey);
                    pairwiseKey = null;
                    CryptographicOperations.ZeroMemory(signalKeyId);
                    if (localPathBinding != null)
                        CryptographicOperations.ZeroMemory(localPathBinding);
                    localPathBinding = null;
                    lock (ticketSync)
                    {
                        if (currentTicket != null)
                            CryptographicOperations.ZeroMemory(currentTicket);
                        currentTicket = null;
                    }
                    foreach (var cutover in cutovers.Values)
                        CryptographicOperations.ZeroMemory(cutover.BarrierSha256);
                    cutovers.Clear();
                    Interlocked.Exchange(ref activeCutoverCount, 0);
                    foreach (var committed in committedCutovers.Values)
                        CryptographicOperations.ZeroMemory(committed.BarrierSha256);
                    committedCutovers.Clear();
                    Interlocked.Exchange(ref committedCutoverCount, 0);
                    NotifyNominationFailed(lastFault);
                    ClearNominationArtifacts();
                    pcToAndroidCarrier = ProductionDirectCarrierV5.Relay;
                    androidToPcCarrier = ProductionDirectCarrierV5.Relay;
                    pcToAndroidSessionCarriers.Clear();
                    androidToPcSessionCarriers.Clear();
                    Publish();
                    onRetired(this);
                    lifetime.Dispose();
                }
            }

            /// <summary>
        /// Single-flight entry from the RenewalDue native event. The FFI emits
        /// the event once per ticket generation, so the actor owns the retry
        /// loop: fetch → apply → on accepted, the stored proof slides to the
        /// new generation; on any failure, retry with backoff until the path
        /// retires and let the native hard stop terminate the carrier.
        /// </summary>
        private void KickTicketRenewal()
        {
            if (options.RenewTicketAsync == null) return;
            if (Interlocked.CompareExchange(ref renewalInFlight, 1, 0) != 0) return;
            byte[] proof;
            lock (ticketSync) proof = currentTicket?.ToArray();
            if (proof == null)
            {
                Interlocked.Exchange(ref renewalInFlight, 0);
                return;
            }
            var _ = RenewTicketLoopAsync(proof);
        }

        private async Task RenewTicketLoopAsync(byte[] proof)
        {
            try
            {
                // Renewal opens at expiry minus 60s and the native hard stop
                // lands at expiry plus 30s, so the whole budget is ~90s. The
                // backoff keeps attempts inside that window without hammering
                // the server: 1s, 2s, 4s, then 8s floors.
                var backoffMilliseconds = 1_000;
                while (Volatile.Read(ref retired) == 0)
                {
                    byte[] renewed = null;
                    try
                    {
                        renewed = await options.RenewTicketAsync(
                                PeerDeviceId, path.ConnectionId, proof, lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error) when (
                        error is not OutOfMemoryException &&
                        error is not OperationCanceledException)
                    {
                        renewed = null;
                    }
                    if (renewed != null)
                    {
                        var status = native.RenewApplyTicket(path, renewed);
                        if (status == ProductionDirectNativeResultV5.Accepted)
                        {
                            lock (ticketSync)
                            {
                                currentTicket = renewed;
                            }
                            return;
                        }
                        renewed = null;
                    }
                    if (Volatile.Read(ref retired) != 0) return;
                    await Task.Delay(backoffMilliseconds, lifetime.Token)
                        .ConfigureAwait(false);
                    backoffMilliseconds = Math.Min(backoffMilliseconds * 2, 8_000);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally
            {
                Interlocked.Exchange(ref renewalInFlight, 0);
            }
        }

        private Task<ProductionDirectDataRouteOutcome?> DrainNativeAsync(
                Guid routedSessionId = default,
                ulong routedSequence = 0)
            {
                ProductionDirectDataRouteOutcome? routedBy = null;
                for (var index = 0; index < 256; index++)
                {
                    var polled = native.PollRouteAction(path);
                    if (polled.Status == ProductionDirectNativeResultV5.NoData) break;
                    if (polled.Status != ProductionDirectNativeResultV5.Accepted || polled.Action == null)
                        throw new PeerRetiredException("DIRECT_ACTION_POLL_FAILED");
                    var actionOutcome = HandleAction(polled.Action);
                    if (routedSessionId != Guid.Empty && routedSequence > 0 &&
                        polled.Action.SessionId == routedSessionId &&
                        polled.Action.EnvelopeSequence == routedSequence &&
                        actionOutcome.HasValue)
                    {
                        routedBy = PreferRouteOutcome(routedBy, actionOutcome.Value);
                    }
                }
                foreach (var value in native.Poll(64))
                {
                    ValidateNativeEventFence(value);
                    ObserveNativeEvent(value);
                    // TransportReceipt releases native retransmission memory
                    // only. It deliberately has no durable-ACK callback.
                    if (value.Kind == ProductionDirectNativeEventKindV5.SignRequest)
                    {
                        HandleSignRequest(value);
                        continue;
                    }
                    if (value.Kind == ProductionDirectNativeEventKindV5.PathReady)
                    {
                        if (value.Status != ProductionDirectNativeResultV5.Accepted)
                            throw new PeerRetiredException("DIRECT_PATH_READY_INVALID");
                        TryObservePathReady();
                        continue;
                    }
                    if (value.Kind == ProductionDirectNativeEventKindV5.RenewalDue)
                    {
                        // Ticket renewal due (transport audit #2). The FFI
                        // emits the event once per ticket generation; the
                        // actor owns the renewal loop from here — fetch from
                        // the server, apply through renew_apply, retry with
                        // backoff until it lands or the path retires. The
                        // native hard stop at expiry+grace stays the fallback.
                        KickTicketRenewal();
                        continue;
                    }
                    if (value.Kind == ProductionDirectNativeEventKindV5.DirectFailed ||
                        value.Kind == ProductionDirectNativeEventKindV5.QueueLimit ||
                        value.Status is not ProductionDirectNativeResultV5.Accepted and
                            not ProductionDirectNativeResultV5.NoData)
                        throw new PeerRetiredException("DIRECT_NATIVE_FAILED");
                }
                DrainReceivedEnvelopes();
                return Task.FromResult(routedBy);
            }

            private void HandleSignRequest(ProductionDirectNativeEventV5 value)
            {
                if (value.Status != ProductionDirectNativeResultV5.Accepted ||
                    value.FrameSequence == 0 || value.PayloadBytes == 0)
                    throw new PeerRetiredException("DIRECT_SIGN_REQUEST_INVALID");
                var pending = native.ReceiveSigningPayload(path);
                if (pending.Status != ProductionDirectNativeResultV5.Accepted ||
                    pending.RequestId != value.FrameSequence || pending.Payload.Length == 0 ||
                    pending.Payload.Length != value.PayloadBytes ||
                    pending.RequiredBytes != value.PayloadBytes)
                {
                    if (pending.Payload.Length > 0)
                        CryptographicOperations.ZeroMemory(pending.Payload);
                    throw new PeerRetiredException("DIRECT_SIGN_TRANSCRIPT_INVALID");
                }
                byte[] signature = null;
                try
                {
                    signature = signNativeTranscript(pending.Payload);
                    if (signature?.Length != 64)
                        throw new PeerRetiredException("DIRECT_SIGNATURE_INVALID");
                    if (native.SubmitSignature(path, pending.RequestId, signature) !=
                        ProductionDirectNativeResultV5.Accepted)
                        throw new PeerRetiredException("DIRECT_SIGNATURE_REJECTED");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pending.Payload);
                    if (signature != null) CryptographicOperations.ZeroMemory(signature);
                }
            }

            private void DrainReceivedEnvelopes()
            {
                for (var index = 0; index < 256; index++)
                {
                    var received = native.ReceiveEnvelope(path);
                    if (received.Status == ProductionDirectNativeResultV5.NoData) break;
                    if (received.Status != ProductionDirectNativeResultV5.Accepted ||
                        received.Envelope.Length == 0)
                        throw new PeerRetiredException("DIRECT_RECEIVE_FAILED");
                    try
                    {
                        var metadata = options.ParseIngressMetadata(received.Envelope) ??
                            throw new PeerRetiredException("DIRECT_ENVELOPE_INVALID");
                        var status = native.ArbitrateIngress(new ProductionDirectIngressRequestV5
                        {
                            Path = path,
                            Direction = ProductionDirectDirectionV5.AndroidToPc,
                            Source = ProductionDirectIngressSourceV5.Direct,
                            SessionId = metadata.SessionId,
                            EnvelopeSequence = metadata.EnvelopeSequence,
                            NowMilliseconds = checked((ulong)Math.Max(0, options.ClockMilliseconds())),
                            DuplicateGraceMilliseconds = options.DuplicateGraceMilliseconds
                        }, received.Envelope);
                        if (status != ProductionDirectNativeResultV5.Accepted)
                            throw new PeerRetiredException("DIRECT_INGRESS_ARBITRATION_FAILED");
                    }
                    finally { CryptographicOperations.ZeroMemory(received.Envelope); }
                }
                for (var index = 0; index < 256; index++)
                {
                    var polled = native.PollRouteAction(path);
                    if (polled.Status == ProductionDirectNativeResultV5.NoData) break;
                    if (polled.Status != ProductionDirectNativeResultV5.Accepted || polled.Action == null)
                        throw new PeerRetiredException("DIRECT_INGRESS_ACTION_FAILED");
                    HandleAction(polled.Action);
                }
            }

            private ProductionDirectDataRouteOutcome? HandleAction(
                ProductionDirectRouteActionV5 action)
            {
                if (!ActionMatchesPath(action))
                    throw new PeerRetiredException("DIRECT_ACTION_FENCE_INVALID");
                // HOLD/DROP remain product-visible native actions below. The
                // counters observe them without consuming that ownership
                // transfer or changing existing scheduler behavior.
                if (action.Kind == ProductionDirectRouteActionKindV5.IngressHold)
                    IncrementCounter(ref ingressHoldCount);
                else if (action.Kind == ProductionDirectRouteActionKindV5.IngressDrop)
                    IncrementCounter(ref ingressDropCount);
                switch (action.Kind)
                {
                    case ProductionDirectRouteActionKindV5.RelayEnvelope:
                        try
                        {
                            var outcome = TransferRelay(
                                options,
                                PeerDeviceId,
                                action.SessionId,
                                action.EnvelopeSequence,
                                action.Payload);
                            if (action.Reason != ProductionDirectRouteReasonV5.None)
                                RecordRelayReplay(action.PayloadBytes);
                            return outcome;
                        }
                        finally { CryptographicOperations.ZeroMemory(action.Payload); }
                    case ProductionDirectRouteActionKindV5.IngressDeliver:
                        try
                        {
                            TransferIngress(
                                options,
                                PeerDeviceId,
                                action.Carrier == ProductionDirectCarrierV5.Relay
                                    ? ProductionDirectIngressSourceV5.Relay
                                    : ProductionDirectIngressSourceV5.Direct,
                                path,
                                action.Payload);
                        }
                        finally { CryptographicOperations.ZeroMemory(action.Payload); }
                        return null;
                    case ProductionDirectRouteActionKindV5.Carrier:
                        if (action.Direction == ProductionDirectDirectionV5.PcToAndroid)
                            pcToAndroidCarrier = action.Carrier ?? ProductionDirectCarrierV5.Relay;
                        else
                            androidToPcCarrier = action.Carrier ?? ProductionDirectCarrierV5.Relay;
                        if (action.Carrier is ProductionDirectCarrierV5.LanDirect or
                            ProductionDirectCarrierV5.NatDirect)
                            CommitCutoverDirection(action.Direction);
                        else
                            ClearSessionCarriers(action.Direction);
                        Publish();
                        return null;
                    case ProductionDirectRouteActionKindV5.SessionCarrier:
                        var sessionCarriers = action.Direction ==
                            ProductionDirectDirectionV5.PcToAndroid
                                ? pcToAndroidSessionCarriers
                                : androidToPcSessionCarriers;
                        if (action.Carrier is ProductionDirectCarrierV5.LanDirect or
                            ProductionDirectCarrierV5.NatDirect)
                        {
                            sessionCarriers[action.SessionId] = action.Carrier.Value;
                            // The first activation changes the direction from
                            // Relay to Direct and therefore emits a CARRIER
                            // action.  A later incremental batch keeps that
                            // direction Direct, so native emits only
                            // SESSION_CARRIER for the newly committed session.
                            // Either action is sufficient proof that the
                            // serialized native owner committed the batch.
                            CommitCutoverDirection(action.Direction);
                        }
                        else
                            sessionCarriers.Remove(action.SessionId);
                        Publish();
                        return null;
                    case ProductionDirectRouteActionKindV5.Fallback:
                        RecordFallback(ReasonCode(action.Reason));
                        // Rejection/timeout of a late incremental proposal
                        // returns only that uncommitted batch to Relay. A
                        // fallback for a previously committed barrier owns the
                        // whole direction and clears every session projection.
                        if (action.BarrierId == Guid.Empty && committedCutovers.Values.Any(value =>
                                value.Direction == action.Direction) ||
                            action.BarrierId != Guid.Empty &&
                                committedCutovers.ContainsKey(action.BarrierId))
                        {
                            if (action.Direction == ProductionDirectDirectionV5.PcToAndroid)
                                pcToAndroidCarrier = ProductionDirectCarrierV5.Relay;
                            else
                                androidToPcCarrier = ProductionDirectCarrierV5.Relay;
                            ClearSessionCarriers(action.Direction);
                        }
                        Publish();
                        SendCutoverAction(action);
                        return null;
                    case ProductionDirectRouteActionKindV5.Accept:
                    case ProductionDirectRouteActionKindV5.Commit:
                        SendCutoverAction(action);
                        return null;
                    default:
                        if (options.EnqueueNativeAction != null)
                        {
                            var ownedKey = pairwiseKey.ToArray();
                            try
                            {
                                options.EnqueueNativeAction(new ProductionDirectDataNativeAction
                                {
                                    PeerDeviceId = PeerDeviceId,
                                    Action = action,
                                    PairwiseKey = ownedKey
                                });
                                ownedKey = null;
                            }
                            finally
                            {
                                if (ownedKey != null)
                                    CryptographicOperations.ZeroMemory(ownedKey);
                            }
                        }
                        return action.Kind == ProductionDirectRouteActionKindV5.DirectEnvelope
                            ? ProductionDirectDataRouteOutcome.DirectAccepted
                            : null;
                }
            }

            private void SendNominationAck()
            {
                if (nominationAckSent) return;
                var plaintext = ProductionDirectCutoverSignalCodec.CreateNominationAck(nomination);
                try
                {
                    if (!TrySendEncrypted(TransportSignalTypeV1.NominationAckV2, plaintext))
                        throw new PeerRetiredException("DIRECT_NOMINATION_ACK_FAILED");
                    nominationAckSent = true;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            private void TryObservePathReady()
            {
                if (nomination == null || localPathBinding != null) return;
                var info = native.PathInfo(path);
                if (info.Status == ProductionDirectNativeResultV5.NotReady) return;
                if (info.Status != ProductionDirectNativeResultV5.Accepted || info.Info == null ||
                    info.Info.Path == null || !info.Info.Path.Equals(path))
                    throw new PeerRetiredException("DIRECT_PATH_INFO_INVALID");
                if (!info.Info.Ready) return;
                if (
                    info.Info.PathKind is not (ProductionDirectPathKindV5.Lan or
                        ProductionDirectPathKindV5.Nat) ||
                    info.Info.DirectPathBinding?.Length != 32 ||
                    IsAllZero(info.Info.DirectPathBinding))
                    throw new PeerRetiredException("DIRECT_PATH_INFO_INVALID");
                localPathBinding = info.Info.DirectPathBinding.ToArray();
                SendNominationResult();
                TryCompleteNomination();
                Publish();
            }

            private void SendNominationResult()
            {
                if (nominationResultSent || localPathBinding?.Length != 32) return;
                var plaintext = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                    nomination, localPathBinding);
                try
                {
                    if (!TrySendEncrypted(TransportSignalTypeV1.NominationResultV2, plaintext))
                        throw new PeerRetiredException("DIRECT_NOMINATION_RESULT_SEND_FAILED");
                    nominationResultSent = true;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            private void TryCompleteNomination()
            {
                if (nominationCompletionNotified || localPathBinding?.Length != 32 ||
                    peerPathBinding?.Length != 32) return;
                if (!CryptographicOperations.FixedTimeEquals(localPathBinding, peerPathBinding))
                    throw new PeerRetiredException("DIRECT_PROMOTED_BINDING_MISMATCH");
                nominationCompletionNotified = true;
                var completed = nominationCompleted;
                if (completed == null)
                {
                    ClearNominationArtifacts();
                    return;
                }
                var owned = localPathBinding.ToArray();
                try { completed(owned); }
                finally
                {
                    CryptographicOperations.ZeroMemory(owned);
                    ClearNominationArtifacts();
                }
            }

            private void NotifyNominationFailed(string reason)
            {
                if (nomination == null || nominationCompletionNotified || nominationFailureNotified) return;
                nominationFailureNotified = true;
                var failed = nominationFailed;
                try
                {
                    failed?.Invoke(string.IsNullOrWhiteSpace(reason)
                        ? "DIRECT_PROMOTED_FAILED" : NormalizeReason(reason));
                }
                catch (Exception) { }
                finally { ClearNominationArtifacts(); }
            }

            private void ClearNominationArtifacts()
            {
                ProductionDirectDataPromotionHandoff.ZeroNomination(nomination);
                nomination = null;
                if (peerPathBinding != null)
                    CryptographicOperations.ZeroMemory(peerPathBinding);
                peerPathBinding = null;
                nominationCompleted = null;
                nominationFailed = null;
            }

            private bool TrySendEncrypted(TransportSignalTypeV1 type, byte[] plaintext)
            {
                if (plaintext?.Length is not (> 0) || localSignalSequence >= long.MaxValue)
                    return false;
                var metadata = new TransportSignalEnvelopeV1
                {
                    ProtocolVersion = 1,
                    SignalType = type,
                    ConnectionId = ByteString.CopyFrom(GuidBytes(path.ConnectionId)),
                    SenderDeviceId = ByteString.CopyFrom(GuidBytes(localDeviceId)),
                    RecipientDeviceId = ByteString.CopyFrom(GuidBytes(PeerDeviceId)),
                    PathEpoch = path.PathEpoch,
                    SignalSeq = localSignalSequence + 1,
                    SignalAck = remoteSignalSequence,
                    SentAtMs = options.ClockMilliseconds(),
                    KeyId = ByteString.CopyFrom(signalKeyId)
                };
                var encrypted = ProductionDirectTransportCrypto.EncryptSignal(
                    pairwiseKey, metadata, plaintext);
                ValidateSignalFence(encrypted, inbound: false, currentSignalGate());
                if (!signaling.TrySend(encrypted)) return false;
                localSignalSequence = encrypted.SignalSeq;
                return true;
            }

            private void SendCutoverAction(ProductionDirectRouteActionV5 action)
            {
                var wireBarrierId = action.BarrierId;
                var fullDirectionFallback = action.Kind ==
                    ProductionDirectRouteActionKindV5.Fallback &&
                    (wireBarrierId == Guid.Empty ||
                     committedCutovers.ContainsKey(wireBarrierId));
                CutoverBarrierState state;
                var stateFound = false;
                if (action.Kind == ProductionDirectRouteActionKindV5.Fallback &&
                    wireBarrierId == Guid.Empty)
                {
                    state = committedCutovers.Values
                        .Where(value => value.Direction == action.Direction)
                        .OrderBy(value => value.BarrierId)
                        .FirstOrDefault();
                    stateFound = state != null;
                    if (stateFound) wireBarrierId = state.BarrierId;
                }
                else
                {
                    stateFound = action.Kind == ProductionDirectRouteActionKindV5.Fallback
                        ? TryFallbackCutover(wireBarrierId, out state)
                        : cutovers.TryGetValue(wireBarrierId, out state);
                }
                if (!stateFound ||
                    state.Direction != action.Direction ||
                    action.Kind != ProductionDirectRouteActionKindV5.Fallback &&
                        !CryptographicOperations.FixedTimeEquals(
                            state.BarrierSha256, action.BarrierSha256))
                    throw new PeerRetiredException("DIRECT_CUTOVER_ACTION_INVALID");

                byte[] plaintext;
                TransportSignalTypeV1 type;
                switch (action.Kind)
                {
                    case ProductionDirectRouteActionKindV5.Accept:
                        plaintext = ProductionDirectCutoverSignalCodec.CreateAccept(
                            action.BarrierId,
                            action.BarrierSha256,
                            action.Accepted,
                            action.Accepted ? string.Empty : ReasonCode(action.Reason));
                        type = TransportSignalTypeV1.CutoverAccept;
                        break;
                    case ProductionDirectRouteActionKindV5.Commit:
                        plaintext = ProductionDirectCutoverSignalCodec.CreateCommit(
                            action.BarrierId, action.BarrierSha256);
                        type = TransportSignalTypeV1.CutoverCommit;
                        break;
                    case ProductionDirectRouteActionKindV5.Fallback:
                        plaintext = ProductionDirectCutoverSignalCodec.CreateFallback(
                            wireBarrierId,
                            ReasonCode(action.Reason),
                            action.DirectFrameSequence);
                        type = TransportSignalTypeV1.Fallback;
                        break;
                    default:
                        throw new PeerRetiredException("DIRECT_CUTOVER_ACTION_INVALID");
                }

                var sent = false;
                try
                {
                    sent = TrySendCutoverEncrypted(type, plaintext);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }

                if (sent && action.Kind == ProductionDirectRouteActionKindV5.Commit && action.IsEcho)
                {
                    var committed = native.CommitEchoSent(TransitionRequest(
                        state,
                        accepted: false,
                        isEcho: true));
                    if (committed != ProductionDirectNativeResultV5.Accepted)
                        throw new PeerRetiredException("DIRECT_COMMIT_ECHO_REJECTED");
                }
                if (action.Kind == ProductionDirectRouteActionKindV5.Fallback ||
                    action.Kind == ProductionDirectRouteActionKindV5.Accept && !action.Accepted)
                {
                    RemoveCutover(wireBarrierId);
                    if (fullDirectionFallback)
                    {
                        foreach (var committed in committedCutovers.Values
                            .Where(value => value.Direction == action.Direction).ToArray())
                            RemoveCommittedCutover(committed.BarrierId);
                    }
                    else
                    {
                        RemoveCommittedCutover(wireBarrierId);
                    }
                    Publish();
                }
            }

            private void TickCutovers(ulong now)
            {
                foreach (var state in cutovers.Values.ToArray())
                {
                    var result = native.Tick(TransitionRequest(
                        state,
                        accepted: false,
                        isEcho: false,
                        now: now));
                    if (IsBatchLocalTransitionRefusal(result))
                        continue;
                    if (result is not ProductionDirectNativeResultV5.Accepted and
                        not ProductionDirectNativeResultV5.NoData and
                        not ProductionDirectNativeResultV5.NotReady)
                        throw new PeerRetiredException("DIRECT_CUTOVER_TICK_FAILED");
                }
            }

            private static bool IsBatchLocalTransitionRefusal(
                ProductionDirectNativeResultV5 result) =>
                result is ProductionDirectNativeResultV5.Protocol or
                    ProductionDirectNativeResultV5.Limit or
                    ProductionDirectNativeResultV5.QueueFull or
                    ProductionDirectNativeResultV5.Timeout;

            private bool TrySendCutoverEncrypted(
                TransportSignalTypeV1 type,
                byte[] plaintext)
            {
                try { return TrySendEncrypted(type, plaintext); }
                catch (InvalidOperationException error) when (
                    string.Equals(
                        error.Message,
                        "SIGNAL_DATA_GENERATION_STALE",
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            private ProductionDirectProposalRequestV5 ProposalRequest(
                ProductionDirectCutoverProposal proposal,
                ProductionDirectDirectionV5 direction) => new()
            {
                Path = path,
                Direction = direction,
                BarrierId = proposal.BarrierId,
                BarrierSha256 = proposal.BarrierSha256.ToArray(),
                DirectPathBinding = proposal.DirectPathBinding.ToArray(),
                DuplicateGraceMilliseconds = proposal.DuplicateGraceMilliseconds,
                NowMilliseconds = checked((ulong)Math.Max(0, options.ClockMilliseconds())),
                TimeoutMilliseconds = 10_000,
                Sessions = proposal.SessionBarriers.Select(value =>
                    new ProductionDirectSessionBarrierV5
                    {
                        SessionId = value.SessionId,
                        FirstDirectSequence = value.FirstDirectSequence
                    }).ToArray()
            };

            private ProductionDirectTransitionRequestV5 TransitionRequest(
                CutoverBarrierState state,
                bool accepted,
                bool isEcho,
                ulong? now = null) => new()
            {
                Path = path,
                Direction = state.Direction,
                BarrierId = state.BarrierId,
                BarrierSha256 = state.BarrierSha256.ToArray(),
                Accepted = accepted,
                IsEcho = isEcho,
                NowMilliseconds = now ?? checked((ulong)Math.Max(0, options.ClockMilliseconds())),
                TimeoutMilliseconds = 10_000
            };

            private bool TryCutover(Guid barrierId, byte[] hash, out CutoverBarrierState state)
            {
                if (!cutovers.TryGetValue(barrierId, out state) || hash?.Length != 32 ||
                    !CryptographicOperations.FixedTimeEquals(state.BarrierSha256, hash))
                {
                    state = null;
                    return false;
                }
                return true;
            }

            private void RemoveCutover(Guid barrierId)
            {
                if (cutovers.Remove(barrierId, out var removed))
                {
                    CryptographicOperations.ZeroMemory(removed.BarrierSha256);
                    Interlocked.Decrement(ref activeCutoverCount);
                }
            }

            private void CommitCutoverDirection(ProductionDirectDirectionV5 direction)
            {
                var completedStates = cutovers.Values
                    .Where(value => value.Direction == direction).ToArray();
                // A repeated carrier notification must not erase the compact
                // fallback authority installed by the original commit.
                if (completedStates.Length == 0) return;
                foreach (var completed in completedStates)
                {
                    if (!cutovers.Remove(completed.BarrierId)) continue;
                    Interlocked.Decrement(ref activeCutoverCount);
                    committedCutovers[completed.BarrierId] = completed;
                    Interlocked.Increment(ref committedCutoverCount);
                }
            }

            private void ForgetCommittedSession(
                ProductionDirectDirectionV5 direction,
                Guid sessionId)
            {
                foreach (var state in committedCutovers.Values
                    .Where(value => value.Direction == direction).ToArray())
                {
                    state.SessionIds.Remove(sessionId);
                    if (state.SessionIds.Count == 0)
                        RemoveCommittedCutover(state.BarrierId);
                }
            }

            private void ClearSessionCarriers(ProductionDirectDirectionV5 direction)
            {
                var carriers = direction == ProductionDirectDirectionV5.PcToAndroid
                    ? pcToAndroidSessionCarriers
                    : androidToPcSessionCarriers;
                carriers.Clear();
            }

            private bool TryFallbackCutover(Guid barrierId, out CutoverBarrierState state) =>
                cutovers.TryGetValue(barrierId, out state) ||
                committedCutovers.TryGetValue(barrierId, out state);

            private void RemoveCommittedCutover(Guid barrierId)
            {
                if (committedCutovers.Remove(barrierId, out var removed))
                {
                    CryptographicOperations.ZeroMemory(removed.BarrierSha256);
                    Interlocked.Decrement(ref committedCutoverCount);
                }
            }

            private void ValidateNativeEventFence(ProductionDirectNativeEventV5 value)
            {
                if (value == null || value.PathEpoch != path.PathEpoch ||
                    value.AttemptId != path.AttemptId)
                    throw new PeerRetiredException("DIRECT_EVENT_FENCE_INVALID");
            }

            private void ObserveNativeEvent(ProductionDirectNativeEventV5 value)
            {
                switch (value.Kind)
                {
                    case ProductionDirectNativeEventKindV5.EnvelopeQueued:
                        IncrementCounter(ref directFramesSent);
                        break;
                    case ProductionDirectNativeEventKindV5.EnvelopeAvailable:
                        IncrementCounter(ref directFramesReceived);
                        ObserveHighWatermark(
                            ref lastReceivedEnvelopeFrameSequence,
                            value.FrameSequence);
                        break;
                    case ProductionDirectNativeEventKindV5.TransportReceipt
                        when value.Flags.HasFlag(ProductionDirectNativeEventFlagsV5.Inbound):
                        IncrementCounter(ref transportReceiptsReceived);
                        ObserveHighWatermark(
                            ref transportReceiptHighWatermark,
                            value.FrameSequence);
                        break;
                    case ProductionDirectNativeEventKindV5.TransportReceipt
                        when value.Flags.HasFlag(ProductionDirectNativeEventFlagsV5.Outbound):
                        IncrementCounter(ref transportReceiptsSent);
                        break;
                    case ProductionDirectNativeEventKindV5.QueueLimit:
                        IncrementCounter(ref nativeQueueLimitCount);
                        break;
                }
            }

            private void RecordRelayReplay(uint payloadBytes)
            {
                IncrementCounter(ref relayReplayEnvelopeCount);
                AddCounter(ref relayReplayBytes, payloadBytes);
            }

            private void RecordFallback(string reasonCode)
            {
                IncrementCounter(ref fallbackCount);
                Volatile.Write(
                    ref lastFallbackReasonCode,
                    NormalizeReason(reasonCode));
            }

            private static ProductionDirectDataRouteOutcome PreferRouteOutcome(
                ProductionDirectDataRouteOutcome? current,
                ProductionDirectDataRouteOutcome candidate)
            {
                if (!current.HasValue || candidate == ProductionDirectDataRouteOutcome.DirectAccepted)
                    return candidate;
                if (current.Value == ProductionDirectDataRouteOutcome.DirectAccepted)
                    return current.Value;
                if (candidate == ProductionDirectDataRouteOutcome.RelaySent)
                    return candidate;
                return current.Value;
            }

            private void DrainRecoveryActions()
            {
                for (var index = 0; index < 260; index++)
                {
                    var polled = native.PollRouteAction(path);
                    if (polled.Status == ProductionDirectNativeResultV5.NoData) return;
                    if (polled.Status != ProductionDirectNativeResultV5.Accepted ||
                        polled.Action == null) return;
                    if (!ActionMatchesPath(polled.Action))
                    {
                        if (polled.Action.Payload?.Length > 0)
                            CryptographicOperations.ZeroMemory(polled.Action.Payload);
                        return;
                    }
                    var action = polled.Action;
                    if (action.Kind == ProductionDirectRouteActionKindV5.RelayEnvelope)
                    {
                        try
                        {
                            TransferRelay(
                                options,
                                PeerDeviceId,
                                action.SessionId,
                                action.EnvelopeSequence,
                                action.Payload);
                            if (action.Reason != ProductionDirectRouteReasonV5.None)
                                RecordRelayReplay(action.PayloadBytes);
                        }
                        finally { CryptographicOperations.ZeroMemory(action.Payload); }
                    }
                    else if (action.Kind == ProductionDirectRouteActionKindV5.IngressDeliver)
                    {
                        try
                        {
                            TransferIngress(
                                options,
                                PeerDeviceId,
                                action.Carrier == ProductionDirectCarrierV5.Relay
                                    ? ProductionDirectIngressSourceV5.Relay
                                    : ProductionDirectIngressSourceV5.Direct,
                                path,
                                action.Payload);
                        }
                        finally { CryptographicOperations.ZeroMemory(action.Payload); }
                    }
                    else if (action.Kind == ProductionDirectRouteActionKindV5.Fallback)
                    {
                        RecordFallback(ReasonCode(action.Reason));
                        if (options.EnqueueNativeAction == null) continue;
                        var ownedKey = pairwiseKey.ToArray();
                        try
                        {
                            options.EnqueueNativeAction(new ProductionDirectDataNativeAction
                            {
                                PeerDeviceId = PeerDeviceId,
                                Action = action,
                                PairwiseKey = ownedKey
                            });
                            ownedKey = null;
                        }
                        finally
                        {
                            if (ownedKey != null)
                                CryptographicOperations.ZeroMemory(ownedKey);
                        }
                    }
                    // A predecessor may drain recovery actions only. Direct,
                    // hold/drop, carrier, accept, and commit can never mutate a
                    // successor actor during retirement.
                }
            }

            private bool ActionMatchesPath(ProductionDirectRouteActionV5 action) =>
                action?.Path != null && action.Path.Equals(path);

            private void ValidateSignalFence(
                TransportSignalEnvelopeV1 signal,
                bool inbound,
                SignalGate gate)
            {
                var sender = ReadGuid(signal.SenderDeviceId.Span);
                var recipient = ReadGuid(signal.RecipientDeviceId.Span);
                var connection = ReadGuid(signal.ConnectionId.Span);
                if (signal.ProtocolVersion != 1 || signal.PathEpoch != path.PathEpoch ||
                    connection != path.ConnectionId ||
                    signal.KeyId.Length != signalKeyId.Length ||
                    !CryptographicOperations.FixedTimeEquals(
                        signal.KeyId.Span, signalKeyId) ||
                    (inbound ? sender != PeerDeviceId || recipient != localDeviceId :
                        sender != localDeviceId || recipient != PeerDeviceId))
                    throw new PeerRetiredException("SIGNAL_FENCE_INVALID");
                var liveGate = currentSignalGate();
                if (IsCutoverSignal(signal.SignalType))
                {
                    var fallback = signal.SignalType == TransportSignalTypeV1.Fallback;
                    if (!gate.Connected || !liveGate.Connected || gate.Generation <= 0 ||
                        gate.Generation != liveGate.Generation ||
                        !fallback && (!gate.DataAvailable || !liveGate.DataAvailable ||
                            gate.Generation != AdmissionGeneration))
                        throw new InvalidOperationException("SIGNAL_DATA_GENERATION_STALE");
                }
                if (!IsCutoverSignal(signal.SignalType) && !IsCleanupSignal(signal.SignalType) &&
                    (int)signal.SignalType is < 1 or > 14)
                    throw new PeerRetiredException("SIGNAL_TYPE_INVALID");
                if (inbound)
                {
                    if (signal.SignalSeq != remoteSignalSequence + 1 ||
                        signal.SignalAck < remoteSignalAcknowledgement ||
                        signal.SignalAck > localSignalSequence)
                        throw new PeerRetiredException("SIGNAL_CURSOR_INVALID");
                }
                else if (signal.SignalSeq != localSignalSequence + 1 ||
                    signal.SignalAck != remoteSignalSequence)
                    throw new InvalidOperationException("SIGNAL_CURSOR_INVALID");
            }

            private void Publish()
            {
                var snapshot = BuildStateSnapshot();
                Volatile.Write(ref publishedSnapshot, snapshot);
                try { options.PeerStateChanged?.Invoke(snapshot); }
                catch (Exception) { }
            }

            private bool SensitiveStateIsCleared() => pairwiseKey == null &&
                IsAllZero(signalKeyId) && localPathBinding == null && peerPathBinding == null &&
                nomination == null && Volatile.Read(ref activeCutoverCount) == 0 &&
                Volatile.Read(ref committedCutoverCount) == 0;

            private bool TryReserveBytes(int count)
            {
                if (count < 0 || count > options.PeerMailboxCapacityBytes) return false;
                while (true)
                {
                    var current = Volatile.Read(ref mailboxBytes);
                    if (current > options.PeerMailboxCapacityBytes - count) return false;
                    if (Interlocked.CompareExchange(ref mailboxBytes, current + count, current) == current)
                        return true;
                }
            }

            private void ReleaseBytes(int count)
            {
                if (count <= 0) return;
                var remaining = Interlocked.Add(ref mailboxBytes, -count);
                if (remaining < 0) Interlocked.Exchange(ref mailboxBytes, 0);
            }

            private static ulong ReadCounter(ref long value) =>
                checked((ulong)Math.Max(0L, Volatile.Read(ref value)));

            private static void IncrementCounter(ref long value) =>
                AddCounter(ref value, 1);

            private static void AddCounter(ref long value, long amount)
            {
                if (amount <= 0) return;
                while (true)
                {
                    var current = Volatile.Read(ref value);
                    if (current == long.MaxValue) return;
                    var next = current > long.MaxValue - amount
                        ? long.MaxValue
                        : current + amount;
                    if (Interlocked.CompareExchange(ref value, next, current) == current)
                        return;
                }
            }

            private static void ObserveHighWatermark(ref long value, ulong observed)
            {
                if (observed == 0 || observed > long.MaxValue) return;
                var candidate = checked((long)observed);
                while (true)
                {
                    var current = Volatile.Read(ref value);
                    if (candidate <= current) return;
                    if (Interlocked.CompareExchange(ref value, candidate, current) == current)
                        return;
                }
            }

            private static Guid ReadGuid(ReadOnlySpan<byte> value)
            {
                if (value.Length != 16) throw new PeerRetiredException("SIGNAL_UUID_INVALID");
                return Guid.ParseExact(Convert.ToHexString(value), "N");
            }

            private static byte[] GuidBytes(Guid value) =>
                Convert.FromHexString(value.ToString("N"));

            private static bool IsAllZero(ReadOnlySpan<byte> value)
            {
                byte aggregate = 0;
                foreach (var item in value) aggregate |= item;
                return aggregate == 0;
            }

            private static string ReasonCode(ProductionDirectRouteReasonV5 value) => value switch
            {
                ProductionDirectRouteReasonV5.BufferEnvelopeLimit => "BUFFER_ENVELOPE_LIMIT",
                ProductionDirectRouteReasonV5.BufferByteLimit => "BUFFER_BYTE_LIMIT",
                ProductionDirectRouteReasonV5.DirectFailure => "DIRECT_FAILURE",
                ProductionDirectRouteReasonV5.EpochReplaced => "EPOCH_REPLACED",
                ProductionDirectRouteReasonV5.NegotiationTimeout => "NEGOTIATION_TIMEOUT",
                ProductionDirectRouteReasonV5.PeerRejected => "PEER_REJECTED",
                ProductionDirectRouteReasonV5.BarrierMismatch => "BARRIER_MISMATCH",
                ProductionDirectRouteReasonV5.InvalidEnvelope => "INVALID_ENVELOPE",
                ProductionDirectRouteReasonV5.ProtocolViolation => "PROTOCOL_VIOLATION",
                ProductionDirectRouteReasonV5.RejectInvalidEpoch => "REJECT_INVALID_EPOCH",
                ProductionDirectRouteReasonV5.RejectFutureEpoch => "REJECT_FUTURE_EPOCH",
                ProductionDirectRouteReasonV5.RejectInvalidProposal => "REJECT_INVALID_PROPOSAL",
                ProductionDirectRouteReasonV5.RejectInvalidTimeout => "REJECT_INVALID_TIMEOUT",
                ProductionDirectRouteReasonV5.RejectPathBindingMismatch =>
                    "REJECT_PATH_BINDING_MISMATCH",
                ProductionDirectRouteReasonV5.RejectDirectionBusy => "REJECT_DIRECTION_BUSY",
                ProductionDirectRouteReasonV5.RejectBarrierIdMismatch =>
                    "REJECT_BARRIER_ID_MISMATCH",
                ProductionDirectRouteReasonV5.RejectBarrierHashMismatch =>
                    "REJECT_BARRIER_HASH_MISMATCH",
                ProductionDirectRouteReasonV5.RejectUnexpectedAccept =>
                    "REJECT_UNEXPECTED_ACCEPT",
                ProductionDirectRouteReasonV5.RejectLateCommit => "REJECT_LATE_COMMIT",
                ProductionDirectRouteReasonV5.RejectPrefixTimeout => "REJECT_PREFIX_TIMEOUT",
                _ => "PROTOCOL_VIOLATION"
            };

            private sealed class CutoverBarrierState
            {
                internal Guid BarrierId { get; init; }
                internal byte[] BarrierSha256 { get; init; }
                internal ProductionDirectDirectionV5 Direction { get; init; }
                internal bool LocallyProposed { get; init; }
                internal HashSet<Guid> SessionIds { get; init; }

                internal static CutoverBarrierState FromProposal(
                    ProductionDirectCutoverProposal proposal,
                    ProductionDirectDirectionV5 direction,
                    bool locallyProposed) => new()
                {
                    BarrierId = proposal.BarrierId,
                    BarrierSha256 = proposal.BarrierSha256.ToArray(),
                    Direction = direction,
                    LocallyProposed = locallyProposed,
                    SessionIds = proposal.SessionBarriers
                        .Select(value => value.SessionId)
                        .ToHashSet()
                };
            }
        }

        private sealed class PeerRetiredException : Exception
        {
            internal PeerRetiredException(string message) : base(message) { }
        }

        private sealed class RelayOwnershipTransferException : Exception
        {
            private readonly Guid peerDeviceId;
            private readonly Guid sessionId;
            private readonly ulong sequence;

            internal RelayOwnershipTransferException(
                Guid peerDeviceId,
                Guid sessionId,
                ulong sequence,
                Exception innerException)
                : base("RELAY_OWNERSHIP_TRANSFER_REJECTED", innerException)
            {
                this.peerDeviceId = peerDeviceId;
                this.sessionId = sessionId;
                this.sequence = sequence;
            }

            internal bool Matches(Guid peer, Guid session, ulong envelopeSequence) =>
                peer == peerDeviceId && session == sessionId && envelopeSequence == sequence;
        }

        private static ProductionDirectPathFenceV5 CopyPath(ProductionDirectPathFenceV5 value) =>
            value == null ? null : new ProductionDirectPathFenceV5
            {
                PathEpoch = value.PathEpoch,
                ConnectionId = value.ConnectionId,
                AttemptId = value.AttemptId
            };

        private static ProductionDirectCutoverProposal CopyProposal(
            ProductionDirectCutoverProposal value) => new()
        {
            BarrierId = value.BarrierId,
            Direction = value.Direction,
            DirectPathBinding = value.DirectPathBinding.ToArray(),
            BarrierSha256 = value.BarrierSha256.ToArray(),
            DuplicateGraceMilliseconds = value.DuplicateGraceMilliseconds,
            SessionBarriers = value.SessionBarriers.Select(item =>
                new ProductionDirectSessionCutoverBarrier
                {
                    SessionId = item.SessionId,
                    FirstDirectSequence = item.FirstDirectSequence
                }).ToArray(),
            WireBytes = value.WireBytes.ToArray()
        };

        private static void ZeroProposal(ProductionDirectCutoverProposal value)
        {
            if (value == null) return;
            if (value.DirectPathBinding != null)
                CryptographicOperations.ZeroMemory(value.DirectPathBinding);
            if (value.BarrierSha256 != null)
                CryptographicOperations.ZeroMemory(value.BarrierSha256);
            if (value.WireBytes != null)
                CryptographicOperations.ZeroMemory(value.WireBytes);
        }

        private static bool ProposalSecretsCleared(ProductionDirectCutoverProposal value) =>
            value != null && IsZero(value.DirectPathBinding) &&
            IsZero(value.BarrierSha256) && IsZero(value.WireBytes);

        private static bool IsZero(byte[] value) =>
            value == null || value.All(item => item == 0);

        private static string NormalizeReason(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 128 ? value : value[..128];
        }
    }
}

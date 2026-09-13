using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    /// <summary>
    /// ABI v4 multipair shadow runtime for the Windows PC responder. Android
    /// remains the ticket initiator in the current product flow. This runtime
    /// owns only its independent signalling lane and native Direct handle; it
    /// cannot recreate, stop, replace, or route the authoritative Relay.
    /// </summary>
    internal sealed class ProductionDirectMultipairShadowRuntime : IAsyncDisposable
    {
        internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

        private readonly bool enabled;
        private readonly Guid localDeviceId;
        private readonly byte[] signalKeyId;
        private readonly DeviceIdentity identity;
        private readonly ProductionDirectTransportSupervisor supervisor;
        private readonly Func<IProductionDirectShadowSignaling> signalingFactory;
        private readonly Func<IProductionDirectShadowNativeV4> nativeFactory;
        private readonly Func<CancellationToken, Task<IReadOnlyList<ProductionDirectPeerAuthorization>>> peerProvider;
        private readonly Func<bool> nativeCapability;
        private readonly IProductionDirectShadowPlatform platform;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Func<long> clockMilliseconds;
        private readonly ProductionDirectTransportDiagnostics diagnostics;
        private readonly IProductionDirectShadowSignaling sharedSignaling;
        private readonly Func<ProductionDirectDataPromotionHandoff, CancellationToken, Task<bool>>
            promoteToDataRuntimeAsync;
        private readonly object sync = new();
        private readonly CancellationTokenSource lifetime = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private SignalingLane lane;
        private long laneGeneration;
        private Task worker;
        private Attempt active;
        private PromotionLease currentPromotion;
        private long promotionGeneration;
        private bool relayConnected;
        private bool handoffActive;
        private long relayGeneration;
        private long networkGeneration;
        private int disposed;
        private ProductionDirectShadowSnapshot snapshot;

        public ProductionDirectMultipairShadowRuntime(
            bool enabled,
            Guid localDeviceId,
            string signalKeyId,
            DeviceIdentity identity,
            ProductionDirectTransportSupervisor supervisor,
            Func<IProductionDirectShadowSignaling> signalingFactory,
            Func<CancellationToken, Task<IReadOnlyList<ProductionDirectPeerAuthorization>>> peerProvider,
            Func<IProductionDirectShadowNativeV4> nativeFactory = null,
            IProductionDirectShadowPlatform platform = null,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<long> clockMilliseconds = null,
            Func<bool> nativeCapability = null,
            ProductionDirectTransportDiagnostics diagnostics = null,
            IProductionDirectShadowSignaling sharedSignaling = null,
            Func<ProductionDirectDataPromotionHandoff, CancellationToken, Task<bool>>
                promoteToDataRuntimeAsync = null)
        {
            if (localDeviceId == Guid.Empty) throw new ArgumentException("A local device is required.");
            if (!ProductionDirectTransportSignalingClient.ValidKeyId(signalKeyId))
                throw new ArgumentException("A valid responder device key ID is required.", nameof(signalKeyId));
            this.enabled = enabled;
            this.localDeviceId = localDeviceId;
            this.signalKeyId = Encoding.UTF8.GetBytes(signalKeyId);
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            this.signalingFactory = signalingFactory ?? throw new ArgumentNullException(nameof(signalingFactory));
            this.peerProvider = peerProvider ?? throw new ArgumentNullException(nameof(peerProvider));
            this.nativeFactory = nativeFactory ?? (() => new ProductionDirectNativeBoundaryV4(enabled: true));
            this.nativeCapability = nativeCapability ?? (() => ProductionDirectNativeCapabilityV4.IsUsable());
            this.platform = platform ?? new ProductionWindowsDirectShadowPlatform();
            this.delay = delay ?? Task.Delay;
            this.clockMilliseconds = clockMilliseconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            this.diagnostics = diagnostics;
            this.sharedSignaling = sharedSignaling;
            this.promoteToDataRuntimeAsync = promoteToDataRuntimeAsync;
            if (sharedSignaling != null)
            {
                lane = new SignalingLane(++laneGeneration, sharedSignaling, ownsSignaling: false)
                {
                    Started = true
                };
            }
            snapshot = new ProductionDirectShadowSnapshot
            {
                State = enabled ? ProductionDirectShadowState.WaitingForRelay : ProductionDirectShadowState.Disabled,
                ReasonCode = enabled ? string.Empty : "FEATURE_DISABLED"
            };
        }

        public ProductionDirectShadowSnapshot Snapshot { get { lock (sync) return snapshot; } }

        /// <summary>
        /// Feeds pre-promotion signalling from the single process-wide reader.
        /// In shared mode this runtime never calls Start/ReadSignalsAsync on
        /// the signalling client and never disposes that shared client.
        /// </summary>
        internal bool TryDispatchPrePromotionSignal(TransportSignalEnvelopeV1 envelope)
        {
            if (sharedSignaling == null || envelope == null ||
                Volatile.Read(ref disposed) != 0 ||
                (int)envelope.SignalType is not (>= 1 and <= 5) and not (>= 11 and <= 14) &&
                envelope.SignalType != TransportSignalTypeV1.PathClose)
                return false;
            SignalingLane current;
            lock (sync) current = lane;
            return current?.Incoming.Writer.TryWrite(envelope.Clone()) == true;
        }

        public bool Start()
        {
            if (!enabled || Volatile.Read(ref disposed) != 0) return false;
            lock (sync)
            {
                if (worker != null) return false;
                worker = RunAsync(lifetime.Token);
                if (relayConnected) Wake();
                return true;
            }
        }

        public void ObserveRelayConnected(bool connected)
        {
            ObserveRelayConnected(connected, relayGeneration);
        }

        public void ObserveRelayConnected(bool connected, long generation)
        {
            Attempt current = null;
            lock (sync)
            {
                relayGeneration = Math.Max(0, generation);
                relayConnected = connected;
                supervisor.ObserveRelayConnected(connected);
                if (!connected) current = active;
                if (!connected && !handoffActive &&
                    snapshot.State is not ProductionDirectShadowState.Disabled and not ProductionDirectShadowState.Stopped)
                    PublishLocked(ProductionDirectShadowState.WaitingForRelay, "RELAY_UNAVAILABLE", current);
            }
            if (current != null) Retire(current, "RELAY_UNAVAILABLE", false);
            if (connected) Wake();
        }

        public void NotifyNetworkChanged()
        {
            Attempt current;
            bool promoted;
            lock (sync)
            {
                networkGeneration = checked(networkGeneration + 1);
                current = active;
                promoted = handoffActive;
                if (current == null && promoted)
                    InvalidatePromotionLocked(currentPromotion);
                if (current == null)
                    ObserveDiagnosticsLocked(snapshot.State, "NETWORK_CHANGED", null);
            }
            if (current != null) Retire(current, "NETWORK_CHANGED", true);
            else if (promoted)
            {
                supervisor.NotifyNetworkChanged();
                Publish(relayConnected ? ProductionDirectShadowState.RelayOnly :
                    ProductionDirectShadowState.WaitingForRelay, "NETWORK_CHANGED", null);
                if (relayConnected) Wake();
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await wake.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (!RelayConnected()) continue;
                if (HandoffActive()) continue;
                try { await RunAttemptAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Attempt current; lock (sync) current = active;
                    if (current != null) Retire(current, "DIRECT_MULTIPAIR_FAILED", false);
                    else Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_MULTIPAIR_FAILED", null);
                }
                if (!token.IsCancellationRequested && RelayConnected() && !HandoffActive())
                {
                    try { await delay(RetryDelay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    Wake();
                }
            }
        }

        private async Task RunAttemptAsync(CancellationToken token)
        {
            Publish(ProductionDirectShadowState.Configuring, string.Empty, null);
            if (!nativeCapability())
            {
                Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_ABI_V4_UNAVAILABLE", null);
                return;
            }
            var currentLane = await GetLaneAsync(token).ConfigureAwait(false);
            var configResult = await currentLane.Signaling.FetchConfigAsync(token).ConfigureAwait(false);
            if (configResult.Status != ProductionDirectSignalingCallStatus.Success ||
                !ProductionDirectTransportSignalingClient.HasMultipairShadowCapabilities(configResult.Value))
            {
                Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_MULTIPAIR_NOT_NEGOTIATED", null);
                return;
            }
            if (!currentLane.Started)
            {
                if (!currentLane.Signaling.Start())
                {
                    currentLane.MarkTerminal();
                    Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_SIGNAL_START_FAILED", null);
                    return;
                }
                currentLane.Started = true;
                currentLane.Reader = PumpSignalsAsync(currentLane, token);
            }
            var peers = await peerProvider(token).ConfigureAwait(false);
            if (peers == null || !peers.Any(value => value != null))
            {
                DisposePeers(peers, null);
                Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_NO_AUTHORIZED_PEER", null);
                return;
            }
            InboundOffer inbound = null;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(AttemptTimeout);
                try { inbound = await WaitForOfferAsync(currentLane, peers, configResult.Value, wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_OFFER_TIMEOUT", null);
                    return;
                }
                DisposePeers(peers, inbound.Peer);
                var attempt = new Attempt(inbound, Guid.NewGuid(), configResult.Value, currentLane);
                inbound.TransferOwnership();
                if (!supervisor.TryBeginAttempt(attempt.Peer.DeviceId, attempt.ConnectionId,
                        attempt.Ticket.Ticket.PathEpoch, out var supervisorToken, out _))
                {
                    attempt.Dispose();
                    Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_ATTEMPT_REJECTED", null);
                    return;
                }
                attempt.SupervisorToken = supervisorToken;
                lock (sync)
                {
                    if (disposed != 0 || !relayConnected || active != null)
                    {
                        supervisor.TryFallbackAttempt(supervisorToken, "RELAY_UNAVAILABLE");
                        attempt.Dispose();
                        PublishLocked(ProductionDirectShadowState.WaitingForRelay, "RELAY_UNAVAILABLE", null);
                        return;
                    }
                    active = attempt;
                }
                var gather = await platform.PrepareGatherAsync(configResult.Value.StunServers, token).ConfigureAwait(false);
                if (!IsCurrent(attempt)) return;
                var native = nativeFactory();
                attempt.Native = native;
                if (!native.Snapshot.NativeAvailable || native.Gather(new ProductionDirectGatherRequestV4
                    {
                        Epoch = attempt.Epoch,
                        AttemptId = attempt.AttemptId,
                        NetworkHandle = 0,
                        LocalBind = gather.LocalBind,
                        AdvertisedLocalAddress = gather.AdvertisedLocalAddress,
                        NetworkCost = 0,
                        CandidateExpiresAtMilliseconds = attempt.Ticket.Ticket.ExpiresAtMs,
                        StunServers = gather.StunServers,
                        GatherTimeoutMilliseconds = 1_500
                    }) != ProductionDirectNativeCommandResult.Accepted)
                {
                    Retire(attempt, "DIRECT_ABI_V4_UNAVAILABLE", false);
                    return;
                }
                Publish(ProductionDirectShadowState.Gathering, string.Empty, attempt);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                var remaining = Math.Max(1, Math.Min(AttemptTimeout.TotalMilliseconds,
                    attempt.Ticket.Ticket.ExpiresAtMs - clockMilliseconds()));
                bounded.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                try { await DriveAsync(attempt, bounded.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (!attempt.ShadowReady) Retire(attempt, "DIRECT_ATTEMPT_TIMEOUT", false);
                }
            }
            finally
            {
                inbound?.Dispose();
                if (inbound == null) DisposePeers(peers, null);
            }
        }

        private async Task DriveAsync(Attempt attempt, CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsCurrent(attempt))
            {
                foreach (var item in attempt.Native.Poll(32))
                {
                    if (!IsCurrent(attempt)) return;
                    // Generation is handle-global, including native diagnostics for
                    // stale commands. Consume that slot before filtering its owner,
                    // otherwise the next live-owner event looks like a gap.
                    if (item.Generation != attempt.LastNativeGeneration + 1)
                    {
                        Retire(attempt, "DIRECT_NATIVE_EVENT_FENCE", false);
                        return;
                    }
                    attempt.LastNativeGeneration = item.Generation;
                    if (item.Kind == ProductionDirectNativeEventKindV4.IgnoredStale) continue;
                    if (item.Epoch != attempt.Epoch || item.AttemptId != attempt.AttemptId)
                    {
                        Retire(attempt, "DIRECT_NATIVE_EVENT_FENCE", false);
                        return;
                    }
                    if (!HandleNativeEvent(attempt, item)) return;
                }

                while (attempt.Lane.Incoming.Reader.TryRead(out var envelope))
                {
                    if (!Matches(attempt, envelope)) continue;
                    if (!AcceptEnvelopeCursor(attempt, envelope))
                    {
                        Retire(attempt, "DIRECT_SIGNAL_CURSOR_REJECTED", false);
                        return;
                    }
                    if (!HandleSignal(attempt, envelope))
                    {
                        Retire(attempt, "DIRECT_SIGNAL_PAYLOAD_REJECTED", false);
                        return;
                    }
                    attempt.LastRemoteSequence = envelope.SignalSeq;
                    attempt.LastRemoteAcknowledgement = envelope.SignalAck;
                    if (!TrySubmitPendingBatches(attempt)) return;
                }

                if (!TrySubmitPendingBatches(attempt) ||
                    !await TryCommitPendingNominationAsync(attempt, token).ConfigureAwait(false) ||
                    !TryComplete(attempt)) return;
                if (attempt.Promoted)
                {
                    CompletePromotionHandoff(attempt);
                    return;
                }
                await delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
            }
        }

        private bool HandleNativeEvent(Attempt attempt, ProductionDirectNativeEventV4 item)
        {
            if (item.Kind == ProductionDirectNativeEventKindV4.IdentityReady)
            {
                if (attempt.LocalCertificateSha256 != null || item.OpaquePayload?.Length is not (> 0 and <= 2_048))
                    return Fail(attempt, "DIRECT_IDENTITY_REJECTED");
                attempt.LocalCertificateSha256 = SHA256.HashData(item.OpaquePayload);
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.CandidateBatch)
            {
                if (item.BatchSequence != attempt.LocalCandidateBatchSequence + 1 ||
                    item.TotalCount != attempt.LocalCandidates.Count + item.Candidates.Count || item.TotalCount > 32)
                    return Fail(attempt, "DIRECT_CANDIDATE_BATCH_REJECTED");
                foreach (var candidate in item.Candidates)
                    if (!attempt.LocalCandidates.TryAdd(candidate.CandidateId, candidate))
                        return Fail(attempt, "DIRECT_CANDIDATE_DUPLICATED");
                attempt.LocalCandidateBatchSequence = item.BatchSequence;
                attempt.LocalGatherComplete = item.Complete;
                if (!SendLocalCandidates(attempt, item.Candidates, item.Complete))
                    return Fail(attempt, "DIRECT_SIGNAL_QUEUE_FAILED");
                if (item.Complete)
                {
                    attempt.LocalCandidatePlanComplete = true;
                    if (attempt.RemoteGatherComplete) supervisor.MarkGatheringComplete(attempt.SupervisorToken);
                }
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.PairValidated)
            {
                if (item.CheckRoundId != attempt.CheckRoundId || item.Pair == null ||
                    !attempt.PlannedPairs.TryGetValue(item.Pair.Key, out var planned) ||
                    !planned.Equals(item.Pair) || !attempt.ValidatedPairs.TryAdd(item.Pair.Key, item.Pair))
                    return Fail(attempt, "DIRECT_PAIR_VALIDATION_REJECTED");
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.ChecksComplete)
            {
                if (item.CheckRoundId != attempt.CheckRoundId || item.TotalCount != attempt.PlannedPairs.Count ||
                    item.ValidatedPairCount != attempt.ValidatedPairs.Count) return Fail(attempt, "DIRECT_CHECK_RESULT_REJECTED");
                attempt.ChecksComplete = true;
                if (item.ValidatedPairCount == 0) return Fail(attempt, "DIRECT_CHECKS_EXHAUSTED");
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.NominationReady)
            {
                if (!attempt.CommitSubmitted || attempt.AckSent || !MatchesSelection(attempt, item.CheckRoundId,
                        item.NominationId, item.Pair)) return Fail(attempt, "DIRECT_NOMINATION_READY_REJECTED");
                attempt.NominationReady = true;
                if (!SendNominationAck(attempt)) return Fail(attempt, "DIRECT_NOMINATION_ACK_FAILED");
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.SignRequest)
            {
                if (!attempt.CommitSubmitted || item.RequestId == 0 || item.OpaquePayload?.Length is not (> 0 and <= 2_048))
                    return Fail(attempt, "DIRECT_SIGN_REQUEST_REJECTED");
                var signature = identity.Sign(item.OpaquePayload);
                try
                {
                    if (attempt.Native.SubmitSignature(attempt.Epoch, attempt.AttemptId, item.RequestId, signature) !=
                        ProductionDirectNativeCommandResult.Accepted)
                        return Fail(attempt, "DIRECT_SIGNATURE_REJECTED");
                }
                finally { CryptographicOperations.ZeroMemory(signature); }
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.ShadowReady)
            {
                if (!attempt.CommitSubmitted || !attempt.AckSent || attempt.LocalPathBinding != null ||
                    !MatchesSelection(attempt, item.CheckRoundId, item.NominationId, item.Pair) ||
                    item.DirectPathBinding?.Length != 32 || IsAllZero(item.DirectPathBinding))
                    return Fail(attempt, "DIRECT_SHADOW_READY_REJECTED");
                attempt.LocalPathBinding = item.DirectPathBinding.ToArray();
                if (!SendNominationResult(attempt)) return Fail(attempt, "DIRECT_RESULT_FAILED");
            }
            else if (item.Kind == ProductionDirectNativeEventKindV4.DirectFailed)
                return Fail(attempt, "DIRECT_NATIVE_FAILED");
            return true;
        }

        private bool HandleSignal(Attempt attempt, TransportSignalEnvelopeV1 envelope)
        {
            byte[] plaintext = null;
            try
            {
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(attempt.Peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1) return false;
                if (envelope.SignalType == TransportSignalTypeV1.CandidateUpdate &&
                    payload.SignalCase == TransportSignalPayloadV1.SignalOneofCase.CandidateUpdate)
                    return HandleRemoteCandidateUpdate(attempt, payload.CandidateUpdate, plaintext);
                if (envelope.SignalType == TransportSignalTypeV1.ConnectivityCheckBatchV2 &&
                    payload.SignalCase == TransportSignalPayloadV1.SignalOneofCase.ConnectivityCheckBatchV2)
                    return HandleCheckBatch(attempt, payload.ConnectivityCheckBatchV2, plaintext);
                if (envelope.SignalType == TransportSignalTypeV1.NominationV2 &&
                    payload.SignalCase == TransportSignalPayloadV1.SignalOneofCase.NominationV2)
                    return HandleNomination(attempt, payload.NominationV2, plaintext);
                if (envelope.SignalType == TransportSignalTypeV1.NominationResultV2 &&
                    payload.SignalCase == TransportSignalPayloadV1.SignalOneofCase.NominationResultV2)
                    return HandlePeerResult(attempt, payload.NominationResultV2, plaintext);
                return false;
            }
            catch (Exception error) when (error is InvalidDataException or CryptographicException or
                InvalidProtocolBufferException or ArgumentException or OverflowException)
            { return false; }
            finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
        }

        private bool HandleRemoteCandidateUpdate(Attempt attempt, DirectCandidateUpdateV1 update, byte[] plaintext)
        {
            var canonicalPayload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                CandidateUpdate = new DirectCandidateUpdateV1 { GatheringComplete = update.GatheringComplete }
            };
            canonicalPayload.CandidateUpdate.Candidates.Add(update.Candidates.Select(CanonicalCandidate));
            if (!CanonicalEquals(plaintext, canonicalPayload) || attempt.RemoteGatherComplete ||
                update.Candidates.Count > 8 || (update.Candidates.Count == 0 && !update.GatheringComplete) ||
                attempt.RemoteCandidates.Count + update.Candidates.Count > 32) return false;
            var incoming = update.Candidates.Select(value =>
                ProductionDirectMultipairPlanner.Decode(value, attempt.Ticket.Ticket.ExpiresAtMs)).ToArray();
            var hosts = new HashSet<Guid>(attempt.RemoteCandidates.Values
                .Where(value => value.Kind == ProductionDirectCandidateKindV4.Host)
                .Select(value => value.CandidateId));
            foreach (var candidate in incoming.Where(value => value.Kind == ProductionDirectCandidateKindV4.Host))
                hosts.Add(candidate.CandidateId);
            if (incoming.Any(value => value.Kind == ProductionDirectCandidateKindV4.ServerReflexive &&
                !hosts.Contains(value.RelatedCandidateId))) return false;
            foreach (var candidate in incoming)
            {
                if (!AddRemoteCandidate(attempt, candidate)) return false;
            }
            attempt.RemoteGatherComplete = update.GatheringComplete;
            if (attempt.RemoteGatherComplete && attempt.LocalGatherComplete)
                supervisor.MarkGatheringComplete(attempt.SupervisorToken);
            return true;
        }

        private bool HandleCheckBatch(Attempt attempt, DirectConnectivityCheckBatchV2 value, byte[] plaintext)
        {
            var canonicalPayload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                ConnectivityCheckBatchV2 = new DirectConnectivityCheckBatchV2
                {
                    CheckRoundId = value.CheckRoundId,
                    ConnectivityNonce = value.ConnectivityNonce,
                    BatchSeq = value.BatchSeq,
                    PlanComplete = value.PlanComplete
                }
            };
            canonicalPayload.ConnectivityCheckBatchV2.Pairs.Add(value.Pairs.Select(CanonicalPair));
            if (!CanonicalEquals(plaintext, canonicalPayload) ||
                value.CheckRoundId.Length != 16 || IsAllZero(value.CheckRoundId.Span) ||
                value.ConnectivityNonce.Length != 16 || IsAllZero(value.ConnectivityNonce.Span) ||
                value.BatchSeq != attempt.RemoteCheckBatchSequence + 1 || value.Pairs.Count > 8 ||
                (value.Pairs.Count == 0 && !value.PlanComplete) || attempt.RemotePlanComplete ||
                (value.PlanComplete && (!attempt.LocalGatherComplete || !attempt.RemoteGatherComplete)) ||
                attempt.PendingPairCount + value.Pairs.Count > 32) return false;
            var round = GuidFromBytes(value.CheckRoundId.Span);
            if (attempt.CheckRoundId == Guid.Empty)
            {
                if (value.BatchSeq != 1) return false;
                attempt.CheckRoundId = round;
                attempt.ConnectivityNonce = value.ConnectivityNonce.ToByteArray();
            }
            else if (!FixedId(attempt.CheckRoundId, round) ||
                !CryptographicOperations.FixedTimeEquals(attempt.ConnectivityNonce, value.ConnectivityNonce.Span))
                return false;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var wirePairs = new List<DirectCandidatePairV1>();
            foreach (var pair in value.Pairs)
            {
                if (pair == null || pair.InitiatorCandidateId.Length != 16 ||
                    pair.ResponderCandidateId.Length != 16 || IsAllZero(pair.InitiatorCandidateId.Span) ||
                    IsAllZero(pair.ResponderCandidateId.Span)) return false;
                var key = Convert.ToHexString(pair.InitiatorCandidateId.Span) + ":" +
                    Convert.ToHexString(pair.ResponderCandidateId.Span);
                if (!unique.Add(key) || attempt.WirePairKeys.Contains(key)) return false;
                wirePairs.Add(pair.Clone());
                attempt.WirePairKeys.Add(key);
            }
            attempt.PendingBatches.Enqueue(new PendingBatch(value.BatchSeq, wirePairs.AsReadOnly(), value.PlanComplete));
            attempt.PendingPairCount += value.Pairs.Count;
            attempt.RemoteCheckBatchSequence = value.BatchSeq;
            attempt.RemotePlanComplete = value.PlanComplete;
            return true;
        }

        private bool HandleNomination(Attempt attempt, DirectNominationV2 value, byte[] plaintext)
        {
            var canonicalPayload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationV2 = new DirectNominationV2
                {
                    NominationId = value.NominationId,
                    CheckRoundId = value.CheckRoundId,
                    WinningPair = value.WinningPair == null ? null : CanonicalPair(value.WinningPair)
                }
            };
            if (!CanonicalEquals(plaintext, canonicalPayload) || attempt.PendingNomination != null ||
                !attempt.RemotePlanComplete || value.NominationId.Length != 16 ||
                IsAllZero(value.NominationId.Span) || value.CheckRoundId.Length != 16 ||
                !FixedBytes(value.CheckRoundId.Span, GuidBytes(attempt.CheckRoundId)) || value.WinningPair == null)
                return false;
            var pairKey = PairKey(value.WinningPair);
            if (!attempt.PlannedPairs.TryGetValue(pairKey, out var pair) ||
                !ProductionDirectMultipairPlanner.PairIdsEqual(value.WinningPair, pair)) return false;
            attempt.NominationId = GuidFromBytes(value.NominationId.Span);
            attempt.SelectedPair = pair;
            attempt.PendingNomination = value.Clone();
            return true;
        }

        private bool HandlePeerResult(Attempt attempt, DirectNominationResultV2 value, byte[] plaintext)
        {
            var canonicalPayload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationResultV2 = new DirectNominationResultV2
                {
                    NominationId = value.NominationId,
                    CheckRoundId = value.CheckRoundId,
                    WinningPair = value.WinningPair == null ? null : CanonicalPair(value.WinningPair),
                    Result = value.Result,
                    ReasonCode = value.ReasonCode,
                    DirectPathBinding = value.DirectPathBinding
                }
            };
            if (!CanonicalEquals(plaintext, canonicalPayload) || attempt.PeerPathBinding != null ||
                value.Result != DirectPathResultV1.Accepted || !string.IsNullOrEmpty(value.ReasonCode) ||
                value.DirectPathBinding.Length != 32 || IsAllZero(value.DirectPathBinding.Span) ||
                value.WinningPair == null || !MatchesSelection(attempt, value.CheckRoundId.Span,
                    value.NominationId.Span, value.WinningPair)) return false;
            attempt.PeerPathBinding = value.DirectPathBinding.ToByteArray();
            return true;
        }

        private bool TrySubmitPendingBatches(Attempt attempt)
        {
            if (attempt.LocalCertificateSha256 == null) return true;
            while (attempt.PendingBatches.TryPeek(out var batch))
            {
                var pairs = new List<ProductionDirectCandidatePairV4>();
                var referencesUnknownRemoteCandidate = false;
                var referencesUnknownLocalCandidate = false;
                foreach (var wire in batch.Pairs)
                {
                    var initiatorId = GuidFromBytes(wire.InitiatorCandidateId.Span);
                    var responderId = GuidFromBytes(wire.ResponderCandidateId.Span);
                    var remoteKnown = attempt.RemoteCandidates.TryGetValue(initiatorId, out var initiator);
                    var localKnown = attempt.LocalCandidates.TryGetValue(responderId, out var responder);
                    if (!remoteKnown || !localKnown)
                    {
                        referencesUnknownRemoteCandidate |= !remoteKnown;
                        referencesUnknownLocalCandidate |= !localKnown;
                        break;
                    }
                    if (initiator.Kind == ProductionDirectCandidateKindV4.Host &&
                        responder.Kind == ProductionDirectCandidateKindV4.Host &&
                        !ProductionDirectMultipairPlanner.SameConservativeLan(
                            initiator.Endpoint.Address, responder.Endpoint.Address))
                        return Fail(attempt, "DIRECT_HOST_ROUTE_UNSAFE");
                    var rank = checked((uint)attempt.PlannedPairs.Count + (uint)pairs.Count + 1);
                    pairs.Add(new ProductionDirectCandidatePairV4
                    {
                        Rank = rank,
                        Initiator = initiator,
                        Responder = responder
                    });
                }
                if (referencesUnknownRemoteCandidate || referencesUnknownLocalCandidate)
                {
                    if ((referencesUnknownRemoteCandidate && attempt.RemoteGatherComplete) ||
                        (referencesUnknownLocalCandidate && attempt.LocalGatherComplete))
                        return Fail(attempt, "DIRECT_PAIR_REFERENCES_UNKNOWN_CANDIDATE");
                    return true;
                }
                ProductionDirectNativeCommandResult result;
                if (!attempt.NativeChecksStarted)
                {
                    var serverKeyId = Encoding.UTF8.GetString(attempt.Ticket.Ticket.ServerSigningKeyId.Span);
                    if (!attempt.Config.TicketSigningKeys.TryGetValue(serverKeyId, out var serverKey))
                        return Fail(attempt, "DIRECT_TICKET_KEY_MISSING");
                    attempt.LocalSendProbeKey = ProductionDirectTransportCrypto.DeriveProbeKeyV2(
                        attempt.Peer.PairwiseKey, attempt.ConnectionId, attempt.Epoch, attempt.CheckRoundId,
                        localDeviceId, attempt.Peer.DeviceId);
                    attempt.PeerReceiveProbeKey = ProductionDirectTransportCrypto.DeriveProbeKeyV2(
                        attempt.Peer.PairwiseKey, attempt.ConnectionId, attempt.Epoch, attempt.CheckRoundId,
                        attempt.Peer.DeviceId, localDeviceId);
                    result = attempt.Native.BeginChecks(new ProductionDirectBeginChecksRequestV4
                    {
                        Epoch = attempt.Epoch, AttemptId = attempt.AttemptId, ConnectionId = attempt.ConnectionId,
                        LocalDeviceId = localDeviceId, PeerDeviceId = attempt.Peer.DeviceId, NetworkHandle = 0,
                        NowMilliseconds = clockMilliseconds(), TimeoutMilliseconds = 10_000,
                        Role = ProductionDirectNativeRole.Responder, Ticket = attempt.Ticket.WireBytes,
                        TicketId = GuidFromBytes(attempt.Ticket.Ticket.TicketId.Span),
                        CheckRoundId = attempt.CheckRoundId, ConnectivityNonce = attempt.ConnectivityNonce,
                        ServerSigningPublicKey = serverKey.PublicKey,
                        LocalSendProbeHmacKey = attempt.LocalSendProbeKey,
                        PeerReceiveProbeHmacKey = attempt.PeerReceiveProbeKey,
                        BatchSequence = batch.Sequence,
                        TotalPairCount = checked((uint)(attempt.PlannedPairs.Count + pairs.Count)),
                        PlanComplete = batch.Complete, Pairs = pairs.AsReadOnly()
                    });
                    if (result == ProductionDirectNativeCommandResult.Accepted) attempt.NativeChecksStarted = true;
                }
                else
                {
                    result = attempt.Native.AppendCheckPairs(new ProductionDirectAppendPairsRequestV4
                    {
                        Epoch = attempt.Epoch, AttemptId = attempt.AttemptId, CheckRoundId = attempt.CheckRoundId,
                        BatchSequence = batch.Sequence,
                        TotalPairCount = checked((uint)(attempt.PlannedPairs.Count + pairs.Count)),
                        PlanComplete = batch.Complete, Pairs = pairs.AsReadOnly()
                    });
                }
                if (result != ProductionDirectNativeCommandResult.Accepted)
                    return Fail(attempt, "DIRECT_CHECK_BATCH_NATIVE_REJECTED");
                foreach (var pair in pairs) attempt.PlannedPairs.Add(pair.Key, pair);
                attempt.PendingBatches.Dequeue();
            }
            return true;
        }

        private async Task<bool> TryCommitPendingNominationAsync(
            Attempt attempt,
            CancellationToken token)
        {
            if (attempt.PendingNomination == null || attempt.CommitSubmitted ||
                !attempt.ValidatedPairs.TryGetValue(attempt.SelectedPair.Key, out var validated)) return true;
            // A nomination cannot consume the retained socket until every final
            // pair batch has crossed the native boundary. RemotePlanComplete is
            // receipt state; an unresolved trickled candidate can still leave
            // that final batch queued locally.
            if (!attempt.NativeChecksStarted || !attempt.RemotePlanComplete ||
                attempt.PendingBatches.Count != 0) return true;
            if (!validated.Equals(attempt.SelectedPair)) return Fail(attempt, "DIRECT_NOMINATION_PAIR_MISMATCH");
            var proofNonce = RandomNumberGenerator.GetBytes(32);
            try
            {
                var nomination = new ProductionDirectCommitRequestV4
                {
                    Epoch = attempt.Epoch, AttemptId = attempt.AttemptId, CheckRoundId = attempt.CheckRoundId,
                    NominationId = attempt.NominationId, WinningPair = attempt.SelectedPair,
                    Role = ProductionDirectNativeRole.Responder, TimeoutMilliseconds = 10_000,
                    PeerCertificateSha256 = attempt.RemoteCertificateSha256,
                    PeerDeviceSigningPublicKey = attempt.Peer.SigningPublicKey,
                    LocalProofNonce = proofNonce, NowMilliseconds = clockMilliseconds()
                };
                if (promoteToDataRuntimeAsync != null)
                {
                    if (attempt.Native is not IProductionDirectPromotableNativeV4 promotable)
                        return Fail(attempt, "DIRECT_PROMOTION_UNAVAILABLE");
                    PromotionLease lease;
                    lock (sync)
                    {
                        if (disposed != 0 || !ReferenceEquals(active, attempt) ||
                            relayGeneration < 1 || currentPromotion != null)
                            return false;
                        lease = new PromotionLease(++promotionGeneration, attempt);
                        currentPromotion = lease;
                    }
                    using var handoff = new ProductionDirectDataPromotionHandoff(
                        attempt.Peer.DeviceId,
                        attempt.ConnectionId,
                        attempt.Peer.KeyEpoch,
                        relayGeneration,
                        attempt.LocalSequence,
                        attempt.LastRemoteSequence,
                        attempt.LastRemoteAcknowledgement,
                        attempt.Peer.PairwiseKey,
                        signalKeyId,
                        promotable,
                        nomination,
                        identity.Sign,
                        binding => ObservePromotedReady(lease, binding),
                        reason => ObservePromotedFailure(lease, reason));
                    bool promoted;
                    try
                    {
                        promoted = await promoteToDataRuntimeAsync(handoff, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        promoted = false;
                    }
                    if (!promoted)
                    {
                        lock (sync) InvalidatePromotionLocked(lease);
                        return Fail(attempt, "DIRECT_PROMOTION_FAILED");
                    }
                    IProductionDirectShadowNativeV4 transferred;
                    lock (sync)
                    {
                        if (disposed != 0 || !ReferenceEquals(active, attempt) ||
                            !ReferenceEquals(currentPromotion, lease) || lease.Retired)
                            return false;
                        transferred = attempt.Native;
                        attempt.Native = null;
                        attempt.CommitSubmitted = true;
                        attempt.Promoted = true;
                    }
                    try { transferred?.Dispose(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    return true;
                }

                var result = attempt.Native.CommitNomination(nomination);
                if (result != ProductionDirectNativeCommandResult.Accepted)
                    return Fail(attempt, "DIRECT_NOMINATION_NATIVE_REJECTED");
                attempt.CommitSubmitted = true;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(proofNonce); }
        }

        private bool SendLocalCandidates(
            Attempt attempt, IReadOnlyList<ProductionDirectCandidateV4> additions, bool complete)
        {
            if (attempt.LocalCertificateSha256 == null) return false;
            TransportSignalPayloadV1 payload;
            TransportSignalTypeV1 type;
            if (!attempt.AnswerSent)
            {
                if (additions.Count == 0 || additions[0].Kind != ProductionDirectCandidateKindV4.Host) return false;
                payload = new TransportSignalPayloadV1
                {
                    FormatVersion = 1,
                    PathAnswer = new DirectPathAnswerV1
                    {
                        TicketId = attempt.Ticket.Ticket.TicketId,
                        QuicCertificateSha256 = ByteString.CopyFrom(attempt.LocalCertificateSha256),
                        GatheringComplete = complete,
                        Result = DirectPathResultV1.Accepted,
                        ReasonCode = string.Empty
                    }
                };
                payload.PathAnswer.Candidates.Add(additions.Select(ProductionDirectMultipairPlanner.Encode));
                type = TransportSignalTypeV1.PathAnswer;
            }
            else
            {
                payload = new TransportSignalPayloadV1
                {
                    FormatVersion = 1,
                    CandidateUpdate = new DirectCandidateUpdateV1 { GatheringComplete = complete }
                };
                payload.CandidateUpdate.Candidates.Add(additions.Select(ProductionDirectMultipairPlanner.Encode));
                type = TransportSignalTypeV1.CandidateUpdate;
            }
            if (!SendEncrypted(attempt, type, payload)) return false;
            attempt.AnswerSent = true;
            Publish(ProductionDirectShadowState.Negotiating, string.Empty, attempt);
            return true;
        }

        private bool SendNominationAck(Attempt attempt)
        {
            var payload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationAckV2 = new DirectNominationAckV2
                {
                    NominationId = ByteString.CopyFrom(GuidBytes(attempt.NominationId)),
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(attempt.CheckRoundId)),
                    WinningPair = ProductionDirectMultipairPlanner.EncodePair(attempt.SelectedPair),
                    Result = DirectPathResultV1.Accepted,
                    ReasonCode = string.Empty
                }
            };
            if (!SendEncrypted(attempt, TransportSignalTypeV1.NominationAckV2, payload)) return false;
            attempt.AckSent = true;
            return true;
        }

        private bool SendNominationResult(Attempt attempt)
        {
            if (attempt.LocalResultSent || attempt.LocalPathBinding?.Length != 32) return false;
            var payload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationResultV2 = new DirectNominationResultV2
                {
                    NominationId = ByteString.CopyFrom(GuidBytes(attempt.NominationId)),
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(attempt.CheckRoundId)),
                    WinningPair = ProductionDirectMultipairPlanner.EncodePair(attempt.SelectedPair),
                    Result = DirectPathResultV1.Accepted,
                    ReasonCode = string.Empty,
                    DirectPathBinding = ByteString.CopyFrom(attempt.LocalPathBinding)
                }
            };
            if (!SendEncrypted(attempt, TransportSignalTypeV1.NominationResultV2, payload)) return false;
            attempt.LocalResultSent = true;
            return true;
        }

        private bool TryComplete(Attempt attempt)
        {
            if (attempt.LocalPathBinding == null || attempt.PeerPathBinding == null) return true;
            if (!CryptographicOperations.FixedTimeEquals(attempt.LocalPathBinding, attempt.PeerPathBinding))
                return Fail(attempt, "DIRECT_SHADOW_BINDING_MISMATCH");
            if (attempt.ShadowReady) return true;
            supervisor.MarkShadowReady(attempt.SupervisorToken, attempt.LocalPathBinding);
            attempt.ShadowReady = true;
            attempt.ReleaseSecretsAfterReady();
            Publish(ProductionDirectShadowState.ShadowReadyNotRouted, string.Empty, attempt);
            return true;
        }

        private bool SendEncrypted(
            Attempt attempt, TransportSignalTypeV1 type, TransportSignalPayloadV1 payload)
        {
            var plaintext = payload.ToByteArray();
            try
            {
                var metadata = new TransportSignalEnvelopeV1
                {
                    ProtocolVersion = 1, SignalType = type,
                    ConnectionId = ByteString.CopyFrom(GuidBytes(attempt.ConnectionId)),
                    SenderDeviceId = ByteString.CopyFrom(GuidBytes(localDeviceId)),
                    RecipientDeviceId = ByteString.CopyFrom(GuidBytes(attempt.Peer.DeviceId)),
                    PathEpoch = attempt.Epoch, SignalSeq = ++attempt.LocalSequence,
                    SignalAck = attempt.LastRemoteSequence, SentAtMs = clockMilliseconds(),
                    KeyId = ByteString.CopyFrom(signalKeyId)
                };
                return attempt.Lane.Signaling.TrySend(
                    ProductionDirectTransportCrypto.EncryptSignal(attempt.Peer.PairwiseKey, metadata, plaintext));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private async Task<InboundOffer> WaitForOfferAsync(
            SignalingLane currentLane,
            IReadOnlyList<ProductionDirectPeerAuthorization> peers,
            ProductionDirectTransportServerConfig config,
            CancellationToken token)
        {
            var authorized = peers.Where(value => value != null).GroupBy(value => value.DeviceId)
                .ToDictionary(group => group.Key, group => group.First());
            while (!token.IsCancellationRequested)
            {
                while (currentLane.Incoming.Reader.TryRead(out var envelope))
                {
                    if (envelope?.SignalType != TransportSignalTypeV1.PathOffer || envelope.SignalSeq != 1 ||
                        envelope.SignalAck != 0 || envelope.PathEpoch is 0 or > long.MaxValue) continue;
                    Guid sender;
                    try { sender = GuidFromBytes(envelope.SenderDeviceId.Span); }
                    catch (Exception) { continue; }
                    if (!authorized.TryGetValue(sender, out var peer)) continue;
                    if (TryReadOffer(currentLane, peer, config, envelope, out var result)) return result;
                    throw new InvalidDataException("DIRECT_OFFER_REJECTED");
                }
                if (currentLane.IsTerminal) throw new InvalidDataException("DIRECT_SIGNAL_FAILED");
                await delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
            }
            throw new OperationCanceledException(token);
        }

        private bool TryReadOffer(
            SignalingLane currentLane,
            ProductionDirectPeerAuthorization peer,
            ProductionDirectTransportServerConfig config,
            TransportSignalEnvelopeV1 envelope,
            out InboundOffer result)
        {
            result = null; byte[] plaintext = null; ProductionValidatedDirectPathTicket ticket = null;
            try
            {
                if (!FixedBytes(envelope.KeyId.Span, signalKeyId)) return false;
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1 || payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.PathOffer)
                    return false;
                var offer = payload.PathOffer;
                if (!CanonicalEquals(plaintext, CanonicalOffer(offer)) || offer.QuicCertificateSha256.Length != 32 ||
                    IsAllZero(offer.QuicCertificateSha256.Span) || offer.Candidates.Count is < 1 or > 8 ||
                    offer.Candidates[0].Kind != DirectCandidateKindV1.Host ||
                    offer.Ticket.PathEpoch != envelope.PathEpoch ||
                    !offer.Ticket.ConnectionId.Span.SequenceEqual(envelope.ConnectionId.Span) ||
                    !offer.Ticket.InitiatorDeviceId.Span.SequenceEqual(envelope.SenderDeviceId.Span) ||
                    !offer.Ticket.ResponderDeviceId.Span.SequenceEqual(GuidBytes(localDeviceId))) return false;
                var connection = GuidFromBytes(envelope.ConnectionId.Span);
                ticket = currentLane.Signaling.ValidateIncomingTicket(offer.Ticket, peer.DeviceId, connection, config);
                var candidates = offer.Candidates.Select(value =>
                    ProductionDirectMultipairPlanner.Decode(value, offer.Ticket.ExpiresAtMs)).ToArray();
                ValidateRemoteCandidateSet(candidates);
                result = new InboundOffer(peer, connection, ticket, envelope.SignalSeq,
                    offer.QuicCertificateSha256.ToByteArray(), candidates, offer.GatheringComplete);
                ticket = null;
                return true;
            }
            catch (Exception error) when (error is InvalidDataException or CryptographicException or
                InvalidProtocolBufferException or ArgumentException or OverflowException) { return false; }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ticket?.WireBytes != null) CryptographicOperations.ZeroMemory(ticket.WireBytes);
            }
        }

        private static TransportSignalPayloadV1 CanonicalOffer(DirectPathOfferV1 offer)
        {
            var result = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                PathOffer = new DirectPathOfferV1
                {
                    Ticket = ProductionDirectTransportSignalingClient.CanonicalTicket(offer.Ticket),
                    QuicCertificateSha256 = offer.QuicCertificateSha256,
                    GatheringComplete = offer.GatheringComplete
                }
            };
            result.PathOffer.Candidates.Add(offer.Candidates.Select(CanonicalCandidate));
            return result;
        }

        private bool AcceptEnvelopeCursor(Attempt attempt, TransportSignalEnvelopeV1 value) =>
            value.SignalSeq == attempt.LastRemoteSequence + 1 && value.SignalAck >= attempt.LastRemoteAcknowledgement &&
            value.SignalAck <= attempt.LocalSequence && value.SignalType is >= TransportSignalTypeV1.CandidateUpdate and
                <= TransportSignalTypeV1.NominationResultV2;

        private bool Matches(Attempt attempt, TransportSignalEnvelopeV1 envelope) => envelope != null &&
            envelope.ConnectionId.Span.SequenceEqual(GuidBytes(attempt.ConnectionId)) &&
            envelope.SenderDeviceId.Span.SequenceEqual(GuidBytes(attempt.Peer.DeviceId)) &&
            envelope.RecipientDeviceId.Span.SequenceEqual(GuidBytes(localDeviceId)) &&
            FixedBytes(envelope.KeyId.Span, signalKeyId) && envelope.PathEpoch == attempt.Epoch;

        private static bool MatchesSelection(Attempt attempt, Guid round, Guid nomination,
            ProductionDirectCandidatePairV4 pair) => attempt.SelectedPair != null &&
            FixedId(attempt.CheckRoundId, round) && FixedId(attempt.NominationId, nomination) &&
            FixedId(attempt.SelectedPair.Initiator.CandidateId, pair.Initiator.CandidateId) &&
            FixedId(attempt.SelectedPair.Responder.CandidateId, pair.Responder.CandidateId) &&
            attempt.SelectedPair.Equals(pair);

        private static bool MatchesSelection(Attempt attempt, ReadOnlySpan<byte> round,
            ReadOnlySpan<byte> nomination, DirectCandidatePairV1 pair) => attempt.SelectedPair != null &&
            FixedBytes(round, GuidBytes(attempt.CheckRoundId)) &&
            FixedBytes(nomination, GuidBytes(attempt.NominationId)) &&
            FixedBytes(pair.InitiatorCandidateId.Span, GuidBytes(attempt.SelectedPair.Initiator.CandidateId)) &&
            FixedBytes(pair.ResponderCandidateId.Span, GuidBytes(attempt.SelectedPair.Responder.CandidateId));

        private static DirectCandidateV1 CanonicalCandidate(DirectCandidateV1 value) => new()
        {
            CandidateId = value.CandidateId, Kind = value.Kind, Carrier = value.Carrier,
            IpAddress = value.IpAddress, Port = value.Port, Priority = value.Priority,
            RelatedCandidateId = value.RelatedCandidateId, NetworkCost = value.NetworkCost,
            ExpiresAtMs = value.ExpiresAtMs
        };

        private static DirectCandidatePairV1 CanonicalPair(DirectCandidatePairV1 value) => new()
        {
            InitiatorCandidateId = value.InitiatorCandidateId,
            ResponderCandidateId = value.ResponderCandidateId
        };

        private static bool CanonicalEquals(byte[] plaintext, TransportSignalPayloadV1 canonical)
        {
            var bytes = canonical.ToByteArray();
            try { return plaintext.AsSpan().SequenceEqual(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static string PairKey(DirectCandidatePairV1 value) =>
            GuidFromBytes(value.InitiatorCandidateId.Span).ToString("N") + ":" +
            GuidFromBytes(value.ResponderCandidateId.Span).ToString("N");

        private static bool AddRemoteCandidate(Attempt attempt, ProductionDirectCandidateV4 candidate)
        {
            if (attempt.RemoteCandidates.ContainsKey(candidate.CandidateId) ||
                attempt.RemoteCandidates.Values.Any(value => value.Endpoint.Port == candidate.Endpoint.Port &&
                    value.Endpoint.Address.Equals(candidate.Endpoint.Address))) return false;
            if (candidate.Kind == ProductionDirectCandidateKindV4.ServerReflexive &&
                (!attempt.RemoteCandidates.TryGetValue(candidate.RelatedCandidateId, out var host) ||
                 host.Kind != ProductionDirectCandidateKindV4.Host)) return false;
            attempt.RemoteCandidates.Add(candidate.CandidateId, candidate);
            return true;
        }

        private static void ValidateRemoteCandidateSet(IReadOnlyList<ProductionDirectCandidateV4> candidates)
        {
            var ids = new HashSet<Guid>(); var endpoints = new HashSet<string>(StringComparer.Ordinal);
            var hosts = new HashSet<Guid>();
            foreach (var candidate in candidates)
            {
                if (!ids.Add(candidate.CandidateId) ||
                    !endpoints.Add(candidate.Endpoint.Address + ":" + candidate.Endpoint.Port)) throw new ArgumentException();
                if (candidate.Kind == ProductionDirectCandidateKindV4.Host) hosts.Add(candidate.CandidateId);
            }
            if (candidates.Any(value => value.Kind == ProductionDirectCandidateKindV4.ServerReflexive &&
                !hosts.Contains(value.RelatedCandidateId))) throw new ArgumentException();
        }

        private bool Fail(Attempt attempt, string reason)
        {
            Retire(attempt, reason, false);
            return false;
        }

        private void Retire(Attempt attempt, string reason, bool networkChanged)
        {
            IProductionDirectShadowNativeV4 native = null;
            lock (sync)
            {
                if (!ReferenceEquals(active, attempt)) return;
                active = null; native = attempt.Native; attempt.Native = null;
                if (ReferenceEquals(currentPromotion?.Attempt, attempt))
                    InvalidatePromotionLocked(currentPromotion);
                PublishLocked(relayConnected ? ProductionDirectShadowState.RelayOnly :
                    ProductionDirectShadowState.WaitingForRelay, reason, attempt);
            }
            try
            {
                if (networkChanged) native?.NetworkChanged(attempt.Epoch, attempt.AttemptId);
                else native?.Cancel(attempt.Epoch, attempt.AttemptId);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try { native?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try
            {
                if (networkChanged) supervisor.NotifyNetworkChanged();
                else if (!string.Equals(reason, "RELAY_UNAVAILABLE", StringComparison.Ordinal))
                    supervisor.TryFallbackAttempt(attempt.SupervisorToken, reason);
            }
            finally { attempt.Dispose(); }
            Wake();
        }

        private async Task<SignalingLane> GetLaneAsync(CancellationToken token)
        {
            SignalingLane stale = null; SignalingLane result;
            lock (sync)
            {
                if (lane != null && lane.IsTerminal) { stale = lane; lane = null; }
                if (lane == null)
                {
                    if (sharedSignaling != null)
                        lane = new SignalingLane(++laneGeneration, sharedSignaling,
                            ownsSignaling: false) { Started = true };
                    else
                        lane = new SignalingLane(++laneGeneration, signalingFactory(),
                            ownsSignaling: true);
                }
                result = lane;
            }
            if (stale != null) await stale.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task PumpSignalsAsync(SignalingLane currentLane, CancellationToken token)
        {
            try
            {
                await foreach (var value in currentLane.Signaling.ReadSignalsAsync(token).ConfigureAwait(false))
                    if (!currentLane.Incoming.Writer.TryWrite(value))
                        throw new InvalidDataException("DIRECT_SIGNAL_QUEUE_FULL");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Attempt current; lock (sync) current = active;
                if (current != null && ReferenceEquals(current.Lane, currentLane)) Retire(current, "DIRECT_SIGNAL_FAILED", false);
            }
            finally { currentLane.MarkTerminal(); currentLane.Incoming.Writer.TryComplete(); Wake(); }
        }

        private bool IsCurrent(Attempt value) { lock (sync) return ReferenceEquals(active, value); }
        private bool RelayConnected() { lock (sync) return relayConnected; }
        private bool HandoffActive() { lock (sync) return handoffActive; }

        private void CompletePromotionHandoff(Attempt attempt)
        {
            string earlyFailure = null;
            PromotionLease lease;
            lock (sync)
            {
                lease = currentPromotion;
                if (!ReferenceEquals(active, attempt) || lease == null ||
                    !ReferenceEquals(lease.Attempt, attempt) || lease.Retired) return;
                active = null;
                earlyFailure = lease.FailureReason;
                handoffActive = string.IsNullOrEmpty(earlyFailure);
                if (handoffActive)
                {
                    lease.HandedOff = true;
                    PublishLocked(lease.Ready
                            ? ProductionDirectShadowState.ShadowReadyNotRouted
                            : ProductionDirectShadowState.Negotiating,
                        lease.Ready ? string.Empty : "DIRECT_V5_PROMOTED", attempt);
                }
                else
                {
                    InvalidatePromotionLocked(lease);
                    PublishLocked(relayConnected ? ProductionDirectShadowState.RelayOnly :
                        ProductionDirectShadowState.WaitingForRelay,
                        earlyFailure,
                        attempt);
                }
            }
            if (!string.IsNullOrEmpty(earlyFailure))
            {
                supervisor.TryFallbackAttempt(attempt.SupervisorToken, earlyFailure);
                if (relayConnected) Wake();
            }
            attempt.Dispose();
        }

        private void ObservePromotedReady(PromotionLease lease, byte[] binding)
        {
            try
            {
                if (binding?.Length != 32 || IsAllZero(binding))
                    throw new InvalidDataException("DIRECT_PROMOTED_BINDING_INVALID");
                lock (sync)
                {
                    if (disposed != 0 || lease == null || lease.Retired || lease.Ready ||
                        !ReferenceEquals(currentPromotion, lease)) return;
                    supervisor.MarkShadowReady(lease.Attempt.SupervisorToken, binding);
                    lease.Ready = true;
                    if (!lease.HandedOff) return;
                    PublishLocked(ProductionDirectShadowState.ShadowReadyNotRouted,
                        string.Empty, lease.Attempt);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ObservePromotedFailure(lease, "DIRECT_PROMOTED_READY_FAILED");
            }
        }

        private void ObservePromotedFailure(PromotionLease lease, string reason)
        {
            string failure;
            ProductionDirectAttemptToken supervisorToken;
            lock (sync)
            {
                if (disposed != 0 || lease == null || lease.Retired || lease.Ready ||
                    !ReferenceEquals(currentPromotion, lease)) return;
                failure = string.IsNullOrWhiteSpace(reason)
                    ? "DIRECT_PROMOTED_FAILED" : reason;
                lease.FailureReason = failure;
                if (!lease.HandedOff) return;
                supervisorToken = lease.Attempt.SupervisorToken;
                var attempt = lease.Attempt;
                InvalidatePromotionLocked(lease);
                PublishLocked(relayConnected ? ProductionDirectShadowState.RelayOnly :
                    ProductionDirectShadowState.WaitingForRelay,
                    failure,
                    attempt);
            }
            supervisor.TryFallbackAttempt(supervisorToken, failure);
            if (relayConnected) Wake();
        }

        private void InvalidatePromotionLocked(PromotionLease lease)
        {
            if (lease != null) lease.Retired = true;
            if (ReferenceEquals(currentPromotion, lease)) currentPromotion = null;
            handoffActive = false;
        }
        private void Publish(ProductionDirectShadowState state, string reason, Attempt attempt)
        { lock (sync) PublishLocked(state, reason, attempt); }
        private void PublishLocked(ProductionDirectShadowState state, string reason, Attempt attempt)
        {
            snapshot = new ProductionDirectShadowSnapshot
            {
                State = state, ReasonCode = reason ?? string.Empty, Epoch = attempt?.Epoch ?? 0,
                AttemptId = attempt?.AttemptId ?? Guid.Empty, PeerDeviceId = attempt?.Peer?.DeviceId ?? Guid.Empty
            };
            ObserveDiagnosticsLocked(state, reason, attempt);
        }

        private void ObserveDiagnosticsLocked(
            ProductionDirectShadowState state,
            string reason,
            Attempt attempt)
        {
            if (diagnostics?.Enabled != true) return;
            var selected = attempt?.SelectedPair;
            diagnostics.Observe(
                ProductionDirectTransportDiagnostics.StateCode(state),
                reason,
                relayGeneration,
                networkGeneration,
                attempt?.Epoch ?? 0,
                selected?.Rank ?? 0,
                selected == null
                    ? "NONE"
                    : ProductionDirectTransportDiagnostics.CandidateType(selected.Responder.Kind),
                selected == null
                    ? "NONE"
                    : ProductionDirectTransportDiagnostics.CandidateType(selected.Initiator.Kind));
        }
        private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); Attempt current; Task running; SignalingLane currentLane;
            lock (sync)
            {
                current = active; running = worker; currentLane = lane; lane = null;
                if (current == null) InvalidatePromotionLocked(currentPromotion);
            }
            if (current != null) Retire(current, "STOPPED", false);
            lock (sync) PublishLocked(ProductionDirectShadowState.Stopped, "STOPPED", null);
            if (currentLane != null) await currentLane.DisposeAsync().ConfigureAwait(false);
            if (running != null) { try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            CryptographicOperations.ZeroMemory(signalKeyId);
            diagnostics?.Dispose();
            lifetime.Dispose(); wake.Dispose();
        }

        private static void DisposePeers(IReadOnlyList<ProductionDirectPeerAuthorization> peers,
            ProductionDirectPeerAuthorization retained)
        { if (peers != null) foreach (var value in peers) if (value != null && !ReferenceEquals(value, retained)) value.Dispose(); }
        private static bool FixedId(Guid left, Guid right) => FixedBytes(GuidBytes(left), GuidBytes(right));
        private static bool FixedBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
            left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        private static bool IsAllZero(ReadOnlySpan<byte> value)
        { byte aggregate = 0; foreach (var item in value) aggregate |= item; return aggregate == 0; }
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromBytes(ReadOnlySpan<byte> value)
        { if (value.Length != 16) throw new ArgumentException(); return Guid.ParseExact(Convert.ToHexString(value), "N"); }

        private sealed class PendingBatch
        {
            public PendingBatch(uint sequence, IReadOnlyList<DirectCandidatePairV1> pairs, bool complete)
            { Sequence = sequence; Pairs = pairs; Complete = complete; }
            public uint Sequence { get; }
            public IReadOnlyList<DirectCandidatePairV1> Pairs { get; }
            public bool Complete { get; }
        }

        private sealed class PromotionLease
        {
            internal PromotionLease(long generation, Attempt attempt)
            {
                Generation = generation;
                Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
            }
            internal long Generation { get; }
            internal Attempt Attempt { get; }
            internal bool HandedOff { get; set; }
            internal bool Ready { get; set; }
            internal bool Retired { get; set; }
            internal string FailureReason { get; set; }
        }

        private sealed class SignalingLane : IAsyncDisposable
        {
            private int terminal; private int disposed;
            private readonly bool ownsSignaling;
            public SignalingLane(long generation, IProductionDirectShadowSignaling signaling,
                bool ownsSignaling)
            {
                Generation = generation; Signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
                this.ownsSignaling = ownsSignaling;
                Incoming = Channel.CreateBounded<TransportSignalEnvelopeV1>(new BoundedChannelOptions(64)
                { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            }
            public long Generation { get; }
            public IProductionDirectShadowSignaling Signaling { get; }
            public Channel<TransportSignalEnvelopeV1> Incoming { get; }
            public bool Started { get; set; }
            public Task Reader { get; set; }
            public bool IsTerminal => Volatile.Read(ref terminal) != 0 || (Reader?.IsCompleted ?? false);
            public void MarkTerminal() => Interlocked.Exchange(ref terminal, 1);
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                MarkTerminal(); Incoming.Writer.TryComplete();
                if (ownsSignaling) await Signaling.DisposeAsync().ConfigureAwait(false);
                if (Reader != null) { try { await Reader.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                while (Incoming.Reader.TryRead(out _)) { }
            }
        }

        private sealed class InboundOffer : IDisposable
        {
            private bool transferred;
            public InboundOffer(ProductionDirectPeerAuthorization peer, Guid connectionId,
                ProductionValidatedDirectPathTicket ticket, ulong sequence, byte[] certificate,
                IReadOnlyList<ProductionDirectCandidateV4> candidates, bool complete)
            { Peer = peer; ConnectionId = connectionId; Ticket = ticket; Sequence = sequence;
              Certificate = certificate; Candidates = candidates; Complete = complete; }
            public ProductionDirectPeerAuthorization Peer { get; }
            public Guid ConnectionId { get; }
            public ProductionValidatedDirectPathTicket Ticket { get; }
            public ulong Sequence { get; }
            public byte[] Certificate { get; }
            public IReadOnlyList<ProductionDirectCandidateV4> Candidates { get; }
            public bool Complete { get; }
            public void TransferOwnership() => transferred = true;
            public void Dispose()
            { if (!transferred) { Peer.Dispose(); CryptographicOperations.ZeroMemory(Certificate); CryptographicOperations.ZeroMemory(Ticket.WireBytes); } }
        }

        private sealed class Attempt : IDisposable
        {
            public Attempt(InboundOffer offer, Guid attemptId, ProductionDirectTransportServerConfig config,
                SignalingLane lane)
            {
                Peer = offer.Peer; ConnectionId = offer.ConnectionId; Ticket = offer.Ticket; AttemptId = attemptId;
                Config = config; Lane = lane; LastRemoteSequence = offer.Sequence;
                RemoteCertificateSha256 = offer.Certificate;
                foreach (var candidate in offer.Candidates) RemoteCandidates.Add(candidate.CandidateId, candidate);
                RemoteGatherComplete = offer.Complete;
            }
            public ulong Epoch => Ticket.Ticket.PathEpoch;
            public ProductionDirectPeerAuthorization Peer { get; }
            public Guid ConnectionId { get; }
            public ProductionValidatedDirectPathTicket Ticket { get; }
            public Guid AttemptId { get; }
            public ProductionDirectTransportServerConfig Config { get; }
            public SignalingLane Lane { get; }
            public ProductionDirectAttemptToken SupervisorToken { get; set; }
            public IProductionDirectShadowNativeV4 Native { get; set; }
            public Dictionary<Guid, ProductionDirectCandidateV4> LocalCandidates { get; } = new();
            public Dictionary<Guid, ProductionDirectCandidateV4> RemoteCandidates { get; } = new();
            public Dictionary<string, ProductionDirectCandidatePairV4> PlannedPairs { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, ProductionDirectCandidatePairV4> ValidatedPairs { get; } = new(StringComparer.Ordinal);
            public HashSet<string> WirePairKeys { get; } = new(StringComparer.Ordinal);
            public Queue<PendingBatch> PendingBatches { get; } = new();
            public byte[] LocalCertificateSha256 { get; set; }
            public byte[] RemoteCertificateSha256 { get; set; }
            public byte[] ConnectivityNonce { get; set; }
            public byte[] LocalSendProbeKey { get; set; }
            public byte[] PeerReceiveProbeKey { get; set; }
            public byte[] LocalPathBinding { get; set; }
            public byte[] PeerPathBinding { get; set; }
            public DirectNominationV2 PendingNomination { get; set; }
            public ProductionDirectCandidatePairV4 SelectedPair { get; set; }
            public Guid CheckRoundId { get; set; }
            public Guid NominationId { get; set; }
            public ulong LocalSequence { get; set; }
            public ulong LastRemoteSequence { get; set; }
            public ulong LastRemoteAcknowledgement { get; set; }
            public ulong LastNativeGeneration { get; set; }
            public uint LocalCandidateBatchSequence { get; set; }
            public uint RemoteCheckBatchSequence { get; set; }
            public int PendingPairCount { get; set; }
            public bool LocalGatherComplete { get; set; }
            public bool LocalCandidatePlanComplete { get; set; }
            public bool RemoteGatherComplete { get; set; }
            public bool RemotePlanComplete { get; set; }
            public bool AnswerSent { get; set; }
            public bool NativeChecksStarted { get; set; }
            public bool ChecksComplete { get; set; }
            public bool CommitSubmitted { get; set; }
            public bool NominationReady { get; set; }
            public bool AckSent { get; set; }
            public bool LocalResultSent { get; set; }
            public bool ShadowReady { get; set; }
            public bool Promoted { get; set; }
            public bool PromotionReady { get; set; }
            public string PromotionFailure { get; set; }
            public void ReleaseSecretsAfterReady()
            {
                Peer.Dispose();
                CryptographicOperations.ZeroMemory(Ticket.WireBytes);
                Zero(LocalSendProbeKey); LocalSendProbeKey = null;
                Zero(PeerReceiveProbeKey); PeerReceiveProbeKey = null;
                Zero(ConnectivityNonce); ConnectivityNonce = null;
            }

            public void Dispose()
            {
                var ownedNative = Native;
                Native = null;
                try { ownedNative?.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                finally
                {
                    try { Peer.Dispose(); }
                    finally
                    {
                        Zero(Ticket.WireBytes); Zero(LocalCertificateSha256); Zero(RemoteCertificateSha256);
                        Zero(ConnectivityNonce); Zero(LocalSendProbeKey); Zero(PeerReceiveProbeKey);
                        Zero(LocalPathBinding); Zero(PeerPathBinding);
                        LocalCandidates.Clear(); RemoteCandidates.Clear(); PlannedPairs.Clear(); ValidatedPairs.Clear();
                        PendingBatches.Clear(); WirePairKeys.Clear();
                    }
                }
            }
            private static void Zero(byte[] value) { if (value != null) CryptographicOperations.ZeroMemory(value); }
        }
    }
}

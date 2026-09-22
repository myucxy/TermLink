using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionAgentRuntimeDirectWiringValidation
    {
        internal static async Task ValidateAsync()
        {
            await ValidateDisconnectedRelayOwnershipAndExactFlushAsync().ConfigureAwait(false);
            await ValidateDirectFailureIsolationAndNoDuplicateFallbackAsync().ConfigureAwait(false);
            await ValidateDroppedDirectOutcomeWakesWithoutDuplicateRelayAsync().ConfigureAwait(false);
            await ValidateBackpressureRemainsCallerOwnedAsync().ConfigureAwait(false);
            await ValidateRelayCapacityReleaseWakesBlockedSessionsAsync().ConfigureAwait(false);
            ValidateSharedSignalingReaderStartFence();
            await ValidateSharedSignalingDisposeOrderAsync().ConfigureAwait(false);
            ValidateRelayIngressDirectArbitrationAllowlist();
            ValidateDirectIngressApplicationCursorWaitsForDurability();
            ValidateLateSessionCutoverPassIsNotLost();
            ValidateSessionCarrierAggregatesEveryViewer();
            ValidateAuthorityReplacementFenceShape();
            ValidateProductionGateAndCommonIngressShape();
        }

        private static void ValidateLateSessionCutoverPassIsNotLost()
        {
            var pending = new ConcurrentDictionary<Guid, byte>();
            var peer = Guid.NewGuid();
            Require(ProductionAgentRuntime.RequestPcToAndroidCutoverPass(pending, peer),
                "The first actor-owned cutover pass was not scheduled.");
            Require(!ProductionAgentRuntime.RequestPcToAndroidCutoverPass(pending, peer) &&
                    pending.TryGetValue(peer, out var requested) && requested == 1,
                "A late SUBSCRIBE did not request a follow-up cutover pass.");
            Require(ProductionAgentRuntime.CompletePcToAndroidCutoverPass(pending, peer) &&
                    pending.TryGetValue(peer, out var running) && running == 0,
                "The coalescer lost the late-session follow-up pass.");
            Require(!ProductionAgentRuntime.CompletePcToAndroidCutoverPass(pending, peer) &&
                    !pending.ContainsKey(peer),
                "A completed cutover worker remained permanently pending.");
            Require(ProductionAgentRuntime.RequestPcToAndroidCutoverPass(pending, peer),
                "A later independent session could not start a new cutover worker.");
            Require(!ProductionAgentRuntime.CompletePcToAndroidCutoverPass(pending, peer),
                "An unrequested cutover pass repeated spuriously.");
        }

        private static void ValidateDirectIngressApplicationCursorWaitsForDurability()
        {
            var tracker = new ProductionDirectIngressApplyTracker();
            tracker.Observe(1);
            Require(tracker.MarkApplied(1) == 1 && tracker.HighestContiguous == 1,
                "The first durably applied Direct envelope did not advance its cursor.");

            tracker.Observe(2); // InputEvent is admitted; SessionHost result is pending.
            tracker.Observe(3); // A later control envelope completes synchronously.
            Require(tracker.MarkApplied(3) == 0 && tracker.HighestContiguous == 1,
                "A later control envelope advanced across a pending input result.");

            Require(tracker.MarkApplied(2) == 3 && tracker.HighestContiguous == 3,
                "Completing the durable input did not release its contiguous suffix once.");
            Require(tracker.MarkApplied(2) == 0 && tracker.HighestContiguous == 3 &&
                    tracker.DuplicateApplyCount == 1,
                "A duplicate durable completion moved the application cursor twice.");
        }

        private static void ValidateAuthorityReplacementFenceShape()
        {
            var runtime = typeof(ProductionAgentRuntime);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Require(runtime.GetField("subscriberAuthoritySync", flags) != null &&
                    runtime.GetField("directAuthorityKeyEpoch", flags) != null &&
                    runtime.GetField("directAuthorityTransitioning", flags) != null,
                "Subscriber replacement is not protected by an explicit authority transaction.");
            Require(runtime.GetMethod("BeginDirectAuthorityTransition", flags) != null &&
                    runtime.GetMethod("RetireDirectAuthorityPeersAsync", flags) != null &&
                    runtime.GetMethod("EndDirectAuthorityTransition", flags) != null &&
                    runtime.GetMethod("DirectHandoffAuthorityMatches", flags) != null &&
                    runtime.GetMethod("DirectCutoverReservationIsCurrent", flags) != null,
                "Key/subscriber replacement is missing a promotion or cutover completion fence.");
            var applyEpoch = runtime.GetMethod("ApplyPCUpsertKeyEpochAsync", flags);
            Require(applyEpoch != null && applyEpoch.ReturnType == typeof(Task),
                "PC key-epoch advancement cannot join old Direct actor retirement.");
        }

        private static void ValidateSessionCarrierAggregatesEveryViewer()
        {
            var session = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var lanFirst = PeerCarrier(first, session, ProductionDirectCarrierV5.LanDirect);
            var lanSecond = PeerCarrier(second, session, ProductionDirectCarrierV5.LanDirect);
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    new[] { first, second },
                    new[] { lanFirst, lanSecond }) == ProductionSessionCarrier.LanDirect,
                "Two fully committed LAN viewers were not reported as LAN Direct.");

            var natSecond = PeerCarrier(second, session, ProductionDirectCarrierV5.NatDirect);
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    new[] { first, second },
                    new[] { lanFirst, natSecond }) == ProductionSessionCarrier.Relay,
                "Mixed LAN/NAT viewers were overstated as one Direct session carrier.");
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    new[] { first, second },
                    new[] { lanFirst }) == ProductionSessionCarrier.Relay,
                "A missing collaborative viewer path was hidden by another viewer's Direct path.");

            var partialSecond = PeerCarrier(
                second,
                session,
                ProductionDirectCarrierV5.LanDirect,
                ProductionDirectCarrierV5.Relay);
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    new[] { first, second },
                    new[] { lanFirst, partialSecond }) == ProductionSessionCarrier.Relay,
                "A one-direction viewer cutover was reported as session-wide Direct.");

            var retiredSecond = PeerCarrier(second, session, ProductionDirectCarrierV5.LanDirect);
            retiredSecond = new ProductionDirectDataPeerSnapshot
            {
                PeerDeviceId = retiredSecond.PeerDeviceId,
                Retired = true,
                PcToAndroidSessionCarriers = retiredSecond.PcToAndroidSessionCarriers,
                AndroidToPcSessionCarriers = retiredSecond.AndroidToPcSessionCarriers
            };
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    new[] { first, second },
                    new[] { lanFirst, retiredSecond }) == ProductionSessionCarrier.Relay,
                "A retired viewer path was reported as session-wide Direct.");
            Require(ProductionAgentRuntime.ResolveSessionCarrier(
                    session,
                    Array.Empty<Guid>(),
                    new[] { lanFirst }) == ProductionSessionCarrier.Relay,
                "A session without viewers was reported as Direct.");
        }

        private static ProductionDirectDataPeerSnapshot PeerCarrier(
            Guid peer,
            Guid session,
            ProductionDirectCarrierV5 pcToAndroid,
            ProductionDirectCarrierV5? androidToPc = null) => new()
        {
            PeerDeviceId = peer,
            PcToAndroidSessionCarriers = new Dictionary<Guid, ProductionDirectCarrierV5>
            {
                [session] = pcToAndroid
            },
            AndroidToPcSessionCarriers = new Dictionary<Guid, ProductionDirectCarrierV5>
            {
                [session] = androidToPc ?? pcToAndroid
            }
        };

        private static void ValidateSharedSignalingReaderStartFence()
        {
            var shadowStarts = 0;
            Require(!ProductionAgentRuntime.TryStartSharedDirectRuntimes(
                    () => false,
                    () => { shadowStarts++; return true; }) && shadowStarts == 0,
                "The V4 shadow started without the shared signalling reader owner.");
            Require(!ProductionAgentRuntime.TryStartSharedDirectRuntimes(
                    () => throw new InvalidOperationException("SIGNALLING_START_FAILED"),
                    () => { shadowStarts++; return true; }) && shadowStarts == 0,
                "A shared signalling start failure escaped or started the V4 shadow.");
            Require(!ProductionAgentRuntime.TryStartSharedDirectRuntimes(
                    () => true,
                    () => { shadowStarts++; return false; }) && shadowStarts == 1,
                "A V4 shadow start rejection was reported as successful.");
            Require(!ProductionAgentRuntime.TryStartSharedDirectRuntimes(
                    () => true,
                    () => throw new InvalidOperationException("SHADOW_START_FAILED")) &&
                shadowStarts == 1,
                "A V4 shadow start exception escaped the optional Direct lane.");
            Require(ProductionAgentRuntime.TryStartSharedDirectRuntimes(
                    () => true,
                    () => { shadowStarts++; return true; }) && shadowStarts == 2,
                "The V4 shadow did not start after its sole signalling reader started.");
        }

        private static async Task ValidateSharedSignalingDisposeOrderAsync()
        {
            var order = new List<string>();
            await ProductionAgentRuntime.DisposeSharedDirectRuntimesInOrderAsync(
                () =>
                {
                    order.Add("shadow");
                    return ValueTask.CompletedTask;
                },
                () => order.Add("observer"),
                () =>
                {
                    order.Add("reader-owner");
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            Require(order.SequenceEqual(new[] { "shadow", "observer", "reader-owner" }),
                "Shared signalling was not disposed after its non-owning shadow and observer.");

            order.Clear();
            await ProductionAgentRuntime.DisposeSharedDirectRuntimesInOrderAsync(
                () => throw new InvalidOperationException("SHADOW_DISPOSE_FAILED"),
                () =>
                {
                    order.Add("observer");
                    throw new InvalidOperationException("OBSERVER_DETACH_FAILED");
                },
                () =>
                {
                    order.Add("reader-owner");
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            Require(order.SequenceEqual(new[] { "observer", "reader-owner" }),
                "A shadow/observer cleanup failure stranded the shared signalling owner.");
        }

        private static void ValidateRelayIngressDirectArbitrationAllowlist()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var input = InboundEnvelope(local, peer, MessageType.InputEvent, Direction.AndroidToPc);
            Require(ProductionAgentRuntime.IsRelayIngressDirectArbitrationEligible(input, peer),
                "An authenticated Android input was excluded from Direct duplicate arbitration.");

            foreach (var relayOnlyType in new[]
                {
                    MessageType.Subscribe,
                    MessageType.Unsubscribe,
                    MessageType.OutputAck,
                    MessageType.FlowControl,
                    MessageType.ResizeRequest,
                    MessageType.HostHistoryRequest,
                    MessageType.HostHistoryControl,
                    MessageType.SessionTerminateRequest
                })
            {
                Require(!ProductionAgentRuntime.IsRelayIngressDirectArbitrationEligible(
                        InboundEnvelope(local, peer, relayOnlyType, Direction.AndroidToPc),
                        peer),
                    $"Relay-only {relayOnlyType} entered Direct duplicate arbitration.");
            }
            Require(!ProductionAgentRuntime.IsRelayIngressDirectArbitrationEligible(
                    input,
                    Guid.NewGuid()) &&
                !ProductionAgentRuntime.IsRelayIngressDirectArbitrationEligible(
                    InboundEnvelope(local, peer, MessageType.InputEvent, Direction.PcToAndroid),
                    peer),
                "A mismatched sender or direction entered Direct duplicate arbitration.");
        }

        private static async Task ValidateDisconnectedRelayOwnershipAndExactFlushAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var exact = EnvelopeBytes(local, peer, session, 1);
            var expected = exact.ToArray();
            var sent = new ConcurrentQueue<byte[]>();
            await using var outbox = new ProductionRelayExactOutbox(
                retryDelay: TimeSpan.FromMilliseconds(5));

            Require(await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    null,
                    outbox,
                    peer,
                    session,
                    1,
                    exact,
                    directEligible: true,
                    observeDirectFault: null,
                    CancellationToken.None).ConfigureAwait(false),
                "A disconnected Relay did not synchronously transfer ownership to its outbox.");
            Require(outbox.Snapshot().Single().PendingFrames == 1 &&
                exact.SequenceEqual(expected),
                "Relay ownership transfer mutated the publisher's authoritative cached bytes.");

            outbox.ObserveRelayConnected(7, (request, _) =>
            {
                sent.Enqueue(request.ExactEnvelope.ToArray());
                return Task.FromResult(true);
            });
            await WaitUntilAsync(() => sent.Count == 1).ConfigureAwait(false);
            Require(sent.TryDequeue(out var delivered) && delivered.SequenceEqual(expected) &&
                outbox.Snapshot().Single().PendingFrames == 1,
                "A new Relay generation did not flush exact bytes or retained them only until transport send.");
            outbox.ObserveRelayDisconnected(7);
            Require(outbox.Snapshot().Single().PendingFrames == 1,
                "Relay disconnect incorrectly pruned an item without durable ACK.");
            CryptographicOperations.ZeroMemory(delivered);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(exact);
        }

        private static async Task ValidateDirectFailureIsolationAndNoDuplicateFallbackAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var exact = EnvelopeBytes(local, peer, session, 9);
            var supervisor = new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences());
            var relayGeneration = supervisor.MarkPhysicalConnected();
            var sessionAlive = true;
            var directFaultCallbacks = 0;
            await using var outbox = new ProductionRelayExactOutbox(
                retryDelay: TimeSpan.FromMilliseconds(5));
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                new ProductionDirectDataRuntimeOptions
                {
                    EnqueueRelayEnvelope = value =>
                        ProductionAgentExactEnvelopeRouter.AcceptOwnedRelayEnvelope(outbox, value),
                    EnqueueIngress = value => CryptographicOperations.ZeroMemory(value.ExactEnvelope),
                    ParseIngressMetadata = ParseMetadata
                });
            await runtime.RegisterPeerAsync(new ProductionDirectDataPeerRegistration
            {
                PeerDeviceId = peer,
                Path = Fence(),
                KeyEpoch = 1,
                AdmissionGeneration = 1,
                PairwiseKey = Enumerable.Repeat((byte)0x45, 32).ToArray(),
                SignalKeyId = ByteString.CopyFromUtf8("device-key-validation").ToByteArray(),
                SignNativeTranscript = _ => Enumerable.Repeat((byte)0x77, 64).ToArray(),
                Native = new RejectingNative()
            }).ConfigureAwait(false);

            Require(await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    runtime,
                    outbox,
                    peer,
                    session,
                    9,
                    exact,
                    directEligible: true,
                    (_, _) => directFaultCallbacks++,
                    CancellationToken.None).ConfigureAwait(false),
                "A normal Direct rejection did not transfer its exact fallback to Relay.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);
            Require(supervisor.Snapshot().PhysicalGeneration == relayGeneration &&
                supervisor.Snapshot().PhysicalConnected && sessionAlive &&
                outbox.Snapshot().Single().PendingFrames == 1,
                "A Direct failure changed Relay generation, closed the session, or duplicated fallback.");

            // A later durable-publisher retry is byte-identical. It may revisit
            // the router, but the Relay owner must retain one keyed frame only.
            Require(await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    runtime,
                    outbox,
                    peer,
                    session,
                    9,
                    exact,
                    directEligible: true,
                    (_, _) => directFaultCallbacks++,
                    CancellationToken.None).ConfigureAwait(false) &&
                outbox.Snapshot().Single().PendingFrames == 1,
                "A cached retry created a second Relay fallback for the same exact key.");
            Require(directFaultCallbacks == 0,
                "A handled native fallback escaped as a second managed Direct failure.");
            supervisor.Dispose();
            CryptographicOperations.ZeroMemory(exact);
        }

        private static async Task ValidateBackpressureRemainsCallerOwnedAsync()
        {
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var local = Guid.NewGuid();
            var first = EnvelopeBytes(local, peer, session, 1);
            var rejected = EnvelopeBytes(local, peer, session, 2);
            var expectedRejected = rejected.ToArray();
            await using var outbox = new ProductionRelayExactOutbox(
                frameCapacityPerPeer: 1,
                byteCapacityPerPeer: ProductionRelayExactOutbox.MaximumBytesPerPeer,
                retryDelay: TimeSpan.FromMilliseconds(5));
            Require(await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    null, outbox, peer, session, 1, first, true, null,
                    CancellationToken.None).ConfigureAwait(false),
                "The capacity test could not fill the Relay outbox.");
            Require(!await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    null, outbox, peer, session, 2, rejected, true, null,
                    CancellationToken.None).ConfigureAwait(false) &&
                rejected.SequenceEqual(expectedRejected) &&
                outbox.Snapshot().Single().PendingFrames == 1,
                "Relay backpressure took ownership, zeroed cached bytes, or evicted the queue head.");
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(rejected);
            CryptographicOperations.ZeroMemory(expectedRejected);
        }

        private static async Task ValidateDroppedDirectOutcomeWakesWithoutDuplicateRelayAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var fillerSession = Guid.NewGuid();
            var exact = EnvelopeBytes(local, peer, session, 9);
            var filler = EnvelopeBytes(local, peer, fillerSession, 1);
            var directFaultCallbacks = 0;
            await using var outbox = new ProductionRelayExactOutbox(
                frameCapacityPerPeer: 1,
                byteCapacityPerPeer: ProductionRelayExactOutbox.MaximumBytesPerPeer,
                retryDelay: TimeSpan.FromMilliseconds(5));
            var native = new RejectingNative(() =>
            {
                ProductionAgentExactEnvelopeRouter.AcceptOwnedRelayEnvelope(
                    outbox,
                    new ProductionDirectDataRelayEnvelope
                    {
                        PeerDeviceId = peer,
                        SessionId = fillerSession,
                        EnvelopeSequence = 1,
                        ExactEnvelope = filler.ToArray()
                    });
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                new ProductionDirectDataRuntimeOptions
                {
                    EnqueueRelayEnvelope = value =>
                        ProductionAgentExactEnvelopeRouter.AcceptOwnedRelayEnvelope(outbox, value),
                    EnqueueIngress = value => CryptographicOperations.ZeroMemory(value.ExactEnvelope),
                    ParseIngressMetadata = ParseMetadata
                });
            await runtime.RegisterPeerAsync(new ProductionDirectDataPeerRegistration
            {
                PeerDeviceId = peer,
                Path = Fence(),
                KeyEpoch = 1,
                AdmissionGeneration = 1,
                PairwiseKey = Enumerable.Repeat((byte)0x45, 32).ToArray(),
                SignalKeyId = ByteString.CopyFromUtf8("device-key-validation").ToByteArray(),
                SignNativeTranscript = _ => Enumerable.Repeat((byte)0x77, 64).ToArray(),
                Native = native
            }).ConfigureAwait(false);

            Require(!await ProductionAgentExactEnvelopeRouter.RouteAsync(
                    runtime,
                    outbox,
                    peer,
                    session,
                    9,
                    exact,
                    directEligible: true,
                    (_, reason) =>
                    {
                        if (reason == "DIRECT_ROUTE_DROPPED") directFaultCallbacks++;
                    },
                    CancellationToken.None).ConfigureAwait(false),
                "A Relay ownership race was reported as a successful Direct publication.");
            Require(directFaultCallbacks == 1 &&
                    outbox.Snapshot().Single().PendingFrames == 1,
                "A dropped Direct transfer did not wake retry or created a second Relay payload.");
            CryptographicOperations.ZeroMemory(exact);
            CryptographicOperations.ZeroMemory(filler);
        }

        private static async Task ValidateRelayCapacityReleaseWakesBlockedSessionsAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var occupyingSession = Guid.NewGuid();
            var blockedSession = Guid.NewGuid();
            var occupying = EnvelopeBytes(local, peer, occupyingSession, 1);
            var blocked = EnvelopeBytes(local, peer, blockedSession, 1);
            var publisherWakes = 0;
            await using var outbox = new ProductionRelayExactOutbox(
                frameCapacityPerPeer: 1,
                byteCapacityPerPeer: ProductionRelayExactOutbox.MaximumBytesPerPeer,
                retryDelay: TimeSpan.FromMilliseconds(5));
            ProductionAgentExactEnvelopeRouter.AcceptOwnedRelayEnvelope(
                outbox,
                new ProductionDirectDataRelayEnvelope
                {
                    PeerDeviceId = peer,
                    SessionId = occupyingSession,
                    EnvelopeSequence = 1,
                    ExactEnvelope = occupying.ToArray()
                });
            Require(!outbox.CanAccept(new ProductionDirectDataRelayEnvelope
                {
                    PeerDeviceId = peer,
                    SessionId = blockedSession,
                    EnvelopeSequence = 1,
                    ExactEnvelope = blocked
                }),
                "The Relay capacity test did not block its second session.");

            var removed = outbox.PruneDurablyAcknowledged(peer, occupyingSession, 1);
            Require(ProductionAgentRuntime.NotifyRelayCapacityReleased(
                    removed,
                    () => publisherWakes++) &&
                publisherWakes == 1 &&
                outbox.CanAccept(new ProductionDirectDataRelayEnvelope
                {
                    PeerDeviceId = peer,
                    SessionId = blockedSession,
                    EnvelopeSequence = 1,
                    ExactEnvelope = blocked
                }),
                "A durable ACK did not wake a different session blocked on Relay capacity.");
            Require(!ProductionAgentRuntime.NotifyRelayCapacityReleased(
                    0,
                    () => publisherWakes++) && publisherWakes == 1,
                "A no-op durable ACK generated a spurious publisher wake.");
            CryptographicOperations.ZeroMemory(occupying);
            CryptographicOperations.ZeroMemory(blocked);
        }

        private static void ValidateProductionGateAndCommonIngressShape()
        {
            var metadata = typeof(ProductionAgentRuntime).Assembly
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .SingleOrDefault(value => string.Equals(
                    value.Key,
                    "TermLink.DirectDataPlaneEnabled",
                    StringComparison.Ordinal));
            Require(metadata != null &&
                    bool.TryParse(metadata.Value, out var compiledAcceptance) &&
                    compiledAcceptance == ProductionAgentRuntime.DirectTransportDataPlaneBuildEnabled,
                "The Direct data-plane binary marker did not match its compiled gate.");
            var handler = typeof(ProductionAgentRuntime).GetMethod(
                "HandleIncomingAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(handler != null && handler.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(ProductionDirectIngressSourceV5)) &&
                handler.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(ProductionDirectPathFenceV5)),
                "Relay and Direct do not expose one path-fenced carrier-aware business ingress handler.");
            Require(typeof(ProductionAgentRuntime).GetField(
                    "relayExactOutbox",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(ProductionAgentRuntime).GetField(
                    "directDataRuntime",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(ProductionAgentRuntime).GetField(
                    "directSignaling",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(ProductionAgentRuntime).GetField(
                    "directShadowRuntime",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "ProductionAgentRuntime is missing its long-lived Relay/shared-signalling owners.");
            Require(typeof(ProductionAgentRuntime).GetMethod(
                    "DirectRoutingPolicyLoopAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null &&
                typeof(ProductionAgentRuntime).GetMethod(
                    "SnapshotActiveDirectPeerIds",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "Windows Direct routing policy is missing periodic refresh or bounded retirement.");
        }

        private static ProductionDirectDataIngressMetadata ParseMetadata(byte[] exact)
        {
            var envelope = Envelope.Parser.ParseFrom(exact);
            return new ProductionDirectDataIngressMetadata
            {
                SessionId = Guid.ParseExact(Convert.ToHexString(envelope.SessionId.Span), "N"),
                EnvelopeSequence = envelope.Seq
            };
        }

        private static byte[] EnvelopeBytes(
            Guid local,
            Guid peer,
            Guid session,
            ulong sequence) => new Envelope
        {
            ProtocolVersion = 1,
            MessageType = MessageType.OutputChunk,
            SessionId = ByteString.CopyFrom(GuidBytes(session)),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
            Direction = Direction.PcToAndroid,
            Seq = sequence,
            SentAtMs = 1,
            KeyId = ByteString.CopyFromUtf8("epoch-00000001"),
            Nonce = ByteString.CopyFrom(new byte[12]),
            Ciphertext = ByteString.CopyFrom(new byte[16])
        }.ToByteArray();

        private static Envelope InboundEnvelope(
            Guid local,
            Guid peer,
            MessageType messageType,
            Direction direction) => new()
        {
            ProtocolVersion = 1,
            MessageType = messageType,
            SessionId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(local)),
            Direction = direction,
            Seq = 1,
            SentAtMs = 1,
            KeyId = ByteString.CopyFromUtf8("epoch-00000001"),
            Nonce = ByteString.CopyFrom(new byte[12]),
            Ciphertext = ByteString.CopyFrom(new byte[16])
        };

        private static ProductionDirectPathFenceV5 Fence() => new()
        {
            PathEpoch = 1,
            ConnectionId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid()
        };

        private static byte[] GuidBytes(Guid value) =>
            Convert.FromHexString(value.ToString("N"));

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeSignaling : IProductionDirectShadowSignaling
        {
            public bool Start() => true;
            public bool TrySend(TransportSignalEnvelopeV1 envelope) => true;
            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                yield break;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
                FetchConfigAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Disabled());
            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
                IssueTicketAsync(
                    Guid targetDeviceId,
                    Guid connectionId,
                    ProductionDirectTransportServerConfig config,
                    CancellationToken cancellationToken = default) =>
                Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Disabled());
            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(
                DirectPathTicketV1 ticket,
                Guid initiatorDeviceId,
                Guid connectionId,
                ProductionDirectTransportServerConfig config) =>
                throw new NotSupportedException();
        }

        private sealed class RejectingNative : IProductionDirectDataNativePeer
        {
            private readonly Action beforeRoute;

            internal RejectingNative(Action beforeRoute = null)
            {
                this.beforeRoute = beforeRoute;
            }

            public bool NativeAvailable => true;
            public ProductionDirectNativeResultV5 RouteEnvelope(
                ProductionDirectRouteEnvelopeRequestV5 request,
                byte[] exactEnvelope)
            {
                beforeRoute?.Invoke();
                return ProductionDirectNativeResultV5.QueueFull;
            }
            public ProductionDirectNativeResultV5 ArbitrateIngress(
                ProductionDirectIngressRequestV5 request,
                byte[] exactEnvelope) => ProductionDirectNativeResultV5.QueueFull;
            public ProductionDirectNativeResultV5 TickIngress(
                ProductionDirectPathFenceV5 path,
                ulong nowMilliseconds) => ProductionDirectNativeResultV5.NoData;
            public ProductionDirectSigningPayloadV5 ReceiveSigningPayload(
                ProductionDirectPathFenceV5 path) => new()
                {
                    Status = ProductionDirectNativeResultV5.NoData,
                    Payload = Array.Empty<byte>()
                };
            public ProductionDirectNativeResultV5 SubmitSignature(
                ProductionDirectPathFenceV5 path,
                ulong requestId,
                byte[] signature) => ProductionDirectNativeResultV5.InvalidArgument;
            public (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
                PollRouteAction(ProductionDirectPathFenceV5 path) =>
                (ProductionDirectNativeResultV5.NoData, null);
            public ProductionDirectNativeReceiveV5 ReceiveEnvelope(
                ProductionDirectPathFenceV5 path) => new()
                {
                    Status = ProductionDirectNativeResultV5.NoData,
                    Envelope = Array.Empty<byte>()
                };
            public IReadOnlyList<ProductionDirectNativeEventV5> Poll(int maximumEvents = 32) =>
                Array.Empty<ProductionDirectNativeEventV5>();
            public ProductionDirectNativeResultV5 UpdateAppliedCursor(
                ProductionDirectCursorUpdateV5 request) => ProductionDirectNativeResultV5.Accepted;
            public ProductionDirectNativeResultV5 DeregisterSession(
                ProductionDirectSessionDeregisterV5 request) =>
                ProductionDirectNativeResultV5.Accepted;
            public ProductionDirectNativeResultV5 NetworkChanged(
                ProductionDirectPathFenceV5 path) => ProductionDirectNativeResultV5.Accepted;
            public ProductionDirectNativeResultV5 Cancel(
                ProductionDirectPathFenceV5 path) => ProductionDirectNativeResultV5.Cancelled;
            public void Dispose() { }
        }
    }
}

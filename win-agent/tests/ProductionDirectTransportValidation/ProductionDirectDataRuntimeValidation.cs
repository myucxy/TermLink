using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionDirectDataRuntimeValidation
    {
        internal static async Task ValidateAsync()
        {
            await ValidateExactFallbackAndPeerIsolationAsync().ConfigureAwait(false);
            await ValidateStalePathCannotReplaceSuccessorAsync().ConfigureAwait(false);
            await ValidateFencedRetirementCannotRemoveSuccessorAsync().ConfigureAwait(false);
            await ValidateBoundedMailboxFallsBackAndRetiresOnlyOnePeerAsync().ConfigureAwait(false);
            await ValidateMailboxRelayFailureStillRetiresExactActorAsync().ConfigureAwait(false);
            await ValidateMailboxIngressFailureStillRetiresExactActorAsync().ConfigureAwait(false);
            await ValidateCursorMailboxRejectionRetiresExactActorAsync().ConfigureAwait(false);
            await ValidateNetworkChangeCannotBeDroppedByFullMailboxAsync().ConfigureAwait(false);
            await ValidateNativeRelayOutcomeAndOwnershipAsync().ConfigureAwait(false);
            await ValidateRelayCapacityRaceReturnsDroppedForDurableRetryAsync().ConfigureAwait(false);
            await ValidateNativeAcceptedOwnershipPreventsManagedDuplicateAsync().ConfigureAwait(false);
            await ValidateRelayOnlyControlEnvelopeUsesTheSameRouterAsync().ConfigureAwait(false);
            await ValidateIngressAndReceiptAuthorityAsync().ConfigureAwait(false);
            await ValidateSingleReaderSignalGatesAndShadowDispatchAsync().ConfigureAwait(false);
            await ValidatePromotionNominationAndSignalCursorHandoffAsync().ConfigureAwait(false);
            await ValidatePromotionFencedByNetworkChangeAsync().ConfigureAwait(false);
            await ValidatePromotionFailureIsOneShotAsync().ConfigureAwait(false);
            await ValidateBidirectionalCutoverAndFallbackAsync().ConfigureAwait(false);
            await ValidateIncrementalSessionProjectionAndDeregistrationAsync().ConfigureAwait(false);
            await ValidateIncrementalBatchFailureIsolationAsync().ConfigureAwait(false);
            await ValidateReadOnlyCumulativeDiagnosticsAsync().ConfigureAwait(false);
            await ValidateTerminalNativeDiagnosticsArePublishedAsync().ConfigureAwait(false);
            await ValidateDisposeFailureStillClearsAndIsolatesAsync().ConfigureAwait(false);
            await ValidateNominationCallbackFailuresAreContainedAsync().ConfigureAwait(false);
            await ValidateWrongSignalKeyRetiresOnlyItsPeerAsync().ConfigureAwait(false);
            await ValidateSignRequestIsNarrowAndZeroedAsync().ConfigureAwait(false);
            await ValidateRetirementDrainsOnlyFencedRecoveryAsync().ConfigureAwait(false);
            await ValidateTicketRenewalSlidesTheProofGenerationAsync().ConfigureAwait(false);
            await ValidateRenewalDueWithoutARenewalDelegateIsInertAsync().ConfigureAwait(false);
        }

        private static async Task ValidateReadOnlyCumulativeDiagnosticsAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            var relay = new ConcurrentQueue<byte[]>();
            var nativeActions = new ConcurrentQueue<ProductionDirectDataNativeAction>();
            var replay = OutputEnvelope(local, peer, session, 8);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.IngressHold,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = session,
                EnvelopeSequence = 6,
                Carrier = ProductionDirectCarrierV5.LanDirect,
                PayloadBytes = 10
            });
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.IngressDrop,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = session,
                EnvelopeSequence = 7,
                Carrier = ProductionDirectCarrierV5.LanDirect,
                PayloadBytes = 11
            });
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 8,
                Carrier = ProductionDirectCarrierV5.Relay,
                Reason = ProductionDirectRouteReasonV5.DirectFailure,
                PayloadBytes = checked((uint)replay.Length),
                Payload = replay.ToArray()
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.EnvelopeQueued,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Outbound |
                    ProductionDirectNativeEventFlagsV5.TransportCopy,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 4,
                PayloadBytes = 12
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.EnvelopeAvailable,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Inbound,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 9,
                PayloadBytes = 13
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.TransportReceipt,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Inbound |
                    ProductionDirectNativeEventFlagsV5.TransportCopy,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 8,
                ReleasedFrames = 1
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.TransportReceipt,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Outbound,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 10
            });

            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options(
                    relay: relay,
                    nativeActions: nativeActions));
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);
            Require(await runtime.UpdateAppliedCursorAsync(
                    peer, ProductionDirectDirectionV5.AndroidToPc, session, 1)
                    .ConfigureAwait(false),
                "The diagnostics fixture could not drain native observations.");
            var first = runtime.Snapshot().Single();
            byte[] relayed = null;
            var forwardedKinds = new List<ProductionDirectRouteActionKindV5>();
            while (nativeActions.TryDequeue(out var forwarded))
            {
                forwardedKinds.Add(forwarded.Action.Kind);
                CryptographicOperations.ZeroMemory(forwarded.PairwiseKey);
            }
            Require(first.DirectFramesSent == 1 && first.DirectFramesReceived == 1 &&
                    first.TransportReceiptsSent == 1 &&
                    first.TransportReceiptsReceived == 1 &&
                    first.TransportReceiptHighWatermark == 8 &&
                    first.LastReceivedEnvelopeFrameSequence == 9 &&
                    first.IngressHoldCount == 1 && first.IngressDropCount == 1 &&
                    first.IngressDuplicateDropCount == null &&
                    first.IngressDigestConflictCount == null && first.IngressGapCount == null &&
                    first.RelayReplayEnvelopeCount == 1 &&
                    first.RelayReplayBytes == (ulong)replay.Length &&
                    first.FallbackCount == 0 &&
                    string.IsNullOrEmpty(first.LastFallbackReasonCode) &&
                    first.NativeQueueLimitCount == 0 && first.PeerMailboxBytes == 0 &&
                    (first.PcToAndroidSessionCarriers as
                        ICollection<KeyValuePair<Guid, ProductionDirectCarrierV5>>)?.IsReadOnly == true &&
                    (first.AndroidToPcSessionCarriers as
                        ICollection<KeyValuePair<Guid, ProductionDirectCarrierV5>>)?.IsReadOnly == true &&
                    forwardedKinds.SequenceEqual(new[]
                    {
                        ProductionDirectRouteActionKindV5.IngressHold,
                        ProductionDirectRouteActionKindV5.IngressDrop
                    }) &&
                    relay.TryDequeue(out relayed) && relayed.SequenceEqual(replay),
                "The peer snapshot fabricated, lost, or misclassified native diagnostics.");
            CryptographicOperations.ZeroMemory(relayed);

            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.EnvelopeAvailable,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Inbound,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 7,
                PayloadBytes = 1
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.TransportReceipt,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Inbound |
                    ProductionDirectNativeEventFlagsV5.TransportCopy,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 6
            });
            Require(await runtime.UpdateAppliedCursorAsync(
                    peer, ProductionDirectDirectionV5.AndroidToPc, session, 2)
                    .ConfigureAwait(false),
                "The diagnostics fixture could not accumulate a second observation batch.");
            var cumulative = runtime.Snapshot().Single();
            Require(cumulative.DirectFramesReceived == 2 &&
                    cumulative.TransportReceiptsReceived == 2 &&
                    cumulative.TransportReceiptHighWatermark == 8 &&
                    cumulative.LastReceivedEnvelopeFrameSequence == 9,
                "Cumulative counters regressed their monotonic high-watermarks.");
            CryptographicOperations.ZeroMemory(replay);
        }

        private static async Task ValidateTerminalNativeDiagnosticsArePublishedAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            var published = new ConcurrentQueue<ProductionDirectDataPeerSnapshot>();
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.QueueLimit,
                Status = ProductionDirectNativeResultV5.QueueFull,
                Flags = ProductionDirectNativeEventFlagsV5.None,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId
            });

            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(peerStates: published));
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);
            Require(!await runtime.UpdateAppliedCursorAsync(
                    peer,
                    ProductionDirectDirectionV5.AndroidToPc,
                    session,
                    1).ConfigureAwait(false),
                "A terminal native queue event was reported as a successful cursor update.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);
            var terminal = published.LastOrDefault(value => value.PeerDeviceId == peer);
            Require(terminal != null && terminal.Retired && terminal.SensitiveStateCleared &&
                    terminal.NativeQueueLimitCount == 1 &&
                    terminal.LastFault == "DIRECT_NATIVE_FAILED",
                "The terminal actor publication lost its native queue-limit diagnosis.");
        }

        private static async Task ValidateIncrementalSessionProjectionAndDeregistrationAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var sessionA = Guid.NewGuid();
            var sessionB = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var binding = Enumerable.Repeat((byte)0x71, 32).ToArray();
            var nomination = PromotionNomination();
            var native = new FakeNative
            {
                CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                    new ProductionDirectPathInfoV5
                    {
                        Path = Fence(2),
                        PathKind = ProductionDirectPathKindV5.Lan,
                        Ready = true,
                        DirectPathBinding = binding.ToArray()
                    })
            };
            await using var runtime = new ProductionDirectDataRuntime(
                local, signaling, Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 31, true);
            Require(runtime.Start(), "The incremental-session fixture did not start.");

            var nominationCompleted = false;
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                peer, Fence(2).ConnectionId, 1, 31, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), nomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                _ => nominationCompleted = true,
                _ => { }))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The incremental-session peer could not be promoted.");
            }
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var nominationResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                nomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2, 1, 2, nominationResult));
            }
            finally { CryptographicOperations.ZeroMemory(nominationResult); }
            await WaitUntilAsync(() => nominationCompleted &&
                runtime.Snapshot().Single().ReadyForSessionCutover).ConfigureAwait(false);

            Require(await runtime.StartPcToAndroidCutoverAsync(
                    peer,
                    new[] { new ProductionDirectSessionCutoverBarrier
                        { SessionId = sessionA, FirstDirectSequence = 10 } })
                    .ConfigureAwait(false) &&
                    native.LastProposal?.Sessions.Single().SessionId == sessionA &&
                    native.LastProposal.Sessions.Single().FirstDirectSequence == 10 &&
                    native.LastProposal.DirectPathBinding.SequenceEqual(binding),
                "The actor-owned proposal leaked or lost its session/path binding.");
            var firstBarrier = native.LastProposal.BarrierId;
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                Carrier = ProductionDirectCarrierV5.LanDirect
            });
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.SessionCarrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = sessionA, Carrier = ProductionDirectCarrierV5.LanDirect
            });
            Require(await runtime.UpdateAppliedCursorAsync(
                    peer, ProductionDirectDirectionV5.AndroidToPc, sessionA, 1)
                    .ConfigureAwait(false) &&
                    runtime.Snapshot().Single() is var first &&
                    first.CommittedCutoverCount == 1 &&
                    first.PcToAndroidSessionCarriers.TryGetValue(
                        sessionA, out var firstCarrier) &&
                    firstCarrier == ProductionDirectCarrierV5.LanDirect,
                "The first committed session was not projected independently.");

            Require(await runtime.StartPcToAndroidCutoverAsync(
                    peer,
                    new[] { new ProductionDirectSessionCutoverBarrier
                        { SessionId = sessionB, FirstDirectSequence = 20 } })
                    .ConfigureAwait(false),
                "A late session could not start an incremental cutover.");
            var secondBarrier = native.LastProposal.BarrierId;
            Require(secondBarrier != firstBarrier,
                "An incremental cutover reused a committed barrier identifier.");
            // The direction is already Direct, so the native incremental
            // commit intentionally emits SESSION_CARRIER without repeating
            // the direction-level CARRIER action.
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.SessionCarrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = sessionB, Carrier = ProductionDirectCarrierV5.LanDirect
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, sessionB, 1)
                .ConfigureAwait(false);
            var both = runtime.Snapshot().Single();
            Require(both.CommittedCutoverCount == 2 &&
                    both.PcToAndroidSessionCarriers.Count == 2,
                "The second commit erased the first session's fallback authority.");

            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.SessionCarrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = sessionA, Carrier = ProductionDirectCarrierV5.Relay
            });
            Require(await runtime.DeregisterSessionAsync(
                    peer, ProductionDirectDirectionV5.PcToAndroid, sessionA)
                    .ConfigureAwait(false) &&
                    native.DeregisterCalls == 1 &&
                    runtime.Snapshot().Single() is var deregistered &&
                    !deregistered.PcToAndroidSessionCarriers.ContainsKey(sessionA) &&
                    deregistered.PcToAndroidSessionCarriers.ContainsKey(sessionB) &&
                    deregistered.CommittedCutoverCount == 1,
                "Single-session deregistration disturbed a sibling Direct session.");

            var fallbackSignalsBefore = signaling.Sent.Count(value =>
                value.SignalType == TransportSignalTypeV1.Fallback);
            // A newer signalling generation may withdraw new-cutover
            // capability while an established Direct session still needs to
            // converge.  FALLBACK remains cleanup authority for its committed
            // barrier and must not be filtered with proposal admission.
            runtime.ObserveSignalingState(true, 32, false);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Fallback,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                BarrierId = Guid.Empty,
                Reason = ProductionDirectRouteReasonV5.DirectFailure
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, sessionB, 2)
                .ConfigureAwait(false);
            var fallback = runtime.Snapshot().Single();
            Require(fallback.PcToAndroidCarrier == ProductionDirectCarrierV5.Relay &&
                    fallback.PcToAndroidSessionCarriers.Count == 0 &&
                    fallback.CommittedCutoverCount == 0 &&
                    fallback.FallbackCount == 1 &&
                    fallback.LastFallbackReasonCode == "DIRECT_FAILURE" &&
                    signaling.Sent.Count(value =>
                        value.SignalType == TransportSignalTypeV1.Fallback) ==
                        fallbackSignalsBefore + 1,
                "A full-direction failure did not select exactly one committed wire barrier.");
        }

        private static async Task ValidateIncrementalBatchFailureIsolationAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var sessionA = Guid.NewGuid();
            var sessionB = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var binding = Enumerable.Repeat((byte)0x72, 32).ToArray();
            var nomination = PromotionNomination();
            var native = new FakeNative
            {
                CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                    new ProductionDirectPathInfoV5
                    {
                        Path = Fence(2),
                        PathKind = ProductionDirectPathKindV5.Lan,
                        Ready = true,
                        DirectPathBinding = binding.ToArray()
                    })
            };
            await using var runtime = new ProductionDirectDataRuntime(
                local, signaling, Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 41, true);
            Require(runtime.Start(), "The batch-isolation fixture did not start.");
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                peer, Fence(2).ConnectionId, 1, 41, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), nomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                _ => { },
                _ => { }))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The batch-isolation peer could not be promoted.");
            }
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var nominationResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                nomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2, 1, 2, nominationResult));
            }
            finally { CryptographicOperations.ZeroMemory(nominationResult); }
            await WaitUntilAsync(() => runtime.Snapshot().Single().ReadyForSessionCutover)
                .ConfigureAwait(false);

            Require(await runtime.StartPcToAndroidCutoverAsync(
                    peer,
                    new[] { new ProductionDirectSessionCutoverBarrier
                        { SessionId = sessionA, FirstDirectSequence = 10 } })
                    .ConfigureAwait(false),
                "The committed baseline batch could not start.");
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                Carrier = ProductionDirectCarrierV5.LanDirect
            });
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.SessionCarrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = sessionA, Carrier = ProductionDirectCarrierV5.LanDirect
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, sessionA, 1)
                .ConfigureAwait(false);
            Require(runtime.Snapshot().Single() is var committed &&
                    committed.CommittedCutoverCount == 1 &&
                    committed.PcToAndroidSessionCarriers.ContainsKey(sessionA),
                "The batch-isolation baseline did not commit.");

            native.StartProposalResult = ProductionDirectNativeResultV5.Protocol;
            Require(!await runtime.StartPcToAndroidCutoverAsync(
                    peer,
                    new[] { new ProductionDirectSessionCutoverBarrier
                        { SessionId = sessionB, FirstDirectSequence = 20 } })
                    .ConfigureAwait(false),
                "A native batch-local refusal was reported as a successful proposal.");
            var afterRefusal = runtime.Snapshot().Single();
            Require(!afterRefusal.Retired && afterRefusal.ActiveCutoverCount == 0 &&
                    afterRefusal.CommittedCutoverCount == 1 &&
                    afterRefusal.PcToAndroidSessionCarriers.ContainsKey(sessionA),
                "A batch-local native refusal retired or changed the committed session.");

            var remoteProposal = ProductionDirectCutoverCodec.CreateProposal(
                Fence(2).ConnectionId,
                2,
                Direction.AndroidToPc,
                Guid.NewGuid(),
                binding,
                new[] { new ProductionDirectSessionCutoverBarrier
                    { SessionId = sessionB, FirstDirectSequence = 20 } },
                500);
            var remoteProposalBytes = ProductionDirectCutoverSignalCodec.CreateProposal(
                remoteProposal);
            native.ReceiveProposalResult = ProductionDirectNativeResultV5.Protocol;
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.CutoverProposal, 2, 3, remoteProposalBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(remoteProposalBytes);
                CryptographicOperations.ZeroMemory(remoteProposal.DirectPathBinding);
                CryptographicOperations.ZeroMemory(remoteProposal.BarrierSha256);
                CryptographicOperations.ZeroMemory(remoteProposal.WireBytes);
            }
            await WaitUntilAsync(() => native.ReceiveProposalCalls == 1 &&
                runtime.Snapshot().Single().ActiveCutoverCount == 0)
                .ConfigureAwait(false);
            Require(runtime.Snapshot().Single() is var afterRemoteRefusal &&
                    !afterRemoteRefusal.Retired &&
                    afterRemoteRefusal.ActiveCutoverCount == 0 &&
                    afterRemoteRefusal.CommittedCutoverCount == 1 &&
                    afterRemoteRefusal.PcToAndroidSessionCarriers.ContainsKey(sessionA),
                "A valid remote batch refusal retired the peer or changed the committed session.");

            native.StartProposalResult = ProductionDirectNativeResultV5.Accepted;
            native.ReceiveProposalResult = ProductionDirectNativeResultV5.Accepted;
            signaling.SendAllowed = false;
            Require(!await runtime.StartPcToAndroidCutoverAsync(
                    peer,
                    new[] { new ProductionDirectSessionCutoverBarrier
                        { SessionId = sessionB, FirstDirectSequence = 20 } })
                    .ConfigureAwait(false),
                "A failed proposal signal was reported as sent.");
            var afterSignalFailure = runtime.Snapshot().Single();
            Require(!afterSignalFailure.Retired && afterSignalFailure.ActiveCutoverCount == 1 &&
                    afterSignalFailure.CommittedCutoverCount == 1 &&
                    afterSignalFailure.PcToAndroidSessionCarriers.ContainsKey(sessionA),
                "A proposal signalling failure retired the peer or erased committed authority.");

            var failedBarrier = native.LastProposal.BarrierId;
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Fallback,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                BarrierId = failedBarrier,
                Reason = ProductionDirectRouteReasonV5.NegotiationTimeout
            });
            native.TickResult = ProductionDirectNativeResultV5.Protocol;
            await WaitUntilAsync(() => runtime.Snapshot().Single().ActiveCutoverCount == 0)
                .ConfigureAwait(false);
            native.TickResult = ProductionDirectNativeResultV5.Accepted;
            var afterTimeout = runtime.Snapshot().Single();
            Require(!afterTimeout.Retired && afterTimeout.ActiveCutoverCount == 0 &&
                    afterTimeout.CommittedCutoverCount == 1 &&
                    afterTimeout.PcToAndroidSessionCarriers.ContainsKey(sessionA),
                "A failed fallback notice prevented local batch cleanup.");

            signaling.SendAllowed = true;
            runtime.ObserveSignalingState(true, 42, false);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Fallback,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                BarrierId = Guid.Empty,
                Reason = ProductionDirectRouteReasonV5.DirectFailure
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, sessionA, 3)
                .ConfigureAwait(false);
            var afterCommittedFallback = runtime.Snapshot().Single();
            Require(!afterCommittedFallback.Retired &&
                    afterCommittedFallback.PcToAndroidCarrier == ProductionDirectCarrierV5.Relay &&
                    afterCommittedFallback.PcToAndroidSessionCarriers.Count == 0 &&
                    afterCommittedFallback.CommittedCutoverCount == 0 &&
                    signaling.Sent.Last().SignalType == TransportSignalTypeV1.Fallback,
                "A committed fallback could not converge after capability withdrawal.");
        }

        private static async Task ValidateStalePathCannotReplaceSuccessorAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var current = new FakeNative();
            var stale = new FakeNative();
            var duplicate = new FakeNative();
            var successor = new FakeNative();
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());

            Require(await runtime.RegisterPeerAsync(Registration(peer, 5, current, pathEpoch: 5))
                    .ConfigureAwait(false) &&
                    !await runtime.RegisterPeerAsync(Registration(peer, 6, stale, pathEpoch: 4))
                        .ConfigureAwait(false) &&
                    !await runtime.RegisterPeerAsync(Registration(peer, 6, duplicate, pathEpoch: 5))
                        .ConfigureAwait(false),
                "A stale or duplicate Direct path replaced the current per-peer actor.");
            Require(runtime.Snapshot().Single().Path.PathEpoch == 5 &&
                    await runtime.RouteEnvelopeAsync(
                        peer, session, 1, OutputEnvelope(local, peer, session, 1), true)
                        .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted &&
                    current.DisposeCalls == 0 && stale.DisposeCalls == 0 && duplicate.DisposeCalls == 0,
                "A rejected registration changed ownership or routing for the current Direct path.");

            Require(await runtime.RegisterPeerAsync(Registration(peer, 6, successor, pathEpoch: 6))
                    .ConfigureAwait(false),
                "A higher Direct path epoch could not replace its predecessor.");
            await WaitUntilAsync(() => current.DisposeCalls == 1).ConfigureAwait(false);
            Require(runtime.Snapshot().Single().Path.PathEpoch == 6,
                "The higher Direct path epoch did not become the sole peer actor.");
        }

        private static async Task ValidateFencedRetirementCannotRemoveSuccessorAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var predecessorNative = new FakeNative();
            var successorNative = new FakeNative();
            var predecessorPath = new ProductionDirectPathFenceV5
            {
                PathEpoch = 20,
                ConnectionId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid()
            };
            var successorPath = new ProductionDirectPathFenceV5
            {
                PathEpoch = 21,
                ConnectionId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid()
            };
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());
            Require(await runtime.RegisterPeerAsync(
                    Registration(peer, 20, predecessorNative, path: predecessorPath))
                    .ConfigureAwait(false) &&
                    await runtime.RegisterPeerAsync(
                        Registration(peer, 21, successorNative, path: successorPath))
                        .ConfigureAwait(false),
                "The retirement race fixture could not install its successor path.");
            await WaitUntilAsync(() => predecessorNative.DisposeCalls == 1).ConfigureAwait(false);

            var wrongConnection = new ProductionDirectPathFenceV5
            {
                PathEpoch = successorPath.PathEpoch,
                ConnectionId = Guid.NewGuid(),
                AttemptId = successorPath.AttemptId
            };
            var wrongAttempt = new ProductionDirectPathFenceV5
            {
                PathEpoch = successorPath.PathEpoch,
                ConnectionId = successorPath.ConnectionId,
                AttemptId = Guid.NewGuid()
            };
            Require(!await runtime.RetirePeerAsync(
                        peer, predecessorPath, "LATE_PREDECESSOR_CALLBACK").ConfigureAwait(false) &&
                    !await runtime.RetirePeerAsync(
                        peer, wrongConnection, "WRONG_CONNECTION").ConfigureAwait(false) &&
                    !await runtime.RetirePeerAsync(
                        peer, wrongAttempt, "WRONG_ATTEMPT").ConfigureAwait(false) &&
                    runtime.Snapshot().Single().Path.Equals(successorPath) &&
                    successorNative.DisposeCalls == 0,
                "A partial or predecessor path fence retired the current successor actor.");

            Require(await runtime.RetirePeerAsync(
                        peer, successorPath, "EXACT_PATH_RETIRE").ConfigureAwait(false) &&
                    runtime.Snapshot().Count == 0 && successorNative.DisposeCalls == 1,
                "An exact three-field path fence did not retire its current actor.");
        }

        private static async Task ValidateExactFallbackAndPeerIsolationAsync()
        {
            var local = Guid.NewGuid();
            var peerA = Guid.NewGuid();
            var peerB = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relay = new ConcurrentQueue<byte[]>();
            var signals = new FakeSignaling();
            var options = Options(relay: relay);
            await using var runtime = new ProductionDirectDataRuntime(local, signals, options);
            runtime.ObserveSignalingState(true, 7, true);
            var nativeA = new FakeNative { RouteResult = ProductionDirectNativeResultV5.QueueFull };
            var nativeB = new FakeNative();
            await runtime.RegisterPeerAsync(Registration(peerA, 7, nativeA)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(Registration(peerB, 7, nativeB)).ConfigureAwait(false);
            Require(runtime.Start(), "The production coordinator did not start.");

            var exactA = OutputEnvelope(local, peerA, session, 1);
            var exactB = OutputEnvelope(local, peerB, session, 1);
            var outcomeA = await runtime.RouteEnvelopeAsync(
                peerA, session, 1, exactA, true).ConfigureAwait(false);
            var outcomeB = await runtime.RouteEnvelopeAsync(
                peerB, session, 1, exactB, true).ConfigureAwait(false);
            Require(outcomeA == ProductionDirectDataRouteOutcome.RelaySent &&
                    outcomeB == ProductionDirectDataRouteOutcome.DirectAccepted &&
                    relay.TryDequeue(out var relayed) && relayed.SequenceEqual(exactA),
                "A native non-OK result did not Relay the byte-identical current Envelope.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peerA))
                .ConfigureAwait(false);
            Require(runtime.Snapshot().Any(value => value.PeerDeviceId == peerB),
                "One peer failure retired a healthy Android peer.");

            nativeB.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = Guid.Empty,
                Carrier = ProductionDirectCarrierV5.LanDirect
            });
            var exactB2 = OutputEnvelope(local, peerB, session, 2);
            Require(await runtime.RouteEnvelopeAsync(peerB, session, 2, exactB2, true)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                "The healthy peer stopped routing after another peer failed.");
            runtime.ObserveSignalingState(false, 8, false);
            runtime.ObserveSignalingState(true, 9, true);
            var snapshot = runtime.Snapshot().Single(value => value.PeerDeviceId == peerB);
            Require(snapshot.PcToAndroidCarrier == ProductionDirectCarrierV5.LanDirect &&
                    snapshot.CommittedDirect,
                "A signaling generation change retired committed Direct state.");

            var mismatchRejected = false;
            try
            {
                await runtime.RouteEnvelopeAsync(peerB, Guid.NewGuid(), 2, exactB2, true)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException) { mismatchRejected = true; }
            Require(mismatchRejected,
                "Mismatched opaque Envelope route metadata crossed the Direct boundary.");
        }

        private static async Task ValidateBoundedMailboxFallsBackAndRetiresOnlyOnePeerAsync()
        {
            var local = Guid.NewGuid();
            var peerA = Guid.NewGuid();
            var peerB = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relay = new ConcurrentQueue<byte[]>();
            var states = new ConcurrentQueue<ProductionDirectDataPeerSnapshot>();
            var gate = new ManualResetEventSlim(false);
            var blocking = new FakeNative { RouteGate = gate };
            var healthy = new FakeNative();
            var first = OutputEnvelope(local, peerA, session, 1);
            var options = Options(
                relay: relay,
                mailboxBytes: first.Length * 2L,
                peerStates: states);
            await using var runtime = new ProductionDirectDataRuntime(local, new FakeSignaling(), options);
            await runtime.RegisterPeerAsync(Registration(peerA, 3, blocking)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(Registration(peerB, 3, healthy)).ConfigureAwait(false);

            var route1 = runtime.RouteEnvelopeAsync(peerA, session, 1, first, true);
            Require(blocking.RouteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The blocking peer did not enter native routing.");
            var second = OutputEnvelope(local, peerA, session, 2);
            var third = OutputEnvelope(local, peerA, session, 3);
            var route2 = runtime.RouteEnvelopeAsync(peerA, session, 2, second, true);
            var route3 = runtime.RouteEnvelopeAsync(peerA, session, 3, third, true);
            var saturated = await route3.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(saturated == ProductionDirectDataRouteOutcome.RelaySent,
                "A byte-saturated actor did not immediately Relay its current Envelope.");
            gate.Set();
            await Task.WhenAll(route1, route2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peerA))
                .ConfigureAwait(false);
            Require(blocking.LastRouteEnvelope?.All(value => value == 0) == true &&
                    states.Any(value => value.PeerDeviceId == peerA && value.Retired &&
                        value.OwnedCommandDataCleared && value.SensitiveStateCleared),
                "Mailbox rejection/retirement retained an owned Envelope or peer secret.");
            var peerBEnvelope = OutputEnvelope(local, peerB, session, 1);
            Require(await runtime.RouteEnvelopeAsync(peerB, session, 1, peerBEnvelope, true)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                "Mailbox saturation escaped its peer actor.");
        }

        private static async Task ValidateMailboxRelayFailureStillRetiresExactActorAsync()
        {
            var local = Guid.NewGuid();
            var saturatedPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            using var gate = new ManualResetEventSlim(false);
            var saturatedNative = new FakeNative { RouteGate = gate };
            var healthyNative = new FakeNative();
            var first = OutputEnvelope(local, saturatedPeer, session, 1);
            var callbackCalls = 0;
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(
                    mailboxBytes: first.Length * 2L,
                    enqueueRelay: _ =>
                    {
                        Interlocked.Increment(ref callbackCalls);
                        throw new InvalidOperationException("Relay mailbox filled concurrently.");
                    }));
            await runtime.RegisterPeerAsync(
                Registration(saturatedPeer, 3, saturatedNative)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(
                Registration(healthyPeer, 3, healthyNative)).ConfigureAwait(false);

            var firstRoute = runtime.RouteEnvelopeAsync(
                saturatedPeer, session, 1, first, true);
            Require(saturatedNative.RouteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The throwing mailbox callback test did not block its actor.");
            var secondRoute = runtime.RouteEnvelopeAsync(
                saturatedPeer,
                session,
                2,
                OutputEnvelope(local, saturatedPeer, session, 2),
                true);
            var callbackRejected = false;
            try
            {
                await runtime.RouteEnvelopeAsync(
                    saturatedPeer,
                    session,
                    3,
                    OutputEnvelope(local, saturatedPeer, session, 3),
                    true).ConfigureAwait(false);
            }
            catch (Exception error) when (error.Message == "RELAY_OWNERSHIP_TRANSFER_REJECTED")
            {
                callbackRejected = true;
            }
            Require(callbackRejected && callbackCalls == 1,
                "The deterministic mailbox fallback did not reach its rejecting Relay callback.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value =>
                value.PeerDeviceId != saturatedPeer)).ConfigureAwait(false);
            gate.Set();
            await Task.WhenAll(firstRoute, secondRoute).WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Require(runtime.Snapshot().Single().PeerDeviceId == healthyPeer &&
                    await runtime.RouteEnvelopeAsync(
                        healthyPeer,
                        session,
                        1,
                        OutputEnvelope(local, healthyPeer, session, 1),
                        true).ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                "A throwing Relay callback skipped exact actor retirement or damaged another peer.");
        }

        private static async Task ValidateMailboxIngressFailureStillRetiresExactActorAsync()
        {
            var local = Guid.NewGuid();
            var saturatedPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            using var gate = new ManualResetEventSlim(false);
            var saturatedNative = new FakeNative { RouteGate = gate };
            var healthyNative = new FakeNative();
            var first = OutputEnvelope(local, saturatedPeer, session, 1);
            byte[] rejectedIngress = null;
            ProductionDirectPathFenceV5 rejectedPath = null;
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(
                    mailboxBytes: first.Length * 2L,
                    enqueueIngress: value =>
                    {
                        rejectedIngress = value.ExactEnvelope;
                        rejectedPath = value.Path;
                        throw new InvalidOperationException("Business ingress filled concurrently.");
                    }));
            await runtime.RegisterPeerAsync(
                Registration(saturatedPeer, 3, saturatedNative)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(
                Registration(healthyPeer, 3, healthyNative)).ConfigureAwait(false);

            var firstRoute = runtime.RouteEnvelopeAsync(
                saturatedPeer, session, 1, first, true);
            Require(saturatedNative.RouteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The throwing ingress callback test did not block its actor.");
            var secondRoute = runtime.RouteEnvelopeAsync(
                saturatedPeer,
                session,
                2,
                OutputEnvelope(local, saturatedPeer, session, 2),
                true);
            var exactIngress = InputEnvelope(saturatedPeer, local, session, 3);
            var callbackRejected = false;
            try
            {
                await runtime.ArbitrateIngressAsync(
                    saturatedPeer,
                    ProductionDirectIngressSourceV5.Relay,
                    exactIngress).ConfigureAwait(false);
            }
            catch (InvalidOperationException error) when (
                error.Message == "Business ingress filled concurrently.")
            {
                callbackRejected = true;
            }
            Require(callbackRejected && rejectedPath?.Equals(Fence(2)) == true &&
                    rejectedIngress?.All(value => value == 0) == true &&
                    exactIngress.Any(value => value != 0),
                "Rejected Relay ingress lost its actor fence or retained its transferred clone.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value =>
                value.PeerDeviceId != saturatedPeer)).ConfigureAwait(false);
            gate.Set();
            await Task.WhenAll(firstRoute, secondRoute).WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Require(runtime.Snapshot().Single().PeerDeviceId == healthyPeer &&
                    await runtime.RouteEnvelopeAsync(
                        healthyPeer,
                        session,
                        1,
                        OutputEnvelope(local, healthyPeer, session, 1),
                        true).ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                "A throwing ingress callback skipped exact actor retirement or damaged another peer.");
        }

        private static async Task ValidateCursorMailboxRejectionRetiresExactActorAsync()
        {
            var local = Guid.NewGuid();
            var saturatedPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            using var gate = new ManualResetEventSlim(false);
            var saturatedNative = new FakeNative { RouteGate = gate };
            var healthyNative = new FakeNative();
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());
            await runtime.RegisterPeerAsync(
                Registration(saturatedPeer, 3, saturatedNative)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(
                Registration(healthyPeer, 3, healthyNative)).ConfigureAwait(false);

            var pending = new List<Task<ProductionDirectDataRouteOutcome>>
            {
                runtime.RouteEnvelopeAsync(
                    saturatedPeer,
                    session,
                    1,
                    OutputEnvelope(local, saturatedPeer, session, 1),
                    true)
            };
            Require(saturatedNative.RouteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The cursor-mailbox test did not block its actor worker.");
            for (ulong sequence = 2; sequence <= 5; sequence++)
            {
                pending.Add(runtime.RouteEnvelopeAsync(
                    saturatedPeer,
                    session,
                    sequence,
                    OutputEnvelope(local, saturatedPeer, session, sequence),
                    true));
            }

            Require(!await runtime.UpdateAppliedCursorAsync(
                        saturatedPeer,
                        ProductionDirectDirectionV5.AndroidToPc,
                        session,
                        1).ConfigureAwait(false),
                "A full peer mailbox accepted an unfenced cursor update.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value =>
                value.PeerDeviceId != saturatedPeer)).ConfigureAwait(false);
            gate.Set();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var healthyExact = OutputEnvelope(local, healthyPeer, session, 1);
            Require(runtime.Snapshot().Single().PeerDeviceId == healthyPeer &&
                    await runtime.RouteEnvelopeAsync(
                        healthyPeer, session, 1, healthyExact, true).ConfigureAwait(false) ==
                    ProductionDirectDataRouteOutcome.DirectAccepted,
                "Cursor mailbox rejection retired a healthy peer or left the saturated actor installed.");
        }

        private static async Task ValidateNetworkChangeCannotBeDroppedByFullMailboxAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var gate = new ManualResetEventSlim(false);
            var native = new FakeNative { RouteGate = gate };
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());
            await runtime.RegisterPeerAsync(Registration(peer, 3, native)).ConfigureAwait(false);

            var routes = new List<Task<ProductionDirectDataRouteOutcome>>
            {
                runtime.RouteEnvelopeAsync(
                    peer, session, 1, OutputEnvelope(local, peer, session, 1), true)
            };
            Require(native.RouteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The network-change test did not enter native routing.");
            for (ulong sequence = 2; sequence <= 5; sequence++)
            {
                routes.Add(runtime.RouteEnvelopeAsync(
                    peer,
                    session,
                    sequence,
                    OutputEnvelope(local, peer, session, sequence),
                    true));
            }

            runtime.NotifyNetworkChanged();
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);
            gate.Set();
            await Task.WhenAll(routes).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(runtime.Snapshot().All(value => value.PeerDeviceId != peer),
                "A full mailbox dropped network change and left a stale Direct path alive.");
        }

        private static async Task ValidateNativeRelayOutcomeAndOwnershipAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relayOutbox = new ConcurrentQueue<byte[]>();
            var native = new FakeNative();
            var exact = OutputEnvelope(local, peer, session, 7);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 7,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)exact.Length),
                Payload = exact.ToArray()
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(relay: relayOutbox, relayImmediate: false));
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);

            var outcome = await runtime.RouteEnvelopeAsync(peer, session, 7, exact, true)
                .ConfigureAwait(false);
            byte[] owned = null;
            Require(outcome == ProductionDirectDataRouteOutcome.RelayDeferred &&
                    relayOutbox.TryDequeue(out owned) && owned.SequenceEqual(exact),
                "A native Relay route was misreported or failed to transfer exact ownership.");
            if (owned != null) CryptographicOperations.ZeroMemory(owned);
        }

        private static async Task ValidateRelayCapacityRaceReturnsDroppedForDurableRetryAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            var exact = OutputEnvelope(local, peer, session, 8);
            var actionPayload = exact.ToArray();
            byte[] rejectedOwned = null;
            byte[] retriedOwned = null;
            var attempts = 0;
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 8,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)actionPayload.Length),
                Payload = actionPayload
            });
            var options = Options(enqueueRelay: value =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    rejectedOwned = value.ExactEnvelope;
                    throw new InvalidOperationException("Relay filled after advisory CanAccept.");
                }
                retriedOwned = value.ExactEnvelope;
                return true;
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), options);
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);

            var outcome = await runtime.RouteEnvelopeAsync(peer, session, 8, exact, true)
                .ConfigureAwait(false);
            Require(outcome == ProductionDirectDataRouteOutcome.Dropped && attempts == 1 &&
                    rejectedOwned?.All(value => value == 0) == true &&
                    actionPayload.All(value => value == 0) && exact.Any(value => value != 0),
                "A CanAccept-to-enqueue race falsely reported Relay ownership or retained native bytes.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);

            Require(await runtime.RouteEnvelopeAsync(peer, session, 8, exact, true)
                        .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.RelaySent &&
                    attempts == 2 && retriedOwned != null && retriedOwned.SequenceEqual(exact),
                "The durable exact-byte source could not retry after Relay rejected native ownership.");
            CryptographicOperations.ZeroMemory(retriedOwned);
        }

        private static async Task ValidateNativeAcceptedOwnershipPreventsManagedDuplicateAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relayOutbox = new ConcurrentQueue<byte[]>();
            var native = new FakeNative();
            var exact = OutputEnvelope(local, peer, session, 9);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(3),
                Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                Carrier = ProductionDirectCarrierV5.LanDirect
            });
            native.CancelActions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 9,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)exact.Length),
                Payload = exact.ToArray()
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options(relay: relayOutbox));
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);

            Require(await runtime.RouteEnvelopeAsync(peer, session, 9, exact, true)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.RelayDeferred,
                "A native-owned Envelope fault was not reported as recovery-owned.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);
            byte[] recovered = null;
            Require(relayOutbox.Count == 1 && relayOutbox.TryDequeue(out recovered) &&
                    recovered.SequenceEqual(exact),
                "Native recovery and managed fallback duplicated an accepted Envelope.");
            if (recovered != null) CryptographicOperations.ZeroMemory(recovered);
        }

        private static async Task ValidateRelayOnlyControlEnvelopeUsesTheSameRouterAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relayOutbox = new ConcurrentQueue<byte[]>();
            var native = new FakeNative();
            var exact = OutputEnvelope(
                local, peer, session, 12, MessageType.SessionInterrupted);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 12,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)exact.Length),
                Payload = exact.ToArray()
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options(relay: relayOutbox));
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);

            byte[] relayed = null;
            Require(await runtime.RouteEnvelopeAsync(peer, session, 12, exact, false)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.RelaySent &&
                    relayOutbox.TryDequeue(out relayed) && relayed.SequenceEqual(exact),
                "A Relay-only control Envelope bypassed or was rejected by the common router.");
            if (relayed != null) CryptographicOperations.ZeroMemory(relayed);
        }

        private static async Task ValidateIngressAndReceiptAuthorityAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var delivered = new ConcurrentQueue<byte[]>();
            var deliveredSources = new ConcurrentQueue<ProductionDirectIngressSourceV5>();
            var deliveries = new ConcurrentQueue<ProductionDirectDataIngressDelivery>();
            var native = new FakeNative();
            var exact = InputEnvelope(peer, local, session, 11);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.IngressDeliver,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = session,
                EnvelopeSequence = 11,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)exact.Length),
                Payload = exact.ToArray()
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.TransportReceipt,
                Status = ProductionDirectNativeResultV5.Accepted,
                Flags = ProductionDirectNativeEventFlagsV5.Inbound,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 8,
                ReleasedFrames = 1
            });
            var options = Options(
                delivered: delivered,
                deliveredSources: deliveredSources,
                deliveries: deliveries);
            await using var runtime = new ProductionDirectDataRuntime(local, new FakeSignaling(), options);
            var registration = Registration(peer, 4, native);
            await runtime.RegisterPeerAsync(registration).ConfigureAwait(false);
            ProductionDirectDataIngressDelivery delivery = null;
            Require(await runtime.ArbitrateIngressAsync(
                    peer, ProductionDirectIngressSourceV5.Relay, exact).ConfigureAwait(false) &&
                    delivered.TryDequeue(out var value) && value.SequenceEqual(exact) &&
                    !ReferenceEquals(value, exact) &&
                    deliveredSources.TryDequeue(out var deliveredSource) &&
                    deliveredSource == ProductionDirectIngressSourceV5.Relay &&
                    deliveries.TryDequeue(out delivery) &&
                    delivery.Path?.Equals(registration.Path) == true &&
                    !ReferenceEquals(delivery.Path, registration.Path),
                "An actor ingress delivery was mislabeled, lost, shallow-copied, or not path-fenced.");
            var secondExact = InputEnvelope(peer, local, session, 12);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.IngressDeliver,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = session,
                EnvelopeSequence = 12,
                Carrier = ProductionDirectCarrierV5.LanDirect,
                PayloadBytes = checked((uint)secondExact.Length),
                Payload = secondExact.ToArray()
            });
            Require(await runtime.ArbitrateIngressAsync(
                    peer, ProductionDirectIngressSourceV5.Direct, secondExact).ConfigureAwait(false) &&
                    delivered.TryDequeue(out var secondValue) &&
                    secondValue.SequenceEqual(secondExact) &&
                    deliveredSources.TryDequeue(out var secondSource) &&
                    secondSource == ProductionDirectIngressSourceV5.Direct &&
                    deliveries.TryDequeue(out var secondDelivery) &&
                    secondDelivery.Path?.Equals(delivery.Path) == true &&
                    !ReferenceEquals(secondDelivery.Path, delivery.Path),
                "Repeated actor deliveries shared a mutable path object instead of independent fences.");
            Require(native.CursorCalls == 0,
                "A transport receipt advanced the durable application cursor.");

            var rejectedRelay = new FakeNative
            {
                IngressResult = ProductionDirectNativeResultV5.QueueFull
            };
            await runtime.RegisterPeerAsync(Registration(peer, 5, rejectedRelay, pathEpoch: 3)).ConfigureAwait(false);
            Require(await runtime.ArbitrateIngressAsync(
                    peer, ProductionDirectIngressSourceV5.Relay, exact).ConfigureAwait(false) &&
                    delivered.TryDequeue(out var fallback) && fallback.SequenceEqual(exact) &&
                    deliveries.TryDequeue(out var actorFallback) &&
                    actorFallback.Path?.Equals(Fence(3)) == true,
                "Relay ingress was lost or detached from its actor path when native arbitration rejected it.");

            var directOnly = new FakeNative { IngressResult = ProductionDirectNativeResultV5.QueueFull };
            await runtime.RegisterPeerAsync(Registration(peer, 6, directOnly, pathEpoch: 4)).ConfigureAwait(false);
            Require(!await runtime.ArbitrateIngressAsync(
                    peer, ProductionDirectIngressSourceV5.Direct, exact).ConfigureAwait(false) &&
                    delivered.IsEmpty,
                "Rejected Direct ingress bypassed Relay arbitration into the business handler.");

            var actorlessPeer = Guid.NewGuid();
            Require(await runtime.ArbitrateIngressAsync(
                    actorlessPeer, ProductionDirectIngressSourceV5.Relay, exact).ConfigureAwait(false) &&
                    deliveries.TryDequeue(out var publicFallback) && publicFallback.Path == null,
                "The public Relay fallback falsely claimed a Direct actor path.");
        }

        private static async Task ValidateSingleReaderSignalGatesAndShadowDispatchAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = Fence(2).ConnectionId;
            var signaling = new FakeSignaling();
            var productionSignals = new ConcurrentQueue<TransportSignalTypeV1>();
            var shadowSignals = new ConcurrentQueue<TransportSignalTypeV1>();
            var options = Options(
                handleSignal: (_, signal, _, _) =>
                {
                    productionSignals.Enqueue(signal.SignalType);
                    return Task.CompletedTask;
                },
                handleShadow: (signal, _) =>
                {
                    shadowSignals.Enqueue(signal.SignalType);
                    return Task.CompletedTask;
                });
            await using var runtime = new ProductionDirectDataRuntime(local, signaling, options);
            runtime.ObserveSignalingState(true, 6, true);
            Require(runtime.Start() && !runtime.Start() && signaling.ReaderCount == 1,
                "The coordinator did not enforce one signaling reader.");

            var discoveryPeer = Guid.NewGuid();
            signaling.Enqueue(Signal(discoveryPeer, local, connection,
                TransportSignalTypeV1.NominationV2, 1));
            await WaitUntilAsync(() => shadowSignals.Contains(TransportSignalTypeV1.NominationV2))
                .ConfigureAwait(false);

            await runtime.RegisterPeerAsync(Registration(peer, 6, new FakeNative())).ConfigureAwait(false);
            signaling.Enqueue(Signal(peer, local, Fence(2).ConnectionId,
                TransportSignalTypeV1.CutoverProposal, 1));
            await WaitUntilAsync(() => productionSignals.Contains(
                TransportSignalTypeV1.CutoverProposal)).ConfigureAwait(false);
            runtime.ObserveSignalingState(false, 7, false);
            signaling.Enqueue(Signal(peer, local, Fence(2).ConnectionId,
                TransportSignalTypeV1.CutoverAccept, 2));
            signaling.Enqueue(Signal(peer, local, Fence(2).ConnectionId,
                TransportSignalTypeV1.PathClose, 2));
            await WaitUntilAsync(() => productionSignals.Contains(TransportSignalTypeV1.PathClose))
                .ConfigureAwait(false);
            Require(!productionSignals.Contains(TransportSignalTypeV1.CutoverAccept),
                "A data-gated 6-9 signal crossed an unavailable generation.");
            await WaitUntilAsync(() => runtime.Snapshot().All(value => value.PeerDeviceId != peer))
                .ConfigureAwait(false);

            var survivingPeer = Guid.NewGuid();
            await runtime.RegisterPeerAsync(Registration(survivingPeer, 7, new FakeNative()))
                .ConfigureAwait(false);
            signaling.Fail(new InvalidOperationException("signal lane failed"));
            await Task.Delay(50).ConfigureAwait(false);
            Require(runtime.Snapshot().Any(value => value.PeerDeviceId == survivingPeer),
                "A signaling reader failure retired a healthy Direct peer.");
        }

        private static async Task ValidatePromotionNominationAndSignalCursorHandoffAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var native = new FakeNative();
            var binding = Enumerable.Repeat((byte)0x3c, 32).ToArray();
            var nomination = PromotionNomination();
            native.CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                new ProductionDirectPathInfoV5
                {
                    Path = Fence(2),
                    PathKind = ProductionDirectPathKindV5.Lan,
                    Ready = true,
                    DirectPathBinding = binding.ToArray()
                });
            byte[] completedBinding = null;
            string failure = null;
            var v4 = new FakePromotableV4();
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                signaling,
                Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 7, true);
            Require(runtime.Start(), "The promotion test did not start its single signal reader.");
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                peer,
                Fence(2).ConnectionId,
                3,
                7,
                5,
                4,
                3,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                v4,
                nomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                value => completedBinding = value.ToArray(),
                value => failure = value))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "A valid V4 nomination was not promoted into one V5 actor.");
            }
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var sent = signaling.Sent.ToArray();
            Require(native.PromoteCalls == 1 && v4.PromotionCalls == 1 &&
                    sent[0].SignalType == TransportSignalTypeV1.NominationAckV2 &&
                    sent[0].SignalSeq == 6 && sent[0].SignalAck == 4 &&
                    sent[1].SignalType == TransportSignalTypeV1.NominationResultV2 &&
                    sent[1].SignalSeq == 7 && sent[1].SignalAck == 4,
                "Promotion did not continue the V4 signal cursors or emit ACK/result once.");

            var peerResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                nomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    peer,
                    local,
                    Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2,
                    5,
                    7,
                    peerResult));
            }
            finally { CryptographicOperations.ZeroMemory(peerResult); }
            await WaitUntilAsync(() => completedBinding != null).ConfigureAwait(false);
            Require(completedBinding.SequenceEqual(binding) && string.IsNullOrEmpty(failure),
                "Matching peer nomination result did not complete the promoted path.");
            CryptographicOperations.ZeroMemory(completedBinding);
        }

        private static async Task ValidatePromotionFailureIsOneShotAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var native = new FakeNative { PromoteResult = ProductionDirectNativeResultV5.Protocol };
            var v4 = new FakePromotableV4();
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 2, true);
            Require(runtime.Start(), "The failed promotion test did not start.");
            using var handoff = new ProductionDirectDataPromotionHandoff(
                peer,
                Fence(2).ConnectionId,
                1,
                2,
                0,
                0,
                0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                v4,
                PromotionNomination(),
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray());
            Require(!await runtime.PromotePeerAsync(handoff).ConfigureAwait(false) &&
                    !await runtime.PromotePeerAsync(handoff).ConfigureAwait(false) &&
                    native.PromoteCalls == 2 && v4.PromotionCalls == 2 && runtime.Snapshot().Count == 0,
                "A failed promotion installed a peer or reused the V4 owner successfully.");
        }

        private static async Task ValidatePromotionFencedByNetworkChangeAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            using var promoteGate = new ManualResetEventSlim(false);
            var native = new FakeNative { PromoteGate = promoteGate };
            var v4 = new FakePromotableV4();
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                new FakeSignaling(),
                Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 8, true);
            Require(runtime.Start(), "The network-fenced promotion test did not start.");
            using var handoff = new ProductionDirectDataPromotionHandoff(
                peer,
                Fence(2).ConnectionId,
                1,
                8,
                0,
                0,
                0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                v4,
                PromotionNomination(),
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray());

            var promotion = runtime.PromotePeerAsync(handoff);
            Require(native.PromoteEntered.Wait(TimeSpan.FromSeconds(2)),
                "The promotion did not enter the native ownership seam.");
            runtime.NotifyNetworkChanged();
            promoteGate.Set();
            Require(!await promotion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) &&
                    runtime.Snapshot().Count == 0 && native.DisposeCalls == 1,
                "A network change raced past promotion and installed a stale V5 actor.");
        }

        private static async Task ValidateBidirectionalCutoverAndFallbackAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var native = new FakeNative { EmitRelayCarrierOnReceiveFallback = true };
            var binding = Enumerable.Repeat((byte)0x3c, 32).ToArray();
            var nomination = PromotionNomination();
            native.CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                new ProductionDirectPathInfoV5
                {
                    Path = Fence(2), PathKind = ProductionDirectPathKindV5.Nat, Ready = true,
                    DirectPathBinding = binding.ToArray()
                });
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                signaling,
                Options(createNative: _ => native));
            runtime.ObserveSignalingState(true, 9, true);
            Require(runtime.Start(), "The cutover test did not start.");
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                peer, Fence(2).ConnectionId, 4, 9, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), nomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                _ => { },
                _ => { }))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The cutover peer was not promoted.");
            }
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var peerResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                nomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2, 1, 2, peerResult));
            }
            finally { CryptographicOperations.ZeroMemory(peerResult); }
            await Task.Delay(50).ConfigureAwait(false);

            var localProposal = ProductionDirectCutoverCodec.CreateProposal(
                Fence(2).ConnectionId,
                2,
                Direction.PcToAndroid,
                Guid.NewGuid(),
                binding,
                new[] { new ProductionDirectSessionCutoverBarrier
                    { SessionId = session, FirstDirectSequence = 10 } },
                500);
            Require(await runtime.StartCutoverAsync(peer, localProposal).ConfigureAwait(false) &&
                    native.StartProposalCalls == 1 &&
                    runtime.Snapshot().Single().ActiveCutoverCount == 1,
                "Windows did not start its PC-to-Android cutover.");
            var accept = ProductionDirectCutoverSignalCodec.CreateAccept(
                localProposal.BarrierId, localProposal.BarrierSha256, true, string.Empty);
            try
            {
                signaling.Enqueue(EncryptedSignal(peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.CutoverAccept, 2, 3, accept));
            }
            finally { CryptographicOperations.ZeroMemory(accept); }
            await WaitUntilAsync(() => native.ReceiveAcceptCalls == 1).ConfigureAwait(false);
            Require(native.LastAccept?.Direction == ProductionDirectDirectionV5.PcToAndroid,
                "A valid PC-to-Android accept was not delivered to native.");
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Commit,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                BarrierId = localProposal.BarrierId,
                BarrierSha256 = localProposal.BarrierSha256.ToArray(), IsEcho = false
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, session, 18).ConfigureAwait(false);
            await WaitUntilAsync(() => signaling.Sent.Any(value =>
                value.SignalType == TransportSignalTypeV1.CutoverCommit)).ConfigureAwait(false);
            Require(native.CommitEchoCalls == 0,
                "Sending the initial PC-to-Android commit incorrectly acknowledged a commit echo.");

            var remoteProposal = ProductionDirectCutoverCodec.CreateProposal(
                Fence(2).ConnectionId,
                2,
                Direction.AndroidToPc,
                Guid.NewGuid(),
                binding,
                new[] { new ProductionDirectSessionCutoverBarrier
                    { SessionId = session, FirstDirectSequence = 20 } },
                500);
            var proposalPlaintext = ProductionDirectCutoverSignalCodec.CreateProposal(remoteProposal);
            try
            {
                signaling.Enqueue(EncryptedSignal(peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.CutoverProposal, 3, 4, proposalPlaintext));
            }
            finally { CryptographicOperations.ZeroMemory(proposalPlaintext); }
            await WaitUntilAsync(() => native.ReceiveProposalCalls == 1).ConfigureAwait(false);
            Require(runtime.Snapshot().Single().ActiveCutoverCount == 2,
                "The two independent cutover directions did not retain separate barriers.");
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Accept,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                BarrierId = remoteProposal.BarrierId,
                BarrierSha256 = remoteProposal.BarrierSha256.ToArray(), Accepted = true
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, session, 19).ConfigureAwait(false);
            await WaitUntilAsync(() => signaling.Sent.Any(value =>
                value.SignalType == TransportSignalTypeV1.CutoverAccept)).ConfigureAwait(false);
            var remoteCommit = ProductionDirectCutoverSignalCodec.CreateCommit(
                remoteProposal.BarrierId, remoteProposal.BarrierSha256);
            try
            {
                signaling.Enqueue(EncryptedSignal(peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.CutoverCommit, 4, 5, remoteCommit));
            }
            finally { CryptographicOperations.ZeroMemory(remoteCommit); }
            await WaitUntilAsync(() => native.ReceiveCommitCalls == 1).ConfigureAwait(false);
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Commit,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                BarrierId = remoteProposal.BarrierId,
                BarrierSha256 = remoteProposal.BarrierSha256.ToArray(), IsEcho = true
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, session, 20).ConfigureAwait(false);
            await WaitUntilAsync(() => native.CommitEchoCalls == 1).ConfigureAwait(false);
            Require(native.LastCommit?.Direction == ProductionDirectDirectionV5.AndroidToPc &&
                    native.LastCommitEcho?.Direction == ProductionDirectDirectionV5.AndroidToPc,
                "Android-to-PC commit/echo did not stay on its restored direction.");

            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                Carrier = ProductionDirectCarrierV5.NatDirect
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, session, 21).ConfigureAwait(false);
            Require(runtime.Snapshot().Single() is var pcCarrier &&
                    pcCarrier.PcToAndroidCarrier == ProductionDirectCarrierV5.NatDirect &&
                    pcCarrier.ActiveCutoverCount == 1,
                "PC-to-Android Direct carrier did not immediately release only its barrier.");

            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2), Kind = ProductionDirectRouteActionKindV5.Carrier,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                Carrier = ProductionDirectCarrierV5.LanDirect
            });
            await runtime.UpdateAppliedCursorAsync(
                peer, ProductionDirectDirectionV5.AndroidToPc, session, 22).ConfigureAwait(false);
            Require(runtime.Snapshot().Single() is var androidCarrier &&
                    androidCarrier.AndroidToPcCarrier == ProductionDirectCarrierV5.LanDirect &&
                    androidCarrier.ActiveCutoverCount == 0,
                "Android-to-PC Direct carrier did not immediately release its barrier.");

            var sentBeforeLocalFallback = signaling.Sent.Count;
            native.Actions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.Fallback,
                Direction = ProductionDirectDirectionV5.AndroidToPc,
                BarrierId = remoteProposal.BarrierId,
                BarrierSha256 = remoteProposal.BarrierSha256.ToArray(),
                Carrier = ProductionDirectCarrierV5.Relay,
                Reason = ProductionDirectRouteReasonV5.DirectFailure,
                DirectFrameSequence = 20
            });
            Require(await runtime.UpdateAppliedCursorAsync(
                        peer,
                        ProductionDirectDirectionV5.AndroidToPc,
                        session,
                        23).ConfigureAwait(false) &&
                    signaling.Sent.Count == sentBeforeLocalFallback + 1 &&
                    signaling.Sent.Last().SignalType == TransportSignalTypeV1.Fallback &&
                    runtime.Snapshot().Single() is var locallyRecovered &&
                    locallyRecovered.PcToAndroidCarrier == ProductionDirectCarrierV5.NatDirect &&
                    locallyRecovered.AndroidToPcCarrier == ProductionDirectCarrierV5.Relay,
                "A local native fallback lost committed authority or changed the reverse Direct direction.");

            var fallback = ProductionDirectCutoverSignalCodec.CreateFallback(
                localProposal.BarrierId, "DIRECT_FAILURE", 0);
            runtime.ObserveSignalingState(true, 10, false);
            try
            {
                signaling.Enqueue(EncryptedSignal(peer, local, Fence(2).ConnectionId,
                    TransportSignalTypeV1.Fallback, 5, 7, fallback));
            }
            finally { CryptographicOperations.ZeroMemory(fallback); }
            await WaitUntilAsync(() => native.ReceiveFallbackCalls == 1).ConfigureAwait(false);
            Require(native.LastFallback?.Direction == ProductionDirectDirectionV5.PcToAndroid &&
                    runtime.Snapshot().Single() is var recovered &&
                    recovered.PcToAndroidCarrier == ProductionDirectCarrierV5.Relay &&
                    recovered.AndroidToPcCarrier == ProductionDirectCarrierV5.Relay &&
                    recovered.ActiveCutoverCount == 0,
                "Committed PC-to-Android fallback lost its compact direction authority or affected the other direction.");
        }

        private static async Task ValidateDisposeFailureStillClearsAndIsolatesAsync()
        {
            var local = Guid.NewGuid();
            var failingPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var relay = new ConcurrentQueue<byte[]>();
            var states = new ConcurrentQueue<ProductionDirectDataPeerSnapshot>();
            var binding = Enumerable.Repeat((byte)0x4d, 32).ToArray();
            var nomination = PromotionNomination();
            var failingNative = new FakeNative
            {
                CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                    new ProductionDirectPathInfoV5
                    {
                        Path = Fence(2),
                        PathKind = ProductionDirectPathKindV5.Lan,
                        Ready = true,
                        DirectPathBinding = binding.ToArray()
                    })
            };
            var healthyNative = new FakeNative();
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                signaling,
                Options(
                    relay: relay,
                    createNative: _ => failingNative,
                    peerStates: states));
            runtime.ObserveSignalingState(true, 12, true);
            Require(runtime.Start(), "The throwing-dispose test did not start.");

            var nominationCompleted = false;
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                failingPeer, Fence(2).ConnectionId, 1, 12, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), nomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                _ => nominationCompleted = true,
                _ => { }))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The throwing-dispose peer could not be promoted.");
            }
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var peerResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                nomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    failingPeer,
                    local,
                    Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2,
                    1,
                    2,
                    peerResult));
            }
            finally { CryptographicOperations.ZeroMemory(peerResult); }
            await WaitUntilAsync(() => nominationCompleted).ConfigureAwait(false);

            var proposal = ProductionDirectCutoverCodec.CreateProposal(
                Fence(2).ConnectionId,
                2,
                Direction.PcToAndroid,
                Guid.NewGuid(),
                binding,
                new[]
                {
                    new ProductionDirectSessionCutoverBarrier
                        { SessionId = session, FirstDirectSequence = 1 }
                },
                500);
            Require(await runtime.StartCutoverAsync(failingPeer, proposal).ConfigureAwait(false) &&
                    runtime.Snapshot().Single().ActiveCutoverCount == 1,
                "The throwing-dispose fixture did not retain an active cutover secret.");
            Require(await runtime.RegisterPeerAsync(
                    Registration(healthyPeer, 12, healthyNative)).ConfigureAwait(false),
                "The healthy isolation peer was not registered.");

            failingNative.DisposeError = new InvalidOperationException("dispose failed");
            Require(await runtime.RetirePeerAsync(
                        failingPeer, Fence(2), "TEST_DISPOSE_FAILURE").ConfigureAwait(false) &&
                    failingNative.DisposeCalls == 1 &&
                    runtime.Snapshot().Count == 1 &&
                    runtime.Snapshot().Single().PeerDeviceId == healthyPeer &&
                    states.Any(value => value.PeerDeviceId == failingPeer && value.Retired &&
                        value.ActiveCutoverCount == 0 && value.SensitiveStateCleared &&
                        value.OwnedCommandDataCleared),
                "A throwing native Dispose interrupted secret cleanup or removed another peer.");

            var failedExact = OutputEnvelope(local, failingPeer, session, 1);
            var healthyExact = OutputEnvelope(local, healthyPeer, session, 1);
            Require(await runtime.RouteEnvelopeAsync(
                        failingPeer, session, 1, failedExact, true).ConfigureAwait(false) ==
                    ProductionDirectDataRouteOutcome.RelaySent &&
                    relay.TryDequeue(out var relayed) && relayed.SequenceEqual(failedExact) &&
                    await runtime.RouteEnvelopeAsync(
                        healthyPeer, session, 1, healthyExact, true).ConfigureAwait(false) ==
                    ProductionDirectDataRouteOutcome.DirectAccepted,
                "A throwing native Dispose damaged Relay fallback or a healthy peer actor.");
            CryptographicOperations.ZeroMemory(proposal.DirectPathBinding);
            CryptographicOperations.ZeroMemory(proposal.BarrierSha256);
            CryptographicOperations.ZeroMemory(proposal.WireBytes);
        }

        private static async Task ValidateNominationCallbackFailuresAreContainedAsync()
        {
            var local = Guid.NewGuid();
            var completedPeer = Guid.NewGuid();
            var failedPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var signaling = new FakeSignaling();
            var states = new ConcurrentQueue<ProductionDirectDataPeerSnapshot>();
            var binding = Enumerable.Repeat((byte)0x6e, 32).ToArray();
            var completedNative = new FakeNative
            {
                CurrentPathInfo = (ProductionDirectNativeResultV5.Accepted,
                    new ProductionDirectPathInfoV5
                    {
                        Path = Fence(2),
                        PathKind = ProductionDirectPathKindV5.Nat,
                        Ready = true,
                        DirectPathBinding = binding.ToArray()
                    })
            };
            var failedNative = new FakeNative();
            var natives = new ConcurrentQueue<FakeNative>(new[] { completedNative, failedNative });
            await using var runtime = new ProductionDirectDataRuntime(
                local,
                signaling,
                Options(
                    createNative: _ => natives.TryDequeue(out var value)
                        ? value
                        : throw new InvalidOperationException("Unexpected native allocation."),
                    peerStates: states));
            runtime.ObserveSignalingState(true, 13, true);
            Require(runtime.Start(), "The nomination callback test did not start.");
            await runtime.RegisterPeerAsync(Registration(healthyPeer, 13, new FakeNative()))
                .ConfigureAwait(false);

            byte[] callbackBinding = null;
            var completedCalls = 0;
            var unexpectedFailureCalls = 0;
            var completedNomination = PromotionNomination();
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                completedPeer, Fence(2).ConnectionId, 1, 13, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), completedNomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                value =>
                {
                    callbackBinding = value;
                    Interlocked.Increment(ref completedCalls);
                    throw new InvalidOperationException("completed callback failed");
                },
                _ => Interlocked.Increment(ref unexpectedFailureCalls)))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The completed-callback peer could not be promoted.");
            }
            Require(completedNative.PromotionSecretsCleared,
                "PromotePeer retained its temporary nomination secrets.");
            await WaitUntilAsync(() => signaling.Sent.Count >= 2).ConfigureAwait(false);
            var completedResult = ProductionDirectCutoverSignalCodec.CreateNominationResult(
                completedNomination, binding);
            try
            {
                signaling.Enqueue(EncryptedSignal(
                    completedPeer,
                    local,
                    Fence(2).ConnectionId,
                    TransportSignalTypeV1.NominationResultV2,
                    1,
                    2,
                    completedResult));
            }
            finally { CryptographicOperations.ZeroMemory(completedResult); }
            await WaitUntilAsync(() => runtime.Snapshot().All(value =>
                value.PeerDeviceId != completedPeer)).ConfigureAwait(false);
            Require(completedCalls == 1 && unexpectedFailureCalls == 0 &&
                    callbackBinding?.All(value => value == 0) == true &&
                    states.Any(value => value.PeerDeviceId == completedPeer && value.Retired &&
                        value.SensitiveStateCleared),
                "A throwing nomination-completed callback leaked its owned binding or escaped cleanup.");

            var failedCalls = 0;
            var unexpectedCompletedCalls = 0;
            var failedNomination = PromotionNomination();
            using (var handoff = new ProductionDirectDataPromotionHandoff(
                failedPeer, Fence(2).ConnectionId, 1, 13, 0, 0, 0,
                Enumerable.Repeat((byte)0x2a, 32).ToArray(),
                ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
                new FakePromotableV4(), failedNomination,
                _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
                _ => Interlocked.Increment(ref unexpectedCompletedCalls),
                _ =>
                {
                    Interlocked.Increment(ref failedCalls);
                    throw new InvalidOperationException("failed callback failed");
                }))
            {
                Require(await runtime.PromotePeerAsync(handoff).ConfigureAwait(false),
                    "The failed-callback peer could not be promoted.");
            }
            Require(failedNative.PromotionSecretsCleared,
                "The failed-callback promotion retained temporary nomination secrets.");
            await WaitUntilAsync(() => signaling.Sent.Count >= 3).ConfigureAwait(false);
            Require(await runtime.RetirePeerAsync(
                        failedPeer, Fence(2), "TEST_NOMINATION_FAILURE").ConfigureAwait(false) &&
                    failedCalls == 1 && unexpectedCompletedCalls == 0 &&
                    states.Any(value => value.PeerDeviceId == failedPeer && value.Retired &&
                        value.SensitiveStateCleared) &&
                    runtime.Snapshot().Single().PeerDeviceId == healthyPeer &&
                    await runtime.RouteEnvelopeAsync(
                        healthyPeer,
                        session,
                        1,
                        OutputEnvelope(local, healthyPeer, session, 1),
                        true).ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                "A throwing nomination-failed callback escaped cleanup or damaged another peer.");
        }

        private static async Task ValidateWrongSignalKeyRetiresOnlyItsPeerAsync()
        {
            var local = Guid.NewGuid();
            var rejectedPeer = Guid.NewGuid();
            var healthyPeer = Guid.NewGuid();
            var signaling = new FakeSignaling();
            await using var runtime = new ProductionDirectDataRuntime(local, signaling, Options());
            runtime.ObserveSignalingState(true, 5, true);
            await runtime.RegisterPeerAsync(Registration(rejectedPeer, 5, new FakeNative()))
                .ConfigureAwait(false);
            await runtime.RegisterPeerAsync(Registration(healthyPeer, 5, new FakeNative()))
                .ConfigureAwait(false);
            Require(runtime.Start(), "The key-fence test did not start.");
            var wrong = Signal(rejectedPeer, local, Fence(2).ConnectionId,
                TransportSignalTypeV1.CutoverProposal, 1);
            wrong.KeyId = ByteString.CopyFromUtf8("wrong-device-key");
            signaling.Enqueue(wrong);
            await WaitUntilAsync(() => runtime.Snapshot().All(value =>
                value.PeerDeviceId != rejectedPeer)).ConfigureAwait(false);
            Require(runtime.Snapshot().Any(value => value.PeerDeviceId == healthyPeer),
                "A wrong signal key escaped its peer fence or retired a healthy peer.");
        }

        private static async Task ValidateSignRequestIsNarrowAndZeroedAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            var transcript = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            native.SigningPayloads.Enqueue(new ProductionDirectSigningPayloadV5
            {
                Status = ProductionDirectNativeResultV5.Accepted,
                RequestId = 17,
                Payload = transcript,
                RequiredBytes = checked((uint)transcript.Length)
            });
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.SignRequest,
                Status = ProductionDirectNativeResultV5.Accepted,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId,
                FrameSequence = 17,
                PayloadBytes = checked((uint)transcript.Length)
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);
            var exact = OutputEnvelope(local, peer, session, 1);
            Require(await runtime.RouteEnvelopeAsync(peer, session, 1, exact, true)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted &&
                    native.SubmittedRequestId == 17 && native.SubmittedSignatureLength == 64 &&
                    transcript.All(value => value == 0),
                "A native sign request escaped request-id fencing or retained its transcript.");
        }

        private static async Task ValidateTicketRenewalSlidesTheProofGenerationAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            var originalTicket = Enumerable.Repeat((byte)0x11, 200).ToArray();
            var renewedTicket = Enumerable.Repeat((byte)0x22, 200).ToArray();
            var presentedProofs = new ConcurrentQueue<byte[]>();
            var renewCalls = 0;
            native.RenewApplies.Enqueue(ProductionDirectNativeResultV5.RelayFallback);
            native.RenewApplies.Enqueue(ProductionDirectNativeResultV5.Accepted);
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.RenewalDue,
                Status = ProductionDirectNativeResultV5.Accepted,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options(renewTicket: (ticketPeer, connection, proof, _) =>
                {
                    if (ticketPeer != peer || connection != Fence(2).ConnectionId)
                        throw new InvalidOperationException("renewal identity mismatch");
                    presentedProofs.Enqueue(proof.ToArray());
                    return Task.FromResult(Interlocked.Increment(ref renewCalls) == 1
                        ? null
                        : renewedTicket.ToArray());
                }));
            await runtime.RegisterPeerAsync(
                Registration(peer, 2, native, ticketWireBytes: originalTicket))
                .ConfigureAwait(false);
            {
                var exact = OutputEnvelope(local, peer, session, 1);
                Require(await runtime.RouteEnvelopeAsync(peer, session, 1, exact, true)
                        .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted,
                    "Renewal wiring changed the direct routing path.");
                var applicationDeadline = DateTime.UtcNow.AddSeconds(15);
                while (presentedProofs.Count < 3 && DateTime.UtcNow < applicationDeadline)
                    await Task.Delay(50).ConfigureAwait(false);
                Require(native.RenewApplyCalls == 2 &&
                        native.RenewApplies.IsEmpty &&
                        presentedProofs.Count == 3 &&
                        presentedProofs.All(proof => proof.SequenceEqual(originalTicket)) &&
                        native.LastRenewTicket.SequenceEqual(renewedTicket),
                    "Ticket renewal did not slide the proof generation after one refused fetch " +
                    "and one refused apply.");
            }
        }

        private static async Task ValidateRenewalDueWithoutARenewalDelegateIsInertAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var native = new FakeNative();
            native.Events.Enqueue(new ProductionDirectNativeEventV5
            {
                Kind = ProductionDirectNativeEventKindV5.RenewalDue,
                Status = ProductionDirectNativeResultV5.Accepted,
                PathEpoch = 2,
                AttemptId = Fence(2).AttemptId
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options());
            await runtime.RegisterPeerAsync(Registration(peer, 2, native)).ConfigureAwait(false);
            var exact = OutputEnvelope(local, peer, session, 1);
            Require(await runtime.RouteEnvelopeAsync(peer, session, 1, exact, true)
                    .ConfigureAwait(false) == ProductionDirectDataRouteOutcome.DirectAccepted &&
                    native.RenewApplyCalls == 0,
                "A runtime without a renewal delegate still attempted a renewal.");
        }

        private static async Task ValidateRetirementDrainsOnlyFencedRecoveryAsync()
        {
            var local = Guid.NewGuid();
            var goodPeer = Guid.NewGuid();
            var stalePeer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var relay = new ConcurrentQueue<byte[]>();
            var goodExact = OutputEnvelope(local, goodPeer, session, 40);
            var staleExact = OutputEnvelope(local, stalePeer, session, 41);
            var good = new FakeNative();
            good.CancelActions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(2),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 40,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)goodExact.Length),
                Payload = goodExact.ToArray()
            });
            var stale = new FakeNative();
            stale.CancelActions.Enqueue(new ProductionDirectRouteActionV5
            {
                Path = Fence(3),
                Kind = ProductionDirectRouteActionKindV5.RelayEnvelope,
                Direction = ProductionDirectDirectionV5.PcToAndroid,
                SessionId = session,
                EnvelopeSequence = 41,
                Carrier = ProductionDirectCarrierV5.Relay,
                PayloadBytes = checked((uint)staleExact.Length),
                Payload = staleExact.ToArray()
            });
            await using var runtime = new ProductionDirectDataRuntime(
                local, new FakeSignaling(), Options(relay: relay));
            await runtime.RegisterPeerAsync(Registration(goodPeer, 2, good)).ConfigureAwait(false);
            await runtime.RegisterPeerAsync(Registration(stalePeer, 2, stale)).ConfigureAwait(false);
            Require(await runtime.RetirePeerAsync(goodPeer, "TEST_RETIRE").ConfigureAwait(false) &&
                    await runtime.RetirePeerAsync(stalePeer, "TEST_RETIRE").ConfigureAwait(false) &&
                    relay.TryDequeue(out var recovered) && recovered.SequenceEqual(goodExact) &&
                    relay.IsEmpty,
                "Retirement did not drain exact recovery actions or accepted a stale path fence.");
        }

        private static ProductionDirectDataRuntimeOptions Options(
            ConcurrentQueue<byte[]> relay = null,
            ConcurrentQueue<byte[]> delivered = null,
            ConcurrentQueue<ProductionDirectIngressSourceV5> deliveredSources = null,
            ConcurrentQueue<ProductionDirectDataIngressDelivery> deliveries = null,
            long mailboxBytes = 8L * 1024 * 1024,
            bool relayImmediate = true,
            Func<Guid, TransportSignalEnvelopeV1, byte[], CancellationToken, Task> handleSignal = null,
            Func<TransportSignalEnvelopeV1, CancellationToken, Task> handleShadow = null,
            Func<ProductionDirectPathFenceV5, IProductionDirectDataNativePeer> createNative = null,
            ConcurrentQueue<ProductionDirectDataPeerSnapshot> peerStates = null,
            Func<ProductionDirectDataRelayEnvelope, bool> enqueueRelay = null,
            Action<ProductionDirectDataIngressDelivery> enqueueIngress = null,
            ConcurrentQueue<ProductionDirectDataNativeAction> nativeActions = null,
            Func<Guid, Guid, byte[], CancellationToken, Task<byte[]>> renewTicket = null) => new()
        {
            PeerMailboxCapacity = 4,
            PeerMailboxCapacityBytes = mailboxBytes,
            ClockMilliseconds = () => 1000,
            EnqueueRelayEnvelope = enqueueRelay ?? (value =>
                {
                    relay?.Enqueue(value.ExactEnvelope);
                    return relayImmediate;
                }),
            EnqueueIngress = enqueueIngress ?? (value =>
                {
                    delivered?.Enqueue(value.ExactEnvelope);
                    deliveredSources?.Enqueue(value.Source);
                    deliveries?.Enqueue(value);
                }),
            ParseIngressMetadata = bytes =>
            {
                var envelope = Envelope.Parser.ParseFrom(bytes);
                return new ProductionDirectDataIngressMetadata
                {
                    SessionId = Guid.ParseExact(Convert.ToHexString(envelope.SessionId.Span), "N"),
                    EnvelopeSequence = envelope.Seq
                };
            },
            HandleSignalAsync = handleSignal,
            HandlePrePromotionSignalAsync = handleShadow,
            CreateNativePeer = createNative,
            EnqueueNativeAction = nativeActions == null
                ? null
                : value => nativeActions.Enqueue(value),
            PeerStateChanged = value => peerStates?.Enqueue(value),
            RenewTicketAsync = renewTicket
        };

        private static ProductionDirectDataPeerRegistration Registration(
            Guid peer,
            long generation,
            FakeNative native,
            ulong pathEpoch = 2,
            ProductionDirectPathFenceV5 path = null,
            byte[] ticketWireBytes = null) => new()
        {
            PeerDeviceId = peer,
            Path = path ?? Fence(pathEpoch),
            KeyEpoch = 1,
            AdmissionGeneration = generation,
            PairwiseKey = Enumerable.Repeat((byte)0x2a, 32).ToArray(),
            SignalKeyId = ByteString.CopyFromUtf8("device-key-1").ToByteArray(),
            TicketWireBytes = ticketWireBytes,
            SignNativeTranscript = _ => Enumerable.Repeat((byte)0x5a, 64).ToArray(),
            Native = native
        };

        private static ProductionDirectPathFenceV5 Fence(ulong epoch) => new()
        {
            PathEpoch = epoch,
            ConnectionId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            AttemptId = Guid.Parse("11111111-2222-4333-8444-555555555555")
        };

        private static byte[] OutputEnvelope(
            Guid local,
            Guid peer,
            Guid session,
            ulong sequence,
            MessageType messageType = MessageType.OutputChunk) => new Envelope
        {
            ProtocolVersion = 1,
            MessageType = messageType,
            SessionId = ByteString.CopyFrom(GuidBytes(session)),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
            Direction = Direction.PcToAndroid,
            Seq = sequence,
            Ciphertext = ByteString.CopyFrom(new byte[16])
        }.ToByteArray();

        private static byte[] InputEnvelope(
            Guid peer, Guid local, Guid session, ulong sequence) => new Envelope
        {
            ProtocolVersion = 1,
            MessageType = MessageType.InputEvent,
            SessionId = ByteString.CopyFrom(GuidBytes(session)),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(local)),
            Direction = Direction.AndroidToPc,
            Seq = sequence,
            Ciphertext = ByteString.CopyFrom(new byte[16])
        }.ToByteArray();

        private static ProductionDirectCommitRequestV4 PromotionNomination()
        {
            var initiator = new ProductionDirectCandidateV4
            {
                CandidateId = Guid.Parse("11111111-aaaa-4bbb-8ccc-111111111111"),
                Endpoint = new ProductionDirectNativeEndpoint(
                    System.Net.IPAddress.Parse("192.168.1.20"), 43000),
                Kind = ProductionDirectCandidateKindV4.Host,
                Priority = 100,
                NetworkCost = 10,
                ExpiresAtMilliseconds = 50_000
            };
            var responder = new ProductionDirectCandidateV4
            {
                CandidateId = Guid.Parse("22222222-aaaa-4bbb-8ccc-222222222222"),
                Endpoint = new ProductionDirectNativeEndpoint(
                    System.Net.IPAddress.Parse("192.168.1.21"), 43001),
                Kind = ProductionDirectCandidateKindV4.Host,
                Priority = 99,
                NetworkCost = 10,
                ExpiresAtMilliseconds = 50_000
            };
            return new ProductionDirectCommitRequestV4
            {
                Epoch = 2,
                AttemptId = Fence(2).AttemptId,
                CheckRoundId = Guid.Parse("33333333-aaaa-4bbb-8ccc-333333333333"),
                NominationId = Guid.Parse("44444444-aaaa-4bbb-8ccc-444444444444"),
                WinningPair = new ProductionDirectCandidatePairV4
                {
                    Rank = 1, Initiator = initiator, Responder = responder
                },
                Role = ProductionDirectNativeRole.Responder,
                TimeoutMilliseconds = 10_000,
                PeerCertificateSha256 = Enumerable.Repeat((byte)0x11, 32).ToArray(),
                PeerDeviceSigningPublicKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
                LocalProofNonce = Enumerable.Repeat((byte)0x33, 32).ToArray(),
                NowMilliseconds = 1_000
            };
        }

        private static TransportSignalEnvelopeV1 EncryptedSignal(
            Guid sender,
            Guid recipient,
            Guid connection,
            TransportSignalTypeV1 type,
            ulong sequence,
            ulong acknowledgement,
            byte[] plaintext)
        {
            var metadata = Signal(sender, recipient, connection, type, sequence);
            metadata.SignalAck = acknowledgement;
            metadata.SentAtMs = 1000;
            metadata.Nonce = ByteString.Empty;
            metadata.Ciphertext = ByteString.Empty;
            return ProductionDirectTransportCrypto.EncryptSignal(
                Enumerable.Repeat((byte)0x2a, 32).ToArray(), metadata, plaintext);
        }

        private static TransportSignalEnvelopeV1 Signal(
            Guid sender,
            Guid recipient,
            Guid connection,
            TransportSignalTypeV1 type,
            ulong sequence) => new()
        {
            ProtocolVersion = 1,
            SignalType = type,
            ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(recipient)),
            PathEpoch = 2,
            SignalSeq = sequence,
            SignalAck = 0,
            KeyId = ByteString.CopyFromUtf8("device-key-1"),
            Nonce = ByteString.CopyFrom(new byte[12]),
            Ciphertext = ByteString.CopyFrom(new byte[16])
        };

        private static byte[] GuidBytes(Guid value) =>
            Convert.FromHexString(value.ToString("N"));

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Direct data runtime validation timed out.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeSignaling : IProductionDirectShadowSignaling
        {
            private readonly Channel<TransportSignalEnvelopeV1> incoming =
                Channel.CreateUnbounded<TransportSignalEnvelopeV1>();
            private Exception failure;
            internal int ReaderCount;
            internal readonly ConcurrentQueue<TransportSignalEnvelopeV1> Sent = new();
            internal bool SendAllowed = true;

            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
                FetchConfigAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>
                    .Disabled());
            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
                IssueTicketAsync(Guid targetDeviceId, Guid connectionId,
                    ProductionDirectTransportServerConfig config,
                    CancellationToken cancellationToken = default) =>
                Task.FromResult(ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>
                    .Disabled());
            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(
                DirectPathTicketV1 ticket, Guid initiatorDeviceId, Guid connectionId,
                ProductionDirectTransportServerConfig config) => throw new NotSupportedException();
            public bool Start() => true;
            public bool TrySend(TransportSignalEnvelopeV1 envelope)
            {
                if (!SendAllowed) return false;
                Sent.Enqueue(envelope.Clone());
                return true;
            }
            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref ReaderCount);
                while (await incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (incoming.Reader.TryRead(out var value)) yield return value;
                }
                if (failure != null) throw failure;
            }
            internal void Enqueue(TransportSignalEnvelopeV1 value) => incoming.Writer.TryWrite(value);
            internal void Fail(Exception error)
            {
                failure = error;
                incoming.Writer.TryComplete();
            }
            public ValueTask DisposeAsync()
            {
                incoming.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakePromotableV4 : IProductionDirectPromotableNativeV4
        {
            private int attempted;
            internal int PromotionCalls;
            public ProductionDirectNativeSnapshot Snapshot => null;
            public ProductionDirectNativePromotionResultV4 TryPromoteNomination(
                ProductionDirectCommitRequestV4 request,
                IProductionDirectNativePromotionOperationV4 operation)
            {
                Interlocked.Increment(ref PromotionCalls);
                return Interlocked.Exchange(ref attempted, 1) == 0
                    ? ProductionDirectNativePromotionResultV4.Completed(0)
                    : ProductionDirectNativePromotionResultV4.NotInvoked(
                        ProductionDirectNativeCommandResult.RelayFallback);
            }
            public ProductionDirectNativeCommandResult Gather(
                ProductionDirectGatherRequestV4 request) =>
                ProductionDirectNativeCommandResult.RelayFallback;
            public ProductionDirectNativeCommandResult BeginChecks(
                ProductionDirectBeginChecksRequestV4 request) =>
                ProductionDirectNativeCommandResult.RelayFallback;
            public ProductionDirectNativeCommandResult AppendCheckPairs(
                ProductionDirectAppendPairsRequestV4 request) =>
                ProductionDirectNativeCommandResult.RelayFallback;
            public ProductionDirectNativeCommandResult CommitNomination(
                ProductionDirectCommitRequestV4 request) =>
                ProductionDirectNativeCommandResult.RelayFallback;
            public ProductionDirectNativeCommandResult Cancel(ulong epoch, Guid attemptId) =>
                ProductionDirectNativeCommandResult.Accepted;
            public ProductionDirectNativeCommandResult NetworkChanged(ulong epoch, Guid attemptId) =>
                ProductionDirectNativeCommandResult.Accepted;
            public ProductionDirectNativeCommandResult SubmitSignature(
                ulong epoch, Guid attemptId, ulong requestId, byte[] signature) =>
                ProductionDirectNativeCommandResult.Accepted;
            public IReadOnlyList<ProductionDirectNativeEventV4> Poll(int maximumEvents = 32) =>
                Array.Empty<ProductionDirectNativeEventV4>();
            public void Dispose() { }
        }

        private sealed class FakeNative : IProductionDirectDataNativePeer
        {
            internal readonly ConcurrentQueue<ProductionDirectRouteActionV5> Actions = new();
            internal readonly ConcurrentQueue<ProductionDirectRouteActionV5> CancelActions = new();
            internal readonly ConcurrentQueue<ProductionDirectNativeEventV5> Events = new();
            internal readonly ConcurrentQueue<ProductionDirectSigningPayloadV5> SigningPayloads = new();
            internal readonly ManualResetEventSlim RouteEntered = new(false);
            internal readonly ManualResetEventSlim PromoteEntered = new(false);
            internal ManualResetEventSlim RouteGate;
            internal ManualResetEventSlim PromoteGate;
            internal Exception DisposeError;
            internal bool EmitRelayCarrierOnReceiveFallback;
            internal ProductionDirectNativeResultV5 RouteResult = ProductionDirectNativeResultV5.Accepted;
            internal ProductionDirectNativeResultV5 IngressResult = ProductionDirectNativeResultV5.Accepted;
            internal ProductionDirectNativeResultV5 StartProposalResult =
                ProductionDirectNativeResultV5.Accepted;
            internal ProductionDirectNativeResultV5 ReceiveProposalResult =
                ProductionDirectNativeResultV5.Accepted;
            internal ProductionDirectNativeResultV5 TickResult =
                ProductionDirectNativeResultV5.Accepted;
            internal int CursorCalls;
            internal int DeregisterCalls;
            internal ProductionDirectSessionDeregisterV5 LastDeregister;
            internal ulong SubmittedRequestId;
            internal int SubmittedSignatureLength;
            internal int PromoteCalls;
            internal int StartProposalCalls;
            internal int ReceiveProposalCalls;
            internal int ReceiveAcceptCalls;
            internal int ReceiveCommitCalls;
            internal int CommitEchoCalls;
            internal int ReceiveFallbackCalls;
            internal int TickCalls;
            internal int DisposeCalls;
            internal ProductionDirectNativeResultV5 PromoteResult =
                ProductionDirectNativeResultV5.Accepted;
            internal (ProductionDirectNativeResultV5 Status, ProductionDirectPathInfoV5 Info)
                CurrentPathInfo = (ProductionDirectNativeResultV5.NotReady, null);
            internal ProductionDirectProposalRequestV5 LastProposal;
            internal ProductionDirectTransitionRequestV5 LastAccept;
            internal ProductionDirectTransitionRequestV5 LastCommit;
            internal ProductionDirectTransitionRequestV5 LastCommitEcho;
            internal ProductionDirectTransitionRequestV5 LastFallback;
            internal byte[] LastRouteEnvelope;
            internal ProductionDirectCommitRequestV4 PromotedNomination;
            internal bool PromotionSecretsCleared => PromotedNomination != null &&
                PromotedNomination.PeerCertificateSha256.All(value => value == 0) &&
                PromotedNomination.PeerDeviceSigningPublicKey.All(value => value == 0) &&
                PromotedNomination.LocalProofNonce.All(value => value == 0);
            public bool NativeAvailable => true;
            public ProductionDirectNativeResultV5 Promote(
                IProductionDirectPromotableNativeV4 v4,
                ProductionDirectPromoteRequestV5 request)
            {
                Interlocked.Increment(ref PromoteCalls);
                PromotedNomination = request.Nomination;
                PromoteEntered.Set();
                PromoteGate?.Wait(TimeSpan.FromSeconds(5));
                _ = v4.TryPromoteNomination(request.Nomination, new NoopPromotionOperation());
                return PromoteResult;
            }
            public (ProductionDirectNativeResultV5 Status, ProductionDirectPathInfoV5 Info)
                PathInfo(ProductionDirectPathFenceV5 path) => CurrentPathInfo;
            public ProductionDirectNativeResultV5 StartProposal(
                ProductionDirectProposalRequestV5 request)
            {
                Interlocked.Increment(ref StartProposalCalls);
                LastProposal = request;
                return StartProposalResult;
            }
            public ProductionDirectNativeResultV5 ReceiveProposal(
                ProductionDirectProposalRequestV5 request)
            {
                Interlocked.Increment(ref ReceiveProposalCalls);
                LastProposal = request;
                return ReceiveProposalResult;
            }
            public ProductionDirectNativeResultV5 ReceiveAccept(
                ProductionDirectTransitionRequestV5 request)
            {
                Interlocked.Increment(ref ReceiveAcceptCalls);
                LastAccept = request;
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 ReceiveCommit(
                ProductionDirectTransitionRequestV5 request)
            {
                Interlocked.Increment(ref ReceiveCommitCalls);
                LastCommit = request;
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 CommitEchoSent(
                ProductionDirectTransitionRequestV5 request)
            {
                Interlocked.Increment(ref CommitEchoCalls);
                LastCommitEcho = request;
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 ReceiveFallback(
                ProductionDirectTransitionRequestV5 request)
            {
                Interlocked.Increment(ref ReceiveFallbackCalls);
                LastFallback = request;
                if (EmitRelayCarrierOnReceiveFallback)
                {
                    Actions.Enqueue(new ProductionDirectRouteActionV5
                    {
                        Path = request.Path,
                        Kind = ProductionDirectRouteActionKindV5.Carrier,
                        Direction = request.Direction,
                        Carrier = ProductionDirectCarrierV5.Relay
                    });
                }
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 Tick(
                ProductionDirectTransitionRequestV5 request)
            {
                Interlocked.Increment(ref TickCalls);
                return TickResult;
            }
            public ProductionDirectNativeResultV5 RouteEnvelope(
                ProductionDirectRouteEnvelopeRequestV5 request, byte[] exactEnvelope)
            {
                RouteEntered.Set();
                RouteGate?.Wait(TimeSpan.FromSeconds(5));
                LastRouteEnvelope = exactEnvelope;
                return RouteResult;
            }
            public ProductionDirectNativeResultV5 ArbitrateIngress(
                ProductionDirectIngressRequestV5 request, byte[] exactEnvelope) => IngressResult;
            public ProductionDirectNativeResultV5 TickIngress(
                ProductionDirectPathFenceV5 path, ulong nowMilliseconds) =>
                ProductionDirectNativeResultV5.Accepted;
            public ProductionDirectSigningPayloadV5 ReceiveSigningPayload(
                ProductionDirectPathFenceV5 path) => SigningPayloads.TryDequeue(out var value)
                    ? value
                    : new ProductionDirectSigningPayloadV5
                    {
                        Status = ProductionDirectNativeResultV5.NoData
                    };
            public ProductionDirectNativeResultV5 SubmitSignature(
                ProductionDirectPathFenceV5 path, ulong requestId, byte[] signature)
            {
                SubmittedRequestId = requestId;
                SubmittedSignatureLength = signature?.Length ?? 0;
                return ProductionDirectNativeResultV5.Accepted;
            }
            public (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
                PollRouteAction(ProductionDirectPathFenceV5 path) => Actions.TryDequeue(out var value)
                    ? (ProductionDirectNativeResultV5.Accepted, value)
                    : (ProductionDirectNativeResultV5.NoData, null);
            public ProductionDirectNativeReceiveV5 ReceiveEnvelope(ProductionDirectPathFenceV5 path) =>
                new() { Status = ProductionDirectNativeResultV5.NoData };
            public IReadOnlyList<ProductionDirectNativeEventV5> Poll(int maximumEvents = 32)
            {
                var values = new List<ProductionDirectNativeEventV5>();
                while (values.Count < maximumEvents && Events.TryDequeue(out var value)) values.Add(value);
                return values;
            }
            public ProductionDirectNativeResultV5 UpdateAppliedCursor(
                ProductionDirectCursorUpdateV5 request)
            {
                Interlocked.Increment(ref CursorCalls);
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 DeregisterSession(
                ProductionDirectSessionDeregisterV5 request)
            {
                Interlocked.Increment(ref DeregisterCalls);
                LastDeregister = request;
                return ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 NetworkChanged(ProductionDirectPathFenceV5 path) =>
                ProductionDirectNativeResultV5.Accepted;
            internal readonly ConcurrentQueue<ProductionDirectNativeResultV5> RenewApplies = new();
            internal int RenewApplyCalls;
            internal byte[] LastRenewTicket;
            public ProductionDirectNativeResultV5 RenewApplyTicket(
                ProductionDirectPathFenceV5 path, byte[] ticket)
            {
                Interlocked.Increment(ref RenewApplyCalls);
                LastRenewTicket = ticket?.ToArray();
                return RenewApplies.TryDequeue(out var status)
                    ? status
                    : ProductionDirectNativeResultV5.Accepted;
            }
            public ProductionDirectNativeResultV5 Cancel(ProductionDirectPathFenceV5 path) =>
                DrainCancelActions();
            public void Dispose()
            {
                Interlocked.Increment(ref DisposeCalls);
                if (DisposeError != null) throw DisposeError;
            }

            private ProductionDirectNativeResultV5 DrainCancelActions()
            {
                while (CancelActions.TryDequeue(out var value)) Actions.Enqueue(value);
                return ProductionDirectNativeResultV5.Accepted;
            }

            private sealed class NoopPromotionOperation : IProductionDirectNativePromotionOperationV4
            {
                public int Invoke(
                    IntPtr transientV4Handle,
                    ref TermLinkDirectCommitNominationV4 nomination) => 0;
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectNative.Validation
{
    internal static class ProductionDirectNativeV5Validation
    {
        internal static async Task ValidateAsync()
        {
            ValidateExactLayoutAndFailClosedLoading();
            ValidateCapacityAndPathFence();
            ValidateTemporaryNativeBufferOwnership();
            ValidateSigningAndEnvelopeTwoPhaseReads();
            ValidateOwnedRouteAndIngress();
            ValidateMetadataOnlyAndPayloadActions();
            ValidateEndpointDirections();
            ValidateSuccessorDrainsPredecessorRecovery();
            ValidateProtocolFailurePreservesRecoveryDrain();
            ValidateEveryEventShapeAndErrors();
            ValidateMalformedRecordsFailClosed();
            await ValidateDestroySerializationAsync().ConfigureAwait(false);
            ValidateRealLibraryIfRequested();
        }

        private static void ValidateExactLayoutAndFailClosedLoading()
        {
            Require(ProductionDirectNativeAbiV5.TryValidateLayout(out var error),
                "ABI v5 layout failed at " + error + ".");
            var sizes = new Dictionary<Type, int>
            {
                [typeof(TermLinkDirectPathV5)] = 64, [typeof(TermLinkDirectCreateV5)] = 112,
                [typeof(TermLinkDirectEventV5)] = 80, [typeof(TermLinkSocketAddressV5)] = 48,
                [typeof(TermLinkDirectCandidateV5)] = 128,
                [typeof(TermLinkDirectCandidatePairV5)] = 288,
                [typeof(TermLinkDirectPromoteV5)] = 544, [typeof(TermLinkDirectPathInfoV5)] = 128,
                [typeof(TermLinkDirectSignatureV5)] = 232,
                [typeof(TermLinkDirectSessionBarrierV5)] = 24,
                [typeof(TermLinkDirectProposalV5)] = 968,
                [typeof(TermLinkDirectTransitionV5)] = 176, [typeof(TermLinkDirectCursorV5)] = 120,
                [typeof(TermLinkDirectRouteEnvelopeV5)] = 128,
                [typeof(TermLinkDirectIngressV5)] = 136,
                [typeof(TermLinkDirectRouteActionV5)] = 200
            };
            Require(sizes.All(pair => Marshal.SizeOf(pair.Key) == pair.Value),
                "One of the 16 ABI v5 records drifted from its frozen size.");
            Require(Offset<TermLinkDirectPromoteV5>(nameof(TermLinkDirectPromoteV5.WinningPair)) == 104 &&
                    Offset<TermLinkDirectProposalV5>(nameof(TermLinkDirectProposalV5.Sessions)) == 184 &&
                    Offset<TermLinkDirectRouteActionV5>(nameof(TermLinkDirectRouteActionV5.PayloadBytes)) == 180 &&
                    Offset<TermLinkDirectIngressV5>(nameof(TermLinkDirectIngressV5.Reserved)) == 120,
                "Nested or tail ABI v5 offsets drifted.");

            var disabledLoader = new FakeLoader(new FakeApi());
            using (var disabled = new ProductionDirectNativeBoundaryV5(enabled: false, loader: disabledLoader))
                Require(!disabled.NativeAvailable && disabledLoader.LoadCalls == 0,
                    "Disabled v5 loaded native code.");
            var mismatch = new FakeApi { ReportedAbi = 4 };
            using (var boundary = new ProductionDirectNativeBoundaryV5(
                enabled: true, initialPath: Path(1), loader: new FakeLoader(mismatch)))
                Require(!boundary.NativeAvailable && mismatch.CreateCalls == 0 && mismatch.DisposeCalls == 1,
                    "A non-v5 API crossed the boundary.");

            var missing = new ProductionDirectNativeLibraryLoaderV5(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "termlink-v5-missing-" + Guid.NewGuid().ToString("N") + ".dll"));
            Require(!missing.TryLoad(out _, out var missingError) && missingError == "NATIVE_LIBRARY_MISSING",
                "Missing native library did not fail closed.");
            var partial = new ProductionDirectNativeLibraryLoaderV5(
                System.IO.Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
            Require(!partial.TryLoad(out _, out var partialError) && partialError == "NATIVE_LIBRARY_V5_INVALID",
                "A partial v5 export surface was admitted.");

            var methods = typeof(IProductionDirectNativeApiV5).GetMethods();
            Require(methods.Length == 26 &&
                    methods.All(item => item.Name is not ("SendEnvelope" or "SubmitReceipt")),
                "Managed API does not expose exactly the frozen 26 native operations.");
        }

        private static void ValidateCapacityAndPathFence()
        {
            var path = Path(2);
            var api = new FakeApi();
            using var boundary = Boundary(api, path);
            Require(boundary.NativeAvailable && api.LastCreate.QueueCapacityFrames == 16 &&
                    api.LastCreate.QueueCapacityBytes == 4096 && api.LastCreate.Path.PathEpoch == 2,
                "Create did not copy the exact initial path/capacity.");
            var successor = Path(3);
            Require(boundary.PathUsage(successor) == ProductionDirectNativeResultV5.Accepted &&
                    boundary.CurrentPath.Equals(successor), "Successor path was not installed.");
            Require(boundary.PathUsage(path) == ProductionDirectNativeResultV5.StalePath && api.PathCalls == 1,
                "Stale path crossed into native.");
            var tooLarge = new FakeLoader(new FakeApi());
            using var invalid = new ProductionDirectNativeBoundaryV5(enabled: true,
                queueCapacityFrames: ProductionDirectNativeAbiV5.MaximumFrames + 1,
                initialPath: path, loader: tooLarge);
            Require(!invalid.NativeAvailable && tooLarge.LoadCalls == 0,
                "Over-limit capacity loaded native code.");
        }

        private static void ValidateSigningAndEnvelopeTwoPhaseReads()
        {
            var path = Path(4);
            var api = new FakeApi();
            api.Signing.Enqueue((77, new byte[] { 4, 5, 6 }));
            api.Incoming.Enqueue((4, new byte[] { 7, 8, 9, 10 }));
            using var boundary = Boundary(api, path);
            var signing = boundary.ReceiveSigningPayload(path);
            Require(signing.Status == ProductionDirectNativeResultV5.Accepted && signing.RequestId == 77 &&
                    signing.Payload.SequenceEqual(new byte[] { 4, 5, 6 }) && api.SigningProbeCalls == 1 &&
                    api.Signing.Count == 0, "Signing payload two-phase read dequeued or rewrote its probe.");
            Require(boundary.SubmitSignature(path, 77, new byte[] { 1, 2, 3 }) ==
                    ProductionDirectNativeResultV5.Accepted && api.LastSignature.RequestId == 77 &&
                    api.LastSignature.Signature.Take(3).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                    api.LastSignature.Signature.Skip(3).All(value => value == 0),
                "Signature was not padded/owned exactly.");
            Require(boundary.SubmitSignature(path, 77, new byte[] { 1, 2, 3 }) ==
                    ProductionDirectNativeResultV5.InvalidArgument &&
                    boundary.SubmitSignature(path, 88, new byte[] { 1, 2, 3 }) ==
                    ProductionDirectNativeResultV5.InvalidArgument && api.SubmitSignatureCalls == 1,
                "A duplicate or unsolicited signature crossed the Windows endpoint boundary.");
            var received = boundary.ReceiveEnvelope(path);
            Require(received.Status == ProductionDirectNativeResultV5.Accepted && received.FrameSequence == 4 &&
                    received.RequiredBytes == 4 && received.Envelope.SequenceEqual(new byte[] { 7, 8, 9, 10 }) &&
                    api.EnvelopeProbeCalls == 1 && api.Incoming.Count == 0,
                "Envelope two-phase short-buffer read changed the queue front.");
            var none = boundary.ReceiveEnvelope(path);
            Require(none.Status == ProductionDirectNativeResultV5.NoData && none.Envelope.Length == 0,
                "Empty receive did not preserve the output sentinel: " + none.Status +
                ", native=" + boundary.NativeAvailable + ".");
        }

        private static void ValidateTemporaryNativeBufferOwnership()
        {
            var path = Path(15);
            var pathApi = new FakeApi { PathInfoReady = true };
            using (var boundary = Boundary(pathApi, path))
            {
                var result = boundary.PathInfo(path);
                Require(result.Status == ProductionDirectNativeResultV5.Accepted && result.Info?.Ready == true &&
                        result.Info.PathKind == ProductionDirectPathKindV5.Lan &&
                        result.Info.DirectPathBinding.SequenceEqual(Enumerable.Repeat((byte)0x5a, 32)) &&
                        !ReferenceEquals(result.Info.DirectPathBinding,
                            pathApi.LastPathInfoOutput.DirectPathBinding) &&
                        PathBuffersAreZero(pathApi.LastPathInfoInput) &&
                        PathInfoBuffersAreZero(pathApi.LastPathInfoOutput),
                    "PathInfo did not isolate its returned binding before clearing every native buffer.");
            }

            var rejectedApi = new FakeApi
            {
                PathInfoReady = true,
                PathInfoStatus = ProductionDirectNativeAbiV5.ErrorTimeout
            };
            using (var boundary = Boundary(rejectedApi, path))
            {
                var result = boundary.PathInfo(path);
                Require(result.Status == ProductionDirectNativeResultV5.Timeout && result.Info == null &&
                        PathBuffersAreZero(rejectedApi.LastPathInfoInput) &&
                        PathInfoBuffersAreZero(rejectedApi.LastPathInfoOutput),
                    "A non-OK PathInfo call retained native input or output buffers.");
            }

            var malformedPathApi = new FakeApi { PathInfoReady = true, CorruptPathInfo = true };
            using (var boundary = Boundary(malformedPathApi, path))
            {
                var result = boundary.PathInfo(path);
                Require(result.Status == ProductionDirectNativeResultV5.RelayFallback && result.Info == null &&
                        !boundary.NativeAvailable && PathBuffersAreZero(malformedPathApi.LastPathInfoInput) &&
                        PathInfoBuffersAreZero(malformedPathApi.LastPathInfoOutput),
                    "A malformed PathInfo record failed closed without clearing its native buffers.");
            }

            var exceptionalPathApi = new FakeApi { PathInfoReady = true, ThrowPathInfo = true };
            using (var boundary = Boundary(exceptionalPathApi, path))
            {
                var result = boundary.PathInfo(path);
                Require(result.Status == ProductionDirectNativeResultV5.RelayFallback && result.Info == null &&
                        !boundary.NativeAvailable && PathBuffersAreZero(exceptionalPathApi.LastPathInfoInput) &&
                        PathInfoBuffersAreZero(exceptionalPathApi.LastPathInfoOutput),
                    "An exceptional PathInfo call retained native input or output buffers.");
            }

            var metadataApi = new FakeApi();
            var metadataNative = ControlAction(
                path, ProductionDirectRouteActionKindV5.Accept, accepted: true);
            var expectedBarrierHash = metadataNative.BarrierSha256.ToArray();
            metadataApi.Actions.Enqueue((metadataNative, Array.Empty<byte>()));
            using (var boundary = Boundary(metadataApi, path))
            {
                var result = boundary.PollRouteAction(path);
                Require(result.Status == ProductionDirectNativeResultV5.Accepted &&
                        result.Action?.Kind == ProductionDirectRouteActionKindV5.Accept &&
                        result.Action.BarrierSha256.SequenceEqual(expectedBarrierHash) &&
                        !ReferenceEquals(result.Action.BarrierSha256,
                            metadataApi.LastPolledAction.BarrierSha256) &&
                        RouteActionBuffersAreZero(metadataApi.LastPolledAction),
                    "A metadata route action did not survive independent native-record clearing.");
            }

            var payloadApi = new FakeApi();
            var session = Guid.Parse("30000000-0000-0000-0000-000000000015");
            var payloadNative = EnvelopeAction(
                path, ProductionDirectRouteActionKindV5.RelayEnvelope, session, payloadBytes: 4);
            payloadApi.Actions.Enqueue((payloadNative, new byte[] { 1, 3, 5, 7 }));
            using (var boundary = Boundary(payloadApi, path))
            {
                var result = boundary.PollRouteAction(path);
                Require(result.Status == ProductionDirectNativeResultV5.Accepted &&
                        result.Action?.Payload.SequenceEqual(new byte[] { 1, 3, 5, 7 }) == true &&
                        payloadApi.ActionProbeCalls == 1 &&
                        RouteActionBuffersAreZero(payloadApi.LastPolledAction),
                    "A short-buffer route action lost its owned DTO payload or retained its native record.");
            }

            var malformedActionApi = new FakeApi();
            var malformedNative = EnvelopeAction(
                path, ProductionDirectRouteActionKindV5.RelayEnvelope, session, payloadBytes: 2);
            malformedNative.Flags = 1;
            malformedActionApi.Actions.Enqueue((malformedNative, new byte[] { 2, 4 }));
            using (var boundary = Boundary(malformedActionApi, path))
            {
                var result = boundary.PollRouteAction(path);
                Require(result.Status == ProductionDirectNativeResultV5.RelayFallback && result.Action == null &&
                        !boundary.NativeAvailable && malformedActionApi.ActionProbeCalls == 1 &&
                        RouteActionBuffersAreZero(malformedActionApi.LastPolledAction),
                    "A malformed short-buffer route action failed closed without clearing its native record.");
            }


            var exceptionalActionApi = new FakeApi { ThrowOnActionPayloadRead = true };
            var exceptionalNative = EnvelopeAction(
                path, ProductionDirectRouteActionKindV5.RelayEnvelope, session, payloadBytes: 2);
            exceptionalActionApi.Actions.Enqueue((exceptionalNative, new byte[] { 6, 8 }));
            using (var boundary = Boundary(exceptionalActionApi, path))
            {
                var result = boundary.PollRouteAction(path);
                Require(result.Status == ProductionDirectNativeResultV5.RelayFallback && result.Action == null &&
                        !boundary.NativeAvailable && exceptionalActionApi.ActionProbeCalls == 1 &&
                        RouteActionBuffersAreZero(exceptionalActionApi.LastPolledAction),
                    "An exceptional short-buffer route action retained its native record.");
            }
        }

        private static void ValidateOwnedRouteAndIngress()
        {
            var path = Path(5);
            var api = new FakeApi();
            using var boundary = Boundary(api, path);
            var route = Route(path);
            var caller = new byte[] { 0x0a, 0x02, 0x54, 0x4c };
            Require(boundary.RouteEnvelope(route, caller) == ProductionDirectNativeResultV5.Accepted,
                "Valid exact Envelope route failed.");
            caller.AsSpan().Fill(0xff);
            Require(api.LastRoute.SequenceEqual(new byte[] { 0x0a, 0x02, 0x54, 0x4c }),
                "Native did not receive an owned byte-identical route copy.");
            api.RouteStatus = -3;
            caller = new byte[] { 1, 2, 3 };
            Require(boundary.RouteEnvelope(route, caller) == ProductionDirectNativeResultV5.QueueFull &&
                    caller.SequenceEqual(new byte[] { 1, 2, 3 }),
                "Non-OK routing consumed or altered caller-owned bytes.");
            api.RouteStatus = 0;
            var ingress = new ProductionDirectIngressRequestV5
            {
                Path = path, Direction = ProductionDirectDirectionV5.AndroidToPc,
                Source = ProductionDirectIngressSourceV5.Direct, SessionId = route.SessionId,
                EnvelopeSequence = 1, NowMilliseconds = 10, DuplicateGraceMilliseconds = 250
            };
            caller = new byte[] { 9, 8, 7 };
            Require(boundary.ArbitrateIngress(ingress, caller) == ProductionDirectNativeResultV5.Accepted,
                "Valid Direct ingress arbitration failed.");
            caller.AsSpan().Fill(0);
            Require(api.LastIngress.SequenceEqual(new byte[] { 9, 8, 7 }),
                "Ingress did not receive its private exact copy.");
            Require(boundary.UpdateAppliedCursor(new ProductionDirectCursorUpdateV5
            {
                Path = path, Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = route.SessionId, HighestContiguous = 1
            }) == ProductionDirectNativeResultV5.Accepted && api.LastCursor.HighestContiguous == 1,
                "Caller-verified applied cursor notification was not forwarded.");
            Require(boundary.DeregisterSession(new ProductionDirectSessionDeregisterV5
            {
                Path = path, Direction = ProductionDirectDirectionV5.AndroidToPc,
                SessionId = route.SessionId
            }) == ProductionDirectNativeResultV5.Accepted &&
                api.LastDeregister.HighestContiguous == 0 &&
                GuidFromNativeBytes(api.LastDeregister.SessionId) == route.SessionId,
                "Session deregistration did not use the frozen zero-cursor record.");
            api.TickIngressStatus = -11;
            Require(boundary.TickIngress(path, 10) == ProductionDirectNativeResultV5.Timeout,
                "TIMEOUT status was not preserved.");
        }

        private static void ValidateMetadataOnlyAndPayloadActions()
        {
            var path = Path(6);
            var api = new FakeApi();
            var session = Guid.Parse("30000000-0000-0000-0000-000000000006");
            using var boundary = Boundary(api, path);
            foreach (var kind in new[]
            {
                ProductionDirectRouteActionKindV5.DirectEnvelope,
                ProductionDirectRouteActionKindV5.IngressHold,
                ProductionDirectRouteActionKindV5.IngressDrop
            })
            {
                api.Actions.Enqueue((EnvelopeAction(path, kind, session, payloadBytes: 4), Array.Empty<byte>()));
                var polled = boundary.PollRouteAction(path);
                Require(polled.Status == ProductionDirectNativeResultV5.Accepted && polled.Action.Kind == kind &&
                        polled.Action.PayloadBytes == 4 && polled.Action.Payload.Length == 0,
                    kind + " metadata-only payload length was confused with logical payload_bytes.");
            }
            var directFromFallbackBatch = EnvelopeAction(path,
                ProductionDirectRouteActionKindV5.DirectEnvelope, session, payloadBytes: 4);
            // Native applies one fallback_reason to every transmission in a
            // flush batch. Direct entries staged before frame-sequence
            // exhaustion can therefore legitimately carry reason 1..9.
            directFromFallbackBatch.Reason = 9;
            api.Actions.Enqueue((directFromFallbackBatch, Array.Empty<byte>()));
            Require(boundary.PollRouteAction(path).Action.Reason ==
                    ProductionDirectRouteReasonV5.ProtocolViolation,
                "A Direct action staged before same-batch fallback lost its legal reason.");
            api.Actions.Enqueue((EnvelopeAction(path, ProductionDirectRouteActionKindV5.RelayEnvelope,
                session, payloadBytes: 3), new byte[] { 1, 2, 3 }));
            var relay = boundary.PollRouteAction(path);
            Require(relay.Status == ProductionDirectNativeResultV5.Accepted &&
                    relay.Action.Payload.SequenceEqual(new byte[] { 1, 2, 3 }) && api.ActionProbeCalls == 1,
                "Relay route action short-buffer retry changed or lost the queue front.");
            api.Actions.Enqueue((SessionCarrierAction(path, session,
                ProductionDirectCarrierV5.LanDirect), Array.Empty<byte>()));
            var sessionCarrier = boundary.PollRouteAction(path);
            Require(sessionCarrier.Status == ProductionDirectNativeResultV5.Accepted &&
                    sessionCarrier.Action.Kind == ProductionDirectRouteActionKindV5.SessionCarrier &&
                    sessionCarrier.Action.SessionId == session &&
                    sessionCarrier.Action.Carrier == ProductionDirectCarrierV5.LanDirect,
                "Per-session carrier action was not decoded with its exact session identity.");
            api.Actions.Enqueue((EnvelopeAction(path, ProductionDirectRouteActionKindV5.IngressDeliver,
                session, payloadBytes: 2), new byte[] { 8, 9 }));
            Require(boundary.PollRouteAction(path).Action.Payload.SequenceEqual(new byte[] { 8, 9 }),
                "Ingress delivery did not carry exact payload bytes.");
            api.Actions.Enqueue((new TermLinkDirectRouteActionV5
            {
                AbiVersion = 5, StructSize = 200, Path = NativePath(path), Kind = 6, Direction = 1,
                SessionId = new byte[16], DirectFrameSequence = 3, BarrierId = GuidBytes(
                    Guid.Parse("40000000-0000-0000-0000-000000000006")),
                BarrierSha256 = new byte[32], Reason = 3, Reserved = new ulong[2]
            }, Array.Empty<byte>()));
            var fallback = boundary.PollRouteAction(path);
            Require(fallback.Status == ProductionDirectNativeResultV5.Accepted &&
                    fallback.Action.Kind == ProductionDirectRouteActionKindV5.Fallback &&
                    fallback.Action.DirectFrameSequence == 3,
                "Legal pre-data fallback direct_frame_seq=3 was rejected.");
            foreach (var frame in new[]
            {
                0UL, 4UL, ProductionDirectNativeAbiV5.MaximumFrameSequence
            })
            {
                api.Actions.Enqueue((ControlAction(path, ProductionDirectRouteActionKindV5.Fallback,
                    directFrame: frame, reason: 3), Array.Empty<byte>()));
                Require(boundary.PollRouteAction(path).Action.DirectFrameSequence == frame,
                    "Legal fallback direct_frame_seq=" + frame + " was rejected.");
            }
            api.Actions.Enqueue((ControlAction(path, ProductionDirectRouteActionKindV5.Accept,
                accepted: true), Array.Empty<byte>()));
            api.Actions.Enqueue((ControlAction(path, ProductionDirectRouteActionKindV5.Commit,
                echo: true), Array.Empty<byte>()));
            api.Actions.Enqueue((ControlAction(path, ProductionDirectRouteActionKindV5.Carrier,
                carrier: 2), Array.Empty<byte>()));
            var controls = new[]
            {
                boundary.PollRouteAction(path).Action, boundary.PollRouteAction(path).Action,
                boundary.PollRouteAction(path).Action
            };
            Require(controls.Select(value => value.Kind).SequenceEqual(new[]
            {
                ProductionDirectRouteActionKindV5.Accept,
                ProductionDirectRouteActionKindV5.Commit,
                ProductionDirectRouteActionKindV5.Carrier
            }), "A valid Accept/Commit/Carrier action shape was rejected.");

            foreach (var invalidFrame in new[]
            {
                1UL, 2UL, ProductionDirectNativeAbiV5.MaximumFrameSequence + 1
            })
            {
                var invalidApi = new FakeApi();
                using var invalidBoundary = Boundary(invalidApi, path);
                invalidApi.Actions.Enqueue((ControlAction(path, ProductionDirectRouteActionKindV5.Fallback,
                    directFrame: invalidFrame, reason: 3), Array.Empty<byte>()));
                Require(invalidBoundary.PollRouteAction(path).Status ==
                        ProductionDirectNativeResultV5.RelayFallback && !invalidBoundary.NativeAvailable,
                    "Illegal fallback direct_frame_seq=" + invalidFrame + " was admitted.");
            }
        }

        private static void ValidateEveryEventShapeAndErrors()
        {
            var path = Path(7);
            var api = new FakeApi();
            using var boundary = Boundary(api, path);
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.PathUsed));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.EnvelopeQueued,
                flags: 1 | 4, frame: 4, payload: 2));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.EnvelopeAvailable,
                flags: 2, frame: 4, payload: 2));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.TransportReceipt,
                flags: 1, frame: 5));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.Cancelled));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.NetworkChanged));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.QueueLimit, status: -3));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.PathReady, flags: 8));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.DirectFailed, status: -12));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.Ping, flags: 1, frame: 6));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.Pong, flags: 2, frame: 7));
            api.Events.Enqueue(Event(path, ProductionDirectNativeEventKindV5.SignRequest, frame: 1, payload: 20));
            var events = boundary.Poll(12);
            Require(events.Count == 12 && events.Select(item => item.Kind).Distinct().Count() == 12 &&
                    events.Single(item => item.Kind == ProductionDirectNativeEventKindV5.DirectFailed).Status ==
                        ProductionDirectNativeResultV5.TicketExpired,
                "A valid event shape or TICKET_EXPIRED status was rejected/changed.");
        }

        private static void ValidateEndpointDirections()
        {
            var path = Path(11);
            var api = new FakeApi();
            using var boundary = Boundary(api, path);
            Require(boundary.StartProposal(Proposal(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.StartProposal(Proposal(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.InvalidArgument,
                "Windows start-proposal admitted a non-PC outbound direction.");
            Require(boundary.ReceiveProposal(Proposal(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.ReceiveProposal(Proposal(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.InvalidArgument,
                "Windows receive-proposal admitted a non-Android outbound direction.");
            Require(boundary.ReceiveAccept(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.ReceiveAccept(Transition(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.InvalidArgument,
                "Windows receive-accept direction was not endpoint-fenced.");
            Require(boundary.ReceiveCommit(Transition(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.CommitEchoSent(Transition(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.ReceiveCommit(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.CommitEchoSent(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.InvalidArgument,
                "Windows receive-commit did not admit both initial/echo directions or echo-sent lost its role fence.");
            Require(boundary.ReceiveFallback(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.ReceiveFallback(Transition(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.Tick(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Accepted &&
                    boundary.Tick(Transition(path, ProductionDirectDirectionV5.AndroidToPc)) ==
                    ProductionDirectNativeResultV5.Accepted,
                "Bidirectional fallback/tick semantics were over-constrained.");

            var route = Route(path);
            Require(boundary.RouteEnvelope(new ProductionDirectRouteEnvelopeRequestV5
                    {
                        Path = path, Direction = ProductionDirectDirectionV5.AndroidToPc,
                        RouteEligible = route.RouteEligible, SessionId = route.SessionId,
                        EnvelopeSequence = route.EnvelopeSequence, NowMilliseconds = route.NowMilliseconds
                    }, new byte[] { 1 }) == ProductionDirectNativeResultV5.InvalidArgument,
                "Windows outbound routing admitted Android-to-PC data.");
            Require(boundary.ArbitrateIngress(new ProductionDirectIngressRequestV5
                    {
                        Path = path, Direction = ProductionDirectDirectionV5.PcToAndroid,
                        Source = ProductionDirectIngressSourceV5.Relay, SessionId = route.SessionId,
                        EnvelopeSequence = 1, NowMilliseconds = 1, DuplicateGraceMilliseconds = 0
                    }, new byte[] { 1 }) == ProductionDirectNativeResultV5.InvalidArgument,
                "Windows ingress arbitration admitted PC-to-Android data.");
            Require(boundary.UpdateAppliedCursor(new ProductionDirectCursorUpdateV5
                    {
                        Path = path, Direction = ProductionDirectDirectionV5.PcToAndroid,
                        SessionId = route.SessionId, HighestContiguous = 1
                    }) == ProductionDirectNativeResultV5.InvalidArgument,
                "Windows applied cursor admitted an outbound direction.");
        }

        private static void ValidateSuccessorDrainsPredecessorRecovery()
        {
            var predecessor = Path(12);
            var successor = Path(13);
            var session = Guid.Parse("30000000-0000-0000-0000-000000000012");
            var api = new FakeApi();
            using var boundary = Boundary(api, predecessor);
            api.Actions.Enqueue((EnvelopeAction(predecessor,
                ProductionDirectRouteActionKindV5.DirectEnvelope, session, 3), Array.Empty<byte>()));
            api.Actions.Enqueue((EnvelopeAction(predecessor,
                ProductionDirectRouteActionKindV5.RelayEnvelope, session, 3), new byte[] { 1, 2, 3 }));
            api.Actions.Enqueue((EnvelopeAction(predecessor,
                ProductionDirectRouteActionKindV5.IngressHold, session, 3), Array.Empty<byte>()));
            api.Actions.Enqueue((ControlAction(predecessor,
                ProductionDirectRouteActionKindV5.Fallback, directFrame: 3, reason: 4), Array.Empty<byte>()));
            api.Actions.Enqueue((ControlAction(predecessor,
                ProductionDirectRouteActionKindV5.Carrier, carrier: 1), Array.Empty<byte>()));
            api.Actions.Enqueue((ControlAction(successor,
                ProductionDirectRouteActionKindV5.Carrier, carrier: 1), Array.Empty<byte>()));

            Require(boundary.PathUsage(successor) == ProductionDirectNativeResultV5.Accepted,
                "The successor path was not installed before predecessor drain.");
            var relay = boundary.PollRouteAction(successor);
            var fallback = boundary.PollRouteAction(successor);
            var current = boundary.PollRouteAction(successor);
            Require(relay.Status == ProductionDirectNativeResultV5.Accepted &&
                    relay.Action.Path.Equals(predecessor) &&
                    relay.Action.Kind == ProductionDirectRouteActionKindV5.RelayEnvelope &&
                    relay.Action.Payload.SequenceEqual(new byte[] { 1, 2, 3 }) &&
                    fallback.Action.Path.Equals(predecessor) &&
                    fallback.Action.Kind == ProductionDirectRouteActionKindV5.Fallback &&
                    current.Action.Path.Equals(successor) &&
                    current.Action.Kind == ProductionDirectRouteActionKindV5.Carrier &&
                    boundary.NativeAvailable,
                "Successor polling did not preserve ordered predecessor Relay recovery actions.");
            Require(boundary.PollRouteAction(successor).Status == ProductionDirectNativeResultV5.NoData,
                "The predecessor recovery queue did not drain completely.");

            api.Actions.Enqueue((ControlAction(predecessor,
                ProductionDirectRouteActionKindV5.Fallback, directFrame: 3, reason: 4), Array.Empty<byte>()));
            Require(boundary.PollRouteAction(successor).Status == ProductionDirectNativeResultV5.RelayFallback &&
                    !boundary.NativeAvailable,
                "An unregistered predecessor fence was accepted after the drain completed.");
        }

        private static void ValidateProtocolFailurePreservesRecoveryDrain()
        {
            var path = Path(14);
            var api = new FakeApi { ReceiveAcceptStatus = ProductionDirectNativeAbiV5.ErrorProtocol };
            var session = Guid.Parse("30000000-0000-0000-0000-000000000014");
            using var boundary = Boundary(api, path);
            api.Actions.Enqueue((EnvelopeAction(path,
                ProductionDirectRouteActionKindV5.RelayEnvelope, session, 3), new byte[] { 4, 5, 6 }));
            api.Actions.Enqueue((ControlAction(path,
                ProductionDirectRouteActionKindV5.Fallback, directFrame: 3, reason: 7), Array.Empty<byte>()));

            Require(boundary.ReceiveAccept(Transition(path, ProductionDirectDirectionV5.PcToAndroid)) ==
                    ProductionDirectNativeResultV5.Protocol && boundary.NativeAvailable,
                "A path-level PROTOCOL result destroyed native before recovery actions could drain.");
            var relay = boundary.PollRouteAction(path);
            var fallback = boundary.PollRouteAction(path);
            Require(relay.Status == ProductionDirectNativeResultV5.Accepted &&
                    relay.Action.Payload.SequenceEqual(new byte[] { 4, 5, 6 }) &&
                    fallback.Status == ProductionDirectNativeResultV5.Accepted &&
                    fallback.Action.Kind == ProductionDirectRouteActionKindV5.Fallback &&
                    boundary.NativeAvailable,
                "A protocol rejection lost native-owned exact Relay recovery.");

            api.RouteStatus = 42;
            Require(boundary.RouteEnvelope(Route(path), new byte[] { 1 }) ==
                    ProductionDirectNativeResultV5.RelayFallback && !boundary.NativeAvailable,
                "An unknown native status was not treated as ABI corruption.");
        }

        private static void ValidateMalformedRecordsFailClosed()
        {
            var path = Path(8);
            var eventApi = new FakeApi();
            using (var boundary = Boundary(eventApi, path))
            {
                var malformed = Event(path, ProductionDirectNativeEventKindV5.PathReady, flags: 8);
                malformed.PayloadBytes = 1;
                eventApi.Events.Enqueue(malformed);
                Require(boundary.Poll().Count == 0 && !boundary.NativeAvailable && eventApi.DestroyCalls == 1,
                    "Malformed event did not retire only the Direct boundary.");
            }
            var actionApi = new FakeApi();
            using (var boundary = Boundary(actionApi, path))
            {
                var malformed = EnvelopeAction(path, ProductionDirectRouteActionKindV5.DirectEnvelope,
                    Guid.Parse("30000000-0000-0000-0000-000000000008"), 4);
                malformed.Flags = 1;
                actionApi.Actions.Enqueue((malformed, Array.Empty<byte>()));
                Require(boundary.PollRouteAction(path).Status == ProductionDirectNativeResultV5.RelayFallback &&
                        !boundary.NativeAvailable && actionApi.DestroyCalls == 1,
                    "Malformed route action did not fail closed.");
            }
            var emptyEventApi = new FakeApi { CorruptEmptyEvent = true };
            using (var boundary = Boundary(emptyEventApi, path))
                Require(boundary.Poll().Count == 0 && !boundary.NativeAvailable &&
                        emptyEventApi.DestroyCalls == 1,
                    "poll_event=0 was allowed to modify its output record.");
        }

        private static async Task ValidateDestroySerializationAsync()
        {
            var path = Path(9);
            var api = new FakeApi
            {
                OperationEntered = new ManualResetEventSlim(false),
                OperationRelease = new ManualResetEventSlim(false)
            };
            var boundary = Boundary(api, path);
            var send = Task.Run(() => boundary.RouteEnvelope(Route(path), new byte[] { 1, 2, 3 }));
            Require(api.OperationEntered.Wait(TimeSpan.FromSeconds(5)), "Native blocking fixture did not enter.");
            var dispose = Task.Run(boundary.Dispose);
            await Task.Delay(50).ConfigureAwait(false);
            Require(api.DestroyCalls == 0, "Destroy raced an active v5 operation.");
            api.OperationRelease.Set();
            await Task.WhenAll(send, dispose).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(api.DestroyCalls == 1 && api.DisposeCalls == 1 && !api.DestroyRacedOperation,
                "Destroy/unload was not externally serialized.");
            api.OperationEntered.Dispose(); api.OperationRelease.Dispose();
        }

        private static void ValidateRealLibraryIfRequested()
        {
            var libraryPath = Environment.GetEnvironmentVariable("TERMLINK_DIRECT_NATIVE_TEST_DLL");
            if (string.IsNullOrWhiteSpace(libraryPath)) return;
            var path = Path(10);
            using var boundary = new ProductionDirectNativeBoundaryV5(enabled: true,
                queueCapacityFrames: 8, queueCapacityBytes: 4096, initialPath: path,
                loader: new ProductionDirectNativeLibraryLoaderV5(libraryPath));
            Require(boundary.NativeAvailable, "Real native DLL did not expose the complete frozen v5 table.");
            Require(boundary.Attach(path) == ProductionDirectNativeResultV5.NotReady,
                "Unpromoted real v5 attach seam did not fail closed as NOT_READY.");
        }

        private static ProductionDirectRouteEnvelopeRequestV5 Route(ProductionDirectPathFenceV5 path) => new()
        {
            Path = path, Direction = ProductionDirectDirectionV5.PcToAndroid, RouteEligible = true,
            SessionId = Guid.Parse("30000000-0000-0000-0000-000000000005"),
            EnvelopeSequence = 1, NowMilliseconds = 10
        };

        private static ProductionDirectProposalRequestV5 Proposal(
            ProductionDirectPathFenceV5 path, ProductionDirectDirectionV5 direction) => new()
        {
            Path = path, Direction = direction,
            BarrierId = Guid.Parse("40000000-0000-0000-0000-000000000011"),
            BarrierSha256 = Enumerable.Repeat((byte)1, 32).ToArray(),
            DirectPathBinding = Enumerable.Repeat((byte)2, 32).ToArray(),
            DuplicateGraceMilliseconds = 250, NowMilliseconds = 1, TimeoutMilliseconds = 1_000,
            Sessions = new[]
            {
                new ProductionDirectSessionBarrierV5
                {
                    SessionId = Guid.Parse("30000000-0000-0000-0000-000000000011"),
                    FirstDirectSequence = 1
                }
            }
        };

        private static ProductionDirectTransitionRequestV5 Transition(
            ProductionDirectPathFenceV5 path, ProductionDirectDirectionV5 direction) => new()
        {
            Path = path, Direction = direction,
            BarrierId = Guid.Parse("40000000-0000-0000-0000-000000000011"),
            BarrierSha256 = Enumerable.Repeat((byte)1, 32).ToArray(),
            Accepted = true, NowMilliseconds = 1, TimeoutMilliseconds = 1_000
        };

        private static TermLinkDirectRouteActionV5 EnvelopeAction(ProductionDirectPathFenceV5 path,
            ProductionDirectRouteActionKindV5 kind, Guid session, uint payloadBytes)
        {
            var carrier = kind switch
            {
                ProductionDirectRouteActionKindV5.RelayEnvelope => 1u,
                ProductionDirectRouteActionKindV5.DirectEnvelope => 2u,
                _ => 2u
            };
            return new TermLinkDirectRouteActionV5
            {
                AbiVersion = 5, StructSize = 200, Path = NativePath(path), Kind = (uint)kind,
                Direction = 1, SessionId = GuidBytes(session), EnvelopeSequence = 1,
                DirectFrameSequence = kind == ProductionDirectRouteActionKindV5.DirectEnvelope ? 4UL : 0,
                BarrierId = new byte[16], BarrierSha256 = new byte[32], Carrier = carrier,
                PayloadBytes = payloadBytes, Reserved = new ulong[2]
            };
        }

        private static TermLinkDirectRouteActionV5 ControlAction(ProductionDirectPathFenceV5 path,
            ProductionDirectRouteActionKindV5 kind, ulong directFrame = 0, bool accepted = false,
            bool echo = false, uint carrier = 0, uint reason = 0)
        {
            var hasBarrier = kind is ProductionDirectRouteActionKindV5.Accept or
                ProductionDirectRouteActionKindV5.Commit;
            return new TermLinkDirectRouteActionV5
            {
                AbiVersion = 5, StructSize = 200, Path = NativePath(path), Kind = (uint)kind,
                Direction = 1, SessionId = new byte[16], DirectFrameSequence = directFrame,
                BarrierId = hasBarrier
                    ? GuidBytes(Guid.Parse("40000000-0000-0000-0000-000000000006"))
                    : new byte[16],
                BarrierSha256 = hasBarrier ? Enumerable.Repeat((byte)7, 32).ToArray() : new byte[32],
                Accepted = accepted ? 1u : 0u, IsEcho = echo ? 1u : 0u,
                Carrier = carrier, Reason = reason, Reserved = new ulong[2]
            };
        }

        private static TermLinkDirectRouteActionV5 SessionCarrierAction(
            ProductionDirectPathFenceV5 path,
            Guid session,
            ProductionDirectCarrierV5 carrier) => new()
        {
            AbiVersion = 5, StructSize = 200, Path = NativePath(path),
            Kind = (uint)ProductionDirectRouteActionKindV5.SessionCarrier,
            Direction = (uint)ProductionDirectDirectionV5.PcToAndroid,
            SessionId = GuidBytes(session), BarrierId = new byte[16],
            BarrierSha256 = new byte[32], Carrier = (uint)carrier,
            Reserved = new ulong[2]
        };

        private static TermLinkDirectEventV5 Event(ProductionDirectPathFenceV5 path,
            ProductionDirectNativeEventKindV5 kind, int status = 0, uint flags = 0,
            ulong frame = 0, uint payload = 0, uint released = 0) => new()
        {
            AbiVersion = 5, StructSize = 80, Kind = (uint)kind, Status = status, Flags = flags,
            PathEpoch = path.PathEpoch, AttemptId = GuidBytes(path.AttemptId), FrameSequence = frame,
            PayloadBytes = payload, ReleasedFrames = released, Reserved = new ulong[2]
        };

        private static ProductionDirectNativeBoundaryV5 Boundary(FakeApi api,
            ProductionDirectPathFenceV5 path) => new(enabled: true, queueCapacityFrames: 16,
                queueCapacityBytes: 4096, initialPath: path, loader: new FakeLoader(api));
        private static ProductionDirectPathFenceV5 Path(ulong epoch) => new()
        {
            PathEpoch = epoch,
            ConnectionId = Guid.Parse("10000000-0000-0000-0000-" + epoch.ToString("x12")),
            AttemptId = Guid.Parse("20000000-0000-0000-0000-" + epoch.ToString("x12"))
        };
        private static TermLinkDirectPathV5 NativePath(ProductionDirectPathFenceV5 value) => new()
        {
            AbiVersion = 5, StructSize = 64, PathEpoch = value.PathEpoch,
            ConnectionId = GuidBytes(value.ConnectionId), AttemptId = GuidBytes(value.AttemptId),
            Reserved = new ulong[2]
        };
        private static int Offset<T>(string field) where T : struct =>
            checked((int)Marshal.OffsetOf<T>(field));
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromNativeBytes(byte[] value) =>
            Guid.ParseExact(Convert.ToHexString(value), "N");
        private static bool PathBuffersAreZero(TermLinkDirectPathV5 value) =>
            IsZero(value.ConnectionId) && IsZero(value.AttemptId) && IsZero(value.Reserved);
        private static bool PathInfoBuffersAreZero(TermLinkDirectPathInfoV5 value) =>
            PathBuffersAreZero(value.Path) && IsZero(value.DirectPathBinding) && IsZero(value.Reserved);
        private static bool RouteActionBuffersAreZero(TermLinkDirectRouteActionV5 value) =>
            PathBuffersAreZero(value.Path) && IsZero(value.SessionId) && IsZero(value.BarrierId) &&
            IsZero(value.BarrierSha256) && IsZero(value.Reserved);
        private static bool IsZero(byte[] value) => value != null && value.All(item => item == 0);
        private static bool IsZero(ulong[] value) => value != null && value.All(item => item == 0);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeLoader : IProductionDirectNativeLoaderV5
        {
            private readonly IProductionDirectNativeApiV5 api;
            private int calls;
            internal FakeLoader(IProductionDirectNativeApiV5 api) => this.api = api;
            internal int LoadCalls => calls;
            public bool TryLoad(out IProductionDirectNativeApiV5 value, out string error)
            {
                Interlocked.Increment(ref calls); value = api; error = string.Empty; return value != null;
            }
        }

        private sealed class FakeApi : IProductionDirectNativeApiV5
        {
            private int activeOperation;
            private int createCalls;
            private int pathCalls;
            private int disposeCalls;
            private int destroyCalls;
            private int envelopeProbeCalls;
            private int signingProbeCalls;
            private int actionProbeCalls;
            private int destroyRacedOperation;
            private int submitSignatureCalls;

            internal uint ReportedAbi { get; init; } = 5;
            internal int RouteStatus { get; set; }
            internal int TickIngressStatus { get; set; }
            internal int ReceiveAcceptStatus { get; init; }
            internal int PathInfoStatus { get; init; }
            internal bool PathInfoReady { get; init; }
            internal bool CorruptPathInfo { get; init; }
            internal bool ThrowPathInfo { get; init; }
            internal bool ThrowOnActionPayloadRead { get; init; }
            internal bool CorruptEmptyEvent { get; init; }
            internal ManualResetEventSlim OperationEntered { get; init; }
            internal ManualResetEventSlim OperationRelease { get; init; }
            internal ConcurrentQueue<(ulong Id, byte[] Bytes)> Signing { get; } = new();
            internal ConcurrentQueue<(ulong Sequence, byte[] Bytes)> Incoming { get; } = new();
            internal ConcurrentQueue<(TermLinkDirectRouteActionV5 Action, byte[] Bytes)> Actions { get; } = new();
            internal ConcurrentQueue<TermLinkDirectEventV5> Events { get; } = new();
            internal TermLinkDirectCreateV5 LastCreate { get; private set; }
            internal TermLinkDirectSignatureV5 LastSignature { get; private set; }
            internal TermLinkDirectCursorV5 LastCursor { get; private set; }
            internal TermLinkDirectCursorV5 LastDeregister { get; private set; }
            internal TermLinkDirectPathV5 LastPathInfoInput { get; private set; }
            internal TermLinkDirectPathInfoV5 LastPathInfoOutput { get; private set; }
            internal TermLinkDirectRouteActionV5 LastPolledAction { get; private set; }
            internal byte[] LastRoute { get; private set; } = Array.Empty<byte>();
            internal byte[] LastIngress { get; private set; } = Array.Empty<byte>();
            internal int CreateCalls => createCalls;
            internal int PathCalls => pathCalls;
            internal int DisposeCalls => disposeCalls;
            internal int DestroyCalls => destroyCalls;
            internal int EnvelopeProbeCalls => envelopeProbeCalls;
            internal int SigningProbeCalls => signingProbeCalls;
            internal int ActionProbeCalls => actionProbeCalls;
            internal bool DestroyRacedOperation => destroyRacedOperation != 0;
            internal int SubmitSignatureCalls => submitSignatureCalls;

            public uint AbiVersion() => ReportedAbi;
            public IntPtr Create(ref TermLinkDirectCreateV5 create)
            {
                Interlocked.Increment(ref createCalls); LastCreate = create; return new IntPtr(1);
            }
            public int PathUsage(IntPtr handle, ref TermLinkDirectPathV5 path)
            {
                Interlocked.Increment(ref pathCalls); return 0;
            }
            public int Attach(IntPtr handle, ref TermLinkDirectPathV5 path) => -6;
            public int Promote(IntPtr handle, IntPtr v4Handle, ref TermLinkDirectPromoteV5 promote) => 0;
            public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV5 signature)
            {
                Interlocked.Increment(ref submitSignatureCalls);
                var owned = signature;
                owned.Signature = signature.Signature.ToArray();
                owned.Reserved = signature.Reserved.ToArray();
                LastSignature = owned;
                return 0;
            }
            public int ReceiveSigningPayload(IntPtr handle, ref TermLinkDirectPathV5 path,
                ref ulong requestId, IntPtr output, uint capacity, ref uint required) =>
                Read(Signing, ref requestId, output, capacity, ref required, ref signingProbeCalls);
            public int PathInfo(IntPtr handle, ref TermLinkDirectPathV5 path,
                ref TermLinkDirectPathInfoV5 output)
            {
                LastPathInfoInput = path;
                output = new TermLinkDirectPathInfoV5
                {
                    AbiVersion = 5,
                    StructSize = 128,
                    Path = new TermLinkDirectPathV5
                    {
                        AbiVersion = path.AbiVersion,
                        StructSize = path.StructSize,
                        PathEpoch = path.PathEpoch,
                        ConnectionId = path.ConnectionId.ToArray(),
                        AttemptId = path.AttemptId.ToArray(),
                        Reserved = path.Reserved.ToArray()
                    },
                    PathKind = PathInfoReady ? 1u : 0u,
                    Ready = PathInfoReady ? 1u : 0u,
                    DirectPathBinding = PathInfoReady
                        ? Enumerable.Repeat((byte)0x5a, 32).ToArray()
                        : new byte[32],
                    Reserved = new ulong[2]
                };
                if (CorruptPathInfo) output.Reserved[0] = 1;
                LastPathInfoOutput = output;
                if (ThrowPathInfo) throw new InvalidOperationException("PathInfo fixture failure.");
                return PathInfoStatus;
            }
            public int ReceiveEnvelope(IntPtr handle, ref TermLinkDirectPathV5 path,
                ref ulong sequence, IntPtr output, uint capacity, ref uint required) =>
                Read(Incoming, ref sequence, output, capacity, ref required, ref envelopeProbeCalls);
            public int StartProposal(IntPtr handle, ref TermLinkDirectProposalV5 proposal) => 0;
            public int ReceiveProposal(IntPtr handle, ref TermLinkDirectProposalV5 proposal) => 0;
            public int ReceiveAccept(IntPtr handle, ref TermLinkDirectTransitionV5 transition) =>
                ReceiveAcceptStatus;
            public int ReceiveCommit(IntPtr handle, ref TermLinkDirectTransitionV5 transition) => 0;
            public int CommitEchoSent(IntPtr handle, ref TermLinkDirectTransitionV5 transition) => 0;
            public int ReceiveFallback(IntPtr handle, ref TermLinkDirectTransitionV5 transition) => 0;
            public int Tick(IntPtr handle, ref TermLinkDirectTransitionV5 transition) => 0;
            public int UpdateAppliedCursor(IntPtr handle, ref TermLinkDirectCursorV5 cursor)
            {
                LastCursor = cursor; return 0;
            }
            public int DeregisterSession(IntPtr handle, ref TermLinkDirectCursorV5 session)
            {
                LastDeregister = new TermLinkDirectCursorV5
                {
                    AbiVersion = session.AbiVersion,
                    StructSize = session.StructSize,
                    Path = new TermLinkDirectPathV5
                    {
                        AbiVersion = session.Path.AbiVersion,
                        StructSize = session.Path.StructSize,
                        PathEpoch = session.Path.PathEpoch,
                        ConnectionId = session.Path.ConnectionId.ToArray(),
                        AttemptId = session.Path.AttemptId.ToArray(),
                        Reserved = session.Path.Reserved.ToArray()
                    },
                    Direction = session.Direction,
                    SessionId = session.SessionId.ToArray(),
                    HighestContiguous = session.HighestContiguous,
                    Reserved = session.Reserved.ToArray()
                };
                return 0;
            }
            public int RouteEnvelope(IntPtr handle, ref TermLinkDirectRouteEnvelopeV5 route,
                IntPtr envelope, uint envelopeBytes)
            {
                EnterOperation(); LastRoute = Copy(envelope, envelopeBytes); ExitOperation(); return RouteStatus;
            }
            public int PollRouteAction(IntPtr handle, ref TermLinkDirectRouteActionV5 action,
                IntPtr output, uint capacity, ref uint required)
            {
                if (!Actions.TryPeek(out var front)) { required = 0; return 1; }
                required = checked((uint)front.Bytes.Length);
                if (front.Bytes.Length > 0 && (output == IntPtr.Zero || capacity < front.Bytes.Length))
                {
                    Interlocked.Increment(ref actionProbeCalls); return -5;
                }
                Require(Actions.TryDequeue(out front), "Fake action queue changed.");
                action = front.Action;
                LastPolledAction = action;
                if (front.Bytes.Length > 0) Marshal.Copy(front.Bytes, 0, output, front.Bytes.Length);
                if (ThrowOnActionPayloadRead && output != IntPtr.Zero)
                    throw new InvalidOperationException("Route-action fixture failure.");
                return 0;
            }
            public int ArbitrateIngress(IntPtr handle, ref TermLinkDirectIngressV5 ingress,
                IntPtr envelope, uint envelopeBytes)
            {
                LastIngress = Copy(envelope, envelopeBytes); return 0;
            }
            public int TickIngress(IntPtr handle, ref TermLinkDirectPathV5 path, ulong nowMilliseconds) =>
                TickIngressStatus;
            public int PollEvent(IntPtr handle, ref TermLinkDirectEventV5 output)
            {
                if (!Events.TryDequeue(out var value))
                {
                    if (CorruptEmptyEvent) output.Kind = 12;
                    return 0;
                }
                output = value; return 1;
            }
            public int Cancel(IntPtr handle, ref TermLinkDirectPathV5 path) => 0;
            public int NetworkChanged(IntPtr handle, ref TermLinkDirectPathV5 path) => 0;
            public void Destroy(IntPtr handle)
            {
                if (Volatile.Read(ref activeOperation) != 0) Interlocked.Exchange(ref destroyRacedOperation, 1);
                Interlocked.Increment(ref destroyCalls);
            }
            public void Dispose() => Interlocked.Increment(ref disposeCalls);

            private static int Read(ConcurrentQueue<(ulong Id, byte[] Bytes)> queue, ref ulong id,
                IntPtr output, uint capacity, ref uint required, ref int probes)
            {
                if (!queue.TryPeek(out var front)) { required = 0; return 1; }
                required = checked((uint)front.Bytes.Length);
                if (output == IntPtr.Zero || capacity < front.Bytes.Length)
                {
                    Interlocked.Increment(ref probes); return -5;
                }
                Require(queue.TryDequeue(out front), "Fake read queue changed.");
                Marshal.Copy(front.Bytes, 0, output, front.Bytes.Length); id = front.Id; return 0;
            }
            private static byte[] Copy(IntPtr value, uint length)
            {
                var bytes = new byte[length]; Marshal.Copy(value, bytes, 0, bytes.Length); return bytes;
            }
            private void EnterOperation()
            {
                if (OperationEntered == null) return;
                Interlocked.Exchange(ref activeOperation, 1); OperationEntered.Set();
                OperationRelease.Wait(TimeSpan.FromSeconds(5));
            }
            private void ExitOperation()
            {
                if (OperationEntered != null) Interlocked.Exchange(ref activeOperation, 0);
            }
        }
    }
}

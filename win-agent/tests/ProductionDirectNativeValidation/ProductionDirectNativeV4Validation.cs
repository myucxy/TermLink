using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using TermLink.Agent;

namespace TermLink.ProductionDirectNative.Validation
{
    internal static class ProductionDirectNativeV4Validation
    {
        internal static void Validate()
        {
            ValidateLayoutAndCapabilityGate();
            ValidateCandidateBatchPairAndNominationFlow();
            ValidateGenerationAndSelectionFences();
            ValidateNominationRequiresCompletePlan();
            ValidateRealAbiLibraryIfRequested();
        }

        private static void ValidateLayoutAndCapabilityGate()
        {
            Require(ProductionDirectNativeAbiV4.TryValidateLayout(out var error),
                "ABI v4 layout failed at " + error + ".");
            Require(Marshal.SizeOf<TermLinkDirectBeginChecksV4>() == 4_656 &&
                    Marshal.OffsetOf<TermLinkDirectBeginChecksV4>(nameof(TermLinkDirectBeginChecksV4.Pairs)).ToInt32() == 2_320 &&
                    Marshal.SizeOf<TermLinkDirectEventV4>() == 2_224 &&
                    Marshal.OffsetOf<TermLinkDirectEventV4>(nameof(TermLinkDirectEventV4.Payload)).ToInt32() == 112,
                "ABI v4 exact size/offset contract drifted.");
            var abi3 = new FakeApi { ReportedAbi = 3 };
            Require(!ProductionDirectNativeCapabilityV4.IsUsable(new FakeLoader(abi3)) &&
                    abi3.CreateCalls == 0 && abi3.DisposeCalls == 1,
                "An ABI v3 library advertised multipair v2.");
            var abi4 = new FakeApi();
            Require(ProductionDirectNativeCapabilityV4.IsUsable(new FakeLoader(abi4)) &&
                    abi4.CreateCalls == 1 && abi4.DestroyCalls == 1 && abi4.DisposeCalls == 1,
                "A complete ABI v4 symbol boundary was not considered usable.");
        }

        private static void ValidateCandidateBatchPairAndNominationFlow()
        {
            var api = new FakeApi();
            using var boundary = new ProductionDirectNativeBoundaryV4(true, 32, new FakeLoader(api));
            var epoch = 7UL; var attempt = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            Require(boundary.Gather(Gather(epoch, attempt, expires)) == ProductionDirectNativeCommandResult.Accepted,
                "ABI v4 Gather was rejected.");
            Require(api.LastGather.StunCount == 0 && api.LastGather.StunServers.All(IsZeroSocket),
                "ABI v4 encoded header-bearing socket records into unused STUN slots.");
            var local = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.60.10"), 30_010,
                ProductionDirectCandidateKindV4.Host, expires, 100);
            api.Enqueue(CandidateBatchEvent(1, epoch, attempt, 1, new[] { local }, 1, false));
            var candidates = boundary.Poll(8);
            Require(candidates.Count == 1 && candidates[0].Candidates.Count == 1 && !candidates[0].Complete,
                "ABI v4 HOST-first candidate batch was rejected.");

            var pairs = Enumerable.Range(1, 4).Select(index => new ProductionDirectCandidatePairV4
            {
                Rank = checked((uint)index),
                Initiator = Candidate(Guid.NewGuid(), Guid.Empty,
                    IPAddress.Parse("192.168.60." + (20 + index)), 31_000 + index,
                    ProductionDirectCandidateKindV4.Host, expires, checked((uint)(200 - index))),
                Responder = local
            }).ToArray();
            var round = Guid.NewGuid();
            Require(boundary.BeginChecks(Begin(epoch, attempt, round, pairs, planComplete: false)) ==
                    ProductionDirectNativeCommandResult.Accepted && api.BeginCalls == 1 &&
                    api.LastBegin.PairCount == 4 && api.LastBegin.PlanComplete == 0,
                "ABI v4 blocked HOST checks until gathering completed.");

            var localReflexive = Candidate(Guid.NewGuid(), local.CandidateId,
                IPAddress.Parse("203.0.113.10"), 30_011,
                ProductionDirectCandidateKindV4.ServerReflexive, expires, 90);
            api.Enqueue(CandidateBatchEvent(2, epoch, attempt, 2, new[] { localReflexive }, 2, true));
            var trickle = boundary.Poll(8);
            Require(trickle.Count == 1 && trickle[0].Candidates.Single().Equals(localReflexive) &&
                    trickle[0].Complete,
                "ABI v4 did not retain its gathering owner after HOST checks began.");
            var remoteReflexive = Candidate(Guid.NewGuid(), pairs[0].Initiator.CandidateId,
                IPAddress.Parse("203.0.113.20"), 31_100,
                ProductionDirectCandidateKindV4.ServerReflexive, expires, 80);
            var fifth = new ProductionDirectCandidatePairV4
            { Rank = 5, Initiator = remoteReflexive, Responder = localReflexive };
            Require(boundary.AppendCheckPairs(new ProductionDirectAppendPairsRequestV4
                {
                    Epoch = epoch, AttemptId = attempt, CheckRoundId = round,
                    BatchSequence = 2, TotalPairCount = 5, PlanComplete = true,
                    Pairs = new[] { fifth }
                }) == ProductionDirectNativeCommandResult.Accepted && api.AppendCalls == 1 &&
                    api.LastAppend.PairCount == 1 && api.LastAppend.TotalPairCount == 5 &&
                    api.LastAppend.PlanComplete == 1,
                "ABI v4 did not append the later server-reflexive pair to the HOST check owner.");
            api.Enqueue(PairValidatedEvent(3, epoch, attempt, round, fifth));
            var validated = boundary.Poll(4).Single();
            Require(validated.Pair.Equals(fifth) && validated.Pair.Rank == 5,
                "ABI v4 could not validate the fifth pair after four failures.");
            var nomination = Guid.NewGuid();
            Require(boundary.CommitNomination(new ProductionDirectCommitRequestV4
                {
                    Epoch = epoch, AttemptId = attempt, CheckRoundId = round, NominationId = nomination,
                    WinningPair = fifth, Role = ProductionDirectNativeRole.Responder,
                    PeerCertificateSha256 = Enumerable.Repeat((byte)0x41, 32).ToArray(),
                    PeerDeviceSigningPublicKey = Enumerable.Repeat((byte)0x42, 32).ToArray(),
                    LocalProofNonce = Enumerable.Repeat((byte)0x43, 32).ToArray(),
                    NowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }) == ProductionDirectNativeCommandResult.Accepted && api.CommitCalls == 1 &&
                    api.LastCommit.SelectionConfirmed == 1 && api.LastCommit.WinningPair.Rank == 5,
                "ABI v4 committed a pair other than the exact validated winner.");
            api.Enqueue(NominationReadyEvent(4, epoch, attempt, round, nomination, fifth));
            api.Enqueue(ShadowReadyEvent(5, epoch, attempt, round, nomination, fifth,
                Enumerable.Repeat((byte)0x66, 32).ToArray()));
            var ready = boundary.Poll(8);
            Require(ready.Count == 2 && ready[0].Kind == ProductionDirectNativeEventKindV4.NominationReady &&
                    ready[1].Kind == ProductionDirectNativeEventKindV4.ShadowReady &&
                    ready[1].DirectPathBinding.All(value => value == 0x66) &&
                    !boundary.Snapshot.DirectRoutingPermitted,
                "ABI v4 nomination/shadow-ready flow escaped the shadow gate.");
        }

        private static void ValidateGenerationAndSelectionFences()
        {
            var relayGeneration = 91UL;
            var api = new FakeApi();
            using var boundary = new ProductionDirectNativeBoundaryV4(true, 8, new FakeLoader(api));
            var attempt = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            boundary.Gather(Gather(1, attempt, expires));
            var local = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.70.10"), 30_100,
                ProductionDirectCandidateKindV4.Host, expires, 10);
            api.Enqueue(CandidateBatchEvent(2, 1, attempt, 1, new[] { local }, 1, true));
            Require(boundary.Poll().Count == 0 && !boundary.Snapshot.NativeAvailable &&
                    relayGeneration == 91 && !boundary.Snapshot.DirectRoutingPermitted,
                "A native generation gap affected Relay identity or escaped fail-closed handling.");

            var secondApi = new FakeApi();
            using var second = new ProductionDirectNativeBoundaryV4(true, 8, new FakeLoader(secondApi));
            var secondAttempt = Guid.NewGuid();
            second.Gather(Gather(2, secondAttempt, expires));
            var secondLocal = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.60.10"), 30_100,
                ProductionDirectCandidateKindV4.Host, expires, 10);
            secondApi.Enqueue(CandidateBatchEvent(1, 2, secondAttempt, 1, new[] { secondLocal }, 1, true));
            var candidateEvents = second.Poll();
            Require(candidateEvents.Count == 1 && candidateEvents[0].Candidates.Count == 1 &&
                    second.Snapshot.NativeAvailable && second.Snapshot.Fault == ProductionDirectNativeFault.None,
                "A fresh attempt did not accept its advertised local HOST candidate.");
            var pair = new ProductionDirectCandidatePairV4
            {
                Rank = 1,
                Initiator = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.60.20"), 31_100,
                    ProductionDirectCandidateKindV4.Host, expires, 10),
                Responder = secondLocal
            };
            var round = Guid.NewGuid();
            Require(second.BeginChecks(Begin(2, secondAttempt, round, new[] { pair })) ==
                    ProductionDirectNativeCommandResult.Accepted && secondApi.BeginCalls == 1 &&
                    second.Snapshot.NativeAvailable,
                "A fresh attempt did not reach native checks before nomination fencing.");
            secondApi.Enqueue(PairValidatedEvent(2, 2, secondAttempt, round, pair));
            var validationEvents = second.Poll();
            Require(validationEvents.Count == 1 &&
                    validationEvents[0].Kind == ProductionDirectNativeEventKindV4.PairValidated &&
                    second.Snapshot.NativeAvailable && second.Snapshot.NativeGeneration == 2,
                "The exact planned pair was not accepted before nomination fencing.");
            var changed = new ProductionDirectCandidatePairV4
            { Rank = pair.Rank, Initiator = pair.Initiator, Responder = Candidate(local.CandidateId, Guid.Empty,
                secondLocal.Endpoint.Address, secondLocal.Endpoint.Port, secondLocal.Kind, expires, secondLocal.Priority + 1) };
            var nominationResult = second.CommitNomination(new ProductionDirectCommitRequestV4
                {
                    Epoch = 2, AttemptId = secondAttempt, CheckRoundId = round, NominationId = Guid.NewGuid(),
                    WinningPair = changed, Role = ProductionDirectNativeRole.Responder,
                    PeerCertificateSha256 = new byte[32], PeerDeviceSigningPublicKey = new byte[32],
                    LocalProofNonce = new byte[32], NowMilliseconds = 1
                });
            Require(nominationResult == ProductionDirectNativeCommandResult.RelayFallback && secondApi.CommitCalls == 0 &&
                    !second.Snapshot.NativeAvailable &&
                    second.Snapshot.Fault == ProductionDirectNativeFault.InvalidRequest &&
                    second.Snapshot.Reason == ProductionDirectNativeReason.InvalidAbi &&
                    second.Snapshot.NativeGeneration == 2 && relayGeneration == 91,
                "A stale/mismatched nomination crossed the native boundary or changed Relay generation.");
        }

        private static void ValidateNominationRequiresCompletePlan()
        {
            var api = new FakeApi();
            using var boundary = new ProductionDirectNativeBoundaryV4(true, 8, new FakeLoader(api));
            var attempt = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            Require(boundary.Gather(Gather(3, attempt, expires)) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "The incomplete-plan nomination fixture could not gather.");
            var local = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.60.10"),
                30_200, ProductionDirectCandidateKindV4.Host, expires, 100);
            api.Enqueue(CandidateBatchEvent(1, 3, attempt, 1, new[] { local }, 1, true));
            Require(boundary.Poll().Count == 1,
                "The incomplete-plan nomination fixture did not publish its HOST candidate.");
            var pair = new ProductionDirectCandidatePairV4
            {
                Rank = 1,
                Initiator = Candidate(Guid.NewGuid(), Guid.Empty, IPAddress.Parse("192.168.60.20"),
                    31_200, ProductionDirectCandidateKindV4.Host, expires, 99),
                Responder = local
            };
            var round = Guid.NewGuid();
            Require(boundary.BeginChecks(Begin(3, attempt, round, new[] { pair }, planComplete: false)) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "The incomplete HOST-first plan did not start checks.");
            api.Enqueue(PairValidatedEvent(2, 3, attempt, round, pair));
            Require(boundary.Poll().Single().Kind == ProductionDirectNativeEventKindV4.PairValidated,
                "The incomplete-plan fixture did not observe its earlier validated pair.");
            Require(boundary.CommitNomination(new ProductionDirectCommitRequestV4
                {
                    Epoch = 3,
                    AttemptId = attempt,
                    CheckRoundId = round,
                    NominationId = Guid.NewGuid(),
                    WinningPair = pair,
                    Role = ProductionDirectNativeRole.Responder,
                    PeerCertificateSha256 = Enumerable.Repeat((byte)0x71, 32).ToArray(),
                    PeerDeviceSigningPublicKey = Enumerable.Repeat((byte)0x72, 32).ToArray(),
                    LocalProofNonce = Enumerable.Repeat((byte)0x73, 32).ToArray(),
                    NowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }) == ProductionDirectNativeCommandResult.RelayFallback && api.CommitCalls == 0 &&
                    !boundary.Snapshot.DirectRoutingPermitted,
                "A validated earlier pair was nominated before the final pair plan reached native.");
        }

        private static void ValidateRealAbiLibraryIfRequested()
        {
            var path = Environment.GetEnvironmentVariable("TERMLINK_DIRECT_NATIVE_TEST_DLL");
            if (string.IsNullOrWhiteSpace(path)) return;
            var loader = new ProductionDirectNativeLibraryLoaderV4(path);
            Require(ProductionDirectNativeCapabilityV4.IsUsable(loader),
                "The real native DLL did not expose a complete usable ABI v4 symbol boundary.");
            var address = ActiveAddress();
            var initiatorAttempt = Guid.NewGuid();
            var responderAttempt = Guid.NewGuid();
            const ulong epoch = 7;
            const long nowMilliseconds = 1_900_000_060_000;
            const long candidateExpiresAtMilliseconds = 1_900_000_110_000;
            using var initiator = new ProductionDirectNativeBoundaryV4(
                true, 32, new ProductionDirectNativeLibraryLoaderV4(path));
            using var responder = new ProductionDirectNativeBoundaryV4(
                true, 32, new ProductionDirectNativeLibraryLoaderV4(path));
            Require(initiator.Gather(RealGather(epoch, initiatorAttempt, address,
                            candidateExpiresAtMilliseconds)) == ProductionDirectNativeCommandResult.Accepted &&
                    responder.Gather(RealGather(epoch, responderAttempt, address,
                            candidateExpiresAtMilliseconds)) == ProductionDirectNativeCommandResult.Accepted &&
                    initiator.Snapshot.NativeAvailable && responder.Snapshot.NativeAvailable,
                "The real ABI v4 DLL did not accept both independent Gather owners.");

            var initiatorGather = AwaitRealIdentityAndHost(initiator, address, "initiator");
            var responderGather = AwaitRealIdentityAndHost(responder, address, "responder");
            var pair = new ProductionDirectCandidatePairV4
            {
                Rank = 1,
                Initiator = initiatorGather.Host,
                Responder = responderGather.Host
            };
            var checkRound = Guid.NewGuid();
            var ticket = Base64Url(
                "CAESEAARIjNEVUZ3iJmqu8zd7v8aEBAhMkNUZUdoiZqrvM3e7_AiECAxQlNkdUhpipusvc7f8AEqEDBBUmN0hUlqi5ytvs_g8QIyEEBRYnOElUprjJ2uv9Dh8gM4B0CA8MyGpjdIwJnUhqY3UgEBWhAAAQIDBAUGBwgJCgsMDQ4PYhBkaXJlY3QtdjEtdmVjdG9yakCwJTg3vrjJ8h6M6z4BR3XkogSgH3TQI4jp2mBhQTUyJWcO36MFe3MVh3Zxk-uAFShR9t2MynoQJEk4LrR53KkI");
            var serverSigningKey = Base64Url("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg");
            var initiatorDevice = Guid.Parse("20314253-6475-4869-8a9b-acbdcedff001");
            var responderDevice = Guid.Parse("30415263-7485-496a-8b9c-adbecfe0f102");
            var connection = Guid.Parse("40516273-8495-4a6b-8c9d-aebfd0e1f203");
            var ticketId = Guid.Parse("00112233-4455-4677-8899-aabbccddeeff");
            var nonce = Enumerable.Repeat((byte)0x0d, 16).ToArray();
            Require(initiator.BeginChecks(RealBegin(
                        epoch, initiatorAttempt, connection, initiatorDevice, responderDevice,
                        ProductionDirectNativeRole.Initiator, ticket, ticketId, checkRound, nonce,
                        serverSigningKey, Enumerable.Repeat((byte)0x03, 32).ToArray(),
                        Enumerable.Repeat((byte)0x04, 32).ToArray(), pair, nowMilliseconds)) ==
                    ProductionDirectNativeCommandResult.Accepted &&
                    responder.BeginChecks(RealBegin(
                        epoch, responderAttempt, connection, responderDevice, initiatorDevice,
                        ProductionDirectNativeRole.Responder, ticket, ticketId, checkRound, nonce,
                        serverSigningKey, Enumerable.Repeat((byte)0x04, 32).ToArray(),
                        Enumerable.Repeat((byte)0x03, 32).ToArray(), pair, nowMilliseconds)) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "The real ABI v4 DLL rejected a ticket-bound two-handle check round.");

            var initiatorEvents = new List<ProductionDirectNativeEventV4>();
            var responderEvents = new List<ProductionDirectNativeEventV4>();
            var validationDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < validationDeadline &&
                   (!HasExactValidatedPair(initiatorEvents, checkRound, pair) ||
                    !HasExactValidatedPair(responderEvents, checkRound, pair)))
            {
                initiatorEvents.AddRange(initiator.Poll(32));
                responderEvents.AddRange(responder.Poll(32));
                Thread.Sleep(5);
            }
            Require(HasExactValidatedPair(initiatorEvents, checkRound, pair) &&
                    HasExactValidatedPair(responderEvents, checkRound, pair) &&
                    initiatorEvents.Where(value => value.Kind == ProductionDirectNativeEventKindV4.PairValidated)
                        .All(value => value.ProbeAttempts is >= 1 and <= 4) &&
                    responderEvents.Where(value => value.Kind == ProductionDirectNativeEventKindV4.PairValidated)
                        .All(value => value.ProbeAttempts is >= 1 and <= 4) &&
                    initiator.Snapshot.NativeAvailable && responder.Snapshot.NativeAvailable &&
                    !initiator.Snapshot.DirectRoutingPermitted && !responder.Snapshot.DirectRoutingPermitted,
                "The real ABI v4 DLL did not authenticate both probe directions with 1..4 attempts.");

            var nomination = Guid.NewGuid();
            Require(initiator.CommitNomination(RealCommit(
                        epoch, initiatorAttempt, checkRound, nomination, pair,
                        ProductionDirectNativeRole.Initiator,
                        SHA256.HashData(responderGather.Certificate), nowMilliseconds, 0x51, 0x61)) ==
                    ProductionDirectNativeCommandResult.Accepted &&
                    responder.CommitNomination(RealCommit(
                        epoch, responderAttempt, checkRound, nomination, pair,
                        ProductionDirectNativeRole.Responder,
                        SHA256.HashData(initiatorGather.Certificate), nowMilliseconds, 0x52, 0x62)) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "The real ABI v4 DLL rejected the exact validated nomination.");
            var nominationDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < nominationDeadline &&
                   (!HasExactNomination(initiatorEvents, checkRound, nomination, pair) ||
                    !HasExactNomination(responderEvents, checkRound, nomination, pair)))
            {
                initiatorEvents.AddRange(initiator.Poll(32));
                responderEvents.AddRange(responder.Poll(32));
                Thread.Sleep(5);
            }
            Require(HasExactNomination(initiatorEvents, checkRound, nomination, pair) &&
                    HasExactNomination(responderEvents, checkRound, nomination, pair) &&
                    !initiator.Snapshot.DirectRoutingPermitted && !responder.Snapshot.DirectRoutingPermitted,
                "The real ABI v4 DLL did not retain the exact pair through nomination.");
            initiator.Cancel(epoch, initiatorAttempt);
            responder.Cancel(epoch, responderAttempt);
        }

        private static ProductionDirectGatherRequestV4 RealGather(
            ulong epoch, Guid attempt, IPAddress address, long expires) => new()
        {
            Epoch = epoch,
            AttemptId = attempt,
            LocalBind = new ProductionDirectNativeEndpoint(address, 0),
            AdvertisedLocalAddress = address,
            CandidateExpiresAtMilliseconds = expires,
            GatherTimeoutMilliseconds = 500
        };

        private static (byte[] Certificate, ProductionDirectCandidateV4 Host) AwaitRealIdentityAndHost(
            ProductionDirectNativeBoundaryV4 boundary, IPAddress address, string side)
        {
            byte[] certificate = null;
            ProductionDirectCandidateV4 host = null;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && (certificate == null || host == null))
            {
                foreach (var value in boundary.Poll(32))
                {
                    if (value.Kind == ProductionDirectNativeEventKindV4.IdentityReady &&
                        value.OpaquePayload?.Length is > 0 and <= 2_048)
                        certificate = value.OpaquePayload;
                    if (value.Kind == ProductionDirectNativeEventKindV4.CandidateBatch)
                        host ??= value.Candidates.FirstOrDefault(candidate =>
                            candidate.Kind == ProductionDirectCandidateKindV4.Host &&
                            candidate.Endpoint.Address.Equals(address));
                }
                if (certificate == null || host == null) Thread.Sleep(5);
            }
            Require(certificate != null && host != null && boundary.Snapshot.NativeAvailable &&
                    !boundary.Snapshot.DirectRoutingPermitted,
                "The real ABI v4 " + side + " did not produce owned identity/HOST events after Gather.");
            return (certificate, host);
        }

        private static ProductionDirectBeginChecksRequestV4 RealBegin(
            ulong epoch, Guid attempt, Guid connection, Guid localDevice, Guid peerDevice,
            ProductionDirectNativeRole role, byte[] ticket, Guid ticketId, Guid checkRound,
            byte[] nonce, byte[] serverSigningKey, byte[] sendKey, byte[] receiveKey,
            ProductionDirectCandidatePairV4 pair, long nowMilliseconds) => new()
        {
            Epoch = epoch,
            AttemptId = attempt,
            ConnectionId = connection,
            LocalDeviceId = localDevice,
            PeerDeviceId = peerDevice,
            NowMilliseconds = nowMilliseconds,
            TimeoutMilliseconds = 5_000,
            Role = role,
            Ticket = ticket,
            TicketId = ticketId,
            CheckRoundId = checkRound,
            ConnectivityNonce = nonce,
            ServerSigningPublicKey = serverSigningKey,
            LocalSendProbeHmacKey = sendKey,
            PeerReceiveProbeHmacKey = receiveKey,
            BatchSequence = 1,
            TotalPairCount = 1,
            PlanComplete = true,
            Pairs = new[] { pair }
        };

        private static ProductionDirectCommitRequestV4 RealCommit(
            ulong epoch, Guid attempt, Guid checkRound, Guid nomination,
            ProductionDirectCandidatePairV4 pair, ProductionDirectNativeRole role,
            byte[] peerCertificateSha256, long nowMilliseconds, byte signingKeyByte,
            byte proofNonceByte) => new()
        {
            Epoch = epoch,
            AttemptId = attempt,
            CheckRoundId = checkRound,
            NominationId = nomination,
            WinningPair = pair,
            Role = role,
            TimeoutMilliseconds = 5_000,
            PeerCertificateSha256 = peerCertificateSha256,
            PeerDeviceSigningPublicKey = Enumerable.Repeat(signingKeyByte, 32).ToArray(),
            LocalProofNonce = Enumerable.Repeat(proofNonceByte, 32).ToArray(),
            NowMilliseconds = nowMilliseconds
        };

        private static bool HasExactValidatedPair(
            IEnumerable<ProductionDirectNativeEventV4> events, Guid checkRound,
            ProductionDirectCandidatePairV4 pair) => events.Any(value =>
                value.Kind == ProductionDirectNativeEventKindV4.PairValidated &&
                value.CheckRoundId == checkRound && value.Pair?.Equals(pair) == true &&
                value.ProbeAttempts is >= 1 and <= 4);

        private static bool HasExactNomination(
            IEnumerable<ProductionDirectNativeEventV4> events, Guid checkRound, Guid nomination,
            ProductionDirectCandidatePairV4 pair) => events.Any(value =>
                value.Kind == ProductionDirectNativeEventKindV4.NominationReady &&
                value.CheckRoundId == checkRound && value.NominationId == nomination &&
                value.Pair?.Equals(pair) == true);

        private static byte[] Base64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            return Convert.FromBase64String(normalized);
        }

        private static IPAddress ActiveAddress() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(value => value.GetIPProperties().UnicastAddresses)
            .Select(value => value.Address)
             .First(value => value.AddressFamily == AddressFamily.InterNetwork &&
                 ProductionDirectCandidateCollector.IsAllowedHostAddress(value));

        private static ProductionDirectGatherRequestV4 Gather(ulong epoch, Guid attempt, long expires) => new()
        {
            Epoch = epoch, AttemptId = attempt, LocalBind = new ProductionDirectNativeEndpoint(
                IPAddress.Parse(epoch == 1 ? "192.168.70.10" : "192.168.60.10"), 0),
            AdvertisedLocalAddress = IPAddress.Parse(epoch == 1 ? "192.168.70.10" : "192.168.60.10"),
            CandidateExpiresAtMilliseconds = expires, GatherTimeoutMilliseconds = 500
        };

        private static ProductionDirectBeginChecksRequestV4 Begin(ulong epoch, Guid attempt,
            Guid round, IReadOnlyList<ProductionDirectCandidatePairV4> pairs,
            bool planComplete = true) => new()
        {
            Epoch = epoch, AttemptId = attempt, ConnectionId = Guid.NewGuid(), LocalDeviceId = Guid.NewGuid(),
            PeerDeviceId = Guid.NewGuid(), NowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Role = ProductionDirectNativeRole.Responder, Ticket = Enumerable.Range(1, 128).Select(v => (byte)v).ToArray(),
            TicketId = Guid.NewGuid(), CheckRoundId = round,
            ConnectivityNonce = Enumerable.Repeat((byte)0x31, 16).ToArray(),
            ServerSigningPublicKey = Enumerable.Repeat((byte)0x21, 32).ToArray(),
            LocalSendProbeHmacKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            PeerReceiveProbeHmacKey = Enumerable.Repeat((byte)0x23, 32).ToArray(),
            BatchSequence = 1, TotalPairCount = checked((uint)pairs.Count),
            PlanComplete = planComplete, Pairs = pairs
        };

        private static ProductionDirectCandidateV4 Candidate(Guid id, Guid related, IPAddress address,
            int port, ProductionDirectCandidateKindV4 kind, long expires, uint priority) => new()
        { CandidateId = id, RelatedCandidateId = related, Endpoint = new ProductionDirectNativeEndpoint(address, port),
          Kind = kind, Priority = priority, NetworkCost = 0, ExpiresAtMilliseconds = expires };

        private static TermLinkDirectEventV4 CandidateBatchEvent(ulong generation, ulong epoch, Guid attempt,
            uint sequence, IReadOnlyList<ProductionDirectCandidateV4> candidates, uint total, bool complete)
        {
            var values = Enumerable.Range(0, 8).Select(_ => ZeroCandidate()).ToArray();
            for (var index = 0; index < candidates.Count; index++) values[index] = EncodeCandidate(candidates[index]);
            return Event(ProductionDirectNativeEventKindV4.CandidateBatch, ProductionDirectNativeAbiV4.CandidateBatchSize,
                generation, epoch, attempt, StructBytes(new TermLinkDirectCandidateBatchV4
                { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CandidateBatchSize, Epoch = epoch,
                  AttemptId = GuidBytes(attempt), BatchSequence = sequence, CandidateCount = checked((uint)candidates.Count),
                  TotalCandidateCount = total, GatheringComplete = complete ? 1u : 0u,
                  Candidates = values, Reserved = new ulong[2] }));
        }

        private static TermLinkDirectEventV4 PairValidatedEvent(ulong generation, ulong epoch, Guid attempt,
            Guid round, ProductionDirectCandidatePairV4 pair) => Event(
                ProductionDirectNativeEventKindV4.PairValidated, ProductionDirectNativeAbiV4.PairValidatedSize,
                generation, epoch, attempt, StructBytes(new TermLinkDirectPairValidatedV4
                { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.PairValidatedSize, Epoch = epoch,
                  AttemptId = GuidBytes(attempt), CheckRoundId = GuidBytes(round), Pair = EncodePair(pair),
                  RoundTripTimeMilliseconds = 12, ProbeAttempts = 4, Reserved = new ulong[2] }));

        private static TermLinkDirectEventV4 NominationReadyEvent(ulong generation, ulong epoch, Guid attempt,
            Guid round, Guid nomination, ProductionDirectCandidatePairV4 pair) => Event(
                ProductionDirectNativeEventKindV4.NominationReady, ProductionDirectNativeAbiV4.NominationReadySize,
                generation, epoch, attempt, StructBytes(new TermLinkDirectNominationReadyV4
                { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.NominationReadySize, Epoch = epoch,
                  AttemptId = GuidBytes(attempt), CheckRoundId = GuidBytes(round), NominationId = GuidBytes(nomination),
                  WinningPair = EncodePair(pair), Role = 2, Reserved = new ulong[2] }));

        private static TermLinkDirectEventV4 ShadowReadyEvent(ulong generation, ulong epoch, Guid attempt,
            Guid round, Guid nomination, ProductionDirectCandidatePairV4 pair, byte[] binding) => Event(
                ProductionDirectNativeEventKindV4.ShadowReady, ProductionDirectNativeAbiV4.ShadowReadySize,
                generation, epoch, attempt, StructBytes(new TermLinkDirectShadowReadyV4
                { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.ShadowReadySize, Epoch = epoch,
                  AttemptId = GuidBytes(attempt), CheckRoundId = GuidBytes(round), NominationId = GuidBytes(nomination),
                  WinningPair = EncodePair(pair), DirectPathBinding = binding, Reserved = new ulong[2] }));

        private static TermLinkDirectEventV4 Event(ProductionDirectNativeEventKindV4 kind, int payloadSize,
            ulong generation, ulong epoch, Guid attempt, byte[] payload)
        {
            var buffer = new byte[ProductionDirectNativeAbiV4.EventPayloadSize]; Array.Copy(payload, buffer, payload.Length);
            return new TermLinkDirectEventV4
            { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.EventSize, Kind = (uint)kind,
              State = kind == ProductionDirectNativeEventKindV4.ShadowReady ? 6u : 3u, PayloadSize = checked((uint)payloadSize),
              Epoch = epoch, Generation = generation, AttemptId = GuidBytes(attempt),
              Endpoint = new TermLinkSocketAddressV4 { Ip = new byte[16], Reserved = new ulong[2] },
              Payload = buffer, Reserved = new ulong[4] };
        }

        private static TermLinkDirectCandidateV4 EncodeCandidate(ProductionDirectCandidateV4 value)
        {
            var bytes = value.Endpoint.Address.GetAddressBytes(); var address = TermLinkSocketAddressV4.Empty();
            Array.Copy(bytes, address.Ip, bytes.Length); address.IpLength = checked((uint)bytes.Length);
            address.Port = checked((uint)value.Endpoint.Port);
            return new TermLinkDirectCandidateV4
            { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CandidateSize,
              CandidateId = GuidBytes(value.CandidateId), RelatedCandidateId = GuidBytes(value.RelatedCandidateId),
              Endpoint = address, Kind = (uint)value.Kind, Carrier = 1, Priority = value.Priority,
              NetworkCost = value.NetworkCost, ExpiresAtMilliseconds = value.ExpiresAtMilliseconds,
              Reserved = new ulong[2] };
        }

        private static TermLinkDirectCandidateV4 ZeroCandidate() => new()
        { CandidateId = new byte[16], RelatedCandidateId = new byte[16], Endpoint = new TermLinkSocketAddressV4
          { Ip = new byte[16], Reserved = new ulong[2] }, Reserved = new ulong[2] };

        private static TermLinkDirectCandidatePairV4 EncodePair(ProductionDirectCandidatePairV4 value) => new()
        { AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CandidatePairSize, Rank = value.Rank,
          InitiatorCandidate = EncodeCandidate(value.Initiator), ResponderCandidate = EncodeCandidate(value.Responder),
          Reserved = new ulong[2] };

        private static byte[] StructBytes<T>(T value) where T : struct
        {
            var size = Marshal.SizeOf<T>(); var pointer = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(value, pointer, false); var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); return bytes; }
            finally { Marshal.DestroyStructure<T>(pointer); Marshal.FreeHGlobal(pointer); }
        }

        private static byte[] GuidBytes(Guid value) => value == Guid.Empty ? new byte[16] : Convert.FromHexString(value.ToString("N"));
        private static bool IsZeroSocket(TermLinkSocketAddressV4 value) =>
            value.AbiVersion == 0 && value.StructSize == 0 &&
            value.Ip?.All(item => item == 0) == true && value.IpLength == 0 && value.Port == 0 &&
            value.Reserved?.All(item => item == 0) == true;

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class FakeLoader : IProductionDirectNativeLoaderV4
        {
            private readonly IProductionDirectNativeApiV4 api;
            public FakeLoader(IProductionDirectNativeApiV4 api) { this.api = api; }
            public bool TryLoad(out IProductionDirectNativeApiV4 value, out string error)
            { value = api; error = string.Empty; return api != null; }
        }

        private sealed class FakeApi : IProductionDirectNativeApiV4
        {
            private readonly ConcurrentQueue<TermLinkDirectEventV4> events = new();
            public uint ReportedAbi { get; init; } = 4;
            public int CreateCalls { get; private set; }
            public int BeginCalls { get; private set; }
            public int AppendCalls { get; private set; }
            public int CommitCalls { get; private set; }
            public int DestroyCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public TermLinkDirectBeginChecksV4 LastBegin { get; private set; }
            public TermLinkDirectGatherV4 LastGather { get; private set; }
            public TermLinkDirectAppendCheckPairsV4 LastAppend { get; private set; }
            public TermLinkDirectCommitNominationV4 LastCommit { get; private set; }
            public uint AbiVersion() => ReportedAbi;
            public IntPtr Create(ref TermLinkDirectCreateV4 create) { CreateCalls++; return new IntPtr(CreateCalls); }
            public int Gather(IntPtr handle, ref TermLinkDirectGatherV4 gather)
            { LastGather = gather; return 0; }
            public int BeginChecks(IntPtr handle, ref TermLinkDirectBeginChecksV4 begin) { BeginCalls++; LastBegin = begin; return 0; }
            public int AppendCheckPairs(IntPtr handle, ref TermLinkDirectAppendCheckPairsV4 append)
            { AppendCalls++; LastAppend = append; return 0; }
            public int CommitNomination(IntPtr handle, ref TermLinkDirectCommitNominationV4 commit)
            { CommitCalls++; LastCommit = commit; return 0; }
            public int Cancel(IntPtr handle, ref TermLinkDirectControlV4 control) => 0;
            public int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV4 control) => 0;
            public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV4 signature) => 0;
            public int PollEvent(IntPtr handle, ref TermLinkDirectEventV4 output)
            { if (!events.TryDequeue(out var value)) return 0; output = value; return 1; }
            public void Destroy(IntPtr handle) => DestroyCalls++;
            public void Dispose() => DisposeCalls++;
            public void Enqueue(TermLinkDirectEventV4 value) => events.Enqueue(value);
        }
    }
}

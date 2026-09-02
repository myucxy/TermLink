using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Agent;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionDirectMultipairRuntimeValidation
    {
        internal static async Task ValidateAsync()
        {
            var now = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
            var pc = Guid.NewGuid(); var android = Guid.NewGuid(); var connection = Guid.NewGuid();
            var pairwise = Enumerable.Range(0, 32).Select(value => (byte)(0x40 + value)).ToArray();
            using var pcIdentity = DeviceIdentity.Generate();
            using var androidIdentity = DeviceIdentity.Generate();
            using var serverIdentity = DeviceIdentity.Generate();
            var config = Config(serverIdentity.SigningPublicKey);
            var ticket = Ticket(now, serverIdentity, Guid.NewGuid(), android, pc, connection);
            await using var signaling = new FakeSignaling(pc, pcIdentity, config, now.ToUnixTimeMilliseconds());
            var native = new FakeNativeV4(pcIdentity.SigningPublicKey, pairwise);
            var directSupervisor = new ProductionDirectTransportSupervisor(() => now.UtcDateTime);
            directSupervisor.ObserveRelayConnected(true);
            using var relay = new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences(), nextJitterMilliseconds: () => 0,
                utcNow: () => now.UtcDateTime);
            var relayGeneration = relay.MarkPhysicalConnected();
            await using var runtime = new ProductionDirectMultipairShadowRuntime(
                true, pc, ProductionAgentRuntime.DeviceKeyId(pc), pcIdentity, directSupervisor, () => signaling,
                _ => Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(new[]
                {
                    new ProductionDirectPeerAuthorization(android, pairwise,
                        androidIdentity.SigningPublicKey, 1)
                }),
                nativeFactory: () => native,
                platform: new FakePlatform(),
                delay: ShortDelay,
                clockMilliseconds: () => now.ToUnixTimeMilliseconds(),
                nativeCapability: () => true);
            Require(runtime.Start(), "The ABI v4 multipair runtime did not start.");
            runtime.ObserveRelayConnected(true);
            var drive = DriveAndroidAsync(runtime, signaling, native, ticket, android, pc, pairwise,
                now.ToUnixTimeMilliseconds());
            try { await drive.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Android v2 driver timed out at " + signaling.LastRuntimeState +
                    " runtime=" + runtime.Snapshot.State + "/" + runtime.Snapshot.ReasonCode, error);
            }
            await WaitUntilAsync(() => runtime.Snapshot.State == ProductionDirectShadowState.ShadowReadyNotRouted,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(native.BeginCalls == 1 && native.AppendCalls == 1 && native.CommitCalls == 1 &&
                    native.SignatureVerified &&
                    native.KeysVerified && native.CommittedRank == 5 && !runtime.Snapshot.DirectRoutingPermitted &&
                    directSupervisor.Snapshot().State == ProductionDirectPathState.ShadowReadyNotRouted &&
                    relay.Snapshot().PhysicalConnected && relay.Snapshot().PhysicalGeneration == relayGeneration &&
                    relay.Snapshot().ScheduledReconnects == 0,
                "Windows v4 responder did not complete the exact fifth-pair shadow flow.");

            native.InjectFailure();
            await WaitUntilAsync(() => runtime.Snapshot.State == ProductionDirectShadowState.RelayOnly &&
                runtime.Snapshot.ReasonCode == "DIRECT_NATIVE_FAILED", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(relay.Snapshot().PhysicalConnected && relay.Snapshot().PhysicalGeneration == relayGeneration &&
                    relay.Snapshot().ScheduledReconnects == 0 && directSupervisor.Snapshot().RelayConnected &&
                    native.DisposeCalls == 1,
                "An ABI v4 Direct fault recreated or disconnected the authoritative Relay.");
            await ValidatePrematureFinalPlanFailsClosedAsync(
                pcIdentity, androidIdentity, serverIdentity, pairwise, now).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(pairwise);

            await ValidateUnavailableNativeFailsClosedAsync(pc, pcIdentity).ConfigureAwait(false);
            await ValidateUnsupportedCapabilityFailsClosedAsync(pc, pcIdentity, android,
                androidIdentity, serverIdentity, now).ConfigureAwait(false);
        }

        private static async Task ValidateUnavailableNativeFailsClosedAsync(
            Guid pc, DeviceIdentity pcIdentity)
        {
            var signalingCalls = 0;
            var nativeCalls = 0;
            var peerCalls = 0;
            var supervisor = new ProductionDirectTransportSupervisor();
            await using var runtime = new ProductionDirectMultipairShadowRuntime(
                true, pc, ProductionAgentRuntime.DeviceKeyId(pc), pcIdentity, supervisor,
                () => { signalingCalls++; throw new InvalidOperationException(); },
                _ => { peerCalls++; return Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(
                    Array.Empty<ProductionDirectPeerAuthorization>()); },
                nativeFactory: () => { nativeCalls++; throw new InvalidOperationException(); },
                delay: ShortDelay,
                nativeCapability: () => false);
            runtime.Start();
            runtime.ObserveRelayConnected(true);
            await WaitUntilAsync(() => runtime.Snapshot.ReasonCode == "DIRECT_ABI_V4_UNAVAILABLE",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(signalingCalls == 0 && nativeCalls == 0 && peerCalls == 0 &&
                    !runtime.Snapshot.DirectRoutingPermitted,
                "An unusable ABI v4 library advertised multipair or started Direct work.");
        }

        private static async Task ValidatePrematureFinalPlanFailsClosedAsync(
            DeviceIdentity pcIdentity, DeviceIdentity androidIdentity, DeviceIdentity serverIdentity,
            byte[] pairwise, DateTimeOffset now)
        {
            var pc = Guid.NewGuid();
            var android = Guid.NewGuid();
            var ticket = Ticket(now, serverIdentity, Guid.NewGuid(), android, pc, Guid.NewGuid());
            var config = Config(serverIdentity.SigningPublicKey);
            await using var signaling = new FakeSignaling(
                pc, pcIdentity, config, now.ToUnixTimeMilliseconds());
            var native = new FakeNativeV4(pcIdentity.SigningPublicKey, pairwise);
            var supervisor = new ProductionDirectTransportSupervisor(() => now.UtcDateTime);
            await using var runtime = new ProductionDirectMultipairShadowRuntime(
                true, pc, ProductionAgentRuntime.DeviceKeyId(pc), pcIdentity, supervisor, () => signaling,
                _ => Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(new[]
                {
                    new ProductionDirectPeerAuthorization(
                        android, pairwise, androidIdentity.SigningPublicKey, 1)
                }),
                nativeFactory: () => native,
                platform: new FakePlatform(),
                delay: ShortDelay,
                clockMilliseconds: () => now.ToUnixTimeMilliseconds(),
                nativeCapability: () => true);
            runtime.Start();
            runtime.ObserveRelayConnected(true);

            var remote = Candidate(Guid.NewGuid(), "192.168.80.20", 42_000,
                ticket.ExpiresAtMs, 2_000_000_000);
            var offer = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                PathOffer = new DirectPathOfferV1
                {
                    Ticket = ticket.Clone(),
                    QuicCertificateSha256 = ByteString.CopyFrom(SHA256.HashData(
                        Encoding.ASCII.GetBytes("premature-plan-certificate"))),
                    GatheringComplete = false
                }
            };
            offer.PathOffer.Candidates.Add(remote);
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.PathOffer, 1, 0, now.ToUnixTimeMilliseconds(), offer));
            var answer = Decrypt(pairwise, await signaling.ReadOutboundAsync().WaitAsync(
                TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            var final = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                ConnectivityCheckBatchV2 = new DirectConnectivityCheckBatchV2
                {
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                    ConnectivityNonce = ByteString.CopyFrom(Enumerable.Repeat((byte)0x51, 16).ToArray()),
                    BatchSeq = 1,
                    PlanComplete = true
                }
            };
            final.ConnectivityCheckBatchV2.Pairs.Add(new DirectCandidatePairV1
            {
                InitiatorCandidateId = remote.CandidateId,
                ResponderCandidateId = answer.PathAnswer.Candidates.Single().CandidateId
            });
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.ConnectivityCheckBatchV2, 2, 1,
                now.ToUnixTimeMilliseconds(), final));
            await WaitUntilAsync(() => runtime.Snapshot.ReasonCode == "DIRECT_SIGNAL_PAYLOAD_REJECTED",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(native.BeginCalls == 0 && !runtime.Snapshot.DirectRoutingPermitted,
                "A final pair plan crossed into native before both candidate streams completed.");
        }

        private static async Task DriveAndroidAsync(ProductionDirectMultipairShadowRuntime runtime,
            FakeSignaling signaling, FakeNativeV4 native,
            DirectPathTicketV1 ticket, Guid android, Guid pc, byte[] pairwise, long now)
        {
            var remoteHosts = Enumerable.Range(0, 4).Select(index => Candidate(
                Guid.ParseExact((index + 1).ToString("x32"), "N"),
                "192.168.80." + (20 + index), 40_000 + index, ticket.ExpiresAtMs,
                checked((uint)(2_000_000_000 - index)))).ToArray();
            var remoteReflexive = Candidate(
                Guid.ParseExact("00000000000000000000000000000005", "N"),
                "203.0.113.20", 40_004, ticket.ExpiresAtMs, 1_999_999_996,
                DirectCandidateKindV1.ServerReflexive, remoteHosts[0].CandidateId);
            var offer = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                PathOffer = new DirectPathOfferV1
                {
                    Ticket = ticket.Clone(),
                    QuicCertificateSha256 = ByteString.CopyFrom(SHA256.HashData(
                        Encoding.ASCII.GetBytes("android-v4-certificate"))),
                    GatheringComplete = false
                }
            };
            offer.PathOffer.Candidates.Add(remoteHosts);
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.PathOffer, 1, 0, now, offer));

            var answerEnvelope = await signaling.ReadOutboundAsync().ConfigureAwait(false);
            var answer = Decrypt(pairwise, answerEnvelope);
            Require(answerEnvelope.SignalType == TransportSignalTypeV1.PathAnswer &&
                    answerEnvelope.SignalSeq == 1 && answerEnvelope.SignalAck == 1 &&
                    answerEnvelope.KeyId.Span.SequenceEqual(
                        Encoding.UTF8.GetBytes(ProductionAgentRuntime.DeviceKeyId(pc))) &&
                    answer.PathAnswer.Candidates.Count == 1 && !answer.PathAnswer.GatheringComplete,
                "Windows did not publish the HOST-first PathAnswer.");
            var responder = answer.PathAnswer.Candidates[0];
            var round = Guid.ParseExact("30303030303030303030303030303030", "N");
            var hostCheck = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                ConnectivityCheckBatchV2 = new DirectConnectivityCheckBatchV2
                {
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(round)),
                    ConnectivityNonce = ByteString.CopyFrom(Enumerable.Repeat((byte)0x31, 16).ToArray()),
                    BatchSeq = 1,
                    PlanComplete = false
                }
            };
            foreach (var candidate in remoteHosts) hostCheck.ConnectivityCheckBatchV2.Pairs.Add(
                new DirectCandidatePairV1
                {
                    InitiatorCandidateId = candidate.CandidateId,
                    ResponderCandidateId = responder.CandidateId
                });
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.ConnectivityCheckBatchV2, 2, 1, now, hostCheck,
                "mismatched-test-key"));
            await Task.Delay(50).ConfigureAwait(false);
            Require(native.BeginCalls == 0,
                "A post-offer envelope with a non-responder key ID reached native checks.");
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.ConnectivityCheckBatchV2, 2, 1, now, hostCheck));
            await WaitUntilAsync(() => native.BeginCalls == 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(!native.LocalGatherCompleted && native.AppendCalls == 0,
                "Windows delayed HOST checks until local or remote gathering completed.");

            native.InjectIgnoredStale();
            native.CompleteGather();
            var trickleEnvelope = await signaling.ReadOutboundAsync().ConfigureAwait(false);
            var trickle = Decrypt(pairwise, trickleEnvelope);
            Require(trickleEnvelope.SignalType == TransportSignalTypeV1.CandidateUpdate &&
                    trickleEnvelope.SignalSeq == 2 && trickle.CandidateUpdate.Candidates.Count == 1 &&
                    trickle.CandidateUpdate.Candidates[0].Kind == DirectCandidateKindV1.ServerReflexive &&
                    trickle.CandidateUpdate.GatheringComplete,
                "Windows did not publish the later server-reflexive candidate batch.");
            var responderReflexive = trickle.CandidateUpdate.Candidates[0];

            var remoteUpdate = new TransportSignalPayloadV1
            { FormatVersion = 1, CandidateUpdate = new DirectCandidateUpdateV1 { GatheringComplete = true } };
            remoteUpdate.CandidateUpdate.Candidates.Add(remoteReflexive);
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.CandidateUpdate, 3, 2, now, remoteUpdate));
            var reflexiveCheck = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                ConnectivityCheckBatchV2 = new DirectConnectivityCheckBatchV2
                {
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(round)),
                    ConnectivityNonce = ByteString.CopyFrom(Enumerable.Repeat((byte)0x31, 16).ToArray()),
                    BatchSeq = 2,
                    PlanComplete = true
                }
            };
            reflexiveCheck.ConnectivityCheckBatchV2.Pairs.Add(new DirectCandidatePairV1
            {
                InitiatorCandidateId = remoteReflexive.CandidateId,
                ResponderCandidateId = responderReflexive.CandidateId
            });
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.ConnectivityCheckBatchV2, 4, 2, now, reflexiveCheck));
            await WaitUntilAsync(() => native.PairDelivered, TimeSpan.FromSeconds(5),
                () => " state=" + runtime.Snapshot.State + "/" + runtime.Snapshot.ReasonCode +
                    " " + signaling.LastRuntimeState + " begin=" + native.BeginCalls +
                    " append=" + native.AppendCalls).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
            if (runtime.Snapshot.State == ProductionDirectShadowState.RelayOnly)
                throw new InvalidOperationException("Runtime retired before nomination: " + runtime.Snapshot.ReasonCode);

            var nominationId = Guid.NewGuid();
            var nomination = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationV2 = new DirectNominationV2
                {
                    NominationId = ByteString.CopyFrom(GuidBytes(nominationId)),
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(round)),
                    WinningPair = reflexiveCheck.ConnectivityCheckBatchV2.Pairs[0].Clone()
                }
            };
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                    TransportSignalTypeV1.NominationV2, 5, 2, now, nomination));
            var ackEnvelope = await signaling.ReadOutboundAsync().ConfigureAwait(false);
            var ack = Decrypt(pairwise, ackEnvelope);
            Require(ackEnvelope.SignalType == TransportSignalTypeV1.NominationAckV2 &&
                    ackEnvelope.SignalSeq == 3 && ackEnvelope.SignalAck == 5 &&
                    ack.NominationAckV2.Result == DirectPathResultV1.Accepted &&
                    ack.NominationAckV2.WinningPair.Equals(nomination.NominationV2.WinningPair) &&
                    native.NominationReadyDelivered,
                "Windows acknowledged a nomination before native NominationReady or changed its pair.");
            var resultEnvelope = await signaling.ReadOutboundAsync().ConfigureAwait(false);
            var result = Decrypt(pairwise, resultEnvelope);
            Require(resultEnvelope.SignalType == TransportSignalTypeV1.NominationResultV2 &&
                    resultEnvelope.SignalSeq == 4 && resultEnvelope.SignalAck == 5 &&
                    result.NominationResultV2.DirectPathBinding.Span.SequenceEqual(FakeNativeV4.Binding()),
                "Windows sent ResultV2 before ShadowReadyV4 or changed its binding.");
            var peerResult = new TransportSignalPayloadV1
            { FormatVersion = 1, NominationResultV2 = result.NominationResultV2.Clone() };
            signaling.Inject(Encrypt(pairwise, ticket, android, pc,
                TransportSignalTypeV1.NominationResultV2, 6, 4, now, peerResult));
        }

        private static async Task ValidateUnsupportedCapabilityFailsClosedAsync(Guid pc,
            DeviceIdentity pcIdentity, Guid android, DeviceIdentity androidIdentity,
            DeviceIdentity serverIdentity, DateTimeOffset now)
        {
            var v1Config = Config(serverIdentity.SigningPublicKey, multipair: false);
            await using var signaling = new StaticConfigSignaling(v1Config);
            var supervisor = new ProductionDirectTransportSupervisor(() => now.UtcDateTime);
            supervisor.ObserveRelayConnected(true);
            var nativeCalls = 0;
            await using var runtime = new ProductionDirectMultipairShadowRuntime(true, pc,
                ProductionAgentRuntime.DeviceKeyId(pc), pcIdentity,
                supervisor, () => signaling,
                _ => Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(new[]
                { new ProductionDirectPeerAuthorization(android, new byte[32], androidIdentity.SigningPublicKey, 1) }),
                nativeFactory: () => { nativeCalls++; throw new InvalidOperationException(); },
                platform: new FakePlatform(), delay: ShortDelay,
                clockMilliseconds: () => now.ToUnixTimeMilliseconds(),
                nativeCapability: () => true);
            runtime.Start(); runtime.ObserveRelayConnected(true);
            await WaitUntilAsync(() => runtime.Snapshot.ReasonCode == "DIRECT_MULTIPAIR_NOT_NEGOTIATED",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(nativeCalls == 0 && !runtime.Snapshot.DirectRoutingPermitted,
                "An unsupported peer reached ABI v4 or routing authority.");
        }

        private static TransportSignalEnvelopeV1 Encrypt(byte[] key, DirectPathTicketV1 ticket,
            Guid sender, Guid recipient, TransportSignalTypeV1 type, ulong seq, ulong ack,
            long now, TransportSignalPayloadV1 payload, string keyId = null)
        {
            var metadata = new TransportSignalEnvelopeV1
            {
                ProtocolVersion = 1, SignalType = type, ConnectionId = ticket.ConnectionId,
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(recipient)),
                PathEpoch = ticket.PathEpoch, SignalSeq = seq, SignalAck = ack, SentAtMs = now,
                KeyId = ByteString.CopyFromUtf8(keyId ?? ProductionAgentRuntime.DeviceKeyId(
                    Guid.ParseExact(Convert.ToHexString(ticket.ResponderDeviceId.Span), "N")))
            };
            var plaintext = payload.ToByteArray();
            try { return ProductionDirectTransportCrypto.EncryptSignal(key, metadata, plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static TransportSignalPayloadV1 Decrypt(byte[] key, TransportSignalEnvelopeV1 envelope)
        {
            var plaintext = ProductionDirectTransportCrypto.DecryptSignal(key, envelope);
            try { return TransportSignalPayloadV1.Parser.ParseFrom(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static ProductionDirectTransportServerConfig Config(byte[] serverKey, bool multipair = true)
        {
            var keys = new ReadOnlyDictionary<string, ProductionDirectTicketSigningKey>(
                new Dictionary<string, ProductionDirectTicketSigningKey>(StringComparer.Ordinal)
                { ["shadow-server-v1"] = new ProductionDirectTicketSigningKey("shadow-server-v1", serverKey) });
            var capabilities = new List<string>
            {
                ProductionDirectTransportSignalingClient.DirectSignalingCapability,
                ProductionDirectTransportSignalingClient.DirectShadowCapability
            };
            if (multipair) capabilities.Add(ProductionDirectTransportSignalingClient.DirectMultipairShadowCapability);
            return new ProductionDirectTransportServerConfig(1, capabilities.AsReadOnly(), keys,
                Array.Empty<string>(), false);
        }

        private static DirectPathTicketV1 Ticket(DateTimeOffset now, DeviceIdentity server,
            Guid account, Guid initiator, Guid responder, Guid connection)
        {
            var ticket = new DirectPathTicketV1
            {
                FormatVersion = 1, TicketId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                AccountId = ByteString.CopyFrom(GuidBytes(account)),
                InitiatorDeviceId = ByteString.CopyFrom(GuidBytes(initiator)),
                ResponderDeviceId = ByteString.CopyFrom(GuidBytes(responder)),
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)), PathEpoch = 9,
                IssuedAtMs = now.ToUnixTimeMilliseconds(), ExpiresAtMs = now.AddSeconds(90).ToUnixTimeMilliseconds(),
                ServerNonce = ByteString.CopyFrom(Enumerable.Repeat((byte)0x71, 16).ToArray()),
                ServerSigningKeyId = ByteString.CopyFromUtf8("shadow-server-v1")
            };
            ticket.AllowedCarriers.Add(DirectCarrierV1.UdpQuic);
            var claims = ticket.ToByteArray(); var domain = Encoding.ASCII.GetBytes("TERMLINK-DIRECT-TICKET-V1\n");
            var signed = domain.Concat(claims).ToArray();
            try { ticket.ServerSignature = ByteString.CopyFrom(server.Sign(signed)); }
            finally { CryptographicOperations.ZeroMemory(claims); CryptographicOperations.ZeroMemory(domain); CryptographicOperations.ZeroMemory(signed); }
            return ticket;
        }

        private static DirectCandidateV1 Candidate(Guid id, string ip, int port, long expires, uint priority,
            DirectCandidateKindV1 kind = DirectCandidateKindV1.Host,
            ByteString relatedCandidateId = null) => new()
        {
            CandidateId = ByteString.CopyFrom(GuidBytes(id)), Kind = kind,
            Carrier = DirectCarrierV1.UdpQuic, IpAddress = ByteString.CopyFrom(IPAddress.Parse(ip).GetAddressBytes()),
            Port = checked((uint)port), Priority = priority, ExpiresAtMs = expires,
            RelatedCandidateId = relatedCandidateId ?? ByteString.Empty
        };

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout,
            Func<string> diagnostics = null)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (!predicate())
            { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Multipair runtime validation timed out." + (diagnostics?.Invoke() ?? string.Empty)); await Task.Delay(5).ConfigureAwait(false); }
        }

        private static Task ShortDelay(TimeSpan value, CancellationToken token) => value <= TimeSpan.FromMilliseconds(20)
            ? Task.Delay(TimeSpan.FromMilliseconds(1), token) : Task.Delay(Timeout.InfiniteTimeSpan, token);
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class FakePlatform : IProductionDirectShadowPlatform
        {
            public Task<ProductionDirectGatherPlatform> PrepareGatherAsync(IReadOnlyList<string> stunServers,
                CancellationToken cancellationToken) => Task.FromResult(new ProductionDirectGatherPlatform
                {
                    LocalBind = new ProductionDirectNativeEndpoint(IPAddress.Parse("192.168.80.10"), 0),
                    AdvertisedLocalAddress = IPAddress.Parse("192.168.80.10"),
                    StunServers = Array.Empty<ProductionDirectNativeEndpoint>()
                });
        }

        private sealed class FakeSignaling : IProductionDirectShadowSignaling
        {
            private readonly Channel<TransportSignalEnvelopeV1> incoming = Channel.CreateUnbounded<TransportSignalEnvelopeV1>();
            private readonly Channel<TransportSignalEnvelopeV1> outgoing = Channel.CreateUnbounded<TransportSignalEnvelopeV1>();
            private readonly ProductionDirectTransportServerConfig config;
            private readonly ProductionDirectTransportSignalingClient validator;
            public FakeSignaling(Guid pc, DeviceIdentity identity, ProductionDirectTransportServerConfig config, long now)
            {
                this.config = config;
                validator = new ProductionDirectTransportSignalingClient("https://multipair.test", pc, identity,
                    _ => Task.FromResult("unused"), enabled: true, clockMilliseconds: () => now,
                    multipairV2Enabled: true);
            }
            public string LastRuntimeState => $"outgoing={outgoing.Reader.Count},incoming={incoming.Reader.Count}";
            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>> FetchConfigAsync(
                CancellationToken cancellationToken = default) => Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Success(config));
            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>> IssueTicketAsync(
                Guid targetDeviceId, Guid connectionId, ProductionDirectTransportServerConfig value,
                CancellationToken cancellationToken = default) => throw new InvalidOperationException();
            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(DirectPathTicketV1 ticket,
                Guid initiatorDeviceId, Guid connectionId, ProductionDirectTransportServerConfig value) =>
                validator.ValidateIncomingTicket(ticket, initiatorDeviceId, connectionId, value);
            public bool Start() => true;
            public bool TrySend(TransportSignalEnvelopeV1 envelope) => outgoing.Writer.TryWrite(envelope);
            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            { while (await incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                while (incoming.Reader.TryRead(out var value)) yield return value; }
            public void Inject(TransportSignalEnvelopeV1 value) => incoming.Writer.TryWrite(value);
            public Task<TransportSignalEnvelopeV1> ReadOutboundAsync() => outgoing.Reader.ReadAsync().AsTask();
            public async ValueTask DisposeAsync()
            { incoming.Writer.TryComplete(); outgoing.Writer.TryComplete(); await validator.DisposeAsync().ConfigureAwait(false); }
        }

        private sealed class StaticConfigSignaling : IProductionDirectShadowSignaling
        {
            private readonly ProductionDirectTransportServerConfig config;
            public StaticConfigSignaling(ProductionDirectTransportServerConfig config) { this.config = config; }
            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>> FetchConfigAsync(
                CancellationToken cancellationToken = default) => Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Success(config));
            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>> IssueTicketAsync(
                Guid targetDeviceId, Guid connectionId, ProductionDirectTransportServerConfig value,
                CancellationToken cancellationToken = default) => throw new InvalidOperationException();
            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(DirectPathTicketV1 ticket,
                Guid initiatorDeviceId, Guid connectionId, ProductionDirectTransportServerConfig value) => throw new InvalidOperationException();
            public bool Start() => throw new InvalidOperationException();
            public bool TrySend(TransportSignalEnvelopeV1 envelope) => throw new InvalidOperationException();
            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.Yield(); yield break; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class FakeNativeV4 : IProductionDirectShadowNativeV4
        {
            private readonly ConcurrentQueue<ProductionDirectNativeEventV4> events = new();
            private readonly byte[] pcPublicKey; private readonly byte[] pairwise;
            private ulong epoch; private Guid attempt; private ulong generation;
            private ProductionDirectCandidateV4 localHost;
            private ProductionDirectCandidatePairV4 winner;
            public FakeNativeV4(byte[] pcPublicKey, byte[] pairwise)
            { this.pcPublicKey = pcPublicKey.ToArray(); this.pairwise = pairwise.ToArray(); }
            public int BeginCalls { get; private set; }
            public int AppendCalls { get; private set; }
            public int CommitCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public uint CommittedRank { get; private set; }
            public bool KeysVerified { get; private set; }
            public bool SignatureVerified { get; private set; }
            public bool PairDelivered { get; private set; }
            public bool NominationReadyDelivered { get; private set; }
            public bool LocalGatherCompleted { get; private set; }
            public ProductionDirectNativeSnapshot Snapshot => new()
            { State = ProductionDirectNativeState.RelayOnly, NativeAvailable = DisposeCalls == 0 };
            public static byte[] Binding() => SHA256.HashData(Encoding.ASCII.GetBytes("multipair-shared-binding"));
            public ProductionDirectNativeCommandResult Gather(ProductionDirectGatherRequestV4 request)
            {
                epoch = request.Epoch; attempt = request.AttemptId;
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.IdentityReady,
                    opaque: Encoding.ASCII.GetBytes("pc-v4-certificate")));
                localHost = new ProductionDirectCandidateV4
                { CandidateId = Guid.NewGuid(), Endpoint = new ProductionDirectNativeEndpoint(IPAddress.Parse("192.168.80.10"), 41_000),
                  Kind = ProductionDirectCandidateKindV4.Host, Priority = 2_000_000_000,
                  ExpiresAtMilliseconds = request.CandidateExpiresAtMilliseconds };
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.CandidateBatch,
                    batch: 1, total: 1, candidates: new[] { localHost }, complete: false));
                return ProductionDirectNativeCommandResult.Accepted;
            }
            public ProductionDirectNativeCommandResult BeginChecks(ProductionDirectBeginChecksRequestV4 request)
            {
                BeginCalls++;
                var local = ProductionDirectTransportCrypto.DeriveProbeKeyV2(pairwise, request.ConnectionId,
                    request.Epoch, request.CheckRoundId, request.LocalDeviceId, request.PeerDeviceId);
                var peer = ProductionDirectTransportCrypto.DeriveProbeKeyV2(pairwise, request.ConnectionId,
                    request.Epoch, request.CheckRoundId, request.PeerDeviceId, request.LocalDeviceId);
                try { KeysVerified = CryptographicOperations.FixedTimeEquals(local, request.LocalSendProbeHmacKey) &&
                    CryptographicOperations.FixedTimeEquals(peer, request.PeerReceiveProbeHmacKey); }
                finally { CryptographicOperations.ZeroMemory(local); CryptographicOperations.ZeroMemory(peer); }
                return KeysVerified && request.Pairs.Count == 4 && !request.PlanComplete &&
                    request.Pairs.All(value => value.Responder.Equals(localHost))
                    ? ProductionDirectNativeCommandResult.Accepted : ProductionDirectNativeCommandResult.RelayFallback;
            }
            public ProductionDirectNativeCommandResult AppendCheckPairs(ProductionDirectAppendPairsRequestV4 request)
            {
                AppendCalls++;
                winner = request.Pairs.SingleOrDefault();
                if (request.BatchSequence != 2 || request.TotalPairCount != 5 || !request.PlanComplete ||
                    winner?.Rank != 5 || winner.Responder.Kind != ProductionDirectCandidateKindV4.ServerReflexive)
                    return ProductionDirectNativeCommandResult.RelayFallback;
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.PairValidated,
                    pair: winner, round: request.CheckRoundId, rtt: 18, probes: 4));
                return ProductionDirectNativeCommandResult.Accepted;
            }
            public void CompleteGather()
            {
                if (LocalGatherCompleted || localHost == null) throw new InvalidOperationException();
                LocalGatherCompleted = true;
                var reflexive = new ProductionDirectCandidateV4
                {
                    CandidateId = Guid.NewGuid(), RelatedCandidateId = localHost.CandidateId,
                    Endpoint = new ProductionDirectNativeEndpoint(IPAddress.Parse("203.0.113.10"), 41_001),
                    Kind = ProductionDirectCandidateKindV4.ServerReflexive, Priority = 1_999_999_999,
                    ExpiresAtMilliseconds = localHost.ExpiresAtMilliseconds
                };
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.CandidateBatch,
                    batch: 2, total: 2, candidates: new[] { reflexive }, complete: true));
            }
            public ProductionDirectNativeCommandResult CommitNomination(ProductionDirectCommitRequestV4 request)
            {
                CommitCalls++; CommittedRank = request.WinningPair.Rank;
                if (!request.WinningPair.Equals(winner) || request.Role != ProductionDirectNativeRole.Responder)
                    return ProductionDirectNativeCommandResult.RelayFallback;
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.NominationReady,
                    pair: winner, round: request.CheckRoundId, nomination: request.NominationId));
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.SignRequest,
                    opaque: SHA256.HashData(Encoding.ASCII.GetBytes("v4-proof")), request: 77));
                events.Enqueue(Event(ProductionDirectNativeEventKindV4.ShadowReady,
                    pair: winner, round: request.CheckRoundId, nomination: request.NominationId, binding: Binding()));
                return ProductionDirectNativeCommandResult.Accepted;
            }
            public ProductionDirectNativeCommandResult Cancel(ulong valueEpoch, Guid valueAttempt) => ProductionDirectNativeCommandResult.Accepted;
            public ProductionDirectNativeCommandResult NetworkChanged(ulong valueEpoch, Guid valueAttempt) => ProductionDirectNativeCommandResult.Accepted;
            public ProductionDirectNativeCommandResult SubmitSignature(ulong valueEpoch, Guid valueAttempt, ulong requestId, byte[] signature)
            {
                var signer = new Ed25519Signer(); signer.Init(false, new Ed25519PublicKeyParameters(pcPublicKey));
                var body = SHA256.HashData(Encoding.ASCII.GetBytes("v4-proof")); signer.BlockUpdate(body, 0, body.Length);
                SignatureVerified = requestId == 77 && signer.VerifySignature(signature);
                return SignatureVerified ? ProductionDirectNativeCommandResult.Accepted : ProductionDirectNativeCommandResult.RelayFallback;
            }
            public IReadOnlyList<ProductionDirectNativeEventV4> Poll(int maximumEvents = 32)
            {
                var result = new List<ProductionDirectNativeEventV4>();
                while (result.Count < maximumEvents && events.TryDequeue(out var value))
                { if (value.Kind == ProductionDirectNativeEventKindV4.PairValidated) PairDelivered = true;
                  if (value.Kind == ProductionDirectNativeEventKindV4.NominationReady) NominationReadyDelivered = true;
                  result.Add(value); }
                return result;
            }
            public void InjectFailure() => events.Enqueue(Event(ProductionDirectNativeEventKindV4.DirectFailed));
            public void InjectIgnoredStale() => events.Enqueue(new ProductionDirectNativeEventV4
            {
                Kind = ProductionDirectNativeEventKindV4.IgnoredStale,
                State = ProductionDirectNativeState.RelayOnly,
                Epoch = epoch - 1,
                AttemptId = Guid.NewGuid(),
                Generation = ++generation
            });
            public void Dispose() { DisposeCalls++; CryptographicOperations.ZeroMemory(pcPublicKey); CryptographicOperations.ZeroMemory(pairwise); }
            private ProductionDirectNativeEventV4 Event(ProductionDirectNativeEventKindV4 kind,
                byte[] opaque = null, uint batch = 0, uint total = 0,
                IReadOnlyList<ProductionDirectCandidateV4> candidates = null, bool complete = false,
                ProductionDirectCandidatePairV4 pair = null, Guid round = default, Guid nomination = default,
                uint rtt = 0, uint probes = 0, ulong request = 0, byte[] binding = null) => new()
            {
                Kind = kind, State = kind == ProductionDirectNativeEventKindV4.ShadowReady
                    ? ProductionDirectNativeState.ShadowDirect : ProductionDirectNativeState.Probing,
                Epoch = epoch, AttemptId = attempt, Generation = ++generation, RequestId = request,
                OpaquePayload = opaque, BatchSequence = batch, TotalCount = total,
                Candidates = candidates ?? Array.Empty<ProductionDirectCandidateV4>(), Complete = complete,
                Pair = pair, CheckRoundId = round, NominationId = nomination,
                RoundTripTimeMilliseconds = rtt, ProbeAttempts = probes, DirectPathBinding = binding
            };
        }
    }
}

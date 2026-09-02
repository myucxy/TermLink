using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    internal static class ProductionDirectShadowRuntimeValidation
    {
        public static async Task ValidateAsync()
        {
            await ValidateDisabledRuntimeHasNoSideEffectsAsync().ConfigureAwait(false);
            ValidateCandidatePairSelection();
            var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
            var pcDeviceId = Guid.NewGuid();
            var androidDeviceId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var pairwiseKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            using var pcIdentity = DeviceIdentity.Generate();
            using var androidIdentity = DeviceIdentity.Generate();
            using var serverIdentity = DeviceIdentity.Generate();
            var config = Config(serverIdentity.SigningPublicKey);
            var ticket = Ticket(
                now,
                serverIdentity,
                accountId,
                androidDeviceId,
                pcDeviceId,
                connectionId);
            var platform = new FakePlatform();
            var native = new FakeNative(pcIdentity.SigningPublicKey, pairwiseKey);
            await using var signaling = new FakeSignaling(
                pcDeviceId,
                pcIdentity,
                config,
                now.ToUnixTimeMilliseconds());
            var directSupervisor = new ProductionDirectTransportSupervisor(() => now.UtcDateTime);
            directSupervisor.ObserveRelayConnected(true);
            using var relaySupervisor = new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences(),
                nextJitterMilliseconds: () => 0,
                utcNow: () => now.UtcDateTime);
            var relayGeneration = relaySupervisor.MarkPhysicalConnected();
            var peer = new ProductionDirectPeerAuthorization(
                androidDeviceId,
                pairwiseKey,
                androidIdentity.SigningPublicKey,
                keyEpoch: 1);
            await using var runtime = new ProductionDirectShadowRuntime(
                enabled: true,
                localDeviceId: pcDeviceId,
                signalKeyId: ProductionAgentRuntime.DeviceKeyId(pcDeviceId),
                identity: pcIdentity,
                supervisor: directSupervisor,
                signalingFactory: () => signaling,
                peerProvider: _ => Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(
                    new[] { peer }),
                nativeFactory: () => native,
                platform: platform,
                delay: DelayAsync,
                clockMilliseconds: () => now.ToUnixTimeMilliseconds());

            Require(runtime.Start(), "The direct shadow runtime did not start.");
            runtime.ObserveRelayConnected(true);
            var android = DriveAndroidInitiatorToShadowReadyAsync(
                signaling,
                native,
                ticket,
                androidDeviceId,
                pcDeviceId,
                pairwiseKey,
                now.ToUnixTimeMilliseconds());
            await android.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(
                () => runtime.Snapshot.State == ProductionDirectShadowState.ShadowReadyNotRouted,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var ready = runtime.Snapshot;
            var directReady = directSupervisor.Snapshot();
            Require(!ready.DirectRoutingPermitted && !native.Snapshot.DirectRoutingPermitted &&
                    native.MarkCandidatesCalls == 1 && native.NominateCalls == 1 &&
                    native.SignatureCalls == 1 && native.SignatureVerified &&
                    native.DirectionalKeysVerified &&
                    native.LastRole == ProductionDirectNativeRole.Responder &&
                    signaling.IssueTicketCalls == 0 &&
                    directReady.State == ProductionDirectPathState.ShadowReadyNotRouted &&
                    directReady.AuthenticatedProbeSha256.SequenceEqual(FakeNative.SharedBinding()),
                "The Android initiator and Windows responder did not reach authenticated shadow-ready.");
            var relayWhileShadowReady = relaySupervisor.Snapshot();
            Require(relayWhileShadowReady.PhysicalConnected &&
                    relayWhileShadowReady.PhysicalGeneration == relayGeneration &&
                    relayWhileShadowReady.ScheduledReconnects == 0 &&
                    directSupervisor.Snapshot().RelayConnected &&
                    native.DisposeCalls == 0,
                "Shadow-ready modified or replaced the Relay generation.");

            native.InjectFailure();
            await WaitUntilAsync(
                () => runtime.Snapshot.State == ProductionDirectShadowState.RelayOnly &&
                    runtime.Snapshot.ReasonCode == "DIRECT_NATIVE_FAILED",
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var relayAfterDirectFailure = relaySupervisor.Snapshot();
            Require(relayAfterDirectFailure.PhysicalConnected &&
                    relayAfterDirectFailure.PhysicalGeneration == relayGeneration &&
                    relayAfterDirectFailure.ScheduledReconnects == 0 &&
                    directSupervisor.Snapshot().RelayConnected && native.DisposeCalls == 1,
                "A post-ready Direct fault replaced or reconnected the Relay generation.");

            CryptographicOperations.ZeroMemory(pairwiseKey);

            await ValidateSignalingLaneRecreationAsync().ConfigureAwait(false);
        }

        private static async Task ValidateDisabledRuntimeHasNoSideEffectsAsync()
        {
            var signalingFactoryCalls = 0;
            var nativeFactoryCalls = 0;
            var peerProviderCalls = 0;
            using var identity = DeviceIdentity.Generate();
            var supervisor = new ProductionDirectTransportSupervisor();
            var localDeviceId = Guid.NewGuid();
            await using var runtime = new ProductionDirectShadowRuntime(
                enabled: false,
                localDeviceId: localDeviceId,
                signalKeyId: ProductionAgentRuntime.DeviceKeyId(localDeviceId),
                identity: identity,
                supervisor: supervisor,
                signalingFactory: () =>
                {
                    signalingFactoryCalls++;
                    throw new InvalidOperationException("Disabled Direct created signalling.");
                },
                peerProvider: _ =>
                {
                    peerProviderCalls++;
                    throw new InvalidOperationException("Disabled Direct requested peers.");
                },
                nativeFactory: () =>
                {
                    nativeFactoryCalls++;
                    throw new InvalidOperationException("Disabled Direct loaded native code.");
                });
            Require(!runtime.Start(), "A disabled Direct shadow runtime started.");
            runtime.ObserveRelayConnected(true);
            runtime.NotifyNetworkChanged();
            await Task.Delay(25).ConfigureAwait(false);
            Require(signalingFactoryCalls == 0 && nativeFactoryCalls == 0 && peerProviderCalls == 0 &&
                    runtime.Snapshot.State == ProductionDirectShadowState.Disabled &&
                    !runtime.Snapshot.DirectRoutingPermitted,
                "Disabled Direct performed I/O or exposed routing permission.");
        }

        private static void ValidateCandidatePairSelection()
        {
            var localLan = SelectionCandidate(
                DirectCandidateKindV1.Host,
                "192.168.40.10",
                priority: 10,
                identifier: 0x31);
            var remoteLan = SelectionCandidate(
                DirectCandidateKindV1.Host,
                "192.168.40.220",
                priority: 9,
                identifier: 0x41);
            var localSrflx = SelectionCandidate(
                DirectCandidateKindV1.ServerReflexive,
                "8.8.8.8",
                priority: 2_000,
                identifier: 0x32);
            var remoteSrflx = SelectionCandidate(
                DirectCandidateKindV1.ServerReflexive,
                "1.1.1.1",
                priority: 2_100,
                identifier: 0x42);
            var lan = ProductionDirectShadowRuntime.SelectInitiatorCandidatePair(
                new[] { localSrflx, localLan },
                new[] { remoteSrflx, remoteLan });
            Require(lan != null && ReferenceEquals(lan.Local, localLan) &&
                    ReferenceEquals(lan.Remote, remoteLan),
                "A conservative RFC1918 /24 pair did not win the LAN-first nomination policy.");

            var unrelatedLocalHost = SelectionCandidate(
                DirectCandidateKindV1.Host,
                "192.168.50.10",
                priority: uint.MaxValue,
                identifier: 0x11);
            var unrelatedRemoteHost = SelectionCandidate(
                DirectCandidateKindV1.Host,
                "10.0.0.20",
                priority: uint.MaxValue,
                identifier: 0x21);
            var lowerIdentifierSrflx = SelectionCandidate(
                DirectCandidateKindV1.ServerReflexive,
                "9.9.9.9",
                priority: 2_000,
                identifier: 0x12);
            var higherIdentifierSrflx = SelectionCandidate(
                DirectCandidateKindV1.ServerReflexive,
                "8.8.4.4",
                priority: 2_000,
                identifier: 0x13);
            var dualNat = ProductionDirectShadowRuntime.SelectInitiatorCandidatePair(
                new[] { unrelatedLocalHost, higherIdentifierSrflx, lowerIdentifierSrflx },
                new[] { unrelatedRemoteHost, remoteSrflx });
            Require(dualNat != null && ReferenceEquals(dualNat.Local, lowerIdentifierSrflx) &&
                    ReferenceEquals(dualNat.Remote, remoteSrflx),
                "Dual-NAT selection did not prefer SRFLX/SRFLX with stable priority/ID ties.");
        }

        private static async Task ValidateSignalingLaneRecreationAsync()
        {
            var now = new DateTimeOffset(2026, 9, 2, 8, 5, 0, TimeSpan.Zero);
            var pcDeviceId = Guid.NewGuid();
            var androidDeviceId = Guid.NewGuid();
            var pairwiseKey = Enumerable.Repeat((byte)0x61, 32).ToArray();
            using var pcIdentity = DeviceIdentity.Generate();
            using var androidIdentity = DeviceIdentity.Generate();
            using var serverIdentity = DeviceIdentity.Generate();
            var config = Config(serverIdentity.SigningPublicKey);
            var lanes = new ConcurrentQueue<RecreatingFakeSignaling>();
            var laneOrdinal = 0;
            var directSupervisor = new ProductionDirectTransportSupervisor(() => now.UtcDateTime);
            directSupervisor.ObserveRelayConnected(true);

            await using var runtime = new ProductionDirectShadowRuntime(
                enabled: true,
                localDeviceId: pcDeviceId,
                signalKeyId: ProductionAgentRuntime.DeviceKeyId(pcDeviceId),
                identity: pcIdentity,
                supervisor: directSupervisor,
                signalingFactory: () =>
                {
                    var lane = new RecreatingFakeSignaling(
                        config,
                        failReader: Interlocked.Increment(ref laneOrdinal) == 1);
                    lanes.Enqueue(lane);
                    return lane;
                },
                peerProvider: _ => Task.FromResult<IReadOnlyList<ProductionDirectPeerAuthorization>>(
                    new[]
                    {
                        new ProductionDirectPeerAuthorization(
                            androidDeviceId,
                            pairwiseKey,
                            androidIdentity.SigningPublicKey,
                            keyEpoch: 1)
                    }),
                nativeFactory: () => throw new InvalidOperationException(
                    "A lane-only validation must not create native state."),
                platform: new RejectingPlatform(),
                delay: (duration, token) => duration >= ProductionDirectShadowRuntime.RetryDelay
                    ? Task.CompletedTask
                    : Task.Delay(TimeSpan.FromMilliseconds(1), token),
                clockMilliseconds: () => now.ToUnixTimeMilliseconds());

            Require(runtime.Start(), "The lane recreation runtime did not start.");
            runtime.ObserveRelayConnected(true);
            await WaitUntilAsync(
                () => lanes.ToArray() is var current && current.Length == 2 &&
                    current[1].FetchConfigCalls == 1 && current[1].StartCalls == 1,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            var observed = lanes.ToArray();
            Require(observed[0].FetchConfigCalls == 1 && observed[0].StartCalls == 1 &&
                    observed[0].DisposeCalls == 1 && observed[1].DisposeCalls == 0 &&
                    !runtime.Snapshot.DirectRoutingPermitted,
                "A terminal signaling reader was reused instead of replacing its exact lane generation.");

            CryptographicOperations.ZeroMemory(pairwiseKey);
        }

        private static async Task DriveAndroidInitiatorToShadowReadyAsync(
            FakeSignaling signaling,
            FakeNative native,
            DirectPathTicketV1 ticket,
            Guid androidDeviceId,
            Guid pcDeviceId,
            byte[] pairwiseKey,
            long nowMilliseconds)
        {
            var remoteCandidate = Candidate("192.168.50.3", 50_003, ticket.ExpiresAtMs);
            var offer = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                PathOffer = new DirectPathOfferV1
                {
                    Ticket = ticket.Clone(),
                    QuicCertificateSha256 = ByteString.CopyFrom(
                        SHA256.HashData(Encoding.ASCII.GetBytes("android-certificate"))),
                    GatheringComplete = true
                }
            };
            offer.PathOffer.Candidates.Add(remoteCandidate);
            signaling.Inject(Encrypt(
                pairwiseKey,
                ticket,
                androidDeviceId,
                pcDeviceId,
                TransportSignalTypeV1.PathOffer,
                sequence: 1,
                acknowledgement: 0,
                nowMilliseconds,
                offer));

            var answerEnvelope = await signaling.ReadOutboundAsync().WaitAsync(
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var answer = Decrypt(pairwiseKey, answerEnvelope);
            Require(answerEnvelope.SignalType == TransportSignalTypeV1.PathAnswer &&
                    answerEnvelope.SignalSeq == 1 && answerEnvelope.SignalAck == 1 &&
                    answer.SignalCase == TransportSignalPayloadV1.SignalOneofCase.PathAnswer &&
                    answer.PathAnswer.Result == DirectPathResultV1.Accepted &&
                    answer.PathAnswer.Candidates.Count == 1,
                "Windows did not return the exact PATH_ANSWER responder state.");
            var localCandidate = answer.PathAnswer.Candidates[0];
            var nominationId = Enumerable.Repeat((byte)0x31, 16).ToArray();
            var nomination = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                Nomination = new DirectNominationV1
                {
                    NominationId = ByteString.CopyFrom(nominationId),
                    Pair = new DirectCandidatePairV1
                    {
                        InitiatorCandidateId = remoteCandidate.CandidateId,
                        ResponderCandidateId = localCandidate.CandidateId
                    },
                    ConnectivityNonce = ByteString.CopyFrom(
                        Enumerable.Repeat((byte)0x42, 16).ToArray())
                }
            };
            signaling.Inject(Encrypt(
                pairwiseKey,
                ticket,
                androidDeviceId,
                pcDeviceId,
                TransportSignalTypeV1.Nomination,
                sequence: 2,
                acknowledgement: 1,
                nowMilliseconds,
                nomination,
                keyId: "mismatched-test-key"));
            await Task.Delay(50).ConfigureAwait(false);
            Require(native.NominateCalls == 0,
                "A post-offer envelope with a non-responder key ID reached native nomination.");
            signaling.Inject(Encrypt(
                pairwiseKey,
                ticket,
                androidDeviceId,
                pcDeviceId,
                TransportSignalTypeV1.Nomination,
                sequence: 2,
                acknowledgement: 1,
                nowMilliseconds,
                nomination));

            var resultEnvelope = await signaling.ReadOutboundAsync().WaitAsync(
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var result = Decrypt(pairwiseKey, resultEnvelope);
            Require(resultEnvelope.SignalType == TransportSignalTypeV1.NominationResult &&
                    resultEnvelope.SignalSeq == 2 && resultEnvelope.SignalAck == 2 &&
                    result.SignalCase == TransportSignalPayloadV1.SignalOneofCase.NominationResult &&
                    result.NominationResult.Result == DirectPathResultV1.Accepted &&
                    result.NominationResult.NominationId.Span.SequenceEqual(nominationId) &&
                    result.NominationResult.Pair.InitiatorCandidateId.Equals(
                        remoteCandidate.CandidateId) &&
                    result.NominationResult.Pair.ResponderCandidateId.Equals(
                        localCandidate.CandidateId) &&
                    result.NominationResult.DirectPathBinding.Span.SequenceEqual(
                        FakeNative.SharedBinding()),
                "Windows did not return its exact authenticated nomination result.");
            var peerResult = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationResult = result.NominationResult.Clone()
            };
            signaling.Inject(Encrypt(
                pairwiseKey,
                ticket,
                androidDeviceId,
                pcDeviceId,
                TransportSignalTypeV1.NominationResult,
                sequence: 3,
                acknowledgement: 2,
                nowMilliseconds,
                peerResult));
        }

        private static TransportSignalEnvelopeV1 Encrypt(
            byte[] pairwiseKey,
            DirectPathTicketV1 ticket,
            Guid sender,
            Guid recipient,
            TransportSignalTypeV1 type,
            ulong sequence,
            ulong acknowledgement,
            long nowMilliseconds,
            TransportSignalPayloadV1 payload,
            string keyId = null)
        {
            var metadata = new TransportSignalEnvelopeV1
            {
                ProtocolVersion = 1,
                SignalType = type,
                ConnectionId = ticket.ConnectionId,
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(recipient)),
                PathEpoch = ticket.PathEpoch,
                SignalSeq = sequence,
                SignalAck = acknowledgement,
                SentAtMs = nowMilliseconds,
                KeyId = ByteString.CopyFromUtf8(keyId ?? ProductionAgentRuntime.DeviceKeyId(
                    Guid.ParseExact(Convert.ToHexString(ticket.ResponderDeviceId.Span), "N")))
            };
            var plaintext = payload.ToByteArray();
            try { return ProductionDirectTransportCrypto.EncryptSignal(
                pairwiseKey, metadata, plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static TransportSignalPayloadV1 Decrypt(
            byte[] pairwiseKey,
            TransportSignalEnvelopeV1 envelope)
        {
            var plaintext = ProductionDirectTransportCrypto.DecryptSignal(pairwiseKey, envelope);
            try { return TransportSignalPayloadV1.Parser.ParseFrom(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static ProductionDirectTransportServerConfig Config(byte[] serverPublicKey)
        {
            var keys = new ReadOnlyDictionary<string, ProductionDirectTicketSigningKey>(
                new Dictionary<string, ProductionDirectTicketSigningKey>(StringComparer.Ordinal)
                {
                    ["shadow-server-v1"] = new ProductionDirectTicketSigningKey(
                        "shadow-server-v1", serverPublicKey)
                });
            return new ProductionDirectTransportServerConfig(
                1,
                Array.AsReadOnly(new[]
                {
                    ProductionDirectTransportSignalingClient.DirectSignalingCapability,
                    ProductionDirectTransportSignalingClient.DirectShadowCapability
                }),
                keys,
                Array.Empty<string>(),
                directRoutingPermitted: false);
        }

        private static DirectPathTicketV1 Ticket(
            DateTimeOffset now,
            DeviceIdentity serverIdentity,
            Guid accountId,
            Guid initiator,
            Guid responder,
            Guid connectionId)
        {
            var ticket = new DirectPathTicketV1
            {
                FormatVersion = 1,
                TicketId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                AccountId = ByteString.CopyFrom(GuidBytes(accountId)),
                InitiatorDeviceId = ByteString.CopyFrom(GuidBytes(initiator)),
                ResponderDeviceId = ByteString.CopyFrom(GuidBytes(responder)),
                ConnectionId = ByteString.CopyFrom(GuidBytes(connectionId)),
                PathEpoch = 7,
                IssuedAtMs = now.ToUnixTimeMilliseconds(),
                ExpiresAtMs = now.AddSeconds(90).ToUnixTimeMilliseconds(),
                ServerNonce = ByteString.CopyFrom(
                    Enumerable.Repeat((byte)0x71, 16).ToArray()),
                ServerSigningKeyId = ByteString.CopyFromUtf8("shadow-server-v1")
            };
            ticket.AllowedCarriers.Add(DirectCarrierV1.UdpQuic);
            var claims = ticket.ToByteArray();
            var domain = Encoding.ASCII.GetBytes("TERMLINK-DIRECT-TICKET-V1\n");
            var signed = domain.Concat(claims).ToArray();
            try { ticket.ServerSignature = ByteString.CopyFrom(serverIdentity.Sign(signed)); }
            finally
            {
                CryptographicOperations.ZeroMemory(claims);
                CryptographicOperations.ZeroMemory(domain);
                CryptographicOperations.ZeroMemory(signed);
            }
            return ticket;
        }

        private static DirectCandidateV1 Candidate(string address, int port, long expiresAt) => new()
        {
            CandidateId = ByteString.CopyFrom(
                Enumerable.Repeat((byte)0x21, 16).ToArray()),
            Kind = DirectCandidateKindV1.Host,
            Carrier = DirectCarrierV1.UdpQuic,
            IpAddress = ByteString.CopyFrom(IPAddress.Parse(address).GetAddressBytes()),
            Port = checked((uint)port),
            Priority = 2_000_000_000,
            ExpiresAtMs = expiresAt
        };

        private static DirectCandidateV1 SelectionCandidate(
            DirectCandidateKindV1 kind,
            string address,
            uint priority,
            byte identifier) => new()
        {
            CandidateId = ByteString.CopyFrom(Enumerable.Repeat(identifier, 16).ToArray()),
            Kind = kind,
            Carrier = DirectCarrierV1.UdpQuic,
            IpAddress = ByteString.CopyFrom(IPAddress.Parse(address).GetAddressBytes()),
            Port = 50_000,
            Priority = priority,
            RelatedCandidateId = kind == DirectCandidateKindV1.ServerReflexive
                ? ByteString.CopyFrom(Enumerable.Repeat((byte)0x7f, 16).ToArray())
                : ByteString.Empty,
            ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
        };

        private static Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            duration <= TimeSpan.FromMilliseconds(20)
                ? Task.Delay(TimeSpan.FromMilliseconds(1), token)
                : Task.Delay(Timeout.InfiniteTimeSpan, token);

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Shadow validation timed out.");
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakePlatform : IProductionDirectShadowPlatform
        {
            public Task<ProductionDirectGatherPlatform> PrepareGatherAsync(
                IReadOnlyList<string> stunServers,
                CancellationToken cancellationToken) =>
                Task.FromResult(new ProductionDirectGatherPlatform
                {
                    LocalBind = new ProductionDirectNativeEndpoint(
                        IPAddress.Parse("192.168.50.2"), 0),
                    AdvertisedLocalAddress = IPAddress.Parse("192.168.50.2"),
                    StunServers = Array.Empty<ProductionDirectNativeEndpoint>()
                });
        }

        private sealed class RejectingPlatform : IProductionDirectShadowPlatform
        {
            public Task<ProductionDirectGatherPlatform> PrepareGatherAsync(
                IReadOnlyList<string> stunServers,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException(
                    "A signaling-lane recreation test must not begin candidate gathering.");
        }

        private sealed class RecreatingFakeSignaling : IProductionDirectShadowSignaling
        {
            private readonly ProductionDirectTransportServerConfig config;
            private readonly bool failReader;

            public RecreatingFakeSignaling(
                ProductionDirectTransportServerConfig config,
                bool failReader)
            {
                this.config = config;
                this.failReader = failReader;
            }

            public int FetchConfigCalls { get; private set; }
            public int StartCalls { get; private set; }
            public int DisposeCalls { get; private set; }

            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
                FetchConfigAsync(CancellationToken cancellationToken = default)
            {
                FetchConfigCalls++;
                return Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>
                        .Success(config));
            }

            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
                IssueTicketAsync(
                    Guid targetDeviceId,
                    Guid connectionId,
                    ProductionDirectTransportServerConfig value,
                    CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(
                    "The Windows responder lane must not issue a ticket.");

            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(
                DirectPathTicketV1 ticket,
                Guid initiatorDeviceId,
                Guid connectionId,
                ProductionDirectTransportServerConfig value) =>
                throw new InvalidOperationException(
                    "The lane recreation fixture does not accept an offer.");

            public bool Start()
            {
                StartCalls++;
                return true;
            }

            public bool TrySend(TransportSignalEnvelopeV1 envelope) =>
                throw new InvalidOperationException(
                    "The lane recreation fixture must not send a signal.");

            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (failReader) throw new InvalidDataException("fixture signaling failure");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            public ValueTask DisposeAsync()
            {
                DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeSignaling : IProductionDirectShadowSignaling
        {
            private readonly Channel<TransportSignalEnvelopeV1> incoming =
                Channel.CreateUnbounded<TransportSignalEnvelopeV1>();
            private readonly Channel<TransportSignalEnvelopeV1> outgoing =
                Channel.CreateUnbounded<TransportSignalEnvelopeV1>();
            private readonly ProductionDirectTransportSignalingClient validator;
            private readonly ProductionDirectTransportServerConfig config;

            public FakeSignaling(
                Guid pcDeviceId,
                DeviceIdentity identity,
                ProductionDirectTransportServerConfig config,
                long nowMilliseconds)
            {
                this.config = config;
                validator = new ProductionDirectTransportSignalingClient(
                    "https://shadow-validation.test",
                    pcDeviceId,
                    identity,
                    _ => Task.FromResult("unused"),
                    enabled: true,
                    clockMilliseconds: () => nowMilliseconds);
            }

            public int IssueTicketCalls { get; private set; }

            public Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
                FetchConfigAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>
                    .Success(config));

            public Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
                IssueTicketAsync(
                    Guid targetDeviceId,
                    Guid connectionId,
                    ProductionDirectTransportServerConfig value,
                    CancellationToken cancellationToken = default)
            {
                IssueTicketCalls++;
                return Task.FromResult(
                    ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                        ProductionDirectSignalingFaultCode.TicketRejected));
            }

            public ProductionValidatedDirectPathTicket ValidateIncomingTicket(
                DirectPathTicketV1 ticket,
                Guid initiatorDeviceId,
                Guid connectionId,
                ProductionDirectTransportServerConfig value) =>
                validator.ValidateIncomingTicket(
                    ticket, initiatorDeviceId, connectionId, value);

            public bool Start() => true;

            public bool TrySend(TransportSignalEnvelopeV1 envelope) =>
                outgoing.Writer.TryWrite(envelope);

            public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                while (await incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    while (incoming.Reader.TryRead(out var value)) yield return value;
            }

            public void Inject(TransportSignalEnvelopeV1 value) => incoming.Writer.TryWrite(value);

            public Task<TransportSignalEnvelopeV1> ReadOutboundAsync() =>
                outgoing.Reader.ReadAsync().AsTask();

            public async ValueTask DisposeAsync()
            {
                incoming.Writer.TryComplete();
                outgoing.Writer.TryComplete();
                await validator.DisposeAsync().ConfigureAwait(false);
            }
        }

        private sealed class FakeNative : IProductionDirectShadowNative
        {
            private readonly ConcurrentQueue<ProductionDirectNativeEvent> events = new();
            private readonly byte[] pcSigningPublicKey;
            private readonly byte[] pairwiseKey;
            private readonly byte[] signRequest =
                SHA256.HashData(Encoding.ASCII.GetBytes("peer-proof-transcript"));
            private ulong epoch;
            private Guid attemptId;
            private long disposed;

            public FakeNative(byte[] pcSigningPublicKey, byte[] pairwiseKey)
            {
                this.pcSigningPublicKey = pcSigningPublicKey?.ToArray() ??
                    throw new ArgumentNullException(nameof(pcSigningPublicKey));
                this.pairwiseKey = pairwiseKey?.ToArray() ??
                    throw new ArgumentNullException(nameof(pairwiseKey));
            }

            public ProductionDirectNativeSnapshot Snapshot => new()
            {
                State = ProductionDirectNativeState.RelayOnly,
                NativeAvailable = Volatile.Read(ref disposed) == 0,
                Disposed = Volatile.Read(ref disposed) != 0
            };

            public int MarkCandidatesCalls { get; private set; }
            public int NominateCalls { get; private set; }
            public int SignatureCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public ProductionDirectNativeRole LastRole { get; private set; }
            public bool SignatureVerified { get; private set; }
            public bool DirectionalKeysVerified { get; private set; }

            public static byte[] SharedBinding() =>
                SHA256.HashData(Encoding.ASCII.GetBytes("shared-path-binding"));

            public ProductionDirectNativeCommandResult Gather(
                ProductionDirectNativeGatherRequest request)
            {
                epoch = request.Epoch;
                attemptId = request.AttemptId;
                var bound = new ProductionDirectNativeEndpoint(
                    IPAddress.Parse("192.168.50.2"), 50_002);
                events.Enqueue(new ProductionDirectNativeEvent
                {
                    Kind = ProductionDirectNativeEventKind.IdentityReady,
                    State = ProductionDirectNativeState.Gathering,
                    Epoch = epoch,
                    AttemptId = attemptId,
                    Generation = 1,
                    Payload = Encoding.ASCII.GetBytes("pc-temporary-certificate")
                });
                events.Enqueue(new ProductionDirectNativeEvent
                {
                    Kind = ProductionDirectNativeEventKind.CandidatesReady,
                    State = ProductionDirectNativeState.Gathering,
                    Epoch = epoch,
                    AttemptId = attemptId,
                    Generation = 2,
                    Endpoint = bound,
                    Candidates = new[] { bound },
                    Payload = Array.Empty<byte>()
                });
                return ProductionDirectNativeCommandResult.Accepted;
            }

            public ProductionDirectNativeCommandResult MarkCandidatesExchanged(
                ulong valueEpoch,
                Guid valueAttemptId,
                ulong generation,
                IReadOnlyList<ProductionDirectNativeEndpoint> candidates)
            {
                MarkCandidatesCalls++;
                return valueEpoch == epoch && valueAttemptId == attemptId &&
                    generation == 2 && candidates.Count == 1
                    ? ProductionDirectNativeCommandResult.Accepted
                    : ProductionDirectNativeCommandResult.RelayFallback;
            }

            public ProductionDirectNativeCommandResult Nominate(
                ProductionDirectNativeStartRequest request)
            {
                NominateCalls++;
                LastRole = request.Role;
                var expectedLocal = ProductionDirectTransportCrypto.DeriveProbeKey(
                    pairwiseKey,
                    request.ConnectionId,
                    request.Epoch,
                    request.NominationId,
                    request.LocalDeviceId,
                    request.PeerDeviceId);
                var expectedPeer = ProductionDirectTransportCrypto.DeriveProbeKey(
                    pairwiseKey,
                    request.ConnectionId,
                    request.Epoch,
                    request.NominationId,
                    request.PeerDeviceId,
                    request.LocalDeviceId);
                try
                {
                    DirectionalKeysVerified =
                        CryptographicOperations.FixedTimeEquals(
                            expectedLocal,
                            request.LocalSendProbeHmacKey) &&
                        CryptographicOperations.FixedTimeEquals(
                            expectedPeer,
                            request.PeerReceiveProbeHmacKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedLocal);
                    CryptographicOperations.ZeroMemory(expectedPeer);
                }
                if (request.Epoch != epoch || request.AttemptId != attemptId ||
                    request.Role != ProductionDirectNativeRole.Responder ||
                    request.PeerCertificateSha256?.Length != 32 ||
                    request.PeerDeviceSigningPublicKey?.Length != 32 ||
                    request.LocalSendProbeHmacKey?.Length != 32 ||
                    request.PeerReceiveProbeHmacKey?.Length != 32 ||
                    !DirectionalKeysVerified ||
                    request.LocalSendProbeHmacKey.SequenceEqual(request.PeerReceiveProbeHmacKey) ||
                    request.LocalProbeNonce?.Length != 16 ||
                    !request.LocalProbeNonce.SequenceEqual(request.PeerProbeNonce) ||
                    request.LocalProofNonce?.Length != 32 ||
                    request.LocalProofNonce.All(value => value == 0))
                    return ProductionDirectNativeCommandResult.RelayFallback;
                events.Enqueue(new ProductionDirectNativeEvent
                {
                    Kind = ProductionDirectNativeEventKind.SignRequest,
                    State = ProductionDirectNativeState.Authenticating,
                    Epoch = epoch,
                    AttemptId = attemptId,
                    Generation = 3,
                    RequestId = 91,
                    Payload = (byte[])signRequest.Clone()
                });
                events.Enqueue(new ProductionDirectNativeEvent
                {
                    Kind = ProductionDirectNativeEventKind.ShadowReady,
                    State = ProductionDirectNativeState.ShadowDirect,
                    Epoch = epoch,
                    AttemptId = attemptId,
                    Generation = 4,
                    Endpoint = request.RemoteCandidate,
                    Payload = SharedBinding()
                });
                return ProductionDirectNativeCommandResult.Accepted;
            }

            public ProductionDirectNativeCommandResult Cancel(ulong valueEpoch, Guid valueAttemptId) =>
                valueEpoch == epoch && valueAttemptId == attemptId
                    ? ProductionDirectNativeCommandResult.Accepted
                    : ProductionDirectNativeCommandResult.IgnoredStale;

            public ProductionDirectNativeCommandResult NetworkChanged(
                ulong valueEpoch,
                Guid valueAttemptId) => Cancel(valueEpoch, valueAttemptId);

            public ProductionDirectNativeCommandResult SubmitSignature(
                ulong valueEpoch,
                Guid valueAttemptId,
                ulong requestId,
                byte[] signature)
            {
                SignatureCalls++;
                SignatureVerified = VerifySignature(pcSigningPublicKey, signRequest, signature);
                return valueEpoch == epoch && valueAttemptId == attemptId &&
                    requestId == 91 && SignatureVerified
                    ? ProductionDirectNativeCommandResult.Accepted
                    : ProductionDirectNativeCommandResult.RelayFallback;
            }

            private static bool VerifySignature(byte[] publicKey, byte[] message, byte[] signature)
            {
                if (publicKey?.Length != 32 || message == null || signature?.Length != 64)
                    return false;
                var verifier = new Ed25519Signer();
                verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
                verifier.BlockUpdate(message, 0, message.Length);
                return verifier.VerifySignature(signature);
            }

            public IReadOnlyList<ProductionDirectNativeEvent> Poll(int maximumEvents = 32)
            {
                var result = new List<ProductionDirectNativeEvent>();
                while (result.Count < maximumEvents && events.TryDequeue(out var value))
                    result.Add(value);
                return result;
            }

            public void InjectFailure()
            {
                events.Enqueue(new ProductionDirectNativeEvent
                {
                    Kind = ProductionDirectNativeEventKind.DirectFailed,
                    State = ProductionDirectNativeState.RelayOnly,
                    Reason = ProductionDirectNativeReason.Quic,
                    Epoch = epoch,
                    AttemptId = attemptId,
                    Generation = 5,
                    Payload = Array.Empty<byte>()
                });
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    DisposeCalls++;
                    CryptographicOperations.ZeroMemory(pcSigningPublicKey);
                    CryptographicOperations.ZeroMemory(pairwiseKey);
                    CryptographicOperations.ZeroMemory(signRequest);
                }
            }
        }
    }
}

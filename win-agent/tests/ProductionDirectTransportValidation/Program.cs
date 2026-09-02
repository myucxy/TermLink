using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class Program
    {
        private const uint StunMagicCookie = 0x2112A442;

        private static async Task<int> Main()
        {
            try
            {
                ValidateRelayFallbackAndFoundationBoundary();
                ValidateFeatureAdvertisementDefaultsOff();
                DirectTransportDiagnosticsValidation.Validate();
                ValidateEpochRateAndFailureCooldown();
                ValidatePeerIsolationAndStaleGenerationCallbacks();
                ValidateCandidateFilteringAndSocketLease();
                ValidateStunTransactionAndXorMappedAddress();
                ValidateCanonicalTicketCrossLanguageVector();
                DirectTransportCryptoVectorValidation.Validate();
                DirectTransportMultipairV2Validation.Validate();
                ValidateTicketSignalAndProbeCryptography();
                await ValidateBidirectionalUdpProbeAsync().ConfigureAwait(false);
                await ProductionDirectShadowRuntimeValidation.ValidateAsync().ConfigureAwait(false);
                await ProductionDirectMultipairRuntimeValidation.ValidateAsync().ConfigureAwait(false);
                Console.WriteLine("Production direct transport foundation validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateRelayFallbackAndFoundationBoundary()
        {
            var supervisor = new ProductionDirectTransportSupervisor();
            supervisor.ObserveRelayConnected(true);
            Require(supervisor.TryBeginAttempt(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    out var attempt,
                    out var rejection) &&
                    rejection.Length == 0,
                "A healthy Relay did not permit direct gathering.");
            supervisor.MarkGatheringComplete(attempt);
            var authenticatedProbeSha256 = SHA256.HashData(
                Encoding.ASCII.GetBytes("authenticated-probe-transcript"));
            supervisor.MarkConnectivityProbeVerified(attempt, authenticatedProbeSha256);
            var verified = supervisor.Snapshot();
            Require(verified.State == ProductionDirectPathState.ProbeVerifiedNotDirect &&
                    verified.RelayConnected && !verified.DirectRoutingPermitted &&
                    verified.AttemptGeneration == attempt.Generation &&
                    verified.AuthenticatedProbeSha256.SequenceEqual(authenticatedProbeSha256),
                "A verified probe escaped the Relay-only foundation boundary.");

            Require(supervisor.TryFallbackAttempt(attempt, "PROBE_ONLY_COMPLETE"),
                "The current direct attempt could not synchronously fall back.");
            var fallback = supervisor.Snapshot();
            Require(fallback.State == ProductionDirectPathState.RelayOnly && fallback.RelayConnected &&
                    fallback.ConnectionId == Guid.Empty && fallback.PeerDeviceId == Guid.Empty &&
                    fallback.PathEpoch == 0 && fallback.FallbackReason == "PROBE_ONLY_COMPLETE" &&
                    fallback.AuthenticatedProbeSha256.Length == 0 &&
                    !supervisor.TryFallbackAttempt(attempt, "STALE_CALLBACK"),
                "A direct failure did not synchronously return to an intact Relay.");

            var invalidOrder = new ProductionDirectTransportSupervisor();
            invalidOrder.ObserveRelayConnected(true);
            Require(invalidOrder.TryBeginAttempt(
                    Guid.NewGuid(), Guid.NewGuid(), 1, out var currentAttempt, out _),
                "Ordered-transition fixture could not start.");
            RequireThrows<InvalidOperationException>(
                () => invalidOrder.MarkGatheringComplete(default),
                "A missing attempt token was accepted.");
            invalidOrder.MarkGatheringComplete(currentAttempt);
            RequireThrows<ArgumentException>(
                () => invalidOrder.MarkConnectivityProbeVerified(currentAttempt, new byte[32]),
                "An empty authenticated-probe digest was accepted.");
            invalidOrder.ObserveRelayConnected(false);
            Require(invalidOrder.Snapshot().State == ProductionDirectPathState.RelayOnly,
                "Loss of the mandatory Relay fallback left negotiation active.");
        }

        private static void ValidateFeatureAdvertisementDefaultsOff()
        {
            Require(!ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(null) &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(string.Empty) &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("0") &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("false") &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("yes") &&
                    ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(" 1 ") &&
                    ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(" TrUe "),
                "The Direct shadow runtime flag was not strict and default-off.");
            using var hello = JsonDocument.Parse(
                ProductionAgentRuntime.BuildHelloPayloadWithFeatures(
                    terminalSnapshotEnabled: true,
                    typedInputEnabled: true));
            var capabilities = hello.RootElement.GetProperty("capabilities")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Require(!capabilities.Any(value =>
                    value?.Contains("direct", StringComparison.OrdinalIgnoreCase) == true),
                "A Direct transport capability leaked into the Relay HELLO.");
        }

        private static void ValidateEpochRateAndFailureCooldown()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var supervisor = new ProductionDirectTransportSupervisor(() => now);
            supervisor.ObserveRelayConnected(true);
            var peer = Guid.NewGuid();
            for (ulong epoch = 1; epoch <= 3; epoch++)
            {
                Require(supervisor.TryBeginAttempt(
                        peer, Guid.NewGuid(), epoch, out var attempt, out _),
                    "A valid monotonic epoch was rejected.");
                Require(supervisor.TryFallbackAttempt(attempt, "PROBE_TIMEOUT"),
                    "A valid attempt fallback was rejected.");
                now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            }
            Require(!supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out _, out var cooldown) &&
                    cooldown == "PATH_COOLDOWN",
                "Three direct failures did not enter the bounded cooldown.");
            now = now.Add(ProductionDirectTransportSupervisor.FailureCooldown);
            Require(supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out var fourthAttempt, out _),
                "The direct-path cooldown did not expire.");
            Require(supervisor.TryFallbackAttempt(fourthAttempt, "PROBE_TIMEOUT"),
                "The post-cooldown attempt could not fall back.");
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(!supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out _, out var stale) &&
                    stale == "STALE_PATH_EPOCH",
                "A stale path epoch was accepted after fallback.");
            Require(ProductionDirectTransportSupervisor.ValidReasonCode("NETWORK_CHANGED") &&
                    !ProductionDirectTransportSupervisor.ValidReasonCode("network changed") &&
                    !ProductionDirectTransportSupervisor.ValidReasonCode(new string('A', 65)),
                "Fallback reason-code bounds are not fail closed.");
        }

        private static void ValidatePeerIsolationAndStaleGenerationCallbacks()
        {
            var now = new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);
            var supervisor = new ProductionDirectTransportSupervisor(() => now);
            supervisor.ObserveRelayConnected(true);
            var firstPeer = Guid.NewGuid();
            var secondPeer = Guid.NewGuid();
            Require(supervisor.TryBeginAttempt(
                    firstPeer, Guid.NewGuid(), 100, out var firstToken, out _),
                "The first peer could not start an attempt.");
            Require(supervisor.TryFallbackAttempt(firstToken, "PROBE_TIMEOUT"),
                "The first peer could not return to Relay.");
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(supervisor.TryBeginAttempt(
                    secondPeer, Guid.NewGuid(), 1, out var secondToken, out _),
                "One peer's high epoch incorrectly rejected another peer's epoch.");
            var secondBeforeStaleCallbacks = supervisor.Snapshot();
            RequireThrows<InvalidOperationException>(
                () => supervisor.MarkGatheringComplete(firstToken),
                "A stale attempt callback advanced a newer peer attempt.");
            Require(!supervisor.TryFallbackAttempt(firstToken, "STALE_CALLBACK"),
                "A stale attempt callback retired a newer peer attempt.");
            var secondAfterStaleCallbacks = supervisor.Snapshot();
            Require(secondAfterStaleCallbacks.State == secondBeforeStaleCallbacks.State &&
                    secondAfterStaleCallbacks.PeerDeviceId == secondPeer &&
                    secondAfterStaleCallbacks.ConnectionId == secondBeforeStaleCallbacks.ConnectionId &&
                    secondAfterStaleCallbacks.PathEpoch == 1 &&
                    secondAfterStaleCallbacks.AttemptGeneration == secondToken.Generation,
                "A stale attempt callback mutated the current generation identity.");
            Require(supervisor.TryFallbackAttempt(secondToken, "PROBE_TIMEOUT"),
                "The second peer could not return to Relay.");

            for (ulong epoch = 101; epoch <= 102; epoch++)
            {
                now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
                Require(supervisor.TryBeginAttempt(
                        firstPeer, Guid.NewGuid(), epoch, out var token, out _),
                    "The first peer could not accumulate its own failure policy.");
                Require(supervisor.TryFallbackAttempt(token, "PROBE_TIMEOUT"),
                    "The first peer failure was not recorded.");
            }
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(!supervisor.TryBeginAttempt(
                    firstPeer, Guid.NewGuid(), 104, out _, out var firstCooldown) &&
                    firstCooldown == "PATH_COOLDOWN",
                "Three failures did not cool down the affected peer.");
            Require(supervisor.TryBeginAttempt(
                    secondPeer, Guid.NewGuid(), 2, out var unaffectedToken, out _),
                "One peer's cooldown incorrectly blocked another peer.");
            Require(supervisor.TryFallbackAttempt(unaffectedToken, "PROBE_TIMEOUT"),
                "The unaffected peer could not return to Relay.");
        }

        private static void ValidateCandidateFilteringAndSocketLease()
        {
            Require(!ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Loopback) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.IPv6Loopback) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("169.254.1.2")) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("224.0.0.1")) &&
                    ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("192.168.1.10")) &&
                    ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("2001:db8::1")),
                "Host candidate filtering accepted an unsafe address or rejected a routable address.");

            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(value => value.GetIPProperties().UnicastAddresses)
                .Select(value => value.Address)
                .Where(ProductionDirectCandidateCollector.IsAllowedHostAddress)
                .Take(1)
                .ToArray();
            if (localAddresses.Length == 0) return;
            var leases = ProductionDirectCandidateCollector.BindHostCandidates(
                localAddresses,
                DateTime.UtcNow.AddMinutes(1));
            try
            {
                Require(leases.Count == 1, "An active routable interface did not produce a host candidate.");
                var lease = leases[0];
                var endpoint = (IPEndPoint)lease.Socket.LocalEndPoint;
                Require(lease.Candidate.Kind == DirectCandidateKindV1.Host &&
                        lease.Candidate.Carrier == DirectCarrierV1.UdpQuic &&
                        lease.Candidate.Port == endpoint.Port &&
                        lease.Candidate.IpAddress.Span.SequenceEqual(endpoint.Address.GetAddressBytes()),
                    "The retained UDP socket does not match its advertised candidate.");
            }
            finally
            {
                foreach (var lease in leases) lease.Dispose();
            }
        }

        private static void ValidateStunTransactionAndXorMappedAddress()
        {
            var transaction = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
            var request = ProductionStunClient.CreateBindingRequest(transaction);
            Require(request.Length == 20 && BinaryPrimitives.ReadUInt16BigEndian(request) == 1 &&
                    BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4)) == StunMagicCookie &&
                    request.AsSpan(8).SequenceEqual(transaction),
                "RFC 8489 Binding request framing is invalid.");

            var expected = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
            var response = CreateStunIPv4Success(transaction, expected);
            var parsed = ProductionStunClient.ParseBindingSuccess(response, transaction);
            Require(parsed.Equals(expected), "XOR-MAPPED-ADDRESS was not decoded byte-exactly.");

            var spoofed = (byte[])transaction.Clone();
            spoofed[0] ^= 0xff;
            RequireThrows<InvalidOperationException>(
                () => ProductionStunClient.ParseBindingSuccess(response, spoofed),
                "A STUN response with the wrong transaction ID was accepted.");
            response[^1] ^= 0x01;
            var changed = ProductionStunClient.ParseBindingSuccess(response, transaction);
            Require(!changed.Equals(expected),
                "The STUN parser ignored a changed authenticated address field.");
        }

        private static byte[] CreateStunIPv4Success(byte[] transaction, IPEndPoint endpoint)
        {
            var response = new byte[32];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 0x0101);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4, 4), StunMagicCookie);
            transaction.CopyTo(response, 8);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20, 2), 0x0020);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22, 2), 8);
            response[25] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(
                response.AsSpan(26, 2),
                (ushort)(endpoint.Port ^ (StunMagicCookie >> 16)));
            var address = endpoint.Address.GetAddressBytes();
            Span<byte> cookie = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, StunMagicCookie);
            for (var index = 0; index < 4; index++)
                response[28 + index] = (byte)(address[index] ^ cookie[index]);
            return response;
        }

        private static void ValidateTicketSignalAndProbeCryptography()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = Guid.NewGuid();
            var keyId = Encoding.ASCII.GetBytes("direct-key-v1");
            using var server = DeviceIdentity.Generate();
            var ticket = new DirectPathTicketV1
            {
                FormatVersion = 1,
                TicketId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                AccountId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                InitiatorDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                ResponderDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = 7,
                IssuedAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                ExpiresAtMs = new DateTimeOffset(now.AddMinutes(2)).ToUnixTimeMilliseconds(),
                ServerNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                ServerSigningKeyId = ByteString.CopyFrom(keyId)
            };
            ticket.AllowedCarriers.Add(DirectCarrierV1.UdpQuic);
            var claims = ticket.ToByteArray();
            var ticketDomain = Encoding.ASCII.GetBytes("TERMLINK-DIRECT-TICKET-V1\n");
            var signed = ticketDomain.Concat(claims).ToArray();
            ticket.ServerSignature = ByteString.CopyFrom(server.Sign(signed));
            ProductionDirectTransportCrypto.ValidateTicket(
                ticket,
                server.SigningPublicKey,
                keyId,
                local,
                peer,
                now);
            var tamperedTicket = ticket.Clone();
            tamperedTicket.PathEpoch++;
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.ValidateTicket(
                    tamperedTicket,
                    server.SigningPublicKey,
                    keyId,
                    local,
                    peer,
                    now),
                "A modified direct ticket retained authority.");

            var pairwise = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var signal = new TransportSignalEnvelopeV1
            {
                ProtocolVersion = 1,
                SignalType = TransportSignalTypeV1.PathOffer,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
                PathEpoch = 7,
                SignalSeq = 1,
                SignalAck = 0,
                SentAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                KeyId = ByteString.CopyFromUtf8("signal-v1")
            };
            var plaintext = Encoding.UTF8.GetBytes("opaque-candidate-payload");
            var encrypted = ProductionDirectTransportCrypto.EncryptSignal(
                pairwise,
                signal,
                plaintext);
            var decrypted = ProductionDirectTransportCrypto.DecryptSignal(pairwise, encrypted);
            Require(decrypted.SequenceEqual(plaintext),
                "Independent transport signalling AEAD did not round-trip.");
            var changedSignal = encrypted.Clone();
            var changedCipher = changedSignal.Ciphertext.ToByteArray();
            changedCipher[0] ^= 1;
            changedSignal.Ciphertext = ByteString.CopyFrom(changedCipher);
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.DecryptSignal(pairwise, changedSignal),
                "Modified transport signal ciphertext was accepted.");

            var probe = new DirectConnectivityDatagramV1
            {
                ProtocolVersion = 1,
                Kind = DirectConnectivityDatagramKindV1.Probe,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = 7,
                TicketId = ticket.TicketId,
                NominationId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                Pair = new DirectCandidatePairV1
                {
                    InitiatorCandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                    ResponderCandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16))
                },
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                ProbeNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                SentAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds()
            };
            var packet = ProductionDirectTransportCrypto.CreateProbePacket(pairwise, probe, peer);
            var verified = ProductionDirectTransportCrypto.ValidateProbePacket(
                pairwise,
                packet,
                local,
                peer,
                connection,
                7,
                now);
            Require(verified.Kind == DirectConnectivityDatagramKindV1.Probe &&
                    verified.AntiAmplificationPadding.Length == 16 &&
                    verified.AuthTag.Length == 16 &&
                    DirectConnectivityDatagramV1.AntiAmplificationPaddingFieldNumber == 12 &&
                    DirectConnectivityDatagramV1.AuthTagFieldNumber == 13,
                "Authenticated UDP probe fields 12/13 do not match the wire contract.");

            var ack = verified.Clone();
            ack.Kind = DirectConnectivityDatagramKindV1.ProbeAck;
            ack.SenderDeviceId = ByteString.CopyFrom(GuidBytes(peer));
            ack.ResponseNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16));
            ack.AntiAmplificationPadding = ByteString.Empty;
            ack.AuthTag = ByteString.Empty;
            var ackPacket = ProductionDirectTransportCrypto.CreateProbePacket(pairwise, ack, local);
            var verifiedAck = ProductionDirectTransportCrypto.ValidateProbePacket(
                pairwise,
                ackPacket,
                peer,
                local,
                connection,
                7,
                now);
            Require(verifiedAck.Kind == DirectConnectivityDatagramKindV1.ProbeAck &&
                    verifiedAck.ResponseNonce.Length == 16 &&
                    verifiedAck.AntiAmplificationPadding.Length == 0 &&
                    ackPacket.Length <= packet.Length,
                "PROBE_ACK violated the authenticated anti-amplification contract.");
            var paddedAck = ack.Clone();
            paddedAck.AntiAmplificationPadding = ByteString.CopyFrom(new byte[16]);
            RequireThrows<InvalidDataException>(
                () => ProductionDirectTransportCrypto.CreateProbePacket(pairwise, paddedAck, local),
                "A padded PROBE_ACK was accepted.");
            var extremeTimestampProbe = probe.Clone();
            extremeTimestampProbe.SentAtMs = long.MaxValue;
            var extremeTimestampPacket = ProductionDirectTransportCrypto.CreateProbePacket(
                pairwise,
                extremeTimestampProbe,
                peer);
            RequireThrows<InvalidDataException>(
                () => ProductionDirectTransportCrypto.ValidateProbePacket(
                    pairwise,
                    extremeTimestampPacket,
                    local,
                    peer,
                    connection,
                    7,
                    now),
                "An extreme authenticated probe timestamp bypassed the freshness window.");

            packet[^1] ^= 1;
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.ValidateProbePacket(
                    pairwise,
                    packet,
                    local,
                    peer,
                    connection,
                    7,
                    now),
                "Modified UDP connectivity probe was accepted.");
        }

        private static void ValidateCanonicalTicketCrossLanguageVector()
        {
            var vectorPath = FindRepositoryFile(
                "protocol", "test-vectors", "direct-transport-ticket-v1.json");
            using var document = JsonDocument.Parse(File.ReadAllBytes(vectorPath));
            var root = document.RootElement;
            var publicKey = DecodeBase64Url(
                root.GetProperty("server_public_key_base64url").GetString());
            var ticketWire = DecodeBase64Url(
                root.GetProperty("ticket_protobuf_base64url").GetString());
            var expectedDigest = root.GetProperty("ticket_sha256").GetString();
            Require(string.Equals(
                    Convert.ToHexString(SHA256.HashData(ticketWire)).ToLowerInvariant(),
                    expectedDigest,
                    StringComparison.Ordinal),
                "The canonical direct-ticket vector digest changed.");
            var ticket = DirectPathTicketV1.Parser.ParseFrom(ticketWire);
            Require(ticketWire.SequenceEqual(ticket.ToByteArray()),
                "The server direct ticket is not canonical generated-protobuf wire.");
            var initiator = Guid.Parse(root.GetProperty("initiator_device_id").GetString());
            var responder = Guid.Parse(root.GetProperty("responder_device_id").GetString());
            var keyId = Encoding.UTF8.GetBytes(
                root.GetProperty("server_signing_key_id").GetString());
            var now = DateTimeOffset.FromUnixTimeMilliseconds(
                root.GetProperty("issued_at_ms").GetInt64() + 1_000).UtcDateTime;
            ProductionDirectTransportCrypto.ValidateTicket(
                ticket,
                publicKey,
                keyId,
                initiator,
                responder,
                now);
        }

        private static string FindRepositoryFile(params string[] segments)
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(Path.GetFullPath(start));
                while (current != null)
                {
                    var parts = new string[segments.Length + 1];
                    parts[0] = current.FullName;
                    Array.Copy(segments, 0, parts, 1, segments.Length);
                    var candidate = Path.Combine(parts);
                    if (File.Exists(candidate)) return candidate;
                    current = current.Parent;
                }
            }
            throw new FileNotFoundException(
                "The canonical direct-transport ticket vector was not found.");
        }

        private static byte[] DecodeBase64Url(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('='))
                throw new InvalidDataException("A canonical unpadded base64url value is required.");
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return Convert.FromBase64String(padded);
        }

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static async Task ValidateBidirectionalUdpProbeAsync()
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(value => value.GetIPProperties().UnicastAddresses)
                .Select(value => value.Address)
                .FirstOrDefault(ProductionDirectCandidateCollector.IsAllowedHostAddress);
            if (address == null) return;
            var firstSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var secondSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                firstSocket.DualMode = false;
                secondSocket.DualMode = false;
            }
            firstSocket.Bind(new IPEndPoint(address, 0));
            secondSocket.Bind(new IPEndPoint(address, 0));
            using var first = new ProductionDirectUdpCandidateLease(
                firstSocket,
                CandidateFor((IPEndPoint)firstSocket.LocalEndPoint));
            using var second = new ProductionDirectUdpCandidateLease(
                secondSocket,
                CandidateFor((IPEndPoint)secondSocket.LocalEndPoint));
            var retainedEndpoint = (IPEndPoint)first.Socket.LocalEndPoint;
            using var stunSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                stunSocket.DualMode = false;
            stunSocket.Bind(new IPEndPoint(address, 0));
            using var stunTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var mappedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
            var stunResponse = RespondToOneStunRequestAsync(
                stunSocket,
                mappedEndpoint,
                stunTimeout.Token);
            var discovered = await ProductionStunClient.QueryServerReflexiveAsync(
                first,
                (IPEndPoint)stunSocket.LocalEndPoint,
                TimeSpan.FromSeconds(2),
                stunTimeout.Token).ConfigureAwait(false);
            await stunResponse.ConfigureAwait(false);
            Require(discovered.Equals(mappedEndpoint) &&
                    ((IPEndPoint)first.Socket.LocalEndPoint).Equals(retainedEndpoint),
                "STUN did not retain the exact candidate socket and port for probing.");

            var firstDevice = Guid.NewGuid();
            var secondDevice = Guid.NewGuid();
            var connection = Guid.NewGuid();
            var ticket = RandomNumberGenerator.GetBytes(16);
            var nomination = RandomNumberGenerator.GetBytes(16);
            var pair = new DirectCandidatePairV1
            {
                InitiatorCandidateId = first.Candidate.CandidateId,
                ResponderCandidateId = second.Candidate.CandidateId
            };
            var firstProbe = ProbeFor(
                firstDevice,
                connection,
                ticket,
                nomination,
                pair,
                9);
            var secondProbe = ProbeFor(
                secondDevice,
                connection,
                ticket,
                nomination,
                pair,
                9);
            var pairwise = RandomNumberGenerator.GetBytes(32);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var heldReceiveProtocol = await first.ClaimReceiveProtocolAsync(
                probeTimeout.Token).ConfigureAwait(false);
            var blockedProbeElapsed = Stopwatch.StartNew();
            var blockedProbe = await ProductionDirectConnectivityProbe.ProbeAsync(
                first,
                (IPEndPoint)second.Socket.LocalEndPoint,
                firstProbe,
                secondDevice,
                pairwise,
                probeTimeout.Token).ConfigureAwait(false);
            Require(!blockedProbe && blockedProbeElapsed.Elapsed < TimeSpan.FromSeconds(3),
                "Waiting for the receive owner exceeded the probe's hard operation budget.");
            heldReceiveProtocol.Dispose();
            var firstProbeTask = ProductionDirectConnectivityProbe.ProbeAsync(
                first,
                (IPEndPoint)second.Socket.LocalEndPoint,
                firstProbe,
                secondDevice,
                pairwise,
                probeTimeout.Token);
            var secondProbeTask = ProductionDirectConnectivityProbe.ProbeAsync(
                second,
                (IPEndPoint)first.Socket.LocalEndPoint,
                secondProbe,
                firstDevice,
                pairwise,
                probeTimeout.Token);
            var outcomes = await Task.WhenAll(
                firstProbeTask,
                secondProbeTask).ConfigureAwait(false);
            Require(outcomes.All(value => value),
                "Two retained candidate sockets did not complete authenticated simultaneous UDP probing.");
            Require(((IPEndPoint)first.Socket.LocalEndPoint).Equals(retainedEndpoint),
                "The candidate port changed between STUN and authenticated probing.");
        }

        private static async Task RespondToOneStunRequestAsync(
            Socket server,
            IPEndPoint mappedEndpoint,
            CancellationToken token)
        {
            var request = new byte[64];
            EndPoint remote = server.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(IPAddress.Any, 0)
                : new IPEndPoint(IPAddress.IPv6Any, 0);
            var received = await server.ReceiveFromAsync(
                request,
                SocketFlags.None,
                remote,
                token).ConfigureAwait(false);
            if (received.ReceivedBytes != 20)
                throw new InvalidOperationException("The local STUN fixture received an invalid request.");
            var response = CreateStunIPv4Success(
                request.AsSpan(8, 12).ToArray(),
                mappedEndpoint);
            var wrongTransaction = request.AsSpan(8, 12).ToArray();
            wrongTransaction[0] ^= 0xff;
            var delayedResponse = CreateStunIPv4Success(
                wrongTransaction,
                mappedEndpoint);
            await server.SendToAsync(
                delayedResponse,
                SocketFlags.None,
                received.RemoteEndPoint,
                token).ConfigureAwait(false);
            using (var unrelated = new Socket(
                server.AddressFamily,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                if (server.AddressFamily == AddressFamily.InterNetworkV6)
                    unrelated.DualMode = false;
                var serverLocal = (IPEndPoint)server.LocalEndPoint;
                unrelated.Bind(new IPEndPoint(serverLocal.Address, 0));
                await unrelated.SendToAsync(
                    response,
                    SocketFlags.None,
                    received.RemoteEndPoint,
                    token).ConfigureAwait(false);
            }
            await server.SendToAsync(
                response,
                SocketFlags.None,
                received.RemoteEndPoint,
                token).ConfigureAwait(false);
        }

        private static DirectCandidateV1 CandidateFor(IPEndPoint endpoint)
        {
            return new DirectCandidateV1
            {
                CandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                Kind = DirectCandidateKindV1.Host,
                Carrier = DirectCarrierV1.UdpQuic,
                IpAddress = ByteString.CopyFrom(endpoint.Address.GetAddressBytes()),
                Port = checked((uint)endpoint.Port),
                Priority = 1,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
            };
        }

        private static DirectConnectivityDatagramV1 ProbeFor(
            Guid sender,
            Guid connection,
            byte[] ticket,
            byte[] nomination,
            DirectCandidatePairV1 pair,
            ulong epoch)
        {
            return new DirectConnectivityDatagramV1
            {
                ProtocolVersion = 1,
                Kind = DirectConnectivityDatagramKindV1.Probe,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = epoch,
                TicketId = ByteString.CopyFrom(ticket),
                NominationId = ByteString.CopyFrom(nomination),
                Pair = pair.Clone(),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
                ProbeNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private static void RequireThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal static class ProductionDirectTransportCrypto
    {
        private static readonly byte[] TicketDomain = Encoding.ASCII.GetBytes(
            "TERMLINK-DIRECT-TICKET-V1\n");
        private static readonly byte[] SignalDomain = Encoding.ASCII.GetBytes(
            "TERMLINK-TRANSPORT-SIGNAL-AEAD-V1");
        private static readonly byte[] ProbeDomain = Encoding.ASCII.GetBytes(
            "TERMLINK-DIRECT-PROBE-HMAC-V1");
        private static readonly byte[] ProbeMacDomain = Encoding.ASCII.GetBytes(
            "TERMLINK-DIRECT-PROBE-HMAC-V1\0");
        private static readonly byte[] ProbePrefix = Encoding.ASCII.GetBytes("TLD1\n");
        private static readonly byte[] ProbeDomainV2 = Encoding.ASCII.GetBytes(
            "TERMLINK-DIRECT-PROBE-HMAC-V2");
        private static readonly byte[] PairProbeNonceDomainV2 = Encoding.ASCII.GetBytes(
            "TERMLINK-DIRECT-PAIR-PROBE-NONCE-V2\0");
        private const int MaximumSignalBytes = 64 << 10;
        private const int MaximumProbeBytes = 512;

        public static void ValidateTicket(
            DirectPathTicketV1 ticket,
            byte[] serverPublicKey,
            byte[] expectedSigningKeyId,
            Guid localDeviceId,
            Guid peerDeviceId,
            DateTime nowUtc)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            if (serverPublicKey == null || serverPublicKey.Length != 32)
                throw new ArgumentException("An Ed25519 ticket key is required.", nameof(serverPublicKey));
            if (expectedSigningKeyId == null || expectedSigningKeyId.Length is < 1 or > 64)
                throw new ArgumentException("A bounded ticket key ID is required.", nameof(expectedSigningKeyId));
            if (localDeviceId == Guid.Empty || peerDeviceId == Guid.Empty || localDeviceId == peerDeviceId)
                throw new ArgumentException("Two distinct direct-transport devices are required.");
            var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();
            if (ticket.FormatVersion != 1 || ticket.TicketId.Length != 16 ||
                ticket.AccountId.Length != 16 || ticket.InitiatorDeviceId.Length != 16 ||
                ticket.ResponderDeviceId.Length != 16 || ticket.ConnectionId.Length != 16 ||
                ticket.ServerNonce.Length != 16 || ticket.PathEpoch == 0 ||
                ticket.PathEpoch > long.MaxValue || ticket.ServerSignature.Length != 64 ||
                ticket.ServerSigningKeyId.Length is < 1 or > 64 ||
                !CryptographicOperations.FixedTimeEquals(
                    ticket.ServerSigningKeyId.Span,
                    expectedSigningKeyId) ||
                ticket.AllowedCarriers.Count is < 1 or > 2 ||
                ticket.AllowedCarriers.Any(value => value != DirectCarrierV1.UdpQuic) ||
                ticket.AllowedCarriers.Distinct().Count() != ticket.AllowedCarriers.Count ||
                ticket.IssuedAtMs <= 0 || ticket.ExpiresAtMs <= 0 ||
                ticket.IssuedAtMs > ticket.ExpiresAtMs ||
                ticket.ExpiresAtMs - ticket.IssuedAtMs > TimeSpan.FromMinutes(2).TotalMilliseconds ||
                now < ticket.IssuedAtMs - TimeSpan.FromSeconds(30).TotalMilliseconds ||
                now > ticket.ExpiresAtMs + TimeSpan.FromSeconds(30).TotalMilliseconds)
            {
                throw new InvalidDataException("Direct path ticket contract is invalid.");
            }
            var initiator = GuidFromNetworkBytes(ticket.InitiatorDeviceId.Span);
            var responder = GuidFromNetworkBytes(ticket.ResponderDeviceId.Span);
            if (!(initiator == localDeviceId && responder == peerDeviceId) &&
                !(initiator == peerDeviceId && responder == localDeviceId))
                throw new InvalidDataException("Direct path ticket device pair is invalid.");

            var claims = ticket.Clone();
            claims.ServerSignature = ByteString.Empty;
            var claimsBytes = claims.ToByteArray();
            var signed = Concat(TicketDomain, claimsBytes);
            try
            {
                var verifier = new Ed25519Signer();
                verifier.Init(false, new Ed25519PublicKeyParameters(serverPublicKey, 0));
                verifier.BlockUpdate(signed, 0, signed.Length);
                if (!verifier.VerifySignature(ticket.ServerSignature.ToByteArray()))
                    throw new CryptographicException("Direct path ticket signature is invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(claimsBytes);
                CryptographicOperations.ZeroMemory(signed);
            }
        }

        public static TransportSignalEnvelopeV1 EncryptSignal(
            byte[] pairwiseKey,
            TransportSignalEnvelopeV1 metadata,
            byte[] plaintext)
        {
            ValidateSignalMetadata(metadata, requireCiphertext: false);
            if (plaintext == null || plaintext.Length == 0 || plaintext.Length > MaximumSignalBytes - 256)
                throw new ArgumentException("A bounded signal plaintext is required.", nameof(plaintext));
            var result = metadata.Clone();
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            result.Nonce = ByteString.CopyFrom(nonce);
            result.Ciphertext = ByteString.Empty;
            var key = DeriveSignalKey(pairwiseKey, result);
            var aad = result.ToByteArray();
            try
            {
                result.Ciphertext = ByteString.CopyFrom(
                    ProcessChaCha(encrypt: true, key, nonce, aad, plaintext));
                if (result.CalculateSize() > MaximumSignalBytes)
                    throw new InvalidDataException("Encrypted transport signal exceeds its wire limit.");
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(aad);
            }
        }

        public static byte[] DecryptSignal(
            byte[] pairwiseKey,
            TransportSignalEnvelopeV1 envelope)
        {
            ValidateSignalMetadata(envelope, requireCiphertext: true);
            if (envelope.CalculateSize() > MaximumSignalBytes)
                throw new InvalidDataException("Encrypted transport signal exceeds its wire limit.");
            var metadata = envelope.Clone();
            metadata.Ciphertext = ByteString.Empty;
            var key = DeriveSignalKey(pairwiseKey, metadata);
            var aad = metadata.ToByteArray();
            var nonce = envelope.Nonce.ToByteArray();
            var ciphertext = envelope.Ciphertext.ToByteArray();
            try { return ProcessChaCha(false, key, nonce, aad, ciphertext); }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(aad);
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        public static byte[] CreateProbePacket(
            byte[] pairwiseKey,
            DirectConnectivityDatagramV1 datagram,
            Guid recipientDeviceId)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            if (recipientDeviceId == Guid.Empty ||
                GuidFromNetworkBytes(datagram.SenderDeviceId.Span) == recipientDeviceId)
                throw new ArgumentException("A distinct probe recipient is required.", nameof(recipientDeviceId));
            var unsigned = datagram.Clone();
            unsigned.AuthTag = ByteString.Empty;
            if (unsigned.Kind == DirectConnectivityDatagramKindV1.Probe &&
                unsigned.AntiAmplificationPadding.Length == 0)
                unsigned.AntiAmplificationPadding = ByteString.CopyFrom(
                    RandomNumberGenerator.GetBytes(16));
            ValidateProbeMetadata(unsigned, requireTag: false);
            var key = DeriveProbeKey(pairwiseKey, unsigned, recipientDeviceId);
            var body = unsigned.ToByteArray();
            var authenticated = Concat(ProbeMacDomain, body);
            try
            {
                var tag = HMACSHA256.HashData(key, authenticated).AsSpan(0, 16).ToArray();
                unsigned.AuthTag = ByteString.CopyFrom(tag);
                var encoded = unsigned.ToByteArray();
                var packet = Concat(ProbePrefix, encoded);
                if (packet.Length > MaximumProbeBytes)
                {
                    CryptographicOperations.ZeroMemory(packet);
                    throw new InvalidDataException("Direct connectivity probe exceeds its wire limit.");
                }
                return packet;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(body);
                CryptographicOperations.ZeroMemory(authenticated);
            }
        }

        public static DirectConnectivityDatagramV1 ValidateProbePacket(
            byte[] pairwiseKey,
            ReadOnlySpan<byte> packet,
            Guid expectedSenderDeviceId,
            Guid localRecipientDeviceId,
            Guid expectedConnectionId,
            ulong expectedPathEpoch,
            DateTime nowUtc)
        {
            if (packet.Length <= ProbePrefix.Length || packet.Length > MaximumProbeBytes ||
                !packet.Slice(0, ProbePrefix.Length).SequenceEqual(ProbePrefix))
                throw new InvalidDataException("Direct connectivity probe framing is invalid.");
            DirectConnectivityDatagramV1 parsed;
            var encoded = packet.Slice(ProbePrefix.Length).ToArray();
            try { parsed = DirectConnectivityDatagramV1.Parser.ParseFrom(encoded); }
            catch (InvalidProtocolBufferException error)
            {
                throw new InvalidDataException("Direct connectivity probe protobuf is invalid.", error);
            }
            ValidateProbeMetadata(parsed, requireTag: true);
            var canonical = KnownProbeFields(parsed, includeTag: true).ToByteArray();
            if (!encoded.AsSpan().SequenceEqual(canonical))
                throw new InvalidDataException(
                    "Direct connectivity probe is non-canonical or contains unknown fields.");
            var nowMilliseconds = new DateTimeOffset(
                DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var maximumClockSkewMilliseconds = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
            if (expectedSenderDeviceId == Guid.Empty || localRecipientDeviceId == Guid.Empty ||
                expectedSenderDeviceId == localRecipientDeviceId ||
                GuidFromNetworkBytes(parsed.SenderDeviceId.Span) != expectedSenderDeviceId ||
                GuidFromNetworkBytes(parsed.ConnectionId.Span) != expectedConnectionId ||
                parsed.PathEpoch != expectedPathEpoch ||
                parsed.SentAtMs < nowMilliseconds - maximumClockSkewMilliseconds ||
                parsed.SentAtMs > nowMilliseconds + maximumClockSkewMilliseconds)
                throw new InvalidDataException("Direct connectivity probe authority is invalid.");
            var expected = parsed.AuthTag.ToByteArray();
            var unsigned = KnownProbeFields(parsed, includeTag: false);
            var key = DeriveProbeKey(pairwiseKey, unsigned, localRecipientDeviceId);
            var body = unsigned.ToByteArray();
            var authenticated = Concat(ProbeMacDomain, body);
            var actual = HMACSHA256.HashData(key, authenticated).AsSpan(0, 16).ToArray();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new CryptographicException("Direct connectivity probe authentication failed.");
                return parsed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(encoded);
                CryptographicOperations.ZeroMemory(canonical);
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(body);
                CryptographicOperations.ZeroMemory(authenticated);
                CryptographicOperations.ZeroMemory(actual);
            }
        }

        private static void ValidateSignalMetadata(
            TransportSignalEnvelopeV1 value,
            bool requireCiphertext)
        {
            if (value == null || value.ProtocolVersion != 1 ||
                value.SignalType < TransportSignalTypeV1.PathOffer ||
                value.SignalType > TransportSignalTypeV1.NominationResultV2 ||
                value.ConnectionId.Length != 16 || value.SenderDeviceId.Length != 16 ||
                value.RecipientDeviceId.Length != 16 || value.PathEpoch == 0 ||
                value.PathEpoch > long.MaxValue || value.SignalSeq == 0 ||
                value.SignalSeq > long.MaxValue || value.SignalAck > long.MaxValue ||
                value.SentAtMs <= 0 || value.KeyId.Length is < 1 or > 64 ||
                (requireCiphertext
                    ? value.Nonce.Length != 12 || value.Ciphertext.Length < 16
                    : value.Nonce.Length != 0 || value.Ciphertext.Length != 0))
                throw new InvalidDataException("Transport signal metadata is invalid.");
        }

        private static byte[] DeriveSignalKey(
            byte[] pairwiseKey,
            TransportSignalEnvelopeV1 metadata)
        {
            ValidatePairwiseKey(pairwiseKey);
            var epoch = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(epoch, metadata.PathEpoch);
            var info = Concat(
                SignalDomain,
                metadata.ConnectionId.ToByteArray(),
                epoch,
                metadata.SenderDeviceId.ToByteArray(),
                metadata.RecipientDeviceId.ToByteArray(),
                metadata.KeyId.ToByteArray());
            var saltInput = Concat(SignalDomain, new byte[] { 0 });
            var salt = SHA256.HashData(saltInput);
            try { return Hkdf(pairwiseKey, salt, info); }
            finally
            {
                CryptographicOperations.ZeroMemory(epoch);
                CryptographicOperations.ZeroMemory(info);
                CryptographicOperations.ZeroMemory(saltInput);
                CryptographicOperations.ZeroMemory(salt);
            }
        }

        private static void ValidateProbeMetadata(
            DirectConnectivityDatagramV1 value,
            bool requireTag)
        {
            if (value == null || value.ProtocolVersion != 1 ||
                value.Kind < DirectConnectivityDatagramKindV1.Probe ||
                value.Kind > DirectConnectivityDatagramKindV1.ProbeAck ||
                value.ConnectionId.Length != 16 || value.TicketId.Length != 16 ||
                value.NominationId.Length != 16 || value.SenderDeviceId.Length != 16 ||
                value.ProbeNonce.Length != 16 || value.PathEpoch == 0 ||
                value.PathEpoch > long.MaxValue || value.SentAtMs <= 0 ||
                value.Pair == null || value.Pair.InitiatorCandidateId.Length != 16 ||
                value.Pair.ResponderCandidateId.Length != 16 ||
                (value.Kind == DirectConnectivityDatagramKindV1.Probe
                    ? value.ResponseNonce.Length != 0 || value.AntiAmplificationPadding.Length != 16
                    : value.ResponseNonce.Length != 16 || value.AntiAmplificationPadding.Length != 0) ||
                (requireTag ? value.AuthTag.Length != 16 : value.AuthTag.Length != 0))
                throw new InvalidDataException("Direct connectivity probe metadata is invalid.");
        }

        private static byte[] DeriveProbeKey(
            byte[] pairwiseKey,
            DirectConnectivityDatagramV1 datagram,
            Guid recipientDeviceId)
        {
            return DeriveProbeKey(
                pairwiseKey,
                GuidFromNetworkBytes(datagram.ConnectionId.Span),
                datagram.PathEpoch,
                GuidFromNetworkBytes(datagram.NominationId.Span),
                GuidFromNetworkBytes(datagram.SenderDeviceId.Span),
                recipientDeviceId);
        }

        internal static byte[] DeriveProbeKey(
            byte[] pairwiseKey,
            Guid connectionId,
            ulong pathEpoch,
            Guid nominationId,
            Guid senderDeviceId,
            Guid recipientDeviceId)
        {
            ValidatePairwiseKey(pairwiseKey);
            if (connectionId == Guid.Empty || nominationId == Guid.Empty ||
                senderDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty ||
                senderDeviceId == recipientDeviceId || pathEpoch is < 1 or > long.MaxValue)
                throw new ArgumentException("Direct probe key authority is invalid.");
            var epoch = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(epoch, pathEpoch);
            var info = Concat(
                ProbeDomain,
                GuidNetworkBytes(connectionId),
                epoch,
                GuidNetworkBytes(nominationId),
                GuidNetworkBytes(senderDeviceId),
                GuidNetworkBytes(recipientDeviceId));
            var saltInput = Concat(ProbeDomain, new byte[] { 0 });
            var salt = SHA256.HashData(saltInput);
            try { return Hkdf(pairwiseKey, salt, info); }
            finally
            {
                CryptographicOperations.ZeroMemory(epoch);
                CryptographicOperations.ZeroMemory(info);
                CryptographicOperations.ZeroMemory(saltInput);
                CryptographicOperations.ZeroMemory(salt);
            }
        }

        internal static byte[] DeriveProbeKeyV2(
            byte[] pairwiseKey,
            Guid connectionId,
            ulong pathEpoch,
            Guid checkRoundId,
            Guid senderDeviceId,
            Guid recipientDeviceId)
        {
            ValidatePairwiseKey(pairwiseKey);
            if (connectionId == Guid.Empty || checkRoundId == Guid.Empty ||
                senderDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty ||
                senderDeviceId == recipientDeviceId || pathEpoch is < 1 or > long.MaxValue)
                throw new ArgumentException("Direct V2 probe key authority is invalid.");
            var epoch = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(epoch, pathEpoch);
            var info = Concat(
                ProbeDomainV2,
                GuidNetworkBytes(connectionId),
                epoch,
                GuidNetworkBytes(checkRoundId),
                GuidNetworkBytes(senderDeviceId),
                GuidNetworkBytes(recipientDeviceId));
            var saltInput = Concat(ProbeDomainV2, new byte[] { 0 });
            var salt = SHA256.HashData(saltInput);
            try { return Hkdf(pairwiseKey, salt, info); }
            finally
            {
                CryptographicOperations.ZeroMemory(epoch);
                CryptographicOperations.ZeroMemory(info);
                CryptographicOperations.ZeroMemory(saltInput);
                CryptographicOperations.ZeroMemory(salt);
            }
        }

        internal static byte[] DerivePairProbeNonceV2(
            byte[] directionKey,
            byte[] connectivityNonce,
            Guid initiatorCandidateId,
            Guid responderCandidateId)
        {
            if (directionKey?.Length != 32 || connectivityNonce?.Length != 16 ||
                IsAllZero(directionKey) || IsAllZero(connectivityNonce) ||
                initiatorCandidateId == Guid.Empty || responderCandidateId == Guid.Empty ||
                initiatorCandidateId == responderCandidateId)
                throw new ArgumentException("Direct V2 pair nonce authority is invalid.");
            var input = Concat(
                PairProbeNonceDomainV2,
                connectivityNonce,
                GuidNetworkBytes(initiatorCandidateId),
                GuidNetworkBytes(responderCandidateId));
            var digest = HMACSHA256.HashData(directionKey, input);
            try { return digest.AsSpan(0, 16).ToArray(); }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        private static DirectConnectivityDatagramV1 KnownProbeFields(
            DirectConnectivityDatagramV1 value,
            bool includeTag)
        {
            return new DirectConnectivityDatagramV1
            {
                ProtocolVersion = value.ProtocolVersion,
                Kind = value.Kind,
                ConnectionId = value.ConnectionId,
                PathEpoch = value.PathEpoch,
                TicketId = value.TicketId,
                NominationId = value.NominationId,
                Pair = value.Pair?.Clone(),
                SenderDeviceId = value.SenderDeviceId,
                ProbeNonce = value.ProbeNonce,
                ResponseNonce = value.ResponseNonce,
                SentAtMs = value.SentAtMs,
                AntiAmplificationPadding = value.AntiAmplificationPadding,
                AuthTag = includeTag ? value.AuthTag : ByteString.Empty
            };
        }

        private static byte[] ProcessChaCha(
            bool encrypt,
            byte[] key,
            byte[] nonce,
            byte[] aad,
            byte[] input)
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
            var output = new byte[cipher.GetOutputSize(input.Length)];
            try
            {
                var length = cipher.ProcessBytes(input, 0, input.Length, output, 0);
                length += cipher.DoFinal(output, length);
                return output.AsSpan(0, length).ToArray();
            }
            catch (InvalidCipherTextException error)
            {
                throw new CryptographicException("Transport signal authentication failed.", error);
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }

        private static byte[] Hkdf(byte[] input, byte[] salt, byte[] info)
        {
            var generator = new HkdfBytesGenerator(new Sha256Digest());
            generator.Init(new HkdfParameters(input, salt, info));
            var result = new byte[32];
            generator.GenerateBytes(result, 0, result.Length);
            return result;
        }

        private static void ValidatePairwiseKey(byte[] key)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("A 32-byte pairwise key is required.", nameof(key));
        }

        private static Guid GuidFromNetworkBytes(ReadOnlySpan<byte> value)
        {
            if (value.Length != 16) throw new InvalidDataException("UUID must contain 16 bytes.");
            return Guid.ParseExact(Convert.ToHexString(value), "N");
        }

        private static byte[] GuidNetworkBytes(Guid value)
        {
            if (value == Guid.Empty) throw new ArgumentException("A non-empty UUID is required.", nameof(value));
            return Convert.FromHexString(value.ToString("N"));
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private static byte[] Concat(params byte[][] values)
        {
            using var stream = new MemoryStream();
            foreach (var value in values) stream.Write(value);
            return stream.ToArray();
        }
    }
}

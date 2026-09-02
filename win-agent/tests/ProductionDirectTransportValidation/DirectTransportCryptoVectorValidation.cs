using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Agent;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class DirectTransportCryptoVectorValidation
    {
        public static void Validate()
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(FindVector()));
            var vector = document.RootElement;
            var connection = UuidNetworkBytes(
                Text(vector, "connection_id"),
                Hex(vector, "connection_id_network_hex"));
            var sender = UuidNetworkBytes(
                Text(vector, "sender_device_id"),
                Hex(vector, "sender_device_id_network_hex"));
            var recipient = UuidNetworkBytes(
                Text(vector, "recipient_device_id"),
                Hex(vector, "recipient_device_id_network_hex"));
            var epoch = vector.GetProperty("path_epoch").GetUInt64();
            var pairwise = Hex(vector, "pairwise_secret_hex");

            ValidateSignal(vector, pairwise, connection, sender, recipient, epoch);
            ValidateProbe(vector, pairwise, connection, sender, recipient, epoch);
            ValidateProofExporterFramesAndBinding(vector, connection, sender, recipient, epoch);
        }

        private static void ValidateSignal(
            JsonElement vector,
            byte[] pairwise,
            byte[] connection,
            byte[] sender,
            byte[] recipient,
            ulong epoch)
        {
            var signal = vector.GetProperty("signal");
            var wire = Hex(signal, "envelope_protobuf_hex");
            var envelope = TransportSignalEnvelopeV1.Parser.ParseFrom(wire);
            Require(wire.SequenceEqual(envelope.ToByteArray()),
                "The common signal vector is not canonical generated protobuf.");
            Require(connection.SequenceEqual(envelope.ConnectionId.Span.ToArray()) &&
                    sender.SequenceEqual(envelope.SenderDeviceId.Span.ToArray()) &&
                    recipient.SequenceEqual(envelope.RecipientDeviceId.Span.ToArray()) &&
                    envelope.PathEpoch == epoch,
                "The common signal UUID network order or epoch binding changed.");

            var metadata = envelope.Clone();
            metadata.Ciphertext = ByteString.Empty;
            var aad = metadata.ToByteArray();
            RequireBytes(aad, Hex(signal, "aad_protobuf_hex"), "signal AAD");
            var domain = Encoding.ASCII.GetBytes("TERMLINK-TRANSPORT-SIGNAL-AEAD-V1");
            var saltInput = Concat(domain, new byte[] { 0 });
            RequireBytes(saltInput, Hex(signal, "salt_input_hex"), "signal salt input");
            var salt = SHA256.HashData(saltInput);
            RequireBytes(salt, Hex(signal, "salt_sha256_hex"), "signal salt");
            var info = Concat(
                domain,
                connection,
                UInt64BigEndian(epoch),
                sender,
                recipient,
                envelope.KeyId.ToByteArray());
            RequireBytes(info, Hex(signal, "hkdf_info_hex"), "signal HKDF info");
            var key = Hkdf(pairwise, salt, info);
            RequireBytes(key, Hex(signal, "derived_key_hex"), "signal key");
            var plaintext = ProductionDirectTransportCrypto.DecryptSignal(pairwise, envelope);
            RequireBytes(plaintext, Hex(signal, "plaintext_protobuf_hex"), "signal plaintext");
            RequireBytes(envelope.Ciphertext.ToByteArray(),
                Hex(signal, "ciphertext_and_tag_hex"), "signal ciphertext");
            RequireBytes(SHA256.HashData(wire),
                Hex(signal, "envelope_sha256_hex"), "signal envelope digest");
        }

        private static void ValidateProbe(
            JsonElement vector,
            byte[] pairwise,
            byte[] connection,
            byte[] sender,
            byte[] recipient,
            ulong epoch)
        {
            var probe = vector.GetProperty("probe");
            var domain = Encoding.ASCII.GetBytes("TERMLINK-DIRECT-PROBE-HMAC-V1");
            var macDomain = Concat(domain, new byte[] { 0 });
            RequireBytes(macDomain, Hex(probe, "salt_input_hex"), "probe salt input");
            var salt = SHA256.HashData(macDomain);
            RequireBytes(salt, Hex(probe, "salt_sha256_hex"), "probe salt");
            var nomination = UuidNetworkBytes(Text(probe, "nomination_id"));
            var info = Concat(
                domain,
                connection,
                UInt64BigEndian(epoch),
                nomination,
                sender,
                recipient);
            RequireBytes(info, Hex(probe, "hkdf_info_hex"), "probe HKDF info");
            var key = Hkdf(pairwise, salt, info);
            RequireBytes(key, Hex(probe, "derived_key_hex"), "probe key");

            var packet = Hex(probe, "packet_hex");
            var senderGuid = Guid.Parse(Text(vector, "sender_device_id"));
            var recipientGuid = Guid.Parse(Text(vector, "recipient_device_id"));
            var connectionGuid = Guid.Parse(Text(vector, "connection_id"));
            var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(
                probe.GetProperty("sent_at_ms").GetInt64()).UtcDateTime;
            var parsed = ProductionDirectTransportCrypto.ValidateProbePacket(
                pairwise,
                packet,
                senderGuid,
                recipientGuid,
                connectionGuid,
                epoch,
                sentAt);
            var unsignedProbe = parsed.Clone();
            unsignedProbe.AuthTag = ByteString.Empty;
            Require(unsignedProbe.ToByteArray().SequenceEqual(Hex(probe, "unsigned_protobuf_hex")),
                "Production probe parser changed the common canonical unsigned wire.");
            RequireBytes(parsed.AuthTag.ToByteArray(), Hex(probe, "auth_tag_hex"), "probe tag");
            Require(packet.Length == probe.GetProperty("packet_length").GetInt32(),
                "The common probe packet length changed.");
            RequireBytes(SHA256.HashData(packet),
                Hex(probe, "packet_sha256_hex"), "probe packet digest");

            var macInput = Concat(macDomain, Hex(probe, "unsigned_protobuf_hex"));
            RequireBytes(macInput, Hex(probe, "hmac_input_hex"), "probe HMAC input");
            RequireBytes(HMACSHA256.HashData(key, macInput).AsSpan(0, 16).ToArray(),
                parsed.AuthTag.ToByteArray(), "probe HMAC result");
        }

        private static void ValidateProofExporterFramesAndBinding(
            JsonElement vector,
            byte[] connection,
            byte[] sender,
            byte[] recipient,
            ulong epoch)
        {
            var proof = vector.GetProperty("peer_proof");
            var frames = vector.GetProperty("peer_proof_frames");
            var keys = frames.GetProperty("test_only_device_keys");
            var ticketHash = Hex(vector, "ticket_sha256_hex");
            var exporterHash = Hex(proof, "tls_exporter_sha256_hex");
            Require(Text(proof, "tls_exporter_label_ascii") == "EXPORTER-TERMLINK-DIRECT-V1",
                "The TLS exporter label changed.");
            var exporterContext = Concat(ticketHash, connection, UInt64BigEndian(epoch));
            RequireBytes(exporterContext,
                Hex(proof, "tls_exporter_context_hex"), "TLS exporter context");

            var initiatorTranscript = PeerProofTranscript(
                ticketHash,
                1,
                sender,
                recipient,
                connection,
                epoch,
                Hex(proof, "initiator_proof_nonce_hex"),
                exporterHash);
            RequireBytes(initiatorTranscript,
                Hex(proof, "initiator_transcript_hex"), "initiator proof transcript");
            var initiatorHash = SHA256.HashData(initiatorTranscript);
            RequireBytes(initiatorHash,
                Hex(proof, "initiator_transcript_sha256_hex"), "initiator proof hash");
            RequireEd25519(
                Hex(keys, "initiator_public_key_hex"),
                initiatorTranscript,
                Hex(frames.GetProperty("initiator"), "signature_hex"));

            var responderTranscript = PeerProofTranscript(
                ticketHash,
                2,
                recipient,
                sender,
                connection,
                epoch,
                Hex(proof, "responder_proof_nonce_hex"),
                exporterHash);
            RequireBytes(responderTranscript,
                Hex(proof, "responder_transcript_hex"), "responder proof transcript");
            var responderHash = SHA256.HashData(responderTranscript);
            RequireBytes(responderHash,
                Hex(proof, "responder_transcript_sha256_hex"), "responder proof hash");
            RequireEd25519(
                Hex(keys, "responder_public_key_hex"),
                responderTranscript,
                Hex(frames.GetProperty("responder"), "signature_hex"));

            ValidateFramePair(frames.GetProperty("initiator"), connection, epoch);
            ValidateFramePair(frames.GetProperty("responder"), connection, epoch);

            var probe = vector.GetProperty("probe");
            var binding = vector.GetProperty("path_binding");
            var preimage = Concat(
                Encoding.ASCII.GetBytes("TERMLINK-DIRECT-PATH-BINDING-V1\0"),
                ticketHash,
                UuidNetworkBytes(Text(probe, "initiator_candidate_id")),
                UuidNetworkBytes(Text(probe, "responder_candidate_id")),
                Hex(binding, "initiator_certificate_sha256_hex"),
                Hex(binding, "responder_certificate_sha256_hex"),
                initiatorHash,
                responderHash,
                exporterHash);
            RequireBytes(preimage, Hex(binding, "preimage_hex"), "path binding preimage");
            RequireBytes(SHA256.HashData(preimage), Hex(binding, "sha256_hex"), "path binding");
        }

        private static void ValidateFramePair(JsonElement direction, byte[] connection, ulong epoch)
        {
            var proofRecord = Hex(direction, "peer_proof_record_hex");
            var proofFrame = ParseLengthPrefixedFrame(
                proofRecord,
                direction.GetProperty("peer_proof_frame_length").GetInt32());
            Require(proofFrame.FrameSeq == 1 && proofFrame.BodyCase == DirectPathFrameV1.BodyOneofCase.PeerProof &&
                    proofFrame.ConnectionId.Span.SequenceEqual(connection) && proofFrame.PathEpoch == epoch,
                "The common peer-proof first-frame contract changed.");
            RequireBytes(proofFrame.ToByteArray(),
                Hex(direction, "peer_proof_frame_protobuf_hex"), "peer-proof frame");
            RequireBytes(proofFrame.PeerProof.ToByteArray(),
                Hex(direction, "peer_proof_protobuf_hex"), "peer-proof payload");

            var resultRecord = Hex(direction, "peer_result_record_hex");
            var resultFrame = ParseLengthPrefixedFrame(
                resultRecord,
                direction.GetProperty("peer_result_frame_length").GetInt32());
            Require(resultFrame.FrameSeq == 2 &&
                    resultFrame.BodyCase == DirectPathFrameV1.BodyOneofCase.PeerProofResult,
                "The common peer-proof result second-frame contract changed.");
            RequireBytes(resultFrame.ToByteArray(),
                Hex(direction, "peer_result_frame_protobuf_hex"), "peer-proof result frame");
            RequireBytes(resultFrame.PeerProofResult.ToByteArray(),
                Hex(direction, "peer_result_protobuf_hex"), "peer-proof result payload");
        }

        private static DirectPathFrameV1 ParseLengthPrefixedFrame(byte[] record, int expectedLength)
        {
            Require(record.Length >= 5, "The common direct frame is truncated.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(0, 4));
            Require(length == expectedLength && length == record.Length - 4,
                "The common direct frame length prefix changed.");
            return DirectPathFrameV1.Parser.ParseFrom(record.AsSpan(4).ToArray());
        }

        private static byte[] PeerProofTranscript(
            byte[] ticketHash,
            byte role,
            byte[] device,
            byte[] peer,
            byte[] connection,
            ulong epoch,
            byte[] nonce,
            byte[] exporterHash) => Concat(
                Encoding.ASCII.GetBytes("TERMLINK-DIRECT-PEER-PROOF-V1\n"),
                ticketHash,
                new[] { role },
                device,
                peer,
                connection,
                UInt64BigEndian(epoch),
                new[] { checked((byte)nonce.Length) },
                nonce,
                exporterHash);

        private static void RequireEd25519(byte[] publicKey, byte[] message, byte[] signature)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            Require(verifier.VerifySignature(signature),
                "The common peer-proof Ed25519 signature was rejected.");
        }

        private static byte[] Hkdf(byte[] input, byte[] salt, byte[] info)
        {
            var generator = new HkdfBytesGenerator(new Sha256Digest());
            generator.Init(new HkdfParameters(input, salt, info));
            var result = new byte[32];
            generator.GenerateBytes(result, 0, result.Length);
            return result;
        }

        private static byte[] UInt64BigEndian(ulong value)
        {
            var result = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(result, value);
            return result;
        }

        private static byte[] UuidNetworkBytes(string value, byte[] expected = null)
        {
            var parsed = Guid.Parse(value);
            var bytes = Convert.FromHexString(parsed.ToString("N"));
            if (expected != null) RequireBytes(bytes, expected, "UUID network order");
            return bytes;
        }

        private static string Text(JsonElement value, string name) =>
            value.GetProperty(name).GetString() ?? throw new InvalidDataException(
                $"The common vector field {name} is empty.");

        private static byte[] Hex(JsonElement value, string name) =>
            Convert.FromHexString(Text(value, name));

        private static byte[] Concat(params byte[][] values)
        {
            using var stream = new MemoryStream();
            foreach (var value in values) stream.Write(value);
            return stream.ToArray();
        }

        private static void RequireBytes(byte[] actual, byte[] expected, string name)
        {
            Require(actual.SequenceEqual(expected),
                $"The common direct-transport {name} vector changed.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string FindVector()
        {
            var output = Path.Combine(AppContext.BaseDirectory, "direct-transport-crypto-v1.json");
            if (File.Exists(output)) return output;
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(Path.GetFullPath(start));
                while (current != null)
                {
                    var candidate = Path.Combine(
                        current.FullName,
                        "protocol",
                        "test-vectors",
                        "direct-transport-crypto-v1.json");
                    if (File.Exists(candidate)) return candidate;
                    current = current.Parent;
                }
            }
            throw new FileNotFoundException("The common direct-transport crypto vector was not found.");
        }
    }
}

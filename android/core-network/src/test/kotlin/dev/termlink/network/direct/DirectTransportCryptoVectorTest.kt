package dev.termlink.network.direct

import dev.termlink.protocol.transport.v1.DirectPathFrameV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.modes.ChaCha20Poly1305
import org.bouncycastle.crypto.params.AEADParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.params.HKDFParameters
import org.bouncycastle.crypto.params.KeyParameter
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectTransportCryptoVectorTest {
    @Test
    fun allDirectTransportCryptoDomainsMatchSharedVector() {
        val vector = Json.parseToJsonElement(
            findRepositoryFile("protocol/test-vectors/direct-transport-crypto-v1.json").readText(),
        ).jsonObject
        val connection = uuidNetworkBytes(
            vector.string("connection_id"),
            vector.hex("connection_id_network_hex"),
        )
        val sender = uuidNetworkBytes(
            vector.string("sender_device_id"),
            vector.hex("sender_device_id_network_hex"),
        )
        val recipient = uuidNetworkBytes(
            vector.string("recipient_device_id"),
            vector.hex("recipient_device_id_network_hex"),
        )
        val epoch = vector.long("path_epoch")
        val pairwise = vector.hex("pairwise_secret_hex")

        validateSignal(vector.obj("signal"), pairwise, connection, sender, recipient, epoch)
        validateProbe(vector, pairwise, connection, sender, recipient, epoch)
        validateProofExporterFramesAndBinding(vector, connection, sender, recipient, epoch)
    }

    private fun validateSignal(
        signal: JsonObject,
        pairwise: ByteArray,
        connection: ByteArray,
        sender: ByteArray,
        recipient: ByteArray,
        epoch: Long,
    ) {
        val wire = signal.hex("envelope_protobuf_hex")
        val envelope = TransportSignalEnvelopeV1.parseFrom(wire)
        assertArrayEquals(connection, envelope.connectionId.toByteArray())
        assertArrayEquals(sender, envelope.senderDeviceId.toByteArray())
        assertArrayEquals(recipient, envelope.recipientDeviceId.toByteArray())
        assertEquals(epoch, envelope.pathEpoch)
        assertArrayEquals(wire, envelope.toByteArray())

        val aad = envelope.toBuilder().clearCiphertext().build().toByteArray()
        assertArrayEquals(signal.hex("aad_protobuf_hex"), aad)
        val domain = "TERMLINK-TRANSPORT-SIGNAL-AEAD-V1".encodeToByteArray()
        val saltInput = domain + byteArrayOf(0)
        assertArrayEquals(signal.hex("salt_input_hex"), saltInput)
        val salt = sha256(saltInput)
        assertArrayEquals(signal.hex("salt_sha256_hex"), salt)
        val info = domain + connection + epoch.toBigEndian() + sender + recipient + envelope.keyId.toByteArray()
        assertArrayEquals(signal.hex("hkdf_info_hex"), info)
        val key = hkdf(pairwise, salt, info)
        assertArrayEquals(signal.hex("derived_key_hex"), key)
        val plaintext = decryptChaCha(
            key,
            envelope.nonce.toByteArray(),
            aad,
            envelope.ciphertext.toByteArray(),
        )
        assertArrayEquals(signal.hex("plaintext_protobuf_hex"), plaintext)
        assertArrayEquals(signal.hex("ciphertext_and_tag_hex"), envelope.ciphertext.toByteArray())
        assertArrayEquals(signal.hex("envelope_sha256_hex"), sha256(wire))
    }

    private fun validateProbe(
        vector: JsonObject,
        pairwise: ByteArray,
        connection: ByteArray,
        sender: ByteArray,
        recipient: ByteArray,
        epoch: Long,
    ) {
        val probe = vector.obj("probe")
        val domain = "TERMLINK-DIRECT-PROBE-HMAC-V1".encodeToByteArray()
        val saltInput = domain + byteArrayOf(0)
        assertArrayEquals(probe.hex("salt_input_hex"), saltInput)
        val salt = sha256(saltInput)
        assertArrayEquals(probe.hex("salt_sha256_hex"), salt)
        val nomination = uuidNetworkBytes(probe.string("nomination_id"))
        val info = domain + connection + epoch.toBigEndian() + nomination + sender + recipient
        assertArrayEquals(probe.hex("hkdf_info_hex"), info)
        val key = hkdf(pairwise, salt, info)
        assertArrayEquals(probe.hex("derived_key_hex"), key)

        val template = DirectProbeTemplate(
            connectionId = connection,
            pathEpoch = epoch,
            ticketId = uuidNetworkBytes(vector.string("ticket_id")),
            nominationId = nomination,
            initiatorCandidateId = uuidNetworkBytes(probe.string("initiator_candidate_id")),
            responderCandidateId = uuidNetworkBytes(probe.string("responder_candidate_id")),
            senderDeviceId = sender,
        )
        val packet = probe.hex("packet_hex")
        DirectProbeCodec(key).use { codec ->
            val decoded = codec.decode(
                packet,
                DirectProbeExpectation(template, DirectProbeKind.PROBE),
                probe.long("sent_at_ms"),
            )
            assertEquals(DirectProbeKind.PROBE, decoded.kind)
            assertArrayEquals(probe.hex("probe_nonce_hex"), decoded.probeNonce)
        }
        assertEquals(probe.long("packet_length"), packet.size.toLong())
        assertArrayEquals(probe.hex("packet_sha256_hex"), sha256(packet))
    }

    private fun validateProofExporterFramesAndBinding(
        vector: JsonObject,
        connection: ByteArray,
        sender: ByteArray,
        recipient: ByteArray,
        epoch: Long,
    ) {
        val proof = vector.obj("peer_proof")
        val frames = vector.obj("peer_proof_frames")
        val keys = frames.obj("test_only_device_keys")
        val ticketHash = vector.hex("ticket_sha256_hex")
        val exporterHash = proof.hex("tls_exporter_sha256_hex")
        assertEquals("EXPORTER-TERMLINK-DIRECT-V1", proof.string("tls_exporter_label_ascii"))
        val exporterContext = ticketHash + connection + epoch.toBigEndian()
        assertArrayEquals(proof.hex("tls_exporter_context_hex"), exporterContext)

        val initiatorTranscript = peerProofTranscript(
            ticketHash,
            role = 1,
            sender,
            recipient,
            connection,
            epoch,
            proof.hex("initiator_proof_nonce_hex"),
            exporterHash,
        )
        assertArrayEquals(proof.hex("initiator_transcript_hex"), initiatorTranscript)
        val initiatorHash = sha256(initiatorTranscript)
        assertArrayEquals(proof.hex("initiator_transcript_sha256_hex"), initiatorHash)
        assertEd25519(
            keys.hex("initiator_public_key_hex"),
            initiatorTranscript,
            frames.obj("initiator").hex("signature_hex"),
        )

        val responderTranscript = peerProofTranscript(
            ticketHash,
            role = 2,
            recipient,
            sender,
            connection,
            epoch,
            proof.hex("responder_proof_nonce_hex"),
            exporterHash,
        )
        assertArrayEquals(proof.hex("responder_transcript_hex"), responderTranscript)
        val responderHash = sha256(responderTranscript)
        assertArrayEquals(proof.hex("responder_transcript_sha256_hex"), responderHash)
        assertEd25519(
            keys.hex("responder_public_key_hex"),
            responderTranscript,
            frames.obj("responder").hex("signature_hex"),
        )

        validateFramePair(frames.obj("initiator"), connection, epoch)
        validateFramePair(frames.obj("responder"), connection, epoch)

        val probe = vector.obj("probe")
        val binding = vector.obj("path_binding")
        val preimage = "TERMLINK-DIRECT-PATH-BINDING-V1\u0000".encodeToByteArray() +
            ticketHash +
            uuidNetworkBytes(probe.string("initiator_candidate_id")) +
            uuidNetworkBytes(probe.string("responder_candidate_id")) +
            binding.hex("initiator_certificate_sha256_hex") +
            binding.hex("responder_certificate_sha256_hex") +
            initiatorHash + responderHash + exporterHash
        assertArrayEquals(binding.hex("preimage_hex"), preimage)
        assertArrayEquals(binding.hex("sha256_hex"), sha256(preimage))
    }

    private fun validateFramePair(direction: JsonObject, connection: ByteArray, epoch: Long) {
        val proofFrame = decodeLengthPrefixedFrame(
            direction.hex("peer_proof_record_hex"),
            direction.long("peer_proof_frame_length").toInt(),
        )
        assertEquals(1L, proofFrame.frameSeq)
        assertTrue(proofFrame.hasPeerProof())
        assertArrayEquals(connection, proofFrame.connectionId.toByteArray())
        assertEquals(epoch, proofFrame.pathEpoch)
        assertArrayEquals(direction.hex("peer_proof_frame_protobuf_hex"), proofFrame.toByteArray())
        assertArrayEquals(direction.hex("peer_proof_protobuf_hex"), proofFrame.peerProof.toByteArray())

        val resultFrame = decodeLengthPrefixedFrame(
            direction.hex("peer_result_record_hex"),
            direction.long("peer_result_frame_length").toInt(),
        )
        assertEquals(2L, resultFrame.frameSeq)
        assertTrue(resultFrame.hasPeerProofResult())
        assertArrayEquals(direction.hex("peer_result_frame_protobuf_hex"), resultFrame.toByteArray())
        assertArrayEquals(direction.hex("peer_result_protobuf_hex"), resultFrame.peerProofResult.toByteArray())
    }

    private fun decodeLengthPrefixedFrame(record: ByteArray, expectedLength: Int): DirectPathFrameV1 {
        require(record.size >= 5)
        val length = ByteBuffer.wrap(record, 0, 4).order(ByteOrder.BIG_ENDIAN).int
        assertEquals(expectedLength, length)
        assertEquals(record.size - 4, length)
        return DirectPathFrameV1.parseFrom(record.copyOfRange(4, record.size))
    }

    private fun decryptChaCha(
        key: ByteArray,
        nonce: ByteArray,
        aad: ByteArray,
        ciphertext: ByteArray,
    ): ByteArray {
        val cipher = ChaCha20Poly1305()
        cipher.init(false, AEADParameters(KeyParameter(key), 128, nonce, aad))
        val output = ByteArray(cipher.getOutputSize(ciphertext.size))
        var length = cipher.processBytes(ciphertext, 0, ciphertext.size, output, 0)
        length += cipher.doFinal(output, length)
        return output.copyOf(length)
    }

    private fun hkdf(input: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
        val generator = HKDFBytesGenerator(SHA256Digest())
        generator.init(HKDFParameters(input, salt, info))
        return ByteArray(32).also { generator.generateBytes(it, 0, it.size) }
    }

    private fun peerProofTranscript(
        ticketHash: ByteArray,
        role: Int,
        device: ByteArray,
        peer: ByteArray,
        connection: ByteArray,
        epoch: Long,
        nonce: ByteArray,
        exporterHash: ByteArray,
    ): ByteArray = "TERMLINK-DIRECT-PEER-PROOF-V1\n".encodeToByteArray() +
        ticketHash + byteArrayOf(role.toByte()) + device + peer + connection + epoch.toBigEndian() +
        byteArrayOf(nonce.size.toByte()) + nonce + exporterHash

    private fun assertEd25519(publicKey: ByteArray, message: ByteArray, signature: ByteArray) {
        val verifier = Ed25519Signer().apply {
            init(false, Ed25519PublicKeyParameters(publicKey, 0))
            update(message, 0, message.size)
        }
        assertTrue("common peer-proof signature was rejected", verifier.verifySignature(signature))
    }

    private fun uuidNetworkBytes(text: String, expected: ByteArray? = null): ByteArray {
        val value = UUID.fromString(text)
        val bytes = ByteBuffer.allocate(16)
            .order(ByteOrder.BIG_ENDIAN)
            .putLong(value.mostSignificantBits)
            .putLong(value.leastSignificantBits)
            .array()
        expected?.let { assertArrayEquals(it, bytes) }
        return bytes
    }

    private fun Long.toBigEndian(): ByteArray = ByteBuffer.allocate(Long.SIZE_BYTES)
        .order(ByteOrder.BIG_ENDIAN)
        .putLong(this)
        .array()

    private fun sha256(value: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(value)

    private fun JsonObject.obj(name: String): JsonObject = getValue(name).jsonObject

    private fun JsonObject.string(name: String): String = getValue(name).jsonPrimitive.content

    private fun JsonObject.long(name: String): Long = string(name).toLong()

    private fun JsonObject.hex(name: String): ByteArray = string(name).hexToBytes()

    private fun String.hexToBytes(): ByteArray {
        require(length % 2 == 0)
        return ByteArray(length / 2) { index -> substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }

    private fun findRepositoryFile(relativePath: String): File {
        val starts = listOfNotNull(
            System.getProperty("user.dir")?.let(::File),
            javaClass.protectionDomain?.codeSource?.location?.toURI()?.let(::File),
        )
        starts.forEach { start ->
            var current: File? = start.canonicalFile
            while (current != null) {
                val candidate = current.resolve(relativePath)
                if (candidate.isFile) return candidate
                current = current.parentFile
            }
        }
        error("canonical direct-transport crypto vector was not found")
    }
}

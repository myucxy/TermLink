package dev.termlink.crypto

import dev.termlink.protocol.transport.v1.DirectPeerRoleV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.File
import java.security.SecureRandom
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import kotlin.test.assertFailsWith

class DirectTransportCryptoTest {
    @Test
    fun productionApisMatchCanonicalDirectTransportVector() {
        val vector = loadVector()
        val signal = vector.obj("signal")
        val probe = vector.obj("probe")
        val peerProof = vector.obj("peer_proof")
        val binding = vector.obj("path_binding")
        val pairwiseKey = vector.hex("pairwise_secret_hex")
        val connectionId = UUID.fromString(vector.string("connection_id"))
        val senderDeviceId = UUID.fromString(vector.string("sender_device_id"))
        val recipientDeviceId = UUID.fromString(vector.string("recipient_device_id"))
        val pathEpoch = vector.long("path_epoch")
        val envelope = TransportSignalEnvelopeV1.parseFrom(signal.hex("envelope_protobuf_hex"))

        assertArrayEquals(
            signal.hex("derived_key_hex"),
            DirectTransportCrypto.deriveSignalKey(pairwiseKey, envelope),
        )
        assertArrayEquals(
            signal.hex("aad_protobuf_hex"),
            DirectTransportCrypto.signalAssociatedData(envelope),
        )
        assertArrayEquals(
            signal.hex("plaintext_protobuf_hex"),
            DirectTransportCrypto.decryptSignal(pairwiseKey, envelope),
        )

        val unsealedMetadata = envelope.toBuilder().clearNonce().clearCiphertext().build()
        val sealed = DirectTransportCrypto.encryptSignal(
            pairwiseKey = pairwiseKey,
            metadata = unsealedMetadata,
            plaintext = signal.hex("plaintext_protobuf_hex"),
            random = FixedSecureRandom(signal.hex("nonce_hex")),
        )
        assertArrayEquals(signal.hex("ciphertext_and_tag_hex"), sealed.ciphertext.toByteArray())
        assertArrayEquals(signal.hex("envelope_protobuf_hex"), sealed.toByteArray())

        assertArrayEquals(
            probe.hex("derived_key_hex"),
            DirectTransportCrypto.deriveProbeKey(
                pairwiseKey = pairwiseKey,
                connectionId = connectionId,
                pathEpoch = pathEpoch,
                nominationId = UUID.fromString(probe.string("nomination_id")),
                senderDeviceId = senderDeviceId,
                recipientDeviceId = recipientDeviceId,
            ),
        )

        val ticketSha256 = vector.hex("ticket_sha256_hex")
        val exporterSha256 = peerProof.hex("tls_exporter_sha256_hex")
        assertEquals(
            peerProof.string("tls_exporter_label_ascii"),
            DirectTransportCrypto.TLS_EXPORTER_LABEL,
        )
        assertArrayEquals(
            peerProof.hex("tls_exporter_context_hex"),
            DirectTransportCrypto.tlsExporterContext(ticketSha256, connectionId, pathEpoch),
        )

        val initiatorNonce = peerProof.hex("initiator_proof_nonce_hex")
        val initiatorTranscript = DirectTransportCrypto.peerProofTranscript(
            ticketSha256 = ticketSha256,
            role = DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_INITIATOR,
            deviceId = senderDeviceId,
            peerDeviceId = recipientDeviceId,
            connectionId = connectionId,
            pathEpoch = pathEpoch,
            proofNonce = initiatorNonce,
            tlsExporterSha256 = exporterSha256,
        )
        assertArrayEquals(peerProof.hex("initiator_transcript_hex"), initiatorTranscript)
        val initiatorProofSha256 = DirectTransportCrypto.peerProofSha256(
            ticketSha256,
            DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_INITIATOR,
            senderDeviceId,
            recipientDeviceId,
            connectionId,
            pathEpoch,
            initiatorNonce,
            exporterSha256,
        )
        assertArrayEquals(peerProof.hex("initiator_transcript_sha256_hex"), initiatorProofSha256)

        val responderNonce = peerProof.hex("responder_proof_nonce_hex")
        val responderTranscript = DirectTransportCrypto.peerProofTranscript(
            ticketSha256 = ticketSha256,
            role = DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_RESPONDER,
            deviceId = recipientDeviceId,
            peerDeviceId = senderDeviceId,
            connectionId = connectionId,
            pathEpoch = pathEpoch,
            proofNonce = responderNonce,
            tlsExporterSha256 = exporterSha256,
        )
        assertArrayEquals(peerProof.hex("responder_transcript_hex"), responderTranscript)
        val responderProofSha256 = DirectTransportCrypto.peerProofSha256(
            ticketSha256,
            DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_RESPONDER,
            recipientDeviceId,
            senderDeviceId,
            connectionId,
            pathEpoch,
            responderNonce,
            exporterSha256,
        )
        assertArrayEquals(peerProof.hex("responder_transcript_sha256_hex"), responderProofSha256)

        assertArrayEquals(
            binding.hex("sha256_hex"),
            DirectTransportCrypto.pathBindingSha256(
                ticketSha256 = ticketSha256,
                initiatorCandidateId = UUID.fromString(probe.string("initiator_candidate_id")),
                responderCandidateId = UUID.fromString(probe.string("responder_candidate_id")),
                initiatorCertificateSha256 = binding.hex("initiator_certificate_sha256_hex"),
                responderCertificateSha256 = binding.hex("responder_certificate_sha256_hex"),
                initiatorProofSha256 = initiatorProofSha256,
                responderProofSha256 = responderProofSha256,
                tlsExporterSha256 = exporterSha256,
            ),
        )
    }

    @Test
    fun malformedLengthsAndRangesAreRejected() {
        val vector = loadVector()
        val signal = vector.obj("signal")
        val probe = vector.obj("probe")
        val peerProof = vector.obj("peer_proof")
        val binding = vector.obj("path_binding")
        val pairwiseKey = vector.hex("pairwise_secret_hex")
        val connectionId = UUID.fromString(vector.string("connection_id"))
        val senderDeviceId = UUID.fromString(vector.string("sender_device_id"))
        val recipientDeviceId = UUID.fromString(vector.string("recipient_device_id"))
        val envelope = TransportSignalEnvelopeV1.parseFrom(signal.hex("envelope_protobuf_hex"))

        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.deriveSignalKey(ByteArray(31), envelope)
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.deriveSignalKey(
                pairwiseKey,
                envelope.toBuilder().setPathEpoch(0).build(),
            )
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.deriveProbeKey(
                pairwiseKey,
                connectionId,
                1,
                UUID.fromString(probe.string("nomination_id")),
                senderDeviceId,
                senderDeviceId,
            )
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.peerProofTranscript(
                vector.hex("ticket_sha256_hex"),
                DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_UNSPECIFIED,
                senderDeviceId,
                recipientDeviceId,
                connectionId,
                1,
                ByteArray(15),
                peerProof.hex("tls_exporter_sha256_hex"),
            )
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.tlsExporterContext(ByteArray(31), connectionId, 1)
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.pathBindingSha256(
                ticketSha256 = vector.hex("ticket_sha256_hex"),
                initiatorCandidateId = UUID.fromString(probe.string("initiator_candidate_id")),
                responderCandidateId = UUID.fromString(probe.string("responder_candidate_id")),
                initiatorCertificateSha256 = ByteArray(31),
                responderCertificateSha256 = binding.hex("responder_certificate_sha256_hex"),
                initiatorProofSha256 = peerProof.hex("initiator_transcript_sha256_hex"),
                responderProofSha256 = peerProof.hex("responder_transcript_sha256_hex"),
                tlsExporterSha256 = peerProof.hex("tls_exporter_sha256_hex"),
            )
        }
    }

    @Test
    fun signalTamperingAndMalformedNonceAreRejected() {
        val vector = loadVector()
        val signal = vector.obj("signal")
        val pairwiseKey = vector.hex("pairwise_secret_hex")
        val envelope = TransportSignalEnvelopeV1.parseFrom(signal.hex("envelope_protobuf_hex"))
        val tamperedCiphertext = envelope.ciphertext.toByteArray().also {
            it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
        }

        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.decryptSignal(
                pairwiseKey,
                envelope.toBuilder().setCiphertext(com.google.protobuf.ByteString.copyFrom(tamperedCiphertext)).build(),
            )
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.decryptSignal(
                pairwiseKey,
                envelope.toBuilder().setSignalAck(envelope.signalAck + 1).build(),
            )
        }
        assertFailsWith<IllegalArgumentException> {
            DirectTransportCrypto.decryptSignal(
                pairwiseKey,
                envelope.toBuilder().setNonce(com.google.protobuf.ByteString.copyFrom(ByteArray(11))).build(),
            )
        }
    }

    @Test
    fun multipairV2PairNonceMatchesSharedVectorAndBindsDirectionAndPair() {
        val vector = Json.parseToJsonElement(
            findRepositoryFile("protocol/test-vectors/direct-transport-multipair-v2.json").readText(),
        ).jsonObject
        val pair = vector.obj("pair")
        val key = vector.hex("local_send_probe_hmac_key_hex")
        val connectivityNonce = vector.hex("connectivity_nonce_hex")
        val initiator = pair.hex("initiator_candidate_id_hex")
        val responder = pair.hex("responder_candidate_id_hex")

        assertArrayEquals(
            vector.hex("local_pair_probe_nonce_hex"),
            DirectTransportCrypto.derivePairProbeNonceV2(
                key, connectivityNonce, initiator, responder,
            ),
        )
        assertFalse(
            DirectTransportCrypto.derivePairProbeNonceV2(
                key, connectivityNonce, initiator, responder,
            ).contentEquals(
                DirectTransportCrypto.derivePairProbeNonceV2(
                    ByteArray(32) { 0x42 }, connectivityNonce, initiator, responder,
                ),
            ),
        )
        assertFalse(
            DirectTransportCrypto.derivePairProbeNonceV2(
                key, connectivityNonce, initiator, responder,
            ).contentEquals(
                DirectTransportCrypto.derivePairProbeNonceV2(
                    key, connectivityNonce, responder, initiator,
                ),
            ),
        )
    }

    private class FixedSecureRandom(source: ByteArray) : SecureRandom() {
        private val bytes = source.copyOf()

        override fun nextBytes(target: ByteArray) {
            require(target.size == bytes.size)
            bytes.copyInto(target)
        }
    }

    private fun loadVector(): JsonObject = Json.parseToJsonElement(
        findRepositoryFile("protocol/test-vectors/direct-transport-crypto-v1.json").readText(),
    ).jsonObject

    private fun JsonObject.obj(name: String): JsonObject = getValue(name).jsonObject

    private fun JsonObject.string(name: String): String = getValue(name).jsonPrimitive.content

    private fun JsonObject.long(name: String): Long = string(name).toLong()

    private fun JsonObject.hex(name: String): ByteArray = string(name).hexToBytes()

    private fun String.hexToBytes(): ByteArray {
        require(length % 2 == 0)
        return ByteArray(length / 2) { index ->
            substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
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

package dev.termlink.crypto

import com.google.protobuf.ByteString
import dev.termlink.protocol.transport.v1.DirectPeerRoleV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import org.bouncycastle.crypto.InvalidCipherTextException
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.modes.ChaCha20Poly1305
import org.bouncycastle.crypto.params.AEADParameters
import org.bouncycastle.crypto.params.HKDFParameters
import org.bouncycastle.crypto.params.KeyParameter

/**
 * Cryptographic primitives for a single Direct transport path attempt.
 *
 * This object deliberately does not decide routing, path lifetime, replay policy, or Relay
 * fallback. Callers retain those responsibilities and provide already-authorized path metadata.
 */
object DirectTransportCrypto {
    const val TLS_EXPORTER_LABEL = "EXPORTER-TERMLINK-DIRECT-V1"
    const val MAX_SIGNAL_BYTES = 64 * 1024

    private const val KEY_BYTES = 32
    private const val UUID_BYTES = 16
    private const val NONCE_BYTES = 12
    private const val TAG_BYTES = 16
    private const val MAX_KEY_ID_BYTES = 64
    private const val MAX_SIGNAL_PLAINTEXT_BYTES = MAX_SIGNAL_BYTES - 256
    private val zeroUuid = UUID(0, 0)
    private val signalDomain = "TERMLINK-TRANSPORT-SIGNAL-AEAD-V1".encodeToByteArray()
    private val probeDomain = "TERMLINK-DIRECT-PROBE-HMAC-V1".encodeToByteArray()
    private val probeDomainV2 = "TERMLINK-DIRECT-PROBE-HMAC-V2".encodeToByteArray()
    private val pairProbeNonceDomainV2 =
        "TERMLINK-DIRECT-PAIR-PROBE-NONCE-V2\u0000".encodeToByteArray()
    private val peerProofDomain = "TERMLINK-DIRECT-PEER-PROOF-V1\n".encodeToByteArray()
    private val pathBindingDomain = "TERMLINK-DIRECT-PATH-BINDING-V1\u0000".encodeToByteArray()
    private val signalSalt = sha256(signalDomain + byteArrayOf(0))
    private val probeSalt = sha256(probeDomain + byteArrayOf(0))
    private val probeSaltV2 = sha256(probeDomainV2 + byteArrayOf(0))

    /** Derives the direction-specific signal key authenticated by [metadata]. */
    fun deriveSignalKey(
        pairwiseKey: ByteArray,
        metadata: TransportSignalEnvelopeV1,
    ): ByteArray {
        validateSignal(metadata, SignalForm.EITHER)
        return deriveSignalKeyValidated(pairwiseKey, metadata)
    }

    /** Returns the canonical protobuf AAD (all envelope fields except ciphertext). */
    fun signalAssociatedData(envelope: TransportSignalEnvelopeV1): ByteArray {
        validateSignal(envelope, SignalForm.SEALED)
        return canonicalSignal(envelope, includeCiphertext = false).toByteArray()
    }

    /**
     * Encrypts one non-empty signal payload and supplies a fresh 96-bit nonce.
     *
     * [metadata] must contain neither nonce nor ciphertext. Supplying [random] is intended for
     * platform injection and deterministic interoperability tests; production callers should use
     * the default system [SecureRandom].
     */
    fun encryptSignal(
        pairwiseKey: ByteArray,
        metadata: TransportSignalEnvelopeV1,
        plaintext: ByteArray,
        random: SecureRandom = SecureRandom(),
    ): TransportSignalEnvelopeV1 {
        validateSignal(metadata, SignalForm.UNSEALED)
        require(plaintext.size in 1..MAX_SIGNAL_PLAINTEXT_BYTES) {
            "Signal plaintext length is invalid"
        }
        val nonce = ByteArray(NONCE_BYTES).also(random::nextBytes)
        val prepared = canonicalSignal(metadata, includeCiphertext = false).toBuilder()
            .setNonce(ByteString.copyFrom(nonce))
            .build()
        val key = deriveSignalKeyValidated(pairwiseKey, prepared)
        val aad = canonicalSignal(prepared, includeCiphertext = false).toByteArray()
        return try {
            val ciphertext = processChaCha(
                encrypt = true,
                key = key,
                nonce = nonce,
                associatedData = aad,
                input = plaintext,
            )
            try {
                canonicalSignal(
                    prepared.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build(),
                    includeCiphertext = true,
                ).also {
                    require(it.serializedSize <= MAX_SIGNAL_BYTES) {
                        "Encrypted signal exceeds the wire limit"
                    }
                }
            } finally {
                ciphertext.fill(0)
            }
        } finally {
            nonce.fill(0)
            key.fill(0)
            aad.fill(0)
        }
    }

    /** Authenticates and decrypts one canonical Direct signal envelope. */
    fun decryptSignal(
        pairwiseKey: ByteArray,
        envelope: TransportSignalEnvelopeV1,
    ): ByteArray {
        validateSignal(envelope, SignalForm.SEALED)
        require(envelope.serializedSize <= MAX_SIGNAL_BYTES) {
            "Encrypted signal exceeds the wire limit"
        }
        val key = deriveSignalKeyValidated(pairwiseKey, envelope)
        val nonce = envelope.nonce.toByteArray()
        val aad = canonicalSignal(envelope, includeCiphertext = false).toByteArray()
        val ciphertext = envelope.ciphertext.toByteArray()
        return try {
            processChaCha(
                encrypt = false,
                key = key,
                nonce = nonce,
                associatedData = aad,
                input = ciphertext,
            )
        } catch (error: InvalidCipherTextException) {
            throw IllegalArgumentException("Transport signal authentication failed", error)
        } finally {
            key.fill(0)
            nonce.fill(0)
            aad.fill(0)
            ciphertext.fill(0)
        }
    }

    /** Derives the HMAC key for one authenticated UDP probe direction. */
    fun deriveProbeKey(
        pairwiseKey: ByteArray,
        connectionId: UUID,
        pathEpoch: Long,
        nominationId: UUID,
        senderDeviceId: UUID,
        recipientDeviceId: UUID,
    ): ByteArray {
        validatePairwiseKey(pairwiseKey)
        requireNonZero(connectionId, "Connection ID")
        requireNonZero(nominationId, "Nomination ID")
        requireDistinctDevices(senderDeviceId, recipientDeviceId)
        require(pathEpoch > 0) { "Path epoch must be positive" }
        val info = concatenate(
            probeDomain,
            SessionCrypto.uuidBytes(connectionId),
            SessionCrypto.longBytes(pathEpoch),
            SessionCrypto.uuidBytes(nominationId),
            SessionCrypto.uuidBytes(senderDeviceId),
            SessionCrypto.uuidBytes(recipientDeviceId),
        )
        return try {
            hkdf(pairwiseKey, probeSalt, info)
        } finally {
            info.fill(0)
        }
    }

    /** Derives one direction key for a multipair-v2 connectivity-check round. */
    fun deriveProbeKeyV2(
        pairwiseKey: ByteArray,
        connectionId: UUID,
        pathEpoch: Long,
        checkRoundId: ByteArray,
        senderDeviceId: UUID,
        recipientDeviceId: UUID,
    ): ByteArray {
        validatePairwiseKey(pairwiseKey)
        requireNonZero(connectionId, "Connection ID")
        requireIdentifier(checkRoundId, "Check round ID")
        requireDistinctDevices(senderDeviceId, recipientDeviceId)
        require(pathEpoch > 0) { "Path epoch must be positive" }
        val info = concatenate(
            probeDomainV2,
            SessionCrypto.uuidBytes(connectionId),
            SessionCrypto.longBytes(pathEpoch),
            checkRoundId,
            SessionCrypto.uuidBytes(senderDeviceId),
            SessionCrypto.uuidBytes(recipientDeviceId),
        )
        return try {
            hkdf(pairwiseKey, probeSaltV2, info)
        } finally {
            info.fill(0)
        }
    }

    /** Derives the deterministic sender nonce for one role-oriented multipair candidate pair. */
    fun derivePairProbeNonceV2(
        directionKey: ByteArray,
        connectivityNonce: ByteArray,
        initiatorCandidateId: ByteArray,
        responderCandidateId: ByteArray,
    ): ByteArray {
        require(directionKey.size == KEY_BYTES) { "Probe direction key must be 32 bytes" }
        requireIdentifier(connectivityNonce, "Connectivity nonce")
        requireIdentifier(initiatorCandidateId, "Initiator candidate ID")
        requireIdentifier(responderCandidateId, "Responder candidate ID")
        require(!MessageDigest.isEqual(initiatorCandidateId, responderCandidateId)) {
            "Candidate IDs must be distinct"
        }
        val input = concatenate(
            pairProbeNonceDomainV2,
            connectivityNonce,
            initiatorCandidateId,
            responderCandidateId,
        )
        val key = directionKey.copyOf()
        return try {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(key, "HmacSHA256"))
            mac.doFinal(input).copyOf(UUID_BYTES)
        } finally {
            key.fill(0)
            input.fill(0)
        }
    }

    /** Builds the exact bytes signed by an Ed25519 Direct peer proof. */
    fun peerProofTranscript(
        ticketSha256: ByteArray,
        role: DirectPeerRoleV1,
        deviceId: UUID,
        peerDeviceId: UUID,
        connectionId: UUID,
        pathEpoch: Long,
        proofNonce: ByteArray,
        tlsExporterSha256: ByteArray,
    ): ByteArray {
        requireHash(ticketSha256, "Ticket SHA-256")
        requireHash(tlsExporterSha256, "TLS exporter SHA-256")
        require(role == DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_INITIATOR ||
            role == DirectPeerRoleV1.DIRECT_PEER_ROLE_V1_RESPONDER) {
            "Peer role is invalid"
        }
        requireDistinctDevices(deviceId, peerDeviceId)
        requireNonZero(connectionId, "Connection ID")
        require(pathEpoch > 0) { "Path epoch must be positive" }
        require(proofNonce.size in 16..32) { "Proof nonce length is invalid" }
        return ByteArrayOutputStream(peerProofDomain.size + 121 + proofNonce.size).use { output ->
            output.write(peerProofDomain)
            output.write(ticketSha256)
            output.write(role.number)
            output.write(SessionCrypto.uuidBytes(deviceId))
            output.write(SessionCrypto.uuidBytes(peerDeviceId))
            output.write(SessionCrypto.uuidBytes(connectionId))
            output.write(SessionCrypto.longBytes(pathEpoch))
            output.write(proofNonce.size)
            output.write(proofNonce)
            output.write(tlsExporterSha256)
            output.toByteArray()
        }
    }

    /** SHA-256 of [peerProofTranscript], excluding the device signature and record framing. */
    fun peerProofSha256(
        ticketSha256: ByteArray,
        role: DirectPeerRoleV1,
        deviceId: UUID,
        peerDeviceId: UUID,
        connectionId: UUID,
        pathEpoch: Long,
        proofNonce: ByteArray,
        tlsExporterSha256: ByteArray,
    ): ByteArray {
        val transcript = peerProofTranscript(
            ticketSha256,
            role,
            deviceId,
            peerDeviceId,
            connectionId,
            pathEpoch,
            proofNonce,
            tlsExporterSha256,
        )
        return try {
            sha256(transcript)
        } finally {
            transcript.fill(0)
        }
    }

    /** Context supplied verbatim to the V1 TLS exporter. */
    fun tlsExporterContext(
        ticketSha256: ByteArray,
        connectionId: UUID,
        pathEpoch: Long,
    ): ByteArray {
        requireHash(ticketSha256, "Ticket SHA-256")
        requireNonZero(connectionId, "Connection ID")
        require(pathEpoch > 0) { "Path epoch must be positive" }
        return concatenate(
            ticketSha256,
            SessionCrypto.uuidBytes(connectionId),
            SessionCrypto.longBytes(pathEpoch),
        )
    }

    /**
     * Computes the role-ordered binding for a fully authenticated QUIC path.
     * Initiator fields always precede responder fields, irrespective of the local role.
     */
    fun pathBindingSha256(
        ticketSha256: ByteArray,
        initiatorCandidateId: UUID,
        responderCandidateId: UUID,
        initiatorCertificateSha256: ByteArray,
        responderCertificateSha256: ByteArray,
        initiatorProofSha256: ByteArray,
        responderProofSha256: ByteArray,
        tlsExporterSha256: ByteArray,
    ): ByteArray {
        requireHash(ticketSha256, "Ticket SHA-256")
        requireNonZero(initiatorCandidateId, "Initiator candidate ID")
        requireNonZero(responderCandidateId, "Responder candidate ID")
        require(initiatorCandidateId != responderCandidateId) { "Candidate IDs must be distinct" }
        requireHash(initiatorCertificateSha256, "Initiator certificate SHA-256")
        requireHash(responderCertificateSha256, "Responder certificate SHA-256")
        requireHash(initiatorProofSha256, "Initiator proof SHA-256")
        requireHash(responderProofSha256, "Responder proof SHA-256")
        requireHash(tlsExporterSha256, "TLS exporter SHA-256")
        val preimage = concatenate(
            pathBindingDomain,
            ticketSha256,
            SessionCrypto.uuidBytes(initiatorCandidateId),
            SessionCrypto.uuidBytes(responderCandidateId),
            initiatorCertificateSha256,
            responderCertificateSha256,
            initiatorProofSha256,
            responderProofSha256,
            tlsExporterSha256,
        )
        return try {
            sha256(preimage)
        } finally {
            preimage.fill(0)
        }
    }

    private fun deriveSignalKeyValidated(
        pairwiseKey: ByteArray,
        metadata: TransportSignalEnvelopeV1,
    ): ByteArray {
        validatePairwiseKey(pairwiseKey)
        val info = concatenate(
            signalDomain,
            metadata.connectionId.toByteArray(),
            SessionCrypto.longBytes(metadata.pathEpoch),
            metadata.senderDeviceId.toByteArray(),
            metadata.recipientDeviceId.toByteArray(),
            metadata.keyId.toByteArray(),
        )
        return try {
            hkdf(pairwiseKey, signalSalt, info)
        } finally {
            info.fill(0)
        }
    }

    private fun validateSignal(value: TransportSignalEnvelopeV1, form: SignalForm) {
        require(value.toByteArray().contentEquals(canonicalSignal(value, includeCiphertext = true).toByteArray())) {
            "Signal protobuf contains unsupported fields"
        }
        require(value.protocolVersion == 1) { "Signal protocol version is invalid" }
        require(value.signalType.number in 1..14) { "Signal type is invalid" }
        requireUuidBytes(value.connectionId, "Connection ID", nonZero = true)
        requireUuidBytes(value.senderDeviceId, "Sender device ID", nonZero = true)
        requireUuidBytes(value.recipientDeviceId, "Recipient device ID", nonZero = true)
        require(!value.senderDeviceId.equals(value.recipientDeviceId)) { "Signal devices must be distinct" }
        require(value.pathEpoch > 0) { "Path epoch must be positive" }
        require(value.signalSeq > 0) { "Signal sequence must be positive" }
        require(value.signalAck >= 0) { "Signal acknowledgement is out of range" }
        require(value.sentAtMs > 0) { "Signal timestamp must be positive" }
        require(value.keyId.size() in 1..MAX_KEY_ID_BYTES) { "Signal key ID length is invalid" }
        val unsealed = value.nonce.isEmpty && value.ciphertext.isEmpty
        val sealed = value.nonce.size() == NONCE_BYTES && value.ciphertext.size() >= TAG_BYTES
        require(
            when (form) {
                SignalForm.UNSEALED -> unsealed
                SignalForm.SEALED -> sealed
                SignalForm.EITHER -> unsealed || sealed
            },
        ) { "Signal cryptographic metadata is invalid" }
        require(value.serializedSize <= MAX_SIGNAL_BYTES) { "Signal exceeds the wire limit" }
    }

    private fun canonicalSignal(
        value: TransportSignalEnvelopeV1,
        includeCiphertext: Boolean,
    ): TransportSignalEnvelopeV1 = TransportSignalEnvelopeV1.newBuilder()
        .setProtocolVersion(value.protocolVersion)
        .setSignalType(value.signalType)
        .setConnectionId(value.connectionId)
        .setSenderDeviceId(value.senderDeviceId)
        .setRecipientDeviceId(value.recipientDeviceId)
        .setPathEpoch(value.pathEpoch)
        .setSignalSeq(value.signalSeq)
        .setSignalAck(value.signalAck)
        .setSentAtMs(value.sentAtMs)
        .setKeyId(value.keyId)
        .setNonce(value.nonce)
        .setCiphertext(if (includeCiphertext) value.ciphertext else ByteString.EMPTY)
        .build()

    private fun processChaCha(
        encrypt: Boolean,
        key: ByteArray,
        nonce: ByteArray,
        associatedData: ByteArray,
        input: ByteArray,
    ): ByteArray {
        require(key.size == KEY_BYTES) { "Signal key must be 32 bytes" }
        require(nonce.size == NONCE_BYTES) { "Signal nonce must be 12 bytes" }
        val cipher = ChaCha20Poly1305()
        cipher.init(encrypt, AEADParameters(KeyParameter(key), 128, nonce, associatedData))
        val output = ByteArray(cipher.getOutputSize(input.size))
        return try {
            var length = cipher.processBytes(input, 0, input.size, output, 0)
            length += cipher.doFinal(output, length)
            output.copyOf(length)
        } finally {
            output.fill(0)
        }
    }

    private fun hkdf(input: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
        val inputCopy = input.copyOf()
        val saltCopy = salt.copyOf()
        val infoCopy = info.copyOf()
        return try {
            val generator = HKDFBytesGenerator(SHA256Digest())
            generator.init(HKDFParameters(inputCopy, saltCopy, infoCopy))
            ByteArray(KEY_BYTES).also { generator.generateBytes(it, 0, it.size) }
        } finally {
            inputCopy.fill(0)
            saltCopy.fill(0)
            infoCopy.fill(0)
        }
    }

    private fun validatePairwiseKey(value: ByteArray) {
        require(value.size == KEY_BYTES) { "Pairwise key must be 32 bytes" }
    }

    private fun requireHash(value: ByteArray, name: String) {
        require(value.size == KEY_BYTES) { "$name must be 32 bytes" }
    }

    private fun requireIdentifier(value: ByteArray, name: String) {
        require(value.size == UUID_BYTES && value.any { it != 0.toByte() }) {
            "$name must be a non-zero 16-byte identifier"
        }
    }

    private fun requireNonZero(value: UUID, name: String) {
        require(value != zeroUuid) { "$name must be non-zero" }
    }

    private fun requireDistinctDevices(first: UUID, second: UUID) {
        requireNonZero(first, "Device ID")
        requireNonZero(second, "Peer device ID")
        require(first != second) { "Device IDs must be distinct" }
    }

    private fun requireUuidBytes(value: ByteString, name: String, nonZero: Boolean) {
        require(value.size() == UUID_BYTES) { "$name must be 16 bytes" }
        require(!nonZero || value.asReadOnlyByteBuffer().run {
            var any = false
            while (hasRemaining()) {
                if (get().toInt() != 0) any = true
            }
            any
        }) { "$name must be non-zero" }
    }

    private fun sha256(value: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(value)

    private fun concatenate(vararg values: ByteArray): ByteArray =
        ByteArrayOutputStream(values.sumOf(ByteArray::size)).use { output ->
            values.forEach(output::write)
            output.toByteArray()
        }

    private enum class SignalForm { UNSEALED, SEALED, EITHER }
}

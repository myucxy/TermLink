package dev.termlink.network.direct

import com.google.protobuf.ByteString
import com.google.protobuf.InvalidProtocolBufferException
import dev.termlink.protocol.transport.v1.DirectCandidatePairV1
import dev.termlink.protocol.transport.v1.DirectConnectivityDatagramKindV1
import dev.termlink.protocol.transport.v1.DirectConnectivityDatagramV1
import java.io.Closeable
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

internal enum class DirectProbeKind {
    PROBE,
    PROBE_ACK,
}

internal data class DirectProbeTemplate(
    val connectionId: ByteArray,
    val pathEpoch: Long,
    val ticketId: ByteArray,
    val nominationId: ByteArray,
    val initiatorCandidateId: ByteArray,
    val responderCandidateId: ByteArray,
    val senderDeviceId: ByteArray,
) {
    init {
        require(connectionId.size == DirectProbeCodec.ID_BYTES) { "connection id must be 16 bytes" }
        require(pathEpoch > 0) { "path epoch must be positive" }
        require(ticketId.size == DirectProbeCodec.ID_BYTES) { "ticket id must be 16 bytes" }
        require(nominationId.size == DirectProbeCodec.ID_BYTES) { "nomination id must be 16 bytes" }
        require(initiatorCandidateId.size == DirectProbeCodec.ID_BYTES) { "initiator candidate id must be 16 bytes" }
        require(responderCandidateId.size == DirectProbeCodec.ID_BYTES) { "responder candidate id must be 16 bytes" }
        require(senderDeviceId.size == DirectProbeCodec.ID_BYTES) { "sender device id must be 16 bytes" }
    }
}

internal data class DirectProbeExpectation(
    val template: DirectProbeTemplate,
    val kind: DirectProbeKind,
    val expectedProbeNonce: ByteArray? = null,
) {
    init {
        require(expectedProbeNonce == null || expectedProbeNonce.size == DirectProbeCodec.NONCE_BYTES) {
            "expected probe nonce must be 16 bytes"
        }
        require(kind != DirectProbeKind.PROBE_ACK || expectedProbeNonce != null) {
            "probe acknowledgement requires the original probe nonce"
        }
    }
}

internal data class DecodedDirectProbe(
    val kind: DirectProbeKind,
    val probeNonce: ByteArray,
    val responseNonce: ByteArray?,
    val sentAtMillis: Long,
)

internal class DirectProbeValidationException(message: String) : Exception(message)

/** Authenticated, bounded UDP wire codec only. It does not create a reliable or QUIC transport. */
internal class DirectProbeCodec(authKey: ByteArray) : Closeable {
    private val key = authKey.copyOf()
    private val secureRandom = SecureRandom()

    init {
        require(key.size == AUTH_KEY_BYTES) { "direct probe HMAC key must be 32 bytes" }
    }

    fun encodeProbe(template: DirectProbeTemplate, probeNonce: ByteArray, sentAtMillis: Long): ByteArray =
        encode(
            template,
            DirectProbeKind.PROBE,
            probeNonce,
            responseNonce = null,
            sentAtMillis,
            antiAmplificationPadding = ByteArray(ANTI_AMPLIFICATION_PADDING_BYTES).also(
                secureRandom::nextBytes,
            ),
        )

    fun encodeAcknowledgement(
        template: DirectProbeTemplate,
        probeNonce: ByteArray,
        responseNonce: ByteArray,
        sentAtMillis: Long,
    ): ByteArray = encode(
        template,
        DirectProbeKind.PROBE_ACK,
        probeNonce,
        responseNonce,
        sentAtMillis,
        antiAmplificationPadding = null,
    )

    @Throws(DirectProbeValidationException::class)
    fun decode(
        packet: ByteArray,
        expectation: DirectProbeExpectation,
        nowMillis: Long,
    ): DecodedDirectProbe {
        rejectUnless(packet.size in (WIRE_PREFIX.size + 1)..MAX_PACKET_BYTES, "probe packet size is invalid")
        rejectUnless(packet.copyOfRange(0, WIRE_PREFIX.size).contentEquals(WIRE_PREFIX), "probe prefix mismatch")
        val protobufBytes = packet.copyOfRange(WIRE_PREFIX.size, packet.size)
        val parsed = try {
            DirectConnectivityDatagramV1.parseFrom(protobufBytes)
        } catch (_: InvalidProtocolBufferException) {
            throw DirectProbeValidationException("probe protobuf is malformed")
        }
        validateFields(parsed, expectation, nowMillis)

        val canonicalKnownFields = knownFieldsBuilder(parsed)
            .setAuthTag(parsed.authTag)
            .build()
            .toByteArray()
        rejectUnless(
            protobufBytes.contentEquals(canonicalKnownFields),
            "probe protobuf is non-canonical or contains unknown fields",
        )
        val suppliedTag = parsed.authTag.toByteArray()
        val expectedTag = authenticate(knownFieldsBuilder(parsed).build().toByteArray())
        rejectUnless(MessageDigest.isEqual(suppliedTag, expectedTag), "probe authentication failed")

        return DecodedDirectProbe(
            kind = parsed.kind.toFoundationKind(),
            probeNonce = parsed.probeNonce.toByteArray(),
            responseNonce = parsed.responseNonce.takeUnless(ByteString::isEmpty)?.toByteArray(),
            sentAtMillis = parsed.sentAtMs,
        )
    }

    override fun close() {
        key.fill(0)
    }

    private fun encode(
        template: DirectProbeTemplate,
        kind: DirectProbeKind,
        probeNonce: ByteArray,
        responseNonce: ByteArray?,
        sentAtMillis: Long,
        antiAmplificationPadding: ByteArray?,
    ): ByteArray {
        require(probeNonce.size == NONCE_BYTES) { "probe nonce must be 16 bytes" }
        when (kind) {
            DirectProbeKind.PROBE -> {
                require(responseNonce == null) { "probe must not carry a response nonce" }
                require(antiAmplificationPadding?.size == ANTI_AMPLIFICATION_PADDING_BYTES) {
                    "probe anti-amplification padding must be 16 bytes"
                }
            }
            DirectProbeKind.PROBE_ACK -> require(responseNonce?.size == NONCE_BYTES) {
                "probe acknowledgement response nonce must be 16 bytes"
            }
        }
        if (kind == DirectProbeKind.PROBE_ACK) {
            require(antiAmplificationPadding == null) {
                "probe acknowledgement must not carry anti-amplification padding"
            }
        }
        val withoutTag = builder(
            template,
            kind,
            probeNonce,
            responseNonce,
            sentAtMillis,
            antiAmplificationPadding,
        ).build()
        val message = withoutTag.toBuilder()
            .setAuthTag(ByteString.copyFrom(authenticate(withoutTag.toByteArray())))
            .build()
        val packet = WIRE_PREFIX + message.toByteArray()
        require(packet.size <= MAX_PACKET_BYTES) { "probe packet exceeds the wire limit" }
        return packet
    }

    private fun validateFields(
        message: DirectConnectivityDatagramV1,
        expectation: DirectProbeExpectation,
        nowMillis: Long,
    ) {
        val expected = expectation.template
        rejectUnless(message.protocolVersion == PROTOCOL_VERSION, "probe protocol version mismatch")
        rejectUnless(message.hasPair(), "probe candidate pair is missing")
        rejectUnless(message.kind.toFoundationKind() == expectation.kind, "probe kind mismatch")
        rejectUnless(message.connectionId.matches(expected.connectionId), "probe connection id mismatch")
        rejectUnless(message.pathEpoch > 0 && message.pathEpoch == expected.pathEpoch, "probe epoch mismatch")
        rejectUnless(message.ticketId.matches(expected.ticketId), "probe ticket id mismatch")
        rejectUnless(message.nominationId.matches(expected.nominationId), "probe nomination id mismatch")
        rejectUnless(
            message.pair.initiatorCandidateId.matches(expected.initiatorCandidateId) &&
                message.pair.responderCandidateId.matches(expected.responderCandidateId),
            "probe candidate pair mismatch",
        )
        rejectUnless(message.senderDeviceId.matches(expected.senderDeviceId), "probe sender mismatch")
        rejectUnless(message.probeNonce.size() == NONCE_BYTES, "probe nonce length is invalid")
        when (expectation.kind) {
            DirectProbeKind.PROBE -> rejectUnless(message.responseNonce.isEmpty, "probe response nonce must be empty")
            DirectProbeKind.PROBE_ACK -> {
                rejectUnless(message.responseNonce.size() == NONCE_BYTES, "probe response nonce is invalid")
                rejectUnless(
                    message.probeNonce.matches(checkNotNull(expectation.expectedProbeNonce)),
                    "probe acknowledgement nonce mismatch",
                )
            }
        }
        when (expectation.kind) {
            DirectProbeKind.PROBE -> rejectUnless(
                message.antiAmplificationPadding.size() == ANTI_AMPLIFICATION_PADDING_BYTES,
                "probe anti-amplification padding length is invalid",
            )
            DirectProbeKind.PROBE_ACK -> rejectUnless(
                message.antiAmplificationPadding.isEmpty,
                "probe acknowledgement must not carry anti-amplification padding",
            )
        }
        rejectUnless(
            isWithinClockSkew(nowMillis, message.sentAtMs),
            "probe timestamp is outside the allowed window",
        )
        rejectUnless(message.authTag.size() == AUTH_TAG_BYTES, "probe authentication tag length is invalid")
    }

    private fun builder(
        template: DirectProbeTemplate,
        kind: DirectProbeKind,
        probeNonce: ByteArray,
        responseNonce: ByteArray?,
        sentAtMillis: Long,
        antiAmplificationPadding: ByteArray?,
    ): DirectConnectivityDatagramV1.Builder = DirectConnectivityDatagramV1.newBuilder()
        .setProtocolVersion(PROTOCOL_VERSION)
        .setKind(kind.toProtocolKind())
        .setConnectionId(ByteString.copyFrom(template.connectionId))
        .setPathEpoch(template.pathEpoch)
        .setTicketId(ByteString.copyFrom(template.ticketId))
        .setNominationId(ByteString.copyFrom(template.nominationId))
        .setPair(
            DirectCandidatePairV1.newBuilder()
                .setInitiatorCandidateId(ByteString.copyFrom(template.initiatorCandidateId))
                .setResponderCandidateId(ByteString.copyFrom(template.responderCandidateId)),
        )
        .setSenderDeviceId(ByteString.copyFrom(template.senderDeviceId))
        .setProbeNonce(ByteString.copyFrom(probeNonce))
        .setResponseNonce(ByteString.copyFrom(responseNonce ?: ByteArray(0)))
        .setSentAtMs(sentAtMillis)
        .setAntiAmplificationPadding(ByteString.copyFrom(antiAmplificationPadding ?: ByteArray(0)))

    private fun knownFieldsBuilder(
        parsed: DirectConnectivityDatagramV1,
    ): DirectConnectivityDatagramV1.Builder = DirectConnectivityDatagramV1.newBuilder()
        .setProtocolVersion(parsed.protocolVersion)
        .setKind(parsed.kind)
        .setConnectionId(parsed.connectionId)
        .setPathEpoch(parsed.pathEpoch)
        .setTicketId(parsed.ticketId)
        .setNominationId(parsed.nominationId)
        .setPair(parsed.pair)
        .setSenderDeviceId(parsed.senderDeviceId)
        .setProbeNonce(parsed.probeNonce)
        .setResponseNonce(parsed.responseNonce)
        .setSentAtMs(parsed.sentAtMs)
        .setAntiAmplificationPadding(parsed.antiAmplificationPadding)

    private fun authenticate(canonicalFields: ByteArray): ByteArray {
        val mac = Mac.getInstance(HMAC_ALGORITHM)
        mac.init(SecretKeySpec(key, HMAC_ALGORITHM))
        mac.update(HMAC_DOMAIN)
        return mac.doFinal(canonicalFields).copyOf(AUTH_TAG_BYTES)
    }

    private fun isWithinClockSkew(nowMillis: Long, sentAtMillis: Long): Boolean {
        val difference = try {
            Math.subtractExact(nowMillis, sentAtMillis)
        } catch (_: ArithmeticException) {
            return false
        }
        return difference != Long.MIN_VALUE && Math.abs(difference) <= MAX_CLOCK_SKEW_MILLIS
    }

    private fun ByteString.matches(expected: ByteArray): Boolean =
        size() == expected.size && MessageDigest.isEqual(toByteArray(), expected)

    private fun DirectProbeKind.toProtocolKind(): DirectConnectivityDatagramKindV1 = when (this) {
        DirectProbeKind.PROBE ->
            DirectConnectivityDatagramKindV1.DIRECT_CONNECTIVITY_DATAGRAM_KIND_V1_PROBE
        DirectProbeKind.PROBE_ACK ->
            DirectConnectivityDatagramKindV1.DIRECT_CONNECTIVITY_DATAGRAM_KIND_V1_PROBE_ACK
    }

    private fun DirectConnectivityDatagramKindV1.toFoundationKind(): DirectProbeKind = when (this) {
        DirectConnectivityDatagramKindV1.DIRECT_CONNECTIVITY_DATAGRAM_KIND_V1_PROBE -> DirectProbeKind.PROBE
        DirectConnectivityDatagramKindV1.DIRECT_CONNECTIVITY_DATAGRAM_KIND_V1_PROBE_ACK ->
            DirectProbeKind.PROBE_ACK
        else -> throw DirectProbeValidationException("unsupported probe kind")
    }

    private fun rejectUnless(condition: Boolean, message: String) {
        if (!condition) throw DirectProbeValidationException(message)
    }

    internal companion object {
        internal const val PROTOCOL_VERSION = 1
        internal const val ID_BYTES = 16
        internal const val NONCE_BYTES = 16
        internal const val AUTH_KEY_BYTES = 32
        internal const val AUTH_TAG_BYTES = 16
        internal const val ANTI_AMPLIFICATION_PADDING_BYTES = 16
        internal const val MAX_PACKET_BYTES = 512
        internal const val MAX_CLOCK_SKEW_MILLIS = 30_000L
        internal val WIRE_PREFIX = "TLD1\n".encodeToByteArray()
        private val HMAC_DOMAIN = "TERMLINK-DIRECT-PROBE-HMAC-V1\u0000".encodeToByteArray()
        private const val HMAC_ALGORITHM = "HmacSHA256"
    }
}

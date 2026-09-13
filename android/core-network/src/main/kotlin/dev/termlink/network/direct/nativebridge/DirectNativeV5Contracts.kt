package dev.termlink.network.direct.nativebridge

import java.io.Closeable
import java.nio.ByteBuffer
import java.nio.ByteOrder

internal const val DIRECT_NATIVE_ABI_VERSION_V5 = 5
internal const val DIRECT_NATIVE_MAX_FRAMES_V5 = 256
internal const val DIRECT_NATIVE_MAX_BYTES_V5 = 8 * 1024 * 1024
internal const val DIRECT_NATIVE_FAILURE_ACTION_TAIL_V5 = 4
internal const val DIRECT_NATIVE_MAX_RETAINED_FRAMES_V5 =
    DIRECT_NATIVE_MAX_FRAMES_V5 + DIRECT_NATIVE_FAILURE_ACTION_TAIL_V5
internal const val DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5 = 1024 * 1024 - 1
internal const val DIRECT_NATIVE_FIRST_DATA_FRAME_SEQUENCE_V5 = 4L
internal const val DIRECT_NATIVE_MAX_FRAME_SEQUENCE_V5 = Long.MAX_VALUE
internal const val DIRECT_NATIVE_MAX_APPLIED_CURSORS_PER_DIRECTION_V5 = 256
internal const val DIRECT_NATIVE_MAX_SESSIONS_V5 = 32
internal const val DIRECT_NATIVE_MAX_SIGNATURE_BYTES_V5 = 128

internal object DirectNativeStatusV5 {
    const val OK = 0
    const val NO_DATA = 1
    const val INVALID_HANDLE = -1
    const val INVALID_ARGUMENT = -2
    const val QUEUE_FULL = -3
    const val STALE_PATH = -4
    const val BUFFER_TOO_SMALL = -5
    const val NOT_READY = -6
    const val CANCELLED = -7
    const val LIMIT = -8
    const val PROTOCOL = -9
    const val DISABLED = -10
    const val TIMEOUT = -11
    const val TICKET_EXPIRED = -12
}

/** Runtime proof for the fixed-layout records encoded by this Kotlin bridge. */
internal class DirectNativeAbiContractV5 private constructor(raw: LongArray) {
    private val values = raw.copyOf()
    val abiVersion: Int get() = values[0].toInt()
    val isCompatible: Boolean get() = values.contentEquals(EXPECTED)

    internal companion object {
        /*
         * Version, hard limits, record sizes, then record alignments. Header
         * static_asserts cover every field offset. Requiring every size and
         * alignment here prevents a stale packaged .so from accepting Kotlin
         * records before a handle is created.
         */
        private val EXPECTED = longArrayOf(
            5,
            256, 8L * 1024L * 1024L, 1024L * 1024L - 1L,
            4, 260, 4, Long.MAX_VALUE, 256,
            64, 112, 80, 48, 128, 288, 544, 128, 232, 24, 968, 176, 120, 128, 136, 200,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        )

        fun from(raw: LongArray): DirectNativeAbiContractV5? =
            raw.takeIf { it.size == EXPECTED.size }?.let(::DirectNativeAbiContractV5)
    }
}

internal class DirectNativePathV5(
    val epoch: Long,
    connectionId: ByteArray,
    attemptId: ByteArray,
) {
    val connectionId = connectionId.copyOf()
    val attemptId = attemptId.copyOf()

    init {
        require(epoch > 0) { "Direct v5 path epoch must be positive" }
        require(this.connectionId.isNonZeroIdentifierV5())
        require(this.attemptId.isNonZeroIdentifierV5())
    }

    internal fun ownedCopy() = DirectNativePathV5(epoch, connectionId, attemptId)
    internal fun sameFence(other: DirectNativePathV5): Boolean =
        epoch == other.epoch && connectionId.contentEquals(other.connectionId) &&
            attemptId.contentEquals(other.attemptId)

    internal fun encode(): ByteArray = abiRecord(PATH_BYTES).apply {
        putLong(8, epoch)
        putBytes(16, connectionId)
        putBytes(32, attemptId)
    }.array()

    override fun equals(other: Any?): Boolean = other is DirectNativePathV5 && sameFence(other)
    override fun hashCode(): Int =
        31 * (31 * epoch.hashCode() + connectionId.contentHashCode()) + attemptId.contentHashCode()

    internal companion object {
        const val PATH_BYTES = 64
    }
}

internal data class DirectNativeCreateConfigV5(
    val enabled: Boolean = false,
    val queueCapacityFrames: Int = DIRECT_NATIVE_MAX_FRAMES_V5,
    val queueCapacityBytes: Long = DIRECT_NATIVE_MAX_BYTES_V5.toLong(),
    val initialPath: DirectNativePathV5? = null,
) {
    init {
        require(queueCapacityFrames in 1..DIRECT_NATIVE_MAX_FRAMES_V5)
        require(queueCapacityBytes in 1..DIRECT_NATIVE_MAX_BYTES_V5.toLong())
    }
}

internal enum class DirectNativeDirectionV5(val code: Int) {
    PC_TO_ANDROID(1), ANDROID_TO_PC(2);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code }
    }
}

internal enum class DirectNativePathKindV5(val code: Int) {
    LAN(1), NAT(2);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code }
    }
}

internal enum class DirectNativeCarrierV5(val code: Int) {
    RELAY(1), LAN_DIRECT(2), NAT_DIRECT(3);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code }
    }
}

internal enum class DirectNativeIngressSourceV5(val code: Int) {
    RELAY(1), DIRECT(2);
}

internal class DirectNativePromoteRequestV5(
    path: DirectNativePathV5,
    val nomination: DirectNativeCommitNominationRequestV4,
) {
    val path = path.ownedCopy()

    init {
        require(path.epoch == nomination.epoch)
        require(path.attemptId.contentEquals(nomination.attemptId))
    }

    /** Android's semantic outbound direction is fixed, independent of QUIC role. */
    internal fun encode(): ByteArray = abiRecord(PROMOTE_BYTES).apply {
        putBytes(8, path.encode())
        putBytes(72, nomination.checkRoundId)
        putBytes(88, nomination.nominationId)
        putCandidatePairV5(104, nomination.winningPair)
        putInt(392, nomination.role.code)
        putInt(396, nomination.timeoutMillis)
        putInt(400, if (nomination.selectionConfirmed) 1 else 0)
        putInt(404, nomination.localProofNonce.size)
        putBytes(408, nomination.peerCertificateSha256)
        putBytes(440, nomination.peerDeviceSigningPublicKey)
        putBytes(472, nomination.localProofNonce)
        putLong(504, nomination.nowMillis)
        putInt(512, DirectNativeDirectionV5.ANDROID_TO_PC.code)
    }.array()

    private companion object {
        const val PROMOTE_BYTES = 544
    }
}

internal data class DirectNativeSigningPayloadV5(val requestId: Long, val bytes: ByteArray)

internal class DirectNativePathInfoV5(
    path: DirectNativePathV5,
    val pathKind: DirectNativePathKindV5?,
    val ready: Boolean,
    directPathBinding: ByteArray,
) {
    val path = path.ownedCopy()
    val directPathBinding = directPathBinding.copyOf()
}

internal data class DirectNativeSessionBarrierV5(
    val sessionId: ByteArray,
    val firstDirectSequence: Long,
) {
    init {
        require(sessionId.isNonZeroIdentifierV5())
        require(firstDirectSequence in 1..Long.MAX_VALUE)
    }
}

internal class DirectNativeProposalV5(
    path: DirectNativePathV5,
    val direction: DirectNativeDirectionV5,
    barrierId: ByteArray,
    barrierSha256: ByteArray,
    directPathBinding: ByteArray,
    val duplicateGraceMillis: Int,
    val nowMillis: Long,
    val timeoutMillis: Long,
    sessions: List<DirectNativeSessionBarrierV5>,
) : Closeable {
    val path = path.ownedCopy()
    val barrierId = barrierId.copyOf()
    val barrierSha256 = barrierSha256.copyOf()
    val directPathBinding = directPathBinding.copyOf()
    val sessions = sessions.map { it.copy(sessionId = it.sessionId.copyOf()) }

    init {
        require(this.barrierId.isNonZeroIdentifierV5())
        require(this.barrierSha256.size == 32 && this.barrierSha256.any { it != 0.toByte() })
        require(this.directPathBinding.size == 32 && this.directPathBinding.any { it != 0.toByte() })
        require(duplicateGraceMillis in 0..5_000)
        require(nowMillis >= 0 && timeoutMillis > 0)
        require(this.sessions.size in 1..DIRECT_NATIVE_MAX_SESSIONS_V5)
        require(this.sessions.map { it.sessionId.contentKeyV5() }.distinct().size == this.sessions.size)
        require(this.sessions.zipWithNext().all { (left, right) ->
            compareUnsignedV5(left.sessionId, right.sessionId) < 0
        }) { "Direct v5 proposal session barriers must be in canonical unsigned order" }
    }

    internal fun encode(): ByteArray = abiRecord(PROPOSAL_BYTES).apply {
        putBytes(8, path.encode())
        putInt(72, direction.code)
        putInt(76, sessions.size)
        putBytes(80, barrierId)
        putBytes(96, barrierSha256)
        putBytes(128, directPathBinding)
        putInt(160, duplicateGraceMillis)
        putLong(168, nowMillis)
        putLong(176, timeoutMillis)
        sessions.forEachIndexed { index, session ->
            val offset = 184 + index * 24
            putBytes(offset, session.sessionId)
            putLong(offset + 16, session.firstDirectSequence)
        }
    }.array()

    override fun close() {
        path.connectionId.fill(0)
        path.attemptId.fill(0)
        barrierId.fill(0)
        barrierSha256.fill(0)
        directPathBinding.fill(0)
        sessions.forEach { it.sessionId.fill(0) }
    }

    private companion object {
        const val PROPOSAL_BYTES = 968
    }
}

internal class DirectNativeTransitionV5(
    path: DirectNativePathV5,
    val direction: DirectNativeDirectionV5,
    barrierId: ByteArray,
    barrierSha256: ByteArray,
    val accepted: Boolean,
    val isEcho: Boolean,
    val nowMillis: Long,
    val timeoutMillis: Long,
) {
    val path = path.ownedCopy()
    val barrierId = barrierId.copyOf()
    val barrierSha256 = barrierSha256.copyOf()

    init {
        require(this.barrierId.isNonZeroIdentifierV5())
        require(this.barrierSha256.size == 32 && this.barrierSha256.any { it != 0.toByte() })
        require(nowMillis >= 0 && timeoutMillis >= 0)
    }

    internal fun encode(): ByteArray = abiRecord(TRANSITION_BYTES).apply {
        putBytes(8, path.encode())
        putInt(72, direction.code)
        // outcome at 76 is reserved by v5 semantics and must remain zero.
        putBytes(80, barrierId)
        putBytes(96, barrierSha256)
        putInt(128, if (accepted) 1 else 0)
        putInt(132, if (isEcho) 1 else 0)
        putLong(144, nowMillis)
        putLong(152, timeoutMillis)
    }.array()

    private companion object {
        const val TRANSITION_BYTES = 176
    }
}

internal class DirectNativeCursorV5(
    path: DirectNativePathV5,
    val direction: DirectNativeDirectionV5,
    sessionId: ByteArray,
    val highestContiguous: Long,
) {
    val path = path.ownedCopy()
    val sessionId = sessionId.copyOf()

    init {
        require(this.sessionId.isNonZeroIdentifierV5())
        require(highestContiguous >= 0)
    }

    internal fun encode(): ByteArray = abiRecord(CURSOR_BYTES).apply {
        putBytes(8, path.encode())
        putInt(72, direction.code)
        putBytes(80, sessionId)
        putLong(96, highestContiguous)
    }.array()

    private companion object {
        const val CURSOR_BYTES = 120
    }
}

internal class DirectNativeRouteEnvelopeV5(
    path: DirectNativePathV5,
    val direction: DirectNativeDirectionV5,
    val routeEligible: Boolean,
    sessionId: ByteArray,
    val envelopeSequence: Long,
    val nowMillis: Long,
) {
    val path = path.ownedCopy()
    val sessionId = sessionId.copyOf()

    init {
        require(this.sessionId.isNonZeroIdentifierV5())
        require(envelopeSequence > 0 && nowMillis >= 0)
    }

    internal fun encode(): ByteArray = abiRecord(ROUTE_ENVELOPE_BYTES).apply {
        putBytes(8, path.encode())
        putInt(72, direction.code)
        putInt(76, if (routeEligible) 1 else 0)
        putBytes(80, sessionId)
        putLong(96, envelopeSequence)
        putLong(104, nowMillis)
    }.array()

    private companion object {
        const val ROUTE_ENVELOPE_BYTES = 128
    }
}

internal class DirectNativeIngressV5(
    path: DirectNativePathV5,
    val direction: DirectNativeDirectionV5,
    val source: DirectNativeIngressSourceV5,
    sessionId: ByteArray,
    val envelopeSequence: Long,
    val nowMillis: Long,
    val duplicateGraceMillis: Int,
) {
    val path = path.ownedCopy()
    val sessionId = sessionId.copyOf()

    init {
        require(this.sessionId.isNonZeroIdentifierV5())
        require(envelopeSequence > 0 && nowMillis >= 0)
        require(duplicateGraceMillis in 0..5_000)
    }

    internal fun encode(): ByteArray = abiRecord(INGRESS_BYTES).apply {
        putBytes(8, path.encode())
        putInt(72, direction.code)
        putInt(76, source.code)
        putBytes(80, sessionId)
        putLong(96, envelopeSequence)
        putLong(104, nowMillis)
        putInt(112, duplicateGraceMillis)
    }.array()

    private companion object {
        const val INGRESS_BYTES = 136
    }
}

internal enum class DirectNativeRouteActionKindV5(val code: Int) {
    DIRECT_ENVELOPE(1), RELAY_ENVELOPE(2), ACCEPT(4), COMMIT(5), FALLBACK(6), CARRIER(7),
    INGRESS_DELIVER(8), INGRESS_HOLD(9), INGRESS_DROP(10), SESSION_CARRIER(11), UNKNOWN(-1);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal data class DirectNativeRouteActionV5(
    val path: DirectNativePathV5,
    val kind: DirectNativeRouteActionKindV5,
    val direction: DirectNativeDirectionV5,
    val sessionId: ByteArray,
    val envelopeSequence: Long,
    val directFrameSequence: Long,
    val barrierId: ByteArray,
    val barrierSha256: ByteArray,
    val accepted: Boolean,
    val isEcho: Boolean,
    val carrier: DirectNativeCarrierV5?,
    val flags: Int,
    val reason: Int,
    val payloadBytes: Int,
    val payload: ByteArray,
)

internal enum class DirectNativeEventKindV5(val code: Int) {
    PATH_USED(1), ENVELOPE_QUEUED(2), ENVELOPE_AVAILABLE(3), TRANSPORT_RECEIPT(4),
    CANCELLED(5), NETWORK_CHANGED(6), QUEUE_LIMIT(7), PATH_READY(8), DIRECT_FAILED(9),
    PING(10), PONG(11), SIGN_REQUEST(12), UNKNOWN(-1);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal data class DirectNativeEventV5(
    val kind: DirectNativeEventKindV5,
    val status: Int,
    val flags: Int,
    val pathEpoch: Long,
    val attemptId: ByteArray,
    val frameSequence: Long,
    val payloadBytes: Int,
    val releasedFrames: Int,
)

internal sealed interface DirectNativeResultV5<out T> {
    data class Success<T>(val value: T) : DirectNativeResultV5<T>
    data class Failure(
        val reason: Int,
        val nativeStatus: Int? = null,
        val pathEpoch: Long = 0,
        val attemptId: ByteArray = ByteArray(16),
    ) : DirectNativeResultV5<Nothing>
}

internal object DirectNativeSyntheticReasonV5 {
    const val NATIVE_UNAVAILABLE = 2_001
    const val ABI_MISMATCH = 2_002
    const val FFI_CALL_FAILED = 2_003
    const val HANDLE_CLOSED = 2_004
    const val STALE_OWNER = 2_005
    const val INVALID_NATIVE_RESPONSE = 2_006
    const val SIGNATURE_NOT_REQUESTED = 2_007
}

internal fun decodePathInfoV5(bytes: ByteArray): DirectNativePathInfoV5? {
    if (bytes.size != 128) return null
    val buffer = bytes.abiBufferV5()
    val path = buffer.decodePathV5(8) ?: return null
    val kindCode = buffer.getInt(72)
    val kind = if (kindCode == 0) null else DirectNativePathKindV5.from(kindCode) ?: return null
    val ready = buffer.getInt(76)
    val binding = bytes.copyOfRange(80, 112)
    if (buffer.getInt(0) != 5 || buffer.getInt(4) != 128 || ready !in 0..1 ||
        ready == 0 && (kind != null || binding.any { it != 0.toByte() }) ||
        ready == 1 && (kind == null || binding.all { it == 0.toByte() }) ||
        bytes.copyOfRange(112, 128).any { it != 0.toByte() }
    ) return null
    return DirectNativePathInfoV5(path, kind, ready == 1, binding)
}

internal fun decodeRouteActionV5(bytes: ByteArray, payload: ByteArray): DirectNativeRouteActionV5? {
    if (bytes.size != 200) return null
    val buffer = bytes.abiBufferV5()
    val path = buffer.decodePathV5(8) ?: return null
    val kind = DirectNativeRouteActionKindV5.from(buffer.getInt(72))
    val direction = DirectNativeDirectionV5.from(buffer.getInt(76)) ?: return null
    val sessionId = bytes.copyOfRange(80, 96)
    val accepted = buffer.getInt(160)
    val echo = buffer.getInt(164)
    val carrierCode = buffer.getInt(168)
    val payloadBytes = buffer.getInt(180)
    val envelopeSequence = buffer.getLong(96)
    val directFrameSequence = buffer.getLong(104)
    val barrierId = bytes.copyOfRange(112, 128)
    val barrierSha256 = bytes.copyOfRange(128, 160)
    val flags = buffer.getInt(172)
    val reason = buffer.getInt(176)
    if (buffer.getInt(0) != 5 || buffer.getInt(4) != 200 ||
        kind == DirectNativeRouteActionKindV5.UNKNOWN || accepted !in 0..1 || echo !in 0..1 ||
        payloadBytes !in 0..DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5 || flags != 0 ||
        bytes.copyOfRange(184, 200).any { it != 0.toByte() }
    ) return null
    val carrier = if (carrierCode == 0) null else DirectNativeCarrierV5.from(carrierCode) ?: return null
    val sessionPresent = sessionId.isNonZeroIdentifierV5()
    val barrierPresent = barrierId.isNonZeroIdentifierV5()
    val digestPresent = barrierSha256.size == 32 && barrierSha256.any { it != 0.toByte() }
    val noSession = sessionId.all { it == 0.toByte() }
    val noBarrier = barrierId.all { it == 0.toByte() }
    val noDigest = barrierSha256.all { it == 0.toByte() }
    val envelopeMetadata = sessionPresent && envelopeSequence > 0 && payloadBytes > 0
    val noEnvelopeMetadata = noSession && envelopeSequence == 0L && payloadBytes == 0
    val dataFrame = directFrameSequence in
        DIRECT_NATIVE_FIRST_DATA_FRAME_SEQUENCE_V5..DIRECT_NATIVE_MAX_FRAME_SEQUENCE_V5
    val noOutcome = accepted == 0 && echo == 0
    val valid = when (kind) {
        DirectNativeRouteActionKindV5.DIRECT_ENVELOPE ->
            envelopeMetadata && dataFrame && noBarrier && noDigest && noOutcome &&
                carrier in setOf(DirectNativeCarrierV5.LAN_DIRECT, DirectNativeCarrierV5.NAT_DIRECT) &&
                reason.isFallbackReasonV5OrZero() && payload.isEmpty()
        DirectNativeRouteActionKindV5.RELAY_ENVELOPE ->
            envelopeMetadata && directFrameSequence == 0L && noBarrier && noDigest && noOutcome &&
                carrier == DirectNativeCarrierV5.RELAY && reason.isFallbackReasonV5OrZero() &&
                payload.size == payloadBytes
        DirectNativeRouteActionKindV5.ACCEPT ->
            noEnvelopeMetadata && directFrameSequence == 0L && barrierPresent && digestPresent &&
                echo == 0 && carrier == null && payload.isEmpty() &&
                if (accepted == 1) reason == 0 else reason.isRejectReasonV5()
        DirectNativeRouteActionKindV5.COMMIT ->
            noEnvelopeMetadata && directFrameSequence == 0L && barrierPresent && digestPresent &&
                accepted == 0 && echo in 0..1 && carrier == null && reason == 0 && payload.isEmpty()
        DirectNativeRouteActionKindV5.FALLBACK ->
            noEnvelopeMetadata &&
                (directFrameSequence == 0L || directFrameSequence in
                    (DIRECT_NATIVE_FIRST_DATA_FRAME_SEQUENCE_V5 - 1)..DIRECT_NATIVE_MAX_FRAME_SEQUENCE_V5) &&
                (noBarrier || barrierPresent) && noDigest && noOutcome && carrier == null &&
                reason.isFallbackReasonV5() && payload.isEmpty()
        DirectNativeRouteActionKindV5.CARRIER ->
            noEnvelopeMetadata && directFrameSequence == 0L && noBarrier && noDigest && noOutcome &&
                carrier != null && reason == 0 && payload.isEmpty()
        DirectNativeRouteActionKindV5.SESSION_CARRIER ->
            sessionPresent && envelopeSequence == 0L && payloadBytes == 0 &&
                directFrameSequence == 0L && noBarrier && noDigest && noOutcome &&
                carrier != null && reason == 0 && payload.isEmpty()
        DirectNativeRouteActionKindV5.INGRESS_DELIVER ->
            envelopeMetadata && directFrameSequence == 0L && noBarrier && noDigest && noOutcome &&
                carrier != null && reason == 0 && payload.size == payloadBytes
        DirectNativeRouteActionKindV5.INGRESS_HOLD,
        DirectNativeRouteActionKindV5.INGRESS_DROP ->
            envelopeMetadata && directFrameSequence == 0L && noBarrier && noDigest && noOutcome &&
                carrier != null && reason == 0 && payload.isEmpty()
        DirectNativeRouteActionKindV5.UNKNOWN -> false
    }
    if (!valid) return null
    return DirectNativeRouteActionV5(
        path, kind, direction, sessionId, envelopeSequence, directFrameSequence,
        barrierId, barrierSha256, accepted == 1, echo == 1,
        carrier, flags, reason, payloadBytes, payload.copyOf(),
    )
}

private fun Int.isFallbackReasonV5(): Boolean = this in 1..9
private fun Int.isFallbackReasonV5OrZero(): Boolean = this == 0 || isFallbackReasonV5()
private fun Int.isRejectReasonV5(): Boolean = this in 101..111

private fun abiRecord(size: Int): ByteBuffer =
    ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN).apply {
        putInt(0, DIRECT_NATIVE_ABI_VERSION_V5)
        putInt(4, size)
    }

private fun ByteBuffer.putCandidatePairV5(offset: Int, pair: DirectNativeCandidatePairV4) {
    putInt(offset, DIRECT_NATIVE_ABI_VERSION_V5)
    putInt(offset + 4, 288)
    putInt(offset + 8, pair.rank)
    putCandidateV5(offset + 16, pair.initiator)
    putCandidateV5(offset + 144, pair.responder)
}

private fun ByteBuffer.putCandidateV5(offset: Int, candidate: DirectNativeCandidateV4) {
    putInt(offset, DIRECT_NATIVE_ABI_VERSION_V5)
    putInt(offset + 4, 128)
    putBytes(offset + 8, candidate.candidateId)
    putBytes(offset + 24, candidate.relatedCandidateId)
    putInt(offset + 40, DIRECT_NATIVE_ABI_VERSION_V5)
    putInt(offset + 44, 48)
    putBytes(offset + 48, candidate.endpoint.ipAddress.copyOf(16))
    putInt(offset + 64, candidate.endpoint.ipAddress.size)
    putInt(offset + 68, candidate.endpoint.port)
    putInt(offset + 88, candidate.kind.code)
    putInt(offset + 92, 1) // UDP/QUIC carrier.
    putInt(offset + 96, candidate.priority)
    putInt(offset + 100, candidate.networkCost)
    putLong(offset + 104, candidate.expiresAtMillis)
}

private fun ByteBuffer.putBytes(offset: Int, value: ByteArray) {
    val previous = position()
    position(offset)
    put(value)
    position(previous)
}

private fun ByteArray.abiBufferV5(): ByteBuffer = ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN)

private fun ByteBuffer.decodePathV5(offset: Int): DirectNativePathV5? {
    if (getInt(offset) != 5 || getInt(offset + 4) != 64 ||
        getLong(offset + 8) <= 0 ||
        (offset + 48 until offset + 64).any { get(it) != 0.toByte() }
    ) return null
    return runCatching {
        DirectNativePathV5(
            getLong(offset + 8),
            array().copyOfRange(offset + 16, offset + 32),
            array().copyOfRange(offset + 32, offset + 48),
        )
    }.getOrNull()
}

private fun ByteArray.isNonZeroIdentifierV5(): Boolean = size == 16 && any { it != 0.toByte() }
private fun ByteArray.contentKeyV5(): String = joinToString(separator = "") { "%02x".format(it) }

private fun compareUnsignedV5(left: ByteArray, right: ByteArray): Int {
    for (index in 0 until minOf(left.size, right.size)) {
        val compared = (left[index].toInt() and 0xff).compareTo(right[index].toInt() and 0xff)
        if (compared != 0) return compared
    }
    return left.size.compareTo(right.size)
}

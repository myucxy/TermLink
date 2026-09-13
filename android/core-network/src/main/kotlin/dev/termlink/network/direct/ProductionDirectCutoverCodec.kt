package dev.termlink.network.direct

import com.google.protobuf.ByteString
import com.google.protobuf.InvalidProtocolBufferException
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.transport.v1.DirectCutoverProposalV1
import dev.termlink.protocol.transport.v1.SessionCutoverBarrierV1
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.Collections
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.io.Closeable

internal data class ProductionDirectSessionCutoverBarrier(
    val sessionId: UUID,
    val firstDirectSequence: Long,
)

/**
 * An immutable, defensively-owned canonical cutover proposal.
 *
 * Byte arrays are copied both when this value is created and whenever a caller
 * reads them. This keeps later caller mutations from changing the authenticated
 * proposal or its digest.
 */
internal class ProductionDirectCutoverProposal private constructor(
    val barrierId: UUID,
    val direction: Direction,
    directPathBinding: ByteArray,
    barrierSha256: ByteArray,
    val duplicateGraceMilliseconds: Int,
    sessionBarriers: List<ProductionDirectSessionCutoverBarrier>,
    wireBytes: ByteArray,
) : Closeable {
    private val closed = AtomicBoolean(false)
    private val ownedDirectPathBinding = directPathBinding.copyOf()
    private val ownedBarrierSha256 = barrierSha256.copyOf()
    private val ownedSessionBarriers = Collections.unmodifiableList(
        sessionBarriers.map { it.copy() },
    )
    private val ownedWireBytes = wireBytes.copyOf()

    val directPathBinding: ByteArray
        get() = ownedDirectPathBinding.copyOf()

    val barrierSha256: ByteArray
        get() = ownedBarrierSha256.copyOf()

    val sessionBarriers: List<ProductionDirectSessionCutoverBarrier>
        get() = ownedSessionBarriers

    val wireBytes: ByteArray
        get() = ownedWireBytes.copyOf()

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        ownedDirectPathBinding.fill(0)
        ownedBarrierSha256.fill(0)
        ownedWireBytes.fill(0)
    }

    internal companion object {
        fun owned(
            barrierId: UUID,
            direction: Direction,
            directPathBinding: ByteArray,
            barrierSha256: ByteArray,
            duplicateGraceMilliseconds: Int,
            sessionBarriers: List<ProductionDirectSessionCutoverBarrier>,
            wireBytes: ByteArray,
        ) = ProductionDirectCutoverProposal(
            barrierId = barrierId,
            direction = direction,
            directPathBinding = directPathBinding,
            barrierSha256 = barrierSha256,
            duplicateGraceMilliseconds = duplicateGraceMilliseconds,
            sessionBarriers = sessionBarriers,
            wireBytes = wireBytes,
        )
    }
}

/**
 * Canonical protobuf and SHA-256 boundary for independently committed Direct
 * directions. It owns no socket, Relay, or durable-ACK state.
 *
 * A received proposal is accepted only when rebuilding its validated known
 * fields produces the exact original bytes. That rejects unknown fields,
 * duplicate singular fields, non-minimal varints, non-canonical field order,
 * and non-canonical session order.
 */
internal object ProductionDirectCutoverCodec {
    internal const val MAXIMUM_SESSION_BARRIERS = 32
    internal const val MAXIMUM_DUPLICATE_GRACE_MILLISECONDS = 5_000
    internal const val MAXIMUM_PROPOSAL_BYTES = 4 * 1024

    private val digestDomain = "TERMLINK-DIRECT-CUTOVER-V1\u0000".encodeToByteArray()
    private val zeroUuid = UUID(0, 0)

    fun createProposal(
        connectionId: UUID,
        pathEpoch: Long,
        direction: Direction,
        barrierId: UUID,
        directPathBinding: ByteArray,
        sessionBarriers: Iterable<ProductionDirectSessionCutoverBarrier>,
        duplicateGraceMilliseconds: Int,
    ): ProductionDirectCutoverProposal {
        validateOwner(connectionId, pathEpoch, direction)
        require(barrierId != zeroUuid) { "A non-empty cutover barrier ID is required." }
        require(directPathBinding.size == DIRECT_PATH_BINDING_BYTES && !directPathBinding.isAllZero()) {
            "A non-zero 32-byte Direct path binding is required."
        }
        require(duplicateGraceMilliseconds in 0..MAXIMUM_DUPLICATE_GRACE_MILLISECONDS) {
            "The duplicate grace interval is outside its allowed range."
        }
        val ownedBinding = directPathBinding.copyOf()
        val canonicalBarriers = canonicalizeBarriers(sessionBarriers)
        val wire = buildMessage(
            direction = direction,
            barrierId = barrierId,
            directPathBinding = ownedBinding,
            sessionBarriers = canonicalBarriers,
            duplicateGraceMilliseconds = duplicateGraceMilliseconds,
        ).toByteArray()
        require(wire.size in 1..MAXIMUM_PROPOSAL_BYTES) {
            "The cutover proposal exceeds its wire limit."
        }
        return result(
            connectionId = connectionId,
            pathEpoch = pathEpoch,
            direction = direction,
            barrierId = barrierId,
            directPathBinding = ownedBinding,
            sessionBarriers = canonicalBarriers,
            duplicateGraceMilliseconds = duplicateGraceMilliseconds,
            wire = wire,
        )
    }

    /** Returns null for every malformed, non-canonical, or owner-mismatched proposal. */
    fun tryParseProposal(
        exactWireBytes: ByteArray,
        connectionId: UUID,
        pathEpoch: Long,
        expectedDirection: Direction,
    ): ProductionDirectCutoverProposal? {
        return try {
            validateOwner(connectionId, pathEpoch, expectedDirection)
            if (exactWireBytes.size !in 1..MAXIMUM_PROPOSAL_BYTES) return null
            val ownedWire = exactWireBytes.copyOf()
            val message = try {
                DirectCutoverProposalV1.parseFrom(ownedWire)
            } catch (_: InvalidProtocolBufferException) {
                return null
            }

            if (
                message.barrierId.size() != UUID_BYTES || message.barrierId.toByteArray().isAllZero() ||
                message.directPathBinding.size() != DIRECT_PATH_BINDING_BYTES ||
                message.directPathBinding.toByteArray().isAllZero() ||
                message.sessionBarriersCount !in 1..MAXIMUM_SESSION_BARRIERS ||
                message.duplicateGraceMs !in 0..MAXIMUM_DUPLICATE_GRACE_MILLISECONDS
            ) {
                return null
            }

            val barriers = ArrayList<ProductionDirectSessionCutoverBarrier>(message.sessionBarriersCount)
            var previousSessionId: ByteArray? = null
            for (value in message.sessionBarriersList) {
                val sessionIdBytes = value.sessionId.toByteArray()
                if (sessionIdBytes.size != UUID_BYTES || sessionIdBytes.isAllZero()) return null
                val pcToAndroid = value.hasPcToAndroidFirstDirectSeq()
                val androidToPc = value.hasAndroidToPcFirstDirectSeq()
                val directionMatches = when (expectedDirection) {
                    Direction.PC_TO_ANDROID -> pcToAndroid && !androidToPc
                    Direction.ANDROID_TO_PC -> androidToPc && !pcToAndroid
                    else -> false
                }
                if (!directionMatches) return null
                val firstSequence = if (pcToAndroid) {
                    value.pcToAndroidFirstDirectSeq
                } else {
                    value.androidToPcFirstDirectSeq
                }
                if (firstSequence !in 1..Long.MAX_VALUE) return null
                if (previousSessionId != null && compareUnsigned(previousSessionId, sessionIdBytes) >= 0) {
                    return null
                }
                previousSessionId = sessionIdBytes
                barriers += ProductionDirectSessionCutoverBarrier(
                    sessionId = uuidFromNetworkBytes(sessionIdBytes),
                    firstDirectSequence = firstSequence,
                )
            }

            val barrierId = uuidFromNetworkBytes(message.barrierId.toByteArray())
            val ownedBinding = message.directPathBinding.toByteArray()
            val canonical = buildMessage(
                direction = expectedDirection,
                barrierId = barrierId,
                directPathBinding = ownedBinding,
                sessionBarriers = barriers,
                duplicateGraceMilliseconds = message.duplicateGraceMs,
            ).toByteArray()
            if (!ownedWire.contentEquals(canonical)) return null
            result(
                connectionId = connectionId,
                pathEpoch = pathEpoch,
                direction = expectedDirection,
                barrierId = barrierId,
                directPathBinding = ownedBinding,
                sessionBarriers = barriers,
                duplicateGraceMilliseconds = message.duplicateGraceMs,
                wire = canonical,
            )
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    fun computeBarrierSha256(
        connectionId: UUID,
        pathEpoch: Long,
        deterministicProposal: ByteArray,
    ): ByteArray {
        require(connectionId != zeroUuid) { "A non-empty Direct connection ID is required." }
        require(pathEpoch > 0) { "The Direct path epoch must be positive." }
        require(deterministicProposal.size in 1..MAXIMUM_PROPOSAL_BYTES) {
            "The deterministic cutover proposal size is invalid."
        }
        val digestInput = ByteArray(
            digestDomain.size + UUID_BYTES + Long.SIZE_BYTES + deterministicProposal.size,
        )
        try {
            var offset = 0
            digestDomain.copyInto(digestInput, offset)
            offset += digestDomain.size
            uuidNetworkBytes(connectionId).copyInto(digestInput, offset)
            offset += UUID_BYTES
            ByteBuffer.wrap(digestInput, offset, Long.SIZE_BYTES)
                .order(ByteOrder.BIG_ENDIAN)
                .putLong(pathEpoch)
            offset += Long.SIZE_BYTES
            deterministicProposal.copyInto(digestInput, offset)
            return MessageDigest.getInstance(SHA_256).digest(digestInput)
        } finally {
            digestInput.fill(0)
        }
    }

    private fun result(
        connectionId: UUID,
        pathEpoch: Long,
        direction: Direction,
        barrierId: UUID,
        directPathBinding: ByteArray,
        sessionBarriers: List<ProductionDirectSessionCutoverBarrier>,
        duplicateGraceMilliseconds: Int,
        wire: ByteArray,
    ): ProductionDirectCutoverProposal = ProductionDirectCutoverProposal.owned(
        barrierId = barrierId,
        direction = direction,
        directPathBinding = directPathBinding,
        barrierSha256 = computeBarrierSha256(connectionId, pathEpoch, wire),
        duplicateGraceMilliseconds = duplicateGraceMilliseconds,
        sessionBarriers = sessionBarriers,
        wireBytes = wire,
    )

    private fun buildMessage(
        direction: Direction,
        barrierId: UUID,
        directPathBinding: ByteArray,
        sessionBarriers: List<ProductionDirectSessionCutoverBarrier>,
        duplicateGraceMilliseconds: Int,
    ): DirectCutoverProposalV1 {
        val result = DirectCutoverProposalV1.newBuilder()
            .setBarrierId(ByteString.copyFrom(uuidNetworkBytes(barrierId)))
            .setDirectPathBinding(ByteString.copyFrom(directPathBinding))
            .setDuplicateGraceMs(duplicateGraceMilliseconds)
        for (value in sessionBarriers) {
            val barrier = SessionCutoverBarrierV1.newBuilder()
                .setSessionId(ByteString.copyFrom(uuidNetworkBytes(value.sessionId)))
            when (direction) {
                Direction.PC_TO_ANDROID -> barrier.pcToAndroidFirstDirectSeq = value.firstDirectSequence
                Direction.ANDROID_TO_PC -> barrier.androidToPcFirstDirectSeq = value.firstDirectSequence
                else -> throw IllegalArgumentException("The Direct cutover direction is invalid.")
            }
            result.addSessionBarriers(barrier)
        }
        return result.build()
    }

    private fun canonicalizeBarriers(
        values: Iterable<ProductionDirectSessionCutoverBarrier>,
    ): List<ProductionDirectSessionCutoverBarrier> {
        val result = values.map { it.copy() }.sortedWith { left, right ->
            compareUnsigned(uuidNetworkBytes(left.sessionId), uuidNetworkBytes(right.sessionId))
        }
        require(result.size in 1..MAXIMUM_SESSION_BARRIERS) {
            "The cutover proposal must contain between 1 and 32 session barriers."
        }
        require(result.all { it.sessionId != zeroUuid && it.firstDirectSequence in 1..Long.MAX_VALUE }) {
            "A cutover session barrier is invalid."
        }
        require(result.zipWithNext().none { (left, right) -> left.sessionId == right.sessionId }) {
            "A cutover proposal cannot contain duplicate session IDs."
        }
        return result
    }

    private fun validateOwner(connectionId: UUID, pathEpoch: Long, direction: Direction) {
        require(connectionId != zeroUuid) { "A non-empty Direct connection ID is required." }
        require(pathEpoch > 0) { "The Direct path epoch must be positive." }
        require(direction == Direction.PC_TO_ANDROID || direction == Direction.ANDROID_TO_PC) {
            "The Direct cutover direction is invalid."
        }
    }

    private fun compareUnsigned(left: ByteArray, right: ByteArray): Int {
        for (index in 0 until minOf(left.size, right.size)) {
            val compared = (left[index].toInt() and 0xff).compareTo(right[index].toInt() and 0xff)
            if (compared != 0) return compared
        }
        return left.size.compareTo(right.size)
    }

    private fun ByteArray.isAllZero(): Boolean {
        var aggregate = 0
        for (value in this) aggregate = aggregate or value.toInt()
        return aggregate == 0
    }

    private fun uuidNetworkBytes(value: UUID): ByteArray = ByteBuffer.allocate(UUID_BYTES)
        .order(ByteOrder.BIG_ENDIAN)
        .putLong(value.mostSignificantBits)
        .putLong(value.leastSignificantBits)
        .array()

    private fun uuidFromNetworkBytes(value: ByteArray): UUID {
        require(value.size == UUID_BYTES) { "A Direct UUID must contain 16 bytes." }
        val input = ByteBuffer.wrap(value).order(ByteOrder.BIG_ENDIAN)
        return UUID(input.long, input.long)
    }

    private const val UUID_BYTES = 16
    private const val DIRECT_PATH_BINDING_BYTES = 32
    private const val SHA_256 = "SHA-256"
}

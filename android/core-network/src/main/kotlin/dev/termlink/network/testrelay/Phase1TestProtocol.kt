package dev.termlink.network.testrelay

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.UUID

internal const val PHASE1_TEST_HEADER_SIZE = 24
internal const val PHASE1_TEST_MAX_FRAME_SIZE = 1 shl 20

internal object Phase1FrameType {
    const val RESUME = 1
    const val CUMULATIVE_ACK = 2
    const val INPUT = 3
    const val RESIZE_REQUEST = 4
    const val LEASE_REQUEST = 5
    const val LEASE_RELEASE = 6
    const val WELCOME = 129
    const val OUTPUT = 130
    const val RESIZE = 131
    const val LIFECYCLE = 132
    const val GAP = 133
    const val INPUT_ACCEPTED = 134
    const val LEASE_GRANTED = 135
    const val LEASE_REVOKED = 136
    const val FLOW_CONTROL = 137
    const val ERROR = 138
    const val TITLE = 139
}

internal object Phase1Lifecycle {
    const val RUNNING = 1
    const val INTERRUPTED = 2
    const val ENDED = 3
}

internal data class Phase1TestFrame(
    val type: Int,
    val sequence: Long = 0,
    val acknowledgement: Long = 0,
    val payload: ByteArray = byteArrayOf(),
)

internal object Phase1TestFrameCodec {
    private val magic = byteArrayOf('T'.code.toByte(), 'L'.code.toByte(), 'T'.code.toByte(), '1'.code.toByte())

    fun encode(frame: Phase1TestFrame): ByteArray {
        require(frame.type in 1..255) { "frame type is out of range" }
        require(frame.sequence >= 0) { "sequence must not be negative" }
        require(frame.acknowledgement >= 0) { "acknowledgement must not be negative" }
        require(frame.payload.size <= PHASE1_TEST_MAX_FRAME_SIZE - PHASE1_TEST_HEADER_SIZE) {
            "phase 1 test payload exceeds limit"
        }
        return ByteBuffer.allocate(PHASE1_TEST_HEADER_SIZE + frame.payload.size)
            .order(ByteOrder.BIG_ENDIAN)
            .put(magic)
            .put(1)
            .put(frame.type.toByte())
            .putShort(0)
            .putLong(frame.sequence)
            .putLong(frame.acknowledgement)
            .put(frame.payload)
            .array()
    }

    fun decode(encoded: ByteArray): Phase1TestFrame {
        require(encoded.size in PHASE1_TEST_HEADER_SIZE..PHASE1_TEST_MAX_FRAME_SIZE) {
            "invalid phase 1 test frame length"
        }
        val buffer = ByteBuffer.wrap(encoded).order(ByteOrder.BIG_ENDIAN)
        val actualMagic = ByteArray(magic.size).also(buffer::get)
        require(actualMagic.contentEquals(magic)) { "phase 1 test frame magic mismatch" }
        require(buffer.get().toInt() and 0xff == 1) { "unsupported phase 1 test protocol version" }
        val type = buffer.get().toInt() and 0xff
        require(buffer.short.toInt() == 0) { "phase 1 test flags must be zero" }
        val sequence = buffer.long
        val acknowledgement = buffer.long
        require(sequence >= 0 && acknowledgement >= 0) { "unsigned field exceeds supported range" }
        val payload = ByteArray(buffer.remaining()).also(buffer::get)
        return Phase1TestFrame(type, sequence, acknowledgement, payload)
    }
}

internal fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16)
    .order(ByteOrder.BIG_ENDIAN)
    .putLong(value.mostSignificantBits)
    .putLong(value.leastSignificantBits)
    .array()

internal fun decodeUuid(bytes: ByteArray): UUID {
    require(bytes.size == 16) { "UUID payload must contain 16 bytes" }
    val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
    return UUID(buffer.long, buffer.long)
}

internal fun uint16Pair(first: Int, second: Int): ByteArray {
    require(first in 1..0xffff && second in 1..0xffff) { "uint16 pair is out of range" }
    return ByteBuffer.allocate(4)
        .order(ByteOrder.BIG_ENDIAN)
        .putShort(first.toShort())
        .putShort(second.toShort())
        .array()
}

internal fun decodeUtf8(bytes: ByteArray): String = StandardCharsets.UTF_8.newDecoder()
    .onMalformedInput(CodingErrorAction.REPORT)
    .onUnmappableCharacter(CodingErrorAction.REPORT)
    .decode(ByteBuffer.wrap(bytes))
    .toString()

package dev.termlink.terminal.nativecore

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Collections

/** Path-free capture records owned by the decoder; close after replay to clear captured bytes. */
class NativeBrokerCapturedGroupV1 internal constructor(
    val sourceSequence: Long,
    captures: List<NativeBrokerCaptureV1>,
) : AutoCloseable {
    val captures: List<NativeBrokerCaptureV1> = Collections.unmodifiableList(ArrayList(captures))
    override fun close() { captures.forEach { it.close() } }
}

/** Canonical TLCAPV1 storage codec. Raw Output is stored by the referring event, never in this blob. */
object NativeBrokerCaptureCodecV1 {
    private val magic = byteArrayOf(84, 76, 67, 65, 80, 86, 49, 0) // TLCAPV1\0
    private const val HEADER_BYTES = 32
    private const val IDENTITY_BYTES = 96
    private const val RECORD_HEADER_BYTES = IDENTITY_BYTES + 8
    const val MAX_BYTES = 32 * 1024 * 1024

    fun encode(sourceSequence: Long, captures: List<NativeBrokerCaptureV1>): ByteArray {
        require(sourceSequence in 1 until Long.MAX_VALUE && captures.size in 1..64) { "Invalid broker capture group" }
        var total = HEADER_BYTES.toLong()
        captures.forEachIndexed { index, capture ->
            require(capture.sourceSequence == sourceSequence && capture.ordinal == index.toLong() &&
                capture.sourceOffsetEnd > 0) { "Broker capture order or identity mismatch" }
            val length = capture.byteCount
            require(length <= NativeBrokerCaptureV1.MAX_CAPTURE_BYTES &&
                (capture.outcome == NativeBrokerOutcomeV1.BYTES || length == 0)) { "Invalid broker capture result" }
            total += RECORD_HEADER_BYTES + length.toLong()
            require(total <= MAX_BYTES) { "Broker capture group exceeds 32 MiB" }
        }
        val encoded = ByteArray(total.toInt())
        try {
            val writer = ByteBuffer.wrap(encoded).order(ByteOrder.LITTLE_ENDIAN)
            writer.put(magic)
            writer.putInt(HEADER_BYTES)
            writer.putInt(captures.size)
            writer.putLong(sourceSequence)
            writer.putInt(encoded.size)
            writer.putInt(0)
            captures.forEachIndexed { index, capture ->
                val identity = capture.copyIdentity()
                try {
                    validateIdentity(identity, 0, sourceSequence, index)
                    val bytes = capture.copyBytes()
                    try {
                        writer.put(identity)
                        writer.putInt(capture.outcome.code)
                        writer.putInt(bytes.size)
                        writer.put(bytes)
                    } finally { bytes.fill(0) }
                } finally {
                    identity.fill(0)
                }
            }
            return encoded
        } catch (error: Throwable) {
            encoded.fill(0)
            throw error
        }
    }

    fun decode(encoded: ByteArray): NativeBrokerCapturedGroupV1 {
        require(encoded.size in HEADER_BYTES + RECORD_HEADER_BYTES..MAX_BYTES) { "Broker capture blob size is invalid" }
        val reader = ByteBuffer.wrap(encoded).order(ByteOrder.LITTLE_ENDIAN)
        require(encoded.copyOfRange(0, 8).contentEquals(magic) && reader.getInt(8) == HEADER_BYTES &&
            reader.getInt(28) == 0 &&
            (reader.getInt(24).toLong() and 0xffff_ffffL) == encoded.size.toLong()) {
            "Broker capture header is malformed"
        }
        val count = reader.getInt(12)
        val sourceSequence = reader.getLong(16)
        require(count in 1..64 && sourceSequence in 1 until Long.MAX_VALUE) { "Invalid broker capture header values" }
        val offsets = IntArray(count)
        val lengths = IntArray(count)
        val outcomes = IntArray(count)
        var cursor = HEADER_BYTES.toLong()
        repeat(count) { index ->
            require(cursor + RECORD_HEADER_BYTES <= encoded.size) { "Truncated broker capture record" }
            val offset = cursor.toInt()
            validateIdentity(encoded, offset, sourceSequence, index)
            val resultCode = reader.getInt(offset + IDENTITY_BYTES)
            val length = reader.getInt(offset + IDENTITY_BYTES + 4).toLong() and 0xffff_ffffL
            require(resultCode in 0..4 && length <= NativeBrokerCaptureV1.MAX_CAPTURE_BYTES &&
                (resultCode == 0 || length == 0L)) { "Invalid broker capture result" }
            cursor += RECORD_HEADER_BYTES + length
            require(cursor <= encoded.size) { "Truncated broker capture bytes" }
            offsets[index] = offset
            lengths[index] = length.toInt()
            outcomes[index] = resultCode
        }
        require(cursor == encoded.size.toLong()) { "Trailing broker capture bytes" }

        val captures = ArrayList<NativeBrokerCaptureV1>(count)
        try {
            repeat(count) { index ->
                val identity = encoded.copyOfRange(offsets[index], offsets[index] + IDENTITY_BYTES)
                try {
                    val dataStart = offsets[index] + RECORD_HEADER_BYTES
                    val bytes = encoded.copyOfRange(dataStart, dataStart + lengths[index])
                    try {
                        captures += NativeBrokerCaptureV1(identity,
                            NativeBrokerOutcomeV1.entries[outcomes[index]], bytes)
                    } finally { bytes.fill(0) }
                } finally {
                    identity.fill(0)
                }
            }
            return NativeBrokerCapturedGroupV1(sourceSequence, captures)
        } catch (error: Throwable) {
            captures.forEach { it.close() }
            throw error
        }
    }

    private fun validateIdentity(bytes: ByteArray, offset: Int, sequence: Long, ordinal: Int) {
        val data = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        require(data.getInt(offset) == TERMLINK_TERMINAL_ABI_VERSION &&
            data.getInt(offset + 4) == IDENTITY_BYTES && data.getInt(offset + 8) == 1 &&
            data.getInt(offset + 12) in listOf('f'.code, 't'.code, 's'.code) &&
            data.getLong(offset + 16) == sequence && data.getInt(offset + 24) != 0 &&
            data.getInt(offset + 28) == ordinal && data.getInt(offset + 76) in 0..1 &&
            data.getLong(offset + 80) == 0L && data.getLong(offset + 88) == 0L) {
            "Malformed broker capture identity"
        }
    }
}

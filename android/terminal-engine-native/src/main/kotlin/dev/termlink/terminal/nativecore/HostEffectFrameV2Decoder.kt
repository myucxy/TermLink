package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalHostEffectFrameV2
import dev.termlink.terminal.api.TerminalHostEffectKindV2
import dev.termlink.terminal.api.TerminalHostEffectV2
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Deterministic rejection of a malformed or unsupported TermLink Host Effect Frame V2. */
class HostEffectFrameV2DecodeException internal constructor(message: String) :
    IllegalArgumentException("host effect frame v2: $message")

/** Strict, allocation-bounded decoder for canonical Host Effect Frame V2 bytes. */
object HostEffectFrameV2Decoder {
    fun decode(bytes: ByteArray): TerminalHostEffectFrameV2 {
        try {
            return Decoder(bytes).decode()
        } catch (error: HostEffectFrameV2DecodeException) {
            throw error
        } catch (error: ArithmeticException) {
            fail("integer overflow")
        } catch (error: IndexOutOfBoundsException) {
            fail("truncated data")
        } catch (error: IllegalArgumentException) {
            fail(error.message ?: "invalid value")
        }
    }

    private class Decoder(private val bytes: ByteArray) {
        private val reader = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)

        fun decode(): TerminalHostEffectFrameV2 {
            if (bytes.size < HEADER_SIZE) fail("header is truncated")
            if (bytes.size > TerminalHostEffectFrameV2.MAX_FRAME_BYTES) {
                fail("frame exceeds ${TerminalHostEffectFrameV2.MAX_FRAME_BYTES} bytes")
            }
            if (!regionEquals(bytes, 0, MAGIC)) fail("bad magic")

            expectU16(8, MAJOR, "major version")
            expectU16(10, MINOR, "minor version")
            expectU16(12, HEADER_SIZE, "header size")
            expectU16(14, RECORD_SIZE, "record size")
            val count = u32(16, "record count")
            if (count > TerminalHostEffectFrameV2.MAX_EFFECTS.toLong()) {
                fail("record count exceeds ${TerminalHostEffectFrameV2.MAX_EFFECTS}")
            }
            val totalLength = u32(20, "total length")
            if (totalLength != bytes.size.toLong()) fail("total length does not match input")
            expectU64(24, HEADER_SIZE.toLong(), "records offset")
            val recordsBytes = multiply(count, RECORD_SIZE.toLong(), "record bytes")
            val expectedPayloadOffset = add(HEADER_SIZE.toLong(), recordsBytes, "payload offset")
            val payloadOffset = u64(32, "payload offset")
            if (payloadOffset != expectedPayloadOffset) fail("payload offset is not canonical")
            if (payloadOffset > bytes.size.toLong()) fail("records are truncated")
            val aggregatePayloadBytes = bytes.size.toLong() - payloadOffset
            if (aggregatePayloadBytes > TerminalHostEffectFrameV2.MAX_PAYLOAD_BYTES) {
                fail("aggregate payload exceeds ${TerminalHostEffectFrameV2.MAX_PAYLOAD_BYTES} bytes")
            }
            expectU64(40, REQUIRED_FEATURES, "required features")
            expectU64(48, 0, "header reserved field")
            val payloadCrc = u32(56, "payload CRC32C")
            val headerCrc = u32(60, "header CRC32C")
            if (crc32c(0, HEADER_CRC_END) != headerCrc) fail("header CRC32C mismatch")
            if (crc32c(HEADER_SIZE, bytes.size) != payloadCrc) fail("payload CRC32C mismatch")

            val effectCount = checkedInt(count, "record count")
            val effects = ArrayList<TerminalHostEffectV2>(effectCount)
            var expectedEffectPayloadOffset = payloadOffset
            var previousSequence: Long? = null
            var previousSourceOffset = 0L
            var previousOrdinal = 0L

            repeat(effectCount) { index ->
                val recordOffset = checkedInt(
                    add(
                        HEADER_SIZE.toLong(),
                        multiply(index.toLong(), RECORD_SIZE.toLong(), "record offset"),
                        "record offset",
                    ),
                    "record offset",
                )
                expectU32(recordOffset, KIND_PTY_REPLY, "effect kind")
                expectU32(recordOffset + 4, 0, "effect flags")
                val sequence = u64(recordOffset + 8, "source sequence")
                val sourceOffset = u32(recordOffset + 16, "source offset end")
                if (sourceOffset !in 1..MAX_SOURCE_OFFSET) fail("source offset end is out of range")
                val ordinal = u32(recordOffset + 20, "effect ordinal")
                validateOrder(
                    sequence,
                    sourceOffset,
                    ordinal,
                    previousSequence,
                    previousSourceOffset,
                    previousOrdinal,
                )
                val effectPayloadOffset = u64(recordOffset + 24, "effect payload offset")
                if (effectPayloadOffset != expectedEffectPayloadOffset) {
                    fail("effect payloads are not contiguous")
                }
                val payloadLength = u32(recordOffset + 32, "effect payload length")
                if (payloadLength !in 1..TerminalHostEffectFrameV2.MAX_REPLY_BYTES.toLong()) {
                    fail("effect payload length is out of range")
                }
                expectU32(recordOffset + 36, 0, "effect reserved field")
                val payloadEnd = add(effectPayloadOffset, payloadLength, "effect payload end")
                if (payloadEnd > bytes.size.toLong()) fail("effect payload is truncated")
                val start = checkedInt(effectPayloadOffset, "effect payload offset")
                val end = checkedInt(payloadEnd, "effect payload end")
                effects += TerminalHostEffectV2(
                    kind = TerminalHostEffectKindV2.PTY_REPLY,
                    sourceSequence = sequence,
                    sourceOffsetEnd = checkedInt(sourceOffset, "source offset end"),
                    ordinal = ordinal,
                    payload = bytes.copyOfRange(start, end),
                )
                expectedEffectPayloadOffset = payloadEnd
                previousSequence = sequence
                previousSourceOffset = sourceOffset
                previousOrdinal = ordinal
            }
            if (expectedEffectPayloadOffset != bytes.size.toLong()) {
                fail("frame has trailing or missing payload bytes")
            }
            return TerminalHostEffectFrameV2(bytes, effects)
        }

        private fun validateOrder(
            sequence: Long,
            sourceOffset: Long,
            ordinal: Long,
            previousSequence: Long?,
            previousSourceOffset: Long,
            previousOrdinal: Long,
        ) {
            if (previousSequence == null) {
                if (ordinal != 0L) fail("first source key does not start at ordinal zero")
                return
            }
            when {
                sequence > previousSequence ||
                    (sequence == previousSequence && sourceOffset > previousSourceOffset) -> {
                    if (ordinal != 0L) fail("new source key does not start at ordinal zero")
                }
                sequence == previousSequence && sourceOffset == previousSourceOffset -> {
                    val expected = add(previousOrdinal, 1, "effect ordinal")
                    if (ordinal != expected) fail("same-key ordinals are not contiguous")
                }
                else -> fail("effect source keys regress")
            }
        }

        private fun crc32c(start: Int, end: Int): Long {
            var crc = -1
            for (index in start until end) {
                crc = crc xor (bytes[index].toInt() and 0xff)
                repeat(8) {
                    crc = (crc ushr 1) xor (CRC32C_POLYNOMIAL and -(crc and 1))
                }
            }
            return crc.inv().toLong() and MAX_U32
        }

        private fun u16(offset: Int): Int = reader.getShort(offset).toInt() and 0xffff

        private fun u32(offset: Int, @Suppress("UNUSED_PARAMETER") field: String): Long =
            reader.getInt(offset).toLong() and MAX_U32

        private fun u64(offset: Int, field: String): Long {
            val value = reader.getLong(offset)
            if (value < 0) fail("$field exceeds the signed host range")
            return value
        }

        private fun expectU16(offset: Int, expected: Int, field: String) {
            if (u16(offset) != expected) fail("unsupported $field")
        }

        private fun expectU32(offset: Int, expected: Long, field: String) {
            if (u32(offset, field) != expected) fail("unsupported $field")
        }

        private fun expectU64(offset: Int, expected: Long, field: String) {
            if (u64(offset, field) != expected) fail("unsupported $field")
        }
    }

    private fun regionEquals(bytes: ByteArray, offset: Int, expected: ByteArray): Boolean {
        if (offset < 0 || offset > bytes.size - expected.size) return false
        return expected.indices.all { bytes[offset + it] == expected[it] }
    }

    private fun add(left: Long, right: Long, field: String): Long = try {
        Math.addExact(left, right)
    } catch (_: ArithmeticException) {
        throw HostEffectFrameV2DecodeException("$field overflows")
    }

    private fun multiply(left: Long, right: Long, field: String): Long = try {
        Math.multiplyExact(left, right)
    } catch (_: ArithmeticException) {
        throw HostEffectFrameV2DecodeException("$field overflows")
    }

    private fun checkedInt(value: Long, field: String): Int {
        if (value !in 0..Int.MAX_VALUE.toLong()) fail("$field exceeds the host range")
        return value.toInt()
    }

    private fun fail(message: String): Nothing = throw HostEffectFrameV2DecodeException(message)

    private const val HEADER_SIZE = 64
    private const val RECORD_SIZE = 40
    private const val HEADER_CRC_END = 60
    private const val MAJOR = 2
    private const val MINOR = 0
    private const val REQUIRED_FEATURES = 1L
    private const val KIND_PTY_REPLY = 1L
    private const val MAX_SOURCE_OFFSET = 1024 * 1024L
    private const val MAX_U32 = 0xffff_ffffL
    // Reflected Castagnoli polynomial 0x82F63B78 represented as signed Int.
    private const val CRC32C_POLYNOMIAL = -2_097_792_136

    private val MAGIC = byteArrayOf(
        'T'.code.toByte(), 'L'.code.toByte(), 'H'.code.toByte(), 'E'.code.toByte(),
        'F'.code.toByte(), '2'.code.toByte(), 0, 0,
    )
}

package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalHostEffectFrameV1
import dev.termlink.terminal.api.TerminalHostEffectKindV1
import dev.termlink.terminal.api.TerminalHostEffectV1
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Deterministic rejection of a malformed or unsupported TermLink Host Effect Frame V1. */
class HostEffectFrameV1DecodeException internal constructor(message: String) :
    IllegalArgumentException("host effect frame v1: $message")

/** Strict, allocation-bounded decoder for the canonical Host Effect Frame V1 wire format. */
object HostEffectFrameV1Decoder {
    fun decode(bytes: ByteArray): TerminalHostEffectFrameV1 {
        try {
            return Decoder(bytes).decode()
        } catch (error: HostEffectFrameV1DecodeException) {
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

        fun decode(): TerminalHostEffectFrameV1 {
            if (bytes.size < HEADER_SIZE) fail("header is truncated")
            if (bytes.size > MAX_FRAME_BYTES) fail("frame exceeds 65,710,862 bytes")
            if (!bytes.regionMatchesHostEffect(0, MAGIC, 0, MAGIC.size)) fail("bad magic")

            expectU16(8, MAJOR, "major version")
            expectU16(10, MINOR, "minor version")
            expectU16(12, HEADER_SIZE, "header size")
            expectU16(14, RECORD_SIZE, "record size")
            val count = u32(16, "record count")
            val totalLength = u32(20, "total length")
            if (totalLength != bytes.size.toLong()) fail("total length does not match input")
            expectU64(24, HEADER_SIZE.toLong(), "records offset")
            val recordsBytes = multiply(count, RECORD_SIZE.toLong(), "record bytes")
            val expectedPayloadOffset = add(HEADER_SIZE.toLong(), recordsBytes, "payload offset")
            val payloadOffset = u64(32, "payload offset")
            if (payloadOffset != expectedPayloadOffset) fail("payload offset is not canonical")
            if (payloadOffset > bytes.size.toLong()) fail("records are truncated")
            expectU64(40, REQUIRED_FEATURES, "required features")
            expectU64(48, 0, "header reserved field")
            val payloadCrc = u32(56, "payload CRC32C")
            val headerCrc = u32(60, "header CRC32C")
            if (crc32c(0, HEADER_CRC_END) != headerCrc) fail("header CRC32C mismatch")
            if (crc32c(HEADER_SIZE, bytes.size) != payloadCrc) fail("payload CRC32C mismatch")

            val effectCount = checkedInt(count, "record count")
            val effects = ArrayList<TerminalHostEffectV1>(effectCount)
            var expectedEffectPayloadOffset = payloadOffset
            var previousSequence: Long? = null
            var previousSourceOffset = 0L

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
                if (sourceOffset !in 1..MAX_SOURCE_OFFSET) {
                    fail("source offset end is out of range")
                }
                expectU32(recordOffset + 20, 0, "effect ordinal")
                validateOrder(
                    sequence = sequence,
                    sourceOffset = sourceOffset,
                    previousSequence = previousSequence,
                    previousSourceOffset = previousSourceOffset,
                )
                val effectPayloadOffset = u64(recordOffset + 24, "effect payload offset")
                if (effectPayloadOffset != expectedEffectPayloadOffset) {
                    fail("effect payloads are not contiguous")
                }
                val payloadLength = u32(recordOffset + 32, "effect payload length")
                if (payloadLength == 0L) fail("effect payload is empty")
                expectU32(recordOffset + 36, 0, "effect reserved field")
                val payloadEnd = add(effectPayloadOffset, payloadLength, "effect payload end")
                if (payloadEnd > bytes.size.toLong()) fail("effect payload is truncated")
                val start = checkedInt(effectPayloadOffset, "effect payload offset")
                val end = checkedInt(payloadEnd, "effect payload end")
                if (!isCanonicalPtyReply(start, end)) fail("PTY reply payload is not canonical")
                effects += TerminalHostEffectV1(
                    kind = TerminalHostEffectKindV1.PTY_REPLY,
                    sourceSequence = sequence,
                    sourceOffsetEnd = sourceOffset.toInt(),
                    ordinal = 0,
                    payload = bytes.copyOfRange(start, end),
                )
                expectedEffectPayloadOffset = payloadEnd
                previousSequence = sequence
                previousSourceOffset = sourceOffset
            }
            if (expectedEffectPayloadOffset != bytes.size.toLong()) {
                fail("frame has trailing or missing payload bytes")
            }
            return TerminalHostEffectFrameV1(effects)
        }

        private fun validateOrder(
            sequence: Long,
            sourceOffset: Long,
            previousSequence: Long?,
            previousSourceOffset: Long,
        ) {
            if (previousSequence == null) {
                return
            }
            if (
                sequence < previousSequence ||
                (sequence == previousSequence && sourceOffset <= previousSourceOffset)
            ) {
                fail("effect records are not strictly ordered")
            }
        }

        private fun isCanonicalPtyReply(start: Int, end: Int): Boolean {
            val length = end - start
            if (
                length !in 4..MAX_REPLY_BYTES ||
                bytes[start] != 0x1b.toByte() ||
                bytes[start + 1] != '['.code.toByte()
            ) {
                return false
            }
            if (
                matches(start, end, REPLY_PRIMARY_DA) ||
                matches(start, end, REPLY_SECONDARY_DA) ||
                matches(start, end, REPLY_DSR_OK) ||
                matches(start, end, REPLY_PRIVATE_DSR_OK)
            ) {
                return true
            }
            if (bytes[end - 1] != 'R'.code.toByte()) return false
            var coordinateStart = start + 2
            if (bytes[coordinateStart] == '?'.code.toByte()) coordinateStart++
            var semicolon = -1
            for (index in coordinateStart until end - 1) {
                if (bytes[index] == ';'.code.toByte()) {
                    if (semicolon != -1) return false
                    semicolon = index
                }
            }
            if (semicolon == -1) return false
            return isCanonicalCoordinate(coordinateStart, semicolon) &&
                isCanonicalCoordinate(semicolon + 1, end - 1)
        }

        private fun matches(start: Int, end: Int, expected: ByteArray): Boolean {
            if (end - start != expected.size) return false
            repeat(expected.size) { index ->
                if (bytes[start + index] != expected[index]) return false
            }
            return true
        }

        private fun isCanonicalCoordinate(start: Int, end: Int): Boolean {
            if (start >= end || bytes[start] == '0'.code.toByte()) return false
            var value = 0
            for (index in start until end) {
                val byte = bytes[index].toInt() and 0xff
                if (byte !in '0'.code..'9'.code) return false
                value = value * 10 + byte - '0'.code
                if (value > MAX_COORDINATE) return false
            }
            return value in 1..MAX_COORDINATE
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

        private fun u32(offset: Int, field: String): Long =
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

    private fun ByteArray.regionMatchesHostEffect(
        thisOffset: Int,
        other: ByteArray,
        otherOffset: Int,
        length: Int,
    ): Boolean {
        if (thisOffset < 0 || otherOffset < 0 || length < 0) return false
        if (thisOffset > size - length || otherOffset > other.size - length) return false
        repeat(length) { index ->
            if (this[thisOffset + index] != other[otherOffset + index]) return false
        }
        return true
    }

    private fun add(left: Long, right: Long, field: String): Long = try {
        Math.addExact(left, right)
    } catch (error: ArithmeticException) {
        throw HostEffectFrameV1DecodeException("$field overflows")
    }

    private fun multiply(left: Long, right: Long, field: String): Long = try {
        Math.multiplyExact(left, right)
    } catch (error: ArithmeticException) {
        throw HostEffectFrameV1DecodeException("$field overflows")
    }

    private fun checkedInt(value: Long, field: String): Int {
        if (value !in 0..Int.MAX_VALUE.toLong()) fail("$field exceeds the host range")
        return value.toInt()
    }

    private fun fail(message: String): Nothing = throw HostEffectFrameV1DecodeException(message)

    private const val HEADER_SIZE = 64
    private const val RECORD_SIZE = 40
    private const val HEADER_CRC_END = 60
    private const val MAX_FRAME_BYTES = 65_710_862
    private const val MAJOR = 1
    private const val MINOR = 0
    private const val REQUIRED_FEATURES = 1L
    private const val KIND_PTY_REPLY = 1L
    private const val MAX_SOURCE_OFFSET = 1024 * 1024L
    private const val MAX_REPLY_BYTES = 11
    private const val MAX_COORDINATE = 512
    private const val MAX_U32 = 0xffff_ffffL
    // Reflected Castagnoli polynomial 0x82F63B78 represented as signed Int.
    private const val CRC32C_POLYNOMIAL = -2_097_792_136

    private val MAGIC = byteArrayOf(
        'T'.code.toByte(), 'L'.code.toByte(), 'H'.code.toByte(), 'E'.code.toByte(),
        'F'.code.toByte(), '1'.code.toByte(), 0, 0,
    )
    private val REPLY_PRIMARY_DA = "\u001b[?1;2c".toByteArray(Charsets.US_ASCII)
    private val REPLY_SECONDARY_DA = "\u001b[>0;0;0c".toByteArray(Charsets.US_ASCII)
    private val REPLY_DSR_OK = "\u001b[0n".toByteArray(Charsets.US_ASCII)
    private val REPLY_PRIVATE_DSR_OK = "\u001b[?0n".toByteArray(Charsets.US_ASCII)
}

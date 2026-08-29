package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderCursorV2
import dev.termlink.terminal.api.TerminalRenderFrameV2
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** A deterministic rejection of a malformed or unsupported TermLink Render Frame V2 payload. */
class RenderFrameV2DecodeException internal constructor(message: String) :
    IllegalArgumentException("render frame v2: $message")

/**
 * Strict, allocation-bounded decoder for the canonical TermLink Render Frame V2 wire format.
 *
 * V2 changes only the header. After validating that header and both original CRC domains, the
 * decoder normalizes a private copy to a V1 header and delegates all six unchanged payload
 * sections to [RenderFrameV1Decoder]. This keeps V1 behavior frozen while sharing every strict
 * payload-layout, table-ordering, reference, UTF-8, and wide-cell invariant.
 */
object RenderFrameV2Decoder {
    fun decode(bytes: ByteArray): TerminalRenderFrameV2 {
        try {
            return decodeStrict(bytes)
        } catch (error: RenderFrameV2DecodeException) {
            throw error
        } catch (error: ArithmeticException) {
            fail("integer overflow")
        } catch (error: IndexOutOfBoundsException) {
            fail("truncated data")
        } catch (error: IllegalArgumentException) {
            fail(error.message ?: "invalid value")
        }
    }

    private fun decodeStrict(bytes: ByteArray): TerminalRenderFrameV2 {
        if (bytes.size < HEADER_SIZE) fail("header is truncated")
        if (bytes.size > MAX_FRAME_BYTES) fail("frame exceeds 16 MiB")
        if (!bytes.regionMatchesV2(0, MAGIC_V2, 0, MAGIC_V2.size)) fail("bad magic")

        val reader = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        expectU16(reader, 8, MAJOR_V2, "major version")
        expectU16(reader, 10, MINOR_V2, "minor version")
        expectU16(reader, 12, HEADER_SIZE, "header size")
        expectU16(reader, 14, DIRECTORY_ENTRY_SIZE, "directory entry size")
        expectU16(reader, 16, SECTION_COUNT, "section count")

        val shapeValue = u8(reader, CURSOR_SHAPE_OFFSET)
        val shape = when (shapeValue) {
            0 -> TerminalRenderCursorShapeV2.BLOCK
            1 -> TerminalRenderCursorShapeV2.UNDERLINE
            2 -> TerminalRenderCursorShapeV2.BAR
            else -> fail("cursor shape is invalid")
        }
        val blinking = when (u8(reader, CURSOR_BLINKING_OFFSET)) {
            0 -> false
            1 -> true
            else -> fail("cursor blinking flag is invalid")
        }
        expectU16(reader, CURSOR_RESERVED_OFFSET, 0, "cursor reserved field")
        expectU64(reader, REQUIRED_FEATURES_OFFSET, REQUIRED_FEATURES_V2, "required features")

        val payloadCrc = u32(reader, PAYLOAD_CRC_OFFSET)
        val headerCrc = u32(reader, HEADER_CRC_OFFSET)
        if (crc32c(bytes, 0, HEADER_CRC_OFFSET) != headerCrc) {
            fail("header CRC32C mismatch")
        }
        if (crc32c(bytes, HEADER_SIZE, bytes.size) != payloadCrc) {
            fail("payload CRC32C mismatch")
        }

        val normalized = bytes.copyOf()
        val normalizedHeader = ByteBuffer.wrap(normalized).order(ByteOrder.LITTLE_ENDIAN)
        MAGIC_V1.copyInto(normalized, 0)
        normalizedHeader.putShort(8, MAJOR_V1.toShort())
        normalizedHeader.putInt(CURSOR_SHAPE_OFFSET, 0)
        normalizedHeader.putLong(REQUIRED_FEATURES_OFFSET, REQUIRED_FEATURES_V1)
        normalizedHeader.putInt(
            HEADER_CRC_OFFSET,
            crc32c(normalized, 0, HEADER_CRC_OFFSET).toInt(),
        )

        val v1 = try {
            RenderFrameV1Decoder.decode(normalized)
        } catch (error: RenderFrameV1DecodeException) {
            fail(error.message ?: "invalid payload")
        }
        return TerminalRenderFrameV2(
            presentationRevision = v1.presentationRevision,
            nextSourceSequence = v1.nextSourceSequence,
            size = v1.size,
            activeScreen = v1.activeScreen,
            reverseVideo = v1.reverseVideo,
            cursor = TerminalRenderCursorV2(
                row = v1.cursor.row,
                column = v1.cursor.column,
                visible = v1.cursor.visible,
                wrapPending = v1.cursor.wrapPending,
                shape = shape,
                blinking = blinking,
            ),
            graphemes = v1.graphemes,
            renditions = v1.renditions,
            palette = v1.palette,
            hyperlinks = v1.hyperlinks,
            rows = v1.rows,
            title = v1.title,
        )
    }

    private fun u8(reader: ByteBuffer, offset: Int): Int =
        reader.get(offset).toInt() and 0xff

    private fun u16(reader: ByteBuffer, offset: Int): Int =
        reader.getShort(offset).toInt() and 0xffff

    private fun u32(reader: ByteBuffer, offset: Int): Long =
        reader.getInt(offset).toLong() and 0xffff_ffffL

    private fun u64(reader: ByteBuffer, offset: Int): Long {
        val value = reader.getLong(offset)
        if (value < 0) fail("required features exceeds the signed host range")
        return value
    }

    private fun expectU16(reader: ByteBuffer, offset: Int, expected: Int, field: String) {
        if (u16(reader, offset) != expected) fail("unsupported $field")
    }

    private fun expectU64(reader: ByteBuffer, offset: Int, expected: Long, field: String) {
        if (u64(reader, offset) != expected) fail("unsupported $field")
    }

    private fun crc32c(bytes: ByteArray, start: Int, end: Int): Long {
        var crc = -1
        for (index in start until end) {
            crc = crc xor (bytes[index].toInt() and 0xff)
            repeat(8) {
                crc = (crc ushr 1) xor (CRC32C_POLYNOMIAL and -(crc and 1))
            }
        }
        return crc.inv().toLong() and 0xffff_ffffL
    }

    private fun ByteArray.regionMatchesV2(
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

    private fun fail(message: String): Nothing = throw RenderFrameV2DecodeException(message)

    private const val HEADER_SIZE = 96
    private const val DIRECTORY_ENTRY_SIZE = 24
    private const val SECTION_COUNT = 6
    private const val MAX_FRAME_BYTES = 16 * 1024 * 1024
    private const val MAJOR_V1 = 1
    private const val MAJOR_V2 = 2
    private const val MINOR_V2 = 0
    private const val CURSOR_SHAPE_OFFSET = 68
    private const val CURSOR_BLINKING_OFFSET = 69
    private const val CURSOR_RESERVED_OFFSET = 70
    private const val REQUIRED_FEATURES_OFFSET = 72
    private const val PAYLOAD_CRC_OFFSET = 88
    private const val HEADER_CRC_OFFSET = 92
    private const val REQUIRED_FEATURES_V1 = 0x1fL
    private const val REQUIRED_FEATURES_V2 = 0x3fL
    // Reflected Castagnoli polynomial 0x82F63B78 represented as signed Int.
    private const val CRC32C_POLYNOMIAL = -2_097_792_136

    private val MAGIC_V1 = byteArrayOf(
        'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
        'R'.code.toByte(), 'M'.code.toByte(), '1'.code.toByte(), 0,
    )
    private val MAGIC_V2 = byteArrayOf(
        'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
        'R'.code.toByte(), 'M'.code.toByte(), '2'.code.toByte(), 0,
    )
}

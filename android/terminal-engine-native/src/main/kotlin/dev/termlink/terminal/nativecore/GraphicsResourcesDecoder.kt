package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalGraphicsResource
import dev.termlink.terminal.api.TerminalGraphicsResources
import java.nio.ByteBuffer
import java.nio.ByteOrder

class GraphicsResourcesDecodeException internal constructor(message: String) :
    IllegalArgumentException("graphics resources: $message")

/** Strict TLGRS1 decoder. Input arrays must not be modified concurrently with decoding. */
object GraphicsResourcesDecoder {
    fun decode(bytes: ByteArray): TerminalGraphicsResources = inspect(bytes).materialize()

    /** Borrows the input so an enclosing frame can validate references before copying any pixels. */
    internal fun inspect(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset): ResourceFrame {
        if (offset < 0 || length < HEADER_BYTES || offset.toLong() + length > bytes.size ||
            length > TerminalGraphicsResources.MAX_FRAME_BYTES) fail("frame length is out of range")
        val reader = ByteBuffer.wrap(bytes, offset, length).slice().order(ByteOrder.LITTLE_ENDIAN)
        if (MAGIC.indices.any { reader[it] != MAGIC[it] } ||
            reader.getShort(8).toInt() != 1 || reader.getShort(10).toInt() != HEADER_BYTES) {
            fail("unsupported header")
        }
        fun u32(at: Int): Long = reader.getInt(at).toLong() and 0xffff_ffffL
        val nextId = reader.getLong(16)
        val count = u32(24)
        val declaredBytes = reader.getLong(32)
        val frameCrc = reader.getInt(40)
        if (u32(12) != length.toLong() || nextId == 0L || count > TerminalGraphicsResources.MAX_RESOURCES ||
            reader.getInt(28) != 0 || reader.getInt(44) != 0 ||
            declaredBytes !in 0L..TerminalGraphicsResources.MAX_PIXEL_BYTES.toLong()) {
            fail("invalid header values")
        }
        if (frameCrc != checksum(bytes, offset, offset + 40, offset + 44, offset + length)) {
            fail("frame CRC32C mismatch")
        }
        var cursor = HEADER_BYTES
        var previousId = 0L
        var total = 0L
        val entries = ArrayList<Entry>(count.toInt())
        repeat(count.toInt()) {
            if (length - cursor < ENTRY_BYTES) fail("resource header is truncated")
            val id = reader.getLong(cursor)
            val width = u32(cursor + 8)
            val height = u32(cursor + 12)
            val format = when (reader[cursor + 16].toInt()) {
                1 -> TerminalGraphicsFormat.RGBA8888
                2 -> TerminalGraphicsFormat.A8
                else -> fail("unknown pixel format")
            }
            val pixelLength = u32(cursor + 20)
            if (java.lang.Long.compareUnsigned(id, previousId) <= 0 ||
                java.lang.Long.compareUnsigned(id, nextId) >= 0) fail("invalid resource ID order or range")
            if ((17..19).any { reader[cursor + it].toInt() != 0 } || reader.getInt(cursor + 28) != 0) {
                fail("nonzero resource reserved field")
            }
            if (width !in 1L..TerminalGraphicsResource.MAX_DIMENSION.toLong() ||
                height !in 1L..TerminalGraphicsResource.MAX_DIMENSION.toLong()) fail("invalid image dimensions")
            // Bounds are checked before multiplication; even the maximum dimensions fit in Long.
            val expected = width * height * format.bytesPerPixel
            if (pixelLength != expected || expected > TerminalGraphicsResource.MAX_PIXEL_BYTES) {
                fail("pixel length does not match dimensions or exceeds the resource limit")
            }
            val start = cursor + ENTRY_BYTES
            val end = start.toLong() + pixelLength
            val padded = (end + 3) and -4L
            if (padded > length) fail("pixels or padding are truncated")
            for (at in end.toInt() until padded.toInt()) {
                if (reader[at].toInt() != 0) fail("nonzero pixel padding")
            }
            val pixelCrc = reader.getInt(cursor + 24)
            if (pixelCrc != checksum(bytes, offset + start, offset + end.toInt())) fail("pixel CRC32C mismatch")
            total += pixelLength
            if (total > TerminalGraphicsResources.MAX_PIXEL_BYTES) fail("session pixel limit exceeded")
            entries.add(Entry(id, Metadata(width.toInt(), height.toInt(), format), start, pixelLength.toInt(), pixelCrc))
            previousId = id
            cursor = padded.toInt()
        }
        if (cursor != length || total != declaredBytes) fail("trailing data or pixel total mismatch")
        return ResourceFrame(bytes, offset, length, nextId, total.toInt(), frameCrc, entries)
    }

    internal data class Metadata(val width: Int, val height: Int, val format: TerminalGraphicsFormat)

    internal data class Entry(val id: Long, val metadata: Metadata, val start: Int, val length: Int, val crc: Int)

    /** Thread-confined borrow: callers must keep the input unchanged until materialize returns. */
    internal class ResourceFrame internal constructor(
        private val bytes: ByteArray,
        private val offset: Int,
        private val length: Int,
        val nextId: Long,
        val pixelBytes: Int,
        private val frameCrc: Int,
        private val entries: List<Entry>,
    ) {
        fun ids(): List<Long> = entries.map { it.id }

        fun metadata(id: Long): Metadata? {
            val index = entries.binarySearch { java.lang.Long.compareUnsigned(it.id, id) }
            return if (index < 0) null else entries[index].metadata
        }

        fun materialize(): TerminalGraphicsResources {
            val current = inspect(bytes, offset, length)
            if (current.frameCrc != frameCrc || current.nextId != nextId ||
                current.pixelBytes != pixelBytes || current.entries != entries) fail("borrowed input changed")
            return TerminalGraphicsResources(nextId, entries.map { entry ->
                TerminalGraphicsResource(entry.id, entry.metadata.width, entry.metadata.height, entry.metadata.format,
                    ByteBuffer.wrap(bytes, offset + entry.start, entry.length))
            })
        }
    }

    // java.util.zip.CRC32C is unavailable on the minimum supported Android API level.
    private fun checksum(bytes: ByteArray, start: Int, end: Int, secondStart: Int = end, secondEnd: Int = end): Int {
        var crc = -1
        for (index in start until end) crc = (crc ushr 8) xor CRC_TABLE[(crc xor bytes[index].toInt()) and 255]
        for (index in secondStart until secondEnd) crc = (crc ushr 8) xor CRC_TABLE[(crc xor bytes[index].toInt()) and 255]
        return crc.inv()
    }

    private fun fail(message: String): Nothing = throw GraphicsResourcesDecodeException(message)

    private const val HEADER_BYTES = 48
    private const val ENTRY_BYTES = 32
    private val MAGIC = "TLGRS1\u0000\u0000".toByteArray(Charsets.US_ASCII)
    private val CRC_TABLE = IntArray(256) { initial ->
        var value = initial
        repeat(8) { value = (value ushr 1) xor (if (value and 1 != 0) 0x82f63b78.toInt() else 0) }
        value
    }
}

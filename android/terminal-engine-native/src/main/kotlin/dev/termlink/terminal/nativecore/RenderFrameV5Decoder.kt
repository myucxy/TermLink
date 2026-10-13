package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalGraphicsResources
import dev.termlink.terminal.api.TerminalRenderClipV5
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalRenderGlyphV5
import dev.termlink.terminal.api.TerminalRenderImageProtocolV5
import dev.termlink.terminal.api.TerminalRenderImageV5
import dev.termlink.terminal.api.TerminalRenderRatioV5
import dev.termlink.terminal.api.TerminalRenderSourceV5
import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction

class RenderFrameV5DecodeException internal constructor(message: String) :
    IllegalArgumentException("render frame v5: $message")

/** Development V5 only. The input must remain unchanged throughout this call. */
object RenderFrameV5Decoder {
    fun decode(bytes: ByteArray): TerminalRenderFrameV5 = try {
        decodeFrame(bytes)
    } catch (error: RenderFrameV5DecodeException) {
        throw error
    } catch (error: IllegalArgumentException) {
        fail(error.message ?: "invalid value")
    } catch (_: IndexOutOfBoundsException) {
        fail("truncated data")
    } catch (_: CharacterCodingException) {
        fail("invalid glyph UTF-8")
    }

    private fun decodeFrame(bytes: ByteArray): TerminalRenderFrameV5 {
        if (bytes.size !in HEADER..TerminalRenderFrameV5.MAX_FRAME_BYTES) fail("frame length is out of range")
        val reader = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        fun u32(at: Int): Long = reader.getInt(at).toLong() and 0xffff_ffffL
        if (MAGIC.indices.any { bytes[it] != MAGIC[it] } || reader.getShort(8).toInt() != 5 ||
            reader.getShort(10).toInt() != HEADER) fail("unsupported header")
        val columns = u32(24)
        val rows = u32(28)
        if (columns !in 1..512L || rows !in 1..512L) fail("viewport dimensions are out of range")
        val size = TerminalSize(columns.toInt(), rows.toInt())
        val textLength = u32(32)
        val resourceLength = u32(36)
        val imageCount = u32(40)
        val glyphCount = u32(44)
        if (textLength !in 240..MAX_TEXT_BYTES.toLong() || resourceLength !in 48..TerminalGraphicsResources.MAX_FRAME_BYTES.toLong() ||
            imageCount > TerminalRenderFrameV5.MAX_IMAGES || glyphCount > columns * rows || u32(12) != bytes.size.toLong() ||
            HEADER + textLength + resourceLength + imageCount * IMAGE_BYTES + glyphCount * GLYPH_BYTES != bytes.size.toLong() ||
            u32(48) != 7L || reader.getLong(56) != 0L) fail("invalid header values")
        if (reader.getInt(52) != checksum(bytes)) fail("CRC32C mismatch")
        val textEnd = HEADER + textLength.toInt()
        val resourceEnd = textEnd + resourceLength.toInt()
        if (TEXT_MAGIC.indices.any { bytes[HEADER + it] != TEXT_MAGIC[it] } ||
            reader.getLong(HEADER + 24) != reader.getLong(16) || reader.getLong(HEADER + 32) != reader.getLong(16) ||
            u32(HEADER + 40) != columns || u32(HEADER + 44) != rows) fail("embedded text version, revisions or dimensions disagree")
        val resources = GraphicsResourcesDecoder.inspect(bytes, textEnd, resourceLength.toInt())
        val images = ArrayList<TerminalRenderImageV5>(imageCount.toInt())
        val glyphs = ArrayList<TerminalRenderGlyphV5>(glyphCount.toInt())
        val referenced = HashSet<Long>()
        val placements = HashSet<Long>()
        var offset = resourceEnd
        repeat(imageCount.toInt()) {
            if (u32(offset + 124) != 0L) fail("nonzero image reserved field")
            fun ratio(index: Int) = TerminalRenderRatioV5(reader.getLong(offset + 48 + index * 16), reader.getLong(offset + 56 + index * 16))
            fun small(at: Int) = checkedInt(u32(offset + at))
            val image = TerminalRenderImageV5(
                placementId = reader.getLong(offset), resourceId = reader.getLong(offset + 8),
                column = reader.getInt(offset + 16), row = reader.getInt(offset + 20), z = reader.getInt(offset + 24), imageId = u32(offset + 28),
                source = TerminalRenderSourceV5(small(32), small(36), small(40), small(44)),
                x = ratio(0), y = ratio(1), width = ratio(2), height = ratio(3),
                allocatedColumns = small(112), allocatedRows = small(116),
                protocol = when (u32(offset + 120)) {
                    1L -> TerminalRenderImageProtocolV5.SIXEL
                    2L -> TerminalRenderImageProtocolV5.KITTY
                    else -> fail("unknown image protocol")
                },
                clip = TerminalRenderClipV5(small(128), small(132), small(136), small(140)),
            )
            if (images.lastOrNull()?.let { it >= image } == true || !placements.add(image.placementId)) fail("invalid image order or duplicate placement")
            if (!image.intersectsViewport(size)) fail("image clip misses viewport")
            val metadata = resources.metadata(image.resourceId) ?: fail("missing image resource")
            if (metadata.format != TerminalGraphicsFormat.RGBA8888 || !image.source.fits(metadata.width, metadata.height)) {
                fail("invalid image format or source bounds")
            }
            referenced.add(image.resourceId)
            images.add(image)
            offset += IMAGE_BYTES
        }
        repeat(glyphCount.toInt()) {
            if (u32(offset + 28) != 0L) fail("nonzero glyph reserved field")
            val glyph = TerminalRenderGlyphV5(checkedInt(u32(offset)), checkedInt(u32(offset + 4)), reader.getLong(offset + 8),
                checkedInt(u32(offset + 16)), checkedInt(u32(offset + 20)), checkedInt(u32(offset + 24)))
            if (glyphs.lastOrNull()?.let { it >= glyph } == true) fail("invalid glyph order or duplicate cell")
            val metadata = resources.metadata(glyph.resourceId) ?: fail("missing glyph resource")
            if (metadata.format != TerminalGraphicsFormat.A8 || glyph.x.toLong() + glyph.width > metadata.width ||
                glyph.height > metadata.height) fail("invalid glyph atlas format or bounds")
            preflightGlyph(reader, textLength.toInt(), size, glyph)
            referenced.add(glyph.resourceId)
            glyphs.add(glyph)
            offset += GLYPH_BYTES
        }
        if (resources.ids().any { it !in referenced }) fail("unreferenced graphics resource")
        // All draw references are checked before copying text, and full V4 validation precedes pixels.
        val text = RenderFrameV4Decoder.decode(bytes.copyOfRange(HEADER, textEnd))
        if (glyphs.any { !it.targetsSingleCell(text) }) fail("invalid glyph text cell")
        return TerminalRenderFrameV5(text, resources.materialize(), images, glyphs)
    }

    private fun preflightGlyph(reader: ByteBuffer, textLength: Int, size: TerminalSize, glyph: TerminalRenderGlyphV5) {
        if (glyph.column >= size.columns || glyph.row >= size.rows) fail("glyph cell is outside viewport")
        fun u32(at: Int): Long = reader.getInt(HEADER + at).toLong() and 0xffff_ffffL
        fun section(index: Int): Pair<Int, Int> {
            val record = 96 + index * 24
            val start = u32(record + 8)
            val length = u32(record + 12)
            if (start < 240 || start + length > textLength) fail("invalid embedded text section bounds")
            return start.toInt() to length.toInt()
        }
        val (rowStart, rowLength) = section(4)
        val expectedRows = 8L + size.rows * 8L + size.columns.toLong() * size.rows * 12
        if (rowLength.toLong() != expectedRows || u32(rowStart) != 8L || u32(rowStart + 4) != 12L) fail("invalid embedded rows")
        val flags = u32(rowStart + 8 + glyph.row * 8 + 4)
        if (flags > 7 || glyph.column >= if (flags ushr 1 == 0L) size.columns else maxOf(1, size.columns / 2)) {
            fail("glyph is outside effective row")
        }
        val cell = rowStart + 8 + size.rows * 8 + (glyph.row * size.columns + glyph.column) * 12
        val gid = u32(cell)
        val (graphemeStart, graphemeLength) = section(0)
        val count = u32(96 + 16)
        if (gid !in 1..count || count > size.columns.toLong() * size.rows || count * 8 > graphemeLength ||
            u32(cell + 8) and 1L != 0L) fail("glyph cell is not a narrow lead")
        val record = graphemeStart + (gid.toInt() - 1) * 8
        val start = u32(record)
        val length = reader.getShort(HEADER + record + 4).toInt() and 0xffff
        if (reader[HEADER + record + 6].toInt() != 1 || length !in 1..4 || start < count * 8 ||
            start + length > graphemeLength) fail("glyph grapheme is not a single narrow scalar")
        val input = reader.duplicate().apply {
            position(HEADER + graphemeStart + start.toInt())
            limit(position() + length)
        }
        val text = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(input).toString()
        if (text.codePointCount(0, text.length) != 1) fail("glyph cell contains combining characters")
    }

    private fun checkedInt(value: Long): Int {
        if (value > Int.MAX_VALUE) fail("field exceeds signed 32-bit bounds")
        return value.toInt()
    }

    private fun checksum(bytes: ByteArray): Int {
        var crc = -1
        for (index in bytes.indices) {
            if (index in 52..55) continue
            crc = (crc ushr 8) xor CRC_TABLE[(crc xor bytes[index].toInt()) and 255]
        }
        return crc.inv()
    }

    private fun fail(message: String): Nothing = throw RenderFrameV5DecodeException(message)

    private const val HEADER = 64
    private const val IMAGE_BYTES = 144
    private const val GLYPH_BYTES = 32
    private const val MAX_TEXT_BYTES = 32 * 1024 * 1024
    private val MAGIC = "TLRFRM5\u0000".toByteArray(Charsets.US_ASCII)
    private val TEXT_MAGIC = "TLRFRM4\u0000".toByteArray(Charsets.US_ASCII)
    private val CRC_TABLE = IntArray(256) { initial ->
        var value = initial
        repeat(8) { value = (value ushr 1) xor (if (value and 1 != 0) 0x82f63b78.toInt() else 0) }
        value
    }
}

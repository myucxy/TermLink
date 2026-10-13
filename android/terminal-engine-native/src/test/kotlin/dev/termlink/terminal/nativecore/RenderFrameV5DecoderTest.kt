package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalRenderImageProtocolV5
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class RenderFrameV5DecoderTest {
    @Test
    fun rustPublicApiFixturesMatchTheRecordedPixelAndGlyphOracles() {
        // Self-authored GPL-3.0-only vectors from core's examples/graphics_interop.rs.
        val path = System.getenv("TERMLINK_GRAPHICS_FIXTURES")
        org.junit.Assume.assumeTrue("TERMLINK_GRAPHICS_FIXTURES was not supplied", path != null)
        val directory = java.io.File(requireNotNull(path))
        val expected = mapOf("empty" to Triple(0, 0, 0), "kitty-crop-scrolled" to Triple(1, 0, 1),
            "sixel-region-clip" to Triple(1, 0, 1), "drcs-live" to Triple(1, 4, 3), "drcs-scrolled-live" to Triple(1, 0, 1),
            "drcs-history-frame-000" to Triple(1, 4, 3), "drcs-history-frame-001" to Triple(1, 0, 1), "drcs-frozen-viewport" to Triple(1, 4, 3))
        for ((name, counts) in expected) {
            val decoded = RenderFrameV5Decoder.decode(java.io.File(directory, "$name.v5.bin").readBytes())
            assertEquals(counts, Triple(decoded.images.size, decoded.glyphs.size, decoded.resources.entries.size), name)
        }
        val kitty = RenderFrameV5Decoder.decode(java.io.File(directory, "kitty-crop-scrolled.v5.bin").readBytes())
        assertEquals(-1, kitty.images.single().row)
        assertEquals(-7, kitty.images.single().z)
        assertEquals(1, kitty.images.single().source.x)
        assertEquals(5L, kitty.images.single().y.numerator)
        assertEquals(6L, kitty.images.single().y.denominator)
        assertContentEquals("ff0000ff00ff00ff0000ffffffffff80".chunked(2).map { it.toInt(16).toByte() }.toByteArray(),
            kitty.resources[1]!!.copyPixels())
        val sixel = RenderFrameV5Decoder.decode(java.io.File(directory, "sixel-region-clip.v5.bin").readBytes())
        assertEquals(-2, sixel.images.single().row)
        assertEquals(3, sixel.images.single().clip.top)
        assertEquals(5, sixel.images.single().clip.bottom)
        val drcs = RenderFrameV5Decoder.decode(java.io.File(directory, "drcs-live.v5.bin").readBytes())
        assertEquals(listOf(2L, 2L, 2L, 3L), drcs.glyphs.map { it.resourceId })
        for (glyph in drcs.glyphs) {
            val atlas = requireNotNull(drcs.resources[glyph.resourceId])
            val bytes = atlas.copyPixels()
            repeat(glyph.height) { row -> repeat(glyph.width) { column ->
                val filled = if (glyph.resourceId == 2L) row < 6 else row == 1
                assertEquals(if (filled) 255 else 0, bytes[row * atlas.width + glyph.x + column].toInt() and 255)
            } }
        }
    }

    @Test
    fun decodesImageAndGlyphLayersAndOwnsTheInputPixels() {
        val bytes = frame()
        val decoded = RenderFrameV5Decoder.decode(bytes)
        bytes.fill(0)
        assertEquals(5, decoded.formatVersion)
        assertEquals(7L, decoded.presentationRevision)
        assertEquals("A", decoded.text.graphemes.single().text)
        assertEquals(TerminalRenderImageProtocolV5.KITTY, decoded.images.single().protocol)
        assertEquals(2, decoded.images.single().clip.right)
        assertEquals(2L, decoded.glyphs.single().resourceId)
        assertEquals(TerminalGraphicsFormat.A8, decoded.resources[2]!!.format)
        assertContentEquals(byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8), decoded.resources[1]!!.copyPixels())
    }

    @Test
    fun acceptsSignedClippedOriginsSixelAndUnsignedPlacementOrder() {
        val negative = image().also { put32(it, 16, -1); put32(it, 20, -1); put32(it, 28, 0); put32(it, 120, 1) }
        assertEquals(-1, RenderFrameV5Decoder.decode(frame(images = listOf(negative))).images.single().column)
        val first = image().also { put64(it, 0, Long.MAX_VALUE) }
        val second = image().also { put64(it, 0, Long.MIN_VALUE) }
        assertEquals(listOf(Long.MAX_VALUE, Long.MIN_VALUE),
            RenderFrameV5Decoder.decode(frame(images = listOf(first, second))).images.map { it.placementId })
        rejects(frame(images = listOf(second, first)))
    }

    @Test
    fun acceptsLargeCanonicalRatiosWithCrossProductsBeyondSignedLong() {
        val denominator = 1L shl 32
        val image = image().also {
            put64(it, 48, 1); put64(it, 56, denominator)
            put64(it, 80, 4096 * denominator - 1); put64(it, 88, denominator); put32(it, 112, 4096)
        }
        assertEquals(denominator, RenderFrameV5Decoder.decode(frame(images = listOf(image))).images.single().width.denominator)
        put64(image, 80, 4096 * denominator + 1)
        rejects(frame(images = listOf(image)))
    }

    @Test
    fun rejectsUnknownHeadersOverflowingLengthsAndCounts() {
        for ((offset, value) in listOf(0 to 0L, 8 to 4L, 10 to 63L, 12 to 0xffff_ffffL,
            24 to 0L, 28 to 513L, 32 to 0xffff_ffffL, 36 to 0xffff_ffffL, 40 to 4097L, 44 to 3L, 48 to 1L, 56 to 1L)) {
            rejects(frame().also { put32(it, offset, value); seal(it) })
        }
        rejects(frame().also { put64(it, 16, 8); seal(it) })
    }

    @Test
    fun rejectsOuterNestedTextAndNestedResourceCrcFailures() {
        rejects(frame().also { it[it.lastIndex] = 1 })
        rejects(frame().also { it[64 + 92] = 0; seal(it) })
        rejects(frame().also { it[64 + get32(it, 32) + 40] = 0; seal(it) })
    }

    @Test
    fun rejectsEmbeddedRevisionDimensionAndVersionMismatch() {
        for ((offset, value) in listOf(6 to '3'.code.toLong(), 24 to 8L, 32 to 8L, 40 to 3L, 44 to 2L)) {
            val text = text().also { put32(it, offset, value); sealText(it) }
            rejects(frame(text = text))
        }
        assertFailsWith<RenderFrameV4DecodeException> { RenderFrameV4Decoder.decode(frame()) }
        assertFailsWith<RenderFrameV3DecodeException> { RenderFrameV3Decoder.decode(frame()) }
    }

    @Test
    fun rejectsImageReferenceFormatAndSourceFailures() {
        for ((offset, value) in listOf(0 to 0L, 8 to 99L, 8 to 2L, 32 to 2L, 36 to 1L,
            40 to 0L, 44 to 2L, 120 to 3L, 124 to 1L)) {
            rejects(frame(images = listOf(image().also { put32(it, offset, value) })))
        }
        rejects(frame(images = listOf(image().also { put32(it, 120, 1) })))
        rejects(frame(images = emptyList()))
        rejects(frame(glyphs = emptyList()))
    }

    @Test
    fun rejectsInvalidRatioBoundsCanonicalFormAndExtent() {
        for ((offset, value) in listOf(48 to -1L, 48 to ((1L shl 48) + 1), 56 to 0L, 56 to ((1L shl 32) + 1),
            56 to 2L, 80 to 0L, 80 to 3L, 96 to 0L)) {
            rejects(frame(images = listOf(image().also { put64(it, offset, value) })))
        }
        rejects(frame(images = listOf(image().also { put64(it, 80, 2); put64(it, 88, 2) })))
    }

    @Test
    fun rejectsEmptyOutsideAllocationAndInvisibleClips() {
        for ((offset, value) in listOf(16 to -4097L, 20 to 4097L, 16 to -2L, 20 to 1L,
            112 to 0L, 116 to 4097L, 128 to 2L, 132 to 2L, 136 to 3L, 140 to 3L, 128 to -1L)) {
            rejects(frame(images = listOf(image().also { put32(it, offset, value) })))
        }
        rejects(frame(images = listOf(image().also { put32(it, 16, -1); put32(it, 136, 1) })))
    }

    @Test
    fun rejectsDuplicatePlacementEvenAcrossOrderedZLayers() {
        rejects(frame(images = listOf(image(), image().also { put32(it, 24, 1) })))
        rejects(frame(images = listOf(image(), image())))
    }

    @Test
    fun rejectsGlyphReferenceBoundsReservedAndOrderingFailures() {
        for ((offset, value) in listOf(0 to 2L, 4 to 1L, 8 to 99L, 8 to 1L, 16 to 3L,
            20 to 0L, 20 to 4L, 24 to 3L, 28 to 1L, 0 to 0xffff_ffffL)) {
            rejects(frame(glyphs = listOf(glyph().also { put32(it, offset, value) })))
        }
        rejects(frame(glyphs = listOf(glyph(), glyph())))
        rejects(frame(glyphs = listOf(glyph().also { put32(it, 0, 1) }, glyph())))
        assertEquals(2, RenderFrameV5Decoder.decode(frame(glyphs = listOf(glyph(), glyph().also { put32(it, 0, 1) }))).glyphs.size)
    }

    @Test
    fun glyphsRejectWideContinuationCombiningAndScaledPaddingCells() {
        rejects(frame(text = text(value = "界", width = 2)))
        rejects(frame(text = text(value = "界", width = 2), glyphs = listOf(glyph().also { put32(it, 0, 1) })))
        rejects(frame(text = text(value = "A\u0301")))
        rejects(frame(text = text(scaled = true), glyphs = listOf(glyph().also { put32(it, 0, 1) })))
        assertEquals(1, RenderFrameV5Decoder.decode(frame(text = text(scaled = true))).glyphs.size)
        assertEquals("\uD801\uDC00", RenderFrameV5Decoder.decode(frame(text = text(value = "\uD801\uDC00"))).text.graphemes.single().text)
    }

    @Test
    fun glyphPreflightBoundsMalformedV4DirectoryBeforeTextCopy() {
        for ((offset, value) in listOf(96 + 8 to 0xffff_ffffL, 96 + 12 to 0xffff_ffffL,
            96 + 16 to 0xffff_ffffL, 96 + 4 * 24 + 8 to 0xffff_ffffL)) {
            rejects(frame(text = text().also { put32(it, offset, value); sealText(it) }))
        }
    }

    @Test
    fun rejectsTruncationAndTrailingBytes() {
        val bytes = frame()
        for (length in listOf(0, 63, 64, 65, 64 + get32(bytes, 32), imageOffset(bytes), bytes.size - 1)) {
            val truncated = bytes.copyOf(length)
            if (length >= 64) { put32(truncated, 12, length.toLong()); seal(truncated) }
            rejects(truncated)
        }
        rejects(bytes.copyOf(bytes.size + 1).also { put32(it, 12, it.size.toLong()); seal(it) })
    }

    private fun rejects(bytes: ByteArray) {
        assertFailsWith<RenderFrameV5DecodeException> { RenderFrameV5Decoder.decode(bytes) }
    }

    private fun frame(text: ByteArray = text(), images: List<ByteArray> = listOf(image()), glyphs: List<ByteArray> = listOf(glyph())): ByteArray {
        val resources = resources()
        val bytes = ByteArray(64 + text.size + resources.size + images.size * 144 + glyphs.size * 32)
        "TLRFRM5\u0000".toByteArray().copyInto(bytes)
        put16(bytes, 8, 5); put16(bytes, 10, 64); put32(bytes, 12, bytes.size.toLong()); put64(bytes, 16, 7)
        put32(bytes, 24, 2); put32(bytes, 28, 1); put32(bytes, 32, text.size.toLong()); put32(bytes, 36, resources.size.toLong())
        put32(bytes, 40, images.size.toLong()); put32(bytes, 44, glyphs.size.toLong()); put32(bytes, 48, 7)
        text.copyInto(bytes, 64); resources.copyInto(bytes, 64 + text.size)
        var offset = 64 + text.size + resources.size
        for (draw in images + glyphs) { draw.copyInto(bytes, offset); offset += draw.size }
        seal(bytes)
        return bytes
    }

    private fun image() = ByteArray(144).also {
        put64(it, 0, 1); put64(it, 8, 1); put32(it, 28, 1)
        put32(it, 40, 1); put32(it, 44, 1)
        put64(it, 56, 1); put64(it, 72, 1); put64(it, 80, 1); put64(it, 88, 1); put64(it, 96, 1); put64(it, 104, 1)
        put32(it, 112, 2); put32(it, 116, 2); put32(it, 120, 2); put32(it, 136, 2); put32(it, 140, 2)
    }

    private fun glyph() = ByteArray(32).also { put64(it, 8, 2); put32(it, 20, 2); put32(it, 24, 2) }

    private fun resources(): ByteArray {
        val bytes = ByteArray(128)
        "TLGRS1\u0000\u0000".toByteArray().copyInto(bytes)
        put16(bytes, 8, 1); put16(bytes, 10, 48); put32(bytes, 12, bytes.size.toLong()); put64(bytes, 16, 3)
        put32(bytes, 24, 2); put64(bytes, 32, 14)
        put64(bytes, 48, 1); put32(bytes, 56, 2); put32(bytes, 60, 1); bytes[64] = 1; put32(bytes, 68, 8)
        byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8).copyInto(bytes, 80); put32(bytes, 72, crc(bytes, 80, 88))
        put64(bytes, 88, 2); put32(bytes, 96, 3); put32(bytes, 100, 2); bytes[104] = 2; put32(bytes, 108, 6)
        byteArrayOf(9, 10, 11, 12, 13, 14).copyInto(bytes, 120); put32(bytes, 112, crc(bytes, 120, 126))
        put32(bytes, 40, CRC32C().apply { update(bytes, 0, 40); update(bytes, 44, bytes.size - 44) }.value)
        return bytes
    }

    private fun text(value: String = "A", width: Int = 1, scaled: Boolean = false): ByteArray {
        val strings = if (scaled) listOf(" ", value) else listOf(value)
        val encoded = strings.map { it.toByteArray(Charsets.UTF_8) }
        val graphemes = ByteArray(strings.size * 8 + encoded.sumOf { it.size })
        var offset = strings.size * 8
        for ((index, data) in encoded.withIndex()) {
            put32(graphemes, index * 8, offset.toLong()); put16(graphemes, index * 8 + 4, data.size)
            graphemes[index * 8 + 6] = width.toByte(); data.copyInto(graphemes, offset); offset += data.size
        }
        val rows = ByteArray(40)
        put32(rows, 0, 8); put32(rows, 4, 12); put32(rows, 8, if (scaled) 1 else 2); put32(rows, 12, if (scaled) 2 else 0)
        put32(rows, 16, if (scaled) 2 else 1); put32(rows, 28, if (width == 2) 0 else 1); put32(rows, 36, if (width == 2) 1 else 0)
        val sections = listOf(graphemes, ByteArray(16), ByteArray(264 * 4) { 255.toByte() }, ByteArray(0), rows, ByteArray(0))
        val counts = listOf(strings.size, 1, 264, 0, 2, 0)
        val records = listOf(8, 16, 4, 16, 0, 0)
        offset = 240
        val offsets = sections.map { section -> offset.also { offset = (offset + section.size + 7) and -8 } }
        val bytes = ByteArray(offset)
        "TLRFRM4\u0000".toByteArray().copyInto(bytes)
        put16(bytes, 8, 4); put16(bytes, 12, 96); put16(bytes, 14, 24); put16(bytes, 16, 6)
        put32(bytes, 20, bytes.size.toLong()); put64(bytes, 24, 7); put64(bytes, 32, 7)
        put32(bytes, 40, 2); put32(bytes, 44, 1); put32(bytes, 56, 1); put32(bytes, 60, 2); put32(bytes, 64, 96); put64(bytes, 72, 0x7ff)
        for ((index, section) in sections.withIndex()) {
            val record = 96 + index * 24
            put32(bytes, record, index + 1L); put16(bytes, record + 4, if (index == 1) 2 else 1); put16(bytes, record + 6, 1)
            put32(bytes, record + 8, offsets[index].toLong()); put32(bytes, record + 12, section.size.toLong())
            put32(bytes, record + 16, counts[index].toLong()); put32(bytes, record + 20, records[index].toLong())
            section.copyInto(bytes, offsets[index])
        }
        sealText(bytes)
        return bytes
    }

    private fun imageOffset(bytes: ByteArray) = 64 + get32(bytes, 32) + get32(bytes, 36)
    private fun put16(bytes: ByteArray, offset: Int, value: Int) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort()) }
    private fun put32(bytes: ByteArray, offset: Int, value: Long) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value.toInt()) }
    private fun put32(bytes: ByteArray, offset: Int, value: Int) = put32(bytes, offset, value.toLong())
    private fun put64(bytes: ByteArray, offset: Int, value: Long) { ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value) }
    private fun get32(bytes: ByteArray, offset: Int) = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(offset)
    private fun crc(bytes: ByteArray, start: Int, end: Int): Long = CRC32C().apply { update(bytes, start, end - start) }.value
    private fun sealText(bytes: ByteArray) { put32(bytes, 88, crc(bytes, 96, bytes.size)); put32(bytes, 92, crc(bytes, 0, 92)) }
    private fun seal(bytes: ByteArray) { put32(bytes, 52, CRC32C().apply { update(bytes, 0, 52); update(bytes, 56, bytes.size - 56) }.value) }
}

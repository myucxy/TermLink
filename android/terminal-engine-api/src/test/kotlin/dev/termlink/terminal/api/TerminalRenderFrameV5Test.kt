package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

class TerminalRenderFrameV5Test {
    @Test
    fun immutableFrameRetainsTextAndSharesOwnedResources() {
        val resources = resources()
        val text = text()
        val images = mutableListOf(image())
        val glyphs = mutableListOf(glyph())
        val frame = TerminalRenderFrameV5(text, resources, images, glyphs)
        images.clear(); glyphs.clear()
        assertEquals(5, frame.formatVersion)
        assertEquals(7L, frame.presentationRevision)
        assertEquals(TerminalSize(2, 1), frame.size)
        assertSame(text, frame.text)
        val presented: TerminalTextFrame = frame
        assertEquals(text.rows, presented.rows)
        assertEquals(text.graphemes, presented.graphemes)
        assertEquals(text.nextSourceSequence, presented.nextSourceSequence)
        assertSame(resources[1], frame.resources[1])
        assertEquals(1, frame.images.size)
        assertEquals(1, frame.glyphs.size)
        assertFailsWith<UnsupportedOperationException> { (frame.images as MutableList).clear() }
        assertFailsWith<UnsupportedOperationException> { (frame.glyphs as MutableList).clear() }
    }

    @Test
    fun exactRatioArithmeticAcceptsLargeCrossProductsAndRejectsTinyOverflow() {
        val denominator = 1L shl 32
        val valid = image().copy(x = TerminalRenderRatioV5(1, denominator),
            width = TerminalRenderRatioV5(4096 * denominator - 1, denominator), allocatedColumns = 4096)
        assertEquals(4096, valid.allocatedColumns)
        assertFailsWith<IllegalArgumentException> {
            valid.copy(width = TerminalRenderRatioV5(4096 * denominator + 1, denominator))
        }
        for ((n, d) in listOf(-1L to 1L, 1L to 0L, 2L to 2L, 0L to 2L,
            ((1L shl 48) + 1) to 1L, 1L to ((1L shl 32) + 1))) {
            assertFailsWith<IllegalArgumentException> { TerminalRenderRatioV5(n, d) }
        }
    }

    @Test
    fun clipUsesTheSignedOriginAndMustIntersectTheViewport() {
        assertTrue(image().copy(column = -1, row = -1).intersectsViewport(TerminalSize(2, 1)))
        assertFalse(image().copy(column = -2).intersectsViewport(TerminalSize(2, 1)))
        assertFailsWith<IllegalArgumentException> { image().copy(column = -4097) }
        assertFailsWith<IllegalArgumentException> { TerminalRenderClipV5(1, 0, 1, 2) }
        assertFailsWith<IllegalArgumentException> { image().copy(clip = TerminalRenderClipV5(0, 0, 3, 2)) }
        assertFailsWith<IllegalArgumentException> {
            TerminalRenderFrameV5(text(), resources(), listOf(image().copy(row = -2)), listOf(glyph()))
        }
    }

    @Test
    fun rejectsMissingExtraAndWrongFormatReferences() {
        for ((images, glyphs) in listOf(
            emptyList<TerminalRenderImageV5>() to listOf(glyph()),
            listOf(image().copy(resourceId = 9)) to listOf(glyph()),
            listOf(image().copy(resourceId = 2)) to listOf(glyph()),
            listOf(image()) to listOf(glyph().copy(resourceId = 1)),
            listOf(image()) to listOf(glyph().copy(x = 1)),
        )) assertFailsWith<IllegalArgumentException> { TerminalRenderFrameV5(text(), resources(), images, glyphs) }
        assertFailsWith<IllegalArgumentException> {
            TerminalRenderFrameV5(text(), resources(), listOf(image().copy(source = TerminalRenderSourceV5(1, 0, 1, 1))), listOf(glyph()))
        }
    }

    @Test
    fun orderingIsStrictAndPlacementIdentityIsUniqueAcrossZLayers() {
        val low = image().copy(placementId = Long.MAX_VALUE)
        val high = image().copy(placementId = Long.MIN_VALUE)
        assertTrue(low < high)
        TerminalRenderFrameV5(text(), resources(), listOf(low, high), listOf(glyph()))
        for (images in listOf(listOf(high, low), listOf(image(), image().copy(z = 1)))) {
            assertFailsWith<IllegalArgumentException> { TerminalRenderFrameV5(text(), resources(), images, listOf(glyph())) }
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalRenderFrameV5(text(), resources(), listOf(image()), listOf(glyph(), glyph()))
        }
    }

    @Test
    fun glyphRequiresOneNarrowScalarAndAnEffectiveCell() {
        assertTrue(glyph().targetsSingleCell(text("\uD801\uDC00")))
        assertFalse(glyph().targetsSingleCell(text("A\u0301")))
        assertFalse(glyph().targetsSingleCell(text("界", 2)))
        assertFalse(glyph().copy(column = 1).targetsSingleCell(text(lineRendition = TerminalLineRendition.DOUBLE_WIDTH)))
        assertFailsWith<IllegalArgumentException> {
            TerminalRenderFrameV5(text("A\u0301"), resources(), listOf(image()), listOf(glyph()))
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalRenderFrameV5(text(nextSequence = 8), resources(), listOf(image()), listOf(glyph()))
        }
    }

    private fun image() = TerminalRenderImageV5(1, 1, 0, 0, 0, 1, TerminalRenderSourceV5(0, 0, 1, 1),
        TerminalRenderRatioV5(0, 1), TerminalRenderRatioV5(0, 1), TerminalRenderRatioV5(1, 1), TerminalRenderRatioV5(1, 1),
        2, 2, TerminalRenderImageProtocolV5.KITTY, TerminalRenderClipV5(0, 0, 2, 2))

    private fun glyph() = TerminalRenderGlyphV5(0, 0, 2, 0, 1, 1)

    private fun resources() = TerminalGraphicsResources(3, listOf(
        TerminalGraphicsResource(1, 1, 1, TerminalGraphicsFormat.RGBA8888, ByteArray(4)),
        TerminalGraphicsResource(2, 1, 1, TerminalGraphicsFormat.A8, byteArrayOf(127)),
    ))

    private fun text(value: String = "A", width: Int = 1, nextSequence: Long = 7,
        lineRendition: TerminalLineRendition = TerminalLineRendition.SINGLE_WIDTH) = TerminalRenderFrameV4(
        TerminalWidthProfile.UNICODE_16, 7, nextSequence, TerminalSize(2, 1), TerminalRenderScreenV1.PRIMARY, false,
        TerminalRenderCursorV2(0, 0, true, false, TerminalRenderCursorShapeV2.BLOCK, false),
        listOf(TerminalRenderGraphemeV1(value, width)),
        listOf(TerminalRenderRenditionV3(TerminalRenderColorV1.Default, TerminalRenderColorV1.Default, TerminalRenderColorV1.Default)),
        TerminalRenderPaletteV1(List(256) { null }, null, null, null), emptyList(),
        listOf(TerminalRenderRowV1(1, false, List(2) { TerminalRenderCellV1(1, 0, false, false, 0) }, lineRendition)), "",
    )
}

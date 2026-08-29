package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TerminalRenderFrameV3Test {
    @Test
    fun carriesV3RenditionsWhileReusingImmutableEarlierPayloadDtos() {
        val sourceGraphemes = mutableListOf(TerminalRenderGraphemeV1("A", 1))
        val sourceRenditions = mutableListOf(
            TerminalRenderRenditionV3(
                foreground = TerminalRenderColorV1.Rgb(0x112233),
                background = TerminalRenderColorV1.Indexed(7),
                underlineColor = TerminalRenderColorV1.Rgb(0x445566),
                underlineStyle = TerminalRenderUnderlineStyleV3.CURLY,
                faint = true,
                overline = true,
            ),
        )
        val frame = TerminalRenderFrameV3(
            presentationRevision = 4,
            nextSourceSequence = 9,
            size = TerminalSize(columns = 1, rows = 1),
            activeScreen = TerminalRenderScreenV1.PRIMARY,
            reverseVideo = false,
            cursor = TerminalRenderCursorV2(
                row = 0,
                column = 0,
                visible = true,
                wrapPending = false,
                shape = TerminalRenderCursorShapeV2.BAR,
                blinking = true,
            ),
            graphemes = sourceGraphemes,
            renditions = sourceRenditions,
            palette = emptyPalette(),
            hyperlinks = emptyList(),
            rows = listOf(
                TerminalRenderRowV1(
                    meaningfulEnd = 1,
                    softWrapped = false,
                    cells = listOf(TerminalRenderCellV1(1, 0, false, false, 0)),
                ),
            ),
            title = "frame v3",
        )

        sourceGraphemes.clear()
        sourceRenditions.clear()

        assertEquals(3, frame.formatVersion)
        assertEquals(TerminalRenderCursorShapeV2.BAR, frame.cursor.shape)
        assertEquals(TerminalRenderUnderlineStyleV3.CURLY, frame.renditions.single().underlineStyle)
        assertTrue(frame.renditions.single().underline)
        assertTrue(frame.renditions.single().faint)
        assertTrue(frame.renditions.single().overline)
        assertEquals(TerminalRenderGraphemeV1("A", 1), frame.graphemes.single())
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.renditions as MutableList<TerminalRenderRenditionV3>).clear()
        }
    }

    @Test
    fun noneStyleHasNoUnderline() {
        val rendition = TerminalRenderRenditionV3(
            foreground = TerminalRenderColorV1.Default,
            background = TerminalRenderColorV1.Default,
            underlineColor = TerminalRenderColorV1.Default,
        )

        assertFalse(rendition.underline)
        assertEquals(6, TerminalRenderUnderlineStyleV3.entries.size)
    }

    @Test
    fun rejectsCursorAndRowGeometryOutsideTheFrame() {
        assertFailsWith<IllegalArgumentException> {
            frame(
                cursor = TerminalRenderCursorV2(
                    1,
                    0,
                    true,
                    false,
                    TerminalRenderCursorShapeV2.BLOCK,
                    false,
                ),
            )
        }
        assertFailsWith<IllegalArgumentException> { frame(rows = emptyList()) }
    }

    private fun frame(
        cursor: TerminalRenderCursorV2 = TerminalRenderCursorV2(
            0,
            0,
            true,
            false,
            TerminalRenderCursorShapeV2.BLOCK,
            false,
        ),
        rows: List<TerminalRenderRowV1> = listOf(
            TerminalRenderRowV1(
                1,
                false,
                listOf(TerminalRenderCellV1(1, 0, false, false, 0)),
            ),
        ),
    ) = TerminalRenderFrameV3(
        presentationRevision = 0,
        nextSourceSequence = 0,
        size = TerminalSize(1, 1),
        activeScreen = TerminalRenderScreenV1.PRIMARY,
        reverseVideo = false,
        cursor = cursor,
        graphemes = listOf(TerminalRenderGraphemeV1("A", 1)),
        renditions = listOf(
            TerminalRenderRenditionV3(
                TerminalRenderColorV1.Default,
                TerminalRenderColorV1.Default,
                TerminalRenderColorV1.Default,
            ),
        ),
        palette = emptyPalette(),
        hyperlinks = emptyList(),
        rows = rows,
        title = "",
    )

    private fun emptyPalette() = TerminalRenderPaletteV1(
        colorsRgb = List(256) { null },
        defaultForegroundRgb = null,
        defaultBackgroundRgb = null,
        defaultCursorRgb = null,
    )
}

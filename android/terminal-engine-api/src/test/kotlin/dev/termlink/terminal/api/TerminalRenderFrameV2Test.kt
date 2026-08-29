package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class TerminalRenderFrameV2Test {
    @Test
    fun carriesV2CursorStateWhileReusingImmutableV1PayloadDtos() {
        val cells = listOf(TerminalRenderCellV1(1, 0, false, false, 0))
        val frame = TerminalRenderFrameV2(
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
            graphemes = listOf(TerminalRenderGraphemeV1("A", 1)),
            renditions = listOf(
                TerminalRenderRenditionV1(
                    TerminalRenderColorV1.Default,
                    TerminalRenderColorV1.Default,
                ),
            ),
            palette = emptyPalette(),
            hyperlinks = emptyList(),
            rows = listOf(TerminalRenderRowV1(1, false, cells)),
            title = "frame v2",
        )

        assertEquals(2, frame.formatVersion)
        assertEquals(TerminalRenderCursorShapeV2.BAR, frame.cursor.shape)
        assertEquals(TerminalRenderGraphemeV1("A", 1), frame.graphemes.single())
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.graphemes as MutableList<TerminalRenderGraphemeV1>).clear()
        }
    }

    @Test
    fun rejectsCursorAndRowGeometryOutsideTheFrame() {
        assertFailsWith<IllegalArgumentException> {
            frame(cursor = TerminalRenderCursorV2(1, 0, true, false, TerminalRenderCursorShapeV2.BLOCK, false))
        }
        assertFailsWith<IllegalArgumentException> {
            frame(rows = emptyList())
        }
    }

    private fun frame(
        cursor: TerminalRenderCursorV2 =
            TerminalRenderCursorV2(0, 0, true, false, TerminalRenderCursorShapeV2.BLOCK, false),
        rows: List<TerminalRenderRowV1> = listOf(
            TerminalRenderRowV1(1, false, listOf(TerminalRenderCellV1(1, 0, false, false, 0))),
        ),
    ) = TerminalRenderFrameV2(
        presentationRevision = 0,
        nextSourceSequence = 0,
        size = TerminalSize(1, 1),
        activeScreen = TerminalRenderScreenV1.PRIMARY,
        reverseVideo = false,
        cursor = cursor,
        graphemes = listOf(TerminalRenderGraphemeV1("A", 1)),
        renditions = listOf(
            TerminalRenderRenditionV1(TerminalRenderColorV1.Default, TerminalRenderColorV1.Default),
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

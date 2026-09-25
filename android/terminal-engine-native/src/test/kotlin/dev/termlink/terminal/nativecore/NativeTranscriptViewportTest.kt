package dev.termlink.terminal.nativecore

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.view.View
import android.view.inputmethod.EditorInfo
import androidx.test.core.app.ApplicationProvider
import dev.termlink.terminal.api.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class NativeTranscriptViewportTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val theme = NativeTerminalEngine.DEFAULT_THEME

    @Test
    fun fontsSpacingAndWideCellsPreserveHistoryAndPaintContent() {
        for (font in listOf(14f, 23f)) for (spacing in listOf(0.8f, 1f, 1.4f)) {
            val native = NativeTranscriptViewport()
            try {
                native.applyTheme(theme)
                native.replacePages(listOf(page(101L..140L)), 48)
                val view = native.createView(context, font, spacing, {}, {}) as NativeTerminalView
                layout(view, 640, 240)
                val state = native.debugState()
                assertEquals(40, state.rowCount)
                assertEquals(48, state.columns)
                assertEquals(140L, state.firstVisibleLineId!! + state.visibleText.lines().size - 1)
                assertTrue(state.visibleText.endsWith("中e\u0301"))
                assertTrue(native.scrollToLineId(120))
                assertTrue(native.debugState().visibleText.startsWith("L120 Aa0+-_= 中e\u0301"))
                val pixels = IntArray(640 * 240)
                render(view).getPixels(pixels, 0, 640, 0, 0, 640, 240)
                assertTrue("Styled history must paint visible glyphs", pixels.toSet().size > 8)
                assertNull(view.onCreateInputConnection(EditorInfo()))
            } finally { native.close() }
        }
    }

    @Test
    fun narrowTinyAndShortHistoryStayBoundedAndReadOnly() {
        for ((width, height) in listOf(1 to 320, 20 to 1, 1200 to 600, 240 to 120)) {
            for (count in listOf(4L, 30L)) {
                val native = NativeTranscriptViewport(initialColumns = 240, initialRows = 4)
                try {
                    native.applyTheme(theme)
                    native.replacePages(listOf(page(101L until 101L + count)), 240)
                    val view = native.createView(context, 30f, 1f, {}, {}) as NativeTerminalView
                    layout(view, width, height)
                    assertEquals(240, native.debugState().columns)
                    assertTrue(native.debugState().firstVisibleLineId in 101L until 101L + count)
                    assertTrue(native.debugState().appliedTextSizePixels >= 1f)
                    assertTrue(view.transcriptVisibleRows() >= 1)
                    assertEquals(width, render(view).width)
                    assertFalse(view.onCheckIsTextEditor())
                    assertNull(view.onCreateInputConnection(EditorInfo()))
                } finally { native.close() }
            }
        }
    }

    @Test
    fun prependResizeRemountAndReadOnlyBoundary() {
        val viewport = NativeTranscriptViewport()
        val newest = page(101L..140L)
        viewport.replacePages(listOf(newest), 48)
        var requests = 0
        val view = viewport.createView(context, onScrollPastTop = { requests++ }, onScrollPastBottom = {}) as NativeTerminalView
        layout(view, 240, 120)
        assertFalse(view.onCheckIsTextEditor())
        assertNull(view.onCreateInputConnection(EditorInfo()))
        view.positionViewportForTest(3, 0f)
        assertTrue(viewport.scrollToLineId(110))
        assertEquals(3f, view.transcriptPresentation().leftColumn)
        val saved = viewport.debugState().firstVisibleLineId
        viewport.replacePages(listOf(page(81L..100L), newest), 48)
        assertEquals(saved, viewport.debugState().firstVisibleLineId)
        assertEquals(3f, view.transcriptPresentation().leftColumn)
        layout(view, 300, 160)
        assertEquals(saved, viewport.debugState().firstVisibleLineId)
        val replacement = viewport.createView(context, onScrollPastTop = {}, onScrollPastBottom = {})
        layout(replacement, 300, 160)
        assertEquals(saved, viewport.debugState().firstVisibleLineId)
        assertEquals(0, requests)
        assertFalse(viewport.scrollToLineId(80))
        viewport.clear()
        viewport.replacePages(listOf(newest), 48)
        assertTrue("Reopening starts at the history tail", requireNotNull(viewport.debugState().firstVisibleLineId) > 110L)
        viewport.scrollToLineId(110)
        viewport.replacePages(listOf(newest.copy(snapshotAnchorSequence = 101)), 48)
        assertTrue("A new anchor does not reuse a stale viewport", requireNotNull(viewport.debugState().firstVisibleLineId) > 110L)
        assertFalse(TerminalEngine::class.java.isAssignableFrom(NativeTranscriptViewport::class.java))
        viewport.close()
    }

    @Test
    fun invalidPagesFailAtomicallyAndCapacityRemainsBounded() {
        val viewport = NativeTranscriptViewport()
        val five = (0..4).map { page((it * 200L + 1)..((it + 1) * 200L)) }
        viewport.replacePages(five)
        assertEquals(1_000, viewport.debugState().rowCount)
        val invalid = listOf(
            five + page(1_001L..1_002L),
            listOf(page(1L..2L), page(2L..3L)),
            listOf(page(1L..2L).copy(palette = themePalette().copy(colorsRgb = emptyList()))),
            listOf(page(1L..2L).let { p -> p.copy(rows = p.rows.map { it.copy(sealedAtSequence = 101) }) }),
        )
        for (pages in invalid) {
            assertThrows(IllegalArgumentException::class.java) { viewport.replacePages(pages) }
            assertEquals(1_000, viewport.debugState().rowCount)
        }
        viewport.close()
    }

    @Test
    fun hostGraphemeWidthsArePreservedAndMalformedContinuationsRejected() {
        val emoji = "👩‍💻"
        val row = TerminalRowSnapshotV1(
            listOf(TerminalCellSnapshotV1(emoji, 2, 0), TerminalCellSnapshotV1("", 0, 0, true)),
            lineId = 1, sealedAtSequence = 1,
        )
        val page = page(1L..1L).copy(columns = 2, rows = listOf(row))
        val projection = NativeTranscriptMapper.map(listOf(page), 2)
        assertEquals(emoji, projection.frame.graphemes.last().text)
        assertEquals(2, projection.frame.graphemes.last().displayWidth)
        assertThrows(IllegalArgumentException::class.java) {
            NativeTranscriptMapper.map(listOf(page.copy(rows = listOf(row.copy(cells = row.cells.take(1))))), 2)
        }
    }

    private fun layout(view: View, width: Int, height: Int) {
        view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY))
        view.layout(0, 0, width, height)
    }

    private fun render(view: View): Bitmap = Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888).also {
        val canvas = Canvas(it)
        canvas.drawColor(theme.background.value)
        view.draw(canvas)
    }

    private fun page(ids: LongRange): TerminalTranscriptPageV1 {
        val styles = listOf(
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Default, TerminalColorSnapshotV1.Default),
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Palette(1), TerminalColorSnapshotV1.Palette(4), attributes = TerminalCellAttributesSnapshotV1(bold = true)),
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Rgb(0xffcc33), TerminalColorSnapshotV1.Default, attributes = TerminalCellAttributesSnapshotV1(italic = true)),
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Default, TerminalColorSnapshotV1.Default, attributes = TerminalCellAttributesSnapshotV1(underline = true), underlineStyle = TerminalUnderlineStyleSnapshotV1.SINGLE),
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Default, TerminalColorSnapshotV1.Default, attributes = TerminalCellAttributesSnapshotV1(dim = true, strikethrough = true)),
            TerminalStyleSnapshotV1(TerminalColorSnapshotV1.Palette(2), TerminalColorSnapshotV1.Default, attributes = TerminalCellAttributesSnapshotV1(blink = true, inverse = true)),
        )
        val rows = ids.map { id ->
            val style = (id % styles.size).toInt()
            val cells = "L$id Aa0+-_= ".map { TerminalCellSnapshotV1(it.toString(), 1, style) }.toMutableList()
            cells += TerminalCellSnapshotV1("中", 2, style)
            cells += TerminalCellSnapshotV1("", 0, style, true)
            cells += TerminalCellSnapshotV1("e\u0301", 1, style)
            while (cells.size < 48) cells += TerminalCellSnapshotV1(" ", 1, style)
            TerminalRowSnapshotV1(cells, lineId = id, sealedAtSequence = 90)
        }
        return TerminalTranscriptPageV1(snapshotAnchorSequence = 100, firstLineId = ids.first, lastLineId = ids.last,
            hasMoreBefore = true, nextBeforeLineId = ids.first, columns = 48, rows = rows, styleTable = styles, palette = themePalette())
    }

    private fun themePalette() = TerminalPaletteSnapshotV1(List(256) { it * 0x010101 }, 0xffffff, 0, 0xffffff)
}

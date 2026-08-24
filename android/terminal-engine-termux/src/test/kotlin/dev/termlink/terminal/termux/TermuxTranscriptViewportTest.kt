package dev.termlink.terminal.termux

import android.content.Context
import android.view.View
import androidx.test.core.app.ApplicationProvider
import com.termux.view.TerminalView
import dev.termlink.terminal.api.TerminalCellAttributesSnapshotV1
import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalUnderlineStyleSnapshotV1
import java.lang.ref.WeakReference
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class TermuxTranscriptViewportTest {
    @Test
    fun viewportAcceptsOnlyProjectionPagesAndExposesNoFeedBoundary() {
        assertFalse(TerminalEngine::class.java.isAssignableFrom(TermuxTranscriptViewport::class.java))
        assertFalse(TermuxTranscriptViewport::class.java.methods.any { it.name == "feed" })

        val viewport = TermuxTranscriptViewport(initialRows = 4)
        viewport.replacePages(listOf(page(101L..104L, hasMoreBefore = false)))

        val debug = viewport.debugState()
        assertEquals(44L, debug.snapshotAnchorSequence)
        assertEquals(101L, debug.firstVisibleLineId)
        assertEquals(1, debug.pageCount)
        assertEquals(4, debug.rowCount)
        assertContains(debug.visibleText, "L101")
        assertContains(debug.visibleText, "L104")
        viewport.close()
    }

    @Test
    fun prependKeepsTheSameFirstVisibleLineId() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val viewport = TermuxTranscriptViewport(initialRows = 4)
        val newest = page(101L..130L, hasMoreBefore = true)
        viewport.replacePages(listOf(newest))
        val view = viewport.createView(context, onScrollPastTop = {}, onScrollPastBottom = {}) as TerminalView
        view.replaceEmulatorFromSession()
        view.setTopRow(-10)
        val anchor = view.topVisibleLineId
        assertFalse(anchor == 0L)

        viewport.replacePages(
            listOf(
                page(81L..100L, hasMoreBefore = false),
                newest,
            ),
        )

        assertEquals(anchor, view.topVisibleLineId)
        assertEquals(2, viewport.debugState().pageCount)
        viewport.close()
    }

    @Test
    fun overlappingOrOutOfOrderPagesAreRejected() {
        val viewport = TermuxTranscriptViewport(initialRows = 4)
        assertFailsWith<IllegalArgumentException> {
            viewport.replacePages(
                listOf(
                    page(101L..110L, hasMoreBefore = false),
                    page(110L..120L, hasMoreBefore = true),
                ),
            )
        }
        viewport.close()
    }

    @Test
    fun rendererAcceptsExactlyFiveFullPagesAndRejectsUnboundedAccumulation() {
        val viewport = TermuxTranscriptViewport(initialRows = 20)
        val fivePages = (0 until 5).map { index ->
            page(
                lineIds = (index * 200L + 1L)..((index + 1L) * 200L),
                hasMoreBefore = index > 0,
            )
        }
        viewport.replacePages(fivePages)
        assertEquals(TermuxTranscriptViewport.MAX_RENDERED_ROWS, viewport.debugState().rowCount)

        assertFailsWith<IllegalArgumentException> {
            viewport.replacePages(
                fivePages + page(1001L..1200L, hasMoreBefore = true),
            )
        }
        assertEquals(TermuxTranscriptViewport.MAX_RENDERED_ROWS, viewport.debugState().rowCount)
        viewport.close()
    }

    @Test
    fun authoritativeWidePcGridIsFittedWithoutBeingResizedByThePhone() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val viewport = TermuxTranscriptViewport(initialColumns = 240, initialRows = 20)
        viewport.replacePages(listOf(page(101L..104L, hasMoreBefore = false)), authoritativeColumns = 240)
        val view = viewport.createView(
            context = context,
            textSizeSp = 30f,
            onScrollPastTop = {},
            onScrollPastBottom = {},
        ) as TerminalView
        view.measure(
            View.MeasureSpec.makeMeasureSpec(1, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(320, View.MeasureSpec.EXACTLY),
        )
        view.layout(0, 0, 1, 320)
        view.updateSize()

        val debug = viewport.debugState()
        val readableFloor = minimumReadableTextSizePixels(context)
        assertEquals(240, debug.columns)
        assertEquals(readableFloor, debug.appliedTextSizePixels)
        assertEquals(
            debug.appliedTextSizePixels,
            fittedTranscriptTextSizeForColumnsPx(1, 240, 30, readableFloor),
        )
        assertTrue(view.visibleColumns < debug.columns)
        viewport.close()
    }

    @Test
    fun tinyViewportStaysAtDetachedBottomBoundaryInsteadOfFollowingCursorZero() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val viewport = TermuxTranscriptViewport(initialColumns = WIDTH, initialRows = 4)
        viewport.replacePages(listOf(page(101L..130L, hasMoreBefore = false)))
        val view = viewport.createView(
            context = context,
            textSizeSp = 30f,
            onScrollPastTop = {},
            onScrollPastBottom = {},
        ) as TerminalView

        // One real measure/layout must complete both the width-fit and final-font row pass.
        view.measure(
            View.MeasureSpec.makeMeasureSpec(20, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(1, View.MeasureSpec.EXACTLY),
        )
        view.layout(0, 0, 20, 1)
        view.onScreenUpdated()

        val debug = viewport.debugState()
        assertEquals(maxOf(4, view.visibleRows), debug.emulatorRows)
        assertTrue(debug.sizeUpdateRequests in 1..4)
        assertEquals(view.maximumTopRowForViewport, view.topRow)
        assertEquals(130L, view.topVisibleLineId)
        assertFalse(view.isFollowingOutput)
        viewport.close()
    }

    @Test
    fun oneLayoutConvergesWideReadableFloorFontAndFinalRows() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val viewport = TermuxTranscriptViewport(initialColumns = 240, initialRows = 4)
        viewport.replacePages(
            pages = listOf(page(101L..130L, hasMoreBefore = false, columns = 240)),
            authoritativeColumns = 240,
        )
        val view = viewport.createView(
            context = context,
            textSizeSp = 30f,
            onScrollPastTop = {},
            onScrollPastBottom = {},
        ) as TerminalView

        view.measure(
            View.MeasureSpec.makeMeasureSpec(1, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(320, View.MeasureSpec.EXACTLY),
        )
        view.layout(0, 0, 1, 320)

        val debug = viewport.debugState()
        assertEquals(minimumReadableTextSizePixels(context), debug.appliedTextSizePixels)
        assertEquals(maxOf(4, view.visibleRows), debug.emulatorRows)
        assertTrue(debug.sizeUpdateRequests in 1..4)
        assertEquals(240, debug.columns)
        viewport.close()
    }

    @Test
    fun replaceResizeAndReleasedViewRemountPreserveFullPresentationAndLineId() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val viewport = TermuxTranscriptViewport(initialColumns = WIDTH, initialRows = 4)
        val newest = page(101L..130L, hasMoreBefore = true)
        viewport.replacePages(listOf(newest))
        val first = viewport.createView(
            context = context,
            textSizeSp = 30f,
            onScrollPastTop = {},
            onScrollPastBottom = {},
        ) as TerminalView
        first.measure(
            View.MeasureSpec.makeMeasureSpec(1, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(40, View.MeasureSpec.EXACTLY),
        )
        first.layout(0, 0, 1, 40)
        first.restorePresentationState(intArrayOf(-5, 3, 0, 1))
        val anchor = first.topVisibleLineId
        assertTrue(anchor > 0)

        viewport.replacePages(listOf(page(81L..100L, hasMoreBefore = false), newest))
        assertEquals(anchor, first.topVisibleLineId)
        assertEquals(3, first.leftColumn)
        assertEquals(listOf(0, 0), first.capturePresentationState().takeLast(2))

        TermuxTranscriptViewport::class.java.getDeclaredField("viewReference").apply {
            isAccessible = true
            set(viewport, WeakReference<TerminalView>(null))
        }
        viewport.replacePages(listOf(page(81L..100L, hasMoreBefore = false), newest))
        val replacement = viewport.createView(
            context = context,
            textSizeSp = 30f,
            onScrollPastTop = {},
            onScrollPastBottom = {},
        ) as TerminalView
        replacement.measure(
            View.MeasureSpec.makeMeasureSpec(1, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(40, View.MeasureSpec.EXACTLY),
        )
        replacement.layout(0, 0, 1, 40)

        assertEquals(anchor, replacement.topVisibleLineId)
        assertEquals(3, replacement.leftColumn)
        assertEquals(listOf(0, 0), replacement.capturePresentationState().takeLast(2))
        viewport.close()
    }

    private fun page(
        lineIds: LongRange,
        hasMoreBefore: Boolean,
        columns: Int = WIDTH,
    ): TerminalTranscriptPageV1 {
        val rows = lineIds.map { lineId -> row("L$lineId", lineId, columns) }
        return TerminalTranscriptPageV1(
            snapshotAnchorSequence = 44,
            firstLineId = lineIds.first,
            lastLineId = lineIds.last,
            hasMoreBefore = hasMoreBefore,
            nextBeforeLineId = if (hasMoreBefore) lineIds.first else 0,
            columns = columns,
            rows = rows,
            styleTable = listOf(defaultStyle()),
            palette = palette(),
        )
    }

    private fun row(text: String, lineId: Long, columns: Int = WIDTH): TerminalRowSnapshotV1 {
        val padded = text.padEnd(columns).take(columns)
        return TerminalRowSnapshotV1(
            cells = padded.map { character ->
                TerminalCellSnapshotV1(character.toString(), displayWidth = 1, styleIndex = 0)
            },
            lineId = lineId,
            sealedAtSequence = 40,
        )
    }

    private fun defaultStyle() = TerminalStyleSnapshotV1(
        foreground = TerminalColorSnapshotV1.Default,
        background = TerminalColorSnapshotV1.Default,
        attributes = TerminalCellAttributesSnapshotV1(),
        underlineStyle = TerminalUnderlineStyleSnapshotV1.NONE,
    )

    private fun palette() = TerminalPaletteSnapshotV1(
        colorsRgb = List(256) { index -> index * 0x010101 },
        defaultForegroundRgb = 0xffffff,
        defaultBackgroundRgb = 0,
        defaultCursorRgb = 0xffffff,
    )

    private companion object {
        const val WIDTH = 8
    }
}

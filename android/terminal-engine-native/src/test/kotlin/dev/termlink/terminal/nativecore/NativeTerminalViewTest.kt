package dev.termlink.terminal.nativecore

import android.app.Activity
import android.graphics.Canvas
import android.graphics.Paint
import android.os.Looper
import android.view.MotionEvent
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalRenderCellV1
import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderCursorV2
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalRenderGraphemeV1
import dev.termlink.terminal.api.TerminalRenderPaletteV1
import dev.termlink.terminal.api.TerminalRenderRenditionV3
import dev.termlink.terminal.api.TerminalRenderRowV1
import dev.termlink.terminal.api.TerminalRenderScreenV1
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeTerminalViewTest {
    @Test
    fun wideContinuationBackgroundIsPaintedBeforeWideGraphemeText() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val view = NativeTerminalView(
            context = controller.get(),
            engine = null,
            textSizeSp = 14f,
            lineSpacingMultiplier = 1f,
        )
        try {
            view.layout(0, 0, 400, 200)
            val canvas = RecordingCanvas()

            view.drawFrame(canvas, wideChineseFrame(), testTheme())

            val textIndex = canvas.events.indexOfFirst { it is DrawEvent.Text }
            assertTrue("the wide grapheme was not painted", textIndex >= 0)
            assertEquals("中", (canvas.events[textIndex] as DrawEvent.Text).text)
            assertEquals(
                "the surface and both cell backgrounds must precede wide text",
                3,
                canvas.events.take(textIndex).count { it is DrawEvent.Rect },
            )
            assertTrue(
                "a continuation-cell background painted after wide text would erase its right half",
                canvas.events.drop(textIndex + 1).none { it is DrawEvent.Rect },
            )
        } finally {
            controller.destroy()
        }
    }

    @Test
    fun singleTapPerformsAccessibleClickAndFocusesTerminal() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = FakeNativeBridge(),
        )
        try {
            engine.setReadOnly(false)
            val view = engine.createView(controller.get()) as NativeTerminalView
            var clickCount = 0
            view.setOnClickListener { clickCount++ }
            controller.get().setContentView(view)

            val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 16f, 16f, 0)
            val up = MotionEvent.obtain(0, 20, MotionEvent.ACTION_UP, 16f, 16f, 0)
            try {
                assertTrue(view.dispatchTouchEvent(down))
                assertTrue(view.dispatchTouchEvent(up))
            } finally {
                down.recycle()
                up.recycle()
            }
            shadowOf(Looper.getMainLooper()).idle()

            assertEquals(1, clickCount)
            assertTrue(view.hasFocus())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    private fun wideChineseFrame(): TerminalRenderFrameV3 = TerminalRenderFrameV3(
        presentationRevision = 1,
        nextSourceSequence = 1,
        size = TerminalSize(2, 1),
        activeScreen = TerminalRenderScreenV1.PRIMARY,
        reverseVideo = false,
        cursor = TerminalRenderCursorV2(
            row = 0,
            column = 0,
            visible = false,
            wrapPending = false,
            shape = TerminalRenderCursorShapeV2.BLOCK,
            blinking = false,
        ),
        graphemes = listOf(TerminalRenderGraphemeV1("中", 2)),
        renditions = listOf(
            TerminalRenderRenditionV3(
                foreground = TerminalRenderColorV1.Default,
                background = TerminalRenderColorV1.Default,
                underlineColor = TerminalRenderColorV1.Default,
            ),
        ),
        palette = TerminalRenderPaletteV1(
            colorsRgb = List(256) { null },
            defaultForegroundRgb = null,
            defaultBackgroundRgb = null,
            defaultCursorRgb = null,
        ),
        hyperlinks = emptyList(),
        rows = listOf(
            TerminalRenderRowV1(
                meaningfulEnd = 2,
                softWrapped = false,
                cells = listOf(
                    TerminalRenderCellV1(
                        graphemeId = 1,
                        renditionId = 0,
                        wideContinuation = false,
                        isProtected = false,
                        hyperlinkReference = 0,
                    ),
                    TerminalRenderCellV1(
                        graphemeId = 0,
                        renditionId = 0,
                        wideContinuation = true,
                        isProtected = false,
                        hyperlinkReference = 0,
                    ),
                ),
            ),
        ),
        title = "",
    )

    private fun testTheme(): TerminalThemeV1 = TerminalThemeV1(
        name = "test",
        foreground = ArgbColor(0xffffffff.toInt()),
        background = ArgbColor(0xff000000.toInt()),
        cursor = ArgbColor(0xffffffff.toInt()),
        selectionBackground = ArgbColor(0xff334455.toInt()),
        selectionForeground = null,
        ansiColors = List(16) { ArgbColor(0xff000000.toInt() or it) },
        boldAsBright = false,
        fontFamilyHint = null,
    )

    private sealed interface DrawEvent {
        data object Rect : DrawEvent
        data class Text(val text: String) : DrawEvent
    }

    private class RecordingCanvas : Canvas() {
        val events = mutableListOf<DrawEvent>()

        override fun drawRect(left: Float, top: Float, right: Float, bottom: Float, paint: Paint) {
            events += DrawEvent.Rect
        }

        override fun drawText(text: String, x: Float, y: Float, paint: Paint) {
            events += DrawEvent.Text(text)
        }
    }
}

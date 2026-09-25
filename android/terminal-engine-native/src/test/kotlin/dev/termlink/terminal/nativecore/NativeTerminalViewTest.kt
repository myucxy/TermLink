package dev.termlink.terminal.nativecore

import android.app.Activity
import android.graphics.Canvas
import android.graphics.Paint
import android.os.Looper
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
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
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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
    fun fingerRoutingIsExplicitAndPinnedUntilReleaseWhilePhysicalMouseStaysIndependent() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), FakeNativeBridge())
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1006h".encodeToByteArray()))
            engine.setReadOnly(false)
            assertTrue(engine.mouseReporting.value)
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 600)
            var clicks = 0
            view.setOnClickListener { clicks++ }
            var time = 0L
            fun event(action: Int, mouse: Boolean = false) {
                val event = MotionEvent.obtain(0, time, action, 16f, 16f, 0)
                time += 20L
                if (mouse) event.source = android.view.InputDevice.SOURCE_MOUSE
                try { assertTrue(view.dispatchTouchEvent(event)) } finally { event.recycle() }
            }
            // Default finger taps are local even while Codex enables all-motion reporting.
            event(MotionEvent.ACTION_DOWN)
            event(MotionEvent.ACTION_UP)
            shadowOf(Looper.getMainLooper()).idle()
            assertEquals(1, clicks)
            assertEquals(null, kotlinx.coroutines.withTimeoutOrNull(50) { engine.inputEvents.first() })
            engine.setTouchMouseEnabled(true)
            event(MotionEvent.ACTION_DOWN)
            assertEquals(null, kotlinx.coroutines.withTimeoutOrNull(50) { engine.inputEvents.first() })
            engine.setTouchMouseEnabled(false) // Changing the preference cannot reroute half a gesture.
            event(MotionEvent.ACTION_UP)
            event(MotionEvent.ACTION_DOWN, mouse = true)
            event(MotionEvent.ACTION_MOVE, mouse = true)
            event(MotionEvent.ACTION_CANCEL, mouse = true)
            shadowOf(Looper.getMainLooper()).idle()
            assertEquals(1, clicks)
            val reports = withTimeout(1_000) { engine.inputEvents.take(5).toList() }.map { it.decodeToString() }
            assertTrue(reports[0].startsWith("\u001b[<0;") && reports[0].endsWith("M"))
            assertTrue(reports[1].endsWith("m"))
            assertEquals(reports[0], reports[2])
            assertTrue(reports[3].startsWith("\u001b[<32;") && reports[3].endsWith("M"))
            assertEquals(reports[1], reports[4])
            assertEquals(null, kotlinx.coroutines.withTimeoutOrNull(50) { engine.inputEvents.first() })

            engine.setTouchMouseEnabled(true)
            event(MotionEvent.ACTION_DOWN)
            engine.setReadOnly(true)
            event(MotionEvent.ACTION_UP)
            shadowOf(Looper.getMainLooper()).idle()
            assertEquals(1, clicks)
            assertEquals(null, kotlinx.coroutines.withTimeoutOrNull(50) { engine.inputEvents.first() })
            engine.setReadOnly(false)
            engine.feed(2, ByteBuffer.wrap("\u001b[?1003l".encodeToByteArray()))
            time += 500L // Outside the existing local double-tap recognition window.
            event(MotionEvent.ACTION_DOWN)
            event(MotionEvent.ACTION_UP)
            assertEquals(2, clicks) // saved ON preference cannot capture a non-mouse program
            assertEquals(null, kotlinx.coroutines.withTimeoutOrNull(50) { engine.inputEvents.first() })
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun mouseSwipesSendOnlyWheelsAndHoldDragReleasesOnInterrupt() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), FakeNativeBridge())
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1006h".encodeToByteArray()))
            engine.setReadOnly(false)
            engine.setTouchMouseEnabled(true)
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 600, 600)
            var clicks = 0
            view.setOnClickListener { clicks++ }
            var down = 0L
            fun event(action: Int, x: Float = 120f, y: Float = 120f, pointers: Int = 1) {
                val now = android.os.SystemClock.uptimeMillis()
                if (action == MotionEvent.ACTION_DOWN) down = now
                val properties = Array(pointers) { MotionEvent.PointerProperties().apply { id = it; toolType = MotionEvent.TOOL_TYPE_FINGER } }
                val coords = Array(pointers) { MotionEvent.PointerCoords().apply { this.x = x + it * 20; this.y = y; pressure = 1f; size = 1f } }
                val input = MotionEvent.obtain(down, now, action, pointers, properties, coords,
                    0, 0, 1f, 1f, 0, 0, android.view.InputDevice.SOURCE_TOUCHSCREEN, 0)
                try { assertTrue(view.dispatchTouchEvent(input)) } finally { input.recycle() }
            }
            suspend fun drain(): List<String> {
                val result = mutableListOf<String>()
                while (true) result += (kotlinx.coroutines.withTimeoutOrNull(30) { engine.inputEvents.first() }
                    ?: break).decodeToString()
                return result
            }
            fun waitHold() = shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(
                android.view.ViewConfiguration.getLongPressTimeout().toLong() + 10))
            for ((x, y, code) in listOf(Triple(120f, 380f, 64), Triple(120f, 20f, 65),
                    Triple(380f, 120f, 66), Triple(20f, 120f, 67))) {
                event(MotionEvent.ACTION_DOWN)
                assertTrue(drain().isEmpty())
                event(MotionEvent.ACTION_MOVE, x, y)
                event(MotionEvent.ACTION_UP, x, y)
                val reports = drain()
                assertTrue(reports.isNotEmpty())
                assertTrue(reports.all { it.startsWith("\u001b[<$code;") && it.endsWith("M") })
            }
            assertEquals(0L, engine.historyOffsetRowsForTest())
            assertEquals(0, clicks)

            repeat(2) { event(MotionEvent.ACTION_DOWN); event(MotionEvent.ACTION_UP) }
            val doubleClick = drain()
            assertEquals(4, doubleClick.size)
            assertTrue(doubleClick[0].endsWith("M") && doubleClick[1].endsWith("m"))
            assertEquals(doubleClick.take(2), doubleClick.drop(2))

            event(MotionEvent.ACTION_DOWN)
            waitHold()
            event(MotionEvent.ACTION_MOVE, y = 200f)
            event(MotionEvent.ACTION_CANCEL, y = 200f)
            val drag = drain()
            assertEquals(3, drag.size)
            assertTrue(drag[0].startsWith("\u001b[<0;") && drag[0].endsWith("M"))
            assertTrue(drag[1].startsWith("\u001b[<32;"))
            assertTrue(drag[2].endsWith("m"))

            event(MotionEvent.ACTION_DOWN)
            event(MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT), pointers = 2)
            waitHold()
            event(MotionEvent.ACTION_UP)
            assertTrue(drain().isEmpty())

            event(MotionEvent.ACTION_DOWN)
            waitHold()
            view.onWindowFocusChanged(false)
            event(MotionEvent.ACTION_UP)
            assertEquals(2, drain().size) // one press and one release, no click after focus loss

            event(MotionEvent.ACTION_DOWN)
            engine.setReadOnly(true)
            event(MotionEvent.ACTION_MOVE, y = 200f)
            engine.setReadOnly(false)
            event(MotionEvent.ACTION_UP, y = 200f)
            assertTrue(drain().isEmpty()) // do not resume half a gesture under a new lease
        } finally { engine.dispose(); controller.destroy() }
    }

    @Test
    fun bothImeDeletionApisSubmitBackspaceAndForwardDeleteKeys() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val activity = controller.get()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), FakeNativeBridge())
        engine.setReadOnly(false)
        val view = engine.createView(activity)
        activity.setContentView(view)
        view.requestFocus()
        shadowOf(Looper.getMainLooper()).idle()
        val connection = requireNotNull(view.onCreateInputConnection(EditorInfo()))

        connection.deleteSurroundingText(2, 1)
        connection.deleteSurroundingTextInCodePoints(1, 2)
        shadowOf(Looper.getMainLooper()).idle()

        val events = runBlocking {
            withTimeout(1_000) { engine.inputEvents.take(6).toList() }
        }
        assertEquals(listOf(3, 3, 6, 3, 6, 6).map { "kitty:$it:0:1:0" },
            events.map { it.decodeToString() })

        engine.dispose()
        controller.destroy()
    }


    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun viewportMeasurementUsesFontSpacingAndVisibleAreaWithoutResizingAuthority() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val engine = NativeTerminalEngine(TerminalSize(120, 40), FakeNativeBridge())
        try {
            fun measure(font: Float, spacing: Float, height: Int = 600): dev.termlink.terminal.api.TerminalViewportMetrics {
                val view = engine.createView(controller.get(), font, spacing) as NativeTerminalView
                controller.get().setContentView(view)
                view.layout(0, 0, 400, height)
                return requireNotNull(engine.measuredViewport())
            }
            val normal = measure(13f, 1f)
            val large = measure(26f, 1f)
            val spaced = measure(13f, 1.5f)
            val keyboard = measure(13f, 1f, 300)
            assertTrue(large.size.columns < normal.size.columns)
            assertTrue(large.size.rows < normal.size.rows)
            assertEquals(normal.size.columns, spaced.size.columns)
            assertTrue(spaced.size.rows < normal.size.rows)
            assertEquals(normal.size.columns, keyboard.size.columns)
            assertTrue(keyboard.size.rows < normal.size.rows)
            assertTrue(normal.size.columns * normal.cellWidthPx <= normal.widthPx)
            assertTrue((normal.size.columns + 1) * normal.cellWidthPx > normal.widthPx)
            assertTrue(normal.size.rows * normal.cellHeightPx <= normal.heightPx)
            assertEquals(TerminalSize(120, 40), engine.authoritativeSize.value)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun imeCompositionCommitsOneCharacterExactlyOnce() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), bridge)
        try {
            engine.setReadOnly(false)
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            val input = requireNotNull(view.onCreateInputConnection(EditorInfo()))

            assertTrue(input.setComposingText("1", 1))
            assertTrue(input.commitText("1", 1))

            assertEquals(1, bridge.inputV3Calls)
            assertEquals("1", bridge.lastInputV3?.copyAssociatedText()?.decodeToString())

            assertTrue(input.commitText("1", 1))
            assertEquals(2, bridge.inputV3Calls)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun detachedHistoryViewStopsItsWorkerAndCanRestartAfterReattachment() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 20
            forcedScrollbackGenerationRows = 20
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 600)
            bridge.scrollbackRenderThreads.clear()

            dispatchDrag(view, fromY = 120f, toY = 520f)
            awaitHistory(view)
            val firstWorker = bridge.scrollbackRenderThreads.last()
            assertTrue(view.hasHistoryExecutorForTest())

            controller.get().setContentView(View(controller.get()))
            shadowOf(Looper.getMainLooper()).idle()
            assertFalse("detaching the terminal must release its private worker", view.hasHistoryExecutorForTest())

            controller.get().setContentView(view)
            view.layout(0, 0, 400, 600)
            dispatchDrag(view, fromY = 520f, toY = 120f)
            awaitHistory(view)

            assertTrue(view.hasHistoryExecutorForTest())
            assertTrue("reattachment must use a fresh worker", bridge.scrollbackRenderThreads.last() !== firstWorker)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

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
                "the surface and merged cell background must precede wide text",
                2,
                canvas.events.take(textIndex).count { it is DrawEvent.Rect },
            )
            assertTrue(
                "a continuation-cell background painted after wide text would erase its right half",
                canvas.events.drop(textIndex + 1).none { it is DrawEvent.Rect },
            )

            canvas.events.clear()
            view.layout(0, 0, 10, 100)
            view.positionViewportForTest(column = 1, fraction = 0.25f)
            view.drawFrame(canvas, wideChineseFrame(), testTheme())
            assertTrue(
                "a wide grapheme clipped through its continuation column must remain visible",
                canvas.events.any { it == DrawEvent.Text("中") },
            )
        } finally {
            controller.destroy()
        }
    }

    @Test
    fun adjacentAsciiCellsWithTheSameRenditionUseOneTextDraw() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val view = NativeTerminalView(
            context = controller.get(),
            engine = null,
            textSizeSp = 14f,
            lineSpacingMultiplier = 1f,
        )
        try {
            view.layout(0, 0, 500, 100)
            view.forceTextRunWidthMatchForTest(true)
            val canvas = RecordingCanvas()

            view.drawFrame(canvas, asciiFrame("TermLink"), testTheme())

            assertEquals(listOf(DrawEvent.Text("TermLink")), canvas.events.filterIsInstance<DrawEvent.Text>())
        } finally {
            controller.destroy()
        }
    }

    @Test
    fun asciiTextRunsStopAtRenditionBoundariesAndBatchDecorations() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val view = NativeTerminalView(
            context = controller.get(),
            engine = null,
            textSizeSp = 14f,
            lineSpacingMultiplier = 1f,
        )
        try {
            view.layout(0, 0, 500, 100)
            view.forceTextRunWidthMatchForTest(true)
            val canvas = RecordingCanvas()
            val underlined = testRendition(underlineStyle = TerminalRenderUnderlineStyleV3.SINGLE)
            val faintUnderlined = underlined.copy(faint = true)

            view.drawFrame(
                canvas,
                asciiFrame(
                    text = "ABCD",
                    renditionIds = listOf(0, 0, 1, 1),
                    renditions = listOf(underlined, faintUnderlined),
                ),
                testTheme(),
            )

            assertEquals(
                listOf(DrawEvent.Text("AB"), DrawEvent.Text("CD")),
                canvas.events.filterIsInstance<DrawEvent.Text>(),
            )
            assertEquals(
                "each safe text run should share one decoration draw",
                2,
                canvas.events.count { it is DrawEvent.Line },
            )
        } finally {
            controller.destroy()
        }
    }

    @Test
    fun asciiTextRunFallsBackToCellsWhenItsWidthDoesNotMatchTheGrid() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val view = NativeTerminalView(
            context = controller.get(),
            engine = null,
            textSizeSp = 14f,
            lineSpacingMultiplier = 1f,
        )
        try {
            view.layout(0, 0, 500, 100)
            view.forceTextRunWidthMatchForTest(false)
            val canvas = RecordingCanvas()

            view.drawFrame(canvas, asciiFrame("ABC"), testTheme())

            assertEquals(
                listOf(DrawEvent.Text("A"), DrawEvent.Text("B"), DrawEvent.Text("C")),
                canvas.events.filterIsInstance<DrawEvent.Text>(),
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

    @Test
    fun singleTapCancelsSelectionWithoutActivatingTerminalClick() {
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
            view.layout(0, 0, 400, 600)
            view.seedSelectionForTest()
            assertTrue(view.hasSelectionForTest())

            val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 200f, 300f, 0)
            val up = MotionEvent.obtain(0, 20, MotionEvent.ACTION_UP, 200f, 300f, 0)
            try {
                assertTrue(view.dispatchTouchEvent(down))
                assertTrue(view.dispatchTouchEvent(up))
            } finally {
                down.recycle()
                up.recycle()
            }

            assertFalse("a tap should close the native selection", view.hasSelectionForTest())
            assertEquals("the tap used to dismiss selection must be consumed", 0, clickCount)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun selectionHandleCanBeDraggedWithoutActivatingTerminalClick() {
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
            view.layout(0, 0, 400, 600)
            view.seedSelectionForTest()

            val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 16f, 16f, 0)
            val move = MotionEvent.obtain(0, 100, MotionEvent.ACTION_MOVE, 200f, 300f, 0)
            val up = MotionEvent.obtain(0, 120, MotionEvent.ACTION_UP, 200f, 300f, 0)
            try {
                assertTrue(view.dispatchTouchEvent(down))
                assertTrue(view.dispatchTouchEvent(move))
                assertTrue(view.dispatchTouchEvent(up))
            } finally {
                down.recycle()
                move.recycle()
                up.recycle()
            }

            assertTrue("dragging a selection handle keeps selection active", view.hasSelectionForTest())
            assertEquals("selection handle drag must not click the terminal", 0, clickCount)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun newVerticalSwipeClearsSelectionBeforeBrowsingHistory() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 12
            forcedScrollbackGenerationRows = 12
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 600)
            view.seedSelectionForTest()

            dispatchDrag(view, fromY = 120f, toY = 520f)
            awaitHistory(view)

            assertFalse("a new swipe must leave selection mode", view.hasSelectionForTest())
            assertTrue("the same swipe must remain available for history", engine.historyOffsetRowsForTest() > 0)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun cursorKeepsHorizontalBreathingRoomAtBothViewportEdges() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(
                size = size,
                cursorRow = 0,
                cursorColumn = 60,
                nextSourceSequence = 1,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 200, 200)

            // The exact cell width is intentionally an implementation detail;
            // assert the invariant through the view's own cursor visibility
            // helper and a non-zero edge reservation.
            assertTrue(view.cursorHorizontalBreathingRoomForTest() > 0f)
            assertTrue(view.isCursorFullyVisibleForTest())
            assertTrue(view.isCursorComfortablyVisibleForTest())
            assertTrue("the viewport should move toward an interior cursor", view.viewportXForTest() > 0f)

            val rightEdgeViewport = view.viewportXForTest()
            bridge.forcedRenderFrameV3 = cursorFrame(
                size = size,
                cursorRow = 0,
                cursorColumn = 20,
                nextSourceSequence = 2,
            )
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))
            shadowOf(Looper.getMainLooper()).idle()

            assertTrue(view.isCursorFullyVisibleForTest())
            assertTrue(view.isCursorComfortablyVisibleForTest())
            assertTrue(
                "moving toward the left edge should retain an interior margin",
                view.viewportXForTest() < rightEdgeViewport,
            )
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun visibleCenteredTuiCursorDoesNotMoveTheViewportOnDirectionInput() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(100, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(
                size = size,
                cursorRow = 5,
                cursorColumn = 40,
                nextSourceSequence = 1,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 300)
            // Model a full-screen TUI such as OpenCode: its real input cursor is
            // intentionally inside a centered prompt rather than at column zero.
            view.positionViewportForTest(column = 20, fraction = 0f)
            assertTrue(view.isCursorComfortablyVisibleForTest())
            val viewportXBefore = view.viewportXForTest()
            val viewportYBefore = view.viewportYForTest()

            engine.setReadOnly(false)
            assertTrue(engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_DOWN))
            shadowOf(Looper.getMainLooper()).idle()

            assertEquals(viewportXBefore, view.viewportXForTest(), 0.01f)
            assertEquals(viewportYBefore, view.viewportYForTest(), 0.01f)
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun verticalDragBrowsesLocalHistoryInBothDirections() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 12
            forcedScrollbackGenerationRows = 12
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            engine.setTouchMouseEnabled(true)
            engine.setReadOnly(false)
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1006h\u001b[?1003l".encodeToByteArray()))
            assertFalse(engine.mouseReporting.value)
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 600)
            bridge.scrollbackRenderThreads.clear()
            val uiThread = Thread.currentThread()

            dispatchDrag(view, fromY = 120f, toY = 520f)
            awaitHistory(view)
            assertTrue("downward drag should reveal older rows", engine.historyOffsetRowsForTest() > 0)
            assertFalse(view.isFollowingCursorForTest())
            assertTrue(
                "history projection must not block the UI thread",
                synchronized(bridge.scrollbackRenderThreads) {
                    bridge.scrollbackRenderThreads.none { it === uiThread }
                },
            )

            dispatchDrag(view, fromY = 520f, toY = 20f)
            awaitHistory(view)
            assertEquals(0L, engine.historyOffsetRowsForTest())

            dispatchDrag(view, fromY = 520f, toY = 20f)
            awaitHistory(view)
            assertTrue("crossing the live boundary should restore cursor following", view.isFollowingCursorForTest())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun hiddenCursorArrowInputKeepsTheBrowsedViewportWhileStillEncodingInput() {
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 12
            forcedScrollbackGenerationRows = 12
            forcedScrollbackFrames[3] = cursorFrame(
                size = size,
                cursorRow = 23,
                nextSourceSequence = 1,
                cursorVisible = false,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val navigation = engine.beginHistoryNavigation()
            assertEquals(NativeHistoryScrollResult.MOVED, engine.scrollHistoryRows(3, navigation))
            assertFalse(engine.frames.value.cursor.visible)
            val encodedBefore = bridge.inputV1Calls + bridge.inputV2Calls + bridge.inputV3Calls

            engine.setReadOnly(false)
            assertTrue(engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP))

            assertEquals(3L, engine.historyOffsetRowsForTest())
            assertTrue(
                "the direction key must still reach terminal-core",
                bridge.inputV1Calls + bridge.inputV2Calls + bridge.inputV3Calls > encodedBefore,
            )
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun visibleCursorInputStillReturnsTheNativeViewportToLiveOutput() {
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 12
            forcedScrollbackGenerationRows = 12
            forcedScrollbackFrames[3] = cursorFrame(
                size = size,
                cursorRow = 23,
                nextSourceSequence = 1,
                cursorVisible = true,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val navigation = engine.beginHistoryNavigation()
            assertEquals(NativeHistoryScrollResult.MOVED, engine.scrollHistoryRows(3, navigation))

            engine.setReadOnly(false)
            assertTrue(engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP))

            assertEquals(0L, engine.historyOffsetRowsForTest())
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun shrinkingLiveViewportKeepsBottomCursorFullyVisible() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(
                size = size,
                cursorRow = 23,
                cursorColumn = 79,
                nextSourceSequence = 1,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 80)

            view.layout(0, 0, 20, 20)

            assertTrue(view.isFollowingCursorForTest())
            assertTrue(
                "the viewport should move when the IME reduces its height; " +
                    "cursor=${engine.frames.value.cursor.row}, viewport=${view.viewportYForTest()}, height=${view.height}",
                view.viewportYForTest() > 0f,
            )
            assertTrue("the viewport should keep a right-edge cursor visible", view.viewportXForTest() > 0f)
            assertTrue("the complete cursor cell must remain visible", view.isCursorFullyVisibleForTest())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun horizontalDragIsAxisLockedAndStartsAnInertialScroll() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 12
            forcedScrollbackGenerationRows = 12
            forcedRenderFrameV3 = cursorFrame(
                size = size,
                cursorRow = 0,
                cursorColumn = 79,
                nextSourceSequence = 1,
            )
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 20, 200)
            bridge.scrollbackRenderThreads.clear()

            dispatchHorizontalDrag(view, fromX = 180f, toX = 0f)
            awaitHistory(view)

            assertTrue(view.viewportXForTest() > 0f)
            assertFalse("manual horizontal browsing must pause cursor following", view.isFollowingCursorForTest())
            assertTrue("a fast drag should continue with an inertial scroll", view.hasActiveFlingForTest())
            assertTrue(
                "axis locking must keep a horizontal gesture out of terminal history",
                bridge.scrollbackRenderThreads.isEmpty(),
            )
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun viewportResizePreservesHistoryPositionUntilInputReturnsToLiveCursor() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(size = size, cursorRow = 23, nextSourceSequence = 1)
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 20)
            assertTrue(view.isCursorFullyVisibleForTest())

            dispatchDrag(view, fromY = 2f, toY = 100f)
            val historyViewportY = view.viewportYForTest()
            assertFalse(view.isFollowingCursorForTest())

            view.layout(0, 0, 400, 10)
            assertEquals(historyViewportY, view.viewportYForTest(), 0.01f)
            assertFalse(
                "resizing while browsing must not pull history back to the cursor; " +
                    "viewport=${view.viewportYForTest()}, height=${view.height}",
                view.isCursorFullyVisibleForTest(),
            )

            engine.scrollToLatest()
            shadowOf(Looper.getMainLooper()).idle()

            assertTrue(view.isFollowingCursorForTest())
            assertTrue(view.isCursorFullyVisibleForTest())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    fun outputFramesFollowCursorOnlyWhileViewportIsLive() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(size = size, cursorRow = 0, nextSourceSequence = 1)
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 20)

            bridge.forcedRenderFrameV3 = cursorFrame(size = size, cursorRow = 23, nextSourceSequence = 2)
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))
            shadowOf(Looper.getMainLooper()).idle()

            assertTrue("live output should keep the moved cursor visible", view.isCursorFullyVisibleForTest())

            dispatchDrag(view, fromY = 2f, toY = 100f)
            val historyViewportY = view.viewportYForTest()
            assertFalse(view.isFollowingCursorForTest())

            bridge.forcedRenderFrameV3 = cursorFrame(size = size, cursorRow = 22, nextSourceSequence = 3)
            engine.feed(2, ByteBuffer.wrap(byteArrayOf(2)))
            shadowOf(Looper.getMainLooper()).idle()

            assertEquals(historyViewportY, view.viewportYForTest(), 0.01f)
            assertFalse(view.isFollowingCursorForTest())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    private fun dispatchDrag(view: NativeTerminalView, fromY: Float, toY: Float) {
        val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 100f, fromY, 0)
        val move = MotionEvent.obtain(0, 100, MotionEvent.ACTION_MOVE, 100f, toY, 0)
        val up = MotionEvent.obtain(0, 120, MotionEvent.ACTION_UP, 100f, toY, 0)
        try {
            assertTrue(view.dispatchTouchEvent(down))
            assertTrue(view.dispatchTouchEvent(move))
            assertTrue(view.dispatchTouchEvent(up))
        } finally {
            down.recycle()
            move.recycle()
            up.recycle()
        }
    }

    private fun dispatchHorizontalDrag(view: NativeTerminalView, fromX: Float, toX: Float) {
        val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, fromX, 10f, 0)
        val move = MotionEvent.obtain(0, 20, MotionEvent.ACTION_MOVE, toX + 20f, 10f, 0)
        val up = MotionEvent.obtain(0, 30, MotionEvent.ACTION_UP, toX, 10f, 0)
        try {
            assertTrue(view.dispatchTouchEvent(down))
            assertTrue(view.dispatchTouchEvent(move))
            assertTrue(view.dispatchTouchEvent(up))
        } finally {
            down.recycle()
            move.recycle()
            up.recycle()
        }
    }

    private fun awaitHistory(view: NativeTerminalView) {
        view.awaitHistoryRequestsForTest()
        shadowOf(Looper.getMainLooper()).idle()
    }

    private fun cursorFrame(
        size: TerminalSize,
        cursorRow: Int,
        cursorColumn: Int = 0,
        nextSourceSequence: Long,
        cursorVisible: Boolean = true,
    ): NativeBridgeRenderFrame = NativeBridgeRenderFrame(
        payload = fakeRenderFrameV3(
            nextSourceSequence = nextSourceSequence,
            size = size,
            cursorRow = cursorRow,
            cursorColumn = cursorColumn,
            cursorVisible = cursorVisible,
        ),
    )

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

    private fun asciiFrame(
        text: String,
        renditionIds: List<Int> = List(text.length) { 0 },
        renditions: List<TerminalRenderRenditionV3> = listOf(testRendition()),
    ): TerminalRenderFrameV3 {
        require(text.isNotEmpty())
        require(renditionIds.size == text.length)
        return TerminalRenderFrameV3(
            presentationRevision = 1,
            nextSourceSequence = 1,
            size = TerminalSize(text.length, 1),
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
            graphemes = text.map { TerminalRenderGraphemeV1(it.toString(), 1) },
            renditions = renditions,
            palette = TerminalRenderPaletteV1(
                colorsRgb = List(256) { null },
                defaultForegroundRgb = null,
                defaultBackgroundRgb = null,
                defaultCursorRgb = null,
            ),
            hyperlinks = emptyList(),
            rows = listOf(
                TerminalRenderRowV1(
                    meaningfulEnd = text.length,
                    softWrapped = false,
                    cells = text.indices.map { index ->
                        TerminalRenderCellV1(
                            graphemeId = index + 1,
                            renditionId = renditionIds[index],
                            wideContinuation = false,
                            isProtected = false,
                            hyperlinkReference = 0,
                        )
                    },
                ),
            ),
            title = "",
        )
    }

    private fun testRendition(
        underlineStyle: TerminalRenderUnderlineStyleV3 = TerminalRenderUnderlineStyleV3.NONE,
    ): TerminalRenderRenditionV3 = TerminalRenderRenditionV3(
        foreground = TerminalRenderColorV1.Default,
        background = TerminalRenderColorV1.Default,
        underlineColor = TerminalRenderColorV1.Default,
        underlineStyle = underlineStyle,
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
        data object Line : DrawEvent
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

        override fun drawLine(startX: Float, startY: Float, stopX: Float, stopY: Float, paint: Paint) {
            events += DrawEvent.Line
        }
    }
}

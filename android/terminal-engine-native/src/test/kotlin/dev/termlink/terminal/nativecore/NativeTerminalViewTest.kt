package dev.termlink.terminal.nativecore

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.os.Looper
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalGraphicsResource
import dev.termlink.terminal.api.TerminalGraphicsResources
import dev.termlink.terminal.api.TerminalLineRendition
import dev.termlink.terminal.api.TerminalRenderCellV1
import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderCursorV2
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalRenderFrameV4
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalRenderGlyphV5
import dev.termlink.terminal.api.TerminalRenderImageV5
import dev.termlink.terminal.api.TerminalRenderImageProtocolV5
import dev.termlink.terminal.api.TerminalRenderRatioV5
import dev.termlink.terminal.api.TerminalRenderSourceV5
import dev.termlink.terminal.api.TerminalRenderClipV5
import dev.termlink.terminal.api.TerminalRenderGraphemeV1
import dev.termlink.terminal.api.TerminalRenderPaletteV1
import dev.termlink.terminal.api.TerminalRenderRenditionV3
import dev.termlink.terminal.api.TerminalRenderRowV1
import dev.termlink.terminal.api.TerminalRenderScreenV1
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalWidthProfile
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
    fun coveringLiveViewportRevokesVisibleDisplayUntilUncovered() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val engine = NativeTerminalEngine(bridge = FakeNativeBridge())
        fun showTestWindow(view: View) {
            // Robolectric attaches this Activity's View but leaves AttachInfo at GONE.
            // Set the window state explicitly; the production visibility guard stays intact.
            val attachInfo = requireNotNull(org.robolectric.util.ReflectionHelpers.getField<Any>(view, "mAttachInfo"))
            org.robolectric.util.ReflectionHelpers.setField(attachInfo, "mWindowVisibility", View.VISIBLE)
        }
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            controller.visible()
            view.layout(0, 0, 400, 300)
            controller.windowFocusChanged(true)
            assertTrue("test View must be attached before visibility is checked", view.isAttachedToWindow)
            assertTrue("test View must be shown", view.isShown)
            showTestWindow(view)
            assertEquals(View.VISIBLE, view.windowVisibility)
            engine.setLiveViewportUncovered(view, false)
            assertFalse("the active engine must register the attached View", view.isLiveViewportUncovered())
            engine.setLiveViewportUncovered(view, true)
            assertTrue(engine.hasVisibleDisplay())

            engine.setLiveViewportUncovered(view, false)
            assertFalse("history overlay must reject an already pending confirmation", engine.hasVisibleDisplay())
            engine.setLiveViewportUncovered(view, true)
            assertTrue(engine.hasVisibleDisplay())

            val replacement = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(replacement)
            replacement.layout(0, 0, 400, 300)
            assertTrue(replacement.isAttachedToWindow)
            showTestWindow(replacement)
            assertTrue(engine.hasVisibleDisplay())
            engine.setLiveViewportUncovered(view, false)
            assertTrue("an old View must not cover the replacement renderer", engine.hasVisibleDisplay())
            engine.setLiveViewportUncovered(replacement, false)
            assertFalse("frozen history must hide the live renderer even while attached", engine.hasVisibleDisplay())
            engine.setLiveViewportUncovered(replacement, true)
            replacement.visibility = View.INVISIBLE
            assertFalse(engine.hasVisibleDisplay())
        } finally {
            engine.dispose()
            controller.destroy()
        }
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun rustGraphicsFixturesReachCanvasWithTheRecordedCropClipAndMasks() = withGraphicsView { view ->
        val path = System.getenv("TERMLINK_GRAPHICS_FIXTURES")
        org.junit.Assume.assumeTrue("TERMLINK_GRAPHICS_FIXTURES was not supplied", path != null)
        fun fixture(name: String) = RenderFrameV5Decoder.decode(java.io.File(requireNotNull(path), "$name.v5.bin").readBytes())
        val theme = testTheme().copy(foreground = ArgbColor(0xff112233.toInt()), background = ArgbColor(0xff445566.toInt()))
        val kitty = fixture("kitty-crop-scrolled")
        var bitmap = renderGraphics(view, kitty, theme)
        assertEquals(0xff00ff00.toInt(), cellPixel(view, bitmap, 0.5f, 0.25f))
        assertEquals(0xffa2aab3.toInt(), cellPixel(view, bitmap, 0.5f, 0.75f))
        val kittyBitmap = requireNotNull(view.graphicsBitmapForTest(kitty.images.single().resourceId))
        val independentlyDecodedKitty = fixture("kitty-crop-scrolled")
        org.junit.Assert.assertNotSame(kitty.resources[1], independentlyDecodedKitty.resources[1])
        renderGraphics(view, independentlyDecodedKitty, theme)
        org.junit.Assert.assertSame(kittyBitmap, view.graphicsBitmapForTest(kitty.images.single().resourceId))
        bitmap = renderGraphics(view, fixture("sixel-region-clip"), theme)
        assertEquals(0xff445566.toInt(), cellPixel(view, bitmap, 0.25f, 0.5f))
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 0.25f, 1.5f))
        assertEquals(0xff445566.toInt(), cellPixel(view, bitmap, 0.75f, 1.5f))
        bitmap = renderGraphics(view, fixture("drcs-live"), theme)
        assertEquals(0xff112233.toInt(), cellPixel(view, bitmap, 0.5f, 0.15f))
        assertEquals(0xff445566.toInt(), cellPixel(view, bitmap, 0.5f, 0.65f))
        assertEquals(0xff112233.toInt(), cellPixel(view, bitmap, 3.5f, 0.09375f))
        assertEquals(0xff445566.toInt(), cellPixel(view, bitmap, 3.5f, 0.5f))
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun graphicsLayersRespectCellBackgroundsGlyphsAndPositiveZ() = withGraphicsView { view ->
        val red = rgbaResource(1, 255, 0, 0)
        val green = rgbaResource(2, 0, 255, 0)
        val mask = TerminalGraphicsResource(3, 1, 1, TerminalGraphicsFormat.A8, byteArrayOf(-1))
        val text = graphicsText(renditions = listOf(testRendition(), testRendition().copy(background = TerminalRenderColorV1.Rgb(0x0000ff))),
            renditionIds = listOf(0, 1))
        val below = graphicsImage(resource = 1, z = -1_073_741_825, columns = 2)
        var bitmap = renderGraphics(view, graphicsFrame(text, listOf(red), listOf(below)))
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        assertEquals(0xff0000ff.toInt(), cellPixel(view, bitmap, 1.5f, 0.5f))

        val aboveBackground = graphicsImage(resource = 2, z = -1_073_741_824, columns = 2)
        val glyph = TerminalRenderGlyphV5(0, 0, 3, 0, 1, 1)
        bitmap = renderGraphics(view, graphicsFrame(text, listOf(red, green, mask), listOf(below, aboveBackground), listOf(glyph)))
        assertEquals(0xffffffff.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        assertEquals(0xff00ff00.toInt(), cellPixel(view, bitmap, 1.5f, 0.5f))

        bitmap = renderGraphics(view, graphicsFrame(text, listOf(green, mask),
            listOf(aboveBackground.copy(z = 0)), listOf(glyph)))
        assertEquals(0xff00ff00.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        val explicitDefault = graphicsText(renditions = listOf(testRendition().copy(background = TerminalRenderColorV1.Rgb(0))))
        bitmap = renderGraphics(view, graphicsFrame(explicitDefault, listOf(red), listOf(below)))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun rgbaStraightAlphaIsPremultipliedOnceAndSourceCropIsApplied() = withGraphicsView { view ->
        val red = rgbaResource(1, 255, 0, 0, 128)
        val text = graphicsText(renditions = listOf(testRendition().copy(background = TerminalRenderColorV1.Rgb(0x0000ff))))
        var bitmap = renderGraphics(view, graphicsFrame(text, listOf(red), listOf(graphicsImage(z = -1))))
        assertEquals(0xff80007f.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        val colors = TerminalGraphicsResource(1, 2, 1, TerminalGraphicsFormat.RGBA8888,
            byteArrayOf(-1, 0, 0, -1, 0, -1, 0, -1))
        val crop = graphicsImage().copy(source = TerminalRenderSourceV5(1, 0, 1, 1), width = TerminalRenderRatioV5(1, 2))
        bitmap = renderGraphics(view, graphicsFrame(graphicsText(), listOf(colors), listOf(crop)))
        assertEquals(0xff00ff00.toInt(), cellPixel(view, bitmap, 0.25f, 0.5f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.75f, 0.5f))
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun imagesClipNegativeOriginsAndDoNotPaintOutsideTheViewport() = withGraphicsView { view ->
        val resource = rgbaResource(1, 255, 0, 0)
        val draw = graphicsImage(columns = 3, rows = 2).copy(column = -1, row = -1,
            clip = TerminalRenderClipV5(2, 1, 3, 2))
        val bitmap = renderGraphics(view, graphicsFrame(graphicsText(), listOf(resource), listOf(draw)))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 1.5f, 0.5f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 1.5f, 1.25f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, -0.25f, 0.5f))
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun alphaAtlasUsesTextColorAndNeverPaintsTheFallbackCharacter() = withGraphicsView { view ->
        val resource = TerminalGraphicsResource(1, 2, 2, TerminalGraphicsFormat.A8, byteArrayOf(-1, 0, -128, -1))
        val glyph = TerminalRenderGlyphV5(0, 0, 1, 0, 2, 2)
        val rendition = testRendition().copy(foreground = TerminalRenderColorV1.Rgb(0xff0000), blink = true)
        val frame = graphicsFrame(graphicsText(fallback = "\ufffd", renditions = listOf(rendition)), listOf(resource), glyphs = listOf(glyph))
        var bitmap = renderGraphics(view, frame)
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 0.25f, 0.25f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.75f, 0.25f))
        assertEquals(0xff800000.toInt(), cellPixel(view, bitmap, 0.25f, 0.75f))
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 0.75f, 0.75f))
        val noFallback = renderGraphics(view, graphicsFrame(graphicsText(renditions = listOf(rendition)), listOf(resource), glyphs = listOf(glyph)))
        val cell = view.cellGeometryForTest()
        for (y in cell.top.toInt() + 1 until cell.bottom.toInt()) for (x in cell.left.toInt() + 1 until cell.right.toInt()) {
            assertEquals("DRCS must replace U+FFFD at $x,$y", noFallback.getPixel(x, y), bitmap.getPixel(x, y))
        }
        bitmap = renderGraphics(view, graphicsFrame(graphicsText(renditions = listOf(rendition.copy(conceal = true))),
            listOf(resource), glyphs = listOf(glyph)))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.25f, 0.25f))
        bitmap = renderGraphics(view, graphicsFrame(graphicsText(renditions = listOf(rendition.copy(inverse = true,
            background = TerminalRenderColorV1.Rgb(0x0000ff)))), listOf(resource), glyphs = listOf(glyph)))
        assertEquals(0xff0000ff.toInt(), cellPixel(view, bitmap, 0.25f, 0.25f))
        assertEquals(0xffff0000.toInt(), cellPixel(view, bitmap, 0.75f, 0.25f))
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun drcsDoubleHeightHalvesShareTheAtlasAndRespectRowClipping() = withGraphicsView { view ->
        val resource = TerminalGraphicsResource(1, 2, 2, TerminalGraphicsFormat.A8, byteArrayOf(-1, 0, 0, -1))
        val text = graphicsText(rowStyles = listOf(TerminalLineRendition.DOUBLE_HEIGHT_TOP, TerminalLineRendition.DOUBLE_HEIGHT_BOTTOM))
        val glyphs = listOf(TerminalRenderGlyphV5(0, 0, 1, 0, 2, 2), TerminalRenderGlyphV5(0, 1, 1, 0, 2, 2))
        val bitmap = renderGraphics(view, graphicsFrame(text, listOf(resource), glyphs = glyphs))
        assertEquals(0xffffffff.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 1.5f, 0.5f))
        assertEquals(0xff000000.toInt(), cellPixel(view, bitmap, 0.5f, 1.5f))
        assertEquals(0xffffffff.toInt(), cellPixel(view, bitmap, 1.5f, 1.5f))
        assertEquals(1, view.graphicsCacheForTest().first)
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun bitmapCacheReusesPixelsAcrossIndependentResourceDecodes() = withGraphicsView { view ->
        val first = decodedRgbaResource(255, 0, 0)
        val second = decodedRgbaResource(255, 0, 0)
        org.junit.Assert.assertNotSame(first, second)
        renderGraphics(view, graphicsFrame(graphicsText(), listOf(first), listOf(graphicsImage())))
        val originalBitmap = requireNotNull(view.graphicsBitmapForTest(1))
        val nextFrame = graphicsFrame(graphicsText(), listOf(second), listOf(graphicsImage()))
        assertEquals(0xffff0000.toInt(), cellPixel(view, renderGraphics(view, nextFrame), 0.5f, 0.5f))
        org.junit.Assert.assertSame(originalBitmap, view.graphicsBitmapForTest(1))
        renderGraphics(view, nextFrame)
        org.junit.Assert.assertSame(originalBitmap, view.graphicsBitmapForTest(1))
        assertEquals(1 to 4L, view.graphicsCacheForTest())
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun bitmapCacheEvictsReusedIdsAndReleasesReferencesWhenFrameOrSessionEnds() = withGraphicsView { view ->
        val oldResource = decodedRgbaResource(255, 0, 0)
        val oldFrame = graphicsFrame(graphicsText(), listOf(oldResource), listOf(graphicsImage()))
        renderGraphics(view, oldFrame)
        val originalBitmap = requireNotNull(view.graphicsBitmapForTest(1))
        assertEquals(1, view.graphicsCacheForTest().first)
        val newer = graphicsFrame(graphicsText(), listOf(decodedRgbaResource(0, 0, 255)), listOf(graphicsImage()))
        assertEquals(0xff0000ff.toInt(), cellPixel(view, renderGraphics(view, newer), 0.5f, 0.5f))
        org.junit.Assert.assertNotSame(originalBitmap, view.graphicsBitmapForTest(1))
        assertEquals(1, view.graphicsCacheForTest().first)
        assertEquals(4L, view.graphicsCacheForTest().second)
        assertEquals(0xffff0000.toInt(), cellPixel(view, renderGraphics(view, oldFrame), 0.5f, 0.5f))
        org.junit.Assert.assertArrayEquals(byteArrayOf(-1, 0, 0, -1), oldResource.copyPixels())
        view.drawFrame(Canvas(Bitmap.createBitmap(400, 300, Bitmap.Config.ARGB_8888)), graphicsText(), testTheme())
        assertEquals(0 to 0L, view.graphicsCacheForTest())
        renderGraphics(view, oldFrame)
        view.detachEngine()
        assertEquals(0 to 0L, view.graphicsCacheForTest())
    }

    @Test
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun cursorRemainsAbovePositiveZImages() = withGraphicsView { view ->
        val frame = graphicsFrame(graphicsText(cursorVisible = true), listOf(rgbaResource(1, 0, 0, 255)), listOf(graphicsImage()))
        val bitmap = renderGraphics(view, frame)
        assertEquals(0xff7070ff.toInt(), cellPixel(view, bitmap, 0.5f, 0.5f))
    }

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
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Config(sdk = [35])
    fun reusedImeSizedViewportShowsImageAboveReturnedCursor() = runBlocking {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val size = TerminalSize(80, 24)
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = cursorFrame(size, cursorRow = 23, nextSourceSequence = 1)
        }
        val engine = NativeTerminalEngine(initialSize = size, bridge = bridge)
        try {
            val view = engine.createView(controller.get()) as NativeTerminalView
            controller.get().setContentView(view)
            view.layout(0, 0, 400, 120)
            assertTrue("the IME sized surface begins at the live cursor", view.viewportYForTest() > 0f)

            bridge.forcedRenderFrameV3 = cursorFrame(size, cursorRow = 2, nextSourceSequence = 2)
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))
            shadowOf(Looper.getMainLooper()).idle()

            val firstCell = view.cellGeometryForTest()
            assertTrue("following the returned cursor must retain the image rows above it", firstCell.top >= 0f)
            assertTrue(view.isCursorFullyVisibleForTest())
            val magenta = rgbaResource(1, 255, 0, 255)
            val frame = graphicsFrame(
                graphicsText(rowStyles = List(3) { TerminalLineRendition.SINGLE_WIDTH }),
                listOf(magenta),
                listOf(graphicsImage(resource = 1, columns = 1, rows = 2)),
            )
            val bitmap = renderGraphics(view, frame)
            assertEquals(0xffff00ff.toInt(), bitmap.getPixel(
                (firstCell.left + firstCell.width() / 2f).toInt(),
                (firstCell.top + firstCell.height() / 2f).toInt(),
            ))
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

    private fun withGraphicsView(block: (NativeTerminalView) -> Unit) {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val view = NativeTerminalView(controller.get(), null, 20f, 1f)
        try {
            view.layout(0, 0, 400, 300)
            block(view)
        } finally {
            view.detachEngine()
            controller.destroy()
        }
    }

    private fun renderGraphics(view: NativeTerminalView, frame: TerminalRenderFrameV5, theme: TerminalThemeV1 = testTheme()): Bitmap =
        Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888).also {
            view.drawFrame(Canvas(it), frame, theme)
        }

    private fun cellPixel(view: NativeTerminalView, bitmap: Bitmap, column: Float, row: Float): Int {
        val cell = view.cellGeometryForTest()
        return bitmap.getPixel((cell.left + cell.width() * column).toInt(), (cell.top + cell.height() * row).toInt())
    }

    private fun rgbaResource(id: Long, red: Int, green: Int, blue: Int, alpha: Int = 255) =
        TerminalGraphicsResource(id, 1, 1, TerminalGraphicsFormat.RGBA8888,
            byteArrayOf(red.toByte(), green.toByte(), blue.toByte(), alpha.toByte()))

    private fun decodedRgbaResource(red: Int, green: Int, blue: Int): TerminalGraphicsResource {
        val bytes = ByteArray(84)
        "TLGRS1\u0000\u0000".toByteArray(Charsets.US_ASCII).copyInto(bytes)
        graphicsPut16(bytes, 8, 1); graphicsPut16(bytes, 10, 48); graphicsPut32(bytes, 12, bytes.size)
        graphicsPut64(bytes, 16, 2); graphicsPut32(bytes, 24, 1); graphicsPut64(bytes, 32, 4)
        graphicsPut64(bytes, 48, 1); graphicsPut32(bytes, 56, 1); graphicsPut32(bytes, 60, 1)
        bytes[64] = 1
        graphicsPut32(bytes, 68, 4)
        byteArrayOf(red.toByte(), green.toByte(), blue.toByte(), -1).copyInto(bytes, 80)
        graphicsPut32(bytes, 72, java.util.zip.CRC32C().apply { update(bytes, 80, 4) }.value.toInt())
        graphicsSeal(bytes, 40)
        return requireNotNull(GraphicsResourcesDecoder.decode(bytes)[1])
    }

    private fun graphicsImage(resource: Long = 1, z: Int = 0, columns: Int = 1, rows: Int = 1) =
        TerminalRenderImageV5(resource, resource, 0, 0, z, resource, TerminalRenderSourceV5(0, 0, 1, 1),
            TerminalRenderRatioV5(0, 1), TerminalRenderRatioV5(0, 1), TerminalRenderRatioV5(columns.toLong(), 1),
            TerminalRenderRatioV5(rows.toLong(), 1), columns, rows, TerminalRenderImageProtocolV5.KITTY,
            TerminalRenderClipV5(0, 0, columns, rows))

    private fun graphicsFrame(text: TerminalRenderFrameV4, resources: List<TerminalGraphicsResource>,
        images: List<TerminalRenderImageV5> = emptyList(), glyphs: List<TerminalRenderGlyphV5> = emptyList()) =
        TerminalRenderFrameV5(text, TerminalGraphicsResources((resources.maxOfOrNull { it.id } ?: 0) + 1, resources.sortedBy { it.id }),
            images.sorted(), glyphs.sorted())

    private fun graphicsText(fallback: String = " ", renditions: List<TerminalRenderRenditionV3> = listOf(testRendition()),
        renditionIds: List<Int> = listOf(0, 0), rowStyles: List<TerminalLineRendition> = listOf(TerminalLineRendition.SINGLE_WIDTH),
        cursorVisible: Boolean = false): TerminalRenderFrameV4 = TerminalRenderFrameV4(
        TerminalWidthProfile.UNICODE_16, 1, 1, TerminalSize(2, rowStyles.size), TerminalRenderScreenV1.PRIMARY, false,
        TerminalRenderCursorV2(0, 0, cursorVisible, false, TerminalRenderCursorShapeV2.BLOCK, false),
        listOf(TerminalRenderGraphemeV1(fallback, 1)), renditions,
        TerminalRenderPaletteV1(List(256) { null }, null, null, null), emptyList(),
        rowStyles.map { style -> TerminalRenderRowV1(style.columns(2), false,
            List(2) { TerminalRenderCellV1(1, renditionIds[it], false, false, 0) }, style) }, "",
    )

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

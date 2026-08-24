package dev.termlink.terminal.termux

import android.content.Context
import android.content.ClipData
import android.content.ClipboardManager
import android.view.KeyEvent
import android.view.View
import android.widget.Scroller
import androidx.test.core.app.ApplicationProvider
import com.termux.view.TerminalView
import com.termux.view.TerminalSessionPort
import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import java.lang.ref.WeakReference
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class TermuxTerminalViewportTest {
    @Test
    fun acceptedInputCancelsFlingClearsRemaindersAndRestoresBothAxes() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(240, 20))
        val view = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(view, width = 120, height = 120)
        engine.feed(1, bytes((1..40).joinToString("") { "line-$it\r\n" }))
        view.restorePresentationState(intArrayOf(-4, 30, 0, 0))
        val scroller = startFling(view)
        setFloatField(view, "mScrollRemainder", 7.5f)
        setFloatField(view, "mHorizontalScrollRemainder", 3.25f)

        engine.setReadOnly(false)
        assertTrue(engine.submitText("x"))

        assertTrue(scroller.isFinished)
        assertEquals(0f, floatField(view, "mScrollRemainder"))
        assertEquals(0f, floatField(view, "mHorizontalScrollRemainder"))
        assertTrue(view.isFollowingOutput)
        assertEquals(1, view.capturePresentationState()[3])
        engine.dispose()
    }

    @Test
    fun rejectedTerminalViewInputDoesNotUnlockBrowseState() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(240, 20))
        val view = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(view, width = 120, height = 120)
        engine.feed(1, bytes((1..30).joinToString("") { "row-$it\r\n" }))
        view.restorePresentationState(intArrayOf(-3, 30, 0, 0))

        assertTrue(view.handleKeyCode(KeyEvent.KEYCODE_ENTER, 0))

        assertEquals(-3, view.topRow)
        assertEquals(30, view.leftColumn)
        assertEquals(listOf(0, 0), view.capturePresentationState().takeLast(2))
        engine.dispose()
    }

    @Test
    fun acceptedInputListenerIgnoresRejectsEmptyInputAndAutomaticCsiReplies() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(132, 43))
        val view = engine.createView(context, textSizeSp = 30f) as TerminalView
        layout(view, width = 360, height = 180)
        val notifications = AtomicInteger()
        engine.setOnAcceptedLocalUserInputListener { notifications.incrementAndGet() }

        assertFalse(engine.submitText("rejected"))
        assertEquals(0, notifications.get())
        engine.setReadOnly(false)
        assertTrue(engine.submitText(""))
        assertEquals(0, notifications.get())
        assertTrue(engine.submitText("x"))
        assertEquals(1, notifications.get())
        assertEquals("x", engine.inputEvents.take(1).toList().single().decodeToString())

        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("test", "paste"))
        val sessionPort = TermuxTerminalEngine::class.java.getDeclaredField("sessionPort").run {
            isAccessible = true
            get(engine) as TerminalSessionPort
        }
        sessionPort.onPasteTextFromClipboard()
        assertEquals(2, notifications.get())
        assertEquals("paste", engine.inputEvents.take(1).toList().single().decodeToString())

        val response = async(start = CoroutineStart.UNDISPATCHED) {
            engine.inputEvents.take(1).toList().single()
        }
        engine.feed(1, bytes("\u001b[16t"))
        val expected = "\u001b[6;${view.mRenderer.fontLineSpacing};${view.mRenderer.fontWidth.toInt()}t"
        assertEquals(expected, response.await().decodeToString())
        assertEquals(2, notifications.get())
        assertEquals(TerminalSize(132, 43), engine.currentSize())

        engine.setOnAcceptedLocalUserInputListener(null)
        assertTrue(engine.submitText("listener-cleared"))
        assertEquals(2, notifications.get())
        engine.dispose()
    }

    @Test
    fun interactionModeChangesCancelFlingAndAtomicallyExitTwoAxisBrowsing() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(240, 20))
        val view = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(view, width = 120, height = 120)
        engine.feed(1, bytes((1..35).joinToString("") { "row-$it\r\n" }))

        suspend fun transition(sequence: Long, escape: String) {
            view.restorePresentationState(intArrayOf(-3, 30, 0, 0))
            val scroller = startFling(view)
            setFloatField(view, "mScrollRemainder", 2.5f)
            engine.feed(sequence, bytes(escape))
            assertTrue(scroller.isFinished)
            assertEquals(0f, floatField(view, "mScrollRemainder"))
            assertEquals(listOf(1, 1), view.capturePresentationState().takeLast(2))
        }

        transition(2, "\u001b[?1049h")
        transition(3, "\u001b[?1000h")
        transition(4, "\u001b[?1000l\u001b[?1049l")
        engine.dispose()
    }

    @Test
    fun verticalBrowseLockFreezesHorizontalViewportUntilInteractiveTailResume() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(240, 20))
        val view = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(view, width = 120, height = 120)
        engine.feed(1, bytes((1..30).joinToString("") { "row-$it\r\n" }))
        view.restorePresentationState(intArrayOf(-3, 30, 0, 1))
        assertEquals(listOf(0, 0), view.capturePresentationState().takeLast(2))

        engine.feed(2, bytes("\u001b[1;220Hnew-output"))
        assertEquals(30, view.leftColumn)
        assertFalse(view.isFollowingOutput)

        engine.feed(3, bytes("\u001b[?1000h"))
        assertTrue(view.isFollowingOutput)
        assertEquals(1, view.capturePresentationState()[3])
        assertTrue(view.leftColumn > 30)
        engine.dispose()
    }

    @Test
    fun browsingHistoryKeepsItsAnchorAcrossOutputThemeAndHeightChanges() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(80, 20))
        val view = engine.createView(context, textSizeSp = 16f) as TerminalView
        layout(view, width = 720, height = 120)
        engine.feed(1, bytes((1..40).joinToString("") { "line-$it\r\n" }))
        assertTrue(view.mEmulator.screen.activeTranscriptRows > 5)

        view.setTopRow(-5)
        assertFalse(view.isFollowingOutput)
        engine.feed(2, bytes("\u001b]0;theme-only\u0007"))
        assertEquals(-5, view.topRow)

        engine.feed(3, bytes("new-output\r\n"))
        val anchoredAfterOutput = view.topRow
        assertTrue(anchoredAfterOutput < -5)
        assertFalse(view.isFollowingOutput)

        val rowsBeforeIme = view.visibleRows
        layout(view, width = 720, height = 72)
        assertTrue(view.visibleRows < rowsBeforeIme)
        assertEquals(anchoredAfterOutput, view.topRow)
        assertFalse(view.isFollowingOutput)

        assertFalse(engine.submitText("blocked"))
        assertEquals(anchoredAfterOutput, view.topRow)
        assertFalse(view.isFollowingOutput)

        engine.setReadOnly(false)
        assertTrue(engine.submitText("x"))
        assertTrue(view.isFollowingOutput)
        assertTrue(view.mEmulator.cursorRow in view.topRow until (view.topRow + view.visibleRows))
        engine.dispose()
    }

    @Test
    fun imeStyleHeightReductionChangesRowsButNotFontOrRemoteGrid() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(100, 40))
        val view = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(view, width = 720, height = 360)
        engine.feed(1, bytes((1..50).joinToString("") { "tail-$it\r\n" }))
        view.scrollToLatest()
        val fontBefore = view.textSizePixels
        val rowsBefore = view.visibleRows
        val topBefore = view.topRow

        layout(view, width = 720, height = 96)

        assertEquals(fontBefore, view.textSizePixels)
        assertTrue(view.visibleRows < rowsBefore)
        assertTrue(view.topRow >= topBefore)
        assertTrue(view.mEmulator.cursorRow in view.topRow until (view.topRow + view.visibleRows))
        assertEquals(TerminalSize(100, 40), engine.authoritativeSize.value)
        assertEquals(TerminalSize(100, 40), engine.currentSize())
        engine.dispose()
    }

    @Test
    fun presentationStateSurvivesAComposeStyleViewRemount() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = TermuxTerminalEngine(TerminalSize(240, 24))
        val first = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(first, width = 120, height = 120)
        engine.feed(1, bytes((1..35).joinToString("") { "row-$it\r\n" }))
        first.restorePresentationState(intArrayOf(-4, 30, 0, 0))
        assertEquals(-4, first.topRow)
        assertEquals(30, first.leftColumn)
        val anchor = first.topVisibleLineId

        // Simulate AndroidView release/GC: the engine has no surviving View reference, so remount
        // must use its own persistent presentation snapshot and stable line ID.
        TermuxTerminalEngine::class.java.getDeclaredField("viewReference").apply {
            isAccessible = true
            set(engine, WeakReference<TerminalView>(null))
        }
        engine.feed(2, bytes("detached-output\r\n"))

        val replacement = engine.createView(context, textSizeSp = 18f) as TerminalView
        layout(replacement, width = 120, height = 72)

        if (anchor > 0) assertEquals(anchor, replacement.topVisibleLineId)
        else assertEquals(-5, replacement.topRow)
        assertEquals(30, replacement.leftColumn)
        assertFalse(replacement.isFollowingOutput)
        assertEquals(TerminalSize(240, 24), engine.authoritativeSize.value)
        engine.dispose()
    }

    private fun layout(view: View, width: Int, height: Int) {
        view.measure(
            View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY),
        )
        view.layout(0, 0, width, height)
        (view as TerminalView).updateSize()
    }

    private fun startFling(view: TerminalView): Scroller {
        val scroller = TerminalView::class.java.getDeclaredField("mScroller").run {
            isAccessible = true
            get(view) as Scroller
        }
        scroller.fling(0, view.topRow, 0, 4_000, 0, 0, -100, 100)
        assertFalse(scroller.isFinished)
        return scroller
    }

    private fun setFloatField(view: TerminalView, name: String, value: Float) {
        TerminalView::class.java.getDeclaredField(name).apply {
            isAccessible = true
            setFloat(view, value)
        }
    }

    private fun floatField(view: TerminalView, name: String): Float =
        TerminalView::class.java.getDeclaredField(name).run {
            isAccessible = true
            getFloat(view)
        }

    private fun bytes(value: String): ByteBuffer = ByteBuffer.wrap(value.encodeToByteArray())
}

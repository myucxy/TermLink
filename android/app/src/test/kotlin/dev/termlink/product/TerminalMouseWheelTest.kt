package dev.termlink.product

import android.os.Looper
import android.view.MotionEvent
import android.view.View
import androidx.activity.ComponentActivity
import dev.termlink.terminal.api.TerminalMouseWheelDirection
import java.time.Duration
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class TerminalMouseWheelTest {
    @Test
    fun deadZoneAndRepeatRateAreSymmetricAndBounded() {
        listOf(0f, 8f, -8f, Float.NaN, Float.POSITIVE_INFINITY).forEach { assertNull(wheelRepeatMillis(it)) }
        assertTrue(wheelRepeatMillis(9f)!! > wheelRepeatMillis(48f)!!)
        assertEquals(wheelRepeatMillis(48f), wheelRepeatMillis(-48f))
        assertEquals(50L, wheelRepeatMillis(96f))
        assertEquals(50L, wheelRepeatMillis(10000f))
    }

    @Test
    fun tapsDragReversePauseAndEveryInterruptionStopWithoutBacklog() {
        val controller = Robolectric.buildActivity(ComponentActivity::class.java).setup().visible()
        val activity = controller.get()
        val wheel = TerminalMouseWheelView(activity)
        val reports = mutableListOf<TerminalMouseWheelDirection>()
        var accepting = true
        wheel.sendWheel = { if (accepting) { reports += it; true } else false }
        activity.setContentView(wheel)
        controller.windowFocusChanged(true)
        val handle = wheel.getChildAt(1)
        val density = activity.resources.displayMetrics.density
        fun tick(ms: Long = 1000) = shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(ms))
        fun event(action: Int, offsetDp: Float = 0f) {
            val event = MotionEvent.obtain(0, 0, action, 24f * density, (150f + offsetDp) * density, 0)
            try { handle.dispatchTouchEvent(event) } finally { event.recycle() }
        }
        fun drag() { event(MotionEvent.ACTION_DOWN); event(MotionEvent.ACTION_MOVE, -96f) }
        fun noMore() { val count = reports.size; tick(); assertEquals(count, reports.size) }
        try {
            wheel.getChildAt(0).performClick()
            wheel.getChildAt(2).performClick()
            assertEquals(listOf(TerminalMouseWheelDirection.UP, TerminalMouseWheelDirection.DOWN), reports)
            noMore()
            drag(); tick()
            assertEquals(22, reports.size) // Exactly 20 detents per second, plus the two taps.
            event(MotionEvent.ACTION_MOVE, 96f); tick(50)
            assertEquals(TerminalMouseWheelDirection.DOWN, reports.last())
            event(MotionEvent.ACTION_MOVE, 0f); noMore() // Paused, but the same drag can resume.
            event(MotionEvent.ACTION_MOVE, -96f); tick(50)
            assertEquals(TerminalMouseWheelDirection.UP, reports.last())
            event(MotionEvent.ACTION_UP); assertEquals(0f, handle.translationY, 0f); noMore()
            drag(); event(MotionEvent.ACTION_CANCEL); noMore()
            drag()
            val pointers = Array(2) { MotionEvent.PointerProperties().apply { id = it; toolType = MotionEvent.TOOL_TYPE_FINGER } }
            val coordinates = Array(2) { MotionEvent.PointerCoords().apply { x = 24f; y = 72f } }
            val secondFinger = MotionEvent.obtain(0, 1,
                MotionEvent.ACTION_POINTER_DOWN or (1 shl MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                2, pointers, coordinates, 0, 0, 1f, 1f, 0, 0, 0, 0)
            try { wheel.dispatchTouchEvent(secondFinger) } finally { secondFinger.recycle() }
            noMore()
            drag(); wheel.onWindowFocusChanged(false); noMore()
            wheel.onWindowFocusChanged(true)
            drag(); wheel.visibility = View.GONE; noMore(); wheel.visibility = View.VISIBLE
            drag(); accepting = false; noMore(); accepting = true; noMore() // Rejection latches stopped.
            drag(); controller.pause(); noMore(); controller.resume(); noMore()
            drag(); activity.setContentView(View(activity)); noMore()
        } finally { controller.pause().stop().destroy() }
    }
}

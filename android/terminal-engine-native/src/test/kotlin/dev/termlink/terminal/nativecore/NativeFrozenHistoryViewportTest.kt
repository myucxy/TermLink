package dev.termlink.terminal.nativecore

import android.app.Activity
import android.os.Looper
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import dev.termlink.terminal.api.TerminalRenderFrameV5
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.math.abs

@RunWith(RobolectricTestRunner::class)
class NativeFrozenHistoryViewportTest {
    @Test
    fun prependsFrozenV5FramesWithoutCreatingAnInputSurface() {
        val activity = Robolectric.buildActivity(Activity::class.java).setup().get()
        val viewport = NativeFrozenHistoryViewport()
        val newest = page(lineId = 20, requestedAnchor = -1, hasMore = true, rows = 32)
        viewport.replacePages(listOf(newest))
        val root = viewport.createView(activity, onScrollPastTop = {}, onScrollPastBottom = {}) as HorizontalScrollView
        activity.setContentView(root, ViewGroup.LayoutParams(320, 20))
        root.measure(View.MeasureSpec.makeMeasureSpec(320, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(20, View.MeasureSpec.EXACTLY))
        root.layout(0, 0, 320, 20)
        shadowOf(Looper.getMainLooper()).idle()
        val vertical = root.getChildAt(0) as ScrollView
        val content = vertical.getChildAt(0) as LinearLayout
        assertEquals(32, content.childCount)
        assertTrue(vertical.scrollY > 0,
            "the newest rows should start at the bottom (viewport=${vertical.height}, content=${content.height}, frame=${content.getChildAt(0).height})")
        val beforePrepend = vertical.scrollY
        val renderer = content.getChildAt(0) as NativeTerminalView
        assertNull(renderer.onCreateInputConnection(EditorInfo()))
        assertFalse(renderer.isFocusable)

        viewport.replacePages(listOf(page(lineId = 10, requestedAnchor = 20, hasMore = false), newest))
        root.measure(View.MeasureSpec.makeMeasureSpec(320, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(20, View.MeasureSpec.EXACTLY))
        root.layout(0, 0, 320, 20)
        shadowOf(Looper.getMainLooper()).idle()
        assertEquals(33, content.childCount)
        val prependedHeight = content.getChildAt(0).height
        assertTrue(abs((vertical.scrollY - beforePrepend) - prependedHeight) <= 1,
            "prepending a page must preserve the visible line and row")
        assertNull((content.getChildAt(0) as NativeTerminalView).onCreateInputConnection(EditorInfo()))
        viewport.close()
        assertEquals(0, content.childCount)
    }

    @Test
    fun badAnchorOrDuplicateLineCannotReplaceVisiblePages() {
        val activity = Robolectric.buildActivity(Activity::class.java).setup().get()
        val viewport = NativeFrozenHistoryViewport()
        val newest = page(lineId = 20, requestedAnchor = -1, hasMore = true)
        viewport.replacePages(listOf(newest))
        val root = viewport.createView(activity, onScrollPastTop = {}, onScrollPastBottom = {}) as HorizontalScrollView
        activity.setContentView(root, ViewGroup.LayoutParams(320, 100))
        val content = (root.getChildAt(0) as ScrollView).getChildAt(0) as LinearLayout
        assertFailsWith<IllegalArgumentException> {
            viewport.replacePages(listOf(page(lineId = 10, requestedAnchor = 99, hasMore = false), newest))
        }
        assertFailsWith<IllegalArgumentException> {
            viewport.replacePages(listOf(page(lineId = 20, requestedAnchor = 20, hasMore = false), newest))
        }
        assertEquals(1, content.childCount)
        viewport.close()
    }

    private fun page(lineId: Long, requestedAnchor: Long, hasMore: Boolean, rows: Int = 1): NativeHistoryPage {
        val frame = RenderFrameV5Decoder.decode(graphicsFrame())
        return NativeHistoryPage(7, requestedAnchor, lineId, hasMore, false, 2, 2 * rows, 0,
            listOf(NativeHistoryLine(lineId, 7, 0, rows.toLong(), 0, false)),
            List<TerminalRenderFrameV5>(rows) { frame })
    }
}

package dev.termlink.terminal.nativecore

import android.content.Context
import android.os.Looper
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalThemeV1
import java.lang.ref.WeakReference
import kotlin.math.abs
import kotlin.math.floor

/** Detached, read-only History V2 presentation. It never owns a parser, input stream or ACK. */
class NativeFrozenHistoryViewport : AutoCloseable {
    private var pages: List<NativeHistoryPage> = emptyList()
    private var theme = NativeTerminalEngine.DEFAULT_THEME
    private var viewReference: WeakReference<FrozenScrollView>? = null
    private var disposed = false

    fun replacePages(replacement: List<NativeHistoryPage>) {
        checkMainThread()
        validate(replacement)
        val view = viewReference?.get()
        val anchor = view?.visibleAnchor(pages)
        pages = replacement.toList()
        view?.replaceFrames(pages, theme, anchor)
    }

    fun applyTheme(replacement: TerminalThemeV1) {
        checkMainThread()
        theme = replacement
        viewReference?.get()?.frameViews?.forEach { it.updateFrozenTheme(replacement) }
    }

    fun createView(
        context: Context,
        textSizeSp: Float = 14f,
        lineSpacingMultiplier: Float = 1f,
        onScrollPastTop: () -> Unit,
        onScrollPastBottom: () -> Unit,
    ): View {
        checkMainThread()
        viewReference?.get()?.clearFrames()
        return FrozenScrollView(context, textSizeSp, lineSpacingMultiplier, onScrollPastTop, onScrollPastBottom)
            .also {
                viewReference = WeakReference(it)
                it.replaceFrames(pages, theme, null)
            }
    }

    override fun close() {
        if (disposed) return
        checkMainThread()
        viewReference?.get()?.clearFrames()
        viewReference = null
        pages = emptyList()
        disposed = true
    }

    private fun checkMainThread() {
        check(!disposed) { "Frozen history viewport is disposed" }
        require(Looper.myLooper() == Looper.getMainLooper()) { "Frozen history viewport requires the main thread" }
    }

    private fun validate(candidate: List<NativeHistoryPage>) {
        require(candidate.size <= 2) { "Frozen history viewport holds at most two pages" }
        val first = candidate.firstOrNull() ?: return
        val revision = first.revision
        val columns = first.columns
        val frameVersion = candidate.asSequence().flatMap { it.frames.asSequence() }.firstOrNull()?.formatVersion
        require(frameVersion == null || frameVersion == 5) { "Frozen History V2 requires V5 frames" }
        var cells = 0L
        var resourceBytes = 0L
        val lineIds = HashSet<Long>()
        candidate.forEachIndexed { index, page ->
            require(page.revision == revision && page.columns == columns &&
                (index == candidate.lastIndex || page.requestedAnchor == candidate[index + 1].nextBeforeLineId)) {
                "Frozen history pages belong to different captures or anchors"
            }
            require(page.frames.all { it.formatVersion == frameVersion } && page.lines.all { lineIds.add(it.id) }) {
                "Frozen history pages contain mixed frames or duplicate lines"
            }
            cells += page.cells
            resourceBytes += page.frames.sumOf { (it as? TerminalRenderFrameV5)?.resources?.pixelBytes?.toLong() ?: 0L }
        }
        require(cells <= 16_384 && resourceBytes <= 16L * 1024 * 1024) {
            "Frozen history viewport exceeds Android display budget"
        }
    }

    private data class VisibleAnchor(val lineId: Long, val rowWithinLine: Int, val rowFraction: Float)

    private inner class FrozenScrollView(
        context: Context,
        private val textSizeSp: Float,
        private val lineSpacingMultiplier: Float,
        private val onPastTop: () -> Unit,
        private val onPastBottom: () -> Unit,
    ) : HorizontalScrollView(context) {
        private val frames = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        private val vertical = object : ScrollView(context) {
            override fun onScrollChanged(left: Int, top: Int, oldLeft: Int, oldTop: Int) {
                super.onScrollChanged(left, top, oldLeft, oldTop)
                if (top > 0) reportedTop = false
                if (top < (frames.height - height).coerceAtLeast(0)) reportedBottom = false
            }
        }.apply {
            isFillViewport = true
            addView(frames, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT))
        }
        val frameViews = ArrayList<NativeTerminalView>()
        private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
        private var startX = 0f
        private var startY = 0f
        private var reportedTop = false
        private var reportedBottom = false
        private var replacementGeneration = 0

        init {
            isFillViewport = true
            addView(vertical, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.MATCH_PARENT))
        }

        fun clearFrames() {
            replacementGeneration++
            frameViews.forEach { it.detachEngine() }
            frameViews.clear()
            frames.removeAllViews()
        }

        fun replaceFrames(pages: List<NativeHistoryPage>, theme: TerminalThemeV1, anchor: VisibleAnchor?) {
            clearFrames()
            val generation = replacementGeneration
            pages.flatMap { it.frames }.forEach { frame ->
                val child = NativeTerminalView(context, null, textSizeSp, lineSpacingMultiplier,
                    frozenFrame = frame, frozenTheme = theme)
                frameViews += child
                frames.addView(child, LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))
            }
            reportedTop = false
            reportedBottom = false
            frames.addOnLayoutChangeListener(object : View.OnLayoutChangeListener {
                override fun onLayoutChange(
                    view: View, left: Int, top: Int, right: Int, bottom: Int,
                    oldLeft: Int, oldTop: Int, oldRight: Int, oldBottom: Int,
                ) {
                    frames.removeOnLayoutChangeListener(this)
                    if (generation != replacementGeneration) return
                    post {
                        if (generation != replacementGeneration) return@post
                        if (anchor == null) vertical.scrollTo(0, (frames.height - vertical.height).coerceAtLeast(0))
                        else vertical.scrollTo(0, pixelForAnchor(pages, anchor) ?:
                            (frames.height - vertical.height).coerceAtLeast(0))
                    }
                }
            })
        }

        fun visibleAnchor(pages: List<NativeHistoryPage>): VisibleAnchor? {
            if (frameViews.isEmpty()) return null
            var globalRow = 0
            var rowFraction = 0f
            val sourceFrames = pages.flatMap { it.frames }
            for ((index, view) in frameViews.withIndex()) {
                val frameRows = sourceFrames.getOrNull(index)?.size?.rows ?: return null
                if (vertical.scrollY < view.bottom) {
                    val position = (vertical.scrollY - view.top).coerceAtLeast(0) / view.frozenCellHeight()
                    val row = floor(position).toInt().coerceIn(0, frameRows - 1)
                    globalRow += row
                    rowFraction = position - row
                    break
                }
                globalRow += frameRows
            }
            var start = 0
            for (page in pages) {
                for (line in page.lines) {
                    val first = start + line.firstRow.toInt()
                    val end = first + line.rowCount.toInt()
                    if (globalRow in first until end) return VisibleAnchor(line.id, globalRow - first, rowFraction)
                }
                start += page.frames.sumOf { it.size.rows }
            }
            return null
        }

        private fun pixelForAnchor(pages: List<NativeHistoryPage>, anchor: VisibleAnchor): Int? {
            var globalRow = 0
            for (page in pages) {
                val line = page.lines.firstOrNull { it.id == anchor.lineId }
                if (line != null) {
                    globalRow += line.firstRow.toInt() + anchor.rowWithinLine.coerceAtMost(line.rowCount.toInt() - 1)
                    break
                }
                globalRow += page.frames.sumOf { it.size.rows }
            }
            if (pages.none { page -> page.lines.any { it.id == anchor.lineId } }) return null
            val sourceFrames = pages.flatMap { it.frames }
            var rowsBefore = 0
            for (index in frameViews.indices) {
                val view = frameViews[index]
                val rows = sourceFrames[index].size.rows
                if (globalRow < rowsBefore + rows) {
                    return (view.top + (globalRow - rowsBefore + anchor.rowFraction) * view.frozenCellHeight()).toInt()
                }
                rowsBefore += rows
            }
            return null
        }

        override fun dispatchTouchEvent(event: MotionEvent): Boolean {
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { startX = event.x; startY = event.y }
                MotionEvent.ACTION_MOVE -> {
                    val dx = event.x - startX
                    val dy = event.y - startY
                    if (abs(dy) > touchSlop && abs(dy) > abs(dx)) {
                        if (dy > 0 && vertical.scrollY == 0 && !reportedTop) {
                            reportedTop = true
                            onPastTop()
                        } else if (dy < 0 && vertical.scrollY >= (frames.height - vertical.height).coerceAtLeast(0) && !reportedBottom) {
                            reportedBottom = true
                            onPastBottom()
                        }
                    }
                }
            }
            return super.dispatchTouchEvent(event)
        }

    }
}

package dev.termlink.terminal.nativecore

import android.content.Context
import android.os.Looper
import android.view.View
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import java.lang.ref.WeakReference

internal data class NativeTranscriptPresentation(
    val topLineId: Long?,
    val rowOffset: Float = 0f,
    val leftColumn: Float = 0f,
    val atBottom: Boolean = true,
    val allowPartialBottom: Boolean = false,
)

/** Detached history: no native handle, parser, input stream, resize request or ACK authority. */
class NativeTranscriptViewport(
    initialColumns: Int = 80,
    private val initialRows: Int = 24,
) : AutoCloseable {
    internal var projection = NativeTranscriptMapper.map(emptyList(), initialColumns)
        private set
    internal var theme: TerminalThemeV1 = NativeTerminalEngine.DEFAULT_THEME
        private set
    internal var presentation: NativeTranscriptPresentation? = null
        private set
    internal var userTextSizePixels: Float? = null
    private var configuredTextSizeSp: Float? = null
    private var viewReference: WeakReference<NativeTerminalView>? = null
    private var disposed = false

    init {
        require(initialRows in 1..TerminalTranscriptPageV1.MAX_VIEWPORT_ROWS)
    }

    fun replacePages(
        pages: List<TerminalTranscriptPageV1>,
        authoritativeColumns: Int = projection.frame.size.columns,
    ) {
        checkThread()
        val replacement = NativeTranscriptMapper.map(pages, authoritativeColumns)
        val view = viewReference?.get()
        val saved = if (projection.anchor == replacement.anchor) {
            view?.transcriptPresentation() ?: presentation
        } else null
        // Validate and construct the entire immutable projection before publishing any state.
        projection = replacement
        presentation = saved
        view?.updateTranscript(saved)
    }

    fun clear(authoritativeColumns: Int = projection.frame.size.columns) {
        checkThread()
        val replacement = NativeTranscriptMapper.map(emptyList(), authoritativeColumns)
        projection = replacement
        presentation = null
        viewReference?.get()?.updateTranscript(null)
    }

    fun applyTheme(theme: TerminalThemeV1) {
        checkThread()
        if (this.theme == theme) return
        val view = viewReference?.get()
        val saved = view?.transcriptPresentation() ?: presentation
        this.theme = theme
        view?.updateTranscript(saved)
    }

    fun createView(
        context: Context,
        textSizeSp: Float = 14f,
        lineSpacingMultiplier: Float = 1f,
        onScrollPastTop: () -> Unit,
        onScrollPastBottom: () -> Unit,
    ): View {
        checkThread()
        require(Looper.myLooper() == Looper.getMainLooper())
        viewReference?.get()?.let { previous ->
            presentation = previous.transcriptPresentation()
            previous.detachEngine()
        }
        if (configuredTextSizeSp != textSizeSp) userTextSizePixels = null
        configuredTextSizeSp = textSizeSp
        return NativeTerminalView(
            context, engine = null, textSizeSp = textSizeSp,
            lineSpacingMultiplier = lineSpacingMultiplier,
            onScrollPastTop = onScrollPastTop, onScrollPastBottom = onScrollPastBottom,
            transcript = this,
        ).also { viewReference = WeakReference(it) }
    }

    internal fun attachView(view: NativeTerminalView): Boolean =
        !disposed && viewReference?.get() === view

    internal fun savePresentation(view: NativeTerminalView, state: NativeTranscriptPresentation) {
        if (!disposed && viewReference?.get() === view) presentation = state
    }

    fun scrollToLineId(lineId: Long): Boolean {
        checkThread()
        require(lineId > 0)
        val row = projection.lineIds.indexOf(lineId)
        val view = viewReference?.get() ?: return false
        if (row < 0) return false
        view.updateTranscript(view.transcriptPresentation().copy(
            topLineId = lineId, rowOffset = 0f, atBottom = false, allowPartialBottom = true,
        ))
        return true
    }

    fun scrollPastTopBoundary() {
        checkThread()
        viewReference?.get()?.signalScrollPastTop()
    }

    fun debugState(): NativeTranscriptViewportDebugState {
        checkThread()
        val view = viewReference?.get()
        val state = view?.transcriptPresentation() ?: presentation
        val visibleRows = view?.transcriptVisibleRows() ?: initialRows
        val index = if (state?.atBottom != false) {
            (projection.lineIds.size - visibleRows).coerceAtLeast(0)
        } else {
            projection.lineIds.indexOf(state.topLineId).coerceAtLeast(0)
        }
        val frame = projection.frame
        return NativeTranscriptViewportDebugState(
            visibleText = frame.rows.drop(index).take(visibleRows).joinToString("\n") { row ->
                row.cells.take(row.meaningfulEnd).filterNot { it.wideContinuation }.joinToString("") {
                    frame.graphemes[it.graphemeId - 1].text
                }.trimEnd()
            },
            snapshotAnchorSequence = projection.anchor,
            topRow = index,
            firstVisibleLineId = projection.lineIds.getOrNull(index),
            pageCount = projection.pageCount,
            rowCount = projection.lineIds.size,
            columns = frame.size.columns,
            appliedTextSizePixels = view?.transcriptTextSizePixels() ?: 0f,
        )
    }

    override fun close() {
        if (disposed) return
        checkThread()
        viewReference?.get()?.detachEngine()
        viewReference = null
        presentation = null
        projection = NativeTranscriptMapper.map(emptyList(), projection.frame.size.columns)
        disposed = true
    }

    private fun checkThread() {
        check(!disposed) { "Transcript viewport is disposed" }
        require(viewReference?.get() == null || Looper.myLooper() == Looper.getMainLooper()) {
            "An attached transcript viewport must be accessed on the main thread"
        }
    }
}

data class NativeTranscriptViewportDebugState(
    val visibleText: String,
    val snapshotAnchorSequence: Long?,
    val topRow: Int,
    val firstVisibleLineId: Long?,
    val pageCount: Int,
    val rowCount: Int,
    val columns: Int,
    val appliedTextSizePixels: Float,
)

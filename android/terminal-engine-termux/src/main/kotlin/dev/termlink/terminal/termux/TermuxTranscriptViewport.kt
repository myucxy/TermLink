package dev.termlink.terminal.termux

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Paint
import android.graphics.Typeface
import android.os.Looper
import android.util.TypedValue
import android.view.View
import com.termux.terminal.TerminalEmulator
import com.termux.terminal.TerminalOutput
import com.termux.terminal.TextStyle
import com.termux.view.TerminalSessionPort
import com.termux.view.TerminalView
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalTypefaceResolver
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import java.lang.ref.WeakReference
import kotlin.math.roundToInt

/**
 * Independent read-only history surface. It accepts only row projections and deliberately exposes
 * no byte-feed or input API, so browsing cannot mutate the live parser or its ACK cursor.
 */
class TermuxTranscriptViewport(
    initialColumns: Int = DEFAULT_COLUMNS,
    initialRows: Int = DEFAULT_ROWS,
) : AutoCloseable {
    private val lock = Any()
    private var authoritativeColumns = initialColumns.requireSupportedTranscriptColumns()
    private var rows = initialRows.coerceIn(MIN_ROWS, MAX_RENDERED_ROWS)
    private var projection: TermuxTranscriptProjection? = null
    private var pages: List<TerminalTranscriptPageV1> = emptyList()
    private var currentTheme: TerminalThemeV1? = null
    private var viewReference: WeakReference<TerminalView>? = null
    private var contextReference: WeakReference<Context>? = null
    private var presentationState: TermuxPresentationState? = null
    private var viewGeneration = 0L
    private var configuredTextSizePixels = 1
    private var preferredTextSizePixels = 1
    private var userTextSizePixels: Int? = null
    private var appliedTextSizePixels = 1
    private var fittingAttachedView = false
    private var sizeUpdateRequests = 0
    private var disposed = false

    private val terminalOutput = object : TerminalOutput() {
        override fun write(data: ByteArray, offset: Int, count: Int) = Unit
        override fun titleChanged(oldTitle: String?, newTitle: String?) = Unit
        override fun onCopyTextToClipboard(text: String?) = Unit
        override fun onPasteTextFromClipboard() = Unit
        override fun onBell() = Unit
        override fun onColorsChanged() = Unit
    }

    private var emulator = blankEmulator(authoritativeColumns, rows)

    private val sessionPort = object : TerminalSessionPort {
        override fun getEmulator(): TerminalEmulator = synchronized(lock) {
            checkActive()
            this@TermuxTranscriptViewport.emulator
        }

        override fun write(data: String) = Unit

        override fun writeCodePoint(prependEscape: Boolean, codePoint: Int) = Unit

        override fun onCopyTextToClipboard(text: String) {
            val context = synchronized(lock) { contextReference?.get() } ?: return
            val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return
            clipboard.setPrimaryClip(ClipData.newPlainText("Terminal history selection", text))
        }

        override fun onPasteTextFromClipboard() = Unit

        override fun updateSize(
            columns: Int,
            rows: Int,
            cellWidthPixels: Int,
            cellHeightPixels: Int,
        ) {
            synchronized(lock) {
                checkActive()
                sizeUpdateRequests = Math.addExact(sizeUpdateRequests, 1)
            }
            resizeRows(rows)
            fitAttachedViewToAuthoritativeWidth()
        }
    }

    /** Replaces the projection, preserving the first visible stable line ID across a prepend. */
    fun replacePages(
        pages: List<TerminalTranscriptPageV1>,
        authoritativeColumns: Int = this.authoritativeColumns,
    ) {
        val supportedColumns = authoritativeColumns.requireSupportedTranscriptColumns()
        val mapped = TermuxTranscriptMapper.map(pages, supportedColumns)
        val attached = synchronized(lock) {
            checkActive()
            require(viewReference?.get() == null || Looper.myLooper() == Looper.getMainLooper()) {
                "An attached transcript viewport must be updated on the main thread"
            }
            val view = viewReference?.get()
            view to capturePresentationState(view)
        }
        synchronized(lock) {
            checkActive()
            // Construct the replacement before publishing any of its state. If
            // the Termux projection cannot be installed, the caller can surface
            // a stable error while this viewport remains wholly on its last
            // valid renderer instead of retaining new pages behind an old
            // emulator.
            val replacement = projectionEmulator(mapped, rows)
            this.authoritativeColumns = mapped.columns
            this.pages = pages.toList()
            projection = mapped
            emulator = replacement
        }
        attached.first?.let { view ->
            view.replaceEmulatorFromSession()
            restorePresentationState(view, attached.second, defaultToBottom = true)
        }
        fitAttachedViewToAuthoritativeWidth()
    }

    fun clear(authoritativeColumns: Int = this.authoritativeColumns) {
        val supportedColumns = authoritativeColumns.requireSupportedTranscriptColumns()
        val attached = synchronized(lock) {
            checkActive()
            require(viewReference?.get() == null || Looper.myLooper() == Looper.getMainLooper()) {
                "An attached transcript viewport must be cleared on the main thread"
            }
            this.authoritativeColumns = supportedColumns
            pages = emptyList()
            projection = null
            emulator = blankEmulator(supportedColumns, rows).also(::applyThemeTo)
            val view = viewReference?.get()
            view to capturePresentationState(view)
        }
        attached.first?.let { view ->
            view.replaceEmulatorFromSession()
            restorePresentationState(view, attached.second, defaultToBottom = true)
        }
        fitAttachedViewToAuthoritativeWidth()
    }

    fun applyTheme(theme: TerminalThemeV1) {
        var fontChanged = false
        val attachedView = synchronized(lock) {
            checkActive()
            fontChanged = currentTheme?.fontFamilyHint != theme.fontFamilyHint
            currentTheme = theme
            applyThemeTo(emulator)
            viewReference?.get()
        }
        if (fontChanged) {
            attachedView?.setTypeface(TerminalTypefaceResolver.resolve(attachedView.context, theme.fontFamilyHint))
        }
        attachedView?.invalidate()
    }

    fun createView(
        context: Context,
        textSizeSp: Float = DEFAULT_TEXT_SIZE_SP,
        lineSpacingMultiplier: Float = 1f,
        onScrollPastTop: () -> Unit,
        onScrollPastBottom: () -> Unit,
    ): View {
        val mount = synchronized(lock) {
            checkActive()
            val previousView = viewReference?.get()
            if (presentationState == null && previousView != null) {
                presentationState = capturePresentationState(previousView)
            }
            previousView?.setPresentationStateListener(null)
            viewGeneration = Math.addExact(viewGeneration, 1L)
            val saved = presentationState
            saved?.copy(state = saved.state.clone()) to viewGeneration
        }
        val textSizePx = TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_SP,
            textSizeSp,
            context.resources.displayMetrics,
        ).roundToInt().coerceAtLeast(1)
        val boundedLineSpacing = lineSpacingMultiplier.coerceIn(0.8f, 1.8f)
        val fontFamilyHint = synchronized(lock) { currentTheme?.fontFamilyHint }
        return TerminalView(context, null).also { view ->
            view.setTextSize(textSizePx)
            view.setLineSpacing(boundedLineSpacing)
            view.setTypeface(TerminalTypefaceResolver.resolve(context, fontFamilyHint))
            view.setTerminalViewClient(
                TermuxTerminalViewClient(
                    view = view,
                    baseTextSizePx = textSizePx,
                    lineSpacingMultiplier = boundedLineSpacing,
                    minimumReadableTextSizePx = minimumReadableTextSizePixels(context),
                    acceptsInput = false,
                    onTextSizeRequested = ::acceptUserTextSize,
                ),
            )
            view.setScrollBoundaryListener(object : TerminalView.ScrollBoundaryListener {
                override fun onScrollPastTop() = onScrollPastTop()
                override fun onScrollPastBottom() = onScrollPastBottom()
            })
            view.isFocusable = false
            view.isFocusableInTouchMode = false
            synchronized(lock) {
                checkActive()
                if (configuredTextSizePixels != textSizePx) {
                    configuredTextSizePixels = textSizePx
                    preferredTextSizePixels = textSizePx
                    userTextSizePixels = null
                }
                preferredTextSizePixels = textSizePx
                appliedTextSizePixels = textSizePx
                viewReference = WeakReference(view)
                contextReference = WeakReference(context.applicationContext)
            }
            view.attachSession(sessionPort)
            restorePresentationState(view, mount.first, defaultToBottom = true)
            val generation = mount.second
            view.setPresentationStateListener { state, topVisibleLineId ->
                storePresentationState(generation, state, topVisibleLineId)
            }
        }
    }

    /** Structured fields used by tests and debug-only callers. */
    fun debugState(): TermuxTranscriptViewportDebugState = synchronized(lock) {
        checkActive()
        val current = projection
        val view = viewReference?.get()
        TermuxTranscriptViewportDebugState(
            visibleText = visibleText(view?.topRow ?: 0),
            snapshotAnchorSequence = current?.snapshotAnchorSequence,
            topRow = view?.topRow ?: 0,
            firstVisibleLineId = view?.topVisibleLineId?.takeIf { it > 0 }
                ?: current?.lineIds?.getOrNull((current.rows.size - rows).coerceAtLeast(0)),
            pageCount = pages.size,
            rowCount = current?.rows?.size ?: 0,
            columns = emulator.mColumns,
            appliedTextSizePixels = appliedTextSizePixels,
            emulatorRows = emulator.mRows,
            sizeUpdateRequests = sizeUpdateRequests,
        )
    }

    /** Selects an authenticated rendered line without invoking the pagination boundary callback. */
    fun scrollToLineId(lineId: Long): Boolean {
        require(lineId > 0) { "transcript line ID must be positive" }
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        } ?: return false
        require(Looper.myLooper() == Looper.getMainLooper()) {
            "An attached transcript viewport must be scrolled on the main thread"
        }
        return view.scrollToLineId(lineId)
    }

    /** Drives one top-boundary gesture through the attached read-only TerminalView. */
    fun scrollPastTopBoundary() {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        } ?: return
        if (Looper.myLooper() == Looper.getMainLooper()) {
            view.scrollPastTopBoundary()
        } else {
            view.post(view::scrollPastTopBoundary)
        }
    }

    override fun close() {
        synchronized(lock) {
            if (disposed) return
            disposed = true
            pages = emptyList()
            projection = null
            viewReference = null
            contextReference = null
            presentationState = null
        }
    }

    private fun resizeRows(requestedRows: Int) {
        val boundedRows = requestedRows.coerceIn(MIN_ROWS, MAX_RENDERED_ROWS)
        val attached = synchronized(lock) {
            checkActive()
            if (boundedRows == rows) return
            val attachedView = viewReference?.get()
            val saved = capturePresentationState(attachedView)
            rows = boundedRows
            emulator = projection?.let { projectionEmulator(it, rows) }
                ?: blankEmulator(authoritativeColumns, rows).also(::applyThemeTo)
            attachedView to saved
        }
        attached.first?.let { view ->
            view.replaceEmulatorFromSession()
            restorePresentationState(view, attached.second, defaultToBottom = true)
        }
    }

    private fun capturePresentationState(view: TerminalView?): TermuxPresentationState? {
        val captured = if (view == null) {
            presentationState?.let { it.copy(state = it.state.clone()) }
        } else {
            TermuxPresentationState(
                state = view.capturePresentationState(),
                topVisibleLineId = view.topVisibleLineId,
            )
        }
        if (captured != null) presentationState = captured.copy(state = captured.state.clone())
        return captured
    }

    private fun restorePresentationState(
        view: TerminalView,
        saved: TermuxPresentationState?,
        defaultToBottom: Boolean,
    ) {
        if (saved == null) {
            if (defaultToBottom) view.scrollToBottomBoundary()
            return
        }
        view.restorePresentationState(saved.state)
        if (saved.state[2] == 0 && saved.topVisibleLineId > 0) {
            view.scrollToLineId(saved.topVisibleLineId)
        }
    }

    private fun storePresentationState(
        generation: Long,
        state: IntArray,
        topVisibleLineId: Long,
    ) {
        if (state.size != 4) return
        synchronized(lock) {
            if (disposed || generation != viewGeneration) return
            presentationState = TermuxPresentationState(state.clone(), topVisibleLineId)
        }
    }

    /** Fits the PC-owned column grid into the phone width without changing that grid. */
    private fun fitAttachedViewToAuthoritativeWidth() {
        val fit = synchronized(lock) {
            if (fittingAttachedView) return
            val view = viewReference?.get() ?: return
            if (view.width <= 0) return
            val userSize = userTextSizePixels
            val effectivePreferred = userSize ?: preferredTextSizePixels
            val fitted = fittedTranscriptTextSizeForColumnsPx(
                viewWidthPixels = view.width,
                columns = emulator.mColumns,
                preferredTextSizePixels = effectivePreferred,
                minimumReadableTextSizePixels = userSize ?: minimumReadableTextSizePixels(view.context),
                typeface = TerminalTypefaceResolver.resolve(view.context, currentTheme?.fontFamilyHint),
            )
            if (fitted == appliedTextSizePixels) return
            fittingAttachedView = true
            appliedTextSizePixels = fitted
            view to fitted
        }
        try {
            fit.first.setTextSize(fit.second)
            fit.first.invalidate()
        } finally {
            synchronized(lock) { fittingAttachedView = false }
        }
    }

    private fun acceptUserTextSize(requestedPixels: Int) {
        synchronized(lock) {
            checkActive()
            userTextSizePixels = requestedPixels.coerceAtLeast(1)
            appliedTextSizePixels = requestedPixels.coerceAtLeast(1)
        }
    }

    private fun projectionEmulator(
        projection: TermuxTranscriptProjection,
        rows: Int,
    ): TerminalEmulator = TerminalEmulator(
        terminalOutput,
        projection.columns,
        rows,
        1,
        1,
        MAX_BUFFER_ROWS,
        null,
    ).also { candidate ->
        candidate.installReadOnlyTranscriptProjection(
            projection.rows,
            projection.lineIds,
            projection.paletteArgb,
        )
        applyThemeTo(candidate)
    }

    private fun blankEmulator(columns: Int, rows: Int): TerminalEmulator = TerminalEmulator(
        terminalOutput,
        columns,
        rows,
        1,
        1,
        MAX_BUFFER_ROWS,
        null,
    )

    private fun applyThemeTo(target: TerminalEmulator) {
        currentTheme?.let { theme ->
            theme.ansiColors.forEachIndexed { index, color ->
                target.mColors.mCurrentColors[index] = color.value
            }
            target.mColors.mCurrentColors[TextStyle.COLOR_INDEX_FOREGROUND] = theme.foreground.value
            target.mColors.mCurrentColors[TextStyle.COLOR_INDEX_BACKGROUND] = theme.background.value
            target.mColors.mCurrentColors[TextStyle.COLOR_INDEX_CURSOR] = theme.cursor.value
        }
    }

    private fun visibleText(topRow: Int): String {
        val screen = emulator.screen
        val boundedTop = topRow.coerceIn(-screen.activeTranscriptRows, 0)
        val bottom = (boundedTop + emulator.mRows - 1).coerceAtMost(emulator.mRows - 1)
        return screen.getSelectedText(0, boundedTop, emulator.mColumns - 1, bottom, false)
    }

    private fun checkActive() = check(!disposed) { "Transcript viewport is disposed" }

    companion object {
        /**
         * A mobile rolling window. Five maximum-sized protocol pages remain resident, while the
         * authenticated cursor can continue walking toward older output indefinitely.
         */
        const val MAX_RENDERED_ROWS = 1_000

        private const val DEFAULT_COLUMNS = 80
        private const val DEFAULT_ROWS = 24
        private const val MIN_ROWS = 2
        private const val DEFAULT_TEXT_SIZE_SP = 14f
        private const val MAX_BUFFER_ROWS = MAX_RENDERED_ROWS
    }
}

data class TermuxTranscriptViewportDebugState(
    val visibleText: String,
    val snapshotAnchorSequence: Long?,
    val topRow: Int,
    val firstVisibleLineId: Long?,
    val pageCount: Int,
    val rowCount: Int,
    val columns: Int,
    val appliedTextSizePixels: Int,
    val emulatorRows: Int,
    val sizeUpdateRequests: Int,
)

/** Largest preferred-or-smaller monospace font that keeps every PC-owned column visible. */
internal fun fittedTranscriptTextSizeForColumnsPx(
    viewWidthPixels: Int,
    columns: Int,
    preferredTextSizePixels: Int,
    minimumReadableTextSizePixels: Int = 1,
    typeface: Typeface? = Typeface.MONOSPACE,
): Int {
    require(viewWidthPixels > 0) { "view width must be positive" }
    require(columns > 0) { "terminal columns must be positive" }
    require(preferredTextSizePixels > 0) { "preferred text size must be positive" }
    require(minimumReadableTextSizePixels > 0) { "minimum readable text size must be positive" }
    val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.typeface = typeface }
    fun fits(textSize: Int): Boolean {
        paint.textSize = textSize.toFloat()
        val measured = paint.measureText("X")
        val cellWidth = measured.takeIf { it.isFinite() && it > 0f }
            ?: (textSize * FALLBACK_MONOSPACE_WIDTH_RATIO)
        return cellWidth * columns <= viewWidthPixels
    }
    val readableFloor = minimumReadableTextSizePixels.coerceAtMost(preferredTextSizePixels)
    var low = readableFloor
    var high = preferredTextSizePixels
    var best = readableFloor
    while (low <= high) {
        val candidate = (low + high) ushr 1
        if (fits(candidate)) {
            best = candidate
            low = candidate + 1
        } else {
            high = candidate - 1
        }
    }
    return best
}

private fun Int.requireSupportedTranscriptColumns(): Int = also {
    require(it in 1..TerminalTranscriptPageV1.MAX_COLUMNS) {
        "Authoritative transcript width is unsupported"
    }
}

private const val FALLBACK_MONOSPACE_WIDTH_RATIO = 0.62f

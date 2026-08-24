package dev.termlink.terminal.termux

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Paint
import android.graphics.Typeface
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.View
import com.termux.terminal.KeyHandler
import com.termux.terminal.TerminalEmulator
import com.termux.terminal.TerminalOutput
import com.termux.terminal.TextStyle
import com.termux.view.TerminalSessionPort
import com.termux.view.TerminalView
import dev.termlink.terminal.api.CheckpointTerminalEngine
import dev.termlink.terminal.api.InstalledTerminalCheckpoint
import dev.termlink.terminal.api.PreparedTerminalCheckpoint
import dev.termlink.terminal.api.TerminalSessionProjectionSnapshotV1
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalSnapshotV1
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import java.lang.ref.WeakReference
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.coroutines.suspendCoroutine
import kotlin.math.roundToInt

/**
 * TermLink's remote-session adapter for the pinned Termux terminal emulator and view.
 *
 * No Android PTY or local shell is created. Raw transport bytes are appended directly to the
 * emulator and generated terminal input is exposed through [inputEvents].
 */
class TermuxTerminalEngine(
    initialSize: TerminalSize = TerminalSize(DEFAULT_COLUMNS, DEFAULT_ROWS),
    private val transcriptRows: Int = DEFAULT_TRANSCRIPT_ROWS,
) : CheckpointTerminalEngine {
    private val lock = Any()
    private val inputEventChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private val mutableAuthoritativeSize = MutableStateFlow(initialSize)
    private val mutableTitle = MutableStateFlow("")
    private var viewReference: WeakReference<TerminalView>? = null
    private var contextReference: WeakReference<Context>? = null
    private var presentationState: TermuxPresentationState? = null
    private var viewGeneration = 0L
    private var acceptedLocalUserInputListener: (() -> Unit)? = null
    private var acceptedLocalUserInputListenerGeneration = 0L
    private var inputEnqueueRevision = 0L
    private var terminalViewInputStartRevision = 0L
    private var disposed = false
    private var readOnly = true
    private var currentSize = initialSize
    private var cellWidthPixels = 1
    private var cellHeightPixels = 1
    private var currentTheme: TerminalThemeV1? = null
    private var configuredTextSizePixels = 1
    private var preferredTextSizePixels = 1
    private var userTextSizePixels: Int? = null
    private var appliedTextSizePixels = 1
    private var attachedLineSpacingMultiplier = 1f
    private var fittingAttachedView = false
    private var rendererRevision = 0L
    private val checkpointOwner = Any()

    private val terminalOutput = object : TerminalOutput() {
        override fun write(data: ByteArray, offset: Int, count: Int) {
            emitInput(data.copyOfRange(offset, offset + count))
        }

        override fun titleChanged(oldTitle: String?, newTitle: String?) {
            mutableTitle.value = newTitle.orEmpty().take(MAX_TITLE_CHARACTERS)
        }

        override fun onCopyTextToClipboard(text: String?) = Unit

        override fun onPasteTextFromClipboard() = Unit

        override fun onBell() = Unit

        override fun onColorsChanged() = notifyView()
    }

    private var emulator = TerminalEmulator(
        terminalOutput,
        initialSize.columns,
        initialSize.rows,
        cellWidthPixels,
        cellHeightPixels,
        transcriptRows,
        null,
    )

    private val sessionPort = object : TerminalSessionPort {
        override fun getEmulator(): TerminalEmulator = synchronized(lock) {
            checkActive()
            // Qualify the outer field explicitly. Inside TerminalSessionPort,
            // the unqualified Kotlin property resolves back to getEmulator(),
            // causing infinite recursion as soon as TerminalView attaches.
            this@TermuxTerminalEngine.emulator
        }

        override fun write(data: String) {
            emitInput(data.toByteArray(StandardCharsets.UTF_8))
        }

        override fun writeCodePoint(prependEscape: Boolean, codePoint: Int) {
            require(Character.isValidCodePoint(codePoint) &&
                codePoint !in Character.MIN_SURROGATE.code..Character.MAX_SURROGATE.code) {
                "Invalid Unicode code point: $codePoint"
            }
            val encoded = String(Character.toChars(codePoint)).toByteArray(StandardCharsets.UTF_8)
            emitInput(if (prependEscape) byteArrayOf(ESCAPE) + encoded else encoded)
        }

        override fun onCopyTextToClipboard(text: String) {
            val context = synchronized(lock) { contextReference?.get() } ?: return
            val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE)
                as? ClipboardManager ?: return
            clipboard.setPrimaryClip(ClipData.newPlainText("Terminal selection", text))
        }

        override fun onPasteTextFromClipboard() {
            val context = synchronized(lock) { contextReference?.get() } ?: return
            val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE)
                as? ClipboardManager ?: return
            val text = clipboard.primaryClip
                ?.takeIf { it.itemCount > 0 }
                ?.getItemAt(0)
                ?.coerceToText(context)
                ?.toString()
                ?: return
            var accepted = false
            mutateSynchronously {
                val before = inputEnqueueRevision
                emulator.paste(text)
                accepted = inputEnqueueRevision != before
            }
            if (accepted && text.isNotEmpty()) {
                scrollToLatest()
                notifyAcceptedLocalUserInput()
            }
        }

        override fun updateSize(
            columns: Int,
            rows: Int,
            cellWidthPixels: Int,
            cellHeightPixels: Int,
        ) {
            // TerminalView reports how many cells the phone viewport could
            // create. That measurement is presentation-only: the Windows PTY
            // grid remains authoritative and must never be rewritten or sent
            // back as a RESIZE_REQUEST.
            synchronized(lock) {
                checkActive()
                val metricsChanged = this@TermuxTerminalEngine.cellWidthPixels != cellWidthPixels ||
                    this@TermuxTerminalEngine.cellHeightPixels != cellHeightPixels
                this@TermuxTerminalEngine.cellWidthPixels = cellWidthPixels
                this@TermuxTerminalEngine.cellHeightPixels = cellHeightPixels
                // Same-grid resize updates xterm pixel queries without allowing Android to mutate
                // the Windows-owned rows or columns.
                if (metricsChanged) {
                    emulator.resize(
                        currentSize.columns,
                        currentSize.rows,
                        cellWidthPixels,
                        cellHeightPixels,
                    )
                    advanceRendererRevision()
                }
            }
            fitAttachedViewToAuthoritativeGrid()
        }

        override fun isTerminalGridAuthoritative(): Boolean = true
    }

    override val inputEvents: Flow<ByteArray> = inputEventChannel.receiveAsFlow()
    override val sizeEvents: Flow<TerminalSize> = emptyFlow()
    val authoritativeSize: StateFlow<TerminalSize> = mutableAuthoritativeSize.asStateFlow()
    val title: StateFlow<String> = mutableTitle.asStateFlow()

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        require(seq > 0) { "Terminal sequence must be positive" }
        val chunk = ByteArray(bytes.remaining())
        bytes.get(chunk)
        mutateOnViewThread {
            emulator.append(chunk, chunk.size)
        }
    }

    override suspend fun prepareSnapshot(snapshot: TerminalSnapshotV1): PreparedTerminalCheckpoint {
        val captured = synchronized(lock) {
            checkActive()
            PreparationContext(rendererRevision, cellWidthPixels, cellHeightPixels)
        }
        val restoredState = TermuxSnapshotMapper.map(snapshot)
        val candidate = TerminalEmulator(
            terminalOutput,
            snapshot.size.columns,
            snapshot.size.rows,
            captured.cellWidthPixels,
            captured.cellHeightPixels,
            transcriptRows,
            null,
        )
        candidate.restoreState(restoredState)
        return TermuxPreparedCheckpoint(
            owner = checkpointOwner,
            preparedAtRevision = captured.rendererRevision,
            sequence = snapshot.sequence,
            size = snapshot.size,
            projection = snapshot.projection,
            emulator = candidate,
        )
    }

    override suspend fun installPrepared(
        checkpoint: PreparedTerminalCheckpoint,
    ): InstalledTerminalCheckpoint {
        val prepared = checkpoint as? TermuxPreparedCheckpoint
            ?: throw IllegalArgumentException("Checkpoint was not prepared by the Termux engine")
        require(prepared.owner === checkpointOwner) {
            "Checkpoint was prepared by a different Termux engine"
        }
        installPreparedOnViewThread(prepared)
        return InstalledTerminalCheckpoint(
            sequence = prepared.sequence,
            size = prepared.size,
            projection = prepared.projection,
        )
    }

    override fun resize(columns: Int, rows: Int) {
        resizeInternal(
            columns,
            rows,
            cellWidthPixels,
            cellHeightPixels,
        )
        restoreAttachedViewSize()
    }

    override fun applyTheme(theme: TerminalThemeV1) {
        mutateSynchronously {
            currentTheme = theme
            applyThemeToEmulator(theme)
        }
    }

    override fun setReadOnly(readOnly: Boolean) {
        synchronized(lock) {
            checkActive()
            this.readOnly = readOnly
        }
    }

    override fun clear() {
        mutateSynchronously {
            emulator.reset()
            currentTheme?.let(::applyThemeToEmulator)
        }
    }

    override fun dispose() {
        synchronized(lock) {
            if (disposed) return
            disposed = true
            acceptedLocalUserInputListener = null
            acceptedLocalUserInputListenerGeneration = Math.addExact(
                acceptedLocalUserInputListenerGeneration,
                1L,
            )
            viewReference = null
            contextReference = null
            presentationState = null
        }
    }

    /** Creates the real Android terminal surface used by the Compose harness. */
    fun createView(
        context: Context,
        textSizeSp: Float = DEFAULT_TEXT_SIZE_SP,
        lineSpacingMultiplier: Float = 1f,
        onScrollPastTop: (() -> Unit)? = null,
        onScrollPastBottom: (() -> Unit)? = null,
    ): View {
        val mount = synchronized(lock) {
            checkActive()
            val previousView = viewReference?.get()
            if (presentationState == null && previousView != null) {
                presentationState = TermuxPresentationState(
                    state = previousView.capturePresentationState(),
                    topVisibleLineId = previousView.topVisibleLineId,
                )
            }
            previousView?.setPresentationStateListener(null)
            previousView?.setLocalUserInputListener(null)
            viewGeneration = Math.addExact(viewGeneration, 1L)
            val saved = presentationState
            saved?.copy(state = saved.state.clone()) to viewGeneration
        }
        val textSizePx = TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_SP,
            textSizeSp,
            context.resources.displayMetrics,
        )
            .roundToInt()
            .coerceAtLeast(1)
        val boundedLineSpacing = lineSpacingMultiplier.coerceIn(0.8f, 1.8f)
        return TerminalView(context, null).also { view ->
            view.setTextSize(textSizePx)
            view.setLineSpacing(boundedLineSpacing)
            view.setTerminalViewClient(
                TermuxTerminalViewClient(
                    view = view,
                    baseTextSizePx = textSizePx,
                    lineSpacingMultiplier = boundedLineSpacing,
                    minimumReadableTextSizePx = minimumReadableTextSizePixels(context),
                    onTextSizeRequested = ::acceptUserTextSize,
                ),
            )
            if (onScrollPastTop != null || onScrollPastBottom != null) {
                view.setScrollBoundaryListener(object : TerminalView.ScrollBoundaryListener {
                    override fun onScrollPastTop() = onScrollPastTop?.invoke() ?: Unit

                    override fun onScrollPastBottom() = onScrollPastBottom?.invoke() ?: Unit
                })
            }
            view.isFocusable = true
            view.isFocusableInTouchMode = true
            synchronized(lock) {
                checkActive()
                advanceRendererRevision()
                if (configuredTextSizePixels != textSizePx) {
                    configuredTextSizePixels = textSizePx
                    preferredTextSizePixels = textSizePx
                    userTextSizePixels = null
                }
                preferredTextSizePixels = textSizePx
                appliedTextSizePixels = textSizePx
                attachedLineSpacingMultiplier = boundedLineSpacing
                viewReference = WeakReference(view)
                contextReference = WeakReference(context.applicationContext)
            }
            view.attachSession(sessionPort)
            mount.first?.let { saved ->
                view.restorePresentationState(saved.state)
                if (saved.state[2] == 0 && saved.topVisibleLineId > 0) {
                    view.scrollToLineId(saved.topVisibleLineId)
                }
            }
            val generation = mount.second
            view.setPresentationStateListener { state, topVisibleLineId ->
                storePresentationState(generation, state, topVisibleLineId)
            }
            view.setLocalUserInputListener(object : TerminalView.LocalUserInputListener {
                override fun onLocalUserInputStarted() {
                    markTerminalViewLocalUserInputStarted(generation)
                }

                override fun onLocalUserInputAccepted() {
                    onTerminalViewLocalUserInput(generation)
                }
            })
        }
    }

    /** Returns the current visible grid as text for deterministic regression tests. */
    fun visibleText(): String = synchronized(lock) {
        checkActive()
        emulator.getScreen().getSelectedText(
            0,
            0,
            emulator.mColumns - 1,
            emulator.mRows - 1,
            false,
        )
    }

    fun currentSize(): TerminalSize = synchronized(lock) {
        checkActive()
        currentSize
    }

    /**
     * Replaces the listener notified after real local user input has entered [inputEvents].
     * Passing null clears it. Emulator-generated replies (for example CSI queries) never notify it.
     */
    fun setOnAcceptedLocalUserInputListener(listener: (() -> Unit)?) {
        synchronized(lock) {
            checkActive()
            acceptedLocalUserInputListener = listener
            acceptedLocalUserInputListenerGeneration = Math.addExact(
                acceptedLocalUserInputListenerGeneration,
                1L,
            )
        }
    }

    /** Returns an attached live view to its cursor-following tail without changing transport state. */
    fun scrollToLatest() {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        } ?: return
        val scroll = {
            view.scrollToLatest()
        }
        if (Looper.myLooper() == Looper.getMainLooper()) scroll() else view.post(scroll)
    }

    /** Drives the same primary/non-mouse top-boundary path used by an upward history gesture. */
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

    /** Sends committed IME text through the same UTF-8 boundary as the real TerminalView. */
    fun submitText(text: String): Boolean {
        if (!canWriteInput()) return false
        var accepted = false
        text.codePoints().forEach { codePoint ->
            val normalized = if (codePoint == '\n'.code) '\r'.code else codePoint
            accepted = emitInput(String(Character.toChars(normalized)).encodeToByteArray()) || accepted
        }
        if (accepted) {
            scrollToLatest()
            notifyAcceptedLocalUserInput()
        }
        return true
    }

    /** Sends a printable code point, including the VT control and Alt transformations. */
    fun submitCodePoint(codePoint: Int, controlDown: Boolean = false, altDown: Boolean = false): Boolean {
        require(Character.isValidCodePoint(codePoint)) { "Invalid Unicode code point: $codePoint" }
        if (!canWriteInput()) return false
        val transformed = if (controlDown) controlCodePoint(codePoint) else codePoint
        val encoded = String(Character.toChars(transformed)).encodeToByteArray()
        val accepted = emitInput(if (altDown) byteArrayOf(ESCAPE) + encoded else encoded)
        if (accepted) {
            scrollToLatest()
            notifyAcceptedLocalUserInput()
        }
        return true
    }

    /** Sends a non-text Android key using the pinned Termux key mapping. */
    fun submitKeyCode(keyCode: Int, modifiers: Int = 0): Boolean {
        if (!canWriteInput()) return false
        val encoded = synchronized(lock) {
            checkActive()
            KeyHandler.getCode(
                keyCode,
                modifiers,
                emulator.isCursorKeysApplicationMode,
                emulator.isKeypadApplicationMode,
            )
        } ?: return false
        if (emitInput(encoded.encodeToByteArray())) {
            scrollToLatest()
            notifyAcceptedLocalUserInput()
        }
        return true
    }

    /** Sends a named key chord without depending on Android hardware meta-state delivery. */
    fun submitKeyCode(
        keyCode: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
    ): Boolean {
        var modifiers = 0
        if (controlDown) modifiers = modifiers or KeyHandler.KEYMOD_CTRL
        if (altDown) modifiers = modifiers or KeyHandler.KEYMOD_ALT
        if (shiftDown) modifiers = modifiers or KeyHandler.KEYMOD_SHIFT
        return submitKeyCode(keyCode, modifiers)
    }

    /** Applies the emulator's sanitization and DECSET 2004 bracketed-paste behavior. */
    fun submitPaste(text: String): Boolean {
        if (!canWriteInput()) return false
        var accepted = false
        mutateSynchronously {
            val before = inputEnqueueRevision
            emulator.paste(text)
            accepted = inputEnqueueRevision != before
        }
        if (accepted && text.isNotEmpty()) {
            scrollToLatest()
            notifyAcceptedLocalUserInput()
        }
        return true
    }

    fun submitMouseEvent(button: Int, column: Int, row: Int, pressed: Boolean): Boolean {
        if (!canWriteInput()) return false
        val reporting = synchronized(lock) {
            checkActive()
            emulator.isMouseTrackingActive
        }
        if (!reporting) return false
        var accepted = false
        mutateSynchronously {
            val before = inputEnqueueRevision
            emulator.sendMouseEvent(button, column, row, pressed)
            accepted = inputEnqueueRevision != before
        }
        if (accepted) notifyAcceptedLocalUserInput()
        return true
    }

    fun submitFocusChanged(focused: Boolean): Boolean {
        if (!canWriteInput()) return false
        val reporting = synchronized(lock) {
            checkActive()
            emulator.isFocusReportingActive
        }
        if (!reporting) return false
        sessionPort.write(if (focused) "\u001b[I" else "\u001b[O")
        return true
    }

    /** Draft-only state used by the cross-engine Golden Stream comparator. */
    internal fun gridDraft(): TermuxGridDraft = synchronized(lock) {
        checkActive()
        val screen = emulator.screen
        val rows = (0 until emulator.mRows).map { row ->
            val runs = mutableListOf<TermuxStyleRun>()
            for (column in 0 until emulator.mColumns) {
                val style = normalizeStyle(screen.getStyleAt(row, column))
                val current = runs.lastOrNull()
                if (current != null && current.style == style) {
                    runs[runs.lastIndex] = current.copy(end = column + 1)
                } else {
                    runs += TermuxStyleRun(start = column, end = column + 1, style = style)
                }
            }
            TermuxGridRow(
                index = row,
                text = screen.getSelectedText(0, row, emulator.mColumns - 1, row, false, false),
                wrapped = screen.getLineWrap(row),
                styleRuns = runs,
            )
        }
        TermuxGridDraft(
            size = currentSize,
            cursor = TermuxGridCursor(
                column = emulator.cursorCol,
                row = emulator.cursorRow,
                visible = emulator.isCursorEnabled,
                style = when (emulator.cursorStyle) {
                    TerminalEmulator.TERMINAL_CURSOR_STYLE_UNDERLINE -> "underline"
                    TerminalEmulator.TERMINAL_CURSOR_STYLE_BAR -> "bar"
                    else -> "block"
                },
            ),
            modes = TermuxGridModes(
                alternateBuffer = emulator.isAlternateBufferActive,
                applicationCursor = emulator.isCursorKeysApplicationMode,
                applicationKeypad = emulator.isKeypadApplicationMode,
                mouseTracking = emulator.isMouseTrackingActive,
                reverseVideo = emulator.isReverseVideo,
            ),
            rows = rows,
        )
    }

    private fun resizeInternal(
        columns: Int,
        rows: Int,
        cellWidthPixels: Int,
        cellHeightPixels: Int,
    ) {
        val newSize = TerminalSize(columns, rows)
        require(cellWidthPixels > 0 && cellHeightPixels > 0) {
            "Terminal cell dimensions must be positive"
        }
        mutateSynchronously {
            this.cellWidthPixels = cellWidthPixels
            this.cellHeightPixels = cellHeightPixels
            emulator.resize(columns, rows, cellWidthPixels, cellHeightPixels)
            currentSize = newSize
            mutableAuthoritativeSize.value = newSize
        }
    }

    /**
     * A replayed ResizeApplied event describes the Windows PTY grid that produced
     * the following VT bytes. Re-run view measurement only to fit that grid into
     * the available pixels; it must not turn phone bounds into a new grid.
     */
    private fun restoreAttachedViewSize() {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        } ?: return
        if (Looper.myLooper() == Looper.getMainLooper()) {
            view.updateSize()
            return
        }

        val completed = CountDownLatch(1)
        var failure: Throwable? = null
        check(view.post {
            try {
                view.updateSize()
            } catch (error: Throwable) {
                failure = error
            } finally {
                completed.countDown()
            }
        }) { "Terminal view rejected a resize synchronization" }
        check(completed.await(VIEW_UPDATE_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "Timed out synchronizing terminal view size"
        }
        failure?.let { throw it }
    }

    private fun fitAttachedViewToAuthoritativeGrid() {
        val fit = synchronized(lock) {
            if (fittingAttachedView) return
            val view = viewReference?.get() ?: return
            if (view.width <= 0 || view.height <= 0) return
            val userSize = userTextSizePixels
            val effectivePreferred = userSize ?: preferredTextSizePixels
            val fitted = fittedTerminalTextSizePx(
                viewWidthPixels = view.width,
                viewHeightPixels = view.height,
                grid = currentSize,
                preferredTextSizePixels = effectivePreferred,
                minimumReadableTextSizePixels = userSize ?: minimumReadableTextSizePixels(view.context),
                lineSpacingMultiplier = attachedLineSpacingMultiplier,
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

    private fun applyThemeToEmulator(theme: TerminalThemeV1) {
        theme.ansiColors.forEachIndexed { index, color ->
            emulator.mColors.mCurrentColors[index] = color.value
        }
        emulator.mColors.mCurrentColors[TextStyle.COLOR_INDEX_FOREGROUND] = theme.foreground.value
        emulator.mColors.mCurrentColors[TextStyle.COLOR_INDEX_BACKGROUND] = theme.background.value
        emulator.mColors.mCurrentColors[TextStyle.COLOR_INDEX_CURSOR] = theme.cursor.value
    }

    private fun normalizeStyle(encoded: Long): TermuxCellStyle {
        val effects = TextStyle.decodeEffect(encoded)
        return TermuxCellStyle(
            foreground = normalizeColor(
                TextStyle.decodeForeColor(encoded),
                TextStyle.COLOR_INDEX_FOREGROUND,
            ),
            background = normalizeColor(
                TextStyle.decodeBackColor(encoded),
                TextStyle.COLOR_INDEX_BACKGROUND,
            ),
            attributes = TermuxCellAttributes(
                bold = effects has TextStyle.CHARACTER_ATTRIBUTE_BOLD,
                dim = effects has TextStyle.CHARACTER_ATTRIBUTE_DIM,
                italic = effects has TextStyle.CHARACTER_ATTRIBUTE_ITALIC,
                underline = effects has TextStyle.CHARACTER_ATTRIBUTE_UNDERLINE,
                blink = effects has TextStyle.CHARACTER_ATTRIBUTE_BLINK,
                inverse = effects has TextStyle.CHARACTER_ATTRIBUTE_INVERSE,
                invisible = effects has TextStyle.CHARACTER_ATTRIBUTE_INVISIBLE,
                strikethrough = effects has TextStyle.CHARACTER_ATTRIBUTE_STRIKETHROUGH,
            ),
        )
    }

    private fun normalizeColor(value: Int, defaultIndex: Int): TermuxCellColor = when {
        value == defaultIndex -> TermuxCellColor(type = "default", value = null)
        value and -0x1000000 == -0x1000000 -> TermuxCellColor(
            type = "rgb",
            value = "%06x".format(value and 0x00ffffff),
        )
        else -> TermuxCellColor(type = "palette", value = value.toString())
    }

    private infix fun Int.has(flag: Int): Boolean = this and flag != 0

    private fun emitInput(bytes: ByteArray): Boolean {
        return synchronized(lock) {
            checkActive()
            if (readOnly || bytes.isEmpty()) return@synchronized false
            check(inputEventChannel.trySend(bytes.copyOf()).isSuccess) {
                "Terminal input queue rejected an event while the engine was active"
            }
            inputEnqueueRevision = Math.addExact(inputEnqueueRevision, 1L)
            true
        }
    }

    private fun canWriteInput(): Boolean = synchronized(lock) {
        checkActive()
        !readOnly
    }

    private fun controlCodePoint(codePoint: Int): Int = when (codePoint) {
        in 'a'.code..'z'.code -> codePoint - 'a'.code + 1
        in 'A'.code..'Z'.code -> codePoint - 'A'.code + 1
        ' '.code, '2'.code -> 0
        '['.code, '3'.code -> 27
        '\\'.code, '4'.code -> 28
        ']'.code, '5'.code -> 29
        '^'.code, '6'.code -> 30
        '_'.code, '7'.code, '/'.code -> 31
        '8'.code -> 127
        else -> codePoint
    }

    private suspend fun mutateOnViewThread(block: () -> Unit) {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        }
        if (view == null || Looper.myLooper() == Looper.getMainLooper()) {
            synchronized(lock) {
                checkActive()
                block()
                advanceRendererRevision()
                if (view == null) reconcileDetachedPresentationAfterFeed()
            }
            view?.onScreenUpdated()
            return
        }

        suspendCancellableCoroutine { continuation ->
            if (!view.post {
                    try {
                        synchronized(lock) {
                            checkActive()
                            block()
                            advanceRendererRevision()
                        }
                        view.onScreenUpdated()
                        if (continuation.isActive) continuation.resume(Unit)
                    } catch (error: Exception) {
                        if (continuation.isActive) continuation.resumeWithException(error)
                    }
                }
            ) {
                continuation.resumeWithException(
                    IllegalStateException("Terminal view rejected an update"),
                )
            }
        }
    }

    private suspend fun installPreparedOnViewThread(prepared: TermuxPreparedCheckpoint) {
        val dispatcherView = synchronized(lock) {
            checkActive()
            viewReference?.get()
        }
        val install = {
            synchronized(lock) {
                checkActive()
                check(rendererRevision == prepared.preparedAtRevision) {
                    "Renderer changed after checkpoint preparation"
                }
                val previousEmulator = emulator
                val previousSize = currentSize
                val currentView = viewReference?.get()
                emulator = prepared.emulator
                currentSize = prepared.size
                mutableAuthoritativeSize.value = prepared.size
                mutableTitle.value = prepared.emulator.title.orEmpty().take(MAX_TITLE_CHARACTERS)
                try {
                    currentView?.replaceEmulatorFromSession()
                    advanceRendererRevision()
                } catch (failure: Throwable) {
                    emulator = previousEmulator
                    currentSize = previousSize
                    mutableAuthoritativeSize.value = previousSize
                    mutableTitle.value = previousEmulator.title.orEmpty().take(MAX_TITLE_CHARACTERS)
                    try {
                        currentView?.replaceEmulatorFromSession()
                    } catch (rollbackFailure: Throwable) {
                        failure.addSuppressed(rollbackFailure)
                    }
                    throw failure
                }
            }
            fitAttachedViewToAuthoritativeGrid()
        }
        if (dispatcherView == null || Looper.myLooper() == Looper.getMainLooper()) {
            install()
            return
        }

        suspendCoroutine { continuation ->
            if (!dispatcherView.post {
                    try {
                        install()
                        continuation.resume(Unit)
                    } catch (error: Throwable) {
                        continuation.resumeWithException(error)
                    }
                }
            ) {
                continuation.resumeWithException(
                    IllegalStateException("Terminal view rejected a checkpoint installation"),
                )
            }
        }
    }

    private fun mutateSynchronously(block: () -> Unit) {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        }
        if (view == null || Looper.myLooper() == Looper.getMainLooper()) {
            synchronized(lock) {
                checkActive()
                block()
                advanceRendererRevision()
            }
            view?.onScreenUpdated()
            return
        }

        val completed = CountDownLatch(1)
        var failure: Throwable? = null
        check(view.post {
            try {
                synchronized(lock) {
                    checkActive()
                    block()
                    advanceRendererRevision()
                }
                view.onScreenUpdated()
            } catch (error: Throwable) {
                failure = error
            } finally {
                completed.countDown()
            }
        }) { "Terminal view rejected an update" }
        check(completed.await(VIEW_UPDATE_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "Timed out updating terminal view"
        }
        failure?.let { throw it }
    }

    private fun notifyView() {
        val view = synchronized(lock) { viewReference?.get() } ?: return
        view.post { view.onScreenUpdated() }
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

    private fun reconcileDetachedPresentationAfterFeed() {
        val rowShift = emulator.scrollCounter
        val saved = presentationState
        if (saved != null) {
            val state = saved.state.clone()
            if (emulator.isAlternateBufferActive || emulator.isMouseTrackingActive) {
                state[2] = 1
                state[3] = 1
            } else if (state[2] == 0 && rowShift != 0) {
                state[0] = (state[0] - rowShift).coerceAtLeast(-emulator.screen.activeTranscriptRows)
                state[3] = 0
            }
            presentationState = saved.copy(state = state)
        }
        emulator.clearScrollCounter()
    }

    private fun markTerminalViewLocalUserInputStarted(generation: Long) {
        synchronized(lock) {
            if (disposed || generation != viewGeneration) return
            terminalViewInputStartRevision = inputEnqueueRevision
        }
    }

    private fun onTerminalViewLocalUserInput(generation: Long) {
        val accepted = synchronized(lock) {
            if (disposed || generation != viewGeneration || readOnly) return@synchronized false
            val changed = inputEnqueueRevision > terminalViewInputStartRevision
            terminalViewInputStartRevision = inputEnqueueRevision
            changed
        }
        if (accepted) {
            scrollToLatest()
            notifyAcceptedLocalUserInput()
        }
    }

    private fun notifyAcceptedLocalUserInput() {
        val scheduledGeneration = synchronized(lock) {
            if (disposed || acceptedLocalUserInputListener == null) return
            acceptedLocalUserInputListenerGeneration
        }
        val deliver = Runnable {
            val listener = synchronized(lock) {
                if (disposed || scheduledGeneration != acceptedLocalUserInputListenerGeneration) null
                else acceptedLocalUserInputListener
            }
            if (listener != null) runCatching(listener)
        }
        val mainLooper = Looper.getMainLooper()
        if (mainLooper == null || Looper.myLooper() == mainLooper) {
            deliver.run()
        } else if (!Handler(mainLooper).post(deliver)) {
            // A shutting-down UI queue is equivalent to a cleared listener; never run it on a
            // transport worker as a fallback.
            return
        }
    }

    private fun checkActive() {
        check(!disposed) { "Terminal engine is disposed" }
    }

    private fun advanceRendererRevision() {
        rendererRevision = Math.addExact(rendererRevision, 1L)
    }

    private companion object {
        const val DEFAULT_COLUMNS = 80
        const val DEFAULT_ROWS = 24
        const val DEFAULT_TRANSCRIPT_ROWS = 2_000
        const val DEFAULT_TEXT_SIZE_SP = 13f
        const val VIEW_UPDATE_TIMEOUT_SECONDS = 5L
        const val MAX_TITLE_CHARACTERS = 4_096
        const val ESCAPE: Byte = 0x1b
    }

    private data class PreparationContext(
        val rendererRevision: Long,
        val cellWidthPixels: Int,
        val cellHeightPixels: Int,
    )

    private class TermuxPreparedCheckpoint(
        val owner: Any,
        val preparedAtRevision: Long,
        override val sequence: Long,
        override val size: TerminalSize,
        override val projection: TerminalSessionProjectionSnapshotV1,
        val emulator: TerminalEmulator,
    ) : PreparedTerminalCheckpoint
}

internal data class TermuxPresentationState(
    val state: IntArray,
    val topVisibleLineId: Long,
)

/**
 * Largest preferred-or-smaller readable font that fits the remote columns.
 *
 * Rows deliberately do not participate: Android owns only a viewport over the PC-owned grid and
 * fills whatever height remains after the IME and controls are laid out. If even the readable
 * floor cannot fit all columns, TerminalView exposes the remainder through horizontal panning.
 */
internal fun fittedTerminalTextSizePx(
    viewWidthPixels: Int,
    viewHeightPixels: Int,
    grid: TerminalSize,
    preferredTextSizePixels: Int,
    lineSpacingMultiplier: Float,
    minimumReadableTextSizePixels: Int = 1,
): Int {
    require(viewWidthPixels > 0 && viewHeightPixels > 0) { "view dimensions must be positive" }
    require(preferredTextSizePixels > 0) { "preferred text size must be positive" }
    require(minimumReadableTextSizePixels > 0) { "minimum readable text size must be positive" }
    // Keep validation for the persisted preference even though height no longer drives font size.
    lineSpacingMultiplier.coerceIn(0.8f, 1.8f)
    val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { typeface = Typeface.MONOSPACE }
    fun fits(textSize: Int): Boolean {
        paint.textSize = textSize.toFloat()
        val measuredCellWidth = paint.measureText("X")
        val cellWidth = measuredCellWidth.takeIf { it.isFinite() && it > 0f }
            ?: (textSize * FALLBACK_MONOSPACE_WIDTH_RATIO)
        return cellWidth * grid.columns <= viewWidthPixels
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

internal fun minimumReadableTextSizePixels(context: Context): Int = TypedValue.applyDimension(
    TypedValue.COMPLEX_UNIT_SP,
    MINIMUM_READABLE_TEXT_SIZE_SP,
    context.resources.displayMetrics,
).roundToInt().coerceAtLeast(1)

private const val MINIMUM_READABLE_TEXT_SIZE_SP = 10f
private const val FALLBACK_MONOSPACE_WIDTH_RATIO = 0.62f

internal data class TermuxGridDraft(
    val size: TerminalSize,
    val cursor: TermuxGridCursor,
    val modes: TermuxGridModes,
    val rows: List<TermuxGridRow>,
)

internal data class TermuxGridCursor(
    val column: Int,
    val row: Int,
    val visible: Boolean,
    val style: String,
)

internal data class TermuxGridModes(
    val alternateBuffer: Boolean,
    val applicationCursor: Boolean,
    val applicationKeypad: Boolean,
    val mouseTracking: Boolean,
    val reverseVideo: Boolean,
)

internal data class TermuxGridRow(
    val index: Int,
    val text: String,
    val wrapped: Boolean,
    val styleRuns: List<TermuxStyleRun>,
)

internal data class TermuxStyleRun(
    val start: Int,
    val end: Int,
    val style: TermuxCellStyle,
)

internal data class TermuxCellStyle(
    val foreground: TermuxCellColor,
    val background: TermuxCellColor,
    val attributes: TermuxCellAttributes,
)

internal data class TermuxCellColor(
    val type: String,
    val value: String?,
)

internal data class TermuxCellAttributes(
    val bold: Boolean,
    val dim: Boolean,
    val italic: Boolean,
    val underline: Boolean,
    val blink: Boolean,
    val inverse: Boolean,
    val invisible: Boolean,
    val strikethrough: Boolean,
)

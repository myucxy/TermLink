package dev.termlink.terminal.termux

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Looper
import android.util.TypedValue
import android.view.View
import com.termux.terminal.KeyHandler
import com.termux.terminal.TerminalEmulator
import com.termux.terminal.TerminalOutput
import com.termux.terminal.TextStyle
import com.termux.view.TerminalSessionPort
import com.termux.view.TerminalView
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import java.lang.ref.WeakReference
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
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
) : TerminalEngine {
    private val lock = Any()
    private val inputEventChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private val mutableSizeEvents = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 16)
    private var viewReference: WeakReference<TerminalView>? = null
    private var contextReference: WeakReference<Context>? = null
    private var disposed = false
    private var readOnly = true
    private var currentSize = initialSize
    private var cellWidthPixels = 1
    private var cellHeightPixels = 1
    private var currentTheme: TerminalThemeV1? = null

    private val terminalOutput = object : TerminalOutput() {
        override fun write(data: ByteArray, offset: Int, count: Int) {
            emitInput(data.copyOfRange(offset, offset + count))
        }

        override fun titleChanged(oldTitle: String?, newTitle: String?) = Unit

        override fun onCopyTextToClipboard(text: String?) = Unit

        override fun onPasteTextFromClipboard() = Unit

        override fun onBell() = Unit

        override fun onColorsChanged() = notifyView()
    }

    private val emulator = TerminalEmulator(
        terminalOutput,
        initialSize.columns,
        initialSize.rows,
        cellWidthPixels,
        cellHeightPixels,
        transcriptRows,
        null,
    )

    private val sessionPort = object : TerminalSessionPort {
        override fun getEmulator(): TerminalEmulator = this@TermuxTerminalEngine.emulator

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
            mutateSynchronously { emulator.paste(text) }
        }

        override fun updateSize(
            columns: Int,
            rows: Int,
            cellWidthPixels: Int,
            cellHeightPixels: Int,
        ) {
            resizeInternal(
                columns,
                rows,
                cellWidthPixels,
                cellHeightPixels,
                publishSizeEvent = true,
            )
        }
    }

    override val inputEvents: Flow<ByteArray> = inputEventChannel.receiveAsFlow()
    override val sizeEvents: Flow<TerminalSize> = mutableSizeEvents

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        require(seq > 0) { "Terminal sequence must be positive" }
        val chunk = ByteArray(bytes.remaining())
        bytes.get(chunk)
        mutateOnViewThread {
            emulator.append(chunk, chunk.size)
        }
    }

    override fun resize(columns: Int, rows: Int) {
        resizeInternal(
            columns,
            rows,
            cellWidthPixels,
            cellHeightPixels,
            publishSizeEvent = false,
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
            viewReference = null
            contextReference = null
        }
    }

    /** Creates the real Android terminal surface used by the Compose harness. */
    fun createView(
        context: Context,
        textSizeSp: Float = DEFAULT_TEXT_SIZE_SP,
        lineSpacingMultiplier: Float = 1f,
    ): View {
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
            view.setTerminalViewClient(TermuxTerminalViewClient(view, textSizePx, boundedLineSpacing))
            view.isFocusable = true
            view.isFocusableInTouchMode = true
            synchronized(lock) {
                checkActive()
                viewReference = WeakReference(view)
                contextReference = WeakReference(context.applicationContext)
            }
            view.attachSession(sessionPort)
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

    /** Sends committed IME text through the same UTF-8 boundary as the real TerminalView. */
    fun submitText(text: String): Boolean {
        if (!canWriteInput()) return false
        text.codePoints().forEach { codePoint ->
            sessionPort.writeCodePoint(false, if (codePoint == '\n'.code) '\r'.code else codePoint)
        }
        return true
    }

    /** Sends a printable code point, including the VT control and Alt transformations. */
    fun submitCodePoint(codePoint: Int, controlDown: Boolean = false, altDown: Boolean = false): Boolean {
        require(Character.isValidCodePoint(codePoint)) { "Invalid Unicode code point: $codePoint" }
        if (!canWriteInput()) return false
        val transformed = if (controlDown) controlCodePoint(codePoint) else codePoint
        sessionPort.writeCodePoint(altDown, transformed)
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
        sessionPort.write(encoded)
        return true
    }

    /** Applies the emulator's sanitization and DECSET 2004 bracketed-paste behavior. */
    fun submitPaste(text: String): Boolean {
        if (!canWriteInput()) return false
        mutateSynchronously { emulator.paste(text) }
        return true
    }

    fun submitMouseEvent(button: Int, column: Int, row: Int, pressed: Boolean): Boolean {
        if (!canWriteInput()) return false
        val reporting = synchronized(lock) {
            checkActive()
            emulator.isMouseTrackingActive
        }
        if (!reporting) return false
        mutateSynchronously { emulator.sendMouseEvent(button, column, row, pressed) }
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
        publishSizeEvent: Boolean,
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
            if (publishSizeEvent) mutableSizeEvents.tryEmit(newSize)
        }
    }

    /**
     * A replayed ResizeApplied event describes the grid that produced the following VT bytes, but
     * it must not leave an attached Android surface rendering a larger off-screen grid. Re-run the
     * real view measurement after applying the remote event. TerminalView calls updateSize only
     * when the measured grid differs, which restores the local viewport and publishes exactly one
     * outbound size event.
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

    private fun emitInput(bytes: ByteArray) {
        val canWrite = synchronized(lock) {
            checkActive()
            !readOnly
        }
        if (canWrite && bytes.isNotEmpty()) {
            check(inputEventChannel.trySend(bytes.copyOf()).isSuccess) {
                "Terminal input queue rejected an event while the engine was active"
            }
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

    private fun mutateSynchronously(block: () -> Unit) {
        val view = synchronized(lock) {
            checkActive()
            viewReference?.get()
        }
        if (view == null || Looper.myLooper() == Looper.getMainLooper()) {
            synchronized(lock) {
                checkActive()
                block()
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

    private fun checkActive() {
        check(!disposed) { "Terminal engine is disposed" }
    }

    private companion object {
        const val DEFAULT_COLUMNS = 80
        const val DEFAULT_ROWS = 24
        const val DEFAULT_TRANSCRIPT_ROWS = 2_000
        const val DEFAULT_TEXT_SIZE_SP = 13f
        const val VIEW_UPDATE_TIMEOUT_SECONDS = 5L
        const val ESCAPE: Byte = 0x1b
    }
}

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

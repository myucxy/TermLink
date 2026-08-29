package dev.termlink.terminal.nativecore

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.View
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalCellPosition
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalMouseButton
import dev.termlink.terminal.api.TerminalMouseButtons
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMousePosition
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalPixelPosition
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalViewportEngine
import java.lang.ref.WeakReference
import java.nio.ByteBuffer
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.receiveAsFlow

/** Production Android terminal whose parser, render frame, and input modes are terminal-core. */
class NativeTerminalEngine(
    initialSize: TerminalSize = TerminalSize(DEFAULT_COLUMNS, DEFAULT_ROWS),
    private val bridge: NativeBridge = JniNativeBridge,
    private val outputProfile: TerminalOutputProfile = TerminalOutputProfile.UTF8,
    private val historyCapacityRows: Long = DEFAULT_HISTORY_CAPACITY_ROWS,
    private val limits: NativeTerminalLimits = NativeTerminalLimits(),
) : TerminalViewportEngine {
    private val lock = Any()
    private val inputChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private val mutableSize = MutableStateFlow(initialSize)
    private val mutableTitle = MutableStateFlow("")
    private val mutableFrame: MutableStateFlow<TerminalRenderFrameV3>
    private val mainHandler = Handler(Looper.getMainLooper())
    private var terminal = createTerminal(initialSize)
    private var currentTheme = DEFAULT_THEME
    private var readOnly = true
    private var disposed = false
    private var failedClosed = false
    private var viewReference: WeakReference<NativeTerminalView>? = null
    private var acceptedInputListener: (() -> Unit)? = null
    private var listenerGeneration = 0L

    init {
        val firstFrame = terminal.renderV3().renderFrame
        mutableFrame = MutableStateFlow(firstFrame)
        publishFrame(firstFrame, notify = false)
    }

    override val inputEvents: Flow<ByteArray> = inputChannel.receiveAsFlow()
    override val sizeEvents: Flow<TerminalSize> = emptyFlow()
    override val authoritativeSize: StateFlow<TerminalSize> = mutableSize.asStateFlow()
    override val title: StateFlow<String> = mutableTitle.asStateFlow()
    internal val frames: StateFlow<TerminalRenderFrameV3> = mutableFrame.asStateFlow()

    data class InstalledCheckpoint(
        val sequence: Long,
        val size: TerminalSize,
    )

    /** Atomically replaces the live parser with an authenticated canonical Snapshot V2. */
    fun installCheckpoint(checkpoint: ByteArray): InstalledCheckpoint {
        require(checkpoint.isNotEmpty()) { "Native terminal checkpoint must not be empty" }
        val replacement = NativeTerminalHandle.importCheckpoint(bridge, checkpoint, limits)
        val frame = try {
            replacement.renderV3().renderFrame
        } catch (error: Throwable) {
            runCatching(replacement::close).exceptionOrNull()?.let(error::addSuppressed)
            throw error
        }
        val nextSourceSequence = replacement.nextSourceSequence
        require(nextSourceSequence > FIRST_SOURCE_SEQUENCE) {
            "Native terminal checkpoint must follow at least one ordered source event"
        }
        val previous = try {
            synchronized(lock) {
                checkActive()
                val owned = terminal
                terminal = replacement
                failedClosed = false
                readOnly = true
                owned
            }
        } catch (error: Throwable) {
            runCatching(replacement::close).exceptionOrNull()?.let(error::addSuppressed)
            throw error
        }
        try {
            previous.close()
        } catch (error: Throwable) {
            synchronized(lock) {
                failedClosed = true
                readOnly = true
            }
            throw error
        }
        publishFrame(frame, notify = true)
        return InstalledCheckpoint(
            sequence = nextSourceSequence - 1L,
            size = frame.size,
        )
    }

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        require(seq > 0) { "Terminal sequence must be positive" }
        val owned = ByteArray(bytes.remaining())
        bytes.get(owned)
        require(owned.isNotEmpty()) { "Terminal output must not be empty" }
        applyOrdered { it.feed(seq, owned) }
    }

    override suspend fun advance(seq: Long) {
        applyOrdered { it.advance(seq, 0) }
    }

    override suspend fun resize(seq: Long, columns: Int, rows: Int) {
        val size = TerminalSize(columns, rows)
        applyOrdered { it.resize(seq, size) }
    }

    override suspend fun reportGap(seq: Long) {
        synchronized(lock) {
            checkActive()
            if (failedClosed) return
            failedClosed = true
            try {
                terminal.reportGap(seq)
            } catch (error: NativeTerminalException) {
                if (error.status != NativeTerminalStatus.GAP) throw error
            } finally {
                readOnly = true
            }
        }
    }

    /** Local resize is legal only for a fresh generation before its first ordered source event. */
    override fun resize(columns: Int, rows: Int) {
        val size = TerminalSize(columns, rows)
        synchronized(lock) {
            checkActive()
            if (size == mutableSize.value) return
            check(terminal.nextSourceSequence == FIRST_SOURCE_SEQUENCE) {
                "An active native terminal can only be resized by an ordered remote event"
            }
            replaceTerminal(size)
        }
    }

    override fun applyTheme(theme: TerminalThemeV1) {
        synchronized(lock) {
            checkActive()
            currentTheme = theme
        }
        notifyView()
    }

    override fun setReadOnly(readOnly: Boolean) {
        synchronized(lock) {
            checkActive()
            this.readOnly = readOnly || failedClosed
        }
    }

    override fun clear() {
        synchronized(lock) {
            checkActive()
            replaceTerminal(mutableSize.value)
            failedClosed = false
            readOnly = true
        }
    }

    override fun dispose() {
        val owned = synchronized(lock) {
            if (disposed) return
            disposed = true
            listenerGeneration = Math.addExact(listenerGeneration, 1)
            acceptedInputListener = null
            viewReference?.get()?.detachEngine()
            viewReference = null
            terminal
        }
        inputChannel.close()
        owned.close()
    }

    override fun createView(
        context: Context,
        textSizeSp: Float,
        lineSpacingMultiplier: Float,
        onScrollPastTop: (() -> Unit)?,
        onScrollPastBottom: (() -> Unit)?,
    ): View {
        val view = NativeTerminalView(
            context = context,
            engine = this,
            textSizeSp = textSizeSp,
            lineSpacingMultiplier = lineSpacingMultiplier,
            onScrollPastTop = onScrollPastTop,
            onScrollPastBottom = onScrollPastBottom,
        )
        synchronized(lock) {
            checkActive()
            viewReference?.get()?.detachEngine()
            viewReference = WeakReference(view)
        }
        return view
    }

    override fun visibleText(): String = synchronized(lock) {
        checkActive()
        frameText(mutableFrame.value)
    }

    override fun currentSize(): TerminalSize = synchronized(lock) {
        checkActive()
        mutableSize.value
    }

    override fun setOnAcceptedLocalUserInputListener(listener: (() -> Unit)?) {
        synchronized(lock) {
            checkActive()
            acceptedInputListener = listener
            listenerGeneration = Math.addExact(listenerGeneration, 1)
        }
    }

    override fun scrollToLatest() {
        val view = synchronized(lock) { checkActive(); viewReference?.get() } ?: return
        view.post(view::scrollToCursor)
    }

    override fun scrollPastTopBoundary() {
        val view = synchronized(lock) { checkActive(); viewReference?.get() } ?: return
        view.post(view::signalScrollPastTop)
    }

    override fun submitText(text: String): Boolean {
        if (text.isEmpty()) return canAcceptInput()
        val payloads = ArrayList<ByteArray>()
        synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            text.codePoints().forEach { codePoint ->
                payloads += if (codePoint == '\n'.code || codePoint == '\r'.code) {
                    terminal.encodeKey(TerminalKey.ENTER)
                } else {
                    terminal.encodeCharacter(codePoint)
                }
            }
        }
        return emitPayloads(payloads)
    }

    override fun submitCodePoint(codePoint: Int, controlDown: Boolean, altDown: Boolean): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            terminal.encodeCharacter(
                codePoint,
                TerminalInputModifiers(control = controlDown, alt = altDown),
            )
        }
        return emitPayloads(listOf(payload))
    }

    override fun submitKeyCode(keyCode: Int, modifiers: Int): Boolean = submitKeyCode(
        keyCode = keyCode,
        controlDown = modifiers and (TERMINAL_KEYMOD_CTRL or KeyEvent.META_CTRL_ON) != 0,
        altDown = modifiers and (TERMINAL_KEYMOD_ALT or KeyEvent.META_ALT_ON) != 0,
        shiftDown = modifiers and (TERMINAL_KEYMOD_SHIFT or KeyEvent.META_SHIFT_ON) != 0,
    )

    override fun submitKeyCode(
        keyCode: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
    ): Boolean {
        val key = androidTerminalKey(keyCode) ?: return false
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            terminal.encodeKey(
                key,
                TerminalInputModifiers(
                    control = controlDown,
                    alt = altDown,
                    shift = shiftDown,
                ),
            )
        }
        return emitPayloads(listOf(payload))
    }

    override fun submitPaste(text: String): Boolean {
        if (text.isEmpty()) return canAcceptInput()
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            terminal.encodePaste(text.encodeToByteArray())
        }
        return emitPayloads(listOf(payload))
    }

    internal fun submitFocus(focused: Boolean): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            terminal.encodeFocus(focused)
        }
        return emitPayloads(listOf(payload), notifyAccepted = false)
    }

    internal fun submitMouse(input: TerminalMouseInput): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            terminal.encodeMouse(input)
        }
        if (payload.isEmpty()) return false
        return emitPayloads(listOf(payload))
    }

    internal fun theme(): TerminalThemeV1 = synchronized(lock) { currentTheme }

    internal fun detachView(view: NativeTerminalView) {
        synchronized(lock) {
            if (viewReference?.get() === view) viewReference = null
        }
    }

    private fun applyOrdered(apply: (NativeTerminalHandle) -> Unit) {
        val frame = synchronized(lock) {
            checkActive()
            check(!failedClosed) { "Native terminal renderer is failed closed" }
            try {
                apply(terminal)
                terminal.renderV3().renderFrame
            } catch (error: Throwable) {
                failedClosed = true
                readOnly = true
                throw error
            }
        }
        publishFrame(frame, notify = true)
    }

    private fun replaceTerminal(size: TerminalSize) {
        val previous = terminal
        val replacement = createTerminal(size)
        val frame = try {
            replacement.renderV3().renderFrame
        } catch (error: Throwable) {
            runCatching(replacement::close).exceptionOrNull()?.let(error::addSuppressed)
            throw error
        }
        terminal = replacement
        try {
            previous.close()
        } catch (error: Throwable) {
            failedClosed = true
            readOnly = true
            throw error
        }
        publishFrame(frame, notify = true)
    }

    private fun createTerminal(size: TerminalSize): NativeTerminalHandle = NativeTerminalHandle.create(
        bridge = bridge,
        size = size,
        initialNextSourceSequence = FIRST_SOURCE_SEQUENCE,
        outputProfile = outputProfile,
        historyCapacityRows = historyCapacityRows,
        limits = limits,
    )

    private fun publishFrame(frame: TerminalRenderFrameV3, notify: Boolean) {
        mutableFrame.value = frame
        mutableSize.value = frame.size
        mutableTitle.value = frame.title.take(MAX_TITLE_CHARACTERS)
        if (notify) notifyView()
    }

    private fun notifyView() {
        val view = synchronized(lock) { if (disposed) null else viewReference?.get() } ?: return
        if (!view.post(view::onFrameChanged)) viewReference = null
    }

    private fun emitPayloads(
        payloads: List<ByteArray>,
        notifyAccepted: Boolean = true,
    ): Boolean {
        var emitted = false
        for (payload in payloads) {
            if (payload.isEmpty()) continue
            check(inputChannel.trySend(payload.copyOf()).isSuccess) {
                "Terminal input queue rejected an event while the native engine was active"
            }
            emitted = true
        }
        if (emitted && notifyAccepted) notifyAcceptedInput()
        return true
    }

    private fun notifyAcceptedInput() {
        val generation = synchronized(lock) { listenerGeneration }
        mainHandler.post {
            val listener = synchronized(lock) {
                acceptedInputListener.takeIf { !disposed && generation == listenerGeneration }
            }
            listener?.invoke()
        }
    }

    private fun canAcceptInput(): Boolean = synchronized(lock) { canAcceptInputLocked() }

    private fun canAcceptInputLocked(): Boolean = !disposed && !readOnly && !failedClosed

    private fun checkActive() {
        check(!disposed) { "Terminal engine is disposed" }
    }

    private companion object {
        const val DEFAULT_COLUMNS = 80
        const val DEFAULT_ROWS = 24
        const val DEFAULT_HISTORY_CAPACITY_ROWS = 0L
        const val FIRST_SOURCE_SEQUENCE = 1L
        const val MAX_TITLE_CHARACTERS = 4_096
        const val TERMINAL_KEYMOD_ALT = Int.MIN_VALUE
        const val TERMINAL_KEYMOD_CTRL = 0x4000_0000
        const val TERMINAL_KEYMOD_SHIFT = 0x2000_0000

        val DEFAULT_THEME = TerminalThemeV1(
            name = "TermLink",
            foreground = ArgbColor(0xffe9eef5.toInt()),
            background = ArgbColor(0xff080b10.toInt()),
            cursor = ArgbColor(0xfff4c95d.toInt()),
            selectionBackground = ArgbColor(0xff3b82f6.toInt()),
            selectionForeground = null,
            ansiColors = listOf(
                0xff080b10, 0xffd35f5f, 0xff63b46c, 0xffd8b45b,
                0xff6299d6, 0xffb27acb, 0xff56b6b2, 0xffd7dce2,
                0xff626a73, 0xffff7b72, 0xff7ee787, 0xffffd866,
                0xff79b8ff, 0xffd2a8ff, 0xff76e3ea, 0xffffffff,
            ).map { ArgbColor(it.toInt()) },
            boldAsBright = true,
            fontFamilyHint = "monospace",
        )
    }
}

private fun androidTerminalKey(keyCode: Int): TerminalKey? = when (keyCode) {
    KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> TerminalKey.ENTER
    KeyEvent.KEYCODE_TAB -> TerminalKey.TAB
    KeyEvent.KEYCODE_DEL -> TerminalKey.BACKSPACE
    KeyEvent.KEYCODE_ESCAPE -> TerminalKey.ESCAPE
    KeyEvent.KEYCODE_FORWARD_DEL -> TerminalKey.DELETE
    KeyEvent.KEYCODE_INSERT -> TerminalKey.INSERT
    KeyEvent.KEYCODE_MOVE_HOME -> TerminalKey.HOME
    KeyEvent.KEYCODE_MOVE_END -> TerminalKey.END
    KeyEvent.KEYCODE_PAGE_UP -> TerminalKey.PAGE_UP
    KeyEvent.KEYCODE_PAGE_DOWN -> TerminalKey.PAGE_DOWN
    KeyEvent.KEYCODE_DPAD_UP -> TerminalKey.ARROW_UP
    KeyEvent.KEYCODE_DPAD_DOWN -> TerminalKey.ARROW_DOWN
    KeyEvent.KEYCODE_DPAD_LEFT -> TerminalKey.ARROW_LEFT
    KeyEvent.KEYCODE_DPAD_RIGHT -> TerminalKey.ARROW_RIGHT
    in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 ->
        TerminalKey.entries[TerminalKey.F1.ordinal + keyCode - KeyEvent.KEYCODE_F1]
    else -> null
}

internal fun frameText(frame: TerminalRenderFrameV3): String = buildString {
    frame.rows.forEachIndexed { rowIndex, row ->
        var lastNonBlank = -1
        val values = ArrayList<String>(frame.size.columns)
        row.cells.forEachIndexed { column, cell ->
            val text = if (cell.wideContinuation) "" else {
                frame.graphemes.getOrNull(cell.graphemeId - 1)?.text ?: " "
            }
            values += text
            if (text.isNotBlank()) lastNonBlank = column
        }
        val meaningfulEnd = maxOf(row.meaningfulEnd - 1, lastNonBlank)
        if (meaningfulEnd >= 0) {
            for (column in 0..meaningfulEnd.coerceAtMost(values.lastIndex)) append(values[column])
        }
        if (!row.softWrapped && rowIndex != frame.rows.lastIndex) append('\n')
    }
}

internal fun terminalMousePosition(
    column: Int,
    row: Int,
    pixelX: Int,
    pixelY: Int,
): TerminalMousePosition = TerminalMousePosition(
    cell = TerminalCellPosition(column, row),
    pixel = TerminalPixelPosition(pixelX, pixelY),
)

internal fun primaryMouseButtons(held: Boolean): TerminalMouseButtons =
    TerminalMouseButtons(left = held)

internal val PRIMARY_MOUSE_BUTTON: TerminalMouseButton = TerminalMouseButton.LEFT

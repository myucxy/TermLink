package dev.termlink.terminal.nativecore

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.View
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalCellPosition
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalInputDiagnostic
import dev.termlink.terminal.api.TerminalHostEffectFrameV2
import dev.termlink.terminal.api.TerminalHostEffectKindV2
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalKittyFunctionalKey
import dev.termlink.terminal.api.TerminalKittyKeyEventType
import dev.termlink.terminal.api.TerminalMouseButton
import dev.termlink.terminal.api.TerminalMouseButtons
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMousePosition
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalPixelPosition
import dev.termlink.terminal.api.TerminalTextFrame
import dev.termlink.terminal.api.TerminalWidthProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalViewportEngine
import java.lang.ref.WeakReference
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.LinkedHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.receiveAsFlow

internal enum class NativeHistoryScrollResult {
    MOVED,
    UNCHANGED,
    CANCELLED,
    OLDEST_BOUNDARY,
    LIVE_BOUNDARY,
    CAPTURE_EXPIRED,
    CAPTURE_LIMIT,
}

/** Production Android terminal whose parser, render frame, and input modes are terminal-core. */
class NativeTerminalEngine(
    initialSize: TerminalSize = TerminalSize(DEFAULT_COLUMNS, DEFAULT_ROWS),
    private val bridge: NativeBridge = JniNativeBridge,
    private val outputProfile: TerminalOutputProfile = TerminalOutputProfile.UTF8,
    private val historyCapacityRows: Long = DEFAULT_HISTORY_CAPACITY_ROWS,
    limits: NativeTerminalLimits? = null,
    private val widthProfile: TerminalWidthProfile = TerminalWidthProfile.LEGACY_V1,
    private val platformActionSink: dev.termlink.terminal.api.TerminalPlatformActionSink? = null,
    val graphicsProfile: Boolean = false,
) : TerminalViewportEngine {
    val stateDeltaWidthProfile: TerminalWidthProfile get() = widthProfile
    val stateDeltaOutputProfile: TerminalOutputProfile get() = outputProfile

    private val limits = limits ?: if (graphicsProfile) NativeTerminalLimits.graphics()
        else NativeTerminalLimits(maxRenderFrameBytes = TERMLINK_RENDER_FRAME_V4_MAX_BYTES)
    private var lastResizeNoticeMillis = -3000L

    override fun notifyHostResizeResult(result: dev.termlink.terminal.api.TerminalHostResizeResultV1) {
        if (result.error == 0) return
        val view = synchronized(lock) { if (disposed) null else viewReference?.get() } ?: return
        view.post {
            val now = android.os.SystemClock.uptimeMillis()
            if (view.isShown && now - lastResizeNoticeMillis >= 3000 && synchronized(lock) { !disposed && viewReference?.get() === view }) {
                lastResizeNoticeMillis = now
                android.widget.Toast.makeText(view.context, result.describe(), android.widget.Toast.LENGTH_LONG).show()
            }
        }
    }
    private val lock = Any()
    private val inputChannel = Channel<ByteArray>(Channel.UNLIMITED)
    private val mutableSize = MutableStateFlow(initialSize)
    private val mutableTitle = MutableStateFlow("")
    private val mutableMouseReporting = MutableStateFlow(false)
    private val mutableInputDiagnostic = MutableStateFlow<TerminalInputDiagnostic?>(null)
    val inputDiagnostic: StateFlow<TerminalInputDiagnostic?> = mutableInputDiagnostic.asStateFlow()
    fun dismissInputDiagnostic(expected: TerminalInputDiagnostic) {
        mutableInputDiagnostic.compareAndSet(expected, null)
    }
    private val mutableHostSignals = MutableStateFlow(dev.termlink.terminal.api.TerminalHostSignals())
    override val hostSignals = mutableHostSignals.asStateFlow()
    private var pendingPlatformActions = emptyList<dev.termlink.terminal.api.TerminalHostEffectV2>()

    override fun notifyLiveOutputApplied(sequence: Long) {
        val effects = synchronized(lock) {
            if (disposed || failedClosed) return
            val records = pendingPlatformActions.filter { it.sourceSequence == sequence }
            pendingPlatformActions = emptyList()
            records
        }
        if (effects.isNotEmpty()) platformActionSink?.offer(effects)
    }
    @Volatile internal var touchMouseEnabled = false
        private set
    /** UI-thread visibility check for foreground-only, explicitly confirmed platform actions. */
    fun hasVisibleDisplay(): Boolean {
        val view = synchronized(lock) { if (disposed || failedClosed) null else viewReference?.get() }
        return view != null && view.isLiveViewportUncovered() && view.isShown && view.windowVisibility == View.VISIBLE
    }

    /** Compose's covering history layers do not change the Android View's visibility. */
    fun setLiveViewportUncovered(view: View, uncovered: Boolean) {
        synchronized(lock) {
            val attached = viewReference?.get()
            if (attached === view) attached?.setLiveViewportUncovered(uncovered)
        }
    }
    private val mutableFrame: MutableStateFlow<TerminalTextFrame>
    private val mainHandler = Handler(Looper.getMainLooper())
    private var terminal = createTerminal(initialSize)
    private var scrollbackOffsetRows = 0L
    private var scrollbackAvailableRows = 0L
    private var scrollbackGenerationRows = 0L
    @Volatile private var historyCapture: NativeHistoryCapture? = null
    private var currentTheme = DEFAULT_THEME
    private var readOnly = true
    @Volatile private var disposed = false
    private var failedClosed = false
    private val historyNavigationGeneration = AtomicLong()
    private val historyFrameCache = object : LinkedHashMap<Long, NativeScrollbackTextFrame>(
        HISTORY_FRAME_CACHE_SIZE,
        0.75f,
        true,
    ) {
        override fun removeEldestEntry(
            eldest: MutableMap.MutableEntry<Long, NativeScrollbackTextFrame>,
        ): Boolean = size > HISTORY_FRAME_CACHE_SIZE
    }
    private var viewReference: WeakReference<NativeTerminalView>? = null
    private var acceptedInputListener: (() -> Unit)? = null
    private var listenerGeneration = 0L

    init {
        val firstFrame = terminal.renderTextAtScrollback(0)
        acceptScrollbackFrame(firstFrame)
        mutableFrame = MutableStateFlow(firstFrame.renderFrame)
        publishFrame(firstFrame.renderFrame, notify = false)
    }

    override val inputEvents: Flow<ByteArray> = inputChannel.receiveAsFlow()
    override val sizeEvents: Flow<TerminalSize> = emptyFlow()
    override val authoritativeSize: StateFlow<TerminalSize> = mutableSize.asStateFlow()
    override val title: StateFlow<String> = mutableTitle.asStateFlow()
    override val mouseReporting: StateFlow<Boolean> = mutableMouseReporting.asStateFlow()
    internal val frames: StateFlow<TerminalTextFrame> = mutableFrame.asStateFlow()

    fun currentTextFrame(): TerminalTextFrame? = frames.value

    data class InstalledCheckpoint(
        val sequence: Long,
        val size: TerminalSize,
    )

    /** Atomically replaces the live parser with an authenticated canonical Snapshot V2. */
    fun installCheckpoint(checkpoint: ByteArray): InstalledCheckpoint {
        require(checkpoint.isNotEmpty()) { "Native terminal checkpoint must not be empty" }
        require(checkpoint.size <= limits.maxCheckpointBytes) { "Native terminal checkpoint exceeds ${limits.maxCheckpointBytes} bytes" }
        // Inspect the same owned bytes that native validates, even if the caller later edits its array.
        val ownedCheckpoint = checkpoint.copyOf()
        val (replacement, candidateState) = try {
            val candidate = NativeTerminalHandle.importCheckpoint(bridge, ownedCheckpoint, limits, graphicsProfile)
            try {
                require(candidate.nextSourceSequence > FIRST_SOURCE_SEQUENCE) {
                    "Native terminal checkpoint must follow at least one ordered source event"
                }
                val frame = candidate.renderTextAtScrollback(0)
                validateCheckpointProfiles(ownedCheckpoint, frame.renderFrame)
                candidate to Triple(frame, reportsMouse(candidate),
                    if (bridge.supportsHostSignals) candidate.hostSignals() else dev.termlink.terminal.api.TerminalHostSignals())
            } catch (error: Throwable) {
                runCatching(candidate::close).exceptionOrNull()?.let(error::addSuppressed)
                throw error
            }
        } finally {
            ownedCheckpoint.fill(0)
        }
        val (frame, reporting, signals) = candidateState
        val nextSourceSequence = replacement.nextSourceSequence
        val previous = try {
            synchronized(lock) {
                checkActive()
                val owned = terminal
                releaseHistoryCapture()
                terminal = replacement
                pendingPlatformActions = emptyList()
                mutableHostSignals.value = signals
                mutableMouseReporting.value = reporting
                historyNavigationGeneration.incrementAndGet()
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
        synchronized(lock) {
            historyFrameCache.clear()
            acceptScrollbackFrame(frame)
            cacheHistoryFrame(frame)
        }
        publishFrame(frame.renderFrame, notify = true)
        return InstalledCheckpoint(
            sequence = nextSourceSequence - 1L,
            size = frame.renderFrame.size,
        )
    }

    private fun validateCheckpointProfiles(checkpoint: ByteArray, frame: TerminalTextFrame) {
        if (checkpoint.size < 104 || !checkpoint.copyOfRange(0, 8).contentEquals(SNAPSHOT_V2_MAGIC)) {
            throw NativeTerminalContractException("installCheckpoint.profile", "native import accepted an unknown snapshot envelope")
        }
        val header = ByteBuffer.wrap(checkpoint).order(ByteOrder.LITTLE_ENDIAN)
        val minor = header.getShort(10).toInt() and 0xffff
        if (header.getShort(8).toInt() != 2 || header.getShort(12).toInt() != minor ||
            header.getShort(14).toInt() != 104 ||
            if (graphicsProfile) minor != 22 else minor !in 0..21
        ) {
            throw NativeTerminalContractException("installCheckpoint.profile", "native import accepted an unsupported snapshot header")
        }
        val snapshotOutputProfile = if (header.getLong(64) and 0x10_0000L != 0L)
            TerminalOutputProfile.UTF8_WITH_STANDALONE_C1 else TerminalOutputProfile.UTF8
        if (frame.widthProfile != widthProfile || snapshotOutputProfile != outputProfile) {
            throw NativeTerminalException("installCheckpoint.profile", NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
                diagnostic = "Snapshot width/output profiles do not match the configured terminal")
        }
    }

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        require(seq > 0) { "Terminal sequence must be positive" }
        val owned = ByteArray(bytes.remaining())
        bytes.get(owned)
        require(owned.isNotEmpty()) { "Terminal output must not be empty" }
        applyOrdered { it.feedWithHostEffectsV2(seq, owned) }
    }

    override suspend fun applyBrokerCapturedOutput(seq: Long, output: ByteArray, canonicalCaptures: ByteArray) {
        require(graphicsProfile) { "Broker captured Output requires the graphics profile" }
        require(seq > 0 && output.size in 1..64 * 1024) { "Broker Output sequence or size is invalid" }
        NativeBrokerCaptureCodecV1.decode(canonicalCaptures).use { group ->
            require(group.sourceSequence == seq) { "Broker capture source sequence differs from Output" }
            NativeBrokerOutputV1(seq, output, group.captures).use { replay ->
                applyOrdered { it.applyBrokerCapturedV1(replay) }
            }
        }
    }

    override suspend fun advance(seq: Long) {
        applyOrdered { it.advanceWithHostEffectsV2(seq, 0) }
    }

    override suspend fun resize(seq: Long, columns: Int, rows: Int) {
        val size = TerminalSize(columns, rows)
        applyOrdered { it.resizeWithHostEffectsV2(seq, size) }
    }

    override suspend fun applyHostTheme(seq: Long, theme: dev.termlink.terminal.api.TerminalHostThemeV1) {
        applyOrdered { it.applyWithHostEffectsV2(NativeEventBatch.of(listOf(NativeSourceEvent.HostTheme(seq, theme)), limits)) }
    }

    override suspend fun applyHostGeometry(seq: Long, geometry: dev.termlink.terminal.api.TerminalHostGeometryV1) {
        applyOrdered { it.applyWithHostEffectsV2(NativeEventBatch.of(listOf(NativeSourceEvent.HostGeometry(seq, geometry)), limits)) }
    }

    override suspend fun reportGap(seq: Long) {
        synchronized(lock) {
            checkActive()
            if (failedClosed) return
            failedClosed = true
            try {
                discardMirrorHostEffects(terminal.reportGapWithHostEffectsV2(seq))
            } catch (error: NativeTerminalException) {
                if (error.status != NativeTerminalStatus.GAP) throw error
            } finally {
                readOnly = true
                mutableMouseReporting.value = false
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

    override fun setTouchMouseEnabled(enabled: Boolean) {
        touchMouseEnabled = enabled
    }

    override fun setKeyboardVisible(visible: Boolean) {
        val view = synchronized(lock) { if (disposed) null else viewReference?.get() } ?: return
        view.requestKeyboard(visible)
    }

    override fun submitMouseWheel(direction: dev.termlink.terminal.api.TerminalMouseWheelDirection): Boolean {
        val view = synchronized(lock) {
            if (!canAcceptInputLocked() || touchMouseEnabled || !mouseReporting.value) return false
            viewReference?.get()
        } ?: return false
        return view.submitMouseWheel(direction)
    }

    override fun clear() {
        synchronized(lock) {
            checkActive()
            replaceTerminal(mutableSize.value)
            failedClosed = false
            readOnly = true
            mutableInputDiagnostic.value = null
        }
    }

    override fun dispose() {
        val owned = synchronized(lock) {
            if (disposed) return
            disposed = true
            pendingPlatformActions = emptyList()
            releaseHistoryCapture()
            mutableMouseReporting.value = false
            mutableInputDiagnostic.value = null
            listenerGeneration = Math.addExact(listenerGeneration, 1)
            historyNavigationGeneration.incrementAndGet()
            acceptedInputListener = null
            historyFrameCache.clear()
            viewReference?.get()?.detachEngine()
            viewReference = null
            terminal
        }
        inputChannel.close()
        try { owned.close() } finally { runCatching { platformActionSink?.close() } }
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

    override fun visibleText(): String {
        // The published frame is immutable and StateFlow provides a thread-safe value read.
        // Do not take the reducer lock here: a native reduction may legitimately hold it for
        // seconds while the UI only needs the last frame that has already been published.
        checkActive()
        return frameText(mutableFrame.value)
    }

    override fun shellState(): dev.termlink.terminal.api.TerminalShellState = synchronized(lock) {
        checkActive()
        terminal.shellState()
    }

    override fun showShellCommand(commandId: Long): Boolean {
        val generation = historyNavigationGeneration.incrementAndGet()
        val frame = synchronized(lock) {
            checkActive()
            val command = terminal.shellState().commands.firstOrNull { it.id == commandId } ?: return false
            val point = command.prompt
            if (point.alternate || point.page != 1) return false
            if (historyCapture?.let { point.sequence >= it.info.revision } == true) releaseHistoryCapture()
            val existing = historyCapture
            val capture = existing ?: terminal.captureHistory()
            try {
                val offset = capture.resolveAnchor(point.lineId, point.cellOffset)
                val frame = capture.viewport(offset)
                if (historyNavigationGeneration.get() != generation) { if (existing == null) capture.close(); return false }
                historyCapture = capture
                historyFrameCache.clear()
                acceptScrollbackFrame(frame)
                frame
            } catch (error: Throwable) {
                if (existing == null) capture.close()
                if (error is NativeTerminalException && error.status == NativeTerminalStatus.HISTORY_STALE_ANCHOR) return false
                throw error
            }
        }
        publishFrame(frame.renderFrame, notify = true)
        return true
    }

    override fun currentSize(): TerminalSize = synchronized(lock) {
        checkActive()
        mutableSize.value
    }

    override fun measuredViewport(): dev.termlink.terminal.api.TerminalViewportMetrics? =
        viewReference?.get()?.measuredViewport()

    override fun setOnAcceptedLocalUserInputListener(listener: (() -> Unit)?) {
        synchronized(lock) {
            checkActive()
            acceptedInputListener = listener
            listenerGeneration = Math.addExact(listenerGeneration, 1)
        }
    }

    override fun resumeViewportAfterLocalInput() {
        val cursorVisible = synchronized(lock) {
            checkActive()
            historyCapture != null || mutableFrame.value.cursor.visible
        }
        if (cursorVisible) scrollToLatest()
    }

    override fun scrollToLatest() {
        historyNavigationGeneration.incrementAndGet()
        val result = synchronized(lock) {
            checkActive()
            val wasFrozen = historyCapture != null
            releaseHistoryCapture()
            val frame = if (scrollbackOffsetRows == 0L && !wasFrozen) {
                null
            } else {
                try {
                    terminal.renderTextAtScrollback(0).also {
                        acceptScrollbackFrame(it)
                        cacheHistoryFrame(it)
                    }
                } catch (error: NativeTerminalException) {
                    if (!error.isTemporaryPresentationFailure()) throw error
                    scrollbackOffsetRows = 0L
                    null
                }
            }
            frame to viewReference?.get()
        }
        result.first?.let { publishFrame(it.renderFrame, notify = true) }
        result.second?.let { view -> view.post(view::scrollToCursor) }
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
            val iterator = text.codePoints().iterator()
            while (iterator.hasNext()) {
                val codePoint = iterator.nextInt()
                if (codePoint in 0x80..0x9f) {
                    mutableInputDiagnostic.value = TerminalInputDiagnostic.androidC1Blocked(codePoint)
                    return false
                }
                val payload = encodeInputOrNull {
                    when (codePoint) {
                        '\n'.code, '\r'.code -> terminal.encodeKey(TerminalKey.ENTER)
                        '\t'.code -> terminal.encodeKey(TerminalKey.TAB)
                        '\b'.code, 0x7f -> terminal.encodeKey(TerminalKey.BACKSPACE)
                        0x1b -> terminal.encodeKey(TerminalKey.ESCAPE)
                        in 0x00..0x1a -> terminal.encodeCharacter(
                            codePoint = if (codePoint == 0) '@'.code else 'a'.code + codePoint - 1,
                            modifiers = TerminalInputModifiers(control = true),
                        )
                        in 0x1c..0x1f -> terminal.encodeCharacter(
                            codePoint = "\\]^_"[codePoint - 0x1c].code,
                            modifiers = TerminalInputModifiers(control = true),
                        )
                        else -> terminal.encodeCommittedText(String(Character.toChars(codePoint)))
                    }
                } ?: return false
                payloads += payload
            }
        }
        return emitPayloads(payloads)
    }

    override fun submitCodePoint(
        codePoint: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
        shiftedCodePoint: Int?,
    ): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull { terminal.encodeCharacter(
                codePoint = codePoint,
                modifiers = TerminalInputModifiers(
                    control = controlDown,
                    alt = altDown,
                    shift = shiftDown,
                ),
                shiftedCodePoint = shiftedCodePoint,
            ) } ?: return false
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
        return submitPhysicalKeyCode(
            keyCode = keyCode,
            controlDown = controlDown,
            altDown = altDown,
            shiftDown = shiftDown,
        )
    }

    internal fun submitPhysicalKeyCode(
        keyCode: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
        capsLock: Boolean = false,
        numLock: Boolean = false,
        eventType: TerminalKittyKeyEventType = TerminalKittyKeyEventType.PRESS,
    ): Boolean {
        val key = androidTerminalKey(keyCode) ?: return false
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull { terminal.encodeFunctionalKey(
                key,
                TerminalInputModifiers(
                    control = controlDown,
                    alt = altDown,
                    shift = shiftDown,
                    capsLock = capsLock,
                    numLock = numLock,
                ),
                eventType,
            ) } ?: return false
        }
        return emitPayloads(listOf(payload))
    }

    internal fun submitPhysicalCodePoint(
        codePoint: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
        capsLock: Boolean,
        numLock: Boolean,
        shiftedCodePoint: Int?,
        eventType: TerminalKittyKeyEventType,
    ): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull { terminal.encodeCharacter(
                codePoint = codePoint,
                modifiers = TerminalInputModifiers(
                    control = controlDown,
                    alt = altDown,
                    shift = shiftDown,
                    capsLock = capsLock,
                    numLock = numLock,
                ),
                shiftedCodePoint = shiftedCodePoint,
                eventType = eventType,
            ) } ?: return false
        }
        return emitPayloads(listOf(payload))
    }

    override fun submitPaste(text: String): Boolean {
        if (text.isEmpty()) return canAcceptInput()
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull { terminal.encodePaste(text.encodeToByteArray()) } ?: return false
        }
        return emitPayloads(listOf(payload))
    }

    internal fun submitFocus(focused: Boolean): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull(recordDiagnostic = false) { terminal.encodeFocus(focused) } ?: return false
        }
        return emitPayloads(listOf(payload), notifyAccepted = false)
    }

    internal fun submitMouse(input: TerminalMouseInput): Boolean {
        val payload = synchronized(lock) {
            if (!canAcceptInputLocked()) return false
            encodeInputOrNull { terminal.encodeMouse(input) } ?: return false
        }
        if (payload.isEmpty()) return false
        return emitPayloads(listOf(payload))
    }

    internal fun scrollHistoryRows(deltaRows: Int): NativeHistoryScrollResult {
        val generation = historyNavigationGeneration.get()
        return scrollHistoryRows(deltaRows, generation)
    }

    internal fun beginHistoryNavigation(): Long = historyNavigationGeneration.incrementAndGet()

    internal fun scrollHistoryRows(
        deltaRows: Int,
        navigationGeneration: Long,
    ): NativeHistoryScrollResult {
        if (deltaRows == 0) return NativeHistoryScrollResult.MOVED
        if (navigationGeneration != historyNavigationGeneration.get()) {
            return NativeHistoryScrollResult.CANCELLED
        }
        val frame = synchronized(lock) {
            checkActive()
            if (navigationGeneration != historyNavigationGeneration.get()) {
                return NativeHistoryScrollResult.CANCELLED
            }
            val requested = if (deltaRows > 0) {
                saturatedAdd(scrollbackOffsetRows, deltaRows.toLong())
                    .coerceAtMost(scrollbackAvailableRows)
            } else {
                (scrollbackOffsetRows + deltaRows.toLong()).coerceAtLeast(0L)
            }
            if (requested == scrollbackOffsetRows) {
                return if (deltaRows > 0) {
                    NativeHistoryScrollResult.OLDEST_BOUNDARY
                } else {
                    NativeHistoryScrollResult.LIVE_BOUNDARY
                }
            }
            try {
                val projected = if (requested == 0L) {
                    releaseHistoryCapture()
                    terminal.renderTextAtScrollback(0)
                } else if (bridge.supportsHistoryCapture) {
                    val capture = historyCapture ?: terminal.captureHistory().also { historyCapture = it; historyFrameCache.clear() }
                    capture.viewport(requested)
                } else cachedHistoryFrame(requested) ?: terminal.renderTextAtScrollback(requested)
                if (navigationGeneration != historyNavigationGeneration.get()) {
                    return NativeHistoryScrollResult.CANCELLED
                }
                cacheHistoryFrame(projected)
                projected.also(::acceptScrollbackFrame)
            } catch (error: NativeTerminalException) {
                if (error.status == NativeTerminalStatus.HISTORY_STALE_CAPTURE) return NativeHistoryScrollResult.CAPTURE_EXPIRED
                if (error.status == NativeTerminalStatus.LIMIT_EXCEEDED) return NativeHistoryScrollResult.CAPTURE_LIMIT
                if (!error.isTemporaryPresentationFailure()) throw error
                return NativeHistoryScrollResult.UNCHANGED
            }
        }
        publishFrame(frame.renderFrame, notify = true)
        return NativeHistoryScrollResult.MOVED
    }

    internal fun prefetchHistoryRows(deltaRows: Int, navigationGeneration: Long) {
        if (deltaRows == 0) return
        synchronized(lock) {
            checkActive()
            if (navigationGeneration != historyNavigationGeneration.get()) return
            if (historyCapture != null) return
            val distance = kotlin.math.abs(deltaRows.toLong())
            val requested = if (deltaRows > 0) {
                saturatedAdd(scrollbackOffsetRows, distance).coerceAtMost(scrollbackAvailableRows)
            } else {
                (scrollbackOffsetRows - distance).coerceAtLeast(0L)
            }
            if (requested == scrollbackOffsetRows || cachedHistoryFrame(requested) != null) return
            try {
                val frame = terminal.renderTextAtScrollback(requested)
                if (navigationGeneration == historyNavigationGeneration.get()) {
                    cacheHistoryFrame(frame)
                }
            } catch (error: NativeTerminalException) {
                if (!error.isTemporaryPresentationFailure()) throw error
            }
        }
    }

    internal fun historyOffsetRowsForTest(): Long = synchronized(lock) { scrollbackOffsetRows }

    internal fun theme(): TerminalThemeV1 = synchronized(lock) { currentTheme }

    internal fun detachView(view: NativeTerminalView) {
        synchronized(lock) {
            if (viewReference?.get() === view) viewReference = null
        }
    }

    internal fun attachView(view: NativeTerminalView): Boolean = synchronized(lock) {
        if (disposed) return false
        val attached = viewReference?.get()
        if (attached != null && attached !== view) return false
        viewReference = WeakReference(view)
        true
    }

    private fun applyOrdered(apply: (NativeTerminalHandle) -> TerminalHostEffectFrameV2) {
        val frame = synchronized(lock) {
            checkActive()
            check(!failedClosed) { "Native terminal renderer is failed closed" }
            try {
                pendingPlatformActions = emptyList()
                val effects = apply(terminal)
                discardMirrorHostEffects(effects)
                if (platformActionSink != null) pendingPlatformActions = effects.effects.filter { it.kind == TerminalHostEffectKindV2.PLATFORM_ACTION }
                if (bridge.supportsHostSignals) mutableHostSignals.value = terminal.hostSignals()
                mutableMouseReporting.value = reportsMouse(terminal)
                if (historyCapture != null) {
                    // The live mirror continues reducing and ACKing output. Its
                    // arrival never replaces the frozen browsing presentation.
                    try { mutableSize.value = terminal.renderTextAtScrollback(0).renderFrame.size }
                    catch (error: NativeTerminalException) { if (!error.isTemporaryPresentationFailure()) throw error }
                    return@synchronized null
                }
                historyFrameCache.clear()
                try {
                    val live = terminal.renderTextAtScrollback(0)
                    val generationDelta = if (live.generationRows >= scrollbackGenerationRows) {
                        live.generationRows - scrollbackGenerationRows
                    } else {
                        0L
                    }
                    val anchoredOffset = if (scrollbackOffsetRows == 0L) {
                        0L
                    } else {
                        saturatedAdd(scrollbackOffsetRows, generationDelta)
                            .coerceAtMost(live.availableRows)
                    }
                    val visible = if (anchoredOffset == 0L) {
                        live
                    } else {
                        terminal.renderTextAtScrollback(anchoredOffset)
                    }
                    acceptScrollbackFrame(visible)
                    cacheHistoryFrame(visible)
                    visible.renderFrame
                } catch (error: NativeTerminalException) {
                    if (error.isTemporaryPresentationFailure()) {
                        // The source event is already committed, but an incomplete parser token or
                        // synchronized output intentionally withholds presentation. Keep the last
                        // immutable frame visible and continue accepting the ordered stream; a
                        // later event will complete the token/update and publish the new frame.
                        null
                    } else {
                        throw error
                    }
                }
            } catch (error: Throwable) {
                failedClosed = true
                readOnly = true
                mutableMouseReporting.value = false
                throw error
            }
        }
        if (frame != null) publishFrame(frame, notify = true)
    }

    private fun discardMirrorHostEffects(hostEffects: TerminalHostEffectFrameV2) {
        check(hostEffects.formatVersion == TerminalHostEffectFrameV2.FORMAT_VERSION)
        check(hostEffects.effects.all { it.kind == TerminalHostEffectKindV2.PTY_REPLY || it.kind == TerminalHostEffectKindV2.RESIZE_REQUEST || it.kind == TerminalHostEffectKindV2.PLATFORM_ACTION })
        // This engine mirrors a remote Windows PTY. Replies were already emitted by the host,
        // so replaying them through inputEvents would duplicate terminal protocol responses.
    }

    private fun replaceTerminal(size: TerminalSize) {
        val previous = terminal
        val replacement = createTerminal(size)
        val frame = try {
            replacement.renderTextAtScrollback(0)
        } catch (error: Throwable) {
            runCatching(replacement::close).exceptionOrNull()?.let(error::addSuppressed)
            throw error
        }
        releaseHistoryCapture()
        terminal = replacement
        pendingPlatformActions = emptyList()
        mutableHostSignals.value = dev.termlink.terminal.api.TerminalHostSignals()
        mutableMouseReporting.value = false
        historyNavigationGeneration.incrementAndGet()
        historyFrameCache.clear()
        acceptScrollbackFrame(frame)
        cacheHistoryFrame(frame)
        try {
            previous.close()
        } catch (error: Throwable) {
            failedClosed = true
            readOnly = true
            throw error
        }
        publishFrame(frame.renderFrame, notify = true)
    }

    private fun createTerminal(size: TerminalSize): NativeTerminalHandle = if (!graphicsProfile && widthProfile == TerminalWidthProfile.LEGACY_V1) NativeTerminalHandle.create(
        bridge = bridge,
        size = size,
        initialNextSourceSequence = FIRST_SOURCE_SEQUENCE,
        outputProfile = outputProfile,
        historyCapacityRows = historyCapacityRows,
        limits = limits,
    ) else NativeTerminalHandle.createWithProfiles(
        bridge = bridge,
        size = size,
        initialNextSourceSequence = FIRST_SOURCE_SEQUENCE,
        outputProfile = outputProfile,
        widthProfile = widthProfile,
        historyCapacityRows = historyCapacityRows,
        limits = limits,
        graphicsProfile = graphicsProfile,
    )

    private fun publishFrame(frame: TerminalTextFrame, notify: Boolean) {
        mutableFrame.value = frame
        if (historyCapture == null) mutableSize.value = frame.size
        mutableTitle.value = frame.title.take(MAX_TITLE_CHARACTERS)
        if (notify) notifyView()
    }

    /** Pure core encoding probe: no output is queued and no remote mode is guessed from text. */
    private fun reportsMouse(handle: NativeTerminalHandle): Boolean =
        handle.encodeMouse(
            TerminalMouseInput.Press(
                TerminalMouseButton.LEFT,
                TerminalMousePosition(TerminalCellPosition(0, 0), TerminalPixelPosition(0, 0)),
                TerminalMouseButtons(left = true),
            ),
        ).isNotEmpty()

    private fun acceptScrollbackFrame(frame: NativeScrollbackTextFrame) {
        scrollbackAvailableRows = frame.availableRows
        scrollbackGenerationRows = frame.generationRows
        scrollbackOffsetRows = frame.appliedOffsetRows
    }

    private fun cacheHistoryFrame(frame: NativeScrollbackTextFrame) {
        historyFrameCache[frame.appliedOffsetRows] = frame
    }

    private fun cachedHistoryFrame(offsetRows: Long): NativeScrollbackTextFrame? =
        historyFrameCache[offsetRows]?.takeIf { frame ->
            frame.availableRows == scrollbackAvailableRows &&
                frame.generationRows == scrollbackGenerationRows &&
                frame.renderFrame.presentationRevision == terminal.presentationRevision
        }

    private fun releaseHistoryCapture() {
        val capture = historyCapture ?: return
        historyCapture = null
        historyFrameCache.clear()
        capture.close()
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
        if (emitted && notifyAccepted) {
            resumeViewportAfterLocalInput()
            notifyAcceptedInput()
        }
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

    internal fun canAcceptInput(): Boolean = synchronized(lock) { canAcceptInputLocked() }

    private fun canAcceptInputLocked(): Boolean = !disposed && !readOnly && !failedClosed

    private inline fun encodeInputOrNull(
        recordDiagnostic: Boolean = true,
        block: () -> ByteArray,
    ): ByteArray? = try {
        block().also { if (recordDiagnostic) mutableInputDiagnostic.value = null }
    } catch (_: IllegalArgumentException) {
        null
    } catch (error: NativeTerminalException) {
        if (error.status == NativeTerminalStatus.INVALID_ARGUMENT ||
            error.status == NativeTerminalStatus.UNSUPPORTED ||
            error.status == NativeTerminalStatus.LIMIT_EXCEEDED
        ) {
            if (recordDiagnostic) mutableInputDiagnostic.value = error.terminalInputDiagnostic
            null
        } else throw error
    }

    private fun checkActive() {
        check(!disposed) { "Terminal engine is disposed" }
    }

    internal companion object {
        const val DEFAULT_COLUMNS = 80
        const val DEFAULT_ROWS = 24
        const val DEFAULT_HISTORY_CAPACITY_ROWS = 2_000L
        const val HISTORY_FRAME_CACHE_SIZE = 8
        const val FIRST_SOURCE_SEQUENCE = 1L
        const val MAX_TITLE_CHARACTERS = 4_096
        const val TERMINAL_KEYMOD_ALT = Int.MIN_VALUE
        const val TERMINAL_KEYMOD_CTRL = 0x4000_0000
        const val TERMINAL_KEYMOD_SHIFT = 0x2000_0000
        private val SNAPSHOT_V2_MAGIC = "TLSNP2S\u0000".encodeToByteArray()

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

private fun saturatedAdd(value: Long, increment: Long): Long {
    require(value >= 0 && increment >= 0) { "saturated addition requires non-negative values" }
    return if (Long.MAX_VALUE - value < increment) Long.MAX_VALUE else value + increment
}

private fun NativeTerminalException.isTemporaryPresentationFailure(): Boolean =
    status == NativeTerminalStatus.SNAPSHOT_NOT_ELIGIBLE ||
        status == NativeTerminalStatus.PRESENTATION_DEFERRED

private fun androidTerminalKey(keyCode: Int): TerminalKittyFunctionalKey? = when (keyCode) {
    KeyEvent.KEYCODE_ENTER -> TerminalKittyFunctionalKey.ENTER
    KeyEvent.KEYCODE_NUMPAD_ENTER -> TerminalKittyFunctionalKey.KEYPAD_ENTER
    KeyEvent.KEYCODE_TAB -> TerminalKittyFunctionalKey.TAB
    KeyEvent.KEYCODE_DEL -> TerminalKittyFunctionalKey.BACKSPACE
    KeyEvent.KEYCODE_ESCAPE -> TerminalKittyFunctionalKey.ESCAPE
    KeyEvent.KEYCODE_FORWARD_DEL -> TerminalKittyFunctionalKey.DELETE
    KeyEvent.KEYCODE_INSERT -> TerminalKittyFunctionalKey.INSERT
    KeyEvent.KEYCODE_MOVE_HOME -> TerminalKittyFunctionalKey.HOME
    KeyEvent.KEYCODE_MOVE_END -> TerminalKittyFunctionalKey.END
    KeyEvent.KEYCODE_PAGE_UP -> TerminalKittyFunctionalKey.PAGE_UP
    KeyEvent.KEYCODE_PAGE_DOWN -> TerminalKittyFunctionalKey.PAGE_DOWN
    KeyEvent.KEYCODE_DPAD_UP -> TerminalKittyFunctionalKey.ARROW_UP
    KeyEvent.KEYCODE_DPAD_DOWN -> TerminalKittyFunctionalKey.ARROW_DOWN
    KeyEvent.KEYCODE_DPAD_LEFT -> TerminalKittyFunctionalKey.ARROW_LEFT
    KeyEvent.KEYCODE_DPAD_RIGHT -> TerminalKittyFunctionalKey.ARROW_RIGHT
    in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 ->
        TerminalKittyFunctionalKey.entries[
            TerminalKittyFunctionalKey.F1.ordinal + keyCode - KeyEvent.KEYCODE_F1
        ]
    in KeyEvent.KEYCODE_NUMPAD_0..KeyEvent.KEYCODE_NUMPAD_9 ->
        TerminalKittyFunctionalKey.entries[
            TerminalKittyFunctionalKey.KEYPAD_0.ordinal + keyCode - KeyEvent.KEYCODE_NUMPAD_0
        ]
    KeyEvent.KEYCODE_NUMPAD_DOT -> TerminalKittyFunctionalKey.KEYPAD_DECIMAL
    KeyEvent.KEYCODE_NUMPAD_DIVIDE -> TerminalKittyFunctionalKey.KEYPAD_DIVIDE
    KeyEvent.KEYCODE_NUMPAD_MULTIPLY -> TerminalKittyFunctionalKey.KEYPAD_MULTIPLY
    KeyEvent.KEYCODE_NUMPAD_SUBTRACT -> TerminalKittyFunctionalKey.KEYPAD_SUBTRACT
    KeyEvent.KEYCODE_NUMPAD_ADD -> TerminalKittyFunctionalKey.KEYPAD_ADD
    KeyEvent.KEYCODE_NUMPAD_EQUALS -> TerminalKittyFunctionalKey.KEYPAD_EQUAL
    KeyEvent.KEYCODE_NUMPAD_COMMA -> TerminalKittyFunctionalKey.KEYPAD_SEPARATOR
    KeyEvent.KEYCODE_CAPS_LOCK -> TerminalKittyFunctionalKey.CAPS_LOCK
    KeyEvent.KEYCODE_SCROLL_LOCK -> TerminalKittyFunctionalKey.SCROLL_LOCK
    KeyEvent.KEYCODE_NUM_LOCK -> TerminalKittyFunctionalKey.NUM_LOCK
    KeyEvent.KEYCODE_SYSRQ -> TerminalKittyFunctionalKey.PRINT_SCREEN
    KeyEvent.KEYCODE_BREAK -> TerminalKittyFunctionalKey.PAUSE
    KeyEvent.KEYCODE_MENU -> TerminalKittyFunctionalKey.MENU
    else -> null
}

internal fun frameText(frame: TerminalTextFrame): String = buildString {
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

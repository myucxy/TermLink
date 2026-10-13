package dev.termlink.terminal.api

import kotlinx.coroutines.flow.Flow
import java.nio.ByteBuffer

data class TerminalSize(
    val columns: Int,
    val rows: Int,
) {
    init {
        require(columns > 0) { "columns must be positive" }
        require(rows > 0) { "rows must be positive" }
    }
}
@JvmInline
value class ArgbColor(val value: Int)

data class TerminalThemeV1(
    val name: String,
    val foreground: ArgbColor,
    val background: ArgbColor,
    val cursor: ArgbColor,
    val selectionBackground: ArgbColor,
    val selectionForeground: ArgbColor?,
    val ansiColors: List<ArgbColor>,
    val boldAsBright: Boolean,
    val fontFamilyHint: String?,
) {
    init {
        require(ansiColors.size == ANSI_COLOR_COUNT) {
            "TerminalThemeV1 requires exactly $ANSI_COLOR_COUNT ANSI colors"
        }
    }

    private companion object {
        const val ANSI_COLOR_COUNT = 16
    }
}

/**
 * Renderer-independent terminal boundary.
 *
 * Implementations must consume [bytes] before [feed] returns and must not retain or mutate the
 * supplied buffer. Network and replay layers intentionally do not expose decoded text.
 */
interface TerminalEngine {
    val inputEvents: Flow<ByteArray>
    val sizeEvents: Flow<TerminalSize>

    suspend fun feed(seq: Long, bytes: ByteBuffer)

    /** Applies an already authenticated broker capture group with its ordered raw Output. */
    suspend fun applyBrokerCapturedOutput(seq: Long, output: ByteArray, canonicalCaptures: ByteArray): Unit =
        error("This terminal engine does not support broker captured Output")

    /** Consumes one ordered non-output source event without changing terminal presentation. */
    suspend fun advance(seq: Long) = Unit

    /** Live foreground notice only; never called during checkpoint/history replay. */
    fun notifyHostResizeResult(result: TerminalHostResizeResultV1) = Unit

    /** Called once after a newly applied live Output; absent for history, duplicates and checkpoints. */
    fun notifyLiveOutputApplied(sequence: Long) = Unit

    /** Applies one ordered remote grid change. */
    suspend fun resize(seq: Long, columns: Int, rows: Int) = resize(columns, rows)

    /** Selected-host geometry is ordered state, never a local view measurement. */
    suspend fun applyHostGeometry(seq: Long, geometry: TerminalHostGeometryV1): Unit =
        error("This terminal engine does not support ordered host geometry")

    suspend fun applyHostTheme(seq: Long, theme: TerminalHostThemeV1): Unit =
        error("This terminal engine does not support ordered host theme")

    /** Permanently fails the current renderer generation closed at an unrecoverable gap. */
    suspend fun reportGap(seq: Long) = Unit

    fun resize(columns: Int, rows: Int)

    fun applyTheme(theme: TerminalThemeV1)

    fun setReadOnly(readOnly: Boolean)

    fun clear()

    fun dispose()
}

/**
 * Content-free diagnostics exposed by a terminal implementation when an ordered apply fails.
 * Transport logging may record these fields, but must never log terminal input or output bytes.
 */
interface TerminalEngineFailureDiagnostics {
    val terminalOperation: String
    val terminalStatusName: String
    val terminalStatusCode: Int
    /** Escaped, redacted evidence for local error UI only; never transport logging. */
    val terminalProtocolDiagnostic: TerminalProtocolDiagnostic? get() = null
    /** Bounded input encoding evidence for local error UI only; never raw paste or text. */
    val terminalInputDiagnostic: TerminalInputDiagnostic? get() = null
}

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

    fun resize(columns: Int, rows: Int)

    fun applyTheme(theme: TerminalThemeV1)

    fun setReadOnly(readOnly: Boolean)

    fun clear()

    fun dispose()
}

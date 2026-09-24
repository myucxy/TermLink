package dev.termlink.terminal.api

import android.content.Context
import android.view.View
import kotlinx.coroutines.flow.StateFlow

/**
 * Interactive Android viewport shared by the production native engine and detached projections.
 *
 * The ordered [TerminalEngine] methods remain the only way remote state changes. View methods
 * affect presentation or emit semantic input and must never mutate the remote source cursor.
 */
interface TerminalViewportEngine : TerminalEngine {
    val authoritativeSize: StateFlow<TerminalSize>
    val title: StateFlow<String>

    fun createView(
        context: Context,
        textSizeSp: Float = 13f,
        lineSpacingMultiplier: Float = 1f,
        onScrollPastTop: (() -> Unit)? = null,
        onScrollPastBottom: (() -> Unit)? = null,
    ): View

    fun visibleText(): String

    fun currentSize(): TerminalSize

    /** Measured presentation capacity only; never resizes the authoritative grid. Call on UI thread. */
    fun measuredViewport(): TerminalViewportMetrics? = null

    fun setOnAcceptedLocalUserInputListener(listener: (() -> Unit)?)

    /**
     * Applies the viewport policy for accepted local input.
     *
     * Engines may keep the current viewport when a full-screen application has
     * deliberately hidden its cursor. Explicit "return to live" actions must
     * continue to use [scrollToLatest].
     */
    fun resumeViewportAfterLocalInput() = scrollToLatest()

    fun scrollToLatest()

    fun scrollPastTopBoundary()

    fun submitText(text: String): Boolean

    fun submitCodePoint(
        codePoint: Int,
        controlDown: Boolean = false,
        altDown: Boolean = false,
        shiftDown: Boolean = false,
        shiftedCodePoint: Int? = null,
    ): Boolean

    fun submitKeyCode(keyCode: Int, modifiers: Int = 0): Boolean

    fun submitKeyCode(
        keyCode: Int,
        controlDown: Boolean,
        altDown: Boolean,
        shiftDown: Boolean,
    ): Boolean

    fun submitPaste(text: String): Boolean
}

data class TerminalViewportMetrics(
    val size: TerminalSize,
    val widthPx: Int,
    val heightPx: Int,
    val cellWidthPx: Float,
    val cellHeightPx: Float,
)

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

    fun setOnAcceptedLocalUserInputListener(listener: (() -> Unit)?)

    fun scrollToLatest()

    fun scrollPastTopBoundary()

    fun submitText(text: String): Boolean

    fun submitCodePoint(
        codePoint: Int,
        controlDown: Boolean = false,
        altDown: Boolean = false,
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

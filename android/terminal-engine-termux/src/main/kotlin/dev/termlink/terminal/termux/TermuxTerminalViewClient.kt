package dev.termlink.terminal.termux

import android.content.Context
import android.util.Log
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.inputmethod.InputMethodManager
import com.termux.view.TerminalSessionPort
import com.termux.view.TerminalView
import com.termux.view.TerminalViewClient
import kotlin.math.roundToInt

internal class TermuxTerminalViewClient(
    private val view: TerminalView,
    private val baseTextSizePx: Int,
) : TerminalViewClient {
    override fun onScale(scale: Float): Float {
        val bounded = scale.coerceIn(MIN_SCALE, MAX_SCALE)
        view.setTextSize((baseTextSizePx * bounded).roundToInt().coerceAtLeast(1))
        return bounded
    }

    override fun onSingleTapUp(event: MotionEvent) {
        view.requestFocus()
        val inputMethod = view.context.getSystemService(Context.INPUT_METHOD_SERVICE)
            as? InputMethodManager
        inputMethod?.showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)
    }

    override fun shouldBackButtonBeMappedToEscape(): Boolean = false

    override fun shouldEnforceCharBasedInput(): Boolean = false

    override fun shouldUseCtrlSpaceWorkaround(): Boolean = false

    override fun isTerminalViewSelected(): Boolean = view.hasFocus()

    override fun copyModeChanged(copyMode: Boolean) = Unit

    override fun onKeyDown(
        keyCode: Int,
        event: KeyEvent,
        session: TerminalSessionPort,
    ): Boolean = false

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean = false

    override fun onLongPress(event: MotionEvent): Boolean = false

    override fun readControlKey(): Boolean = false

    override fun readAltKey(): Boolean = false

    override fun readShiftKey(): Boolean = false

    override fun readFnKey(): Boolean = false

    override fun onCodePoint(
        codePoint: Int,
        ctrlDown: Boolean,
        session: TerminalSessionPort,
    ): Boolean = false

    override fun onEmulatorSet() = Unit

    override fun logError(tag: String, message: String) {
        Log.e(tag, message)
    }

    override fun logWarn(tag: String, message: String) {
        Log.w(tag, message)
    }

    override fun logInfo(tag: String, message: String) {
        Log.i(tag, message)
    }

    override fun logDebug(tag: String, message: String) {
        Log.d(tag, message)
    }

    override fun logVerbose(tag: String, message: String) {
        Log.v(tag, message)
    }

    override fun logStackTraceWithMessage(tag: String, message: String, error: Exception) {
        Log.e(tag, message, error)
    }

    override fun logStackTrace(tag: String, error: Exception) {
        Log.e(tag, "Terminal view failure", error)
    }

    private companion object {
        const val MIN_SCALE = 0.6f
        const val MAX_SCALE = 2.0f
    }
}

package dev.termlink.product

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.SystemClock
import android.widget.Toast
import dev.termlink.harness.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean

internal data class TerminalClipboardPermit(val leaseId: String, val version: Long)

/** Main-thread only. A request is never an automatic copy permission. */
internal class TerminalClipboardPrompt(
    private val context: Context,
    private val scope: CoroutineScope,
    private val activity: () -> Activity?,
    private val permit: () -> TerminalClipboardPermit?,
    private val visible: () -> Boolean,
    private val active: () -> Boolean,
) {
    private var dialog: AlertDialog? = null
    private var watcher: Job? = null

    fun offer(text: String, expected: TerminalClipboardPermit?) {
        val owner = activity()
        if (owner == null) return
        if (expected == null || !eligible(expected) || !visible() || !owner.hasWindowFocus() || owner.isFinishing || owner.isDestroyed ||
            !busy.compareAndSet(false, true)) return
        val began = SystemClock.elapsedRealtime()
        try {
            val preview = clipboardPreview(text)
            val next = AlertDialog.Builder(owner).setTitle(R.string.terminal_clipboard_title)
                .setMessage(context.getString(R.string.terminal_clipboard_message, text.length, preview))
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.terminal_clipboard_accept) { _, _ ->
                    if (eligible(expected) && visible() && dialog?.window?.decorView?.hasWindowFocus() == true &&
                        SystemClock.elapsedRealtime() - began < 30000) {
                        runCatching { context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("TermLink CLI", text)) }
                            .onFailure { Toast.makeText(context, R.string.terminal_clipboard_failed, Toast.LENGTH_SHORT).show() }
                    } else Toast.makeText(context, R.string.terminal_clipboard_expired, Toast.LENGTH_SHORT).show()
                }.create()
            dialog = next
            next.setOnDismissListener { dialog = null; watcher?.cancel(); watcher = null; busy.set(false) }
            next.show()
            watcher = scope.launch {
                while (dialog === next) {
                    delay(100)
                    val focused = next.window?.decorView?.hasWindowFocus() == true
                    if (!eligible(expected) || !visible() || owner.isFinishing || owner.isDestroyed ||
                        (!focused && SystemClock.elapsedRealtime() - began >= FOCUS_ACQUISITION_GRACE_MILLIS) ||
                        SystemClock.elapsedRealtime() - began >= 30000) {
                        close(); break
                    }
                }
            }
        } catch (_: Exception) { close(); busy.set(false) }
    }

    private fun eligible(expected: TerminalClipboardPermit) = active() &&
        context.terminalActionPreferences().getBoolean(TERMINAL_CLIPBOARD, false) && permit() == expected

    fun close() { watcher?.cancel(); watcher = null; dialog?.dismiss(); dialog = null }
    companion object {
        private val busy = AtomicBoolean()
        private const val FOCUS_ACQUISITION_GRACE_MILLIS = 3_000L
    }
}

internal fun clipboardPreview(text: String): String = buildString {
    text.take(240).forEach { c ->
        when {
            c == '\n' -> append('\n')
            c == '\t' -> append("\\t")
            c.isISOControl() || Character.getType(c) == Character.FORMAT.toInt() -> append("\\u%04X".format(c.code))
            else -> append(c)
        }
    }
    if (text.length > 240) append("…")
}

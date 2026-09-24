package dev.termlink.product

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.harness.R
import dev.termlink.terminal.api.TerminalViewportMetrics
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

// This is the exact IME-only padding allocated by the session scaffold, after
// navigation/bottom-bar overlap. The actual View has already excluded the dock.
internal val LocalSessionImeAvoidancePixels = compositionLocalOf { 0 }

internal fun viewportWithKeyboardHidden(
    measured: TerminalViewportMetrics?,
    imeAvoidancePixels: Int,
): TerminalViewportMetrics? {
    require(imeAvoidancePixels >= 0)
    measured ?: return null
    val height = measured.heightPx.toLong() + imeAvoidancePixels
    if (height > Int.MAX_VALUE || !measured.cellHeightPx.isFinite() || measured.cellHeightPx <= 0) return null
    val rows = kotlin.math.floor(height.toFloat() / measured.cellHeightPx).toInt()
    if (rows !in 2..1000) return null
    return measured.copy(size = TerminalSize(measured.size.columns, rows), heightPx = height.toInt())
}

@Composable
internal fun SessionWindowSizeMenuItems(
    sessionId: UUID,
    metrics: TerminalViewportMetrics?,
    enabled: Boolean,
    onResize: suspend (UUID, TerminalViewportMetrics?) -> TerminalSize,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var busy by remember(sessionId) { mutableStateOf(false) }
    var result by remember(sessionId) { mutableStateOf<String?>(null) }
    fun request(restore: Boolean) {
        if (busy || !enabled || !restore && metrics == null) return
        busy = true
        result = null
        scope.launch {
            try {
                val size = onResize(sessionId, if (restore) null else metrics)
                result = context.getString(R.string.session_resize_applied, size.columns, size.rows)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                result = context.getString(resizeErrorResource(error.message))
            } finally { busy = false }
        }
    }
    HorizontalDivider()
    Text(stringResource(R.string.session_window_size_title),
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp).semantics { heading() },
        style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    DropdownMenuItem(
        text = {
            Column {
                Text(stringResource(R.string.session_resize_fit))
                Text(metrics?.let { stringResource(R.string.session_resize_preview,
                    it.size.columns, it.size.rows, it.widthPx, it.heightPx) }
                    ?: stringResource(R.string.session_resize_unmeasured), style = MaterialTheme.typography.bodySmall)
            }
        },
        modifier = Modifier.testTag("session-resize-fit:$sessionId"),
        enabled = enabled && !busy && metrics != null,
        onClick = { request(false) },
    )
    DropdownMenuItem(
        text = { Text(stringResource(R.string.session_resize_restore)) },
        modifier = Modifier.testTag("session-resize-restore:$sessionId"),
        enabled = enabled && !busy,
        onClick = { request(true) },
    )
    val feedback = if (busy) stringResource(R.string.session_resize_pending) else result
        ?: if (!enabled) stringResource(R.string.session_resize_control_required) else null
    feedback?.let { Text(it, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)
        .semantics { liveRegion = LiveRegionMode.Polite }
        .testTag("session-resize-result:$sessionId"), style = MaterialTheme.typography.bodySmall) }
    HorizontalDivider()
}

internal fun resizeErrorResource(reason: String?): Int = when (reason) {
    "RESIZE_UNSUPPORTED" -> R.string.session_resize_upgrade
    "RESIZE_SERVER_UPGRADE_REQUIRED" -> R.string.session_resize_server_upgrade
    "WINDOW_RESIZE_UNSUPPORTED" -> R.string.session_resize_window_unsupported
    "NO_DESKTOP_SIZE" -> R.string.session_resize_no_restore
    "CONTROL_LEASE_REJECTED", "LOCAL_INPUT_PRIORITY", "RESIZE_INTERRUPTED" -> R.string.session_resize_control_lost
    "RESIZE_TIMEOUT" -> R.string.session_resize_timeout
    "RESIZE_BUSY" -> R.string.session_resize_busy
    "RESIZE_EXPIRED", "RESIZE_EXPIRED_OR_INVALID" -> R.string.session_resize_expired
    else -> R.string.session_resize_failed
}

package dev.termlink.product

import android.content.Context
import androidx.compose.foundation.layout.Row
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import dev.termlink.harness.R
import java.util.UUID

private const val TOUCH_MOUSE_SESSIONS = "touch_mouse_sessions"
private fun Context.inputPreferences() = getSharedPreferences("termlink_product_preferences", Context.MODE_PRIVATE)

internal fun loadTouchMouseEnabled(context: Context, sessionId: UUID): Boolean =
    context.inputPreferences().getStringSet(TOUCH_MOUSE_SESSIONS, emptySet()).orEmpty().contains(sessionId.toString())

internal fun saveTouchMouseEnabled(context: Context, sessionId: UUID, enabled: Boolean) {
    val preferences = context.inputPreferences()
    val sessions = preferences.getStringSet(TOUCH_MOUSE_SESSIONS, emptySet()).orEmpty().toMutableSet()
    if (enabled) sessions.add(sessionId.toString()) else sessions.remove(sessionId.toString())
    preferences.edit().putStringSet(TOUCH_MOUSE_SESSIONS, sessions).apply()
}

internal fun retainTouchMouseSessions(context: Context, sessionIds: List<String>) {
    val preferences = context.inputPreferences()
    val sessions = preferences.getStringSet(TOUCH_MOUSE_SESSIONS, emptySet()).orEmpty()
    preferences.edit().putStringSet(TOUCH_MOUSE_SESSIONS, sessions.intersect(sessionIds.toSet())).apply()
}

@Composable
internal fun SessionInputMenuItems(
    controlling: Boolean,
    mouseReporting: Boolean,
    touchMouseEnabled: Boolean,
    onTouchMouseChanged: (Boolean) -> Unit,
) {
    var showHelp by remember { mutableStateOf(false) }
    val mouseToggleEnabled = touchMouseEnabled || controlling && mouseReporting
    HorizontalDivider()
    Row(verticalAlignment = Alignment.CenterVertically) {
        DropdownMenuItem(
            modifier = Modifier.weight(1f),
            text = { Text(stringResource(R.string.session_touch_mouse)) },
            trailingIcon = { Switch(checked = touchMouseEnabled, onCheckedChange = null, enabled = mouseToggleEnabled) },
            enabled = mouseToggleEnabled,
            onClick = { onTouchMouseChanged(!touchMouseEnabled) },
        )
        IconButton(onClick = { showHelp = true }) {
            Icon(Icons.AutoMirrored.Filled.HelpOutline,
                contentDescription = stringResource(R.string.session_input_title))
        }
    }
    if (showHelp) AlertDialog(
        onDismissRequest = { showHelp = false },
        title = { Text(stringResource(R.string.session_input_title)) },
        text = { Text(stringResource(when {
            !controlling -> R.string.session_input_read_only
            !mouseReporting -> R.string.session_mouse_unavailable
            touchMouseEnabled -> R.string.session_mouse_enabled_detail
            else -> R.string.session_mouse_disabled_detail
        })) },
        confirmButton = { TextButton(onClick = { showHelp = false }) {
            Text(stringResource(android.R.string.ok))
        } },
    )
}

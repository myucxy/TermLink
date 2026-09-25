package dev.termlink.product

import android.content.Context
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
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
    keyboardVisible: Boolean,
    onTouchMouseChanged: (Boolean) -> Unit,
    onKeyboardToggle: () -> Unit,
) {
    val mouseToggleEnabled = touchMouseEnabled || controlling && mouseReporting
    HorizontalDivider()
    Text(stringResource(R.string.session_input_title),
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp).semantics { heading() },
        style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    DropdownMenuItem(
        text = {
            Column {
                Text(stringResource(R.string.session_touch_mouse))
                Text(stringResource(when {
                    !controlling -> R.string.session_input_read_only
                    !mouseReporting -> R.string.session_mouse_unavailable
                    touchMouseEnabled -> R.string.session_mouse_enabled_detail
                    else -> R.string.session_mouse_disabled_detail
                }), style = MaterialTheme.typography.bodySmall)
            }
        },
        leadingIcon = { Icon(Icons.Filled.Mouse, contentDescription = null) },
        trailingIcon = { Switch(checked = touchMouseEnabled, onCheckedChange = null,
            enabled = mouseToggleEnabled) },
        enabled = mouseToggleEnabled,
        onClick = { onTouchMouseChanged(!touchMouseEnabled) },
    )
    DropdownMenuItem(
        text = { Text(stringResource(if (keyboardVisible) R.string.session_hide_keyboard else R.string.session_show_keyboard)) },
        leadingIcon = { Icon(Icons.Filled.Keyboard, contentDescription = null) },
        enabled = controlling || keyboardVisible,
        onClick = onKeyboardToggle,
    )
}

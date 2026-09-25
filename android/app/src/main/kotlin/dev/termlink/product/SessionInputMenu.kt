package dev.termlink.product

import android.content.Context
import android.content.SharedPreferences
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.edit
import dev.termlink.harness.R
import java.util.UUID

private const val TOUCH_MOUSE_SESSIONS = "touch_mouse_sessions"
private const val WHEEL_ON_LEFT = "terminal_wheel_on_left"
private fun Context.inputPreferences() = getSharedPreferences("termlink_product_preferences", Context.MODE_PRIVATE)

internal fun loadWheelOnLeft(context: Context): Boolean = context.inputPreferences().getBoolean(WHEEL_ON_LEFT, false)

internal fun saveWheelOnLeft(context: Context, onLeft: Boolean) {
    context.inputPreferences().edit { putBoolean(WHEEL_ON_LEFT, onLeft) }
}

@Composable
internal fun rememberWheelOnLeft(context: Context): State<Boolean> {
    val preferences = remember(context) { context.inputPreferences() }
    val state = remember(preferences) { mutableStateOf(preferences.getBoolean(WHEEL_ON_LEFT, false)) }
    DisposableEffect(preferences) {
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
            if (key == WHEEL_ON_LEFT || key == null) state.value = preferences.getBoolean(WHEEL_ON_LEFT, false)
        }
        preferences.registerOnSharedPreferenceChangeListener(listener)
        state.value = preferences.getBoolean(WHEEL_ON_LEFT, false)
        onDispose { preferences.unregisterOnSharedPreferenceChangeListener(listener) }
    }
    return state
}

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
    wheelOnLeft: Boolean,
    onWheelSideChanged: (Boolean) -> Unit,
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
    Row(modifier = Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(modifier = Modifier.weight(1f)) {
            Text(stringResource(R.string.session_wheel_side), style = MaterialTheme.typography.bodyMedium)
            Text(stringResource(R.string.session_wheel_side_hint), style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        SingleChoiceSegmentedButtonRow(modifier = Modifier.width(120.dp).height(48.dp)) {
            listOf(true, false).forEachIndexed { index, onLeft ->
                SegmentedButton(
                    selected = wheelOnLeft == onLeft,
                    onClick = { onWheelSideChanged(onLeft) },
                    shape = SegmentedButtonDefaults.itemShape(index, 2),
                    modifier = Modifier.height(36.dp),
                    icon = {},
                ) {
                    Text(stringResource(if (onLeft) R.string.session_wheel_left else R.string.session_wheel_right),
                        style = MaterialTheme.typography.labelMedium, maxLines = 1)
                }
            }
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

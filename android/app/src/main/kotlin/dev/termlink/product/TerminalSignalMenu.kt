package dev.termlink.product

import android.Manifest
import android.content.SharedPreferences
import android.os.Build
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.scale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import dev.termlink.harness.R
import dev.termlink.terminal.api.TerminalViewportEngine

@Composable
internal fun TerminalSignalMenuItems() {
    val context = LocalContext.current
    val preferences = remember(context) { context.terminalActionPreferences() }
    var alerts by remember { mutableStateOf(preferences.getBoolean(TERMINAL_ALERTS, false)) }
    var sounds by remember { mutableStateOf(preferences.getBoolean(TERMINAL_SOUNDS, false)) }
    var clipboard by remember { mutableStateOf(preferences.getBoolean(TERMINAL_CLIPBOARD, false)) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (!granted) Toast.makeText(context, R.string.terminal_notification_denied, Toast.LENGTH_LONG).show()
    }
    DisposableEffect(preferences) {
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ ->
            alerts = preferences.getBoolean(TERMINAL_ALERTS, false)
            sounds = preferences.getBoolean(TERMINAL_SOUNDS, false)
            clipboard = preferences.getBoolean(TERMINAL_CLIPBOARD, false)
        }
        preferences.registerOnSharedPreferenceChangeListener(listener)
        onDispose { preferences.unregisterOnSharedPreferenceChangeListener(listener) }
    }
    HorizontalDivider()
    Text(stringResource(R.string.terminal_signals_device), Modifier.padding(horizontal = 16.dp, vertical = 2.dp),
        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    listOf(Triple(TERMINAL_ALERTS, R.string.terminal_alerts, alerts), Triple(TERMINAL_SOUNDS, R.string.terminal_sounds, sounds),
        Triple(TERMINAL_CLIPBOARD, R.string.terminal_clipboard_requests, clipboard))
        .forEach { (key, label, value) ->
            Row(Modifier.fillMaxWidth().heightIn(min = 40.dp).toggleable(value, role = Role.Switch) { enabled ->
                preferences.edit().putBoolean(key, enabled).apply()
                if (key == TERMINAL_ALERTS && enabled && Build.VERSION.SDK_INT >= 33) permission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(label), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                Switch(value, null, Modifier.height(40.dp).scale(0.75f))
            }
        }
}

@Composable
internal fun TerminalProgressLabel(engine: TerminalViewportEngine?) {
    val progress = engine?.hostSignals?.collectAsState()?.value ?: return
    if (progress.progressState == 0) return
    val text = when (progress.progressState) {
        2 -> stringResource(R.string.terminal_progress_error, progress.progressPercent)
        3 -> stringResource(R.string.terminal_progress_busy)
        4 -> stringResource(R.string.terminal_progress_paused, progress.progressPercent)
        else -> "${progress.progressPercent}%"
    }
    Text(text, style = MaterialTheme.typography.labelSmall,
        color = if (progress.progressState == 2) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
        maxLines = 1)
}

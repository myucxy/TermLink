package dev.termlink.product

import android.widget.Toast
import dev.termlink.harness.R
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.History
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.termlink.terminal.api.TerminalShellState
import dev.termlink.terminal.api.TerminalViewportEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.CancellationException

/** Menu-only additions leave the CLI viewport and dock geometry unchanged. */
@Composable
internal fun ShellIntegrationMenuItems(engine: TerminalViewportEngine?, enabled: Boolean,
    onOpenDirectory: (String) -> Unit, onPrepareCommand: () -> Unit, onDismissMenu: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var commands by remember(engine) { mutableStateOf<TerminalShellState?>(null) }
    var busy by remember(engine) { mutableStateOf(false) }
    fun notice(resource: Int) = Toast.makeText(context, resource, Toast.LENGTH_LONG).show()
    fun load(directory: Boolean) {
        val current = engine ?: return
        scope.launch {
            busy = true
            try {
                val state = withContext(Dispatchers.Default) { current.shellState() }
                if (directory) {
                    val path = TerminalShellState.localWindowsDirectory(state.cwdUri)
                    if (path == null) notice(R.string.shell_directory_unavailable)
                    else { onDismissMenu(); onOpenDirectory(path) }
                } else commands = state
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                notice(R.string.shell_state_unavailable)
            } finally { busy = false }
        }
    }
    DropdownMenuItem(text = { Text(stringResource(R.string.shell_open_directory), style = MaterialTheme.typography.bodyMedium) },
        modifier = Modifier.height(40.dp),
        leadingIcon = { Icon(Icons.Filled.Folder, null, Modifier.size(20.dp)) }, enabled = enabled && engine != null && !busy,
        onClick = { load(true) })
    DropdownMenuItem(text = { Text(stringResource(R.string.shell_commands), style = MaterialTheme.typography.bodyMedium) },
        modifier = Modifier.height(40.dp),
        leadingIcon = { Icon(Icons.Filled.History, null, Modifier.size(20.dp)) }, enabled = engine != null && !busy,
        onClick = { load(false) })
    commands?.let { state ->
        AlertDialog(onDismissRequest = { commands = null },
            title = { Text(stringResource(R.string.shell_commands)) },
            text = {
                if (state.commands.isEmpty()) Text(stringResource(R.string.shell_commands_empty))
                else LazyColumn(Modifier.heightIn(max = 380.dp)) {
                    items(state.commands.asReversed(), key = { it.id }) { command ->
                        Column(Modifier.fillMaxWidth().clickable(enabled = !busy) {
                            scope.launch {
                                busy = true
                                try {
                                    val found = locateShellCommand(engine, command.id, onPrepareCommand)
                                    if (found) { commands = null; onDismissMenu() }
                                    else notice(R.string.shell_anchor_unavailable)
                                } catch (error: Exception) {
                                    if (error is CancellationException) throw error
                                    notice(R.string.shell_state_unavailable)
                                } finally { busy = false }
                            }
                        }.padding(vertical = 8.dp)) {
                            val phase = when (command.phase) {
                                0, 1 -> stringResource(R.string.shell_command_input)
                                2 -> stringResource(R.string.shell_command_running)
                                3 -> command.exitCode?.let { stringResource(R.string.shell_command_exit, it) }
                                    ?: stringResource(R.string.shell_command_finished)
                                else -> stringResource(R.string.shell_command_interrupted)
                            }
                            Text("#${command.id} · $phase", style = MaterialTheme.typography.labelLarge)
                            Text(TerminalShellState.localWindowsDirectory(command.cwdUri) ?: command.cwdUri,
                                style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.MiddleEllipsis)
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { commands = null }) { Text(stringResource(R.string.action_close)) } })
    }
}

internal suspend fun locateShellCommand(
    engine: TerminalViewportEngine?, commandId: Long, onPrepareCommand: () -> Unit,
): Boolean {
    if (engine == null) return false
    onPrepareCommand()
    return withContext(Dispatchers.Default) { engine.showShellCommand(commandId) }
}

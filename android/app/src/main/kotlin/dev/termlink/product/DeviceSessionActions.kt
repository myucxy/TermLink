package dev.termlink.product

import dev.termlink.harness.R

import androidx.compose.ui.res.stringResource

import dev.termlink.model.PcSummary
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.TerminalCommandCapability

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.util.UUID
import kotlinx.coroutines.launch

@Composable
internal fun DeviceSessionActions(workspace: AiWorkspace, pc: PcSummary, opening: Boolean, available: Boolean = true,
    onFiles: () -> Unit, onAi: () -> Unit, onLaunch: (UUID, String, TerminalCommandCapability) -> Unit) {
    var create by rememberSaveable(pc.id.toString()) { mutableStateOf(false) }
    val online = available && pc.online && pc.remoteRoutingEnabled
    val commands = supportedRemoteTerminalCommands(pc)
    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        if (commands.isNotEmpty() && PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities) AiAction(if (opening) "启动中…" else "CLI", Icons.Default.Terminal, online && !opening, filled = true, onClick = { create = true })
        if ("ai_agent_v1" in pc.capabilities) AiAction(stringResource(R.string.ai_ui_chat), Icons.Default.AutoAwesome, online, filled = true,
            onClick = { workspace.beginCreate(pc.id); onAi() })
        if (PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities) AiAction(stringResource(R.string.ai_ui_files), Icons.Default.Folder, online, filled = true, onClick = onFiles)
    }
    if (create) {
        var commandId by rememberSaveable(pc.id.toString()) { mutableStateOf(commands.firstOrNull { it.id == "powershell-7" }?.id ?: commands.firstOrNull()?.id.orEmpty()) }
        var menu by remember { mutableStateOf(false) }
        var browse by remember { mutableStateOf(false) }
        var directory by rememberSaveable(pc.id.toString()) { mutableStateOf("") }
        var loading by remember { mutableStateOf(true) }
        var attempt by remember { mutableIntStateOf(0) }
        var preparing by remember { mutableStateOf(false) }
        val scope = rememberCoroutineScope()
        val catalog = workspace.catalogs[pc.id]
        val command = commands.firstOrNull { it.id == commandId }
        val recovering = workspace.isCliWorkspaceUncertain(pc.id)
        LaunchedEffect(pc.id, attempt) { loading = true; try { workspace.refresh(pc.id) } finally { loading = false } }
        AlertDialog(onDismissRequest = { if (!preparing) create = false }, title = { Text("新建 CLI", style = MaterialTheme.typography.titleMedium) }, text = {
            Column {
                Text(pc.name, style = MaterialTheme.typography.bodySmall)
                Box {
                    TextButton(enabled = !preparing, onClick = { menu = true }) { Text(command?.displayName ?: "选择终端"); Icon(Icons.Default.ArrowDropDown, null) }
                    DropdownMenu(menu, { menu = false }) { commands.forEach { option -> DropdownMenuItem(text = { Text(option.displayName) }, onClick = { commandId = option.id; menu = false }) } }
                }
                if (loading || preparing) LinearProgressIndicator(Modifier.fillMaxWidth())
                Text(directory.ifEmpty { if (catalog?.cliWorkspaceCreation != true) "电脑端尚未提供默认 CLI 目录创建能力，请选择已有目录。" else "由电脑端自动创建独立 CLI 目录" }, maxLines = 4, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                if (workspace.error.isNotEmpty()) Text(workspace.error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                Row {
                    TextButton(enabled = !loading && !preparing && !workspace.isBlocked(pc.id) && catalog?.directoryBrowsing == true && online, onClick = { browse = true }) { Text("选择目录") }
                    if (directory.isNotEmpty()) TextButton(enabled = !preparing, onClick = { directory = "" }) { Text("恢复默认") }
                    if (!loading && catalog == null) TextButton(onClick = { attempt++ }) { Text(stringResource(R.string.ai_ui_retry)) }
                }
            }
        }, confirmButton = { TextButton(enabled = online && !loading && !opening && !preparing && !workspace.isBusy(pc.id) && command != null &&
            (!workspace.isBlocked(pc.id) || recovering && directory.isEmpty()) &&
            (directory.isNotEmpty() || catalog?.cliWorkspaceCreation == true), onClick = {
            val selectedCommand = checkNotNull(command)
            preparing = true
            scope.launch {
                try {
                    val path = directory.ifEmpty { workspace.createCliWorkspace(pc.id).orEmpty() }
                    if (path.isNotEmpty()) { create = false; onLaunch(pc.id, path, selectedCommand) }
                } finally { preparing = false }
            }
        }) { Text(if (recovering) "查询原请求" else "创建") } }, dismissButton = {
            TextButton(enabled = !preparing, onClick = { create = false }) { Text(stringResource(R.string.ai_ui_cancel)) }
        })
        if (browse) AiDirectoryPicker(workspace, pc.id, directory, online, { browse = false }, { directory = it; browse = false })
    }
}

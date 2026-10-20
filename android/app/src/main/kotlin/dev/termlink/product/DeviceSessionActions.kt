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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

internal fun cliWorkspacePath(root: String, time: Date = Date(), id: UUID = UUID.randomUUID()): String {
    require(root.matches(Regex("^[A-Za-z]:[\\\\/].+")) && root.none(Char::isISOControl))
    return root.trimEnd('\\', '/') + "\\cli_" + SimpleDateFormat("yyyyMMdd_HHmmss_SSS", Locale.ROOT).format(time) + "_" + id.toString().replace("-", "")
}

@Composable
internal fun DeviceSessionActions(workspace: AiWorkspace, pc: PcSummary, opening: Boolean, available: Boolean = true,
    onFiles: () -> Unit, onAi: () -> Unit, onLaunch: (UUID, String, TerminalCommandCapability) -> Unit) {
    var create by rememberSaveable(pc.id.toString()) { mutableStateOf(false) }
    var aiUpgradeRequired by rememberSaveable(pc.id.toString()) { mutableStateOf(false) }
    val online = available && pc.online && pc.remoteRoutingEnabled
    val commands = supportedRemoteTerminalCommands(pc)
    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        if (commands.isNotEmpty() && PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities) AiAction(if (opening) "启动中…" else "CLI", Icons.Default.Terminal, online && !opening, filled = true, onClick = { create = true })
        AiAction(stringResource(R.string.ai_ui_chat), Icons.Default.AutoAwesome, online, filled = true, onClick = {
            if ("ai_agent_v1" in pc.capabilities) {
                workspace.beginCreate(pc.id); onAi()
            } else {
                aiUpgradeRequired = true
            }
        })
        if (PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities) AiAction(stringResource(R.string.ai_ui_files), Icons.Default.Folder, online, filled = true, onClick = onFiles)
    }
    if (aiUpgradeRequired) {
        AlertDialog(onDismissRequest = { aiUpgradeRequired = false },
            title = { Text("需要升级电脑端", style = MaterialTheme.typography.titleMedium) },
            text = { Text("${pc.name} 尚未提供 AI 聊天能力。请升级该设备的 Windows 客户端，并在设置中检测 AI Agent 后重试。") },
            confirmButton = { TextButton(onClick = { aiUpgradeRequired = false }) { Text("知道了") } })
    }
    if (create) {
        var commandId by rememberSaveable(pc.id.toString()) { mutableStateOf(commands.firstOrNull { it.id == "powershell-7" }?.id ?: commands.firstOrNull()?.id.orEmpty()) }
        var menu by remember { mutableStateOf(false) }
        var browse by remember { mutableStateOf(false) }
        var directory by rememberSaveable(pc.id.toString()) { mutableStateOf("") }
        var loading by remember { mutableStateOf(true) }
        var attempt by remember { mutableIntStateOf(0) }
        val catalog = workspace.catalogs[pc.id]
        val command = commands.firstOrNull { it.id == commandId }
        LaunchedEffect(pc.id, attempt) { loading = true; try { workspace.refresh(pc.id) } finally { loading = false } }
        AlertDialog(onDismissRequest = { create = false }, title = { Text("新建 CLI", style = MaterialTheme.typography.titleMedium) }, text = {
            Column {
                Text(pc.name, style = MaterialTheme.typography.bodySmall)
                Box {
                    TextButton(onClick = { menu = true }) { Text(command?.displayName ?: "选择终端"); Icon(Icons.Default.ArrowDropDown, null) }
                    DropdownMenu(menu, { menu = false }) { commands.forEach { option -> DropdownMenuItem(text = { Text(option.displayName) }, onClick = { commandId = option.id; menu = false }) } }
                }
                if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
                Text(directory.ifEmpty { if (catalog?.cliWorkspaceRoot.isNullOrEmpty()) "请升级电脑端以使用默认 CLI 工作目录，或选择已有目录。" else "自动创建独立 CLI 目录\n${catalog?.cliWorkspaceRoot}" }, maxLines = 4, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                Row {
                    TextButton(enabled = !loading && catalog?.directoryBrowsing == true && online, onClick = { browse = true }) { Text("选择目录") }
                    if (directory.isNotEmpty()) TextButton(onClick = { directory = "" }) { Text("恢复默认") }
                    if (!loading && catalog == null) TextButton(onClick = { attempt++ }) { Text(stringResource(R.string.ai_ui_retry)) }
                }
            }
        }, confirmButton = { TextButton(enabled = online && !loading && !opening && command != null && (directory.isNotEmpty() || !catalog?.cliWorkspaceRoot.isNullOrEmpty()), onClick = {
            val path = directory.ifEmpty { cliWorkspacePath(checkNotNull(catalog).cliWorkspaceRoot) }
            create = false; onLaunch(pc.id, path, checkNotNull(command))
        }) { Text("创建") } }, dismissButton = { TextButton(onClick = { create = false }) { Text(stringResource(R.string.ai_ui_cancel)) } })
        if (browse) AiDirectoryPicker(workspace, pc.id, directory, online, { browse = false }, { directory = it; browse = false })
    }
}

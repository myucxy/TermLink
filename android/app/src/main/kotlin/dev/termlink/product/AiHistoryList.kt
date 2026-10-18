package dev.termlink.product

import dev.termlink.harness.R

import androidx.compose.ui.res.stringResource

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import dev.termlink.model.PcSummary
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import org.json.JSONObject

internal fun aiNativeHistoryId(agentId: String, id: String): String {
    if (agentId == "opencode") {
        require(id.length in 5..100 && id.startsWith("ses_") && id.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '_' })
        return id
    }
    return UUID.fromString(id).toString()
}
internal fun aiEntryAgent(entry: AiOpened, session: AiConversation?): String = session?.agent?.takeIf { it.isNotBlank() }
    ?: when (entry.agentId) { "codex" -> "Codex"; "claude" -> "Claude Code"; "zcode" -> "ZCode"; "opencode" -> "OpenCode"; else -> "AI" }

internal fun aiVisibleEntries(opened: List<AiOpened>, sessions: Map<String, AiConversation>, devices: Map<UUID, String>, query: String, attention: Boolean) =
    opened.filter { entry ->
        val session = sessions[entry.sessionId]
        (!attention || session?.let(::aiNeedsAttention) == true) && (query.isBlank() ||
            listOf(devices[entry.pcId].orEmpty(), aiEntryAgent(entry, session), session?.let(::aiConversationTitle).orEmpty(), session?.preview.orEmpty())
                .any { it.contains(query.trim(), true) })
    }

@Composable
internal fun AiGroupHeading(name: String, count: Int, icon: ImageVector?, collapsed: Boolean, nested: Boolean = false, toggle: () -> Unit) {
    Row(Modifier.fillMaxWidth()
        .background(if (nested) Color.Transparent else MaterialTheme.colorScheme.surfaceContainerHigh, RoundedCornerShape(8.dp))
        .clickable(role = Role.Button, onClick = toggle).heightIn(min = 40.dp)
        .padding(start = if (nested) 10.dp else 8.dp, end = 6.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        if (icon == null) AiAgentProductIcon(aiAgentId(name), Modifier.size(20.dp))
        else Icon(icon, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
        Text(name, Modifier.weight(1f), style = MaterialTheme.typography.labelLarge,
            fontWeight = if (nested) FontWeight.Medium else FontWeight.SemiBold,
            color = if (nested) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(count.toString(), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Icon(if (collapsed) Icons.Default.ChevronRight else Icons.Default.ExpandMore, if (collapsed) "展开 $name" else "折叠 $name", Modifier.size(20.dp))
    }
}

@Composable
internal fun AiCompactSearch(value: String, change: (String) -> Unit, modifier: Modifier = Modifier) {
    Surface(modifier.heightIn(min = 36.dp), shape = RoundedCornerShape(8.dp), border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)) {
        Row(Modifier.padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Search, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            BasicTextField(value, change, Modifier.weight(1f).padding(horizontal = 8.dp).semantics { contentDescription = "搜索 AI 会话" }, singleLine = true,
                textStyle = MaterialTheme.typography.bodyMedium.copy(color = MaterialTheme.colorScheme.onSurface), cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                decorationBox = { inner -> if (value.isEmpty()) Text(stringResource(R.string.ai_ui_search), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant); inner() })
            if (value.isNotEmpty()) IconButton(onClick = { change("") }, Modifier.size(32.dp)) { Icon(Icons.Default.Close, "清空搜索", Modifier.size(16.dp)) }
        }
    }
}

@Composable
internal fun AiSessionIdentity(session: AiConversation?, modifier: Modifier = Modifier, fallback: String = "AI 对话") {
    Column(modifier.padding(vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(session?.let(::aiConversationTitle) ?: fallback, style = MaterialTheme.typography.bodyMedium)
        if (session != null) Text(session.agent, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AiComputerHistory(workspace: AiWorkspace, pcs: List<PcSummary>, close: () -> Unit) {
    var pcId by remember { mutableStateOf(pcs.firstOrNull()?.id) }
    var agentId by remember(pcId) { mutableStateOf("") }
    var deviceMenu by remember { mutableStateOf(false) }
    var agentMenu by remember { mutableStateOf(false) }
    var entries by remember(pcId, agentId) { mutableStateOf(emptyList<AiNativeEntry>()) }
    var cursor by remember(pcId, agentId) { mutableStateOf("") }
    var loading by remember(pcId, agentId) { mutableStateOf(false) }
    var error by remember(pcId, agentId) { mutableStateOf("") }
    var truncated by remember(pcId, agentId) { mutableStateOf(false) }
    var query by remember { mutableStateOf("") }
    var lifecycle by remember { mutableStateOf<AiConversation?>(null) }
    val collapsed = remember(pcId) { mutableStateMapOf<String, Boolean>() }
    val scope = rememberCoroutineScope()
    val catalog = workspace.catalogs[pcId]
    val agent = catalog?.agents?.firstOrNull { it.id == agentId }
    val native = catalog?.agents.orEmpty().filter { it.supported && it.nativeHistory }
    val online = pcs.any { it.id == pcId } && workspace.unavailable[pcId] != true
    val expectedHost = catalog?.hostId
    suspend fun load(next: Boolean) {
        val pc = pcId ?: return
        val source = agentId
        val host = expectedHost
        loading = true; error = ""
        try {
            val page = workspace.nativeHistory(pc, source, if (next) cursor else "")
            if (pcId == pc && agentId == source && workspace.catalogs[pc]?.hostId == host) {
                entries = ((if (next) entries else emptyList()) + page.sessions).distinctBy { it.id }; cursor = page.nextCursor; truncated = page.truncated
            }
        } catch (failure: Exception) { if (failure is CancellationException) throw failure; if (pcId == pc && agentId == source) error = failure.message ?: "读取失败，请重试。" }
        finally { if (pcId == pc && agentId == source) loading = false }
    }
    LaunchedEffect(pcId, agentId, expectedHost, online) { entries = emptyList(); cursor = ""; if (online && agentId.isNotBlank()) load(false) }
    ModalBottomSheet(onDismissRequest = close) {
        Column(Modifier.fillMaxWidth().heightIn(max = 600.dp).padding(horizontal = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.ai_ui_history), Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                IconButton(onClick = close) { Icon(Icons.Default.Close, "关闭电脑历史") }
            }
            Row {
                Box(Modifier.weight(1f)) {
                    TextButton(onClick = { deviceMenu = true }) { Text(pcs.firstOrNull { it.id == pcId }?.name ?: "设备离线", maxLines = 1); Icon(Icons.Default.ExpandMore, null, Modifier.size(16.dp)) }
                    DropdownMenu(deviceMenu, { deviceMenu = false }) { pcs.forEach { pc -> DropdownMenuItem(text = { Text(pc.name) }, onClick = { pcId = pc.id; deviceMenu = false }) } }
                }
                Box {
                    TextButton(onClick = { agentMenu = true }, enabled = online) { Text(agent?.name ?: "TermLink 记录"); Icon(Icons.Default.ExpandMore, null, Modifier.size(16.dp)) }
                    DropdownMenu(agentMenu, { agentMenu = false }) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.ai_ui_termlink_history)) }, onClick = { agentId = ""; agentMenu = false })
                        native.forEach { option -> DropdownMenuItem(text = { Text("${option.name} 原生历史") }, onClick = { agentId = option.id; agentMenu = false }) }
                    }
                }
            }
            AiCompactSearch(query, { query = it.take(120) }, Modifier.fillMaxWidth())
            if (agentId.isNotBlank()) Text(stringResource(R.string.ai_ui_history_resume), Modifier.padding(vertical = 6.dp), style = MaterialTheme.typography.labelSmall)
            if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (!online) Text(stringResource(R.string.ai_ui_history_offline), Modifier.padding(12.dp))
            else LazyColumn(Modifier.weight(1f, fill = false)) {
                if (agentId.isBlank()) {
                    val sessions = catalog?.sessions.orEmpty().filter { (aiConversationTitle(it) + " " + it.agent).contains(query, true) }
                    if (sessions.isEmpty()) item { Text(stringResource(R.string.ai_ui_history_none), Modifier.padding(vertical = 20.dp), style = MaterialTheme.typography.bodySmall) }
                    sessions.groupBy { it.agent }.forEach { (name, values) ->
                        item(key = "agent:$name") { AiGroupHeading(name, values.size, null, collapsed[name] == true) { collapsed[name] = collapsed[name] != true } }
                        if (collapsed[name] != true) items(values, key = { it.id }) { session ->
                            Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                                Row(Modifier.weight(1f).clickable { workspace.open(session); close() }, verticalAlignment = Alignment.CenterVertically) {
                                    AiSessionIdentity(session, Modifier.weight(1f))
                                    Text(if (session.capabilities.resume && aiIsHistorical(session)) "可恢复" else aiStateLabel(session.state), style = MaterialTheme.typography.labelSmall)
                                }
                                var menu by remember(session.id) { mutableStateOf(false) }
                                Box {
                                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, stringResource(R.string.ai_ui_history_actions), Modifier.size(20.dp)) }
                                    DropdownMenu(menu, { menu = false }) {
                                        DropdownMenuItem(text = { Text(stringResource(if (aiIsHistorical(session)) R.string.ai_ui_delete_history else R.string.ai_end_chat), color = MaterialTheme.colorScheme.error) },
                                            enabled = !workspace.isBlocked(session.pcId), onClick = { menu = false; lifecycle = session })
                                    }
                                }
                            }
                            HorizontalDivider()
                        }
                    }
                } else {
                    val filtered = entries.filter { (it.title + " " + it.directory).contains(query, true) }
                    if (filtered.isEmpty() && !loading && error.isEmpty()) item { Text(stringResource(R.string.ai_ui_no_history), Modifier.padding(vertical = 20.dp)) }
                    items(filtered, key = { it.id }) { entry ->
                        Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(entry.title.ifBlank { "${agent?.name} 历史对话" }, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                                Text(entry.directory, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            TextButton(enabled = pcId?.let { !workspace.isBlocked(it) } == true && !loading, onClick = {
                                pcId?.let { workspace.mutate(it, "import-native", extra = JSONObject().put("agentId", agentId).put("requestId", entry.id)) }; close()
                            }) { Text(stringResource(R.string.ai_ui_continue)) }
                        }
                        HorizontalDivider()
                    }
                    if (cursor.isNotEmpty()) item { TextButton(onClick = { scope.launch { load(true) } }, enabled = !loading) { Text(stringResource(R.string.ai_ui_more)) } }
                    if (truncated) item { Text(stringResource(R.string.ai_ui_history_scope), style = MaterialTheme.typography.labelSmall) }
                }
            }
            if (error.isNotEmpty()) Row(verticalAlignment = Alignment.CenterVertically) { Text(error, Modifier.weight(1f), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = { scope.launch { load(false) } }, enabled = !loading) { Text(stringResource(R.string.ai_ui_retry)) } }
            Spacer(Modifier.height(12.dp))
        }
    }
    lifecycle?.let { session -> AiLifecycleDialog(workspace, session, delete = aiIsHistorical(session)) { lifecycle = null } }
}

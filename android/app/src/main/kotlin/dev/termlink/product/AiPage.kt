package dev.termlink.product

import dev.termlink.harness.R

import androidx.compose.ui.res.stringResource

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.interaction.collectIsDraggedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogWindowProvider
import androidx.core.view.WindowCompat
import dev.termlink.model.PcSummary
import java.util.UUID
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

internal fun aiConversationTitle(session: AiConversation): String = session.title.ifBlank {
    session.messages.firstOrNull { it.role == "user" }?.text?.lineSequence()?.firstOrNull()?.take(80) ?: "${session.agent} 对话"
}
private fun aiDirectoryName(path: String) = path.trimEnd('/', '\\').substringAfterLast('/').substringAfterLast('\\')
internal fun aiNeedsAttention(session: AiConversation) = session.pendingCount > 0 || session.pending.isNotEmpty() || session.error.isNotEmpty()
internal sealed interface AiTimelineEntry {
    val key: String
    data class Message(val value: AiMessage) : AiTimelineEntry { override val key: String get() = "message:" + value.id }
    data class Tools(val values: List<AiMessage>) : AiTimelineEntry {
        override val key: String get() = "tools:" + values.first().id
    }
}

internal fun aiTimelineEntries(messages: List<AiMessage>): List<AiTimelineEntry> {
    val result = mutableListOf<AiTimelineEntry>(); var tools = mutableListOf<AiMessage>()
    fun flush() { if (tools.isNotEmpty()) { result += AiTimelineEntry.Tools(tools); tools = mutableListOf() } }
    messages.forEach { message ->
        if (message.role == "tool") tools += message else { flush(); result += AiTimelineEntry.Message(message) }
    }
    flush(); return result
}
@Composable
private fun aiSessionLabel(session: AiConversation?): String = stringResource(when {
    session == null -> R.string.ai_status_unknown
    session.pendingCount > 0 || session.pending.isNotEmpty() -> R.string.ai_status_pending
    session.error.isNotEmpty() -> R.string.ai_status_error
    session.state == "running" -> R.string.ai_status_running
    session.state == "idle" -> R.string.ai_status_idle
    session.state == "starting" -> R.string.ai_status_starting
    session.state == "exited" -> R.string.ai_status_ended
    session.state == "interrupted" -> R.string.ai_status_interrupted
    session.state == "suspended" -> R.string.ai_status_resumable
    else -> R.string.ai_status_unknown
})

@Composable
internal fun AiPage(workspace: AiWorkspace, snapshot: ProductSnapshot, foreground: Boolean,
    modifier: Modifier = Modifier, onOpenDevices: () -> Unit = {}, onOpenDirectory: suspend (AiFileLocation) -> Unit = {}) {
    val pcs = snapshot.pcs.filter { "ai_agent_v1" in it.capabilities }
    val selected = workspace.selected
    val session = selected?.let(workspace.conversations::get)
    val reference = workspace.opened.firstOrNull { it.sessionId == selected }
    val pc = snapshot.pcs.firstOrNull { it.id == (session?.pcId ?: reference?.pcId) }
    val remote = reference?.takeIf { it.strategy == "desktop_remote" }
    val routeOnline = pc?.online == true && pc.remoteRoutingEnabled && snapshot.signedIn
    val online = routeOnline && workspace.unavailable[pc?.id] != true
    // Once per entry/reconnect, so two phones cannot repeatedly steal control from each other.
    var controlAttempted by remember(selected, online, workspace.catalogs[pc?.id]?.hostId) { mutableStateOf(false) }
    LaunchedEffect(selected, online, foreground, session?.id, workspace.busy, workspace.uncertain) {
        if (foreground && online && session != null && !workspace.busy && !workspace.uncertain && !controlAttempted &&
            session.state !in setOf("exited", "interrupted", "suspended")) {
            controlAttempted = true
            if (!session.canControl) workspace.mutate(session.pcId, "control", session)
        }
    }
    LaunchedEffect(selected, foreground, snapshot.signedIn, routeOnline, pcs.map { Triple(it.id, it.online, it.remoteRoutingEnabled) }) {
        if (foreground && snapshot.signedIn) while (true) {
            if (selected != null && routeOnline) workspace.refresh(checkNotNull(pc).id, selected.takeIf { remote == null })
            else pcs.filter { it.online && it.remoteRoutingEnabled }.forEach { workspace.refresh(it.id) }
            delay(if (selected == null || remote != null) 6_000 else if (workspace.conversations[selected]?.capabilities?.watch == true && workspace.unavailable[pc?.id] != true) 100 else 1_000)
        }
    }
    LaunchedEffect(selected, foreground, online, session?.canControl, session?.controlVersion) {
        if (foreground && online && session?.canControl == true && session.capabilities.watch) while (true) {
            try { workspace.renewControl(workspace.conversations[session.id] ?: break) }
            catch (e: Exception) { if (e is kotlinx.coroutines.CancellationException) throw e }
            delay(45_000)
        }
    }
    BackHandler(enabled = selected != null) { workspace.selected = null }
    Column(modifier.fillMaxSize().windowInsetsPadding(productImeInsets())) {
        if (selected == null) AiConversationList(workspace, snapshot, pcs, onOpenDevices, Modifier.weight(1f))
        else if (remote != null) AiRemotePage(workspace, remote, pc?.name ?: "电脑", online, foreground, Modifier.weight(1f))
        else {
            AiConversationHeader(workspace, session, pc, online)
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
            if (!online || session == null) {
                val reason = when {
                    !snapshot.signedIn -> "登录后重新确认会话状态。"
                    snapshot.connectionState != ProductConnectionState.CONNECTED -> "手机连接暂不可用，正在重新连接。"
                    pc?.online == false -> "电脑已离线，连接恢复后重新确认状态。"
                    pc?.remoteRoutingEnabled == false -> "请先在电脑上开启远程访问。"
                    !online -> "暂时无法连接电脑，正在重新确认状态。"
                    pc?.id in workspace.catalogs -> "此会话已不在电脑当前目录中，可关闭视图后新建。"
                    else -> "正在确认电脑上的会话状态…"
                }
                AiEmptyState(if (online) Icons.Default.ChatBubbleOutline else Icons.Default.CloudOff,
                    if (online) "状态待确认" else "连接不可用 · 状态待确认",
                    reason + if (!online) "\n已隐藏会话内容，未发送草稿保留。" else "", Modifier.weight(1f).fillMaxWidth())
            } else {
                if (androidx.webkit.WebViewFeature.isFeatureSupported(androidx.webkit.WebViewFeature.WEB_MESSAGE_LISTENER)) {
                    key(session.pcId, session.id, workspace.catalogs[session.pcId]?.hostId) {
                        AiWebConversation(workspace, session, Modifier.weight(1f), onOpenDirectory)
                    }
                } else {
                    AiConversationStatus(workspace, session)
                    AiMessages(workspace, session, Modifier.weight(1f))
                }
                if (session.error.isNotEmpty()) Text(aiErrorLabel(session.error), Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
                    color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                when {
                    session.state in setOf("exited", "interrupted", "suspended") -> Row(verticalAlignment = Alignment.CenterVertically) { Text(
                        stringResource(if (session.capabilities.resume) R.string.ai_resume_saved else R.string.ai_ended_pc),
                        Modifier.weight(1f).padding(12.dp), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
                        if (session.capabilities.resume) AiAction(stringResource(R.string.ai_ui_resume), enabled = !workspace.busy && !workspace.uncertain, onClick = { workspace.mutate(session.pcId, "resume", session) }) }
                    session.pending.none { it.kind == "question" } -> AiEnhancedComposer(workspace, session)
                }
            }
        }
        if (workspace.busy) LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp))
        workspace.pendingPcs.filter { workspace.isUncertain(it) }.forEach { pendingPc ->
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("${snapshot.pcs.firstOrNull { it.id == pendingPc }?.name ?: "电脑"} · ${workspace.pendingDescription(pendingPc)} · 结果待确认",
                    Modifier.weight(1f), style = MaterialTheme.typography.labelSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                AiAction(stringResource(R.string.ai_ui_query_result), onClick = { workspace.retryUncertain(pendingPc) })
            }
        }
        if (workspace.error.isNotEmpty()) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(workspace.error, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }
    workspace.createPc?.let { AiCreateSheet(workspace, pcs, it) }
}

@Composable
private fun AiConversationList(workspace: AiWorkspace, snapshot: ProductSnapshot, pcs: List<PcSummary>, onOpenDevices: () -> Unit, modifier: Modifier) {
    val available = pcs.filter { it.online && it.remoteRoutingEnabled }
    var history by remember { mutableStateOf(false) }
    var ending by remember { mutableStateOf<AiConversation?>(null) }
    val collapsed = remember { mutableStateMapOf<String, Boolean>() }
    val list = rememberLazyListState()
    var highlighted by remember { mutableStateOf<String?>(null) }
    val onlineIds = available.filter { snapshot.signedIn && workspace.unavailable[it.id] != true }.mapTo(hashSetOf()) { it.id }
    val sourceEntries = aiListEntries(workspace.opened, workspace.listPc, workspace.catalogs[workspace.listPc], workspace.listPc in onlineIds,
        workspace.closedSessionIds, workspace.conversations)
    val attention = sourceEntries.count { it.pcId in onlineIds && workspace.conversations[it.sessionId]?.let(::aiNeedsAttention) == true }
    val entries = aiVisibleEntries(sourceEntries, workspace.conversations.filterValues { it.pcId in onlineIds }, snapshot.pcs.associate { it.id to it.name },
        workspace.search, workspace.attentionOnly).sortedByDescending { workspace.isPinned(it.sessionId) }
    val rows = aiListRows(entries, workspace.conversations, collapsed)
    val target = workspace.listTarget
    val targetCollapsed = target?.let { collapsed["device:${it.pcId}"] == true || collapsed[it.key] == true } == true
    LaunchedEffect(target, rows.map { it.key }, targetCollapsed) {
        if (target != null) {
            if (targetCollapsed) {
                collapsed["device:${target.pcId}"] = false
                collapsed[target.key] = false
                return@LaunchedEffect // Locate only after recomposition has expanded the actual list rows.
            }
            val index = rows.indexOfFirst { it.key == target.key }
            if (index >= 0) {
                list.scrollToItem(index)
                highlighted = target.key
                workspace.listTarget = null
            }
        }
    }
    LaunchedEffect(highlighted) { if (highlighted != null) { delay(2_000); highlighted = null } }
    if (history) AiComputerHistory(workspace, available) { history = false }
    ending?.let { AiLifecycleDialog(workspace, it, delete = false) { ending = null } }
    Column(modifier) {
        Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.ai_chats_title), Modifier.weight(1f), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            IconButton(onClick = { history = true }, enabled = available.isNotEmpty()) { Icon(Icons.Default.History, stringResource(R.string.ai_ui_history), Modifier.size(20.dp)) }
            AiAction(stringResource(R.string.ai_ui_create), Icons.Default.Add, enabled = available.isNotEmpty(), filled = true, description = stringResource(R.string.ai_ui_new_chat),
                onClick = { workspace.beginCreate(available.first().id, lockPc = false) })
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            AiAction(stringResource(R.string.ai_all_count, sourceEntries.size), filled = !workspace.attentionOnly, onClick = { workspace.attentionOnly = false })
            AiAction(stringResource(R.string.ai_attention_count, attention), filled = workspace.attentionOnly, onClick = { workspace.attentionOnly = true })
            Text(if (workspace.listPc == null) stringResource(R.string.ai_phone_opened) else stringResource(R.string.ai_including_pc, snapshot.pcs.firstOrNull { it.id == workspace.listPc }?.name.orEmpty()),
                Modifier.weight(1f).padding(start = 4.dp), maxLines = 1, overflow = TextOverflow.Ellipsis,
                color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
            if (workspace.listPc != null) IconButton(onClick = { workspace.listPc = null; workspace.listTarget = null }, Modifier.size(40.dp)) {
                Icon(Icons.Default.Close, stringResource(R.string.ai_phone_only), Modifier.size(18.dp))
            }
        }
        AiListTools(workspace, onlineIds, entries)
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = list, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)) {
            if (entries.isEmpty()) item {
                AiEmptyState(Icons.Default.AutoAwesome, if (workspace.attentionOnly) "暂无待处理请求" else "开始一段 AI 对话",
                    if (pcs.isEmpty()) "电脑与服务端需支持 AI 对话，原命令行会话仍在 CLI。"
                    else if (workspace.attentionOnly) "需要审批、回答或关注的对话会显示在这里。"
                    else "点击右上角新建，或从设备打开已有对话。", Modifier.fillMaxWidth().padding(vertical = 40.dp))
            }
            items(rows, key = { it.key }) { row -> when (row) {
                is AiListRow.Device -> AiGroupHeading(snapshot.pcs.firstOrNull { it.id == row.pcId }?.name ?: "电脑", row.count,
                    Icons.Default.Computer, collapsed[row.key] == true) { collapsed[row.key] = collapsed[row.key] != true }
                is AiListRow.Agent -> Box(Modifier.background(if (highlighted == row.key) MaterialTheme.colorScheme.primary.copy(alpha = .12f) else Color.Transparent, RoundedCornerShape(8.dp))) {
                    AiGroupHeading(aiAgentName(row.agentId), row.count, null, collapsed[row.key] == true, true) { collapsed[row.key] = collapsed[row.key] != true }
                }
                is AiListRow.Session -> {
                val opened = row.entry
                val value = workspace.conversations[opened.sessionId]
                val phoneOpened = workspace.opened.any { it.sessionId == opened.sessionId }
                val host = snapshot.pcs.firstOrNull { it.id == opened.pcId }
                val online = snapshot.signedIn && host?.online == true && host.remoteRoutingEnabled && workspace.unavailable[host.id] != true
                Box(Modifier.padding(start = 28.dp).background(MaterialTheme.colorScheme.surfaceContainerLow, RoundedCornerShape(8.dp))) {
                if (opened.strategy == "desktop_remote") {
                    var menu by remember { mutableStateOf(false) }
                    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable(enabled = online, role = Role.Button) {
                        if (phoneOpened) workspace.selected = opened.sessionId else workspace.openRemote(opened.pcId, opened.agentId)
                    }.padding(start = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(stringResource(R.string.ai_remote_strategy), Modifier.weight(1f),
                            style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (!online) AiStatusTag("待确认") else Icon(Icons.AutoMirrored.Filled.ArrowForward, null,
                            Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Box {
                            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, "ZCode 远控视图操作", Modifier.size(18.dp)) }
                            DropdownMenu(menu, { menu = false }) {
                                DropdownMenuItem(text = { Text("关闭手机视图") }, onClick = { menu = false; workspace.closeView(opened.sessionId) })
                            }
                        }
                    }
                } else AiConversationRow(value, host?.name ?: "电脑", online, opened.pcId in workspace.catalogs,
                    { if (phoneOpened) workspace.selected = opened.sessionId else if (online && value != null) workspace.open(value) },
                    { workspace.closeView(opened.sessionId) },
                    if (online && value != null && !aiIsHistorical(value)) ({ ending = value }) else null)
                }
                Spacer(Modifier.height(4.dp))
                }
            } }
            item { Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
                AiAction(stringResource(R.string.ai_ui_view_pc), Icons.Default.ChevronRight, onClick = onOpenDevices)
            } }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun AiConversationRow(session: AiConversation?, pcName: String, online: Boolean, catalogKnown: Boolean, onOpen: () -> Unit, onClose: () -> Unit, onEnd: (() -> Unit)?) {
    var menu by remember { mutableStateOf(false) }
    var confirmClose by remember { mutableStateOf(false) }
    Box {
        Column(Modifier.fillMaxWidth().combinedClickable(onClick = onOpen, onLongClick = { menu = true }, onLongClickLabel = "对话操作")
            .heightIn(min = 48.dp).padding(horizontal = 10.dp, vertical = 6.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(if (online && session != null) aiConversationTitle(session) else "${session?.agent ?: "AI"} 对话",
                    Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                AiStatusTag(if (!online) "待确认" else if (session == null && catalogKnown) "已失效" else aiSessionLabel(session), online && session?.let(::aiNeedsAttention) == true)
            }
            val preview = session?.preview?.ifBlank { session.messages.lastOrNull { it.role != "tool" }?.text.orEmpty() }.orEmpty()
            Text(if (!online) "连接不可用，已隐藏对话摘要" else preview.ifBlank {
                if (session == null && catalogKnown) "电脑端已无此会话，可长按关闭视图"
                else if (session == null) "正在确认电脑上的会话状态…" else "打开对话继续查看" },
                maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text("关闭手机视图") }, onClick = { menu = false; confirmClose = true })
            if (onEnd != null) DropdownMenuItem(text = { Text(stringResource(R.string.ai_end_chat), color = MaterialTheme.colorScheme.error) }, onClick = { menu = false; onEnd() })
        }
    }
    if (confirmClose) AlertDialog(onDismissRequest = { confirmClose = false }, title = { Text(stringResource(R.string.ai_ui_close_view_question)) },
        text = { Column { AiSessionIdentity(session?.takeIf { online }, fallback = "${session?.agent ?: "AI"} · $pcName")
            Text(stringResource(R.string.ai_ui_close_view_hint), style = MaterialTheme.typography.bodySmall) } },
        confirmButton = { TextButton(onClick = { confirmClose = false; onClose() }) { Text(stringResource(R.string.ai_ui_close_view)) } },
        dismissButton = { TextButton(onClick = { confirmClose = false }) { Text(stringResource(R.string.ai_ui_cancel)) } })
}

@Composable
private fun AiConversationHeader(workspace: AiWorkspace, session: AiConversation?, pc: PcSummary?, online: Boolean) {
    var menu by remember { mutableStateOf(false) }
    var confirmEnd by remember { mutableStateOf(false) }
    var confirmClose by remember { mutableStateOf(false) }
    var showDetails by remember { mutableStateOf(false) }
    var showActions by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    Row(Modifier.fillMaxWidth().heightIn(min = 40.dp).padding(end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        // As in the CLI header, compact layout retains Compose's expanded touch targets.
        IconButton(onClick = { workspace.selected = null }, modifier = Modifier.width(40.dp).height(36.dp)) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.ai_back_list), Modifier.size(20.dp)) }
        Column(Modifier.weight(1f).clickable(enabled = session != null && online) { showDetails = true }) {
            Text(if (online && session != null) aiConversationTitle(session) else stringResource(R.string.ai_chats_title), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
            Text(listOfNotNull(session?.agent, pc?.name, session?.takeIf { online }?.directory?.let(::aiDirectoryName)).joinToString(" · "),
                maxLines = 1, overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
        }
        Box {
            IconButton(onClick = { menu = true }, modifier = Modifier.width(40.dp).height(36.dp)) { Icon(if (online) Icons.Default.Cloud else Icons.Default.CloudOff, stringResource(R.string.ai_chat_menu), Modifier.size(20.dp)) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                AiMenuLabel("连接 · ${if (online) "服务端中转" else "状态待确认"}")
                DropdownMenuItem(text = { Text("重新确认连接", style = MaterialTheme.typography.bodyMedium) }, enabled = pc != null && !workspace.busy && !workspace.uncertain,
                    onClick = { menu = false; pc?.let { scope.launch { workspace.refresh(it.id, session?.id)
                        session?.id?.let(workspace.conversations::get)?.takeIf { s -> !s.canControl && s.state !in setOf("exited", "interrupted", "suspended") }?.let { s -> workspace.mutate(s.pcId, "control", s) }
                    } } })
                HorizontalDivider()
                AiMenuLabel(stringResource(R.string.ai_current_chat))
                DropdownMenuItem(text = { Text(stringResource(R.string.ai_manage_export)) }, enabled = session != null && online, onClick = { menu = false; showActions = true })
                DropdownMenuItem(text = { Text(stringResource(R.string.ai_interrupt), style = MaterialTheme.typography.bodyMedium) },
                    enabled = online && session?.canControl == true && session.state == "running" && !workspace.busy && !workspace.uncertain,
                    onClick = { menu = false; session?.let { workspace.mutate(it.pcId, "interrupt", it) } })
                DropdownMenuItem(text = { Text(stringResource(R.string.ai_ui_close_view), style = MaterialTheme.typography.bodyMedium) },
                    onClick = { menu = false; confirmClose = true })
                DropdownMenuItem(text = { Text(stringResource(R.string.ai_end_chat), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) },
                    enabled = online && session != null && !aiIsHistorical(session) && !workspace.isBlocked(session.pcId),
                    onClick = { menu = false; confirmEnd = true })
            }
        }
    }
    if (showActions && session != null && online) AiSessionActions(workspace, session) { showActions = false }
    if (confirmClose) AlertDialog(onDismissRequest = { confirmClose = false }, title = { Text(stringResource(R.string.ai_ui_close_view_question)) },
        text = { Column { AiSessionIdentity(session?.takeIf { online }); Text(stringResource(R.string.ai_ui_close_view_hint), style = MaterialTheme.typography.bodySmall) } },
        confirmButton = { TextButton(onClick = { confirmClose = false; workspace.selected?.let(workspace::closeView) }) { Text(stringResource(R.string.ai_ui_close_view)) } },
        dismissButton = { TextButton(onClick = { confirmClose = false }) { Text(stringResource(R.string.ai_ui_cancel)) } })
    if (showDetails && session != null && online) AlertDialog(onDismissRequest = { showDetails = false }, title = { Text(stringResource(R.string.ai_ui_session_info)) },
        text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("${session.agent} · ${pc?.name.orEmpty()}")
            Text(stringResource(R.string.ai_ui_workdir), style = MaterialTheme.typography.labelSmall)
            SelectionContainer { Text(session.directory, style = MaterialTheme.typography.bodySmall) }
            AiCopyAction(session.directory, "复制目录")
        } }, confirmButton = { TextButton(onClick = { showDetails = false }) { Text(stringResource(R.string.ai_ui_close)) } })
    if (confirmEnd && session != null) AiLifecycleDialog(workspace, session, delete = false) { confirmEnd = false }
}

@Composable
private fun AiMenuLabel(text: String) { Text(text, Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
    color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall) }

@Composable
private fun AiConversationStatus(workspace: AiWorkspace, session: AiConversation) {
    val waiting = session.pendingCount > 0 || session.pending.isNotEmpty()
    if (!waiting && session.state != "running") return
    val color = if (waiting) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.primary
    Surface(color = color.copy(alpha = .09f)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(if (waiting) Icons.Default.PendingActions else if (session.state == "running") Icons.Default.AutoAwesome else Icons.Default.Lock,
                null, Modifier.size(16.dp), tint = color)
            Spacer(Modifier.width(6.dp))
            Text(if (waiting) "等待处理 · ${maxOf(session.pendingCount, session.pending.size)} 项" else "正在生成…",
                Modifier.weight(1f), color = color, style = MaterialTheme.typography.labelMedium)
        }
    }
}

@Composable
private fun AiMessages(workspace: AiWorkspace, session: AiConversation, modifier: Modifier) {
    var fullMessage by remember(session.id) { mutableStateOf<AiMessage?>(null) }
    val reading = remember(session.id) { workspace.reading.getOrPut(session.id) { AiReadingState() } }
    val list = reading.list
    val entries = remember(session.messages) { aiTimelineEntries(session.messages) }
    val atBottom by remember(list) { derivedStateOf { !list.canScrollForward } }
    val dragged by list.interactionSource.collectIsDraggedAsState()
    val currentDragged by rememberUpdatedState(dragged)
    val scope = rememberCoroutineScope()
    // Reader intent persists with the list across navigation and rotation. A live update,
    // IME resize or expanded detail must never take over while the reader browses above.
    LaunchedEffect(list) {
        var userScroll = false
        snapshotFlow { Triple(currentDragged, list.isScrollInProgress, !list.canScrollForward) }.collect { (drag, scrolling, bottom) ->
            if (drag) { userScroll = true; reading.followsTail = false }
            if (userScroll && !scrolling && !drag) { reading.followsTail = bottom; userScroll = false }
        }
    }
    val expectedCount = entries.size + session.pending.size + if (session.truncated || session.historyBefore > 0) 1 else 0
    val currentCount by rememberUpdatedState(expectedCount)
    val tailKey by rememberUpdatedState(session.pending.lastOrNull()?.let { "pending:" + it.id } ?: entries.lastOrNull()?.key)
    LaunchedEffect(list) {
        snapshotFlow {
            val layout = list.layoutInfo
            if (!reading.followsTail || currentDragged || layout.totalItemsCount != currentCount) null
            else Triple(currentCount, layout.viewportSize.height, layout.visibleItemsInfo.lastOrNull()?.let { Triple(it.key, it.size, it.offset) })
        }.collectLatest { geometry ->
            if (geometry == null || geometry.first == 0 || geometry.second <= 0) return@collectLatest
            withFrameNanos { }
            if (!reading.followsTail || currentDragged) return@collectLatest
            val end = list.layoutInfo.visibleItemsInfo.lastOrNull()
            if (end?.key != tailKey || list.canScrollForward) list.scrollToItem(currentCount - 1, Int.MAX_VALUE)
        }
    }
    Box(modifier.fillMaxWidth()) {
        if (session.messages.isEmpty() && session.pending.isEmpty()) AiEmptyState(Icons.Default.AutoAwesome, "${session.agent} 已就绪",
            "描述要完成的任务，操作需要批准时会在这里提示。", Modifier.fillMaxSize())
        LazyColumn(Modifier.fillMaxSize(), state = list, verticalArrangement = Arrangement.spacedBy(12.dp), contentPadding = PaddingValues(12.dp)) {
            if (session.truncated || session.historyBefore > 0) item(key = "history-notice") {
                if (session.historyBefore > 0) TextButton(onClick = { reading.followsTail = false; scope.launch { workspace.loadHistory(session) } }) { Text(stringResource(R.string.ai_ui_earlier)) }
                else Text(stringResource(R.string.ai_ui_history_end), style = MaterialTheme.typography.labelSmall)
            }
            items(entries, key = { it.key }) { entry ->
                if (entry is AiTimelineEntry.Tools) {
                    AiToolGroup(entry.values, onInspect = { reading.followsTail = false }, onQuote = { workspace.quote(session.id, it) }, onFull = { fullMessage = it })
                } else Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    val message = (entry as AiTimelineEntry.Message).value
                    if (message.role == "user") Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        Surface(Modifier.widthIn(max = 560.dp).padding(start = 24.dp), shape = RoundedCornerShape(12.dp, 12.dp, 3.dp, 12.dp),
                            color = MaterialTheme.colorScheme.primary.copy(alpha = .17f)) {
                            SelectionContainer { Text(message.text, Modifier.padding(horizontal = 12.dp, vertical = 8.dp), style = MaterialTheme.typography.bodyMedium) }
                        }
                    } else {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            AiAgentBadge(session.agent)
                            Text(session.agent, Modifier.weight(1f), color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelSmall)
                            AiCopyAction(message.text)
                            TextButton(onClick = { workspace.quote(session.id, message.text) }, contentPadding = PaddingValues(4.dp)) { Text(stringResource(R.string.ai_ui_quote), style = MaterialTheme.typography.labelSmall) }
                        }
                        AiReplyContent(message.text)
                    }
                    if (message.fullLength > message.text.length) TextButton(onClick = { fullMessage = message; reading.followsTail = false }) { Text("查看完整内容 · ${message.fullLength} 字符") }
                    if (message.role == "user") Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                        Text(when (message.status) { "accepted" -> "已接收"; "completed" -> "已完成"; "failed" -> "失败"; "uncertain" -> "结果待确认"; else -> "" }, style = MaterialTheme.typography.labelSmall)
                        TextButton(onClick = { workspace.appendDraft(session.id, message.text) }) { Text(stringResource(R.string.ai_ui_append), style = MaterialTheme.typography.labelSmall) }
                    }
                }
            }
            items(session.pending, key = { "pending:" + it.id }) { AiInteraction(workspace, session, it, onInspect = { reading.followsTail = false }) }
        }
        if (!atBottom) {
            Box(Modifier.align(Alignment.BottomEnd).padding(8.dp)) { AiAction(if (session.pending.isNotEmpty()) "待处理 ${session.pending.size} 项" else "回到最新", Icons.Default.ArrowDownward, filled = true,
                onClick = {
                    val pendingIndex = list.layoutInfo.totalItemsCount - session.pending.size
                    reading.followsTail = session.pending.isEmpty()
                    scope.launch {
                        if (session.pending.isNotEmpty()) list.scrollToItem(pendingIndex.coerceAtLeast(0))
                        else list.scrollToItem((list.layoutInfo.totalItemsCount - 1).coerceAtLeast(0), Int.MAX_VALUE)
                    }
                }) }
        }
    }
    fullMessage?.let { AiFullMessageDialog(workspace, session, it) { fullMessage = null } }
}

@Composable
private fun AiToolGroup(values: List<AiMessage>, onInspect: () -> Unit, onQuote: (String) -> Unit = {}, onFull: (AiMessage) -> Unit = {}) {
    var expanded by rememberSaveable(values.firstOrNull()?.id ?: "tools") { mutableStateOf(false) }
    var detailId by rememberSaveable { mutableStateOf<String?>(null) }
    val label = aiToolGroupLabel(values)
    Surface(shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .45f),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)) {
        Column {
            Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable { onInspect(); expanded = !expanded }.padding(horizontal = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Code, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.secondary)
                Column(Modifier.weight(1f).padding(horizontal = 8.dp)) {
                    Text(label, style = MaterialTheme.typography.bodySmall)
                    if (!expanded) Text(values.firstOrNull()?.summary?.ifBlank { "点击查看详情" }.orEmpty(), maxLines = 1, overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelSmall)
                }
                Icon(if (expanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
                    if (expanded) "收起工具详情" else "展开工具详情", Modifier.size(18.dp))
            }
            if (expanded) values.forEach { tool ->
                Column(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable { onInspect(); detailId = tool.id }.padding(start = 36.dp, end = 10.dp, top = 6.dp, bottom = 8.dp)) {
                    Text(tool.title.ifBlank { "工具操作" } + " · " + aiToolStatusLabel(tool.status), style = MaterialTheme.typography.labelMedium)
                    Text(tool.summary.ifBlank { "查看详细记录" }, maxLines = 2, overflow = TextOverflow.Ellipsis,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
    values.firstOrNull { it.id == detailId }?.let { tool ->
        AlertDialog(onDismissRequest = { detailId = null }, title = { Text(tool.title.ifBlank { "工具详情" }) },
            text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(aiToolStatusLabel(tool.status), style = MaterialTheme.typography.labelSmall)
                SelectionContainer { Text(tool.text, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
                if (tool.fullLength > tool.text.length) TextButton(onClick = { detailId = null; onFull(tool) }) { Text(stringResource(R.string.ai_ui_full_tool)) }
            } }, confirmButton = { TextButton(onClick = { detailId = null }) { Text(stringResource(R.string.ai_ui_close)) } },
            dismissButton = { Row { AiCopyAction(tool.text, "复制详情"); TextButton(onClick = { onQuote(tool.text); detailId = null }) { Text(stringResource(R.string.ai_ui_quote)) } } })
    }
}

internal fun aiToolStatusLabel(status: String): String = when (status) {
    "running" -> "执行中"; "completed" -> "已完成"; "failed" -> "失败"; "denied" -> "已拒绝"; "cancelled" -> "已取消"; else -> "状态未报告"
}
internal fun aiToolGroupLabel(values: List<AiMessage>): String {
    val status = when {
        values.any { it.status == "failed" } -> "有失败"
        values.any { it.status == "running" } -> "执行中"
        values.all { it.status == "completed" } -> "已完成"
        else -> "工具记录"
    }
    return if (values.size == 1) values.first().title.ifBlank { "工具操作" } + " · " + aiToolStatusLabel(values.first().status)
        else "$status · ${values.size} 项操作"
}

@Composable
private fun AiInteraction(workspace: AiWorkspace, session: AiConversation, pending: AiPending, onInspect: () -> Unit) {
    val draft = workspace.questionDrafts.getOrPut(session.id to pending.id) { AiQuestionDraft() }
    val answers = draft.answers
    val choices = draft.choices
    var expanded by rememberSaveable(pending.id) { mutableStateOf(false) }
    val enabled = session.canControl && !workspace.busy && !workspace.uncertain
    val tone = MaterialTheme.colorScheme.tertiary
    Surface(shape = RoundedCornerShape(12.dp), color = tone.copy(alpha = .1f), border = BorderStroke(1.dp, tone.copy(alpha = .45f))) {
        Column(Modifier.fillMaxWidth().padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Icon(Icons.Default.Shield, null, Modifier.size(18.dp), tint = tone)
                Text(if (pending.kind == "question") "需要你的回答" else pending.title.ifBlank { "需要你的批准" }, color = tone, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.titleSmall)
            }
            if (pending.kind != "question") Surface(shape = RoundedCornerShape(6.dp), color = MaterialTheme.colorScheme.background) {
                SelectionContainer { Text(if (expanded || pending.summary.isBlank()) pending.detail else pending.summary,
                    Modifier.fillMaxWidth().padding(8.dp), maxLines = if (expanded) Int.MAX_VALUE else 3,
                    overflow = TextOverflow.Ellipsis, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
            }
            pending.questions.forEach { question ->
                Text(question.text, style = MaterialTheme.typography.bodyMedium)
                Column(if (question.multiSelect) Modifier else Modifier.selectableGroup()) {
                    question.options.forEach { option ->
                        val checked = option in choices[question.id].orEmpty()
                        val choose = {
                            val old = choices[question.id].orEmpty()
                            choices[question.id] = if (question.multiSelect) { if (option in old) old - option else old + option } else setOf(option)
                        }
                        val input = if (question.multiSelect) Modifier.toggleable(checked, enabled, Role.Checkbox) { choose() }
                            else Modifier.selectable(checked, enabled, Role.RadioButton, choose)
                        Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).then(input), verticalAlignment = Alignment.CenterVertically) {
                            if (question.multiSelect) Checkbox(checked, null, enabled = enabled) else RadioButton(checked, null, enabled = enabled)
                            Text(option, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
                OutlinedTextField(value = answers[question.id].orEmpty(), onValueChange = { if (it.length <= 4000) answers[question.id] = it },
                    modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ai_ui_answer)) }, maxLines = 3, enabled = enabled, textStyle = MaterialTheme.typography.bodyMedium)
            }
            Text("仅本次请求 · ${aiDirectoryName(session.directory)}", maxLines = 1, overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (!pending.canApprove) Text(stringResource(R.string.ai_ui_oversize_request), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                AiAction(stringResource(R.string.ai_ui_deny), enabled = enabled, onClick = { workspace.mutate(session.pcId, "answer", session, JSONObject().put("requestId", pending.id).put("decision", "deny")) })
                AiAction(if (pending.kind == "question") "提交回答" else "允许本次", filled = true,
                    enabled = enabled && pending.canApprove && (pending.kind != "question" || pending.questions.isNotEmpty() && pending.questions.all { answers[it.id].orEmpty().isNotBlank() || choices[it.id].orEmpty().isNotEmpty() }),
                    onClick = {
                        val response = JSONObject()
                        pending.questions.forEach { q -> response.put(q.id, JSONArray((choices[q.id].orEmpty() + listOfNotNull(answers[q.id]?.takeIf(String::isNotBlank))).toList())) }
                        workspace.mutate(session.pcId, "answer", session, JSONObject().put("requestId", pending.id).put("decision", "allow").put("answers", response))
                    })
            }
            AiAction(if (expanded) "收起请求详情" else "请求详情", if (expanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore, onClick = { onInspect(); expanded = !expanded })
            if (expanded && pending.kind == "question") SelectionContainer { Text(pending.detail, style = MaterialTheme.typography.bodySmall) }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
private fun AiCreateSheet(workspace: AiWorkspace, pcs: List<PcSummary>, initialPc: UUID) {
    var pcKey by rememberSaveable(initialPc) { mutableStateOf(initialPc.toString()) }
    val pcId = UUID.fromString(pcKey)
    val catalog = workspace.catalogs[pcId]
    val options = catalog?.agents.orEmpty().filter { it.supported }
    var agentId by rememberSaveable(pcKey) { mutableStateOf("") }
    val agent = options.firstOrNull { it.id == agentId } ?: options.firstOrNull()
    val initialDirectory = workspace.createDirectory.takeIf { pcId == initialPc }
    var directoryOverride by rememberSaveable(pcKey, initialDirectory) { mutableStateOf(initialDirectory) }
    val perSession = catalog?.directoryMode == "per_session"
    var customDirectory by rememberSaveable(pcKey, initialDirectory) { mutableStateOf(initialDirectory != null) }
    val managed = perSession && !customDirectory
    val directory = if (managed) "" else directoryOverride ?: workspace.preferredDirectory(pcId)
    var browseDirectory by rememberSaveable(pcKey) { mutableStateOf(false) }
    var model by rememberSaveable(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf("") }
    var provider by rememberSaveable(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf("") }
    var mode by rememberSaveable(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf("ask") }
    var reasoning by remember(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf(AiReasoning()) }
    var reasoningEffort by rememberSaveable(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf("") }
    var confirmedMode by rememberSaveable(pcKey, catalog?.hostId, agent?.id, agent?.strategy) { mutableStateOf<String?>(null) }
    var proposedMode by remember { mutableStateOf<String?>(null) }
    val remote = agent?.strategy == "desktop_remote"
    val ready = pcs.any { it.id == pcId && it.online && it.remoteRoutingEnabled } && workspace.unavailable[pcId] != true
    val editable = !workspace.isBlocked(pcId)
    val scope = rememberCoroutineScope()
    LaunchedEffect(pcId) { workspace.refresh(pcId) }
    ModalBottomSheet(onDismissRequest = { if (!workspace.busy) workspace.createPc = null },
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = MaterialTheme.colorScheme.surface, dragHandle = null) {
        val view = LocalView.current
        val lightBars = MaterialTheme.colorScheme.surface.luminance() > .5f
        SideEffect {
            (view.parent as? DialogWindowProvider)?.window?.let { window ->
                WindowCompat.getInsetsController(window, view).apply {
                    isAppearanceLightStatusBars = lightBars; isAppearanceLightNavigationBars = lightBars
                }
            }
        }
        Column(Modifier.fillMaxWidth().heightIn(max = 620.dp).imePadding()) {
            Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.ai_ui_new_chat), Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                IconButton(enabled = !workspace.busy, onClick = { workspace.createPc = null }) { Icon(Icons.Default.Close, "关闭新建对话", Modifier.size(20.dp)) }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
            Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                if (!workspace.createPcLocked && pcs.count { it.online && it.remoteRoutingEnabled } > 1)
                    AiChoice("电脑", pcs.filter { it.online && it.remoteRoutingEnabled }.map { it.id.toString() to it.name }, pcKey, true) {
                        pcKey = it; workspace.beginCreate(UUID.fromString(it), lockPc = false)
                    }
                else Text(pcs.firstOrNull { it.id == pcId }?.name ?: "电脑", Modifier.padding(vertical = 10.dp), style = MaterialTheme.typography.bodySmall)
                when {
                    workspace.unavailable[pcId] == true -> Text(stringResource(R.string.ai_ui_cap_failed), style = MaterialTheme.typography.bodySmall)
                    catalog == null -> Text(stringResource(R.string.ai_ui_cap_loading), style = MaterialTheme.typography.bodySmall)
                    options.isEmpty() -> Text(stringResource(R.string.ai_ui_no_agents), style = MaterialTheme.typography.bodySmall)
                    else -> FlowRow(Modifier.selectableGroup(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        options.forEach { option -> AiAgentChoice(option.id, option.name, selected = option.id == agent?.id,
                            enabled = ready && editable, onClick = { agentId = option.id }) }
                    }
                }
                if (remote) Text(if (initialDirectory != null) "ZCode 桌面远控不接收所选目录，工作目录需在 ZCode 中设置。" else "使用已配置的 ZCode 桌面远控，连接由 ZCode 服务提供。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                else if (agent != null) {
                    val agentConfiguration = agent.configuration
                    val permissionModes = agentConfiguration.permissionModes.ifEmpty {
                        // Compatibility for a pre-capability host. Only the
                        // legacy Codex/Claude fallback is retained by parsing.
                        catalog?.modes?.get(agent.id).orEmpty().map { AiPermissionMode(it) }
                    }
                    LaunchedEffect(agent.id, permissionModes.map { it.id }) {
                        if (permissionModes.none { it.id == mode }) {
                            mode = permissionModes.firstOrNull { it.id == "ask" }?.id ?: permissionModes.firstOrNull()?.id.orEmpty()
                            confirmedMode = null
                        }
                    }
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(if (managed) "自动创建独立目录" else "指定目录", style = MaterialTheme.typography.bodySmall)
                            Text(if (managed) "${catalog?.defaultDirectory} / ${agent.id}_时间戳" else directory.ifBlank { "选择电脑上的工作目录" },
                                maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        if (perSession) AiAction(if (managed) "修改" else "恢复默认", enabled = editable,
                            onClick = { customDirectory = !customDirectory })
                    }
                    if (!managed) {
                        OutlinedTextField(directory, { if (it.length <= 4096) directoryOverride = it },
                            label = { Text(stringResource(R.string.ai_ui_pc_workdir)) }, modifier = Modifier.fillMaxWidth(), maxLines = 3, enabled = editable, textStyle = MaterialTheme.typography.bodyMedium)
                        if (catalog?.directoryBrowsing == true) AiAction(stringResource(R.string.ai_ui_browse), Icons.Default.FolderOpen, enabled = editable && ready, onClick = { browseDirectory = true })
                        else Text(stringResource(R.string.ai_ui_upgrade_browse), style = MaterialTheme.typography.labelSmall)
                    }
                    key(pcKey, catalog?.hostId, agent.id, agent.strategy) {
                        AiModelPicker(workspace, pcId, agent.id, agentConfiguration.modelListing, ready, editable,
                            model, provider, onSelect = { selectedModel, selectedProvider ->
                                model = selectedModel; provider = selectedProvider
                                // onReasoning retains the effort only when the
                                // selected model reports that same option.
                            },
                            modelSwitch = agentConfiguration.modelSwitch, reasoning = reasoning, reasoningEffort = reasoningEffort,
                            reasoningSwitch = agentConfiguration.reasoningSwitch, reasoningScope = agentConfiguration.reasoningScope,
                            onReasoning = { next ->
                                reasoning = next
                                if (!next.contains(reasoningEffort)) reasoningEffort = ""
                            }, onEffortSelect = { reasoningEffort = it })
                    }
                    if (agentConfiguration.permissionSwitch && permissionModes.size > 1)
                        AiChoice("授权", permissionModes.map { it.id to aiPermissionLabel(it) }, mode, editable, Icons.Default.Security) { candidate ->
                            permissionModes.firstOrNull { it.id == candidate }?.let { selected ->
                                if (selected.id != mode) {
                                    confirmedMode = null
                                    if (selected.requiresConfirmation) proposedMode = selected.id
                                    else mode = selected.id
                                }
                            }
                        }
                }
                if (workspace.error.isNotEmpty()) Text(workspace.error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                if (workspace.uncertain) AiAction(stringResource(R.string.ai_ui_query_create), enabled = !workspace.busy, onClick = workspace::retryUncertain)
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    AiAction(stringResource(R.string.ai_ui_refresh), Icons.Default.Refresh, enabled = editable, onClick = { scope.launch { workspace.refresh(pcId) } })
                    Spacer(Modifier.weight(1f))
                    AiAction(if (workspace.busy) "正在创建…" else if (remote) "打开远控" else "创建对话", Icons.AutoMirrored.Filled.ArrowForward, filled = true,
                        enabled = ready && editable && agent != null && (remote || (managed || directory.isNotBlank()) &&
                            (agent.id != "zcode" || model.isBlank() == provider.isBlank())), onClick = {
                            agent?.let {
                                if (remote) workspace.openRemote(pcId, it.id)
                                else workspace.mutate(pcId, "create", extra = JSONObject().put("agentId", it.id).put("directory", directory.trim())
                                    .put("directoryMode", if (managed) "managed" else "custom")
                                    .put("permissionMode", mode)
                                    .put("modelId", model.trim()).put("providerId", provider.trim())
                                    .put("reasoningEffort", reasoningEffort)
                                    .apply { if (confirmedMode == mode && mode.isNotBlank()) put("confirmedPermissionMode", mode) })
                            }
                        })
                }
            }
            if (workspace.busy) LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp))
        }
    }
    if (browseDirectory && !remote) AiDirectoryPicker(workspace, pcId, directory, ready,
        onDismiss = { browseDirectory = false }, onSelect = { directoryOverride = it; customDirectory = true; browseDirectory = false })
    proposedMode?.let { candidate ->
        val selected = agent?.configuration?.permissionModes?.firstOrNull { it.id == candidate }
            ?: catalog?.modes?.get(agent?.id).orEmpty().map { AiPermissionMode(it) }.firstOrNull { it.id == candidate }
        AlertDialog(onDismissRequest = { proposedMode = null }, title = { Text(selected?.let(::aiPermissionLabel) ?: candidate) }, text = {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(selected?.let(::aiPermissionDetail) ?: "由 Agent 原生处理。")
                if (selected?.requiresConfirmation == true) Text(stringResource(R.string.ai_ui_permission_warning), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }, confirmButton = { TextButton(onClick = { mode = candidate; confirmedMode = candidate; proposedMode = null }) { Text(stringResource(R.string.ai_ui_confirm_new)) } },
            dismissButton = { TextButton(onClick = { proposedMode = null }) { Text(stringResource(R.string.ai_ui_cancel)) } })
    }
}

@Composable
private fun AiAgentChoice(id: String, name: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val dark = MaterialTheme.colorScheme.surface.luminance() < .5f
    val accent = when (aiAgentId(id)) {
        "codex" -> if (dark) Color(0xFF72DBBE) else Color(0xFF116859)
        "claude" -> if (dark) Color(0xFFFFB595) else Color(0xFF954727)
        "zcode" -> if (dark) Color(0xFFCBB8FF) else Color(0xFF6248A8)
        "grok" -> if (dark) Color(0xFF9BCBFF) else Color(0xFF305F96)
        else -> MaterialTheme.colorScheme.primary
    }
    val color = accent.copy(alpha = if (enabled) 1f else .5f)
    Box(Modifier.heightIn(min = 48.dp).widthIn(min = 48.dp)
        .selectable(selected, enabled, Role.RadioButton, onClick), contentAlignment = Alignment.CenterStart) {
        Surface(shape = RoundedCornerShape(50), color = color.copy(alpha = if (selected) .18f else .08f),
            border = BorderStroke(1.dp, color.copy(alpha = if (selected) .7f else .2f))) {
            Row(Modifier.padding(horizontal = 10.dp, vertical = 7.dp), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                AiAgentProductIcon(id, Modifier.size(18.dp).testTag("ai-agent-choice-icon-${aiAgentId(id)}"))
                Text(name, Modifier.weight(1f, fill = false), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.labelMedium)
                if (selected) Icon(Icons.Default.Check, null, Modifier.size(14.dp), tint = color)
            }
        }
    }
}

@Composable
private fun AiChoice(label: String, options: List<Pair<String, String>>, selected: String, enabled: Boolean,
    icon: ImageVector = Icons.Default.LaptopWindows, onSelect: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Box {
        Row(Modifier.heightIn(min = 48.dp).clickable(enabled = enabled && options.isNotEmpty(), role = Role.Button) { expanded = true }, verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.primary)
            Text("$label · ${options.firstOrNull { it.first == selected }?.second ?: "请选择"}", Modifier.weight(1f, fill = false).padding(horizontal = 8.dp),
                maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
            Icon(Icons.Default.ExpandMore, "选择$label", Modifier.size(18.dp))
        }
        DropdownMenu(expanded, { expanded = false }) {
            options.forEach { (id, name) -> DropdownMenuItem(text = { Text(name) }, onClick = { onSelect(id); expanded = false }) }
        }
    }
}

@Composable
internal fun AiAction(text: String, icon: ImageVector? = null, enabled: Boolean = true, filled: Boolean = false,
    description: String? = null, onClick: () -> Unit) {
    val color = if (enabled) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = .5f)
    Box(Modifier.heightIn(min = 48.dp).widthIn(min = 48.dp).clickable(enabled = enabled, role = Role.Button, onClick = onClick)
        .then(if (description == null) Modifier else Modifier.semantics { contentDescription = description }).padding(horizontal = 4.dp), contentAlignment = Alignment.Center) {
        Surface(shape = RoundedCornerShape(8.dp), color = if (filled) color.copy(alpha = .14f) else Color.Transparent,
            border = if (filled) BorderStroke(1.dp, color.copy(alpha = .35f)) else null) {
            Row(Modifier.padding(horizontal = 8.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                if (icon != null) Icon(icon, null, Modifier.size(16.dp), tint = color)
                Text(text, color = color, style = MaterialTheme.typography.labelMedium)
            }
        }
    }
}

@Composable
private fun AiAgentBadge(name: String) {
    AiAgentProductIcon(aiAgentId(name))
}

@Composable
private fun AiStatusTag(text: String, attention: Boolean = false) {
    val color = if (attention) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.onSurfaceVariant
    Surface(shape = RoundedCornerShape(5.dp), color = color.copy(alpha = .12f)) { Text(text, Modifier.padding(horizontal = 6.dp, vertical = 3.dp), color = color, style = MaterialTheme.typography.labelSmall) }
}

@Composable
private fun AiEmptyState(icon: ImageVector, title: String, detail: String, modifier: Modifier) {
    Column(modifier.padding(20.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(icon, null, Modifier.size(28.dp), tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(12.dp)); Text(title, style = MaterialTheme.typography.titleSmall); Spacer(Modifier.height(8.dp))
        Text(detail, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
    }
}

internal fun aiAgentStatusLabel(status: String): String = when (status) {
    "available" -> "协议可用"; "missing" -> "未安装"; "runtime_missing" -> "缺少运行环境"
    "desktop_only" -> "桌面版已安装，需兼容 CLI"; "runtime_blocked" -> "系统阻止启动引擎"
    "incompatible" -> "协议不兼容"; "probe_failed" -> "检测失败"; else -> "状态待确认"
}
internal fun aiStateLabel(state: String?): String = when (state) {
    "starting" -> "启动中"; "idle" -> "就绪"; "running" -> "生成中"; "exited" -> "已结束"; "interrupted" -> "已断开"; "suspended" -> "可恢复"; else -> "状态待确认"
}

@Composable
internal fun AiDeviceSessions(workspace: AiWorkspace, pc: PcSummary, expanded: Boolean, foreground: Boolean, onOpen: () -> Unit) {
    if ("ai_agent_v1" !in pc.capabilities) return
    LaunchedEffect(pc.id, expanded, pc.online, pc.remoteRoutingEnabled, foreground) {
        if (expanded && pc.online && pc.remoteRoutingEnabled && foreground) while (true) { workspace.refresh(pc.id); delay(6_000) }
    }
    if (!expanded) return
    val catalog = workspace.catalogs[pc.id]
    val online = pc.online && pc.remoteRoutingEnabled && workspace.unavailable[pc.id] != true
    val groups = aiCatalogEntries(pc.id, catalog).filterNot { it.sessionId in workspace.closedSessionIds }
        .groupBy { aiEntryAgentId(it, workspace.conversations[it.sessionId]) }
    Column(Modifier.padding(horizontal = 12.dp, vertical = 2.dp)) {
        when {
            !online -> Text(stringResource(R.string.ai_ui_ai_wait), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            catalog == null -> Text(stringResource(R.string.ai_ui_ai_loading), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            groups.isEmpty() -> Text(stringResource(R.string.ai_ui_ai_empty), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            else -> groups.forEach { (agentId, entries) ->
                Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable(role = Role.Button) {
                    workspace.browseAgentGroup(pc.id, agentId); onOpen()
                }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    AiAgentProductIcon(agentId)
                    Text(aiAgentName(agentId), Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                    val count = entries.count { it.strategy != "desktop_remote" }
                    Text(if (count == 0) stringResource(R.string.ai_remote_strategy) else stringResource(R.string.ai_group_count, count), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Icon(Icons.Default.ChevronRight, stringResource(R.string.ai_group_open, pc.name, aiAgentName(agentId)), Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

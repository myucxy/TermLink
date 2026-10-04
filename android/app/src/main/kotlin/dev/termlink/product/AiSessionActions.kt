package dev.termlink.product

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.FileProvider
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

@Composable
internal fun AiListTools(workspace: AiWorkspace, onlineIds: Set<UUID>) {
    var menu by remember { mutableStateOf(false) }
    var closing by remember { mutableStateOf(false) }
    val checked = remember { mutableStateMapOf<String, Boolean>() }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { workspace.setNotifications(it) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        AiCompactSearch(workspace.search, { workspace.search = it.take(120) }, Modifier.weight(1f))
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, "AI 列表操作") }
            DropdownMenu(menu, { menu = false }) {
                DropdownMenuItem(text = { Text("批量关闭手机视图") }, onClick = { menu = false; checked.clear(); closing = true })
                DropdownMenuItem(text = { Text(if (workspace.notificationsEnabled) "关闭后台提醒" else "开启后台提醒") }, onClick = {
                    menu = false
                    if (workspace.notificationsEnabled) workspace.setNotifications(false)
                    else if (Build.VERSION.SDK_INT >= 33) permission.launch(Manifest.permission.POST_NOTIFICATIONS) else workspace.setNotifications(true)
                })
            }
        }
    }
    if (closing) AlertDialog(onDismissRequest = { closing = false }, title = { Text("关闭手机视图") }, text = {
        Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())) {
            Text("电脑任务继续运行。未发送草稿和手机待发队列将被清除。", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { workspace.opened.forEach { checked[it.sessionId] = true } }) { Text("全选") }
            workspace.opened.forEach { entry -> Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked[entry.sessionId] == true, { checked[entry.sessionId] = it })
                AiSessionIdentity(workspace.conversations[entry.sessionId]?.takeIf { entry.pcId in onlineIds }, Modifier.weight(1f), aiEntryAgent(entry, null) + " · " + entry.sessionId.take(8))
            } }
        }
    }, confirmButton = { TextButton(onClick = { checked.filterValues { it }.keys.toList().forEach(workspace::closeView); checked.clear(); closing = false }) { Text("关闭所选") } }, dismissButton = { TextButton(onClick = { closing = false }) { Text("取消") } })
}

internal fun aiUsageLabel(raw: String): String = runCatching {
    val value = JSONObject(raw)
    val last = value.optJSONObject("last")
    val total = value.optJSONObject("total") ?: value.optJSONObject("tokens")
    buildList {
        if (total?.has("totalTokens") == true) add("累计 ${total.getLong("totalTokens")} tokens")
        if (total?.has("input_tokens") == true) add("输入 ${total.getLong("input_tokens")} · 输出 ${total.optLong("output_tokens")}")
        if (last?.has("totalTokens") == true && value.optLong("modelContextWindow") > 0) add("最近一轮 ${last.getLong("totalTokens")} / ${value.getLong("modelContextWindow")}")
        if (value.has("costUsd") && !value.isNull("costUsd")) add("Agent 报告费用 USD ${value.get("costUsd")}")
    }.joinToString("\n").ifBlank { "Agent 暂未报告用量" }
}.getOrDefault("Agent 暂未报告用量")

@Composable
internal fun AiFullMessageDialog(workspace: AiWorkspace, session: AiConversation, message: AiMessage, close: () -> Unit) {
    var content by remember(message.id) { mutableStateOf("") }
    var failure by remember { mutableStateOf(false) }
    var page by remember { mutableIntStateOf(0) }
    LaunchedEffect(session.id, message.id) {
        try { content = workspace.fullMessage(session, message) }
        catch (error: Exception) { if (error is kotlinx.coroutines.CancellationException) throw error; failure = true }
    }
    val pages = remember(content) { content.chunked(16000) }
    AlertDialog(onDismissRequest = close, title = { Text("完整内容") }, text = { Column {
        if (failure) Text("读取失败，请关闭后重试。") else if (pages.isEmpty()) LinearProgressIndicator(Modifier.fillMaxWidth())
        else { SelectionContainer { Text(pages[page], Modifier.heightIn(max = 380.dp).verticalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = page > 0, onClick = { page-- }) { Text("上一页") }
                Text("${page + 1}/${pages.size}", style = MaterialTheme.typography.labelSmall)
                TextButton(enabled = page < pages.lastIndex, onClick = { page++ }) { Text("下一页") }
            }
        }
    } }, confirmButton = { TextButton(onClick = close) { Text("关闭") } }, dismissButton = { if (content.isNotEmpty()) AiCopyAction(content, "复制全文") })
}

@Composable
internal fun AiSessionActions(workspace: AiWorkspace, session: AiConversation, close: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var title by remember(session.id) { mutableStateOf(session.title) }
    var detail by remember { mutableStateOf<String?>(null) }
    var status by remember { mutableStateOf("") }
    var exporting by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("text/markdown")) { uri ->
        if (uri != null) scope.launch {
            exporting = true
            try { val text = workspace.exportMarkdown(session); withContext(Dispatchers.IO) { context.contentResolver.openOutputStream(uri)?.bufferedWriter()?.use { it.write(text) } ?: error("No output") }; status = "已导出" }
            catch (failure: Exception) { if (failure is kotlinx.coroutines.CancellationException) throw failure; status = "导出失败，请重试" }
            finally { exporting = false }
        }
    }
    AlertDialog(onDismissRequest = close, title = { Text("会话管理") }, text = {
        Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            OutlinedTextField(title, { title = it.filterNot(Char::isISOControl).take(80) }, label = { Text("名称") }, singleLine = true)
            Row {
                TextButton(onClick = { workspace.mutate(session.pcId, "rename", session, JSONObject().put("text", title)); close() }, enabled = session.canControl && title.isNotBlank() && !workspace.busy) { Text("重命名") }
                TextButton(onClick = { workspace.togglePin(session.id) }) { Text(if (workspace.isPinned(session.id)) "取消置顶" else "置顶") }
            }
            Text(aiUsageLabel(session.usage), style = MaterialTheme.typography.bodySmall)
            if (session.archiveError.isNotEmpty()) Text("电脑历史保存失败，请检查磁盘空间。", color = MaterialTheme.colorScheme.error)
            if (session.capabilities.history && session.state in setOf("exited", "interrupted", "suspended")) TextButton(onClick = { confirmDelete = true }, enabled = !workspace.busy && !workspace.uncertain) { Text("删除电脑上的会话记录", color = MaterialTheme.colorScheme.error) }
            if (session.compacting) Text("Agent 正在整理上下文…", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { detail = session.diff.ifBlank { session.messages.filter { it.role == "tool" && it.title == "修改文件" }.joinToString("\n\n") { it.text }.ifBlank { "Agent 尚未报告文件变更。" } } }) { Text("查看文件变更 / Diff") }
            if (session.capabilities.fork) TextButton(onClick = { workspace.mutate(session.pcId, "fork", session); close() }, enabled = session.canControl && session.state == "idle" && session.pendingCount == 0 && !workspace.busy && !workspace.uncertain) { Text("从当前上下文创建分支") }
            if (session.capabilities.fork) Text("分支继承当前上下文，共用电脑工作目录。", style = MaterialTheme.typography.labelSmall)
            if (session.capabilities.history) {
                TextButton(onClick = { save.launch("termlink-${session.id.take(8)}.md") }, enabled = !exporting) { Text("导出 Markdown") }
                TextButton(enabled = !exporting, onClick = { scope.launch {
                    exporting = true
                    try {
                        val text = workspace.exportMarkdown(session)
                        val file = withContext(Dispatchers.IO) { File(context.cacheDir, "ai/exports/${UUID.randomUUID()}.md").also { it.parentFile?.mkdirs(); it.writeText(text) } }
                        val uri = FileProvider.getUriForFile(context, context.packageName + ".fileprovider", file)
                        context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/markdown").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "分享对话"))
                    } catch (failure: Exception) { if (failure is kotlinx.coroutines.CancellationException) throw failure; status = "分享失败，请重试" }
                    finally { exporting = false }
                } }) { Text("分享对话") }
            }
            if (exporting) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (status.isNotEmpty()) Text(status, style = MaterialTheme.typography.bodySmall)
        }
    }, confirmButton = { TextButton(onClick = close) { Text("关闭") } })
    if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false }, title = { Text("删除会话记录？") }, text = { Column { AiSessionIdentity(session); Text("删除 Windows 中的这条 TermLink 历史记录。工作目录、文件和 Agent 自己的原生历史保留。") } },
        confirmButton = { TextButton(onClick = { confirmDelete = false; workspace.mutate(session.pcId, "forget", session); close() }) { Text("删除记录") } }, dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("取消") } })
    detail?.let { text -> AlertDialog(onDismissRequest = { detail = null }, title = { Text("文件变更") }, text = {
        SelectionContainer { Text(text, Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
    }, confirmButton = { TextButton(onClick = { detail = null }) { Text("关闭") } }, dismissButton = { Row { AiCopyAction(text); TextButton(onClick = { workspace.quote(session.id, text); detail = null; close() }) { Text("引用") } } }) }
}

/** Best-effort reminders while this application process and its authenticated connection live.
 * No prompt text or provider content is placed in a lock-screen notification. */
@Composable
internal fun AiAttentionMonitor(workspace: AiWorkspace, snapshot: ProductSnapshot, foreground: Boolean) {
    val context = LocalContext.current
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    val latest by rememberUpdatedState(snapshot)
    SideEffect { workspace.foreground = foreground }
    DisposableEffect(workspace) {
        val manager = context.getSystemService(NotificationManager::class.java)
        val account = aiAccountKey(latest)
        manager.createNotificationChannel(NotificationChannel("ai_attention", "AI 任务提醒", NotificationManager.IMPORTANCE_DEFAULT))
        workspace.notifyAttention = { session, text ->
            runCatching { showAiNotification(context, aiAccountKey(latest), workspace.catalogs[session.pcId]?.hostId.orEmpty(), session, text) }
                .onSuccess { aiReminderTrace("notification posted") }.onFailure { aiReminderTrace("notification failed: ${it.javaClass.simpleName}") }
        }
        workspace.clearAttention = { id -> manager.cancel(id.hashCode()); AiNotificationTickets(context).removeSession(account, id) }
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_STOP) {
                workspace.watchInBackground(if (latest.signedIn) latest.pcs.filter { it.online && it.remoteRoutingEnabled }.mapTo(hashSetOf()) { it.id } else emptySet())
            } else if (event == androidx.lifecycle.Lifecycle.Event.ON_START) workspace.stopBackgroundWatch()
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); workspace.stopBackgroundWatch(); workspace.notifyAttention = null; workspace.clearAttention = null }
    }
    LaunchedEffect(workspace, foreground, workspace.notificationsEnabled, snapshot.signedIn, snapshot.pcs.map { it.id to it.online }) {
        if (foreground) workspace.stopBackgroundWatch()
        else workspace.watchInBackground(if (snapshot.signedIn) snapshot.pcs.filter { it.online && it.remoteRoutingEnabled }.mapTo(hashSetOf()) { it.id } else emptySet())
    }
}

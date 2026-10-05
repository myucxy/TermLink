package dev.termlink.product

import dev.termlink.harness.R

import androidx.compose.ui.res.stringResource

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
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
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
internal fun AiListTools(workspace: AiWorkspace, onlineIds: Set<UUID>, entries: List<AiOpened>) {
    var menu by remember { mutableStateOf(false) }
    var closing by remember { mutableStateOf(false) }
    var reminderSettings by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { workspace.setNotifications(it) }
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        AiCompactSearch(workspace.search, { workspace.search = it.take(120) }, Modifier.weight(1f))
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, "AI 列表操作") }
            DropdownMenu(menu, { menu = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.ai_ui_close_bulk)) }, onClick = { menu = false; closing = true })
                DropdownMenuItem(text = { Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.ai_reminder_settings)) }, onClick = { menu = false; reminderSettings = true })
            }
        }
    }
    if (reminderSettings) {
        var allowed by remember { mutableStateOf(false) }
        val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
        DisposableEffect(lifecycle) {
            fun refresh() {
                val manager = context.getSystemService(NotificationManager::class.java)
                allowed = NotificationManagerCompat.from(context).areNotificationsEnabled() &&
                    manager.getNotificationChannel("ai_attention")?.importance != NotificationManager.IMPORTANCE_NONE
            }
            refresh()
            val observer = androidx.lifecycle.LifecycleEventObserver { _, event -> if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME) refresh() }
            lifecycle.addObserver(observer)
            onDispose { lifecycle.removeObserver(observer) }
        }
        AlertDialog(onDismissRequest = { reminderSettings = false }, title = { Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.ai_reminder_settings)) }, text = {
            Column {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.ai_reminder_running), Modifier.weight(1f))
                    Switch(workspace.notificationsEnabled, { enabled ->
                        if (enabled && Build.VERSION.SDK_INT >= 33 && !allowed) permission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        else workspace.setNotifications(enabled)
                    })
                }
                Text(androidx.compose.ui.res.stringResource(if (allowed) dev.termlink.harness.R.string.ai_notifications_allowed else dev.termlink.harness.R.string.ai_notifications_blocked), style = MaterialTheme.typography.bodySmall)
                Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.ai_reminder_limit), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.bodySmall)
            }
        }, confirmButton = { TextButton(onClick = { reminderSettings = false }) { Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.action_close)) } },
            dismissButton = { TextButton(onClick = {
                runCatching { context.startActivity(Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName)) }
            }) { Text(androidx.compose.ui.res.stringResource(dev.termlink.harness.R.string.ai_notification_system_settings)) } })
    }
    if (closing) AiCloseViewsDialog(workspace, entries, onlineIds) { closing = false }
}

@Composable
internal fun AiCloseViewsDialog(workspace: AiWorkspace, entries: List<AiOpened>, onlineIds: Set<UUID>, close: () -> Unit) {
    val checked = remember { mutableStateMapOf<String, Boolean>() }
    val selected = entries.filter { checked[it.sessionId] == true }
    AlertDialog(onDismissRequest = close, title = { Text(stringResource(R.string.ai_ui_close_bulk)) }, text = {
        Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.ai_ui_close_view_hint), style = MaterialTheme.typography.bodySmall)
            if (entries.isEmpty()) Text(stringResource(R.string.ai_ui_close_empty), Modifier.padding(vertical = 16.dp))
            else TextButton(onClick = {
                val select = selected.size != entries.size
                entries.forEach { checked[it.sessionId] = select }
            }) { Text(stringResource(if (selected.size == entries.size) R.string.ai_ui_deselect_all else R.string.ai_ui_select_all)) }
            entries.forEach { entry -> Row(
                Modifier.fillMaxWidth().heightIn(min = 48.dp).toggleable(
                    value = checked[entry.sessionId] == true, role = Role.Checkbox,
                    onValueChange = { checked[entry.sessionId] = it }),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Checkbox(checked[entry.sessionId] == true, onCheckedChange = null, modifier = Modifier.padding(end = 8.dp))
                AiSessionIdentity(workspace.conversations[entry.sessionId]?.takeIf { entry.pcId in onlineIds }, Modifier.weight(1f), aiEntryAgent(entry, null) + " · " + entry.sessionId.take(8))
            } }
        }
    }, confirmButton = { TextButton(
        enabled = selected.isNotEmpty(),
        onClick = { selected.forEach { workspace.closeView(it.sessionId) }; close() },
    ) { Text("${stringResource(R.string.ai_ui_close_selected)} (${selected.size})") } }, dismissButton = { TextButton(onClick = close) { Text(stringResource(R.string.ai_ui_cancel)) } })
}

@Composable
internal fun AiLifecycleDialog(workspace: AiWorkspace, session: AiConversation, delete: Boolean, close: () -> Unit) {
    val scope = rememberCoroutineScope()
    val busy = workspace.isBusy(session.pcId)
    val blocked = workspace.isBlocked(session.pcId)
    AlertDialog(onDismissRequest = { if (!busy) close() }, title = {
        Text(stringResource(if (delete) R.string.ai_ui_delete_question else R.string.ai_ui_end_question))
    }, text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        AiSessionIdentity(session)
        Text(stringResource(if (delete) R.string.ai_ui_delete_hint else R.string.ai_ui_end_hint))
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (workspace.error.isNotBlank()) Text(workspace.error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
    } }, confirmButton = { TextButton(enabled = !blocked, onClick = { scope.launch {
        val current = workspace.conversations[session.id] ?: session
        val succeeded = if (delete) workspace.mutate(current.pcId, "forget", current)?.await() == true
            else workspace.endSession(current).await()
        if (succeeded) close()
    } }) { Text(stringResource(if (delete) R.string.ai_ui_delete_history else R.string.ai_ui_end)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = close) { Text(stringResource(R.string.ai_ui_cancel)) } })
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
    AlertDialog(onDismissRequest = close, title = { Text(stringResource(R.string.ai_ui_full)) }, text = { Column {
        if (failure) Text(stringResource(R.string.ai_ui_read_failed)) else if (pages.isEmpty()) LinearProgressIndicator(Modifier.fillMaxWidth())
        else { SelectionContainer { Text(pages[page], Modifier.heightIn(max = 380.dp).verticalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = page > 0, onClick = { page-- }) { Text(stringResource(R.string.ai_ui_previous_page)) }
                Text("${page + 1}/${pages.size}", style = MaterialTheme.typography.labelSmall)
                TextButton(enabled = page < pages.lastIndex, onClick = { page++ }) { Text(stringResource(R.string.ai_ui_next_page)) }
            }
        }
    } }, confirmButton = { TextButton(onClick = close) { Text(stringResource(R.string.ai_ui_close)) } }, dismissButton = { if (content.isNotEmpty()) AiCopyAction(content, "复制全文") })
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
    AlertDialog(onDismissRequest = close, title = { Text(stringResource(R.string.ai_ui_manage)) }, text = {
        Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            OutlinedTextField(title, { title = it.filterNot(Char::isISOControl).take(80) }, label = { Text(stringResource(R.string.ai_ui_name)) }, singleLine = true)
            Row {
                TextButton(onClick = { workspace.mutate(session.pcId, "rename", session, JSONObject().put("text", title)); close() }, enabled = session.canControl && title.isNotBlank() && !workspace.busy) { Text(stringResource(R.string.ai_ui_rename)) }
                TextButton(onClick = { workspace.togglePin(session.id) }) { Text(if (workspace.isPinned(session.id)) "取消置顶" else "置顶") }
            }
            Text(aiUsageLabel(session.usage), style = MaterialTheme.typography.bodySmall)
            if (session.archiveError.isNotEmpty()) Text(stringResource(R.string.ai_ui_archive_failed), color = MaterialTheme.colorScheme.error)
            if (session.capabilities.history && aiIsHistorical(session)) TextButton(onClick = { confirmDelete = true }, enabled = !workspace.isBlocked(session.pcId)) { Text(stringResource(R.string.ai_ui_delete_history), color = MaterialTheme.colorScheme.error) }
            if (session.compacting) Text(stringResource(R.string.ai_ui_compacting), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { detail = session.diff.ifBlank { session.messages.filter { it.role == "tool" && it.title == "修改文件" }.joinToString("\n\n") { it.text }.ifBlank { "Agent 尚未报告文件变更。" } } }) { Text(stringResource(R.string.ai_ui_diff)) }
            if (session.capabilities.fork) TextButton(onClick = { workspace.mutate(session.pcId, "fork", session); close() }, enabled = session.canControl && session.state == "idle" && session.pendingCount == 0 && !workspace.busy && !workspace.uncertain) { Text(stringResource(R.string.ai_ui_fork)) }
            if (session.capabilities.fork) Text(stringResource(R.string.ai_ui_fork_hint), style = MaterialTheme.typography.labelSmall)
            if (session.capabilities.history) {
                TextButton(onClick = { save.launch("termlink-${session.id.take(8)}.md") }, enabled = !exporting) { Text(stringResource(R.string.ai_ui_export)) }
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
    }, confirmButton = { TextButton(onClick = close) { Text(stringResource(R.string.ai_ui_close)) } })
    if (confirmDelete) AiLifecycleDialog(workspace, session, delete = true) { confirmDelete = false }
    detail?.let { text -> AlertDialog(onDismissRequest = { detail = null }, title = { Text("文件变更") }, text = {
        SelectionContainer { Text(text, Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall) }
    }, confirmButton = { TextButton(onClick = { detail = null }) { Text(stringResource(R.string.ai_ui_close)) } }, dismissButton = { Row { AiCopyAction(text); TextButton(onClick = { workspace.quote(session.id, text); detail = null; close() }) { Text(stringResource(R.string.ai_ui_quote)) } } }) }
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

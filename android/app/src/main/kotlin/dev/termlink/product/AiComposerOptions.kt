package dev.termlink.product

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import java.io.File
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal data class AiQueued(val id: String, val text: String, val host: String, val controlVersion: Long, val failed: Boolean = false, val restored: Boolean = false)
internal class AiUpload(val id: String, val uri: Uri, val host: String, val controlVersion: Long) {
    var name by mutableStateOf("附件")
    var mime = "application/octet-stream"
    var size = 0L
    var hash = ""
    var file: File? = null
    var sent by mutableLongStateOf(0)
    var status by mutableStateOf("preparing")
    var error by mutableStateOf("")
    var job: Job? = null
    suspend fun prepare(context: Context, caps: AiCapabilities, directory: File? = null) = withContext(Dispatchers.IO) {
        require(uri.scheme == "content")
        val resolver = context.contentResolver
        resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) name = cursor.getString(0).orEmpty().filterNot(Char::isISOControl).take(180).ifBlank { "附件" }
        }
        mime = resolver.getType(uri).orEmpty().ifBlank { "application/octet-stream" }.take(120)
        val target = File(directory ?: File(context.cacheDir, "ai/uploads"), id).also { it.parentFile?.mkdirs() }
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            size = 0
            resolver.openInputStream(uri).use { input ->
                requireNotNull(input)
                target.outputStream().use { output ->
                    val bytes = ByteArray(32768)
                    while (true) {
                        val count = input.read(bytes); if (count < 0) break
                        size += count; require(size <= caps.maxFileBytes) { "附件超过单文件大小限制" }
                        digest.update(bytes, 0, count); output.write(bytes, 0, count)
                    }
                }
            }
            require(size > 0) { "不能上传空文件" }
            hash = digest.digest().joinToString("") { "%02x".format(it) }; file = target
        } catch (failure: Exception) { target.delete(); throw failure }
    }
    suspend fun validateCache(caps: AiCapabilities) = withContext(Dispatchers.IO) {
        val staged = checkNotNull(file) { "附件缓存已清理，请重新选择文件。" }
        require(size in 1..caps.maxFileBytes && staged.isFile && staged.length() == size) { "附件缓存已失效，请重新选择文件。" }
        val digest = MessageDigest.getInstance("SHA-256")
        staged.inputStream().use { input -> val bytes = ByteArray(32768); while (true) {
            val count = input.read(bytes); if (count < 0) break; digest.update(bytes, 0, count)
        } }
        require(digest.digest().joinToString("") { "%02x".format(it) } == hash) { "附件缓存发生变化，请重新选择文件。" }
    }
}

internal fun aiModeLabel(mode: String) = when (mode) {
    "ask", "default" -> "按需确认"
    "plan" -> "计划模式"
    "read-only" -> "只读沙箱"
    "accept-edits", "accept_edits" -> "自动编辑"
    "auto_review", "auto-review" -> "帮我批准"
    "full-access", "full_access" -> "完全访问"
    "dont-ask", "dont_ask" -> "不询问"
    else -> mode.ifBlank { "授权方式" }
}
internal fun aiModeDetail(mode: String) = when (mode) {
    "plan" -> "使用 Claude 原生计划模式。它是工具权限策略，不是操作系统只读沙箱。"
    "read-only" -> "使用 Codex 原生只读沙箱；需要提升权限的操作仍会请求审批。"
    "accept-edits" -> "使用 Claude 原生 acceptEdits，自动批准其定义的文件编辑工具。命令及其他操作仍按 Agent 原生规则处理。"
    "ask", "default" -> "沿用 Agent 原生默认审批规则；需要授权的操作会显示审批卡片。"
    else -> "由 Agent 原生处理的未知授权模式；TermLink 不会自行扩大权限。"
}
internal fun aiPermissionLabel(mode: AiPermissionMode): String = mode.label.ifBlank { mode.id }
internal fun aiPermissionDetail(mode: AiPermissionMode): String = mode.description.ifBlank {
    "由 ${mode.id} Agent 原生处理。TermLink 不会代替 Agent 扩大权限。"
}

@Composable
internal fun AiEnhancedComposer(workspace: AiWorkspace, session: AiConversation) {
    val context = LocalContext.current
    val draft = workspace.drafts[session.id].orEmpty()
    val files = workspace.attachments[session.id].orEmpty()
    val references = workspace.fileReferences[session.id].orEmpty()
    val enabled = session.canControl && !workspace.busy && !workspace.uncertain
    val configurable = enabled && session.state == "idle" && session.pendingCount == 0
    var menu by remember { mutableStateOf(false) }
    var modes by remember { mutableStateOf(false) }
    var confirmMode by remember { mutableStateOf<AiPermissionMode?>(null) }
    var inspect by remember { mutableStateOf<AiUpload?>(null) }
    var browseFile by remember { mutableStateOf(false) }
    var cameraUri by remember { mutableStateOf<Uri?>(null) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        uris.take((session.capabilities.maxFiles - files.size).coerceAtLeast(0)).forEach { workspace.upload(context, it, session) }
    }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { success ->
        if (success) cameraUri?.let { workspace.upload(context, it, session) }
    }
    Surface(color = MaterialTheme.colorScheme.surface) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 10.dp, vertical = 4.dp)) {
            workspace.queued[session.id].orEmpty().forEach { item ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${if (item.restored) "已恢复 · 请取回后发送" else if (item.failed) "发送失败 · 请取回" else "手机待发"} · ${item.text}", Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall)
                    TextButton(onClick = { workspace.cancelQueued(session.id, item) }) { Text("取回", style = MaterialTheme.typography.labelSmall) }
                }
            }
            if (files.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                files.forEach { file ->
                    InputChip(selected = false, onClick = { inspect = file }, label = {
                        Text(file.name.take(18) + " · " + when (file.status) { "ready" -> "就绪"; "failed" -> "重试"; "uploading" -> "${file.sent * 100 / file.size.coerceAtLeast(1)}%"; else -> "准备中" }, style = MaterialTheme.typography.labelSmall)
                    }, trailingIcon = { IconButton(onClick = { workspace.removeUpload(session, file) }, Modifier.size(32.dp)) { Icon(Icons.Default.Close, "移除 ${file.name}", Modifier.size(14.dp)) } })
                }
            }
            if (references.isNotEmpty()) Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                references.forEach { path -> InputChip(false, { workspace.fileReferences[session.id] = references - path }, label = { Text("@" + path.substringAfterLast('\\').take(28), style = MaterialTheme.typography.labelSmall) }, trailingIcon = { Icon(Icons.Default.Close, "移除电脑文件引用", Modifier.size(14.dp)) }) }
            }
            Surface(shape = RoundedCornerShape(12.dp), color = MaterialTheme.colorScheme.background, border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)) {
                Column {
                    BasicTextField(draft, { if (it.length <= 16000) workspace.drafts[session.id] = it },
                        Modifier.fillMaxWidth().heightIn(min = 44.dp).padding(horizontal = 12.dp, vertical = 10.dp).semantics { contentDescription = "输入消息" },
                        enabled = enabled, maxLines = 5, textStyle = MaterialTheme.typography.bodyMedium.copy(color = MaterialTheme.colorScheme.onSurface), cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                        decorationBox = { inner -> if (draft.isEmpty()) Text(if (session.canControl) "输入消息…" else "请在云朵菜单重新确认连接", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium); inner() })
                    if (draft.startsWith("/") && !draft.contains(' ') && session.slashCommands.isNotEmpty()) Row(Modifier.horizontalScroll(rememberScrollState())) {
                        session.slashCommands.filter { it.startsWith(draft.removePrefix("/")) }.take(8).forEach { command -> TextButton(onClick = { workspace.drafts[session.id] = "/$command " }) { Text("/$command") } }
                    }
                    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp), verticalAlignment = Alignment.CenterVertically) {
                        if (session.capabilities.attachments || session.capabilities.steer && session.state == "running") Box {
                            IconButton(onClick = { menu = true }, enabled = enabled) { Icon(Icons.Default.Add, "添加附件或引用", Modifier.size(20.dp)) }
                            DropdownMenu(menu, { menu = false }) {
                                if (session.capabilities.attachments) {
                                    DropdownMenuItem(text = { Text("图片") }, onClick = { menu = false; picker.launch(arrayOf("image/*")) })
                                    DropdownMenuItem(text = { Text("手机文件") }, onClick = { menu = false; picker.launch(arrayOf("*/*")) })
                                    DropdownMenuItem(text = { Text("拍照") }, onClick = {
                                        menu = false
                                        val file = File(context.cacheDir, "ai/camera/${UUID.randomUUID()}.jpg").also { it.parentFile?.mkdirs() }
                                        cameraUri = FileProvider.getUriForFile(context, context.packageName + ".fileprovider", file)
                                        runCatching { camera.launch(cameraUri!!) }.onFailure { cameraUri = null; file.delete() }
                                    })
                                }
                                if (session.capabilities.attachments) DropdownMenuItem(text = { Text("引用电脑文件 @") }, enabled = references.size < 5, onClick = { menu = false; browseFile = true })
                                if (session.capabilities.steer && session.state == "running") DropdownMenuItem(text = { Text("立即补充当前任务") }, enabled = draft.isNotBlank() && files.isEmpty(), onClick = {
                                    menu = false; workspace.mutate(session.pcId, "steer", session, JSONObject().put("text", draft))
                                })
                            }
                        }
                        val configuration = session.capabilities.configuration
                        val permissionModes = configuration.permissionModes
                        val showModelControl = configuration.hasModelControl || configuration.reasoningSwitch || configuration.reasoningScope == "new_session"
                        if (configuration.permissionSwitch && permissionModes.isNotEmpty()) {
                            Box {
                                TextButton(onClick = { modes = true }, enabled = configurable, modifier = Modifier.widthIn(min = 48.dp, max = 112.dp), contentPadding = PaddingValues(horizontal = 4.dp)) {
                                    Text(permissionModes.firstOrNull { it.id == session.permissionMode }?.let(::aiPermissionLabel) ?: session.permissionMode.ifBlank { "授权方式" },
                                        modifier = Modifier.weight(1f, fill = false), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall)
                                    Icon(Icons.Default.ExpandMore, "授权方式", Modifier.size(14.dp))
                                }
                                DropdownMenu(modes, { modes = false }) {
                                    permissionModes.forEach { mode ->
                                        DropdownMenuItem(text = {
                                            Column {
                                                Text(aiPermissionLabel(mode))
                                                if (mode.description.isNotBlank()) Text(mode.description, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall)
                                            }
                                        }, onClick = {
                                            modes = false
                                            if (mode.requiresConfirmation && mode.id != session.permissionMode) confirmMode = mode
                                            else workspace.configure(session, mode = mode.id)
                                        })
                                    }
                                }
                            }
                        }
                        Spacer(Modifier.width(4.dp))
                        if (showModelControl) {
                            AiModelPicker(workspace, session.pcId, session.agentId, configuration.modelListing, true, configurable,
                                session.modelId, session.providerId, onSelect = { model, provider -> workspace.configure(session, model, provider) },
                                modifier = Modifier.weight(1f), compact = true,
                                modelSwitch = configuration.modelSwitch, reasoning = session.reasoning, reasoningEffort = session.reasoningEffort,
                                reasoningSwitch = configuration.reasoningSwitch, reasoningScope = configuration.reasoningScope, existingSession = true,
                                allowDefaultModel = session.agentId != "zcode",
                                onEffortSelect = { effort -> workspace.configure(session, reasoningEffort = effort) })
                        } else Spacer(Modifier.weight(1f))
                        val running = session.state == "running"
                        val sendable = enabled && session.state == "idle" && session.pendingCount == 0 && (draft.isNotBlank() || files.isNotEmpty() || references.isNotEmpty()) && files.all { it.status == "ready" }
                        if (running && draft.isNotBlank()) IconButton(onClick = { workspace.enqueue(session) }, enabled = enabled && files.isEmpty() && references.isEmpty()) { Icon(Icons.AutoMirrored.Filled.PlaylistAdd, "加入手机待发队列", Modifier.size(20.dp)) }
                        IconButton(enabled = if (running) enabled else sendable, onClick = { if (running) workspace.mutate(session.pcId, "interrupt", session) else workspace.send(session) }) {
                            Icon(if (running) Icons.Default.Stop else Icons.AutoMirrored.Filled.Send, if (running) "停止生成" else "发送消息", Modifier.size(20.dp), tint = MaterialTheme.colorScheme.primary)
                        }
                    }
                }
            }
        }
    }
    if (browseFile) AiDirectoryPicker(workspace, session.pcId, session.directory, true, onDismiss = { browseFile = false }, onSelect = { path -> workspace.fileReferences[session.id] = (references + path).distinct().take(5); browseFile = false }, selectFile = true)
    confirmMode?.let { mode -> AlertDialog(onDismissRequest = { confirmMode = null }, title = { Text(aiPermissionLabel(mode)) }, text = {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(aiPermissionDetail(mode))
            if (mode.requiresConfirmation) Text("该授权模式可能扩大 Agent 的操作范围，请确认后继续。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { TextButton(enabled = configurable, onClick = { confirmMode = null; workspace.configure(session, mode = mode.id, confirmedMode = mode.id) }) { Text("确认并应用") } },
        dismissButton = { TextButton(onClick = { confirmMode = null }) { Text("取消") } }) }
    if (session.settingsNotice.isNotBlank()) Text(session.settingsNotice, Modifier.padding(horizontal = 12.dp, vertical = 2.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    inspect?.let { file -> AlertDialog(onDismissRequest = { inspect = null }, title = { Text(file.name) }, text = { Text(if (file.error.isNotEmpty()) file.error else "${file.size} 字节 · ${file.status}\n" + if (session.capabilities.nativeImages) "图片使用原生输入，识图取决于所选模型。其他附件作为电脑文件引用。" else "附件作为电脑文件引用。") },
        confirmButton = { if (file.status == "failed") TextButton(onClick = { inspect = null; workspace.retryUpload(context, session, file) }) { Text("重试上传") } else TextButton(onClick = {
            file.file?.let { staged -> runCatching { context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(FileProvider.getUriForFile(context, context.packageName + ".fileprovider", staged), file.mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)) } }
        }) { Text("预览") } }, dismissButton = { TextButton(onClick = { inspect = null; workspace.removeUpload(session, file) }) { Text("移除") } }) }
}

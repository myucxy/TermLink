package dev.termlink.product

import android.content.SharedPreferences
import android.content.Context
import androidx.lifecycle.ViewModel
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.delay
import androidx.compose.foundation.lazy.LazyListState
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.async
import kotlinx.coroutines.Deferred
import org.json.JSONArray
import org.json.JSONObject

internal data class AiPermissionMode(
    val id: String,
    val label: String = "",
    val description: String = "",
    val requiresConfirmation: Boolean = true,
)
internal data class AiReasoningOption(val value: String, val description: String = "")
internal data class AiReasoning(
    val status: String = "unknown",
    val options: List<AiReasoningOption> = emptyList(),
    val defaultValue: String = "",
) {
    val available: Boolean get() = status == "available" && options.isNotEmpty()
    fun contains(value: String): Boolean = value.isNotBlank() && options.any { it.value == value }
    fun effective(value: String): String = if (contains(value)) value else defaultValue.takeIf { it.isNotBlank() && contains(it) }.orEmpty()
}
internal data class AiAgentConfiguration(
    val modelListing: Boolean = false,
    val modelSwitch: Boolean = false,
    val reasoningSwitch: Boolean = false,
    val reasoningScope: String = "session",
    val permissionSwitch: Boolean = false,
    val permissionModes: List<AiPermissionMode> = emptyList(),
) {
    val hasModelControl: Boolean get() = modelSwitch || modelListing
    val hasPermissionControl: Boolean get() = permissionSwitch && permissionModes.isNotEmpty()
}
internal data class AiAgentOption(val id: String, val name: String, val version: String, val status: String, val strategy: String = "native",
    val configuration: AiAgentConfiguration = AiAgentConfiguration(), val nativeHistory: Boolean = false) {
    val supported: Boolean get() = status == "available" && (strategy == "native" || strategy == "desktop_remote" && id == "zcode")
}
internal data class AiMessage(val id: String, val role: String, val text: String,
    val kind: String = "text", val title: String = "", val summary: String = "", val status: String = "unknown", val model: String = "", val mode: String = "", val fullLength: Int = 0)
internal data class AiCapabilities(val configure: Boolean = false, val steer: Boolean = false, val resume: Boolean = false,
    val fork: Boolean = false, val history: Boolean = false, val attachments: Boolean = false,
    val nativeImages: Boolean = false, val modes: List<String> = emptyList(), val maxFileBytes: Long = 0,
    val maxTotalBytes: Long = 0, val maxFiles: Int = 0, val chunkBytes: Int = 0,
    val configuration: AiAgentConfiguration = AiAgentConfiguration(), val changes: Boolean = false, val search: Boolean = false, val artifacts: Boolean = false, val watch: Boolean = false, val diffRead: Boolean = false)
internal data class AiQuestion(val id: String, val text: String, val options: List<String>, val multiSelect: Boolean)
internal data class AiPending(val id: String, val kind: String, val detail: String, val questions: List<AiQuestion>, val canApprove: Boolean,
    val title: String = "", val summary: String = "")
internal class AiReadingState {
    val list = LazyListState()
    var followsTail by mutableStateOf(true)
}
internal data class AiConversation(
    val id: String, val pcId: UUID, val agent: String, val directory: String, val state: String,
    val error: String, val controlVersion: Long, val canControl: Boolean,
    val messages: List<AiMessage>, val pending: List<AiPending>, val truncated: Boolean,
    val title: String = "", val preview: String = "", val pendingCount: Int = pending.size,
    val agentId: String = "", val modelId: String = "", val providerId: String = "", val permissionMode: String = "ask",
    val settingsVersion: Long = 0, val configurationScope: String = "", val capabilities: AiCapabilities = AiCapabilities(),
    val historyBefore: Long = 0, val historyCount: Int = 0, val diff: String = "", val usage: String = "", val compacting: Boolean = false,
    val slashCommands: List<String> = emptyList(), val archiveError: String = "",
    val reasoningEffort: String = "", val reasoning: AiReasoning = AiReasoning(), val settingsNotice: String = "",
    val streamId: String = "", val cursor: String = "0", val phase: String = "", val turnStartedAtMs: Long = 0,
    val lastActivityAtMs: Long = 0, val plan: String = "",
    val diffLength: Int = diff.length, val diffTruncated: Boolean = false,
)
internal data class AiCatalog(val hostId: String, val agents: List<AiAgentOption>, val sessions: List<AiConversation>, val defaultDirectory: String = "",
    val directoryMode: String = "custom", val directoryBrowsing: Boolean = false, val modelListing: Boolean = false, val modes: Map<String, List<String>> = emptyMap(), val cliWorkspaceRoot: String = "")
internal data class AiModelOption(val modelId: String, val name: String, val providerId: String = "", val isDefault: Boolean = false,
    val reasoning: AiReasoning = AiReasoning())
internal data class AiModelList(val models: List<AiModelOption>, val truncated: Boolean)
internal data class AiNativeEntry(val id: String, val title: String, val directory: String, val updatedAtMs: Long)
internal data class AiNativePage(val sessions: List<AiNativeEntry>, val nextCursor: String, val truncated: Boolean)
internal data class AiOpened(val pcId: UUID, val sessionId: String, val strategy: String = "native", val agentId: String = "")
internal class AiQuestionDraft {
    val answers = mutableStateMapOf<String, String>()
    val choices = mutableStateMapOf<String, Set<String>>()
}

internal class AiWorkspaceViewModel : ViewModel() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var current: AiWorkspace? = null
    private var account = ""
    private var repository: ProductRepository? = null
    fun acquire(context: Context, repository: ProductRepository, accountKey: String): AiWorkspace {
        if (this.repository !== repository || account != accountKey) {
            current?.retire(clearPending = true)
            this.repository = repository; account = accountKey
            val protectedStore = OidcSessionStore(context.applicationContext)
            current = AiWorkspace(repository, scope, context.getSharedPreferences("ai-views-$accountKey", Context.MODE_PRIVATE),
                seal = { protectedStore.sealAiOperation(accountKey, it) },
                unseal = { protectedStore.openAiOperation(accountKey, it) },
                uploadDirectory = java.io.File(context.cacheDir, "ai/uploads/$accountKey"), context = context.applicationContext)
        }
        return checkNotNull(current)
    }
    override fun onCleared() { current?.retire(); scope.cancel() }
}

/** Host transcripts stay on the PC. Phone view references and encrypted unsent drafts are local. */
internal class AiWorkspace(
    private val repository: ProductRepository,
    private val scope: CoroutineScope,
    private val preferences: SharedPreferences,
    private val seal: (String) -> String = { it },
    private val unseal: (String) -> String = { it },
    private val uploadDirectory: java.io.File? = null,
    context: Context? = null,
) {
    val downloads = context?.let { AiDownloads(it, repository, scope, preferences, seal, unseal) }
    val catalogs = mutableStateMapOf<UUID, AiCatalog>()
    val conversations = mutableStateMapOf<String, AiConversation>()
    val drafts = mutableStateMapOf<String, String>()
    val questionDrafts = mutableMapOf<Pair<String, String>, AiQuestionDraft>()
    val reading = mutableMapOf<String, AiReadingState>()
    var fileLocation by mutableStateOf<AiFileLocation?>(null)

    // Account-scoped preferences and the existing Keystore envelope also protect question drafts.
    fun webUiState(session: AiConversation): JSONObject = runCatching {
        val host = catalogs[session.pcId]?.hostId.orEmpty()
        val stored = JSONObject(unseal(preferences.getString("web:${session.id}", null) ?: return JSONObject()))
        if (stored.optString("host") != host || stored.optString("pc") != session.pcId.toString()) return JSONObject()
        sanitizeAiWebState(stored.getJSONObject("state"), session)
    }.getOrElse { JSONObject() }

    fun saveWebUiState(session: AiConversation, value: JSONObject) {
        check(!retired && selected == session.id)
        val safe = sanitizeAiWebState(value, session)
        val stored = JSONObject().put("host", catalogs[session.pcId]?.hostId.orEmpty()).put("pc", session.pcId.toString()).put("state", safe)
        preferences.edit().putString("web:${session.id}", seal(stored.toString())).apply()
    }
    val attachments = mutableStateMapOf<String, List<AiUpload>>()
    val fileReferences = mutableStateMapOf<String, List<String>>()
    val queued = mutableStateMapOf<String, List<AiQueued>>()
    val pinned = mutableStateMapOf<String, Boolean>()
    var search by mutableStateOf("")
    var notificationsEnabled by mutableStateOf(preferences.getBoolean("notifications", false))
    var foreground = true
    var notifyAttention: ((AiConversation, String) -> Unit)? = null
    var clearAttention: ((String) -> Unit)? = null
    private var attentionJob: kotlinx.coroutines.Job? = null
    fun watchInBackground(online: Set<UUID>) {
        saveDrafts()
        foreground = false; attentionJob?.cancel()
        aiReminderTrace("background enabled=$notificationsEnabled retired=$retired online=${online.size}")
        if (!notificationsEnabled || retired) return
        attentionJob = scope.launch {
            while (!retired && notificationsEnabled && !foreground) {
                opened.filter { it.pcId in online && it.strategy == "native" && conversations.containsKey(it.sessionId) }.forEach { refresh(it.pcId, it.sessionId) }
                kotlinx.coroutines.delay(6000)
            }
        }
    }
    fun stopBackgroundWatch() { aiReminderTrace("foreground"); foreground = true; attentionJob?.cancel(); attentionJob = null }
    val unavailable = mutableStateMapOf<UUID, Boolean>()
    var opened by mutableStateOf(readOpened())
        private set
    var closedSessionIds by mutableStateOf(preferences.getStringSet("closed_sessions", emptySet()).orEmpty().toSet())
        private set
    var selected by mutableStateOf<String?>(null)
    var listPc by mutableStateOf<UUID?>(null)
    var listTarget by mutableStateOf<AiGroupTarget?>(null)
    fun browseAgentGroup(pcId: UUID, agentId: String) {
        selected = null
        createPc = null
        search = ""
        attentionOnly = false
        listPc = pcId
        listTarget = AiGroupTarget(pcId, aiAgentId(agentId))
    }
    var createPc by mutableStateOf<UUID?>(null)
    var createPcLocked by mutableStateOf(false)
        private set
    var createDirectory by mutableStateOf<String?>(null)
        private set
    fun beginCreate(pcId: UUID, directory: String? = null, lockPc: Boolean = true) {
        createDirectory = directory?.takeIf(String::isNotBlank)
        createPcLocked = lockPc
        createPc = pcId
    }
    var attentionOnly by mutableStateOf(false)
    private val busyPcs = mutableStateMapOf<UUID, Boolean>()
    private val pending = mutableStateMapOf<UUID, String>().apply {
        runCatching { preferences.getString("pending", null)?.let { encoded ->
            val stored = JSONObject(unseal(encoded))
            val requests = stored.optJSONArray("requests")?.objects() ?: listOf(stored)
            requests.forEach { put(UUID.fromString(it.getString("pc")), it.getString("body")) }
        } }
    }
    private val displayedPc: UUID? get() = createPc ?: conversations[selected]?.pcId ?: opened.firstOrNull { it.sessionId == selected }?.pcId ?: listPc
    fun isBusy(pc: UUID) = busyPcs[pc] == true
    fun isUncertain(pc: UUID) = pc in pending && !isBusy(pc)
    fun isBlocked(pc: UUID) = isBusy(pc) || pc in pending
    val busy: Boolean get() = displayedPc?.let(::isBusy) ?: busyPcs.values.any { it }
    val uncertain: Boolean get() = displayedPc?.let(::isUncertain) ?: pending.keys.any(::isUncertain)
    val pendingPcs: List<UUID> get() = pending.keys.toList()
    fun pendingDescription(pc: UUID): String = runCatching {
        val request = JSONObject(pending.getValue(pc))
        val session = conversations[request.optString("sessionId")]
        val action = when (request.optString("action")) {
            "create" -> "创建对话"; "send", "steer" -> "发送消息"; "answer" -> "审批/回答"
            "configure" -> "更改设置"; "interrupt" -> "停止生成"; else -> "会话操作"
        }
        listOfNotNull(session?.let(::aiConversationTitle), action).joinToString(" · ")
    }.getOrDefault("会话操作")
    var error by mutableStateOf("")
        private set
    private val loading = mutableSetOf<UUID>()
    private val mutationVersions = mutableMapOf<UUID, Long>()
    private fun version(pc: UUID) = mutationVersions[pc] ?: 0L
    private fun advance(pc: UUID): Long = (version(pc) + 1).also { mutationVersions[pc] = it }
    private fun savePending(values: Map<UUID, String>) {
        val editor = preferences.edit()
        if (values.isEmpty()) editor.remove("pending") else {
            val requests = values.map { (pc, body) -> JSONObject().put("pc", pc).put("body", body) }
            // Keep the legacy first request fields readable during upgrade; never mint replacement operation IDs.
            editor.putString("pending", seal(JSONObject(requests.first().toString()).put("requests", JSONArray(requests)).toString()))
        }
        check(editor.commit())
    }
    private var retired = false
    private var draftJob: kotlinx.coroutines.Job? = null
    init {
        if (uncertain) error = "发现尚未确认的请求，请重试查询电脑回执。"
        runCatching { preferences.getString("drafts", null)?.let { encoded ->
            val value = JSONObject(unseal(encoded))
            val stored = value.optJSONObject("drafts") ?: value
            stored.keys().asSequence().take(64).forEach { id -> if (opened.any { it.sessionId == id }) drafts[id] = stored.getString(id).take(16000) }
            value.optJSONObject("queues")?.let { queues -> queues.keys().asSequence().take(64).forEach { id ->
                if (opened.any { it.sessionId == id }) queued[id] = queues.getJSONArray(id).objects().take(8).map { q ->
                    AiQueued(UUID.fromString(q.getString("id")).toString(), q.getString("text").take(16000), q.getString("host"), q.getLong("controlVersion"), restored = true)
                }
            } }
            value.optJSONObject("references")?.let { refs -> refs.keys().asSequence().take(64).forEach { id ->
                if (opened.any { it.sessionId == id }) fileReferences[id] = refs.getJSONArray(id).let { a -> (0 until minOf(20, a.length())).map { a.getString(it).take(4096) } }
            } }
            value.optJSONObject("uploads")?.let { uploads -> uploads.keys().asSequence().take(64).forEach { id ->
                if (uploadDirectory != null && opened.any { it.sessionId == id }) attachments[id] = uploads.getJSONArray(id).objects().take(10).mapNotNull { item ->
                    runCatching {
                        val key = UUID.fromString(item.getString("id")).toString()
                        val file = java.io.File(uploadDirectory, key)
                        require(file.canonicalFile.parentFile == checkNotNull(uploadDirectory).canonicalFile && file.isFile && file.length() == item.getLong("size"))
                        AiUpload(key, android.net.Uri.EMPTY, item.getString("host"), item.getLong("controlVersion")).apply {
                            name = item.getString("name").take(180); mime = item.getString("mime").take(120)
                            size = item.getLong("size"); hash = item.getString("hash"); this.file = file
                            require(size in 1..(50L * 1024 * 1024) && hash.matches(Regex("[0-9a-f]{64}")))
                            status = "failed"; error = "已恢复附件，请重试上传以确认电脑端状态。"
                        }
                    }.getOrNull()
                }
            } }
        } }
        draftJob = scope.launch { snapshotFlow { listOf(drafts.toMap(), queued.toMap(), fileReferences.toMap(), uploadMetadata().toString()) }.collectLatest {
            delay(250)
            saveDrafts()
        } }
    }

    private fun uploadMetadata() = JSONObject().apply {
        attachments.entries.take(64).forEach { (id, entries) -> put(id, JSONArray().apply {
            entries.filter { it.file != null && it.hash.isNotEmpty() }.take(10).forEach { item ->
                put(JSONObject().put("id", item.id).put("host", item.host).put("controlVersion", item.controlVersion)
                    .put("name", item.name).put("mime", item.mime).put("size", item.size).put("hash", item.hash).put("status", item.status))
            }
        }) }
    }

    private fun saveDrafts() {
        if (retired) return
        runCatching {
            val queues = JSONObject().apply { queued.entries.take(64).forEach { (id, items) -> put(id, JSONArray().apply {
                items.take(8).forEach { q -> put(JSONObject().put("id", q.id).put("text", q.text.take(16000)).put("host", q.host).put("controlVersion", q.controlVersion)) }
            }) } }
            val refs = JSONObject().apply { fileReferences.entries.take(64).forEach { (id, values) -> put(id, JSONArray(values.take(20))) } }
            val value = JSONObject().put("drafts", JSONObject(drafts.toMap())).put("queues", queues).put("references", refs).put("uploads", uploadMetadata())
            check(preferences.edit().putString("drafts", seal(value.toString())).commit())
        }.onFailure { error = "草稿尚未安全保存，请保留当前页面。" }
    }

    fun togglePin(id: String) { pinned[id] = !isPinned(id); preferences.edit().putBoolean("pin:$id", pinned[id] == true).apply() }
    fun isPinned(id: String) = pinned[id] ?: preferences.getBoolean("pin:$id", false)
    fun setNotifications(enabled: Boolean) { notificationsEnabled = enabled; preferences.edit().putBoolean("notifications", enabled).apply() }
    /**
     * Configure the host Agent.  A null reasoningEffort is deliberately omitted:
     * model/mode changes must let the host retain the effort only when it is still
     * valid, otherwise the host chooses that model's default.  An empty string is
     * an explicit reset and is therefore encoded.
     */
    fun configure(session: AiConversation, model: String = session.modelId, provider: String = session.providerId,
        mode: String = session.permissionMode, reasoningEffort: String? = null, confirmedMode: String? = null) =
        mutate(session.pcId, "configure", session, JSONObject().put("modelId", model).put("providerId", provider)
            .put("permissionMode", mode).put("expectedSettingsVersion", session.settingsVersion).apply {
                if (reasoningEffort != null) put("reasoningEffort", reasoningEffort)
                if (!confirmedMode.isNullOrBlank()) put("confirmedPermissionMode", confirmedMode)
            })

    fun appendDraft(id: String, text: String): Boolean {
        val next = listOf(drafts[id].orEmpty(), text).filter { it.isNotEmpty() }.joinToString("\n\n")
        if (next.length > 16000) { error = "输入超过 16000 字符，原草稿和待发消息已保留，请先整理草稿。"; return false }
        drafts[id] = next
        return true
    }
    fun quote(id: String, text: String) = appendDraft(id, "> " + text.replace("\n", "\n> ") + "\n")
    fun enqueue(session: AiConversation) {
        val text = drafts[session.id].orEmpty()
        if (text.isBlank() || !session.canControl || attachments[session.id].orEmpty().isNotEmpty() || fileReferences[session.id].orEmpty().isNotEmpty() || queued[session.id].orEmpty().size >= 8) return
        queued[session.id] = queued[session.id].orEmpty() + AiQueued(UUID.randomUUID().toString(), text, catalogs[session.pcId]?.hostId.orEmpty(), session.controlVersion)
        drafts.remove(session.id)
    }
    fun cancelQueued(id: String, item: AiQueued) {
        if (queued[id].orEmpty().none { it.id == item.id }) return
        if (appendDraft(id, item.text)) queued[id] = queued[id].orEmpty().filterNot { it.id == item.id }
    }
    private fun drainQueue(session: AiConversation) {
        val item = queued[session.id]?.firstOrNull() ?: return
        if (item.failed || item.restored) return
        if (isBlocked(session.pcId) || !session.canControl || session.state != "idle" || session.pendingCount > 0 || session.error.isNotEmpty()) return
        if (catalogs[session.pcId]?.hostId != item.host || session.controlVersion != item.controlVersion) return
        mutate(session.pcId, "send", session, JSONObject().put("text", item.text).put("queueId", item.id))
    }
    suspend fun loadHistory(session: AiConversation) {
        if (!session.capabilities.history || session.historyBefore <= 0) return
        try {
            val host = catalogs[session.pcId]?.hostId ?: return
            val result = JSONObject(repository.aiRequest(session.pcId, JSONObject().put("action", "history").put("hostId", host)
                .put("sessionId", session.id).put("streamId", session.streamId).put("offset", session.historyBefore).toString()))
            check(!retired && host == catalogs[session.pcId]?.hostId && result.getString("hostId") == host)
            if (result.optString("error").isNotEmpty()) throw java.io.IOException()
            val page = result.getJSONObject("history")
            val older = page.getJSONArray("messages").objects().map(::parseAiMessage)
            require(older.size <= 64 && page.getLong("before") < session.historyBefore)
            conversations[session.id]?.let { current -> conversations[session.id] = current.copy(messages = (older + current.messages).distinctBy { it.id }, historyBefore = page.getLong("before")) }
        } catch (failure: Exception) { if (failure is CancellationException) throw failure; error = "读取历史失败，请重试。" }
    }
    suspend fun exportMarkdown(session: AiConversation): String {
        val host = catalogs[session.pcId]?.hostId ?: throw java.io.IOException()
        var before = 0L
        val pages = mutableListOf<List<AiMessage>>()
        do {
            val value = JSONObject(repository.aiRequest(session.pcId, JSONObject().put("action", "history").put("hostId", host).put("sessionId", session.id).put("offset", before).toString()))
            check(!retired && host == catalogs[session.pcId]?.hostId && value.getString("hostId") == host)
            val page = value.getJSONObject("history")
            pages += page.getJSONArray("messages").objects().map(::parseAiMessage)
            val next = page.getLong("before"); require(next >= 0 && (before == 0L || next < before)); before = next
            require(pages.size <= 10000)
        } while (before > 0)
        val output = StringBuilder("# ${session.title}\n\n")
        for (message in pages.asReversed().flatten()) {
            output.append("## ${message.role}\n\n").append(if (message.fullLength > message.text.length) fullMessage(session, message) else message.text).append("\n\n")
        }
        return output.toString()
    }
    suspend fun fullMessage(session: AiConversation, message: AiMessage): String {
        val host = catalogs[session.pcId]?.hostId ?: throw java.io.IOException()
        val output = StringBuilder(); var offset = 0L
        do {
            val value = JSONObject(repository.aiRequest(session.pcId, JSONObject().put("action", "message").put("hostId", host)
                .put("sessionId", session.id).put("requestId", message.id).put("offset", offset).toString()))
            check(!retired && catalogs[session.pcId]?.hostId == host && value.getString("hostId") == host)
            val page = value.getJSONObject("message"); require(page.getString("id") == message.id && page.getLong("offset") == offset)
            val next = page.getLong("nextOffset"); val length = page.getLong("length")
            require(length in 0..1048577 && next in offset..length && (next > offset || next == length))
            output.append(page.getString("text")); require(output.length <= 1048577); offset = next
        } while (offset < length)
        return output.toString()
    }

    fun upload(context: Context, uri: android.net.Uri, session: AiConversation) {
        if (retired || !session.canControl || !session.capabilities.attachments || attachments[session.id].orEmpty().size >= session.capabilities.maxFiles) return
        val entry = AiUpload(UUID.randomUUID().toString(), uri, catalogs[session.pcId]?.hostId.orEmpty(), session.controlVersion)
        attachments[session.id] = attachments[session.id].orEmpty() + entry
        retryUpload(context, session, entry)
    }
    fun retryUpload(context: Context, session: AiConversation, entry: AiUpload) {
        if (retired || entry.job?.isActive == true) return
        entry.job = scope.launch {
            try {
                entry.status = "preparing"; entry.error = ""
                check(session.canControl && catalogs[session.pcId]?.hostId == entry.host && session.controlVersion == entry.controlVersion) { "电脑或控制权已变化，请移除附件后重新选择。" }
                if (entry.file == null) entry.prepare(context, session.capabilities, uploadDirectory)
                else entry.validateCache(session.capabilities)
                saveDrafts()
                suspend fun request(action: String, extra: JSONObject = JSONObject()): JSONObject {
                    check(!retired && catalogs[session.pcId]?.hostId == entry.host && conversations[session.id]?.controlVersion == entry.controlVersion)
                    val value = JSONObject(repository.aiRequest(session.pcId, extra.put("action", action).put("hostId", entry.host)
                        .put("sessionId", session.id).put("controlVersion", entry.controlVersion).put("attachmentId", entry.id).toString()))
                    check(!retired && value.getString("hostId") == entry.host && catalogs[session.pcId]?.hostId == entry.host)
                    if (value.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(value.getString("error")))
                    return value
                }
                val begin = request("attachment.begin", JSONObject().put("fileName", entry.name).put("mime", entry.mime).put("size", entry.size).put("sha256", entry.hash)).getJSONObject("attachment")
                entry.sent = begin.getLong("offset"); require(entry.sent in 0..entry.size)
                entry.status = "uploading"
                if (!begin.getBoolean("ready")) {
                    kotlinx.coroutines.withContext(Dispatchers.IO) {
                        java.io.RandomAccessFile(checkNotNull(entry.file), "r").use { file ->
                            file.seek(entry.sent)
                            val bytes = ByteArray(session.capabilities.chunkBytes.coerceIn(1, 24576))
                            while (entry.sent < entry.size) {
                                val count = file.read(bytes); check(count > 0)
                                val expected = entry.sent + count
                                val reply = request("attachment.chunk", JSONObject().put("offset", entry.sent).put("data", android.util.Base64.encodeToString(bytes, 0, count, android.util.Base64.NO_WRAP)))
                                check(reply.getJSONObject("attachment").getLong("offset") == expected); entry.sent = expected
                            }
                        }
                    }
                    request("attachment.commit")
                }
                entry.status = "ready"
            } catch (failure: Exception) {
                if (failure is CancellationException && failure !is TimeoutCancellationException) throw failure
                entry.status = "failed"; entry.error = failure.message?.take(160) ?: "上传失败，可重试"
            } finally { saveDrafts() }
        }
    }
    fun removeUpload(session: AiConversation, entry: AiUpload) {
        entry.job?.cancel(); entry.file?.delete()
        attachments[session.id] = attachments[session.id].orEmpty().filterNot { it.id == entry.id }
        scope.launch { runCatching { repository.aiRequest(session.pcId, JSONObject().put("action", "attachment.remove").put("hostId", entry.host)
            .put("sessionId", session.id).put("controlVersion", entry.controlVersion).put("attachmentId", entry.id).toString()) } }
    }
    fun send(session: AiConversation) = mutate(session.pcId, "send", session, JSONObject().put("text", drafts[session.id].orEmpty())
        .put("attachmentIds", JSONArray(attachments[session.id].orEmpty().filter { it.status == "ready" }.map { it.id }))
        .put("fileReferences", JSONArray(fileReferences[session.id].orEmpty())))

    private fun readOpened(): List<AiOpened> = runCatching {
        JSONArray(preferences.getString("opened", "[]")).objects().take(64).map {
            val pc = UUID.fromString(it.getString("pc"))
            if (it.optString("strategy") == "desktop_remote" && it.optString("agentId") == "zcode")
                AiOpened(pc, "remote:$pc:zcode", "desktop_remote", "zcode")
            else AiOpened(pc, UUID.fromString(it.getString("session")).toString(), agentId = it.optString("agentId"))
        }
    }.getOrDefault(emptyList())

    private fun saveOpened() {
        preferences.edit().putStringSet("closed_sessions", closedSessionIds).putString("opened", JSONArray().apply { opened.forEach {
            put(JSONObject().put("pc", it.pcId).put("session", it.sessionId).put("strategy", it.strategy).put("agentId", it.agentId))
        } }.toString()).apply()
    }

    fun open(session: AiConversation) {
        conversations.putIfAbsent(session.id, session)
        if (aiIsHistorical(session)) {
            opened = opened.filterNot { it.sessionId == session.id }
            closedSessionIds = closedSessionIds + session.id
        } else {
            closedSessionIds = closedSessionIds - session.id
            if (opened.none { it.sessionId == session.id }) opened = (opened + AiOpened(session.pcId, session.id, agentId = session.agentId)).takeLast(64)
        }
        saveOpened()
        selected = session.id
    }

    fun preferredDirectory(pcId: UUID): String = preferences.getString("directory:$pcId", null)
        ?: catalogs[pcId]?.defaultDirectory.orEmpty()

    suspend fun directories(pcId: UUID, path: String, offset: Long = 0, includeFiles: Boolean = false): RemoteDirectoryPage {
        val catalog = checkNotNull(catalogs[pcId])
        check(!retired && catalog.directoryBrowsing)
        require(path.length <= 4096 && '\u0000' !in path && offset >= 0)
        val result = JSONObject(repository.aiRequest(pcId, JSONObject().put("action", if (includeFiles) "files" else "directories").put("hostId", catalog.hostId)
            .put("directory", path).put("offset", offset).toString()))
        check(!retired && catalogs[pcId]?.hostId == catalog.hostId)
        if (result.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(result.getString("error")))
        require(result.getString("hostId") == catalog.hostId)
        val page = result.getJSONObject("directories")
        val entries = page.getJSONArray("entries").objects()
        require(entries.size <= 500)
        return RemoteDirectoryPage(page.getString("currentPath"), page.optString("parentPath").takeIf { !page.isNull("parentPath") },
            entries.map {
                require(it.getString("kind") == "directory" || includeFiles && it.getString("kind") == "file")
                RemoteFileEntry(it.getString("name"), it.getString("path"), if (it.getString("kind") == "directory") RemoteFileKind.DIRECTORY else RemoteFileKind.FILE)
            }, if (page.isNull("nextOffset")) null else page.getLong("nextOffset").also { require(it > offset) })
    }

    suspend fun models(pcId: UUID, agentId: String): AiModelList {
        val catalog = checkNotNull(catalogs[pcId])
        val agent = catalog.agents.single { it.id == agentId }
        check(!retired && agent.configuration.modelListing && agent.supported && agent.strategy == "native")
        val result = JSONObject(repository.aiRequest(pcId, JSONObject().put("action", "models")
            .put("hostId", catalog.hostId).put("agentId", agentId).toString()))
        check(!retired && catalogs[pcId]?.hostId == catalog.hostId &&
            catalogs[pcId]?.agents?.any { it.id == agentId && it.supported && it.strategy == "native" } == true)
        if (result.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(result.getString("error")))
        check(result.getString("hostId") == catalog.hostId && result.getString("agentId") == agentId)
        val values = result.getJSONArray("models")
        require(values.length() <= 200)
        val models = values.objects().map {
            val id = it.getString("modelId")
            val provider = it.optString("providerId")
            require(id.isNotBlank() && id.length <= 200 && provider.length <= 200 &&
                (id + provider).none(Char::isISOControl) && (agentId != "zcode" || provider.isNotBlank()))
            val name = it.optString("name").takeIf { name -> name.isNotBlank() && name.length <= 200 && name.none(Char::isISOControl) } ?: id
            AiModelOption(id, name, provider, it.optBoolean("isDefault"), parseReasoning(it.optJSONObject("reasoning")))
        }.distinctBy { it.providerId to it.modelId }
        return AiModelList(models, result.optBoolean("truncated"))
    }

    suspend fun nativeHistory(pcId: UUID, agentId: String, cursor: String = ""): AiNativePage {
        val host = checkNotNull(catalogs[pcId]).hostId
        check(!retired && catalogs[pcId]?.agents?.any { it.id == agentId && it.supported && it.nativeHistory } == true)
        val result = JSONObject(repository.aiRequest(pcId, JSONObject().put("action", "native-history").put("hostId", host)
            .put("agentId", agentId).put("text", cursor).toString()))
        check(!retired && catalogs[pcId]?.hostId == host)
        if (result.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(result.getString("error")))
        check(result.getString("hostId") == host && result.getString("agentId") == agentId)
        val page = result.getJSONObject("nativeHistory")
        val entries = page.getJSONArray("sessions").objects()
        require(entries.size <= 30)
        return AiNativePage(entries.map { AiNativeEntry(aiNativeHistoryId(agentId, it.getString("nativeId")),
            it.optString("title").take(160), it.getString("directory").take(4096), it.optLong("updatedAtMs")) },
            page.optString("nextCursor").also { require(it.length <= 4096) }, page.optBoolean("truncated"))
    }

    fun openRemote(pcId: UUID, agentId: String) {
        if (retired || isBlocked(pcId) || catalogs[pcId]?.agents?.none { it.id == agentId && it.strategy == "desktop_remote" && it.supported } != false) return
        val entry = AiOpened(pcId, "remote:$pcId:$agentId", "desktop_remote", agentId)
        closedSessionIds = closedSessionIds - entry.sessionId
        if (entry !in opened) opened = (opened + entry).takeLast(64)
        saveOpened()
        selected = entry.sessionId; createPc = null
    }

    suspend fun remoteAddress(entry: AiOpened): String {
        val host = catalogs[entry.pcId]?.hostId ?: throw java.io.IOException("电脑能力尚未就绪")
        check(!retired && entry.strategy == "desktop_remote" && entry.agentId == "zcode")
        val result = JSONObject(repository.aiRequest(entry.pcId, JSONObject().put("action", "remote")
            .put("hostId", host).put("agentId", entry.agentId).toString()))
        if (result.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(result.getString("error")))
        check(!retired && catalogs[entry.pcId]?.hostId == host && result.getString("hostId") == host &&
            result.getString("strategy") == "desktop_remote" && result.getString("agentId") == entry.agentId)
        return result.getString("remoteUrl").also { require(isAllowedZcodeRemoteUrl(it)) }
    }

    fun closeView(id: String) {
        // Catalog-only rows can also be dismissed. Persist the choice so a
        // catalog refresh or process restart cannot reopen the phone view.
        closedSessionIds = closedSessionIds + id
        opened = opened.filterNot { it.sessionId == id }; saveOpened()
        conversations.remove(id); reading.remove(id); drafts.remove(id)
        clearAttention?.invoke(id)
        preferences.edit().remove("web:$id").apply()
        questionDrafts.keys.removeAll { it.first == id }
        queued.remove(id); fileReferences.remove(id); attachments.remove(id)?.forEach { it.job?.cancel(); it.file?.delete() }
        if (selected == id) selected = null
        saveDrafts()
    }

    fun endSession(session: AiConversation): Deferred<Boolean> = scope.async {
        var current = conversations[session.id]?.takeIf { it.pcId == session.pcId }
            ?: catalogs[session.pcId]?.sessions?.firstOrNull { it.id == session.id } ?: return@async false
        if (aiIsHistorical(current)) return@async false
        if (!current.canControl) {
            if (mutate(current.pcId, "control", current)?.await() != true) return@async false
            current = conversations[session.id]?.takeIf { it.pcId == session.pcId && it.canControl } ?: return@async false
        }
        mutate(current.pcId, "end", current)?.await() == true
    }

    fun retire(clearPending: Boolean = false) {
        fileLocation = null
        downloads?.retire(clearPending)
        if (clearPending) opened.forEach { clearAttention?.invoke(it.sessionId) }
        if (!clearPending) saveDrafts()
        draftJob?.cancel(); draftJob = null
        stopBackgroundWatch()
        retired = true; catalogs.clear(); conversations.clear(); drafts.clear(); unavailable.clear(); pending.clear(); busyPcs.clear()
        questionDrafts.clear()
        queued.clear(); fileReferences.clear(); attachments.values.flatten().forEach { it.job?.cancel(); if (clearPending) it.file?.delete() }; attachments.clear()
        if (clearPending) preferences.edit().apply { remove("pending"); remove("drafts"); preferences.all.keys.filter { it.startsWith("web:") }.forEach(::remove) }.commit()
    }

    suspend fun renewControl(session: AiConversation) {
        val host = catalogs[session.pcId]?.hostId ?: return
        if (retired || !session.canControl || !session.capabilities.watch || selected != session.id || !foreground) return
        val result = JSONObject(repository.aiRequest(session.pcId, JSONObject().put("action", "renew").put("hostId", host)
            .put("sessionId", session.id).put("controlVersion", session.controlVersion).toString()))
        if (!retired && catalogs[session.pcId]?.hostId == host && result.optString("error").isNotEmpty()) error = aiErrorLabel(result.getString("error"))
    }

    /** Read-only Web operations: host and session are chosen by native code, never by JavaScript. */
    suspend fun readFeature(session: AiConversation, action: String, extra: JSONObject = JSONObject()): JSONObject {
        require(action in setOf("search", "artifacts", "artifact.read", "diff.read"))
        val host = checkNotNull(catalogs[session.pcId]).hostId
        check(!retired && conversations[session.id]?.pcId == session.pcId)
        val result = JSONObject(repository.aiRequest(session.pcId, extra.put("action", action).put("hostId", host)
            .put("sessionId", session.id).toString()))
        check(!retired && catalogs[session.pcId]?.hostId == host && conversations[session.id]?.pcId == session.pcId)
        if (result.optString("error").isNotEmpty()) throw java.io.IOException(aiErrorLabel(result.getString("error")))
        check(result.getString("hostId") == host)
        return result.getJSONObject(if (action == "search") "search" else if (action == "diff.read") "diff" else "artifacts")
    }

    suspend fun refresh(pcId: UUID, sessionId: String? = null) {
        if (retired || isBlocked(pcId) || !loading.add(pcId)) return
        val version = version(pcId)
        try {
            val host = catalogs[pcId]?.hostId
            val current = sessionId?.let(conversations::get)
            val body = if (sessionId != null && host != null)
                JSONObject().put("action", if (current?.capabilities?.watch == true && foreground && selected == sessionId) "watch" else if (current?.capabilities?.changes == true) "changes" else "poll").put("hostId", host).put("sessionId", sessionId)
                    .put("streamId", current?.streamId.orEmpty()).put("cursor", current?.cursor ?: "0")
                else JSONObject().put("action", "catalog")
            val result = JSONObject(repository.aiRequest(pcId, body.toString()))
            if (retired || isBlocked(pcId) || version != version(pcId)) return
            if (result.optString("error") == "SESSION_NOT_FOUND" && sessionId != null) conversations.remove(sessionId)
            accept(pcId, result)
            unavailable.remove(pcId)
            conversations.values.filter { it.pcId == pcId }.forEach(::drainQueue)
        } catch (failure: Exception) {
            if (failure is CancellationException && failure !is TimeoutCancellationException) throw failure
            if (!retired && !isBlocked(pcId) && version == version(pcId)) {
                unavailable[pcId] = true
                error = "暂时无法连接电脑，正在重新确认状态。"
            }
        }
        finally { loading.remove(pcId) }
    }

    /** A notification must not use a cached snapshot or wait for the active long poll. */
    suspend fun readNotificationSession(pcId: UUID, id: String): AiConversation {
        check(!retired && !isBlocked(pcId)) { "请先确认这台电脑尚未完成的操作。" }
        val version = advance(pcId)
        val catalog = JSONObject(repository.aiRequest(pcId, JSONObject().put("action", "catalog").toString()))
        check(!retired && !isBlocked(pcId) && version == version(pcId))
        check(catalog.optString("error").isEmpty()) { aiErrorLabel(catalog.optString("error")) }
        accept(pcId, catalog)
        val host = catalog.getString("hostId")
        val result = JSONObject(repository.aiRequest(pcId, JSONObject().put("action", "poll").put("hostId", host).put("sessionId", id).toString()))
        check(!retired && !isBlocked(pcId) && version == version(pcId) && result.getString("hostId") == host)
        check(result.optString("error").isEmpty()) { aiErrorLabel(result.optString("error")) }
        check(result.getJSONObject("session").getString("id") == id)
        accept(pcId, result); unavailable.remove(pcId)
        return checkNotNull(conversations[id])
    }

    fun mutate(pcId: UUID, action: String, session: AiConversation? = null, extra: JSONObject = JSONObject()): Deferred<Boolean>? {
        if (isBlocked(pcId) || retired) return null
        val host = catalogs[pcId]?.hostId ?: return null
        val body = extra.put("action", action).put("hostId", host).put("operationId", UUID.randomUUID().toString())
        if (session != null) body.put("sessionId", session.id).put("controlVersion", session.controlVersion)
        val encoded = body.toString()
        try {
            savePending(pending.toMap() + (pcId to encoded))
        } catch (_: Exception) { error = "无法安全保存发送状态，未发送。"; return null }
        pending[pcId] = encoded
        return submit(pcId)
    }

    fun retryUncertain() { (displayedPc ?: pending.keys.firstOrNull(::isUncertain))?.let(::retryUncertain) }
    fun retryUncertain(pcId: UUID) { if (isUncertain(pcId) && !retired) submit(pcId) }

    private fun submit(pcId: UUID): Deferred<Boolean>? {
        val body = pending[pcId] ?: return null
        advance(pcId)
        busyPcs[pcId] = true; error = ""
        return scope.async {
            try {
                val result = JSONObject(repository.aiRequest(pcId, body))
                if (retired) return@async false
                val request = JSONObject(body)
                if (result.optString("error").isEmpty() && request.optString("action") in setOf("end", "forget")) {
                    check(result.getString("hostId") == request.getString("hostId"))
                    if (request.getString("action") == "forget") check(result.getString("deletedSessionId") == request.getString("sessionId"))
                    else {
                        val ended = parseConversation(pcId, result.getJSONObject("session"))
                        check(ended.id == request.getString("sessionId") && aiIsHistorical(ended))
                    }
                }
                accept(pcId, result)
                unavailable.remove(pcId)
                savePending(pending.toMap() - pcId)
                pending.remove(pcId)
                if (result.optString("error").isNotEmpty() && request.has("queueId")) {
                    val id = request.optString("sessionId")
                    queued[id] = queued[id].orEmpty().map { if (it.id == request.optString("queueId")) it.copy(failed = true) else it }
                }
                if (result.optString("error").isEmpty() && request.optString("action") == "steer" && drafts[request.optString("sessionId")] == request.optString("text")) drafts.remove(request.optString("sessionId"))
                if (result.optString("error").isEmpty()) {
                    if (request.optString("action") == "send") {
                        val id = request.optString("sessionId")
                        if (request.has("queueId")) queued[id] = queued[id].orEmpty().filterNot { it.id == request.optString("queueId") }
                        else { if (drafts[id] == request.optString("text")) drafts.remove(id); fileReferences.remove(id); attachments.remove(id)?.forEach { it.file?.delete() } }
                    }
                    if (request.optString("action") in setOf("create", "fork", "import-native")) {
                        result.optJSONObject("session")?.let {
                            val created = parseConversation(pcId, it)
                            if (displayedPc == null || displayedPc == pcId) open(created)
                            if (request.optString("directoryMode") != "managed") preferences.edit().putString("directory:$pcId", created.directory).apply()
                        }
                        if (createPc == pcId) createPc = null
                    }
                    // Only a confirmed end removes the phone view. Lost replies
                    // retain the exact operation ID for receipt recovery.
                    if (request.optString("action") == "end") {
                        request.optString("sessionId").takeIf { it.isNotBlank() }?.let(::closeView)
                    }
                    if (request.optString("action") == "resume") {
                        result.optJSONObject("session")?.let { open(parseConversation(pcId, it)) }
                    }
                }
                result.optString("error").isEmpty()
            } catch (failure: Exception) {
                if (failure is CancellationException && failure !is TimeoutCancellationException) throw failure
                if (!retired) { unavailable[pcId] = true; error = "尚未收到电脑回执。请查询原请求结果，不会重复执行。" }
                false
            } finally { busyPcs.remove(pcId) }
        }
    }

    private fun accept(pcId: UUID, result: JSONObject) {
        val code = result.optString("error")
        if (code.isNotEmpty()) {
            error = aiErrorLabel(code)
            if (code == "HOST_CHANGED") { catalogs.remove(pcId); conversations.entries.removeAll { it.value.pcId == pcId } }
        } else error = ""
        result.optJSONArray("agents")?.let { agents ->
            val sessions = result.getJSONArray("sessions").objects().map { parseConversation(pcId, it) }
            val legacyModes = result.optJSONObject("configurationModes")?.let { obj ->
                obj.keys().asSequence().take(16).associateWith { name ->
                    obj.optJSONArray(name)?.let { a -> (0 until minOf(a.length(), 8)).map(a::getString) }.orEmpty()
                }
            }.orEmpty()
            val globalModelListing = result.optBoolean("modelListing")
            val next = AiCatalog(result.getString("hostId"), agents.objects().map {
                val id = it.getString("id")
                AiAgentOption(id, it.getString("name"), it.optString("version"), it.getString("status"), it.optString("strategy", "native"),
                    parseAgentConfiguration(it.optJSONObject("configuration"), id, globalModelListing, legacyModes[id].orEmpty()), it.optBoolean("nativeHistory"))
            }, sessions, result.optString("defaultDirectory"), result.optString("directoryMode", "custom"), result.optBoolean("directoryBrowsing"), globalModelListing,
                legacyModes, result.optString("cliWorkspaceRoot"))
            require(next.agents.size <= 16 && sessions.size <= 64)
            if (catalogs[pcId]?.hostId != next.hostId) {
                conversations.entries.removeAll { it.value.pcId == pcId }
                opened.filter { it.pcId == pcId }.forEach { entry -> attachments[entry.sessionId]?.forEach { upload ->
                    if (upload.host != next.hostId) { upload.job?.cancel(); upload.status = "failed"; upload.error = "电脑已重启，请移除附件后重新选择。" }
                } }
            }
            catalogs[pcId] = next
            val historical = sessions.filter(::aiIsHistorical).map { it.id }
            if (historical.any { it !in closedSessionIds }) { closedSessionIds = closedSessionIds + historical; saveOpened() }
            val updatedOpened = opened.map { entry ->
                if (entry.pcId == pcId && entry.agentId.isEmpty()) sessions.firstOrNull { it.id == entry.sessionId }?.let { entry.copy(agentId = it.agentId) } ?: entry else entry
            }
            if (updatedOpened != opened) { opened = updatedOpened; saveOpened() }
            val listed = sessions.mapTo(hashSetOf()) { it.id }
            conversations.entries.removeAll { it.value.pcId == pcId && it.key !in listed }
            sessions.forEach { metadata ->
                val old = conversations[metadata.id]
                conversations[metadata.id] = metadata.copy(messages = old?.messages.orEmpty(), historyBefore = if (old?.messages?.isNotEmpty() == true) old.historyBefore else metadata.historyBefore,
                    pending = if (metadata.pendingCount == 0) emptyList() else old?.pending.orEmpty(), streamId = old?.streamId.orEmpty(), cursor = old?.cursor ?: "0")
            }
        }
        result.optJSONObject("session")?.let {
            val next = parseConversation(pcId, it)
            if (aiIsHistorical(next) && next.id !in closedSessionIds) { closedSessionIds = closedSessionIds + next.id; saveOpened() }
            val old = conversations[next.id]
            if (old?.state != next.state) aiReminderTrace("transition ${old?.state} -> ${next.state}; foreground=$foreground enabled=$notificationsEnabled callback=${notifyAttention != null}")
            if (notificationsEnabled && !foreground && old != null && (old.pendingCount == 0 && next.pendingCount > 0 || old.state == "running" && next.state != "running" || old.error.isEmpty() && next.error.isNotEmpty()))
                notifyAttention?.invoke(next, if (next.pendingCount > 0) "AI 会话需要审批或回答" else if (next.error.isNotEmpty()) "AI 会话需要关注" else "AI 任务已完成")
            if (old != null && old.pendingCount > 0 && next.pendingCount == 0) clearAttention?.invoke(next.id)
            conversations[next.id] = if (old != null && !it.optBoolean("reset") && (old.streamId.isEmpty() || old.streamId == next.streamId) && next.capabilities.history && old.messages.isNotEmpty()) {
                val latest = next.messages.associateBy { message -> message.id }
                next.copy(messages = (old.messages.map { message -> latest[message.id] ?: message } + next.messages).distinctBy { message -> message.id }, historyBefore = minOf(old.historyBefore, next.historyBefore))
            } else next
            // Keep history metadata current even before the next catalog poll.
            catalogs[pcId]?.let { catalog ->
                val metadata = next.copy(messages = emptyList(), pending = emptyList())
                catalogs[pcId] = catalog.copy(sessions = catalog.sessions.filterNot { s -> s.id == next.id } + metadata)
            }
        }
        result.optString("deletedSessionId").takeIf { it.isNotEmpty() }?.let { id ->
            if (conversations[id]?.pcId == pcId || catalogs[pcId]?.sessions?.any { it.id == id } == true || opened.any { it.pcId == pcId && it.sessionId == id }) {
                closeView(id)
                catalogs[pcId]?.let { catalogs[pcId] = it.copy(sessions = it.sessions.filterNot { s -> s.id == id }) }
                pinned.remove(id); preferences.edit().remove("pin:$id").apply()
            }
        }
        questionDrafts.keys.removeAll { (id, request) -> conversations[id]?.pending?.none { it.id == request } != false }
    }
}

internal fun JSONArray.objects(): List<JSONObject> = (0 until length()).map(::getJSONObject)
internal fun aiReminderTrace(message: String) { if (dev.termlink.harness.BuildConfig.DEBUG) android.util.Log.d("TermLinkAiAttention", message) }
internal fun parseAiMessage(it: JSONObject) = AiMessage(it.getString("id"), it.getString("role"), it.getString("text"),
    it.optString("kind", "text"), it.optString("title").take(80), it.optString("summary").take(240), it.optString("status", "unknown"), it.optString("model"), it.optString("mode"), it.optInt("fullLength"))

internal fun parseReasoning(value: JSONObject?): AiReasoning {
    if (value == null) return AiReasoning()
    val status = when (value.optString("status", "unknown")) {
        "available", "unsupported", "unknown" -> value.optString("status", "unknown")
        else -> "unknown"
    }
    val options = value.optJSONArray("options")?.objects()?.take(32)?.mapNotNull { option ->
        val id = option.optString("value").trim()
        if (id.isBlank() || id.length > 80 || id.any(Char::isISOControl)) null
        else AiReasoningOption(id, option.optString("description").filterNot(Char::isISOControl).take(240))
    }?.distinctBy { it.value }.orEmpty()
    return AiReasoning(status, options, value.optString("defaultValue").takeIf { it.length <= 80 && it.none(Char::isISOControl) }.orEmpty())
}

private fun parsePermissionMode(value: Any): AiPermissionMode? = when (value) {
    is String -> value.trim().takeIf { it.isNotBlank() && it.length <= 80 && it.none(Char::isISOControl) }?.let { AiPermissionMode(it) }
    is JSONObject -> {
        val id = value.optString("id").trim()
        if (id.isBlank() || id.length > 80 || id.any(Char::isISOControl)) null
        else AiPermissionMode(id, value.optString("label").filterNot(Char::isISOControl).take(120),
            value.optString("description").filterNot(Char::isISOControl).take(320), value.optBoolean("requiresConfirmation", true))
    }
    else -> null
}

internal fun parsePermissionModes(value: JSONObject?): List<AiPermissionMode> =
    value?.optJSONArray("permissionModes")?.let { array ->
        (0 until minOf(array.length(), 32)).mapNotNull { index ->
            runCatching {
                val item = array.get(index)
                parsePermissionMode(item)
            }.getOrNull()
        }.distinctBy { it.id }
    }.orEmpty()

internal fun parseAgentConfiguration(value: JSONObject?, agentId: String = "", legacyModelListing: Boolean = false,
    legacyModes: List<String> = emptyList()): AiAgentConfiguration {
    val legacy = value == null && agentId in setOf("codex", "claude")
    val modes = parsePermissionModes(value).ifEmpty {
        if (legacy) legacyModes.mapNotNull(::parsePermissionMode) else emptyList()
    }
    return AiAgentConfiguration(
        modelListing = value?.optBoolean("modelListing") ?: (legacy && legacyModelListing),
        modelSwitch = value?.optBoolean("modelSwitch") ?: (legacy && legacyModelListing),
        reasoningSwitch = value?.optBoolean("reasoningSwitch") ?: false,
        reasoningScope = value?.optString("reasoningScope", "session")?.takeIf { it in setOf("next_turn", "new_session", "session") } ?: "session",
        permissionSwitch = value?.optBoolean("permissionSwitch") ?: (legacy && modes.isNotEmpty()),
        permissionModes = modes,
    )
}

internal fun parseConversation(pcId: UUID, value: JSONObject): AiConversation {
    val id = UUID.fromString(value.getString("id")).toString()
    require(value.getString("kind") == "ai")
    return AiConversation(id, pcId, value.getString("agentName"), value.getString("directory"),
        value.getString("state"), value.optString("error"), value.getLong("controlVersion"), value.getBoolean("canControl"),
        value.optJSONArray("messages")?.objects().orEmpty().map(::parseAiMessage),
        value.optJSONArray("pending")?.objects().orEmpty().map { pending ->
            AiPending(pending.getString("id"), pending.getString("kind"), pending.getString("detail"),
                pending.getJSONArray("questions").objects().map { q ->
                    val options = q.getJSONArray("options")
                    AiQuestion(q.getString("id"), q.getString("text"), (0 until options.length()).map(options::getString), q.getBoolean("multiSelect"))
                }, pending.optBoolean("canApprove", false), pending.optString("title").take(80), pending.optString("summary").take(240))
        }, value.optBoolean("truncated"), value.optString("title"), value.optString("preview"),
        value.optInt("pendingCount", value.optJSONArray("pending")?.length() ?: 0).coerceIn(0, 64),
         value.optString("agentId"), value.optString("modelId"), value.optString("providerId"), value.optString("permissionMode", "ask"),
         value.optLong("settingsVersion"), value.optString("configurationScope"), value.optJSONObject("capabilities")?.let { c ->
             val agentId = value.optString("agentId")
             val legacyModes = c.optJSONArray("modes")?.let { a -> (0 until minOf(a.length(), 32)).map(a::getString) }.orEmpty()
             val configuration = parseAgentConfiguration(c.optJSONObject("configuration"), agentId, c.optBoolean("configure"), legacyModes)
            AiCapabilities(c.optBoolean("configure"), c.optBoolean("steer"), c.optBoolean("resume"), c.optBoolean("fork"), c.optBoolean("history"),
                 c.optBoolean("attachments"), c.optBoolean("nativeImages"), legacyModes,
                 c.optLong("maxFileBytes").coerceIn(0, 20L * 1024 * 1024), c.optLong("maxTotalBytes").coerceIn(0, 50L * 1024 * 1024), c.optInt("maxFiles").coerceIn(0, 5), c.optInt("chunkBytes").coerceIn(0, 24576), configuration,
                 c.optBoolean("changes"), c.optBoolean("search"), c.optBoolean("artifacts"), c.optBoolean("watch"), c.optBoolean("diffRead"))
         } ?: AiCapabilities(), value.optLong("historyBefore"), value.optInt("historyCount"), value.optString("diff"), value.optJSONObject("usage")?.toString().orEmpty(), value.optBoolean("compacting"),
         value.optJSONArray("slashCommands")?.let { a -> (0 until minOf(a.length(), 100)).map(a::getString) }.orEmpty(), value.optString("archiveError"),
         value.optString("reasoningEffort"), parseReasoning(value.optJSONObject("reasoning")), value.optString("settingsNotice"),
         value.optString("streamId"), value.optString("cursor", "0"), value.optString("phase"), value.optLong("turnStartedAtMs"), value.optLong("lastActivityAtMs"), value.optJSONArray("plan")?.toString().orEmpty(),
         value.optInt("diffLength", value.optString("diff").length), value.optBoolean("diffTruncated"))
}

internal fun aiErrorLabel(code: String): String = when (code) {
    "HOST_UPDATING" -> "电脑正在升级客户端，请等待重新连接后再操作。"
    "HISTORY_GAP" -> "历史保留范围已变化，请重新进入会话读取最新内容。"
    "DIFF_CHANGED" -> "文件变更记录已更新，请重新读取。"
    "ARTIFACT_CHANGED" -> "文件已变化，请重新打开后查看或保存。"
    "ARTIFACT_TOO_LARGE" -> "文件超过 50 MiB，请在电脑端处理。"
    "INVALID_ARTIFACT", "ARTIFACT_OUTSIDE_WORKSPACE", "ARTIFACT_UNAVAILABLE" -> "文件不在可读取的工作目录内，或已移动、被占用。"
    "HISTORICAL_PERMISSION_UNAVAILABLE" -> "无法读取此历史会话的授权方式，未使用默认权限替代。"
    "NATIVE_HISTORY_REFRESH_REQUIRED" -> "历史列表已过期，请重新打开电脑历史。"
    "NATIVE_HISTORY_UNSUPPORTED", "NATIVE_HISTORY_UNAVAILABLE", "INVALID_CURSOR" -> "暂时无法读取原生历史，请刷新或检查电脑 Agent 版本。"
    "SETTINGS_CHANGED" -> "会话配置已改变，请确认最新模型与授权后重试。"
    "REASONING_REQUIRES_NEW_SESSION" -> "该 Agent 只能在新建会话时设置推理强度；请新建会话后选择模型和强度。"
    "INVALID_REASONING_EFFORT" -> "当前模型不支持此推理强度，请刷新模型列表后重新选择。"
    "PERMISSION_CONFIRMATION_REQUIRED" -> "该授权方式需要明确确认，请重新选择并确认。"
    "PERMISSION_MODE_UNAVAILABLE" -> "当前电脑的 Agent 或管理策略不允许该授权方式。"
    "ONE_SETTING_AT_A_TIME" -> "请分别修改授权方式和模型设置。"
    "DEFAULT_REASONING_UNAVAILABLE" -> "该模型未提供默认推理强度，请选择具体档位。"
    "SETTINGS_OUTCOME_UNKNOWN" -> "Agent 未确认配置结果，请恢复会话后重新确认。"
    "CONFIGURE_UNSUPPORTED", "DEFAULT_MODEL_UNAVAILABLE" -> "此 Agent 无法确认该配置，请使用具体模型或新建会话。"
    "RESUME_UNAVAILABLE", "FORK_UNAVAILABLE" -> "当前 Agent 或原生会话不支持此操作。"
    "ATTACHMENT_LIMIT", "IMAGE_TOO_LARGE" -> "附件达到大小或数量限制（Claude 原生图片每张最多 5 MiB）。"
    "ATTACHMENT_HASH", "ATTACHMENT_CONFLICT" -> "附件校验失败，请移除后重新选择上传。"
    "ATTACHMENT_NOT_FOUND", "ATTACHMENT_NOT_READY" -> "附件尚未就绪或电脑已重启，请重新上传。"
    "INVALID_IMAGE", "INVALID_ATTACHMENT", "ATTACHMENT_PATH_UNSAFE" -> "附件格式或目标目录不可用。"
    "HISTORY_SAVE_FAILED" -> "电脑历史保存失败，请检查磁盘空间。"
    "MODEL_LIST_UNSUPPORTED" -> "此 Agent 暂不提供模型列表，可使用默认模型或手动输入。"
    "MODEL_LIST_BUSY", "MODEL_LIST_TIMEOUT", "MODEL_LIST_UNAVAILABLE" -> "模型列表暂时不可用，可重试或手动输入。"
    "AI_WORKSPACE_CREATE_FAILED" -> "无法在 Windows 安装目录下创建会话目录，请检查写权限或改选已有目录。"
    "DIRECTORY_BROWSE_FAILED" -> "无法读取该目录，请检查权限或返回上级目录。"
    "INVALID_DIRECTORY_MODE" -> "目录选项已改变，请刷新电脑能力后重试。"
    "REMOTE_NOT_CONFIGURED", "REMOTE_CONFIGURATION_INVALID" -> "请在 Windows 设置中重新保存 ZCode 桌面远控地址。"
    "AGENT_STRATEGY_CHANGED" -> "电脑端接入方式已改变，请刷新能力后重新打开。"
    "AGENT_MODEL_REQUIRED", "PROVIDER_AND_MODEL_REQUIRED" -> "请先在电脑端配置模型，或填写已配置的 Provider 与模型 ID。"
    "CONTROL_CHANGED" -> "控制权已改变，请重新获取控制权。"
    "APPROVAL_EXPIRED" -> "该请求已处理或已失效。"
    "HOST_CHANGED", "HOST_RETIRED" -> "电脑端已重启，请刷新会话目录。"
    "SESSION_NOT_FOUND", "SESSION_ENDED" -> "电脑端已无此活动会话，可关闭视图后新建。"
    "LOCAL_DIRECTORY_REQUIRED" -> "请输入电脑上存在的本地绝对目录。"
    "AGENT_UNAVAILABLE", "AGENT_PROTOCOL_INCOMPATIBLE", "AGENT_PROTOCOL_UNSUPPORTED" -> "该 Agent 当前不可用，请在电脑端重新检测。"
    "AGENT_BUSY" -> "Agent 正在处理任务，请等待或中断后再发送。"
    "SESSION_CAPACITY", "RECEIPT_CAPACITY" -> "电脑端已达到本轮容量限制，请结束不用的任务或重启 Agent。"
    "INTERACTION_TOO_LARGE" -> "操作详情过长，请在电脑端处理。"
    "ANSWER_REQUIRED" -> "请完成问题回答。"
    "AGENT_TIMEOUT", "SEND_OUTCOME_UNKNOWN" -> "Agent 未及时确认操作；请检查电脑端状态。"
    "AGENT_REPORTED_ERROR", "AGENT_REQUEST_REJECTED" -> "Agent 返回错误，请检查电脑端模型、认证或网络。"
    "AGENT_DESKTOP_AUTH_REQUIRED" -> "此模型需要 ZCode 桌面授权；当前桥接仅支持电脑上独立配置的 CLI API 模型。"
    "AGENT_AUTH_REQUIRED" -> "Agent 尚未登录，请先在电脑端完成登录后重试；Grok 可运行 grok login。"
    "PERMISSION_REQUIRES_NEW_SESSION" -> "此 Agent 的授权方式需在新建对话时设置。"
    "APPROVAL_OPTION_UNSUPPORTED" -> "Agent 未提供单次允许选项，可拒绝本次请求；不会自动授予永久权限。"
    "CANCEL_OUTCOME_UNKNOWN" -> "Agent 未确认停止，连接已结束；可重新打开或恢复会话。"
    "AGENT_NETWORK_RETRY" -> "Agent 正在重试模型网络请求，可等待或中断。"
    "AGENT_RATE_LIMITED" -> "模型服务限流或额度不足，Agent 正在重试；可停止后稍候重试，或在电脑端检查账号额度。"
    "AGENT_EXITED", "AGENT_STREAM_LOST", "AGENT_START_FAILED", "AGENT_OPERATION_FAILED" -> "Agent 已断开或启动失败，请检查电脑端安装与配置。"
    else -> "电脑未能完成请求，请刷新后重试。"
}

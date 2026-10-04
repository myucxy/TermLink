package dev.termlink.product

import dev.termlink.harness.R

import androidx.compose.ui.res.stringResource

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.view.ViewGroup
import android.webkit.*
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream

internal const val AI_WEB_ORIGIN = "https://appassets.androidplatform.net"
private const val AI_WEB_INDEX = "$AI_WEB_ORIGIN/assets/ai-web/index.html"

internal fun aiWebRequest(raw: String): JSONObject {
    require(raw.length in 2..65536)
    val request = JSONObject(raw)
    require(request.optString("id").matches(Regex("[0-9]{1,16}")))
    require(request.optString("action") in setOf("ready", "history", "interrupt", "answer", "copy", "quote", "edit",
        "search", "artifacts", "artifact.read", "diff.read", "message", "open-link", "open-file", "open-directory", "download", "ui-state", "back-state"))
    return request
}

internal fun aiArtifactRelativePath(directory: String, value: String): String {
    require(value.length in 1..2048 && value.none(Char::isISOControl))
    val path = value.replace('\\', '/')
    val root = directory.replace('\\', '/').trimEnd('/') + "/"
    val relative = if (path.startsWith(root, ignoreCase = true)) path.substring(root.length) else path
    require(!relative.startsWith('/') && ':' !in relative && relative.split('/').none { it == ".." })
    return relative
}

internal data class AiFileLocation(val pcId: java.util.UUID, val sessionId: String, val directory: String, val filePath: String)
internal fun aiArtifactLocation(session: AiConversation, value: String): AiFileLocation {
    val relative = aiArtifactRelativePath(session.directory, value)
    require(relative.split('/').all { it.isNotBlank() && it != "." && it.none { c -> c in "<>\"|?*" } })
    val absolute = session.directory.replace('\\', '/')
    require(absolute.matches(Regex("[A-Za-z]:/.*")) && absolute.none(Char::isISOControl))
    val root = absolute.trimEnd('/')
    val path = "$root/$relative".replace('/', '\\')
    return AiFileLocation(session.pcId, session.id, path.substringBeforeLast('\\').let { if (it.endsWith(':')) "$it\\" else it }, path)
}

/** Sends changed messages only; host revisions and native state remain authoritative. */
internal class AiWebUpdates {
    private var previous = emptyList<AiMessage>()
    private var initialized = false
    fun encode(session: AiConversation, busy: Boolean, uncertain: Boolean, dark: Boolean, scale: Float): JSONObject {
        val old = previous.associateBy { it.id }
        val changed = session.messages.filter { old[it.id] != it }
        val result = JSONObject().put("reset", !initialized)
            .put("session", JSONObject().put("id", session.id).put("agent", session.agent).put("state", session.state)
                .put("phase", session.phase).put("turnStartedAtMs", session.turnStartedAtMs).put("lastActivityAtMs", session.lastActivityAtMs)
                .put("canControl", session.canControl).put("busy", busy).put("uncertain", uncertain)
                .put("historyBefore", session.historyBefore).put("truncated", session.truncated)
                .put("diff", session.diff).put("usage", session.usage).put("error", session.error)
                .put("diffLength", session.diffLength).put("diffTruncated", session.diffTruncated).put("diffRead", session.capabilities.diffRead)
                .put("artifacts", session.capabilities.artifacts).put("search", session.capabilities.search)
                .put("dark", dark).put("fontScale", scale).put("language", java.util.Locale.getDefault().language)
                .put("plan", runCatching { JSONArray(session.plan) }.getOrElse { JSONArray() })
                .put("pending", JSONArray().apply { session.pending.forEach { pending -> put(JSONObject()
                    .put("id", pending.id).put("kind", pending.kind).put("title", pending.title).put("summary", pending.summary)
                    .put("detail", pending.detail).put("canApprove", pending.canApprove)
                    .put("questions", JSONArray().apply { pending.questions.forEach { q -> put(JSONObject().put("id", q.id)
                        .put("text", q.text).put("options", JSONArray(q.options)).put("multiSelect", q.multiSelect)) } })) } }))
            .put("messages", JSONArray().apply { changed.forEach { m -> put(JSONObject().put("id", m.id).put("role", m.role)
                .put("kind", m.kind).put("text", m.text).put("title", m.title).put("summary", m.summary)
                .put("status", m.status).put("fullLength", m.fullLength)) } })
        if (!initialized || previous.map { it.id } != session.messages.map { it.id }) result.put("order", JSONArray(session.messages.map { it.id }))
        previous = session.messages; initialized = true
        return result
    }
}

@Composable
internal fun AiWebConversation(workspace: AiWorkspace, session: AiConversation, modifier: Modifier,
    onOpenDirectory: suspend (AiFileLocation) -> Unit = {}) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val current by rememberUpdatedState(session)
    val host = workspace.catalogs[session.pcId]?.hostId
    val currentHost by rememberUpdatedState(workspace.catalogs[session.pcId]?.hostId)
    val dark = MaterialTheme.colorScheme.background.luminance() < .5f
    val scale = LocalDensity.current.fontScale
    var view by remember { mutableStateOf<WebView?>(null) }
    var ready by remember { mutableStateOf(false) }
    var failure by remember { mutableStateOf("") }
    var webCanGoBack by remember { mutableStateOf(false) }
    androidx.activity.compose.BackHandler(enabled = ready && webCanGoBack) { view?.evaluateJavascript("window.termlinkBack?.()", null) }
    var external by remember { mutableStateOf<String?>(null) }
    var generation by remember { mutableIntStateOf(0) }
    var updates by remember { mutableStateOf(AiWebUpdates()) }
    val activeRequests = remember { mutableMapOf<String, Boolean>() }
    val download = rememberAiArtifactDownload(workspace, session)

    fun valid() = currentHost == host && workspace.selected == session.id && workspace.unavailable[session.pcId] != true
    fun reply(web: WebView, id: String, value: Any? = null, error: String? = null) {
        if (view !== web || !valid()) return
        val body = JSONObject().put("id", id).put("value", value ?: JSONObject.NULL).put("error", error ?: JSONObject.NULL)
        web.evaluateJavascript("window.termlinkReply?.(JSON.parse(${JSONObject.quote(body.toString())}))", null)
    }

    LaunchedEffect(session, workspace.busy, workspace.uncertain, ready, dark, scale) {
        if (ready && valid()) {
            val payload = updates.encode(session, workspace.busy, workspace.uncertain, dark, scale)
            if (payload.optBoolean("reset")) payload.put("uiState", workspace.webUiState(session))
            view?.evaluateJavascript("window.termlinkUpdate?.(JSON.parse(${JSONObject.quote(payload.toString())}))", null)
        }
    }
    DisposableEffect(Unit) { onDispose { ready = false; view?.stopLoading(); view?.destroy(); view = null } }
    Column(modifier.fillMaxWidth()) {
        if (failure.isNotEmpty()) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
            Text(failure, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { failure = ""; ready = false; generation++ }) { Text(stringResource(R.string.ai_ui_reload)) }
        }
        key(generation) {
            AndroidView(modifier = Modifier.weight(1f).fillMaxWidth(), factory = { ctx ->
                val loader = WebViewAssetLoader.Builder().addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(ctx)).build()
                WebView(ctx).apply {
                    view = this
                    layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
                    settings.javaScriptEnabled = true
                    settings.allowFileAccess = false; settings.allowContentAccess = false
                    settings.domStorageEnabled = false; settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                    settings.setGeolocationEnabled(false); settings.javaScriptCanOpenWindowsAutomatically = false
                    settings.setSupportMultipleWindows(false); settings.textZoom = 100
                    CookieManager.getInstance().setAcceptThirdPartyCookies(this, false)
                    webChromeClient = object : WebChromeClient() {
                        override fun onPermissionRequest(request: PermissionRequest) { request.deny() }
                    }
                    webViewClient = object : WebViewClient() {
                        override fun shouldInterceptRequest(web: WebView, request: WebResourceRequest): WebResourceResponse =
                            loader.shouldInterceptRequest(request.url) ?: WebResourceResponse("text/plain", "utf-8", 403, "Blocked", emptyMap(), ByteArrayInputStream(ByteArray(0)))
                        override fun shouldOverrideUrlLoading(web: WebView, request: WebResourceRequest): Boolean =
                            request.isForMainFrame || !request.url.toString().startsWith("blob:$AI_WEB_ORIGIN/")
                        override fun onReceivedError(web: WebView, request: WebResourceRequest, error: WebResourceError) {
                            if (request.isForMainFrame) { ready = false; failure = "会话页面加载失败，草稿已保留。" }
                        }
                        override fun onRenderProcessGone(web: WebView, detail: RenderProcessGoneDetail): Boolean {
                            ready = false; failure = "页面渲染已停止，可重新加载。"; web.destroy(); if (view === web) view = null
                            return true
                        }
                    }
                    WebViewCompat.addWebMessageListener(this, "TermLink", setOf(AI_WEB_ORIGIN)) { web, message, origin, mainFrame, _ ->
                        if (!mainFrame || origin.toString().trimEnd('/') != AI_WEB_ORIGIN || web.url != AI_WEB_INDEX || !valid()) return@addWebMessageListener
                        val request = runCatching { aiWebRequest(message.data.orEmpty()) }.getOrNull() ?: return@addWebMessageListener
                        val id = request.getString("id")
                        if (id in activeRequests) return@addWebMessageListener
                        val command = request.getString("action") in setOf("interrupt", "answer")
                        if (activeRequests.values.count { it == command } >= if (command) 1 else 4) {
                            reply(web, id, error = "请等待当前操作完成"); return@addWebMessageListener
                        }
                        activeRequests[id] = command
                        scope.launch {
                            try {
                                val active = current
                                val action = request.getString("action")
                                val text = request.optString("text")
                                val value: Any = when (action) {
                                    "ready" -> { updates = AiWebUpdates(); ready = true; true }
                                    "back-state" -> { webCanGoBack = request.optBoolean("canGoBack"); true }
                                    "ui-state" -> { workspace.saveWebUiState(active, request.getJSONObject("state")); true }
                                    "copy" -> { require(text.length <= 1048577); (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("AI", text)); true }
                                    "quote" -> { require(text.length <= 65536); check(workspace.quote(active.id, text)) { workspace.error }; true }
                                    "edit" -> { require(text.length <= 65536); check(workspace.appendDraft(active.id, text)) { workspace.error }; true }
                                    "history" -> { workspace.loadHistory(active); true }
                                    "message" -> workspace.fullMessage(active, AiMessage(request.getString("messageId").also { require(it.length in 1..256) }, "", ""))
                                    "diff.read" -> {
                                        check(active.capabilities.diffRead)
                                        val offset = request.getLong("offset"); val version = request.optString("version")
                                        require(offset in 0..262145 && version.length <= 100)
                                        workspace.readFeature(active, "diff.read", JSONObject().put("offset", offset).put("text", version))
                                    }
                                    "search", "artifacts" -> { val query = request.optString("query"); require(query.length <= 200)
                                        val offset = request.optLong("offset"); require(offset in 0..1000000)
                                        check(if (action == "search") active.capabilities.search else active.capabilities.artifacts)
                                        workspace.readFeature(active, action, JSONObject().put("text", query).put("offset", offset)) }
                                    "artifact.read", "open-file" -> {
                                        check(active.capabilities.artifacts)
                                        val file = aiArtifactRelativePath(active.directory, request.getString(if (action == "open-file") "path" else "fileId"))
                                        val offset = request.optLong("offset"); val version = request.optString("version")
                                        require(offset in 0..50L * 1024 * 1024 && version.length <= 100)
                                        workspace.readFeature(active, "artifact.read", JSONObject().put("requestId", file).put("offset", offset).put("cursor", version))
                                    }
                                    "download" -> { check(active.capabilities.artifacts)
                                        val file = aiArtifactRelativePath(active.directory, request.getString("fileId"))
                                        val version = request.getString("version"); require(version.length in 1..100)
                                        download(AiArtifactDownload(file, version, file.substringAfterLast('/'))); true }
                                    "open-directory" -> {
                                        check(active.capabilities.artifacts)
                                        val location = aiArtifactLocation(active, request.getString("fileId"))
                                        onOpenDirectory(location); true
                                    }
                                    "open-link" -> { val url = request.getString("url"); val uri = Uri.parse(url)
                                        require(url.length <= 4096 && uri.scheme in setOf("http", "https") && !uri.host.isNullOrBlank() && uri.userInfo == null)
                                        external = url; true }
                                    "interrupt", "answer" -> {
                                        check(active.canControl && !workspace.busy && !workspace.uncertain)
                                        val extra = JSONObject()
                                        if (action == "answer") {
                                            val pending = active.pending.single { it.id == request.getString("requestId") }
                                            val decision = request.getString("decision"); require(decision in setOf("allow", "deny") && (decision == "deny" || pending.canApprove))
                                            val answers = request.optJSONObject("answers") ?: JSONObject(); require(answers.toString().length <= 24000)
                                            extra.put("requestId", pending.id).put("decision", decision).put("answers", answers)
                                        }
                                        check(workspace.mutate(active.pcId, action, active, extra)?.await() == true) {
                                            workspace.error.ifBlank { "操作尚未完成，请确认电脑回执。" }
                                        }; true
                                    }
                                    else -> error("不支持的操作")
                                }
                                reply(web, id, value)
                            } catch (e: Exception) {
                                if (e is CancellationException) throw e
                                reply(web, id, error = e.message?.take(200) ?: "操作失败，请重试")
                            } finally { activeRequests.remove(id) }
                        }
                    }
                    loadUrl(AI_WEB_INDEX)
                }
            }, onRelease = { web -> WebViewCompat.removeWebMessageListener(web, "TermLink"); web.stopLoading(); web.destroy(); if (view === web) view = null })
        }
    }
    external?.let { url -> AlertDialog(onDismissRequest = { external = null }, title = { Text(stringResource(R.string.ai_ui_external_link)) },
        text = { Text(url, maxLines = 5) }, confirmButton = { TextButton(onClick = { external = null
            runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }.onFailure { failure = "没有可用浏览器。" }
        }) { Text(stringResource(R.string.ai_ui_open)) } }, dismissButton = { TextButton(onClick = { external = null }) { Text(stringResource(R.string.ai_ui_cancel)) } }) }
}

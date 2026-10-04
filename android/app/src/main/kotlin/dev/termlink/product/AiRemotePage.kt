package dev.termlink.product

import android.content.Intent
import android.net.Uri
import android.net.http.SslError
import android.view.ViewGroup
import android.webkit.*
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.view.doOnLayout
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import java.net.URI

internal fun isAllowedZcodeRemoteUrl(value: String): Boolean = runCatching {
    val uri = URI(value)
    value.length in 1..8192 && value.none { it.isWhitespace() || it.isISOControl() || it == '\\' } &&
        uri.scheme.equals("https", true) && uri.host.equals("zcode.z.ai", true) &&
        uri.port in setOf(-1, 443) && uri.rawUserInfo == null &&
        uri.normalize().rawPath.let { it == "/remote" || it.startsWith("/remote/") }
}.getOrDefault(false)

/** The host supplies the link over E2EE; webpage traffic uses ZCode's own service. */
@Composable
internal fun AiRemotePage(workspace: AiWorkspace, entry: AiOpened, pcName: String, online: Boolean, foreground: Boolean, modifier: Modifier) {
    var generation by remember(entry.sessionId) { mutableIntStateOf(0) }
    var address by remember(entry.sessionId) { mutableStateOf<String?>(null) }
    var failure by remember(entry.sessionId) { mutableStateOf("") }
    var progress by remember(entry.sessionId) { mutableIntStateOf(0) }
    var menu by remember { mutableStateOf(false) }
    var remoteView by remember { mutableStateOf<WebView?>(null) }
    var canGoBack by remember { mutableStateOf(false) }
    androidx.activity.compose.BackHandler(enabled = canGoBack && online) { remoteView?.goBack() }
    val context = LocalContext.current
    var fileCallback by remember { mutableStateOf<ValueCallback<Array<Uri>>?>(null) }
    var fileLimit by remember { mutableIntStateOf(1) }
    val currentOnline by rememberUpdatedState(online)
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        fileCallback?.onReceiveValue(uris.take(fileLimit).takeIf { currentOnline && it.isNotEmpty() }?.toTypedArray()); fileCallback = null
    }
    DisposableEffect(entry, online) { onDispose { fileCallback?.onReceiveValue(null); fileCallback = null } }
    val host = workspace.catalogs[entry.pcId]?.hostId
    val configured = workspace.catalogs[entry.pcId]?.agents?.any { it.id == entry.agentId && it.strategy == "desktop_remote" && it.supported } == true
    LaunchedEffect(entry, host, online, configured, generation) {
        address = null; failure = ""; progress = 0
        if (online && host != null && configured) try { address = workspace.remoteAddress(entry) }
        catch (error: Exception) {
            if (error is CancellationException && error !is TimeoutCancellationException) throw error
            failure = "无法取得远控地址，请检查电脑端配置后重试。"
        }
    }
    Column(modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth().heightIn(min = 52.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { workspace.selected = null }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "返回 AI 列表") }
            Column(Modifier.weight(1f)) {
                Text("ZCode", style = MaterialTheme.typography.titleSmall)
                Text("$pcName · 桌面远控", maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall)
            }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, "ZCode 远控菜单") }
                DropdownMenu(menu, { menu = false }) {
                    DropdownMenuItem(text = { Text("重新获取地址") }, enabled = online, onClick = { menu = false; generation++ })
                    DropdownMenuItem(text = { Text("在浏览器打开") }, enabled = address != null, onClick = {
                        menu = false
                        address?.takeIf(::isAllowedZcodeRemoteUrl)?.let {
                            runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(it))) }
                                .onFailure { failure = "没有可用浏览器。" }
                        }
                    })
                    DropdownMenuItem(text = { Text("关闭手机视图") }, onClick = { menu = false; workspace.closeView(entry.sessionId) })
                }
            }
        }
        HorizontalDivider()
        // Dispose the webpage when the host is offline, credentials change, or this view closes.
        val target = address.takeIf { online && configured }
        if (target != null) {
            if (progress < 100 && failure.isEmpty()) LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp))
            if (failure.isNotEmpty()) Text(failure, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            key(target, generation) {
                AndroidView(modifier = Modifier.weight(1f).fillMaxWidth(), factory = { context ->
                    WebView(context).apply {
                        remoteView = this
                        // A wrap-content WebView gives percentage-height pages a zero-height root.
                        layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
                        settings.javaScriptEnabled = true
                        settings.domStorageEnabled = true
                        settings.useWideViewPort = true
                        settings.loadWithOverviewMode = true
                        settings.safeBrowsingEnabled = true
                        settings.allowFileAccess = false
                        settings.allowContentAccess = false
                        settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                        settings.setGeolocationEnabled(false)
                        settings.javaScriptCanOpenWindowsAutomatically = false
                        settings.cacheMode = WebSettings.LOAD_NO_CACHE
                        CookieManager.getInstance().setAcceptThirdPartyCookies(this, false)
                        webChromeClient = object : WebChromeClient() {
                            override fun onProgressChanged(view: WebView, newProgress: Int) { progress = newProgress }
                            override fun onShowFileChooser(view: WebView, callback: ValueCallback<Array<Uri>>, params: FileChooserParams): Boolean {
                                if (!currentOnline || fileCallback != null || !isAllowedZcodeRemoteUrl(view.url.orEmpty())) return false
                                fileCallback?.onReceiveValue(null); fileCallback = callback
                                fileLimit = if (params.mode == FileChooserParams.MODE_OPEN_MULTIPLE) 20 else 1
                                val types = params.acceptTypes.filter { it.contains('/') && !it.any(Char::isWhitespace) }.toTypedArray()
                                return runCatching { filePicker.launch(types.ifEmpty { arrayOf("*/*") }); true }.getOrElse { callback.onReceiveValue(null); fileCallback = null; true }
                            }
                        }
                        webViewClient = object : WebViewClient() {
                            override fun doUpdateVisitedHistory(view: WebView, url: String?, isReload: Boolean) { canGoBack = view.canGoBack() }
                            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                                val blocked = !isAllowedZcodeRemoteUrl(request.url.toString())
                                if (blocked && request.isForMainFrame) failure = "页面跳转受限，可从菜单使用浏览器打开。"
                                return blocked
                            }
                            override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: SslError) {
                                handler.cancel(); failure = "远控网页证书验证失败。"
                            }
                            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                                if (request.isForMainFrame) failure = "远控网页加载失败，请重试或使用浏览器。"
                            }
                            override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
                                if (request.isForMainFrame) failure = "远控服务暂不可用，请稍后重试。"
                            }
                            override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                                address = null; failure = "网页渲染已中断，请从菜单重新获取地址。"
                                return true
                            }
                        }
                        doOnLayout { loadUrl(target) }
                    }
                }, update = { if (foreground) it.onResume() else it.onPause() }, onRelease = {
                    remoteView = null; canGoBack = false
                    fileCallback?.onReceiveValue(null); fileCallback = null
                    it.stopLoading(); it.webChromeClient = null; it.webViewClient = WebViewClient()
                    it.clearHistory(); it.removeAllViews(); it.destroy()
                })
            }
        } else Box(Modifier.weight(1f).fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) {
            Text(when {
                !online -> "电脑连接不可用，已隐藏远控页面。"
                failure.isNotEmpty() -> failure
                host != null && !configured -> "请在 Windows 设置中配置 ZCode 桌面远控地址。"
                else -> "正在获取远控地址…"
            }, style = MaterialTheme.typography.bodyMedium)
        }
    }
}

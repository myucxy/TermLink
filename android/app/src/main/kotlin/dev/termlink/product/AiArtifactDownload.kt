package dev.termlink.product

import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext

internal data class AiArtifactDownload(val id: String, val version: String, val name: String)
private data class PendingArtifactDownload(val file: AiArtifactDownload, val session: AiConversation, val host: String)

/** SAF chooses a destination; the account workspace owns the download, not this page. */
@Composable
internal fun rememberAiArtifactDownload(workspace: AiWorkspace, session: AiConversation): (AiArtifactDownload) -> Unit {
    val context = LocalContext.current
    var pending by remember { mutableStateOf<PendingArtifactDownload?>(null) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val file = pending; pending = null
        if (uri != null && file != null) {
            runCatching {
                check(workspace.catalogs[file.session.pcId]?.hostId == file.host)
                checkNotNull(workspace.downloads).start(file.session, file.host, file.session.pcId.toString().take(8), file.file, uri)
            }.onSuccess {
                Toast.makeText(context, "已加入下载中心，可离开当前页面。", Toast.LENGTH_SHORT).show()
            }.onFailure { Toast.makeText(context, "无法开始下载，请重新读取文件后重试。", Toast.LENGTH_LONG).show() }
        }
    }
    return { file ->
        check(pending == null) { "请先选择保存位置" }
        pending = PendingArtifactDownload(file, session, checkNotNull(workspace.catalogs[session.pcId]).hostId)
        launcher.launch(file.name.take(160))
    }
}

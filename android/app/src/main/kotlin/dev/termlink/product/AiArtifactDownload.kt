package dev.termlink.product

import android.provider.DocumentsContract
import android.util.Base64
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

internal data class AiArtifactDownload(val id: String, val version: String, val name: String)

/** SAF decides the destination. Bytes are streamed natively; no JS or arbitrary phone paths. */
@Composable
internal fun rememberAiArtifactDownload(workspace: AiWorkspace, session: AiConversation): (AiArtifactDownload) -> Unit {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var pending by remember { mutableStateOf<AiArtifactDownload?>(null) }
    var saving by remember { mutableStateOf(false) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val file = pending; pending = null
        if (uri != null && file != null) scope.launch {
            saving = true
            try {
                val stream = withContext(Dispatchers.IO) { checkNotNull(context.contentResolver.openOutputStream(uri, "w")) }
                stream.use { output ->
                    var offset = 0L; var expectedSize: Long? = null
                    do {
                        val page = workspace.readFeature(session, "artifact.read", JSONObject().put("requestId", file.id)
                            .put("cursor", file.version).put("offset", offset))
                        val size = page.getLong("size"); val next = page.getLong("nextOffset")
                        val bytes = Base64.decode(page.getString("data"), Base64.NO_WRAP)
                        require(page.getString("version") == file.version && page.getString("id") == file.id &&
                            page.getLong("offset") == offset && size in 0..50L * 1024 * 1024 &&
                            (expectedSize == null || expectedSize == size) && bytes.size <= 65536 &&
                            next == offset + bytes.size && next <= size && (next > offset || next == size))
                        expectedSize = size
                        withContext(Dispatchers.IO) { output.write(bytes) }
                        offset = next
                    } while (offset < size)
                }
                Toast.makeText(context, "文件已保存", Toast.LENGTH_SHORT).show()
            } catch (error: Exception) {
                runCatching { DocumentsContract.deleteDocument(context.contentResolver, uri) }
                if (error is CancellationException) throw error
                Toast.makeText(context, "保存失败，文件可能已变化，请重新读取。", Toast.LENGTH_LONG).show()
            } finally { saving = false }
        }
    }
    return { file ->
        check(!saving && pending == null) { "请先完成当前文件保存" }
        pending = file; launcher.launch(file.name.take(160))
    }
}

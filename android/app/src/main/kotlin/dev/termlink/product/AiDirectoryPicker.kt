package dev.termlink.product

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import java.util.UUID

/** Read-only host directory browsing; never changes the regular file browser's state. */
@Composable
internal fun AiDirectoryPicker(workspace: AiWorkspace, pcId: UUID, initialPath: String, online: Boolean,
    onDismiss: () -> Unit, onSelect: (String) -> Unit, selectFile: Boolean = false) {
    val host = workspace.catalogs[pcId]?.hostId
    var path by rememberSaveable(pcId, host, online) { mutableStateOf(initialPath) }
    var offset by rememberSaveable(pcId, host, online) { mutableLongStateOf(0) }
    var attempt by remember { mutableIntStateOf(0) }
    var page by remember(pcId, host, online) { mutableStateOf<RemoteDirectoryPage?>(null) }
    var loading by remember(pcId, host, online) { mutableStateOf(true) }
    var error by remember(pcId, host, online) { mutableStateOf("") }
    fun navigate(next: String) { page = null; loading = true; path = next; offset = 0; attempt++ }
    LaunchedEffect(pcId, path, offset, attempt, host, online) {
        loading = true; error = ""
        if (!online) { page = null; error = "电脑连接不可用"; loading = false; return@LaunchedEffect }
        try {
            val next = workspace.directories(pcId, path, offset, includeFiles = selectFile)
            page = next.copy(entries = (if (offset > 0) page?.entries.orEmpty() else emptyList()) + next.entries)
        } catch (failure: Exception) {
            if (failure is CancellationException && failure !is TimeoutCancellationException) throw failure
            error = "无法读取目录，请检查电脑连接或目录权限。"
        } finally { loading = false }
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(if (selectFile) "选择电脑文件" else "选择电脑目录", style = MaterialTheme.typography.titleMedium) },
        text = {
            Column(Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(enabled = !loading && path.isNotEmpty(), onClick = { navigate(page?.parentPath.orEmpty()) }) {
                        Icon(Icons.Default.ArrowUpward, "上级目录")
                    }
                    Text(page?.currentPath?.ifBlank { "电脑磁盘" } ?: path.ifBlank { "电脑磁盘" },
                        Modifier.weight(1f), maxLines = 3, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                }
                if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
                if (error.isNotEmpty()) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(error, Modifier.weight(1f), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                    TextButton(enabled = !loading && online, onClick = { attempt++ }) { Text("重试") }
                }
                LazyColumn(Modifier.fillMaxWidth().heightIn(min = 80.dp, max = 320.dp)) {
                    items(page?.entries.orEmpty().distinctBy { it.path }, key = { it.path }) { entry ->
                        Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable(enabled = online && !loading) { if (selectFile && entry.kind == RemoteFileKind.FILE) onSelect(entry.path) else navigate(entry.path) },
                            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Icon(Icons.Default.Folder, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
                            Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                    if (!loading && error.isEmpty() && page?.entries?.isEmpty() == true) item { Text("没有子目录", Modifier.padding(vertical = 12.dp)) }
                    page?.nextOffset?.let { next -> item {
                        TextButton(enabled = online && !loading, onClick = { loading = true; offset = next }) { Text("加载更多") }
                    } }
                }
            }
        }, confirmButton = {
            if (!selectFile) TextButton(enabled = online && !loading && error.isEmpty() && page?.currentPath?.isNotEmpty() == true,
                onClick = { page?.currentPath?.let(onSelect) }) { Text("使用此目录") }
        }, dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } })
}

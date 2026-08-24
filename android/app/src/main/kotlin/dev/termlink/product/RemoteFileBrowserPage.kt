package dev.termlink.product

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.DeleteForever
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.termlink.harness.R
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

@Composable
internal fun RemoteFileBrowserPage(
    state: RemoteFileBrowserState,
    onOpenDirectory: (String) -> Unit,
    onLoadMore: (String, Long) -> Unit,
    onRefresh: (String) -> Unit,
    onDownload: (RemoteFileEntry) -> Unit,
    onOpenShell: (String, RemoteShellType) -> Unit,
    modifier: Modifier = Modifier,
) {
    val parentTarget = remoteFileParentTarget(state.currentPath, state.parentPath)
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item(key = "header") {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(state.pcName, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(
                    state.currentPath.ifBlank { stringResource(R.string.remote_files_root) },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { parentTarget?.let(onOpenDirectory) },
                        enabled = parentTarget != null && !state.loading,
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.remote_files_parent))
                    }
                    OutlinedButton(
                        onClick = { onRefresh(state.currentPath) },
                        enabled = !state.loading,
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.Refresh, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_refresh))
                    }
                }
                if (state.currentPath.isNotBlank()) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        ShellButton(
                            label = stringResource(R.string.action_open_cmd),
                            loading = state.openingShell == RemoteShellType.COMMAND_PROMPT,
                            enabled = state.openingShell == null,
                            onClick = { onOpenShell(state.currentPath, RemoteShellType.COMMAND_PROMPT) },
                        )
                        ShellButton(
                            label = stringResource(R.string.action_open_powershell),
                            loading = state.openingShell == RemoteShellType.WINDOWS_POWERSHELL,
                            enabled = state.openingShell == null,
                            onClick = { onOpenShell(state.currentPath, RemoteShellType.WINDOWS_POWERSHELL) },
                        )
                    }
                }
                state.error?.let { error ->
                    Text(error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                }
                if (state.loading && state.entries.isEmpty()) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
                        horizontalArrangement = Arrangement.Center,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        CircularProgressIndicator(modifier = Modifier.size(24.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text(stringResource(R.string.remote_files_loading))
                    }
                } else if (!state.loading && state.entries.isEmpty() && state.error == null) {
                    Text(
                        stringResource(R.string.remote_files_empty),
                        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
        items(state.entries, key = RemoteFileEntry::path) { entry ->
            RemoteFileEntryRow(
                entry = entry,
                shellOpening = state.openingShell,
                onOpenDirectory = onOpenDirectory,
                onDownload = onDownload,
                onOpenShell = onOpenShell,
            )
        }
        state.nextOffset?.let { offset ->
            item(key = "more-$offset") {
                Button(
                    onClick = { onLoadMore(state.currentPath, offset) },
                    enabled = !state.loading,
                    modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                ) {
                    if (state.loading) {
                        CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(8.dp))
                    }
                    Text(stringResource(R.string.remote_files_load_more))
                }
            }
        }
    }
}

@Composable
private fun RemoteFileEntryRow(
    entry: RemoteFileEntry,
    shellOpening: RemoteShellType?,
    onOpenDirectory: (String) -> Unit,
    onDownload: (RemoteFileEntry) -> Unit,
    onOpenShell: (String, RemoteShellType) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.45f)),
    ) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(enabled = entry.kind == RemoteFileKind.DIRECTORY) {
                        onOpenDirectory(entry.path)
                    }
                    .padding(start = 12.dp, top = 10.dp, end = 4.dp, bottom = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Icon(
                    if (entry.kind == RemoteFileKind.DIRECTORY) Icons.Filled.Folder else Icons.Filled.Description,
                    contentDescription = null,
                    tint = if (entry.kind == RemoteFileKind.DIRECTORY) {
                        MaterialTheme.colorScheme.tertiary
                    } else {
                        MaterialTheme.colorScheme.primary
                    },
                )
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    val details = buildList {
                        entry.sizeBytes?.let { add(formatRemoteBytes(it)) }
                        entry.modifiedAt?.let {
                            add(REMOTE_FILE_TIME_FORMATTER.format(it.atZone(ZoneId.systemDefault())))
                        }
                    }.joinToString(" · ")
                    if (details.isNotEmpty()) {
                        Text(
                            details,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                IconButton(onClick = { onDownload(entry) }, modifier = Modifier.size(48.dp)) {
                    Icon(Icons.Filled.Download, contentDescription = stringResource(R.string.action_download))
                }
            }
            if (entry.kind == RemoteFileKind.DIRECTORY) {
                HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.25f))
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
                    horizontalArrangement = Arrangement.End,
                ) {
                    TextButton(
                        onClick = { onOpenShell(entry.path, RemoteShellType.COMMAND_PROMPT) },
                        enabled = shellOpening == null,
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) { Text(stringResource(R.string.action_open_cmd)) }
                    TextButton(
                        onClick = { onOpenShell(entry.path, RemoteShellType.WINDOWS_POWERSHELL) },
                        enabled = shellOpening == null,
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) { Text(stringResource(R.string.action_open_powershell)) }
                }
            }
        }
    }
}

@Composable
private fun ShellButton(label: String, loading: Boolean, enabled: Boolean, onClick: () -> Unit) {
    OutlinedButton(onClick = onClick, enabled = enabled, modifier = Modifier.heightIn(min = 48.dp)) {
        if (loading) CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
        else Icon(Icons.Filled.Terminal, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(6.dp))
        Text(label)
    }
}

@Composable
internal fun RemoteDownloadCenterPage(
    pcId: UUID,
    pcName: String,
    records: List<RemoteDownloadRecord>,
    onCancel: (UUID) -> Unit,
    onRetry: (RemoteDownloadRecord) -> Unit,
    onDelete: (UUID) -> Unit,
    modifier: Modifier = Modifier,
) {
    val visibleRecords = records.filter { it.pcId == pcId }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item(key = "download-header") {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(pcName, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(
                    stringResource(R.string.remote_download_delete_note),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (visibleRecords.isEmpty()) {
                    Text(
                        stringResource(R.string.remote_download_empty),
                        modifier = Modifier.fillMaxWidth().padding(vertical = 28.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
        items(visibleRecords, key = RemoteDownloadRecord::id) { record ->
            RemoteDownloadRecordRow(record, onCancel, onRetry, onDelete)
        }
    }
}

@Composable
private fun RemoteDownloadRecordRow(
    record: RemoteDownloadRecord,
    onCancel: (UUID) -> Unit,
    onRetry: (RemoteDownloadRecord) -> Unit,
    onDelete: (UUID) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.45f)),
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(
                    if (record.directoryZip) Icons.Filled.Folder else Icons.Filled.Description,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
                Column(modifier = Modifier.weight(1f)) {
                    Text(record.filename, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(
                        remoteDownloadStatusLabel(record),
                        style = MaterialTheme.typography.bodySmall,
                        color = when (record.status) {
                            RemoteDownloadStatus.COMPLETED -> MaterialTheme.colorScheme.secondary
                            RemoteDownloadStatus.FAILED -> MaterialTheme.colorScheme.error
                            else -> MaterialTheme.colorScheme.onSurfaceVariant
                        },
                    )
                }
            }
            if (record.status.isActive) {
                val total = record.totalBytes
                if (total != null && total > 0L) {
                    LinearProgressIndicator(
                        progress = { (record.downloadedBytes.toFloat() / total).coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth(),
                    )
                } else {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            }
            val totalText = record.totalBytes?.let(::formatRemoteBytes) ?: "—"
            Text(
                stringResource(
                    R.string.remote_download_progress,
                    formatRemoteBytes(record.downloadedBytes),
                    totalText,
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                record.remotePath,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                stringResource(
                    R.string.remote_download_target,
                    record.targetUri,
                    if (record.directoryZip) stringResource(R.string.remote_download_zip) else "",
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            record.error?.let {
                Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
            Text(
                REMOTE_FILE_TIME_FORMATTER.format(record.updatedAt.atZone(ZoneId.systemDefault())),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                when {
                    record.status.isActive -> TextButton(
                        onClick = { onCancel(record.id) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.Cancel, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_cancel_download))
                    }
                    record.status == RemoteDownloadStatus.FAILED ||
                        record.status == RemoteDownloadStatus.CANCELLED -> TextButton(
                        onClick = { onRetry(record) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.Refresh, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_retry_download))
                    }
                }
                if (record.status.isTerminal) {
                    TextButton(
                        onClick = { onDelete(record.id) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.DeleteForever, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_delete_download_record))
                    }
                }
            }
        }
    }
}

@Composable
private fun remoteDownloadStatusLabel(record: RemoteDownloadRecord): String = when (record.status) {
    RemoteDownloadStatus.PREPARING -> stringResource(
        if (record.directoryZip) R.string.remote_download_compressing else R.string.remote_download_preparing,
    )
    RemoteDownloadStatus.DOWNLOADING -> stringResource(R.string.remote_download_downloading)
    RemoteDownloadStatus.VERIFYING -> stringResource(R.string.remote_download_verifying)
    RemoteDownloadStatus.COMPLETED -> stringResource(R.string.remote_download_completed)
    RemoteDownloadStatus.FAILED -> stringResource(R.string.remote_download_failed)
    RemoteDownloadStatus.CANCELLED -> stringResource(R.string.remote_download_cancelled)
}

private fun formatRemoteBytes(bytes: Long): String = when {
    bytes >= 1024L * 1024L * 1024L -> String.format(Locale.getDefault(), "%.1f GB", bytes / (1024f * 1024f * 1024f))
    bytes >= 1024L * 1024L -> String.format(Locale.getDefault(), "%.1f MB", bytes / (1024f * 1024f))
    bytes >= 1024L -> String.format(Locale.getDefault(), "%.1f KB", bytes / 1024f)
    else -> "$bytes B"
}

private val REMOTE_FILE_TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")

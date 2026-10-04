package dev.termlink.product

import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import android.util.Base64
import androidx.compose.runtime.mutableStateMapOf
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject

/** Account-owned jobs survive navigation. The exact host, session and artifact version stay pinned. */
internal class AiDownloads(
    context: Context,
    private val repository: ProductRepository,
    private val scope: CoroutineScope,
    private val preferences: SharedPreferences,
    private val seal: (String) -> String,
    private val unseal: (String) -> String,
) {
    private val resolver = context.applicationContext.contentResolver
    private data class Source(val host: String, val session: String, val file: String, val version: String)
    private val sources = mutableMapOf<UUID, Source>()
    val records = mutableStateMapOf<UUID, RemoteDownloadRecord>()
    private val jobs = mutableMapOf<UUID, Job>()
    private var retired = false

    init {
        runCatching {
            val items = JSONArray(unseal(preferences.getString("downloads", null) ?: return@runCatching))
            repeat(minOf(items.length(), MAX_REMOTE_DOWNLOAD_RECORDS)) { index ->
                val item = items.getJSONObject(index)
                val id = UUID.fromString(item.getString("id"))
                val status = RemoteDownloadStatus.valueOf(item.getString("status"))
                sources[id] = Source(item.getString("host"), item.getString("session"), item.getString("file"), item.getString("version"))
                records[id] = RemoteDownloadRecord(id, UUID.fromString(item.getString("pc")), item.getString("name"),
                    item.getString("path"), item.getString("filename"), false,
                    if (status.isActive) RemoteDownloadStatus.FAILED else status, item.getString("uri"),
                    totalBytes = item.optLong("total", -1).takeIf { it >= 0 }, downloadedBytes = item.optLong("bytes"),
                    createdAt = Instant.parse(item.getString("created")),
                    error = if (status.isActive) "下载已中断，请重试。" else item.optString("error").takeIf { it.isNotBlank() })
            }
        }
    }

    fun start(session: AiConversation, host: String, pcName: String, file: AiArtifactDownload, uri: Uri) {
        check(!retired && jobs.size < 3 && records.size < MAX_REMOTE_DOWNLOAD_RECORDS) { "请先完成当前下载或清理下载记录" }
        retainDocument(uri)
        val id = UUID.randomUUID()
        sources[id] = Source(host, session.id, file.id, file.version)
        records[id] = RemoteDownloadRecord(id, session.pcId, repository.snapshot.value.pcs.firstOrNull { it.id == session.pcId }?.name ?: pcName, "${session.title.ifBlank { session.agent }} · ${file.name}",
            file.name, false, RemoteDownloadStatus.PREPARING, uri.toString())
        try { persist() } catch (error: Exception) { records.remove(id); sources.remove(id); throw error }
        launch(id)
    }

    fun retry(id: UUID, uri: Uri) {
        check(!retired && jobs.size < 3 && records[id]?.status in setOf(RemoteDownloadStatus.FAILED, RemoteDownloadStatus.CANCELLED))
        retainDocument(uri)
        update(id) { it.copy(status = RemoteDownloadStatus.PREPARING, targetUri = uri.toString(), downloadedBytes = 0, error = null) }
        launch(id)
    }

    fun cancel(id: UUID) { jobs[id]?.cancel() }
    private fun retainDocument(uri: Uri) {
        require(uri.scheme == "content")
        // SAF providers may only grant temporary access; saving still works for those providers.
        runCatching { resolver.takePersistableUriPermission(uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
    }
    fun remove(id: UUID) {
        check(records[id]?.status?.isActive != true)
        records.remove(id); sources.remove(id); persist()
    }
    fun retire(clear: Boolean) {
        retired = true; jobs.values.toList().forEach { it.cancel() }; jobs.clear()
        if (clear) { records.clear(); sources.clear(); preferences.edit().remove("downloads").commit() }
    }

    private fun launch(id: UUID) {
        val record = records.getValue(id); val source = sources.getValue(id)
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                // Read and validate first, before opening/truncating a destination document.
                var offset = 0L; var size: Long? = null
                var output: java.io.OutputStream? = null
                try {
                    do {
                        val response = JSONObject(repository.aiRequest(record.pcId, JSONObject()
                            .put("action", "artifact.read").put("hostId", source.host).put("sessionId", source.session)
                            .put("requestId", source.file).put("cursor", source.version).put("offset", offset).toString()))
                        currentCoroutineContext().ensureActive(); check(!retired)
                        check(response.optString("error").isEmpty() && response.getString("hostId") == source.host)
                        val page = response.getJSONObject("artifacts")
                        val bytes = Base64.decode(page.getString("data"), Base64.NO_WRAP)
                        val next = validateAiArtifactChunk(page, source.file, source.version, offset, size, bytes.size)
                        size = page.getLong("size")
                        if (output == null) withContext(Dispatchers.IO) { output = checkNotNull(resolver.openOutputStream(Uri.parse(record.targetUri), "wt")) }
                        withContext(Dispatchers.IO) { output!!.write(bytes) }
                        offset = next
                        update(id, persist = false) { it.copy(status = RemoteDownloadStatus.DOWNLOADING, downloadedBytes = offset, totalBytes = size) }
                    } while (offset < size!!)
                } finally { withContext(NonCancellable + Dispatchers.IO) { output?.close() } }
                currentCoroutineContext().ensureActive(); check(!retired)
                update(id) { it.copy(status = RemoteDownloadStatus.COMPLETED) }
            } catch (error: Exception) {
                val cancelled = error is CancellationException && !currentCoroutineContext().isActive
                if (!retired) {
                    update(id, persist = false) { it.copy(status = if (cancelled) RemoteDownloadStatus.CANCELLED else RemoteDownloadStatus.FAILED,
                        error = if (cancelled) null else if (error is TimeoutCancellationException) "读取电脑文件超时，请重试。" else "保存失败，请确认电脑在线且文件未变化，再重试。") }
                    runCatching { persist() }
                }
                if (cancelled) throw error
            } finally { jobs.remove(id) }
        }
        jobs[id] = job; job.start()
    }

    private fun update(id: UUID, persist: Boolean = true, change: (RemoteDownloadRecord) -> RemoteDownloadRecord) {
        val previous = records[id] ?: return
        records[id] = change(previous).copy(updatedAt = Instant.now())
        if (persist) try { persist() } catch (error: Exception) { records[id] = previous; throw error }
    }
    private fun persist() {
        val items = JSONArray()
        records.values.sortedByDescending { it.createdAt }.forEach { r ->
            val s = sources.getValue(r.id)
            items.put(JSONObject().put("id", r.id).put("pc", r.pcId).put("name", r.pcName).put("path", r.remotePath)
                .put("filename", r.filename).put("uri", r.targetUri).put("status", r.status.name).put("total", r.totalBytes ?: -1)
                .put("bytes", r.downloadedBytes).put("created", r.createdAt).put("error", r.error.orEmpty())
                .put("host", s.host).put("session", s.session).put("file", s.file).put("version", s.version))
        }
        check(preferences.edit().putString("downloads", seal(items.toString())).commit()) { "无法保存下载记录" }
    }
}

internal fun validateAiArtifactChunk(page: JSONObject, id: String, version: String, offset: Long, expectedSize: Long?, byteCount: Int): Long {
    val size = page.getLong("size"); val next = page.getLong("nextOffset")
    require(page.getString("version") == version && page.getString("id") == id && page.getLong("offset") == offset &&
        size in 0..50L * 1024 * 1024 && (expectedSize == null || expectedSize == size) && byteCount <= 65536 &&
        next == offset + byteCount && next <= size && (next > offset || next == size))
    return next
}

internal fun openDownloadedDocument(context: Context, record: RemoteDownloadRecord, share: Boolean) {
    runCatching {
        val uri = Uri.parse(record.targetUri)
        require(record.status == RemoteDownloadStatus.COMPLETED && uri.scheme == "content")
        val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(record.filename.substringAfterLast('.', "").lowercase())
            ?: context.contentResolver.getType(uri) ?: "application/octet-stream"
        val intent = if (share) android.content.Intent(android.content.Intent.ACTION_SEND).setType(mime)
            .putExtra(android.content.Intent.EXTRA_STREAM, uri) else android.content.Intent(android.content.Intent.ACTION_VIEW).setDataAndType(uri, mime)
        intent.addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
        intent.clipData = android.content.ClipData.newRawUri(record.filename, uri)
        context.startActivity(if (share) android.content.Intent.createChooser(intent, null) else intent)
    }.onFailure {
        android.widget.Toast.makeText(context, dev.termlink.harness.R.string.download_open_failed, android.widget.Toast.LENGTH_LONG).show()
    }
}

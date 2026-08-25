package dev.termlink.product

import android.annotation.SuppressLint
import android.content.Context
import dev.termlink.model.PcSummary
import java.time.Instant
import java.security.MessageDigest
import java.util.Locale
import java.util.UUID
import java.util.Base64
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.json.JSONArray
import org.json.JSONObject

enum class RemoteFileKind(val wireValue: String) {
    FILE("file"),
    DIRECTORY("directory");

    companion object {
        fun fromWireValue(value: String): RemoteFileKind =
            entries.firstOrNull { it.wireValue == value.lowercase(Locale.ROOT) }
                ?: error("Unknown remote file kind")
    }
}

data class RemoteFileEntry(
    val name: String,
    val path: String,
    val kind: RemoteFileKind,
    val sizeBytes: Long? = null,
    val modifiedAt: Instant? = null,
    val attributes: Set<String> = emptySet(),
)

data class RemoteDirectoryPage(
    val currentPath: String,
    val parentPath: String?,
    val entries: List<RemoteFileEntry>,
    val nextOffset: Long?,
)

internal data class RemoteFileOperationResponse(
    val id: UUID,
    val status: String,
    val result: JsonObject?,
    val error: String?,
    val download: JsonObject?,
)

/** Parses the shared create/get/delete envelope; DOWNLOAD metadata remains top-level. */
internal fun remoteFileOperationResponseFromJson(root: JsonObject): RemoteFileOperationResponse {
    fun JsonObject.requiredString(name: String): String = getValue(name).jsonPrimitive.content
    val operation = root.getValue("file_operation").jsonObject
    val status = operation.requiredString("status")
    require(status in REMOTE_FILE_OPERATION_STATUSES) {
        "The server returned an unknown Windows file-operation state"
    }
    return RemoteFileOperationResponse(
        id = UUID.fromString(operation.requiredString("id")),
        status = status,
        result = operation["result"]?.takeUnless { it is JsonNull }?.jsonObject,
        error = operation["error"]?.jsonPrimitive?.contentOrNull?.take(1024),
        download = root["download"]?.takeUnless { it is JsonNull }?.jsonObject,
    )
}

private val REMOTE_FILE_OPERATION_STATUSES = setOf(
    "PENDING",
    "PROCESSING",
    "SUCCEEDED",
    "FAILED",
    "CANCELLED",
    "EXPIRED",
)

enum class RemoteShellType(val wireValue: String) {
    COMMAND_PROMPT("command-prompt"),
    WINDOWS_POWERSHELL("windows-powershell");

    companion object {
        fun fromWireValue(value: String): RemoteShellType =
            entries.firstOrNull { it.wireValue == value }
                ?: error("Unsupported Windows shell type")
    }
}

/** Maps only server/Agent-authoritative capabilities in a stable UX order. */
internal fun supportedRemoteShellTypes(pc: PcSummary): List<RemoteShellType> =
    RemoteShellType.entries.filter { shell -> shell.wireValue in pc.supportedShells }

data class RemoteDirectoryFavorite(
    val pcId: UUID,
    val pcName: String,
    val directoryName: String,
    val path: String,
)

internal fun isRemoteDirectoryFavorite(
    favorites: List<RemoteDirectoryFavorite>,
    pcId: UUID,
    path: String,
): Boolean {
    val pathKey = remoteFavoritePathKey(path)
    return favorites.any { favorite ->
        favorite.pcId == pcId && remoteFavoritePathKey(favorite.path) == pathKey
    }
}

internal fun toggleRemoteDirectoryFavorite(
    favorites: List<RemoteDirectoryFavorite>,
    favorite: RemoteDirectoryFavorite,
): List<RemoteDirectoryFavorite> {
    require(favorite.path.isNotBlank() && favorite.path.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES)
    val existing = isRemoteDirectoryFavorite(favorites, favorite.pcId, favorite.path)
    val retained = removeRemoteDirectoryFavorite(favorites, favorite)
    return if (existing) retained else (listOf(favorite) + retained).take(MAX_REMOTE_DIRECTORY_FAVORITES)
}

/** Removes a bookmark without the add-on-miss semantics of [toggleRemoteDirectoryFavorite]. */
internal fun removeRemoteDirectoryFavorite(
    favorites: List<RemoteDirectoryFavorite>,
    favorite: RemoteDirectoryFavorite,
): List<RemoteDirectoryFavorite> {
    val pathKey = remoteFavoritePathKey(favorite.path)
    return favorites.filterNot { item ->
        item.pcId == favorite.pcId && remoteFavoritePathKey(item.path) == pathKey
    }
}

internal fun remoteDirectoryFavoriteAccountScope(serverUrl: String?, accountLabel: String?): String? {
    val server = serverUrl?.trim()?.takeIf(String::isNotEmpty) ?: return null
    val account = accountLabel?.trim()?.takeIf(String::isNotEmpty) ?: return null
    return "$server\n$account"
}

private fun remoteFavoritePathKey(path: String): String =
    path.replace('/', '\\').trimEnd('\\').lowercase(Locale.ROOT)

internal interface RemoteDirectoryFavoriteStore {
    fun load(accountScope: String): List<RemoteDirectoryFavorite>
    fun save(accountScope: String, favorites: List<RemoteDirectoryFavorite>)
}

@SuppressLint("ApplySharedPref", "UseKtx")
internal class SharedPreferencesRemoteDirectoryFavoriteStore(context: Context) : RemoteDirectoryFavoriteStore {
    private val preferences = context.applicationContext.getSharedPreferences(
        REMOTE_DIRECTORY_FAVORITES_PREFERENCES,
        Context.MODE_PRIVATE,
    )

    override fun load(accountScope: String): List<RemoteDirectoryFavorite> {
        val raw = preferences.getString(storageKey(accountScope), null) ?: return emptyList()
        return runCatching {
            val array = JSONArray(raw)
            buildList {
                repeat(array.length().coerceAtMost(MAX_REMOTE_DIRECTORY_FAVORITES)) { index ->
                    val item = array.getJSONObject(index)
                    val favorite = RemoteDirectoryFavorite(
                        pcId = UUID.fromString(item.getString("pc_id")),
                        pcName = item.getString("pc_name").take(256),
                        directoryName = item.getString("directory_name").take(256),
                        path = item.getString("path").take(REMOTE_FILE_PATH_MAX_BYTES),
                    )
                    if (favorite.path.isNotBlank() &&
                        !isRemoteDirectoryFavorite(this, favorite.pcId, favorite.path)
                    ) add(favorite)
                }
            }
        }.getOrElse { emptyList() }
    }

    override fun save(accountScope: String, favorites: List<RemoteDirectoryFavorite>) {
        val array = JSONArray()
        favorites.take(MAX_REMOTE_DIRECTORY_FAVORITES).forEach { favorite ->
            array.put(
                JSONObject().apply {
                    put("pc_id", favorite.pcId.toString())
                    put("pc_name", favorite.pcName.take(256))
                    put("directory_name", favorite.directoryName.take(256))
                    put("path", favorite.path.take(REMOTE_FILE_PATH_MAX_BYTES))
                },
            )
        }
        check(preferences.edit().putString(storageKey(accountScope), array.toString()).commit()) {
            "Remote directory favorites could not be persisted"
        }
    }

    private fun storageKey(accountScope: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(accountScope.encodeToByteArray())
        return REMOTE_DIRECTORY_FAVORITES_KEY_PREFIX + digest.joinToString("") { byte ->
            "%02x".format(byte.toInt() and 0xff)
        }
    }

    private companion object {
        const val REMOTE_DIRECTORY_FAVORITES_PREFERENCES = "termlink_remote_directory_favorites"
        const val REMOTE_DIRECTORY_FAVORITES_KEY_PREFIX = "favorites_v1_"
    }
}

data class RemoteFileDownloadSelection(
    val pcId: UUID,
    val pcName: String,
    val remotePath: String,
    val suggestedFilename: String,
    val directoryZip: Boolean,
    val retryRecordId: UUID? = null,
)

internal fun remoteDownloadSelection(
    pcId: UUID,
    pcName: String,
    entry: RemoteFileEntry,
): RemoteFileDownloadSelection {
    val fallback = if (entry.kind == RemoteFileKind.DIRECTORY) "directory" else "download"
    val safeName = truncateRemoteUtf8(
        entry.name
            .replace(Regex("[\\/\\u0000-\\u001f\\u007f]"), "_")
            .trim()
            .trimEnd('.')
            .ifBlank { fallback },
        maxBytes = 240,
    ).ifBlank { fallback }
    val directoryZip = entry.kind == RemoteFileKind.DIRECTORY
    val filename = if (directoryZip && !safeName.endsWith(".zip", ignoreCase = true)) {
        "$safeName.zip"
    } else {
        safeName
    }
    return RemoteFileDownloadSelection(pcId, pcName, entry.path, filename, directoryZip)
}

enum class RemoteDownloadStatus {
    PREPARING,
    DOWNLOADING,
    VERIFYING,
    COMPLETED,
    FAILED,
    CANCELLED;

    val isActive: Boolean
        get() = this == PREPARING || this == DOWNLOADING || this == VERIFYING

    val isTerminal: Boolean
        get() = !isActive
}

data class RemoteDownloadRecord(
    val id: UUID,
    val pcId: UUID,
    val pcName: String,
    val remotePath: String,
    val filename: String,
    val directoryZip: Boolean,
    val status: RemoteDownloadStatus,
    val targetUri: String,
    val operationId: UUID? = null,
    val totalBytes: Long? = null,
    val downloadedBytes: Long = 0,
    val createdAt: Instant = Instant.now(),
    val updatedAt: Instant = createdAt,
    val error: String? = null,
)

internal fun restoreRemoteDownloadRecords(
    records: List<RemoteDownloadRecord>,
    now: Instant = Instant.now(),
): List<RemoteDownloadRecord> = records.map { record ->
    if (record.status.isActive) {
        record.copy(
            status = RemoteDownloadStatus.FAILED,
            updatedAt = now,
            error = "The download was interrupted when the Android app restarted",
        )
    } else {
        record
    }
}

/** Metadata-only removal. The SAF document named by targetUri is deliberately untouched. */
internal fun removeRemoteDownloadRecord(
    records: List<RemoteDownloadRecord>,
    recordId: UUID,
): List<RemoteDownloadRecord> = records.filterNot { it.id == recordId }

internal data class RemoteDownloadHttpRequest(
    val url: String,
    val method: String,
    val headers: Map<String, List<String>>,
)

internal data class RemoteDownloadDescriptor(
    val filename: String,
    val sizeBytes: Long,
    val sha256: ByteArray,
    val directoryZip: Boolean,
    val request: RemoteDownloadHttpRequest,
)

internal fun remoteDownloadDescriptorFromJson(value: JsonObject): RemoteDownloadDescriptor {
    fun JsonObject.requiredString(name: String): String = getValue(name).jsonPrimitive.content
    val filename = value.requiredString("filename")
    require(filename.isNotBlank() && filename.encodeToByteArray().size <= 255 &&
        filename.none { it == '/' || it == '\\' || it == '\u0000' || it == '\r' || it == '\n' }
    ) { "The Windows PC returned an invalid download filename" }
    val size = value.getValue("size_bytes").jsonPrimitive.long
    require(size in 0..MAX_REMOTE_FILE_TRANSFER_BYTES) { "The Windows download is too large" }
    val digest = Base64.getUrlDecoder().decode(value.requiredString("sha256"))
    require(digest.size == 32) { "The Windows PC returned an invalid download digest" }
    val request = value.getValue("request").jsonObject
    request["expires_at"]?.takeUnless { it is JsonNull }?.jsonPrimitive?.contentOrNull?.let(Instant::parse)
    val headers = request["headers"]?.jsonObject?.mapValues { (_, headerValue) ->
        if (headerValue is JsonArray) {
            headerValue.map { it.jsonPrimitive.content }
        } else {
            // Compatibility with an older single-string fixture. Production Go
            // responses encode every header value as an array.
            listOf(headerValue.jsonPrimitive.content)
        }
    }.orEmpty()
    return RemoteDownloadDescriptor(
        filename = filename,
        sizeBytes = size,
        sha256 = digest,
        directoryZip = value.getValue("directory_zip").jsonPrimitive.boolean,
        request = RemoteDownloadHttpRequest(
            url = request.requiredString("url"),
            method = request.requiredString("method"),
            headers = headers,
        ),
    )
}

private fun truncateRemoteUtf8(value: String, maxBytes: Int): String {
    require(maxBytes > 0)
    var bytes = 0
    var end = 0
    while (end < value.length) {
        val codePoint = Character.codePointAt(value, end)
        val charCount = Character.charCount(codePoint)
        val encodedSize = String(Character.toChars(codePoint)).encodeToByteArray().size
        if (bytes + encodedSize > maxBytes) break
        bytes += encodedSize
        end += charCount
    }
    return value.substring(0, end)
}

internal interface RemoteDownloadRecordStore {
    fun load(): List<RemoteDownloadRecord>
    fun save(records: List<RemoteDownloadRecord>)
    fun clear()
}

@SuppressLint("ApplySharedPref", "UseKtx")
internal class SharedPreferencesRemoteDownloadRecordStore(context: Context) : RemoteDownloadRecordStore {
    private val preferences = context.applicationContext.getSharedPreferences(
        REMOTE_DOWNLOAD_PREFERENCES,
        Context.MODE_PRIVATE,
    )

    override fun load(): List<RemoteDownloadRecord> {
        val raw = preferences.getString(REMOTE_DOWNLOAD_RECORDS_KEY, null) ?: return emptyList()
        return runCatching {
            val array = JSONArray(raw)
            buildList {
                repeat(array.length()) { index ->
                    val item = array.getJSONObject(index)
                    add(
                        RemoteDownloadRecord(
                            id = UUID.fromString(item.getString("id")),
                            pcId = UUID.fromString(item.getString("pc_id")),
                            pcName = item.getString("pc_name").take(256),
                            remotePath = item.getString("remote_path").take(4096),
                            filename = item.getString("filename").take(255),
                            directoryZip = item.getBoolean("directory_zip"),
                            status = RemoteDownloadStatus.valueOf(item.getString("status")),
                            targetUri = item.getString("target_uri"),
                            operationId = item.optString("operation_id").takeIf(String::isNotBlank)?.let(UUID::fromString),
                            totalBytes = item.optLong("total_bytes", -1L).takeIf { it >= 0L },
                            downloadedBytes = item.optLong("downloaded_bytes", 0L).coerceAtLeast(0L),
                            createdAt = Instant.parse(item.getString("created_at")),
                            updatedAt = Instant.parse(item.getString("updated_at")),
                            error = item.optString("error").takeIf(String::isNotBlank)?.take(1024),
                        ),
                    )
                }
            }
        }.getOrElse { emptyList() }
    }

    override fun save(records: List<RemoteDownloadRecord>) {
        val array = JSONArray()
        records.take(MAX_REMOTE_DOWNLOAD_RECORDS).forEach { record ->
            array.put(
                JSONObject().apply {
                    put("id", record.id.toString())
                    put("pc_id", record.pcId.toString())
                    put("pc_name", record.pcName)
                    put("remote_path", record.remotePath)
                    put("filename", record.filename)
                    put("directory_zip", record.directoryZip)
                    put("status", record.status.name)
                    put("target_uri", record.targetUri)
                    put("operation_id", record.operationId?.toString() ?: "")
                    put("total_bytes", record.totalBytes ?: -1L)
                    put("downloaded_bytes", record.downloadedBytes)
                    put("created_at", record.createdAt.toString())
                    put("updated_at", record.updatedAt.toString())
                    put("error", record.error ?: "")
                },
            )
        }
        check(
            preferences.edit().putString(REMOTE_DOWNLOAD_RECORDS_KEY, array.toString()).commit(),
        ) { "Remote download metadata could not be persisted" }
    }

    override fun clear() {
        preferences.edit().remove(REMOTE_DOWNLOAD_RECORDS_KEY).commit()
    }

    private companion object {
        const val REMOTE_DOWNLOAD_PREFERENCES = "termlink_remote_downloads"
        const val REMOTE_DOWNLOAD_RECORDS_KEY = "records_v1"
    }
}

internal const val MAX_REMOTE_DOWNLOAD_RECORDS = 100
internal const val MAX_REMOTE_DIRECTORY_FAVORITES = 100
private const val REMOTE_FILE_PATH_MAX_BYTES = 4_096
internal const val MAX_REMOTE_FILE_TRANSFER_BYTES = 5_368_709_120L

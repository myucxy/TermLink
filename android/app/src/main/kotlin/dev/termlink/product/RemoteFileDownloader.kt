package dev.termlink.product

import android.content.ContentResolver
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.URI
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request

internal class RemoteFileDownloader(
    private val contentResolver: ContentResolver,
    client: OkHttpClient,
    private val allowCleartextForLocalTest: Boolean,
) {
    private val client = client.newBuilder()
        .followRedirects(true)
        .followSslRedirects(true)
        .build()

    suspend fun download(
        descriptor: RemoteDownloadDescriptor,
        destination: Uri,
        onProgress: (Long) -> Unit,
        onVerifying: () -> Unit,
    ) = withContext(Dispatchers.IO) {
        require(destination.scheme == ContentResolver.SCHEME_CONTENT) {
            "Remote downloads require an Android document URI"
        }
        require(descriptor.sizeBytes in 0..MAX_REMOTE_FILE_TRANSFER_BYTES) {
            "Remote download size is out of bounds"
        }
        require(descriptor.sha256.size == 32) { "Remote download SHA-256 is invalid" }
        require(descriptor.request.method.equals("GET", ignoreCase = true)) {
            "Remote download method must be GET"
        }
        require(remoteFileDownloadUriAllowed(URI(descriptor.request.url), allowCleartextForLocalTest)) {
            "Remote downloads must use HTTPS"
        }

        val request = Request.Builder()
            .url(descriptor.request.url)
            .get()
            .apply {
                descriptor.request.headers.forEach { (name, values) ->
                    if (!name.equals("host", ignoreCase = true) &&
                        !name.equals("content-length", ignoreCase = true)
                    ) {
                        values.forEach { value -> addHeader(name, value) }
                    }
                }
            }
            .build()
        val call = client.newCall(request)
        val cancellation = currentCoroutineContext()[Job]?.invokeOnCompletion { cause ->
            if (cause != null) call.cancel()
        }
        try {
            call.execute().use { response ->
                check(response.isSuccessful) { "Remote download returned HTTP ${response.code}" }
                require(
                    remoteFileDownloadUriAllowed(
                        URI(response.request.url.toString()),
                        allowCleartextForLocalTest,
                    ),
                ) { "Remote download redirected to an unsafe URL" }
                val body = response.body ?: error("Remote download was empty")
                val declaredLength = body.contentLength()
                require(declaredLength < 0 || declaredLength == descriptor.sizeBytes) {
                    "Remote download size mismatch"
                }
                val output = contentResolver.openOutputStream(destination, "wt")
                    ?: error("The selected Android document cannot be opened")
                output.buffered().use { target ->
                    body.byteStream().buffered().use { source ->
                        copyRemoteDownloadAndVerify(
                            input = source,
                            output = target,
                            expectedSize = descriptor.sizeBytes,
                            expectedSha256 = descriptor.sha256,
                            onProgress = onProgress,
                            onVerifying = onVerifying,
                        )
                    }
                }
            }
        } finally {
            cancellation?.dispose()
        }
    }

    internal fun cleanupIncompleteDocument(uri: Uri) {
        cleanupIncompleteRemoteDocument(contentResolver, uri)
    }

    internal fun releasePersistedPermission(uri: Uri) {
        releaseRemoteDocumentPermission(contentResolver, uri)
    }
}

/** Deletes an incomplete SAF document, or truncates it when its provider cannot delete. */
internal fun cleanupIncompleteRemoteDocument(contentResolver: ContentResolver, uri: Uri) {
    val deleted = runCatching {
        DocumentsContract.deleteDocument(contentResolver, uri)
    }.getOrDefault(false)
    if (!deleted) {
        runCatching { contentResolver.openOutputStream(uri, "wt")?.use { } }
    }
}

/** A terminal download no longer needs a durable grant; releasing it never deletes the document. */
internal fun releaseRemoteDocumentPermission(contentResolver: ContentResolver, uri: Uri) {
    listOf(Intent.FLAG_GRANT_READ_URI_PERMISSION, Intent.FLAG_GRANT_WRITE_URI_PERMISSION).forEach { flag ->
        runCatching { contentResolver.releasePersistableUriPermission(uri, flag) }
    }
}

internal suspend fun copyRemoteDownloadAndVerify(
    input: InputStream,
    output: OutputStream,
    expectedSize: Long,
    expectedSha256: ByteArray,
    onProgress: (Long) -> Unit = {},
    onVerifying: () -> Unit = {},
    bufferSize: Int = 128 * 1024,
) {
    require(expectedSize in 0..MAX_REMOTE_FILE_TRANSFER_BYTES) { "Remote download size is out of bounds" }
    require(expectedSha256.size == 32) { "Remote download SHA-256 is invalid" }
    require(bufferSize > 0) { "Download buffer size must be positive" }
    val digest = MessageDigest.getInstance("SHA-256")
    val buffer = ByteArray(bufferSize)
    var total = 0L
    onProgress(0L)
    while (true) {
        currentCoroutineContext().ensureActive()
        val count = try {
            input.read(buffer)
        } catch (error: IOException) {
            throw RemoteFileDownloadException("Remote download was interrupted", error)
        }
        if (count < 0) break
        if (count == 0) continue
        total = Math.addExact(total, count.toLong())
        if (total > expectedSize) {
            throw RemoteFileDownloadException("Remote download exceeded its declared size")
        }
        output.write(buffer, 0, count)
        digest.update(buffer, 0, count)
        onProgress(total)
    }
    output.flush()
    if (total != expectedSize) {
        throw RemoteFileDownloadException("Remote download ended before its declared size")
    }
    onVerifying()
    if (!MessageDigest.isEqual(digest.digest(), expectedSha256)) {
        throw RemoteFileDownloadException("Remote download SHA-256 verification failed")
    }
}

internal fun remoteFileDownloadUriAllowed(uri: URI, allowCleartextForLocalTest: Boolean): Boolean {
    val scheme = uri.scheme?.lowercase()
    return !uri.host.isNullOrBlank() && uri.rawUserInfo == null &&
        (scheme == "https" || allowCleartextForLocalTest && scheme == "http")
}

internal class RemoteFileDownloadException(message: String, cause: Throwable? = null) :
    Exception(message, cause)

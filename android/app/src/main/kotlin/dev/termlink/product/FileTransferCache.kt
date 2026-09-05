package dev.termlink.product

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal data class FileTransferCommit(
    val committedOffset: Long,
    val acknowledged: Boolean,
)

/**
 * Owns every Android-side transfer temporary under cacheDir/file-transfers.
 * SAF is touched only after the private .part file has reached the declared
 * length and passed SHA-256 verification.
 */
internal class FileTransferCache(
    context: Context,
    private val contentResolver: ContentResolver = context.contentResolver,
) {
    internal val root: File = File(context.cacheDir, "file-transfers").canonicalFile

    init {
        root.mkdirs()
        require(root.isDirectory) { "File-transfer cache directory is unavailable" }
    }

    fun partFile(transferId: UUID): File {
        return transferFile(transferId, PART_FILENAME)
    }

    suspend fun committedOffset(transferId: UUID, expectedSize: Long): Long = withContext(Dispatchers.IO) {
        require(expectedSize in 0..MAX_REMOTE_FILE_TRANSFER_BYTES)
        val part = partFile(transferId)
        if (!part.exists()) {
            if (expectedSize == 0L) {
                RandomAccessFile(part, "rw").use { it.fd.sync() }
            }
            return@withContext 0L
        }
        val length = part.length()
        if (length <= expectedSize) length else {
            RandomAccessFile(part, "rw").use { it.setLength(0L); it.fd.sync() }
            0L
        }
    }

    suspend fun commitChunk(
        transferId: UUID,
        expectedSize: Long,
        offset: Long,
        bytes: ByteArray,
    ): FileTransferCommit = withContext(Dispatchers.IO) {
        require(expectedSize in 0..MAX_REMOTE_FILE_TRANSFER_BYTES)
        require(offset >= 0L && bytes.isNotEmpty() && bytes.size <= FILE_TRANSFER_CHUNK_BYTES)
        val part = partFile(transferId)
        RandomAccessFile(part, "rw").use { file ->
            var committed = file.length()
            if (committed > expectedSize) {
                file.setLength(0L)
                file.fd.sync()
                committed = 0L
            }
            val end = Math.addExact(offset, bytes.size.toLong())
            require(end <= expectedSize) { "File-transfer chunk exceeds its declared size" }
            when {
                offset == committed -> {
                    file.seek(offset)
                    file.write(bytes)
                    file.fd.sync()
                    FileTransferCommit(end, acknowledged = true)
                }
                offset > committed || end > committed ->
                    FileTransferCommit(committed, acknowledged = false)
                else -> {
                    val existing = ByteArray(bytes.size)
                    file.seek(offset)
                    file.readFully(existing)
                    val same = MessageDigest.isEqual(existing, bytes)
                    existing.fill(0)
                    require(same) { "A repeated file-transfer chunk changed bytes" }
                    FileTransferCommit(committed, acknowledged = true)
                }
            }
        }
    }

    suspend fun verify(
        transferId: UUID,
        expectedSize: Long,
        expectedSha256: ByteArray,
    ) = withContext(Dispatchers.IO) {
        require(expectedSha256.size == 32)
        val part = partFile(transferId)
        require(part.length() == expectedSize) { "File transfer is incomplete" }
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(128 * 1024)
        FileInputStream(part).buffered().use { input ->
            while (true) {
                currentCoroutineContext().ensureActive()
                val count = input.read(buffer)
                if (count < 0) break
                if (count > 0) digest.update(buffer, 0, count)
            }
        }
        buffer.fill(0)
        if (!MessageDigest.isEqual(digest.digest(), expectedSha256)) {
            // A full-length but corrupt prefix must never be advertised as a
            // resumable complete file on the next explicit retry.
            RandomAccessFile(part, "rw").use { file ->
                file.setLength(0L)
                file.fd.sync()
            }
            throw IllegalArgumentException("File-transfer SHA-256 verification failed")
        }
    }

    suspend fun copyToDocument(
        transferId: UUID,
        destination: Uri,
        expectedSize: Long,
        onProgress: (Long) -> Unit,
    ) = withContext(Dispatchers.IO) {
        require(destination.scheme == ContentResolver.SCHEME_CONTENT) {
            "Remote downloads require an Android document URI"
        }
        val part = partFile(transferId)
        require(part.length() == expectedSize) { "File transfer is incomplete" }
        val output = contentResolver.openOutputStream(destination, "wt")
            ?: error("The selected Android document cannot be opened")
        val buffer = ByteArray(128 * 1024)
        var copied = 0L
        FileInputStream(part).buffered().use { input ->
            output.buffered().use { target ->
                while (true) {
                    currentCoroutineContext().ensureActive()
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (count == 0) continue
                    target.write(buffer, 0, count)
                    copied = Math.addExact(copied, count.toLong())
                    require(copied <= expectedSize) { "File transfer exceeded its declared size" }
                    onProgress(copied)
                }
                target.flush()
            }
        }
        buffer.fill(0)
        require(copied == expectedSize) { "File transfer ended before its declared size" }
    }

    suspend fun isDocumentCommitted(
        transferId: UUID,
        destination: Uri,
        expectedSize: Long,
        expectedSha256: ByteArray,
    ): Boolean = withContext(Dispatchers.IO) {
        require(expectedSize in 0..MAX_REMOTE_FILE_TRANSFER_BYTES)
        require(expectedSha256.size == 32)
        val marker = transferFile(transferId, COMMITTED_FILENAME)
        if (!marker.isFile || marker.length() != COMMITTED_MARKER_BYTES.toLong()) return@withContext false
        val bytes = marker.readBytes()
        try {
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
            val magic = ByteArray(COMMITTED_MAGIC.size).also { buffer.get(it) }
            val size = buffer.long
            val fileHash = ByteArray(32).also { buffer.get(it) }
            val destinationHash = ByteArray(32).also { buffer.get(it) }
            val expectedDestinationHash = destinationDigest(destination)
            try {
                magic.contentEquals(COMMITTED_MAGIC) && size == expectedSize &&
                    MessageDigest.isEqual(fileHash, expectedSha256) &&
                    MessageDigest.isEqual(destinationHash, expectedDestinationHash)
            } finally {
                magic.fill(0)
                fileHash.fill(0)
                destinationHash.fill(0)
                expectedDestinationHash.fill(0)
            }
        } finally {
            bytes.fill(0)
        }
    }

    suspend fun markDocumentCommitted(
        transferId: UUID,
        destination: Uri,
        expectedSize: Long,
        expectedSha256: ByteArray,
    ) = withContext(Dispatchers.IO) {
        require(expectedSize in 0..MAX_REMOTE_FILE_TRANSFER_BYTES)
        require(expectedSha256.size == 32)
        val directory = transferDirectory(transferId, create = true)
        val marker = transferFile(transferId, COMMITTED_FILENAME)
        val staging = transferFile(transferId, "$COMMITTED_FILENAME.tmp")
        val destinationHash = destinationDigest(destination)
        val bytes = ByteBuffer.allocate(COMMITTED_MARKER_BYTES).order(ByteOrder.BIG_ENDIAN)
            .put(COMMITTED_MAGIC)
            .putLong(expectedSize)
            .put(expectedSha256)
            .put(destinationHash)
            .array()
        try {
            if (staging.exists()) check(staging.delete()) { "Stale file-transfer commit marker could not be removed" }
            FileOutputStream(staging).use { output ->
                output.write(bytes)
                output.flush()
                output.fd.sync()
            }
            if (marker.exists()) check(marker.delete()) { "File-transfer commit marker could not be replaced" }
            check(staging.renameTo(marker)) { "File-transfer commit marker could not be committed" }
        } finally {
            destinationHash.fill(0)
            bytes.fill(0)
            if (staging.exists()) staging.delete()
        }
    }

    fun cleanup(transferId: UUID) {
        val directory = transferDirectory(transferId, create = false)
        if (!directory.exists()) return
        require(directory.isDirectory) { "File-transfer cache task path is not a directory" }
        val entries = directory.listFiles().orEmpty()
        require(entries.all { entry ->
            !Files.isSymbolicLink(entry.toPath()) && entry.canonicalFile.parentFile == directory &&
                !entry.isDirectory && entry.name in SAFE_TASK_FILENAMES
        }) { "File-transfer cache contains an unexpected or unsafe entry" }
        entries.forEach { entry ->
            check(entry.delete()) { "File-transfer cache entry could not be removed" }
        }
        check(directory.delete()) {
            "File-transfer cache directory could not be removed"
        }
    }

    private fun transferDirectory(transferId: UUID, create: Boolean): File {
        require(transferId != UUID(0, 0)) { "Transfer ID is required" }
        val candidate = File(root, transferId.toString())
        require(!Files.isSymbolicLink(candidate.toPath())) { "File-transfer cache task path is a symbolic link" }
        val directory = candidate.canonicalFile
        require(directory.parentFile == root) { "File-transfer cache path escaped its root" }
        if (create) {
            directory.mkdirs()
            require(directory.isDirectory) { "File-transfer cache directory is unavailable" }
        }
        return directory
    }

    private fun transferFile(transferId: UUID, filename: String): File {
        require(filename in SAFE_TASK_FILENAMES) { "File-transfer cache filename is invalid" }
        val directory = transferDirectory(transferId, create = true)
        val candidate = File(directory, filename)
        require(!Files.isSymbolicLink(candidate.toPath())) { "File-transfer cache file is a symbolic link" }
        val file = candidate.canonicalFile
        require(file.parentFile == directory) { "File-transfer cache file escaped its task directory" }
        return file
    }

    private fun destinationDigest(destination: Uri): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(destination.toString().encodeToByteArray())

    private companion object {
        const val PART_FILENAME = "payload.part"
        const val COMMITTED_FILENAME = "document.committed"
        val SAFE_TASK_FILENAMES = setOf(PART_FILENAME, COMMITTED_FILENAME, "$COMMITTED_FILENAME.tmp")
        val COMMITTED_MAGIC = "TLFTDOC2".encodeToByteArray()
        const val COMMITTED_MARKER_BYTES = 8 + 8 + 32 + 32
    }
}

internal const val FILE_TRANSFER_CHUNK_BYTES = 256 * 1024

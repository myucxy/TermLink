package dev.termlink.product

import android.content.Context
import android.graphics.Typeface
import android.net.Uri
import android.provider.OpenableColumns
import dev.termlink.terminal.api.TerminalTypefaceResolver
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileOutputStream
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.UUID

internal data class ImportedTerminalFont(
    val displayName: String,
    val sha256: String,
)

internal enum class TerminalFontImportFailure {
    EMPTY,
    TOO_LARGE,
    UNSUPPORTED_FORMAT,
    INVALID_FONT,
    UNREADABLE,
}

internal class TerminalFontImportException(
    val failure: TerminalFontImportFailure,
    cause: Throwable? = null,
) : IllegalArgumentException(failure.name, cause)

internal object TerminalFontStore {
    const val MAX_FONT_BYTES = 16L * 1024L * 1024L

    val acceptedMimeTypes = arrayOf(
        "font/ttf",
        "font/otf",
        "application/x-font-ttf",
        "application/vnd.ms-opentype",
        "application/octet-stream",
    )

    fun hasCustomFont(context: Context): Boolean = customFontFile(context).isFile

    fun removeCustomFont(context: Context): Boolean {
        val file = customFontFile(context)
        val removed = !file.exists() || file.delete()
        if (removed) TerminalTypefaceResolver.invalidateCustomFont()
        return removed
    }

    fun importFont(context: Context, uri: Uri): ImportedTerminalFont {
        val resolver = context.contentResolver
        val declaredSize = resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getLong(0) else null
        }
        val displayName = runCatching { queryDisplayName(context, uri) }
            .getOrDefault(normalizeTerminalFontDisplayName(null))
        if (declaredSize != null && declaredSize > MAX_FONT_BYTES) {
            throw TerminalFontImportException(TerminalFontImportFailure.TOO_LARGE)
        }

        val destination = customFontFile(context)
        val parent = destination.parentFile
            ?: throw TerminalFontImportException(TerminalFontImportFailure.UNREADABLE)
        if (!parent.exists() && !parent.mkdirs()) {
            throw TerminalFontImportException(TerminalFontImportFailure.UNREADABLE)
        }
        val temporary = File(parent, ".font-import-${UUID.randomUUID()}.tmp")
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            val input = resolver.openInputStream(uri)
                ?: throw TerminalFontImportException(TerminalFontImportFailure.UNREADABLE)
            var total = 0L
            BufferedInputStream(input).use { source ->
                BufferedOutputStream(FileOutputStream(temporary)).use { target ->
                    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                    while (true) {
                        val read = source.read(buffer)
                        if (read < 0) break
                        if (read == 0) continue
                        total += read
                        if (total > MAX_FONT_BYTES) {
                            throw TerminalFontImportException(TerminalFontImportFailure.TOO_LARGE)
                        }
                        digest.update(buffer, 0, read)
                        target.write(buffer, 0, read)
                    }
                }
            }
            if (total == 0L) throw TerminalFontImportException(TerminalFontImportFailure.EMPTY)
            if (!hasSupportedOpenTypeHeader(temporary.readBytes(MAX_FONT_HEADER_BYTES))) {
                throw TerminalFontImportException(TerminalFontImportFailure.UNSUPPORTED_FORMAT)
            }
            runCatching { Typeface.Builder(temporary).build() }
                .getOrElse { throw TerminalFontImportException(TerminalFontImportFailure.INVALID_FONT, it) }

            moveAtomicallyOrReplace(temporary, destination)
            TerminalTypefaceResolver.invalidateCustomFont()
            return ImportedTerminalFont(
                displayName = displayName,
                sha256 = digest.digest().joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) },
            )
        } catch (error: TerminalFontImportException) {
            throw error
        } catch (error: Exception) {
            throw TerminalFontImportException(TerminalFontImportFailure.UNREADABLE, error)
        } finally {
            if (temporary.exists()) temporary.delete()
        }
    }

    private fun customFontFile(context: Context): File = TerminalTypefaceResolver.customFontFile(context)

    private fun queryDisplayName(context: Context, uri: Uri): String {
        val candidate = context.contentResolver
            .query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { cursor -> if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getString(0) else null }
        return normalizeTerminalFontDisplayName(candidate)
    }

    private fun moveAtomicallyOrReplace(source: File, destination: File) {
        try {
            Files.move(
                source.toPath(),
                destination.toPath(),
                StandardCopyOption.ATOMIC_MOVE,
                StandardCopyOption.REPLACE_EXISTING,
            )
        } catch (_: AtomicMoveNotSupportedException) {
            Files.move(source.toPath(), destination.toPath(), StandardCopyOption.REPLACE_EXISTING)
        }
    }
}

internal fun normalizeTerminalFontDisplayName(value: String?): String =
    value?.substringAfterLast('/')?.substringAfterLast('\\')?.trim()?.take(96)?.takeIf(String::isNotEmpty)
        ?: "Custom font"

internal fun hasSupportedOpenTypeHeader(header: ByteArray): Boolean {
    if (header.size < MAX_FONT_HEADER_BYTES) return false
    return header.contentEquals(byteArrayOf(0x00, 0x01, 0x00, 0x00)) ||
        header.decodeToString() == "OTTO" ||
        header.decodeToString() == "true" ||
        header.decodeToString() == "typ1"
}

private fun File.readBytes(limit: Int): ByteArray = inputStream().use { input ->
    ByteArray(limit).also { target ->
        var offset = 0
        while (offset < target.size) {
            val read = input.read(target, offset, target.size - offset)
            if (read < 0) return@use target.copyOf(offset)
            offset += read
        }
    }
}

private const val MAX_FONT_HEADER_BYTES = 4

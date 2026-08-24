package dev.termlink.product

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.pm.PackageInfoCompat
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response

/** Downloads an AppShip APK and treats every network and archive field as untrusted. */
internal class ProductUpdateDownloader(
    context: Context,
    client: OkHttpClient,
    private val currentBuild: Long,
) {
    private val appContext = context.applicationContext
    private val downloadClient = client.newBuilder()
        .followRedirects(false)
        .followSslRedirects(false)
        .build()
    private val verifier = ProductApkVerifier(appContext)

    suspend fun download(
        update: ProductUpdateStatus,
        onProgress: (downloadedBytes: Long, totalBytes: Long) -> Unit,
        onVerifying: () -> Unit,
    ): File = withContext(Dispatchers.IO) {
        val downloadUrl = update.downloadUrl
            ?: throw ProductUpdateDownloadException("This update has no APK / 当前更新没有可下载的 APK")
        val expectedSize = update.artifactSizeBytes
            ?: throw ProductUpdateDownloadException("APK size is missing / APK 缺少文件大小")
        val expectedSha256 = update.artifactSha256?.lowercase(Locale.ROOT)
            ?: throw ProductUpdateDownloadException("APK SHA-256 is missing / APK 缺少 SHA-256")
        val expectedBuild = update.buildNumber
            ?: throw ProductUpdateDownloadException("APK build is missing / APK 缺少构建号")
        if (expectedSize <= 0L || !SHA256_PATTERN.matches(expectedSha256) || expectedBuild <= currentBuild) {
            throw ProductUpdateDownloadException("Update metadata is invalid / 更新元数据无效")
        }

        val updateDirectory = File(appContext.cacheDir, UPDATE_DIRECTORY).apply { mkdirs() }
        if (!updateDirectory.isDirectory) {
            throw ProductUpdateDownloadException("Cannot create update cache / 无法创建更新缓存目录")
        }
        val partialFile = File(updateDirectory, "termlink-$expectedBuild.apk.part")
        val finalFile = File(updateDirectory, "termlink-$expectedBuild.apk")

        if (finalFile.exists()) {
            onVerifying()
            if (runCatching {
                    validateDownloadedApk(finalFile, expectedSize, expectedSha256, expectedBuild)
                }.isSuccess
            ) {
                return@withContext finalFile
            }
            finalFile.delete()
        }

        openWithRedirects(downloadUrl, "HEAD").use { head ->
            if (head.code != 200) {
                throw ProductUpdateDownloadException(
                    "Cannot read APK metadata (HTTP ${head.code}) / 无法读取 APK 信息",
                )
            }
            val headLength = head.header("Content-Length")?.toLongOrNull()
            val headDigest = head.header("X-Checksum-SHA256")?.lowercase(Locale.ROOT)
            if (headLength != expectedSize || headDigest != expectedSha256) {
                partialFile.delete()
                throw ProductUpdateDownloadException(
                    "Server APK metadata does not match / 服务端 APK 校验信息不一致",
                )
            }
            val supportsRanges = head.header("Accept-Ranges")
                ?.contains("bytes", ignoreCase = true) == true
            if (!supportsRanges && partialFile.exists()) partialFile.delete()
        }

        var completed = partialFile.takeIf(File::exists)?.length() ?: 0L
        if (completed > expectedSize) {
            partialFile.delete()
            completed = 0L
        }
        onProgress(completed, expectedSize)

        if (completed < expectedSize) {
            var response = openDownload(downloadUrl, completed)
            if (completed > 0L && response.code == 200) {
                response.close()
                partialFile.delete()
                completed = 0L
                onProgress(0L, expectedSize)
                response = openDownload(downloadUrl, 0L)
            }
            response.use { download ->
                if (download.code != 200 && download.code != 206) {
                    throw ProductUpdateDownloadException(
                        "APK download failed (HTTP ${download.code}) / APK 下载失败",
                    )
                }
                if (completed > 0L) {
                    val contentRange = download.header("Content-Range").orEmpty()
                    if (download.code != 206 || !contentRange.startsWith("bytes $completed-")) {
                        throw ProductUpdateDownloadException(
                            "Invalid resume response / APK 断点续传响应无效",
                        )
                    }
                }
                download.header("X-Checksum-SHA256")?.lowercase(Locale.ROOT)?.let { digest ->
                    if (digest != expectedSha256) {
                        throw ProductUpdateDownloadException(
                            "APK checksum header does not match / APK 下载校验头不一致",
                        )
                    }
                }
                val body = download.body
                    ?: throw ProductUpdateDownloadException("APK response is empty / APK 下载响应为空")
                try {
                    body.byteStream().use { input ->
                        FileOutputStream(partialFile, completed > 0L).buffered().use { output ->
                            val buffer = ByteArray(DOWNLOAD_BUFFER_SIZE)
                            var total = completed
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                total += count
                                if (total > expectedSize) {
                                    throw ProductUpdateDownloadException(
                                        "APK is larger than declared / APK 大小超过服务端声明",
                                    )
                                }
                                output.write(buffer, 0, count)
                                onProgress(total, expectedSize)
                            }
                        }
                    }
                } catch (error: ProductUpdateDownloadException) {
                    throw error
                } catch (error: IOException) {
                    throw ProductUpdateDownloadException(
                        "APK download interrupted; retry to resume / APK 下载中断，可重试续传",
                        error,
                    )
                }
            }
        }

        onVerifying()
        try {
            validateUpdateFileIntegrity(partialFile, expectedSize, expectedSha256)
        } catch (error: Throwable) {
            partialFile.delete()
            if (error is ProductUpdateDownloadException) throw error
            throw ProductUpdateDownloadException("APK integrity check failed / APK 完整性校验失败", error)
        }
        if (finalFile.exists() && !finalFile.delete()) {
            throw ProductUpdateDownloadException("Cannot replace cached APK / 无法替换旧的 APK")
        }
        if (!partialFile.renameTo(finalFile)) {
            throw ProductUpdateDownloadException("Cannot save verified APK / 无法保存已校验的 APK")
        }
        try {
            verifier.verify(finalFile, expectedBuild)
        } catch (error: Throwable) {
            finalFile.delete()
            if (error is ProductUpdateDownloadException) throw error
            throw ProductUpdateDownloadException("APK verification failed / APK 校验失败", error)
        }
        finalFile
    }

    private fun validateDownloadedApk(
        file: File,
        expectedSize: Long,
        expectedSha256: String,
        expectedBuild: Long,
    ) {
        validateUpdateFileIntegrity(file, expectedSize, expectedSha256)
        verifier.verify(file, expectedBuild)
    }

    private fun openDownload(url: String, completed: Long): Response = openWithRedirects(
        initialUrl = url,
        method = "GET",
        headers = if (completed > 0L) mapOf("Range" to "bytes=$completed-") else emptyMap(),
    )

    private fun openWithRedirects(
        initialUrl: String,
        method: String,
        headers: Map<String, String> = emptyMap(),
    ): Response {
        var currentUrl = initialUrl
        repeat(MAX_REDIRECTS + 1) { redirectCount ->
            val request = Request.Builder()
                .url(currentUrl)
                .method(method, null)
                .header("User-Agent", "TermLink/$currentBuild Android")
                .header("Accept-Encoding", "identity")
                .apply { headers.forEach { (name, value) -> header(name, value) } }
                .build()
            val response = downloadClient.newCall(request).execute()
            if (response.code !in REDIRECT_CODES) return response
            val location = response.header("Location")
            response.close()
            if (location == null) {
                throw ProductUpdateDownloadException("Redirect has no location / APK 重定向缺少地址")
            }
            if (redirectCount == MAX_REDIRECTS) {
                throw ProductUpdateDownloadException("Too many APK redirects / APK 重定向次数过多")
            }
            currentUrl = AppShipUpdateProtocol.validateHttpsRedirect(currentUrl, location)
        }
        throw ProductUpdateDownloadException("Too many APK redirects / APK 重定向次数过多")
    }

    private companion object {
        const val UPDATE_DIRECTORY = "updates"
        const val DOWNLOAD_BUFFER_SIZE = 64 * 1024
        const val MAX_REDIRECTS = 5
        val REDIRECT_CODES = setOf(301, 302, 303, 307, 308)
        val SHA256_PATTERN = Regex("^[0-9a-f]{64}$")
    }
}

internal class ProductApkVerifier(private val context: Context) {
    fun verify(apk: File, expectedBuild: Long) {
        val packageManager = context.packageManager
        val archive = packageManager.getPackageArchiveInfo(apk.absolutePath, signatureQueryFlags())
            ?: throw ProductUpdateDownloadException("Downloaded file is not an APK / 下载文件不是有效 APK")
        if (archive.packageName != context.packageName) {
            throw ProductUpdateDownloadException("APK package does not match / APK 包名与当前应用不一致")
        }
        val archiveBuild = PackageInfoCompat.getLongVersionCode(archive)
        if (archiveBuild <= currentBuild() || archiveBuild != expectedBuild) {
            throw ProductUpdateDownloadException("APK build does not match / APK 构建号与更新信息不一致")
        }
        val installed = installedPackageInfo(packageManager, context.packageName)
        validateProductSignerDigests(signerDigests(installed), signerDigests(archive))
    }

    private fun currentBuild(): Long {
        val installed = installedPackageInfo(context.packageManager, context.packageName)
        return PackageInfoCompat.getLongVersionCode(installed)
    }

    @Suppress("DEPRECATION")
    private fun installedPackageInfo(packageManager: PackageManager, packageName: String): PackageInfo {
        val flags = signatureQueryFlags()
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            packageManager.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(flags.toLong()))
        } else {
            packageManager.getPackageInfo(packageName, flags)
        }
    }
}

@Suppress("DEPRECATION")
private fun signatureQueryFlags(): Int = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
    PackageManager.GET_SIGNING_CERTIFICATES or PackageManager.GET_SIGNATURES
} else {
    PackageManager.GET_SIGNATURES
}

@Suppress("DEPRECATION")
private fun signerDigests(packageInfo: PackageInfo): Set<String> {
    val currentSigners = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
        packageInfo.signingInfo?.apkContentsSigners.orEmpty()
    } else {
        emptyArray()
    }
    val signatures = currentSigners.takeIf { it.isNotEmpty() } ?: packageInfo.signatures.orEmpty()
    return signatures.mapTo(mutableSetOf()) { signature ->
        MessageDigest.getInstance("SHA-256")
            .digest(signature.toByteArray())
            .joinToString("") { byte -> "%02x".format(byte) }
    }
}

internal fun validateProductSignerDigests(installedSigners: Set<String>, archiveSigners: Set<String>) {
    when {
        installedSigners.isEmpty() -> throw ProductUpdateDownloadException(
            "Cannot read installed app signature / 无法读取当前应用签名",
        )
        archiveSigners.isEmpty() -> throw ProductUpdateDownloadException(
            "Cannot read update APK signature / 无法读取更新 APK 签名",
        )
        installedSigners != archiveSigners -> throw ProductUpdateDownloadException(
            "APK signature does not match this app / APK 签名与当前应用不一致",
        )
    }
}

internal fun validateUpdateFileIntegrity(file: File, expectedSize: Long, expectedSha256: String) {
    if (file.length() != expectedSize) {
        throw ProductUpdateDownloadException("APK size does not match / APK 文件大小不一致")
    }
    if (sha256Hex(file) != expectedSha256.lowercase(Locale.ROOT)) {
        throw ProductUpdateDownloadException("APK SHA-256 does not match / APK 的 SHA-256 校验失败")
    }
}

internal fun sha256Hex(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().buffered().use { input ->
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            digest.update(buffer, 0, count)
        }
    }
    return digest.digest().joinToString("") { byte -> "%02x".format(byte) }
}

internal class ProductUpdateDownloadException(message: String, cause: Throwable? = null) :
    Exception(message, cause)

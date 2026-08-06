package dev.termlink.product

import java.net.URI
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * The public AppShip update contract is deliberately parsed outside the HTTP
 * repository so that response validation stays deterministic and testable.
 * This follows the NuanYa client boundary: the server is treated as an
 * untrusted source and an update is only surfaced after all identity,
 * version, artifact and download-url fields have been checked.
 */
internal object AppShipUpdateProtocol {
    private val json = Json { ignoreUnknownKeys = true }
    private val slugPattern = Regex("^[a-z0-9-]+$")
    private val architecturePattern = Regex("^[A-Za-z0-9._-]{1,40}$")
    private val artifactPath = Regex("^/api/v1/artifacts/[A-Za-z0-9-]{8,64}/download$")
    private val sha256Pattern = Regex("^[0-9a-fA-F]{64}$")

    fun buildCheckUrl(
        baseUrl: String,
        slug: String,
        architecture: String,
        channel: String,
        buildNumber: Long,
    ): String {
        val base = validateBaseUrl(baseUrl)
        require(slugPattern.matches(slug)) { "应用更新标识配置无效" }
        require(channel == "stable" || channel == "beta") { "更新通道配置无效" }
        require(architecturePattern.matches(architecture)) { "设备架构无效" }
        require(buildNumber >= 0) { "当前构建号无效" }
        val query = listOf(
            "platform" to "android",
            "arch" to architecture,
            "channel" to channel,
            "build" to buildNumber.toString(),
        ).joinToString("&") { (key, value) ->
            "${encode(key)}=${encode(value)}"
        }
        return URI(
            "https",
            null,
            base.host,
            base.port,
            "/api/v1/apps/$slug/updates",
            query,
            null,
        ).toASCIIString()
    }

    fun parseAndValidate(
        body: String,
        baseUrl: String,
        expectedSlug: String,
        expectedChannel: String,
        expectedArchitecture: String,
        currentBuild: Long,
    ): ProductUpdateStatus {
        val response = runCatching { json.decodeFromString<UpdateResponseDto>(body) }
            .getOrElse { throw AppShipUpdateException("更新服务返回了无法识别的数据", it) }
        val decision = when (response.status.lowercase()) {
            "no_update" -> ProductUpdateDecision.NO_UPDATE
            "optional" -> ProductUpdateDecision.OPTIONAL
            "required" -> ProductUpdateDecision.REQUIRED
            else -> throw AppShipUpdateException("更新服务返回了未知状态")
        }
        if (response.application != expectedSlug || response.channel != expectedChannel) {
            throw AppShipUpdateException("更新信息与当前应用不匹配")
        }
        val latest = response.latest ?: throw AppShipUpdateException("更新信息缺少版本数据")
        val versionName = latest.versionName?.trim().orEmpty()
        val latestBuild = latest.buildNumber
            ?: throw AppShipUpdateException("更新信息缺少构建号")
        if (versionName.isEmpty() || latestBuild < 0) {
            throw AppShipUpdateException("更新版本数据无效")
        }

        val policy = response.policy ?: throw AppShipUpdateException("更新信息缺少支持策略")
        if (policy.minSupportedBuild != null && policy.minSupportedBuild < 0) {
            throw AppShipUpdateException("最低支持构建号无效")
        }
        if (decision == ProductUpdateDecision.REQUIRED && policy.minSupportedBuild == null) {
            throw AppShipUpdateException("必须更新的信息缺少最低支持构建号")
        }

        val artifact = when (decision) {
            ProductUpdateDecision.NO_UPDATE -> {
                if (response.artifact != null) {
                    throw AppShipUpdateException("无需更新的响应包含了异常制品")
                }
                null
            }
            ProductUpdateDecision.OPTIONAL,
            ProductUpdateDecision.REQUIRED -> {
                if (latestBuild <= currentBuild) {
                    throw AppShipUpdateException("更新构建号没有高于当前版本")
                }
                validateArtifact(response.artifact, baseUrl, expectedArchitecture)
            }
            else -> error("unreachable")
        }

        return ProductUpdateStatus(
            decision = decision,
            versionName = versionName,
            buildNumber = latestBuild,
            releaseNotes = latest.releaseNotes?.trim()?.takeIf { it.isNotEmpty() },
            downloadUrl = artifact?.downloadUrl,
            message = response.message?.trim()?.takeIf { it.isNotEmpty() }
                ?: if (decision == ProductUpdateDecision.NO_UPDATE) "No update available" else null,
            artifactSizeBytes = artifact?.sizeBytes,
            artifactSha256 = artifact?.sha256,
            minSupportedBuild = policy.minSupportedBuild,
        )
    }

    fun normalizeArtifactUrl(baseUrl: String, rawUrl: String): String {
        val base = validateBaseUrl(baseUrl)
        val raw = runCatching { URI(rawUrl.trim()) }
            .getOrElse { throw AppShipUpdateException("更新下载地址无效", it) }
        if (
            raw.userInfo != null ||
            raw.fragment != null ||
            raw.rawQuery != null ||
            !raw.scheme.equals("https", ignoreCase = true) ||
            !artifactPath.matches(raw.path.orEmpty())
        ) {
            throw AppShipUpdateException("更新下载地址不属于 AppShip 制品")
        }
        return URI("https", null, base.host, base.port, raw.path, null, null).toASCIIString()
    }

    fun validateHttpsRedirect(currentUrl: String, location: String): String {
        val resolved = runCatching { URI(currentUrl).resolve(location) }
            .getOrElse { throw AppShipUpdateException("更新下载重定向地址无效", it) }
        if (
            !resolved.scheme.equals("https", ignoreCase = true) ||
            resolved.host.isNullOrBlank() ||
            resolved.userInfo != null
        ) {
            throw AppShipUpdateException("更新下载拒绝了不安全的重定向")
        }
        return resolved.toASCIIString()
    }

    private fun validateArtifact(
        value: ArtifactDto?,
        baseUrl: String,
        expectedArchitecture: String,
    ): Artifact {
        val artifact = value ?: throw AppShipUpdateException("更新信息缺少 APK 制品")
        val size = artifact.sizeBytes ?: throw AppShipUpdateException("APK 缺少文件大小")
        val digest = artifact.sha256?.lowercase().orEmpty()
        if (artifact.id.isNullOrBlank() || artifact.fileKind != "apk" || artifact.platform != "android") {
            throw AppShipUpdateException("更新制品不是可安装的 Android APK")
        }
        if (
            artifact.status != "ready" ||
            artifact.downloadEnabled != true ||
            artifact.signingStatus !in setOf("unknown", "signed", "verified", "manual_approved")
        ) {
            throw AppShipUpdateException("更新 APK 尚未通过服务端发布校验")
        }
        if (artifact.architecture != "universal" && artifact.architecture != expectedArchitecture) {
            throw AppShipUpdateException("更新 APK 与当前设备架构不匹配")
        }
        if (size <= 0 || artifact.expectedSizeBytes?.let { it != size } == true) {
            throw AppShipUpdateException("更新 APK 文件大小无效")
        }
        if (!sha256Pattern.matches(digest)) {
            throw AppShipUpdateException("更新 APK 缺少有效的 SHA-256")
        }
        val mimeType = artifact.mimeType?.trim().orEmpty()
        if (mimeType.isEmpty()) throw AppShipUpdateException("更新 APK 缺少文件类型")
        val filename = artifact.originalFilename?.trim().orEmpty()
        if (filename.isEmpty()) throw AppShipUpdateException("更新 APK 缺少文件名")
        val id = artifact.id
        val downloadUrl = normalizeArtifactUrl(
            baseUrl,
            artifact.downloadUrl ?: throw AppShipUpdateException("更新 APK 缺少下载地址"),
        )
        if (URI(downloadUrl).path != "/api/v1/artifacts/$id/download") {
            throw AppShipUpdateException("更新 APK 的制品标识与下载地址不匹配")
        }
        return Artifact(
            downloadUrl = downloadUrl,
            sizeBytes = size,
            sha256 = digest,
        )
    }

    private fun validateBaseUrl(value: String): URI {
        val uri = runCatching { URI(value.trim().trimEnd('/')) }
            .getOrElse { throw IllegalArgumentException("更新服务地址无效", it) }
        require(uri.scheme.equals("https", ignoreCase = true) && !uri.host.isNullOrBlank() && uri.userInfo == null) {
            "更新服务必须使用 HTTPS"
        }
        return uri
    }

    private fun encode(value: String): String = URLEncoder.encode(value, StandardCharsets.UTF_8.name())

    private data class Artifact(
        val downloadUrl: String,
        val sizeBytes: Long,
        val sha256: String,
    )

    @Serializable
    private data class UpdateResponseDto(
        val status: String,
        val application: String,
        val channel: String,
        val latest: LatestDto? = null,
        val policy: PolicyDto? = null,
        val artifact: ArtifactDto? = null,
        val message: String? = null,
    )

    @Serializable
    private data class LatestDto(
        val versionName: String? = null,
        val buildNumber: Long? = null,
        val publishedAt: String? = null,
        val releaseNotes: String? = null,
    )

    @Serializable
    private data class PolicyDto(
        val minSupportedBuild: Long? = null,
        val forceAfter: String? = null,
    )

    @Serializable
    private data class ArtifactDto(
        val id: String? = null,
        val platform: String? = null,
        val fileKind: String? = null,
        val architecture: String? = null,
        val originalFilename: String? = null,
        val sizeBytes: Long? = null,
        val expectedSizeBytes: Long? = null,
        val sha256: String? = null,
        val mimeType: String? = null,
        val status: String? = null,
        val signingStatus: String? = null,
        val downloadEnabled: Boolean? = null,
        val downloadUrl: String? = null,
    )
}

internal class AppShipUpdateException(message: String, cause: Throwable? = null) :
    IllegalArgumentException(message, cause)

internal fun isUpdateCacheFresh(
    lastCheckedAt: Long,
    now: Long,
    intervalMs: Long = 60_000L,
): Boolean = lastCheckedAt > 0L && now >= lastCheckedAt && now - lastCheckedAt <= intervalMs

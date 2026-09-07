package dev.termlink.product

import java.net.URI
import java.time.Instant
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** Validation and canonicalization used before any production network request. */
internal object ServerUrlPolicy {
    fun normalize(value: String, allowCleartextForLocalTest: Boolean = false): String {
        val candidate = value.trim()
        require(candidate.isNotEmpty()) { "A server URL is required" }
        val uri = try {
            URI(candidate.trimEnd('/'))
        } catch (error: IllegalArgumentException) {
            throw IllegalArgumentException("The server URL is invalid", error)
        }
        val scheme = uri.scheme?.lowercase()
        require(scheme == "https" || allowCleartextForLocalTest && scheme == "http") {
            "A production server must use HTTPS"
        }
        require(uri.rawUserInfo == null) { "Credentials are not allowed in the server URL" }
        require(!uri.host.isNullOrBlank()) { "The server URL must include a host" }
        require(uri.rawQuery == null && uri.rawFragment == null) { "Query and fragment are not allowed" }
        require(uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") { "A server URL cannot include a path" }
        return URI(
            scheme,
            null,
            uri.host.lowercase(),
            uri.port,
            null,
            null,
            null,
        ).toString().trimEnd('/')
    }
}

internal data class PairingChallengeInput(
    val challengeId: UUID,
    val code: String,
    val challengeNonce: String,
)

internal data class PairingChallengeDisplay(
    val challengeId: UUID,
    val code: String,
    val challengeNonce: String,
    val expiresAt: Instant?,
    val targetName: String?,
)

internal object PairingInputParser {
    private const val CODE_LENGTH = 8
    private const val CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

    fun parse(challengeId: String, code: String, challengeNonce: String): PairingChallengeInput {
        val id = try {
            UUID.fromString(normalizeChallengeId(challengeId))
        } catch (error: IllegalArgumentException) {
            throw IllegalArgumentException("The challenge ID is invalid", error)
        }
        val normalizedCode = normalizeCode(code)
        val normalizedNonce = normalizeNonce(challengeNonce)
        return PairingChallengeInput(id, normalizedCode, normalizedNonce)
    }

    private fun normalizeChallengeId(value: String): String = value.trim().map { character ->
        when (character) {
            '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', '\u2212', '\uFF0D' -> '-'
            else -> character
        }
    }.joinToString("")

    fun normalizeCode(value: String): String {
        val normalized = value.filterNot(Char::isWhitespace).replace("-", "").uppercase()
        require(normalized.length == CODE_LENGTH && normalized.all(CODE_ALPHABET::contains)) {
            "The pairing code must contain eight valid characters"
        }
        return normalized
    }

    fun normalizeNonce(value: String): String {
        val normalized = value.filterNot(Char::isWhitespace)
        val decoded = try {
            Base64.getUrlDecoder().decode(normalized)
        } catch (error: IllegalArgumentException) {
            throw IllegalArgumentException("The challenge nonce is invalid", error)
        }
        require(decoded.size == 32) { "The challenge nonce is invalid" }
        return Base64.getUrlEncoder().withoutPadding().encodeToString(decoded)
    }
}

/** Single place for parsing server-shaped product responses. */
internal object ProductResponseParser {
    fun device(root: kotlinx.serialization.json.JsonObject): dev.termlink.model.DeviceSummary = root.run {
        dev.termlink.model.DeviceSummary(
            id = UUID.fromString(requiredString("id")),
            type = dev.termlink.model.DeviceType.valueOf(requiredString("type")),
            name = requiredString("name"),
            keyId = requiredString("key_id"),
            exchangePublicKey = Base64.getDecoder().decode(requiredString("exchange_public_key")).also {
                require(it.size == 32) { "The device exchange public key is invalid" }
            },
            signingPublicKey = Base64.getDecoder().decode(requiredString("signing_public_key")).also {
                require(it.size == 32) { "The device signing public key is invalid" }
            },
            trustState = dev.termlink.model.DeviceTrustState.valueOf(requiredString("trust_state")),
            createdAt = Instant.parse(requiredString("created_at")),
        )
    }

    fun self(root: kotlinx.serialization.json.JsonObject): dev.termlink.model.DeviceSummary =
        device(root.requiredObject("device"))

    fun catalogPlanObjects(root: JsonObject): List<JsonObject> =
        nullableObjectArray(root, "plans")

    fun itemObjects(root: JsonObject): List<JsonObject> =
        nullableObjectArray(root, "items")

    private fun nullableObjectArray(root: JsonObject, name: String): List<JsonObject> =
        when (val value = root.getValue(name)) {
            JsonNull -> emptyList()
            is JsonArray -> value.map(JsonElement::jsonObject)
            else -> error("$name must be an array")
        }

    private fun kotlinx.serialization.json.JsonObject.requiredString(name: String): String =
        getValue(name).jsonPrimitive.content

    private fun kotlinx.serialization.json.JsonObject.requiredObject(name: String): kotlinx.serialization.json.JsonObject =
        getValue(name).jsonObject
}

internal class TokenRefreshCoordinator(private val refresh: suspend (force: Boolean) -> String) {
    private val mutex = Mutex()

    suspend fun freshAccessToken(force: Boolean = false): String = serially {
        refresh(force).also { value ->
            require(value.isNotBlank()) { "OIDC access token is unavailable" }
        }
    }

    /**
     * Lets an owner-aware caller capture and validate its authentication claim
     * only after entering the shared refresh lane. This prevents a request from
     * waiting behind an old runtime and then accidentally refreshing whichever
     * account happens to be current when the wait ends.
     */
    suspend fun <T> serially(action: suspend () -> T): T = mutex.withLock { action() }
}

internal suspend fun <T> boundedRemoteDownloadPreflight(
    timeoutMillis: Long,
    action: suspend () -> T,
): T {
    require(timeoutMillis > 0L) { "Remote download preflight timeout must be positive" }
    val completed = withTimeoutOrNull(timeoutMillis) { Result.success(action()) }
        ?: throw RemoteFileTransferException(
            "File transfer preparation timed out; retry the download / 文件传输准备超时，请重试下载。",
            retryable = true,
        )
    return completed.getOrThrow()
}

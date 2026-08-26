package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import java.io.Closeable
import java.net.URI
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

fun interface AccessTokenProvider { fun accessToken(): String }

class TermLinkApiException(val status: Int, val code: String, val traceId: String?) : Exception("TermLink API $status: $code")

class ProductionApiClient(
    rootUrl: String,
    private val tokenProvider: AccessTokenProvider,
    private val deviceId: UUID,
    private val identity: DeviceIdentity,
    private val httpClient: OkHttpClient = OkHttpClient(),
    allowCleartextForLocalTest: Boolean = false,
) : Closeable {
    private val root = normalizeRoot(rootUrl, allowCleartextForLocalTest)
    private val json = Json { ignoreUnknownKeys = false; explicitNulls = false }

    fun registerDevice(type: String, name: String, keyId: String): JsonElement {
        require(type == "WINDOWS_PC" || type == "PC_AGENT" || type == "ANDROID")
        val timestamp = System.currentTimeMillis()
        val nonceBytes = ByteArray(16).also(java.security.SecureRandom()::nextBytes)
        val nonce = DeviceProof.base64Url(nonceBytes)
        val signing = DeviceProof.base64Url(identity.signingPublicKey)
        val exchange = DeviceProof.base64Url(identity.exchange.publicKey)
        val canonical = listOf(
            "TERMLINK-DEVICE-REGISTER-V1", deviceId.toString(), type, name,
            signing, exchange, keyId, timestamp.toString(), nonce,
        ).joinToString("\n").encodeToByteArray()
        val body = buildJsonObject {
            put("id", deviceId.toString()); put("type", type); put("name", name)
            put("signing_public_key", signing); put("exchange_public_key", exchange); put("key_id", keyId)
            put("timestamp_ms", timestamp); put("nonce", nonce); put("signature", DeviceProof.base64Url(identity.sign(canonical)))
        }
        return execute("POST", "/v1/devices/register", body, includeDeviceProof = false)
    }

    fun get(path: String): JsonElement = execute("GET", path, JsonNull)
    fun post(path: String, body: JsonElement, idempotencyKey: String = UUID.randomUUID().toString()): JsonElement = execute("POST", path, body, idempotencyKey)
    fun put(path: String, body: JsonElement, idempotencyKey: String = UUID.randomUUID().toString()): JsonElement = execute("PUT", path, body, idempotencyKey)
    fun delete(path: String, idempotencyKey: String = UUID.randomUUID().toString()): JsonElement = execute("DELETE", path, JsonNull, idempotencyKey)

    private fun execute(method: String, path: String, bodyElement: JsonElement, idempotencyKey: String? = null, includeDeviceProof: Boolean = true): JsonElement {
        require(path.startsWith("/v1/") && !path.contains("#"))
        val body = if (method == "GET" || method == "DELETE") ByteArray(0) else json.encodeToString(JsonElement.serializer(), bodyElement).encodeToByteArray()
        val builder = Request.Builder().url(root + path).header("Authorization", "Bearer ${tokenProvider.accessToken()}")
        if (idempotencyKey != null) builder.header("Idempotency-Key", idempotencyKey)
        if (includeDeviceProof) {
            val proof = DeviceProof.create(identity, deviceId.toString(), method, path, body)
            builder.header("X-TermLink-Device", proof.deviceId)
                .header("X-TermLink-Timestamp", proof.timestampMillis.toString())
                .header("X-TermLink-Nonce", proof.nonce)
                .header("X-TermLink-Signature", proof.signature)
        }
        val requestBody = body.toRequestBody(JSON_MEDIA_TYPE)
        when (method) {
            "GET" -> builder.get()
            "POST" -> builder.post(requestBody)
            "PUT" -> builder.put(requestBody)
            "DELETE" -> builder.delete()
            else -> error("unsupported method")
        }
        httpClient.newCall(builder.build()).execute().use { response ->
            val raw = response.body?.string().orEmpty()
            val decoded = if (raw.isBlank()) JsonNull else json.parseToJsonElement(raw)
            if (!response.isSuccessful) {
                val error = decoded.jsonObject["error"]?.jsonObject
                throw TermLinkApiException(response.code, error?.get("code")?.toString()?.trim('"') ?: "HTTP_${response.code}", error?.get("trace_id")?.toString()?.trim('"'))
            }
            return decoded
        }
    }

    override fun close() { }

    private companion object {
        val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
        fun normalizeRoot(value: String, allowCleartext: Boolean): String {
            val uri = URI(value.trimEnd('/'))
            require(uri.scheme == "https" || allowCleartext && uri.scheme == "http")
            require(!uri.host.isNullOrBlank() && uri.rawQuery == null && uri.rawFragment == null)
            return value.trimEnd('/')
        }
    }
}

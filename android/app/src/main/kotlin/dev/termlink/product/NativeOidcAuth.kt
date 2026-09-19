package dev.termlink.product

import dev.termlink.harness.BuildConfig
import android.net.Uri
import java.io.ByteArrayOutputStream
import java.util.Base64
import java.net.URI
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.Locale
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.CookieJar
import okhttp3.FormBody
import okhttp3.OkHttpClient
import okhttp3.Request

/** Safe, bounded failures exposed to UI/logging.  Response bodies are never retained. */
internal enum class NativeOidcFailure {
    INVALID_CONFIGURATION,
    DISCOVERY_FAILED,
    TOKEN_REQUEST_FAILED,
    INVALID_CREDENTIALS,
    ACCOUNT_SETUP_REQUIRED,
    NETWORK,
}

internal object EmbeddedAuthPolicy {
    const val LOGIN_PROMPT = "login"
}

internal class NativeOidcException(
    val reason: NativeOidcFailure,
    val interactiveFallbackAvailable: Boolean = reason == NativeOidcFailure.ACCOUNT_SETUP_REQUIRED,
    cause: Throwable? = null,
) : Exception("OIDC authentication failed", cause)

/** Canonical account scope.  It is intentionally issuer + subject, never a username. */
internal object AccountScope {
    fun canonical(issuer: String, subject: String): String {
        val canonicalIssuer = OidcEndpointPolicy.canonicalIssuer(issuer)
        val normalizedSubject = subject.trim()
        require(normalizedSubject.isNotEmpty()) { "OIDC subject is unavailable" }
        require(normalizedSubject.length <= 512) { "OIDC subject is invalid" }
        require(normalizedSubject.none { it.code < 0x20 || it.code == 0x7F }) { "OIDC subject is invalid" }
        return "$canonicalIssuer\n$normalizedSubject"
    }
}

/**
 * Access-token contract reader used before selecting an account-scoped device
 * key. Signature verification remains server-authoritative; these checks stop
 * a wrong token class/client/account from influencing local key selection.
 */
internal object OidcJwtClaims {
    fun accessTokenSubject(
        token: String,
        expectedIssuer: String,
        expectedParty: String,
        nowEpochSeconds: Long = Instant.now().epochSecond,
    ): String {
        val pieces = token.split('.', limit = 4)
        if (pieces.size != 3) throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        val claims = try {
            val decoded = Base64.getUrlDecoder().decode(pieces[1])
            try {
                Json.parseToJsonElement(String(decoded, StandardCharsets.UTF_8)).jsonObject
            } finally {
                decoded.fill(0)
            }
        } catch (_: Exception) {
            throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        }
        val issuer = claims["iss"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        try {
            OidcEndpointPolicy.requireDiscoveryIssuer(expectedIssuer, issuer)
        } catch (error: IllegalArgumentException) {
            throw NativeOidcException(NativeOidcFailure.INVALID_CONFIGURATION, cause = error)
        }
        val subject = claims["sub"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        val party = claims["azp"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        val type = claims["typ"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        val tokenId = claims["jti"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        val expiresAt = claims["exp"]?.jsonPrimitive?.contentOrNull?.toLongOrNull()
        val notBefore = claims["nbf"]?.jsonPrimitive?.contentOrNull?.toLongOrNull()
        if (subject.isEmpty() || subject.length > 512 || subject.any { it.code < 0x20 || it.code == 0x7F } ||
            party != expectedParty || type != "Bearer" || tokenId.isEmpty() ||
            expiresAt == null || expiresAt <= nowEpochSeconds ||
            notBefore != null && notBefore > nowEpochSeconds ||
            !audienceContains(claims["aud"], SERVER_AUDIENCE)
        ) {
            throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        }
        return subject
    }

    /**
     * Reads a non-sensitive display identifier from a JWT payload. This is
     * only a UI fallback for interactive OIDC sessions; authorization
     * remains based on the validated subject above and never on this value.
     */
    fun displayIdentifier(token: String): String? {
        val pieces = token.split('.', limit = 4)
        if (pieces.size != 3) return null
        val claims = try {
            val decoded = Base64.getUrlDecoder().decode(pieces[1])
            try {
                Json.parseToJsonElement(String(decoded, StandardCharsets.UTF_8)).jsonObject
            } finally {
                decoded.fill(0)
            }
        } catch (_: Exception) {
            return null
        }
        return sequenceOf("preferred_username", "email", "username")
            .mapNotNull { key -> claims[key]?.jsonPrimitive?.contentOrNull?.trim() }
            .firstOrNull { value ->
                value.isNotEmpty() && value.length <= 320 && value.none { ch -> ch.code < 0x20 || ch.code == 0x7F }
            }
    }

    private fun audienceContains(value: kotlinx.serialization.json.JsonElement?, expected: String): Boolean = when (value) {
        is JsonPrimitive -> value.contentOrNull == expected
        is JsonArray -> value.any { (it as? JsonPrimitive)?.contentOrNull == expected }
        else -> false
    }

    private const val SERVER_AUDIENCE = "termlink-server"
}

/** OIDC endpoint checks shared by native direct grant and the embedded AppAuth fallback. */
internal object OidcEndpointPolicy {
    fun canonicalIssuer(raw: String): String = try {
        val uri = URI(raw.trim())
        // Debug builds may target a local cleartext server end to end: relax
        // the https requirement only for loopback/private-network issuers.
        val httpAllowed = BuildConfig.DEBUG && isExplicitLocalTestUrl(raw)
        if (httpAllowed) {
            require(uri.scheme.equals("https", ignoreCase = true) || uri.scheme.equals("http", ignoreCase = true))
        } else {
            require(uri.scheme.equals("https", ignoreCase = true))
        }
        require(uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(!uri.host.isNullOrBlank())
        val path = uri.rawPath.orEmpty().trimEnd('/')
        val scheme = if (uri.scheme.equals("https", ignoreCase = true)) "https" else "http"
        URI(scheme, null, uri.host.lowercase(Locale.US), uri.port, if (path.isEmpty()) null else path, null, null)
            .toString()
            .trimEnd('/')
    } catch (error: Exception) {
        throw IllegalArgumentException("OIDC issuer is invalid", error)
    }

    fun canonicalIssuer(uri: Uri): String = canonicalIssuer(uri.toString())

    fun discoveryUri(issuer: String): URI {
        val canonical = canonicalIssuer(issuer)
        return URI("$canonical/.well-known/openid-configuration")
    }

    /** OIDC discovery requires the configured issuer string to match exactly. */
    fun requireDiscoveryIssuer(expectedIssuer: String, discoveredIssuer: String) {
        require(expectedIssuer.trim() == discoveredIssuer.trim()) {
            "OIDC discovery issuer does not match configured issuer"
        }
    }

    fun validateEndpoint(expectedIssuer: String, endpoint: String, name: String): URI {
        val expected = URI(canonicalIssuer(expectedIssuer))
        val actual = try { URI(endpoint.trim()) } catch (error: Exception) {
            throw IllegalArgumentException("OIDC $name endpoint is invalid", error)
        }
        val httpAllowed = BuildConfig.DEBUG && expected.scheme.equals("http", ignoreCase = true)
        if (httpAllowed) {
            require(actual.scheme.equals("https", ignoreCase = true) || actual.scheme.equals("http", ignoreCase = true)) {
                "OIDC $name endpoint must use HTTP(S)"
            }
        } else {
            require(actual.scheme.equals("https", ignoreCase = true)) { "OIDC $name endpoint must use HTTPS" }
        }
        require(actual.rawUserInfo == null && actual.rawQuery == null && actual.rawFragment == null) {
            "OIDC $name endpoint cannot contain credentials, query, or fragment"
        }
        require(!actual.host.isNullOrBlank()) { "OIDC $name endpoint host is invalid" }
        require(origin(expected) == origin(actual)) { "OIDC $name endpoint must share issuer origin" }
        return actual
    }

    private fun origin(uri: URI): String {
        val port = when {
            uri.port >= 0 -> uri.port
            uri.scheme.equals("https", ignoreCase = true) -> 443
            else -> 80
        }
        return "${uri.scheme.lowercase(Locale.US)}://${uri.host.lowercase(Locale.US)}:$port"
    }
}

internal data class NativeOidcConfiguration(
    val issuer: String,
    val clientId: String,
    val redirectUri: Uri,
    val serviceConfiguration: net.openid.appauth.AuthorizationServiceConfiguration,
)

internal data class NativeTokenPayload(
    val accessToken: String,
    val tokenType: String,
    val expiresInSeconds: Long,
    val refreshToken: String?,
    val idToken: String?,
    val scope: String?,
)

/**
 * Direct-grant OIDC transport.  It is intentionally separate from AppAuth:
 * native login sends credentials only to the discovered HTTPS token endpoint,
 * with redirects and cookies disabled.
 */
internal class NativeOidcClient(
    private val http: OkHttpClient,
    private val json: Json = Json { ignoreUnknownKeys = true },
    private val maxResponseBytes: Int = MAX_RESPONSE_BYTES,
) {
    fun discover(expectedIssuer: String, clientId: String, redirectUri: Uri): NativeOidcConfiguration {
        val issuer = OidcEndpointPolicy.canonicalIssuer(expectedIssuer)
        require(clientId.isNotBlank()) { "OIDC client ID is required" }
        require(redirectUri.scheme.equals("dev.termlink", ignoreCase = true)) {
            "OIDC redirect URI is unsupported"
        }
        val discoveryRequest = Request.Builder().url(OidcEndpointPolicy.discoveryUri(issuer).toString()).get().build()
        try {
            val body = execute(discoveryRequest, NativeOidcFailure.DISCOVERY_FAILED)
            val document = parseObject(body, NativeOidcFailure.DISCOVERY_FAILED)
            val discoveredIssuer = document.string("issuer")
            OidcEndpointPolicy.requireDiscoveryIssuer(issuer, discoveredIssuer)
            val authorization = OidcEndpointPolicy.validateEndpoint(issuer, document.string("authorization_endpoint"), "authorization")
            val token = OidcEndpointPolicy.validateEndpoint(issuer, document.string("token_endpoint"), "token")
            return NativeOidcConfiguration(
                issuer = issuer,
                clientId = clientId,
                redirectUri = redirectUri,
                serviceConfiguration = net.openid.appauth.AuthorizationServiceConfiguration(
                    Uri.parse(authorization.toString()),
                    Uri.parse(token.toString()),
                ),
            )
        } catch (error: IllegalArgumentException) {
            throw NativeOidcException(NativeOidcFailure.INVALID_CONFIGURATION, cause = error)
        }
    }

    fun passwordGrant(configuration: NativeOidcConfiguration, username: String, password: CharArray): NativeTokenPayload {
        require(username.trim().isNotEmpty()) { "Username is required" }
        require(password.isNotEmpty()) { "Password is required" }
        val form = FormBody.Builder()
            .add("grant_type", "password")
            .add("client_id", configuration.clientId)
            .add("scope", "openid profile offline_access")
            .add("username", username)
            .add("password", String(password))
            .build()
        val request = Request.Builder().url(configuration.serviceConfiguration.tokenEndpoint.toString()).post(form).build()
        return parseTokenResponse(execute(request, NativeOidcFailure.TOKEN_REQUEST_FAILED))
    }

    fun refresh(configuration: NativeOidcConfiguration, refreshToken: String): NativeTokenPayload {
        require(refreshToken.isNotBlank()) { "Refresh token is unavailable" }
        val form = FormBody.Builder()
            .add("grant_type", "refresh_token")
            .add("client_id", configuration.clientId)
            .add("refresh_token", refreshToken)
            .build()
        val request = Request.Builder().url(configuration.serviceConfiguration.tokenEndpoint.toString()).post(form).build()
        return parseTokenResponse(execute(request, NativeOidcFailure.TOKEN_REQUEST_FAILED))
    }

    private fun parseTokenResponse(body: String): NativeTokenPayload {
        val document = parseObject(body, NativeOidcFailure.TOKEN_REQUEST_FAILED)
        val errorCode = document["error"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        val errorDescription = document["error_description"]?.jsonPrimitive?.contentOrNull?.lowercase(Locale.US).orEmpty()
        if (errorCode.isNotEmpty()) {
            val actionRequired = errorCode in BROWSER_REQUIRED_ERRORS ||
                errorDescription.contains("required action") ||
                errorDescription.contains("verify") ||
                errorDescription.contains("setup")
            val reason = if (actionRequired) {
                NativeOidcFailure.ACCOUNT_SETUP_REQUIRED
            } else if (errorCode == "invalid_grant") {
                NativeOidcFailure.INVALID_CREDENTIALS
            } else {
                NativeOidcFailure.TOKEN_REQUEST_FAILED
            }
            throw NativeOidcException(reason)
        }
        val accessToken = document["access_token"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        if (accessToken.isEmpty()) throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        val tokenType = document["token_type"]?.jsonPrimitive?.contentOrNull?.trim().orEmpty()
        if (!tokenType.equals("Bearer", ignoreCase = true)) {
            throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        }
        val expiresIn = document["expires_in"]?.jsonPrimitive?.contentOrNull?.toLongOrNull()
        if (expiresIn == null || expiresIn !in 1..MAX_TOKEN_LIFETIME_SECONDS) {
            throw NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)
        }
        val scope = document["scope"]?.jsonPrimitive?.contentOrNull
        return NativeTokenPayload(
            accessToken = accessToken,
            tokenType = tokenType,
            expiresInSeconds = expiresIn,
            refreshToken = document["refresh_token"]?.jsonPrimitive?.contentOrNull,
            idToken = document["id_token"]?.jsonPrimitive?.contentOrNull,
            scope = scope,
        )
    }

    private fun execute(request: Request, failure: NativeOidcFailure): String {
        try {
            http.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    val body = response.body
                    // Read only enough to classify an OAuth error.  The body is
                    // dropped immediately and never included in an exception.
                    val bounded = body?.let { readBoundedOidcResponse(it, maxResponseBytes, failure) }.orEmpty()
                    if (failure == NativeOidcFailure.TOKEN_REQUEST_FAILED && bounded.isNotBlank()) {
                        runCatching { parseObject(bounded, failure) }.getOrNull()?.let { document ->
                            val code = document["error"]?.jsonPrimitive?.contentOrNull.orEmpty()
                            val description = document["error_description"]?.jsonPrimitive?.contentOrNull?.lowercase(Locale.US).orEmpty()
                            if (code in BROWSER_REQUIRED_ERRORS || description.contains("required action") || description.contains("setup") || description.contains("verify")) {
                                throw NativeOidcException(NativeOidcFailure.ACCOUNT_SETUP_REQUIRED)
                            }
                            if (code == "invalid_grant") throw NativeOidcException(NativeOidcFailure.INVALID_CREDENTIALS)
                        }
                    }
                    throw NativeOidcException(failure)
                }
                return readBoundedOidcResponse(response.body, maxResponseBytes, failure)
            }
        } catch (error: NativeOidcException) {
            throw error
        } catch (error: Exception) {
            throw NativeOidcException(NativeOidcFailure.NETWORK, cause = error)
        }
    }

    private fun parseObject(raw: String, failure: NativeOidcFailure): JsonObject = try {
        json.parseToJsonElement(raw).jsonObject
    } catch (error: Exception) {
        // Token responses can contain live credentials. Do not retain a JSON
        // parser cause whose diagnostic message may quote response content.
        throw NativeOidcException(failure, cause = if (failure == NativeOidcFailure.TOKEN_REQUEST_FAILED) null else error)
    }

    private fun JsonObject.string(name: String): String =
        get(name)?.jsonPrimitive?.contentOrNull?.trim().orEmpty().also {
            require(it.isNotEmpty()) { "OIDC discovery metadata is incomplete" }
        }

    private companion object {
        const val MAX_RESPONSE_BYTES = 256 * 1024
        const val MAX_TOKEN_LIFETIME_SECONDS = 24 * 60 * 60L
        val BROWSER_REQUIRED_ERRORS = setOf("interaction_required", "login_required", "account_setup_required", "user_inactive")
    }
}

internal fun readBoundedOidcResponse(
    body: okhttp3.ResponseBody?,
    maxResponseBytes: Int,
    failure: NativeOidcFailure,
): String {
    requireNotNull(body) { "OIDC response was empty" }
    body.use { sourceBody ->
        sourceBody.contentLength().takeIf { it >= 0 }?.let { length ->
            if (length > maxResponseBytes) throw NativeOidcException(failure)
        }
        val output = ByteArrayOutputStream(minOf(maxResponseBytes, 16 * 1024))
        val input = sourceBody.byteStream()
        val buffer = ByteArray(8 * 1024)
        var total = 0
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            total += count
            if (total > maxResponseBytes) throw NativeOidcException(failure)
            output.write(buffer, 0, count)
        }
        return output.toString(StandardCharsets.UTF_8.name())
    }
}

internal fun secureNativeHttpClient(): OkHttpClient = OkHttpClient.Builder()
    .followRedirects(false)
    .followSslRedirects(false)
    .cookieJar(CookieJar.NO_COOKIES)
    .callTimeout(java.time.Duration.ofSeconds(20))
    .connectTimeout(java.time.Duration.ofSeconds(10))
    .readTimeout(java.time.Duration.ofSeconds(15))
    .writeTimeout(java.time.Duration.ofSeconds(15))
    .build()

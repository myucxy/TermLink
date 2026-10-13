package dev.termlink.product

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import java.security.KeyStore
import java.util.Base64
import java.util.UUID
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import net.openid.appauth.AuthState
import net.openid.appauth.TokenRequest
import net.openid.appauth.TokenResponse
import org.json.JSONException

internal fun interface AuthStateCipher {
    fun transform(input: ByteArray, encrypt: Boolean): ByteArray
}

/**
 * Encrypted AuthState envelope. The production implementation supplies an Android Keystore
 * cipher; JVM tests can supply a deterministic fake without ever changing production crypto.
 */
internal object AuthStateEnvelope {
    class InvalidEnvelope(cause: Throwable? = null) : IllegalArgumentException("Stored OIDC session is invalid", cause)

    fun seal(plaintext: ByteArray, cipher: AuthStateCipher): String {
        val input = plaintext.copyOf()
        val encrypted = try {
            cipher.transform(input, true)
        } finally {
            input.fill(0)
        }
        return try {
            Base64.getEncoder().withoutPadding().encodeToString(encrypted)
        } finally {
            encrypted.fill(0)
        }
    }

    fun open(encoded: String, cipher: AuthStateCipher): ByteArray {
        val encrypted = try {
            Base64.getDecoder().decode(encoded)
        } catch (error: IllegalArgumentException) {
            throw InvalidEnvelope(error)
        }
        if (encrypted.size <= IV_BYTES) throw InvalidEnvelope()
        return try {
            cipher.transform(encrypted, false)
        } finally {
            encrypted.fill(0)
        }
    }

    private const val IV_BYTES = 12
}

/**
 * Rebuilds an AuthState with a credential-free token request before it is
 * serialized.  Native password-grant requests contain username/password only
 * in the transient OkHttp form; this invariant prevents either value (or any
 * other request parameter) from reaching the encrypted preference plaintext.
 */
internal object CredentialFreeAuthState {
    fun sanitize(state: AuthState): AuthState {
        val configuration = state.authorizationServiceConfiguration
            ?: return AuthState()
        val clean = AuthState(configuration)
        val response = state.lastTokenResponse ?: return clean
        val request = response.request
        val safeRequest = TokenRequest.Builder(configuration, request.clientId)
            // This request is serialization metadata only and is never sent.
            // AppAuth requires a refresh token when grant_type=refresh_token;
            // adding it here would duplicate a live credential inside the
            // request. A private-format sentinel keeps password, authorization code,
            // code verifier, and refresh token out of the persisted request.
            .setGrantType(SANITIZED_REQUEST_GRANT_TYPE)
            .setScope(request.scope)
            .build()
        val safeResponse = TokenResponse.Builder(safeRequest)
            .setTokenType(response.tokenType)
            .setAccessToken(response.accessToken)
            .setAccessTokenExpirationTime(response.accessTokenExpirationTime)
            .setIdToken(response.idToken)
            .setRefreshToken(state.refreshToken)
            .setScope(response.scope)
            // Additional parameters are not needed for refresh and may carry
            // provider-specific credential-like values, so drop them all.
            .setAdditionalParameters(emptyMap())
            .build()
        clean.update(safeResponse, null)
        return clean
    }

    fun serializedWithoutCredentials(state: AuthState): String = sanitize(state).jsonSerializeString()

    internal const val SANITIZED_REQUEST_GRANT_TYPE = "urn:termlink:auth-state:credential-free"
}

internal class OidcSessionStore(
    context: Context,
    private val cipher: AuthStateCipher = AndroidKeystoreCipher(),
) {
    internal fun sealAiOperation(scope: String, value: String): String {
        val bytes = ("TermLink AI Operation V1\n$scope\n" + value).encodeToByteArray()
        return try { AuthStateEnvelope.seal(bytes, cipher) } finally { bytes.fill(0) }
    }

    internal fun openAiOperation(scope: String, value: String): String {
        val bytes = AuthStateEnvelope.open(value, cipher)
        try {
            val text = bytes.decodeToString()
            val prefix = "TermLink AI Operation V1\n$scope\n"
            require(text.startsWith(prefix))
            return text.removePrefix(prefix)
        } finally { bytes.fill(0) }
    }
    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    var serverUrl: String?
        get() = preferences.getString(KEY_SERVER_URL, null)
        set(value) {
            val editor = preferences.edit()
            if (value == null) editor.remove(KEY_SERVER_URL) else editor.putString(KEY_SERVER_URL, value)
            check(editor.commit())
        }

    fun loadAuthState(): AuthState? {
        val encoded = preferences.getString(KEY_AUTH_STATE, null) ?: return null
        val plaintext = try {
            AuthStateEnvelope.open(encoded, cipher)
        } catch (error: Exception) {
            if (error is AuthStateEnvelope.InvalidEnvelope || error is AEADBadTagException) {
                discardInvalidAuthState()
            } else {
                Log.w("OidcSessionStore", "Stored OIDC session could not be decrypted; preserving it for retry")
            }
            return null
        }
        val sanitized = try {
            val restored = AuthState.jsonDeserialize(String(plaintext, Charsets.UTF_8))
            // Migrate sessions written by older builds immediately.  Do not wait
            // for token expiry: an old AuthorizationResponse may still contain an
            // authorization code and PKCE verifier inside the encrypted envelope.
            CredentialFreeAuthState.sanitize(restored)
        } catch (error: Exception) {
            if (error is JSONException || error is IllegalArgumentException) {
                discardInvalidAuthState()
            } else {
                Log.w("OidcSessionStore", "Stored OIDC session could not be restored; preserving it for retry")
            }
            return null
        } finally {
            plaintext.fill(0)
        }
        try {
            saveAuthState(sanitized)
        } catch (_: Exception) {
            // The old envelope remains encrypted; retry its migration on the next load.
            Log.w("OidcSessionStore", "Stored OIDC session migration could not be saved; preserving the previous session")
        }
        return sanitized
    }

    private fun discardInvalidAuthState() {
        Log.w("OidcSessionStore", "Discarding invalid stored OIDC session")
        preferences.edit()
            .remove(KEY_AUTH_STATE)
            .remove(KEY_ACCOUNT_SCOPE)
            .remove(KEY_ACCOUNT_LOGIN)
            .apply()
    }

    var accountScope: String?
        get() = preferences.getString(KEY_ACCOUNT_SCOPE, null)
        set(value) {
            val editor = preferences.edit()
            if (value == null) editor.remove(KEY_ACCOUNT_SCOPE) else editor.putString(KEY_ACCOUNT_SCOPE, value)
            check(editor.commit())
        }

    /**
     * Non-sensitive login identifier shown in the settings account card.
     * This is the username/email entered by the user, never a password or
     * token claim.  It is deliberately kept separate from the encrypted
     * AuthState so the UI can render it without deserializing credentials.
     */
    var accountLogin: String?
        get() = preferences.getString(KEY_ACCOUNT_LOGIN, null)
        set(value) {
            val normalized = value
                ?.trim()
                ?.takeIf { it.isNotEmpty() && it.length <= MAX_ACCOUNT_LOGIN_LENGTH && it.none { ch -> ch.code < 0x20 || ch.code == 0x7F } }
            val editor = preferences.edit()
            if (normalized == null) editor.remove(KEY_ACCOUNT_LOGIN) else editor.putString(KEY_ACCOUNT_LOGIN, normalized)
            check(editor.commit())
        }

    fun saveAuthState(state: AuthState) {
        val sanitized = CredentialFreeAuthState.sanitize(state)
        val plaintext = sanitized.jsonSerializeString().encodeToByteArray()
        try {
            check(preferences.edit().putString(KEY_AUTH_STATE, AuthStateEnvelope.seal(plaintext, cipher)).commit())
        } finally {
            plaintext.fill(0)
        }
    }

    fun clearAuthState() {
        check(preferences.edit().remove(KEY_AUTH_STATE).commit())
    }

    fun reserveInputSequenceRange(
        sessionId: UUID,
        count: Int = INPUT_SEQUENCE_RESERVATION_SIZE,
        clockMillis: Long = System.currentTimeMillis(),
    ): LongRange = synchronized(SEQUENCE_RESERVATION_LOCK) {
        reserveSequenceRange(
            KEY_INPUT_SEQUENCE_PREFIX + sessionId,
            count,
            clockMillis,
        )
    }

    /**
     * Reserves Android -> PC Envelope sequence numbers before use. Because the
     * sequence is the 64-bit suffix of the AEAD nonce, persisting the high
     * watermark prevents nonce reuse when a Session is reopened in the same
     * key epoch.
     */
    fun reserveEnvelopeSequenceRange(
        sessionId: UUID,
        count: Int = ENVELOPE_SEQUENCE_RESERVATION_SIZE,
        clockMillis: Long = System.currentTimeMillis(),
    ): LongRange = synchronized(SEQUENCE_RESERVATION_LOCK) {
        reserveSequenceRange(
            KEY_ENVELOPE_SEQUENCE_PREFIX + sessionId,
            count,
            clockMillis,
        )
    }

    private fun reserveSequenceRange(key: String, count: Int, clockMillis: Long): LongRange {
        require(count in 1..MAX_INPUT_SEQUENCE_RESERVATION_SIZE) { "input sequence reservation is invalid" }
        val storedHighWatermark = preferences.getLong(key, 0L)
        val clockFloor = Math.multiplyExact(clockMillis.coerceAtLeast(1L), INPUT_SEQUENCE_CLOCK_MULTIPLIER)
        val start = Math.addExact(maxOf(storedHighWatermark, clockFloor), 1L)
        val end = Math.addExact(start, count.toLong() - 1L)
        check(preferences.edit().putLong(key, end).commit())
        return start..end
    }

    private class AndroidKeystoreCipher : AuthStateCipher {
        override fun transform(input: ByteArray, encrypt: Boolean): ByteArray {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            return if (encrypt) {
                cipher.init(Cipher.ENCRYPT_MODE, wrappingKey())
                cipher.updateAAD(AAD)
                cipher.iv + cipher.doFinal(input)
            } else {
                val iv = input.copyOfRange(0, IV_BYTES)
                try {
                    cipher.init(Cipher.DECRYPT_MODE, wrappingKey(), GCMParameterSpec(128, iv))
                    cipher.updateAAD(AAD)
                    cipher.doFinal(input, IV_BYTES, input.size - IV_BYTES)
                } finally {
                    iv.fill(0)
                }
            }
        }

        private fun wrappingKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE).run {
            init(
                KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
            generateKey()
        }
    }
    }

    private companion object {
        const val PREFERENCES = "termlink-product-session"
        const val KEY_SERVER_URL = "server_url"
        const val KEY_AUTH_STATE = "auth_state"
        const val KEY_ACCOUNT_SCOPE = "account_scope"
        const val KEY_ACCOUNT_LOGIN = "account_login"
        const val KEY_INPUT_SEQUENCE_PREFIX = "input_sequence_high_watermark_"
        const val KEY_ENVELOPE_SEQUENCE_PREFIX = "envelope_sequence_high_watermark_android_to_pc_"
        const val KEY_ALIAS = "termlink-oidc-session-wrap-v1"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
        const val INPUT_SEQUENCE_RESERVATION_SIZE = 4_096
        const val ENVELOPE_SEQUENCE_RESERVATION_SIZE = 4_096
        const val MAX_INPUT_SEQUENCE_RESERVATION_SIZE = 1_000_000
        const val INPUT_SEQUENCE_CLOCK_MULTIPLIER = 1L shl 20
        const val MAX_ACCOUNT_LOGIN_LENGTH = 320
        val AAD = "TermLink OIDC AuthState V1".encodeToByteArray()
        val SEQUENCE_RESERVATION_LOCK = Any()
    }
}

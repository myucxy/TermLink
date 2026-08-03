package dev.termlink.product

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import net.openid.appauth.AuthState

internal fun interface AuthStateCipher {
    fun transform(input: ByteArray, encrypt: Boolean): ByteArray
}

/**
 * Encrypted AuthState envelope. The production implementation supplies an Android Keystore
 * cipher; JVM tests can supply a deterministic fake without ever changing production crypto.
 */
internal object AuthStateEnvelope {
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
            throw IllegalArgumentException("Stored OIDC session is invalid", error)
        }
        require(encrypted.size > IV_BYTES) { "Stored OIDC session is invalid" }
        return try {
            cipher.transform(encrypted, false)
        } finally {
            encrypted.fill(0)
        }
    }

    private const val IV_BYTES = 12
}

internal class OidcSessionStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
    private val cipher = AndroidKeystoreCipher()

    var serverUrl: String?
        get() = preferences.getString(KEY_SERVER_URL, null)
        set(value) {
            val editor = preferences.edit()
            if (value == null) editor.remove(KEY_SERVER_URL) else editor.putString(KEY_SERVER_URL, value)
            check(editor.commit())
        }

    fun loadAuthState(): AuthState? {
        val encoded = preferences.getString(KEY_AUTH_STATE, null) ?: return null
        val plaintext = AuthStateEnvelope.open(encoded, cipher)
        return try {
            AuthState.jsonDeserialize(String(plaintext, Charsets.UTF_8))
        } finally {
            plaintext.fill(0)
        }
    }

    fun saveAuthState(state: AuthState) {
        val plaintext = state.jsonSerializeString().encodeToByteArray()
        try {
            check(preferences.edit().putString(KEY_AUTH_STATE, AuthStateEnvelope.seal(plaintext, cipher)).commit())
        } finally {
            plaintext.fill(0)
        }
    }

    fun clearAuthState() {
        check(preferences.edit().remove(KEY_AUTH_STATE).commit())
    }

    private inner class AndroidKeystoreCipher : AuthStateCipher {
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
        const val KEY_ALIAS = "termlink-oidc-session-wrap-v1"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
        val AAD = "TermLink OIDC AuthState V1".encodeToByteArray()
    }
}

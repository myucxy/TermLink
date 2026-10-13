package dev.termlink.product

import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import java.security.GeneralSecurityException
import javax.crypto.AEADBadTagException
import net.openid.appauth.AuthState
import net.openid.appauth.AuthorizationServiceConfiguration
import net.openid.appauth.TokenRequest
import net.openid.appauth.TokenResponse
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class OidcSessionStoreAuthTest {
    private lateinit var context: Context
    private lateinit var preferences: SharedPreferences
    private val cipher = AuthStateCipher { input, encrypt ->
        if (encrypt) ByteArray(12) + input else input.copyOfRange(12, input.size)
    }

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        context.deleteSharedPreferences(PREFERENCES_NAME)
        preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
    }

    @After
    fun tearDown() {
        context.deleteSharedPreferences(PREFERENCES_NAME)
    }

    @Test
    fun sanitizingSerializedStateRetainsRefreshTokenOmittedFromLatestResponse() {
        val restored = AuthState.jsonDeserialize(stateWithRetainedRefreshToken().jsonSerializeString())
        assertNull(restored.lastTokenResponse!!.refreshToken)
        assertEquals("retained-refresh-token", restored.refreshToken)

        val sanitized = AuthState.jsonDeserialize(CredentialFreeAuthState.serializedWithoutCredentials(restored))

        assertEquals("latest-access-token", sanitized.accessToken)
        assertEquals("retained-refresh-token", sanitized.refreshToken)
        val request = sanitized.lastTokenResponse!!.request
        assertEquals(CredentialFreeAuthState.SANITIZED_REQUEST_GRANT_TYPE, request.grantType)
        assertNull(request.refreshToken)
        assertNull(request.authorizationCode)
        assertNull(request.codeVerifier)
        assertTrue(request.additionalParameters.isEmpty())
    }

    @Test
    fun migrationEncryptionFailureReturnsValidSessionAndPreservesStoredCredentials() {
        seedStoredSession()
        val before = preferences.all.toMap()
        val failure = IllegalStateException("Keystore temporarily unavailable")
        val failingCipher = AuthStateCipher { input, encrypt ->
            if (encrypt) throw failure
            cipher.transform(input, false)
        }

        val loaded = OidcSessionStore(context, failingCipher).loadAuthState()

        assertEquals("retained-refresh-token", loaded?.refreshToken)
        assertEquals(before, preferences.all)
    }

    @Test
    fun migrationCommitFailureReturnsValidSessionAndPreservesStoredCredentials() {
        seedStoredSession()
        val before = preferences.all.toMap()
        val failingPreferences = object : SharedPreferences by preferences {
            override fun edit(): SharedPreferences.Editor {
                val editor = preferences.edit()
                return object : SharedPreferences.Editor by editor {
                    override fun putString(key: String?, value: String?): SharedPreferences.Editor {
                        editor.putString(key, value)
                        return this
                    }

                    override fun commit(): Boolean = false
                }
            }
        }
        val failingContext = object : ContextWrapper(context) {
            override fun getApplicationContext(): Context = this
            override fun getSharedPreferences(name: String?, mode: Int): SharedPreferences = failingPreferences
        }

        val loaded = OidcSessionStore(failingContext, cipher).loadAuthState()

        assertEquals("retained-refresh-token", loaded?.refreshToken)
        assertEquals(before, preferences.all)
    }

    @Test
    fun transientDecryptionFailuresPreserveStoredCredentialsAndAllowRetry() {
        for (failure in listOf(GeneralSecurityException("Keystore unavailable"), IllegalArgumentException("Provider unavailable"))) {
            seedStoredSession()
            val before = preferences.all.toMap()
            var unavailable = true
            val retryingCipher = AuthStateCipher { input, encrypt ->
                if (!encrypt && unavailable) throw failure
                cipher.transform(input, encrypt)
            }
            val store = OidcSessionStore(context, retryingCipher)

            assertNull(store.loadAuthState())
            assertEquals(before, preferences.all)

            unavailable = false
            assertEquals("retained-refresh-token", store.loadAuthState()?.refreshToken)
            assertEquals("issuer|subject", preferences.getString("account_scope", null))
            assertEquals("signed-in-user", preferences.getString("account_login", null))
        }
    }

    @Test
    fun invalidJsonInsideValidEnvelopeClearsStoredCredentials() {
        seedStoredSession("invalid-json")

        assertNull(OidcSessionStore(context, cipher).loadAuthState())
        assertStoredCredentialsCleared()
    }

    @Test
    fun invalidAuthenticationTagClearsStoredCredentials() {
        seedStoredSession()
        val failingCipher = AuthStateCipher { _, _ -> throw AEADBadTagException("Invalid tag") }

        assertNull(OidcSessionStore(context, failingCipher).loadAuthState())
        assertStoredCredentialsCleared()
    }

    private fun assertStoredCredentialsCleared() {
        assertNull(preferences.getString("auth_state", null))
        assertNull(preferences.getString("account_scope", null))
        assertNull(preferences.getString("account_login", null))
    }

    private fun seedStoredSession(plaintext: String = stateWithRetainedRefreshToken().jsonSerializeString()) {
        val encoded = AuthStateEnvelope.seal(plaintext.encodeToByteArray(), cipher)
        check(preferences.edit()
            .putString("auth_state", encoded)
            .putString("account_scope", "issuer|subject")
            .putString("account_login", "signed-in-user")
            .commit())
    }

    private fun stateWithRetainedRefreshToken(): AuthState {
        val configuration = AuthorizationServiceConfiguration(
            Uri.parse("https://issuer.example/authorize"),
            Uri.parse("https://issuer.example/token"),
        )
        val request = TokenRequest.Builder(configuration, "termlink-android")
            .setGrantType("refresh_token")
            .setRefreshToken("retained-refresh-token")
            .build()
        return AuthState(configuration).apply {
            update(TokenResponse.Builder(request)
                .setAccessToken("previous-access-token")
                .setRefreshToken("retained-refresh-token")
                .build(), null)
            update(TokenResponse.Builder(request)
                .setAccessToken("latest-access-token")
                .build(), null)
        }
    }

    private companion object {
        const val PREFERENCES_NAME = "termlink-product-session"
    }
}

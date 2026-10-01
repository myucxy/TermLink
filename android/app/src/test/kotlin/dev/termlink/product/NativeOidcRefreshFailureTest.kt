package dev.termlink.product

import android.net.Uri
import java.io.IOException
import net.openid.appauth.AuthorizationServiceConfiguration
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeOidcRefreshFailureTest {
    private val configuration = NativeOidcConfiguration(
        "https://issuer.example", "android", Uri.parse("dev.termlink:/callback"),
        AuthorizationServiceConfiguration(
            Uri.parse("https://issuer.example/authorize"), Uri.parse("https://issuer.example/token"),
        ),
    )

    @Test
    fun onlyExplicitCredentialOrInteractionFailuresTerminateSignIn() {
        for ((status, body, expected) in listOf(
            Triple(400, """{"error":"invalid_grant"}""", NativeOidcFailure.INVALID_CREDENTIALS),
            Triple(400, """{"error":"login_required"}""", NativeOidcFailure.ACCOUNT_SETUP_REQUIRED),
            Triple(401, """{"error":"interaction_required"}""", NativeOidcFailure.ACCOUNT_SETUP_REQUIRED),
            Triple(400, """{"error":"invalid_grant","error_description":"Account setup required"}""", NativeOidcFailure.ACCOUNT_SETUP_REQUIRED),
            Triple(400, """{"error":"invalid_client","error_description":"Verify server setup"}""", NativeOidcFailure.TOKEN_REQUEST_FAILED),
            Triple(200, """{"error":"invalid_client","error_description":"Verify server setup"}""", NativeOidcFailure.TOKEN_REQUEST_FAILED),
            Triple(503, """{"error":"invalid_grant"}""", NativeOidcFailure.TOKEN_REQUEST_FAILED),
            Triple(502, """{"error":"login_required"}""", NativeOidcFailure.TOKEN_REQUEST_FAILED),
            Triple(401, "<html>proxy authentication</html>", NativeOidcFailure.TOKEN_REQUEST_FAILED),
        )) {
            val http = OkHttpClient.Builder().addInterceptor { chain ->
                Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1)
                    .code(status).message("test")
                    .body(body.toResponseBody("application/json".toMediaType())).build()
            }.build()
            val failure = assertThrows(NativeOidcException::class.java) {
                NativeOidcClient(http).refresh(configuration, "test-refresh-token")
            }
            assertEquals("status=$status", expected, failure.reason)
            assertEquals(expected != NativeOidcFailure.TOKEN_REQUEST_FAILED, isAuthenticationRefreshRejection(failure))
        }
    }

    @Test
    fun unavailableIdentityServiceKeepsSignIn() {
        val http = OkHttpClient.Builder().addInterceptor { throw IOException("unavailable") }.build()
        val failure = assertThrows(NativeOidcException::class.java) {
            NativeOidcClient(http).refresh(configuration, "test-refresh-token")
        }
        assertEquals(NativeOidcFailure.NETWORK, failure.reason)
        assertFalse(isAuthenticationRefreshRejection(failure))
    }
}

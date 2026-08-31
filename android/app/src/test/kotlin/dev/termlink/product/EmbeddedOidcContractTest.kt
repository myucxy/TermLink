package dev.termlink.product

import android.content.ComponentName
import android.content.Context
import android.net.Uri
import android.view.WindowManager
import androidx.test.core.app.ApplicationProvider
import net.openid.appauth.AuthorizationException
import net.openid.appauth.AuthorizationRequest
import net.openid.appauth.AuthorizationResponse
import net.openid.appauth.AuthorizationServiceConfiguration
import net.openid.appauth.ResponseTypeValues
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class EmbeddedOidcContractTest {
    @Test
    @Suppress("DEPRECATION")
    fun embeddedActivityIsPrivateAndResizesAboveTheIme() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val activityInfo = context.packageManager.getActivityInfo(
            ComponentName(context, EmbeddedOidcActivity::class.java),
            0,
        )

        assertFalse(activityInfo.exported)
        assertEquals(
            WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE,
            activityInfo.softInputMode and WindowManager.LayoutParams.SOFT_INPUT_MASK_ADJUST,
        )
    }

    @Test
    fun hostedRegistrationLaunchRetainsPkceRequestAndUsesFixedPage() {
        val request = request()
        val launch = EmbeddedOidcLaunch(
            request,
            EmbeddedOidcContract.registrationUri("https://termlink.example/"),
            registrationFlow = true,
        )

        assertEquals("https://termlink.example/register", launch.initialUri.toString())
        assertTrue(EmbeddedOidcContract.isTrustedLaunch(launch, Uri.parse("https://termlink.example")))
        assertTrue(requireNotNull(request.codeVerifier).isNotBlank())
        assertEquals("S256", request.codeVerifierChallengeMethod)
    }

    @Test
    fun embeddedNavigationAllowsOnlyServerAndIssuerOrigins() {
        val root = Uri.parse("https://termlink.example")
        val oidcRequest = request()
        val authorization = oidcRequest.toUri()

        assertTrue(EmbeddedOidcContract.isTrustedWebOrigin(Uri.parse("https://termlink.example/register"), root, authorization))
        assertTrue(EmbeddedOidcContract.isTrustedWebOrigin(Uri.parse("https://identity.example/realms/termlink/login-actions/authenticate"), root, authorization))
        assertFalse(EmbeddedOidcContract.isTrustedWebOrigin(Uri.parse("https://evil.example/register"), root, authorization))
        assertFalse(EmbeddedOidcContract.isTrustedWebOrigin(Uri.parse("http://termlink.example/register"), root, authorization))
        assertTrue(
            EmbeddedOidcContract.isTrustedMainFrameDestination(
                Uri.parse("https://termlink.example/register"),
                root,
                authorization,
                oidcRequest.redirectUri,
            ),
        )
        assertTrue(
            EmbeddedOidcContract.isTrustedMainFrameDestination(
                Uri.parse("dev.termlink:/oauth2redirect?code=one&state=state-1"),
                root,
                authorization,
                oidcRequest.redirectUri,
            ),
        )
        assertFalse(
            EmbeddedOidcContract.isTrustedMainFrameDestination(
                Uri.parse("https://evil.example/post-target"),
                root,
                authorization,
                oidcRequest.redirectUri,
            ),
        )
        assertFalse(
            EmbeddedOidcContract.isTrustedMainFrameDestination(
                Uri.parse("https://termlink.example:444/register"),
                root,
                authorization,
                oidcRequest.redirectUri,
            ),
        )
        assertFalse(
            EmbeddedOidcContract.isTrustedMainFrameDestination(
                Uri.parse("https://user@termlink.example/register"),
                root,
                authorization,
                oidcRequest.redirectUri,
            ),
        )
    }

    @Test
    fun publicLoginReturnsTheRegistrationFlowToAndroidOidc() {
        val root = Uri.parse("https://termlink.example")

        assertTrue(EmbeddedOidcContract.isPublicLogin(root, Uri.parse("https://termlink.example/login")))
        assertFalse(EmbeddedOidcContract.isPublicLogin(root, Uri.parse("https://evil.example/login")))
        assertFalse(EmbeddedOidcContract.isPublicLogin(root, Uri.parse("https://termlink.example/account")))
    }

    @Test
    fun authorizationCallbackRequiresExactRedirectStateAndSingleOutcome() {
        val request = request()
        val valid = Uri.parse("dev.termlink:/oauth2redirect?code=code-1&state=state-1")
        val validResult = EmbeddedOidcContract.authorizationResult(request, valid)
        val response = AuthorizationResponse.fromIntent(validResult)

        assertNotNull(response)
        assertEquals("code-1", response?.authorizationCode)
        assertNull(AuthorizationException.fromIntent(validResult))

        val mismatch = EmbeddedOidcContract.authorizationResult(
            request,
            Uri.parse("dev.termlink:/oauth2redirect?code=code-1&state=wrong"),
        )
        assertEquals(
            AuthorizationException.AuthorizationRequestErrors.STATE_MISMATCH.code,
            AuthorizationException.fromIntent(mismatch)?.code,
        )

        val duplicate = EmbeddedOidcContract.authorizationResult(
            request,
            Uri.parse("dev.termlink:/oauth2redirect?code=one&code=two&state=state-1"),
        )
        assertEquals(
            AuthorizationException.AuthorizationRequestErrors.INVALID_REQUEST.code,
            AuthorizationException.fromIntent(duplicate)?.code,
        )
    }

    private fun request(): AuthorizationRequest {
        val configuration = AuthorizationServiceConfiguration(
            Uri.parse("https://identity.example/realms/termlink/protocol/openid-connect/auth"),
            Uri.parse("https://identity.example/realms/termlink/protocol/openid-connect/token"),
        )
        return AuthorizationRequest.Builder(
            configuration,
            "termlink-android",
            ResponseTypeValues.CODE,
            Uri.parse("dev.termlink:/oauth2redirect"),
        )
            .setScope("openid profile offline_access")
            .setState("state-1")
            .setNonce("nonce-1")
            .setPrompt(EmbeddedAuthPolicy.LOGIN_PROMPT)
            .build()
    }
}

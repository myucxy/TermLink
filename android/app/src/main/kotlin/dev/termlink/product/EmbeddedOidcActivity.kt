package dev.termlink.product

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.SslErrorHandler
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import dev.termlink.harness.BuildConfig
import dev.termlink.harness.R
import net.openid.appauth.AuthorizationException
import net.openid.appauth.AuthorizationRequest
import net.openid.appauth.AuthorizationResponse
import java.io.ByteArrayInputStream

/**
 * App-owned OIDC surface used for hosted registration and identity required actions.
 *
 * The activity is deliberately non-exported. It accepts only an exact AppAuth request
 * URI or the fixed TermLink registration page, never exposes a JavaScript bridge, and
 * returns only a state-checked OAuth response to the calling product activity.
 */
internal class EmbeddedOidcActivity : ComponentActivity() {
    private lateinit var authorizationRequest: AuthorizationRequest
    private lateinit var authorizationUri: Uri
    private lateinit var serverRoot: Uri
    private lateinit var webView: WebView
    private lateinit var progress: ProgressBar
    private lateinit var errorView: TextView
    private var registrationFlow = false
    private var registrationHandedOffToAuthorization = false
    private var terminalResultDelivered = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.setFlags(
            android.view.WindowManager.LayoutParams.FLAG_SECURE,
            android.view.WindowManager.LayoutParams.FLAG_SECURE,
        )
        val launch = runCatching { EmbeddedOidcContract.readLaunch(intent) }.getOrElse {
            finishWithInvalidRequest()
            return
        }
        authorizationRequest = launch.request
        authorizationUri = authorizationRequest.toUri()
        // The embedded authorization surface must follow the same endpoint
        // selected on the sign-in page.  Reading the persisted value here
        // avoids sending a registration flow back to the build-time default.
        val configuredServerUrl = runCatching {
            normalizeProductServerUrl(
                OidcSessionStore(applicationContext).serverUrl ?: PRODUCT_PUBLIC_SERVER_URL,
            )
        }.getOrElse {
            finishWithInvalidRequest()
            return
        }
        serverRoot = Uri.parse(configuredServerUrl)
        registrationFlow = savedInstanceState?.getBoolean(STATE_REGISTRATION_FLOW, launch.registrationFlow)
            ?: launch.registrationFlow
        registrationHandedOffToAuthorization = savedInstanceState
            ?.getBoolean(STATE_REGISTRATION_HANDED_OFF, false)
            ?: false
        if (!EmbeddedOidcContract.isTrustedLaunch(launch, serverRoot)) {
            finishWithInvalidRequest()
            return
        }
        createContentView()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (registrationHandedOffToAuthorization) {
                    cancelFlow()
                } else if (::webView.isInitialized && webView.canGoBack()) {
                    webView.goBack()
                } else {
                    cancelFlow()
                }
            }
        })
        if (savedInstanceState == null) {
            webView.loadUrl(launch.initialUri.toString())
        } else if (webView.restoreState(savedInstanceState) == null) {
            webView.loadUrl(launch.initialUri.toString())
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        if (::webView.isInitialized) webView.saveState(outState)
        outState.putBoolean(STATE_REGISTRATION_FLOW, registrationFlow)
        outState.putBoolean(STATE_REGISTRATION_HANDED_OFF, registrationHandedOffToAuthorization)
        super.onSaveInstanceState(outState)
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun createContentView() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.rgb(11, 15, 20))
        }
        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            val systemBars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            view.setPadding(
                systemBars.left,
                systemBars.top,
                systemBars.right,
                maxOf(systemBars.bottom, ime.bottom),
            )
            insets
        }
        val toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = android.view.Gravity.CENTER_VERTICAL
            setPadding(dp(8), dp(8), dp(16), dp(8))
        }
        val close = ImageButton(this).apply {
            setImageResource(android.R.drawable.ic_menu_close_clear_cancel)
            setColorFilter(Color.WHITE)
            setBackgroundColor(Color.TRANSPARENT)
            contentDescription = getString(R.string.action_close)
            setOnClickListener { cancelFlow() }
        }
        val title = TextView(this).apply {
            text = getString(R.string.embedded_identity_title)
            setTextColor(Color.WHITE)
            textSize = 18f
            setPadding(dp(8), 0, 0, 0)
        }
        toolbar.addView(close, LinearLayout.LayoutParams(dp(48), dp(48)))
        toolbar.addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
        }
        val content = FrameLayout(this)
        webView = WebView(this).apply {
            setBackgroundColor(Color.rgb(11, 15, 20))
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.javaScriptCanOpenWindowsAutomatically = false
            settings.setSupportMultipleWindows(false)
            settings.mixedContentMode = android.webkit.WebSettings.MIXED_CONTENT_NEVER_ALLOW
            settings.safeBrowsingEnabled = true
            CookieManager.getInstance().setAcceptCookie(true)
            CookieManager.getInstance().setAcceptThirdPartyCookies(this, true)
            webChromeClient = object : WebChromeClient() {
                override fun onProgressChanged(view: WebView?, newProgress: Int) {
                    this@EmbeddedOidcActivity.progress.progress = newProgress
                    this@EmbeddedOidcActivity.progress.visibility = if (newProgress >= 100) View.GONE else View.VISIBLE
                }
            }
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    if (!request.isForMainFrame) return false
                    return routeMainFrame(request.url)
                }

                @Deprecated("Used by Android WebView on legacy redirects")
                override fun shouldOverrideUrlLoading(view: WebView, url: String): Boolean =
                    routeMainFrame(Uri.parse(url))

                override fun shouldInterceptRequest(
                    view: WebView,
                    request: WebResourceRequest,
                ): android.webkit.WebResourceResponse? {
                    if (!request.isForMainFrame || EmbeddedOidcContract.isTrustedMainFrameDestination(
                            request.url,
                            serverRoot,
                            authorizationUri,
                            authorizationRequest.redirectUri,
                        )
                    ) {
                        return null
                    }
                    view.post { showNavigationBlocked() }
                    return android.webkit.WebResourceResponse(
                        "text/plain",
                        "UTF-8",
                        403,
                        "Forbidden",
                        mapOf("Cache-Control" to "no-store"),
                        ByteArrayInputStream(ByteArray(0)),
                    )
                }

                override fun onPageStarted(view: WebView, url: String, favicon: android.graphics.Bitmap?) {
                    val uri = Uri.parse(url)
                    if (EmbeddedOidcContract.isExpectedRedirect(authorizationRequest.redirectUri, uri)) {
                        finishAuthorization(uri)
                        view.stopLoading()
                        return
                    }
                    if (!EmbeddedOidcContract.isTrustedWebOrigin(uri, serverRoot, authorizationUri)) {
                        view.stopLoading()
                        showNavigationBlocked()
                        return
                    }
                    if (registrationFlow && EmbeddedOidcContract.isPublicLogin(serverRoot, uri)) {
                        view.stopLoading()
                        handOffRegistrationToAuthorization()
                        return
                    }
                    errorView.visibility = View.GONE
                }

                override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                    if (request.isForMainFrame) showLoadError()
                }

                override fun onReceivedHttpError(
                    view: WebView,
                    request: WebResourceRequest,
                    errorResponse: android.webkit.WebResourceResponse,
                ) {
                    if (request.isForMainFrame && errorResponse.statusCode >= 500) showLoadError()
                }

                override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: android.net.http.SslError) {
                    handler.cancel()
                    showLoadError()
                }
            }
        }
        errorView = TextView(this).apply {
            text = getString(R.string.embedded_identity_load_failed)
            setTextColor(Color.WHITE)
            setBackgroundColor(Color.rgb(90, 28, 35))
            setPadding(dp(18), dp(14), dp(18), dp(14))
            visibility = View.GONE
        }
        content.addView(webView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        content.addView(
            errorView,
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT),
        )
        root.addView(toolbar, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        root.addView(progress, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(3)))
        root.addView(content, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        setContentView(root)
    }

    private fun routeMainFrame(uri: Uri): Boolean {
        if (EmbeddedOidcContract.isExpectedRedirect(authorizationRequest.redirectUri, uri)) {
            finishAuthorization(uri)
            return true
        }
        if (registrationFlow && EmbeddedOidcContract.isPublicLogin(serverRoot, uri)) {
            handOffRegistrationToAuthorization()
            return true
        }
        if (EmbeddedOidcContract.isTrustedWebOrigin(uri, serverRoot, authorizationUri)) return false
        showNavigationBlocked()
        return true
    }

    private fun handOffRegistrationToAuthorization() {
        registrationFlow = false
        registrationHandedOffToAuthorization = true
        webView.loadUrl(authorizationUri.toString())
    }

    private fun finishAuthorization(callback: Uri) {
        if (terminalResultDelivered) return
        terminalResultDelivered = true
        val result = EmbeddedOidcContract.authorizationResult(authorizationRequest, callback)
        setResult(Activity.RESULT_OK, result)
        finish()
    }

    private fun finishWithInvalidRequest() {
        if (terminalResultDelivered) return
        terminalResultDelivered = true
        setResult(Activity.RESULT_OK, AuthorizationException.AuthorizationRequestErrors.INVALID_REQUEST.toIntent())
        finish()
    }

    private fun cancelFlow() {
        if (terminalResultDelivered) return
        terminalResultDelivered = true
        setResult(Activity.RESULT_CANCELED)
        finish()
    }

    private fun showLoadError() {
        errorView.text = getString(R.string.embedded_identity_load_failed)
        errorView.visibility = View.VISIBLE
    }

    private fun showNavigationBlocked() {
        if (terminalResultDelivered || !::errorView.isInitialized) return
        progress.visibility = View.GONE
        errorView.text = getString(R.string.embedded_identity_navigation_blocked)
        errorView.visibility = View.VISIBLE
    }

    override fun onDestroy() {
        if (::webView.isInitialized) {
            webView.stopLoading()
            webView.webChromeClient = null
            webView.webViewClient = WebViewClient()
            webView.destroy()
        }
        super.onDestroy()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private companion object {
        const val STATE_REGISTRATION_FLOW = "embedded_oidc.registration_flow"
        const val STATE_REGISTRATION_HANDED_OFF = "embedded_oidc.registration_handed_off"
    }
}

internal data class EmbeddedOidcLaunch(
    val request: AuthorizationRequest,
    val initialUri: Uri,
    val registrationFlow: Boolean,
)

internal object EmbeddedOidcContract {
    private const val EXTRA_REQUEST = "dev.termlink.product.embedded_oidc.REQUEST"
    private const val EXTRA_INITIAL_URI = "dev.termlink.product.embedded_oidc.INITIAL_URI"
    private const val EXTRA_REGISTRATION = "dev.termlink.product.embedded_oidc.REGISTRATION"

    fun createIntent(context: Context, request: AuthorizationRequest, initialUri: Uri, registrationFlow: Boolean): Intent =
        Intent(context, EmbeddedOidcActivity::class.java).apply {
            putExtra(EXTRA_REQUEST, request.jsonSerializeString())
            putExtra(EXTRA_INITIAL_URI, initialUri.toString())
            putExtra(EXTRA_REGISTRATION, registrationFlow)
        }

    fun readLaunch(intent: Intent): EmbeddedOidcLaunch {
        val request = AuthorizationRequest.jsonDeserialize(requireNotNull(intent.getStringExtra(EXTRA_REQUEST)))
        val initialUri = Uri.parse(requireNotNull(intent.getStringExtra(EXTRA_INITIAL_URI)))
        return EmbeddedOidcLaunch(request, initialUri, intent.getBooleanExtra(EXTRA_REGISTRATION, false))
    }

    fun registrationUri(serverRoot: String): Uri = Uri.parse(serverRoot.trimEnd('/') + "/register")

    fun isTrustedLaunch(launch: EmbeddedOidcLaunch, serverRoot: Uri): Boolean {
        val requestUri = launch.request.toUri()
        val initialMatches = if (launch.registrationFlow) {
            sameResource(launch.initialUri, registrationUri(serverRoot.toString()))
        } else {
            launch.initialUri == requestUri
        }
        return initialMatches &&
            isHttpsOrAllowedLocal(serverRoot) &&
            isHttpsOrAllowedLocal(requestUri) &&
            sameResource(Uri.parse("dev.termlink:/oauth2redirect"), launch.request.redirectUri)
    }

    fun isPublicLogin(serverRoot: Uri, candidate: Uri): Boolean =
        sameOrigin(serverRoot, candidate) && candidate.path == "/login" && candidate.fragment == null

    fun isTrustedWebOrigin(candidate: Uri, serverRoot: Uri, authorizationUri: Uri): Boolean =
        isHttpsOrAllowedLocal(candidate) && (sameOrigin(candidate, serverRoot) || sameOrigin(candidate, authorizationUri))

    fun isTrustedMainFrameDestination(
        candidate: Uri,
        serverRoot: Uri,
        authorizationUri: Uri,
        redirectUri: Uri,
    ): Boolean = isExpectedRedirect(redirectUri, candidate) ||
        isTrustedWebOrigin(candidate, serverRoot, authorizationUri)

    fun isExpectedRedirect(expected: Uri, candidate: Uri): Boolean =
        expected.scheme.equals(candidate.scheme, ignoreCase = true) &&
            expected.encodedAuthority == candidate.encodedAuthority &&
            expected.path == candidate.path

    fun authorizationResult(request: AuthorizationRequest, callback: Uri): Intent {
        if (!isExpectedRedirect(request.redirectUri, callback)) {
            return AuthorizationException.AuthorizationRequestErrors.INVALID_REQUEST.toIntent()
        }
        val stateValues = callback.getQueryParameters("state")
        if (stateValues.size != 1 || request.state == null || stateValues.single() != request.state) {
            return AuthorizationException.AuthorizationRequestErrors.STATE_MISMATCH.toIntent()
        }
        val codeCount = callback.getQueryParameters("code").size
        val errorCount = callback.getQueryParameters("error").size
        if (codeCount + errorCount != 1) {
            return AuthorizationException.AuthorizationRequestErrors.INVALID_REQUEST.toIntent()
        }
        return runCatching {
            if (errorCount == 1) {
                AuthorizationException.fromOAuthRedirect(callback).toIntent()
            } else {
                AuthorizationResponse.Builder(request).fromUri(callback).build().toIntent()
            }
        }.getOrElse { AuthorizationException.AuthorizationRequestErrors.INVALID_REQUEST.toIntent() }
    }

    private fun sameResource(left: Uri, right: Uri): Boolean =
        sameOrigin(left, right) && left.path == right.path && left.query == right.query && left.fragment == right.fragment

    private fun sameOrigin(left: Uri, right: Uri): Boolean =
        left.scheme.equals(right.scheme, ignoreCase = true) &&
            left.host.equals(right.host, ignoreCase = true) &&
            effectivePort(left) == effectivePort(right) &&
            left.userInfo == null && right.userInfo == null

    private fun effectivePort(uri: Uri): Int = when {
        uri.port >= 0 -> uri.port
        uri.scheme.equals("https", ignoreCase = true) -> 443
        else -> 80
    }

    private fun isHttpsOrAllowedLocal(uri: Uri): Boolean =
        uri.scheme.equals("https", ignoreCase = true) ||
            BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST &&
            uri.scheme.equals("http", ignoreCase = true) &&
            (uri.host == "localhost" || uri.host == "127.0.0.1" || uri.host == "10.0.2.2")
}

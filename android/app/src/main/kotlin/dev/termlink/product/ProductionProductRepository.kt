package dev.termlink.product

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.view.KeyEvent
import androidx.room.withTransaction
import dev.termlink.billing.BillingCoordinator
import dev.termlink.billing.BillingServerGateway
import dev.termlink.billing.BillingSnapshot
import dev.termlink.billing.PlayBillingAdapter
import dev.termlink.crypto.DeviceKeyVault
import dev.termlink.crypto.HistoryCrypto
import dev.termlink.crypto.SessionCrypto
import dev.termlink.database.TermLinkDatabase
import dev.termlink.history.HistoryRepository
import dev.termlink.harness.R
import dev.termlink.model.CatalogPlan
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.Entitlement
import dev.termlink.model.HistoryObject
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.InputSequenceProvider
import dev.termlink.network.production.ProductionApiClient
import dev.termlink.network.production.ProductionRealtimeSessionClient
import dev.termlink.network.production.ProductionRealtimeSessionConfig
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.HistoryChunkV1
import dev.termlink.protocol.realtime.v1.HistoryEventV1
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.LeasePayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.ResizePayload
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.time.Instant
import java.net.URI
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.isActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import kotlinx.serialization.json.buildJsonObject
import net.openid.appauth.AuthState
import net.openid.appauth.AuthorizationException
import net.openid.appauth.AuthorizationRequest
import net.openid.appauth.AuthorizationResponse
import net.openid.appauth.AuthorizationService
import net.openid.appauth.ResponseTypeValues
import net.openid.appauth.TokenRequest
import net.openid.appauth.TokenResponse
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.concurrent.thread

internal class ProductionProductRepository(
    private val activity: Activity,
    private val launchAuthorization: (Intent) -> Unit,
) : ProductRepository {
    private val sessionStore = OidcSessionStore(activity)
    private val keyVault = DeviceKeyVault(activity)
    private val database = TermLinkDatabase.open(activity)
    private val historyRepository = HistoryRepository(database)
    private val authService = AuthorizationService(activity)
    private val http = OkHttpClient()
    private val nativeAuthHttp = secureNativeHttpClient()
    private val nativeOidc = NativeOidcClient(nativeAuthHttp, Json { ignoreUnknownKeys = true })
    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val liveSessions = ConcurrentHashMap<UUID, LiveProductSession>()
    private val historyEngines = ConcurrentHashMap<UUID, TermuxTerminalEngine>()
    private val historyDownloads = ConcurrentHashMap<UUID, HistoryDownloadRequest>()
    private val liveSessionLock = Any()
    private val accessStateLock = Mutex()
    private val refreshStateLock = Mutex()
    private val keyMetadataRefreshLock = Mutex()
    private val tokenRefresh = TokenRefreshCoordinator { requestFreshAccessToken() }
    // The first production client is intentionally pinned to the public service.
    // Migrate older installs that may still have a locally edited endpoint.
    private val initialServerUrl = PUBLIC_SERVER_URL.also {
        if (sessionStore.serverUrl != it) sessionStore.serverUrl = it
    }
    private val billing = BillingCoordinator(
        adapter = PlayBillingAdapter(activity, activity),
        gateway = object : BillingServerGateway {
            override suspend fun verifyGooglePurchase(productId: String, purchaseToken: String): Boolean = withApi { api ->
                api.post(
                    "/v1/billing/google/verify",
                    buildJsonObject {
                        put("product_id", productId)
                        put("purchase_token", purchaseToken)
                    },
                )
                true
            }

            override suspend fun refreshEntitlement() {
                refresh()
            }
        },
        scope = scope,
    )
    private var authState: AuthState? = sessionStore.loadAuthState()
    private var storedIdentity: DeviceKeyVault.StoredIdentity? = null
    private var accountScope: String? = sessionStore.accountScope
    private var oidcConfiguration: NativeOidcConfiguration? = null
    private var closed = false

    private val mutableSnapshot = MutableStateFlow(
        ProductSnapshot(
            signedIn = authState?.isAuthorized == true,
            configured = true,
            authenticationState = if (authState?.isAuthorized == true) {
                ProductAuthenticationState.AUTHENTICATED
            } else {
                ProductAuthenticationState.SIGNED_OUT
            },
            connectionState = if (authState?.isAuthorized == true) {
                ProductConnectionState.CONNECTING
            } else {
                ProductConnectionState.CONFIGURED
            },
            enrollmentState = ProductEnrollmentState.UNKNOWN,
            serverUrl = initialServerUrl,
        ),
    )
    override val snapshot: StateFlow<ProductSnapshot> = mutableSnapshot.asStateFlow()
    private val billingStateJob = scope.launch {
        billing.snapshot.collect { state ->
            mutableSnapshot.value = mutableSnapshot.value.copy(billing = state)
        }
    }

    init {
        if (authState?.isAuthorized == true) {
            scope.launch { runCatching { refresh() } }
        }
    }

    override suspend fun configureServer(url: String) {
        val normalized = PUBLIC_SERVER_URL
        val changed = normalized != sessionStore.serverUrl
        if (changed) {
            closeLiveSessions()
            closeHistoryEngines()
            clearRoomCache()
            authState = null
            sessionStore.clearAuthState()
            storedIdentity?.identity?.destroy()
            storedIdentity = null
            accountScope = null
            sessionStore.accountScope = null
            oidcConfiguration = null
        }
        sessionStore.serverUrl = normalized
        billing.reset()
        mutableSnapshot.value = ProductSnapshot(
            configured = true,
            authenticationState = ProductAuthenticationState.SIGNED_OUT,
            connectionState = ProductConnectionState.CONFIGURED,
            serverUrl = normalized,
        )
    }

    override suspend fun signIn(username: String, password: CharArray) {
        check(!closed) { "Repository is closed" }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            authenticationState = ProductAuthenticationState.NATIVE_CREDENTIALS,
            connectionState = ProductConnectionState.CONFIGURED,
            connectionError = null,
        )
        try {
            val configuration = loadOidcConfiguration()
            val payload = withContext(Dispatchers.IO) {
                nativeOidc.passwordGrant(configuration, username, password)
            }
            val subject = OidcJwtClaims.accessTokenSubject(payload.accessToken, configuration.issuer, configuration.clientId)
            activateAccount(configuration, subject, payload)
            bootstrapAuthenticatedSession()
        } catch (error: NativeOidcException) {
            publishFailure(error)
            if (error.browserFallbackAvailable) {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    authenticationState = ProductAuthenticationState.BROWSER_SETUP_REQUIRED,
                    connectionState = ProductConnectionState.ERROR,
                    connectionError = null,
                )
            }
            throw error
        } catch (error: Throwable) {
            publishFailure(error)
            throw error
        } finally {
            password.fill('\u0000')
        }
    }

    override suspend fun register(username: String, email: String, password: CharArray, confirm: CharArray) {
        check(!closed) { "Repository is closed" }
        require(username.trim().length in 3..128) { "Username is invalid" }
        require(email.trim().length in 3..320) { "Email is invalid" }
        require(password.size in 8..1024 && password.contentEquals(confirm)) { "Password is invalid" }
        try {
            val root = PUBLIC_SERVER_URL
            val body = buildJsonObject {
                put("username", username.trim())
                put("email", email.trim())
                put("password", String(password))
                put("password_confirm", String(confirm))
            }
            val request = Request.Builder()
                .url("$root/v1/auth/register")
                .post(body.toString().toRequestBody("application/json; charset=utf-8".toMediaType()))
                .header("Cache-Control", "no-store")
                .build()
            withContext(Dispatchers.IO) {
                nativeAuthHttp.newCall(request).execute().use { response ->
                    if (!response.isSuccessful) throw IllegalStateException("Account registration was rejected")
                }
            }
        } finally {
            password.fill('\u0000')
            confirm.fill('\u0000')
        }
    }

    override suspend fun beginBrowserRegistration() = beginBrowserAuth(BrowserAuthFlow.REGISTRATION)

    override suspend fun beginBrowserAccountSetup() = beginBrowserAuth(BrowserAuthFlow.ACCOUNT_SETUP)

    private suspend fun beginBrowserAuth(flow: BrowserAuthFlow) {
        check(!closed) { "Repository is closed" }
        try {
            val configuration = loadOidcConfiguration()
            mutableSnapshot.value = mutableSnapshot.value.copy(authenticationState = ProductAuthenticationState.BROWSER_SETUP_REQUIRED)
            authState = AuthState(configuration.serviceConfiguration)
            val requestBuilder = AuthorizationRequest.Builder(
                configuration.serviceConfiguration,
                configuration.clientId,
                ResponseTypeValues.CODE,
                configuration.redirectUri,
            ).setScope("openid profile offline_access")
            if (flow == BrowserAuthFlow.REGISTRATION) {
                // OIDC prompt=create is the interoperable Keycloak
                // registration surface; credentials stay in the browser.
                requestBuilder.setPrompt(BrowserAuthPolicy.REGISTRATION_PROMPT)
            } else {
                // A normal forced browser login lets Keycloak execute whichever
                // Required Actions are actually pending (temporary password,
                // MFA, passkey, profile, or email) instead of guessing one.
                requestBuilder.setPrompt(BrowserAuthPolicy.ACCOUNT_SETUP_PROMPT)
            }
            val request = requestBuilder.build()
            launchAuthorization(authService.getAuthorizationRequestIntent(request))
        } catch (error: Throwable) {
            publishFailure(error)
            throw error
        }
    }

    fun handleAuthorizationResult(data: Intent?) {
        if (data == null) {
            publishFailure(IllegalStateException("OIDC sign-in was canceled"))
            return
        }
        val response = AuthorizationResponse.fromIntent(data)
        val exception = AuthorizationException.fromIntent(data)
        val state = authState ?: AuthState()
        state.update(response, exception)
        authState = state
        if (response == null) {
            publishFailure(exception ?: IllegalStateException("OIDC authorization failed"))
            return
        }
        authService.performTokenRequest(response.createTokenExchangeRequest()) { tokenResponse, tokenException ->
            state.update(tokenResponse, tokenException)
            if (tokenResponse == null) {
                publishFailure(tokenException ?: IllegalStateException("OIDC token exchange failed"))
                return@performTokenRequest
            }
            val configuration = oidcConfiguration
            val accessToken = tokenResponse.accessToken
            if (configuration == null || accessToken.isNullOrBlank()) {
                publishFailure(IllegalStateException("OIDC access token is unavailable"))
                return@performTokenRequest
            }
            val subject = try {
                OidcJwtClaims.accessTokenSubject(accessToken, configuration.issuer, configuration.clientId)
            } catch (error: Throwable) {
                publishFailure(error)
                return@performTokenRequest
            }
            val cleanState = CredentialFreeAuthState.sanitize(state)
            scope.launch {
                runCatching {
                    // Persist the issuer+subject scope first. If the process is
                    // killed after AuthState is saved, startup must never see
                    // an authorized session without an account-scoped key.
                    establishAccountScope(AccountScope.canonical(configuration.issuer, subject))
                    authState = cleanState
                    sessionStore.saveAuthState(cleanState)
                    mutableSnapshot.value = mutableSnapshot.value.copy(
                        signedIn = true,
                        configured = true,
                        authenticationState = ProductAuthenticationState.AUTHENTICATED,
                        connectionState = ProductConnectionState.CONNECTING,
                        connectionError = null,
                        lastSyncedAt = null,
                    )
                    bootstrapAuthenticatedSession()
                }.onFailure(::publishFailure)
            }
        }
    }

    override suspend fun signOut() {
        authState = null
        sessionStore.clearAuthState()
        closeLiveSessions()
        closeHistoryEngines()
        clearRoomCache()
        storedIdentity?.identity?.destroy()
        storedIdentity = null
        accountScope = null
        sessionStore.accountScope = null
        oidcConfiguration = null
        billing.reset()
        mutableSnapshot.value = ProductSnapshot(
            configured = true,
            authenticationState = ProductAuthenticationState.SIGNED_OUT,
            connectionState = ProductConnectionState.CONFIGURED,
            enrollmentState = ProductEnrollmentState.UNKNOWN,
            serverUrl = PUBLIC_SERVER_URL,
        )
    }

    override suspend fun confirmEnrollment() {
        check(mutableSnapshot.value.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
            "No pending enrollment confirmation is available"
        }
        withApi { api ->
            api.post("/v1/devices/self/trust", buildJsonObject { })
            val self = ProductResponseParser.self(api.get("/v1/devices/self").jsonObject)
            if (self.trustState == DeviceTrustState.REVOKED) {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    enrollmentState = ProductEnrollmentState.REVOKED,
                    enrollmentConfirmationRequired = false,
                    devices = listOf(self),
                )
                return@withApi
            }
            check(self.trustState == DeviceTrustState.TRUSTED) { "The server did not trust this device" }
            mutableSnapshot.value = mutableSnapshot.value.copy(
                enrollmentState = ProductEnrollmentState.TRUSTED,
                enrollmentConfirmationRequired = false,
                accountLabel = "Connected account",
                devices = listOf(self),
            )
            refreshWithApiPublished(api)
        }
    }

    override suspend fun cancelEnrollment() {
        if (mutableSnapshot.value.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
            // Cancellation is a real auth cancellation: erase transient token,
            // runtime engines, and Room cache, while signOut deliberately keeps
            // the account-scoped identity in the vault for a later login.
            signOut()
        }
    }

    override suspend fun approveDevice(deviceId: UUID) = withApi { api ->
        val pc = mutableSnapshot.value.pcs.firstOrNull() ?: error("No manageable PC is available")
        val result = api.post(
            "/v1/pairing/challenges",
            buildJsonObject {
                put("target_device_id", deviceId.toString())
                put("pc_id", pc.id.toString())
                // Legacy pairing callers are retained for compatibility, but
                // the first release gives same-account Android devices the
                // maximum account-scoped permission by default.
                put("permission", PRODUCT_DEFAULT_ANDROID_PERMISSION)
            },
        ).jsonObject
        val challenge = PairingChallengeDisplay(
            challengeId = UUID.fromString(result.requiredString("challenge_id")),
            code = PairingInputParser.normalizeCode(result.requiredString("code")),
            challengeNonce = PairingInputParser.normalizeNonce(result.requiredString("challenge_nonce")),
            expiresAt = result.optionalInstant("expires_at"),
            targetName = result["target"]?.jsonObject?.get("name")?.jsonPrimitive?.contentOrNull,
        )
        mutableSnapshot.value = mutableSnapshot.value.copy(
            pairingChallenge = PairingChallengeState(
                challengeId = challenge.challengeId,
                code = challenge.code,
                challengeNonce = challenge.challengeNonce,
                expiresAt = challenge.expiresAt,
                targetName = challenge.targetName,
            ),
        )
        appendActivity(
            "Pairing challenge created",
            "${challenge.targetName ?: "Target device"} | expires ${challenge.expiresAt ?: "soon"}",
            Severity.WARNING,
        )
    }

    override suspend fun completePairing(challengeId: String, code: String, challengeNonce: String) = withApi { api ->
        val input = PairingInputParser.parse(challengeId, code, challengeNonce)
        api.post(
            "/v1/pairing/challenges/${input.challengeId}/complete",
            buildJsonObject {
                put("code", input.code)
                put("challenge_nonce", input.challengeNonce)
            },
        )
        mutableSnapshot.value = mutableSnapshot.value.copy(pairingChallenge = null)
        appendActivity("Device pairing completed", "This device is now trusted by the server", Severity.SUCCESS)
        refreshWithApiPublished(api)
    }

    override suspend fun purchaseProduct(productId: String) {
        billing.purchase(productId)
    }

    override suspend fun restorePurchases() {
        billing.restore()
    }

    override suspend fun revokeDevice(deviceId: UUID) = withApi { api ->
        api.post("/v1/devices/$deviceId/revoke", buildJsonObject { })
        refreshWithApiPublished(api)
    }

    override suspend fun requestControl(sessionId: UUID) {
        freshAccessToken()
        openSession(sessionId)
        liveSessions.getValue(sessionId).client.requestControl()
        appendActivity("Control granted", "Session $sessionId", Severity.SUCCESS)
    }

    override suspend fun releaseControl(sessionId: UUID) {
        val live = liveSessions[sessionId] ?: error("Open the session before releasing control")
        freshAccessToken()
        live.client.releaseControl()
        appendActivity("Control released", "Session $sessionId", Severity.INFO)
    }

    override suspend fun releaseAllControlLeases() {
        // Keep the realtime clients and terminal engines alive for read-only
        // viewing. Only the server-issued control leases are released.
        val active = liveSessions.values.filter {
            it.client.sessionState.value is RemoteSessionState.Controlling
        }
        if (active.isEmpty()) return

        val tokenFailure = runCatching { freshAccessToken() }.exceptionOrNull()
        if (tokenFailure != null) publishFailure(tokenFailure)
        var firstFailure: Throwable? = null
        if (tokenFailure != null) firstFailure = tokenFailure
        active.forEach { live ->
            runCatching { live.client.releaseControl() }
                .onFailure { error -> firstFailure = firstFailure ?: error }
        }
        if (firstFailure != null) {
            publishFailure(firstFailure!!)
        } else {
            appendActivity("Control leases released", "Device backgrounded", Severity.INFO)
        }
    }

    override suspend fun openSession(sessionId: UUID) {
        if (liveSessions.containsKey(sessionId)) return
        closeLiveSessionsExcept(emptySet())
        val product = mutableSnapshot.value
        val summary = product.sessions.firstOrNull { it.id == sessionId }
            ?: error("The selected session is unavailable")
        require(summary.state != SessionState.EXITED) { "The selected session has exited" }
        val pc = product.pcs.firstOrNull { it.id == summary.pcId }
            ?: error("The session PC is unavailable")
        val pcDevice = product.devices.firstOrNull {
            it.id == pc.deviceId && it.type == DeviceType.WINDOWS_PC && it.trustState == DeviceTrustState.TRUSTED
        } ?: error("The session PC is not a trusted device")
        require(pcDevice.exchangePublicKey.size == 32) { "The PC exchange public key is unavailable" }
        freshAccessToken()
        val root = PUBLIC_SERVER_URL
        val stored = identity()
        val engine = TermuxTerminalEngine(
            initialSize = TerminalSize(summary.columns.coerceAtLeast(1), summary.rows.coerceAtLeast(1)),
        )
        val client = try {
            val restoredHistory = restoreLiveHistory(
                session = summary,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                engine = engine,
            )
            val ackRendererGeneration = AtomicLong(restoredHistory.rendererGeneration)
            ProductionRealtimeSessionClient(
                engine = engine,
                config = ProductionRealtimeSessionConfig(
                    rootUrl = root,
                    tokenProvider = AccessTokenProvider {
                        authState?.accessToken ?: error("OIDC access token is unavailable")
                    },
                    androidDeviceId = stored.deviceId,
                    pcDeviceId = pc.deviceId,
                    sessionId = sessionId,
                    keyEpoch = pc.keyEpoch,
                    pcExchangePublicKey = pcDevice.exchangePublicKey.copyOf(),
                ),
                identity = stored.identity,
                parentScope = scope,
                httpClient = http,
                inputSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveInputSequenceRange(sessionId)
                },
                envelopeSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveEnvelopeSequenceRange(sessionId)
                },
                initialAcknowledgedSequence = restoredHistory.acknowledgement,
                initialObservedLocalInputEpoch = restoredHistory.localInputEpoch,
                acknowledgementObserver = { sequence ->
                    historyRepository.putAckCursor(
                        sessionId,
                        LIVE_OUTPUT_ACK_DIRECTION,
                        ackRendererGeneration.get(),
                        sequence.toULong(),
                    ).toLong()
                },
                acknowledgementResetObserver = {
                    val reset = historyRepository.resetAckCursor(
                        sessionId,
                        LIVE_OUTPUT_ACK_DIRECTION,
                        ackRendererGeneration.get(),
                    )
                    ackRendererGeneration.set(reset.rendererGeneration)
                },
                authoritativeMetadataRefreshObserver = { refreshLiveKeyMetadata() },
            )
        } catch (error: Throwable) {
            engine.dispose()
            throw error
        }
        val live = LiveProductSession(engine, client)
        val installed = synchronized(liveSessionLock) {
            if (liveSessions.containsKey(sessionId)) false
            else {
                liveSessions[sessionId] = live
                true
            }
        }
        if (!installed) {
            client.close()
            return
        }
        live.stateJob = scope.launch {
            client.sessionState.collect { state ->
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    sessionStates = mutableSnapshot.value.sessionStates + (sessionId to state),
                )
                if (state is RemoteSessionState.HistoryGap && live.recoveryJob?.isActive != true) {
                    live.recoveryJob = scope.launch {
                        recoverLiveHistoryGap(sessionId, live)
                    }
                }
            }
        }
        live.tokenRefreshJob = scope.launch {
            while (isActive) {
                delay(LIVE_TOKEN_REFRESH_INTERVAL_MILLIS)
                runCatching { freshAccessToken() }
                    .onFailure { error ->
                        if (isActive) publishFailure(error)
                        delay(LIVE_TOKEN_REFRESH_RETRY_MILLIS)
                    }
            }
        }
        client.start()
    }

    override fun terminalEngine(sessionId: UUID): TermuxTerminalEngine? = liveSessions[sessionId]?.engine

    override fun historyEngine(historyObjectId: UUID): TermuxTerminalEngine? = historyEngines[historyObjectId]

    override suspend fun stopSession(sessionId: UUID): Unit =
        error("Remote shell termination is not exposed by the production protocol")

    override suspend fun sendPaste(sessionId: UUID, text: String) {
        require(text.isNotEmpty()) { "Paste text is empty" }
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        check(live.engine.submitPaste(text)) { "Request control before sending input" }
    }

    override suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction) {
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        val accepted = when (action) {
            TerminalAction.ESCAPE -> live.engine.submitKeyCode(KeyEvent.KEYCODE_ESCAPE)
            TerminalAction.TAB -> live.engine.submitKeyCode(KeyEvent.KEYCODE_TAB)
            TerminalAction.ENTER -> live.engine.submitKeyCode(KeyEvent.KEYCODE_ENTER)
            TerminalAction.ARROW_LEFT -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_LEFT)
            TerminalAction.ARROW_UP -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP)
            TerminalAction.ARROW_DOWN -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_DOWN)
            TerminalAction.ARROW_RIGHT -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_RIGHT)
            TerminalAction.CTRL_C -> live.engine.submitCodePoint('c'.code, controlDown = true)
        }
        check(accepted) { "Request control before sending input" }
    }

    override suspend fun replayHistory(entry: HistoryEntry) {
        require(entry.historyObject.kind == HistoryObject.Kind.CHUNK) { "This history object cannot be replayed by this client" }
        val download = historyDownloads[entry.historyObject.id] ?: error("Refresh history before replaying this object")
        val product = mutableSnapshot.value
        val session = product.sessions.firstOrNull { it.id == entry.sessionId } ?: error("The history session is unavailable")
        val pc = product.pcs.firstOrNull { it.id == session.pcId } ?: error("The history PC is unavailable")
        val pcDevice = product.devices.firstOrNull {
            it.id == pc.deviceId && it.type == DeviceType.WINDOWS_PC && it.trustState == DeviceTrustState.TRUSTED
        } ?: error("The history PC is not trusted")
        val stored = identity()
        val historyEpoch = historyKeyEpoch(stored.deviceId, entry.historyObject.keyId)
        require(historyEpoch != null && historyEpoch <= pc.keyEpoch) {
            "The selected history key is not available on this device"
        }
        val chunk = openHistoryChunk(entry, pc, pcDevice, stored, download)

        val engine = historyEngines.getOrPut(entry.historyObject.id) { TermuxTerminalEngine() }
        engine.setReadOnly(true)
        engine.clear()
        engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
        chunk.eventsList.forEach { event ->
            applyHistoryEvent(engine, event)
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            replayedHistoryObjectIds = mutableSnapshot.value.replayedHistoryObjectIds + entry.historyObject.id,
        )
        appendActivity("History replayed", entry.sessionLabel, Severity.SUCCESS)
    }

    override suspend fun refresh() {
        check(!closed) { "Repository is closed" }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            connectionState = if (mutableSnapshot.value.signedIn) {
                ProductConnectionState.CONNECTING
            } else if (mutableSnapshot.value.configured) {
                ProductConnectionState.CONFIGURED
            } else {
                ProductConnectionState.SIGNED_OUT
            },
            connectionError = null,
        )
        try {
            withApi { api -> refreshWithApi(api) }
        } catch (error: Throwable) {
            publishFailure(error)
            throw error
        }
    }

    private suspend fun refreshWithApi(api: ProductionApiClient): Unit = refreshStateLock.withLock {
        val self = ProductResponseParser.self(api.get("/v1/devices/self").jsonObject)
        if (self.trustState != DeviceTrustState.TRUSTED) {
            clearAccessControlledState(self)
            return
        }
        val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
        val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
        val sessions = pcs.flatMap { pc ->
            ProductResponseParser.itemObjects(api.get("/v1/pcs/${pc.id}/sessions").jsonObject).map { it.toSession() }
        }
        val entitlement = api.get("/v1/account/entitlements").jsonObject.toEntitlement()
        val catalogPlans = ProductResponseParser.catalogPlanObjects(api.get("/v1/billing/plans").jsonObject)
            .map { it.toCatalogPlan() }
        val catalog = catalogPlans.firstOrNull { it.key == entitlement.planKey }
        // Reject an epoch rollback or same-epoch public-key substitution before
        // any untrusted metadata reaches Room. Refreshes are serialized so a
        // slower response cannot overwrite a newer validated snapshot.
        validateAuthoritativeSessionKeys(devices, pcs, sessions)
        historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
        sessions.forEach { historyRepository.upsertSession(it) }
        val ownDeviceId = identity().deviceId
        val refreshedDownloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val historyEntries = sessions.flatMap { session ->
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@flatMap emptyList()
            val response = try {
                api.get("/v1/sessions/${session.id}/history").jsonObject
            } catch (error: TermLinkApiException) {
                if (error.status == 503) return@flatMap emptyList()
                throw error
            }
            ProductResponseParser.itemObjects(response).mapNotNull { item ->
                val keyId = item.requiredString("key_id")
                val historyEpoch = historyKeyEpoch(ownDeviceId, keyId)
                if (!item.requiredString("kind").equals("chunk", ignoreCase = true) ||
                    historyEpoch == null || historyEpoch > pc.keyEpoch
                ) {
                    return@mapNotNull null
                }
                val parsed = item.toHistoryObject(session.id)
                val request = item.getValue("download").jsonObject.toHistoryDownloadRequest()
                refreshedDownloads[parsed.id] = request
                val pcName = pcs.firstOrNull { it.id == session.pcId }?.name ?: "Windows PC"
                HistoryEntry(session.id, "$pcName | ${session.shellType}", parsed, item.optionalInstant("created_at"))
            }
        }
        historyDownloads.clear()
        historyDownloads.putAll(refreshedDownloads)
        rotateLiveSessionKeys(devices, pcs, sessions)
        closeHistoryEnginesExcept(historyEntries.mapTo(mutableSetOf()) { it.historyObject.id })
        val trustedPcIds = pcs.asSequence()
            .filter { pc ->
                devices.any { device ->
                    device.id == pc.deviceId &&
                        device.type == DeviceType.WINDOWS_PC &&
                        device.trustState == DeviceTrustState.TRUSTED
                }
            }
            .mapTo(mutableSetOf(), PcSummary::id)
        closeLiveSessionsExcept(
            sessions.filter {
                it.state != SessionState.EXITED && it.pcId in trustedPcIds
            }.mapTo(mutableSetOf(), SessionSummary::id),
        )
        val retainedStates = mutableSnapshot.value.sessionStates.filterKeys { id -> sessions.any { it.id == id } }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            signedIn = true,
            configured = true,
            connectionState = ProductConnectionState.CONNECTED,
            connectionError = null,
            enrollmentState = ProductEnrollmentState.TRUSTED,
            enrollmentConfirmationRequired = false,
            accountLabel = "Connected account",
            devices = devices,
            pcs = pcs,
            sessions = sessions,
            sessionStates = retainedStates,
            history = historyEntries,
            replayedHistoryObjectIds = mutableSnapshot.value.replayedHistoryObjectIds.intersect(
                historyEntries.mapTo(mutableSetOf()) { it.historyObject.id },
            ),
            catalogPlans = catalogPlans,
            entitlement = entitlement,
            catalogPlan = catalog,
            lastSyncedAt = Instant.now(),
        )
        scope.launch { billing.syncCatalog(catalogPlans) }
    }

    private suspend fun refreshWithApiPublished(api: ProductionApiClient) {
        try {
            refreshWithApi(api)
        } catch (error: Throwable) {
            publishFailure(error)
            throw error
        }
    }

    private suspend fun <T> withApi(block: suspend (ProductionApiClient) -> T): T = withContext(Dispatchers.IO) {
        val root = PUBLIC_SERVER_URL
        val token = freshAccessToken()
        val identity = identity()
        ProductionApiClient(root, AccessTokenProvider { token }, identity.deviceId, identity.identity, http, allowCleartextForLocalTest = false).use { api ->
            ensureRegistered(api, identity)
            block(api)
        }
    }

    private fun ensureRegistered(api: ProductionApiClient, identity: DeviceKeyVault.StoredIdentity) {
        try {
            api.registerDevice("ANDROID", android.os.Build.MODEL.take(128), "device-key-${identity.deviceId}")
        } catch (error: TermLinkApiException) {
            if (error.status != 409) throw error
            // A registration conflict is not proof that this device is ours.
            // Read the server-authoritative self association and reject any
            // mismatch rather than blindly continuing after a 409.
            val current = runCatching { ProductResponseParser.self(api.get("/v1/devices/self").jsonObject) }
                .getOrElse { throw IllegalStateException("Device registration conflict could not be verified") }
            check(current.id == identity.deviceId) { "Device registration conflict could not be verified" }
        }
    }

    private fun identity(): DeviceKeyVault.StoredIdentity {
        val scope = accountScope ?: error("OIDC account scope is unavailable")
        return storedIdentity ?: keyVault.loadOrCreate(scope).also { storedIdentity = it }
    }

    private suspend fun freshAccessToken(): String = tokenRefresh.freshAccessToken()

    private suspend fun requestFreshAccessToken(): String = suspendCancellableCoroutine { continuation ->
        val state = authState ?: run {
            continuation.resumeWithException(IllegalStateException("Sign in first"))
            return@suspendCancellableCoroutine
        }
        // Persisted AuthState intentionally omits AuthorizationResponse and all
        // original token-request credentials.  AppAuth's refresh helper relies
        // on that discarded request metadata, so every flow (native login and
        // browser PKCE fallback) refreshes through the same bounded, no-cookie
        // token transport instead.
        scope.launch {
            try {
                val configuration = ensureOidcConfiguration()
                val current = state.accessToken
                if (!state.needsTokenRefresh && !current.isNullOrBlank()) {
                    if (continuation.isActive) continuation.resume(current)
                    return@launch
                }
                val refreshToken = state.refreshToken ?: throw IllegalStateException("OIDC refresh token is unavailable")
                val payload = withContext(Dispatchers.IO) { nativeOidc.refresh(configuration, refreshToken) }
                val refreshedSubject = OidcJwtClaims.accessTokenSubject(
                    payload.accessToken,
                    configuration.issuer,
                    configuration.clientId,
                )
                check(accountScope == AccountScope.canonical(configuration.issuer, refreshedSubject)) {
                    "OIDC refresh changed account identity"
                }
                val request = TokenRequest.Builder(configuration.serviceConfiguration, configuration.clientId)
                    // Metadata only. A real refresh request here would require
                    // duplicating the live refresh token inside TokenRequest.
                    .setGrantType(CredentialFreeAuthState.SANITIZED_REQUEST_GRANT_TYPE)
                    .setScope(payload.scope ?: state.scope)
                    .build()
                val tokenResponse = TokenResponse.Builder(request)
                    .setTokenType(payload.tokenType)
                    .setAccessToken(payload.accessToken)
                    .setAccessTokenExpiresIn(payload.expiresInSeconds)
                    .setRefreshToken(payload.refreshToken ?: refreshToken)
                    .setIdToken(payload.idToken ?: state.idToken)
                    .setScope(payload.scope ?: state.scope)
                    .setAdditionalParameters(emptyMap())
                    .build()
                val updated = AuthState(configuration.serviceConfiguration)
                updated.update(tokenResponse, null)
                authState = CredentialFreeAuthState.sanitize(updated)
                sessionStore.saveAuthState(authState ?: updated)
                if (continuation.isActive) continuation.resume(payload.accessToken)
            } catch (error: Throwable) {
                if (continuation.isActive) continuation.resumeWithException(error)
            }
        }
    }

    private suspend fun ensureOidcConfiguration(): NativeOidcConfiguration {
        oidcConfiguration?.let { return it }
        val root = PUBLIC_SERVER_URL
        val client = fetchClientConfiguration(root)
        return withContext(Dispatchers.IO) {
            nativeOidc.discover(client.issuer.toString(), client.clientId, client.redirectUri)
        }.also { oidcConfiguration = it }
    }

    private suspend fun loadOidcConfiguration(): NativeOidcConfiguration = ensureOidcConfiguration()

    private suspend fun activateAccount(
        configuration: NativeOidcConfiguration,
        subject: String,
        payload: NativeTokenPayload,
    ) {
        val nextScope = AccountScope.canonical(configuration.issuer, subject)
        establishAccountScope(nextScope)
        val request = TokenRequest.Builder(configuration.serviceConfiguration, configuration.clientId)
            .setGrantType(TokenRequest.GRANT_TYPE_PASSWORD)
            .setScope(payload.scope ?: "openid profile offline_access")
            .build()
        val tokenResponse = TokenResponse.Builder(request)
            .setTokenType(payload.tokenType)
            .setAccessToken(payload.accessToken)
            .setAccessTokenExpiresIn(payload.expiresInSeconds)
            .setRefreshToken(payload.refreshToken)
            .setIdToken(payload.idToken)
            .setScope(payload.scope ?: "openid profile offline_access")
            .setAdditionalParameters(emptyMap())
            .build()
        val clean = AuthState(configuration.serviceConfiguration)
        clean.update(tokenResponse, null)
        authState = CredentialFreeAuthState.sanitize(clean)
        sessionStore.saveAuthState(authState ?: clean)
    }

    private suspend fun bootstrapAuthenticatedSession() {
        val configuration = ensureOidcConfiguration()
        val accessToken = authState?.accessToken ?: error("OIDC access token is unavailable")
        val subject = OidcJwtClaims.accessTokenSubject(accessToken, configuration.issuer, configuration.clientId)
        establishAccountScope(AccountScope.canonical(configuration.issuer, subject))
        withApi { api ->
            val self = ProductResponseParser.self(api.get("/v1/devices/self").jsonObject)
            when (self.trustState) {
                DeviceTrustState.TRUSTED -> {
                    mutableSnapshot.value = mutableSnapshot.value.copy(
                        signedIn = true,
                        configured = true,
                        connectionState = ProductConnectionState.CONNECTED,
                        enrollmentState = ProductEnrollmentState.TRUSTED,
                        enrollmentConfirmationRequired = false,
                        accountLabel = "Connected account",
                        devices = listOf(self),
                    )
                    refreshWithApi(api)
                }
                DeviceTrustState.PENDING -> {
                    clearAccessControlledState(self, prompt = true)
                }
                DeviceTrustState.REVOKED -> {
                    clearAccessControlledState(self, prompt = false)
                }
            }
        }
    }

    private suspend fun establishAccountScope(nextScope: String) {
        if (accountScope != null && accountScope != nextScope) {
            closeLiveSessions()
            closeHistoryEngines()
            clearRoomCache()
            billing.reset()
        }
        if (accountScope != nextScope) {
            storedIdentity?.identity?.destroy()
            storedIdentity = null
        }
        accountScope = nextScope
        sessionStore.accountScope = nextScope
    }

    private suspend fun fetchClientConfiguration(root: String): ClientConfiguration = withContext(Dispatchers.IO) {
        val request = Request.Builder().url("$root/v1/client-config").get().build()
        nativeAuthHttp.newCall(request).execute().use { response ->
            check(response.isSuccessful) { "TermLink client configuration returned HTTP ${response.code}" }
            val raw = readBoundedOidcResponse(response.body, 64 * 1024, NativeOidcFailure.INVALID_CONFIGURATION)
            val body = json.parseToJsonElement(raw).jsonObject
            val issuer = Uri.parse(body.requiredString("oidc_issuer"))
            val redirect = Uri.parse(body.requiredString("redirect_uri"))
            OidcEndpointPolicy.canonicalIssuer(issuer)
            check(redirect.scheme.equals("dev.termlink", ignoreCase = true)) { "Server returned an unsupported redirect URI" }
            ClientConfiguration(
                issuer = issuer,
                clientId = body.requiredString("oidc_client_id"),
                redirectUri = redirect,
            )
        }
    }

    /**
     * A server refresh can downgrade this device from trusted to pending or
     * revoked.  Close all access-bearing runtime state and erase the local
     * encrypted session/history rows before exposing the downgraded snapshot.
     * The device identity is intentionally retained so the pairing flow can
     * complete without manufacturing a new device.
     */
    private suspend fun clearAccessControlledState(
        self: DeviceSummary,
        prompt: Boolean = mutableSnapshot.value.enrollmentConfirmationRequired,
    ) = accessStateLock.withLock {
        closeLiveSessions()
        closeHistoryEngines()
        billing.reset()
        clearRoomCache()
        val current = mutableSnapshot.value
        mutableSnapshot.value = current.copy(
            signedIn = true,
            configured = true,
            connectionState = ProductConnectionState.CONNECTED,
            connectionError = null,
            enrollmentState = when (self.trustState) {
                DeviceTrustState.PENDING -> ProductEnrollmentState.PENDING_CONFIRMATION
                DeviceTrustState.REVOKED -> ProductEnrollmentState.REVOKED
                DeviceTrustState.TRUSTED -> ProductEnrollmentState.TRUSTED
            },
            enrollmentConfirmationRequired = prompt && self.trustState == DeviceTrustState.PENDING,
            accountLabel = if (self.trustState == DeviceTrustState.REVOKED) "Revoked account" else "Account setup required",
            devices = listOf(self),
            pcs = emptyList(),
            sessions = emptyList(),
            sessionStates = emptyMap(),
            pairingChallenge = null,
            history = emptyList(),
            replayedHistoryObjectIds = emptySet(),
            catalogPlans = emptyList(),
            entitlement = null,
            catalogPlan = null,
            billing = BillingSnapshot(),
            lastSyncedAt = Instant.now(),
        )
    }

    private suspend fun clearRoomCache() = withContext(Dispatchers.IO) {
        database.withTransaction {
            database.eventChunks().clear()
            database.snapshots().clear()
            database.ackCursors().clear()
            database.sessions().clear()
            database.pcs().clear()
            database.devices().clear()
            database.preferences().clear()
        }
    }

    private fun appendActivity(title: String, detail: String, severity: Severity) {
        mutableSnapshot.value = mutableSnapshot.value.withLocalActivity(
            ActivityEntry(UUID.randomUUID().toString(), Instant.now(), title, sanitizeActivityDetail(detail), severity),
        )
    }

    private fun publishFailure(error: Throwable) {
        val message = safeFailureMessage(error)
        mutableSnapshot.value = mutableSnapshot.value.copy(
            connectionState = ProductConnectionState.ERROR,
            connectionError = message,
        )
        appendActivity("Connection failed", message, Severity.ERROR)
    }

    private fun safeFailureMessage(error: Throwable): String = when (error) {
        is NativeOidcException -> when (error.reason) {
            NativeOidcFailure.INVALID_CREDENTIALS -> activity.getString(R.string.failure_invalid_credentials)
            NativeOidcFailure.ACCOUNT_SETUP_REQUIRED -> activity.getString(R.string.sign_in_account_setup_required)
            NativeOidcFailure.DISCOVERY_FAILED,
            NativeOidcFailure.NETWORK -> activity.getString(R.string.failure_identity_network)
            NativeOidcFailure.INVALID_CONFIGURATION -> activity.getString(R.string.failure_identity_configuration)
            NativeOidcFailure.TOKEN_REQUEST_FAILED -> activity.getString(R.string.failure_identity_rejected)
        }
        is TermLinkApiException -> activity.getString(R.string.failure_server_http, error.status)
        else -> activity.getString(R.string.failure_server_request)
    }

    private fun sanitizeActivityDetail(value: String): String =
        sensitiveIdentifierPattern.replace(value, "session")

    private fun closeLiveSessionsExcept(retained: Set<UUID>) {
        synchronized(liveSessionLock) {
            liveSessions.keys.filterNot(retained::contains).forEach { sessionId ->
                liveSessions.remove(sessionId)?.close()
            }
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            sessionStates = mutableSnapshot.value.sessionStates.filterKeys(retained::contains),
        )
    }

    private suspend fun rotateLiveSessionKeys(
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
        sessions: List<SessionSummary>,
    ) {
        liveSessions.toMap().forEach { (sessionId, live) ->
            val session = sessions.firstOrNull { it.id == sessionId } ?: return@forEach
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@forEach
            val device = devices.firstOrNull {
                it.id == pc.deviceId &&
                    it.type == DeviceType.WINDOWS_PC &&
                    it.trustState == DeviceTrustState.TRUSTED
            } ?: return@forEach
            live.client.rotateKeyMaterial(pc.keyEpoch, device.exchangePublicKey)
        }
    }

    /**
     * Unknown realtime key IDs trigger this narrow authoritative refresh. It
     * intentionally avoids account, billing, and history endpoints so an
     * unavailable history object store cannot block an epoch transition.
     */
    private suspend fun refreshLiveKeyMetadata(): Unit = keyMetadataRefreshLock.withLock {
        withApi { api ->
            val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
            val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
            val sessions = mutableSnapshot.value.sessions
            liveSessions.toMap().forEach { (sessionId, live) ->
                val session = sessions.firstOrNull { it.id == sessionId } ?: return@forEach
                val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@forEach
                val device = devices.firstOrNull {
                    it.id == pc.deviceId &&
                        it.type == DeviceType.WINDOWS_PC &&
                        it.trustState == DeviceTrustState.TRUSTED
                } ?: return@forEach
                live.client.validateKeyMaterialUpdate(pc.keyEpoch, device.exchangePublicKey)
            }
            // Persist the authoritative epoch/public-key pair before any live
            // session can start encrypting or decrypting with the new epoch.
            historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
            rotateLiveSessionKeys(devices, pcs, sessions)
            val trustedPcIds = pcs.asSequence()
                .filter { pc ->
                    devices.any { device ->
                        device.id == pc.deviceId &&
                            device.type == DeviceType.WINDOWS_PC &&
                            device.trustState == DeviceTrustState.TRUSTED
                    }
                }
                .mapTo(mutableSetOf(), PcSummary::id)
            closeLiveSessionsExcept(
                sessions.filter { it.state != SessionState.EXITED && it.pcId in trustedPcIds }
                    .mapTo(mutableSetOf(), SessionSummary::id),
            )
        }
    }

    private suspend fun validateAuthoritativeSessionKeys(
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
        sessions: List<SessionSummary>,
    ) {
        val storedPcs = historyRepository.pcs().associateBy(PcSummary::id)
        val storedDevices = historyRepository.devices().associateBy(DeviceSummary::id)
        pcs.forEach { pc ->
            val storedPc = storedPcs[pc.id] ?: return@forEach
            require(pc.keyEpoch >= storedPc.keyEpoch) { "key epoch must not regress" }
            val nextKey = devices.firstOrNull { it.id == pc.deviceId }?.exchangePublicKey
            val storedKey = storedDevices[storedPc.deviceId]?.exchangePublicKey
            if (nextKey != null && storedKey != null && storedKey.isNotEmpty()) {
                require(nextKey.contentEquals(storedKey)) {
                    "PC exchange key changed without a new device identity"
                }
            }
        }
        liveSessions.toMap().forEach { (sessionId, live) ->
            val session = sessions.firstOrNull { it.id == sessionId } ?: return@forEach
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@forEach
            val device = devices.firstOrNull {
                it.id == pc.deviceId &&
                    it.type == DeviceType.WINDOWS_PC &&
                    it.trustState == DeviceTrustState.TRUSTED
            } ?: return@forEach
            live.client.validateKeyMaterialUpdate(pc.keyEpoch, device.exchangePublicKey)
        }
    }

    private fun closeLiveSessions() {
        synchronized(liveSessionLock) {
            liveSessions.values.toList().forEach(LiveProductSession::close)
            liveSessions.clear()
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(sessionStates = emptyMap())
    }

    private fun closeHistoryEnginesExcept(retained: Set<UUID>) {
        historyEngines.keys.filterNot(retained::contains).forEach { objectId -> historyEngines.remove(objectId)?.dispose() }
    }

    private fun closeHistoryEngines() {
        historyEngines.values.toList().forEach(TermuxTerminalEngine::dispose)
        historyEngines.clear()
        historyDownloads.clear()
        mutableSnapshot.value = mutableSnapshot.value.copy(replayedHistoryObjectIds = emptySet())
    }

    override fun close() {
        if (closed) return
        closed = true
        closeLiveSessions()
        closeHistoryEngines()
        billing.close()
        billingStateJob.cancel()
        scope.cancel()
        storedIdentity?.identity?.destroy()
        storedIdentity = null
        authService.dispose()
        database.close()
        http.dispatcher.cancelAll()
        val executor = http.dispatcher.executorService
        val connectionPool = http.connectionPool
        thread(start = true, isDaemon = true, name = "TermLink-OkHttp-Close") {
            try {
                connectionPool.evictAll()
            } finally {
                executor.shutdown()
            }
        }
    }

    private fun normalizeServerUrl(value: String): String = ServerUrlPolicy.normalize(value)

    private enum class BrowserAuthFlow { REGISTRATION, ACCOUNT_SETUP }

    private data class ClientConfiguration(val issuer: Uri, val clientId: String, val redirectUri: Uri)

    private data class HistoryDownloadRequest(
        val url: String,
        val method: String,
        val headers: Map<String, List<String>>,
    )

    private class LiveProductSession(
        val engine: TermuxTerminalEngine,
        val client: ProductionRealtimeSessionClient,
    ) : AutoCloseable {
        var stateJob: Job? = null
        var tokenRefreshJob: Job? = null
        var recoveryJob: Job? = null

        override fun close() {
            stateJob?.cancel()
            stateJob = null
            tokenRefreshJob?.cancel()
            tokenRefreshJob = null
            recoveryJob?.cancel()
            recoveryJob = null
            client.close()
        }
    }

    internal class ReservedInputSequenceProvider(
        private val reserve: () -> LongRange,
    ) : InputSequenceProvider {
        private var next = 1L
        private var endInclusive = 0L

        @Synchronized
        override fun next(): Long {
            if (next > endInclusive) {
                val range = reserve()
                require(range.first > 0 && range.first <= range.last) { "reserved input sequence range is invalid" }
                next = range.first
                endInclusive = range.last
            }
            val value = next
            if (value == endInclusive) {
                next = 1L
                endInclusive = 0L
            } else {
                next = Math.addExact(value, 1L)
            }
            return value
        }
    }

    private fun JsonObject.requiredString(name: String): String = getValue(name).jsonPrimitive.content
    private fun JsonObject.instant(name: String): Instant = Instant.parse(requiredString(name))
    private fun JsonObject.optionalInstant(name: String): Instant? = get(name)?.jsonPrimitive?.contentOrNull?.let(Instant::parse)

    private fun JsonObject.toDevice() = DeviceSummary(
        id = UUID.fromString(requiredString("id")),
        type = DeviceType.valueOf(requiredString("type")),
        name = requiredString("name"),
        keyId = requiredString("key_id"),
        exchangePublicKey = java.util.Base64.getDecoder().decode(requiredString("exchange_public_key")).also {
            require(it.size == 32) { "Device exchange public key must be 32 bytes" }
        },
        trustState = DeviceTrustState.valueOf(requiredString("trust_state")),
        createdAt = instant("created_at"),
    )

    private fun JsonObject.toPc() = PcSummary(
        id = UUID.fromString(requiredString("id")),
        deviceId = UUID.fromString(requiredString("device_id")),
        name = requiredString("name"),
        agentVersion = requiredString("agent_version"),
        keyEpoch = getValue("key_epoch").jsonPrimitive.long,
        remoteRoutingEnabled = getValue("remote_routing_enabled").jsonPrimitive.boolean,
        online = optionalInstant("last_seen_at")?.isAfter(Instant.now().minusSeconds(45)) == true,
        lastSeenAt = optionalInstant("last_seen_at"),
    )

    private fun JsonObject.toSession() = SessionSummary(
        id = UUID.fromString(requiredString("id")),
        pcId = UUID.fromString(requiredString("pc_id")),
        shellType = requiredString("shell_type"),
        encryptedTitle = get("title_cipher")?.jsonPrimitive?.contentOrNull?.let(java.util.Base64.getDecoder()::decode),
        state = SessionState.valueOf(requiredString("state")),
        columns = getValue("columns").jsonPrimitive.int,
        rows = getValue("rows").jsonPrimitive.int,
        lastOutputSequence = getValue("last_output_seq").jsonPrimitive.long.toULong(),
        createdAt = instant("created_at"),
        endedAt = optionalInstant("ended_at"),
    )

    private fun JsonObject.toEntitlement() = Entitlement(
        planKey = requiredString("plan_key"),
        catalogVersion = getValue("catalog_version").jsonPrimitive.long,
        maxRemotePcs = getValue("max_remote_pcs").jsonPrimitive.int,
        maxLiveSessions = getValue("max_live_sessions").jsonPrimitive.int,
        featureFlags = getValue("feature_flags").jsonObject.mapValues { it.value.jsonPrimitive.boolean },
        version = getValue("version").jsonPrimitive.long,
        effectiveAt = instant("effective_at"),
        expiresAt = optionalInstant("expires_at"),
    )

    private fun JsonObject.toCatalogPlan() = CatalogPlan(
        key = requiredString("key"),
        displayName = requiredString("display_name"),
        description = requiredString("description"),
        billingPeriod = requiredString("billing_period"),
        maxRemotePcs = getValue("max_remote_pcs").jsonPrimitive.int,
        maxLiveSessions = getValue("max_live_sessions").jsonPrimitive.int,
        featureFlags = getValue("feature_flags").jsonObject.mapValues { it.value.jsonPrimitive.boolean },
        channelProducts = getValue("channel_products").jsonObject.mapValues { it.value.jsonPrimitive.content },
    )

    private fun JsonObject.toHistoryObject(sessionId: UUID): HistoryObject {
        val id = UUID.fromString(requiredString("id"))
        val start = getValue("start_seq").jsonPrimitive.long.toULong()
        val end = getValue("end_seq").jsonPrimitive.long.toULong()
        require(start > 0u && end >= start) { "Invalid history sequence range" }
        val digest = Base64.getUrlDecoder().decode(requiredString("ciphertext_sha256"))
        require(digest.size == 32) { "Invalid history digest" }
        return HistoryObject(
            id = id,
            kind = HistoryObject.Kind.CHUNK,
            objectKey = "server/${sessionId}/chunk/$id",
            keyId = requiredString("key_id"),
            ciphertextSha256 = digest,
            sizeBytes = getValue("size_bytes").jsonPrimitive.long,
            startSequence = start,
            endSequence = end,
            snapshotSequence = null,
            formatName = null,
            formatVersion = null,
        )
    }

    private fun JsonObject.toHistoryDownloadRequest(): HistoryDownloadRequest {
        val uri = URI(requiredString("url"))
        require(uri.scheme.equals("https", ignoreCase = true)) { "History downloads must use HTTPS" }
        val method = requiredString("method").uppercase()
        require(method == "GET") { "History download method must be GET" }
        val headers = get("headers")?.jsonObject?.mapValues { (_, value) ->
            value.jsonArray.map { it.jsonPrimitive.content }
        }.orEmpty()
        return HistoryDownloadRequest(uri.toString(), method, headers)
    }

    private fun downloadHistory(request: HistoryDownloadRequest, metadata: HistoryObject): ByteArray {
        val builder = Request.Builder().url(request.url).get()
        request.headers.forEach { (name, values) ->
            if (!name.equals("host", ignoreCase = true)) values.forEach { builder.addHeader(name, it) }
        }
        http.newCall(builder.build()).execute().use { response ->
            check(response.isSuccessful) { "History download returned HTTP ${response.code}" }
            val bytes = response.body?.bytes() ?: error("History download was empty")
            require(bytes.size.toLong() == metadata.sizeBytes) { "History download size mismatch" }
            require(MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(bytes), metadata.ciphertextSha256)) {
                "History download integrity check failed"
            }
            return bytes
        }
    }

    /**
     * Rebuilds the renderer through the last durable cumulative ACK before a
     * fresh socket subscribes. A cursor is never used by itself: if encrypted
     * history cannot reconstruct a contiguous prefix from sequence 1, the
     * client safely resumes from 0 and lets the Agent replay its bounded log.
     */
    private suspend fun restoreLiveHistory(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TermuxTerminalEngine,
    ): RestoredLiveHistory {
        val cursor = historyRepository.ackCursorState(session.id, LIVE_OUTPUT_ACK_DIRECTION)
            ?: historyRepository.initializeAckCursor(session.id, LIVE_OUTPUT_ACK_DIRECTION)
        if (cursor.sequence == 0uL) {
            return RestoredLiveHistory(rendererGeneration = cursor.rendererGeneration)
        }
        val target = cursor.sequence
        suspend fun resetRendererAndCursor(): RestoredLiveHistory {
            engine.clear()
            engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
            val reset = historyRepository.resetAckCursor(
                session.id,
                LIVE_OUTPUT_ACK_DIRECTION,
                cursor.rendererGeneration,
            )
            return RestoredLiveHistory(rendererGeneration = reset.rendererGeneration)
        }
        val candidates = mutableSnapshot.value.history
            .asSequence()
            .filter { entry ->
                entry.sessionId == session.id &&
                    entry.historyObject.kind == HistoryObject.Kind.CHUNK &&
                    historyKeyEpoch(stored.deviceId, entry.historyObject.keyId)?.let { it <= pc.keyEpoch } == true
            }
            .sortedWith(
                compareBy<HistoryEntry> { it.historyObject.startSequence ?: ULong.MAX_VALUE }
                    .thenBy { it.historyObject.endSequence ?: ULong.MAX_VALUE },
            )
            .toList()
        if (candidates.isEmpty()) return resetRendererAndCursor()

        var expected = 1uL
        var restored = 0uL
        var localInputEpoch = 0L
        return try {
            engine.clear()
            engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
            while (expected <= target) {
                val entry = candidates.firstOrNull { it.historyObject.startSequence == expected } ?: break
                val download = historyDownloads[entry.historyObject.id] ?: break
                val chunk = openHistoryChunk(entry, pc, pcDevice, stored, download)
                for (event in chunk.eventsList) {
                    val sequence = event.seq.toULong()
                    check(sequence == expected) { "History recovery is not contiguous" }
                    if (sequence > target) break
                    localInputEpoch = maxOf(localInputEpoch, applyHistoryEvent(engine, event))
                    restored = sequence
                    expected++
                }
                if (restored >= target) break
                val end = requireNotNull(entry.historyObject.endSequence) { "History end sequence is missing" }
                if (restored < end) break
            }
            if (restored == target) {
                RestoredLiveHistory(
                    acknowledgement = restored.toLong(),
                    localInputEpoch = localInputEpoch,
                    rendererGeneration = cursor.rendererGeneration,
                )
            } else {
                resetRendererAndCursor()
            }
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            resetRendererAndCursor()
        }
    }

    private suspend fun openHistoryChunk(
        entry: HistoryEntry,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        download: HistoryDownloadRequest,
    ): HistoryChunkV1 = withContext(Dispatchers.IO) {
        val metadata = entry.historyObject
        val start = requireNotNull(metadata.startSequence) { "History start sequence is missing" }
        val end = requireNotNull(metadata.endSequence) { "History end sequence is missing" }
        val cached = try {
            historyRepository.getHistoryObject(metadata.id)?.takeIf { value ->
                MessageDigest.isEqual(value.metadata.ciphertextSha256, metadata.ciphertextSha256)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            null
        }
        val sealedObject = cached?.ciphertext ?: downloadHistory(download, metadata)
        val historyEpoch = requireNotNull(historyKeyEpoch(stored.deviceId, metadata.keyId)) {
            "History key ID is not for this device"
        }
        require(historyEpoch <= pc.keyEpoch) { "History key epoch is newer than the authoritative PC epoch" }
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            historyEpoch,
        )
        try {
            if (cached == null) {
                historyRepository.putEventChunk(
                    sessionId = entry.sessionId,
                    startSequence = start,
                    endSequence = end,
                    objectKey = metadata.objectKey,
                    keyId = metadata.keyId,
                    ciphertext = sealedObject,
                    id = metadata.id,
                    ciphertextSha256 = metadata.ciphertextSha256,
                )
            }
            HistoryCrypto.open(
                pairwise,
                sealedObject,
                entry.sessionId,
                pc.deviceId,
                stored.deviceId,
                historyEpoch,
                metadata.keyId,
                start,
                end,
            )
        } finally {
            pairwise.fill(0)
            sealedObject.fill(0)
        }
    }

    private suspend fun recoverLiveHistoryGap(
        sessionId: UUID,
        live: LiveProductSession,
    ) {
        var retry = 0
        while (
            scope.isActive &&
            liveSessions[sessionId] === live &&
            live.client.sessionState.value is RemoteSessionState.HistoryGap
        ) {
            try {
                refresh()
                if (liveSessions[sessionId] !== live) return
                val product = mutableSnapshot.value
                val session = product.sessions.firstOrNull { it.id == sessionId } ?: return
                val pc = product.pcs.firstOrNull { it.id == session.pcId } ?: return
                val pcDevice = product.devices.firstOrNull {
                    it.id == pc.deviceId &&
                        it.type == DeviceType.WINDOWS_PC &&
                        it.trustState == DeviceTrustState.TRUSTED
                } ?: return
                val activeGap = live.client.sessionState.value as? RemoteSessionState.HistoryGap ?: return
                val missingEnd = activeGap.receivedSequence - 1
                val recovery = if (missingEnd > 0) {
                    loadLiveHistoryEvents(session, pc, pcDevice, identity(), missingEnd)
                } else {
                    null
                }
                if (recovery != null) {
                    try {
                        val recovered = live.client.recoverFromHistory(
                            recovery.events,
                            recovery.localInputEpoch,
                            activeGap,
                        )
                        appendActivity(
                            "History gap recovered",
                            "Encrypted replay restored through ACK $recovered",
                            Severity.SUCCESS,
                        )
                        return
                    } finally {
                        recovery.events.forEach { event ->
                            if (event is RemoteSessionEvent.Output) event.bytes.fill(0)
                        }
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                // The encrypted history upload can lag the realtime stream.
                // Keep the renderer read-only and retry with bounded backoff.
            }
            delay(HISTORY_RECOVERY_DELAYS_MILLIS[retry.coerceAtMost(HISTORY_RECOVERY_DELAYS_MILLIS.lastIndex)])
            retry++
        }
    }

    private suspend fun loadLiveHistoryEvents(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        throughSequence: Long,
    ): LoadedLiveHistory? {
        require(throughSequence > 0) { "History recovery sequence must be positive" }
        val through = throughSequence.toULong()
        val candidates = mutableSnapshot.value.history
            .asSequence()
            .filter { entry ->
                entry.sessionId == session.id &&
                    entry.historyObject.kind == HistoryObject.Kind.CHUNK &&
                    historyKeyEpoch(stored.deviceId, entry.historyObject.keyId)?.let { it <= pc.keyEpoch } == true
            }
            .sortedWith(
                compareBy<HistoryEntry> { it.historyObject.startSequence ?: ULong.MAX_VALUE }
                    .thenBy { it.historyObject.endSequence ?: ULong.MAX_VALUE },
            )
            .toList()
        var expected = 1uL
        var localInputEpoch = 0L
        val events = mutableListOf<RemoteSessionEvent>()
        try {
            while (expected <= through) {
                val entry = candidates.firstOrNull { it.historyObject.startSequence == expected }
                if (entry == null) {
                    wipeHistoryEvents(events)
                    return null
                }
                val download = historyDownloads[entry.historyObject.id]
                if (download == null) {
                    wipeHistoryEvents(events)
                    return null
                }
                val chunk = openHistoryChunk(entry, pc, pcDevice, stored, download)
                for (event in chunk.eventsList) {
                    val sequence = event.seq.toULong()
                    check(sequence == expected) { "History recovery is not contiguous" }
                    if (sequence > through) break
                    val decoded = decodeHistoryEvent(event)
                    events += decoded.event
                    localInputEpoch = maxOf(localInputEpoch, decoded.localInputEpoch)
                    expected++
                }
                if (expected <= through && expected <= requireNotNull(entry.historyObject.endSequence)) {
                    wipeHistoryEvents(events)
                    return null
                }
            }
        } catch (error: Exception) {
            wipeHistoryEvents(events)
            throw error
        }
        return LoadedLiveHistory(events, localInputEpoch)
    }

    private fun decodeHistoryEvent(event: HistoryEventV1): DecodedHistoryEvent {
        require(event.seq > 0) { "History event sequence must be positive" }
        val payload = event.payload.toByteArray()
        return try {
            when (event.messageType) {
                MessageType.OUTPUT_CHUNK -> {
                    val output = OutputChunkPayload.parseFrom(payload).output.toByteArray()
                    DecodedHistoryEvent(RemoteSessionEvent.Output(event.seq, output))
                }
                MessageType.RESIZE_APPLIED -> ResizePayload.parseFrom(payload).let { resize ->
                    DecodedHistoryEvent(
                        RemoteSessionEvent.Resize(
                            event.seq,
                            TerminalSize(resize.columns.coerceAtLeast(1), resize.rows.coerceAtLeast(1)),
                        ),
                    )
                }
                MessageType.SESSION_UPSERT -> SessionUpsertPayload.parseFrom(payload).let { value ->
                    val lifecycle = when (value.lifecycle.uppercase()) {
                        "RUNNING", "STARTED", "CHANGED" -> RemoteLifecycle.RUNNING
                        "INTERRUPTED" -> RemoteLifecycle.INTERRUPTED
                        "EXITED", "ENDED" -> RemoteLifecycle.ENDED
                        else -> error("Unknown history lifecycle")
                    }
                    DecodedHistoryEvent(
                        RemoteSessionEvent.Lifecycle(
                            event.seq,
                            lifecycle,
                            value.exitCode.takeIf { value.hasExitCode() },
                        ),
                    )
                }
                MessageType.SESSION_STARTED -> DecodedHistoryEvent(
                    RemoteSessionEvent.Lifecycle(event.seq, RemoteLifecycle.RUNNING),
                )
                MessageType.SESSION_INTERRUPTED -> DecodedHistoryEvent(
                    RemoteSessionEvent.Lifecycle(event.seq, RemoteLifecycle.INTERRUPTED),
                )
                MessageType.SESSION_ENDED -> SessionUpsertPayload.parseFrom(payload).let { value ->
                    DecodedHistoryEvent(
                        RemoteSessionEvent.Lifecycle(
                            event.seq,
                            RemoteLifecycle.ENDED,
                            value.exitCode.takeIf { value.hasExitCode() },
                        ),
                    )
                }
                MessageType.INPUT_ACCEPTED,
                MessageType.INPUT_REJECTED,
                -> InputResultPayload.parseFrom(payload).let { value ->
                    val inputId = uuidFromBytes(value.inputId.toByteArray())
                    require((event.messageType == MessageType.INPUT_ACCEPTED) == value.accepted) {
                        "History input result type mismatch"
                    }
                    DecodedHistoryEvent(
                        RemoteSessionEvent.InputResult(
                            event.seq,
                            inputId,
                            value.accepted,
                            value.reasonCode.ifBlank { null },
                        ),
                    )
                }
                MessageType.LEASE_GRANTED -> DecodedHistoryEvent(RemoteSessionEvent.ControlGranted(event.seq))
                MessageType.LEASE_DENIED,
                MessageType.LEASE_REVOKED,
                -> LeasePayload.parseFrom(payload).let { value ->
                    DecodedHistoryEvent(
                        RemoteSessionEvent.ControlRevoked(
                            event.seq,
                            value.reasonCode.ifBlank { "control lease revoked" },
                        ),
                        value.localInputEpoch,
                    )
                }
                MessageType.GAP -> error("History contains a permanent gap")
                else -> error("Unsupported history event ${event.messageType}")
            }
        } finally {
            payload.fill(0)
        }
    }

    private fun uuidFromBytes(value: ByteArray): UUID {
        try {
            require(value.size == 16) { "History UUID must be 16 bytes" }
            val buffer = ByteBuffer.wrap(value)
            return UUID(buffer.long, buffer.long)
        } finally {
            value.fill(0)
        }
    }

    private fun historyKeyEpoch(recipientDeviceId: UUID, keyId: String): Long? {
        val prefix = "history-v1:${recipientDeviceId.toString().replace("-", "")}:"
        if (!keyId.startsWith(prefix)) return null
        val encodedEpoch = keyId.removePrefix(prefix)
        if (encodedEpoch.length !in 8..19 || encodedEpoch.any { it !in '0'..'9' }) return null
        val epoch = encodedEpoch.toLongOrNull()?.takeIf { it > 0 } ?: return null
        return epoch.takeIf { HistoryCrypto.keyId(recipientDeviceId, it) == keyId }
    }

    private fun wipeHistoryEvents(events: List<RemoteSessionEvent>) {
        events.forEach { event ->
            if (event is RemoteSessionEvent.Output) event.bytes.fill(0)
        }
    }

    private suspend fun applyHistoryEvent(engine: TermuxTerminalEngine, event: HistoryEventV1): Long {
        val payload = event.payload.toByteArray()
        return try {
            when (event.messageType) {
                MessageType.OUTPUT_CHUNK -> {
                    val output = OutputChunkPayload.parseFrom(payload).output.toByteArray()
                    try { engine.feed(event.seq, ByteBuffer.wrap(output)) } finally { output.fill(0) }
                    0
                }
                MessageType.RESIZE_APPLIED -> ResizePayload.parseFrom(payload).also { resize ->
                    engine.resize(resize.columns.coerceAtLeast(1), resize.rows.coerceAtLeast(1))
                }.let { 0 }
                MessageType.GAP -> {
                    val gap = GapPayload.parseFrom(payload)
                    error("History gap ${gap.start}..${gap.end}")
                }
                MessageType.SESSION_UPSERT,
                MessageType.SESSION_STARTED,
                MessageType.SESSION_INTERRUPTED,
                MessageType.SESSION_ENDED,
                MessageType.INPUT_ACCEPTED,
                MessageType.INPUT_REJECTED,
                MessageType.LEASE_GRANTED -> 0
                MessageType.LEASE_DENIED,
                MessageType.LEASE_REVOKED,
                -> LeasePayload.parseFrom(payload).localInputEpoch
                else -> error("Unsupported history event ${event.messageType}")
            }
        } finally {
            payload.fill(0)
        }
    }

    private data class RestoredLiveHistory(
        val acknowledgement: Long = 0,
        val localInputEpoch: Long = 0,
        val rendererGeneration: Long,
    )

    private data class LoadedLiveHistory(
        val events: List<RemoteSessionEvent>,
        val localInputEpoch: Long,
    )

    private data class DecodedHistoryEvent(
        val event: RemoteSessionEvent,
        val localInputEpoch: Long = 0,
    )

    private companion object {
        const val PUBLIC_SERVER_URL = "https://termlink.kget.cc:8910"
        const val LIVE_TOKEN_REFRESH_INTERVAL_MILLIS = 15_000L
        const val LIVE_TOKEN_REFRESH_RETRY_MILLIS = 45_000L
        const val LIVE_OUTPUT_ACK_DIRECTION = "PC_TO_ANDROID"
        val HISTORY_RECOVERY_DELAYS_MILLIS = longArrayOf(500, 1_000, 2_000, 4_000, 8_000, 15_000, 30_000)
        val sensitiveIdentifierPattern = Regex("(?i)\\b[0-9a-f]{8}-[0-9a-f-]{27,}\\b")
    }
}

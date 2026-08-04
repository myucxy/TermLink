package dev.termlink.product

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.view.KeyEvent
import dev.termlink.billing.BillingCoordinator
import dev.termlink.billing.BillingServerGateway
import dev.termlink.billing.PlayBillingAdapter
import dev.termlink.crypto.DeviceKeyVault
import dev.termlink.crypto.HistoryCrypto
import dev.termlink.crypto.SessionCrypto
import dev.termlink.database.TermLinkDatabase
import dev.termlink.history.HistoryRepository
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
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.ResizePayload
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
import kotlinx.coroutines.CoroutineScope
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
import net.openid.appauth.AuthorizationServiceConfiguration
import net.openid.appauth.ResponseTypeValues
import okhttp3.OkHttpClient
import okhttp3.Request
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

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
    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val liveSessions = ConcurrentHashMap<UUID, LiveProductSession>()
    private val historyEngines = ConcurrentHashMap<UUID, TermuxTerminalEngine>()
    private val historyDownloads = ConcurrentHashMap<UUID, HistoryDownloadRequest>()
    private val liveSessionLock = Any()
    private val tokenRefresh = TokenRefreshCoordinator { requestFreshAccessToken() }
    private val initialServerUrl = sessionStore.serverUrl ?: PUBLIC_SERVER_URL.also {
        sessionStore.serverUrl = it
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
    private var closed = false

    private val mutableSnapshot = MutableStateFlow(
        ProductSnapshot(
            signedIn = authState?.isAuthorized == true,
            configured = true,
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
        if (authState?.isAuthorized == true && sessionStore.serverUrl != null) {
            scope.launch { runCatching { refresh() }.onFailure(::publishFailure) }
        }
    }

    override suspend fun configureServer(url: String) {
        val normalized = normalizeServerUrl(url)
        val changed = normalized != sessionStore.serverUrl
        if (changed) {
            closeLiveSessions()
            closeHistoryEngines()
            authState = null
            sessionStore.clearAuthState()
            storedIdentity?.identity?.destroy()
            storedIdentity = null
            keyVault.clear()
        }
        sessionStore.serverUrl = normalized
        billing.reset()
        mutableSnapshot.value = ProductSnapshot(configured = true, serverUrl = normalized)
    }

    override suspend fun signIn() {
        check(!closed) { "Repository is closed" }
        val root = sessionStore.serverUrl ?: error("Configure a TermLink server first")
        val client = fetchClientConfiguration(root)
        val serviceConfiguration = fetchIssuerConfiguration(client.issuer)
        check(client.redirectUri.scheme == "dev.termlink") { "Server returned an unsupported redirect URI" }
        authState = AuthState(serviceConfiguration)
        val request = AuthorizationRequest.Builder(
            serviceConfiguration,
            client.clientId,
            ResponseTypeValues.CODE,
            client.redirectUri,
        ).setScope("openid profile offline_access").build()
        launchAuthorization(authService.getAuthorizationRequestIntent(request))
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
            sessionStore.saveAuthState(state)
            mutableSnapshot.value = mutableSnapshot.value.copy(signedIn = true, configured = true)
            scope.launch { runCatching { refresh() }.onFailure(::publishFailure) }
        }
    }

    override suspend fun signOut() {
        authState = null
        sessionStore.clearAuthState()
        closeLiveSessions()
        closeHistoryEngines()
        storedIdentity?.identity?.destroy()
        storedIdentity = null
        keyVault.clear()
        billing.reset()
        mutableSnapshot.value = ProductSnapshot(configured = sessionStore.serverUrl != null, serverUrl = sessionStore.serverUrl)
    }

    override suspend fun approveDevice(deviceId: UUID) = withApi { api ->
        val pc = mutableSnapshot.value.pcs.firstOrNull() ?: error("No manageable PC is available")
        val result = api.post(
            "/v1/pairing/challenges",
            buildJsonObject {
                put("target_device_id", deviceId.toString())
                put("pc_id", pc.id.toString())
                put("permission", "CONTROL")
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
        refreshWithApi(api)
    }

    override suspend fun purchaseProduct(productId: String) {
        billing.purchase(productId)
    }

    override suspend fun restorePurchases() {
        billing.restore()
    }

    override suspend fun revokeDevice(deviceId: UUID) = withApi { api ->
        api.post("/v1/devices/$deviceId/revoke", buildJsonObject { })
        refreshWithApi(api)
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
        val root = sessionStore.serverUrl ?: error("Configure a TermLink server first")
        val stored = identity()
        val engine = TermuxTerminalEngine(
            initialSize = TerminalSize(summary.columns.coerceAtLeast(1), summary.rows.coerceAtLeast(1)),
        )
        val client = ProductionRealtimeSessionClient(
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
        )
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
        val expectedKeyId = HistoryCrypto.keyId(stored.deviceId, pc.keyEpoch)
        require(entry.historyObject.keyId == expectedKeyId) { "The selected history key is not available on this device" }
        val start = requireNotNull(entry.historyObject.startSequence) { "History start sequence is missing" }
        val end = requireNotNull(entry.historyObject.endSequence) { "History end sequence is missing" }

        val chunk = withContext(Dispatchers.IO) {
            val sealedObject = downloadHistory(download, entry.historyObject)
            val pairwise = SessionCrypto.derivePairwiseKey(
                stored.identity.exchange.privateKey,
                pcDevice.exchangePublicKey,
                pc.deviceId,
                stored.deviceId,
                pc.keyEpoch,
            )
            try {
                historyRepository.putEventChunk(
                    sessionId = entry.sessionId,
                    startSequence = start,
                    endSequence = end,
                    objectKey = entry.historyObject.objectKey,
                    keyId = entry.historyObject.keyId,
                    ciphertext = sealedObject,
                    id = entry.historyObject.id,
                    ciphertextSha256 = entry.historyObject.ciphertextSha256,
                )
                HistoryCrypto.open(
                    pairwise,
                    sealedObject,
                    entry.sessionId,
                    pc.deviceId,
                    stored.deviceId,
                    pc.keyEpoch,
                    entry.historyObject.keyId,
                    start,
                    end,
                )
            } finally {
                pairwise.fill(0)
                sealedObject.fill(0)
            }
        }

        val engine = historyEngines.getOrPut(entry.historyObject.id) { TermuxTerminalEngine() }
        engine.setReadOnly(true)
        engine.clear()
        engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
        chunk.eventsList.forEach { event ->
            val payload = event.payload.toByteArray()
            try {
                when (event.messageType) {
                    MessageType.OUTPUT_CHUNK -> {
                        val output = OutputChunkPayload.parseFrom(payload).output.toByteArray()
                        try { engine.feed(event.seq, ByteBuffer.wrap(output)) } finally { output.fill(0) }
                    }
                    MessageType.RESIZE_APPLIED -> ResizePayload.parseFrom(payload).also { resize ->
                        engine.resize(resize.columns.coerceAtLeast(1), resize.rows.coerceAtLeast(1))
                    }
                    MessageType.GAP -> GapPayload.parseFrom(payload).also { gap ->
                        error("History gap ${gap.start}..${gap.end}")
                    }
                    MessageType.SESSION_UPSERT,
                    MessageType.SESSION_STARTED,
                    MessageType.SESSION_INTERRUPTED,
                    MessageType.SESSION_ENDED,
                    MessageType.INPUT_ACCEPTED,
                    MessageType.INPUT_REJECTED,
                    MessageType.LEASE_REVOKED,
                    -> Unit
                    else -> error("Unsupported history event ${event.messageType}")
                }
            } finally {
                payload.fill(0)
            }
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            replayedHistoryObjectIds = mutableSnapshot.value.replayedHistoryObjectIds + entry.historyObject.id,
        )
        appendActivity("History replayed", entry.sessionLabel, Severity.SUCCESS)
    }

    override suspend fun refresh() = withApi { api -> refreshWithApi(api) }

    private suspend fun refreshWithApi(api: ProductionApiClient) {
        val self = ProductResponseParser.self(api.get("/v1/devices/self").jsonObject)
        if (self.trustState != DeviceTrustState.TRUSTED) {
            closeLiveSessions()
            mutableSnapshot.value = mutableSnapshot.value.copy(
                signedIn = true,
                configured = true,
                accountLabel = "Approval pending",
                devices = listOf(self),
                pcs = emptyList(),
                sessions = emptyList(),
                history = emptyList(),
                lastSyncedAt = Instant.now(),
            )
            return
        }
        val previous = mutableSnapshot.value
        val trustedDevices = previous.devices
            .map { device -> if (device.id == self.id) self else device }
            .let { devices -> if (devices.any { it.id == self.id }) devices else listOf(self) + devices }
        mutableSnapshot.value = previous.copy(
            signedIn = true,
            configured = true,
            accountLabel = "Connected account",
            devices = trustedDevices,
            lastSyncedAt = Instant.now(),
        )
        val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
        val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
        val sessions = pcs.flatMap { pc ->
            ProductResponseParser.itemObjects(api.get("/v1/pcs/${pc.id}/sessions").jsonObject).map { it.toSession() }
        }
        val entitlement = api.get("/v1/account/entitlements").jsonObject.toEntitlement()
        val catalogPlans = ProductResponseParser.catalogPlanObjects(api.get("/v1/billing/plans").jsonObject)
            .map { it.toCatalogPlan() }
        val catalog = catalogPlans.firstOrNull { it.key == entitlement.planKey }
        devices.forEach { historyRepository.upsertDevice(it) }
        pcs.forEach { historyRepository.upsertPc(it) }
        sessions.forEach { historyRepository.upsertSession(it) }
        val ownDeviceId = identity().deviceId
        val refreshedDownloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val historyEntries = sessions.flatMap { session ->
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@flatMap emptyList()
            val expectedKeyId = HistoryCrypto.keyId(ownDeviceId, pc.keyEpoch)
            val response = try {
                api.get("/v1/sessions/${session.id}/history").jsonObject
            } catch (error: TermLinkApiException) {
                if (error.status == 503) return@flatMap emptyList()
                throw error
            }
            ProductResponseParser.itemObjects(response).mapNotNull { item ->
                if (!item.requiredString("kind").equals("chunk", ignoreCase = true) || item.requiredString("key_id") != expectedKeyId) {
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
        closeHistoryEnginesExcept(historyEntries.mapTo(mutableSetOf()) { it.historyObject.id })
        closeLiveSessionsExcept(sessions.filter { it.state != SessionState.EXITED }.mapTo(mutableSetOf()) { it.id })
        val retainedStates = mutableSnapshot.value.sessionStates.filterKeys { id -> sessions.any { it.id == id } }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            signedIn = true,
            configured = true,
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

    private suspend fun <T> withApi(block: suspend (ProductionApiClient) -> T): T = withContext(Dispatchers.IO) {
        val root = sessionStore.serverUrl ?: error("Configure a TermLink server first")
        val token = freshAccessToken()
        val identity = identity()
        ProductionApiClient(root, AccessTokenProvider { token }, identity.deviceId, identity.identity, http, allowCleartextForLocalTest = false).use { api ->
            try {
                api.registerDevice("ANDROID", android.os.Build.MODEL.take(128), "device-key-${identity.deviceId}")
            } catch (error: TermLinkApiException) {
                if (error.status != 409) throw error
            }
            block(api)
        }
    }

    private fun identity(): DeviceKeyVault.StoredIdentity = storedIdentity ?: keyVault.loadOrCreate().also { storedIdentity = it }

    private suspend fun freshAccessToken(): String = tokenRefresh.freshAccessToken()

    private suspend fun requestFreshAccessToken(): String = suspendCancellableCoroutine { continuation ->
        val state = authState ?: run {
            continuation.resumeWithException(IllegalStateException("Sign in first"))
            return@suspendCancellableCoroutine
        }
        state.performActionWithFreshTokens(authService) { accessToken, _, exception ->
            if (!continuation.isActive) return@performActionWithFreshTokens
            if (exception != null || accessToken == null) {
                continuation.resumeWithException(exception ?: IllegalStateException("OIDC access token is unavailable"))
            } else {
                sessionStore.saveAuthState(state)
                continuation.resume(accessToken)
            }
        }
    }

    private suspend fun fetchClientConfiguration(root: String): ClientConfiguration = withContext(Dispatchers.IO) {
        val request = Request.Builder().url("$root/v1/client-config").get().build()
        http.newCall(request).execute().use { response ->
            check(response.isSuccessful) { "TermLink client configuration returned HTTP ${response.code}" }
            val body = json.parseToJsonElement(response.body?.string().orEmpty()).jsonObject
            ClientConfiguration(
                issuer = Uri.parse(body.requiredString("oidc_issuer")),
                clientId = body.requiredString("oidc_client_id"),
                redirectUri = Uri.parse(body.requiredString("redirect_uri")),
            )
        }
    }

    private suspend fun fetchIssuerConfiguration(issuer: Uri): AuthorizationServiceConfiguration =
        suspendCancellableCoroutine { continuation ->
            AuthorizationServiceConfiguration.fetchFromIssuer(issuer) { configuration, exception ->
                if (!continuation.isActive) return@fetchFromIssuer
                if (configuration == null) continuation.resumeWithException(exception ?: IllegalStateException("OIDC discovery failed"))
                else continuation.resume(configuration)
            }
        }

    private fun appendActivity(title: String, detail: String, severity: Severity) {
        mutableSnapshot.value = mutableSnapshot.value.copy(
            activity = listOf(ActivityEntry(UUID.randomUUID().toString(), Instant.now(), title, detail, severity)) + mutableSnapshot.value.activity,
        )
    }

    private fun publishFailure(error: Throwable) {
        appendActivity("Connection failed", error.message ?: error::class.java.simpleName, Severity.ERROR)
    }

    private fun closeLiveSessionsExcept(retained: Set<UUID>) {
        liveSessions.keys.filterNot(retained::contains).forEach { sessionId ->
            liveSessions.remove(sessionId)?.close()
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            sessionStates = mutableSnapshot.value.sessionStates.filterKeys(retained::contains),
        )
    }

    private fun closeLiveSessions() {
        liveSessions.values.toList().forEach(LiveProductSession::close)
        liveSessions.clear()
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
        http.dispatcher.executorService.shutdown()
        http.connectionPool.evictAll()
    }

    private fun normalizeServerUrl(value: String): String = ServerUrlPolicy.normalize(value)

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

        override fun close() {
            stateJob?.cancel()
            stateJob = null
            tokenRefreshJob?.cancel()
            tokenRefreshJob = null
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

    private companion object {
        const val PUBLIC_SERVER_URL = "https://termlink.kget.cc:8910"
        const val LIVE_TOKEN_REFRESH_INTERVAL_MILLIS = 15_000L
        const val LIVE_TOKEN_REFRESH_RETRY_MILLIS = 45_000L
    }
}

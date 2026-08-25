package dev.termlink.product

import android.app.Activity
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.os.Build
import android.util.Log
import android.view.KeyEvent
import androidx.room.withTransaction
import dev.termlink.billing.BillingCoordinator
import dev.termlink.billing.BillingServerGateway
import dev.termlink.billing.BillingSnapshot
import dev.termlink.billing.PlayBillingAdapter
import dev.termlink.crypto.DeviceKeyVault
import dev.termlink.crypto.HistoryCrypto
import dev.termlink.crypto.SessionCrypto
import dev.termlink.crypto.SessionTitleCrypto
import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.database.TermLinkDatabase
import dev.termlink.history.HistoryRepository
import dev.termlink.history.HistoryObjectCounts
import dev.termlink.history.EncryptedTranscriptPage
import dev.termlink.history.RendererCheckpointState
import dev.termlink.history.TranscriptPageMetadata
import dev.termlink.harness.BuildConfig
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
import dev.termlink.network.production.ProductionRealtimeMultiplexer
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
import dev.termlink.session.DurableAckCommit
import dev.termlink.session.RendererBootstrapState
import dev.termlink.session.InputSubmission
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalInput
import dev.termlink.terminal.api.PreparedTerminalCheckpoint
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.time.Instant
import java.net.URI
import java.net.URLEncoder
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.nio.charset.StandardCharsets
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.isActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
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

internal suspend fun <T> withClearedRegistrationSecrets(
    password: CharArray,
    confirm: CharArray,
    operation: suspend () -> T,
): T = try {
    operation()
} finally {
    password.fill('\u0000')
    confirm.fill('\u0000')
}

internal fun accountRegistrationRequestBody(
    username: String,
    email: String,
    firstName: String,
    lastName: String,
    password: CharArray,
    confirm: CharArray,
): String {
    val normalizedUsername = username.trim()
    val normalizedEmail = email.trim()
    val normalizedFirstName = firstName.trim()
    val normalizedLastName = lastName.trim()
    require(normalizedUsername.length in 3..128 && username.none { it == '\r' || it == '\n' }) { "Username is invalid" }
    require(normalizedEmail.length in 3..320 && normalizedEmail.contains("@") && email.none { it == '\r' || it == '\n' }) {
        "Email is invalid"
    }
    require(normalizedFirstName.length in 1..128 && firstName.none { it == '\r' || it == '\n' }) { "First name is invalid" }
    require(normalizedLastName.length in 1..128 && lastName.none { it == '\r' || it == '\n' }) { "Last name is invalid" }
    require(password.size in 8..1024 && password.contentEquals(confirm)) { "Password is invalid" }
    return buildJsonObject {
        put("username", normalizedUsername)
        put("email", normalizedEmail)
        put("first_name", normalizedFirstName)
        put("last_name", normalizedLastName)
        put("password", String(password))
        put("password_confirm", String(confirm))
    }.toString()
}

/** Thread-visible token handoff shared by every long-lived realtime client. */
internal class RealtimeAccessTokenSource {
    private val current = AtomicReference<String?>(null)

    fun publish(token: String): String {
        require(token.isNotBlank()) { "OIDC access token is unavailable" }
        current.set(token)
        return token
    }

    fun requireCurrent(): String = current.get() ?: error("OIDC access token is unavailable")

    fun clear() {
        current.set(null)
    }
}

/** Invalidates any asynchronous token result captured by an older auth lifecycle. */
internal class AuthenticationLifecycleFence {
    private val generation = AtomicLong(0)

    fun capture(): Long = generation.get()

    fun invalidate(): Long = generation.incrementAndGet()

    fun isCurrent(captured: Long): Boolean = generation.get() == captured
}

internal fun authenticationExpiredSnapshot(
    snapshot: ProductSnapshot,
    message: String,
): ProductSnapshot = snapshot.copy(
    signedIn = false,
    accountLabel = null,
    authenticationState = ProductAuthenticationState.SIGNED_OUT,
    connectionState = ProductConnectionState.ERROR,
    connectionError = message,
)

/** Reads only the reachability decision published by the PC catalog. */
internal fun authoritativePcOnline(root: JsonObject): Boolean =
    root["online"]?.jsonPrimitive?.booleanOrNull == true

/** Applies only runtime facts emitted by the engine bound to this session. */
internal fun projectLiveSessionSummary(
    session: SessionSummary,
    remoteState: RemoteSessionState,
    authoritativeSize: TerminalSize,
): SessionSummary = session.copy(
    state = when (remoteState) {
        is RemoteSessionState.Exited -> SessionState.EXITED
        is RemoteSessionState.Reconnecting -> if (remoteState.reason == "remote session interrupted") {
            SessionState.INTERRUPTED
        } else {
            session.state
        }
        is RemoteSessionState.Live,
        is RemoteSessionState.ReadOnly,
        is RemoteSessionState.Controlling -> SessionState.RUNNING
        is RemoteSessionState.HistoryGap -> session.state
    },
    online = when (remoteState) {
        is RemoteSessionState.Exited -> false
        // An opened renderer remains a valid read-only viewer while transport
        // or remote lifecycle recovery is in progress. Publishing catalog
        // reachability here made the selected detail disappear on one stale
        // online=false poll even though the renderer was still recoverable.
        is RemoteSessionState.Reconnecting -> true
        is RemoteSessionState.Live,
        is RemoteSessionState.ReadOnly,
        is RemoteSessionState.Controlling -> true
        is RemoteSessionState.HistoryGap -> true
    },
    columns = authoritativeSize.columns,
    rows = authoritativeSize.rows,
)

private data class RuntimeSessionProjection(
    val state: RemoteSessionState,
    val size: TerminalSize,
    val titleOverride: String?,
)

/**
 * Null means neither realtime metadata nor the compatibility renderer has a
 * title, so the authenticated directory title remains authoritative. Blank is
 * intentionally preserved because it is an explicit realtime clear event.
 */
internal fun liveSessionTitleOverride(
    publishedTitle: String?,
    rendererTitle: String,
): String? = publishedTitle ?: rendererTitle.takeIf(String::isNotBlank)

internal fun changedPcDeviceIdentitySessionIds(
    storedPcs: List<PcSummary>,
    incomingPcs: List<PcSummary>,
    sessions: List<SessionSummary>,
): Set<UUID> {
    val storedDeviceIds = storedPcs.associate { it.id to it.deviceId }
    val changedPcIds = incomingPcs.asSequence()
        .filter { incoming -> storedDeviceIds[incoming.id]?.let { it != incoming.deviceId } == true }
        .mapTo(hashSetOf(), PcSummary::id)
    return sessions.asSequence()
        .filter { it.pcId in changedPcIds }
        .mapTo(hashSetOf(), SessionSummary::id)
}

internal fun productSessionUpsertEvent(
    sequence: Long,
    payload: SessionUpsertPayload,
): RemoteSessionEvent {
    require(sequence > 0) { "session event sequence must be positive" }
    if (payload.hasTitle()) return RemoteSessionEvent.Title(sequence, payload.title)
    val lifecycle = when (payload.lifecycle.uppercase()) {
        "RUNNING", "STARTED", "CHANGED" -> RemoteLifecycle.RUNNING
        "INTERRUPTED" -> RemoteLifecycle.INTERRUPTED
        "EXITED", "ENDED" -> RemoteLifecycle.ENDED
        else -> error("Unknown history lifecycle")
    }
    return RemoteSessionEvent.Lifecycle(
        sequence,
        lifecycle,
        payload.exitCode.takeIf { payload.hasExitCode() },
    )
}

/** Reconciles an authoritative catalog without touching global connection UI state. */
internal fun reconcileSessionCatalog(
    current: ProductSnapshot,
    pcs: List<PcSummary>,
    sessions: List<SessionSummary>,
    deletedSessionIds: Set<UUID>,
): ProductSnapshot {
    val filtered = sessions.filterNot { it.id in deletedSessionIds }
    val retained = filtered.mapTo(hashSetOf(), SessionSummary::id)
    val pruned = current.sessions.asSequence()
        .map(SessionSummary::id)
        .filterNot(retained::contains)
        .fold(current) { snapshot, sessionId -> snapshot.withoutSession(sessionId) }
    return pruned.copy(pcs = pcs, sessions = filtered)
}

/** Applies authenticated directory titles without retaining a stale value after a replacement envelope fails closed. */
internal fun reconcileSessionDirectoryTitles(
    current: Map<UUID, String>,
    sessions: List<SessionSummary>,
    decrypted: Map<UUID, String>,
    realtime: Map<UUID, String> = emptyMap(),
): MutableMap<UUID, String> {
    val retainedIds = sessions.mapTo(hashSetOf(), SessionSummary::id)
    val result = current.filterKeys(retainedIds::contains).toMutableMap()
    sessions.forEach { session ->
        if (session.encryptedTitle == null) return@forEach
        val title = decrypted[session.id]
        if (title.isNullOrBlank()) result.remove(session.id) else result[session.id] = title
    }
    // Directory envelopes and realtime title events share the public snapshot
    // map, but not the same lifecycle. A malformed/re-keyed directory envelope
    // must fail closed without deleting a newer title already authenticated by
    // the open realtime session. Presence in this map is significant: an
    // explicit blank realtime title clears both the old realtime value and any
    // directory fallback for that live session.
    realtime.forEach { (sessionId, title) ->
        if (sessionId !in retainedIds) return@forEach
        if (title.isBlank()) result.remove(sessionId) else result[sessionId] = title
    }
    return result
}

/**
 * An epoch and exchange key are scoped to one Windows device identity. A PC
 * record that moves to a new device identity starts a fresh epoch namespace;
 * only transitions within the same identity are monotonic and key-stable.
 */
internal fun validateAuthoritativePcKeyTransition(
    storedPc: PcSummary,
    storedDevice: DeviceSummary?,
    incomingPc: PcSummary,
    incomingDevice: DeviceSummary?,
) {
    require(incomingPc.id == storedPc.id) { "PC key transition changed the PC identity" }
    if (incomingPc.deviceId != storedPc.deviceId) return
    require(incomingPc.keyEpoch >= storedPc.keyEpoch) { "key epoch must not regress" }
    val storedKey = storedDevice?.takeIf { it.id == storedPc.deviceId }?.exchangePublicKey
    val incomingKey = incomingDevice?.takeIf { it.id == incomingPc.deviceId }?.exchangePublicKey
    if (incomingKey != null && storedKey != null && storedKey.isNotEmpty()) {
        require(incomingKey.contentEquals(storedKey)) {
            "PC exchange key changed without a new device identity"
        }
    }
}

/** Keeps opened renderers through transient reachability changes; EXITED is terminal. */
internal fun retainedLiveSessionIds(
    sessions: List<SessionSummary>,
    trustedPcIds: Set<UUID>,
): Set<UUID> = sessions.asSequence()
    .filter { it.state != SessionState.EXITED && it.pcId in trustedPcIds }
    .mapTo(hashSetOf(), SessionSummary::id)

internal data class ProductRealtimeDiagnostics(
    val socketPhase: String,
    val socketGeneration: Int,
    val connectedGeneration: Int,
    val socketState: String,
    val endpointCount: Int,
    val transportState: String,
    val remoteState: String,
    val transportCollectorActive: Boolean,
    val reconnectActive: Boolean,
    val subscriberReady: Boolean,
    val reconnectAttempt: Int,
    val subscribeSendCount: Long,
    val lastSubscribeCursor: Long,
    val flowControlSendCount: Long,
    val lastReceivedSequence: Long,
    val lastHandledSequence: Long,
    val networkAvailableCount: Int,
    val networkLostCount: Int,
    val networkReconnectCount: Int,
)

private const val REMOTE_FILE_OPERATION_LIST = "LIST"
private const val REMOTE_FILE_OPERATION_DOWNLOAD = "DOWNLOAD"
private const val REMOTE_FILE_OPERATION_OPEN_TERMINAL = "OPEN_TERMINAL"
private const val REMOTE_FILE_STATUS_PENDING = "PENDING"
private const val REMOTE_FILE_STATUS_PROCESSING = "PROCESSING"
private const val REMOTE_FILE_STATUS_SUCCEEDED = "SUCCEEDED"
private const val REMOTE_FILE_STATUS_FAILED = "FAILED"
private const val REMOTE_FILE_STATUS_CANCELLED = "CANCELLED"
private const val REMOTE_FILE_STATUS_EXPIRED = "EXPIRED"
private const val REMOTE_FILE_OPERATION_POLL_INTERVAL_MILLIS = 750L
private const val REMOTE_DOWNLOAD_PERSIST_INTERVAL_MILLIS = 1_000L
private const val REMOTE_SHELL_CATALOG_TIMEOUT_MILLIS = 30_000L
private const val REMOTE_FILE_PATH_MAX_BYTES = 4_096
private const val REMOTE_FILE_LIST_PAGE_MAX_ENTRIES = 500

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
    private val remoteDownloadStore: RemoteDownloadRecordStore =
        SharedPreferencesRemoteDownloadRecordStore(activity)
    private val storedRemoteDownloads = remoteDownloadStore.load()
    private val interruptedRemoteDownloads = storedRemoteDownloads
        .filter { it.status.isActive }
    private val restoredRemoteDownloads = restoreRemoteDownloadRecords(storedRemoteDownloads).also {
        remoteDownloadStore.save(it)
    }
    private val remoteFileDownloader = RemoteFileDownloader(
        contentResolver = activity.contentResolver,
        client = http,
        allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
    )
    private val terminalSnapshotReadinessHttp = http.newBuilder()
        .callTimeout(TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .build()
    private val updatePreferences = activity.getSharedPreferences(UPDATE_PREFERENCES, android.content.Context.MODE_PRIVATE)
    private val nativeAuthHttp = secureNativeHttpClient()
    private val nativeOidc = NativeOidcClient(nativeAuthHttp, Json { ignoreUnknownKeys = true })
    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val connectivityManager = activity.getSystemService(ConnectivityManager::class.java)
    private val realtimeNetworkLock = Any()
    private var realtimeNetwork: Network? = null
    private var hasObservedRealtimeNetwork = false
    private var realtimeNetworkCallbackRegistered = false
    private val realtimeNetworkAvailableCount = AtomicInteger(0)
    private val realtimeNetworkLostCount = AtomicInteger(0)
    private val realtimeNetworkReconnectCount = AtomicInteger(0)
    private val realtimeNetworkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            realtimeNetworkAvailableCount.incrementAndGet()
            val reconnect = synchronized(realtimeNetworkLock) {
                val changed = hasObservedRealtimeNetwork && realtimeNetwork != network
                hasObservedRealtimeNetwork = true
                realtimeNetwork = network
                changed
            }
            if (reconnect) {
                realtimeNetworkReconnectCount.incrementAndGet()
                scope.launch {
                    realtimeMultiplexer?.onNetworkAvailable()
                }
            }
        }

        override fun onLost(network: Network) {
            realtimeNetworkLostCount.incrementAndGet()
            synchronized(realtimeNetworkLock) {
                hasObservedRealtimeNetwork = true
                if (realtimeNetwork == network) realtimeNetwork = null
            }
        }
    }
    private val liveSessions = ConcurrentHashMap<UUID, LiveProductSession>()
    @Volatile private var realtimeMultiplexer: ProductionRealtimeMultiplexer? = null
    private val historyEngines = ConcurrentHashMap<UUID, TermuxTerminalEngine>()
    private val historyDownloads = ConcurrentHashMap<UUID, HistoryDownloadRequest>()
    private val transcriptLoads = ConcurrentHashMap<TranscriptLoadKey, Deferred<Unit>>()
    private val deletedSessionIds = ConcurrentHashMap.newKeySet<UUID>()
    private val newlyLaunchedSessionIds = ConcurrentHashMap.newKeySet<UUID>()
    private val remoteDownloadJobs = ConcurrentHashMap<UUID, Job>()
    private var remoteDownloadRecoveryJob: Job? = null
    private val remoteDownloadStateLock = Any()
    private val liveSessionLock = Any()
    private val accessStateLock = Mutex()
    private val refreshStateLock = Mutex()
    private val sessionLifecycleLock = Mutex()
    private val updateCheckLock = Mutex()
    private val updateDownloadLock = Mutex()
    private val keyMetadataRefreshLock = Mutex()
    private val realtimeAccessToken = RealtimeAccessTokenSource()
    private val authenticationLifecycle = AuthenticationLifecycleFence()
    private var pendingBrowserAuthenticationGeneration: Long? = null
    private val tokenRefresh = TokenRefreshCoordinator(::requestFreshAccessToken)
    // The first production client is intentionally pinned to the public service.
    // Migrate older installs that may still have a locally edited endpoint.
    private val initialServerUrl = PRODUCT_PUBLIC_SERVER_URL.also {
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
    private val updateArchitecture = Build.SUPPORTED_ABIS.firstOrNull()?.takeIf { it.isNotBlank() } ?: "universal"
    private val updateRequestKey = "$PRODUCT_APPSHIP_SLUG|android|$updateArchitecture|stable|${BuildConfig.VERSION_CODE}"
    private val updateDownloader = ProductUpdateDownloader(
        context = activity,
        client = http,
        currentBuild = BuildConfig.VERSION_CODE.toLong(),
    )

    private fun accountDisplayLabel(): String? = sessionStore.accountLogin

    private val mutableSnapshot = MutableStateFlow(
        ProductSnapshot(
            signedIn = authState?.isAuthorized == true,
            accountLabel = sessionStore.accountLogin,
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
            remoteDownloads = restoredRemoteDownloads,
        ),
    )
    override val snapshot: StateFlow<ProductSnapshot> = mutableSnapshot.asStateFlow()
    private val billingStateJob = scope.launch {
        billing.snapshot.collect { state ->
            mutableSnapshot.value = mutableSnapshot.value.copy(billing = state)
        }
    }
    private val sessionCatalogPollJob = scope.launch {
        while (isActive) {
            delay(SESSION_CATALOG_POLL_INTERVAL_MILLIS)
            if (!mutableSnapshot.value.signedIn ||
                mutableSnapshot.value.enrollmentState == ProductEnrollmentState.REVOKED
            ) continue
            try {
                withApi { api -> pollSessionCatalogWithApi(api) }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Throwable) {
                // A transient catalog poll must not replace the authoritative
                // global connection state or flash a user-visible error.
            }
        }
    }

    init {
        realtimeNetworkCallbackRegistered = runCatching {
            connectivityManager.registerDefaultNetworkCallback(realtimeNetworkCallback)
            true
        }.getOrDefault(false)
        if (authState?.isAuthorized == true) {
            scope.launch { runCatching { refresh() } }
        }
        if (interruptedRemoteDownloads.isNotEmpty()) {
            remoteDownloadRecoveryJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                withContext(NonCancellable) {
                    withContext(Dispatchers.IO) {
                        interruptedRemoteDownloads.forEach { record ->
                            Uri.parse(record.targetUri).takeIf {
                                it.scheme == android.content.ContentResolver.SCHEME_CONTENT
                            }?.let { uri ->
                                remoteFileDownloader.cleanupIncompleteDocument(uri)
                                remoteFileDownloader.releasePersistedPermission(uri)
                            }
                        }
                    }
                    // A killed process may also have left Windows compressing
                    // or uploading. Reuse the persisted operation ID when the
                    // restored account can still authenticate.
                    interruptedRemoteDownloads.forEach { record ->
                        record.operationId?.let { operationId ->
                            cancelKnownRemoteFileOperationBestEffort(record.pcId, operationId)
                        }
                    }
                }
            }
        }
    }

    override suspend fun configureServer(url: String) {
        val normalized = PRODUCT_PUBLIC_SERVER_URL
        val changed = normalized != sessionStore.serverUrl
        if (changed) {
            cancelRemoteDownloadsAndJoin(clearMetadata = true)
            closeLiveSessions()
            closeHistoryEngines()
            clearRoomCache()
            authenticationLifecycle.invalidate()
            authState = null
            realtimeAccessToken.clear()
            sessionStore.clearAuthState()
            sessionStore.accountLogin = null
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
        val authenticationGeneration = authenticationLifecycle.invalidate()
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
            requireCurrentAuthentication(authenticationGeneration)
            val subject = OidcJwtClaims.accessTokenSubject(payload.accessToken, configuration.issuer, configuration.clientId)
            activateAccount(configuration, subject, payload, authenticationGeneration)
            sessionStore.accountLogin = username
            bootstrapAuthenticatedSession()
        } catch (error: NativeOidcException) {
            if (!closed && authenticationLifecycle.isCurrent(authenticationGeneration)) publishFailure(error)
            if (!closed && authenticationLifecycle.isCurrent(authenticationGeneration) && error.browserFallbackAvailable) {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    authenticationState = ProductAuthenticationState.BROWSER_SETUP_REQUIRED,
                    connectionState = ProductConnectionState.ERROR,
                    connectionError = null,
                )
            }
            throw error
        } catch (error: Throwable) {
            if (!closed && authenticationLifecycle.isCurrent(authenticationGeneration)) publishFailure(error)
            throw error
        } finally {
            password.fill('\u0000')
        }
    }

    override suspend fun register(
        username: String,
        email: String,
        firstName: String,
        lastName: String,
        password: CharArray,
        confirm: CharArray,
    ) = withClearedRegistrationSecrets(password, confirm) {
        check(!closed) { "Repository is closed" }
        val body = accountRegistrationRequestBody(
            username = username,
            email = email,
            firstName = firstName,
            lastName = lastName,
            password = password,
            confirm = confirm,
        )
        val root = PRODUCT_PUBLIC_SERVER_URL
        val request = Request.Builder()
            .url("$root/v1/auth/register")
            .post(body.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .header("Cache-Control", "no-store")
            .build()
        withContext(Dispatchers.IO) {
            nativeAuthHttp.newCall(request).execute().use { response ->
                if (!response.isSuccessful) throw IllegalStateException("Account registration was rejected")
            }
        }
    }

    override suspend fun checkForUpdate(force: Boolean): ProductUpdateStatus = updateCheckLock.withLock {
        withContext(Dispatchers.IO) {
            val baseUrl = BuildConfig.TERMLINK_APPSHIP_BASE_URL.trim().trimEnd('/')
            if (baseUrl.isBlank()) {
                return@withContext publishUpdateStatus(
                    ProductUpdateStatus(
                        decision = ProductUpdateDecision.NOT_CONFIGURED,
                        message = "Update service is not configured",
                        checkedAt = Instant.now(),
                    ),
                )
            }

            val cached = cachedUpdateStatus(baseUrl)
            val lastCheckedAt = updatePreferences.getLong(UPDATE_CHECKED_AT_KEY, 0L)
            if (
                !force &&
                cached != null &&
                isUpdateCacheFresh(
                    lastCheckedAt,
                    System.currentTimeMillis(),
                    intervalMs = UPDATE_CACHE_INTERVAL_MILLIS,
                )
            ) {
                return@withContext publishCheckedUpdateStatus(
                    cached.copy(fromCache = true, checkedAt = Instant.now()),
                    force = false,
                )
            }

            val status = try {
                val checkUrl = AppShipUpdateProtocol.buildCheckUrl(
                    baseUrl = baseUrl,
                    slug = PRODUCT_APPSHIP_SLUG,
                    architecture = updateArchitecture,
                    channel = UPDATE_CHANNEL,
                    buildNumber = BuildConfig.VERSION_CODE.toLong(),
                )
                val request = Request.Builder()
                    .url(checkUrl)
                    .get()
                    .header("Accept", "application/json")
                    .header("Cache-Control", "no-cache")
                    .apply {
                        // Do not send an ETag from another build/ABI/channel.
                        // NuanYa only revalidates when the request-key-bound
                        // cached response is available; otherwise a 304
                        // cannot be safely interpreted by this client.
                        if (cached != null) {
                            updatePreferences.getString(UPDATE_ETAG_KEY, null)?.let { header("If-None-Match", it) }
                        }
                    }
                    .build()
                http.newCall(request).execute().use { response ->
                    when (response.code) {
                        200 -> {
                            val body = readLimitedText(response.body?.byteStream())
                            val decision = AppShipUpdateProtocol.parseAndValidate(
                                body = body,
                                baseUrl = baseUrl,
                                expectedSlug = PRODUCT_APPSHIP_SLUG,
                                expectedChannel = UPDATE_CHANNEL,
                                expectedArchitecture = updateArchitecture,
                                currentBuild = BuildConfig.VERSION_CODE.toLong(),
                            )
                            saveUpdateCache(body, response.header("ETag"))
                            decision.copy(fromCache = false, checkedAt = Instant.now())
                        }
                        304 -> {
                            val cachedDecision = cached
                                ?: throw AppShipUpdateException("更新缓存已失效，请重新检测")
                            updatePreferences.edit()
                                .putLong(UPDATE_CHECKED_AT_KEY, System.currentTimeMillis())
                                .commit()
                            cachedDecision.copy(fromCache = true, checkedAt = Instant.now())
                        }
                        404 -> throw AppShipUpdateException("更新服务尚未发布 TermLink 版本")
                        422 -> throw AppShipUpdateException("当前应用的更新参数配置无效")
                        429 -> throw AppShipUpdateException("更新检测过于频繁，请稍后重试")
                        in 500..599 -> throw AppShipUpdateException("更新服务暂时不可用，请稍后重试")
                        else -> throw AppShipUpdateException("更新检测失败（HTTP ${response.code}）")
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: AppShipUpdateException) {
                ProductUpdateStatus(
                    decision = ProductUpdateDecision.ERROR,
                    message = error.message?.take(160) ?: "Update check failed",
                    checkedAt = Instant.now(),
                )
            } catch (error: java.io.IOException) {
                ProductUpdateStatus(
                    decision = ProductUpdateDecision.ERROR,
                    message = "Unable to connect to update service",
                    checkedAt = Instant.now(),
                )
            } catch (error: Throwable) {
                ProductUpdateStatus(
                    decision = ProductUpdateDecision.ERROR,
                    message = error.message?.take(160) ?: "Update check failed",
                    checkedAt = Instant.now(),
                )
            }
            publishCheckedUpdateStatus(status, force)
        }
    }

    override suspend fun downloadUpdate() = updateDownloadLock.withLock {
        val update = mutableSnapshot.value.updateStatus
        val build = update.buildNumber
            ?: return@withLock publishUpdateDownloadState(
                expectedBuild = null,
                state = ProductUpdateDownloadState.Failed(
                    "Update metadata is incomplete / 更新信息不完整",
                ),
            )
        if (update.decision !in setOf(ProductUpdateDecision.OPTIONAL, ProductUpdateDecision.REQUIRED)) {
            return@withLock
        }
        publishUpdateDownloadState(
            build,
            ProductUpdateDownloadState.Downloading(0L, update.artifactSizeBytes ?: 0L),
        )
        try {
            val file = updateDownloader.download(
                update = update,
                onProgress = { downloaded, total ->
                    publishUpdateDownloadState(
                        build,
                        ProductUpdateDownloadState.Downloading(downloaded, total),
                    )
                },
                onVerifying = {
                    publishUpdateDownloadState(build, ProductUpdateDownloadState.Verifying)
                },
            )
            publishUpdateDownloadState(build, ProductUpdateDownloadState.Ready(file.absolutePath))
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            publishUpdateDownloadState(
                build,
                ProductUpdateDownloadState.Failed(
                    error.message?.take(240) ?: "APK download failed / APK 下载失败",
                ),
            )
        }
    }

    override fun dismissUpdate() {
        mutableSnapshot.update { snapshot ->
            snapshot.copy(updateStatus = snapshot.updateStatus.copy(dialogVisible = false))
        }
    }

    override suspend fun beginBrowserRegistration() = beginBrowserAuth(BrowserAuthFlow.REGISTRATION)

    override suspend fun beginBrowserAccountSetup() = beginBrowserAuth(BrowserAuthFlow.ACCOUNT_SETUP)

    private suspend fun beginBrowserAuth(flow: BrowserAuthFlow) {
        check(!closed) { "Repository is closed" }
        val authenticationGeneration = authenticationLifecycle.invalidate()
        try {
            val configuration = loadOidcConfiguration()
            requireCurrentAuthentication(authenticationGeneration)
            mutableSnapshot.value = mutableSnapshot.value.copy(authenticationState = ProductAuthenticationState.BROWSER_SETUP_REQUIRED)
            authState = AuthState(configuration.serviceConfiguration)
            pendingBrowserAuthenticationGeneration = authenticationGeneration
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
            if (pendingBrowserAuthenticationGeneration == authenticationGeneration) {
                pendingBrowserAuthenticationGeneration = null
            }
            if (!closed && authenticationLifecycle.isCurrent(authenticationGeneration)) publishFailure(error)
            throw error
        }
    }

    fun handleAuthorizationResult(data: Intent?) {
        val authenticationGeneration = pendingBrowserAuthenticationGeneration ?: return
        pendingBrowserAuthenticationGeneration = null
        if (data == null) {
            if (!closed && authenticationLifecycle.isCurrent(authenticationGeneration) && authState != null) {
                publishFailure(IllegalStateException("OIDC sign-in was canceled"))
            }
            return
        }
        val response = AuthorizationResponse.fromIntent(data)
        val exception = AuthorizationException.fromIntent(data)
        if (closed || !authenticationLifecycle.isCurrent(authenticationGeneration)) return
        val state = authState ?: return
        state.update(response, exception)
        authState = state
        if (response == null) {
            publishFailure(exception ?: IllegalStateException("OIDC authorization failed"))
            return
        }
        authService.performTokenRequest(response.createTokenExchangeRequest()) { tokenResponse, tokenException ->
            state.update(tokenResponse, tokenException)
            if (!isCurrentAuthentication(authenticationGeneration, state)) return@performTokenRequest
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
                    requireCurrentAuthentication(authenticationGeneration, state)
                    if (sessionStore.accountLogin == null) {
                        sessionStore.accountLogin = OidcJwtClaims.displayIdentifier(tokenResponse.idToken ?: accessToken)
                    }
                    // Persist the issuer+subject scope first. If the process is
                    // killed after AuthState is saved, startup must never see
                    // an authorized session without an account-scoped key.
                    establishAccountScope(AccountScope.canonical(configuration.issuer, subject)) {
                        requireCurrentAuthentication(authenticationGeneration, state)
                    }
                    requireCurrentAuthentication(authenticationGeneration, state)
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
                }.onFailure { error ->
                    if (isCurrentAuthentication(authenticationGeneration, state)) publishFailure(error)
                }
            }
        }
    }

    override suspend fun signOut() {
        cancelRemoteDownloadsAndJoin(clearMetadata = true)
        authenticationLifecycle.invalidate()
        authState = null
        realtimeAccessToken.clear()
        sessionStore.clearAuthState()
        sessionStore.accountLogin = null
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
            serverUrl = PRODUCT_PUBLIC_SERVER_URL,
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
                accountLabel = accountDisplayLabel(),
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
        try {
            freshAccessToken()
            openSession(sessionId)
            val live = liveSessions.getValue(sessionId)
            val acquired = live.controlRequestLock.withLock {
                acquireControlLeaseWithRetry(
                    remoteState = { live.client.sessionState.value },
                    request = live.client::requestControl,
                )
            }
            if (acquired) appendActivity("Control granted", "Session $sessionId", Severity.SUCCESS)
            clearSessionOpenFailure(sessionId)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            val classified = classifySessionOpenException(error, SessionFailureStage.CONTROL)
            observeSessionOpenFailure(
                sessionId = sessionId,
                failure = classified.failure,
                reconnect = liveSessions[sessionId] == null,
            )
            throw classified
        }
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
        // releaseControl also advances the client's local arbitration fence
        // when no lease has arrived yet. Include every live client so an HTTP
        // acquire response that races with onStop is deleted as stale.
        val active = liveSessions.values.toList()
        if (active.isEmpty()) return

        val tokenFailure = try {
            freshAccessToken()
            null
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            error
        }
        if (tokenFailure != null) publishFailure(tokenFailure)
        var firstFailure: Throwable? = null
        if (tokenFailure != null) firstFailure = tokenFailure
        active.forEach { live ->
            try {
                live.client.releaseControl()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Throwable) {
                firstFailure = firstFailure ?: error
            }
        }
        if (firstFailure != null) {
            publishFailure(firstFailure!!)
        } else {
            appendActivity("Control leases released", "Device backgrounded", Severity.INFO)
        }
    }

    override suspend fun openSession(sessionId: UUID): Unit = sessionLifecycleLock.withLock {
        liveSessions[sessionId]?.let { existing ->
            if (!shouldReplaceLiveSessionForOpen(
                    liveSessionExists = true,
                    remoteState = mutableSnapshot.value.sessionStates[sessionId],
                )
            ) return@withLock

            // A failed bootstrap can leave an installed renderer/client behind
            // while the UI exposes retry. Reusing it makes retry a no-op. Fence
            // that exact instance, close it, and rebuild under the lifecycle
            // mutex so no second client can register the same session UUID.
            val removed = synchronized(liveSessionLock) {
                liveSessions.remove(sessionId, existing)
            }
            if (removed) existing.close()
            if (liveSessions.containsKey(sessionId)) return@withLock
        }
        // Keep every opened session alive. The Android pager is a viewer over
        // multiple concurrent terminals; switching pages must not terminate
        // another session's realtime stream.
        val product = mutableSnapshot.value
        val summary = product.sessions.firstOrNull { it.id == sessionId }
            ?: throw sessionOpenException(SessionOpenFailureCode.SESSION_UNAVAILABLE)
        if (summary.state == SessionState.EXITED) {
            throw sessionOpenException(SessionOpenFailureCode.SESSION_ENDED)
        }
        if (!summary.online) throw sessionOpenException(SessionOpenFailureCode.SESSION_OFFLINE)
        val pc = product.pcs.firstOrNull { it.id == summary.pcId }
            ?: throw sessionOpenException(SessionOpenFailureCode.PC_UNAVAILABLE)
        val pcDevice = product.devices.firstOrNull {
            it.id == pc.deviceId && it.type == DeviceType.WINDOWS_PC && it.trustState == DeviceTrustState.TRUSTED
        } ?: throw sessionOpenException(SessionOpenFailureCode.PC_NOT_TRUSTED)
        if (pcDevice.exchangePublicKey.size != 32) {
            throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        }
        freshAccessToken()
        mutableSnapshot.update { current ->
            current.copy(
                sessionOpenFailures = current.sessionOpenFailures - sessionId,
                sessionStates = current.sessionStates + (
                    sessionId to RemoteSessionState.Reconnecting(
                        lastAckedSequence = current.liveAcknowledgements[sessionId]?.toLong() ?: 0L,
                        reason = SESSION_BOOTSTRAP_IN_PROGRESS_REASON,
                    )
                ),
            )
        }
        val root = PRODUCT_PUBLIC_SERVER_URL
        val stored = identity()
        val engine = TermuxTerminalEngine(
            initialSize = TerminalSize(summary.columns.coerceAtLeast(1), summary.rows.coerceAtLeast(1)),
        )
        val restoredHistory = try {
            restoreLiveHistory(
                session = summary,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                engine = engine,
                allowFreshReplayFromZero = sessionId in newlyLaunchedSessionIds,
            )
        } catch (error: CancellationException) {
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_PREPARE)
            observeBootstrapFailure(sessionId, classified.failure)
            throw classified
        }
        val transport = try {
            deviceRealtimeMultiplexer(stored).register(sessionId)
        } catch (error: CancellationException) {
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            observeBootstrapFailure(sessionId, classified.failure)
            throw classified
        }
        val client = try {
            ProductionRealtimeSessionClient(
                engine = engine,
                config = ProductionRealtimeSessionConfig(
                    rootUrl = root,
                    tokenProvider = AccessTokenProvider(realtimeAccessToken::requireCurrent),
                    androidDeviceId = stored.deviceId,
                    pcDeviceId = pc.deviceId,
                    sessionId = sessionId,
                    keyEpoch = pc.keyEpoch,
                    pcExchangePublicKey = pcDevice.exchangePublicKey.copyOf(),
                ),
                identity = stored.identity,
                parentScope = scope,
                transport = transport,
                httpClient = http,
                allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
                inputSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveInputSequenceRange(sessionId)
                },
                envelopeSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveEnvelopeSequenceRange(sessionId)
                },
                initialRendererState = restoredHistory.rendererState,
                initialObservedLocalInputEpoch = restoredHistory.localInputEpoch,
                acknowledgementObserver = { expectedGeneration, sequence ->
                    if (sessionId in deletedSessionIds) {
                        DurableAckCommit(sequence, expectedGeneration)
                    } else {
                        val persisted = historyRepository.putAckCursor(
                            sessionId,
                            LIVE_OUTPUT_ACK_DIRECTION,
                            expectedGeneration,
                            sequence.toULong(),
                        )
                        observeLiveAcknowledgement(sessionId, persisted)
                        DurableAckCommit(persisted.toLong(), expectedGeneration)
                    }
                },
                acknowledgementResetObserver = { expectedGeneration ->
                    if (sessionId in deletedSessionIds) {
                        DurableAckCommit(0, expectedGeneration + 1)
                    } else {
                        val reset = historyRepository.resetAckCursor(
                            sessionId,
                            LIVE_OUTPUT_ACK_DIRECTION,
                            expectedGeneration,
                        )
                        observeLiveAcknowledgement(sessionId, reset.sequence)
                        DurableAckCommit(reset.sequence.toLong(), reset.rendererGeneration)
                    }
                },
                authoritativeMetadataRefreshObserver = { refreshLiveKeyMetadata() },
            )
        } catch (error: CancellationException) {
            transport.close()
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            transport.close()
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            observeBootstrapFailure(sessionId, classified.failure)
            throw classified
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
            return@withLock
        }
        observeBootstrap(sessionId, restoredHistory)
        live.stateJob = scope.launch {
            combine(
                client.sessionState,
                engine.authoritativeSize,
                client.sessionTitle,
                engine.title,
            ) { state, size, publishedTitle, rendererTitle ->
                // Console-title polling and OSC parsing are complementary.
                // Prefer the ordered E2EE metadata event and retain the
                // renderer title as compatibility fallback for older Agents.
                RuntimeSessionProjection(
                    state,
                    size,
                    liveSessionTitleOverride(publishedTitle, rendererTitle),
                )
            }.collect { runtime ->
                mutableSnapshot.update { product ->
                    val index = product.sessions.indexOfFirst { it.id == sessionId }
                    if (index < 0) return@update product
                    val projected = projectLiveSessionSummary(
                        product.sessions[index],
                        runtime.state,
                        runtime.size,
                    )
                    val sessions = product.sessions.toMutableList().also { it[index] = projected }
                    product.copy(
                        sessions = sessions,
                        sessionStates = product.sessionStates + (sessionId to runtime.state),
                        sessionTitles = when {
                            runtime.titleOverride == null -> product.sessionTitles
                            runtime.titleOverride.isBlank() -> product.sessionTitles - sessionId
                            else -> product.sessionTitles + (sessionId to runtime.titleOverride)
                        },
                    )
                }
                val state = runtime.state
                if (state is RemoteSessionState.HistoryGap && live.recoveryJob?.isActive != true) {
                    live.recoveryJob = scope.launch {
                        recoverLiveHistoryGap(sessionId, live)
                    }
                }
            }
        }
        live.tokenRefreshJob = scope.launch {
            while (isActive && mutableSnapshot.value.signedIn) {
                delay(LIVE_TOKEN_REFRESH_INTERVAL_MILLIS)
                runCatching { freshAccessToken() }
                    .onFailure { error ->
                        if (isActive && mutableSnapshot.value.signedIn) {
                            publishFailure(error)
                            delay(LIVE_TOKEN_REFRESH_RETRY_MILLIS)
                        }
                    }
            }
        }
        try {
            client.start()
            newlyLaunchedSessionIds.remove(sessionId)
        } catch (error: CancellationException) {
            synchronized(liveSessionLock) {
                liveSessions.remove(sessionId, live)
            }
            live.close()
            throw error
        } catch (error: Throwable) {
            synchronized(liveSessionLock) {
                liveSessions.remove(sessionId, live)
            }
            live.close()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            observeBootstrapFailure(sessionId, classified.failure)
            throw classified
        }
    }

    override suspend fun closeSessionView(sessionId: UUID): Unit = sessionLifecycleLock.withLock {
        check(!closed) { "Repository is closed" }
        newlyLaunchedSessionIds.remove(sessionId)
        val live = synchronized(liveSessionLock) { liveSessions.remove(sessionId) }
        live?.closeAndJoin()
        // Closing an Android tab is intentionally local. Keep the authoritative
        // catalog row and encrypted history so the Windows shell remains visible
        // on Devices and can be opened again without creating a new session.
        mutableSnapshot.update { snapshot ->
            snapshot.copy(
                sessionStates = snapshot.sessionStates - sessionId,
                sessionOpenFailures = snapshot.sessionOpenFailures - sessionId,
                transcriptViewports = snapshot.transcriptViewports - sessionId,
                liveAcknowledgements = snapshot.liveAcknowledgements - sessionId,
                bootstrapSources = snapshot.bootstrapSources - sessionId,
            )
        }
    }

    override fun terminalEngine(sessionId: UUID): TermuxTerminalEngine? = liveSessions[sessionId]?.engine

    override fun historyEngine(historyObjectId: UUID): TermuxTerminalEngine? = historyEngines[historyObjectId]

    override suspend fun loadTranscriptBefore(sessionId: UUID) {
        check(!closed) { "Repository is closed" }
        val viewport = mutableSnapshot.value.transcriptViewports[sessionId] ?: return
        val before = viewport.nextBeforeLineId ?: return
        val key = TranscriptLoadKey(
            sessionId = sessionId,
            snapshotSequence = viewport.snapshotAnchorSequence,
            beforeLineId = before,
            cursor = viewport.nextCursor,
            viewportGeneration = viewport.viewportGeneration,
        )
        awaitCoalescedTranscriptLoad(scope, transcriptLoads, key) {
            sessionLifecycleLock.withLock { loadTranscriptPage(key) }
        }
    }

    override fun clearTranscript(sessionId: UUID) {
        mutableSnapshot.update { product ->
            val existing = product.transcriptViewports[sessionId] ?: return@update product
            product.copy(
                transcriptViewports = product.transcriptViewports + (
                    sessionId to existing.copy(
                        nextBeforeLineId = existing.initialBeforeLineId.takeIf { it > 0u },
                        nextCursor = null,
                        pages = emptyList(),
                        loadedObjectIds = emptySet(),
                        consumedCursors = emptySet(),
                        loading = false,
                        error = null,
                        hasMoreBefore = existing.initialBeforeLineId > 0u,
                        viewportGeneration = Math.addExact(existing.viewportGeneration, 1L),
                    )
                ),
            )
        }
    }

    /** Debug/E2E hook: returns cache counts only and never opens encrypted objects. */
    internal suspend fun debugHistoryObjectCounts(sessionId: UUID): HistoryObjectCounts =
        historyRepository.historyObjectCounts(sessionId)

    internal fun debugRealtimeDiagnostics(sessionId: UUID): ProductRealtimeDiagnostics? {
        val realtime = liveSessions[sessionId]?.client?.diagnostics() ?: return null
        return ProductRealtimeDiagnostics(
            socketPhase = realtime.multiplexer.phase,
            socketGeneration = realtime.multiplexer.socket.generation,
            connectedGeneration = realtime.multiplexer.socket.connectedGeneration,
            socketState = realtime.multiplexer.socket.state,
            endpointCount = realtime.multiplexer.endpointCount,
            transportState = realtime.transportState,
            remoteState = realtime.remoteState,
            transportCollectorActive = realtime.transportCollectorActive,
            reconnectActive = realtime.reconnectActive,
            subscriberReady = realtime.subscriberReady,
            reconnectAttempt = realtime.reconnectAttempt,
            subscribeSendCount = realtime.subscribeSendCount,
            lastSubscribeCursor = realtime.lastSubscribeCursor,
            flowControlSendCount = realtime.flowControlSendCount,
            lastReceivedSequence = realtime.lastReceivedSequence,
            lastHandledSequence = realtime.lastHandledSequence,
            networkAvailableCount = realtimeNetworkAvailableCount.get(),
            networkLostCount = realtimeNetworkLostCount.get(),
            networkReconnectCount = realtimeNetworkReconnectCount.get(),
        )
    }

    override suspend fun stopSession(sessionId: UUID) {
        check(!closed) { "Repository is closed" }
        val product = mutableSnapshot.value
        val session = product.sessions.firstOrNull { it.id == sessionId }
            ?: error("The selected session is unavailable")
        require(session.online) { "Only an online session can be closed" }

        // Opening is idempotent and registers this exact UUID on the shared
        // device socket. The client then waits for its own SUBSCRIBE route to
        // be ready before sending the E2EE termination request.
        openSession(sessionId)
        val live = liveSessions[sessionId] ?: error("The selected session is no longer available")
        live.client.requestTermination()

        // The encrypted realtime request intentionally contains no server-
        // readable command.  The Windows Agent therefore needs a short window
        // to terminate the exact SessionHost and publish EXITED before the
        // REST deletion fence will accept this session.  Retry only that
        // explicit active-session conflict; authentication, authorization and
        // transport failures must remain visible to the user.  Once deletion
        // succeeds, permanently fence in-flight catalog/realtime events and
        // remove the renderer, Room history, and directory row together.
        withApi { api ->
            refreshStateLock.withLock {
                sessionLifecycleLock.withLock {
                    deleteClosedSessionAfterTermination(
                        deleteRemote = { api.delete("/v1/sessions/$sessionId") },
                        deleteLocal = { deleteLocalSession(sessionId) },
                    )
                }
            }
        }
        appendActivity(
            "Session closed",
            "The Windows command line was closed and its session record was removed",
            Severity.INFO,
        )
    }

    override suspend fun deleteSession(sessionId: UUID) {
        check(!closed) { "Repository is closed" }
        withApi { api ->
            // A refresh that began before this request must publish before the
            // deletion, never resurrect the deleted session afterward. The
            // lifecycle lock also prevents an open/replay from installing a
            // new client or history engine between server success and cleanup.
            refreshStateLock.withLock {
                sessionLifecycleLock.withLock {
                    deleteSessionAfterRemoteSuccess(
                        deleteRemote = { api.delete("/v1/sessions/$sessionId") },
                        deleteLocal = { deleteLocalSession(sessionId) },
                    )
                }
            }
        }
    }

    override suspend fun sendPaste(sessionId: UUID, text: String) {
        require(text.isNotEmpty()) { "Paste text is empty" }
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        check(live.engine.submitPaste(text)) { "Request control before sending input" }
    }

    override suspend fun sendCommand(sessionId: UUID, text: String) {
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        require(text.isNotEmpty()) { "Command text is empty" }
        val submission = live.client.remoteSession.submitInput(TerminalInput.Text(text + '\r'))
        commandSubmissionFailure(submission)?.let(::error)
    }

    override suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction) {
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        val accepted = when (action) {
            TerminalAction.ESCAPE -> live.engine.submitKeyCode(KeyEvent.KEYCODE_ESCAPE)
            TerminalAction.TAB -> live.engine.submitKeyCode(KeyEvent.KEYCODE_TAB)
            TerminalAction.ENTER -> live.engine.submitKeyCode(KeyEvent.KEYCODE_ENTER)
            TerminalAction.BACKSPACE -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DEL)
            TerminalAction.DELETE -> live.engine.submitKeyCode(KeyEvent.KEYCODE_FORWARD_DEL)
            TerminalAction.ARROW_LEFT -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_LEFT)
            TerminalAction.ARROW_UP -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP)
            TerminalAction.ARROW_DOWN -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_DOWN)
            TerminalAction.ARROW_RIGHT -> live.engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_RIGHT)
            TerminalAction.CTRL_C -> live.engine.submitCodePoint('c'.code, controlDown = true)
        }
        check(accepted) { "Request control before sending input" }
    }

    override suspend fun sendTerminalKeyCombination(sessionId: UUID, combination: String) {
        val live = liveSessions[sessionId] ?: error("Open the session before sending input")
        val parsed = TerminalKeyCombination.parse(combination)
            ?: throw IllegalArgumentException("Unsupported terminal key combination")
        val printable = when (parsed.key) {
            "Space" -> ' '.code
            else -> parsed.printableCodePoint
        }
        val accepted = if (printable != null) {
            val shifted = if (parsed.shift) terminalShiftedCodePoint(printable) else printable
            live.engine.submitCodePoint(
                shifted,
                controlDown = parsed.control,
                altDown = parsed.alt,
            )
        } else {
            val keyCode = when (parsed.key) {
                "Esc" -> KeyEvent.KEYCODE_ESCAPE
                "Tab" -> KeyEvent.KEYCODE_TAB
                "Enter" -> KeyEvent.KEYCODE_ENTER
                "Backspace" -> KeyEvent.KEYCODE_DEL
                "Delete" -> KeyEvent.KEYCODE_FORWARD_DEL
                "Left" -> KeyEvent.KEYCODE_DPAD_LEFT
                "Up" -> KeyEvent.KEYCODE_DPAD_UP
                "Down" -> KeyEvent.KEYCODE_DPAD_DOWN
                "Right" -> KeyEvent.KEYCODE_DPAD_RIGHT
                "Home" -> KeyEvent.KEYCODE_MOVE_HOME
                "End" -> KeyEvent.KEYCODE_MOVE_END
                "PageUp" -> KeyEvent.KEYCODE_PAGE_UP
                "PageDown" -> KeyEvent.KEYCODE_PAGE_DOWN
                "Insert" -> KeyEvent.KEYCODE_INSERT
                else -> parsed.key.drop(1).toIntOrNull()
                    ?.takeIf { parsed.key.startsWith("F") && it in 1..12 }
                    ?.let { KeyEvent.KEYCODE_F1 + it - 1 }
                    ?: throw IllegalArgumentException("Unsupported terminal key: ${parsed.key}")
            }
            live.engine.submitKeyCode(
                keyCode,
                controlDown = parsed.control,
                altDown = parsed.alt,
                shiftDown = parsed.shift,
            )
        }
        check(accepted) { "Request control before sending input" }
    }

    override suspend fun replayHistory(entry: HistoryEntry): Unit = sessionLifecycleLock.withLock {
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

    override suspend fun browseRemoteDirectory(
        pcId: UUID,
        pcName: String,
        path: String,
        offset: Long?,
    ) {
        require(path.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES && '\u0000' !in path) {
            "The Windows path is invalid"
        }
        require(offset == null || offset >= 0L) { "The directory offset is invalid" }
        val requestId = UUID.randomUUID()
        mutableSnapshot.update { current ->
            current.copy(
                remoteFileBrowser = reduceRemoteFileBrowser(
                    current.remoteFileBrowser,
                    RemoteFileBrowserEvent.LoadStarted(requestId, pcId, pcName, path, offset),
                ),
            )
        }
        try {
            val operation = createRemoteFileOperation(
                pcId = pcId,
                operation = REMOTE_FILE_OPERATION_LIST,
                path = path,
                offset = offset,
            )
            val completed = awaitRemoteFileOperation(pcId, operation)
            val page = completed.result?.toRemoteDirectoryPage()
                ?: error("The Windows PC returned no directory listing")
            mutableSnapshot.update { current ->
                current.copy(
                    remoteFileBrowser = reduceRemoteFileBrowser(
                        current.remoteFileBrowser,
                        RemoteFileBrowserEvent.LoadSucceeded(requestId, page),
                    ),
                )
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            mutableSnapshot.update { current ->
                current.copy(
                    remoteFileBrowser = reduceRemoteFileBrowser(
                        current.remoteFileBrowser,
                        RemoteFileBrowserEvent.LoadFailed(requestId, remoteFileErrorMessage(error)),
                    ),
                )
            }
            throw error
        }
    }

    override fun startRemoteDownload(selection: RemoteFileDownloadSelection, targetUri: Uri) {
        check(!closed) { "Repository is closed" }
        require(selection.retryRecordId == null) { "A new download cannot carry a retry record ID" }
        require(targetUri.scheme == android.content.ContentResolver.SCHEME_CONTENT) {
            "The selected Android document is invalid"
        }
        require(selection.remotePath.isNotBlank() &&
            selection.remotePath.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES &&
            '\u0000' !in selection.remotePath
        ) { "The Windows path is invalid" }
        val now = Instant.now()
        val record = RemoteDownloadRecord(
            id = UUID.randomUUID(),
            pcId = selection.pcId,
            pcName = selection.pcName.take(256),
            remotePath = selection.remotePath,
            filename = selection.suggestedFilename.take(255),
            directoryZip = selection.directoryZip,
            status = RemoteDownloadStatus.PREPARING,
            targetUri = targetUri.toString(),
            createdAt = now,
            updatedAt = now,
        )
        updateRemoteDownloads { records -> listOf(record) + records.filterNot { it.id == record.id } }
        launchRemoteDownload(record.id)
    }

    override fun retryRemoteDownload(recordId: UUID, targetUri: Uri) {
        check(!closed) { "Repository is closed" }
        require(targetUri.scheme == android.content.ContentResolver.SCHEME_CONTENT) {
            "The selected Android document is invalid"
        }
        val current = mutableSnapshot.value.remoteDownloads.firstOrNull { it.id == recordId }
            ?: error("The download record is unavailable")
        require(current.status == RemoteDownloadStatus.FAILED || current.status == RemoteDownloadStatus.CANCELLED) {
            "Only failed or cancelled downloads can be retried"
        }
        updateRemoteDownload(recordId) { record ->
            record.copy(
                status = RemoteDownloadStatus.PREPARING,
                targetUri = targetUri.toString(),
                operationId = null,
                totalBytes = null,
                downloadedBytes = 0L,
                updatedAt = Instant.now(),
                error = null,
            )
        }
        launchRemoteDownload(recordId)
    }

    override suspend fun cancelRemoteDownload(recordId: UUID) {
        val current = mutableSnapshot.value.remoteDownloads.firstOrNull { it.id == recordId }
            ?: return
        if (!current.status.isActive) return
        remoteDownloadJobs[recordId]?.cancelAndJoin()
        val operationId = mutableSnapshot.value.remoteDownloads
            .firstOrNull { it.id == recordId }
            ?.operationId
        if (operationId != null) {
            try {
                withApi { api ->
                    val cancelled = remoteFileOperationResponseFromJson(
                        api.delete(remoteFileOperationPath(current.pcId, operationId)).jsonObject,
                    )
                    require(cancelled.id == operationId) {
                        "The server returned a different Windows file operation"
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Throwable) {
                // Local cancellation and SAF cleanup are authoritative for the
                // Android surface even if the already-terminal server task races.
            }
        }
        updateRemoteDownload(recordId) { record ->
            if (record.status == RemoteDownloadStatus.COMPLETED) record else record.copy(
                status = RemoteDownloadStatus.CANCELLED,
                updatedAt = Instant.now(),
                error = null,
            )
        }
    }

    override fun deleteRemoteDownloadRecord(recordId: UUID) {
        val record = mutableSnapshot.value.remoteDownloads.firstOrNull { it.id == recordId } ?: return
        require(record.status.isTerminal) { "An active download cannot be removed" }
        // This deliberately changes metadata only. In particular, completed
        // content:// documents are never passed to DocumentsContract.deleteDocument.
        updateRemoteDownloads { records -> removeRemoteDownloadRecord(records, recordId) }
    }

    override suspend fun openRemoteShell(
        pcId: UUID,
        path: String,
        shellType: RemoteShellType,
    ): UUID {
        require(path.isNotBlank() && path.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES && '\u0000' !in path) {
            "The Windows directory is invalid"
        }
        mutableSnapshot.update { current ->
            current.copy(
                remoteFileBrowser = reduceRemoteFileBrowser(
                    current.remoteFileBrowser,
                    RemoteFileBrowserEvent.ShellStarted(shellType),
                ),
            )
        }
        try {
            val operation = createRemoteFileOperation(
                pcId = pcId,
                operation = REMOTE_FILE_OPERATION_OPEN_TERMINAL,
                path = path,
                shellType = shellType,
            )
            val completed = awaitRemoteFileOperation(pcId, operation)
            val result = completed.result ?: error("The Windows PC returned no terminal session")
            val sessionId = UUID.fromString(result.requiredString("session_id"))
            require(RemoteShellType.fromWireValue(result.requiredString("shell_type")) == shellType) {
                "The Windows PC returned a different shell type"
            }
            val ready = withTimeoutOrNull(REMOTE_SHELL_CATALOG_TIMEOUT_MILLIS) {
                while (true) {
                    withApi { api -> pollSessionCatalogWithApi(api) }
                    if (mutableSnapshot.value.sessions.any { it.id == sessionId && it.online }) break
                    delay(REMOTE_FILE_OPERATION_POLL_INTERVAL_MILLIS)
                }
                true
            } ?: false
            check(ready) { "The new terminal session did not become available" }
            newlyLaunchedSessionIds.add(sessionId)
            mutableSnapshot.update { current ->
                current.copy(
                    remoteFileBrowser = reduceRemoteFileBrowser(
                        current.remoteFileBrowser,
                        RemoteFileBrowserEvent.ShellFinished,
                    ),
                )
            }
            return sessionId
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            mutableSnapshot.update { current ->
                current.copy(
                    remoteFileBrowser = reduceRemoteFileBrowser(
                        current.remoteFileBrowser,
                        RemoteFileBrowserEvent.ShellFailed(remoteFileErrorMessage(error)),
                    ),
                )
            }
            throw error
        }
    }

    private fun launchRemoteDownload(recordId: UUID) {
        val job = scope.launch(start = CoroutineStart.LAZY) { performRemoteDownload(recordId) }
        remoteDownloadJobs.put(recordId, job)?.cancel()
        job.invokeOnCompletion { remoteDownloadJobs.remove(recordId, job) }
        job.start()
    }

    private suspend fun performRemoteDownload(recordId: UUID) {
        val initial = mutableSnapshot.value.remoteDownloads.firstOrNull { it.id == recordId }
            ?: return
        val destination = Uri.parse(initial.targetUri)
        try {
            val operation = createRemoteFileOperation(
                pcId = initial.pcId,
                operation = REMOTE_FILE_OPERATION_DOWNLOAD,
                path = initial.remotePath,
                onCreated = { created ->
                    // This callback runs inside the POST response boundary. A
                    // cancellation delivered immediately after withApi returns
                    // therefore cannot strand an operation before its ID is saved.
                    updateRemoteDownload(recordId) { record ->
                        if (!record.status.isActive) record else record.copy(
                            operationId = created.id,
                            updatedAt = Instant.now(),
                        )
                    }
                },
            )
            val completed = awaitRemoteFileOperation(initial.pcId, operation)
            val descriptor = completed.download?.let(::remoteDownloadDescriptorFromJson)
                ?: error("The server returned no download descriptor")
            require(descriptor.directoryZip == initial.directoryZip) {
                "The Windows PC returned an unexpected download type"
            }
            updateRemoteDownload(recordId) { record ->
                if (!record.status.isActive) record else record.copy(
                    filename = descriptor.filename,
                    directoryZip = descriptor.directoryZip,
                    status = RemoteDownloadStatus.DOWNLOADING,
                    totalBytes = descriptor.sizeBytes,
                    downloadedBytes = 0L,
                    updatedAt = Instant.now(),
                    error = null,
                )
            }
            var lastPersistedAt = 0L
            remoteFileDownloader.download(
                descriptor = descriptor,
                destination = destination,
                onProgress = { downloaded ->
                    val nowMillis = System.currentTimeMillis()
                    val persist = downloaded == descriptor.sizeBytes ||
                        nowMillis - lastPersistedAt >= REMOTE_DOWNLOAD_PERSIST_INTERVAL_MILLIS
                    if (persist) lastPersistedAt = nowMillis
                    updateRemoteDownload(recordId, persist = persist) { record ->
                        if (!record.status.isActive) record else record.copy(
                            status = RemoteDownloadStatus.DOWNLOADING,
                            downloadedBytes = downloaded,
                            updatedAt = Instant.now(),
                        )
                    }
                },
                onVerifying = {
                    updateRemoteDownload(recordId) { record ->
                        if (!record.status.isActive) record else record.copy(
                            status = RemoteDownloadStatus.VERIFYING,
                            downloadedBytes = descriptor.sizeBytes,
                            updatedAt = Instant.now(),
                        )
                    }
                },
            )
            updateRemoteDownload(recordId) { record ->
                if (!record.status.isActive) record else record.copy(
                    status = RemoteDownloadStatus.COMPLETED,
                    downloadedBytes = descriptor.sizeBytes,
                    updatedAt = Instant.now(),
                    error = null,
                )
            }
        } catch (error: CancellationException) {
            cancelRemoteFileOperationBestEffort(recordId, initial.pcId)
            discardIncompleteRemoteDocument(destination)
            updateRemoteDownload(recordId) { record ->
                if (record.status == RemoteDownloadStatus.COMPLETED) record else record.copy(
                    status = RemoteDownloadStatus.CANCELLED,
                    updatedAt = Instant.now(),
                    error = null,
                )
            }
            throw error
        } catch (error: Throwable) {
            cancelRemoteFileOperationBestEffort(recordId, initial.pcId)
            discardIncompleteRemoteDocument(destination)
            updateRemoteDownload(recordId) { record ->
                if (record.status == RemoteDownloadStatus.COMPLETED) record else record.copy(
                    status = RemoteDownloadStatus.FAILED,
                    updatedAt = Instant.now(),
                    error = remoteFileErrorMessage(error),
                )
            }
        } finally {
            releaseRemoteDocumentPermission(destination)
        }
    }

    private suspend fun discardIncompleteRemoteDocument(destination: Uri) {
        withContext(NonCancellable + Dispatchers.IO) {
            remoteFileDownloader.cleanupIncompleteDocument(destination)
        }
    }

    private suspend fun releaseRemoteDocumentPermission(destination: Uri) {
        withContext(NonCancellable + Dispatchers.IO) {
            remoteFileDownloader.releasePersistedPermission(destination)
        }
    }

    private suspend fun cancelRemoteFileOperationBestEffort(recordId: UUID, pcId: UUID) {
        val operationId = mutableSnapshot.value.remoteDownloads
            .firstOrNull { it.id == recordId }
            ?.operationId
            ?: return
        cancelKnownRemoteFileOperationBestEffort(pcId, operationId)
    }

    private suspend fun cancelKnownRemoteFileOperationBestEffort(pcId: UUID, operationId: UUID) {
        withContext(NonCancellable) {
            try {
                withApi { api ->
                    val cancelled = remoteFileOperationResponseFromJson(
                        api.delete(remoteFileOperationPath(pcId, operationId)).jsonObject,
                    )
                    require(cancelled.id == operationId) {
                        "The server returned a different Windows file operation"
                    }
                }
            } catch (_: Throwable) {
                // Cancellation remains locally authoritative. A later explicit
                // cancel can retry DELETE, while server expiry is the backstop.
            }
        }
    }

    private fun updateRemoteDownload(
        recordId: UUID,
        persist: Boolean = true,
        transform: (RemoteDownloadRecord) -> RemoteDownloadRecord,
    ) {
        updateRemoteDownloads(persist) { records ->
            records.map { record -> if (record.id == recordId) transform(record) else record }
        }
    }

    private fun updateRemoteDownloads(
        persist: Boolean = true,
        transform: (List<RemoteDownloadRecord>) -> List<RemoteDownloadRecord>,
    ) {
        synchronized(remoteDownloadStateLock) {
            mutableSnapshot.update { current ->
                current.copy(
                    remoteDownloads = transform(current.remoteDownloads)
                        .sortedByDescending(RemoteDownloadRecord::createdAt)
                        .take(MAX_REMOTE_DOWNLOAD_RECORDS),
                )
            }
            if (persist) remoteDownloadStore.save(mutableSnapshot.value.remoteDownloads)
        }
    }

    private suspend fun cancelRemoteDownloadsAndJoin(clearMetadata: Boolean) {
        val jobs = remoteDownloadJobs.values.toList()
        jobs.forEach(Job::cancel)
        for (job in jobs) job.join()
        remoteDownloadJobs.clear()
        if (clearMetadata) {
            synchronized(remoteDownloadStateLock) {
                remoteDownloadStore.clear()
                mutableSnapshot.value = mutableSnapshot.value.copy(remoteDownloads = emptyList())
            }
        }
    }

    private suspend fun createRemoteFileOperation(
        pcId: UUID,
        operation: String,
        path: String,
        offset: Long? = null,
        shellType: RemoteShellType? = null,
        onCreated: (RemoteFileOperationResponse) -> Unit = {},
    ): RemoteFileOperationResponse {
        // One stable key belongs to this create attempt. If transport retry is
        // added below this boundary, it must reuse this value with the same body.
        val idempotencyKey = UUID.randomUUID().toString()
        return withApi { api ->
        remoteFileOperationResponseFromJson(
            api.post(
                remoteFileOperationPath(pcId),
                buildJsonObject {
                    put("operation", operation)
                    put("path", path)
                    offset?.let { put("offset", it) }
                    shellType?.let { put("shell_type", it.wireValue) }
                },
                idempotencyKey = idempotencyKey,
            ).jsonObject,
        ).also(onCreated)
        }
    }

    private suspend fun awaitRemoteFileOperation(
        pcId: UUID,
        initial: RemoteFileOperationResponse,
    ): RemoteFileOperationResponse {
        var current = initial
        while (current.status == REMOTE_FILE_STATUS_PENDING ||
            current.status == REMOTE_FILE_STATUS_PROCESSING
        ) {
            delay(REMOTE_FILE_OPERATION_POLL_INTERVAL_MILLIS)
            current = withApi { api ->
                remoteFileOperationResponseFromJson(
                    api.get(remoteFileOperationPath(pcId, current.id)).jsonObject,
                ).also { response ->
                    require(response.id == current.id) {
                        "The server returned a different Windows file operation"
                    }
                }
            }
        }
        return when (current.status) {
            REMOTE_FILE_STATUS_SUCCEEDED -> current
            REMOTE_FILE_STATUS_FAILED -> error(current.error ?: "The Windows file operation failed")
            REMOTE_FILE_STATUS_CANCELLED -> error("The Windows file operation was cancelled")
            REMOTE_FILE_STATUS_EXPIRED -> error("The Windows file operation expired")
            else -> error("The server returned an unknown Windows file-operation state")
        }
    }

    private fun JsonObject.toRemoteDirectoryPage(): RemoteDirectoryPage {
        val entries = getValue("entries").jsonArray.map { value ->
            val item = value.jsonObject
            val kind = RemoteFileKind.fromWireValue(item.requiredString("kind"))
            val size = item["size_bytes"]?.takeUnless { it is JsonNull }?.jsonPrimitive?.long
            if (kind == RemoteFileKind.DIRECTORY) require(size == null) {
                "A Windows directory unexpectedly reported a byte size"
            }
            RemoteFileEntry(
                name = item.requiredString("name").take(255),
                path = item.requiredString("path").also {
                    require(it.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES && '\u0000' !in it) {
                        "The Windows PC returned an invalid path"
                    }
                },
                kind = kind,
                sizeBytes = size?.also { require(it >= 0L) { "A Windows file reported a negative size" } },
                modifiedAt = item["modified_at"]?.jsonPrimitive?.contentOrNull?.let(Instant::parse),
                attributes = item["attributes"]?.jsonArray
                    ?.mapTo(linkedSetOf()) { it.jsonPrimitive.content }
                    .orEmpty(),
            )
        }
        require(entries.size <= REMOTE_FILE_LIST_PAGE_MAX_ENTRIES) {
            "The Windows PC returned too many directory entries"
        }
        val currentPath = requiredString("current_path")
        val parentPath = get("parent_path")?.jsonPrimitive?.contentOrNull
        val nextOffset = get("next_offset")?.takeUnless { it is JsonNull }?.jsonPrimitive?.long
        require(nextOffset == null || nextOffset >= 0L) { "The Windows PC returned an invalid page offset" }
        return RemoteDirectoryPage(currentPath, parentPath, entries, nextOffset)
    }

    private fun remoteFileOperationPath(pcId: UUID, operationId: UUID? = null): String =
        "/v1/pcs/$pcId/file-operations" + (operationId?.let { "/$it" } ?: "")

    private fun remoteFileErrorMessage(error: Throwable): String = when (error) {
        is TermLinkApiException -> activity.getString(R.string.remote_file_error_server, error.status)
        is java.io.IOException -> activity.getString(R.string.remote_file_error_connection)
        else -> activity.getString(R.string.remote_file_error_generic)
    }

    private suspend fun deleteLocalSession(sessionId: UUID) {
        // Fence any realtime acknowledgement already in flight before the
        // Room transaction. UUIDs are never reused, so the fence is permanent
        // for this repository lifetime.
        deletedSessionIds += sessionId
        val historyObjectIds = mutableSnapshot.value.history.asSequence()
            .filter { it.sessionId == sessionId }
            .mapTo(mutableSetOf()) { it.historyObject.id }
        val live = synchronized(liveSessionLock) { liveSessions.remove(sessionId) }
        live?.closeAndJoin()
        historyObjectIds.forEach { objectId ->
            historyEngines.remove(objectId)?.dispose()
            historyDownloads.remove(objectId)
        }
        historyRepository.deleteSession(sessionId)
        mutableSnapshot.update { snapshot -> snapshot.withoutSession(sessionId) }
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
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            publishFailure(error)
            throw error
        }
    }

    private suspend fun refreshWithApi(api: ProductionApiClient): Unit = refreshStateLock.withLock {
        val self = resolveSameAccountDevice(api, ProductResponseParser.self(api.get("/v1/devices/self").jsonObject))
        if (sameAccountDeviceAction(self.trustState) == SameAccountDeviceAction.BLOCK_REVOKED) {
            clearAccessControlledState(self)
            return
        }
        val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
        val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
        val sessions = pcs.flatMap { pc ->
            ProductResponseParser.itemObjects(
                api.get("/v1/pcs/${pc.id}/sessions?include_ended=true").jsonObject,
            ).map { it.toSession() }
        }.filterNot { it.id in deletedSessionIds }
        val entitlement = api.get("/v1/account/entitlements").jsonObject.toEntitlement()
        val catalogPlans = ProductResponseParser.catalogPlanObjects(api.get("/v1/billing/plans").jsonObject)
            .map { it.toCatalogPlan() }
        val catalog = catalogPlans.firstOrNull { it.key == entitlement.planKey }
        // Reject an epoch rollback or same-epoch public-key substitution before
        // any untrusted metadata reaches Room. Refreshes are serialized so a
        // slower response cannot overwrite a newer validated snapshot.
        closeLiveSessionsForChangedPcIdentities(pcs, sessions)
        validateAuthoritativeSessionKeys(devices, pcs, sessions)
        historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
        sessions.forEach { historyRepository.upsertSession(it) }
        val storedIdentity = identity()
        val ownDeviceId = storedIdentity.deviceId
        val directoryTitles = decryptSessionDirectoryTitles(devices, pcs, sessions, storedIdentity)
        val refreshedDownloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val historyEntries = sessions.flatMap { session ->
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@flatMap emptyList()
            val response = try {
                api.get(sessionChunkHistoryRequestPath(session.id)).jsonObject
            } catch (error: TermLinkApiException) {
                if (error.status == 503) return@flatMap emptyList()
                throw error
            }
            boundedChunkHistoryItems(response).mapNotNull { item ->
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
            retainedLiveSessionIds(sessions, trustedPcIds),
        )
        val runtimeSessions = sessions.map { session ->
            liveSessions[session.id]?.let { live ->
                projectLiveSessionSummary(session, live.client.sessionState.value, live.engine.authoritativeSize.value)
            } ?: session
        }
        val retainedSessionIds = sessions.mapTo(mutableSetOf(), SessionSummary::id)
        mutableSnapshot.update { current ->
            current.copy(
                signedIn = true,
                configured = true,
                connectionState = ProductConnectionState.CONNECTED,
                connectionError = null,
                enrollmentState = ProductEnrollmentState.TRUSTED,
                enrollmentConfirmationRequired = false,
                accountLabel = accountDisplayLabel(),
                devices = devices,
                pcs = pcs,
                sessions = runtimeSessions,
                sessionStates = current.sessionStates.filterKeys(retainedSessionIds::contains),
                sessionOpenFailures = current.sessionOpenFailures.filterKeys(retainedSessionIds::contains),
                sessionTitles = reconcileSessionDirectoryTitles(
                    current.sessionTitles,
                    runtimeSessions,
                    directoryTitles,
                    currentLiveSessionTitles(),
                ),
                history = historyEntries,
                replayedHistoryObjectIds = current.replayedHistoryObjectIds.intersect(
                    historyEntries.mapTo(mutableSetOf()) { it.historyObject.id },
                ),
                transcriptViewports = current.transcriptViewports.filterKeys(retainedSessionIds::contains),
                liveAcknowledgements = current.liveAcknowledgements.filterKeys(retainedSessionIds::contains),
                bootstrapSources = current.bootstrapSources.filterKeys(retainedSessionIds::contains),
                catalogPlans = catalogPlans,
                entitlement = entitlement,
                catalogPlan = catalog,
                lastSyncedAt = Instant.now(),
            )
        }
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

    private suspend fun pollSessionCatalogWithApi(api: ProductionApiClient): Unit = refreshStateLock.withLock {
        val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
        val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
        val catalogSessions = pcs.flatMap { pc ->
            ProductResponseParser.itemObjects(
                api.get("/v1/pcs/${pc.id}/sessions?include_ended=true").jsonObject,
            ).map { it.toSession() }
        }.filterNot { it.id in deletedSessionIds }
        closeLiveSessionsForChangedPcIdentities(pcs, catalogSessions)
        validateAuthoritativeSessionKeys(devices, pcs, catalogSessions)
        historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
        catalogSessions.forEach { historyRepository.upsertSession(it) }
        val directoryTitles = decryptSessionDirectoryTitles(devices, pcs, catalogSessions, identity())
        rotateLiveSessionKeys(devices, pcs, catalogSessions)

        val trustedPcIds = pcs.asSequence()
            .filter { pc ->
                devices.any { device ->
                    device.id == pc.deviceId &&
                        device.type == DeviceType.WINDOWS_PC &&
                        device.trustState == DeviceTrustState.TRUSTED
                }
            }
            .mapTo(hashSetOf(), PcSummary::id)
        closeLiveSessionsExcept(
            retainedLiveSessionIds(catalogSessions, trustedPcIds),
        )

        val runtimeSessions = catalogSessions.map { session ->
            liveSessions[session.id]?.let { live ->
                projectLiveSessionSummary(session, live.client.sessionState.value, live.engine.authoritativeSize.value)
            } ?: session
        }
        mutableSnapshot.update { current ->
            val reconciled = reconcileSessionCatalog(
                current = current.copy(devices = devices),
                pcs = pcs,
                sessions = runtimeSessions,
                deletedSessionIds = deletedSessionIds,
            )
            reconciled.copy(
                sessionTitles = reconcileSessionDirectoryTitles(
                    reconciled.sessionTitles,
                    runtimeSessions,
                    directoryTitles,
                    currentLiveSessionTitles(),
                ),
                lastSyncedAt = Instant.now(),
            )
        }
    }

    private suspend fun <T> withApi(
        httpClient: OkHttpClient = http,
        block: suspend (ProductionApiClient) -> T,
    ): T = withContext(Dispatchers.IO) {
        val root = PRODUCT_PUBLIC_SERVER_URL
        val token = freshAccessToken()
        val identity = identity()
        ProductionApiClient(
            root,
            AccessTokenProvider { token },
            identity.deviceId,
            identity.identity,
            httpClient,
            allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
        ).use { api ->
            ensureRegistered(api, identity)
            block(api)
        }
    }

    private fun decryptSessionDirectoryTitles(
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
        sessions: List<SessionSummary>,
        stored: DeviceKeyVault.StoredIdentity,
    ): Map<UUID, String> {
        val pcsById = pcs.associateBy(PcSummary::id)
        val devicesById = devices.associateBy(DeviceSummary::id)
        return buildMap {
            sessions.forEach { session ->
                val sealed = session.encryptedTitle ?: return@forEach
                val pc = pcsById[session.pcId] ?: return@forEach
                val pcDevice = devicesById[pc.deviceId] ?: return@forEach
                if (pcDevice.type != DeviceType.WINDOWS_PC ||
                    pcDevice.trustState != DeviceTrustState.TRUSTED ||
                    pcDevice.exchangePublicKey.size != 32 || pc.keyEpoch < 1
                ) {
                    return@forEach
                }
                val pairwise = try {
                    SessionCrypto.derivePairwiseKey(
                        stored.identity.exchange.privateKey,
                        pcDevice.exchangePublicKey,
                        pc.deviceId,
                        stored.deviceId,
                        pc.keyEpoch,
                    )
                } catch (_: Exception) {
                    return@forEach
                }
                try {
                    put(
                        session.id,
                        SessionTitleCrypto.open(
                            pairwiseKey = pairwise,
                            sealedEnvelope = sealed,
                            expectedSessionId = session.id,
                            expectedPcDeviceId = pc.deviceId,
                            expectedRecipientDeviceId = stored.deviceId,
                            expectedKeyEpoch = pc.keyEpoch,
                        ),
                    )
                } catch (_: Exception) {
                    // The directory remains usable when a legacy, stale-key or
                    // malformed title envelope is encountered. Its title is
                    // omitted instead of displaying unauthenticated metadata.
                } finally {
                    pairwise.fill(0)
                }
            }
        }
    }

    /** Snapshots the exact tri-state title precedence used by the realtime collector. */
    private fun currentLiveSessionTitles(): Map<UUID, String> = liveSessions.mapValues { (_, live) ->
        liveSessionTitleOverride(live.client.sessionTitle.value, live.engine.title.value)
    }.mapNotNull { (sessionId, title) -> title?.let { sessionId to it } }.toMap()

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

    private suspend fun freshAccessToken(force: Boolean = false): String =
        tokenRefresh.freshAccessToken(force)

    private suspend fun requestFreshAccessToken(force: Boolean): String = suspendCancellableCoroutine { continuation ->
        val generation = authenticationLifecycle.capture()
        val state = authState ?: run {
            continuation.resumeWithException(IllegalStateException("Sign in first"))
            return@suspendCancellableCoroutine
        }
        if (closed || !authenticationLifecycle.isCurrent(generation)) {
            continuation.resumeWithException(IllegalStateException("Authentication lifecycle changed"))
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
                if (!force && !state.needsTokenRefresh && !current.isNullOrBlank()) {
                    requireCurrentAuthentication(generation, state)
                    realtimeAccessToken.publish(current)
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
                requireCurrentAuthentication(generation, state)
                val clean = CredentialFreeAuthState.sanitize(updated)
                authState = clean
                sessionStore.saveAuthState(clean)
                realtimeAccessToken.publish(payload.accessToken)
                if (continuation.isActive) continuation.resume(payload.accessToken)
            } catch (error: Throwable) {
                if (continuation.isActive) continuation.resumeWithException(error)
            }
        }
    }

    private fun requireCurrentAuthentication(generation: Long, state: AuthState) {
        check(isCurrentAuthentication(generation, state)) {
            "Authentication lifecycle changed"
        }
    }

    private fun requireCurrentAuthentication(generation: Long) {
        check(!closed && authenticationLifecycle.isCurrent(generation)) {
            "Authentication lifecycle changed"
        }
    }

    private fun isCurrentAuthentication(generation: Long, state: AuthState): Boolean =
        !closed && authenticationLifecycle.isCurrent(generation) && authState === state

    private suspend fun ensureOidcConfiguration(): NativeOidcConfiguration {
        oidcConfiguration?.let { return it }
        val root = PRODUCT_PUBLIC_SERVER_URL
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
        authenticationGeneration: Long,
    ) {
        requireCurrentAuthentication(authenticationGeneration)
        val nextScope = AccountScope.canonical(configuration.issuer, subject)
        establishAccountScope(nextScope) {
            requireCurrentAuthentication(authenticationGeneration)
        }
        requireCurrentAuthentication(authenticationGeneration)
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
        if (sessionStore.accountLogin == null) {
            sessionStore.accountLogin = OidcJwtClaims.displayIdentifier(accessToken)
        }
        establishAccountScope(AccountScope.canonical(configuration.issuer, subject))
        withApi { api ->
            // withApi registers this account-scoped key first. Continue directly
            // into the authoritative refresh; no enrollment confirmation step is
            // part of the same-account Android flow.
            refreshWithApi(api)
        }
    }

    private fun resolveSameAccountDevice(api: ProductionApiClient, self: DeviceSummary): DeviceSummary =
        when (sameAccountDeviceAction(self.trustState)) {
            SameAccountDeviceAction.REFRESH,
            SameAccountDeviceAction.BLOCK_REVOKED -> self
            SameAccountDeviceAction.ACTIVATE_LEGACY_AND_REFRESH -> {
                // Compatibility for older servers that still create account-owned
                // Android keys as pending. This is automatic and never user-facing.
                api.post("/v1/devices/self/trust", buildJsonObject { })
                ProductResponseParser.self(api.get("/v1/devices/self").jsonObject).also { activated ->
                    check(activated.trustState != DeviceTrustState.PENDING) {
                        "The server did not activate this same-account device"
                    }
                }
            }
        }

    private suspend fun establishAccountScope(
        nextScope: String,
        beforeCommit: () -> Unit = {},
    ) {
        beforeCommit()
        if (accountScope != null && accountScope != nextScope) {
            closeLiveSessions()
            closeHistoryEngines()
            clearRoomCache()
            beforeCommit()
            billing.reset()
        }
        beforeCommit()
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
     * A revoked server response closes access-bearing runtime state and erases
     * local encrypted session/history rows. The account-scoped identity remains
     * in Android Keystore so a later sign-in does not manufacture a new device.
     */
    private suspend fun clearAccessControlledState(
        self: DeviceSummary,
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
            enrollmentConfirmationRequired = false,
            accountLabel = accountDisplayLabel(),
            devices = listOf(self),
            pcs = emptyList(),
            sessions = emptyList(),
            sessionStates = emptyMap(),
            sessionOpenFailures = emptyMap(),
            sessionTitles = emptyMap(),
            pairingChallenge = null,
            history = emptyList(),
            replayedHistoryObjectIds = emptySet(),
            transcriptViewports = emptyMap(),
            liveAcknowledgements = emptyMap(),
            bootstrapSources = emptyMap(),
            catalogPlans = emptyList(),
            entitlement = null,
            catalogPlan = null,
            billing = BillingSnapshot(),
            lastSyncedAt = Instant.now(),
        )
    }

    private suspend fun clearRoomCache() {
        val activeTranscriptLoads = transcriptLoads.values.toSet()
        transcriptLoads.clear()
        activeTranscriptLoads.forEach { it.cancelAndJoin() }
        withContext(Dispatchers.IO) {
            clearProductRoomCache(historyRepository)
        }
    }

    private fun publishUpdateStatus(status: ProductUpdateStatus): ProductUpdateStatus {
        mutableSnapshot.value = mutableSnapshot.value.copy(updateStatus = status)
        return status
    }

    private fun publishCheckedUpdateStatus(
        status: ProductUpdateStatus,
        force: Boolean,
    ): ProductUpdateStatus {
        val previous = mutableSnapshot.value.updateStatus
        val available = status.decision == ProductUpdateDecision.OPTIONAL ||
            status.decision == ProductUpdateDecision.REQUIRED
        val sameBuild = status.buildNumber != null && status.buildNumber == previous.buildNumber
        return publishUpdateStatus(
            status.copy(
                dialogVisible = available && (force || !sameBuild || previous.dialogVisible),
                downloadState = if (sameBuild) previous.downloadState else ProductUpdateDownloadState.Idle,
            ),
        )
    }

    private fun publishUpdateDownloadState(
        expectedBuild: Long?,
        state: ProductUpdateDownloadState,
    ) {
        mutableSnapshot.update { snapshot ->
            val update = snapshot.updateStatus
            if (expectedBuild != null && update.buildNumber != expectedBuild) return@update snapshot
            snapshot.copy(
                updateStatus = update.copy(
                    dialogVisible = true,
                    downloadState = state,
                ),
            )
        }
    }

    private fun cachedUpdateStatus(baseUrl: String): ProductUpdateStatus? {
        if (updatePreferences.getString(UPDATE_REQUEST_KEY, null) != updateRequestKey) return null
        val body = updatePreferences.getString(UPDATE_BODY_KEY, null) ?: return null
        return runCatching {
            AppShipUpdateProtocol.parseAndValidate(
                body = body,
                baseUrl = baseUrl,
                expectedSlug = PRODUCT_APPSHIP_SLUG,
                expectedChannel = UPDATE_CHANNEL,
                expectedArchitecture = updateArchitecture,
                currentBuild = BuildConfig.VERSION_CODE.toLong(),
            )
        }.getOrNull()
    }

    private fun saveUpdateCache(body: String, etag: String?) {
        updatePreferences.edit()
            .putString(UPDATE_REQUEST_KEY, updateRequestKey)
            .putString(UPDATE_BODY_KEY, body)
            .putString(UPDATE_ETAG_KEY, etag)
            .putLong(UPDATE_CHECKED_AT_KEY, System.currentTimeMillis())
            .commit()
    }

    private fun readLimitedText(input: java.io.InputStream?): String {
        if (input == null) throw AppShipUpdateException("更新服务返回了空响应")
        val output = java.io.ByteArrayOutputStream()
        val buffer = ByteArray(8 * 1024)
        var total = 0
        input.use { stream ->
            while (true) {
                val count = stream.read(buffer)
                if (count < 0) break
                total += count
                if (total > UPDATE_MAX_RESPONSE_BYTES) {
                    throw AppShipUpdateException("更新服务响应过大")
                }
                output.write(buffer, 0, count)
            }
        }
        return output.toString(Charsets.UTF_8.name())
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
            sessionOpenFailures = mutableSnapshot.value.sessionOpenFailures.filterKeys(retained::contains),
            sessionTitles = mutableSnapshot.value.sessionTitles.filterKeys(retained::contains),
        )
    }

    /**
     * A live client binds its E2EE material to one Windows device UUID. If the
     * PC record is re-enrolled under a replacement device, retire every client
     * created for the old identity before validating or persisting new keys.
     */
    private suspend fun closeLiveSessionsForChangedPcIdentities(
        incomingPcs: List<PcSummary>,
        incomingSessions: List<SessionSummary>,
    ) {
        val storedPcs = historyRepository.pcs()
        val affected = changedPcDeviceIdentitySessionIds(
            storedPcs = storedPcs,
            incomingPcs = incomingPcs,
            sessions = mutableSnapshot.value.sessions + incomingSessions,
        )
        if (affected.isEmpty()) return
        sessionLifecycleLock.withLock {
            val retired = synchronized(liveSessionLock) {
                affected.mapNotNull { sessionId -> liveSessions.remove(sessionId) }
            }
            retired.forEach { it.closeAndJoin() }
            mutableSnapshot.update { snapshot ->
                snapshot.copy(
                    sessionStates = snapshot.sessionStates - affected,
                    sessionOpenFailures = snapshot.sessionOpenFailures - affected,
                    sessionTitles = snapshot.sessionTitles - affected,
                )
            }
        }
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
            closeLiveSessionsForChangedPcIdentities(pcs, sessions)
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
                retainedLiveSessionIds(sessions, trustedPcIds),
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
            validateAuthoritativePcKeyTransition(
                storedPc = storedPc,
                storedDevice = storedDevices[storedPc.deviceId],
                incomingPc = pc,
                incomingDevice = devices.firstOrNull { it.id == pc.deviceId },
            )
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

    private fun observeLiveAcknowledgement(sessionId: UUID, sequence: ULong) {
        mutableSnapshot.update { product ->
            if (sessionId in deletedSessionIds) product else product.copy(
                liveAcknowledgements = product.liveAcknowledgements + (sessionId to sequence),
            )
        }
    }

    private fun clearSessionOpenFailure(sessionId: UUID) {
        mutableSnapshot.update { current ->
            if (sessionId !in current.sessionOpenFailures) current else current.copy(
                sessionOpenFailures = current.sessionOpenFailures - sessionId,
            )
        }
    }

    private fun observeSessionOpenFailure(
        sessionId: UUID,
        failure: SessionOpenFailure,
        reconnect: Boolean,
    ) {
        mutableSnapshot.update { current ->
            val state = current.sessionStates[sessionId]
            if (state is RemoteSessionState.Exited) {
                current.copy(sessionOpenFailures = current.sessionOpenFailures + (sessionId to failure))
            } else {
                current.copy(
                    sessionOpenFailures = current.sessionOpenFailures + (sessionId to failure),
                    sessionStates = if (!reconnect) current.sessionStates else current.sessionStates + (
                        sessionId to RemoteSessionState.Reconnecting(
                            lastAckedSequence = state?.lastAckedSequence
                                ?: current.liveAcknowledgements[sessionId]?.toLong()
                                ?: 0L,
                            reason = SESSION_BOOTSTRAP_FAILED_REASON,
                        )
                    ),
                )
            }
        }
    }

    private fun observeBootstrapFailure(sessionId: UUID, failure: SessionOpenFailure) =
        observeSessionOpenFailure(sessionId, failure, reconnect = true)

    private fun observeBootstrap(sessionId: UUID, restored: RestoredLiveHistory) {
        mutableSnapshot.update { product ->
            val currentGeneration = product.transcriptViewports[sessionId]?.viewportGeneration ?: 0L
            val viewports = restored.transcriptAnchor?.let { anchor ->
                product.transcriptViewports + (
                    sessionId to TranscriptViewportState(
                        sessionId = sessionId,
                        snapshotAnchorSequence = anchor.snapshotSequence,
                        initialBeforeLineId = anchor.beforeLineId,
                        viewportGeneration = Math.addExact(currentGeneration, 1L),
                    )
                )
            } ?: (product.transcriptViewports - sessionId)
            product.copy(
                transcriptViewports = viewports,
                liveAcknowledgements = product.liveAcknowledgements + (sessionId to restored.durableAcknowledgement),
                bootstrapSources = product.bootstrapSources + (sessionId to restored.bootstrapSource),
                sessionOpenFailures = product.sessionOpenFailures - sessionId,
            )
        }
    }

    private suspend fun loadTranscriptPage(key: TranscriptLoadKey) {
        val selected = mutableSnapshot.value.transcriptViewports[key.sessionId]
        if (selected == null || selected.snapshotAnchorSequence != key.snapshotSequence ||
            selected.nextBeforeLineId != key.beforeLineId || selected.viewportGeneration != key.viewportGeneration
            || selected.nextCursor != key.cursor
        ) return
        mutableSnapshot.update { product ->
            val current = product.transcriptViewports[key.sessionId]
            if (!current.matches(key)) product else product.copy(
                transcriptViewports = product.transcriptViewports + (
                    key.sessionId to current!!.copy(loading = true, error = null)
                ),
            )
        }
        try {
            val product = mutableSnapshot.value
            val session = product.sessions.firstOrNull { it.id == key.sessionId }
                ?: error("The transcript session is unavailable")
            val pc = product.pcs.firstOrNull { it.id == session.pcId }
                ?: error("The transcript PC is unavailable")
            val pcDevice = product.devices.firstOrNull {
                it.id == pc.deviceId && it.type == DeviceType.WINDOWS_PC && it.trustState == DeviceTrustState.TRUSTED
            } ?: error("The transcript PC is not a trusted device")
            val stored = identity()
            // Always fetch the authenticated selector before consulting Room.
            // The ciphertext may be reusable, but the server response owns the
            // fixed-revision continuation cursor and publication readiness.
            val selection = awaitExpectedTranscriptPage {
                latestTranscriptPage(key, session, stored.deviceId, pc.keyEpoch)
            }
            if (selection === TranscriptSelection.Pending) {
                error("The expected terminal transcript page is not available yet")
            }
            if (selection === TranscriptSelection.Complete) {
                finishEmptyTranscriptPage(key)
                return
            }
            val ready = selection as TranscriptSelection.ReadyPage<TranscriptDownloadDescriptor>
            validateTranscriptDescriptorNotLoaded(selected, ready.value.metadata)
            val cached = historyRepository.transcriptPageForCursor(
                key.sessionId,
                key.snapshotSequence,
                key.beforeLineId,
            )
            val encrypted = reuseOrLoadTranscriptCiphertext(cached, ready.value.metadata) {
                val sealed = withContext(Dispatchers.IO) {
                    downloadTranscript(ready.value.download, ready.value.metadata)
                }
                try {
                    historyRepository.putTranscriptPage(
                        sessionId = key.sessionId,
                        snapshotSequence = key.snapshotSequence,
                        requestBeforeLineId = key.beforeLineId,
                        firstLineId = ready.value.metadata.firstLineId,
                        lastLineId = ready.value.metadata.lastLineId,
                        objectKey = ready.value.metadata.objectKey,
                        keyId = ready.value.metadata.keyId,
                        ciphertext = sealed,
                        id = ready.value.metadata.id,
                        ciphertextSha256 = ready.value.metadata.ciphertextSha256,
                    )
                    EncryptedTranscriptPage(ready.value.metadata.copyDigest(), sealed.copyOf())
                } finally {
                    sealed.fill(0)
                }
            }
            val page = try {
                openTranscriptPage(session, pc, pcDevice, stored, encrypted)
            } finally {
                encrypted.ciphertext.fill(0)
            }
            mutableSnapshot.update { currentProduct ->
                val current = currentProduct.transcriptViewports[key.sessionId]
                if (!current.matches(key)) currentProduct else currentProduct.copy(
                    transcriptViewports = currentProduct.transcriptViewports + (
                        key.sessionId to mergeTranscriptPage(
                            viewport = requireNotNull(current),
                            requestedBeforeLineId = key.beforeLineId,
                            requestedCursor = key.cursor,
                            objectId = ready.value.metadata.id,
                            outerHasMore = ready.hasMore,
                            outerNextCursor = ready.nextCursor,
                            page = page,
                        )
                    ),
                )
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            if (BuildConfig.DEBUG) {
                Log.e(
                    "TermLinkTailFirst",
                    "Transcript page load failed session=${key.sessionId} snapshot=${key.snapshotSequence} " +
                        "before=${key.beforeLineId}",
                    error,
                )
            }
            mutableSnapshot.update { product ->
                val current = product.transcriptViewports[key.sessionId]
                if (!current.matches(key)) product else product.copy(
                    transcriptViewports = product.transcriptViewports + (
                        key.sessionId to current!!.copy(
                            loading = false,
                            error = "Unable to load older terminal output",
                        )
                    ),
                )
            }
            throw error
        }
    }

    private fun finishEmptyTranscriptPage(key: TranscriptLoadKey) {
        mutableSnapshot.update { product ->
            val current = product.transcriptViewports[key.sessionId]
            if (!current.matches(key)) product else product.copy(
                transcriptViewports = product.transcriptViewports + (
                    key.sessionId to current!!.copy(
                        nextBeforeLineId = null,
                        nextCursor = null,
                        loading = false,
                        error = null,
                        hasMoreBefore = false,
                    )
                ),
            )
        }
    }

    private fun TranscriptViewportState?.matches(key: TranscriptLoadKey): Boolean =
        this != null && sessionId == key.sessionId && snapshotAnchorSequence == key.snapshotSequence &&
            nextBeforeLineId == key.beforeLineId && nextCursor == key.cursor &&
            viewportGeneration == key.viewportGeneration

    private suspend fun latestTranscriptPage(
        key: TranscriptLoadKey,
        session: SessionSummary,
        recipientDeviceId: UUID,
        authoritativeKeyEpoch: Long,
    ): TranscriptSelection<TranscriptDownloadDescriptor> = withApi { api ->
        val response = api.get(
            transcriptHistoryRequestPath(session.id, key.snapshotSequence, key.beforeLineId, key.cursor),
        ).jsonObject
        when (val selected = parseTranscriptSelectorResponse(response, key.snapshotSequence, key.beforeLineId)) {
            TranscriptSelection.Pending -> TranscriptSelection.Pending
            TranscriptSelection.Complete -> TranscriptSelection.Complete
            is TranscriptSelection.ReadyPage -> TranscriptSelection.ReadyPage(
                value = selected.value.toTranscriptDownloadDescriptor(
                    session = session,
                    recipientDeviceId = recipientDeviceId,
                    expectedSnapshotSequence = key.snapshotSequence,
                    requestBeforeLineId = key.beforeLineId,
                    authoritativeKeyEpoch = authoritativeKeyEpoch,
                ),
                hasMore = selected.hasMore,
                nextCursor = selected.nextCursor,
            )
        }
    }

    private suspend fun openTranscriptPage(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        encrypted: EncryptedTranscriptPage,
    ): TerminalTranscriptPageV1 = withContext(Dispatchers.Default) {
        val metadata = encrypted.metadata
        val keyEpoch = requireNotNull(transcriptKeyEpoch(stored.deviceId, metadata.keyId)) {
            "Transcript key ID is not for this device"
        }
        require(keyEpoch <= pc.keyEpoch) { "Transcript key epoch is newer than the authoritative PC epoch" }
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            keyEpoch,
        )
        try {
            val wire = TerminalSnapshotCrypto.openTranscript(
                pairwiseKey = pairwise,
                sealedObject = encrypted.ciphertext,
                expectedSessionId = session.id,
                expectedPcDeviceId = pc.deviceId,
                expectedRecipientDeviceId = stored.deviceId,
                expectedKeyEpoch = keyEpoch,
                expectedKeyId = metadata.keyId,
                expectedAsOfSnapshotSequence = metadata.snapshotSequence,
                expectedFirstLineId = metadata.firstLineId,
                expectedLastLineId = metadata.lastLineId,
            )
            TerminalTranscriptDomainDecoder.decode(wire).also { page ->
                require(page.snapshotAnchorSequence.toULong() == metadata.snapshotSequence) {
                    "Transcript plaintext changed its snapshot anchor"
                }
                require(page.firstLineId.toULong() == metadata.firstLineId &&
                    page.lastLineId.toULong() == metadata.lastLineId
                ) { "Transcript plaintext changed its line range" }
            }
        } finally {
            pairwise.fill(0)
        }
    }

    private fun closeLiveSessions() {
        closeRetiredRealtimeRuntime(detachRealtimeRuntime())
    }

    private fun detachRealtimeRuntime(): RetiredRealtimeRuntime {
        val retired = synchronized(liveSessionLock) {
            RetiredRealtimeRuntime(
                sessions = liveSessions.values.toList().also { liveSessions.clear() },
                multiplexer = realtimeMultiplexer.also { realtimeMultiplexer = null },
            )
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            sessionStates = emptyMap(),
            sessionOpenFailures = emptyMap(),
            sessionTitles = emptyMap(),
        )
        return retired
    }

    private fun closeRetiredRealtimeRuntime(retired: RetiredRealtimeRuntime) {
        retired.sessions.forEach(LiveProductSession::close)
        retired.multiplexer?.close()
    }

    private fun deviceRealtimeMultiplexer(
        stored: DeviceKeyVault.StoredIdentity,
    ): ProductionRealtimeMultiplexer = synchronized(liveSessionLock) {
        realtimeMultiplexer ?: ProductionRealtimeMultiplexer(
            rootUrl = PRODUCT_PUBLIC_SERVER_URL,
            tokenProvider = AccessTokenProvider(realtimeAccessToken::requireCurrent),
            deviceId = stored.deviceId,
            identity = stored.identity,
            parentScope = scope,
            httpClient = http,
            allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
            authenticationRefresher = dev.termlink.network.production.RealtimeAuthenticationRefresher {
                freshAccessToken(force = true)
            },
            authenticationFailureObserver = ::invalidateAuthentication,
        ).also { realtimeMultiplexer = it }
    }

    private fun invalidateAuthentication(error: Throwable) {
        if (closed || !mutableSnapshot.value.signedIn) return
        authenticationLifecycle.invalidate()
        authState = null
        realtimeAccessToken.clear()
        runCatching { sessionStore.clearAuthState() }
        runCatching { sessionStore.accountLogin = null }
        val message = activity.getString(R.string.failure_authentication_expired)
        mutableSnapshot.value = authenticationExpiredSnapshot(mutableSnapshot.value, message)
        appendActivity("Authentication expired", message, Severity.ERROR)
        val retired = detachRealtimeRuntime()
        // This observer can run inside the multiplexer state collector. Yield
        // to the Repository parent scope before closing the captured old
        // multiplexer. The live map and owner slot are already detached, so a
        // later re-login cannot be closed by this cleanup job.
        scope.launch {
            kotlinx.coroutines.yield()
            closeRetiredRealtimeRuntime(retired)
        }
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
        authenticationLifecycle.invalidate()
        realtimeAccessToken.clear()
        if (realtimeNetworkCallbackRegistered) {
            runCatching { connectivityManager.unregisterNetworkCallback(realtimeNetworkCallback) }
            realtimeNetworkCallbackRegistered = false
        }
        closeLiveSessions()
        closeHistoryEngines()
        val closingDownloadJobs = remoteDownloadJobs.values.toList()
        val closingRecoveryJob = remoteDownloadRecoveryJob
        closingDownloadJobs.forEach(Job::cancel)
        // Keep authentication, device proof keys and OkHttp alive until each
        // cancelled download has had a chance to DELETE its server operation.
        scope.launch {
            for (job in closingDownloadJobs) job.join()
            closingRecoveryJob?.join()
            remoteDownloadRecoveryJob = null
            remoteDownloadJobs.clear()
            updateRemoteDownloads { records ->
                records.map { record ->
                    if (record.status.isActive) record.copy(
                        status = RemoteDownloadStatus.CANCELLED,
                        updatedAt = Instant.now(),
                        error = null,
                    ) else record
                }
            }
            billing.close()
            billingStateJob.cancel()
            sessionCatalogPollJob.cancel()
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
            scope.cancel()
        }
    }

    private enum class BrowserAuthFlow { REGISTRATION, ACCOUNT_SETUP }

    private data class ClientConfiguration(val issuer: Uri, val clientId: String, val redirectUri: Uri)

    private data class HistoryDownloadRequest(
        val url: String,
        val method: String,
        val headers: Map<String, List<String>>,
    )

    private data class SnapshotDownloadDescriptor(
        val metadata: HistoryObject,
        val rows: Int,
        val columns: Int,
        val download: HistoryDownloadRequest,
    )

    private data class TerminalSnapshotReadinessObservation(
        val lastOutputSequence: ULong,
        val snapshot: SnapshotDownloadDescriptor?,
        val failure: SessionOpenFailure? = null,
    )

    private data class TranscriptDownloadDescriptor(
        val metadata: TranscriptPageMetadata,
        val download: HistoryDownloadRequest,
    )

    private data class TranscriptLoadKey(
        val sessionId: UUID,
        val snapshotSequence: ULong,
        val beforeLineId: ULong,
        val cursor: String?,
        val viewportGeneration: Long,
    )

    private data class TranscriptAnchor(
        val snapshotSequence: ULong,
        val beforeLineId: ULong,
    )

    private data class PreparedTerminalSnapshot(
        val checkpoint: PreparedTerminalCheckpoint,
        val transcriptAnchor: TranscriptAnchor,
    )

    private class LiveProductSession(
        val engine: TermuxTerminalEngine,
        val client: ProductionRealtimeSessionClient,
    ) : AutoCloseable {
        val controlRequestLock = Mutex()
        var stateJob: Job? = null
        var tokenRefreshJob: Job? = null
        var recoveryJob: Job? = null

        suspend fun closeAndJoin() {
            val caller = currentCoroutineContext()[Job]
            val jobs = listOfNotNull(stateJob, tokenRefreshJob, recoveryJob)
            close()
            jobs.filterNot { it === caller }.forEach { it.join() }
        }

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

    private data class RetiredRealtimeRuntime(
        val sessions: List<LiveProductSession>,
        val multiplexer: ProductionRealtimeMultiplexer?,
    )

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
        online = authoritativePcOnline(this),
        lastSeenAt = optionalInstant("last_seen_at"),
        supportedShells = get("supported_shells")?.jsonArray
            ?.mapNotNull { value -> value.jsonPrimitive.contentOrNull }
            ?.filterTo(linkedSetOf()) { value ->
                RemoteShellType.entries.any { shell -> shell.wireValue == value }
            }
            .orEmpty(),
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
        // Older servers did not return a session-level reachability signal.
        // Treat that response as offline instead of reviving it from PC state
        // or failing the entire catalog refresh during a rolling deployment.
        online = get("online")?.jsonPrimitive?.booleanOrNull ?: false,
        snapshotStatus = get("snapshot_status")?.jsonPrimitive?.contentOrNull?.also { value ->
            require(value in setOf("PENDING", "READY", "UNSUPPORTED", "FAILED")) {
                "Invalid snapshot producer status"
            }
        },
        snapshotReasonCode = get("snapshot_reason_code")?.jsonPrimitive?.contentOrNull?.also { value ->
            require(value.matches(Regex("[A-Z0-9_]{1,64}"))) { "Invalid snapshot producer reason" }
        },
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

    private fun JsonObject.toSnapshotDownloadDescriptor(
        session: SessionSummary,
        recipientDeviceId: UUID,
        authoritativeKeyEpoch: Long,
    ): SnapshotDownloadDescriptor {
        require(requiredString("kind").equals("snapshot", ignoreCase = true)) {
            "History selector returned a non-snapshot object"
        }
        val id = UUID.fromString(requiredString("id"))
        val sequenceValue = getValue("snapshot_seq").jsonPrimitive.long
        require(sequenceValue > 0) { "Snapshot sequence must be positive" }
        val sequence = sequenceValue.toULong()
        require(sequence <= session.lastOutputSequence) {
            "Snapshot sequence is outside the resumable session range"
        }
        val rows = getValue("rows").jsonPrimitive.int
        val columns = getValue("columns").jsonPrimitive.int
        require(rows in 1..HistoryRepository.MAX_TERMINAL_SNAPSHOT_ROWS) { "Snapshot rows are out of bounds" }
        require(columns in 1..HistoryRepository.MAX_TERMINAL_SNAPSHOT_COLUMNS) { "Snapshot columns are out of bounds" }
        val sizeBytes = getValue("size_bytes").jsonPrimitive.long
        require(sizeBytes in 1..HistoryRepository.MAX_TERMINAL_SNAPSHOT_BYTES) { "Snapshot size is out of bounds" }
        val formatName = requiredString("format_name")
        val formatVersion = getValue("format_version").jsonPrimitive.int
        require(formatName == HistoryRepository.TERMINAL_SNAPSHOT_FORMAT && formatVersion == 1) {
            "Snapshot format is unsupported"
        }
        val keyId = requiredString("key_id")
        val keyEpoch = requireNotNull(snapshotKeyEpoch(recipientDeviceId, keyId)) {
            "Snapshot key ID is not for this device"
        }
        require(keyEpoch <= authoritativeKeyEpoch) { "Snapshot key epoch is newer than the authoritative PC epoch" }
        val digest = Base64.getUrlDecoder().decode(requiredString("ciphertext_sha256"))
        require(digest.size == 32) { "Invalid snapshot digest" }
        return SnapshotDownloadDescriptor(
            metadata = HistoryObject(
                id = id,
                kind = HistoryObject.Kind.SNAPSHOT,
                objectKey = "server/${session.id}/snapshot/$id",
                keyId = keyId,
                ciphertextSha256 = digest,
                sizeBytes = sizeBytes,
                startSequence = null,
                endSequence = null,
                snapshotSequence = sequence,
                formatName = formatName,
                formatVersion = formatVersion,
            ),
            rows = rows,
            columns = columns,
            download = getValue("download").jsonObject.toHistoryDownloadRequest(),
        )
    }

    private fun JsonObject.toTranscriptDownloadDescriptor(
        session: SessionSummary,
        recipientDeviceId: UUID,
        expectedSnapshotSequence: ULong,
        requestBeforeLineId: ULong,
        authoritativeKeyEpoch: Long,
    ): TranscriptDownloadDescriptor {
        require(requiredString("kind").equals("transcript", ignoreCase = true)) {
            "History selector returned a non-transcript object"
        }
        val id = UUID.fromString(requiredString("id"))
        val anchorValue = getValue("as_of_snapshot_seq").jsonPrimitive.long
        require(anchorValue > 0 && anchorValue.toULong() == expectedSnapshotSequence) {
            "Transcript snapshot anchor changed"
        }
        require(expectedSnapshotSequence <= session.lastOutputSequence) {
            "Transcript snapshot anchor is newer than the authoritative session"
        }
        val firstValue = getValue("first_line_id").jsonPrimitive.long
        val lastValue = getValue("last_line_id").jsonPrimitive.long
        require(firstValue > 0 && lastValue >= firstValue && lastValue.toULong() < requestBeforeLineId) {
            "Transcript line range is invalid for its exclusive cursor"
        }
        val sizeBytes = getValue("size_bytes").jsonPrimitive.long
        require(sizeBytes in 1..HistoryRepository.MAX_TERMINAL_TRANSCRIPT_BYTES) {
            "Transcript size is out of bounds"
        }
        val formatName = requiredString("format_name")
        val formatVersion = getValue("format_version").jsonPrimitive.int
        require(formatName == HistoryRepository.TERMINAL_TRANSCRIPT_FORMAT && formatVersion == 1) {
            "Transcript format is unsupported"
        }
        val keyId = requiredString("key_id")
        val keyEpoch = requireNotNull(transcriptKeyEpoch(recipientDeviceId, keyId)) {
            "Transcript key ID is not for this device"
        }
        require(keyEpoch <= authoritativeKeyEpoch) {
            "Transcript key epoch is newer than the authoritative PC epoch"
        }
        val digest = Base64.getUrlDecoder().decode(requiredString("ciphertext_sha256"))
        require(digest.size == HistoryRepository.SHA256_BYTES) { "Invalid transcript digest" }
        return TranscriptDownloadDescriptor(
            metadata = TranscriptPageMetadata(
                id = id,
                sessionId = session.id,
                snapshotSequence = expectedSnapshotSequence,
                requestBeforeLineId = requestBeforeLineId,
                firstLineId = firstValue.toULong(),
                lastLineId = lastValue.toULong(),
                objectKey = "server/${session.id}/transcript/$id",
                keyId = keyId,
                ciphertextSha256 = digest,
                sizeBytes = sizeBytes,
                formatName = formatName,
                formatVersion = formatVersion,
            ),
            download = getValue("download").jsonObject.toHistoryDownloadRequest(),
        )
    }

    private fun JsonObject.toHistoryDownloadRequest(): HistoryDownloadRequest {
        val uri = URI(requiredString("url"))
        require(historyDownloadUriAllowed(uri, BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST)) {
            "History downloads must use HTTPS outside an explicit local debug build"
        }
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

    private fun downloadTranscript(
        request: HistoryDownloadRequest,
        metadata: TranscriptPageMetadata,
    ): ByteArray {
        val builder = Request.Builder().url(request.url).get()
        request.headers.forEach { (name, values) ->
            if (!name.equals("host", ignoreCase = true)) values.forEach { builder.addHeader(name, it) }
        }
        http.newCall(builder.build()).execute().use { response ->
            check(response.isSuccessful) { "Transcript download returned HTTP ${response.code}" }
            val declaredLength = response.body?.contentLength() ?: -1L
            require(declaredLength < 0 || declaredLength == metadata.sizeBytes) {
                "Transcript download size mismatch"
            }
            val bytes = response.body?.bytes() ?: error("Transcript download was empty")
            require(bytes.size.toLong() == metadata.sizeBytes) { "Transcript download size mismatch" }
            require(MessageDigest.isEqual(HistoryRepository.sha256(bytes), metadata.ciphertextSha256)) {
                "Transcript download integrity check failed"
            }
            return bytes
        }
    }

    private suspend fun resetLiveRendererAndCursor(
        session: SessionSummary,
        engine: TermuxTerminalEngine,
        expectedRendererGeneration: Long,
    ): RestoredLiveHistory = withContext(NonCancellable) {
        engine.clear()
        engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
        val reset = historyRepository.resetAckCursor(
            session.id,
            LIVE_OUTPUT_ACK_DIRECTION,
            expectedRendererGeneration,
        )
        RestoredLiveHistory(RendererBootstrapState.Empty(reset.rendererGeneration))
    }

    private suspend fun prepareTerminalSnapshot(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TermuxTerminalEngine,
        metadata: HistoryObject,
        rows: Int,
        columns: Int,
        sealedObject: ByteArray,
    ) = withContext(Dispatchers.Default) {
        val sequence = requireNotNull(metadata.snapshotSequence) { "Snapshot sequence is missing" }
        require(metadata.kind == HistoryObject.Kind.SNAPSHOT) { "History object is not a terminal snapshot" }
        if (metadata.formatName != HistoryRepository.TERMINAL_SNAPSHOT_FORMAT || metadata.formatVersion != 1) {
            throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_FORMAT)
        }
        val keyEpoch = snapshotKeyEpoch(stored.deviceId, metadata.keyId)
            ?: throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_RECIPIENT)
        if (keyEpoch > pc.keyEpoch) throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            keyEpoch,
        )
        try {
            val wire = try {
                TerminalSnapshotCrypto.openSnapshot(
                    pairwiseKey = pairwise,
                    sealedObject = sealedObject,
                    expectedSessionId = session.id,
                    expectedPcDeviceId = pc.deviceId,
                    expectedRecipientDeviceId = stored.deviceId,
                    expectedKeyEpoch = keyEpoch,
                    expectedKeyId = metadata.keyId,
                    expectedSnapshotSequence = sequence,
                    expectedColumns = columns,
                    expectedRows = rows,
                )
            } catch (error: Exception) {
                throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_PREPARE)
            }
            val initialBeforeLineId = try {
                TerminalSnapshotCrypto.initialTranscriptBeforeLineId(wire)
            } catch (error: Exception) {
                throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_FORMAT, error)
            }
            val decoded = try {
                TerminalSnapshotDomainDecoder.decode(wire)
            } catch (error: Exception) {
                throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_FORMAT, error)
            }
            val checkpoint = try {
                engine.prepareSnapshot(decoded)
            } catch (error: Exception) {
                throw sessionOpenException(SessionOpenFailureCode.TERMINAL_RENDERER, error)
            }
            PreparedTerminalSnapshot(
                checkpoint = checkpoint,
                transcriptAnchor = TranscriptAnchor(
                    snapshotSequence = decoded.sequence.toULong(),
                    beforeLineId = initialBeforeLineId,
                ),
            )
        } finally {
            pairwise.fill(0)
        }
    }

    private suspend fun restoreBoundTerminalCheckpoint(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TermuxTerminalEngine,
    ): RestoredLiveHistory? {
        val checkpoint = try {
            historyRepository.rendererCheckpointState(session.id, LIVE_OUTPUT_ACK_DIRECTION)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            // A broken binding must block opening rather than silently reset
            // the durable cursor and trigger a replay from sequence zero.
            throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, error)
        } ?: return null
        val currentTail = try {
            latestAuthoritativeSessionTail(session)
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            null
        } ?: return null
        if (!snapshotHasBoundedLiveSuffix(checkpoint.snapshotSequence, currentTail)) return null
        val prepared = prepareTerminalSnapshot(
            session,
            pc,
            pcDevice,
            stored,
            engine,
            checkpoint.snapshot.metadata,
            checkpoint.snapshotRows,
            checkpoint.snapshotColumns,
            checkpoint.snapshot.ciphertext,
        )
        val confirmedTail = latestAuthoritativeSessionTail(session) ?: return null
        if (!snapshotHasBoundedLiveSuffix(checkpoint.snapshotSequence, confirmedTail)) return null
        return withContext(NonCancellable) {
            val bound = if (checkpoint.durableAcknowledgement > checkpoint.snapshotSequence) {
                try {
                    historyRepository.bindRendererCheckpoint(
                        sessionId = session.id,
                        direction = LIVE_OUTPUT_ACK_DIRECTION,
                        expectedRendererGeneration = checkpoint.rendererGeneration,
                        snapshotId = checkpoint.snapshot.metadata.id,
                        snapshotSequence = checkpoint.snapshotSequence,
                        ciphertextSha256 = checkpoint.snapshot.metadata.ciphertextSha256,
                    )
                } catch (error: Exception) {
                    throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, error)
                }
            } else {
                checkpoint
            }
            try {
                val installed = engine.installPrepared(prepared.checkpoint)
                check(installed.sequence.toULong() == bound.snapshotSequence &&
                    installed.size == TerminalSize(bound.snapshotColumns, bound.snapshotRows)
                ) { "Installed renderer checkpoint does not match its durable binding" }
                RestoredLiveHistory(
                    rendererState = RendererBootstrapState.ExactCheckpoint(
                        sequence = installed.sequence,
                        rendererGeneration = bound.rendererGeneration,
                        snapshotId = bound.snapshot.metadata.id,
                        ciphertextSha256 = bound.snapshot.metadata.ciphertextSha256,
                    ),
                    localInputEpoch = installed.projection.observedLocalInputEpoch,
                    transcriptAnchor = prepared.transcriptAnchor,
                    durableAcknowledgement = bound.snapshotSequence,
                    bootstrapSource = ProductBootstrapSource.BOUND_SNAPSHOT,
                )
            } catch (error: Exception) {
                resetLiveRendererAndCursor(session, engine, bound.rendererGeneration)
                throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_INSTALL)
            }
        }
    }

    private suspend fun latestTerminalSnapshot(
        initialSession: SessionSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authoritativeKeyEpoch: Long,
    ): TerminalSnapshotReadinessObservation? {
        return withApi(terminalSnapshotReadinessHttp) { api ->
            // The session list can become visible before its final snapshot is
            // committed. Refresh only this PC's authoritative session metadata
            // on every bounded readiness attempt, including when the first
            // session observation still reported sequence zero.
            val session = ProductResponseParser.itemObjects(
                api.get("/v1/pcs/${initialSession.pcId}/sessions").jsonObject,
            ).map { it.toSession() }
                .singleOrNull { it.id == initialSession.id }
                ?: throw sessionOpenException(SessionOpenFailureCode.SESSION_UNAVAILABLE)
            var observedFailure: SessionOpenFailure? = null
            val descriptor = if (session.lastOutputSequence == 0uL) {
                null
            } else {
                try {
                    val response = api.get(
                        "/v1/sessions/${session.id}/history" +
                            "?kind=snapshot&format_name=${HistoryRepository.TERMINAL_SNAPSHOT_FORMAT}&limit=1",
                    ).jsonObject
                    val items = ProductResponseParser.itemObjects(response)
                    require(items.size <= 1) { "Latest snapshot selector returned more than one object" }
                    val candidate = items.singleOrNull()?.toSnapshotDownloadDescriptor(
                        session,
                        stored.deviceId,
                        authoritativeKeyEpoch,
                    )
                    candidate?.takeIf { value ->
                        // A near-tail Snapshot opens immediately and realtime
                        // fills only its small bounded suffix. A genuinely old
                        // Snapshot is rejected so opening can never degrade to
                        // an unbounded prefix replay.
                        val current = snapshotHasBoundedLiveSuffix(
                            snapshotSequence = requireNotNull(value.metadata.snapshotSequence),
                            currentSessionSequence = session.lastOutputSequence,
                        )
                        if (!current) observedFailure = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_STALE)
                        current
                    }
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    // Preserve the independently verified current tail even if
                    // object metadata is temporarily unavailable. That latch is
                    // what prevents a stale catalog value of zero from causing a
                    // full realtime replay.
                    if (BuildConfig.DEBUG) {
                        val failure = classifySessionOpenException(
                            error,
                            SessionFailureStage.SNAPSHOT_METADATA,
                        ).failure.code.stableCode
                        Log.e("TermLinkTailFirst", "Snapshot metadata readiness failed [$failure]")
                    }
                    observedFailure = classifySessionOpenException(
                        error,
                        SessionFailureStage.SNAPSHOT_METADATA,
                    ).failure
                    null
                }
            }
            if (descriptor == null) {
                observedFailure = when (session.snapshotStatus) {
                    "UNSUPPORTED" -> SessionOpenFailure(
                        SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED,
                        diagnosticCode = session.snapshotReasonCode,
                    )
                    "FAILED" -> SessionOpenFailure(
                        SessionOpenFailureCode.SNAPSHOT_PRODUCTION_FAILED,
                        diagnosticCode = session.snapshotReasonCode,
                    )
                    else -> observedFailure
                }
            }
            if (BuildConfig.DEBUG) {
                Log.d(
                    "TermLinkTailFirst",
                    "Snapshot readiness tail=${session.lastOutputSequence} " +
                        "candidate=${descriptor?.metadata?.snapshotSequence ?: 0uL}",
                )
            }
            TerminalSnapshotReadinessObservation(session.lastOutputSequence, descriptor, observedFailure)
        }
    }

    private suspend fun latestAuthoritativeSessionTail(initialSession: SessionSummary): ULong? =
        withApi(terminalSnapshotReadinessHttp) { api ->
            ProductResponseParser.itemObjects(
                api.get("/v1/pcs/${initialSession.pcId}/sessions").jsonObject,
            ).map { it.toSession() }
                .singleOrNull { it.id == initialSession.id }
                ?.lastOutputSequence
        }

    /**
     * Reads one immutable, recipient-filtered Chunk catalog. Cursor paging is
     * required here because the server orders history newest first while a
     * safe raw replay must prove the complete prefix beginning at sequence 1.
     */
    private suspend fun latestBoundedEventReplayCatalog(
        session: SessionSummary,
        pc: PcSummary,
        stored: DeviceKeyVault.StoredIdentity,
    ): List<HistoryEntry> = withApi(terminalSnapshotReadinessHttp) { api ->
        val entries = mutableListOf<HistoryEntry>()
        val downloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val objectIds = mutableSetOf<UUID>()
        val consumedCursors = mutableSetOf<String>()
        var cursor: String? = null
        var pageCount = 0
        while (true) {
            require(pageCount < TERMINAL_BOUNDED_REPLAY_MAX_CATALOG_PAGES) {
                "Chunk history catalog exceeded the bounded replay page limit"
            }
            val response = api.get(sessionChunkHistoryRequestPath(session.id, cursor)).jsonObject
            val items = boundedChunkHistoryItems(response)
            for (item in items) {
                val keyId = item.requiredString("key_id")
                val historyEpoch = historyKeyEpoch(stored.deviceId, keyId)
                require(historyEpoch != null && historyEpoch <= pc.keyEpoch) {
                    "Chunk history key is not valid for the current recipient epoch"
                }
                val parsed = item.toHistoryObject(session.id)
                require(objectIds.add(parsed.id)) { "Chunk history paging repeated an object" }
                val download = item.getValue("download").jsonObject.toHistoryDownloadRequest()
                downloads[parsed.id] = download
                entries += HistoryEntry(
                    sessionId = session.id,
                    sessionLabel = "${pc.name} | ${session.shellType}",
                    historyObject = parsed,
                    createdAt = item.optionalInstant("created_at"),
                )
            }
            pageCount++
            val hasMore = response.getValue("has_more").jsonPrimitive.booleanOrNull
                ?: error("Chunk history has_more must be a boolean")
            if (!hasMore) break
            val next = response["next_cursor"]?.jsonPrimitive?.contentOrNull
                ?.takeIf(String::isNotBlank)
                ?: error("Chunk history page omitted its continuation cursor")
            require(consumedCursors.add(next)) { "Chunk history cursor loop detected" }
            cursor = next
        }
        historyDownloads.putAll(downloads)
        entries
    }

    /** Returns null only when no durable binding was changed and the caller may use full replay. */
    private suspend fun restoreRemoteTerminalSnapshot(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TermuxTerminalEngine,
        expectedRendererGeneration: Long,
        descriptor: SnapshotDownloadDescriptor,
        onFailure: (SessionOpenFailure) -> Unit,
    ): RestoredLiveHistory? {
        val metadata = descriptor.metadata
        val sequence = requireNotNull(metadata.snapshotSequence)
        val cached = try {
            historyRepository.getHistoryObject(metadata.id)?.takeIf { value ->
                value.metadata.kind == HistoryObject.Kind.SNAPSHOT &&
                    MessageDigest.isEqual(value.metadata.ciphertextSha256, metadata.ciphertextSha256)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            null
        }
        return prepareBindInstallSnapshot(
            loadAndCache = {
                try {
                    val sealedObject = cached?.ciphertext ?: try {
                        withContext(Dispatchers.IO) {
                            downloadHistory(descriptor.download, metadata)
                        }
                    } catch (error: Exception) {
                        throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_DOWNLOAD)
                    }
                    if (cached == null) {
                        try {
                            historyRepository.putSnapshot(
                                sessionId = session.id,
                                snapshotSequence = sequence,
                                rows = descriptor.rows,
                                columns = descriptor.columns,
                                objectKey = metadata.objectKey,
                                keyId = metadata.keyId,
                                formatName = requireNotNull(metadata.formatName),
                                formatVersion = requireNotNull(metadata.formatVersion),
                                ciphertext = sealedObject,
                                id = metadata.id,
                                ciphertextSha256 = metadata.ciphertextSha256,
                            )
                        } catch (error: Exception) {
                            throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, error)
                        }
                    }
                    sealedObject
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    if (BuildConfig.DEBUG) {
                        val failure = classifySessionOpenException(
                            error,
                            SessionFailureStage.SNAPSHOT_DOWNLOAD,
                        ).failure.code.stableCode
                        Log.e("TermLinkTailFirst", "Snapshot download/cache failed [$failure]")
                    }
                    throw error
                }
            },
            prepare = { sealedObject ->
                try {
                    prepareTerminalSnapshot(
                        session,
                        pc,
                        pcDevice,
                        stored,
                        engine,
                        metadata,
                        descriptor.rows,
                        descriptor.columns,
                        sealedObject,
                    )
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    if (BuildConfig.DEBUG) {
                        val failure = classifySessionOpenException(
                            error,
                            SessionFailureStage.SNAPSHOT_PREPARE,
                        ).failure.code.stableCode
                        Log.e(
                            "TermLinkTailFirst",
                            "Snapshot decrypt/prepare failed [$failure] ${safeSnapshotFailureLocation(error)}",
                        )
                    }
                    throw error
                }
            },
            preparedIsCurrent = {
                val currentTail = latestAuthoritativeSessionTail(session)
                val current = currentTail != null && snapshotHasBoundedLiveSuffix(sequence, currentTail)
                if (BuildConfig.DEBUG && !current) {
                    Log.d(
                        "TermLinkTailFirst",
                        "Snapshot drifted before bind snapshot=$sequence tail=${currentTail ?: 0uL}",
                    )
                }
                if (!current) onFailure(SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_STALE))
                current
            },
            bind = {
                try {
                    historyRepository.bindRendererCheckpoint(
                        sessionId = session.id,
                        direction = LIVE_OUTPUT_ACK_DIRECTION,
                        expectedRendererGeneration = expectedRendererGeneration,
                        snapshotId = metadata.id,
                        snapshotSequence = sequence,
                        ciphertextSha256 = metadata.ciphertextSha256,
                    )
                } catch (error: Exception) {
                    throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, error)
                }
            },
            installAndMap = { prepared, bound ->
                val installed = try {
                    engine.installPrepared(prepared.checkpoint).also { value ->
                        check(value.sequence.toULong() == bound.snapshotSequence &&
                            value.size == TerminalSize(bound.snapshotColumns, bound.snapshotRows)
                        ) { "Installed renderer checkpoint does not match the bound snapshot" }
                    }
                } catch (error: Exception) {
                    throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_INSTALL, error)
                }
                RestoredLiveHistory(
                    rendererState = RendererBootstrapState.ExactCheckpoint(
                        sequence = installed.sequence,
                        rendererGeneration = bound.rendererGeneration,
                        snapshotId = metadata.id,
                        ciphertextSha256 = metadata.ciphertextSha256,
                    ),
                    localInputEpoch = installed.projection.observedLocalInputEpoch,
                    transcriptAnchor = prepared.transcriptAnchor,
                    durableAcknowledgement = bound.durableAcknowledgement,
                    bootstrapSource = ProductBootstrapSource.REMOTE_SNAPSHOT,
                )
            },
            resetAfterInstallFailure = { bound ->
                resetLiveRendererAndCursor(session, engine, bound.rendererGeneration)
                throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_INSTALL)
            },
            onLoadFailure = { error ->
                onFailure(classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_DOWNLOAD).failure)
            },
            onPrepareFailure = { error ->
                onFailure(classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_PREPARE).failure)
            },
        )
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
        allowFreshReplayFromZero: Boolean = false,
    ): RestoredLiveHistory {
        val cursor = historyRepository.ackCursorState(session.id, LIVE_OUTPUT_ACK_DIRECTION)
            ?: historyRepository.initializeAckCursor(session.id, LIVE_OUTPUT_ACK_DIRECTION)
        if (allowFreshReplayFromZero && cursor.sequence == 0uL) {
            val currentTail = try {
                latestAuthoritativeSessionTail(session)
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                null
            }
            if (currentTail != null && canFreshSessionReplayFromZero(currentTail, cursor.sequence)) {
                // This ID was allocated by the current Android launch request,
                // so there is no older output outside the Agent replay window.
                // Connecting immediately avoids reporting TL-H113 while a new
                // session's first Snapshot/history publication is still queued.
                return RestoredLiveHistory(RendererBootstrapState.Empty(cursor.rendererGeneration))
            }
        }
        var latestAuthoritativeTail = session.lastOutputSequence
        var authoritativeReadinessObserved = false
        var latestSnapshotFailure: SessionOpenFailure? = null
        val restoredSnapshot = restoreSnapshotWhenEnabled(
            enabled = BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME,
            remoteRetryDelaysMillis = TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS,
            remoteReadinessTimeoutMillis = TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
            restoreBound = {
                restoreBoundTerminalCheckpoint(
                    session,
                    pc,
                    pcDevice,
                    stored,
                    engine,
                )
            },
            latestRemote = {
                try {
                    latestTerminalSnapshot(session, stored, pc.keyEpoch)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    if (BuildConfig.DEBUG) {
                        val failure = classifySessionOpenException(
                            error,
                            SessionFailureStage.SNAPSHOT_METADATA,
                        ).failure.code.stableCode
                        Log.e("TermLinkTailFirst", "Snapshot readiness request failed [$failure]")
                    }
                    throw error
                }
            },
            remoteReady = { observation ->
                snapshotReadinessIsSettled(
                    snapshotAvailable = observation.snapshot != null,
                    failure = observation.failure,
                )
            },
            onRemoteObserved = { observation ->
                authoritativeReadinessObserved = true
                observation.failure?.let { latestSnapshotFailure = it }
                latestAuthoritativeTail = maxOf(
                    latestAuthoritativeTail,
                    observation.lastOutputSequence,
                )
            },
            restoreRemote = { observation ->
                observation.snapshot?.let { descriptor ->
                    restoreRemoteTerminalSnapshot(
                        session,
                        pc,
                        pcDevice,
                        stored,
                        engine,
                        cursor.rendererGeneration,
                        descriptor,
                        onFailure = { latestSnapshotFailure = it },
                    )
                }
            },
            onRemoteFailure = { error ->
                latestSnapshotFailure = classifySessionOpenException(
                    error,
                    SessionFailureStage.SNAPSHOT_METADATA,
                ).failure
            },
        )
        if (restoredSnapshot != null) {
            // The tail can advance while a large object is downloaded,
            // decrypted, and installed. Revalidate after installation but
            // before the realtime transport is registered. The Snapshot may
            // trail by one bounded flow-control window, never by an unbounded
            // historical prefix.
            val currentTail = latestAuthoritativeSessionTail(session)
                ?: throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_METADATA)
            if (!snapshotHasBoundedLiveSuffix(
                    restoredSnapshot.rendererState.sequence.toULong(),
                    currentTail,
                )
            ) throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_STALE)
            return restoredSnapshot
        }
        // A brand-new silent session has no historical terminal state to
        // bootstrap. Subscribing from zero is tail-safe because sequence zero
        // cannot cause an old output prefix to be replayed.
        if (canSafelySubscribeFromZeroWithoutSnapshot(
                currentSessionSequence = latestAuthoritativeTail,
                durableAcknowledgement = cursor.sequence,
                authoritativeReadinessObserved = authoritativeReadinessObserved,
            )
        ) {
            return RestoredLiveHistory(RendererBootstrapState.Empty(cursor.rendererGeneration))
        }
        val target = if (BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME) {
            latestAuthoritativeTail
        } else {
            cursor.sequence
        }
        if (target == 0uL) {
            return RestoredLiveHistory(RendererBootstrapState.Empty(cursor.rendererGeneration))
        }
        suspend fun resetRendererAndCursor(): RestoredLiveHistory {
            return resetLiveRendererAndCursor(session, engine, cursor.rendererGeneration)
        }
        var candidates = mutableSnapshot.value.history
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
        var replayReadiness = boundedEventReplayReadiness(
            candidates.mapNotNull(HistoryEntry::historySequenceRange),
            target,
        )
        if (BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME &&
            replayReadiness != BoundedEventReplayReadiness.READY
        ) {
            candidates = try {
                latestBoundedEventReplayCatalog(session, pc, stored)
                    .sortedWith(
                        compareBy<HistoryEntry> { it.historyObject.startSequence ?: ULong.MAX_VALUE }
                            .thenBy { it.historyObject.endSequence ?: ULong.MAX_VALUE },
                    )
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (candidates.isEmpty()) {
                    throw classifySessionOpenException(error, SessionFailureStage.HISTORY_METADATA)
                }
                candidates
            }
            replayReadiness = boundedEventReplayReadiness(
                candidates.mapNotNull(HistoryEntry::historySequenceRange),
                target,
            )
        }
        if (replayReadiness != BoundedEventReplayReadiness.READY) {
            if (!BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME) return resetRendererAndCursor()
            val failure = when (replayReadiness) {
                BoundedEventReplayReadiness.NOT_YET_COMPLETE -> SessionOpenFailureCode.HISTORY_NOT_READY
                BoundedEventReplayReadiness.GAP -> SessionOpenFailureCode.HISTORY_GAP
                BoundedEventReplayReadiness.LIMIT_EXCEEDED -> SessionOpenFailureCode.HISTORY_REPLAY_LIMIT
                BoundedEventReplayReadiness.READY -> error("Ready replay was rejected")
            }
            throw SessionOpenException(
                SessionOpenFailure(
                    failure,
                    diagnosticCode = latestSnapshotFailure?.diagnosticCode,
                ),
                latestSnapshotFailure?.let(::SessionOpenException),
            )
        }

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
                val persisted = historyRepository.putAckCursor(
                    sessionId = session.id,
                    direction = LIVE_OUTPUT_ACK_DIRECTION,
                    rendererGeneration = cursor.rendererGeneration,
                    sequence = target,
                )
                check(persisted == target) { "Bounded replay ACK was not persisted" }
                RestoredLiveHistory(
                    rendererState = RendererBootstrapState.FullPrefix(restored.toLong(), cursor.rendererGeneration),
                    localInputEpoch = localInputEpoch,
                    durableAcknowledgement = target,
                    bootstrapSource = ProductBootstrapSource.LEGACY_REPLAY,
                )
            } else {
                if (BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME) {
                    resetRendererAndCursor()
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_REPLAY)
                }
                resetRendererAndCursor()
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: SessionOpenException) {
            throw error
        } catch (error: Exception) {
            val reset = resetRendererAndCursor()
            if (BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME) {
                throw classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY)
            }
            reset
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
                    DecodedHistoryEvent(productSessionUpsertEvent(event.seq, value))
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

    private fun snapshotKeyEpoch(recipientDeviceId: UUID, keyId: String): Long? {
        val prefix = "snapshot-v1:${recipientDeviceId.toString().replace("-", "")}:"
        if (!keyId.startsWith(prefix)) return null
        val encoded = keyId.removePrefix(prefix)
        if (encoded.length != 8 || encoded.any { !it.isDigit() }) return null
        val epoch = encoded.toLongOrNull()?.takeIf { it > 0 } ?: return null
        return epoch.takeIf { TerminalSnapshotCrypto.snapshotKeyId(recipientDeviceId, it) == keyId }
    }

    private fun transcriptKeyEpoch(recipientDeviceId: UUID, keyId: String): Long? {
        val prefix = "transcript-v1:${recipientDeviceId.toString().replace("-", "")}:"
        if (!keyId.startsWith(prefix)) return null
        val encoded = keyId.removePrefix(prefix)
        if (encoded.length != 8 || encoded.any { !it.isDigit() }) return null
        val epoch = encoded.toLongOrNull()?.takeIf { it > 0 } ?: return null
        return epoch.takeIf { TerminalSnapshotCrypto.transcriptKeyId(recipientDeviceId, it) == keyId }
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
        val rendererState: RendererBootstrapState,
        val localInputEpoch: Long = 0,
        val transcriptAnchor: TranscriptAnchor? = null,
        val durableAcknowledgement: ULong = 0u,
        val bootstrapSource: ProductBootstrapSource = ProductBootstrapSource.EMPTY,
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
        const val UPDATE_PREFERENCES = "update_service"
        const val UPDATE_CHANNEL = "stable"
        const val UPDATE_REQUEST_KEY = "cache_request"
        const val UPDATE_BODY_KEY = "cache_body"
        const val UPDATE_ETAG_KEY = "cache_etag"
        const val UPDATE_CHECKED_AT_KEY = "cache_checked_at"
        const val UPDATE_CACHE_INTERVAL_MILLIS = 60_000L
        const val UPDATE_MAX_RESPONSE_BYTES = 1_048_576
        const val LIVE_TOKEN_REFRESH_INTERVAL_MILLIS = 15_000L
        const val LIVE_TOKEN_REFRESH_RETRY_MILLIS = 45_000L
        const val SESSION_CATALOG_POLL_INTERVAL_MILLIS = 2_000L
        const val LIVE_OUTPUT_ACK_DIRECTION = "PC_TO_ANDROID"
        val HISTORY_RECOVERY_DELAYS_MILLIS = longArrayOf(500, 1_000, 2_000, 4_000, 8_000, 15_000, 30_000)
        val sensitiveIdentifierPattern = Regex("(?i)\\b[0-9a-f]{8}-[0-9a-f-]{27,}\\b")
    }
}

/**
 * Returns only implementation metadata that is safe for local Debug diagnostics. Exception
 * messages are deliberately excluded because they can contain transport or terminal data.
 */
private fun safeSnapshotFailureLocation(error: Throwable): String {
    val chain = mutableListOf<Throwable>()
    var current: Throwable? = error
    repeat(8) {
        val cause = current ?: return@repeat
        chain += cause
        current = cause.cause
    }
    for (cause in chain.asReversed()) {
        val frame = cause.stackTrace.firstOrNull { value ->
            value.className.startsWith("com.termux.") ||
                value.className.startsWith("dev.termlink.terminal.")
        } ?: cause.stackTrace.firstOrNull { value ->
            value.className.startsWith("dev.termlink.") &&
                value.className != "dev.termlink.product.SessionOpenFailureKt"
        } ?: continue
        val type = cause::class.java.simpleName.take(80).ifEmpty { "unknown" }
        val className = frame.className.take(160)
        val methodName = frame.methodName.take(80)
        return "type=$type frame=$className.$methodName:${frame.lineNumber}"
    }
    return "type=unknown frame=unavailable"
}

internal suspend fun clearProductRoomCache(historyRepository: HistoryRepository) {
    historyRepository.clearAll()
}

internal const val SESSION_CHUNK_HISTORY_PAGE_LIMIT = 50

internal fun sessionChunkHistoryRequestPath(sessionId: UUID, cursor: String? = null): String =
    if (cursor == null) {
        "/v1/sessions/$sessionId/history?kind=chunk&limit=$SESSION_CHUNK_HISTORY_PAGE_LIMIT"
    } else {
        require(cursor.isNotBlank()) { "Chunk history cursor must not be blank" }
        val encoded = URLEncoder.encode(cursor, StandardCharsets.UTF_8.name())
        "/v1/sessions/$sessionId/history?cursor=$encoded&limit=$SESSION_CHUNK_HISTORY_PAGE_LIMIT"
    }

internal fun boundedChunkHistoryItems(response: JsonObject): List<JsonObject> =
    ProductResponseParser.itemObjects(response).also { items ->
        require(items.size <= SESSION_CHUNK_HISTORY_PAGE_LIMIT) {
            "Chunk history page exceeded the requested limit"
        }
        require(items.all { item ->
            item["kind"]?.jsonPrimitive?.contentOrNull.equals("chunk", ignoreCase = true)
        }) { "Chunk history selector returned a non-chunk object" }
    }

internal data class HistorySequenceRange(val start: ULong, val endInclusive: ULong)

internal enum class BoundedEventReplayReadiness {
    READY,
    NOT_YET_COMPLETE,
    GAP,
    LIMIT_EXCEEDED,
}

private fun HistoryEntry.historySequenceRange(): HistorySequenceRange? {
    val start = historyObject.startSequence ?: return null
    val end = historyObject.endSequence ?: return null
    return HistorySequenceRange(start, end)
}

/**
 * Proves that raw replay can start at 1 and reach [throughSequence] without a
 * missing or overlapping event. The event ceiling matches the Windows
 * realtime flow window and prevents a Snapshot failure from turning into an
 * unbounded cold-start replay.
 */
internal fun boundedEventReplayReadiness(
    ranges: List<HistorySequenceRange>,
    throughSequence: ULong,
    maximumEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
): BoundedEventReplayReadiness {
    require(throughSequence > 0uL) { "Bounded replay target must be positive" }
    require(maximumEvents > 0) { "Bounded replay event limit must be positive" }
    if (throughSequence > maximumEvents.toULong()) {
        return BoundedEventReplayReadiness.LIMIT_EXCEEDED
    }
    if (ranges.isEmpty()) return BoundedEventReplayReadiness.NOT_YET_COMPLETE
    var expected = 1uL
    var coveredTarget = false
    for (range in ranges.sortedWith(compareBy(HistorySequenceRange::start).thenBy(HistorySequenceRange::endInclusive))) {
        // Events after the authoritative Output tail are not part of this
        // bootstrap. Once the target is covered, a later range beginning at or
        // before the target can only be a duplicate/overlap and must fail
        // closed; a range beginning after it is irrelevant to this replay.
        if (coveredTarget) {
            if (range.start <= throughSequence) return BoundedEventReplayReadiness.GAP
            break
        }
        if (range.start == 0uL || range.endInclusive < range.start || range.start != expected) {
            return BoundedEventReplayReadiness.GAP
        }
        if (range.endInclusive >= throughSequence) {
            coveredTarget = true
        } else {
            expected = range.endInclusive + 1uL
        }
    }
    return if (coveredTarget) {
        BoundedEventReplayReadiness.READY
    } else {
        BoundedEventReplayReadiness.NOT_YET_COMPLETE
    }
}

internal const val TERMINAL_BOUNDED_REPLAY_MAX_EVENTS = 4096
internal const val TERMINAL_BOUNDED_REPLAY_MAX_CATALOG_PAGES =
    TERMINAL_BOUNDED_REPLAY_MAX_EVENTS / SESSION_CHUNK_HISTORY_PAGE_LIMIT + 2

internal fun transcriptHistoryRequestPath(
    sessionId: UUID,
    snapshotSequence: ULong,
    beforeLineId: ULong,
    cursor: String? = null,
): String = if (cursor == null) {
    "/v1/sessions/$sessionId/history" +
        "?kind=transcript&as_of_snapshot_seq=$snapshotSequence" +
        "&before_line_id=$beforeLineId&limit=1"
} else {
    require(cursor.isNotBlank()) { "Transcript cursor must not be blank" }
    val encoded = URLEncoder.encode(cursor, StandardCharsets.UTF_8.name())
    "/v1/sessions/$sessionId/history?cursor=$encoded&limit=1"
}

/** Tri-state transcript publication result. No ciphertext is touched while [Pending]. */
internal sealed interface TranscriptSelection<out Value> {
    data object Pending : TranscriptSelection<Nothing>
    data object Complete : TranscriptSelection<Nothing>
    data class ReadyPage<Value>(
        val value: Value,
        val hasMore: Boolean,
        val nextCursor: String?,
    ) : TranscriptSelection<Value>
}

/**
 * Parses the outer authenticated paging contract before any object download.
 * `ready` is optional only for rolling compatibility with the previous server.
 */
internal fun parseTranscriptSelectorResponse(
    response: JsonObject,
    expectedSnapshotSequence: ULong,
    requestBeforeLineId: ULong,
): TranscriptSelection<JsonObject> {
    val anchorPrimitive = response.getValue("as_of_snapshot_seq").jsonPrimitive
    require(!anchorPrimitive.isString) { "Transcript selector anchor must be an integer" }
    val anchorValue = anchorPrimitive.long
    require(anchorValue > 0 && anchorValue.toULong() == expectedSnapshotSequence) {
        "Transcript selector changed its snapshot anchor"
    }
    val items = ProductResponseParser.itemObjects(response)
    require(items.size <= 1) { "Transcript selector returned more than one object" }
    val hasMorePrimitive = response.getValue("has_more").jsonPrimitive
    require(!hasMorePrimitive.isString) { "Transcript selector has_more must be a boolean" }
    val hasMore = hasMorePrimitive.booleanOrNull
        ?: error("Transcript selector has_more must be a boolean")
    val nextCursor = when (val element = response["next_cursor"]) {
        null, JsonNull -> null
        else -> element.jsonPrimitive.also {
            require(it.isString) { "Transcript selector cursor must be a string or null" }
        }.contentOrNull?.also {
            require(it.isNotBlank()) { "Transcript selector cursor must not be blank" }
        } ?: error("Transcript selector cursor must be a string or null")
    }
    require(hasMore == (nextCursor != null)) {
        "Transcript selector continuation contract is inconsistent"
    }

    val explicitReady = response["ready"]?.let { element ->
        val primitive = element.jsonPrimitive
        require(!primitive.isString) { "Transcript selector ready must be a boolean" }
        requireNotNull(primitive.booleanOrNull) { "Transcript selector ready must be a boolean" }
    }
    val ready = explicitReady ?: when {
        items.isNotEmpty() -> true
        requestBeforeLineId > 1uL -> false
        else -> true
    }
    if (!ready) {
        require(items.isEmpty() && !hasMore) {
            "Pending transcript publication must not expose a page or cursor"
        }
        return TranscriptSelection.Pending
    }
    if (items.isEmpty()) {
        require(!hasMore) { "Completed empty transcript publication cannot have another page" }
        return TranscriptSelection.Complete
    }
    return TranscriptSelection.ReadyPage(items.single(), hasMore, nextCursor)
}

internal fun mergeTranscriptPage(
    viewport: TranscriptViewportState,
    requestedBeforeLineId: ULong,
    requestedCursor: String?,
    objectId: UUID,
    outerHasMore: Boolean,
    outerNextCursor: String?,
    page: TerminalTranscriptPageV1,
): TranscriptViewportState {
    require(viewport.nextBeforeLineId == requestedBeforeLineId) { "transcript request cursor is stale" }
    require(viewport.nextCursor == requestedCursor) { "transcript opaque request cursor is stale" }
    require(requestedCursor == null || requestedCursor !in viewport.consumedCursors) {
        "transcript request cursor was already consumed"
    }
    require(objectId !in viewport.loadedObjectIds) { "transcript object was already loaded" }
    require(page.snapshotAnchorSequence >= 0 && page.snapshotAnchorSequence.toULong() == viewport.snapshotAnchorSequence) {
        "transcript page changed the fixed snapshot anchor"
    }
    require(page.firstLineId > 0 && page.lastLineId >= page.firstLineId &&
        page.lastLineId.toULong() < requestedBeforeLineId
    ) { "transcript page is outside its exclusive cursor" }
    val overlaps = viewport.pages.any { existing ->
        page.firstLineId <= existing.lastLineId && existing.firstLineId <= page.lastLineId
    }
    require(!overlaps) { "transcript page overlaps the existing viewport" }
    require(outerHasMore == (outerNextCursor != null)) {
        "transcript outer continuation contract is inconsistent"
    }
    require(page.hasMoreBefore == outerHasMore) {
        "transcript ciphertext disagrees with the authenticated continuation"
    }
    val merged = (viewport.pages + page).sortedBy(TerminalTranscriptPageV1::firstLineId)
    require(merged.zipWithNext().all { (older, newer) -> older.lastLineId < newer.firstLineId }) {
        "transcript page merge is not strictly ordered"
    }
    val next = if (outerHasMore) {
        page.nextBeforeLineId.also {
            require(it > 0 && it == page.firstLineId && it.toULong() < requestedBeforeLineId) {
                "transcript page did not advance its cursor"
            }
        }.toULong()
    } else {
        require(page.nextBeforeLineId == 0L) {
            "completed transcript ciphertext retained a line cursor"
        }
        null
    }
    require(outerNextCursor == null || outerNextCursor != requestedCursor) {
        "transcript opaque cursor did not advance"
    }
    require(outerNextCursor == null || outerNextCursor !in viewport.consumedCursors) {
        "transcript opaque cursor loop was detected"
    }
    val retainedPages = retainOldestTranscriptPages(merged)
    return viewport.copy(
        nextBeforeLineId = next,
        nextCursor = outerNextCursor,
        pages = retainedPages,
        loadedObjectIds = boundedTranscriptIdentitySet(viewport.loadedObjectIds, objectId),
        consumedCursors = requestedCursor?.let {
            boundedTranscriptIdentitySet(viewport.consumedCursors, it)
        } ?: viewport.consumedCursors,
        loading = false,
        error = null,
        hasMoreBefore = next != null,
    )
}

private fun <T> boundedTranscriptIdentitySet(existing: Set<T>, value: T): Set<T> {
    val bounded = LinkedHashSet<T>(minOf(existing.size + 1, MAX_TRANSCRIPT_VIEWPORT_IDENTITIES))
    existing.forEach(bounded::add)
    bounded.add(value)
    while (bounded.size > MAX_TRANSCRIPT_VIEWPORT_IDENTITIES) {
        val iterator = bounded.iterator()
        iterator.next()
        iterator.remove()
    }
    return bounded
}

internal suspend fun <Key> awaitCoalescedTranscriptLoad(
    scope: CoroutineScope,
    active: ConcurrentMap<Key, Deferred<Unit>>,
    key: Key,
    load: suspend () -> Unit,
) {
    val candidate = scope.async(start = CoroutineStart.LAZY) { load() }
    candidate.invokeOnCompletion { active.remove(key, candidate) }
    val existing = active.putIfAbsent(key, candidate)
    val shared = existing ?: candidate.also { it.start() }
    if (existing != null) candidate.cancel()
    shared.await()
}

/**
 * A committed Snapshot is intentionally published before its bulk Transcript.
 * Retry only an explicit server/compatibility [TranscriptSelection.Pending].
 * A ready empty response is authoritative completion even when its line cursor
 * is greater than one.
 */
internal suspend fun <Page> awaitExpectedTranscriptPage(
    retryDelaysMillis: List<Long> = TRANSCRIPT_PUBLICATION_RETRY_DELAYS_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
    fetch: suspend () -> TranscriptSelection<Page>,
): TranscriptSelection<Page> {
    require(retryDelaysMillis.all { it >= 0L }) { "Transcript retry delays must not be negative" }
    val attempts = retryDelaysMillis.size + 1
    repeat(attempts) { attempt ->
        val selected = fetch()
        if (selected !== TranscriptSelection.Pending) return selected
        if (attempt < attempts - 1) pause(retryDelaysMillis[attempt])
    }
    return TranscriptSelection.Pending
}

internal fun validateTranscriptDescriptorNotLoaded(
    viewport: TranscriptViewportState,
    metadata: TranscriptPageMetadata,
) {
    require(metadata.id !in viewport.loadedObjectIds) { "Transcript selector replayed an object" }
    require(viewport.pages.none { page ->
        metadata.firstLineId.toLong() <= page.lastLineId && page.firstLineId <= metadata.lastLineId.toLong()
    }) { "Transcript selector replayed or overlapped a line range" }
}

internal suspend fun reuseOrLoadTranscriptCiphertext(
    cached: EncryptedTranscriptPage?,
    expected: TranscriptPageMetadata,
    load: suspend () -> EncryptedTranscriptPage,
): EncryptedTranscriptPage {
    val selected = cached ?: load()
    require(sameTranscriptMetadata(selected.metadata, expected)) {
        "Cached transcript ciphertext does not match the authenticated selector"
    }
    return selected
}

private fun sameTranscriptMetadata(actual: TranscriptPageMetadata, expected: TranscriptPageMetadata): Boolean =
    actual.id == expected.id && actual.sessionId == expected.sessionId &&
        actual.snapshotSequence == expected.snapshotSequence &&
        actual.requestBeforeLineId == expected.requestBeforeLineId &&
        actual.firstLineId == expected.firstLineId && actual.lastLineId == expected.lastLineId &&
        actual.objectKey == expected.objectKey && actual.keyId == expected.keyId &&
        MessageDigest.isEqual(actual.ciphertextSha256, expected.ciphertextSha256) &&
        actual.sizeBytes == expected.sizeBytes && actual.formatName == expected.formatName &&
        actual.formatVersion == expected.formatVersion

/**
 * Makes the irreversible ordering explicit and independently testable: no
 * local resource is touched unless the server has accepted the deletion.
 * Once accepted, cancellation cannot strand the live client or Room rows.
 */
internal suspend fun deleteSessionAfterRemoteSuccess(
    deleteRemote: suspend () -> Unit,
    deleteLocal: suspend () -> Unit,
) {
    deleteRemote()
    withContext(NonCancellable) { deleteLocal() }
}

/**
 * Waits for the Windows Agent's authoritative EXITED publication before
 * permanently deleting a user-closed session.  Only SESSION_ACTIVE is
 * retryable; every other failure preserves local state and is surfaced.
 */
internal suspend fun deleteClosedSessionAfterTermination(
    retryDelaysMillis: List<Long> = SESSION_TERMINATION_DELETE_RETRY_DELAYS_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
    deleteRemote: suspend () -> Unit,
    deleteLocal: suspend () -> Unit,
) {
    require(retryDelaysMillis.all { it >= 0L }) {
        "Session termination delete retry delays must not be negative"
    }
    for (attempt in 0..retryDelaysMillis.size) {
        try {
            deleteRemote()
            withContext(NonCancellable) { deleteLocal() }
            return
        } catch (error: CancellationException) {
            throw error
        } catch (error: TermLinkApiException) {
            val retryable = error.status == 409 && error.code == "SESSION_ACTIVE" &&
                attempt < retryDelaysMillis.size
            if (!retryable) throw error
        }
        pause(retryDelaysMillis[attempt])
    }
}

internal val SESSION_TERMINATION_DELETE_RETRY_DELAYS_MILLIS: List<Long> =
    listOf(250L, 250L, 500L, 500L, 1_000L, 1_000L, 1_000L, 2_000L, 2_000L, 2_000L, 2_000L)

internal fun historyDownloadUriAllowed(uri: URI, allowCleartextForLocalTest: Boolean): Boolean {
    val scheme = uri.scheme?.lowercase()
    return scheme == "https" || allowCleartextForLocalTest && scheme == "http"
}

/** Runs snapshot bootstrap only when the complete product path is explicitly enabled. */
internal suspend fun <Remote, Restored> restoreSnapshotWhenEnabled(
    enabled: Boolean,
    restoreBound: suspend () -> Restored?,
    latestRemote: suspend () -> Remote?,
    restoreRemote: suspend (Remote) -> Restored?,
    remoteRetryDelaysMillis: List<Long> = emptyList(),
    remoteReadinessTimeoutMillis: Long? = null,
    pause: suspend (Long) -> Unit = { delay(it) },
    remoteReady: (Remote) -> Boolean = { true },
    onRemoteObserved: (Remote) -> Unit = {},
    onRemoteFailure: (Exception) -> Unit = {},
): Restored? {
    if (!enabled) return null
    restoreBound()?.let { return it }
    require(remoteRetryDelaysMillis.all { it >= 0L }) { "Snapshot readiness delays must not be negative" }
    require(remoteReadinessTimeoutMillis == null || remoteReadinessTimeoutMillis > 0L) {
        "Snapshot readiness timeout must be positive"
    }
    suspend fun awaitRemote(): Remote? {
        for (attempt in 0..remoteRetryDelaysMillis.size) {
            val remote = try {
                latestRemote()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                onRemoteFailure(error)
                null
            }
            if (remote != null) {
                onRemoteObserved(remote)
                if (remoteReady(remote)) return remote
            }
            if (attempt < remoteRetryDelaysMillis.size) pause(remoteRetryDelaysMillis[attempt])
        }
        return null
    }
    val remote = if (remoteReadinessTimeoutMillis == null) {
        awaitRemote()
    } else {
        withTimeoutOrNull(remoteReadinessTimeoutMillis) { awaitRemote() }
    } ?: return null
    return restoreRemote(remote)
}

internal fun snapshotHasBoundedLiveSuffix(
    snapshotSequence: ULong,
    currentSessionSequence: ULong,
): Boolean = snapshotSequence > 0uL &&
    snapshotSequence <= currentSessionSequence &&
    currentSessionSequence - snapshotSequence <= TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS.toULong()

/** Sequence zero is safe only when neither endpoint has historical output. */
internal fun canSafelySubscribeFromZeroWithoutSnapshot(
    currentSessionSequence: ULong,
    durableAcknowledgement: ULong,
    authoritativeReadinessObserved: Boolean,
): Boolean = authoritativeReadinessObserved &&
    currentSessionSequence == 0uL && durableAcknowledgement == 0uL

/** A locally requested new shell can replay its bounded startup suffix live. */
internal fun canFreshSessionReplayFromZero(
    currentSessionSequence: ULong,
    durableAcknowledgement: ULong,
): Boolean = durableAcknowledgement == 0uL &&
    currentSessionSequence <= TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS.toULong()

/** A permanent producer rejection cannot become ready by polling the same prefix. */
internal fun snapshotReadinessIsSettled(
    snapshotAvailable: Boolean,
    failure: SessionOpenFailure?,
): Boolean = snapshotAvailable || failure?.code == SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED

internal const val SESSION_BOOTSTRAP_IN_PROGRESS_REASON = "snapshot bootstrap in progress"
internal const val SESSION_BOOTSTRAP_FAILED_REASON = "snapshot bootstrap failed"

internal fun shouldReplaceLiveSessionForOpen(
    liveSessionExists: Boolean,
    remoteState: RemoteSessionState?,
): Boolean = liveSessionExists &&
    remoteState is RemoteSessionState.Reconnecting &&
    remoteState.reason == SESSION_BOOTSTRAP_FAILED_REASON

/**
 * Bounded to 30 seconds in total. A remotely launched Windows shell can spend
 * up to 30 seconds registering with the Agent before its first Snapshot enters
 * the Agent -> object store -> history-index pipeline. Keeping a second bounded
 * window here prevents a new session from being classified as TL-H113 while
 * that normal startup pipeline is still settling.
 * The retry schedule extends beyond the absolute deadline so a fast empty
 * response cannot exhaust the attempts before the readiness window closes.
 */
internal val TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS =
    listOf(
        100L, 200L, 400L, 800L, 1_500L,
        2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L,
        2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L,
    )
internal const val TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS = 30_000L
internal const val TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS = 1_000L
internal val TRANSCRIPT_PUBLICATION_RETRY_DELAYS_MILLIS = listOf(100L, 200L, 400L, 800L, 1_000L, 1_500L)
// Matches one Windows realtime flow-control window. This lets an active
// terminal advance during Snapshot publication without permitting a large
// historical replay to delay or disconnect Android first-open.
internal const val TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS = 128

/**
 * Separates the reversible snapshot work from the durable ACK/checkpoint bind.
 * Once [bind] succeeds, failures must reset that exact generation or propagate.
 */
internal suspend fun <Loaded, Prepared, Bound, Restored> prepareBindInstallSnapshot(
    loadAndCache: suspend () -> Loaded,
    prepare: suspend (Loaded) -> Prepared,
    preparedIsCurrent: suspend (Prepared) -> Boolean = { true },
    bind: suspend () -> Bound,
    installAndMap: suspend (Prepared, Bound) -> Restored,
    resetAfterInstallFailure: suspend (Bound) -> Restored,
    onLoadFailure: (Exception) -> Unit = {},
    onPrepareFailure: (Exception) -> Unit = {},
): Restored? {
    val loaded = try {
        loadAndCache()
    } catch (error: CancellationException) {
        throw error
    } catch (error: Exception) {
        onLoadFailure(error)
        return null
    }
    val prepared = try {
        prepare(loaded)
    } catch (error: CancellationException) {
        throw error
    } catch (error: Exception) {
        onPrepareFailure(error)
        return null
    }
    if (!preparedIsCurrent(prepared)) return null
    return withContext(NonCancellable) {
        val bound = bind()
        try {
            installAndMap(prepared, bound)
        } catch (_: Exception) {
            resetAfterInstallFailure(bound)
        }
    }
}

internal val CONTROL_LEASE_HELD_RETRY_DELAYS_MILLIS = listOf(1_000L, 2_000L, 4_000L, 8_000L, 15_000L, 5_000L)

internal fun commandInputBytes(text: String): ByteArray {
    require(text.isNotEmpty()) { "Command text is empty" }
    return (text + '\r').encodeToByteArray()
}

internal fun commandSubmissionFailure(submission: InputSubmission): String? = when (submission) {
    is InputSubmission.Sent -> null
    is InputSubmission.RejectedByRemote -> "The Windows Agent rejected the command: ${submission.reasonCode}"
    is InputSubmission.DeliveryUnknown -> "Command delivery could not be confirmed"
    InputSubmission.RejectedNoLease -> "Request control before sending a command"
    InputSubmission.RejectedExited -> "The remote session has exited"
    InputSubmission.RejectedEmpty -> "Command text is empty"
}

/**
 * A force-stopped client cannot release its 30-second server lease. Retry only
 * that explicit conflict, and make a final attempt after the full TTL. The
 * caller owns cancellation so leaving the session page stops the schedule.
 */
internal suspend fun acquireControlLeaseWithRetry(
    remoteState: () -> RemoteSessionState,
    request: suspend () -> Unit,
    retryDelaysMillis: List<Long> = CONTROL_LEASE_HELD_RETRY_DELAYS_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
): Boolean {
    if (remoteState() is RemoteSessionState.Controlling) return false

    var retryIndex = 0
    while (true) {
        try {
            request()
            return true
        } catch (error: TermLinkApiException) {
            val retryable = error.status == 409 && error.code == "LEASE_HELD"
            if (!retryable || retryIndex >= retryDelaysMillis.size) throw error
        }
        pause(retryDelaysMillis[retryIndex++])
        if (remoteState() is RemoteSessionState.Controlling) return false
    }
}

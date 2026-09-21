package dev.termlink.product

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
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
import dev.termlink.history.EncryptedHistoryObject
import dev.termlink.history.EncryptedTranscriptPage
import dev.termlink.history.RendererCheckpointState
import dev.termlink.history.TranscriptPageMetadata
import dev.termlink.harness.BuildConfig
import dev.termlink.harness.R
import dev.termlink.model.CatalogPlan
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.isPcAgent
import dev.termlink.model.Entitlement
import dev.termlink.model.HistoryObject
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.model.TerminalCommandCapability
import dev.termlink.network.direct.ProductionDirectAuthorizedPeer
import dev.termlink.network.direct.ProductionDirectCarrier
import dev.termlink.network.direct.ProductionDirectDeviceCoordinator
import dev.termlink.network.direct.ProductionDirectLiveSessionRegistration
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.ControlRequestSupersededException
import dev.termlink.network.production.InputSequenceProvider
import dev.termlink.network.production.ProductionApiClient
import dev.termlink.network.production.ProductionHostHistoryObject
import dev.termlink.network.production.ProductionHostHistoryNotReadyException
import dev.termlink.network.production.ProductionRealtimeMultiplexer
import dev.termlink.network.production.ProductionRealtimeSessionClient
import dev.termlink.network.production.ProductionRendererFatalFailure
import dev.termlink.network.production.ProductionRealtimeSessionConfig
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.HistoryChunkV1
import dev.termlink.protocol.realtime.v1.HistoryEventV1
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
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
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalEngineFailureDiagnostics
import dev.termlink.terminal.api.PreparedTerminalCheckpoint
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalViewportEngine
import dev.termlink.terminal.nativecore.NativeTerminalEngine
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.io.IOException
import java.io.Closeable
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
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
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
import kotlinx.coroutines.ensureActive
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
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
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
import kotlin.concurrent.thread

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

/**
 * Invalidates any asynchronous token result captured by an older auth lifecycle.
 *
 * The pinned Termux sources intentionally exclude OIDC, WebSocket authentication,
 * and remote-session ownership.  TermLink therefore owns this boundary; Termux's
 * generation checks are only a lifecycle-behaviour reference, not reusable code.
 */
internal class AuthenticationLifecycleFence(
    initialGeneration: Long = 0L,
) {
    init {
        require(initialGeneration >= 0L) { "authentication generation cannot be negative" }
    }

    private val lock = Any()
    private var generation = initialGeneration

    fun capture(): Long = synchronized(lock) { generation }

    /** Captures generation and caller-owned state in one transaction. */
    fun <T> capture(action: (Long) -> T): T = synchronized(lock) { action(generation) }

    fun invalidate(action: () -> Unit = {}): Long = synchronized(lock) {
        nextGeneration().also {
            generation = it
            action()
        }
    }

    fun isCurrent(captured: Long): Boolean = synchronized(lock) { generation == captured }

    /**
     * Performs a short, non-suspending commit only while the captured auth
     * lifecycle and its caller-owned state are still authoritative. Starting a
     * successor authentication waits for this commit, so a stale async result
     * cannot pass a check and then overwrite the successor outside the fence.
     */
    fun runIfCurrent(
        captured: Long,
        predicate: () -> Boolean = { true },
        action: () -> Unit,
    ): Boolean = synchronized(lock) {
        if (generation != captured || !predicate()) return@synchronized false
        action()
        true
    }

    /** Atomically claims and retires one exact authentication lifecycle. */
    fun invalidateIfCurrent(
        captured: Long,
        predicate: () -> Boolean = { true },
        action: () -> Unit,
    ): Boolean = synchronized(lock) {
        if (generation != captured || !predicate()) return@synchronized false
        generation = nextGeneration()
        action()
        true
    }

    private fun nextGeneration(): Long {
        check(generation < Long.MAX_VALUE) { "authentication generation exhausted" }
        return generation + 1L
    }
}

/**
 * Serializes exact-runtime retirement without depending on a cancellable owner
 * scope. Registration happens while the authentication CAS is held; a later
 * transition observes the completion even when it raced with an earlier empty
 * check. Each claim also waits for its predecessor, so detached runtimes are
 * closed in authentication-generation order.
 */
internal class AuthenticationRetirementBarrier {
    internal data class Claim(
        val predecessor: Deferred<Unit>?,
        val completion: CompletableDeferred<Unit>,
    )

    private val lock = Any()
    private var tail: CompletableDeferred<Unit>? = null

    fun register(): Claim = synchronized(lock) {
        Claim(
            predecessor = tail,
            completion = CompletableDeferred<Unit>().also { tail = it },
        )
    }

    fun complete(claim: Claim) {
        claim.completion.complete(Unit)
        synchronized(lock) {
            if (tail === claim.completion) tail = null
        }
    }

    suspend fun awaitQuiescence() {
        while (true) {
            val pending = synchronized(lock) { tail } ?: return
            pending.await()
        }
    }
}

/** Exact-owner check shared by callback admission and runtime retirement. */
internal fun authenticationRuntimeClaimMatches(
    currentOwner: Any?,
    currentOwnerGeneration: Long?,
    expectedOwner: Any?,
    expectedGeneration: Long,
): Boolean = if (expectedOwner == null) {
    currentOwnerGeneration == null || currentOwnerGeneration == expectedGeneration
} else {
    currentOwnerGeneration == expectedGeneration && currentOwner === expectedOwner
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
    pcCarrierStates = emptyMap(),
    sessionCarrierStates = emptyMap(),
)

/** Exact runtime identity fence used by asynchronous Direct carrier projection. */
internal fun directRuntimeProjectionClaimMatches(
    currentOwner: Any?,
    currentMultiplexer: Any?,
    currentCoordinator: Any?,
    expectedOwner: Any,
    expectedMultiplexer: Any,
    expectedCoordinator: Any,
): Boolean = currentOwner === expectedOwner &&
    currentMultiplexer === expectedMultiplexer &&
    currentCoordinator === expectedCoordinator

/**
 * Keeps only non-Relay carriers backed by the current trusted, online PC
 * directory. Missing entries deliberately project as Relay in [ProductSnapshot].
 */
internal fun retainAuthoritativePcCarrierStates(
    carrierStates: Map<UUID, PcCarrierState>,
    devices: List<DeviceSummary>,
    pcs: List<PcSummary>,
): Map<UUID, PcCarrierState> {
    val trustedPcDeviceIds = devices.asSequence()
        .filter { it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED }
        .mapTo(hashSetOf(), DeviceSummary::id)
    val admitted = pcs.asSequence()
        .filter(PcSummary::online)
        .map(PcSummary::deviceId)
        .filterTo(hashSetOf(), trustedPcDeviceIds::contains)
    return carrierStates.filter { (deviceId, carrier) ->
        deviceId in admitted && carrier != PcCarrierState.RELAY
    }
}

/**
 * Keeps only exact live-session carriers whose owning PC is still trusted and
 * online. A device-level Direct state is never expanded to unlisted sessions.
 */
internal fun retainAuthoritativeSessionCarrierStates(
    carrierStates: Map<UUID, PcCarrierState>,
    devices: List<DeviceSummary>,
    pcs: List<PcSummary>,
    sessions: List<SessionSummary>,
): Map<UUID, PcCarrierState> {
    val trustedPcDeviceIds = devices.asSequence()
        .filter { it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED }
        .mapTo(hashSetOf(), DeviceSummary::id)
    val admittedPcIds = pcs.asSequence()
        .filter { it.online && it.deviceId in trustedPcDeviceIds }
        .mapTo(hashSetOf(), PcSummary::id)
    val admittedSessionIds = sessions.asSequence()
        .filter { it.online && it.pcId in admittedPcIds }
        .mapTo(hashSetOf(), SessionSummary::id)
    return carrierStates.filter { (sessionId, carrier) ->
        sessionId in admittedSessionIds && carrier != PcCarrierState.RELAY
    }
}

internal fun ProductionDirectCarrier.toPcCarrierState(): PcCarrierState = when (this) {
    ProductionDirectCarrier.RELAY -> PcCarrierState.RELAY
    ProductionDirectCarrier.NEGOTIATING -> PcCarrierState.NEGOTIATING_DIRECT
    ProductionDirectCarrier.LAN_DIRECT -> PcCarrierState.LAN_DIRECT
    ProductionDirectCarrier.NAT_DIRECT -> PcCarrierState.NAT_DIRECT
}

private const val DIRECT_SESSION_CUTOVER_CAPABILITY = "direct_session_cutover_v1"
private const val DIRECT_TRANSPORT_DATA_CAPABILITY = "direct_transport_data_v1"

private class RevokedDeviceIdentityException : Exception()

/**
 * Data-plane Hello capabilities are an optional advertisement. They must not
 * enter the required Welcome set because Relay-only servers remain supported.
 * Host-owned history is negotiated per connection too: without advertising it
 * here the relay silently drops HOST_HISTORY_* frames from the phone, which
 * degrades every session open to live-only output with no snapshot bootstrap
 * and no transcript paging.
 */
internal fun productRealtimeOptionalCapabilities(dataPlaneEnabled: Boolean): Set<String> =
    buildSet {
        add(PcCapabilityIds.HOST_HISTORY_V1)
        if (dataPlaneEnabled) {
            add(DIRECT_SESSION_CUTOVER_CAPABILITY)
            add(DIRECT_TRANSPORT_DATA_CAPABILITY)
        }
    }

/** Only a rejected refresh credential terminates an established sign-in. */
internal fun isAuthenticationRefreshRejection(error: Throwable): Boolean =
    error is NativeOidcException && error.reason == NativeOidcFailure.INVALID_CREDENTIALS

/** An outer refresh catch must not replace the actionable expiry message. */
internal fun shouldPreserveAuthenticationExpiredFailure(
    snapshot: ProductSnapshot,
    expiredMessage: String,
): Boolean = !snapshot.signedIn &&
    snapshot.authenticationState == ProductAuthenticationState.SIGNED_OUT &&
    snapshot.connectionState == ProductConnectionState.ERROR &&
    snapshot.connectionError == expiredMessage

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

internal fun historyGapSessionsReadyForAutoReopen(
    previous: ProductSnapshot,
    incoming: List<SessionSummary>,
): Set<UUID> {
    val previousSessions = previous.sessions.associateBy(SessionSummary::id)
    return incoming.asSequence()
        .filter { session ->
            session.online && session.state == SessionState.RUNNING &&
                previous.sessionOpenFailures[session.id]?.code == SessionOpenFailureCode.HISTORY_GAP &&
                previousSessions[session.id]?.let {
                    session.lastOutputSequence > it.lastOutputSequence
                } == true
        }
        .mapTo(linkedSetOf(), SessionSummary::id)
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
 * The PC epoch is monotonic across Windows device-key replacement. A new
 * device identity may introduce a new exchange key only while advancing that
 * epoch; transitions within one device identity remain monotonic and
 * key-stable.
 */
internal fun validateAuthoritativePcKeyTransition(
    storedPc: PcSummary,
    storedDevice: DeviceSummary?,
    incomingPc: PcSummary,
    incomingDevice: DeviceSummary?,
) {
    require(incomingPc.id == storedPc.id) { "PC key transition changed the PC identity" }
    if (incomingPc.deviceId != storedPc.deviceId) {
        val replacement = requireNotNull(incomingDevice?.takeIf { it.id == incomingPc.deviceId }) {
            "replacement PC identity is missing its device key"
        }
        require(replacement.type.isPcAgent() && replacement.trustState == DeviceTrustState.TRUSTED) {
            "replacement PC identity is not a trusted PC device"
        }
        require(replacement.exchangePublicKey.size == 32 && replacement.exchangePublicKey.any { it.toInt() != 0 }) {
            "replacement PC exchange key is invalid"
        }
        require(incomingPc.keyEpoch > storedPc.keyEpoch) {
            "replacement PC identity must advance the key epoch"
        }
        return
    }
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

/** Retains only already-opened host-owned read-only renderers for trusted PCs. */
internal fun retainedReadOnlySessionIds(
    sessions: List<SessionSummary>,
    pcs: List<PcSummary>,
    trustedPcIds: Set<UUID>,
): Set<UUID> {
    val hostHistoryPcIds = pcs.asSequence()
        .filter { it.id in trustedPcIds && PcCapabilityIds.HOST_HISTORY_V1 in it.capabilities }
        .mapTo(hashSetOf(), PcSummary::id)
    return sessions.asSequence()
        .filter { it.state == SessionState.EXITED && it.pcId in hostHistoryPcIds }
        .mapTo(hashSetOf(), SessionSummary::id)
}

/**
 * Runtime state belongs to an authoritative catalog row even before a
 * renderer has been installed. This is especially important during cold
 * bootstrap, where there is deliberately no engine ownership yet.
 */
internal fun retainedRuntimeMetadataSessionIds(
    authoritativeSessions: List<SessionSummary>,
    readOnlySessionIds: Set<UUID>,
    liveSessionIds: Set<UUID>,
): Set<UUID> = authoritativeSessions.asSequence()
    .map(SessionSummary::id)
    .toCollection(hashSetOf())
    .also { retained ->
        retained.addAll(readOnlySessionIds)
        retained.addAll(liveSessionIds)
    }

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
    val contiguousAppliedSequence: Long = 0,
    val duplicateEventCount: Long = 0,
    val historyGapCount: Long = 0,
    val durableAck: Long = 0,
    val networkAvailableCount: Int,
    val networkLostCount: Int,
    val networkReconnectCount: Int,
)

internal enum class RealtimeNetworkObservationTransition {
    INITIAL,
    RECONNECTED,
    UNCHANGED,
}

internal fun <NetworkToken> realtimeNetworkObservationTransition(
    hasObservedNetwork: Boolean,
    currentNetwork: NetworkToken?,
    observedNetwork: NetworkToken,
): RealtimeNetworkObservationTransition = when {
    !hasObservedNetwork -> RealtimeNetworkObservationTransition.INITIAL
    currentNetwork != observedNetwork -> RealtimeNetworkObservationTransition.RECONNECTED
    else -> RealtimeNetworkObservationTransition.UNCHANGED
}

internal data class RealtimeAppLifecycleState(
    val earliestBackgroundAtMillis: Long? = null,
    val lastForegroundGeneration: Long = 0,
    val lastForegroundAtMillis: Long = 0,
)

internal data class RealtimeForegroundObservation(
    val state: RealtimeAppLifecycleState,
    val backgroundDurationMillis: Long?,
    val accepted: Boolean,
)

/**
 * Serializes lifecycle mutations for one logical session without making an
 * unrelated session wait behind Snapshot download, history replay, JNI, or
 * graceful ACK-writer retirement.  A reference is retained before waiting for
 * the lane, so the registry cannot remove and recreate a second mutex while a
 * waiter still targets the first one.
 */
internal class SessionLifecycleLanes<K : Any> {
    private class Lane {
        val mutex = Mutex()
        var references = 0
    }

    private val registryLock = Any()
    private val lanes = mutableMapOf<K, Lane>()

    suspend fun <T> withLock(key: K, action: suspend () -> T): T {
        val lane = synchronized(registryLock) {
            lanes.getOrPut(key, ::Lane).also { it.references++ }
        }
        return try {
            lane.mutex.withLock { action() }
        } finally {
            synchronized(registryLock) {
                check(lane.references > 0) { "Session lifecycle lane reference underflow" }
                lane.references--
                if (lane.references == 0) {
                    check(lanes[key] === lane) { "Session lifecycle lane identity changed" }
                    lanes.remove(key)
                }
            }
        }
    }

    internal fun activeLaneCount(): Int = synchronized(registryLock) { lanes.size }

    internal fun activeReferenceCount(key: K): Int = synchronized(registryLock) {
        lanes[key]?.references ?: 0
    }
}

internal enum class RendererFatalIncarnationDecision {
    STALE,
    RECOVER,
    FAIL_CLOSED,
}

/** A poison sequence is inherited only by the exact recovery successor. */
internal fun rendererFatalIncarnationDecision(
    expectedIsCurrent: Boolean,
    inheritedFatalSequence: Long?,
    failureSequence: Long,
): RendererFatalIncarnationDecision = when {
    !expectedIsCurrent -> RendererFatalIncarnationDecision.STALE
    inheritedFatalSequence == failureSequence -> RendererFatalIncarnationDecision.FAIL_CLOSED
    else -> RendererFatalIncarnationDecision.RECOVER
}

/**
 * Removes exactly one incarnation, waits for its durable writers to stop, and
 * only then permits a replacement.  A stale callback returns without touching
 * the current map entry.
 */
internal suspend fun <T : Any> retireExactIncarnationAndThen(
    expected: T,
    removeExact: (T) -> Boolean,
    closeAndJoin: suspend (T) -> Unit,
    afterRetired: suspend () -> Unit = {},
): Boolean {
    if (!removeExact(expected)) return false
    closeAndJoin(expected)
    afterRetired()
    return true
}

/** Retains the earliest stop callback until one newer foreground consumes it. */
internal fun recordRealtimeAppBackground(
    state: RealtimeAppLifecycleState,
    elapsedRealtimeMillis: Long,
): RealtimeAppLifecycleState {
    require(elapsedRealtimeMillis >= 0) { "elapsed realtime must not be negative" }
    if (elapsedRealtimeMillis < state.lastForegroundAtMillis) return state
    return state.copy(
        earliestBackgroundAtMillis = minOf(
            state.earliestBackgroundAtMillis ?: elapsedRealtimeMillis,
            elapsedRealtimeMillis,
        ),
    )
}

/** Deduplicates foreground callbacks and returns null duration on first start. */
internal fun observeRealtimeAppForeground(
    state: RealtimeAppLifecycleState,
    generation: Long,
    elapsedRealtimeMillis: Long,
): RealtimeForegroundObservation {
    require(generation > 0) { "foreground generation must be positive" }
    require(elapsedRealtimeMillis >= 0) { "elapsed realtime must not be negative" }
    if (generation <= state.lastForegroundGeneration || elapsedRealtimeMillis < state.lastForegroundAtMillis) {
        return RealtimeForegroundObservation(state, null, accepted = false)
    }
    val backgroundAt = state.earliestBackgroundAtMillis
    return RealtimeForegroundObservation(
        state = RealtimeAppLifecycleState(
            earliestBackgroundAtMillis = null,
            lastForegroundGeneration = generation,
            lastForegroundAtMillis = elapsedRealtimeMillis,
        ),
        backgroundDurationMillis = backgroundAt?.let { (elapsedRealtimeMillis - it).coerceAtLeast(0) },
        accepted = true,
    )
}

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
private const val REMOTE_DOWNLOAD_PREFLIGHT_TIMEOUT_MILLIS = 30_000L
private const val REMOTE_SHELL_CATALOG_TIMEOUT_MILLIS = 30_000L
private const val REMOTE_FILE_PATH_MAX_BYTES = 4_096
private const val REMOTE_FILE_LIST_PAGE_MAX_ENTRIES = 500
private const val AGENT_UPDATE_POLL_INTERVAL_MILLIS = 1_000L

internal fun createIsolatedDirectShadowHttpClient(relayClient: OkHttpClient): OkHttpClient {
    val directClient = OkHttpClient()
    check(directClient.dispatcher !== relayClient.dispatcher) {
        "Direct shadow must not share the Relay dispatcher"
    }
    check(directClient.connectionPool !== relayClient.connectionPool) {
        "Direct shadow must not share the Relay connection pool"
    }
    return directClient
}

internal class ProductionProductRepository(
    private val context: Context,
    private val activityProvider: () -> Activity?,
    private val launchAuthorization: (Intent) -> Unit,
) : ProductRepository {
    private val sessionStore = OidcSessionStore(context)
    private val keyVault = DeviceKeyVault(context)
    private val database = TermLinkDatabase.open(context)
    private val historyRepository = HistoryRepository(database)
    private val authService = AuthorizationService(context)
    private val http = OkHttpClient()
    private val remoteFilePreflightHttp = http.newBuilder()
        .callTimeout(20, TimeUnit.SECONDS)
        .build()
    // Direct is an optional sibling path. Give its blocking REST calls and
    // signalling WebSocket an independent dispatcher/connection pool so a
    // black-holed STUN/signalling attempt cannot consume Relay's HTTP budget.
    private val directShadowHttp = createIsolatedDirectShadowHttpClient(http)
    private val remoteDownloadStore: RemoteDownloadRecordStore =
        SharedPreferencesRemoteDownloadRecordStore(context)
    private val storedRemoteDownloads = remoteDownloadStore.load()
    private val interruptedRemoteDownloads = storedRemoteDownloads
        .filter { it.status.isActive }
    private val restoredRemoteDownloads = restoreRemoteDownloadRecords(storedRemoteDownloads).also {
        remoteDownloadStore.save(it)
    }
    private val fileTransferCache = FileTransferCache(context)
    private val terminalSnapshotReadinessHttp = http.newBuilder()
        .callTimeout(TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .build()
    private val updatePreferences = context.getSharedPreferences(UPDATE_PREFERENCES, Context.MODE_PRIVATE)
    private val nativeAuthHttp = secureNativeHttpClient()
    private val nativeOidc = NativeOidcClient(nativeAuthHttp, Json { ignoreUnknownKeys = true })
    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    // The device WebSocket router, liveness probe and authentication recovery
    // must keep progressing even if Compose or a large terminal projection
    // occupies Main. Session renderers have their own per-session lanes.
    private val realtimeRuntimeScope = CoroutineScope(
        SupervisorJob() + Dispatchers.Default.limitedParallelism(1),
    )
    // Direct performs bounded but blocking control-plane and native work. It
    // must never occupy the single Relay/router permit above.
    private val directCoordinatorRuntimeScope = CoroutineScope(
        SupervisorJob() + Dispatchers.IO,
    )
    private val connectivityManager = context.getSystemService(ConnectivityManager::class.java)
    private val realtimeNetworkLock = Any()
    private val realtimeLifecycleLock = Any()
    private var realtimeNetwork: Network? = null
    private var hasObservedRealtimeNetwork = false
    private var realtimeNetworkCallbackRegistered = false
    private val realtimeNetworkAvailableCount = AtomicInteger(0)
    private val realtimeNetworkLostCount = AtomicInteger(0)
    private val realtimeNetworkReconnectCount = AtomicInteger(0)
    private var realtimeLifecycleState = RealtimeAppLifecycleState()
    /** Foreground was observed while the default network was not yet validated. */
    private var pendingRealtimeForegroundReconcile = false

    private fun isRealtimeNetworkUsable(network: Network? = connectivityManager.activeNetwork): Boolean {
        if (network == null) return false
        val capabilities = connectivityManager.getNetworkCapabilities(network) ?: return false
        return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
            capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) &&
            (Build.VERSION.SDK_INT < Build.VERSION_CODES.P ||
                capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED))
    }

    private fun observeRealtimeNetworkAvailable(network: Network) {
        // onAvailable may precede validation. Do not consume the token until a
        // later capability callback proves the default network is usable.
        if (!isRealtimeNetworkUsable(network)) return
        val transition = synchronized(realtimeNetworkLock) {
            realtimeNetworkObservationTransition(
                hasObservedNetwork = hasObservedRealtimeNetwork,
                currentNetwork = realtimeNetwork,
                observedNetwork = network,
            ).also { observed ->
                if (observed != RealtimeNetworkObservationTransition.UNCHANGED) {
                    hasObservedRealtimeNetwork = true
                    realtimeNetwork = network
                }
            }
        }
        val consumePendingForeground = synchronized(realtimeLifecycleLock) {
            pendingRealtimeForegroundReconcile.also {
                pendingRealtimeForegroundReconcile = false
            }
        }
        if (transition != RealtimeNetworkObservationTransition.UNCHANGED) {
            realtimeNetworkAvailableCount.incrementAndGet()
        }
        if (transition == RealtimeNetworkObservationTransition.RECONNECTED || consumePendingForeground) {
            realtimeNetworkReconnectCount.incrementAndGet()
            realtimeRuntimeScope.launch {
                realtimeMultiplexer?.onNetworkAvailable()
            }
        }
        observeDirectCoordinatorNetwork(network)
    }
    private val realtimeNetworkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            observeRealtimeNetworkAvailable(network)
        }

        override fun onCapabilitiesChanged(network: Network, networkCapabilities: NetworkCapabilities) {
            // Some emulator and OEM stacks replace the default network while
            // it is still being provisioned and deliver the first usable
            // signal as a capability change rather than a second onAvailable.
            // Deduplication keeps this a single reconnect boundary.
            if (networkCapabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                observeRealtimeNetworkAvailable(network)
            }
        }

        override fun onLost(network: Network) {
            realtimeNetworkLostCount.incrementAndGet()
            val removed = synchronized(realtimeNetworkLock) {
                hasObservedRealtimeNetwork = true
                if (realtimeNetwork == network) {
                    realtimeNetwork = null
                    true
                } else {
                    false
                }
            }
            if (removed) observeDirectCoordinatorNetwork(null)
        }
    }

    private fun observeDirectCoordinatorNetwork(network: Network?) {
        val owned = synchronized(liveSessionLock) {
            val owner = realtimeMultiplexerOwner
            val coordinator = directCoordinator
            if (owner == null || coordinator == null) null else owner.authenticationGeneration to coordinator
        } ?: return
        owned.second.observeNetwork(owned.first, network)
    }

    private val liveSessions = ConcurrentHashMap<UUID, LiveProductSession>()
    /**
     * Final renderers restored from an online Windows host. They own no
     * realtime endpoint or control lease; Room remains only a rendering cache.
     */
    private val readOnlySessionEngines = ConcurrentHashMap<UUID, NativeTerminalEngine>()
    /** Fail-closed until a serialized catalog refresh publishes replacement authority. */
    private val sessionAuthorityTransitionFences = ConcurrentHashMap.newKeySet<UUID>()
    @Volatile private var realtimeMultiplexer: ProductionRealtimeMultiplexer? = null
    @Volatile private var realtimeMultiplexerOwner: RealtimeAuthenticationOwner? = null
    @Volatile private var directCoordinator: ProductionDirectDeviceCoordinator? = null
    @Volatile private var directCarrierProjectionJob: Job? = null
    private val historyEngines = ConcurrentHashMap<UUID, TermuxTerminalEngine>()
    private val historyDownloads = ConcurrentHashMap<UUID, HistoryDownloadRequest>()
    private val transcriptLoads = ConcurrentHashMap<TranscriptLoadKey, Deferred<Unit>>()
    private val deletedSessionIds = ConcurrentHashMap.newKeySet<UUID>()
    private val newlyLaunchedSessionIds = ConcurrentHashMap.newKeySet<UUID>()
    private val remoteDownloadJobs = ConcurrentHashMap<UUID, Job>()
    private val remoteDownloadStateLock = Any()
    private val liveSessionLock = Any()
    private val accessStateLock = Mutex()
    private val refreshStateLock = Mutex()
    private val sessionLifecycleLanes = SessionLifecycleLanes<UUID>()
    private val updateCheckLock = Mutex()
    private val updateDownloadLock = Mutex()
    private val keyMetadataRefreshLock = Mutex()
    private val authenticationAccountScopeLock = Mutex()
    /** Serializes auth transitions with local publication of an authenticated refresh. */
    private val authenticationStateMutationLock = Mutex()
    /** Keeps a successor transition behind exact old-runtime closeAndJoin. */
    private val authenticationTransitionLock = Mutex()
    // Authentication lock order is transition -> state mutation -> auth fence
    // -> liveSessionLock. No code holding liveSessionLock may enter the fence.
    private val authenticationRetirementBarrier = AuthenticationRetirementBarrier()
    private val realtimeAccessToken = RealtimeAccessTokenSource()
    // Runtime owners require a positive generation. Start this repository at
    // generation one so persisted authentication can restore a session before
    // the first interactive auth transition advances the fence.
    private val authenticationLifecycle = AuthenticationLifecycleFence(initialGeneration = 1L)
    private var pendingInteractiveAuthenticationGeneration: Long? = null
    private val tokenRefresh = TokenRefreshCoordinator { force ->
        requestFreshAccessToken(force, expectedRuntimeOwner = null)
    }
    // The build default is only the first-run value. A selected endpoint is
    // persisted and is the root for every TermLink request in this runtime.
    private val initialServerUrl = run {
        val stored = sessionStore.serverUrl
        val normalized = stored?.let { runCatching { normalizeProductServerUrl(it) }.getOrNull() }
            ?: PRODUCT_PUBLIC_SERVER_URL
        if (sessionStore.serverUrl != normalized) sessionStore.serverUrl = normalized
        normalized
    }

    private fun currentServerUrl(): String = sessionStore.serverUrl ?: initialServerUrl
    private val billing = BillingCoordinator(
        adapter = PlayBillingAdapter(context, activityProvider),
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
    @Volatile private var authState: AuthState? = sessionStore.loadAuthState()
    private var storedIdentity: DeviceKeyVault.StoredIdentity? = null
    @Volatile private var accountScope: String? = sessionStore.accountScope
    private var oidcConfiguration: NativeOidcConfiguration? = null
    @Volatile private var closed = false
    private var directProductE2EBridgeRegistration: Closeable? = null
    private val updateArchitecture = Build.SUPPORTED_ABIS.firstOrNull()?.takeIf { it.isNotBlank() } ?: "universal"
    private val updateRequestKey = "$PRODUCT_APPSHIP_SLUG|android|$updateArchitecture|stable|${BuildConfig.VERSION_CODE}"
    private val updateDownloader = ProductUpdateDownloader(
        context = context,
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

    override fun onAppBackgrounded(elapsedRealtimeMillis: Long) {
        if (closed) return
        synchronized(realtimeLifecycleLock) {
            realtimeLifecycleState = recordRealtimeAppBackground(
                realtimeLifecycleState,
                elapsedRealtimeMillis,
            )
        }
    }

    override fun onAppForegrounded(generation: Long, elapsedRealtimeMillis: Long) {
        if (closed) return
        val observation = synchronized(realtimeLifecycleLock) {
            observeRealtimeAppForeground(
                realtimeLifecycleState,
                generation,
                elapsedRealtimeMillis,
            ).also { realtimeLifecycleState = it.state }
        }
        if (!observation.accepted) return
        val backgroundDurationMillis = observation.backgroundDurationMillis ?: return
        if (!isRealtimeNetworkUsable()) {
            synchronized(realtimeLifecycleLock) {
                pendingRealtimeForegroundReconcile = true
            }
            // Close the race where capabilities became usable after the first
            // query but before the pending bit was published. Either this path
            // or the capability callback consumes the bit, never neither.
            val consumeNow = isRealtimeNetworkUsable() && synchronized(realtimeLifecycleLock) {
                pendingRealtimeForegroundReconcile.also {
                    pendingRealtimeForegroundReconcile = false
                }
            }
            if (consumeNow) {
                realtimeRuntimeScope.launch {
                    realtimeMultiplexer?.onNetworkAvailable()
                }
            }
            return
        }
        realtimeRuntimeScope.launch {
            realtimeMultiplexer?.onAppForegrounded(backgroundDurationMillis, networkUsable = true)
        }
    }

    /** Content-free lifecycle observation used only by the isolated Debug acceptance bridge. */
    internal fun debugDirectProductE2ELifecycle(): RealtimeAppLifecycleState =
        synchronized(realtimeLifecycleLock) { realtimeLifecycleState }

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
                withAuthenticatedApi { api, generation -> pollSessionCatalogWithApi(api, generation) }
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
        if (BuildConfig.TERMLINK_DIRECT_TRANSPORT_SHADOW &&
            BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE
        ) {
            val bridge = ProductionDirectProductE2EBridge(
                repository = this,
                captureAuthenticationGeneration = authenticationLifecycle::capture,
                isAuthenticationGenerationCurrent = authenticationLifecycle::isCurrent,
                moveProductToBackground = { activityProvider()?.moveTaskToBack(true) == true },
                moveProductToForeground = {
                    val activity = activityProvider()
                    if (activity == null) {
                        false
                    } else {
                        activity.startActivity(
                            Intent(activity, ProductActivity::class.java).addFlags(
                                Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP,
                            ),
                        )
                        true
                    }
                },
            )
            directProductE2EBridgeRegistration = ProductionDirectProductE2EBridgeRegistry.attach(bridge)
        }
        if (authState?.isAuthorized == true) {
            scope.launch { runCatching { refresh() } }
        }
    }

    override suspend fun configureServer(url: String) {
        val normalized = normalizeProductServerUrl(url)
        val changed = normalized != currentServerUrl()
        var authenticationGeneration = authenticationLifecycle.capture()
        if (changed) {
            cancelRemoteDownloadsAndJoin(clearMetadata = true)
            val generation = beginAuthenticationTransition()
            authenticationGeneration = generation
            var retiredIdentity: DeviceKeyVault.StoredIdentity? = null
            authenticationStateMutationLock.withLock {
                if (authenticationLifecycle.isCurrent(generation) && !closed) {
                    closeHistoryEngines()
                    clearRoomCache()
                    authenticationLifecycle.runIfCurrent(generation, predicate = { !closed }) {
                        retiredIdentity = storedIdentity
                        storedIdentity = null
                        accountScope = null
                        sessionStore.accountScope = null
                        oidcConfiguration = null
                    }
                }
            }
            retiredIdentity?.identity?.destroy()
        }
        sessionStore.serverUrl = normalized
        val publishConfigured = {
            billing.reset()
            mutableSnapshot.value = ProductSnapshot(
                configured = true,
                authenticationState = ProductAuthenticationState.SIGNED_OUT,
                connectionState = ProductConnectionState.CONFIGURED,
                serverUrl = normalized,
            )
        }
        authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
            publishConfigured()
        }
    }

    override suspend fun signIn(username: String, password: CharArray) {
        check(!closed) { "Repository is closed" }
        val authenticationGeneration = beginAuthenticationTransition()
        check(authenticationLifecycle.runIfCurrent(authenticationGeneration) {
            mutableSnapshot.value = mutableSnapshot.value.copy(
                authenticationState = ProductAuthenticationState.NATIVE_CREDENTIALS,
                connectionState = ProductConnectionState.CONFIGURED,
                connectionError = null,
            )
        }) { "Authentication lifecycle changed" }
        try {
            val configuration = loadOidcConfiguration()
            val payload = withContext(Dispatchers.IO) {
                nativeOidc.passwordGrant(configuration, username, password)
            }
            requireCurrentAuthentication(authenticationGeneration)
            val subject = OidcJwtClaims.accessTokenSubject(payload.accessToken, configuration.issuer, configuration.clientId)
            activateAccount(configuration, subject, payload, username, authenticationGeneration)
            bootstrapAuthenticatedSession(authenticationGeneration)
        } catch (error: NativeOidcException) {
            authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                publishFailure(error)
                if (error.interactiveFallbackAvailable) {
                    mutableSnapshot.value = mutableSnapshot.value.copy(
                        authenticationState = ProductAuthenticationState.EMBEDDED_SETUP_REQUIRED,
                        connectionState = ProductConnectionState.ERROR,
                        connectionError = null,
                    )
                }
            }
            throw error
        } catch (error: Throwable) {
            authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                publishFailure(error)
            }
            throw error
        } finally {
            password.fill('\u0000')
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

    override suspend fun beginEmbeddedRegistration() = beginEmbeddedAuth(EmbeddedAuthFlow.REGISTRATION)

    override suspend fun beginEmbeddedAccountSetup() = beginEmbeddedAuth(EmbeddedAuthFlow.ACCOUNT_SETUP)

    private suspend fun beginEmbeddedAuth(flow: EmbeddedAuthFlow) {
        check(!closed) { "Repository is closed" }
        val authenticationGeneration = beginAuthenticationTransition()
        try {
            val configuration = loadOidcConfiguration()
            val initialized = authenticationLifecycle.runIfCurrent(
                authenticationGeneration,
                predicate = { !closed && authState == null },
            ) {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    authenticationState = ProductAuthenticationState.EMBEDDED_SETUP_REQUIRED,
                )
                authState = AuthState(configuration.serviceConfiguration)
                pendingInteractiveAuthenticationGeneration = authenticationGeneration
            }
            check(initialized) { "Authentication lifecycle changed" }
            val requestBuilder = AuthorizationRequest.Builder(
                configuration.serviceConfiguration,
                configuration.clientId,
                ResponseTypeValues.CODE,
                configuration.redirectUri,
            ).setScope("openid profile offline_access")
            // The hosted registration page owns policy-driven account creation.
            // Keycloak is entered only for login/required actions afterwards.
            requestBuilder.setPrompt(EmbeddedAuthPolicy.LOGIN_PROMPT)
            val request = requestBuilder.build()
            val registrationFlow = flow == EmbeddedAuthFlow.REGISTRATION
            val initialUri = if (registrationFlow) {
                EmbeddedOidcContract.registrationUri(currentServerUrl())
            } else {
                request.toUri()
            }
            launchAuthorization(
                EmbeddedOidcContract.createIntent(context, request, initialUri, registrationFlow),
            )
        } catch (error: Throwable) {
            authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                if (pendingInteractiveAuthenticationGeneration == authenticationGeneration) {
                    pendingInteractiveAuthenticationGeneration = null
                }
                publishFailure(error)
            }
            throw error
        }
    }

    fun handleAuthorizationResult(data: Intent?) {
        val claim = authenticationLifecycle.capture { currentGeneration ->
            val state = authState
            if (closed || pendingInteractiveAuthenticationGeneration != currentGeneration || state == null) {
                null
            } else {
                pendingInteractiveAuthenticationGeneration = null
                InteractiveAuthorizationClaim(currentGeneration, state)
            }
        } ?: return
        val authenticationGeneration = claim.authenticationGeneration
        val state = claim.authState
        if (data == null) {
            authenticationLifecycle.runIfCurrent(
                authenticationGeneration,
                predicate = { !closed && authState === state },
            ) {
                publishFailure(IllegalStateException("OIDC sign-in was canceled"))
            }
            return
        }
        val response = AuthorizationResponse.fromIntent(data)
        val exception = AuthorizationException.fromIntent(data)
        val responseAccepted = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState === state },
        ) {
            state.update(response, exception)
        }
        if (!responseAccepted) return
        if (response == null) {
            authenticationLifecycle.runIfCurrent(
                authenticationGeneration,
                predicate = { !closed && authState === state },
            ) {
                publishFailure(exception ?: IllegalStateException("OIDC authorization failed"))
            }
            return
        }
        authService.performTokenRequest(response.createTokenExchangeRequest()) { tokenResponse, tokenException ->
            val tokenAccepted = authenticationLifecycle.runIfCurrent(
                authenticationGeneration,
                predicate = { !closed && authState === state },
            ) {
                state.update(tokenResponse, tokenException)
            }
            if (!tokenAccepted) return@performTokenRequest
            if (tokenResponse == null) {
                authenticationLifecycle.runIfCurrent(
                    authenticationGeneration,
                    predicate = { !closed && authState === state },
                ) {
                    publishFailure(tokenException ?: IllegalStateException("OIDC token exchange failed"))
                }
                return@performTokenRequest
            }
            val configuration = oidcConfiguration
            val accessToken = tokenResponse.accessToken
            if (configuration == null || accessToken.isNullOrBlank()) {
                authenticationLifecycle.runIfCurrent(
                    authenticationGeneration,
                    predicate = { !closed && authState === state },
                ) {
                    publishFailure(IllegalStateException("OIDC access token is unavailable"))
                }
                return@performTokenRequest
            }
            val subject = try {
                OidcJwtClaims.accessTokenSubject(accessToken, configuration.issuer, configuration.clientId)
            } catch (error: Throwable) {
                authenticationLifecycle.runIfCurrent(
                    authenticationGeneration,
                    predicate = { !closed && authState === state },
                ) {
                    publishFailure(error)
                }
                return@performTokenRequest
            }
            val cleanState = CredentialFreeAuthState.sanitize(state)
            scope.launch {
                runCatching {
                    requireCurrentAuthentication(authenticationGeneration, state)
                    // Persist the issuer+subject scope first. If the process is
                    // killed after AuthState is saved, startup must never see
                    // an authorized session without an account-scoped key.
                    establishAccountScope(
                        nextScope = AccountScope.canonical(configuration.issuer, subject),
                        authenticationGeneration = authenticationGeneration,
                    )
                    val committed = authenticationLifecycle.runIfCurrent(
                        authenticationGeneration,
                        predicate = { !closed && authState === state },
                    ) {
                        if (sessionStore.accountLogin == null) {
                            sessionStore.accountLogin = OidcJwtClaims.displayIdentifier(
                                tokenResponse.idToken ?: accessToken,
                            )
                        }
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
                    }
                    check(committed) { "Authentication lifecycle changed" }
                    bootstrapAuthenticatedSession(authenticationGeneration)
                }.onFailure { error ->
                    authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                        publishFailure(error)
                    }
                }
            }
        }
    }

    override suspend fun signOut() {
        cancelRemoteDownloadsAndJoin(clearMetadata = true)
        val authenticationGeneration = beginAuthenticationTransition()
        var retiredIdentity: DeviceKeyVault.StoredIdentity? = null
        val committed = authenticationStateMutationLock.withLock {
            if (!authenticationLifecycle.isCurrent(authenticationGeneration) || closed) {
                false
            } else {
                closeHistoryEngines()
                clearRoomCache()
                authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                    retiredIdentity = storedIdentity
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
                        serverUrl = currentServerUrl(),
                    )
                }
            }
        }
        retiredIdentity?.identity?.destroy()
        if (!committed) return
    }

    /**
     * Starts a new authentication lifecycle without ever pairing its generation
     * with the previous AuthState. Any old realtime runtime is retired by exact
     * owner generation after the auth fence has advanced; a concurrently newer
     * runtime is never touched.
     */
    private suspend fun beginAuthenticationTransition(): Long {
        return authenticationTransitionLock.withLock {
            authenticationRetirementBarrier.awaitQuiescence()
            val transition = authenticationStateMutationLock.withLock {
                val generation = authenticationLifecycle.invalidate {
                    authState = null
                    pendingInteractiveAuthenticationGeneration = null
                    realtimeAccessToken.clear()
                    runCatching { sessionStore.clearAuthState() }
                    runCatching { sessionStore.accountLogin = null }
                    mutableSnapshot.update { snapshot ->
                        snapshot.copy(
                            signedIn = false,
                            accountLabel = null,
                            pcCarrierStates = emptyMap(),
                            sessionCarrierStates = emptyMap(),
                            sessionStates = emptyMap(),
                            sessionOpenFailures = emptyMap(),
                            sessionTitles = emptyMap(),
                        )
                    }
                }
                val retired = synchronized(liveSessionLock) {
                    val owner = realtimeMultiplexerOwner
                    if (realtimeMultiplexer != null &&
                        (owner == null || owner.authenticationGeneration < generation)
                    ) {
                        detachRealtimeRuntimeLocked()
                    } else {
                        null
                    }
                }
                AuthenticationTransition(generation, retired)
            }
            if (transition.retiredRuntime != null) {
                closeRetiredRealtimeRuntimeAndJoin(transition.retiredRuntime)
            }
            // An expiry callback can register a retirement after the first
            // quiescence check but before this transition advances the auth
            // fence. Re-check after the CAS so the successor cannot overtake it.
            authenticationRetirementBarrier.awaitQuiescence()
            transition.authenticationGeneration
        }
    }

    override suspend fun confirmEnrollment() {
        check(mutableSnapshot.value.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
            "No pending enrollment confirmation is available"
        }
        withAuthenticatedApi { api, authenticationGeneration ->
            api.post("/v1/devices/self/trust", buildJsonObject { })
            val self = ProductResponseParser.self(api.get("/v1/devices/self").jsonObject)
            if (self.trustState == DeviceTrustState.REVOKED) {
                mutableSnapshot.value = mutableSnapshot.value.copy(
                    enrollmentState = ProductEnrollmentState.REVOKED,
                    enrollmentConfirmationRequired = false,
                    devices = listOf(self),
                )
                return@withAuthenticatedApi
            }
            check(self.trustState == DeviceTrustState.TRUSTED) { "The server did not trust this device" }
            mutableSnapshot.value = mutableSnapshot.value.copy(
                enrollmentState = ProductEnrollmentState.TRUSTED,
                enrollmentConfirmationRequired = false,
                accountLabel = accountDisplayLabel(),
                devices = listOf(self),
            )
            refreshWithApiPublished(api, authenticationGeneration)
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
            context.getString(R.string.activity_pairing_challenge_created),
            context.getString(
                R.string.activity_pairing_challenge_detail,
                challenge.targetName ?: context.getString(R.string.activity_target_device),
                challenge.expiresAt?.toString() ?: context.getString(R.string.time_soon),
            ),
            Severity.WARNING,
        )
    }

    override suspend fun completePairing(
        challengeId: String,
        code: String,
        challengeNonce: String,
    ) = withAuthenticatedApi { api, authenticationGeneration ->
        val input = PairingInputParser.parse(challengeId, code, challengeNonce)
        api.post(
            "/v1/pairing/challenges/${input.challengeId}/complete",
            buildJsonObject {
                put("code", input.code)
                put("challenge_nonce", input.challengeNonce)
            },
        )
        mutableSnapshot.value = mutableSnapshot.value.copy(pairingChallenge = null)
        appendActivity(
            context.getString(R.string.activity_device_pairing_completed),
            context.getString(R.string.activity_device_pairing_completed_detail),
            Severity.SUCCESS,
        )
        refreshWithApiPublished(api, authenticationGeneration)
    }

    override suspend fun purchaseProduct(productId: String) {
        billing.purchase(productId)
    }

    override suspend fun restorePurchases() {
        billing.restore()
    }

    override suspend fun revokeDevice(deviceId: UUID) = withAuthenticatedApi { api, authenticationGeneration ->
        api.post("/v1/devices/$deviceId/revoke", buildJsonObject { })
        refreshWithApiPublished(api, authenticationGeneration)
    }

    override suspend fun agentUpdateEligibility(pcId: UUID): AgentUpdateEligibility {
        val response = withApi { api ->
            api.get("/v1/pcs/$pcId/agent-update-eligibility").jsonObject
        }
        return response.toAgentUpdateEligibility(pcId)
    }

    override suspend fun activeAgentUpdate(pcId: UUID): AgentUpdateProgress? {
        val response = withApi { api ->
            api.get("/v1/pcs/$pcId/agent-updates/active")
        }
        if (response is JsonNull) return null
        return response.jsonObject.getValue("agent_update").jsonObject.toAgentUpdateProgress(pcId)
    }

    override suspend fun runAgentUpdate(
        pcId: UUID,
        expectedTargetBuild: Long,
        onProgress: (AgentUpdateProgress) -> Unit,
    ): AgentUpdateProgress {
        require(expectedTargetBuild > 0) { "The Agent update target build is invalid" }
        var current = withApi { api ->
            api.post(
                "/v1/pcs/$pcId/agent-updates",
                buildJsonObject { put("expected_target_build", expectedTargetBuild) },
                idempotencyKey = UUID.randomUUID().toString(),
            ).jsonObject.getValue("agent_update").jsonObject.toAgentUpdateProgress(pcId)
        }
        onProgress(current)
        return monitorAgentUpdate(pcId, current.id, onProgress)
    }

    override suspend fun monitorAgentUpdate(
        pcId: UUID,
        updateId: UUID,
        onProgress: (AgentUpdateProgress) -> Unit,
    ): AgentUpdateProgress {
        var current = withApi { api ->
            api.get("/v1/pcs/$pcId/agent-updates/$updateId")
                .jsonObject.getValue("agent_update").jsonObject.toAgentUpdateProgress(pcId)
        }
        require(current.id == updateId) { "The server returned a different Agent update" }
        onProgress(current)
        while (!current.terminal) {
            delay(AGENT_UPDATE_POLL_INTERVAL_MILLIS)
            current = withApi { api ->
                api.get("/v1/pcs/$pcId/agent-updates/$updateId")
                    .jsonObject.getValue("agent_update").jsonObject.toAgentUpdateProgress(pcId)
            }
            require(current.id == updateId) { "The server returned a different Agent update" }
            onProgress(current)
        }
        return current
    }

    override suspend fun cancelAgentUpdate(pcId: UUID, updateId: UUID): AgentUpdateProgress = withApi { api ->
        api.delete("/v1/pcs/$pcId/agent-updates/$updateId")
            .jsonObject.getValue("agent_update").jsonObject.toAgentUpdateProgress(pcId)
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
            if (acquired) appendActivity(
                context.getString(R.string.activity_control_granted),
                context.getString(R.string.activity_session_detail, sessionId),
                Severity.SUCCESS,
            )
            clearSessionOpenFailure(sessionId)
        } catch (error: CancellationException) {
            throw error
        } catch (_: ControlRequestSupersededException) {
            // Windows local input is authoritative. A revoke that overtakes
            // this request is a normal control-arbitration result: retain the
            // live read-only session and remove any stale open error instead
            // of presenting TL-N003 or rebuilding the transport.
            clearSessionOpenFailure(sessionId)
        } catch (error: Throwable) {
            if (BuildConfig.DEBUG) {
                Log.w(
                    SESSION_OPEN_LOG_TAG,
                        "control_request_failed type=${error::class.java.simpleName} " +
                        "cause=${error.cause?.javaClass?.simpleName.orEmpty()} " +
                        "api=${(error as? TermLinkApiException)?.status ?: 0}:" +
                        "${(error as? TermLinkApiException)?.code.orEmpty()}",
                )
            }
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
        appendActivity(
            context.getString(R.string.activity_control_released),
            context.getString(R.string.activity_session_detail, sessionId),
            Severity.INFO,
        )
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
            appendActivity(
                context.getString(R.string.activity_control_leases_released),
                context.getString(R.string.activity_device_backgrounded),
                Severity.INFO,
            )
        }
    }

    override suspend fun openSession(sessionId: UUID): Unit = sessionLifecycleLanes.withLock(sessionId) {
        openSessionLocked(sessionId)
    }

    private suspend fun openSessionLocked(
        sessionId: UUID,
        inheritedRendererFatalSequence: Long? = null,
    ) {
        if (sessionId in sessionAuthorityTransitionFences) {
            throw sessionOpenException(SessionOpenFailureCode.PC_NOT_TRUSTED)
        }
        val catalogAtOpen = mutableSnapshot.value
        val catalogSession = catalogAtOpen.sessions.firstOrNull { it.id == sessionId }
            ?: throw sessionOpenException(SessionOpenFailureCode.SESSION_UNAVAILABLE)
        readOnlySessionEngines[sessionId]?.let { existing ->
            if (catalogSession.state == SessionState.EXITED) return
            if (readOnlySessionEngines.remove(sessionId, existing)) existing.dispose()
        }
        liveSessions[sessionId]?.let { existing ->
            if (catalogSession.state != SessionState.EXITED && !shouldReplaceLiveSessionForOpen(
                    liveSessionExists = true,
                    remoteState = mutableSnapshot.value.sessionStates[sessionId],
                )
            ) return

            // A failed bootstrap can leave an installed renderer/client behind
            // while the UI exposes retry. Reusing it makes retry a no-op. Fence
            // that exact instance, close it, and rebuild under the lifecycle
            // mutex so no second client can register the same session UUID.
            val retired = retireExactIncarnationAndThen(
                expected = existing,
                removeExact = { expected ->
                    synchronized(liveSessionLock) {
                        liveSessions.remove(sessionId, expected)
                    }
                },
                closeAndJoin = LiveProductSession::closeAndJoin,
            )
            if (!retired || liveSessions.containsKey(sessionId)) return
        }
        // Keep every opened session alive. The Android pager is a viewer over
        // multiple concurrent terminals; switching pages must not terminate
        // another session's realtime stream.
        val authentication = captureAuthenticationState()
        val authenticationGeneration = authentication.authenticationGeneration
        val product = mutableSnapshot.value
        val summary = product.sessions.firstOrNull { it.id == sessionId }
            ?: throw sessionOpenException(SessionOpenFailureCode.SESSION_UNAVAILABLE)
        val pc = product.pcs.firstOrNull { it.id == summary.pcId }
            ?: throw sessionOpenException(SessionOpenFailureCode.PC_UNAVAILABLE)
        val pcDevice = product.devices.firstOrNull {
            it.id == pc.deviceId && it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED
        } ?: throw sessionOpenException(SessionOpenFailureCode.PC_NOT_TRUSTED)
        if (pcDevice.exchangePublicKey.size != 32) {
            throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        }
        if (summary.state == SessionState.EXITED) {
            openEndedHostHistorySessionLocked(
                session = summary,
                pc = pc,
                pcDevice = pcDevice,
                authentication = authentication,
            )
            return
        }
        if (summary.state == SessionState.INTERRUPTED) {
            // The Windows host confirmed the session is gone (host-lost).
            // Opening would bootstrap against a dead ConPTY and stall until
            // timeout; fail fast instead of letting the tab spin.
            throw sessionOpenException(SessionOpenFailureCode.SESSION_ENDED)
        }
        if (!summary.online) throw sessionOpenException(SessionOpenFailureCode.SESSION_OFFLINE)
        freshAccessToken(expectedGeneration = authenticationGeneration)
        val bootstrapStarted = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState != null && accountScope == authentication.accountScope },
        ) {
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
        }
        check(bootstrapStarted) { "Authentication lifecycle changed" }
        val root = currentServerUrl()
        val stored = authenticationLifecycle.capture { currentGeneration ->
            check(
                !closed &&
                    currentGeneration == authenticationGeneration &&
                    accountScope == authentication.accountScope &&
                    authState != null
            ) { "Authentication lifecycle changed" }
            identity(authentication.accountScope)
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(summary.columns.coerceAtLeast(1), summary.rows.coerceAtLeast(1)),
        )
        // One lane owns every ordered renderer mutation for this incarnation,
        // including the initial Snapshot/history replay and later realtime
        // delivery. This keeps JNI and protobuf work off Main without allowing
        // bootstrap and live frames to race each other.
        val sessionDispatcher = Dispatchers.Default.limitedParallelism(1)
        // Start only the device-scoped handshake while REST chooses/restores a
        // checkpoint. The bounded session endpoint is still registered after
        // bootstrap so realtime ciphertext cannot overflow an unconsumed queue.
        val realtime = deviceRealtimeRuntime(stored, authenticationGeneration)
        realtime.multiplexer.warmConnect()
        var hostHistoryClient: ProductionRealtimeSessionClient? = null
        val restoredHistory = try {
            if (PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities) {
                hostHistoryClient = createHostHistoryBootstrapClient(
                    session = summary,
                    pc = pc,
                    pcDevice = pcDevice,
                    stored = stored,
                    engine = engine,
                    realtime = realtime,
                    sessionDispatcher = sessionDispatcher,
                ).also(ProductionRealtimeSessionClient::start)
            }
            withContext(sessionDispatcher) {
                restoreLiveHistory(
                    session = summary,
                    pc = pc,
                    pcDevice = pcDevice,
                    stored = stored,
                    engine = engine,
                    authenticationGeneration = authenticationGeneration,
                    allowFreshReplayFromZero = sessionId in newlyLaunchedSessionIds,
                    hostHistoryClient = hostHistoryClient,
                )
            }
        } catch (error: CancellationException) {
            withContext(NonCancellable) {
                try {
                    hostHistoryClient?.closeGracefully()
                } catch (_: Throwable) {
                    // Preserve the cancellation and dispose the renderer below.
                }
            }
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            withContext(NonCancellable) {
                try {
                    hostHistoryClient?.closeGracefully()
                } catch (_: Throwable) {
                    // Preserve the bootstrap failure and dispose the renderer below.
                }
            }
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_PREPARE)
            if (!observeBootstrapFailureIfCurrent(
                    sessionId,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        } finally {
            hostHistoryClient?.let { historyClient ->
                withContext(NonCancellable) { historyClient.closeGracefully() }
            }
        }
        val transport = try {
            check(isCurrentRealtimeOwner(realtime.owner)) { "Authentication lifecycle changed" }
            realtime.multiplexer.register(sessionId, pc.deviceId)
        } catch (error: CancellationException) {
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            if (!observeBootstrapFailureIfCurrent(
                    sessionId,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        }
        val directAppliedCursor = AtomicReference(restoredHistory.durableAcknowledgement)
        val liveReference = AtomicReference<LiveProductSession?>(null)
        val client = try {
            ProductionRealtimeSessionClient(
                engine = engine,
                config = ProductionRealtimeSessionConfig(
                    rootUrl = root,
                    tokenProvider = AccessTokenProvider { requireRealtimeAccessToken(realtime.owner) },
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
                    check(isCurrentRealtimeOwner(realtime.owner)) { "Authentication lifecycle changed" }
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
                        directAppliedCursor.updateAndGet { current -> maxOf(current, persisted) }
                        updateDirectAppliedCursorIfCurrent(
                            realtime,
                            pc.deviceId,
                            sessionId,
                            persisted.toLong(),
                        )
                        DurableAckCommit(persisted.toLong(), expectedGeneration)
                    }
                },
                acknowledgementResetObserver = { expectedGeneration ->
                    check(isCurrentRealtimeOwner(realtime.owner)) { "Authentication lifecycle changed" }
                    if (sessionId in deletedSessionIds) {
                        DurableAckCommit(0, expectedGeneration + 1)
                    } else {
                        val current = historyRepository.ackCursorState(
                            sessionId,
                            LIVE_OUTPUT_ACK_DIRECTION,
                        ) ?: error("ACK cursor is unavailable during renderer reset")
                        check(current.rendererGeneration == expectedGeneration) {
                            "ACK renderer generation changed before renderer reset"
                        }
                        check(current.sequence == 0uL) {
                            "A durable cumulative ACK cannot be reset to zero"
                        }
                        val reset = historyRepository.resetAckCursor(
                            sessionId,
                            LIVE_OUTPUT_ACK_DIRECTION,
                            expectedGeneration,
                        )
                        observeLiveAcknowledgement(sessionId, reset.sequence)
                        directAppliedCursor.updateAndGet { current -> maxOf(current, reset.sequence) }
                        updateDirectAppliedCursorIfCurrent(
                            realtime,
                            pc.deviceId,
                            sessionId,
                            reset.sequence.toLong(),
                        )
                        DurableAckCommit(reset.sequence.toLong(), reset.rendererGeneration)
                    }
                },
                authoritativeMetadataRefreshObserver = {
                    if (isCurrentRealtimeOwner(realtime.owner)) refreshLiveKeyMetadata()
                },
                rendererFatalObserver = { failure ->
                    liveReference.get()?.let { failedIncarnation ->
                        realtimeRuntimeScope.launch {
                            recoverRendererFatal(sessionId, failedIncarnation, failure)
                        }
                    }
                },
                sessionDispatcher = sessionDispatcher,
            )
        } catch (error: CancellationException) {
            transport.close()
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            transport.close()
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            if (!observeBootstrapFailureIfCurrent(
                    sessionId,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        }
        val directRegistration = try {
            realtime.directCoordinator.registerLiveSession(pc.deviceId, sessionId, client)
        } catch (error: CancellationException) {
            client.close()
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            client.close()
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            if (!observeBootstrapFailureIfCurrent(
                    sessionId,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        }
        val live = LiveProductSession(
            engine = engine,
            client = client,
            parentScope = scope,
            authenticationOwner = realtime.owner,
            directRegistration = directRegistration,
            directActivationObserver = {
                updateDirectAppliedCursorIfCurrent(
                    realtime,
                    pc.deviceId,
                    sessionId,
                    directAppliedCursor.get().toLong(),
                )
            },
            inheritedRendererFatalSequence = inheritedRendererFatalSequence,
        )
        check(liveReference.compareAndSet(null, live)) { "Live session incarnation was already installed" }
        live.installBackgroundJobs(
            stateJob = scope.launch(start = CoroutineStart.LAZY) {
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
                    val accepted = authenticationLifecycle.runIfCurrent(
                        authenticationGeneration,
                        predicate = {
                            !closed &&
                                realtimeMultiplexerOwner === realtime.owner &&
                                realtimeMultiplexer === realtime.multiplexer &&
                                directCoordinator === realtime.directCoordinator &&
                                storedIdentity === stored &&
                                liveSessions[sessionId] === live
                        },
                    ) {
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
                                sessionOpenFailures = sessionFailuresAfterRuntimeState(
                                    product.sessionOpenFailures,
                                    sessionId,
                                    runtime.state,
                                ),
                                sessionTitles = when {
                                    runtime.titleOverride == null -> product.sessionTitles
                                    runtime.titleOverride.isBlank() -> product.sessionTitles - sessionId
                                    else -> product.sessionTitles + (sessionId to runtime.titleOverride)
                                },
                            )
                        }
                    }
                    if (!accepted) return@collect
                    if (runtime.state is RemoteSessionState.HistoryGap) {
                        live.launchRecovery { recoverLiveHistoryGap(sessionId, live) }
                    }
                }
            },
            tokenRefreshJob = scope.launch(start = CoroutineStart.LAZY) {
                while (isActive && isCurrentRealtimeOwner(realtime.owner)) {
                    delay(LIVE_TOKEN_REFRESH_INTERVAL_MILLIS)
                    runCatching { freshAccessToken(force = false, expectedRuntimeOwner = realtime.owner) }
                        .onFailure { error ->
                            if (isActive && isCurrentRealtimeOwner(realtime.owner)) {
                                publishFailure(error)
                                delay(LIVE_TOKEN_REFRESH_RETRY_MILLIS)
                            }
                        }
                }
            },
        )
        var exactRuntime = true
        var duplicateSession = false
        var installed = false
        val installationCurrent = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = {
                !closed &&
                    authState != null &&
                    accountScope == realtime.owner.accountScope &&
                    storedIdentity === stored
            },
        ) {
            installed = synchronized(liveSessionLock) {
                when {
                    realtimeMultiplexerOwner !== realtime.owner ||
                        realtimeMultiplexer !== realtime.multiplexer ||
                        directCoordinator !== realtime.directCoordinator -> {
                        exactRuntime = false
                        false
                    }
                    liveSessions.containsKey(sessionId) -> {
                        duplicateSession = true
                        false
                    }
                    else -> {
                        liveSessions[sessionId] = live
                        true
                    }
                }
            }
        }
        if (!installationCurrent || !exactRuntime || !installed) {
            live.closeAndJoin()
            if (!installationCurrent || !exactRuntime) {
                throw IllegalStateException("Authentication lifecycle changed")
            }
            check(duplicateSession)
            return
        }
        val bootstrapPublished = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = {
                !closed &&
                    realtimeMultiplexerOwner === realtime.owner &&
                    realtimeMultiplexer === realtime.multiplexer &&
                    directCoordinator === realtime.directCoordinator &&
                    storedIdentity === stored &&
                    liveSessions[sessionId] === live
            },
        ) {
            observeBootstrap(sessionId, restoredHistory)
        }
        if (!bootstrapPublished) {
            val removed = synchronized(liveSessionLock) { liveSessions.remove(sessionId, live) }
            if (removed) live.closeAndJoin()
            throw IllegalStateException("Authentication lifecycle changed")
        }
        try {
            check(live.start()) { "Live session was retired before startup" }
            newlyLaunchedSessionIds.remove(sessionId)
        } catch (error: CancellationException) {
            val removed = synchronized(liveSessionLock) { liveSessions.remove(sessionId, live) }
            if (removed) withContext(NonCancellable) { live.closeAndJoin() }
            throw error
        } catch (error: Throwable) {
            val removed = synchronized(liveSessionLock) { liveSessions.remove(sessionId, live) }
            if (removed) {
                live.closeAndJoin()
            } else {
                // An authentication transition owns exact retirement now. Do
                // not publish its expected startup cancellation as a failure
                // into the successor account snapshot.
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            val classified = classifySessionOpenException(error, SessionFailureStage.REALTIME)
            if (!observeBootstrapFailureIfCurrent(
                    sessionId,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        }
    }

    /**
     * Restores an ended session only while its authoritative Windows host is
     * online. The temporary endpoint advertises a permanent zero replay
     * window and is retired before the renderer is published, so the result
     * is a local read-only view rather than a resurrected live session.
     */
    private suspend fun openEndedHostHistorySessionLocked(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        authentication: AuthenticationStateCapture,
    ) {
        require(session.state == SessionState.EXITED) { "Read-only history requires an ended session" }
        if (PcCapabilityIds.HOST_HISTORY_V1 !in pc.capabilities) {
            throw sessionOpenException(SessionOpenFailureCode.SESSION_ENDED)
        }
        if (!pc.online) throw sessionOpenException(SessionOpenFailureCode.SESSION_OFFLINE)
        val authenticationGeneration = authentication.authenticationGeneration
        freshAccessToken(expectedGeneration = authenticationGeneration)
        val stored = authenticationLifecycle.capture { currentGeneration ->
            check(
                !closed &&
                    currentGeneration == authenticationGeneration &&
                    accountScope == authentication.accountScope &&
                    authState != null
            ) { "Authentication lifecycle changed" }
            identity(authentication.accountScope)
        }
        authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState != null && accountScope == authentication.accountScope },
        ) {
            mutableSnapshot.update { current ->
                current.copy(
                    sessionOpenFailures = current.sessionOpenFailures - session.id,
                    sessionStates = current.sessionStates + (
                        session.id to RemoteSessionState.Exited(
                            lastAckedSequence = current.liveAcknowledgements[session.id]?.toLong() ?: 0L,
                            exitCode = null,
                        )
                    ),
                )
            }
        }.also { check(it) { "Authentication lifecycle changed" } }

        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1)),
        )
        val sessionDispatcher = Dispatchers.Default.limitedParallelism(1)
        val realtime = deviceRealtimeRuntime(stored, authenticationGeneration)
        realtime.multiplexer.warmConnect()
        var historyClient: ProductionRealtimeSessionClient? = null
        val restored = try {
            historyClient = createHostHistoryBootstrapClient(
                session = session,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                engine = engine,
                realtime = realtime,
                sessionDispatcher = sessionDispatcher,
            ).also(ProductionRealtimeSessionClient::start)
            withContext(sessionDispatcher) {
                restoreLiveHistory(
                    session = session,
                    pc = pc,
                    pcDevice = pcDevice,
                    stored = stored,
                    engine = engine,
                    authenticationGeneration = authenticationGeneration,
                    hostHistoryClient = historyClient,
                )
            }
        } catch (error: CancellationException) {
            withContext(NonCancellable) {
                try {
                    historyClient?.closeGracefully()
                } catch (_: Throwable) {
                    // Preserve the cancellation and dispose the renderer below.
                }
            }
            engine.dispose()
            throw error
        } catch (error: Throwable) {
            withContext(NonCancellable) {
                try {
                    historyClient?.closeGracefully()
                } catch (_: Throwable) {
                    // Preserve the bootstrap failure and dispose the renderer below.
                }
            }
            engine.dispose()
            val classified = classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_PREPARE)
            if (!observeBootstrapFailureIfCurrent(
                    session.id,
                    classified.failure,
                    authenticationGeneration,
                    realtime,
                    stored,
                )
            ) {
                throw IllegalStateException("Authentication lifecycle changed", error)
            }
            throw classified
        } finally {
            historyClient?.let { client ->
                withContext(NonCancellable) { client.closeGracefully() }
            }
        }
        engine.setReadOnly(true)

        var duplicate = false
        var installed = false
        val installationCurrent = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = {
                !closed &&
                    realtimeMultiplexerOwner === realtime.owner &&
                    realtimeMultiplexer === realtime.multiplexer &&
                    directCoordinator === realtime.directCoordinator &&
                    storedIdentity === stored &&
                    mutableSnapshot.value.sessions.any {
                        it.id == session.id && it.state == SessionState.EXITED
                    }
            },
        ) {
            synchronized(liveSessionLock) {
                if (readOnlySessionEngines.containsKey(session.id)) {
                    duplicate = true
                } else {
                    readOnlySessionEngines[session.id] = engine
                    installed = true
                }
            }
        }
        if (!installationCurrent || !installed) {
            engine.dispose()
            if (duplicate) return
            throw IllegalStateException("Authentication lifecycle changed")
        }
        val published = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = {
                !closed &&
                    realtimeMultiplexerOwner === realtime.owner &&
                    realtimeMultiplexer === realtime.multiplexer &&
                    directCoordinator === realtime.directCoordinator &&
                    storedIdentity === stored &&
                    readOnlySessionEngines[session.id] === engine
            },
        ) {
            observeBootstrap(session.id, restored)
            mutableSnapshot.update { current ->
                current.copy(
                    sessionStates = current.sessionStates + (
                        session.id to RemoteSessionState.Exited(
                            lastAckedSequence = restored.durableAcknowledgement.toLong(),
                            exitCode = null,
                        )
                    ),
                )
            }
        }
        if (!published) {
            val removed = synchronized(liveSessionLock) {
                readOnlySessionEngines.remove(session.id, engine)
            }
            if (removed) engine.dispose()
            throw IllegalStateException("Authentication lifecycle changed")
        }
    }

    private fun createHostHistoryBootstrapClient(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: NativeTerminalEngine,
        realtime: RealtimeRuntimeClaim,
        disposeEngineOnClose: Boolean = false,
        sessionDispatcher: CoroutineDispatcher = Dispatchers.Default.limitedParallelism(1),
    ): ProductionRealtimeSessionClient {
        // Bootstrap history is a bounded Relay-only consumer. Registering a peer
        // here would admit temporary history frames into the Direct data gateway.
        val transport = realtime.multiplexer.register(session.id)
        return try {
            ProductionRealtimeSessionClient(
                engine = engine,
                config = ProductionRealtimeSessionConfig(
                    rootUrl = currentServerUrl(),
                    tokenProvider = AccessTokenProvider { requireRealtimeAccessToken(realtime.owner) },
                    androidDeviceId = stored.deviceId,
                    pcDeviceId = pc.deviceId,
                    sessionId = session.id,
                    keyEpoch = pc.keyEpoch,
                    pcExchangePublicKey = pcDevice.exchangePublicKey.copyOf(),
                ),
                identity = stored.identity,
                parentScope = scope,
                transport = transport,
                httpClient = http,
                allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
                inputSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveInputSequenceRange(session.id)
                },
                envelopeSequenceProvider = ReservedInputSequenceProvider {
                    sessionStore.reserveEnvelopeSequenceRange(session.id)
                },
                acknowledgementObserver = { rendererGeneration, sequence ->
                    DurableAckCommit(sequence, rendererGeneration)
                },
                acknowledgementResetObserver = { rendererGeneration ->
                    DurableAckCommit(0, rendererGeneration + 1)
                },
                holdLiveReplayForHostHistory = true,
                hostHistoryOnlyMode = true,
                disposeEngineOnClose = disposeEngineOnClose,
                sessionDispatcher = sessionDispatcher,
            )
        } catch (error: Throwable) {
            transport.close()
            throw error
        }
    }

    override suspend fun closeSessionView(sessionId: UUID): Unit = sessionLifecycleLanes.withLock(sessionId) {
        check(!closed) { "Repository is closed" }
        newlyLaunchedSessionIds.remove(sessionId)
        val live = synchronized(liveSessionLock) { liveSessions.remove(sessionId) }
        live?.closeAndJoin()
        val readOnly = synchronized(liveSessionLock) { readOnlySessionEngines.remove(sessionId) }
        readOnly?.dispose()
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

    override fun terminalEngine(sessionId: UUID): TerminalViewportEngine? =
        liveSessions[sessionId]?.engine ?: readOnlySessionEngines[sessionId]

    override fun historyEngine(historyObjectId: UUID): TerminalViewportEngine? = historyEngines[historyObjectId]

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
            sessionLifecycleLanes.withLock(key.sessionId) { loadTranscriptPage(key) }
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
            contiguousAppliedSequence = realtime.contiguousAppliedSequence,
            duplicateEventCount = realtime.duplicateEventCount,
            historyGapCount = realtime.historyGapCount,
            durableAck = realtime.durableAck,
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
        val pc = product.pcs.firstOrNull { it.id == session.pcId }
            ?: error("The selected Windows PC is unavailable")
        require(session.online) { "Only an online session can be closed" }

        // Opening is idempotent and registers this exact UUID on the shared
        // device socket. The client then waits for its own SUBSCRIBE route to
        // be ready before sending the E2EE termination request.
        openSession(sessionId)
        val live = liveSessions[sessionId] ?: error("The selected session is no longer available")
        live.client.requestTermination(
            awaitAcknowledgement = PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities,
        )

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
                sessionLifecycleLanes.withLock(sessionId) {
                    deleteClosedSessionAfterTermination(
                        deleteRemote = { api.delete("/v1/sessions/$sessionId") },
                        deleteLocal = { deleteLocalSession(sessionId) },
                    )
                }
            }
        }
        appendActivity(
            context.getString(R.string.activity_session_closed),
            context.getString(R.string.activity_session_closed_detail),
            Severity.INFO,
        )
    }

    override suspend fun deleteSession(sessionId: UUID) {
        check(!closed) { "Repository is closed" }
        withAuthenticatedApi { api, authenticationGeneration ->
            // A refresh that began before this request must publish before the
            // deletion, never resurrect the deleted session afterward. The
            // lifecycle lock also prevents an open/replay from installing a
            // new client or history engine between server success and cleanup.
            refreshStateLock.withLock {
                sessionLifecycleLanes.withLock(sessionId) {
                    val product = mutableSnapshot.value
                    val session = product.sessions.firstOrNull { it.id == sessionId }
                        ?: error("The selected session is unavailable")
                    val pc = product.pcs.firstOrNull { it.id == session.pcId }
                        ?: error("The selected Windows PC is unavailable")
                    require(!session.online || session.state == SessionState.EXITED) {
                        "Close an online session before deleting it"
                    }
                    if (PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities) {
                        val pcDevice = product.devices.firstOrNull {
                            it.id == pc.deviceId &&
                                it.type.isPcAgent() &&
                                it.trustState == DeviceTrustState.TRUSTED
                        } ?: error("Windows PC 不受信任 / The selected Windows PC is not trusted")
                        check(pc.online) {
                            "Windows PC 已离线，本地主机会话历史未删除 / The Windows PC is offline; its local session history was not deleted"
                        }
                        check(pcDevice.exchangePublicKey.size == 32) {
                            "Windows PC 缺少有效加密密钥 / The selected Windows PC has no valid encryption key"
                        }
                        deleteHostHistorySessionAfterTermination(
                            terminateRemote = {
                                terminateInactiveHostHistorySession(
                                    session = session,
                                    pc = pc,
                                    pcDevice = pcDevice,
                                    stored = identity(),
                                    authenticationGeneration = authenticationGeneration,
                                )
                            },
                            deleteRemote = { api.delete("/v1/sessions/$sessionId") },
                            deleteLocal = { deleteLocalSession(sessionId) },
                        )
                    } else {
                        deleteSessionAfterRemoteSuccess(
                            deleteRemote = { api.delete("/v1/sessions/$sessionId") },
                            deleteLocal = { deleteLocalSession(sessionId) },
                        )
                    }
                }
            }
        }
    }

    /**
     * Explicit deletion of a host-history session is acknowledged by the
     * authoritative Windows Agent before the server catalog or Android cache
     * is touched. The temporary endpoint advertises a permanent zero window,
     * so no terminal replay can enter the throwaway renderer.
     */
    private suspend fun terminateInactiveHostHistorySession(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authenticationGeneration: Long,
    ) {
        require(session.pcId == pc.id) { "Session and Windows PC do not match" }
        withTemporaryHostHistoryClient(
            session = session,
            pc = pc,
            pcDevice = pcDevice,
            stored = stored,
            authenticationGeneration = authenticationGeneration,
        ) { client ->
            client.requestTermination(awaitAcknowledgement = true)
        }
    }

    private suspend fun <T> withTemporaryHostHistoryClient(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authenticationGeneration: Long,
        action: suspend (ProductionRealtimeSessionClient) -> T,
    ): T {
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1)),
        )
        val realtime = deviceRealtimeRuntime(stored, authenticationGeneration)
        realtime.multiplexer.warmConnect()
        var client: ProductionRealtimeSessionClient? = null
        try {
            val activeClient = createHostHistoryBootstrapClient(
                session = session,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                engine = engine,
                realtime = realtime,
                disposeEngineOnClose = true,
            ).also(ProductionRealtimeSessionClient::start)
            client = activeClient
            return action(activeClient)
        } finally {
            withContext(NonCancellable) {
                if (client != null) client.closeGracefully() else engine.dispose()
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
        check(live.engine.submitText(text + '\r')) { "Request control before sending input" }
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
            val base = if (parsed.shift) Character.toLowerCase(printable) else printable
            val shifted = if (parsed.shift) terminalShiftedCodePoint(base) else null
            live.engine.submitCodePoint(
                base,
                controlDown = parsed.control,
                altDown = parsed.alt,
                shiftDown = parsed.shift,
                shiftedCodePoint = shifted,
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

    override suspend fun replayHistory(entry: HistoryEntry): Unit = sessionLifecycleLanes.withLock(entry.sessionId) {
        require(entry.historyObject.kind == HistoryObject.Kind.CHUNK) { "This history object cannot be replayed by this client" }
        val download = historyDownloads[entry.historyObject.id] ?: error("Refresh history before replaying this object")
        val product = mutableSnapshot.value
        val session = product.sessions.firstOrNull { it.id == entry.sessionId } ?: error("The history session is unavailable")
        val pc = product.pcs.firstOrNull { it.id == session.pcId } ?: error("The history PC is unavailable")
        val pcDevice = product.devices.firstOrNull {
            it.id == pc.deviceId && it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED
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
        appendActivity(context.getString(R.string.activity_history_replayed), entry.sessionLabel, Severity.SUCCESS)
    }

    override suspend fun browseRemoteDirectory(
        pcId: UUID,
        pcName: String,
        path: String,
        offset: Long?,
        query: String,
    ) {
        require(path.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES && '\u0000' !in path) {
            "The Windows path is invalid"
        }
        require(offset == null || offset >= 0L) { "The directory offset is invalid" }
        require(isValidRemoteFileSearchQuery(query)) { "The directory search query is invalid" }
        if (offset != null) {
            val current = mutableSnapshot.value.remoteFileBrowser
            require(current.pcId == pcId && current.currentPath == path && current.query == query &&
                current.nextOffset == offset
            ) { "The directory continuation does not match the active search" }
        }
        val requestId = UUID.randomUUID()
        mutableSnapshot.update { current ->
            current.copy(
                remoteFileBrowser = reduceRemoteFileBrowser(
                    current.remoteFileBrowser,
                    RemoteFileBrowserEvent.LoadStarted(requestId, pcId, pcName, path, offset, query),
                ),
            )
        }
        try {
            val operation = createRemoteFileOperation(
                pcId = pcId,
                operation = REMOTE_FILE_OPERATION_LIST,
                path = path,
                offset = offset,
                query = query,
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
            val canResume = record.status == RemoteDownloadStatus.FAILED && record.operationId != null
            record.copy(
                status = if (canResume) RemoteDownloadStatus.RESUMING else RemoteDownloadStatus.PREPARING,
                targetUri = targetUri.toString(),
                operationId = if (canResume) record.operationId else null,
                totalBytes = if (canResume) record.totalBytes else null,
                downloadedBytes = if (canResume) record.downloadedBytes else 0L,
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
            runCatching { fileTransferCache.cleanup(operationId) }
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
        record.operationId?.let { runCatching { fileTransferCache.cleanup(it) } }
        updateRemoteDownloads { records -> removeRemoteDownloadRecord(records, recordId) }
    }

    override suspend fun openRemoteShell(
        pcId: UUID,
        path: String,
        command: TerminalCommandCapability,
    ): UUID {
        require(path.isNotBlank() && path.encodeToByteArray().size <= REMOTE_FILE_PATH_MAX_BYTES && '\u0000' !in path) {
            "The Windows directory is invalid"
        }
        mutableSnapshot.update { current ->
            current.copy(
                remoteFileBrowser = reduceRemoteFileBrowser(
                    current.remoteFileBrowser,
                    RemoteFileBrowserEvent.ShellStarted(command.id),
                ),
            )
        }
        try {
            val operation = createRemoteFileOperation(
                pcId = pcId,
                operation = REMOTE_FILE_OPERATION_OPEN_TERMINAL,
                path = path,
                terminalCommand = command,
            )
            val completed = awaitRemoteFileOperation(pcId, operation)
            val result = completed.result ?: error("The Windows PC returned no terminal session")
            val sessionId = UUID.fromString(result.requiredString("session_id"))
            val resultCommandId = result["terminal_command_id"]?.jsonPrimitive?.contentOrNull
                ?: result.requiredString("shell_type")
            require(resultCommandId == command.id) {
                "The Windows PC returned a different terminal command"
            }
            val ready = withTimeoutOrNull(REMOTE_SHELL_CATALOG_TIMEOUT_MILLIS) {
                while (true) {
                    withAuthenticatedApi { api, generation -> pollSessionCatalogWithApi(api, generation) }
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
        if (remoteDownloadJobs.putIfAbsent(recordId, job) != null) {
            logRemoteDownloadStage(recordId, null, "job_registration", "duplicate")
            job.cancel()
            return
        }
        logRemoteDownloadStage(recordId, null, "job_registration", "accepted")
        job.invokeOnCompletion { remoteDownloadJobs.remove(recordId, job) }
        job.start()
    }

    private fun resumeInterruptedRemoteDownloads() {
        if (closed || interruptedRemoteDownloads.isEmpty()) return
        mutableSnapshot.value.remoteDownloads
            .filter { it.status.isActive }
            .forEach { launchRemoteDownload(it.id) }
    }

    private suspend fun performRemoteDownload(recordId: UUID) {
        val initial = mutableSnapshot.value.remoteDownloads.firstOrNull { it.id == recordId }
            ?: return
        val destination = Uri.parse(initial.targetUri)
        var operationId = initial.operationId
        var initialTransferDescriptor: RealtimeFileTransferDescriptor? = null
        logRemoteDownloadStage(recordId, operationId, "job", "started")
        try {
            if (operationId != null) {
                val existing = runRemoteDownloadPreflight(recordId, { operationId }, "resume_lookup") {
                    getRemoteFileOperation(
                        pcId = initial.pcId,
                        operationId = operationId!!,
                        httpClient = remoteFilePreflightHttp,
                        diagnostic = remoteDownloadApiDiagnostic(recordId, { operationId }, "resume_lookup"),
                    )
                }
                when (existing.status) {
                    REMOTE_FILE_STATUS_PENDING, REMOTE_FILE_STATUS_PROCESSING -> {
                        initialTransferDescriptor = existing.transfer?.let { transfer ->
                            realtimeFileTransferDescriptorFromJson(
                                transfer,
                                expectedTransferId = operationId!!,
                                expectedDirectoryZip = initial.directoryZip,
                            )
                        }
                    }
                    REMOTE_FILE_STATUS_SUCCEEDED -> {
                        updateRemoteDownload(recordId) { record -> record.copy(
                            status = RemoteDownloadStatus.COMPLETED,
                            downloadedBytes = record.totalBytes ?: record.downloadedBytes,
                            updatedAt = Instant.now(),
                            error = null,
                        ) }
                        runCatching { fileTransferCache.cleanup(operationId!!) }
                        releaseRemoteDocumentPermission(destination)
                        return
                    }
                    else -> {
                        runCatching { fileTransferCache.cleanup(operationId!!) }
                        operationId = null
                        initialTransferDescriptor = null
                    }
                }
            }
            if (operationId == null) {
                val operation = runRemoteDownloadPreflight(recordId, { operationId }, "operation_create") {
                    createRemoteFileOperation(
                        pcId = initial.pcId,
                        operation = REMOTE_FILE_OPERATION_DOWNLOAD,
                        path = initial.remotePath,
                        httpClient = remoteFilePreflightHttp,
                        diagnostic = remoteDownloadApiDiagnostic(recordId, { operationId }, "operation_create"),
                        onCreated = { created ->
                            operationId = created.id
                            // Persist inside the POST response boundary so a
                            // process stop cannot strand an unknown server task.
                            updateRemoteDownload(recordId) { record ->
                                if (!record.status.isActive) record else record.copy(
                                    operationId = created.id,
                                    updatedAt = Instant.now(),
                                )
                            }
                        }
                    )
                }
                operationId = operation.id
            }
            val transferId = requireNotNull(operationId)
            logRemoteDownloadStage(recordId, transferId, "peer_key_lookup", "started")
            val snapshot = mutableSnapshot.value
            val pc = snapshot.pcs.firstOrNull { it.id == initial.pcId }
                ?: error("The Windows PC is unavailable")
            val pcDevice = snapshot.devices.firstOrNull {
                it.id == pc.deviceId && it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED
            } ?: error("The Windows PC identity is unavailable")
            val stored = identity()
            logRemoteDownloadStage(recordId, transferId, "peer_key_lookup", "completed")
            val transferClient = ProductionFileTransferClient(
                rootUrl = currentServerUrl(),
                accessToken = realtimeAccessToken::requireCurrent,
                localDeviceId = stored.deviceId,
                identity = stored.identity,
                pcDeviceId = pc.deviceId,
                pcExchangePublicKey = pcDevice.exchangePublicKey,
                keyEpoch = pc.keyEpoch,
                transferId = transferId,
                cache = fileTransferCache,
                httpClient = http,
                allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
            )
            var lastPersistedAt = 0L
            logRemoteDownloadStage(recordId, transferId, "websocket_receive", "started")
            val descriptor = transferClient.receive(
                destination = destination,
                expectedDirectoryZip = initial.directoryZip,
                initialDescriptor = initialTransferDescriptor,
                onOffer = { offer, committed ->
                    updateRemoteDownload(recordId) { record ->
                        if (!record.status.isActive) record else record.copy(
                            filename = offer.filename,
                            directoryZip = offer.directoryZip,
                            status = if (committed > 0L) {
                                RemoteDownloadStatus.RESUMING
                            } else {
                                RemoteDownloadStatus.DOWNLOADING
                            },
                            totalBytes = offer.sizeBytes,
                            downloadedBytes = committed,
                            updatedAt = Instant.now(),
                            error = null,
                        )
                    }
                },
                onProgress = { committed ->
                    val nowMillis = System.currentTimeMillis()
                    val total = mutableSnapshot.value.remoteDownloads
                        .firstOrNull { it.id == recordId }?.totalBytes
                    val persist = committed == total ||
                        nowMillis - lastPersistedAt >= REMOTE_DOWNLOAD_PERSIST_INTERVAL_MILLIS
                    if (persist) lastPersistedAt = nowMillis
                    updateRemoteDownload(recordId, persist = persist) { record ->
                        if (!record.status.isActive) record else record.copy(
                            status = RemoteDownloadStatus.DOWNLOADING,
                            downloadedBytes = committed,
                            updatedAt = Instant.now(),
                        )
                    }
                },
                onVerifying = {
                    updateRemoteDownload(recordId) { record ->
                        if (!record.status.isActive) record else record.copy(
                            status = RemoteDownloadStatus.VERIFYING,
                            downloadedBytes = record.totalBytes ?: record.downloadedBytes,
                            updatedAt = Instant.now(),
                        )
                    }
                },
                completionConfirmed = {
                    val current = getRemoteFileOperation(initial.pcId, transferId)
                    when (current.status) {
                        REMOTE_FILE_STATUS_SUCCEEDED -> true
                        REMOTE_FILE_STATUS_PENDING, REMOTE_FILE_STATUS_PROCESSING -> false
                        REMOTE_FILE_STATUS_FAILED -> throw RemoteFileTransferException(
                            current.error ?: "Windows file transfer failed / Windows 文件传输失败。",
                            retryable = false,
                        )
                        REMOTE_FILE_STATUS_CANCELLED -> throw RemoteFileTransferException(
                            "File transfer was cancelled / 文件传输已取消。",
                            retryable = false,
                        )
                        REMOTE_FILE_STATUS_EXPIRED -> throw RemoteFileTransferException(
                            "File transfer expired / 文件传输已过期。",
                            retryable = false,
                        )
                        else -> error("Unknown file-transfer state")
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
            runCatching { fileTransferCache.cleanup(transferId) }
            releaseRemoteDocumentPermission(destination)
            logRemoteDownloadStage(recordId, transferId, "download", "completed")
        } catch (error: CancellationException) {
            logRemoteDownloadStage(recordId, operationId, "download", "cancelled", error)
            if (!closed) {
                cancelRemoteFileOperationBestEffort(recordId, initial.pcId)
                operationId?.let { runCatching { fileTransferCache.cleanup(it) } }
                discardIncompleteRemoteDocument(destination)
                releaseRemoteDocumentPermission(destination)
                updateRemoteDownload(recordId) { record ->
                    if (record.status == RemoteDownloadStatus.COMPLETED) record else record.copy(
                        status = RemoteDownloadStatus.CANCELLED,
                        updatedAt = Instant.now(),
                        error = null,
                    )
                }
            }
            throw error
        } catch (error: Throwable) {
            logRemoteDownloadStage(recordId, operationId, "download", "failed", error)
            val terminalTransferFailure = error is RemoteFileTransferException && !error.retryable
            if (terminalTransferFailure) {
                operationId?.let { failedOperationId ->
                    cancelKnownRemoteFileOperationBestEffort(initial.pcId, failedOperationId)
                    runCatching { fileTransferCache.cleanup(failedOperationId) }
                }
                discardIncompleteRemoteDocument(destination)
                releaseRemoteDocumentPermission(destination)
            }
            updateRemoteDownload(recordId) { record ->
                if (record.status == RemoteDownloadStatus.COMPLETED) record else record.copy(
                    status = RemoteDownloadStatus.FAILED,
                    operationId = if (terminalTransferFailure) null else record.operationId,
                    totalBytes = if (terminalTransferFailure) null else record.totalBytes,
                    downloadedBytes = if (terminalTransferFailure) 0L else record.downloadedBytes,
                    updatedAt = Instant.now(),
                    error = remoteFileErrorMessage(error),
                )
            }
        }
    }

    private suspend fun discardIncompleteRemoteDocument(destination: Uri) {
        withContext(NonCancellable + Dispatchers.IO) {
            cleanupIncompleteRemoteDocument(context.contentResolver, destination)
        }
    }

    private suspend fun releaseRemoteDocumentPermission(destination: Uri) {
        withContext(NonCancellable + Dispatchers.IO) {
            releaseRemoteDocumentPermission(context.contentResolver, destination)
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
        query: String = "",
        terminalCommand: TerminalCommandCapability? = null,
        onCreated: (RemoteFileOperationResponse) -> Unit = {},
        httpClient: OkHttpClient = http,
        diagnostic: ((String) -> Unit)? = null,
    ): RemoteFileOperationResponse {
        // One stable key belongs to this create attempt. If transport retry is
        // added below this boundary, it must reuse this value with the same body.
        val idempotencyKey = UUID.randomUUID().toString()
        return withApi(httpClient = httpClient, diagnostic = diagnostic) { api ->
        remoteFileOperationResponseFromJson(
            api.post(
                remoteFileOperationPath(pcId),
                buildJsonObject {
                    put("operation", operation)
                    put("path", path)
                    offset?.let { put("offset", it) }
                    if (query.isNotEmpty()) put("query", query)
                    terminalCommand?.let { command ->
                        if (command.source == "legacy") put("shell_type", command.id)
                        else put("terminal_command_id", command.id)
                    }
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

    private suspend fun getRemoteFileOperation(
        pcId: UUID,
        operationId: UUID,
        httpClient: OkHttpClient = http,
        diagnostic: ((String) -> Unit)? = null,
    ): RemoteFileOperationResponse = withApi(httpClient = httpClient, diagnostic = diagnostic) { api ->
        remoteFileOperationResponseFromJson(
            api.get(remoteFileOperationPath(pcId, operationId)).jsonObject,
        ).also { response ->
            require(response.id == operationId) {
                "The server returned a different Windows file operation"
            }
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
        is RemoteFileTransferException -> error.message ?: context.getString(R.string.remote_file_error_generic)
        is TermLinkApiException -> context.getString(R.string.remote_file_error_server, error.status)
        is java.io.IOException -> context.getString(R.string.remote_file_error_connection)
        else -> context.getString(R.string.remote_file_error_generic)
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
        val readOnly = synchronized(liveSessionLock) { readOnlySessionEngines.remove(sessionId) }
        readOnly?.dispose()
        historyObjectIds.forEach { objectId ->
            historyEngines.remove(objectId)?.dispose()
            historyDownloads.remove(objectId)
        }
        historyRepository.deleteSession(sessionId)
        mutableSnapshot.update { snapshot -> snapshot.withoutSession(sessionId) }
    }

    override suspend fun refresh() {
        check(!closed) { "Repository is closed" }
        val authenticationGeneration = authenticationLifecycle.capture()
        authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
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
        }
        try {
            withAuthenticatedApi(expectedGeneration = authenticationGeneration) { api, generation ->
                refreshWithApi(api, generation)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                publishFailure(error)
            }
            throw error
        }
    }

    private suspend fun refreshWithApi(
        api: ProductionApiClient,
        authenticationGeneration: Long,
    ): Unit = refreshStateLock.withLock {
        requireCurrentAuthentication(authenticationGeneration)
        val self = resolveSameAccountDevice(api, ProductResponseParser.self(api.get("/v1/devices/self").jsonObject))
        if (sameAccountDeviceAction(self.trustState) == SameAccountDeviceAction.BLOCK_REVOKED) {
            authenticationStateMutationLock.withLock {
                requireCurrentAuthentication(authenticationGeneration)
                clearAccessControlledState(self, authenticationGeneration)
            }
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
        authenticationStateMutationLock.withLock {
            requireCurrentAuthentication(authenticationGeneration)
        // Reject an epoch rollback or same-epoch public-key substitution before
        // any untrusted metadata reaches Room. Refreshes are serialized so a
        // slower response cannot overwrite a newer validated snapshot.
        validateAuthoritativePcKeyTransitions(devices, pcs)
        closeLiveSessionsForChangedPcIdentities(pcs, sessions)
        validateLiveSessionKeys(devices, pcs, sessions)
        historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
        sessions.forEach { historyRepository.upsertSession(it) }
        val storedIdentity = identity()
        val ownDeviceId = storedIdentity.deviceId
        val directoryTitles = decryptSessionDirectoryTitles(devices, pcs, sessions, storedIdentity)
        val refreshedDownloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val historyEntries = sessions.flatMap { session ->
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@flatMap emptyList()
            // Host-owned history has no server catalog. Snapshot, transcript,
            // and event pages are selected only over the active E2EE session.
            if (PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities) return@flatMap emptyList()
            val response = try {
                api.get(sessionChunkHistoryRequestPath(session.id)).jsonObject
            } catch (error: TermLinkApiException) {
                if (legacyHistoryCatalogUnavailable(error)) return@flatMap emptyList()
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
                        device.type.isPcAgent() &&
                        device.trustState == DeviceTrustState.TRUSTED
                }
            }
            .mapTo(mutableSetOf(), PcSummary::id)
        closeReadOnlySessionsExcept(retainedReadOnlySessionIds(sessions, pcs, trustedPcIds))
        closeLiveSessionsExcept(retainedLiveSessionIds(sessions, trustedPcIds))
        val runtimeSessions = sessions.map { session ->
            liveSessions[session.id]?.let { live ->
                projectLiveSessionSummary(session, live.client.sessionState.value, live.engine.authoritativeSize.value)
            } ?: session
        }
        val retainedSessionIds = sessions.mapTo(mutableSetOf(), SessionSummary::id)
        val published = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState != null },
        ) {
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
                    pcCarrierStates = retainAuthoritativePcCarrierStates(
                        current.pcCarrierStates,
                        devices,
                        pcs,
                    ),
                    sessionCarrierStates = retainAuthoritativeSessionCarrierStates(
                        current.sessionCarrierStates,
                        devices,
                        pcs,
                        runtimeSessions,
                    ),
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
            // refreshStateLock still excludes narrow key refresh here, so this
            // cannot erase a newer transition fence.
            sessionAuthorityTransitionFences.clear()
        }
        check(published) { "Authentication lifecycle changed" }
            resumeInterruptedRemoteDownloads()
            refreshDirectAuthorizedPeers(authenticationGeneration, storedIdentity, devices, pcs)
            scope.launch { billing.syncCatalog(catalogPlans) }
        }
    }

    private suspend fun refreshWithApiPublished(
        api: ProductionApiClient,
        authenticationGeneration: Long,
    ) {
        try {
            refreshWithApi(api, authenticationGeneration)
        } catch (error: Throwable) {
            authenticationLifecycle.runIfCurrent(authenticationGeneration, predicate = { !closed }) {
                publishFailure(error)
            }
            throw error
        }
    }

    private suspend fun pollSessionCatalogWithApi(
        api: ProductionApiClient,
        authenticationGeneration: Long,
    ) {
        val readyToReopen = refreshStateLock.withLock {
            requireCurrentAuthentication(authenticationGeneration)
            val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
            val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
            val catalogSessions = pcs.flatMap { pc ->
                ProductResponseParser.itemObjects(
                    api.get("/v1/pcs/${pc.id}/sessions?include_ended=true").jsonObject,
                ).map { it.toSession() }
            }.filterNot { it.id in deletedSessionIds }
            authenticationStateMutationLock.withLock {
                requireCurrentAuthentication(authenticationGeneration)
                validateAuthoritativePcKeyTransitions(devices, pcs)
                closeLiveSessionsForChangedPcIdentities(pcs, catalogSessions)
                validateLiveSessionKeys(devices, pcs, catalogSessions)
                historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
                catalogSessions.forEach { historyRepository.upsertSession(it) }
                val directoryTitles = decryptSessionDirectoryTitles(devices, pcs, catalogSessions, identity())
                rotateLiveSessionKeys(devices, pcs, catalogSessions)

                val trustedPcIds = pcs.asSequence()
                    .filter { pc ->
                        devices.any { device ->
                            device.id == pc.deviceId &&
                                device.type.isPcAgent() &&
                                device.trustState == DeviceTrustState.TRUSTED
                        }
                    }
                    .mapTo(hashSetOf(), PcSummary::id)
                closeReadOnlySessionsExcept(retainedReadOnlySessionIds(catalogSessions, pcs, trustedPcIds))
                closeLiveSessionsExcept(retainedLiveSessionIds(catalogSessions, trustedPcIds))

                val runtimeSessions = catalogSessions.map { session ->
                    liveSessions[session.id]?.let { live ->
                        projectLiveSessionSummary(session, live.client.sessionState.value, live.engine.authoritativeSize.value)
                    } ?: session
                }
                val previous = mutableSnapshot.value
                val ready = historyGapSessionsReadyForAutoReopen(previous, runtimeSessions)
                val published = authenticationLifecycle.runIfCurrent(
                    authenticationGeneration,
                    predicate = { !closed && authState != null },
                ) {
                    mutableSnapshot.update { current ->
                        val reconciled = reconcileSessionCatalog(
                            current = current.copy(devices = devices),
                            pcs = pcs,
                            sessions = runtimeSessions,
                            deletedSessionIds = deletedSessionIds,
                        )
                        reconciled.copy(
                            pcCarrierStates = retainAuthoritativePcCarrierStates(
                                reconciled.pcCarrierStates,
                                devices,
                                pcs,
                            ),
                            sessionCarrierStates = retainAuthoritativeSessionCarrierStates(
                                reconciled.sessionCarrierStates,
                                devices,
                                pcs,
                                runtimeSessions,
                            ),
                            sessionTitles = reconcileSessionDirectoryTitles(
                                reconciled.sessionTitles,
                                runtimeSessions,
                                directoryTitles,
                                currentLiveSessionTitles(),
                            ),
                            lastSyncedAt = Instant.now(),
                        )
                    }
                    sessionAuthorityTransitionFences.clear()
                }
                check(published) { "Authentication lifecycle changed" }
                refreshDirectAuthorizedPeers(authenticationGeneration, identity(), devices, pcs)
                ready
            }
        }
        readyToReopen.forEach { sessionId ->
            scope.launch {
                try {
                    openSession(sessionId)
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Throwable) {
                    // The normal bootstrap path records the classified failure; wait for more output.
                }
            }
        }
    }

    private suspend fun <T> withApi(
        httpClient: OkHttpClient = http,
        diagnostic: ((String) -> Unit)? = null,
        block: suspend (ProductionApiClient) -> T,
    ): T = withAuthenticatedApi(httpClient = httpClient, diagnostic = diagnostic) { api, _ -> block(api) }

    private suspend fun <T> withAuthenticatedApi(
        httpClient: OkHttpClient = http,
        expectedGeneration: Long? = null,
        diagnostic: ((String) -> Unit)? = null,
        block: suspend (ProductionApiClient, Long) -> T,
    ): T = withContext(Dispatchers.IO) {
        val root = currentServerUrl()
        val initial = captureAuthenticationState(expectedGeneration = expectedGeneration)
        diagnostic?.invoke("access_token_started")
        val token = freshAccessToken(expectedGeneration = initial.authenticationGeneration)
        diagnostic?.invoke("access_token_completed")
        val refreshed = captureAuthenticationState(expectedGeneration = initial.authenticationGeneration)
        check(refreshed.accountScope == initial.accountScope) { "Authentication account scope changed" }
        val identity = identity(initial.accountScope)
        ProductionApiClient(
            root,
            AccessTokenProvider { token },
            identity.deviceId,
            identity.identity,
            httpClient,
            allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
        ).use { api ->
            diagnostic?.invoke("device_registration_started")
            ensureRegistered(api, identity)
            diagnostic?.invoke("device_registration_completed")
            requireCurrentAuthentication(initial.authenticationGeneration)
            diagnostic?.invoke("api_request_started")
            block(api, initial.authenticationGeneration).also {
                requireCurrentAuthentication(initial.authenticationGeneration)
                diagnostic?.invoke("api_request_completed")
            }
        }
    }

    private suspend fun <T> runRemoteDownloadPreflight(
        recordId: UUID,
        operationId: () -> UUID?,
        stage: String,
        block: suspend () -> T,
    ): T {
        logRemoteDownloadStage(recordId, operationId(), stage, "started")
        return try {
            boundedRemoteDownloadPreflight(REMOTE_DOWNLOAD_PREFLIGHT_TIMEOUT_MILLIS, block).also {
                logRemoteDownloadStage(recordId, operationId(), stage, "completed")
            }
        } catch (error: CancellationException) {
            logRemoteDownloadStage(recordId, operationId(), stage, "cancelled", error)
            throw error
        } catch (error: Throwable) {
            logRemoteDownloadStage(recordId, operationId(), stage, "failed", error)
            throw error
        }
    }

    private fun remoteDownloadApiDiagnostic(
        recordId: UUID,
        operationId: () -> UUID?,
        stage: String,
    ): (String) -> Unit = { phase ->
        logRemoteDownloadStage(recordId, operationId(), "${stage}_$phase", "observed")
    }

    private fun logRemoteDownloadStage(
        recordId: UUID,
        operationId: UUID?,
        stage: String,
        result: String,
        error: Throwable? = null,
    ) {
        val failure = error?.let { it::class.java.simpleName.take(80) } ?: "none"
        Log.i(
            REMOTE_DOWNLOAD_LOG_TAG,
            "record_id=$recordId operation_id=${operationId ?: "none"} " +
                "stage=$stage result=$result failure=$failure",
        )
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
                if (!pcDevice.type.isPcAgent() ||
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
                .getOrElse { error ->
                    if (error is TermLinkApiException && error.code == "DEVICE_REVOKED") {
                        throw RevokedDeviceIdentityException()
                    }
                    throw IllegalStateException("Device registration conflict could not be verified")
                }
            check(current.id == identity.deviceId) { "Device registration conflict could not be verified" }
        }
    }

    private suspend fun resetRevokedDeviceIdentity(scope: String) =
        authenticationStateMutationLock.withLock {
            val retired = storedIdentity
            storedIdentity = null
            keyVault.clear(scope)
            retired?.identity?.destroy()
        }

    private fun identity(expectedScope: String? = null): DeviceKeyVault.StoredIdentity {
        val scope = accountScope ?: error("OIDC account scope is unavailable")
        check(expectedScope == null || scope == expectedScope) { "Authentication account scope changed" }
        return storedIdentity ?: keyVault.loadOrCreate(scope).also { storedIdentity = it }
    }

    private suspend fun freshAccessToken(
        force: Boolean = false,
        expectedRuntimeOwner: RealtimeAuthenticationOwner? = null,
        expectedGeneration: Long? = null,
    ): String = if (expectedRuntimeOwner == null && expectedGeneration == null) {
        tokenRefresh.freshAccessToken(force)
    } else {
        tokenRefresh.serially {
            requestFreshAccessToken(force, expectedRuntimeOwner, expectedGeneration).also { value ->
                require(value.isNotBlank()) { "OIDC access token is unavailable" }
            }
        }
    }

    private suspend fun requestFreshAccessToken(
        force: Boolean,
        expectedRuntimeOwner: RealtimeAuthenticationOwner? = null,
        expectedGeneration: Long? = null,
    ): String {
        val captured = captureAuthenticationState(
            expectedGeneration = expectedRuntimeOwner?.authenticationGeneration ?: expectedGeneration,
            expectedRuntimeOwner = expectedRuntimeOwner,
        )
        val generation = captured.authenticationGeneration
        val state = captured.authState
        val expectedAccountScope = captured.accountScope
        // Persisted AuthState intentionally omits AuthorizationResponse and all
        // original token-request credentials.  AppAuth's refresh helper relies
        // on that discarded request metadata, so every flow (native login and
        // embedded PKCE fallback) refreshes through the same bounded, no-cookie
        // token transport instead.
        try {
            val configuration = ensureOidcConfiguration()
            val current = state.accessToken
            if (!force && !state.needsTokenRefresh && !current.isNullOrBlank()) {
                val published = authenticationLifecycle.runIfCurrent(
                    captured = generation,
                    predicate = {
                        !closed &&
                            authState === state &&
                            accountScope == expectedAccountScope &&
                            (expectedRuntimeOwner == null || realtimeMultiplexerOwner === expectedRuntimeOwner)
                    },
                ) {
                    realtimeAccessToken.publish(current)
                }
                check(published) { "Authentication lifecycle changed" }
                return current
            }
            val refreshToken = state.refreshToken ?: error("OIDC refresh token is unavailable")
            val payload = withContext(Dispatchers.IO) { nativeOidc.refresh(configuration, refreshToken) }
            val refreshedSubject = OidcJwtClaims.accessTokenSubject(
                payload.accessToken,
                configuration.issuer,
                configuration.clientId,
            )
            check(expectedAccountScope == AccountScope.canonical(configuration.issuer, refreshedSubject)) {
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
            val clean = CredentialFreeAuthState.sanitize(updated)
            val committed = authenticationLifecycle.runIfCurrent(
                captured = generation,
                predicate = {
                    !closed &&
                        authState === state &&
                        accountScope == expectedAccountScope &&
                        (expectedRuntimeOwner == null || realtimeMultiplexerOwner === expectedRuntimeOwner)
                },
            ) {
                authState = clean
                sessionStore.saveAuthState(clean)
                realtimeAccessToken.publish(payload.accessToken)
            }
            check(committed) { "Authentication lifecycle changed" }
            return payload.accessToken
        } catch (error: Throwable) {
            if (isAuthenticationRefreshRejection(error) &&
                isCurrentAuthentication(generation, state, expectedRuntimeOwner)
            ) {
                invalidateAuthentication(
                    authenticationGeneration = generation,
                    expectedAuthState = state,
                    expectedRuntimeOwner = expectedRuntimeOwner,
                )
            }
            throw error
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

    private fun isCurrentAuthentication(
        generation: Long,
        state: AuthState,
        expectedRuntimeOwner: RealtimeAuthenticationOwner?,
    ): Boolean = isCurrentAuthentication(generation, state) &&
        (expectedRuntimeOwner == null || realtimeMultiplexerOwner === expectedRuntimeOwner)

    private fun captureAuthenticationState(
        expectedGeneration: Long? = null,
        expectedRuntimeOwner: RealtimeAuthenticationOwner? = null,
    ): AuthenticationStateCapture = authenticationLifecycle.capture { currentGeneration ->
        check(!closed && (expectedGeneration == null || currentGeneration == expectedGeneration)) {
            "Authentication lifecycle changed"
        }
        check(expectedRuntimeOwner == null || realtimeMultiplexerOwner === expectedRuntimeOwner) {
            "Realtime authentication owner changed"
        }
        AuthenticationStateCapture(
            authenticationGeneration = currentGeneration,
            authState = authState ?: error("Sign in first"),
            accountScope = accountScope,
        )
    }

    private suspend fun ensureOidcConfiguration(): NativeOidcConfiguration {
        oidcConfiguration?.let { return it }
        val root = currentServerUrl()
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
        accountLogin: String,
        authenticationGeneration: Long,
    ) {
        requireCurrentAuthentication(authenticationGeneration)
        val nextScope = AccountScope.canonical(configuration.issuer, subject)
        establishAccountScope(nextScope, authenticationGeneration)
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
        val sanitized = CredentialFreeAuthState.sanitize(clean)
        val committed = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState == null && accountScope == nextScope },
        ) {
            authState = sanitized
            sessionStore.saveAuthState(sanitized)
            sessionStore.accountLogin = accountLogin
            mutableSnapshot.value = mutableSnapshot.value.copy(
                signedIn = true,
                configured = true,
                accountLabel = accountLogin,
                authenticationState = ProductAuthenticationState.AUTHENTICATED,
                connectionState = ProductConnectionState.CONNECTING,
                connectionError = null,
                lastSyncedAt = null,
            )
        }
        check(committed) { "Authentication lifecycle changed" }
    }

    private suspend fun bootstrapAuthenticatedSession(authenticationGeneration: Long) {
        val captured = captureAuthenticationState(expectedGeneration = authenticationGeneration)
        val configuration = ensureOidcConfiguration()
        val accessToken = captured.authState.accessToken ?: error("OIDC access token is unavailable")
        val subject = OidcJwtClaims.accessTokenSubject(accessToken, configuration.issuer, configuration.clientId)
        val nextScope = AccountScope.canonical(configuration.issuer, subject)
        authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState === captured.authState },
        ) {
            if (sessionStore.accountLogin == null) {
                sessionStore.accountLogin = OidcJwtClaims.displayIdentifier(accessToken)
            }
        }
        establishAccountScope(nextScope, authenticationGeneration)
        try {
            withAuthenticatedApi(expectedGeneration = authenticationGeneration) { api, generation ->
                // withApi registers this account-scoped key first. Continue directly
                // into the authoritative refresh; no enrollment confirmation step is
                // part of the same-account Android flow.
                refreshWithApi(api, generation)
            }
        } catch (_: RevokedDeviceIdentityException) {
            // Re-authentication is the explicit user action that allows an
            // installation whose device key was revoked to enroll a fresh key.
            closeHistoryEngines()
            clearRoomCache()
            billing.reset()
            resetRevokedDeviceIdentity(nextScope)
            withAuthenticatedApi(expectedGeneration = authenticationGeneration) { api, generation ->
                refreshWithApi(api, generation)
            }
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
        authenticationGeneration: Long,
    ) = authenticationStateMutationLock.withLock {
        authenticationAccountScopeLock.withLock {
            val previousScope = authenticationLifecycle.capture { currentGeneration ->
                check(!closed && currentGeneration == authenticationGeneration) {
                    "Authentication lifecycle changed"
                }
                accountScope
            }
            val accountChanged = previousScope != null && previousScope != nextScope
            if (accountChanged) {
                requireCurrentAuthentication(authenticationGeneration)
                closeHistoryEngines()
                clearRoomCache()
                requireCurrentAuthentication(authenticationGeneration)
                billing.reset()
            }
            var retiredIdentity: DeviceKeyVault.StoredIdentity? = null
            val committed = authenticationLifecycle.runIfCurrent(
                authenticationGeneration,
                predicate = { !closed && (accountScope == previousScope || accountScope == nextScope) },
            ) {
                if (accountScope != nextScope) {
                    retiredIdentity = storedIdentity
                    storedIdentity = null
                }
                accountScope = nextScope
                sessionStore.accountScope = nextScope
            }
            retiredIdentity?.identity?.destroy()
            check(committed) { "Authentication lifecycle changed" }
        }
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
     * while this authenticated runtime is marked revoked; a later explicit
     * sign-in rotates it only when the server rejects its registration.
     */
    private suspend fun clearAccessControlledState(
        self: DeviceSummary,
        authenticationGeneration: Long,
    ) = accessStateLock.withLock {
        val retired = synchronized(liveSessionLock) {
            val owner = realtimeMultiplexerOwner
            if (owner?.authenticationGeneration == authenticationGeneration) {
                detachRealtimeRuntimeLocked()
            } else {
                null
            }
        }
        if (retired != null) closeRetiredRealtimeRuntimeAndJoin(retired)
        requireCurrentAuthentication(authenticationGeneration)
        closeHistoryEngines()
        billing.reset()
        clearRoomCache()
        val published = authenticationLifecycle.runIfCurrent(
            authenticationGeneration,
            predicate = { !closed && authState != null },
        ) {
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
                pcCarrierStates = emptyMap(),
                sessionCarrierStates = emptyMap(),
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
        check(published) { "Authentication lifecycle changed" }
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
        val expiredMessage = context.getString(R.string.failure_authentication_expired)
        if (shouldPreserveAuthenticationExpiredFailure(mutableSnapshot.value, expiredMessage)) return
        val message = safeFailureMessage(error)
        mutableSnapshot.value = mutableSnapshot.value.copy(
            connectionState = ProductConnectionState.ERROR,
            connectionError = message,
        )
        appendActivity(context.getString(R.string.activity_connection_failed), message, Severity.ERROR)
    }

    private fun safeFailureMessage(error: Throwable): String = when (error) {
        is NativeOidcException -> when (error.reason) {
            NativeOidcFailure.INVALID_CREDENTIALS -> context.getString(R.string.failure_invalid_credentials)
            NativeOidcFailure.ACCOUNT_SETUP_REQUIRED -> context.getString(R.string.sign_in_account_setup_required)
            NativeOidcFailure.DISCOVERY_FAILED,
            NativeOidcFailure.NETWORK -> context.getString(R.string.failure_identity_network)
            NativeOidcFailure.INVALID_CONFIGURATION -> context.getString(R.string.failure_identity_configuration)
            NativeOidcFailure.TOKEN_REQUEST_FAILED -> context.getString(R.string.failure_identity_rejected)
        }
        is TermLinkApiException -> context.getString(R.string.failure_server_http, error.status)
        else -> context.getString(R.string.failure_server_request)
    }

    private fun sanitizeActivityDetail(value: String): String =
        sensitiveIdentifierPattern.replace(value, "session")

    private suspend fun closeReadOnlySessionsExcept(retained: Set<UUID>) {
        val candidates = readOnlySessionEngines.toMap().filterKeys { it !in retained }
        candidates.forEach { (sessionId, expected) ->
            sessionLifecycleLanes.withLock(sessionId) {
                val removed = synchronized(liveSessionLock) {
                    readOnlySessionEngines.remove(sessionId, expected)
                }
                if (removed) expected.dispose()
            }
        }
        val visible = retainedRuntimeMetadataSessionIds(
            authoritativeSessions = mutableSnapshot.value.sessions,
            readOnlySessionIds = readOnlySessionEngines.keys,
            liveSessionIds = liveSessions.keys,
        )
        mutableSnapshot.update { current ->
            current.copy(
                sessionStates = current.sessionStates.filterKeys(visible::contains),
                sessionOpenFailures = current.sessionOpenFailures.filterKeys(visible::contains),
            )
        }
    }

    private suspend fun closeLiveSessionsExcept(retained: Set<UUID>) {
        val candidates = liveSessions.toMap().filterKeys { it !in retained }
        sessionAuthorityTransitionFences.addAll(
            mutableSnapshot.value.sessions.asSequence()
                .map(SessionSummary::id)
                .filterNot(retained::contains)
                .toSet() + candidates.keys,
        )
        candidates.forEach { (sessionId, expected) ->
            sessionLifecycleLanes.withLock(sessionId) {
                retireExactIncarnationAndThen(
                    expected = expected,
                    removeExact = { incarnation ->
                        synchronized(liveSessionLock) {
                            liveSessions.remove(sessionId, incarnation)
                        }
                    },
                    closeAndJoin = LiveProductSession::closeAndJoin,
                )
            }
        }
        val visible = retained + readOnlySessionEngines.keys
        mutableSnapshot.value = mutableSnapshot.value.copy(
            sessionStates = mutableSnapshot.value.sessionStates.filterKeys(visible::contains),
            sessionOpenFailures = mutableSnapshot.value.sessionOpenFailures.filterKeys(visible::contains),
            sessionTitles = mutableSnapshot.value.sessionTitles.filterKeys(visible::contains),
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
        sessionAuthorityTransitionFences.addAll(affected)
        affected.forEach { sessionId ->
            sessionLifecycleLanes.withLock(sessionId) {
                val expected = liveSessions[sessionId]
                if (expected != null) {
                    retireExactIncarnationAndThen(
                        expected = expected,
                        removeExact = { incarnation ->
                            synchronized(liveSessionLock) {
                                liveSessions.remove(sessionId, incarnation)
                            }
                        },
                        closeAndJoin = LiveProductSession::closeAndJoin,
                    )
                }
                val readOnly = synchronized(liveSessionLock) {
                    readOnlySessionEngines.remove(sessionId)
                }
                readOnly?.dispose()
                mutableSnapshot.update { snapshot ->
                    snapshot.copy(
                        sessionStates = snapshot.sessionStates - sessionId,
                        sessionOpenFailures = snapshot.sessionOpenFailures - sessionId,
                        sessionTitles = snapshot.sessionTitles - sessionId,
                    )
                }
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
                    it.type.isPcAgent() &&
                    it.trustState == DeviceTrustState.TRUSTED
            } ?: return@forEach
            live.client.rotateKeyMaterial(pc.keyEpoch, device.exchangePublicKey)
        }
    }

    /**
     * Publishes only trusted, online PC metadata to the device Direct coordinator. Pairwise secrets
     * are freshly derived, copied by the runtime, and zeroed here before returning.
     */
    private fun refreshDirectAuthorizedPeers(
        authenticationGeneration: Long,
        stored: DeviceKeyVault.StoredIdentity,
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
    ) {
        val coordinator = synchronized(liveSessionLock) {
            val owner = realtimeMultiplexerOwner
            directCoordinator.takeIf {
                owner?.authenticationGeneration == authenticationGeneration &&
                    owner.storedIdentity === stored
            }
        } ?: return
        val devicesById = devices.associateBy(DeviceSummary::id)
        val replacements = pcs.asSequence()
            .filter(PcSummary::online)
            .sortedBy(PcSummary::deviceId)
            .mapNotNull { pc ->
                val device = devicesById[pc.deviceId]?.takeIf {
                    it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED &&
                        it.exchangePublicKey.size == 32 && it.signingPublicKey.size == 32
                } ?: return@mapNotNull null
                val pairwise = runCatching {
                    SessionCrypto.derivePairwiseKey(
                        stored.identity.exchange.privateKey,
                        device.exchangePublicKey,
                        pc.deviceId,
                        stored.deviceId,
                        pc.keyEpoch,
                    )
                }.getOrNull() ?: return@mapNotNull null
                try {
                    ProductionDirectAuthorizedPeer(
                        deviceId = pc.deviceId,
                        keyEpoch = pc.keyEpoch,
                        keyId = device.keyId,
                        pairwiseKey = pairwise,
                        signingPublicKey = device.signingPublicKey,
                    )
                } finally {
                    pairwise.fill(0)
                }
            }
            .toList()
        try {
            coordinator.replaceAuthorizedPeers(authenticationGeneration, replacements)
        } finally {
            replacements.forEach(ProductionDirectAuthorizedPeer::close)
        }
    }

    /**
     * Unknown realtime key IDs trigger this narrow authoritative refresh. It
     * intentionally avoids account, billing, and history endpoints so an
     * unavailable history object store cannot block an epoch transition.
     */
    private suspend fun refreshLiveKeyMetadata(): Unit = keyMetadataRefreshLock.withLock {
        withAuthenticatedApi { api, authenticationGeneration ->
            val stored = identity()
            val devices = ProductResponseParser.itemObjects(api.get("/v1/devices").jsonObject).map { it.toDevice() }
            val pcs = ProductResponseParser.itemObjects(api.get("/v1/pcs").jsonObject).map { it.toPc() }
            val sessions = mutableSnapshot.value.sessions
            // Lock order is keyMetadata -> refreshState -> one session lane.
            // No session lane is held while acquiring either outer lock.
            refreshStateLock.withLock {
                validateAuthoritativePcKeyTransitions(devices, pcs)
                closeLiveSessionsForChangedPcIdentities(pcs, sessions)
                validateLiveSessionKeys(devices, pcs, sessions)
                // Persist the authoritative epoch/public-key pair before any live
                // session can start encrypting or decrypting with the new epoch.
                historyRepository.persistAuthoritativeKeyMetadata(devices, pcs)
                rotateLiveSessionKeys(devices, pcs, sessions)
                val trustedPcIds = pcs.asSequence()
                    .filter { pc ->
                        devices.any { device ->
                            device.id == pc.deviceId &&
                                device.type.isPcAgent() &&
                                device.trustState == DeviceTrustState.TRUSTED
                        }
                    }
                    .mapTo(mutableSetOf(), PcSummary::id)
                closeReadOnlySessionsExcept(retainedReadOnlySessionIds(sessions, pcs, trustedPcIds))
                closeLiveSessionsExcept(retainedLiveSessionIds(sessions, trustedPcIds))
            }
            refreshDirectAuthorizedPeers(authenticationGeneration, stored, devices, pcs)
        }
    }

    private suspend fun validateAuthoritativePcKeyTransitions(
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
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
    }

    private fun validateLiveSessionKeys(
        devices: List<DeviceSummary>,
        pcs: List<PcSummary>,
        sessions: List<SessionSummary>,
    ) {
        liveSessions.toMap().forEach { (sessionId, live) ->
            val session = sessions.firstOrNull { it.id == sessionId } ?: return@forEach
            val pc = pcs.firstOrNull { it.id == session.pcId } ?: return@forEach
            val device = devices.firstOrNull {
                it.id == pc.deviceId &&
                    it.type.isPcAgent() &&
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

    private fun updateDirectAppliedCursorIfCurrent(
        realtime: RealtimeRuntimeClaim,
        peerDeviceId: UUID,
        sessionId: UUID,
        sequence: Long,
    ): Boolean {
        val exactRuntime = synchronized(liveSessionLock) {
            directRuntimeProjectionClaimMatches(
                currentOwner = realtimeMultiplexerOwner,
                currentMultiplexer = realtimeMultiplexer,
                currentCoordinator = directCoordinator,
                expectedOwner = realtime.owner,
                expectedMultiplexer = realtime.multiplexer,
                expectedCoordinator = realtime.directCoordinator,
            )
        }
        return exactRuntime && realtime.directCoordinator.updateAppliedCursor(
            peerDeviceId,
            sessionId,
            sequence,
        )
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

    private fun observeBootstrapFailureIfCurrent(
        sessionId: UUID,
        failure: SessionOpenFailure,
        authenticationGeneration: Long,
        realtime: RealtimeRuntimeClaim,
        stored: DeviceKeyVault.StoredIdentity,
    ): Boolean = authenticationLifecycle.runIfCurrent(
        authenticationGeneration,
        predicate = {
            !closed &&
                realtimeMultiplexerOwner === realtime.owner &&
                realtimeMultiplexer === realtime.multiplexer &&
                directCoordinator === realtime.directCoordinator &&
                storedIdentity === stored
        },
    ) {
        observeBootstrapFailure(sessionId, failure)
    }

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

    private suspend fun loadTranscriptPage(
        key: TranscriptLoadKey,
        remainingAnchorRebases: Int = MAX_TRANSCRIPT_ANCHOR_REBASES,
    ) {
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
                it.id == pc.deviceId && it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED
            } ?: error("The transcript PC is not a trusted device")
            val authentication = captureAuthenticationState()
            val stored = identity(authentication.accountScope)
            if (PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities) {
                val live = liveSessions[key.sessionId]
                if (live != null) {
                    loadHostTranscriptPage(
                        key = key,
                        selected = selected,
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        client = live.client,
                        remainingAnchorRebases = remainingAnchorRebases,
                    )
                } else {
                    check(session.state == SessionState.EXITED &&
                        readOnlySessionEngines.containsKey(key.sessionId)
                    ) { "The transcript session has no authenticated read-only renderer" }
                    check(pc.online) { "The Windows PC is offline" }
                    withTemporaryHostHistoryClient(
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        authenticationGeneration = authentication.authenticationGeneration,
                    ) { client ->
                        loadHostTranscriptPage(
                            key = key,
                            selected = selected,
                            session = session,
                            pc = pc,
                            pcDevice = pcDevice,
                            stored = stored,
                            client = client,
                            remainingAnchorRebases = remainingAnchorRebases,
                        )
                    }
                }
                return
            }
            // Always fetch the authenticated selector before consulting Room.
            // The ciphertext may be reusable, but the server response owns the
            // fixed-revision continuation cursor and publication readiness.
            val selection = awaitExpectedTranscriptPage {
                latestTranscriptPage(key, session, stored.deviceId, pc.keyEpoch)
            }
            if (selection === TranscriptSelection.Pending || selection === TranscriptSelection.Complete) {
                val liveAck = mutableSnapshot.value.liveAcknowledgements[key.sessionId] ?: 0u
                val newerAnchor = if (remainingAnchorRebases > 0) {
                    latestPublishedTranscriptAnchor(
                        key = key,
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        durableLiveAcknowledgement = liveAck,
                    )
                } else {
                    null
                }
                val rebased = newerAnchor?.let { anchor ->
                    rebaseTranscriptViewportToNewerAnchor(
                        viewport = selected,
                        newSnapshotSequence = anchor.snapshotSequence,
                        newInitialBeforeLineId = anchor.beforeLineId,
                        durableLiveAcknowledgement = liveAck,
                    )
                }
                if (rebased != null) {
                    mutableSnapshot.update { currentProduct ->
                        val current = currentProduct.transcriptViewports[key.sessionId]
                        if (!current.matches(key)) currentProduct else currentProduct.copy(
                            transcriptViewports = currentProduct.transcriptViewports +
                                (key.sessionId to rebased),
                        )
                    }
                    val installed = mutableSnapshot.value.transcriptViewports[key.sessionId]
                    if (installed?.snapshotAnchorSequence == rebased.snapshotAnchorSequence &&
                        installed.viewportGeneration == rebased.viewportGeneration
                    ) {
                        loadTranscriptPage(
                            TranscriptLoadKey(
                                sessionId = key.sessionId,
                                snapshotSequence = rebased.snapshotAnchorSequence,
                                beforeLineId = requireNotNull(rebased.nextBeforeLineId),
                                cursor = null,
                                viewportGeneration = rebased.viewportGeneration,
                            ),
                            remainingAnchorRebases - 1,
                        )
                        return
                    }
                }
                if (selection === TranscriptSelection.Complete) {
                    finishEmptyTranscriptPage(key)
                    return
                }
                error("The expected terminal transcript page is not available yet")
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
                            // ProductActivity maps this stable diagnostic to
                            // localized terminal_history_unavailable text.
                            error = TRANSCRIPT_PAGE_LOAD_FAILED_CODE,
                        )
                    ),
                )
            }
            throw error
        }
    }

    private suspend fun loadHostTranscriptPage(
        key: TranscriptLoadKey,
        selected: TranscriptViewportState,
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        client: ProductionRealtimeSessionClient,
        remainingAnchorRebases: Int,
    ) {
        val value = awaitHostHistoryReady {
            client.requestHostTranscriptPage(key.snapshotSequence, key.beforeLineId)
        }
        if (value == null) {
            val liveAck = mutableSnapshot.value.liveAcknowledgements[key.sessionId] ?: 0u
            val newerAnchor = if (remainingAnchorRebases > 0) {
                latestHostTranscriptAnchor(key, session, pc, pcDevice, stored, client, liveAck)
            } else {
                null
            }
            val rebased = newerAnchor?.let { anchor ->
                rebaseTranscriptViewportToNewerAnchor(
                    viewport = selected,
                    newSnapshotSequence = anchor.snapshotSequence,
                    newInitialBeforeLineId = anchor.beforeLineId,
                    durableLiveAcknowledgement = liveAck,
                )
            }
            if (rebased == null) {
                finishEmptyTranscriptPage(key)
                return
            }
            mutableSnapshot.update { product ->
                val current = product.transcriptViewports[key.sessionId]
                if (!current.matches(key)) product else product.copy(
                    transcriptViewports = product.transcriptViewports + (key.sessionId to rebased),
                )
            }
            val installed = mutableSnapshot.value.transcriptViewports[key.sessionId]
            if (installed?.snapshotAnchorSequence == rebased.snapshotAnchorSequence &&
                installed.viewportGeneration == rebased.viewportGeneration
            ) {
                loadHostTranscriptPage(
                    key = TranscriptLoadKey(
                        sessionId = key.sessionId,
                        snapshotSequence = rebased.snapshotAnchorSequence,
                        beforeLineId = requireNotNull(rebased.nextBeforeLineId),
                        cursor = null,
                        viewportGeneration = rebased.viewportGeneration,
                    ),
                    selected = installed,
                    session = session,
                    pc = pc,
                    pcDevice = pcDevice,
                    stored = stored,
                    client = client,
                    remainingAnchorRebases = remainingAnchorRebases - 1,
                )
            }
            return
        }
        try {
            require(value.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_TRANSCRIPT) {
                "Host history object is not a transcript page"
            }
            require(value.snapshotSequence == key.snapshotSequence &&
                value.requestBeforeLineId == key.beforeLineId
            ) { "Host transcript selector changed its fixed cursor" }
            require(value.firstLineId > 0u && value.lastLineId in value.firstLineId until key.beforeLineId) {
                "Host transcript line range is invalid"
            }
            require(value.hasMoreBefore == (value.nextBeforeLineId > 0u)) {
                "Host transcript continuation metadata is inconsistent"
            }
            val metadata = TranscriptPageMetadata(
                id = value.objectId,
                sessionId = session.id,
                snapshotSequence = value.snapshotSequence,
                requestBeforeLineId = value.requestBeforeLineId,
                firstLineId = value.firstLineId,
                lastLineId = value.lastLineId,
                objectKey = hostHistoryObjectKey(session.id, value.objectId),
                keyId = value.keyId,
                ciphertextSha256 = value.ciphertextSha256.copyOf(),
                sizeBytes = value.sealedObject.size.toLong(),
                formatName = value.formatName,
                formatVersion = value.formatVersion,
            )
            validateTranscriptDescriptorNotLoaded(selected, metadata)
            val cached = historyRepository.transcriptPageForCursor(
                key.sessionId,
                key.snapshotSequence,
                key.beforeLineId,
            )
            val encrypted = reuseOrLoadTranscriptCiphertext(cached, metadata) {
                historyRepository.putTranscriptPage(
                    sessionId = key.sessionId,
                    snapshotSequence = key.snapshotSequence,
                    requestBeforeLineId = key.beforeLineId,
                    firstLineId = metadata.firstLineId,
                    lastLineId = metadata.lastLineId,
                    objectKey = metadata.objectKey,
                    keyId = metadata.keyId,
                    ciphertext = value.sealedObject,
                    id = metadata.id,
                    ciphertextSha256 = metadata.ciphertextSha256,
                )
                EncryptedTranscriptPage(metadata.copyDigest(), value.sealedObject.copyOf())
            }
            val page = try {
                openTranscriptPage(session, pc, pcDevice, stored, encrypted)
            } finally {
                encrypted.ciphertext.fill(0)
            }
            val nextCursor = value.nextBeforeLineId.takeIf { value.hasMoreBefore }
                ?.let { "host-history-v1:${value.snapshotSequence}:$it" }
            mutableSnapshot.update { product ->
                val current = product.transcriptViewports[key.sessionId]
                if (!current.matches(key)) product else product.copy(
                    transcriptViewports = product.transcriptViewports + (
                        key.sessionId to mergeTranscriptPage(
                            viewport = requireNotNull(current),
                            requestedBeforeLineId = key.beforeLineId,
                            requestedCursor = key.cursor,
                            objectId = metadata.id,
                            outerHasMore = value.hasMoreBefore,
                            outerNextCursor = nextCursor,
                            page = page,
                        )
                    ),
                )
            }
        } finally {
            value.destroy()
        }
    }

    private suspend fun latestHostTranscriptAnchor(
        key: TranscriptLoadKey,
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        client: ProductionRealtimeSessionClient,
        durableLiveAcknowledgement: ULong,
    ): TranscriptAnchor? {
        val value = awaitHostHistoryReady { client.requestLatestHostSnapshot() } ?: return null
        try {
            if (value.snapshotSequence <= key.snapshotSequence ||
                value.snapshotSequence > durableLiveAcknowledgement
            ) return null
            val descriptor = cacheHostSnapshot(session, pc, stored, value)
            return openTerminalTranscriptAnchor(
                session = session,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                descriptor = descriptor,
                sealedObject = value.sealedObject,
            )
        } finally {
            value.destroy()
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

    private suspend fun latestPublishedTranscriptAnchor(
        key: TranscriptLoadKey,
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        durableLiveAcknowledgement: ULong,
    ): TranscriptAnchor? {
        val descriptor = latestTerminalSnapshot(session, stored, pc.keyEpoch)?.snapshot ?: return null
        val metadata = descriptor.metadata
        val sequence = requireNotNull(metadata.snapshotSequence)
        if (sequence <= key.snapshotSequence || sequence > durableLiveAcknowledgement) return null
        val cached = historyRepository.getHistoryObject(metadata.id)?.takeIf { value ->
            value.metadata.kind == HistoryObject.Kind.SNAPSHOT &&
                MessageDigest.isEqual(value.metadata.ciphertextSha256, metadata.ciphertextSha256)
        }
        val sealedObject = cached?.ciphertext ?: withContext(Dispatchers.IO) {
            downloadHistory(descriptor.download, metadata)
        }
        try {
            if (cached == null) {
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
            }
            return openTerminalTranscriptAnchor(
                session = session,
                pc = pc,
                pcDevice = pcDevice,
                stored = stored,
                descriptor = descriptor,
                sealedObject = sealedObject,
            )
        } finally {
            sealedObject.fill(0)
        }
    }

    private suspend fun openTerminalTranscriptAnchor(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        descriptor: SnapshotDownloadDescriptor,
        sealedObject: ByteArray,
    ): TranscriptAnchor = withContext(Dispatchers.Default) {
        val metadata = descriptor.metadata
        val sequence = requireNotNull(metadata.snapshotSequence)
        require(metadata.kind == HistoryObject.Kind.SNAPSHOT)
        require(metadata.formatName == HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT && metadata.formatVersion == 2)
        val keyEpoch = requireNotNull(terminalCoreSnapshotKeyEpoch(stored.deviceId, metadata.keyId))
        require(keyEpoch <= pc.keyEpoch)
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            keyEpoch,
        )
        try {
            val opened = TerminalSnapshotCrypto.openTerminalCoreSnapshot(
                pairwiseKey = pairwise,
                sealedObject = sealedObject,
                expectedSessionId = session.id,
                expectedPcDeviceId = pc.deviceId,
                expectedRecipientDeviceId = stored.deviceId,
                expectedKeyEpoch = keyEpoch,
                expectedKeyId = metadata.keyId,
                expectedSnapshotSequence = sequence,
                expectedColumns = descriptor.columns,
                expectedRows = descriptor.rows,
            )
            try {
                TranscriptAnchor(sequence, opened.initialTranscriptBeforeLineId)
            } finally {
                opened.checkpoint.fill(0)
            }
        } finally {
            pairwise.fill(0)
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
            detachRealtimeRuntimeLocked()
        }
        mutableSnapshot.value = mutableSnapshot.value.copy(
            pcCarrierStates = emptyMap(),
            sessionCarrierStates = emptyMap(),
            sessionStates = emptyMap(),
            sessionOpenFailures = emptyMap(),
            sessionTitles = emptyMap(),
        )
        return retired
    }

    /** Caller must hold [liveSessionLock]. */
    private fun detachRealtimeRuntimeLocked(): RetiredRealtimeRuntime {
        realtimeMultiplexerOwner = null
        return RetiredRealtimeRuntime(
            sessions = liveSessions.values.toList().also { liveSessions.clear() },
            readOnlyEngines = readOnlySessionEngines.values.toList().also { readOnlySessionEngines.clear() },
            directCoordinator = directCoordinator.also { directCoordinator = null },
            directCarrierProjectionJob = directCarrierProjectionJob.also { directCarrierProjectionJob = null },
            multiplexer = realtimeMultiplexer.also { realtimeMultiplexer = null },
        )
    }

    private fun closeRetiredRealtimeRuntime(retired: RetiredRealtimeRuntime) {
        retired.directCarrierProjectionJob?.cancel()
        retired.directCoordinator?.close()
        retired.sessions.forEach(LiveProductSession::close)
        retired.readOnlyEngines.forEach(NativeTerminalEngine::dispose)
        retired.multiplexer?.close()
    }

    private suspend fun closeRetiredRealtimeRuntimeAndJoin(retired: RetiredRealtimeRuntime) {
        withContext(NonCancellable) {
            var firstFailure: Throwable? = null
            // Signal every independent runtime before awaiting any one of them.
            // A black-holed Direct control-plane request must not delay Relay
            // socket closure or keep live sessions attached during auth switch.
            try {
                retired.directCarrierProjectionJob?.cancel()
                retired.directCoordinator?.close()
            } catch (error: Throwable) {
                firstFailure = firstFailure ?: error
            }
            retired.sessions.forEach { session ->
                try {
                    session.close()
                } catch (error: Throwable) {
                    firstFailure = firstFailure ?: error
                }
            }
            retired.readOnlyEngines.forEach { engine ->
                try {
                    engine.dispose()
                } catch (error: Throwable) {
                    firstFailure = firstFailure ?: error
                }
            }
            try {
                retired.multiplexer?.close()
            } catch (error: Throwable) {
                firstFailure = firstFailure ?: error
            }
            try {
                retired.directCoordinator?.closeAndJoin()
            } catch (error: Throwable) {
                firstFailure = firstFailure ?: error
            }
            try {
                retired.directCarrierProjectionJob?.join()
            } catch (error: Throwable) {
                firstFailure = firstFailure ?: error
            }
            retired.sessions.forEach { session ->
                try {
                    session.closeAndJoin()
                } catch (error: Throwable) {
                    firstFailure = firstFailure ?: error
                }
            }
            firstFailure?.let { throw it }
        }
    }

    private fun detachRealtimeRuntimeIfOwner(
        expectedOwner: RealtimeAuthenticationOwner,
    ): RetiredRealtimeRuntime? = synchronized(liveSessionLock) {
        if (realtimeMultiplexerOwner !== expectedOwner) null else detachRealtimeRuntimeLocked()
    }

    private fun isCurrentRealtimeOwner(owner: RealtimeAuthenticationOwner): Boolean =
        authenticationLifecycle.runIfCurrent(
            owner.authenticationGeneration,
            predicate = {
                !closed &&
                    authState != null &&
                    accountScope == owner.accountScope &&
                    storedIdentity === owner.storedIdentity &&
                    realtimeMultiplexer != null &&
                    directCoordinator != null &&
                    authenticationRuntimeClaimMatches(
                        currentOwner = realtimeMultiplexerOwner,
                        currentOwnerGeneration = realtimeMultiplexerOwner?.authenticationGeneration,
                        expectedOwner = owner,
                        expectedGeneration = owner.authenticationGeneration,
                    )
            },
        ) { }

    private fun requireRealtimeAccessToken(owner: RealtimeAuthenticationOwner): String {
        var token: String? = null
        val accepted = authenticationLifecycle.runIfCurrent(
            owner.authenticationGeneration,
            predicate = {
                !closed &&
                    authState != null &&
                    accountScope == owner.accountScope &&
                    storedIdentity === owner.storedIdentity &&
                    realtimeMultiplexer != null &&
                    directCoordinator != null &&
                    authenticationRuntimeClaimMatches(
                        currentOwner = realtimeMultiplexerOwner,
                        currentOwnerGeneration = realtimeMultiplexerOwner?.authenticationGeneration,
                        expectedOwner = owner,
                        expectedGeneration = owner.authenticationGeneration,
                    )
            },
        ) {
            token = realtimeAccessToken.requireCurrent()
        }
        check(accepted) { "Realtime authentication owner changed" }
        return requireNotNull(token)
    }

    private suspend fun deviceRealtimeRuntime(
        stored: DeviceKeyVault.StoredIdentity,
        expectedAuthenticationGeneration: Long,
    ): RealtimeRuntimeClaim {
        while (true) {
            val authentication = captureAuthenticationState(expectedGeneration = expectedAuthenticationGeneration)
            val expectedAccountScope = requireNotNull(authentication.accountScope) {
                "OIDC account scope is unavailable"
            }
            check(storedIdentity === stored && accountScope == expectedAccountScope) {
                "Authentication identity changed"
            }
            var selectedOwner: RealtimeAuthenticationOwner? = null
            var selectedRuntime: ProductionRealtimeMultiplexer? = null
            var selectedCoordinator: ProductionDirectDeviceCoordinator? = null
            val retired = synchronized(liveSessionLock) {
                val existing = realtimeMultiplexer
                val existingCoordinator = directCoordinator
                val owner = realtimeMultiplexerOwner
                when {
                    existing == null && existingCoordinator == null -> null
                    existing != null && existingCoordinator != null && owner != null &&
                        owner.authenticationGeneration == authentication.authenticationGeneration &&
                        owner.accountScope == expectedAccountScope &&
                        owner.storedIdentity === stored -> {
                        selectedOwner = owner
                        selectedRuntime = existing
                        selectedCoordinator = existingCoordinator
                        null
                    }
                    else -> detachRealtimeRuntimeLocked()
                }
            }
            if (retired != null) {
                closeRetiredRealtimeRuntimeAndJoin(retired)
                continue
            }
            selectedRuntime?.let { existing ->
                val owner = requireNotNull(selectedOwner)
                val coordinator = requireNotNull(selectedCoordinator)
                if (isCurrentRealtimeOwner(owner)) return RealtimeRuntimeClaim(owner, existing, coordinator)
                detachRealtimeRuntimeIfOwner(owner)?.let { closeRetiredRealtimeRuntimeAndJoin(it) }
                throw IllegalStateException("Authentication lifecycle changed")
            }

            val owner = RealtimeAuthenticationOwner(
                authenticationGeneration = authentication.authenticationGeneration,
                accountScope = expectedAccountScope,
                storedIdentity = stored,
            )
            val candidate = ProductionRealtimeMultiplexer(
                rootUrl = currentServerUrl(),
                tokenProvider = AccessTokenProvider { requireRealtimeAccessToken(owner) },
                deviceId = stored.deviceId,
                identity = stored.identity,
                parentScope = realtimeRuntimeScope,
                additionalCapabilities = if (BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME) {
                    setOf(dev.termlink.model.PcCapabilityIds.TERMINAL_SNAPSHOT_V2)
                } else {
                    emptySet()
                },
                optionalAdvertisedCapabilities = productRealtimeOptionalCapabilities(
                    BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE,
                ),
                httpClient = http,
                allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
                authenticationRefresher = dev.termlink.network.production.RealtimeAuthenticationRefresher {
                    freshAccessToken(force = true, expectedRuntimeOwner = owner)
                },
                authenticationFailureObserver = {
                    invalidateAuthentication(
                        authenticationGeneration = owner.authenticationGeneration,
                        expectedAuthState = null,
                        expectedRuntimeOwner = owner,
                    )
                },
            )
            val directCandidate = ProductionDirectDeviceCoordinator(
                authenticationGeneration = owner.authenticationGeneration,
                rootUrl = currentServerUrl(),
                tokenProvider = AccessTokenProvider { requireRealtimeAccessToken(owner) },
                localDeviceId = stored.deviceId,
                identity = stored.identity,
                multiplexer = candidate,
                connectivityManager = connectivityManager,
                parentScope = directCoordinatorRuntimeScope,
                httpClient = directShadowHttp,
                allowCleartextForLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
                shadowDiscoveryEnabled = BuildConfig.TERMLINK_DIRECT_TRANSPORT_SHADOW,
                // Release is hard-disabled. Only an isolated private-network
                // Debug acceptance build can enable this independent gate.
                dataPlaneEnabled = BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE,
            )
            val carrierProjectionCandidate = directCoordinatorRuntimeScope.launch(start = CoroutineStart.LAZY) {
                combine(directCandidate.carriers, directCandidate.sessionCarriers) {
                        pcCarriers, sessionCarriers ->
                    pcCarriers to sessionCarriers
                }.collect { (pcCarriers, sessionCarriers) ->
                    authenticationLifecycle.runIfCurrent(
                        owner.authenticationGeneration,
                        predicate = {
                            !closed &&
                                storedIdentity === stored &&
                                directRuntimeProjectionClaimMatches(
                                    currentOwner = realtimeMultiplexerOwner,
                                    currentMultiplexer = realtimeMultiplexer,
                                    currentCoordinator = directCoordinator,
                                    expectedOwner = owner,
                                    expectedMultiplexer = candidate,
                                    expectedCoordinator = directCandidate,
                                )
                        },
                    ) {
                        mutableSnapshot.update { snapshot ->
                            snapshot.copy(
                                pcCarrierStates = retainAuthoritativePcCarrierStates(
                                    pcCarriers.mapValues { (_, carrier) -> carrier.toPcCarrierState() },
                                    snapshot.devices,
                                    snapshot.pcs,
                                ),
                                sessionCarrierStates = retainAuthoritativeSessionCarrierStates(
                                    sessionCarriers.mapValues { (_, carrier) -> carrier.toPcCarrierState() },
                                    snapshot.devices,
                                    snapshot.pcs,
                                    snapshot.sessions,
                                ),
                            )
                        }
                    }
                }
            }
            var installed = false
            val installationCurrent = authenticationLifecycle.runIfCurrent(
                owner.authenticationGeneration,
                predicate = {
                    !closed &&
                        authState != null &&
                        accountScope == owner.accountScope &&
                        storedIdentity === owner.storedIdentity
                },
            ) {
                installed = synchronized(liveSessionLock) {
                    if (realtimeMultiplexer != null || directCoordinator != null || directCarrierProjectionJob != null) {
                        false
                    } else {
                        realtimeMultiplexerOwner = owner
                        realtimeMultiplexer = candidate
                        directCoordinator = directCandidate
                        directCarrierProjectionJob = carrierProjectionCandidate
                        true
                    }
                }
            }
            if (!installationCurrent || !installed) {
                carrierProjectionCandidate.cancel()
                directCandidate.close()
                candidate.close()
                if (!installationCurrent) throw IllegalStateException("Authentication lifecycle changed")
                continue
            }
            if (isCurrentRealtimeOwner(owner)) {
                carrierProjectionCandidate.start()
                refreshDirectAuthorizedPeers(
                    authenticationGeneration = owner.authenticationGeneration,
                    stored = stored,
                    devices = mutableSnapshot.value.devices,
                    pcs = mutableSnapshot.value.pcs,
                )
                directCandidate.observeNetwork(
                    owner.authenticationGeneration,
                    synchronized(realtimeNetworkLock) { realtimeNetwork },
                )
                directCandidate.start()
                return RealtimeRuntimeClaim(owner, candidate, directCandidate)
            }
            detachRealtimeRuntimeIfOwner(owner)?.let { closeRetiredRealtimeRuntimeAndJoin(it) }
            throw IllegalStateException("Authentication lifecycle changed")
        }
    }

    private fun invalidateAuthentication(
        authenticationGeneration: Long,
        expectedAuthState: AuthState?,
        expectedRuntimeOwner: RealtimeAuthenticationOwner?,
    ) {
        val message = context.getString(R.string.failure_authentication_expired)
        val invalidated = authenticationLifecycle.invalidateIfCurrent(
            captured = authenticationGeneration,
            predicate = {
                !closed &&
                    (expectedAuthState == null || authState === expectedAuthState) &&
                    authenticationRuntimeClaimMatches(
                        currentOwner = realtimeMultiplexerOwner,
                        currentOwnerGeneration = realtimeMultiplexerOwner?.authenticationGeneration,
                        expectedOwner = expectedRuntimeOwner,
                        expectedGeneration = authenticationGeneration,
                    ) &&
                    mutableSnapshot.value.signedIn
            },
        ) {
            authState = null
            pendingInteractiveAuthenticationGeneration = null
            realtimeAccessToken.clear()
            runCatching { sessionStore.clearAuthState() }
            runCatching { sessionStore.accountLogin = null }
            mutableSnapshot.value = authenticationExpiredSnapshot(mutableSnapshot.value, message).copy(
                sessionStates = emptyMap(),
                sessionOpenFailures = emptyMap(),
                sessionTitles = emptyMap(),
            )
            appendActivity(
                context.getString(R.string.activity_authentication_expired),
                message,
                Severity.ERROR,
            )
            val retired = synchronized(liveSessionLock) {
                val owner = realtimeMultiplexerOwner
                val exactOwner = authenticationRuntimeClaimMatches(
                    currentOwner = owner,
                    currentOwnerGeneration = owner?.authenticationGeneration,
                    expectedOwner = expectedRuntimeOwner,
                    expectedGeneration = authenticationGeneration,
                )
                if (exactOwner) detachRealtimeRuntimeLocked() else null
            }
            if (retired != null) {
                val retirement = authenticationRetirementBarrier.register()
                // UNDISPATCHED enters the NonCancellable body before this auth
                // CAS can be released. Repository close therefore cannot cancel
                // a detached runtime before its closeAndJoin has actually begun.
                realtimeRuntimeScope.launch(start = CoroutineStart.UNDISPATCHED) {
                    try {
                        withContext(NonCancellable) {
                            retirement.predecessor?.await()
                            yield()
                            closeRetiredRealtimeRuntimeAndJoin(retired)
                        }
                    } finally {
                        authenticationRetirementBarrier.complete(retirement)
                    }
                }
            }
        }
        if (!invalidated) return
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
        directProductE2EBridgeRegistration?.close()
        directProductE2EBridgeRegistration = null
        authenticationLifecycle.invalidate()
        realtimeAccessToken.clear()
        if (realtimeNetworkCallbackRegistered) {
            runCatching { connectivityManager.unregisterNetworkCallback(realtimeNetworkCallback) }
            realtimeNetworkCallbackRegistered = false
        }
        closeLiveSessions()
        realtimeRuntimeScope.cancel()
        directCoordinatorRuntimeScope.cancel()
        closeHistoryEngines()
        val closingDownloadJobs = remoteDownloadJobs.values.toList()
        closingDownloadJobs.forEach(Job::cancel)
        // Repository shutdown pauses active transfers. Their .part files and
        // operation IDs remain durable so the next process can resume them.
        scope.launch {
            for (job in closingDownloadJobs) job.join()
            remoteDownloadJobs.clear()
            billing.close()
            billingStateJob.cancel()
            sessionCatalogPollJob.cancel()
            storedIdentity?.identity?.destroy()
            storedIdentity = null
            authService.dispose()
            database.close()
            closeOkHttpClient(http, "TermLink-OkHttp-Close")
            closeOkHttpClient(directShadowHttp, "TermLink-Direct-OkHttp-Close")
            scope.cancel()
        }
    }

    private fun closeOkHttpClient(client: OkHttpClient, threadName: String) {
        client.dispatcher.cancelAll()
        val executor = client.dispatcher.executorService
        val connectionPool = client.connectionPool
        thread(start = true, isDaemon = true, name = threadName) {
            try {
                connectionPool.evictAll()
            } finally {
                executor.shutdown()
            }
        }
    }

    private enum class EmbeddedAuthFlow { REGISTRATION, ACCOUNT_SETUP }

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
        val engine: NativeTerminalEngine,
        val client: ProductionRealtimeSessionClient,
        private val parentScope: CoroutineScope,
        val authenticationOwner: RealtimeAuthenticationOwner,
        private val directRegistration: ProductionDirectLiveSessionRegistration,
        private val directActivationObserver: () -> Unit,
        /** Fatal sequence inherited only from the incarnation rebuilt after it. */
        val inheritedRendererFatalSequence: Long? = null,
    ) : AutoCloseable {
        private enum class Lifecycle { NEW, STARTING, STARTED, CLOSING, CLOSED }

        private val lifecycleLock = Any()
        private val startCompleted = CompletableDeferred<Unit>()
        private var lifecycle = Lifecycle.NEW
        private var startInFlight = false
        val controlRequestLock = Mutex()
        private var stateJob: Job? = null
        private var tokenRefreshJob: Job? = null
        private var recoveryJob: Job? = null

        fun installBackgroundJobs(stateJob: Job, tokenRefreshJob: Job) {
            synchronized(lifecycleLock) {
                check(lifecycle == Lifecycle.NEW && this.stateJob == null && this.tokenRefreshJob == null) {
                    "Live session background jobs were already installed"
                }
                this.stateJob = stateJob
                this.tokenRefreshJob = tokenRefreshJob
            }
        }

        /**
         * Starts the client only after the fully initialized incarnation has
         * been published. A concurrent exact retirement changes the lifecycle
         * to CLOSING and waits for this startup before closing the client.
         */
        suspend fun start(): Boolean {
            val admitted = synchronized(lifecycleLock) {
                if (lifecycle != Lifecycle.NEW) {
                    false
                } else {
                    lifecycle = Lifecycle.STARTING
                    startInFlight = true
                    true
                }
            }
            if (!admitted) return false
            try {
                client.start()
                check(directRegistration.activate()) {
                    "Direct live-session registration was retired during startup"
                }
                directActivationObserver()
                return synchronized(lifecycleLock) {
                    if (lifecycle != Lifecycle.STARTING) {
                        false
                    } else {
                        lifecycle = Lifecycle.STARTED
                        stateJob?.start()
                        tokenRefreshJob?.start()
                        true
                    }
                }
            } finally {
                synchronized(lifecycleLock) { startInFlight = false }
                startCompleted.complete(Unit)
            }
        }

        fun launchRecovery(block: suspend () -> Unit): Boolean {
            lateinit var candidate: Job
            candidate = parentScope.launch(start = CoroutineStart.LAZY) {
                try {
                    block()
                } finally {
                    synchronized(lifecycleLock) {
                        if (recoveryJob === candidate) recoveryJob = null
                    }
                }
            }
            val accepted = synchronized(lifecycleLock) {
                if (lifecycle != Lifecycle.STARTED || recoveryJob?.isActive == true) {
                    false
                } else {
                    recoveryJob = candidate
                    true
                }
            }
            if (!accepted) {
                candidate.cancel()
                return false
            }
            candidate.start()
            return true
        }

        suspend fun closeAndJoin() {
            val caller = currentCoroutineContext()[Job]
            var waitForStart = false
            val jobs = synchronized(lifecycleLock) {
                if (lifecycle == Lifecycle.CLOSED) return
                waitForStart = startInFlight
                lifecycle = Lifecycle.CLOSING
                listOfNotNull(stateJob, tokenRefreshJob, recoveryJob).also { closingJobs ->
                    closingJobs.forEach(Job::cancel)
                }
            }
            directRegistration.close()
            if (waitForStart) startCompleted.await()
            client.closeGracefully()
            jobs.filterNot { it === caller }.forEach { it.join() }
            synchronized(lifecycleLock) {
                stateJob = null
                tokenRefreshJob = null
                recoveryJob = null
                lifecycle = Lifecycle.CLOSED
            }
        }

        override fun close() {
            synchronized(lifecycleLock) {
                if (lifecycle == Lifecycle.CLOSED) return
                lifecycle = Lifecycle.CLOSING
                listOfNotNull(stateJob, tokenRefreshJob, recoveryJob).forEach(Job::cancel)
            }
            directRegistration.close()
            client.close()
        }
    }

    private data class RetiredRealtimeRuntime(
        val sessions: List<LiveProductSession>,
        val readOnlyEngines: List<NativeTerminalEngine>,
        val directCoordinator: ProductionDirectDeviceCoordinator?,
        val directCarrierProjectionJob: Job?,
        val multiplexer: ProductionRealtimeMultiplexer?,
    )

    private class RealtimeAuthenticationOwner(
        val authenticationGeneration: Long,
        val accountScope: String,
        val storedIdentity: DeviceKeyVault.StoredIdentity,
    )

    private data class RealtimeRuntimeClaim(
        val owner: RealtimeAuthenticationOwner,
        val multiplexer: ProductionRealtimeMultiplexer,
        val directCoordinator: ProductionDirectDeviceCoordinator,
    )

    private data class AuthenticationStateCapture(
        val authenticationGeneration: Long,
        val authState: AuthState,
        val accountScope: String?,
    )

    private data class AuthenticationTransition(
        val authenticationGeneration: Long,
        val retiredRuntime: RetiredRealtimeRuntime?,
    )

    private data class InteractiveAuthorizationClaim(
        val authenticationGeneration: Long,
        val authState: AuthState,
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
        signingPublicKey = java.util.Base64.getDecoder().decode(requiredString("signing_public_key")).also {
            require(it.size == 32) { "Device signing public key must be 32 bytes" }
        },
        trustState = DeviceTrustState.valueOf(requiredString("trust_state")),
        createdAt = instant("created_at"),
    )

    private fun JsonObject.toPc(): PcSummary {
        val supportedShells = get("supported_shells")?.jsonArray
            ?.mapNotNull { value -> value.jsonPrimitive.contentOrNull }
            ?.filterTo(linkedSetOf()) { value ->
                RemoteShellType.entries.any { shell -> shell.wireValue == value }
            }
            .orEmpty()
        val terminalCommands = get("terminal_commands")?.jsonArray
            ?.map { value ->
                val command = value.jsonObject
                dev.termlink.model.TerminalCommandCapability(
                    id = command.requiredString("id").also { id ->
                        require(id.matches(Regex("[a-z][a-z0-9._-]{0,63}")))
                    },
                    displayName = command.requiredString("display_name").also { name ->
                        require(name.encodeToByteArray().size in 1..80 && name.none(Char::isISOControl))
                    },
                    shellType = command.requiredString("shell_type").also { shell ->
                        require(shell in setOf("command-prompt", "windows-powershell", "powershell-7", "wsl", "custom"))
                    },
                    source = command.requiredString("source").also { source ->
                        require(source == "detected" || source == "manual")
                    },
                ).also { capability ->
                    require(
                        capability.source == "manual" && capability.shellType == "custom" ||
                            capability.source == "detected" && capability.shellType == capability.id &&
                            capability.id in setOf("command-prompt", "windows-powershell", "powershell-7", "wsl"),
                    )
                }
            }
            ?.also { commands ->
                require(commands.size <= 24 && commands.distinctBy { command -> command.id }.size == commands.size)
            }
            .orEmpty()
        val legacyRemoteUpdate = get("remote_agent_update_capable")?.jsonPrimitive?.booleanOrNull ?: false
        val capabilities = get("capabilities")?.jsonArray
            ?.map { value -> value.jsonPrimitive.content }
            ?.also { values ->
                require(values.size <= 64 && values.distinct().size == values.size)
                require(values.all { it.matches(Regex("[a-z][a-z0-9._-]{0,63}")) })
            }
            ?.toSet()
            ?: buildSet {
                add(dev.termlink.model.PcCapabilityIds.REALTIME_TERMINAL_V1)
                add(dev.termlink.model.PcCapabilityIds.FILE_OPERATIONS_V1)
                if (terminalCommands.isNotEmpty() || supportedShells.isNotEmpty()) {
                    add(dev.termlink.model.PcCapabilityIds.TERMINAL_COMMANDS_V1)
                }
                if (legacyRemoteUpdate) add(dev.termlink.model.PcCapabilityIds.REMOTE_AGENT_UPDATE_V1)
            }
        return PcSummary(
            id = UUID.fromString(requiredString("id")),
            deviceId = UUID.fromString(requiredString("device_id")),
            name = requiredString("name"),
            agentVersion = requiredString("agent_version"),
            keyEpoch = getValue("key_epoch").jsonPrimitive.long,
            remoteRoutingEnabled = getValue("remote_routing_enabled").jsonPrimitive.boolean,
            online = authoritativePcOnline(this),
            lastSeenAt = optionalInstant("last_seen_at"),
            platform = get("platform")?.jsonPrimitive?.contentOrNull ?: "windows",
            architecture = get("architecture")?.jsonPrimitive?.contentOrNull ?: "unknown",
            platformVersion = get("platform_version")?.jsonPrimitive?.contentOrNull ?: "legacy",
            capabilities = capabilities,
            supportedShells = supportedShells,
            terminalCommands = terminalCommands,
            remoteAgentUpdateCapable = dev.termlink.model.PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in capabilities,
        )
    }

    private fun JsonObject.toAgentUpdateEligibility(pcId: UUID): AgentUpdateEligibility {
        val eligible = getValue("eligible").jsonPrimitive.boolean
        return AgentUpdateEligibility(
            pcId = pcId,
            eligible = eligible,
            reasonCode = get("reason_code")?.jsonPrimitive?.contentOrNull,
            currentVersion = requiredString("current_version"),
            currentBuild = get("current_build")?.jsonPrimitive?.long,
            targetVersion = get("target_version")?.jsonPrimitive?.contentOrNull,
            targetBuild = get("target_build")?.jsonPrimitive?.long,
            required = get("required")?.jsonPrimitive?.booleanOrNull ?: false,
            releaseNotes = get("release_notes")?.jsonPrimitive?.contentOrNull,
            activeSessionCount = get("active_session_count")?.jsonPrimitive?.int ?: 0,
        ).also { result ->
            require(!result.eligible || result.targetBuild != null && !result.targetVersion.isNullOrBlank()) {
                "The Agent update eligibility response is incomplete"
            }
        }
    }

    private fun JsonObject.toAgentUpdateProgress(expectedPcId: UUID): AgentUpdateProgress = AgentUpdateProgress(
        id = UUID.fromString(requiredString("id")),
        pcId = UUID.fromString(requiredString("pc_id")).also { require(it == expectedPcId) },
        currentVersion = requiredString("current_version"),
        currentBuild = getValue("current_build").jsonPrimitive.long,
        targetVersion = requiredString("target_version"),
        targetBuild = getValue("target_build").jsonPrimitive.long,
        status = requiredString("status").also {
            require(it in setOf("PENDING", "PROCESSING", "SUCCEEDED", "FAILED", "CANCELLED", "EXPIRED", "ROLLED_BACK", "ROLLBACK_FAILED"))
        },
        phase = requiredString("phase").also {
            require(it in setOf("REQUESTED", "PRECHECKING", "DOWNLOADING", "VERIFIED", "DRAINING_SESSIONS", "INSTALLING", "STARTING_NEW_AGENT", "VERIFYING_CONNECTION", "ROLLING_BACK", "SUCCEEDED", "FAILED", "ROLLED_BACK", "ROLLBACK_FAILED"))
        },
        progressPercent = getValue("progress_percent").jsonPrimitive.int.also { require(it in 0..100) },
        activeSessionCount = get("active_session_count")?.jsonPrimitive?.int,
        errorCode = get("error_code")?.jsonPrimitive?.contentOrNull,
        errorDetail = get("error_detail")?.jsonPrimitive?.contentOrNull,
        observedAgentVersion = get("observed_agent_version")?.jsonPrimitive?.contentOrNull,
        observedAgentBuild = get("observed_agent_build")?.jsonPrimitive?.long,
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
        require(sizeBytes in 1..HistoryRepository.MAX_TERMINAL_CORE_SNAPSHOT_BYTES) { "Snapshot size is out of bounds" }
        val formatName = requiredString("format_name")
        val formatVersion = getValue("format_version").jsonPrimitive.int
        require(formatName == HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT && formatVersion == 2) {
            "Snapshot format is unsupported"
        }
        val keyId = requiredString("key_id")
        val keyEpoch = requireNotNull(terminalCoreSnapshotKeyEpoch(recipientDeviceId, keyId)) {
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

    private suspend fun openNativeTerminalSnapshot(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        metadata: HistoryObject,
        rows: Int,
        columns: Int,
        sealedObject: ByteArray,
    ): TerminalSnapshotCrypto.OpenedTerminalCoreSnapshot = withContext(Dispatchers.Default) {
        val sequence = requireNotNull(metadata.snapshotSequence) { "Snapshot sequence is missing" }
        if (metadata.kind != HistoryObject.Kind.SNAPSHOT ||
            metadata.formatName != HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT ||
            metadata.formatVersion != 2
        ) {
            throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_FORMAT)
        }
        val keyEpoch = terminalCoreSnapshotKeyEpoch(stored.deviceId, metadata.keyId)
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
            TerminalSnapshotCrypto.openTerminalCoreSnapshot(
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
        } finally {
            pairwise.fill(0)
        }
    }

    private suspend fun restoreBoundNativeTerminalCheckpoint(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: NativeTerminalEngine,
        authenticationGeneration: Long,
        hostHistoryClient: ProductionRealtimeSessionClient? = null,
    ): RestoredLiveHistory? {
        val bound = try {
            historyRepository.rendererCheckpointState(session.id, LIVE_OUTPUT_ACK_DIRECTION)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, error)
        } ?: return null
        if (bound.snapshot.metadata.formatName != HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT ||
            bound.snapshot.metadata.formatVersion != 2
        ) {
            bound.snapshot.ciphertext.fill(0)
            return null
        }
        if (!snapshotCanResumeThroughDurableAcknowledgement(
                snapshotSequence = bound.snapshotSequence,
                durableAcknowledgement = bound.durableAcknowledgement,
            )
        ) {
            bound.snapshot.ciphertext.fill(0)
            return null
        }
        val outputTail = latestAuthoritativeSessionTail(session, authenticationGeneration)
        if (outputTail == null) {
            bound.snapshot.ciphertext.fill(0)
            return null
        }
        val tail = restorableSessionEventTail(outputTail, bound.durableAcknowledgement)
        if (!snapshotSuffixRestoreIsBounded(
                snapshotSequence = bound.snapshotSequence,
                durableAcknowledgement = bound.durableAcknowledgement,
                authoritativeTail = tail,
            )
        ) {
            bound.snapshot.ciphertext.fill(0)
            return null
        }
        return try {
            val opened = openNativeTerminalSnapshot(
                session,
                pc,
                pcDevice,
                stored,
                bound.snapshot.metadata,
                bound.snapshotRows,
                bound.snapshotColumns,
                bound.snapshot.ciphertext,
            )
            try {
                val installed = try {
                    engine.installCheckpoint(opened.checkpoint)
                } catch (error: Exception) {
                    throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_INSTALL)
                }
                    check(installed.sequence.toULong() == bound.snapshotSequence &&
                        installed.size == TerminalSize(bound.snapshotColumns, bound.snapshotRows)
                    ) { "Installed native checkpoint does not match its durable binding" }
                    var restored = bound.snapshotSequence
                    var localInputEpoch = opened.observedLocalInputEpoch.toLong()
                    var currentTail = restorableSessionEventTail(
                        latestAuthoritativeSessionTail(session, authenticationGeneration)
                            ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_NOT_READY),
                        bound.durableAcknowledgement,
                    )
                    if (!snapshotSuffixRestoreIsBounded(
                            snapshotSequence = bound.snapshotSequence,
                            durableAcknowledgement = bound.durableAcknowledgement,
                            authoritativeTail = currentTail,
                        )
                    ) {
                        resetAbandonedSnapshotRenderer(session, engine)
                        return null
                    }
                    while (!snapshotSuffixRestoreReady(
                            restoredThroughSequence = restored,
                            durableAcknowledgement = bound.durableAcknowledgement,
                            authoritativeTail = currentTail,
                        )
                    ) {
                        if (currentTail <= restored) {
                            throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                        }
                        val requiredTarget = maxOf(
                            restored + 1uL,
                            bound.durableAcknowledgement,
                        )
                        val progress = try {
                            if (hostHistoryClient != null) {
                                replayHostHistoryRange(
                                    client = hostHistoryClient,
                                    session = session,
                                    pc = pc,
                                    pcDevice = pcDevice,
                                    stored = stored,
                                    engine = engine,
                                    afterSequence = restored,
                                    throughSequence = currentTail,
                                    initialLocalInputEpoch = localInputEpoch,
                                ).also { value ->
                                    if (!snapshotSuffixReplayTargetIsBounded(
                                            snapshotSequence = bound.snapshotSequence,
                                            replayTarget = value.restoredThrough,
                                        )
                                    ) throw SnapshotSuffixReplayLimitExceededException()
                                }
                            } else {
                                val replayTarget = cacheStreamedHistoryRange(
                                    session = session,
                                    pc = pc,
                                    stored = stored,
                                    authenticationGeneration = authenticationGeneration,
                                    minimumTarget = requiredTarget,
                                    stopAfterSequence = restored,
                                    validateTargetBeforeCaching = { target ->
                                        if (!snapshotSuffixReplayTargetIsBounded(
                                            snapshotSequence = bound.snapshotSequence,
                                            replayTarget = target,
                                        )
                                        ) throw SnapshotSuffixReplayLimitExceededException()
                                    },
                                )
                                replayCachedHistoryRange(
                                    session = session,
                                    pc = pc,
                                    pcDevice = pcDevice,
                                    stored = stored,
                                    engine = engine,
                                    afterSequence = restored,
                                    throughSequence = replayTarget,
                                    initialLocalInputEpoch = localInputEpoch,
                                )
                            }
                        } catch (_: SnapshotSuffixReplayLimitExceededException) {
                            resetAbandonedSnapshotRenderer(session, engine)
                            return null
                        }
                        if (!snapshotSuffixReplayTargetIsBounded(
                                snapshotSequence = bound.snapshotSequence,
                                replayTarget = progress.restoredThrough,
                            )
                        ) {
                            resetAbandonedSnapshotRenderer(session, engine)
                            return null
                        }
                        if (progress.restoredThrough > Long.MAX_VALUE.toULong()) {
                            throw sessionOpenException(SessionOpenFailureCode.HISTORY_REPLAY)
                        }
                        restored = progress.restoredThrough
                        localInputEpoch = progress.localInputEpoch
                        currentTail = restorableSessionEventTail(
                            latestAuthoritativeSessionTail(session, authenticationGeneration)
                                ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_NOT_READY),
                            bound.durableAcknowledgement,
                        )
                        if (!snapshotSuffixRestoreIsBounded(
                                snapshotSequence = bound.snapshotSequence,
                                durableAcknowledgement = bound.durableAcknowledgement,
                                authoritativeTail = currentTail,
                            )
                        ) {
                            resetAbandonedSnapshotRenderer(session, engine)
                            return null
                        }
                    }
                    val persisted = historyRepository.putAckCursor(
                        sessionId = session.id,
                        direction = LIVE_OUTPUT_ACK_DIRECTION,
                        rendererGeneration = bound.rendererGeneration,
                        sequence = restored,
                    )
                    check(persisted == restored) {
                        "Snapshot suffix ACK was not persisted monotonically"
                    }
                    RestoredLiveHistory(
                        rendererState = RendererBootstrapState.ExactCheckpoint(
                            sequence = restored.toLong(),
                            rendererGeneration = bound.rendererGeneration,
                            snapshotId = bound.snapshot.metadata.id,
                            ciphertextSha256 = bound.snapshot.metadata.ciphertextSha256,
                            snapshotSequence = bound.snapshotSequence.toLong(),
                        ),
                        localInputEpoch = localInputEpoch,
                        transcriptAnchor = TranscriptAnchor(
                            bound.snapshotSequence,
                            opened.initialTranscriptBeforeLineId,
                        ),
                        durableAcknowledgement = restored,
                        bootstrapSource = ProductBootstrapSource.BOUND_SNAPSHOT,
                    )
            } finally {
                opened.checkpoint.fill(0)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: SessionOpenException) {
            throw error
        } catch (error: Exception) {
            throw classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY)
        } finally {
            bound.snapshot.ciphertext.fill(0)
        }
    }

    /** Prevents a rejected local Snapshot/suffix from contaminating a later bootstrap strategy. */
    private fun resetAbandonedSnapshotRenderer(session: SessionSummary, engine: TerminalEngine) {
        engine.clear()
        engine.resize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1))
    }

    /**
     * Emits only bounded structural metadata for a Host History restore. Never
     * include session/object IDs, key IDs, ciphertext, terminal text, titles,
     * paths, URLs, tokens, or exception messages in this diagnostic.
     */
    private fun logHostSnapshotRestore(
        attempt: Int,
        stage: String,
        details: String = "",
        error: Throwable? = null,
    ) {
        val failureCode = (error as? SessionOpenException)?.failure?.code?.stableCode.orEmpty()
        val exceptionClass = error?.javaClass?.simpleName.orEmpty().take(MAX_HISTORY_FAILURE_CLASS_CHARS)
        val suffix = buildString {
            if (details.isNotBlank()) append(' ').append(details)
            if (exceptionClass.isNotBlank()) append(" exception=").append(exceptionClass)
            if (failureCode.isNotBlank()) append(" failure=").append(failureCode)
        }
        val message = "attempt=$attempt stage=$stage$suffix"
        if (error == null) {
            Log.i(HOST_HISTORY_RESTORE_LOG_TAG, message)
        } else {
            Log.e(HOST_HISTORY_RESTORE_LOG_TAG, message)
        }
    }

    private suspend fun restoreRemoteNativeTerminalSnapshot(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: NativeTerminalEngine,
        authenticationGeneration: Long,
        expectedRendererGeneration: Long,
        descriptor: SnapshotDownloadDescriptor,
        hostHistoryClient: ProductionRealtimeSessionClient? = null,
    ): RestoredLiveHistory? {
        val restoreAttempt = HOST_HISTORY_RESTORE_ATTEMPTS.incrementAndGet()
        var restoreStage = "cache_lookup"
        val metadata = descriptor.metadata
        val sequence = requireNotNull(metadata.snapshotSequence)
        logHostSnapshotRestore(
            restoreAttempt,
            "started",
            "snapshot_seq=$sequence expected_generation=$expectedRendererGeneration host_history=${hostHistoryClient != null}",
        )
        val cached = historyRepository.getHistoryObject(metadata.id)?.takeIf { value ->
            value.metadata.kind == HistoryObject.Kind.SNAPSHOT &&
                MessageDigest.isEqual(value.metadata.ciphertextSha256, metadata.ciphertextSha256)
        }
        restoreStage = if (cached == null) "snapshot_download" else "snapshot_cache_read"
        val sealedObject = cached?.ciphertext ?: withContext(Dispatchers.IO) {
            downloadHistory(descriptor.download, metadata)
        }
        try {
            logHostSnapshotRestore(
                restoreAttempt,
                "snapshot_cached",
                "snapshot_seq=$sequence cache_hit=${cached != null} sealed_bytes=${sealedObject.size}",
            )
            if (cached == null) {
                restoreStage = "snapshot_cache_write"
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
            }
            restoreStage = "snapshot_decrypt"
            val opened = openNativeTerminalSnapshot(
                session,
                pc,
                pcDevice,
                stored,
                metadata,
                descriptor.rows,
                descriptor.columns,
                sealedObject,
            )
            logHostSnapshotRestore(
                restoreAttempt,
                "snapshot_opened",
                "snapshot_seq=$sequence checkpoint_bytes=${opened.checkpoint.size}",
            )
            try {
                restoreStage = "authority_confirm"
                val confirmedTail = awaitBoundedAuthoritativeTail(
                    session = session,
                    snapshotSequence = sequence,
                    authenticationGeneration = authenticationGeneration,
                    restoreAttempt = restoreAttempt,
                )
                if (confirmedTail == null) {
                    logHostSnapshotRestore(restoreAttempt, "authority_missing", "snapshot_seq=$sequence")
                    return null
                }
                if (!snapshotHasBoundedLiveSuffix(sequence, confirmedTail)) {
                    logHostSnapshotRestore(
                        restoreAttempt,
                        "authority_out_of_bounds",
                        "snapshot_seq=$sequence output_tail=$confirmedTail",
                    )
                    return null
                }
                return withContext(NonCancellable) {
                    restoreStage = "cursor_read"
                    val hostCursor = if (hostHistoryClient != null) {
                        requireNotNull(
                            historyRepository.ackCursorState(session.id, LIVE_OUTPUT_ACK_DIRECTION),
                        ).also { cursor ->
                            check(cursor.rendererGeneration == expectedRendererGeneration) {
                                "Host renderer generation changed before checkpoint installation"
                            }
                        }
                    } else {
                        null
                    }
                    val legacyBound = if (hostHistoryClient == null) {
                        historyRepository.bindRendererCheckpoint(
                            sessionId = session.id,
                            direction = LIVE_OUTPUT_ACK_DIRECTION,
                            expectedRendererGeneration = expectedRendererGeneration,
                            snapshotId = metadata.id,
                            snapshotSequence = sequence,
                            ciphertextSha256 = metadata.ciphertextSha256,
                        )
                    } else {
                        null
                    }
                    val replayTarget = hostCursor?.let {
                        restorableSessionEventTail(confirmedTail, it.sequence)
                    } ?: requireNotNull(legacyBound).durableAcknowledgement
                    logHostSnapshotRestore(
                        restoreAttempt,
                        "authority_confirmed",
                        "snapshot_seq=$sequence output_tail=$confirmedTail " +
                            "durable_ack=${hostCursor?.sequence ?: replayTarget} replay_target=$replayTarget",
                    )
                    if (hostHistoryClient != null &&
                        !snapshotSuffixReplayTargetIsBounded(sequence, replayTarget)
                    ) {
                        throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                    }
                    restoreStage = "checkpoint_install"
                    val installed = try {
                        val installed = engine.installCheckpoint(opened.checkpoint)
                        check(installed.sequence.toULong() == sequence &&
                            installed.size == TerminalSize(descriptor.columns, descriptor.rows)
                        ) { "Installed native checkpoint does not match the bound snapshot" }
                        installed
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_INSTALL)
                    }
                    logHostSnapshotRestore(
                        restoreAttempt,
                        "checkpoint_installed",
                        "snapshot_seq=$sequence columns=${installed.size.columns} rows=${installed.size.rows}",
                    )
                    var restoredThrough = installed.sequence.toULong()
                    var localInputEpoch = opened.observedLocalInputEpoch.toLong()
                    if (restoredThrough < replayTarget) {
                        val client = hostHistoryClient
                            ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_REPLAY)
                        restoreStage = "suffix_replay"
                        val progress = try {
                            val progress = replayHostHistoryRange(
                                client = client,
                                session = session,
                                pc = pc,
                                pcDevice = pcDevice,
                                stored = stored,
                                engine = engine,
                                afterSequence = restoredThrough,
                                throughSequence = replayTarget,
                                initialLocalInputEpoch = localInputEpoch,
                                diagnosticAttempt = restoreAttempt,
                            )
                            progress
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Exception) {
                            throw classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY)
                        }
                        restoredThrough = progress.restoredThrough
                        localInputEpoch = progress.localInputEpoch
                    }
                    if (restoredThrough < replayTarget) {
                        throw sessionOpenException(SessionOpenFailureCode.HISTORY_REPLAY)
                    }
                    logHostSnapshotRestore(
                        restoreAttempt,
                        "suffix_applied",
                        "snapshot_seq=$sequence replay_target=$replayTarget restored_through=$restoredThrough",
                    )
                    restoreStage = "checkpoint_bind"
                    val bound = try {
                        if (hostHistoryClient != null) {
                            historyRepository.bindHostRendererCheckpoint(
                                sessionId = session.id,
                                direction = LIVE_OUTPUT_ACK_DIRECTION,
                                expectedRendererGeneration = expectedRendererGeneration,
                                snapshotId = metadata.id,
                                snapshotSequence = sequence,
                                renderedThroughSequence = restoredThrough,
                                ciphertextSha256 = metadata.ciphertextSha256,
                            )
                        } else {
                            requireNotNull(legacyBound)
                        }
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_CHECKPOINT)
                    }
                    logHostSnapshotRestore(
                        restoreAttempt,
                        "bind_committed",
                        "snapshot_seq=${bound.snapshotSequence} durable_ack=${bound.durableAcknowledgement} " +
                            "renderer_generation=${bound.rendererGeneration}",
                    )
                    restoreStage = "complete"
                    RestoredLiveHistory(
                        rendererState = RendererBootstrapState.ExactCheckpoint(
                            sequence = restoredThrough.toLong(),
                            rendererGeneration = bound.rendererGeneration,
                            snapshotId = metadata.id,
                            ciphertextSha256 = metadata.ciphertextSha256,
                            snapshotSequence = bound.snapshotSequence.toLong(),
                        ),
                        localInputEpoch = localInputEpoch,
                        transcriptAnchor = TranscriptAnchor(
                            sequence,
                            opened.initialTranscriptBeforeLineId,
                        ),
                        durableAcknowledgement = bound.durableAcknowledgement,
                        bootstrapSource = ProductBootstrapSource.REMOTE_SNAPSHOT,
                    ).also {
                        logHostSnapshotRestore(
                            restoreAttempt,
                            "complete",
                            "snapshot_seq=$sequence restored_through=$restoredThrough",
                        )
                    }
                }
            } finally {
                opened.checkpoint.fill(0)
            }
        } catch (error: CancellationException) {
            logHostSnapshotRestore(restoreAttempt, "cancelled", "last_stage=$restoreStage", error)
            throw error
        } catch (error: Exception) {
            logHostSnapshotRestore(restoreAttempt, "failed", "last_stage=$restoreStage", error)
            throw error
        } finally {
            sealedObject.fill(0)
        }
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
        if (!snapshotMatchesDurableAcknowledgement(
                snapshotSequence = checkpoint.snapshotSequence,
                durableAcknowledgement = checkpoint.durableAcknowledgement,
            )
        ) {
            checkpoint.snapshot.ciphertext.fill(0)
            return null
        }
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
            val bound = checkpoint
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
                        snapshotSequence = bound.snapshotSequence.toLong(),
                    ),
                    localInputEpoch = installed.projection.observedLocalInputEpoch,
                    transcriptAnchor = prepared.transcriptAnchor,
                    durableAcknowledgement = bound.snapshotSequence,
                    bootstrapSource = ProductBootstrapSource.BOUND_SNAPSHOT,
                )
            } catch (error: Exception) {
                throw classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_INSTALL)
            }
        }
    }

    private suspend fun latestTerminalSnapshot(
        initialSession: SessionSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authoritativeKeyEpoch: Long,
        minimumSnapshotSequence: ULong = 0uL,
        expectedAuthenticationGeneration: Long? = null,
    ): TerminalSnapshotReadinessObservation? {
        return withAuthenticatedApi(
            httpClient = terminalSnapshotReadinessHttp,
            expectedGeneration = expectedAuthenticationGeneration,
        ) { api, _ ->
            // The session list can become visible before its final snapshot is
            // committed. Refresh only this PC's authoritative session metadata
            // on every bounded readiness attempt, including when the first
            // session observation still reported sequence zero.
            val session = ProductResponseParser.itemObjects(
                api.get(authoritativeSessionCatalogPath(initialSession.pcId)).jsonObject,
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
                            "?kind=snapshot&format_name=${HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT}&limit=1",
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
                        val candidateSequence = requireNotNull(value.metadata.snapshotSequence)
                        val current = candidateSequence >= minimumSnapshotSequence &&
                            candidateSequence <= session.lastOutputSequence && snapshotHasBoundedLiveSuffix(
                            snapshotSequence = candidateSequence,
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

    private suspend fun latestAuthoritativeSessionTail(
        initialSession: SessionSummary,
        expectedAuthenticationGeneration: Long? = null,
    ): ULong? = withAuthenticatedApi(
        httpClient = terminalSnapshotReadinessHttp,
        expectedGeneration = expectedAuthenticationGeneration,
    ) { api, _ ->
            ProductResponseParser.itemObjects(
                // Ended sessions are deliberately absent from the default live directory. Their
                // final tail remains authoritative while the Windows host serves local history.
                api.get(authoritativeSessionCatalogPath(initialSession.pcId)).jsonObject,
            ).map { it.toSession() }
                .singleOrNull { it.id == initialSession.id }
                ?.lastOutputSequence
        }

    /**
     * Confirms the snapshot sits within the server-authoritative output tail.
     * The Agent flushes catalog metadata on a heartbeat cadence, so a snapshot
     * served for a freshly spawned session can legitimately run ahead of the
     * catalog for one flush interval. Re-poll the tail (bounded) before
     * declaring the snapshot out of bounds; a persistent mismatch still
     * fails closed.
     */
    private suspend fun awaitBoundedAuthoritativeTail(
        session: SessionSummary,
        snapshotSequence: ULong,
        authenticationGeneration: Long?,
        restoreAttempt: Int,
    ): ULong? {
        var tail = latestAuthoritativeSessionTail(session, authenticationGeneration)
        var attempt = 0
        while (tail != null && !snapshotHasBoundedLiveSuffix(snapshotSequence, tail) &&
            attempt < AUTHORITY_TAIL_CATCHUP_RETRY_DELAYS_MILLIS.size
        ) {
            val delayMillis = AUTHORITY_TAIL_CATCHUP_RETRY_DELAYS_MILLIS[attempt]
            logHostSnapshotRestore(
                restoreAttempt,
                "authority_catchup_wait",
                "snapshot_seq=$snapshotSequence output_tail=$tail retry_in=${delayMillis}ms attempt=${attempt + 1}",
            )
            delay(delayMillis)
            attempt++
            tail = latestAuthoritativeSessionTail(session, authenticationGeneration)
        }
        return tail
    }

    /**
     * Reads only the newest, recipient-filtered Chunk catalog pages needed by
     * tail recovery. The server orders this selector newest first, so walking
     * the entire catalog would turn the availability fallback into the same
     * unbounded cold start it is intended to avoid.
     */
    private suspend fun latestBoundedTailReplayCatalog(
        session: SessionSummary,
        pc: PcSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authoritativeTail: ULong,
    ): List<HistoryEntry> = withApi(terminalSnapshotReadinessHttp) { api ->
        val entries = mutableListOf<HistoryEntry>()
        val downloads = mutableMapOf<UUID, HistoryDownloadRequest>()
        val objectIds = mutableSetOf<UUID>()
        val consumedCursors = mutableSetOf<String>()
        var cursor: String? = null
        var pageCount = 0
        while (true) {
            require(pageCount < TERMINAL_TAIL_REPLAY_MAX_CATALOG_PAGES)
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
            val selection = boundedTailReplaySelection(
                entries,
                authoritativeTail,
                HistoryEntry::historySequenceRange,
            )
            if (selection.readiness == BoundedEventReplayReadiness.READY &&
                selection.eventCount >= TERMINAL_BOUNDED_REPLAY_MAX_EVENTS
            ) break
            if (pageCount >= TERMINAL_TAIL_REPLAY_MAX_CATALOG_PAGES) break
            val next = response["next_cursor"]?.jsonPrimitive?.contentOrNull
                ?.takeIf(String::isNotBlank)
                ?: error("Chunk history page omitted its continuation cursor")
            require(consumedCursors.add(next)) { "Chunk history cursor loop detected" }
            cursor = next
        }
        historyDownloads.putAll(downloads)
        entries
    }

    /**
     * Caches one immutable, contiguous CHUNK range while holding at most one
     * catalog page and one encrypted object in memory. The server's signed
     * cursor fixes one catalog revision; [stopAfterSequence] lets a catch-up
     * pass stop at the durable boundary from the preceding pass.
     *
     * The returned target may be newer than [minimumTarget] when output and
     * durable history advanced between the authoritative-tail read and the
     * first catalog request. That is safe because every admitted sequence is
     * still authenticated, contiguous, and later rendered before its ACK.
     */
    private suspend fun cacheStreamedHistoryRange(
        session: SessionSummary,
        pc: PcSummary,
        stored: DeviceKeyVault.StoredIdentity,
        authenticationGeneration: Long,
        minimumTarget: ULong,
        stopAfterSequence: ULong,
        validateTargetBeforeCaching: (ULong) -> Unit = {},
    ): ULong = withAuthenticatedApi(
        httpClient = terminalSnapshotReadinessHttp,
        expectedGeneration = authenticationGeneration,
    ) { api, _ ->
        require(minimumTarget > stopAfterSequence) { "Streamed history target must advance" }

        val firstResponse = awaitStreamedHistoryCatalogHead(
            minimumTarget = minimumTarget,
            loadPage = { api.get(sessionChunkHistoryRequestPath(session.id)).jsonObject },
            newestEndSequence = { response ->
                boundedChunkHistoryItems(response)
                    .firstOrNull()
                    ?.toHistoryObject(session.id)
                    ?.endSequence
            },
        )

        cacheContiguousHistoryRange(
            minimumTarget = minimumTarget,
            stopAfterSequence = stopAfterSequence,
            firstPage = firstResponse.toStreamedHistoryCatalogPage(),
            loadNextPage = { cursor ->
                api.get(sessionChunkHistoryRequestPath(session.id, cursor))
                    .jsonObject
                    .toStreamedHistoryCatalogPage()
            },
            sequenceRange = { item ->
                val metadata = item.toHistoryObject(session.id)
                val start = metadata.startSequence
                val end = metadata.endSequence
                if (start == null || end == null) null else HistorySequenceRange(start, end)
            },
            validateTargetBeforeCaching = validateTargetBeforeCaching,
            cacheCandidate = { item ->
                val metadata = item.toHistoryObject(session.id)
                val historyEpoch = historyKeyEpoch(stored.deviceId, metadata.keyId)
                if (historyEpoch == null || historyEpoch > pc.keyEpoch) {
                    throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
                }
                cacheRemoteHistoryChunk(
                    sessionId = session.id,
                    metadata = metadata,
                    download = item.getValue("download").jsonObject.toHistoryDownloadRequest(),
                )
            },
        )
    }

    private suspend fun cacheRemoteHistoryChunk(
        sessionId: UUID,
        metadata: HistoryObject,
        download: HistoryDownloadRequest,
    ) = withContext(Dispatchers.IO) {
        val cached = historyRepository.getHistoryObject(metadata.id)
        if (cached != null) {
            try {
                val existing = cached.metadata
                require(existing.kind == HistoryObject.Kind.CHUNK &&
                    existing.id == metadata.id &&
                    existing.startSequence == metadata.startSequence &&
                    existing.endSequence == metadata.endSequence &&
                    existing.objectKey == metadata.objectKey &&
                    existing.keyId == metadata.keyId &&
                    existing.sizeBytes == metadata.sizeBytes &&
                    MessageDigest.isEqual(existing.ciphertextSha256, metadata.ciphertextSha256)
                ) { "Cached event chunk metadata changed" }
            } finally {
                cached.ciphertext.fill(0)
            }
            return@withContext
        }

        val sealedObject = downloadHistory(download, metadata)
        try {
            historyRepository.putEventChunk(
                sessionId = sessionId,
                startSequence = requireNotNull(metadata.startSequence),
                endSequence = requireNotNull(metadata.endSequence),
                objectKey = metadata.objectKey,
                keyId = metadata.keyId,
                ciphertext = sealedObject,
                id = metadata.id,
                ciphertextSha256 = metadata.ciphertextSha256,
            )
        } finally {
            sealedObject.fill(0)
        }
    }

    private data class StreamedReplayProgress(
        val restoredThrough: ULong,
        val localInputEpoch: Long,
    )

    private class SnapshotSuffixReplayLimitExceededException : RuntimeException()

    private suspend fun cacheHostSnapshot(
        session: SessionSummary,
        pc: PcSummary,
        stored: DeviceKeyVault.StoredIdentity,
        value: ProductionHostHistoryObject,
    ): SnapshotDownloadDescriptor {
        require(value.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_SNAPSHOT) {
            "Host history object is not a Snapshot"
        }
        require(value.snapshotSequence in 1uL..Long.MAX_VALUE.toULong()) {
            "Host Snapshot sequence is invalid"
        }
        require(value.columns in 1..HistoryRepository.MAX_TERMINAL_SNAPSHOT_COLUMNS &&
            value.rows in 1..HistoryRepository.MAX_TERMINAL_SNAPSHOT_ROWS
        ) { "Host Snapshot dimensions are invalid" }
        require(value.formatName == HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT && value.formatVersion == 2) {
            "Host Snapshot format is unsupported"
        }
        val keyEpoch = terminalCoreSnapshotKeyEpoch(stored.deviceId, value.keyId)
            ?: throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_RECIPIENT)
        if (keyEpoch > pc.keyEpoch) throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        val metadata = value.toHistoryObject(session.id)
        historyRepository.putSnapshot(
            sessionId = session.id,
            snapshotSequence = value.snapshotSequence,
            rows = value.rows,
            columns = value.columns,
            objectKey = metadata.objectKey,
            keyId = value.keyId,
            formatName = value.formatName,
            formatVersion = value.formatVersion,
            ciphertext = value.sealedObject,
            id = value.objectId,
            ciphertextSha256 = value.ciphertextSha256,
        )
        return SnapshotDownloadDescriptor(
            metadata = metadata,
            rows = value.rows,
            columns = value.columns,
            // The snapshot was cached above. This descriptor preserves the
            // existing bind/install path without introducing an HTTP fallback.
            download = HistoryDownloadRequest("host-history-v1", "GET", emptyMap()),
        )
    }

    private suspend fun replayHostHistoryRange(
        client: ProductionRealtimeSessionClient,
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TerminalEngine,
        afterSequence: ULong,
        throughSequence: ULong,
        initialLocalInputEpoch: Long,
        diagnosticAttempt: Int? = null,
    ): StreamedReplayProgress {
        require(afterSequence < throughSequence) { "Host history replay range must advance" }
        var restored = afterSequence
        var localInputEpoch = initialLocalInputEpoch
        while (restored < throughSequence) {
            currentCoroutineContext().ensureActive()
            val remaining = throughSequence - restored
            diagnosticAttempt?.let { attempt ->
                logHostSnapshotRestore(
                    attempt,
                    "event_range_request",
                    "after=$restored through=$throughSequence max_events=${minOf(remaining, 512uL)}",
                )
            }
            val value = awaitHostHistoryReady {
                client.requestHostEventRange(
                    afterSequence = restored,
                    throughSequence = throughSequence,
                    maxEvents = minOf(remaining, 512uL).toInt(),
                )
            } ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_NOT_READY)
            try {
                require(value.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_EVENT_CHUNK) {
                    "Host history object is not an event chunk"
                }
                val expectedStart = restored + 1uL
                require(value.startSequence == expectedStart &&
                    value.endSequence in expectedStart..throughSequence
                ) { "Host history event range is not contiguous" }
                require(value.hasMoreEvents == (value.endSequence < throughSequence)) {
                    "Host history continuation metadata is inconsistent"
                }
                require(value.formatName == HistoryRepository.BOUNDED_EVENT_REPLAY_FORMAT && value.formatVersion == 1) {
                    "Host history event format is unsupported"
                }
                diagnosticAttempt?.let { attempt ->
                    logHostSnapshotRestore(
                        attempt,
                        "event_range_received",
                        "start=${value.startSequence} end=${value.endSequence} has_more=${value.hasMoreEvents} " +
                            "sealed_bytes=${value.sealedObject.size}",
                    )
                }
                val chunk = openHostHistoryChunk(session, pc, pcDevice, stored, value)
                diagnosticAttempt?.let { attempt ->
                    val typeCounts = chunk.eventsList
                        .groupingBy { it.messageType.name }
                        .eachCount()
                        .toSortedMap()
                        .entries
                        .joinToString(",") { (name, count) -> "$name:$count" }
                    logHostSnapshotRestore(
                        attempt,
                        "event_range_opened",
                        "events=${chunk.eventsCount} types=$typeCounts",
                    )
                }
                localInputEpoch = applyContiguousHistoryChunkSuffix(
                    startSequence = value.startSequence,
                    endSequence = value.endSequence,
                    nextSequence = expectedStart,
                    events = chunk.eventsList,
                    eventSequence = { it.seq.toULong() },
                    initialLocalInputEpoch = localInputEpoch,
                    applyEvent = { event -> applyHistoryEvent(engine, event) },
                )
                restored = value.endSequence
                diagnosticAttempt?.let { attempt ->
                    logHostSnapshotRestore(
                        attempt,
                        "event_range_applied",
                        "restored_through=$restored",
                    )
                }
            } finally {
                value.destroy()
            }
            yield()
        }
        return StreamedReplayProgress(restored, localInputEpoch)
    }

    private suspend fun openHostHistoryChunk(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        value: ProductionHostHistoryObject,
    ): HistoryChunkV1 = withContext(Dispatchers.Default) {
        val historyEpoch = historyKeyEpoch(stored.deviceId, value.keyId)
            ?: throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        if (historyEpoch > pc.keyEpoch) throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            historyEpoch,
        )
        try {
            HistoryCrypto.open(
                pairwise,
                value.sealedObject,
                session.id,
                pc.deviceId,
                stored.deviceId,
                historyEpoch,
                value.keyId,
                value.startSequence,
                value.endSequence,
            )
        } finally {
            pairwise.fill(0)
        }
    }

    private fun ProductionHostHistoryObject.toHistoryObject(sessionId: UUID): HistoryObject = HistoryObject(
        id = objectId,
        kind = when (kind) {
            HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_SNAPSHOT -> HistoryObject.Kind.SNAPSHOT
            HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_EVENT_CHUNK -> HistoryObject.Kind.CHUNK
            else -> error("Host history object has no catalog representation")
        },
        objectKey = hostHistoryObjectKey(sessionId, objectId),
        keyId = keyId,
        ciphertextSha256 = ciphertextSha256.copyOf(),
        sizeBytes = sealedObject.size.toLong(),
        startSequence = startSequence.takeIf { it > 0u },
        endSequence = endSequence.takeIf { it > 0u },
        snapshotSequence = snapshotSequence.takeIf { it > 0u },
        formatName = formatName,
        formatVersion = formatVersion,
    )

    private fun hostHistoryObjectKey(sessionId: UUID, objectId: UUID): String =
        "host-history-v1/${sessionId.toString().lowercase()}/${objectId.toString().lowercase()}"

    /** Replays cached ciphertext one immutable CHUNK at a time. */
    private suspend fun replayCachedHistoryRange(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: TerminalEngine,
        afterSequence: ULong,
        throughSequence: ULong,
        initialLocalInputEpoch: Long,
    ): StreamedReplayProgress {
        require(throughSequence > afterSequence) { "Streamed history replay must advance" }
        var expected = afterSequence + 1uL
        var localInputEpoch = initialLocalInputEpoch
        while (expected <= throughSequence) {
            currentCoroutineContext().ensureActive()
            val encrypted = historyRepository.eventChunkContaining(session.id, expected)
                ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            val metadata = encrypted.metadata
            val start = metadata.startSequence
                ?: run {
                    encrypted.ciphertext.fill(0)
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                }
            val end = metadata.endSequence
                ?: run {
                    encrypted.ciphertext.fill(0)
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                }
            if (start > expected || end < expected || end > throughSequence) {
                encrypted.ciphertext.fill(0)
                throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            }
            val chunk = try {
                openCachedHistoryChunk(session, pc, pcDevice, stored, encrypted)
            } finally {
                encrypted.ciphertext.fill(0)
            }
            localInputEpoch = applyContiguousHistoryChunkSuffix(
                startSequence = start,
                endSequence = end,
                nextSequence = expected,
                events = chunk.eventsList,
                eventSequence = { it.seq.toULong() },
                initialLocalInputEpoch = localInputEpoch,
                applyEvent = { event -> applyHistoryEvent(engine, event) },
            )
            expected = end + 1uL
            yield()
        }
        return StreamedReplayProgress(throughSequence, localInputEpoch)
    }

    private suspend fun openCachedHistoryChunk(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        encrypted: EncryptedHistoryObject,
    ): HistoryChunkV1 = withContext(Dispatchers.Default) {
        val metadata = encrypted.metadata
        val start = requireNotNull(metadata.startSequence)
        val end = requireNotNull(metadata.endSequence)
        val historyEpoch = historyKeyEpoch(stored.deviceId, metadata.keyId)
            ?: throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        if (historyEpoch > pc.keyEpoch) throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_KEY)
        val pairwise = SessionCrypto.derivePairwiseKey(
            stored.identity.exchange.privateKey,
            pcDevice.exchangePublicKey,
            pc.deviceId,
            stored.deviceId,
            historyEpoch,
        )
        try {
            HistoryCrypto.open(
                pairwise,
                encrypted.ciphertext,
                session.id,
                pc.deviceId,
                stored.deviceId,
                historyEpoch,
                metadata.keyId,
                start,
                end,
            )
        } finally {
            pairwise.fill(0)
        }
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
                        snapshotSequence = bound.snapshotSequence.toLong(),
                    ),
                    localInputEpoch = installed.projection.observedLocalInputEpoch,
                    transcriptAnchor = prepared.transcriptAnchor,
                    durableAcknowledgement = bound.durableAcknowledgement,
                    bootstrapSource = ProductBootstrapSource.REMOTE_SNAPSHOT,
                )
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
     * Installs an exact Snapshot when available. Otherwise it renders a
     * bounded, contiguous recent Chunk suffix ending at an authoritative tail
     * and declares that loss of the earlier prefix explicitly as TailWindow.
     * A fresh socket is registered only after the rendered tail is rechecked
     * against the realtime replay bound.
     */
    private suspend fun restoreLiveHistory(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        engine: NativeTerminalEngine,
        authenticationGeneration: Long,
        allowFreshReplayFromZero: Boolean = false,
        hostHistoryClient: ProductionRealtimeSessionClient? = null,
    ): RestoredLiveHistory {
        val cursor = historyRepository.ackCursorState(session.id, LIVE_OUTPUT_ACK_DIRECTION)
            ?: historyRepository.initializeAckCursor(session.id, LIVE_OUTPUT_ACK_DIRECTION)
        if (allowFreshReplayFromZero && cursor.sequence == 0uL) {
            val currentTail = try {
                latestAuthoritativeSessionTail(session, authenticationGeneration)
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
        val terminalSnapshotNegotiated = terminalSnapshotRuntimeNegotiated(
            BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME,
            pc.capabilities,
        )
        // Prefer terminal-core's canonical Snapshot V2 and wait through the
        // normal Agent -> object store -> catalog publication window.  CHUNK
        // replay remains the compatibility/availability fallback and stays
        // unbounded in total session length (TL-H115).
        restoreReplaceableBoundOrRemoteCheckpoint(
            negotiated = terminalSnapshotNegotiated,
            restoreBound = {
                restoreBoundNativeTerminalCheckpoint(
                    session,
                    pc,
                    pcDevice,
                    stored,
                    engine,
                    authenticationGeneration,
                    hostHistoryClient,
                )
            },
            abandonRenderer = { resetAbandonedSnapshotRenderer(session, engine) },
            restoreRemote = {
                if (hostHistoryClient != null) {
                    val hostSnapshot = awaitHostHistoryReady {
                        hostHistoryClient.requestLatestHostSnapshot()
                    } ?: return@restoreReplaceableBoundOrRemoteCheckpoint null
                    try {
                        val descriptor = cacheHostSnapshot(
                            session = session,
                            pc = pc,
                            stored = stored,
                            value = hostSnapshot,
                        )
                        return@restoreReplaceableBoundOrRemoteCheckpoint restoreRemoteNativeTerminalSnapshot(
                            session = session,
                            pc = pc,
                            pcDevice = pcDevice,
                            stored = stored,
                            engine = engine,
                            authenticationGeneration = authenticationGeneration,
                            expectedRendererGeneration = cursor.rendererGeneration,
                            descriptor = descriptor,
                            hostHistoryClient = hostHistoryClient,
                        )
                    } finally {
                        hostSnapshot.destroy()
                    }
                }
                val readiness = awaitTerminalSnapshotReadiness(
                    latestObservation = {
                        latestTerminalSnapshot(
                            session,
                            stored,
                            pc.keyEpoch,
                            minimumSnapshotSequence = cursor.sequence,
                            expectedAuthenticationGeneration = authenticationGeneration,
                        )
                    },
                    snapshotOf = TerminalSnapshotReadinessObservation::snapshot,
                    lastOutputSequenceOf = TerminalSnapshotReadinessObservation::lastOutputSequence,
                    failureOf = TerminalSnapshotReadinessObservation::failure,
                    retryDelaysMillis = TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS,
                    timeoutMillis = TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
                    ensureObservationContext = {
                        requireCurrentAuthentication(authenticationGeneration)
                    },
                    observationFailure = { error ->
                        classifySessionOpenException(
                            error,
                            SessionFailureStage.SNAPSHOT_METADATA,
                        ).failure
                    },
                )
                val descriptor = readiness.snapshot
                if (descriptor != null) {
                    restoreRemoteNativeTerminalSnapshot(
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        engine = engine,
                        authenticationGeneration = authenticationGeneration,
                        expectedRendererGeneration = cursor.rendererGeneration,
                        descriptor = descriptor,
                    )
                } else {
                    // ACK zero can still use the complete Chunk compatibility
                    // path below. A positive durable ACK cannot safely rewind
                    // to zero, so retain the latest actionable Snapshot error.
                    if (terminalSnapshotReadinessBlocksChunkFallback(
                            cursor.sequence,
                            readiness.failure,
                        )
                    ) {
                        throwTerminalSnapshotReadinessFailureUnlessUnsupported(readiness.failure)
                    }
                    null
                }
            },
        )?.let { return it }

        val latestAuthoritativeTail = try {
            latestAuthoritativeSessionTail(session, authenticationGeneration)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            throw classifySessionOpenException(error, SessionFailureStage.HISTORY_METADATA)
        } ?: throw sessionOpenException(SessionOpenFailureCode.SESSION_UNAVAILABLE)
        // A brand-new silent session has no historical terminal state to
        // bootstrap. Subscribing from zero is tail-safe because sequence zero
        // cannot cause an old output prefix to be replayed.
        if (canSafelySubscribeFromZeroWithoutSnapshot(
                currentSessionSequence = latestAuthoritativeTail,
                durableAcknowledgement = cursor.sequence,
                authoritativeReadinessObserved = true,
            )
        ) {
            return RestoredLiveHistory(RendererBootstrapState.Empty(cursor.rendererGeneration))
        }
        if (!fullPrefixReplayCanPreserveDurableAcknowledgement(cursor.sequence)) {
            throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
        }
        val activeRendererGeneration = cursor.rendererGeneration
        val initialTail = latestAuthoritativeTail
        if (initialTail == 0uL) {
            if (cursor.sequence == 0uL) {
                return RestoredLiveHistory(RendererBootstrapState.Empty(cursor.rendererGeneration))
            }
            throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
        }
        var restored = 0uL
        var localInputEpoch = 0L
        return try {
            while (true) {
                val progress = if (hostHistoryClient != null) {
                    replayHostHistoryRange(
                        client = hostHistoryClient,
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        engine = engine,
                        afterSequence = restored,
                        throughSequence = initialTail,
                        initialLocalInputEpoch = localInputEpoch,
                    )
                } else {
                    val replayTarget = cacheStreamedHistoryRange(
                        session = session,
                        pc = pc,
                        stored = stored,
                        authenticationGeneration = authenticationGeneration,
                        // Restore every newly durable prefix immediately instead
                        // of waiting for the catalog to catch the authoritative
                        // tail in one step. This keeps both memory and latency
                        // bounded independently of the total session length.
                        minimumTarget = restored + 1uL,
                        stopAfterSequence = restored,
                    )
                    if (replayTarget > Long.MAX_VALUE.toULong()) {
                        throw sessionOpenException(SessionOpenFailureCode.HISTORY_REPLAY)
                    }
                    replayCachedHistoryRange(
                        session = session,
                        pc = pc,
                        pcDevice = pcDevice,
                        stored = stored,
                        engine = engine,
                        afterSequence = restored,
                        throughSequence = replayTarget,
                        initialLocalInputEpoch = localInputEpoch,
                    )
                }
                restored = progress.restoredThrough
                localInputEpoch = progress.localInputEpoch

                val currentTail = latestAuthoritativeSessionTail(session, authenticationGeneration)
                    ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_NOT_READY)
                if (tailReplayHasBoundedRealtimeSuffix(restored, currentTail)) break
                if (currentTail <= restored) {
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                }
                // Output outran the realtime replay window while the prefix was
                // being restored. Cache and render only the next newly durable
                // range, then re-read authority again. There is no total event
                // or elapsed-time limit.
            }

            val persisted = historyRepository.putAckCursor(
                sessionId = session.id,
                direction = LIVE_OUTPUT_ACK_DIRECTION,
                rendererGeneration = activeRendererGeneration,
                sequence = restored,
            )
            check(persisted == restored) { "Streamed history ACK was not persisted" }
            RestoredLiveHistory(
                rendererState = RendererBootstrapState.FullPrefix(
                    sequence = restored.toLong(),
                    rendererGeneration = activeRendererGeneration,
                ),
                localInputEpoch = localInputEpoch,
                durableAcknowledgement = restored,
                bootstrapSource = ProductBootstrapSource.NATIVE_FULL_PREFIX,
            )
        } catch (error: CancellationException) {
            throw error
        } catch (error: SessionOpenException) {
            throw error
        } catch (error: Exception) {
            throw classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY)
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
            isCurrentRealtimeOwner(live.authenticationOwner) &&
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
                        it.type.isPcAgent() &&
                        it.trustState == DeviceTrustState.TRUSTED
                } ?: return
                val activeGap = live.client.sessionState.value as? RemoteSessionState.HistoryGap ?: return
                if (PcCapabilityIds.HOST_HISTORY_V1 in pc.capabilities ||
                    !fullPrefixReplayCanPreserveDurableAcknowledgement(
                        activeGap.lastAckedSequence.toULong(),
                    )
                ) {
                    // Host history can rebuild from a Snapshot without
                    // materializing a complete prefix in Android memory. A
                    // legacy raw-prefix recovery must also reopen whenever it
                    // would regress the durable ACK.
                    scope.launch {
                        sessionLifecycleLanes.withLock(sessionId) {
                            try {
                                // Teardown is a same-session durable-write barrier:
                                // the retired ACK worker must finish before the new
                                // Snapshot bootstrap reads and reuses its cursor generation.
                                retireExactIncarnationAndThen(
                                    expected = live,
                                    removeExact = { expected ->
                                        synchronized(liveSessionLock) {
                                            liveSessions.remove(sessionId, expected)
                                        }
                                    },
                                    closeAndJoin = LiveProductSession::closeAndJoin,
                                    afterRetired = { openSessionLocked(sessionId) },
                                )
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Throwable) {
                                val classified = classifySessionOpenException(
                                    error,
                                    SessionFailureStage.HISTORY_REPLAY,
                                )
                                observeBootstrapFailure(sessionId, classified.failure)
                            }
                        }
                    }
                    return
                }
                val missingEnd = activeGap.receivedSequence - 1
                val recovery = if (missingEnd > 0) {
                    loadLiveHistoryEvents(
                        session,
                        pc,
                        pcDevice,
                        live.authenticationOwner.storedIdentity,
                        missingEnd,
                    )
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
                            context.getString(R.string.activity_history_gap_recovered),
                            context.getString(R.string.activity_history_gap_recovered_detail, recovered),
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

    private suspend fun recoverRendererFatal(
        sessionId: UUID,
        expectedLive: LiveProductSession,
        failure: ProductionRendererFatalFailure,
    ) = sessionLifecycleLanes.withLock(sessionId) {
        if (closed || failure.sessionId != sessionId) return@withLock
        val expectedIsCurrent = liveSessions[sessionId] === expectedLive
        when (rendererFatalIncarnationDecision(
            expectedIsCurrent = expectedIsCurrent,
            inheritedFatalSequence = expectedLive.inheritedRendererFatalSequence,
            failureSequence = failure.sequence,
        )) {
            RendererFatalIncarnationDecision.STALE -> return@withLock
            RendererFatalIncarnationDecision.FAIL_CLOSED -> {
                val retired = retireExactIncarnationAndThen(
                    expected = expectedLive,
                    removeExact = { expected ->
                        synchronized(liveSessionLock) {
                            liveSessions.remove(sessionId, expected)
                        }
                    },
                    closeAndJoin = LiveProductSession::closeAndJoin,
                )
                if (!retired) return@withLock
                val classified = sessionOpenException(
                    SessionOpenFailureCode.NATIVE_RENDERER_FAILED_CLOSED,
                )
                observeBootstrapFailure(sessionId, classified.failure)
            }
            RendererFatalIncarnationDecision.RECOVER -> {
                try {
                    retireExactIncarnationAndThen(
                        expected = expectedLive,
                        removeExact = { expected ->
                            synchronized(liveSessionLock) {
                                liveSessions.remove(sessionId, expected)
                            }
                        },
                        closeAndJoin = LiveProductSession::closeAndJoin,
                        afterRetired = {
                            openSessionLocked(
                                sessionId = sessionId,
                                inheritedRendererFatalSequence = failure.sequence,
                            )
                        },
                    )
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Throwable) {
                    val classified = classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY)
                    observeBootstrapFailure(sessionId, classified.failure)
                }
            }
        }
    }

    private suspend fun loadLiveHistoryEvents(
        session: SessionSummary,
        pc: PcSummary,
        pcDevice: DeviceSummary,
        stored: DeviceKeyVault.StoredIdentity,
        throughSequence: Long,
    ): LoadedLiveHistory? = withContext(Dispatchers.Default) {
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
                    return@withContext null
                }
                val download = historyDownloads[entry.historyObject.id]
                if (download == null) {
                    wipeHistoryEvents(events)
                    return@withContext null
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
                    return@withContext null
                }
            }
        } catch (error: Exception) {
            wipeHistoryEvents(events)
            throw error
        }
        LoadedLiveHistory(events, localInputEpoch)
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

    private fun terminalCoreSnapshotKeyEpoch(recipientDeviceId: UUID, keyId: String): Long? {
        val prefix = "terminal-core-snapshot-v2:${recipientDeviceId.toString().replace("-", "")}:"
        if (!keyId.startsWith(prefix)) return null
        val encoded = keyId.removePrefix(prefix)
        if (encoded.length != 8 || encoded.any { !it.isDigit() }) return null
        val epoch = encoded.toLongOrNull()?.takeIf { it > 0 } ?: return null
        return epoch.takeIf { TerminalSnapshotCrypto.terminalCoreSnapshotKeyId(recipientDeviceId, it) == keyId }
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

    private suspend fun applyHistoryEvent(engine: TerminalEngine, event: HistoryEventV1): Long {
        if (event.messageType == MessageType.GAP) {
            val payload = event.payload.toByteArray()
            try {
                val gap = GapPayload.parseFrom(payload)
                runCatching { engine.reportGap(event.seq) }
                error("History gap ${gap.start}..${gap.end}")
            } finally {
                payload.fill(0)
            }
        }
        val decoded = decodeHistoryEvent(event)
        return try {
            when (val remote = decoded.event) {
                is RemoteSessionEvent.Output ->
                    engine.feed(remote.sequence, ByteBuffer.wrap(remote.bytes).asReadOnlyBuffer())
                is RemoteSessionEvent.Resize ->
                    engine.resize(remote.sequence, remote.size.columns, remote.size.rows)
                is RemoteSessionEvent.Title,
                is RemoteSessionEvent.InputResult,
                is RemoteSessionEvent.ControlGranted,
                is RemoteSessionEvent.ControlRevoked,
                is RemoteSessionEvent.Lifecycle,
                -> engine.advance(remote.sequence)
                is RemoteSessionEvent.Gap -> error("Decoded history unexpectedly retained a gap")
            }
            decoded.localInputEpoch
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            val diagnostics = error as? TerminalEngineFailureDiagnostics
            Log.e(
                HISTORY_REPLAY_LOG_TAG,
                "history_renderer_fatal seq=${event.seq} type=${event.messageType.name} " +
                    "exception=${error::class.java.simpleName.take(MAX_HISTORY_FAILURE_CLASS_CHARS)} " +
                    "terminal_operation=${diagnostics?.terminalOperation.orEmpty()} " +
                    "terminal_status=${diagnostics?.terminalStatusName.orEmpty()} " +
                    "terminal_status_code=${diagnostics?.terminalStatusCode ?: 0}",
            )
            throw error
        } finally {
            if (decoded.event is RemoteSessionEvent.Output) decoded.event.bytes.fill(0)
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
        private const val SESSION_OPEN_LOG_TAG = "TermLinkSessionOpen"
        private const val HISTORY_REPLAY_LOG_TAG = "TermLinkHistoryReplay"
        private const val HOST_HISTORY_RESTORE_LOG_TAG = "TermLinkHostRestore"
        private const val REMOTE_DOWNLOAD_LOG_TAG = "TermLinkFileTransfer"
        private val HOST_HISTORY_RESTORE_ATTEMPTS = AtomicInteger()
        private const val MAX_HISTORY_FAILURE_CLASS_CHARS = 128
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

internal fun authoritativeSessionCatalogPath(pcId: UUID): String =
    "/v1/pcs/$pcId/sessions?include_ended=true"

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

internal data class StreamedHistoryCatalogPage<Candidate>(
    val items: List<Candidate>,
    val hasMore: Boolean,
    val nextCursor: String?,
)

/**
 * Waits for a durable catalog head without imposing an event, elapsed-time, or
 * progress-stall limit. A producer can remain healthy while a slow encrypted
 * batch is being persisted and committed; cancellation is the only local stop
 * condition until the requested durable prefix becomes visible.
 */
internal suspend fun <Page> awaitStreamedHistoryCatalogHead(
    minimumTarget: ULong,
    loadPage: suspend () -> Page,
    newestEndSequence: (Page) -> ULong?,
    pollMillis: Long = TERMINAL_STREAMED_REPLAY_READINESS_POLL_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
): Page {
    require(minimumTarget > 0uL) { "Streamed history target must be positive" }
    require(pollMillis >= 0L) { "Streamed history poll delay must not be negative" }

    while (true) {
        currentCoroutineContext().ensureActive()
        val page = loadPage()
        val newestEnd = newestEndSequence(page)
        if (newestEnd != null && newestEnd >= minimumTarget) return page
        pause(pollMillis)
    }
}

private fun JsonObject.toStreamedHistoryCatalogPage(): StreamedHistoryCatalogPage<JsonObject> =
    StreamedHistoryCatalogPage(
        items = boundedChunkHistoryItems(this),
        hasMore = getValue("has_more").jsonPrimitive.booleanOrNull
            ?: error("Chunk history has_more must be a boolean"),
        nextCursor = this["next_cursor"]?.jsonPrimitive?.contentOrNull,
    )

/**
 * Walks one immutable newest-to-oldest catalog revision and persists every
 * candidate needed to reach [stopAfterSequence]. The algorithm retains only
 * the current bounded page and candidate; neither the number of pages nor the
 * total event count is a failure condition.
 */
internal suspend fun <Candidate> cacheContiguousHistoryRange(
    minimumTarget: ULong,
    stopAfterSequence: ULong,
    firstPage: StreamedHistoryCatalogPage<Candidate>,
    loadNextPage: suspend (String) -> StreamedHistoryCatalogPage<Candidate>,
    sequenceRange: (Candidate) -> HistorySequenceRange?,
    validateTargetBeforeCaching: (ULong) -> Unit = {},
    cacheCandidate: suspend (Candidate) -> Unit,
): ULong {
    require(minimumTarget > stopAfterSequence) { "Streamed history target must advance" }
    var page = firstPage
    var previousCursor: String? = null
    var target: ULong? = null
    var expectedEnd: ULong? = null

    while (true) {
        currentCoroutineContext().ensureActive()
        if (page.items.isEmpty()) throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
        for (candidate in page.items) {
            currentCoroutineContext().ensureActive()
            val range = sequenceRange(candidate)
                ?: throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            if (range.start == 0uL || range.endInclusive < range.start) {
                throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            }
            if (target == null) {
                if (range.endInclusive < minimumTarget) {
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                }
                // A bounded Snapshot suffix supplies its immutable-catalog
                // ceiling here. Rejecting before the first cache callback keeps
                // both network work and Room writes inside the advertised cap.
                validateTargetBeforeCaching(range.endInclusive)
                target = range.endInclusive
                expectedEnd = range.endInclusive
            }
            val expected = requireNotNull(expectedEnd)
            if (expected <= stopAfterSequence) {
                // The catalog is newest-to-oldest. Reaching the Snapshot
                // boundary is not enough to return yet: another item on this
                // page (or the first item on the next page) could overlap the
                // same boundary. Accept only after observing an item wholly at
                // or before the anchor, or the immutable catalog ending.
                if (range.endInclusive > stopAfterSequence) {
                    throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
                }
                return requireNotNull(target)
            }
            val requiredBoundary = stopAfterSequence + 1uL
            if (range.endInclusive != expected) {
                throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            }
            cacheCandidate(candidate)
            expectedEnd = if (range.start <= requiredBoundary) {
                stopAfterSequence
            } else {
                range.start - 1uL
            }
        }
        if (expectedEnd == stopAfterSequence && !page.hasMore) return requireNotNull(target)
        if (!page.hasMore) throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
        val next = page.nextCursor?.takeIf(String::isNotBlank)
            ?: error("Chunk history page omitted its continuation cursor")
        if (next == previousCursor) error("Chunk history cursor did not advance")
        previousCursor = next
        page = loadNextPage(next)
        yield()
    }
}

internal data class HistorySequenceRange(val start: ULong, val endInclusive: ULong)

/**
 * Validates the complete immutable Chunk while applying only the suffix after
 * an already-installed Snapshot. Validating the skipped prefix preserves the
 * catalog's fail-closed integrity guarantee without rendering any event twice.
 */
internal suspend fun <Event> applyContiguousHistoryChunkSuffix(
    startSequence: ULong,
    endSequence: ULong,
    nextSequence: ULong,
    events: List<Event>,
    eventSequence: (Event) -> ULong,
    initialLocalInputEpoch: Long,
    applyEvent: suspend (Event) -> Long,
): Long {
    if (events.isEmpty() ||
        startSequence == 0uL ||
        startSequence > nextSequence ||
        nextSequence > endSequence
    ) throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)

    var expected = startSequence
    var localInputEpoch = initialLocalInputEpoch
    for (event in events) {
        val sequence = eventSequence(event)
        if (sequence != expected || sequence > endSequence) {
            throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
        }
        if (sequence >= nextSequence) {
            localInputEpoch = maxOf(localInputEpoch, applyEvent(event))
        }
        expected++
    }
    if (expected - 1uL != endSequence) {
        throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
    }
    return localInputEpoch
}

internal data class BoundedEventReplayCatalogObservation<Candidate>(
    val authoritativeTail: ULong,
    val candidates: List<Candidate>,
)

internal data class BoundedEventReplayCatalogResult<Candidate>(
    val target: ULong,
    val candidates: List<Candidate>,
    val readiness: BoundedEventReplayReadiness,
)

internal data class BoundedTailReplaySelection<Candidate>(
    val target: ULong,
    val start: ULong? = null,
    val candidates: List<Candidate> = emptyList(),
    val eventCount: Int = 0,
    val readiness: BoundedEventReplayReadiness,
)

internal data class BoundedTailReplayCatalogObservation<Candidate>(
    val authoritativeTail: ULong,
    val candidates: List<Candidate>,
)

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
 * Selects a whole-Chunk suffix ending exactly at [authoritativeTail]. Older
 * chunks are added only while the resulting window remains bounded. A gap or
 * overlap inside the selected suffix is rejected; history before its start is
 * deliberately outside the TailWindow contract.
 */
internal fun <Candidate> boundedTailReplaySelection(
    candidates: List<Candidate>,
    authoritativeTail: ULong,
    sequenceRange: (Candidate) -> HistorySequenceRange?,
    maximumEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
): BoundedTailReplaySelection<Candidate> {
    require(authoritativeTail > 0uL) { "Tail replay target must be positive" }
    require(maximumEvents > 0) { "Tail replay event limit must be positive" }
    if (candidates.isEmpty()) {
        return BoundedTailReplaySelection(
            target = authoritativeTail,
            readiness = BoundedEventReplayReadiness.NOT_YET_COMPLETE,
        )
    }
    val ranged = candidates.map { candidate ->
        val range = sequenceRange(candidate) ?: return BoundedTailReplaySelection(
            target = authoritativeTail,
            readiness = BoundedEventReplayReadiness.GAP,
        )
        if (range.start == 0uL || range.endInclusive < range.start) {
            return BoundedTailReplaySelection(
                target = authoritativeTail,
                readiness = BoundedEventReplayReadiness.GAP,
            )
        }
        candidate to range
    }.sortedWith(
        compareBy<Pair<Candidate, HistorySequenceRange>> { it.second.start }
            .thenBy { it.second.endInclusive },
    )
    val newest = ranged.last()
    if (newest.second.endInclusive != authoritativeTail) {
        // The independently published directory may trail the session row, or
        // the row may have advanced while this catalog page was in flight.
        return BoundedTailReplaySelection(
            target = authoritativeTail,
            readiness = BoundedEventReplayReadiness.NOT_YET_COMPLETE,
        )
    }

    fun eventCount(range: HistorySequenceRange): ULong =
        range.endInclusive - range.start + 1uL

    var selectedIndex = ranged.lastIndex
    var selectedCount = eventCount(newest.second)
    if (selectedCount > maximumEvents.toULong()) {
        return BoundedTailReplaySelection(
            target = authoritativeTail,
            readiness = BoundedEventReplayReadiness.LIMIT_EXCEEDED,
        )
    }
    while (selectedIndex > 0) {
        val older = ranged[selectedIndex - 1].second
        val current = ranged[selectedIndex].second
        val olderCount = eventCount(older)
        // An overlap reaches into the window we already selected and is
        // ambiguous even when the older object would exceed the size budget.
        if (older.endInclusive >= current.start) {
            return BoundedTailReplaySelection(
                target = authoritativeTail,
                readiness = BoundedEventReplayReadiness.GAP,
            )
        }
        if (olderCount > maximumEvents.toULong() - selectedCount) break
        if (older.endInclusive == ULong.MAX_VALUE || older.endInclusive + 1uL != current.start) {
            return BoundedTailReplaySelection(
                target = authoritativeTail,
                readiness = BoundedEventReplayReadiness.GAP,
            )
        }
        selectedCount += olderCount
        selectedIndex--
    }
    val selected = ranged.subList(selectedIndex, ranged.size)
    return BoundedTailReplaySelection(
        target = authoritativeTail,
        start = selected.first().second.start,
        candidates = selected.map { it.first },
        eventCount = selectedCount.toInt(),
        readiness = BoundedEventReplayReadiness.READY,
    )
}

/**
 * Waits for the newest Chunk directory to catch the authoritative session
 * watermark. Cancellation remains caller-owned; transient metadata errors and
 * temporary publication gaps are retried only inside the supplied deadline.
 */
internal suspend fun <Candidate> awaitBoundedTailReplayCatalog(
    initialTarget: ULong,
    latestObservation: suspend () -> BoundedTailReplayCatalogObservation<Candidate>,
    sequenceRange: (Candidate) -> HistorySequenceRange?,
    retryDelaysMillis: List<Long>,
    timeoutMillis: Long,
    maximumEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
    pause: suspend (Long) -> Unit = { delay(it) },
    onObservationFailure: (Exception) -> Unit = {},
): BoundedTailReplaySelection<Candidate> {
    require(initialTarget > 0uL) { "Tail replay target must be positive" }
    require(retryDelaysMillis.all { it >= 0L }) { "Tail readiness delays must not be negative" }
    require(timeoutMillis > 0L) { "Tail readiness timeout must be positive" }

    var result = BoundedTailReplaySelection<Candidate>(
        target = initialTarget,
        readiness = BoundedEventReplayReadiness.NOT_YET_COMPLETE,
    )
    val settled = withTimeoutOrNull(timeoutMillis) {
        for (attempt in 0..retryDelaysMillis.size) {
            val observation = try {
                latestObservation()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                onObservationFailure(error)
                null
            }
            if (observation != null) {
                val target = maxOf(result.target, observation.authoritativeTail)
                result = boundedTailReplaySelection(
                    observation.candidates,
                    target,
                    sequenceRange,
                    maximumEvents,
                )
                if (result.readiness == BoundedEventReplayReadiness.READY ||
                    result.readiness == BoundedEventReplayReadiness.LIMIT_EXCEEDED
                ) return@withTimeoutOrNull result
            }
            if (attempt < retryDelaysMillis.size) pause(retryDelaysMillis[attempt])
        }
        null
    }
    return settled ?: result
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

/**
 * Polls the independently published Chunk catalog until it proves a complete
 * prefix through the latest tail observed before that catalog read. A caller
 * may supply a shorter timeout to share one absolute bootstrap deadline with
 * Snapshot readiness. Metadata failures remain retryable inside that window;
 * caller cancellation is never converted into a readiness result.
 */
internal suspend fun <Candidate> awaitBoundedEventReplayCatalog(
    initialTarget: ULong,
    initialCandidates: List<Candidate>,
    sequenceRange: (Candidate) -> HistorySequenceRange?,
    latestObservation: suspend () -> BoundedEventReplayCatalogObservation<Candidate>,
    retryDelaysMillis: List<Long>,
    timeoutMillis: Long,
    pause: suspend (Long) -> Unit = { delay(it) },
    onObservationFailure: (Exception) -> Unit = {},
): BoundedEventReplayCatalogResult<Candidate> {
    require(initialTarget > 0uL) { "Bounded replay target must be positive" }
    require(retryDelaysMillis.all { it >= 0L }) { "Chunk readiness delays must not be negative" }
    require(timeoutMillis > 0L) { "Chunk readiness timeout must be positive" }

    var result = BoundedEventReplayCatalogResult(
        target = initialTarget,
        candidates = initialCandidates,
        readiness = boundedEventReplayReadiness(initialCandidates.mapNotNull(sequenceRange), initialTarget),
    )
    if (result.readiness == BoundedEventReplayReadiness.READY ||
        result.readiness == BoundedEventReplayReadiness.LIMIT_EXCEEDED
    ) return result

    val settled = withTimeoutOrNull(timeoutMillis) {
        for (attempt in 0..retryDelaysMillis.size) {
            val observation = try {
                latestObservation()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                onObservationFailure(error)
                null
            }
            if (observation != null) {
                val target = maxOf(result.target, observation.authoritativeTail)
                val readiness = boundedEventReplayReadiness(
                    observation.candidates.mapNotNull(sequenceRange),
                    target,
                )
                result = BoundedEventReplayCatalogResult(target, observation.candidates, readiness)
                if (readiness == BoundedEventReplayReadiness.READY ||
                    readiness == BoundedEventReplayReadiness.LIMIT_EXCEEDED
                ) {
                    return@withTimeoutOrNull result
                }
            }
            if (attempt < retryDelaysMillis.size) pause(retryDelaysMillis[attempt])
        }
        null
    }
    return settled ?: result
}

/** True only when realtime can continuously replay the suffix after a full prefix restore. */
internal fun fullPrefixHasBoundedRealtimeSuffix(
    restoredThroughSequence: ULong,
    authoritativeTail: ULong,
    maximumSuffixEvents: Int = TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS,
): Boolean {
    require(maximumSuffixEvents > 0) { "Realtime suffix limit must be positive" }
    return restoredThroughSequence > 0uL &&
        restoredThroughSequence <= authoritativeTail &&
        authoritativeTail - restoredThroughSequence <= maximumSuffixEvents.toULong()
}

internal enum class FullPrefixRealtimeSuffixValidation {
    READY,
    UNAVAILABLE,
    OUTSIDE_WINDOW,
}

/** Re-reads the tail after replay so validation cannot accidentally use its old target. */
internal suspend fun validateFullPrefixRealtimeSuffix(
    restoredThroughSequence: ULong,
    latestAuthoritativeTail: suspend () -> ULong?,
): FullPrefixRealtimeSuffixValidation {
    val authoritativeTail = latestAuthoritativeTail()
        ?: return FullPrefixRealtimeSuffixValidation.UNAVAILABLE
    return if (fullPrefixHasBoundedRealtimeSuffix(restoredThroughSequence, authoritativeTail)) {
        FullPrefixRealtimeSuffixValidation.READY
    } else {
        FullPrefixRealtimeSuffixValidation.OUTSIDE_WINDOW
    }
}

internal fun tailReplayHasBoundedRealtimeSuffix(
    restoredThroughSequence: ULong,
    authoritativeTail: ULong,
    maximumSuffixEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
): Boolean {
    require(maximumSuffixEvents > 0) { "Realtime suffix limit must be positive" }
    return restoredThroughSequence > 0uL &&
        restoredThroughSequence <= authoritativeTail &&
        authoritativeTail - restoredThroughSequence <= maximumSuffixEvents.toULong()
}

/** Re-reads the authority after TailWindow rendering and before its first ACK. */
internal suspend fun validateTailReplayRealtimeSuffix(
    restoredThroughSequence: ULong,
    latestAuthoritativeTail: suspend () -> ULong?,
): FullPrefixRealtimeSuffixValidation {
    val authoritativeTail = latestAuthoritativeTail()
        ?: return FullPrefixRealtimeSuffixValidation.UNAVAILABLE
    return if (tailReplayHasBoundedRealtimeSuffix(restoredThroughSequence, authoritativeTail)) {
        FullPrefixRealtimeSuffixValidation.READY
    } else {
        FullPrefixRealtimeSuffixValidation.OUTSIDE_WINDOW
    }
}

internal const val TERMINAL_BOUNDED_REPLAY_MAX_EVENTS = 4096
internal const val TERMINAL_BOUNDED_REPLAY_MAX_CATALOG_PAGES =
    TERMINAL_BOUNDED_REPLAY_MAX_EVENTS / SESSION_CHUNK_HISTORY_PAGE_LIMIT + 2
internal const val TERMINAL_TAIL_REPLAY_MAX_CATALOG_PAGES = 2
internal const val TERMINAL_TAIL_REPLAY_READINESS_TIMEOUT_MILLIS = 15_000L
internal const val TERMINAL_STREAMED_REPLAY_READINESS_TIMEOUT_MILLIS = 15_000L
internal const val TERMINAL_STREAMED_REPLAY_READINESS_POLL_MILLIS = 250L

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

/**
 * Moves a detached transcript to a newer authenticated Snapshot only after the
 * live renderer has durably acknowledged that Snapshot. This closes the race
 * where tail-first opened S while the Agent replaced S with S+N before S's
 * bulk transcript pages finished publishing.
 */
internal fun rebaseTranscriptViewportToNewerAnchor(
    viewport: TranscriptViewportState,
    newSnapshotSequence: ULong,
    newInitialBeforeLineId: ULong,
    durableLiveAcknowledgement: ULong,
): TranscriptViewportState? {
    if (newSnapshotSequence <= viewport.snapshotAnchorSequence ||
        newSnapshotSequence > durableLiveAcknowledgement ||
        newInitialBeforeLineId == 0uL
    ) return null
    return viewport.copy(
        snapshotAnchorSequence = newSnapshotSequence,
        initialBeforeLineId = newInitialBeforeLineId,
        nextBeforeLineId = newInitialBeforeLineId,
        nextCursor = null,
        pages = emptyList(),
        loadedObjectIds = emptySet(),
        consumedCursors = emptySet(),
        loading = false,
        error = null,
        hasMoreBefore = true,
        viewportGeneration = Math.addExact(viewport.viewportGeneration, 1L),
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
 * A ready empty response is authoritative completion for its fixed Snapshot
 * anchor even when its line cursor is greater than one. The caller may still
 * move to a newer durably acknowledged Snapshot before ending pagination.
 */
internal suspend fun <Page> awaitExpectedTranscriptPage(
    retryDelaysMillis: List<Long> = TRANSCRIPT_PUBLICATION_RETRY_DELAYS_MILLIS,
    readinessTimeoutMillis: Long = TRANSCRIPT_PUBLICATION_READINESS_TIMEOUT_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
    fetch: suspend () -> TranscriptSelection<Page>,
): TranscriptSelection<Page> {
    require(retryDelaysMillis.all { it >= 0L }) { "Transcript retry delays must not be negative" }
    require(readinessTimeoutMillis > 0L) { "Transcript readiness timeout must be positive" }
    val settled = withTimeoutOrNull(readinessTimeoutMillis) {
        for (attempt in 0..retryDelaysMillis.size) {
            val selected = fetch()
            if (selected !== TranscriptSelection.Pending) return@withTimeoutOrNull selected
            if (attempt < retryDelaysMillis.size) pause(retryDelaysMillis[attempt])
        }
        TranscriptSelection.Pending
    }
    return settled ?: TranscriptSelection.Pending
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
 * Host-owned history cannot be deleted from the catalog until Windows has
 * acknowledged the encrypted explicit-close request. A transport timeout,
 * offline PC, or negative acknowledgement therefore leaves both remote and
 * local state untouched.
 */
internal suspend fun deleteHostHistorySessionAfterTermination(
    terminateRemote: suspend () -> Unit,
    deleteRemote: suspend () -> Unit,
    deleteLocal: suspend () -> Unit,
) {
    terminateRemote()
    deleteSessionAfterRemoteSuccess(deleteRemote, deleteLocal)
}

/** HTTP 426 permanently identifies the removed legacy server-owned history catalog. */
internal fun legacyHistoryCatalogUnavailable(error: TermLinkApiException): Boolean =
    error.status == 426

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

internal fun snapshotCanResumeThroughDurableAcknowledgement(
    snapshotSequence: ULong,
    durableAcknowledgement: ULong,
): Boolean = snapshotSequence <= durableAcknowledgement

/**
 * REST exposes the last output sequence, while the durable renderer ACK also
 * covers ordered non-output events such as title, lease, and lifecycle frames.
 * A previously committed ACK is therefore a valid lower bound for the Host
 * History event range even when it is newer than the catalog output cursor.
 */
internal fun restorableSessionEventTail(
    authoritativeOutputTail: ULong,
    durableAcknowledgement: ULong,
): ULong = maxOf(authoritativeOutputTail, durableAcknowledgement)

internal fun snapshotMatchesDurableAcknowledgement(
    snapshotSequence: ULong,
    durableAcknowledgement: ULong,
): Boolean = snapshotSequence == durableAcknowledgement

internal fun snapshotSuffixRestoreReady(
    restoredThroughSequence: ULong,
    durableAcknowledgement: ULong,
    authoritativeTail: ULong,
): Boolean = restoredThroughSequence >= durableAcknowledgement &&
    tailReplayHasBoundedRealtimeSuffix(restoredThroughSequence, authoritativeTail)

internal fun snapshotSuffixRestoreIsBounded(
    snapshotSequence: ULong,
    durableAcknowledgement: ULong,
    authoritativeTail: ULong,
    maximumEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
): Boolean {
    require(maximumEvents > 0) { "Snapshot suffix limit must be positive" }
    return snapshotSequence > 0uL &&
        snapshotSequence <= durableAcknowledgement &&
        durableAcknowledgement <= authoritativeTail &&
        authoritativeTail - snapshotSequence <= maximumEvents.toULong()
}

internal fun snapshotSuffixReplayTargetIsBounded(
    snapshotSequence: ULong,
    replayTarget: ULong,
    maximumEvents: Int = TERMINAL_BOUNDED_REPLAY_MAX_EVENTS,
): Boolean {
    require(maximumEvents > 0) { "Snapshot suffix limit must be positive" }
    return snapshotSequence > 0uL &&
        replayTarget >= snapshotSequence &&
        replayTarget - snapshotSequence <= maximumEvents.toULong()
}

internal fun fullPrefixReplayCanPreserveDurableAcknowledgement(
    durableAcknowledgement: ULong,
): Boolean = durableAcknowledgement == 0uL

internal fun terminalSnapshotRuntimeNegotiated(
    localRuntimeEnabled: Boolean,
    pcCapabilities: Set<String>,
): Boolean = localRuntimeEnabled &&
    dev.termlink.model.PcCapabilityIds.TERMINAL_SNAPSHOT_V2 in pcCapabilities

/**
 * A durable local binding is only one recovery candidate. If the encrypted
 * Chunk suffix behind that binding has been compacted or was never cached, a
 * newer remote Snapshot can still restore the same durable ACK without losing
 * state. Only the explicit catalog-gap result is replaceable; integrity,
 * crypto, key, storage, and renderer failures remain fail-closed.
 */
internal suspend fun <T> restoreReplaceableBoundCheckpoint(
    restoreBound: suspend () -> T?,
    abandonRenderer: () -> Unit,
): T? = try {
    restoreBound()
} catch (error: SessionOpenException) {
    if (error.failure.code != SessionOpenFailureCode.HISTORY_GAP) throw error
    abandonRenderer()
    null
}

/** Encodes the production ordering: local binding, reset on H114, then remote V2. */
internal suspend fun <T> restoreReplaceableBoundOrRemoteCheckpoint(
    negotiated: Boolean,
    restoreBound: suspend () -> T?,
    abandonRenderer: () -> Unit,
    restoreRemote: suspend () -> T?,
): T? {
    if (!negotiated) return null
    restoreReplaceableBoundCheckpoint(restoreBound, abandonRenderer)?.let { return it }
    return restoreRemote()
}

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

/**
 * A permanent unsupported decision settles Snapshot polling immediately. The
 * caller then spends the remainder of the same bounded readiness deadline
 * polling the independent Chunk catalog instead of blindly waiting here.
 */
internal fun snapshotReadinessIsSettled(
    snapshotAvailable: Boolean,
    failure: SessionOpenFailure?,
): Boolean {
    if (failure?.code == SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED) return true
    return snapshotAvailable
}

internal data class TerminalSnapshotReadinessResult<out Snapshot>(
    val snapshot: Snapshot?,
    val failure: SessionOpenFailure?,
)

/**
 * Keeps the latest actionable metadata failure across the bounded publication
 * wait while still allowing a later successful Snapshot to win. Caller
 * cancellation is never converted into readiness exhaustion.
 *
 * Reference audit: termux/termux-app@3df69d1da197dd9bd71a3bafd902dffd720576b4
 * has no remote Snapshot readiness path to reuse. Its local TerminalSession,
 * network, and authentication architecture are explicitly out of scope here;
 * only the boundary that transient peripheral failure must not corrupt or
 * replace renderer state is used as behavioral guidance.
 */
internal suspend fun <Observation, Snapshot> awaitTerminalSnapshotReadiness(
    latestObservation: suspend () -> Observation?,
    snapshotOf: (Observation) -> Snapshot?,
    lastOutputSequenceOf: (Observation) -> ULong,
    failureOf: (Observation) -> SessionOpenFailure?,
    retryDelaysMillis: List<Long>,
    timeoutMillis: Long,
    pause: suspend (Long) -> Unit = { delay(it) },
    ensureObservationContext: () -> Unit = {},
    observationFailure: ((Exception) -> SessionOpenFailure)? = null,
): TerminalSnapshotReadinessResult<Snapshot> {
    require(retryDelaysMillis.all { it >= 0L }) { "Snapshot readiness delays must not be negative" }
    require(timeoutMillis > 0L) { "Snapshot readiness timeout must be positive" }

    var settled: TerminalSnapshotReadinessResult<Snapshot>? = null
    var lastFailure: SessionOpenFailure? = null
    suspend fun poll() {
        for (attempt in 0..retryDelaysMillis.size) {
            currentCoroutineContext().ensureActive()
            ensureObservationContext()
            val observation = try {
                latestObservation()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Do not downgrade an authentication/account generation switch
                // into a retryable Snapshot metadata failure.
                ensureObservationContext()
                val classifier = observationFailure ?: throw error
                lastFailure = classifier(error)
                null
            }
            ensureObservationContext()
            if (observation != null) {
                val snapshot = snapshotOf(observation)
                val failure = failureOf(observation)
                when {
                    snapshot != null -> {
                        settled = TerminalSnapshotReadinessResult(snapshot, failure = null)
                        return
                    }
                    lastOutputSequenceOf(observation) == 0uL -> {
                        settled = TerminalSnapshotReadinessResult(snapshot = null, failure = null)
                        return
                    }
                    failure?.code == SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED -> {
                        settled = TerminalSnapshotReadinessResult(snapshot = null, failure = failure)
                        return
                    }
                    else -> lastFailure = failure
                }
            }
            if (attempt < retryDelaysMillis.size) {
                ensureObservationContext()
                pause(retryDelaysMillis[attempt])
                ensureObservationContext()
            }
        }
    }

    withTimeoutOrNull(timeoutMillis) { poll() }
    return settled ?: TerminalSnapshotReadinessResult(snapshot = null, failure = lastFailure)
}

internal fun throwTerminalSnapshotReadinessFailureUnlessUnsupported(failure: SessionOpenFailure?) {
    if (failure != null && failure.code != SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED) {
        throw SessionOpenException(failure)
    }
}

internal fun terminalSnapshotReadinessBlocksChunkFallback(
    durableAcknowledgement: ULong,
    failure: SessionOpenFailure?,
): Boolean = failure != null &&
    failure.code != SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED &&
    !fullPrefixReplayCanPreserveDurableAcknowledgement(durableAcknowledgement)

internal const val SESSION_BOOTSTRAP_IN_PROGRESS_REASON = "snapshot bootstrap in progress"
internal const val SESSION_BOOTSTRAP_FAILED_REASON = "snapshot bootstrap failed"

internal suspend fun <T> awaitHostHistoryReady(
    retryDelaysMillis: List<Long> = HOST_HISTORY_READINESS_RETRY_DELAYS_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
    load: suspend () -> T,
): T {
    require(retryDelaysMillis.all { it >= 0L }) { "Host history readiness delays must not be negative" }
    for (attempt in 0..retryDelaysMillis.size) {
        currentCoroutineContext().ensureActive()
        try {
            return load()
        } catch (error: ProductionHostHistoryNotReadyException) {
            if (attempt == retryDelaysMillis.size) throw error
        }
        pause(retryDelaysMillis[attempt])
    }
    error("Host history readiness loop exhausted unexpectedly")
}

internal val HOST_HISTORY_READINESS_RETRY_DELAYS_MILLIS =
    listOf(100L, 200L, 400L, 800L, 1_500L, 2_000L, 4_000L)

/**
 * Bounded catch-up window for the snapshot-vs-catalog authority check. The
 * Agent flushes session catalog metadata on a heartbeat cadence (about 15
 * seconds), so a snapshot served right after session spawn can run ahead of
 * the catalog tail for one flush interval; the schedule covers several such
 * intervals before the restore fails closed.
 */
internal val AUTHORITY_TAIL_CATCHUP_RETRY_DELAYS_MILLIS =
    listOf(500L, 1_000L, 2_000L, 4_000L, 8_000L, 15_000L, 15_000L, 15_000L)

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
internal const val TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS = 5_000L
internal const val TRANSCRIPT_PAGE_LOAD_FAILED_CODE = "TRANSCRIPT_PAGE_LOAD_FAILED"
internal const val TRANSCRIPT_PUBLICATION_READINESS_TIMEOUT_MILLIS =
    TERMINAL_STREAMED_REPLAY_READINESS_TIMEOUT_MILLIS
// The schedule deliberately extends beyond the absolute deadline. This keeps
// polling until the full readiness window expires without allowing a slow
// selector request or an unexpectedly fast test pause to make the wait unbounded.
internal val TRANSCRIPT_PUBLICATION_RETRY_DELAYS_MILLIS = listOf(
    100L, 200L, 400L, 800L, 1_000L,
    1_500L, 1_500L, 1_500L, 1_500L, 1_500L,
    1_500L, 1_500L, 1_500L, 1_500L,
)
internal const val MAX_TRANSCRIPT_ANCHOR_REBASES = 8
// Matches one Windows realtime flow-control window. This lets an active
// terminal advance during Snapshot publication without permitting a large
// historical replay to delay or disconnect Android first-open.
internal const val TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS = 128

/**
 * Separates reversible Snapshot work from the durable ACK/checkpoint bind.
 * Once [bind] succeeds its cursor is monotonic: an installation failure is
 * propagated so no socket can subscribe until a later open retries the same
 * durable binding successfully.
 */
internal suspend fun <Loaded, Prepared, Bound, Restored> prepareBindInstallSnapshot(
    loadAndCache: suspend () -> Loaded,
    prepare: suspend (Loaded) -> Prepared,
    preparedIsCurrent: suspend (Prepared) -> Boolean = { true },
    bind: suspend () -> Bound,
    installAndMap: suspend (Prepared, Bound) -> Restored,
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
        installAndMap(prepared, bound)
    }
}

internal val CONTROL_LEASE_HELD_RETRY_DELAYS_MILLIS = listOf(1_000L, 2_000L, 4_000L, 8_000L, 15_000L, 5_000L)
internal val CONTROL_LEASE_TRANSIENT_RETRY_DELAYS_MILLIS = listOf(500L, 1_000L, 2_000L)

internal fun sessionFailuresAfterRuntimeState(
    failures: Map<UUID, SessionOpenFailure>,
    sessionId: UUID,
    state: RemoteSessionState,
): Map<UUID, SessionOpenFailure> =
    if (state is RemoteSessionState.Controlling) failures - sessionId else failures

private fun retryableTransientControlFailure(error: Throwable): Boolean = when (error) {
    is IOException -> true
    is TermLinkApiException -> error.status == 408 || error.status == 425 || error.status == 429 ||
        error.status in 500..599
    else -> false
}

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
    transientRetryDelaysMillis: List<Long> = CONTROL_LEASE_TRANSIENT_RETRY_DELAYS_MILLIS,
    pause: suspend (Long) -> Unit = { delay(it) },
): Boolean {
    if (remoteState() is RemoteSessionState.Controlling) return false

    var heldRetryIndex = 0
    var transientRetryIndex = 0
    while (true) {
        val retryDelay = try {
            request()
            return true
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            when {
                error is TermLinkApiException && error.status == 409 && error.code == "LEASE_HELD" ->
                    retryDelaysMillis.getOrNull(heldRetryIndex++) ?: throw error
                retryableTransientControlFailure(error) ->
                    transientRetryDelaysMillis.getOrNull(transientRetryIndex++) ?: throw error
                else -> throw error
            }
        }
        pause(retryDelay)
        if (remoteState() is RemoteSessionState.Controlling) return false
    }
}

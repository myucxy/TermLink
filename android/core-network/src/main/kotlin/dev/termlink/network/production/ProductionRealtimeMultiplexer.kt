package dev.termlink.network.production

import android.util.Log
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.Envelope
import java.io.Closeable
import java.util.LinkedHashMap
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient

fun interface RealtimeAuthenticationRefresher {
    suspend fun refreshAccessToken()
}

data class ProductionRealtimeMultiplexerDiagnostics(
    val phase: String,
    val endpointCount: Int,
    val socket: ProductionRealtimeSocketDiagnostics,
    val routeDecisions: ProductionRealtimeRouteDecisionCounters = ProductionRealtimeRouteDecisionCounters(),
)

data class ProductionRealtimeRouteDecisionCounters(
    val noEndpoint: Long = 0,
    val endpointClosed: Long = 0,
    val endpointNotSubscribed: Long = 0,
    val rejectedGeneration: Long = 0,
    val overflow: Long = 0,
)

internal enum class ProductionRealtimeRouteDecision {
    DELIVERED,
    NO_ENDPOINT,
    ENDPOINT_CLOSED,
    ENDPOINT_NOT_SUBSCRIBED,
    REJECTED_GENERATION,
    OVERFLOW,
}

/** A side-effect-free classifier keeps every non-delivery decision testable. */
internal fun classifyProductionRealtimeRoute(
    endpointPresent: Boolean,
    endpointClosed: Boolean = false,
    generationRejected: Boolean = false,
    overflow: Boolean = false,
): ProductionRealtimeRouteDecision = when {
    !endpointPresent -> ProductionRealtimeRouteDecision.NO_ENDPOINT
    endpointClosed -> ProductionRealtimeRouteDecision.ENDPOINT_CLOSED
    generationRejected -> ProductionRealtimeRouteDecision.REJECTED_GENERATION
    overflow -> ProductionRealtimeRouteDecision.OVERFLOW
    else -> ProductionRealtimeRouteDecision.DELIVERED
}

/**
 * One session-scoped view over the device WebSocket. Closing this endpoint only
 * unregisters its UUID; ownership of the socket remains at device scope.
 */
interface ProductionRealtimeSessionTransport : Closeable {
    val receivedEnvelopes: Flow<ProductionReceivedEnvelope>
    val receivePressure: StateFlow<ProductionReceivePressure>
    val state: SharedFlow<RealtimeState>
    val connectionEvents: Flow<ProductionRealtimeSessionConnectionEvent>
        get() = state.map { state ->
            ProductionRealtimeSessionConnectionEvent(
                state = state,
                connectedGeneration = if (state is RealtimeState.Connected) {
                    currentConnectedGeneration()
                } else {
                    0
                },
            )
        }

    fun connect()
    fun send(envelope: Envelope): Boolean
    fun sendForConnectedGeneration(envelope: Envelope, expectedGeneration: Int): Boolean {
        if (expectedGeneration <= 0 || currentConnectedGeneration() != expectedGeneration) return false
        return send(envelope)
    }
    fun disconnect(reason: String)
    fun currentConnectedGeneration(): Int
    fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean
    /** Unregisters without treating already-in-flight post-UNSUBSCRIBE frames as fatal. */
    fun closeAfterUnsubscribe() = close()
    fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics = ProductionRealtimeMultiplexerDiagnostics(
        phase = "UNKNOWN",
        endpointCount = 0,
        socket = ProductionRealtimeSocketDiagnostics(0, 0, false, "UNKNOWN", 0, 0),
    )
}

data class ProductionRealtimeSessionConnectionEvent(
    val state: RealtimeState,
    val connectedGeneration: Int,
) {
    init {
        require(connectedGeneration >= 0) { "connectedGeneration must not be negative" }
    }
}

/**
 * Owns the single production WebSocket for one Android device and routes opaque
 * envelopes to the live client registered for the exact session UUID.
 */
class ProductionRealtimeMultiplexer private constructor(
    private val socket: ProductionRealtimeSocket,
    parentScope: CoroutineScope,
    private val authenticationRefresher: RealtimeAuthenticationRefresher,
    private val authenticationFailureObserver: (Throwable) -> Unit,
) : Closeable {
    constructor(
        rootUrl: String,
        tokenProvider: AccessTokenProvider,
        deviceId: UUID,
        identity: DeviceIdentity,
        parentScope: CoroutineScope,
        additionalCapabilities: Set<String> = emptySet(),
        httpClient: OkHttpClient = ProductionRealtimeClient.defaultHttpClient,
        allowCleartextForLocalTest: Boolean = false,
        authenticationRefresher: RealtimeAuthenticationRefresher = RealtimeAuthenticationRefresher { },
        authenticationFailureObserver: (Throwable) -> Unit = { },
    ) : this(
        socket = ProductionRealtimeClient(
            rootUrl = rootUrl,
            tokenProvider = tokenProvider,
            deviceId = deviceId,
            identity = identity,
            role = "android",
            additionalCapabilities = additionalCapabilities,
            httpClient = httpClient,
            allowCleartextForLocalTest = allowCleartextForLocalTest,
        ),
        parentScope = parentScope,
        authenticationRefresher = authenticationRefresher,
        authenticationFailureObserver = authenticationFailureObserver,
    )

    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val closed = AtomicBoolean(false)
    private val connectionLock = Any()
    private val endpointLock = Any()
    private val endpoints = mutableMapOf<UUID, Endpoint>()
    private val closedEndpointIncarnations = LinkedHashMap<UUID, ClosedEndpoint>()
    /** Socket-global fence; it intentionally survives endpoint replacement. */
    private val rejectedThroughGeneration = AtomicInteger(0)
    /** Highest socket generation observed by any endpoint incarnation. */
    private val highestObservedGeneration = AtomicInteger(0)
    private val noEndpointCount = AtomicLong(0)
    private val endpointClosedCount = AtomicLong(0)
    private val endpointNotSubscribedCount = AtomicLong(0)
    private val rejectedGenerationCount = AtomicLong(0)
    private val overflowCount = AtomicLong(0)
    private var socketPhase = SocketPhase.IDLE
    /** Every physical connect owns one token, regardless of which session/lifecycle path requested it. */
    private var activeConnectOwner: ConnectOwner? = null
    /** Strongest lifecycle/network reconcile that arrived after the active owner began. */
    private var pendingSocketReconcile: PendingSocketReconcile? = null
    private var nextConnectOwner = 0L
    private var suppressReplacementDisconnectFailure = false
    private var authenticationRecoveryUsed = false
    private var authenticationRecoveryJob: Job? = null
    private val mutableState = MutableSharedFlow<RealtimeState>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    private val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()
    private val mutableConnectionEvents = MutableSharedFlow<ProductionRealtimeSessionConnectionEvent>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    private val connectionEvents: SharedFlow<ProductionRealtimeSessionConnectionEvent> =
        mutableConnectionEvents.asSharedFlow()
    private val routeJob: Job
    private val stateJob: Job

    private fun publishState(state: RealtimeState) {
        val connectedGeneration = if (state is RealtimeState.Connected) {
            socket.diagnostics().connectedGeneration
        } else {
            0
        }
        mutableConnectionEvents.tryEmit(
            ProductionRealtimeSessionConnectionEvent(state, connectedGeneration),
        )
        mutableState.tryEmit(state)
    }

    init {
        routeJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            socket.receivedEnvelopes.collect { received -> route(received) }
        }
        stateJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            socket.state.collect { state ->
                if (closed.get()) return@collect
                if (state is RealtimeState.AuthenticationRejected) {
                    handleAuthenticationRejected(state)
                } else {
                    var settledFollowUp: ConnectOwner? = null
                    synchronized(connectionLock) {
                        if (closed.get()) return@synchronized
                        var publish = true
                        socketPhase = when (state) {
                            RealtimeState.Connecting -> SocketPhase.CONNECTING
                            RealtimeState.Connected -> {
                                authenticationRecoveryUsed = false
                                activeConnectOwner = null
                                suppressReplacementDisconnectFailure = false
                                SocketPhase.CONNECTED
                            }
                            is RealtimeState.Reconnecting -> SocketPhase.CONNECTING
                            is RealtimeState.AuthenticationFailed -> SocketPhase.AUTHENTICATION_FAILED
                            is RealtimeState.Failed -> {
                                if (activeConnectOwner != null && suppressReplacementDisconnectFailure) {
                                    // socket.disconnect() publishes one expected
                                    // failure before the replacement generation
                                    // starts. Do not let every session schedule
                                    // its own competing reconnect for that event.
                                    suppressReplacementDisconnectFailure = false
                                    publish = false
                                    SocketPhase.CONNECTING
                                } else {
                                    activeConnectOwner = null
                                    suppressReplacementDisconnectFailure = false
                                    SocketPhase.FAILED
                                }
                            }
                            RealtimeState.Closed -> SocketPhase.CLOSED
                            is RealtimeState.AuthenticationRejected -> error("handled above")
                        }
                        if (state is RealtimeState.Connected) {
                            // A lifecycle/network replacement queued during this
                            // handshake owns the boundary before observers may act
                            // on Connected. Publishing this generation first would
                            // let a SessionClient send SUBSCRIBE while the endpoint
                            // gate is already being invalidated for its replacement.
                            settledFollowUp = claimPendingSocketReconcileLocked()
                            if (settledFollowUp != null) publish = false
                        }
                        if (publish) publishState(state)
                    }
                    val followUp = settledFollowUp ?: claimPendingSocketReconcile()
                    followUp?.let(::performOwnedSocketAction)
                }
            }
        }
    }

    /** Registers exactly one consumer for [sessionId]. */
    fun register(sessionId: UUID): ProductionRealtimeSessionTransport {
        var replacementOwner: ConnectOwner? = null
        val endpoint = synchronized(connectionLock) {
            check(!closed.get()) { "production realtime multiplexer is closed" }
            synchronized(endpointLock) {
                check(sessionId !in endpoints) { "production session is already registered" }
                val retired = closedEndpointIncarnations.remove(sessionId)
                Endpoint(sessionId, nextEndpointIncarnation()).also { endpoint ->
                    endpoints[sessionId] = endpoint
                    val generation = socket.diagnostics().generation
                    if (retired != null && retired.retiredGeneration > 0 &&
                        generation == retired.retiredGeneration && activeConnectOwner == null &&
                        socketPhase != SocketPhase.CLOSED &&
                        socketPhase != SocketPhase.AUTHENTICATING &&
                        socketPhase != SocketPhase.AUTHENTICATION_FAILED
                    ) {
                        replacementOwner = claimConnectLocked(
                            trigger = "session endpoint replaced on retired socket generation",
                            action = SocketReconcileAction.REPLACE,
                            requiresEndpoint = true,
                        )
                    }
                    logEndpointLifecycle("endpoint_register", endpoint)
                }
            }
        }
        replacementOwner?.let(::performOwnedSocketAction)
        return endpoint
    }

    override fun close() {
        val removed = synchronized(connectionLock) {
            if (!closed.compareAndSet(false, true)) return
            activeConnectOwner = null
            pendingSocketReconcile = null
            suppressReplacementDisconnectFailure = false
            socketPhase = SocketPhase.CLOSED
            publishState(RealtimeState.Closed)
            synchronized(endpointLock) {
                endpoints.values.toList().also { endpoints.clear() }
            }
        }
        removed.forEach { endpoint ->
            logEndpointLifecycle("endpoint_unregister", endpoint)
            endpoint.closeFromOwner()
        }
        authenticationRecoveryJob?.cancel()
        authenticationRecoveryJob = null
        routeJob.cancel()
        stateJob.cancel()
        socket.close()
        scope.cancel()
    }

    /**
     * Android can restore HTTP before OkHttp recognizes that the old socket
     * belonged to a lost network. A confirmed default-network transition is
     * therefore an explicit socket-generation boundary, not merely a hint.
     */
    fun onNetworkAvailable() {
        requestSocketReconcile(
            trigger = "default network became available",
            forceReplacement = true,
            networkUsable = true,
        )
    }

    /**
     * Reconciles the shared physical socket after Activity resumes. A short
     * background interval keeps a healthy or already-connecting generation;
     * Doze-length intervals and structurally stale sockets cross exactly one
     * replacement boundary for every registered session.
     */
    fun onAppForegrounded(backgroundDurationMillis: Long, networkUsable: Boolean) {
        require(backgroundDurationMillis >= 0) { "background duration must not be negative" }
        requestSocketReconcile(
            trigger = "application returned to foreground",
            forceReplacement = backgroundDurationMillis >= FOREGROUND_RECONNECT_THRESHOLD_MILLIS,
            networkUsable = networkUsable,
        )
    }

    /**
     * Opens the device socket without allocating a session endpoint. Snapshot
     * REST bootstrap can use this to overlap the WebSocket handshake while it
     * restores history, then register the bounded endpoint only when ready.
     */
    fun warmConnect() {
        claimConnect(
            trigger = "realtime warm connect",
            action = SocketReconcileAction.CONNECT,
            requiresEndpoint = false,
        )?.let(::performOwnedSocketAction)
    }

    private fun requestSocketReconcile(
        trigger: String,
        forceReplacement: Boolean,
        networkUsable: Boolean,
    ) {
        if (!networkUsable) return
        val owner = synchronized(connectionLock) {
            if (closed.get() ||
                socketPhase == SocketPhase.CLOSED ||
                socketPhase == SocketPhase.AUTHENTICATING ||
                socketPhase == SocketPhase.AUTHENTICATION_FAILED ||
                synchronized(endpointLock) { endpoints.isEmpty() }
            ) {
                null
            } else if (activeConnectOwner != null) {
                queuePendingSocketReconcileLocked(trigger, forceReplacement)
                null
            } else if (socketPhase == SocketPhase.IDLE || socketPhase == SocketPhase.FAILED) {
                claimConnectLocked(trigger, SocketReconcileAction.CONNECT, requiresEndpoint = true)
            } else if (!forceReplacement && !socketStructurallyStale()) {
                null
            } else {
                claimConnectLocked(trigger, SocketReconcileAction.REPLACE, requiresEndpoint = true)
            }
        }
        owner?.let(::performOwnedSocketAction)
    }

    private fun claimConnect(
        trigger: String,
        action: SocketReconcileAction,
        requiresEndpoint: Boolean,
    ): ConnectOwner? = synchronized(connectionLock) {
        if (closed.get() || activeConnectOwner != null ||
            socketPhase == SocketPhase.CLOSED ||
            socketPhase == SocketPhase.AUTHENTICATING ||
            socketPhase == SocketPhase.AUTHENTICATION_FAILED ||
            (requiresEndpoint && synchronized(endpointLock) { endpoints.isEmpty() }) ||
            (action == SocketReconcileAction.CONNECT &&
                socketPhase != SocketPhase.IDLE && socketPhase != SocketPhase.FAILED)
        ) {
            null
        } else {
            claimConnectLocked(trigger, action, requiresEndpoint)
        }
    }

    /** Must be called while [connectionLock] is held. */
    private fun claimConnectLocked(
        trigger: String,
        action: SocketReconcileAction,
        requiresEndpoint: Boolean,
    ): ConnectOwner {
        val owner = ConnectOwner(++nextConnectOwner, trigger, action, requiresEndpoint)
        synchronized(endpointLock) { endpoints.values.forEach(Endpoint::invalidateSubscription) }
        activeConnectOwner = owner
        suppressReplacementDisconnectFailure = action == SocketReconcileAction.REPLACE
        socketPhase = SocketPhase.CONNECTING
        return owner
    }

    /**
     * The owner token is revalidated at the physical call boundary. Holding
     * [connectionLock] through the non-blocking OkHttp call linearizes it with
     * final-endpoint removal and owner shutdown.
     */
    private fun performOwnedSocketAction(owner: ConnectOwner) {
        var failure: Exception? = null
        synchronized(connectionLock) {
            if (activeConnectOwner != owner || closed.get() ||
                (owner.requiresEndpoint && synchronized(endpointLock) { endpoints.isEmpty() })
            ) {
                if (activeConnectOwner == owner) {
                    activeConnectOwner = null
                    suppressReplacementDisconnectFailure = false
                    if (!closed.get()) socketPhase = SocketPhase.IDLE
                }
                return
            }
            try {
                if (owner.action == SocketReconcileAction.REPLACE) {
                    rejectedThroughGeneration.accumulateAndGet(socket.diagnostics().generation, ::maxOf)
                    // attempt=0 cancels session-local backoff without resetting ACK or renderer state.
                    publishState(RealtimeState.Reconnecting(EXTERNAL_RECONNECT_ATTEMPT, owner.trigger))
                    socket.disconnect(owner.trigger)
                }
                socket.connect()
            } catch (error: Exception) {
                if (activeConnectOwner == owner) {
                    activeConnectOwner = null
                    suppressReplacementDisconnectFailure = false
                    if (!closed.get()) socketPhase = SocketPhase.FAILED
                }
                failure = error
            }
        }
        failure?.let { publishOwnedConnectFailure(owner, it) }
        if (failure != null) {
            claimPendingSocketReconcile()?.let(::performOwnedSocketAction)
        }
    }

    private fun publishOwnedConnectFailure(owner: ConnectOwner, error: Exception) {
        synchronized(connectionLock) {
            if (activeConnectOwner == owner) {
                activeConnectOwner = null
                suppressReplacementDisconnectFailure = false
                if (!closed.get()) socketPhase = SocketPhase.FAILED
            }
            if (!closed.get() && activeConnectOwner == null &&
                (!owner.requiresEndpoint || synchronized(endpointLock) { endpoints.isNotEmpty() })
            ) {
                publishState(RealtimeState.Failed(error.message ?: "${owner.trigger} failed"))
            }
        }
    }

    /** Must be called while [connectionLock] is held. */
    private fun queuePendingSocketReconcileLocked(trigger: String, forceReplacement: Boolean) {
        val current = pendingSocketReconcile
        pendingSocketReconcile = if (current == null) {
            PendingSocketReconcile(trigger, forceReplacement)
        } else {
            PendingSocketReconcile(
                trigger = if (forceReplacement && !current.forceReplacement) trigger else current.trigger,
                forceReplacement = current.forceReplacement || forceReplacement,
            )
        }
    }

    /** Claims one coalesced follow-up only after the preceding owner settles. */
    private fun claimPendingSocketReconcile(): ConnectOwner? = synchronized(connectionLock) {
        claimPendingSocketReconcileLocked()
    }

    /** Must be called while [connectionLock] is held. */
    private fun claimPendingSocketReconcileLocked(): ConnectOwner? {
        val pending = pendingSocketReconcile ?: return null
        if (closed.get() || activeConnectOwner != null ||
            socketPhase == SocketPhase.CLOSED ||
            socketPhase == SocketPhase.AUTHENTICATING ||
            socketPhase == SocketPhase.AUTHENTICATION_FAILED
        ) return null
        if (synchronized(endpointLock) { endpoints.isEmpty() }) {
            pendingSocketReconcile = null
            return null
        }
        val action = when {
            socketPhase == SocketPhase.IDLE || socketPhase == SocketPhase.FAILED ->
                SocketReconcileAction.CONNECT
            pending.forceReplacement || socketStructurallyStale() ->
                SocketReconcileAction.REPLACE
            else -> {
                pendingSocketReconcile = null
                return null
            }
        }
        pendingSocketReconcile = null
        return claimConnectLocked(pending.trigger, action, requiresEndpoint = true)
    }

    private enum class SocketReconcileAction {
        CONNECT,
        REPLACE,
    }

    private data class ConnectOwner(
        val id: Long,
        val trigger: String,
        val action: SocketReconcileAction,
        val requiresEndpoint: Boolean,
    )

    private data class PendingSocketReconcile(
        val trigger: String,
        val forceReplacement: Boolean,
    )

    /** Must be called while [connectionLock] is held. */
    private fun socketStructurallyStale(): Boolean {
        if (socketPhase == SocketPhase.IDLE || socketPhase == SocketPhase.FAILED) return true
        if (socketPhase == SocketPhase.CONNECTING) return false
        if (socketPhase != SocketPhase.CONNECTED) return false
        val diagnostics = socket.diagnostics()
        return !diagnostics.socketPresent || diagnostics.generation <= 0 ||
            diagnostics.connectedGeneration != diagnostics.generation ||
            diagnostics.state != "CONNECTED"
    }

    private fun ensureConnected() {
        check(!closed.get()) { "production realtime multiplexer is closed" }
        claimConnect(
            trigger = "session connect",
            action = SocketReconcileAction.CONNECT,
            requiresEndpoint = true,
        )?.let(::performOwnedSocketAction)
    }

    private fun disconnectSocket(reason: String) {
        synchronized(connectionLock) {
            if (closed.get() || socketPhase == SocketPhase.CLOSED || socketPhase == SocketPhase.FAILED) {
                return
            } else {
                activeConnectOwner = null
                suppressReplacementDisconnectFailure = false
                socketPhase = SocketPhase.FAILED
                synchronized(endpointLock) { endpoints.values.forEach(Endpoint::invalidateSubscription) }
                socket.disconnect(reason)
            }
        }
    }

    private fun handleAuthenticationRejected(rejected: RealtimeState.AuthenticationRejected) {
        val recover = synchronized(connectionLock) {
            activeConnectOwner = null
            suppressReplacementDisconnectFailure = false
            if (closed.get() || socketPhase == SocketPhase.CLOSED) {
                null
            } else if (authenticationRecoveryUsed) {
                socketPhase = SocketPhase.AUTHENTICATION_FAILED
                false
            } else {
                authenticationRecoveryUsed = true
                socketPhase = SocketPhase.AUTHENTICATING
                true
            }
        } ?: return
        if (!recover) {
            publishAuthenticationFailure(
                IllegalStateException("realtime authentication was rejected after token refresh (HTTP ${rejected.status})"),
            )
            return
        }
        synchronized(connectionLock) {
            if (!closed.get() && socketPhase == SocketPhase.AUTHENTICATING) {
                publishState(RealtimeState.Reconnecting(1, "refreshing realtime authentication"))
            }
        }
        authenticationRecoveryJob = scope.launch {
            try {
                authenticationRefresher.refreshAccessToken()
            } catch (error: Throwable) {
                if (!closed.get()) publishAuthenticationFailure(error)
                return@launch
            }
            val reconnect = synchronized(connectionLock) {
                if (closed.get() || socketPhase != SocketPhase.AUTHENTICATING) {
                    false
                } else {
                    socketPhase = SocketPhase.FAILED
                    true
                }
            }
            if (reconnect) {
                claimConnect(
                    trigger = "realtime authentication refreshed",
                    action = SocketReconcileAction.CONNECT,
                    requiresEndpoint = false,
                )?.let(::performOwnedSocketAction)
            }
        }
    }

    private fun publishAuthenticationFailure(error: Throwable) {
        val publish = synchronized(connectionLock) {
            if (closed.get()) {
                false
            } else {
                socketPhase = SocketPhase.AUTHENTICATION_FAILED
                publishState(
                    RealtimeState.AuthenticationFailed(error.message ?: "realtime authentication failed"),
                )
                true
            }
        }
        if (!publish) return
        runCatching { authenticationFailureObserver(error) }
    }

    private fun route(received: ProductionReceivedEnvelope) {
        val generation = received.generation
        val highestGeneration = highestObservedGeneration.accumulateAndGet(generation, ::maxOf)
        if (generation < highestGeneration || generation <= rejectedThroughGeneration.get()) {
            rejectRoute(
                received = received,
                decision = ProductionRealtimeRouteDecision.REJECTED_GENERATION,
                incarnation = 0,
                disconnect = false,
            )
            return
        }

        val sessionId = ProductionRealtimeClient.uuidFromBytes(received.envelope.sessionId.toByteArray())
        val target = synchronized(endpointLock) {
            RouteTarget(
                endpoint = sessionId?.let(endpoints::get),
                closedEndpoint = sessionId?.let(closedEndpointIncarnations::get),
            )
        }
        val endpoint = target.endpoint
        val decision = when {
            endpoint != null -> endpoint.deliver(received)
            target.closedEndpoint != null -> classifyProductionRealtimeRoute(
                endpointPresent = true,
                endpointClosed = true,
            )
            else -> classifyProductionRealtimeRoute(endpointPresent = false)
        }
        if (decision == ProductionRealtimeRouteDecision.DELIVERED) return

        val gracefulRetiredGeneration = target.closedEndpoint?.let { closedEndpoint ->
            closedEndpoint.graceful && closedEndpoint.retiredGeneration == generation
        } == true
        val fatalGeneration = decision == ProductionRealtimeRouteDecision.NO_ENDPOINT ||
            (decision == ProductionRealtimeRouteDecision.ENDPOINT_CLOSED &&
                !gracefulRetiredGeneration) ||
            decision == ProductionRealtimeRouteDecision.OVERFLOW
        if (fatalGeneration) rejectedThroughGeneration.accumulateAndGet(generation, ::maxOf)
        rejectRoute(
            received = received,
            decision = decision,
            incarnation = endpoint?.incarnation ?: target.closedEndpoint?.incarnation ?: 0,
            disconnect = fatalGeneration,
        )
    }

    private fun rejectRoute(
        received: ProductionReceivedEnvelope,
        decision: ProductionRealtimeRouteDecision,
        incarnation: Int,
        disconnect: Boolean,
    ) {
        val generation = received.generation
        val sequence = received.envelope.seq
        received.close()
        recordRouteDecision(decision)
        Log.w(
            REALTIME_LOG_TAG,
            "route_${decision.name.lowercase()} generation=$generation " +
                "seq=$sequence incarnation=$incarnation",
        )
        if (disconnect) {
            disconnectSocket(
                when (decision) {
                    ProductionRealtimeRouteDecision.NO_ENDPOINT -> "session endpoint is not registered"
                    ProductionRealtimeRouteDecision.ENDPOINT_CLOSED -> "session endpoint is closed"
                    ProductionRealtimeRouteDecision.ENDPOINT_NOT_SUBSCRIBED -> "session endpoint is not subscribed"
                    ProductionRealtimeRouteDecision.OVERFLOW -> "bounded session receive queue exceeded"
                    else -> "realtime route rejected"
                },
            )
        }
    }

    private fun unregister(endpoint: Endpoint, droppedThroughGeneration: Int, graceful: Boolean) {
        val removed = synchronized(connectionLock) {
            synchronized(endpointLock) endpoint@{
                if (endpoints[endpoint.sessionId] !== endpoint) return@endpoint false
                if (!graceful && droppedThroughGeneration > 0) {
                    rejectedThroughGeneration.accumulateAndGet(droppedThroughGeneration, ::maxOf)
                }
                endpoints.remove(endpoint.sessionId)
                closedEndpointIncarnations[endpoint.sessionId] = ClosedEndpoint(
                    incarnation = endpoint.incarnation,
                    graceful = graceful,
                    retiredGeneration = socket.diagnostics().generation,
                )
                while (closedEndpointIncarnations.size > MAX_CLOSED_ENDPOINT_TOMBSTONES) {
                    val eldest = closedEndpointIncarnations.entries.iterator()
                    if (eldest.hasNext()) {
                        eldest.next()
                        eldest.remove()
                    }
                }
                val owner = activeConnectOwner
                if (endpoints.isEmpty() && owner?.requiresEndpoint == true) {
                    activeConnectOwner = null
                    pendingSocketReconcile = null
                    suppressReplacementDisconnectFailure = false
                    if (!closed.get()) socketPhase = SocketPhase.IDLE
                }
                true
            }
        }
        if (removed) logEndpointLifecycle("endpoint_unregister", endpoint)
    }

    private inner class Endpoint(
        val sessionId: UUID,
        val incarnation: Int,
    ) : ProductionRealtimeSessionTransport {
        private val endpointClosed = AtomicBoolean(false)
        private val subscribedGeneration = AtomicInteger(0)
        private val admissionLock = Any()
        /** Guarded by [admissionLock]; includes queued and in-flight frames. */
        private val outstandingGenerations = mutableMapOf<Int, Int>()
        private val incoming = Channel<ProductionReceivedEnvelope>(
            capacity = ProductionRealtimeClient.MAX_BUFFERED_EVENTS,
            onUndeliveredElement = ProductionReceivedEnvelope::close,
        )

        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incoming.receiveAsFlow()
        override val receivePressure: StateFlow<ProductionReceivePressure>
            get() = socket.receivePressure
        override val state: SharedFlow<RealtimeState>
            get() = this@ProductionRealtimeMultiplexer.state
        override val connectionEvents: Flow<ProductionRealtimeSessionConnectionEvent>
            get() = this@ProductionRealtimeMultiplexer.connectionEvents

        override fun connect() {
            check(!endpointClosed.get()) { "production session transport is closed" }
            ensureConnected()
        }

        fun invalidateSubscription() {
            subscribedGeneration.set(0)
        }

        override fun send(envelope: Envelope): Boolean = sendInternal(envelope, expectedGeneration = null)

        override fun sendForConnectedGeneration(
            envelope: Envelope,
            expectedGeneration: Int,
        ): Boolean = sendInternal(envelope, expectedGeneration)

        private fun sendInternal(envelope: Envelope, expectedGeneration: Int?): Boolean {
            require(
                ProductionRealtimeClient.uuidFromBytes(envelope.sessionId.toByteArray()) == sessionId,
            ) { "Envelope session does not match the registered endpoint" }
            if (expectedGeneration != null) {
                require(envelope.messageType == dev.termlink.protocol.realtime.v1.MessageType.SUBSCRIBE) {
                    "generation-bound send is reserved for SUBSCRIBE"
                }
                require(expectedGeneration > 0) { "expectedGeneration must be positive" }
            }
            return synchronized(connectionLock) {
                synchronized(admissionLock) {
                    check(!closed.get() && !endpointClosed.get()) { "production session transport is closed" }
                    when (envelope.messageType) {
                        dev.termlink.protocol.realtime.v1.MessageType.SUBSCRIBE -> {
                            val generation = socket.diagnostics().connectedGeneration
                            if (expectedGeneration != null &&
                                (socketPhase != SocketPhase.CONNECTED || activeConnectOwner != null ||
                                    generation != expectedGeneration)
                            ) {
                                false
                            } else {
                                check(generation > 0) { "SUBSCRIBE requires a connected socket generation" }
                                subscribedGeneration.set(0)
                                socket.send(envelope).also { sent ->
                                    if (sent) subscribedGeneration.set(generation)
                                }
                            }
                        }
                        dev.termlink.protocol.realtime.v1.MessageType.UNSUBSCRIBE -> {
                            subscribedGeneration.set(0)
                            socket.send(envelope)
                        }
                        else -> socket.send(envelope)
                    }
                }
            }
        }

        override fun disconnect(reason: String) {
            if (!endpointClosed.get()) disconnectSocket(reason)
        }

        override fun currentConnectedGeneration(): Int = synchronized(connectionLock) {
            if (closed.get() || endpointClosed.get() || socketPhase != SocketPhase.CONNECTED ||
                activeConnectOwner != null
            ) {
                0
            } else {
                socket.diagnostics().connectedGeneration
            }
        }

        override fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean =
            synchronized(connectionLock) {
                val diagnostics = socket.diagnostics()
                if (expectedGeneration <= 0 || closed.get() || endpointClosed.get() ||
                    socketPhase != SocketPhase.CONNECTED || activeConnectOwner != null ||
                    diagnostics.connectedGeneration != expectedGeneration
                ) {
                    false
                } else {
                    // The generation comparison and disconnect share the same
                    // ownership lock as lifecycle/network replacement. A stale
                    // session failure can therefore never disconnect its successor.
                    disconnectSocket(reason)
                    true
                }
            }

        override fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics =
            this@ProductionRealtimeMultiplexer.diagnostics()

        override fun close() {
            closeEndpoint(ownerClosing = false, graceful = false)
        }

        override fun closeAfterUnsubscribe() {
            closeEndpoint(ownerClosing = false, graceful = true)
        }

        fun closeFromOwner() {
            closeEndpoint(ownerClosing = true, graceful = false)
        }

        fun deliver(received: ProductionReceivedEnvelope): ProductionRealtimeRouteDecision {
            synchronized(admissionLock) {
                if (endpointClosed.get()) {
                    return classifyProductionRealtimeRoute(endpointPresent = true, endpointClosed = true)
                }
                return synchronized(endpointLock) {
                    // Revalidate both the selected incarnation and socket-wide
                    // generation immediately before admission. Holding the map
                    // lock through trySend prevents close/register from exposing
                    // a replacement in the gap between this check and enqueue.
                    if (endpointClosed.get() || endpoints[sessionId] !== this) {
                        return@synchronized classifyProductionRealtimeRoute(
                            endpointPresent = true,
                            endpointClosed = true,
                        )
                    }
                    if (received.generation < highestObservedGeneration.get() ||
                        received.generation <= rejectedThroughGeneration.get()
                    ) {
                        return@synchronized classifyProductionRealtimeRoute(
                            endpointPresent = true,
                            generationRejected = true,
                        )
                    }
                    if (subscribedGeneration.get() != received.generation) {
                        return@synchronized ProductionRealtimeRouteDecision.ENDPOINT_NOT_SUBSCRIBED
                    }
                    outstandingGenerations[received.generation] =
                        (outstandingGenerations[received.generation] ?: 0) + 1
                    val observed = received.withCloseObserver {
                        releaseOutstanding(received.generation)
                    }
                    val admission = incoming.trySend(observed)
                    if (admission.isSuccess) {
                        return@synchronized classifyProductionRealtimeRoute(endpointPresent = true)
                    }
                    observed.close()
                    if (admission.isClosed) {
                        return@synchronized classifyProductionRealtimeRoute(
                            endpointPresent = true,
                            endpointClosed = true,
                        )
                    }
                    classifyProductionRealtimeRoute(endpointPresent = true, overflow = true)
                }
            }
        }

        private fun closeEndpoint(ownerClosing: Boolean, graceful: Boolean) {
            val droppedThroughGeneration = synchronized(admissionLock) {
                if (!endpointClosed.compareAndSet(false, true)) return
                outstandingGenerations.keys.maxOrNull() ?: 0
            }

            // Publish the fence while the endpoint still occupies its map slot.
            // register() uses the same lock, so a replacement incarnation can
            // never become visible before the dropped generation is fenced.
            if (ownerClosing) {
                if (!graceful && droppedThroughGeneration > 0) {
                    rejectedThroughGeneration.accumulateAndGet(droppedThroughGeneration, ::maxOf)
                }
            } else {
                unregister(this, droppedThroughGeneration, graceful)
            }
            incoming.cancel()

            if (droppedThroughGeneration > 0) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "endpoint_outstanding_cancelled generation=$droppedThroughGeneration " +
                        "seq=0 incarnation=$incarnation",
                )
                if (!ownerClosing && !graceful) {
                    disconnectSocket("session endpoint closed with outstanding events")
                }
            }
        }

        private fun releaseOutstanding(generation: Int) {
            synchronized(admissionLock) {
                val remaining = (outstandingGenerations[generation] ?: 1) - 1
                if (remaining <= 0) {
                    outstandingGenerations.remove(generation)
                } else {
                    outstandingGenerations[generation] = remaining
                }
            }
        }
    }

    private data class RouteTarget(
        val endpoint: Endpoint?,
        val closedEndpoint: ClosedEndpoint?,
    )

    private data class ClosedEndpoint(
        val incarnation: Int,
        val graceful: Boolean,
        val retiredGeneration: Int,
    )

    private fun recordRouteDecision(decision: ProductionRealtimeRouteDecision) {
        when (decision) {
            ProductionRealtimeRouteDecision.DELIVERED -> Unit
            ProductionRealtimeRouteDecision.NO_ENDPOINT -> noEndpointCount.incrementAndGet()
            ProductionRealtimeRouteDecision.ENDPOINT_CLOSED -> endpointClosedCount.incrementAndGet()
            ProductionRealtimeRouteDecision.ENDPOINT_NOT_SUBSCRIBED -> endpointNotSubscribedCount.incrementAndGet()
            ProductionRealtimeRouteDecision.REJECTED_GENERATION -> rejectedGenerationCount.incrementAndGet()
            ProductionRealtimeRouteDecision.OVERFLOW -> overflowCount.incrementAndGet()
        }
    }

    private fun logEndpointLifecycle(event: String, endpoint: Endpoint) {
        Log.d(
            REALTIME_LOG_TAG,
            "$event generation=${socket.diagnostics().generation} seq=0 " +
                "incarnation=${endpoint.incarnation}",
        )
    }

    private enum class SocketPhase {
        IDLE,
        CONNECTING,
        CONNECTED,
        AUTHENTICATING,
        AUTHENTICATION_FAILED,
        FAILED,
        CLOSED,
    }

    private fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics =
        ProductionRealtimeMultiplexerDiagnostics(
            phase = synchronized(connectionLock) { socketPhase.name },
            endpointCount = synchronized(endpointLock) { endpoints.size },
            socket = socket.diagnostics(),
            routeDecisions = ProductionRealtimeRouteDecisionCounters(
                noEndpoint = noEndpointCount.get(),
                endpointClosed = endpointClosedCount.get(),
                endpointNotSubscribed = endpointNotSubscribedCount.get(),
                rejectedGeneration = rejectedGenerationCount.get(),
                overflow = overflowCount.get(),
            ),
        )

    internal companion object {
        private const val REALTIME_LOG_TAG = "TermLinkRealtime"
        private const val MAX_CLOSED_ENDPOINT_TOMBSTONES = 256
        internal const val FOREGROUND_RECONNECT_THRESHOLD_MILLIS = 15_000L
        internal const val EXTERNAL_RECONNECT_ATTEMPT = 0
        private val endpointIncarnation = AtomicInteger(0)

        private fun nextEndpointIncarnation(): Int = endpointIncarnation.updateAndGet { current ->
            if (current == Int.MAX_VALUE) 1 else current + 1
        }

        fun forTest(
            socket: ProductionRealtimeSocket,
            parentScope: CoroutineScope,
            authenticationRefresher: RealtimeAuthenticationRefresher = RealtimeAuthenticationRefresher { },
            authenticationFailureObserver: (Throwable) -> Unit = { },
        ): ProductionRealtimeMultiplexer = ProductionRealtimeMultiplexer(
            socket,
            parentScope,
            authenticationRefresher,
            authenticationFailureObserver,
        )
    }
}

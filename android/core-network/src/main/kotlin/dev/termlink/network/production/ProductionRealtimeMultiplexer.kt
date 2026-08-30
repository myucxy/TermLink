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
    val rejectedGeneration: Long = 0,
    val overflow: Long = 0,
)

internal enum class ProductionRealtimeRouteDecision {
    DELIVERED,
    NO_ENDPOINT,
    ENDPOINT_CLOSED,
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

    fun connect()
    fun send(envelope: Envelope): Boolean
    fun disconnect(reason: String)
    fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics = ProductionRealtimeMultiplexerDiagnostics(
        phase = "UNKNOWN",
        endpointCount = 0,
        socket = ProductionRealtimeSocketDiagnostics(0, 0, false, "UNKNOWN", 0, 0),
    )
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
    private val closedEndpointIncarnations = LinkedHashMap<UUID, Int>()
    /** Socket-global fence; it intentionally survives endpoint replacement. */
    private val rejectedThroughGeneration = AtomicInteger(0)
    /** Highest socket generation observed by any endpoint incarnation. */
    private val highestObservedGeneration = AtomicInteger(0)
    private val noEndpointCount = AtomicLong(0)
    private val endpointClosedCount = AtomicLong(0)
    private val rejectedGenerationCount = AtomicLong(0)
    private val overflowCount = AtomicLong(0)
    private var socketPhase = SocketPhase.IDLE
    private var authenticationRecoveryUsed = false
    private var authenticationRecoveryJob: Job? = null
    private val mutableState = MutableSharedFlow<RealtimeState>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    private val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()
    private val routeJob: Job
    private val stateJob: Job

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
                    synchronized(connectionLock) {
                        socketPhase = when (state) {
                            RealtimeState.Connecting -> SocketPhase.CONNECTING
                            RealtimeState.Connected -> {
                                authenticationRecoveryUsed = false
                                SocketPhase.CONNECTED
                            }
                            is RealtimeState.Reconnecting -> SocketPhase.CONNECTING
                            is RealtimeState.AuthenticationFailed -> SocketPhase.AUTHENTICATION_FAILED
                            is RealtimeState.Failed -> SocketPhase.FAILED
                            RealtimeState.Closed -> SocketPhase.CLOSED
                            is RealtimeState.AuthenticationRejected -> error("handled above")
                        }
                    }
                    mutableState.tryEmit(state)
                }
            }
        }
    }

    /** Registers exactly one consumer for [sessionId]. */
    fun register(sessionId: UUID): ProductionRealtimeSessionTransport = synchronized(endpointLock) {
        check(!closed.get()) { "production realtime multiplexer is closed" }
        check(sessionId !in endpoints) { "production session is already registered" }
        Endpoint(sessionId, nextEndpointIncarnation()).also { endpoint ->
            closedEndpointIncarnations.remove(sessionId)
            endpoints[sessionId] = endpoint
            logEndpointLifecycle("endpoint_register", endpoint)
        }
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        val removed = synchronized(endpointLock) {
            endpoints.values.toList().also { endpoints.clear() }
        }
        removed.forEach { endpoint ->
            logEndpointLifecycle("endpoint_unregister", endpoint)
            endpoint.closeFromOwner()
        }
        synchronized(connectionLock) { socketPhase = SocketPhase.CLOSED }
        authenticationRecoveryJob?.cancel()
        authenticationRecoveryJob = null
        mutableState.tryEmit(RealtimeState.Closed)
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
        val reconnect = synchronized(connectionLock) {
            if (closed.get() || socketPhase == SocketPhase.CLOSED ||
                socketPhase == SocketPhase.AUTHENTICATING || socketPhase == SocketPhase.AUTHENTICATION_FAILED
            ) {
                false
            } else {
                socketPhase = SocketPhase.FAILED
                true
            }
        }
        if (!reconnect) return
        // Cancel even a socket already marked FAILED. OkHttp can retain that
        // generation until its asynchronous callback unwinds.
        socket.disconnect("default network became available")
        ensureConnected()
    }

    private fun ensureConnected() {
        val shouldConnect = synchronized(connectionLock) {
            check(!closed.get()) { "production realtime multiplexer is closed" }
            if (socketPhase == SocketPhase.IDLE || socketPhase == SocketPhase.FAILED) {
                socketPhase = SocketPhase.CONNECTING
                true
            } else {
                false
            }
        }
        if (shouldConnect) {
            try {
                socket.connect()
            } catch (error: Exception) {
                synchronized(connectionLock) {
                    if (!closed.get()) socketPhase = SocketPhase.FAILED
                }
                throw error
            }
        }
    }

    private fun disconnectSocket(reason: String) {
        val shouldDisconnect = synchronized(connectionLock) {
            if (closed.get() || socketPhase == SocketPhase.CLOSED || socketPhase == SocketPhase.FAILED) {
                false
            } else {
                socketPhase = SocketPhase.FAILED
                true
            }
        }
        if (shouldDisconnect) socket.disconnect(reason)
    }

    private fun handleAuthenticationRejected(rejected: RealtimeState.AuthenticationRejected) {
        val recover = synchronized(connectionLock) {
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
        mutableState.tryEmit(RealtimeState.Reconnecting(1, "refreshing realtime authentication"))
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
            if (reconnect) ensureConnected()
        }
    }

    private fun publishAuthenticationFailure(error: Throwable) {
        synchronized(connectionLock) {
            if (!closed.get()) socketPhase = SocketPhase.AUTHENTICATION_FAILED
        }
        runCatching { authenticationFailureObserver(error) }
        mutableState.tryEmit(
            RealtimeState.AuthenticationFailed(error.message ?: "realtime authentication failed"),
        )
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
                closedIncarnation = sessionId?.let(closedEndpointIncarnations::get),
            )
        }
        val endpoint = target.endpoint
        val decision = when {
            endpoint != null -> endpoint.deliver(received)
            target.closedIncarnation != null -> classifyProductionRealtimeRoute(
                endpointPresent = true,
                endpointClosed = true,
            )
            else -> classifyProductionRealtimeRoute(endpointPresent = false)
        }
        if (decision == ProductionRealtimeRouteDecision.DELIVERED) return

        val fatalGeneration = decision == ProductionRealtimeRouteDecision.NO_ENDPOINT ||
            decision == ProductionRealtimeRouteDecision.ENDPOINT_CLOSED ||
            decision == ProductionRealtimeRouteDecision.OVERFLOW
        if (fatalGeneration) rejectedThroughGeneration.accumulateAndGet(generation, ::maxOf)
        rejectRoute(
            received = received,
            decision = decision,
            incarnation = endpoint?.incarnation ?: target.closedIncarnation ?: 0,
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
                    ProductionRealtimeRouteDecision.OVERFLOW -> "bounded session receive queue exceeded"
                    else -> "realtime route rejected"
                },
            )
        }
    }

    private fun unregister(endpoint: Endpoint, droppedThroughGeneration: Int) {
        val removed = synchronized(endpointLock) {
            if (endpoints[endpoint.sessionId] === endpoint) {
                if (droppedThroughGeneration > 0) {
                    rejectedThroughGeneration.accumulateAndGet(droppedThroughGeneration, ::maxOf)
                }
                endpoints.remove(endpoint.sessionId)
                closedEndpointIncarnations[endpoint.sessionId] = endpoint.incarnation
                while (closedEndpointIncarnations.size > MAX_CLOSED_ENDPOINT_TOMBSTONES) {
                    val eldest = closedEndpointIncarnations.entries.iterator()
                    if (eldest.hasNext()) {
                        eldest.next()
                        eldest.remove()
                    }
                }
                true
            } else {
                false
            }
        }
        if (removed) logEndpointLifecycle("endpoint_unregister", endpoint)
    }

    private inner class Endpoint(
        val sessionId: UUID,
        val incarnation: Int,
    ) : ProductionRealtimeSessionTransport {
        private val endpointClosed = AtomicBoolean(false)
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

        override fun connect() {
            check(!endpointClosed.get()) { "production session transport is closed" }
            ensureConnected()
        }

        override fun send(envelope: Envelope): Boolean {
            check(!endpointClosed.get()) { "production session transport is closed" }
            require(
                ProductionRealtimeClient.uuidFromBytes(envelope.sessionId.toByteArray()) == sessionId,
            ) { "Envelope session does not match the registered endpoint" }
            return socket.send(envelope)
        }

        override fun disconnect(reason: String) {
            if (!endpointClosed.get()) disconnectSocket(reason)
        }

        override fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics =
            this@ProductionRealtimeMultiplexer.diagnostics()

        override fun close() {
            closeEndpoint(ownerClosing = false)
        }

        fun closeFromOwner() {
            closeEndpoint(ownerClosing = true)
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

        private fun closeEndpoint(ownerClosing: Boolean) {
            val droppedThroughGeneration = synchronized(admissionLock) {
                if (!endpointClosed.compareAndSet(false, true)) return
                outstandingGenerations.keys.maxOrNull() ?: 0
            }

            // Publish the fence while the endpoint still occupies its map slot.
            // register() uses the same lock, so a replacement incarnation can
            // never become visible before the dropped generation is fenced.
            if (ownerClosing) {
                if (droppedThroughGeneration > 0) {
                    rejectedThroughGeneration.accumulateAndGet(droppedThroughGeneration, ::maxOf)
                }
            } else {
                unregister(this, droppedThroughGeneration)
            }
            incoming.cancel()

            if (droppedThroughGeneration > 0) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "endpoint_outstanding_cancelled generation=$droppedThroughGeneration " +
                        "seq=0 incarnation=$incarnation",
                )
                if (!ownerClosing) disconnectSocket("session endpoint closed with outstanding events")
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
        val closedIncarnation: Int?,
    )

    private fun recordRouteDecision(decision: ProductionRealtimeRouteDecision) {
        when (decision) {
            ProductionRealtimeRouteDecision.DELIVERED -> Unit
            ProductionRealtimeRouteDecision.NO_ENDPOINT -> noEndpointCount.incrementAndGet()
            ProductionRealtimeRouteDecision.ENDPOINT_CLOSED -> endpointClosedCount.incrementAndGet()
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
                rejectedGeneration = rejectedGenerationCount.get(),
                overflow = overflowCount.get(),
            ),
        )

    internal companion object {
        private const val REALTIME_LOG_TAG = "TermLinkRealtime"
        private const val MAX_CLOSED_ENDPOINT_TOMBSTONES = 256
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

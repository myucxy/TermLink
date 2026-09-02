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
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
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
    /** Reports a session-local transport failure without owning the device socket. */
    fun disconnect(reason: String)
    fun currentConnectedGeneration(): Int
    /** Session-local failures never disconnect a healthy shared generation. */
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
    authenticationRefresher: RealtimeAuthenticationRefresher,
    authenticationFailureObserver: (Throwable) -> Unit,
    retryDelay: ProductionRealtimeRetryDelay,
    retryJitter: ProductionRealtimeRetryJitter,
    stableConnectionMillis: Long,
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
        retryDelay = ProductionRealtimeRetryDelay { delay(it) },
        retryJitter = ProductionRealtimeRetryJitter { base ->
            if (base <= 1L) base else kotlin.random.Random.nextLong(base / 2L, base + 1L)
        },
        stableConnectionMillis = ProductionDeviceConnectionSupervisor.DEFAULT_STABLE_CONNECTION_MILLIS,
    )

    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val closed = AtomicBoolean(false)
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
    private val supervisor = ProductionDeviceConnectionSupervisor(
        socket = socket,
        parentScope = scope,
        authenticationRefresher = authenticationRefresher,
        authenticationFailureObserver = authenticationFailureObserver,
        hasEndpointDemand = { synchronized(endpointLock) { endpoints.isNotEmpty() } },
        onGenerationBoundary = { generation ->
            if (generation > 0) rejectedThroughGeneration.accumulateAndGet(generation, ::maxOf)
            synchronized(endpointLock) { endpoints.values.forEach(Endpoint::invalidateSubscription) }
        },
        retryDelay = retryDelay,
        retryJitter = retryJitter,
        stableConnectionMillis = stableConnectionMillis,
    )
    private val routeJob: Job

    init {
        routeJob = scope.launch {
            socket.receivedEnvelopes.collect { received -> route(received) }
        }
    }

    /**
     * Read-only device Relay observation for optional sibling diagnostics such as Direct shadow.
     * The observer receives no socket, reconnect, disconnect, send, session, or ACK capability.
     */
    val directRelayConnectionEvents: Flow<ProductionRealtimeSessionConnectionEvent>
        get() = supervisor.connectionEvents

    /** Registers exactly one consumer for [sessionId]. */
    fun register(sessionId: UUID): ProductionRealtimeSessionTransport {
        return synchronized(endpointLock) {
            // close() publishes the owner fence before it takes endpointLock.
            // Rechecking under the map lock prevents a register/close race from
            // installing an endpoint after the owner has already drained it.
            check(!closed.get()) { "production realtime multiplexer is closed" }
            check(sessionId !in endpoints) { "production session is already registered" }
            closedEndpointIncarnations.remove(sessionId)
            Endpoint(sessionId, nextEndpointIncarnation()).also { endpoint ->
                endpoints[sessionId] = endpoint
                logEndpointLifecycle("endpoint_register", endpoint)
            }
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
        routeJob.cancel()
        supervisor.close()
        scope.cancel()
    }

    /**
     * Android can restore HTTP before OkHttp recognizes that the old socket
     * belonged to a lost network. A confirmed default-network transition is
     * therefore an explicit socket-generation boundary, not merely a hint.
     */
    fun onNetworkAvailable() {
        supervisor.onNetworkAvailable()
    }

    /**
     * Reconciles the shared physical socket after Activity resumes. A short
     * background interval keeps a healthy or already-connecting generation;
     * Doze-length intervals and structurally stale sockets cross exactly one
     * replacement boundary for every registered session.
     */
    fun onAppForegrounded(backgroundDurationMillis: Long, networkUsable: Boolean) {
        supervisor.onAppForegrounded(backgroundDurationMillis, networkUsable)
    }

    /**
     * Opens the device socket without allocating a session endpoint. Snapshot
     * REST bootstrap can use this to overlap the WebSocket handshake while it
     * restores history, then register the bounded endpoint only when ready.
     */
    fun warmConnect() {
        supervisor.connect(requiresEndpoint = false)
    }

    private fun route(received: ProductionReceivedEnvelope) {
        // The socket reservation protects only OkHttp callback -> device router.
        // Every route outcome, including successful hand-off to a slow endpoint,
        // returns that reservation before this collector accepts the next frame.
        try {
            val generation = received.generation
            val highestGeneration = highestObservedGeneration.accumulateAndGet(generation, ::maxOf)
            if (generation < highestGeneration || generation <= rejectedThroughGeneration.get()) {
                rejectRoute(
                    received = received,
                    decision = ProductionRealtimeRouteDecision.REJECTED_GENERATION,
                    incarnation = 0,
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
            rejectRoute(
                received = received,
                decision = decision,
                incarnation = endpoint?.incarnation ?: target.closedEndpoint?.incarnation ?: 0,
                gracefulRetirement = gracefulRetiredGeneration,
            )
        } finally {
            received.close()
        }
    }

    private fun rejectRoute(
        received: ProductionReceivedEnvelope,
        decision: ProductionRealtimeRouteDecision,
        incarnation: Int,
        gracefulRetirement: Boolean = false,
    ) {
        val generation = received.generation
        val sequence = received.envelope.seq
        recordRouteDecision(decision)
        Log.w(
            REALTIME_LOG_TAG,
            "route_${decision.name.lowercase()} generation=$generation " +
                "seq=$sequence incarnation=$incarnation graceful=$gracefulRetirement",
        )
    }

    private fun unregister(endpoint: Endpoint, graceful: Boolean) {
        val removed = synchronized(endpointLock) endpoint@{
            if (endpoints[endpoint.sessionId] !== endpoint) return@endpoint false
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
            true
        }
        if (removed) {
            logEndpointLifecycle("endpoint_unregister", endpoint)
            supervisor.onEndpointDemandRemoved()
        }
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
        /** Guarded by [admissionLock]; object identity is the reservation token. */
        private val outstandingEnvelopes = mutableSetOf<ProductionReceivedEnvelope>()
        private var bufferedEvents = 0
        private var bufferedWireBytes = 0L
        private var pressurePaused = false
        private val mutableReceivePressure = MutableStateFlow(
            ProductionReceivePressure(0, false, 0, 0),
        )
        private val incoming = Channel<ProductionReceivedEnvelope>(
            capacity = ProductionRealtimeClient.MAX_BUFFERED_EVENTS,
            onUndeliveredElement = ProductionReceivedEnvelope::close,
        )

        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incoming.receiveAsFlow()
        override val receivePressure: StateFlow<ProductionReceivePressure> = mutableReceivePressure
        override val state: SharedFlow<RealtimeState>
            get() = supervisor.state
        override val connectionEvents: Flow<ProductionRealtimeSessionConnectionEvent>
            get() = supervisor.connectionEvents

        override fun connect() {
            check(!endpointClosed.get()) { "production session transport is closed" }
            supervisor.connect(requiresEndpoint = true)
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
                require(expectedGeneration > 0) { "expectedGeneration must be positive" }
            }
            return synchronized(admissionLock) {
                check(!closed.get() && !endpointClosed.get()) { "production session transport is closed" }
                when (envelope.messageType) {
                    dev.termlink.protocol.realtime.v1.MessageType.SUBSCRIBE -> {
                        val generation = expectedGeneration ?: supervisor.currentConnectedGeneration()
                        if (expectedGeneration == null) {
                            check(generation > 0) { "SUBSCRIBE requires a connected socket generation" }
                        }
                        subscribedGeneration.set(0)
                        supervisor.sendForConnectedGeneration(envelope, generation) { sentGeneration ->
                            subscribedGeneration.set(sentGeneration)
                        }
                    }
                    dev.termlink.protocol.realtime.v1.MessageType.UNSUBSCRIBE -> {
                        subscribedGeneration.set(0)
                        supervisor.send(envelope)
                    }
                    else -> if (expectedGeneration != null) {
                        supervisor.sendForConnectedGeneration(envelope, expectedGeneration)
                    } else {
                        supervisor.send(envelope)
                    }
                }
            }
        }

        override fun disconnect(reason: String) {
            if (!endpointClosed.get()) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "endpoint_transport_failure_isolated generation=${supervisor.currentConnectedGeneration()} " +
                        "seq=0 incarnation=$incarnation",
                )
            }
        }

        override fun currentConnectedGeneration(): Int =
            if (closed.get() || endpointClosed.get()) 0 else supervisor.currentConnectedGeneration()

        override fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean =
            false

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
                    val endpointEnvelope = reserveEndpointEnvelopeLocked(received)
                        ?: return@synchronized classifyProductionRealtimeRoute(
                            endpointPresent = true,
                            overflow = true,
                        )
                    val admission = incoming.trySend(endpointEnvelope)
                    if (admission.isSuccess) {
                        return@synchronized classifyProductionRealtimeRoute(endpointPresent = true)
                    }
                    endpointEnvelope.close()
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
            val (droppedThroughGeneration, outstanding) = synchronized(admissionLock) {
                if (!endpointClosed.compareAndSet(false, true)) return
                (outstandingGenerations.keys.maxOrNull() ?: 0) to outstandingEnvelopes.toList()
            }

            if (!ownerClosing) {
                unregister(this, graceful)
            }
            incoming.cancel()
            // A renderer may be blocked with an in-flight item and therefore
            // never reach its own finally block after endpoint retirement.
            // ProductionReceivedEnvelope.close is idempotent, so force-release
            // every endpoint-local token without racing queued cancellation.
            outstanding.forEach(ProductionReceivedEnvelope::close)

            if (droppedThroughGeneration > 0) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "endpoint_outstanding_cancelled generation=$droppedThroughGeneration " +
                        "seq=0 incarnation=$incarnation",
                )
            }
        }

        private fun reserveEndpointEnvelopeLocked(
            received: ProductionReceivedEnvelope,
        ): ProductionReceivedEnvelope? {
            require(received.wireBytes > 0) { "endpoint reservation requires positive wire bytes" }
            val generation = received.generation
            val nextEvents = bufferedEvents + 1
            val nextWireBytes = bufferedWireBytes + received.wireBytes
            if (nextEvents > ProductionRealtimeClient.MAX_BUFFERED_EVENTS ||
                nextWireBytes > ProductionRealtimeClient.HARD_BUFFERED_WIRE_BYTES
            ) {
                pressurePaused = true
                publishReceivePressureLocked(generation)
                return null
            }

            bufferedEvents = nextEvents
            bufferedWireBytes = nextWireBytes
            if (ProductionRealtimeClient.shouldPauseReceive(bufferedEvents, bufferedWireBytes)) {
                pressurePaused = true
            }
            outstandingGenerations[generation] =
                (outstandingGenerations[generation] ?: 0) + 1

            lateinit var endpointEnvelope: ProductionReceivedEnvelope
            endpointEnvelope = ProductionReceivedEnvelope(
                envelope = received.envelope,
                wireBytes = received.wireBytes,
                generation = generation,
                releaseReservation = { wireBytes ->
                    releaseEndpointReservation(endpointEnvelope, generation, wireBytes)
                },
            )
            check(outstandingEnvelopes.add(endpointEnvelope)) {
                "endpoint reservation identity was duplicated"
            }
            publishReceivePressureLocked(generation)
            return endpointEnvelope
        }

        private fun releaseEndpointReservation(
            endpointEnvelope: ProductionReceivedEnvelope,
            generation: Int,
            wireBytes: Int,
        ) {
            synchronized(admissionLock) {
                if (!outstandingEnvelopes.remove(endpointEnvelope)) return
                check(bufferedEvents > 0 && bufferedWireBytes >= wireBytes) {
                    "endpoint receive reservation underflow"
                }
                bufferedEvents--
                bufferedWireBytes -= wireBytes
                val remaining = checkNotNull(outstandingGenerations[generation]) {
                    "endpoint generation reservation was missing"
                } - 1
                if (remaining <= 0) {
                    outstandingGenerations.remove(generation)
                } else {
                    outstandingGenerations[generation] = remaining
                }
                if (pressurePaused &&
                    ProductionRealtimeClient.shouldResumeReceive(bufferedEvents, bufferedWireBytes)
                ) {
                    pressurePaused = false
                }
                publishReceivePressureLocked(generation)
            }
        }

        private fun publishReceivePressureLocked(generation: Int) {
            mutableReceivePressure.value = ProductionReceivePressure(
                generation = maxOf(mutableReceivePressure.value.generation, generation),
                paused = pressurePaused,
                bufferedEvents = bufferedEvents,
                bufferedWireBytes = bufferedWireBytes,
            )
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

    private fun diagnostics(): ProductionRealtimeMultiplexerDiagnostics =
        ProductionRealtimeMultiplexerDiagnostics(
            phase = supervisor.phaseName(),
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
        internal const val FOREGROUND_RECONNECT_THRESHOLD_MILLIS =
            ProductionDeviceConnectionSupervisor.FOREGROUND_RECONNECT_THRESHOLD_MILLIS
        internal const val EXTERNAL_RECONNECT_ATTEMPT =
            ProductionDeviceConnectionSupervisor.EXTERNAL_RECONNECT_ATTEMPT
        private val endpointIncarnation = AtomicInteger(0)

        private fun nextEndpointIncarnation(): Int = endpointIncarnation.updateAndGet { current ->
            if (current == Int.MAX_VALUE) 1 else current + 1
        }

        fun forTest(
            socket: ProductionRealtimeSocket,
            parentScope: CoroutineScope,
            authenticationRefresher: RealtimeAuthenticationRefresher = RealtimeAuthenticationRefresher { },
            authenticationFailureObserver: (Throwable) -> Unit = { },
            retryDelay: ProductionRealtimeRetryDelay = ProductionRealtimeRetryDelay { delay(it) },
            retryJitter: ProductionRealtimeRetryJitter = ProductionRealtimeRetryJitter { it },
            stableConnectionMillis: Long =
                ProductionDeviceConnectionSupervisor.DEFAULT_STABLE_CONNECTION_MILLIS,
        ): ProductionRealtimeMultiplexer = ProductionRealtimeMultiplexer(
            socket,
            parentScope,
            authenticationRefresher,
            authenticationFailureObserver,
            retryDelay,
            retryJitter,
            stableConnectionMillis,
        )
    }
}

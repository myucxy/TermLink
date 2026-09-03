package dev.termlink.network.production

import java.io.Closeable
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.min
import kotlin.random.Random
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout

internal fun interface ProductionRealtimeRetryDelay {
    suspend fun await(delayMillis: Long)
}

internal fun interface ProductionRealtimeRetryJitter {
    fun apply(baseDelayMillis: Long): Long
}

/**
 * Owns the physical, authenticated device WebSocket independently of any one
 * terminal session. Session endpoints may request connectivity, but they never
 * own, close, or retry the shared socket.
 */
internal class ProductionDeviceConnectionSupervisor(
    private val socket: ProductionRealtimeSocket,
    parentScope: CoroutineScope,
    private val authenticationRefresher: RealtimeAuthenticationRefresher,
    private val authenticationFailureObserver: (Throwable) -> Unit,
    private val hasEndpointDemand: () -> Boolean,
    private val onGenerationBoundary: (Int) -> Unit,
    private val retryDelay: ProductionRealtimeRetryDelay = ProductionRealtimeRetryDelay(::delay),
    private val retryJitter: ProductionRealtimeRetryJitter = ProductionRealtimeRetryJitter { base ->
        if (base <= 1L) base else Random.nextLong(base / 2L, base + 1L)
    },
    private val stableConnectionMillis: Long = DEFAULT_STABLE_CONNECTION_MILLIS,
    private val authenticationRefreshTimeoutMillis: Long = DEFAULT_AUTHENTICATION_REFRESH_TIMEOUT_MILLIS,
) : Closeable {
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val closed = AtomicBoolean(false)
    private val lock = Any()
    private var phase = Phase.IDLE
    private var activeConnectOwner: ConnectOwner? = null
    private var nextOwnerId = 0L
    private var suppressReplacementFailure = false
    private var authenticationRecoveryUsed = false
    private var authenticationRecoveryJob: Job? = null
    private var retryJob: Job? = null
    private var stabilityJob: Job? = null
    private var livenessProbeJob: Job? = null
    private var activeLivenessProbe: LivenessProbeOwner? = null
    private var nextLivenessProbeId = 0L
    private var retryAttempt = 0
    private val mutableState = MutableSharedFlow<RealtimeState>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()
    private val mutableConnectionEvents = MutableSharedFlow<ProductionRealtimeSessionConnectionEvent>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val connectionEvents: SharedFlow<ProductionRealtimeSessionConnectionEvent> =
        mutableConnectionEvents.asSharedFlow()
    private val stateJob: Job

    init {
        require(stableConnectionMillis >= 0) { "stableConnectionMillis must not be negative" }
        require(authenticationRefreshTimeoutMillis > 0) {
            "authenticationRefreshTimeoutMillis must be positive"
        }
        stateJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            socket.state.collect(::handleSocketState)
        }
    }

    fun connect(requiresEndpoint: Boolean = true) {
        requestConnect(
            trigger = "device realtime connect",
            action = ReconcileAction.CONNECT,
            requiresEndpoint = requiresEndpoint,
            bypassRetryDelay = false,
        )
    }

    fun onEndpointDemandRemoved() {
        synchronized(lock) {
            if (closed.get() || hasEndpointDemand()) return
            if (activeConnectOwner?.requiresEndpoint == true) activeConnectOwner = null
            cancelLivenessProbeLocked()
            retryJob?.cancel()
            retryJob = null
            if (phase == Phase.IDLE || phase == Phase.FAILED || phase == Phase.RETRY_WAIT) {
                phase = Phase.IDLE
            }
        }
    }

    fun onNetworkAvailable() {
        requestForegroundLivenessReconcile("default network became available")
    }

    fun onAppForegrounded(backgroundDurationMillis: Long, networkUsable: Boolean) {
        require(backgroundDurationMillis >= 0) { "background duration must not be negative" }
        if (!networkUsable) return
        if (backgroundDurationMillis < FOREGROUND_RECONNECT_THRESHOLD_MILLIS) {
            requestLifecycleReconcile(
                trigger = "application returned to foreground",
                forceReplacement = false,
            )
        } else {
            requestForegroundLivenessReconcile("application returned to foreground")
        }
    }

    /**
     * A bounded receive queue rejected authenticated ciphertext, so this socket
     * generation can no longer prove a contiguous stream. Replace that exact
     * generation once and recover every session from its durable ACK. Ordinary
     * renderer/session failures must not use this device-wide boundary.
     */
    fun replaceCurrentGenerationOnReceiveIntegrityFailure(
        expectedGeneration: Int,
        reason: String,
    ) {
        require(expectedGeneration > 0) { "expected generation must be positive" }
        val owner = synchronized(lock) {
            val diagnostics = socket.diagnostics()
            if (closed.get() || phase != Phase.CONNECTED || activeConnectOwner != null ||
                diagnostics.connectedGeneration != expectedGeneration
            ) {
                null
            } else {
                retryJob?.cancel()
                retryJob = null
                claimConnectLocked(
                    trigger = reason,
                    action = ReconcileAction.REPLACE,
                    requiresEndpoint = true,
                )
            }
        }
        owner?.let(::performOwnedSocketAction)
    }

    fun send(envelope: dev.termlink.protocol.realtime.v1.Envelope): Boolean = synchronized(lock) {
        if (closed.get() || phase != Phase.CONNECTED || activeConnectOwner != null) return@synchronized false
        socket.send(envelope)
    }

    fun sendForConnectedGeneration(
        envelope: dev.termlink.protocol.realtime.v1.Envelope,
        expectedGeneration: Int,
        onSent: (Int) -> Unit = { },
    ): Boolean = synchronized(lock) {
        if (expectedGeneration <= 0 || closed.get() || phase != Phase.CONNECTED ||
            activeConnectOwner != null || socket.diagnostics().connectedGeneration != expectedGeneration
        ) {
            return@synchronized false
        }
        socket.send(envelope).also { sent ->
            if (sent) onSent(expectedGeneration)
        }
    }

    fun currentConnectedGeneration(): Int = synchronized(lock) {
        if (closed.get() || phase != Phase.CONNECTED || activeConnectOwner != null) 0
        else socket.diagnostics().connectedGeneration
    }

    fun phaseName(): String = synchronized(lock) { phase.name }

    override fun close() {
        synchronized(lock) {
            if (!closed.compareAndSet(false, true)) return
            phase = Phase.CLOSED
            activeConnectOwner = null
            retryJob?.cancel()
            retryJob = null
            stabilityJob?.cancel()
            stabilityJob = null
            cancelLivenessProbeLocked()
            authenticationRecoveryJob?.cancel()
            authenticationRecoveryJob = null
            publishStateLocked(RealtimeState.Closed)
        }
        stateJob.cancel()
        socket.close()
        scope.cancel()
    }

    private fun requestLifecycleReconcile(trigger: String, forceReplacement: Boolean) {
        val owner = synchronized(lock) {
            if (closed.get() || phase == Phase.CLOSED || phase == Phase.AUTHENTICATING ||
                phase == Phase.AUTHENTICATION_FAILED || !hasEndpointDemand()
            ) {
                null
            } else if (activeConnectOwner != null) {
                // One physical connect owner absorbs concurrent lifecycle and
                // network signals. A second replacement after it connects
                // would add churn precisely while connectivity is recovering.
                null
            } else {
                retryJob?.cancel()
                retryJob = null
                when {
                    phase == Phase.IDLE || phase == Phase.FAILED || phase == Phase.RETRY_WAIT ->
                        claimConnectLocked(trigger, ReconcileAction.CONNECT, requiresEndpoint = true)
                    !forceReplacement && !socketStructurallyStaleLocked() -> null
                    else -> claimConnectLocked(trigger, ReconcileAction.REPLACE, requiresEndpoint = true)
                }
            }
        }
        owner?.let(::performOwnedSocketAction)
    }

    /**
     * A long background interval no longer proves that a healthy WebSocket is
     * stale. Probe the exact authenticated generation when the Relay negotiated
     * support. A legacy Relay without that proof stays connected until its own
     * socket I/O, close, or authenticated heartbeat establishes a hard failure.
     */
    private fun requestForegroundLivenessReconcile(trigger: String) {
        var owner: ConnectOwner? = null
        var probeJobToStart: Job? = null
        synchronized(lock) {
            if (closed.get() || phase == Phase.CLOSED || phase == Phase.AUTHENTICATING ||
                phase == Phase.AUTHENTICATION_FAILED || !hasEndpointDemand() ||
                activeConnectOwner != null
            ) return
            retryJob?.cancel()
            retryJob = null
            when {
                phase == Phase.IDLE || phase == Phase.FAILED || phase == Phase.RETRY_WAIT ->
                    owner = claimConnectLocked(trigger, ReconcileAction.CONNECT, requiresEndpoint = true)
                socketStructurallyStaleLocked() ->
                    owner = claimConnectLocked(trigger, ReconcileAction.REPLACE, requiresEndpoint = true)
                phase != Phase.CONNECTED || activeLivenessProbe != null -> Unit
                else -> {
                    val generation = socket.diagnostics().connectedGeneration
                    if (socket.supportsLivenessProbe(generation)) {
                        val probe = LivenessProbeOwner(++nextLivenessProbeId, generation, trigger)
                        activeLivenessProbe = probe
                        probeJobToStart = scope.launch(start = CoroutineStart.LAZY) {
                            completeForegroundLivenessProbe(probe)
                        }.also { livenessProbeJob = it }
                    }
                }
            }
        }
        owner?.let(::performOwnedSocketAction)
        probeJobToStart?.start()
    }

    private suspend fun completeForegroundLivenessProbe(probe: LivenessProbeOwner) {
        val alive = runCatching {
            socket.probeLiveness(probe.generation, FOREGROUND_LIVENESS_TIMEOUT_MILLIS)
        }.getOrDefault(false)
        val replacement = synchronized(lock) {
            if (activeLivenessProbe != probe) return@synchronized null
            activeLivenessProbe = null
            livenessProbeJob = null
            val diagnostics = socket.diagnostics()
            val sameHealthyGeneration = !closed.get() && phase == Phase.CONNECTED &&
                activeConnectOwner == null && hasEndpointDemand() &&
                diagnostics.generation == probe.generation &&
                diagnostics.connectedGeneration == probe.generation &&
                diagnostics.socketPresent && diagnostics.state == "CONNECTED"
            if (alive || !sameHealthyGeneration) null else {
                claimConnectLocked(
                    trigger = "${probe.trigger}; liveness probe timed out",
                    action = ReconcileAction.REPLACE,
                    requiresEndpoint = true,
                )
            }
        }
        replacement?.let(::performOwnedSocketAction)
    }

    private fun requestConnect(
        trigger: String,
        action: ReconcileAction,
        requiresEndpoint: Boolean,
        bypassRetryDelay: Boolean,
    ) {
        val owner = synchronized(lock) {
            if (closed.get() || phase == Phase.CLOSED || phase == Phase.AUTHENTICATING ||
                phase == Phase.AUTHENTICATION_FAILED || activeConnectOwner != null ||
                (requiresEndpoint && !hasEndpointDemand())
            ) {
                null
            } else if (action == ReconcileAction.CONNECT &&
                phase != Phase.IDLE && phase != Phase.FAILED && phase != Phase.RETRY_WAIT
            ) {
                null
            } else {
                if (bypassRetryDelay) {
                    retryJob?.cancel()
                    retryJob = null
                } else if (retryJob != null) {
                    return@synchronized null
                }
                claimConnectLocked(trigger, action, requiresEndpoint)
            }
        }
        owner?.let(::performOwnedSocketAction)
    }

    private fun claimConnectLocked(
        trigger: String,
        action: ReconcileAction,
        requiresEndpoint: Boolean,
    ): ConnectOwner {
        cancelLivenessProbeLocked()
        val owner = ConnectOwner(++nextOwnerId, trigger, action, requiresEndpoint)
        val oldGeneration = socket.diagnostics().generation
        onGenerationBoundary(oldGeneration)
        activeConnectOwner = owner
        suppressReplacementFailure = action == ReconcileAction.REPLACE
        phase = Phase.CONNECTING
        return owner
    }

    private fun performOwnedSocketAction(owner: ConnectOwner) {
        var failure: Exception? = null
        synchronized(lock) {
            if (activeConnectOwner != owner || closed.get() ||
                (owner.requiresEndpoint && !hasEndpointDemand())
            ) {
                if (activeConnectOwner == owner) {
                    activeConnectOwner = null
                    suppressReplacementFailure = false
                    if (!closed.get()) phase = Phase.IDLE
                }
                return
            }
            try {
                if (owner.action == ReconcileAction.REPLACE) {
                    publishStateLocked(RealtimeState.Reconnecting(EXTERNAL_RECONNECT_ATTEMPT, owner.trigger))
                    socket.disconnect(owner.trigger)
                }
                socket.connect()
            } catch (error: Exception) {
                if (activeConnectOwner == owner) {
                    activeConnectOwner = null
                    suppressReplacementFailure = false
                    if (!closed.get()) phase = Phase.FAILED
                }
                failure = error
            }
        }
        failure?.let { error ->
            synchronized(lock) {
                if (!closed.get()) publishStateLocked(RealtimeState.Failed(safeFailureReason(error)))
            }
            scheduleRetry()
        }
    }

    private fun handleSocketState(state: RealtimeState) {
        if (closed.get()) return
        if (state is RealtimeState.AuthenticationRejected) {
            handleAuthenticationRejected(state)
            return
        }

        var scheduleRetry = false
        synchronized(lock) {
            if (closed.get()) return@synchronized
            when (state) {
                RealtimeState.Connecting -> {
                    phase = Phase.CONNECTING
                    publishStateLocked(state)
                }
                RealtimeState.Connected -> {
                    authenticationRecoveryUsed = false
                    activeConnectOwner = null
                    suppressReplacementFailure = false
                    retryJob?.cancel()
                    retryJob = null
                    phase = Phase.CONNECTED
                    publishStateLocked(state)
                    scheduleStabilityResetLocked()
                }
                is RealtimeState.Reconnecting -> {
                    phase = Phase.CONNECTING
                    publishStateLocked(state)
                }
                is RealtimeState.AuthenticationFailed -> {
                    phase = Phase.AUTHENTICATION_FAILED
                    publishStateLocked(state)
                }
                is RealtimeState.Failed -> {
                    cancelLivenessProbeLocked()
                    if (activeConnectOwner != null && suppressReplacementFailure) {
                        suppressReplacementFailure = false
                        phase = Phase.CONNECTING
                    } else {
                        onGenerationBoundary(socket.diagnostics().generation)
                        activeConnectOwner = null
                        suppressReplacementFailure = false
                        stabilityJob?.cancel()
                        stabilityJob = null
                        phase = Phase.FAILED
                        publishStateLocked(state)
                        scheduleRetry = hasEndpointDemand()
                    }
                }
                RealtimeState.Closed -> {
                    cancelLivenessProbeLocked()
                    phase = Phase.CLOSED
                    publishStateLocked(state)
                }
                is RealtimeState.AuthenticationRejected -> error("handled above")
            }
        }
        if (scheduleRetry) scheduleRetry()
    }

    private fun scheduleRetry() {
        synchronized(lock) {
            if (closed.get() || retryJob != null || activeConnectOwner != null ||
                phase == Phase.AUTHENTICATING || phase == Phase.AUTHENTICATION_FAILED ||
                !hasEndpointDemand()
            ) return
            val attempt = ++retryAttempt
            val delayMillis = retryJitter.apply(retryBaseDelayMillis(attempt))
                .coerceIn(0L, MAX_RETRY_DELAY_MILLIS)
            phase = Phase.RETRY_WAIT
            publishStateLocked(RealtimeState.Reconnecting(attempt, "device connection unavailable"))
            retryJob = scope.launch {
                retryDelay.await(delayMillis)
                val owner = synchronized(lock) {
                    retryJob = null
                    if (closed.get() || activeConnectOwner != null || !hasEndpointDemand() ||
                        phase == Phase.AUTHENTICATING || phase == Phase.AUTHENTICATION_FAILED
                    ) null else claimConnectLocked(
                        trigger = "device realtime retry",
                        action = ReconcileAction.CONNECT,
                        requiresEndpoint = true,
                    )
                }
                owner?.let(::performOwnedSocketAction)
            }
        }
    }

    private fun scheduleStabilityResetLocked() {
        stabilityJob?.cancel()
        stabilityJob = scope.launch {
            retryDelay.await(stableConnectionMillis)
            synchronized(lock) {
                if (!closed.get() && phase == Phase.CONNECTED) retryAttempt = 0
                stabilityJob = null
            }
        }
    }

    private fun handleAuthenticationRejected(rejected: RealtimeState.AuthenticationRejected) {
        val recover = synchronized(lock) {
            cancelLivenessProbeLocked()
            activeConnectOwner = null
            suppressReplacementFailure = false
            retryJob?.cancel()
            retryJob = null
            stabilityJob?.cancel()
            stabilityJob = null
            if (closed.get() || phase == Phase.CLOSED) null
            else if (authenticationRecoveryUsed) {
                phase = Phase.AUTHENTICATION_FAILED
                false
            } else {
                authenticationRecoveryUsed = true
                phase = Phase.AUTHENTICATING
                true
            }
        } ?: return
        if (!recover) {
            publishAuthenticationFailure(
                IllegalStateException(
                    "realtime authentication was rejected after token refresh (HTTP ${rejected.status})",
                ),
            )
            return
        }
        synchronized(lock) {
            if (!closed.get() && phase == Phase.AUTHENTICATING) {
                publishStateLocked(RealtimeState.Reconnecting(1, "refreshing realtime authentication"))
            }
        }
        authenticationRecoveryJob = scope.launch {
            try {
                withTimeout(authenticationRefreshTimeoutMillis) {
                    authenticationRefresher.refreshAccessToken()
                }
            } catch (error: TimeoutCancellationException) {
                handleTransientAuthenticationRefreshFailure(
                    IllegalStateException("realtime authentication refresh timed out", error),
                )
                return@launch
            } catch (error: CancellationException) {
                // Closing the device supervisor or cancelling its owner is not
                // an authentication failure and must not sign the user out.
                throw error
            } catch (error: Throwable) {
                // Refresh transport errors, discovery failures and server 5xx
                // responses do not prove that the refresh credential is bad.
                // Keep the account signed in and retry the physical socket with
                // the normal bounded backoff. The product refresher owns the
                // only authoritative invalid-credential classification.
                handleTransientAuthenticationRefreshFailure(error)
                return@launch
            }
            val reconnect = synchronized(lock) {
                if (closed.get() || phase != Phase.AUTHENTICATING) false
                else {
                    phase = Phase.FAILED
                    true
                }
            }
            if (reconnect) {
                requestConnect(
                    trigger = "realtime authentication refreshed",
                    action = ReconcileAction.CONNECT,
                    requiresEndpoint = false,
                    bypassRetryDelay = true,
                )
            }
        }
    }

    private fun handleTransientAuthenticationRefreshFailure(error: Throwable) {
        val retry = synchronized(lock) {
            if (closed.get() || phase != Phase.AUTHENTICATING) {
                false
            } else {
                // A future authenticated rejection is allowed to attempt a
                // fresh token again. This does not reset the socket retry
                // counter, so repeated transient failures remain bounded.
                authenticationRecoveryUsed = false
                phase = Phase.FAILED
                publishStateLocked(RealtimeState.Failed(safeFailureReason(error)))
                hasEndpointDemand()
            }
        }
        if (retry) scheduleRetry()
    }

    private fun publishAuthenticationFailure(error: Throwable) {
        val publish = synchronized(lock) {
            if (closed.get()) false
            else {
                phase = Phase.AUTHENTICATION_FAILED
                publishStateLocked(
                    RealtimeState.AuthenticationFailed(error.message ?: "realtime authentication failed"),
                )
                true
            }
        }
        if (publish) runCatching { authenticationFailureObserver(error) }
    }

    private fun socketStructurallyStaleLocked(): Boolean {
        if (phase == Phase.IDLE || phase == Phase.FAILED || phase == Phase.RETRY_WAIT) return true
        if (phase == Phase.CONNECTING) return false
        if (phase != Phase.CONNECTED) return false
        val diagnostics = socket.diagnostics()
        return !diagnostics.socketPresent || diagnostics.generation <= 0 ||
            diagnostics.connectedGeneration != diagnostics.generation || diagnostics.state != "CONNECTED"
    }

    private fun cancelLivenessProbeLocked() {
        activeLivenessProbe = null
        livenessProbeJob?.cancel()
        livenessProbeJob = null
    }

    private fun publishStateLocked(state: RealtimeState) {
        val connectedGeneration = if (state is RealtimeState.Connected) {
            socket.diagnostics().connectedGeneration
        } else 0
        mutableConnectionEvents.tryEmit(
            ProductionRealtimeSessionConnectionEvent(state, connectedGeneration),
        )
        mutableState.tryEmit(state)
    }

    private enum class ReconcileAction { CONNECT, REPLACE }

    private enum class Phase {
        IDLE,
        CONNECTING,
        CONNECTED,
        RETRY_WAIT,
        AUTHENTICATING,
        AUTHENTICATION_FAILED,
        FAILED,
        CLOSED,
    }

    private data class ConnectOwner(
        val id: Long,
        val trigger: String,
        val action: ReconcileAction,
        val requiresEndpoint: Boolean,
    )

    private data class LivenessProbeOwner(
        val id: Long,
        val generation: Int,
        val trigger: String,
    )

    internal companion object {
        internal const val FOREGROUND_RECONNECT_THRESHOLD_MILLIS = 15_000L
        internal const val FOREGROUND_LIVENESS_TIMEOUT_MILLIS = 2_000L
        internal const val DEFAULT_AUTHENTICATION_REFRESH_TIMEOUT_MILLIS = 15_000L
        internal const val EXTERNAL_RECONNECT_ATTEMPT = 0
        internal const val DEFAULT_STABLE_CONNECTION_MILLIS = 30_000L
        internal const val INITIAL_RETRY_DELAY_MILLIS = 500L
        internal const val MAX_RETRY_DELAY_MILLIS = 30_000L

        internal fun retryBaseDelayMillis(attempt: Int): Long {
            require(attempt > 0) { "attempt must be positive" }
            val shift = min(attempt - 1, 16)
            return (INITIAL_RETRY_DELAY_MILLIS shl shift).coerceAtMost(MAX_RETRY_DELAY_MILLIS)
        }

        private fun safeFailureReason(error: Throwable): String =
            (error.message ?: "device realtime connection failed").take(160)
    }
}

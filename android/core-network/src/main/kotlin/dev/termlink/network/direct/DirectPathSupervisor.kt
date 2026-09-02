package dev.termlink.network.direct

import java.io.Closeable
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

internal enum class DirectFallbackReason {
    NOT_STARTED,
    FEATURE_DISABLED,
    NETWORK_CHANGED,
    REPLACED,
    CANCELLED,
    GATHERING_FAILED,
    STUN_FAILED,
    PROBE_FAILED,
    ATTEMPT_INCOMPLETE,
    INTERNAL_FAILURE,
    CLOSED,
}

internal sealed interface DirectPathState {
    data class RelayOnly(
        val reason: DirectFallbackReason,
        val retiredEpoch: Long? = null,
    ) : DirectPathState

    data class Gathering(val epoch: Long, val localPort: Int) : DirectPathState
    data class DiscoveringServerReflexive(val epoch: Long, val localPort: Int) : DirectPathState
    data class Probing(val epoch: Long, val localPort: Int) : DirectPathState

    /** UDP peer authentication passed; QUIC/peer proof/cutover are still required and absent here. */
    data class ProbeVerifiedNotDirect(val epoch: Long, val localPort: Int) : DirectPathState
}

/**
 * Scope handed to one bounded direct-path attempt. It contains only the epoch UDP socket and state
 * transitions. There is intentionally no Relay client/socket callback in this API.
 */
internal class DirectPathEpoch internal constructor(
    val epoch: Long,
    val endpoint: DirectDatagramEndpoint,
    private val token: Long,
    private val supervisor: DirectPathSupervisor,
) {
    fun discoveringServerReflexive(): Boolean = supervisor.transition(
        token,
        DirectPathState.DiscoveringServerReflexive(epoch, endpoint.localPort),
    )

    fun probing(): Boolean = supervisor.transition(
        token,
        DirectPathState.Probing(epoch, endpoint.localPort),
    )

    fun probeVerifiedNotDirect(): Boolean = supervisor.transition(
        token,
        DirectPathState.ProbeVerifiedNotDirect(epoch, endpoint.localPort),
    )

    fun fail(reason: DirectFallbackReason) {
        require(reason !in setOf(DirectFallbackReason.FEATURE_DISABLED, DirectFallbackReason.CLOSED)) {
            "attempt cannot report lifecycle-only fallback reason"
        }
        supervisor.failToken(token, reason)
    }
}

internal fun interface DirectPathEpochOperation {
    suspend fun run(epoch: DirectPathEpoch)
}

/**
 * Owns at most one direct-path epoch. All failures synchronously publish RELAY_ONLY and retire the
 * UDP resource; they cannot disconnect or even reference the production Relay socket.
 */
internal class DirectPathSupervisor(
    parentScope: CoroutineScope,
    private val feature: DirectTransportFeature = DirectTransportFeature(),
    private val endpointFactory: DirectDatagramEndpointFactory = SingleSocketDirectDatagramEndpoint.factory,
) : Closeable {
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val lock = Any()
    private val nextToken = AtomicLong()
    private var closed = false
    private var active: ActiveEpoch? = null
    private val mutableState = MutableStateFlow<DirectPathState>(
        DirectPathState.RelayOnly(
            if (feature.enabled) DirectFallbackReason.NOT_STARTED else DirectFallbackReason.FEATURE_DISABLED,
        ),
    )
    val state: StateFlow<DirectPathState> = mutableState.asStateFlow()

    fun startEpoch(epoch: Long, operation: DirectPathEpochOperation): Boolean {
        require(epoch > 0) { "path epoch must be positive" }
        if (!feature.enabled) {
            synchronized(lock) {
                if (!closed) mutableState.value = DirectPathState.RelayOnly(DirectFallbackReason.FEATURE_DISABLED)
            }
            return false
        }
        val endpoint = try {
            endpointFactory.open()
        } catch (_: Throwable) {
            val retired = synchronized(lock) {
                if (!closed) {
                    val prior = fallbackLocked(DirectFallbackReason.GATHERING_FAILED)
                    if (prior == null) {
                        mutableState.value = DirectPathState.RelayOnly(
                            DirectFallbackReason.GATHERING_FAILED,
                            retiredEpoch = epoch,
                        )
                    }
                    prior
                } else null
            }
            retire(retired)
            return false
        }
        val token = nextToken.incrementAndGet()
        val previous = synchronized(lock) {
            if (closed) {
                endpoint.close()
                return false
            }
            val retired = active
            active = ActiveEpoch(token, epoch, endpoint, job = null)
            mutableState.value = DirectPathState.Gathering(epoch, endpoint.localPort)
            retired
        }
        retire(previous)

        val epochScope = DirectPathEpoch(epoch, endpoint, token, this)
        val job = scope.launch {
            try {
                operation.run(epochScope)
                val complete = synchronized(lock) {
                    active?.token == token && mutableState.value is DirectPathState.ProbeVerifiedNotDirect
                }
                if (!complete) completeFailureFromWorker(token, DirectFallbackReason.ATTEMPT_INCOMPLETE)
            } catch (_: CancellationException) {
                completeFailureFromWorker(token, DirectFallbackReason.CANCELLED)
            } catch (_: Throwable) {
                completeFailureFromWorker(token, reasonForCurrentPhase(token))
            }
        }
        synchronized(lock) {
            if (active?.token == token) active = active?.copy(job = job) else job.cancel()
        }
        return true
    }

    fun onNetworkChanged() {
        val retired = synchronized(lock) {
            if (closed) return
            fallbackLocked(DirectFallbackReason.NETWORK_CHANGED)
        }
        retire(retired)
    }

    fun failCurrentEpoch(reason: DirectFallbackReason) {
        require(reason !in setOf(DirectFallbackReason.FEATURE_DISABLED, DirectFallbackReason.CLOSED)) {
            "invalid external fallback reason"
        }
        val retired = synchronized(lock) {
            if (closed) return
            fallbackLocked(reason)
        }
        retire(retired)
    }

    fun currentState(): DirectPathState = mutableState.value

    override fun close() {
        val retired = synchronized(lock) {
            if (closed) return
            closed = true
            fallbackLocked(DirectFallbackReason.CLOSED)
        }
        retire(retired)
        scope.cancel()
    }

    internal fun transition(token: Long, next: DirectPathState): Boolean = synchronized(lock) {
        val currentEpoch = active ?: return false
        if (closed || currentEpoch.token != token || stateEpoch(next) != currentEpoch.epoch) return false
        if (!isAllowedTransition(mutableState.value, next)) return false
        mutableState.value = next
        true
    }

    internal fun failToken(token: Long, reason: DirectFallbackReason) {
        val retired = synchronized(lock) {
            if (closed || active?.token != token) return
            fallbackLocked(reason)
        }
        retire(retired)
    }

    private fun completeFailureFromWorker(token: Long, reason: DirectFallbackReason) {
        val retired = synchronized(lock) {
            if (closed || active?.token != token) return
            fallbackLocked(reason)
        }
        // Do not cancel the currently completing worker; closing its one UDP socket is sufficient.
        retired?.endpoint?.close()
    }

    private fun reasonForCurrentPhase(token: Long): DirectFallbackReason = synchronized(lock) {
        if (active?.token != token) return DirectFallbackReason.CANCELLED
        when (mutableState.value) {
            is DirectPathState.Gathering -> DirectFallbackReason.GATHERING_FAILED
            is DirectPathState.DiscoveringServerReflexive -> DirectFallbackReason.STUN_FAILED
            is DirectPathState.Probing -> DirectFallbackReason.PROBE_FAILED
            is DirectPathState.ProbeVerifiedNotDirect -> DirectFallbackReason.INTERNAL_FAILURE
            is DirectPathState.RelayOnly -> DirectFallbackReason.CANCELLED
        }
    }

    private fun fallbackLocked(reason: DirectFallbackReason): ActiveEpoch? {
        val retired = active
        active = null
        mutableState.value = DirectPathState.RelayOnly(reason, retired?.epoch)
        return retired
    }

    private fun retire(epoch: ActiveEpoch?) {
        epoch ?: return
        epoch.job?.cancel()
        epoch.endpoint.close()
    }

    private fun stateEpoch(state: DirectPathState): Long? = when (state) {
        is DirectPathState.Gathering -> state.epoch
        is DirectPathState.DiscoveringServerReflexive -> state.epoch
        is DirectPathState.Probing -> state.epoch
        is DirectPathState.ProbeVerifiedNotDirect -> state.epoch
        is DirectPathState.RelayOnly -> state.retiredEpoch
    }

    private fun isAllowedTransition(current: DirectPathState, next: DirectPathState): Boolean = when (current) {
        is DirectPathState.Gathering ->
            next is DirectPathState.DiscoveringServerReflexive || next is DirectPathState.Probing
        is DirectPathState.DiscoveringServerReflexive -> next is DirectPathState.Probing
        is DirectPathState.Probing -> next is DirectPathState.ProbeVerifiedNotDirect
        is DirectPathState.ProbeVerifiedNotDirect, is DirectPathState.RelayOnly -> false
    }

    private data class ActiveEpoch(
        val token: Long,
        val epoch: Long,
        val endpoint: DirectDatagramEndpoint,
        val job: Job?,
    )
}

package dev.termlink.network.direct

import dev.termlink.protocol.realtime.v1.Direction
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Classification of one authenticated Direct path. */
internal enum class ProductionDirectPathKind {
    LAN,
    NAT,
}

/** Transport-only carrier state. It grants no session, renderer, lease, or input authority. */
internal enum class ProductionDirectPeerCarrier {
    RELAY,
    NEGOTIATING_DIRECT,
    LAN_DIRECT,
    NAT_DIRECT,
}

internal enum class ProductionDirectPeerRetirementReason {
    REPLACED,
    STALE_INSTALL,
    DIRECT_FAILURE,
    PATH_KIND_MISMATCH,
    PEER_REMOVED,
    AUTHENTICATION_CHANGED,
    NETWORK_CHANGED,
    RELAY_GENERATION_CHANGED,
    SIGNALING_GENERATION_CHANGED,
    SERVER_ROUTING_DISABLED,
    CLOSED,
}

/** Exact native-v5 path owner; an epoch alone is never sufficient authority. */
internal data class ProductionDirectPathFence(
    val pathEpoch: Long,
    val connectionId: UUID,
    val attemptId: UUID,
) {
    init {
        require(pathEpoch > 0) { "Direct path epoch must be positive" }
        require(connectionId != NIL_UUID) { "Direct connection ID is required" }
        require(attemptId != NIL_UUID) { "Direct attempt ID is required" }
    }

    private companion object {
        val NIL_UUID: UUID = UUID(0, 0)
    }
}

/**
 * Minimal lifecycle boundary implemented by a per-PC data-path actor.
 *
 * [retire] must synchronously and idempotently publish the actor's cancellation signal; it must
 * not wait for transport jobs to finish and must never restart or close the shared Relay, Agent,
 * or SessionHost. The registry invokes it outside its own lock. The runtime owner is responsible
 * for eventually calling its suspend/async close-and-join boundary outside this registry, so a
 * slow peer cannot block carrier observation or replacement for unrelated PCs.
 */
internal interface ProductionDirectPeerActor {
    val peerDeviceId: UUID
    val pathFence: ProductionDirectPathFence
    fun retire(reason: ProductionDirectPeerRetirementReason)
}

internal enum class ProductionDirectPeerUpdateResult {
    APPLIED,
    STALE,
    PEER_RETIRED,
}

/**
 * Owns independently replaceable Direct actors keyed only by the PC device UUID.
 *
 * A path is shown as Direct only after both semantic Envelope directions are active on the same
 * authenticated path kind. A partial cutover remains `NEGOTIATING_DIRECT`; absent or retired
 * peers are Relay. Failure and protocol mismatch remove only the exact peer/epoch entry.
 */
internal class ProductionDirectPeerActorRegistry<A : ProductionDirectPeerActor> : AutoCloseable {
    private val lock = Any()
    private val entries = LinkedHashMap<UUID, Entry<A>>()
    private var closed = false
    private val mutableCarriers = MutableStateFlow<Map<UUID, ProductionDirectPeerCarrier>>(emptyMap())

    val carriers: StateFlow<Map<UUID, ProductionDirectPeerCarrier>> = mutableCarriers.asStateFlow()

    fun install(actor: A): Boolean {
        require(actor.peerDeviceId != NIL_UUID) { "Direct peer device ID is required" }

        var retired: A? = null
        var rejected = false
        synchronized(lock) {
            val current = entries[actor.peerDeviceId]
            if (closed || current != null &&
                actor.pathFence.pathEpoch <= current.actor.pathFence.pathEpoch
            ) {
                rejected = true
            } else {
                retired = current?.actor
                entries[actor.peerDeviceId] = Entry(actor)
                publishCarriersLocked()
            }
        }
        if (rejected) {
            actor.retire(
                if (closed) ProductionDirectPeerRetirementReason.CLOSED
                else ProductionDirectPeerRetirementReason.STALE_INSTALL,
            )
            return false
        }
        retired?.retire(ProductionDirectPeerRetirementReason.REPLACED)
        return true
    }

    fun markDirectionActive(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        direction: Direction,
        pathKind: ProductionDirectPathKind,
    ): ProductionDirectPeerUpdateResult {
        require(peerDeviceId != NIL_UUID) { "Direct peer device ID is required" }
        if (direction != Direction.PC_TO_ANDROID && direction != Direction.ANDROID_TO_PC) {
            return ProductionDirectPeerUpdateResult.STALE
        }

        var mismatchedActor: A? = null
        val result = synchronized(lock) {
            val entry = entries[peerDeviceId]
            if (closed || entry == null || entry.actor.pathFence != pathFence) {
                ProductionDirectPeerUpdateResult.STALE
            } else if (entry.pathKind != null && entry.pathKind != pathKind) {
                entries.remove(peerDeviceId)
                publishCarriersLocked()
                mismatchedActor = entry.actor
                ProductionDirectPeerUpdateResult.PEER_RETIRED
            } else {
                entry.pathKind = pathKind
                when (direction) {
                    Direction.PC_TO_ANDROID -> entry.pcToAndroidActive = true
                    Direction.ANDROID_TO_PC -> entry.androidToPcActive = true
                    else -> error("direction was validated above")
                }
                publishCarriersLocked()
                ProductionDirectPeerUpdateResult.APPLIED
            }
        }
        mismatchedActor?.retire(ProductionDirectPeerRetirementReason.PATH_KIND_MISMATCH)
        return result
    }

    /** Returns one direction to Relay without retiring a healthy opposite direction. */
    fun markDirectionRelay(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        direction: Direction,
    ): ProductionDirectPeerUpdateResult {
        if (direction != Direction.PC_TO_ANDROID && direction != Direction.ANDROID_TO_PC) {
            return ProductionDirectPeerUpdateResult.STALE
        }
        return synchronized(lock) {
            val entry = entries[peerDeviceId]
            if (closed || entry == null || entry.actor.pathFence != pathFence) {
                ProductionDirectPeerUpdateResult.STALE
            } else {
                when (direction) {
                    Direction.PC_TO_ANDROID -> entry.pcToAndroidActive = false
                    Direction.ANDROID_TO_PC -> entry.androidToPcActive = false
                    else -> error("direction was validated above")
                }
                if (!entry.pcToAndroidActive && !entry.androidToPcActive) entry.pathKind = null
                publishCarriersLocked()
                ProductionDirectPeerUpdateResult.APPLIED
            }
        }
    }

    fun failPeer(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        reason: ProductionDirectPeerRetirementReason =
            ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
    ): Boolean {
        require(reason != ProductionDirectPeerRetirementReason.STALE_INSTALL) {
            "A live Direct peer cannot be retired as a stale install"
        }
        val retired = synchronized(lock) {
            val entry = entries[peerDeviceId]
            if (entry == null || entry.actor.pathFence != pathFence) {
                null
            } else {
                entries.remove(peerDeviceId)
                publishCarriersLocked()
                entry.actor
            }
        } ?: return false
        retired.retire(reason)
        return true
    }

    fun carrierFor(peerDeviceId: UUID): ProductionDirectPeerCarrier =
        carriers.value[peerDeviceId] ?: ProductionDirectPeerCarrier.RELAY

    fun actorFor(peerDeviceId: UUID): A? = synchronized(lock) { entries[peerDeviceId]?.actor }

    fun retireAll(reason: ProductionDirectPeerRetirementReason) {
        val retired = synchronized(lock) {
            entries.values.map(Entry<A>::actor).also {
                entries.clear()
                publishCarriersLocked()
            }
        }
        retired.forEach { it.retire(reason) }
    }

    override fun close() {
        val retired = synchronized(lock) {
            if (closed) return
            closed = true
            entries.values.map(Entry<A>::actor).also {
                entries.clear()
                publishCarriersLocked()
            }
        }
        retired.forEach { it.retire(ProductionDirectPeerRetirementReason.CLOSED) }
    }

    private fun publishCarriersLocked() {
        mutableCarriers.value = entries.mapValues { (_, entry) -> entry.aggregateCarrier() }
    }

    private class Entry<A : ProductionDirectPeerActor>(val actor: A) {
        var pathKind: ProductionDirectPathKind? = null
        var pcToAndroidActive = false
        var androidToPcActive = false

        fun aggregateCarrier(): ProductionDirectPeerCarrier {
            if (!pcToAndroidActive || !androidToPcActive) {
                return ProductionDirectPeerCarrier.NEGOTIATING_DIRECT
            }
            return when (pathKind) {
                ProductionDirectPathKind.LAN -> ProductionDirectPeerCarrier.LAN_DIRECT
                ProductionDirectPathKind.NAT -> ProductionDirectPeerCarrier.NAT_DIRECT
                null -> ProductionDirectPeerCarrier.RELAY
            }
        }
    }

    private companion object {
        val NIL_UUID: UUID = UUID(0, 0)
    }
}

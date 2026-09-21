package dev.termlink.network.direct

import dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5
import dev.termlink.network.direct.nativebridge.DirectNativeCursorV5
import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import dev.termlink.network.direct.nativebridge.DirectNativeEventV5
import dev.termlink.network.direct.nativebridge.DirectNativeEventKindV5
import dev.termlink.network.direct.nativebridge.DirectNativeIngressSourceV5
import dev.termlink.network.direct.nativebridge.DirectNativeIngressV5
import dev.termlink.network.direct.nativebridge.DirectNativeHandleV5
import dev.termlink.network.direct.nativebridge.DirectNativePathInfoV5
import dev.termlink.network.direct.nativebridge.DirectNativePathV5
import dev.termlink.network.direct.nativebridge.DirectNativeProposalV5
import dev.termlink.network.direct.nativebridge.DirectNativeResultV5
import dev.termlink.network.direct.nativebridge.DirectNativeSigningPayloadV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteEnvelopeV5
import dev.termlink.network.direct.nativebridge.DirectNativeTransitionV5
import dev.termlink.network.production.ProductionDirectCarrierOwner
import dev.termlink.network.production.ProductionRealtimeDirectDataGateway
import dev.termlink.network.production.ProductionRealtimeMultiplexer
import dev.termlink.network.production.ProductionRealtimeNegotiatedCapabilities
import dev.termlink.network.production.ProductionReceivedEnvelope
import dev.termlink.network.production.ProductionRelayCarrierOwner
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.Closeable
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.coroutines.cancellation.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Production data-plane wiring is intentionally compiled dark until the
 * cross-platform v5 cutover suite has passed. Shadow discovery continues to
 * run independently while this constant is false.
 */
internal const val PRODUCTION_DIRECT_DATA_PLANE_ENABLED = false

internal data class ProductionDirectDataAdmission(
    val serverRoutingPermitted: Boolean,
    val relayGeneration: Int,
    val realtimeCapabilities: Set<String>,
    val signalingGeneration: Int,
    val signalingCapabilities: Set<String>,
    val keyEpoch: Long,
) {
    fun permitsNewCutover(): Boolean = serverRoutingPermitted && relayGeneration > 0 &&
        signalingGeneration > 0 && keyEpoch > 0 &&
        DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in realtimeCapabilities &&
        DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in realtimeCapabilities &&
        DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in signalingCapabilities &&
        DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in signalingCapabilities
}

/**
 * Narrow v5 surface used by the per-PC actor. It keeps JNI and test doubles on
 * the same single-writer lane and deliberately exposes no Relay lifecycle API.
 */
internal interface ProductionDirectDataNative : Closeable {
    val path: DirectNativePathV5
    fun pathInfo(): DirectNativeResultV5<DirectNativePathInfoV5>
    fun startProposal(value: DirectNativeProposalV5): DirectNativeResultV5<Unit>
    fun receiveProposal(value: DirectNativeProposalV5): DirectNativeResultV5<Unit>
    fun receiveAccept(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit>
    fun receiveCommit(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit>
    fun commitEchoSent(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit>
    fun receiveFallback(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit>
    fun tick(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit>
    fun updateAppliedCursor(value: DirectNativeCursorV5): DirectNativeResultV5<Unit>
    fun deregisterSession(value: DirectNativeCursorV5): DirectNativeResultV5<Unit>
    fun routeEnvelope(
        value: DirectNativeRouteEnvelopeV5,
        exactEnvelope: ByteArray,
    ): DirectNativeResultV5<Unit>
    fun arbitrateIngress(
        value: DirectNativeIngressV5,
        exactEnvelope: ByteArray,
    ): DirectNativeResultV5<Unit>
    fun tickIngress(nowMillis: Long): DirectNativeResultV5<Unit>
    fun pollRouteAction(): DirectNativeResultV5<DirectNativeRouteActionV5?>
    fun pollEvent(): DirectNativeResultV5<DirectNativeEventV5?>
    fun receiveSigningPayload(): DirectNativeResultV5<DirectNativeSigningPayloadV5?>
    fun submitSignature(requestId: Long, signature: ByteArray): DirectNativeResultV5<Unit>
    fun receiveEnvelope(): DirectNativeResultV5<DirectNativeDataEnvelope?>
    fun cancel(): DirectNativeResultV5<Unit>
    fun networkChanged(): DirectNativeResultV5<Unit>

    /**
     * Applies one server-renewed path ticket (transport audit #2). The peer
     * verifies the renewed ticket against the cutover-time binding and the
     * pinned server signing key, then slides the applied expiry forward.
     * Default-success so pre-renewal test fakes keep compiling.
     */
    fun renewApplyTicket(ticket: ByteArray): DirectNativeResultV5<Unit> =
        DirectNativeResultV5.Success(Unit)
}

internal class JniProductionDirectDataNative(
    private val handle: DirectNativeHandleV5,
    override val path: DirectNativePathV5,
) : ProductionDirectDataNative {
    override fun pathInfo() = handle.pathInfo(path)
    override fun startProposal(value: DirectNativeProposalV5) = handle.startProposal(value)
    override fun receiveProposal(value: DirectNativeProposalV5) = handle.receiveProposal(value)
    override fun receiveAccept(value: DirectNativeTransitionV5) = handle.receiveAccept(value)
    override fun receiveCommit(value: DirectNativeTransitionV5) = handle.receiveCommit(value)
    override fun commitEchoSent(value: DirectNativeTransitionV5) = handle.commitEchoSent(value)
    override fun receiveFallback(value: DirectNativeTransitionV5) = handle.receiveFallback(value)
    override fun tick(value: DirectNativeTransitionV5) = handle.tick(value)
    override fun updateAppliedCursor(value: DirectNativeCursorV5) = handle.updateAppliedCursor(value)
    override fun deregisterSession(value: DirectNativeCursorV5) = handle.deregisterSession(value)
    override fun routeEnvelope(value: DirectNativeRouteEnvelopeV5, exactEnvelope: ByteArray) =
        handle.routeEnvelope(value, exactEnvelope)
    override fun arbitrateIngress(value: DirectNativeIngressV5, exactEnvelope: ByteArray) =
        handle.arbitrateIngress(value, exactEnvelope)
    override fun tickIngress(nowMillis: Long) = handle.tickIngress(path, nowMillis)
    override fun pollRouteAction() = handle.pollRouteAction(path)
    override fun pollEvent() = handle.pollEvent()
    override fun receiveSigningPayload() = handle.receiveSigningPayload(path)
    override fun renewApplyTicket(ticket: ByteArray) = handle.renewApply(path, ticket)
    override fun submitSignature(requestId: Long, signature: ByteArray) =
        handle.submitSignature(path, requestId, signature)
    override fun receiveEnvelope(): DirectNativeResultV5<DirectNativeDataEnvelope?> =
        when (val result = handle.receiveEnvelope(path)) {
            is DirectNativeResultV5.Success -> DirectNativeResultV5.Success(
                result.value?.let { DirectNativeDataEnvelope(it.frameSequence, it.bytes) },
            )
            is DirectNativeResultV5.Failure -> result
        }
    override fun cancel() = handle.cancel(path)
    override fun networkChanged() = handle.networkChanged(path)
    override fun close() = handle.close()
}

internal data class DirectNativeDataEnvelope(
    val frameSequence: Long,
    val exactEnvelopeBytes: ByteArray,
)

internal interface ProductionDirectDataSignalLane {
    val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities>
    fun send(envelope: TransportSignalEnvelopeV1): Boolean
}

internal fun interface ProductionDirectRelayExactSender {
    fun send(peerDeviceId: UUID, exactEnvelopeBytes: ByteArray): Boolean
}

internal fun interface ProductionDirectDataSigner {
    /** Returns an owned Ed25519 signature for the exact native transcript. */
    fun sign(transcript: ByteArray): ByteArray
}

/** Minimal actor boundary exposed to the independent signalling owner. */
internal interface ProductionDirectDataPeerBoundary {
    val peerDeviceId: UUID
    val pathFence: ProductionDirectPathFence
    val keyEpoch: Long
    val native: ProductionDirectDataNative
}

internal fun interface ProductionDirectSignalProcessor {
    fun process(actor: ProductionDirectDataPeerBoundary, envelope: TransportSignalEnvelopeV1): Boolean

    /** Handles one metadata-only native cutover action on this actor's single-writer lane. */
    fun processNativeAction(
        actor: ProductionDirectDataPeerBoundary,
        action: DirectNativeRouteActionV5,
    ): Boolean = false

    /** Advances all active direction/barrier cutover timers on the same lane. */
    fun tick(actor: ProductionDirectDataPeerBoundary, nowMillis: Long): Boolean = true

    fun startCutover(
        actor: ProductionDirectDataPeerBoundary,
        proposal: ProductionDirectCutoverProposal,
    ): Boolean = false

    fun deregisterSession(actor: ProductionDirectDataPeerBoundary, sessionId: UUID): Boolean = true

    /** Reconciles one signalling generation on this peer's single-writer lane. */
    fun signalingCapabilitiesChanged(
        actor: ProductionDirectDataPeerBoundary,
        value: DirectTransportSignalingNegotiatedCapabilities,
    ): Boolean = true

    fun retired(actor: ProductionDirectDataPeerBoundary) = Unit
}

/** Immutable capture used only while admitting a new path/cutover. */
internal data class ProductionDirectDataGate(
    val serverRoutingPermitted: Boolean,
    val relay: ProductionRealtimeNegotiatedCapabilities,
    val signaling: DirectTransportSignalingNegotiatedCapabilities,
    val keyEpoch: Long,
) {
    fun admission(): ProductionDirectDataAdmission = ProductionDirectDataAdmission(
        serverRoutingPermitted = serverRoutingPermitted,
        relayGeneration = relay.generation,
        realtimeCapabilities = relay.capabilities,
        signalingGeneration = signaling.generation,
        signalingCapabilities = signaling.capabilities,
        keyEpoch = keyEpoch,
    )
}

internal data class ProductionDirectDataPeerInstall(
    val peerDeviceId: UUID,
    val pathFence: ProductionDirectPathFence,
    val keyEpoch: Long,
    val native: ProductionDirectDataNative,
    val gate: ProductionDirectDataGate,
    /** Validated path ticket wire bytes at promotion; the renewal proof. */
    val ticketWire: ByteArray? = null,
    /** Server renewal exchange; null disables renewal for this install. */
    val renewTicket: (suspend (UUID, UUID, ByteArray) -> ByteArray?)? = null,
)

private sealed interface ProductionDirectPeerWork {
    data class RelayIngress(val received: ProductionReceivedEnvelope) : ProductionDirectPeerWork
    data class DirectIngress(val exactBytes: ByteArray) : ProductionDirectPeerWork
    data class Outbound(val envelope: Envelope, val exactBytes: ByteArray) : ProductionDirectPeerWork
    data class Signal(val envelope: TransportSignalEnvelopeV1) : ProductionDirectPeerWork
    data class AppliedCursor(val sessionId: UUID, val sequence: Long) : ProductionDirectPeerWork
    data class DeregisterSession(val sessionId: UUID) : ProductionDirectPeerWork
    data class SignalingCapabilitiesChanged(
        val value: DirectTransportSignalingNegotiatedCapabilities,
    ) : ProductionDirectPeerWork
    data class StartCutover(val proposal: ProductionDirectCutoverProposal) : ProductionDirectPeerWork
    data object Pump : ProductionDirectPeerWork
}

private data class PendingRelayIngress(
    val generation: Int,
    val exactBytes: ByteArray,
)

/**
 * Bounded, serialized owner of one PC's authenticated v5 path. No work item is
 * allowed to replace, reconnect or close the shared Relay.
 */
internal class ProductionDirectDataPeerActor(
    override val peerDeviceId: UUID,
    override val pathFence: ProductionDirectPathFence,
    override val keyEpoch: Long,
    override val native: ProductionDirectDataNative,
    private val multiplexer: ProductionRealtimeMultiplexer,
    private val relaySender: ProductionDirectRelayExactSender,
    private val signalProcessor: ProductionDirectSignalProcessor,
    private val signer: ProductionDirectDataSigner,
    parentScope: CoroutineScope,
    private val clockMillis: () -> Long,
    queueFrames: Int,
    queueBytes: Int,
    private val pumpIntervalMillis: Long,
    private val onCarrier: (Direction, DirectNativeCarrierV5) -> Unit,
    private val onSessionCarrier: (UUID, Direction, DirectNativeCarrierV5) -> Unit,
    private val onRetired: (ProductionDirectDataPeerActor, ProductionDirectPeerRetirementReason) -> Unit,
    ticketWire: ByteArray?,
    private val renewTicket: (suspend (peerDeviceId: UUID, connectionId: UUID, proof: ByteArray) -> ByteArray?)?,
) : ProductionDirectPeerActor, ProductionDirectDataPeerBoundary, Closeable {
    private val retired = AtomicBoolean(false)
    private val activated = AtomicBoolean(false)
    private val pumpQueued = AtomicBoolean(false)
    private val queuedBytes = AtomicInteger(0)
    private val renewalInFlight = AtomicBoolean(false)
    private val currentTicketLock = Any()
    private var currentTicket: ByteArray? = ticketWire?.copyOf()
    private val retirementReason = AtomicReference<ProductionDirectPeerRetirementReason?>(null)
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val work = Channel<ProductionDirectPeerWork>(
        capacity = queueFrames,
        onUndeliveredElement = ::destroyWork,
    )
    private val relayIngress = LinkedHashMap<Pair<UUID, Long>, PendingRelayIngress>()
    private val relayRecovery = ArrayDeque<ByteArray>()
    private val relayBacklogged = AtomicBoolean(false)
    private val maxQueueBytes = queueBytes
    private var relayRecoveryBytes = 0
    @Volatile
    private var ownsCurrentFence: () -> Boolean = { false }
    private val pumpJob = scope.launch(start = CoroutineStart.LAZY) {
        while (!retired.get()) {
            delay(pumpIntervalMillis)
            if (pumpQueued.compareAndSet(false, true) &&
                work.trySend(ProductionDirectPeerWork.Pump).isFailure
            ) {
                pumpQueued.set(false)
            }
        }
    }
    private val actorJob = scope.launch(start = CoroutineStart.LAZY) {
        try {
            if (!validatePromotedPath()) {
                requestRetirement(ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
                return@launch
            }
            for (item in work) {
                queuedBytes.addAndGet(-workBytes(item))
                if (item is ProductionDirectPeerWork.Pump) pumpQueued.set(false)
                if (retired.get()) {
                    recoverUnprocessed(item)
                    break
                }
                if (!process(item)) {
                    requestRetirement(ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
                    break
                }
                if (retired.get()) break
                if (!drainNative()) {
                    requestRetirement(ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
                    break
                }
            }
        } finally {
            withContext(NonCancellable) {
                pumpJob.cancelAndJoin()
                if (retirementReason.get() == null) {
                    requestRetirement(ProductionDirectPeerRetirementReason.CLOSED)
                }
                retireNativeAndDrainRecovery()
                drainUnprocessedWork()
                deliverHeldRelayIngress()
                runCatching(native::close)
                synchronized(currentTicketLock) {
                    currentTicket?.fill(0)
                    currentTicket = null
                }
                retryRelayRecovery()
                signalProcessor.retired(this@ProductionDirectDataPeerActor)
                work.cancel()
                scopeJob.cancel()
            }
        }
    }

    init {
        require(peerDeviceId != UUID(0, 0))
        require(native.path.sameFence(pathFence.toNative()))
        require(keyEpoch > 0)
        require(queueFrames in 1..256)
        require(queueBytes in 1..8 * 1024 * 1024)
        require(pumpIntervalMillis > 0)
    }

    /** Starts JNI ownership only after this exact actor has won the registry fence. */
    fun activate(currentFence: () -> Boolean) {
        check(activated.compareAndSet(false, true)) { "Direct peer actor is already active" }
        ownsCurrentFence = currentFence
        actorJob.start()
        pumpJob.start()
    }

    fun offerRelay(received: ProductionReceivedEnvelope): Boolean =
        offer(ProductionDirectPeerWork.RelayIngress(received), received.wireBytes)

    fun offerDirect(exactBytes: ByteArray): Boolean {
        if (exactBytes.isEmpty()) return false
        val owned = exactBytes.copyOf()
        if (offer(ProductionDirectPeerWork.DirectIngress(owned), owned.size)) return true
        owned.fill(0)
        return false
    }

    fun offerSignal(envelope: TransportSignalEnvelopeV1): Boolean =
        offer(ProductionDirectPeerWork.Signal(envelope), envelope.serializedSize)

    fun updateAppliedCursor(sessionId: UUID, sequence: Long): Boolean =
        sequence >= 0 && offer(ProductionDirectPeerWork.AppliedCursor(sessionId, sequence), 0)

    fun deregisterSession(sessionId: UUID): Boolean =
        sessionId != UUID(0, 0) && offer(ProductionDirectPeerWork.DeregisterSession(sessionId), 0)

    fun signalingCapabilitiesChanged(
        value: DirectTransportSignalingNegotiatedCapabilities,
    ): Boolean = offer(ProductionDirectPeerWork.SignalingCapabilitiesChanged(value), 0)

    fun startCutover(proposal: ProductionDirectCutoverProposal): Boolean {
        val accepted = offer(ProductionDirectPeerWork.StartCutover(proposal), proposal.wireBytes.size)
        if (!accepted) proposal.close()
        return accepted
    }

    fun routeOutbound(envelope: Envelope, exactBytes: ByteArray, relayFallback: () -> Boolean): Boolean {
        if (retired.get() || relayBacklogged.get() ||
            !DirectRouteEligibility.isEligible(envelope)
        ) return relayFallback()
        val owned = exactBytes.copyOf()
        if (offer(ProductionDirectPeerWork.Outbound(envelope, owned), owned.size)) return true
        owned.fill(0)
        return relayFallback()
    }

    override fun retire(reason: ProductionDirectPeerRetirementReason) {
        requestRetirement(reason)
        // A rejected install is never activated, but its JNI owner must still
        // be cancelled and closed on its own single-writer lane.
        actorJob.start()
    }

    fun networkChanged() = requestRetirement(ProductionDirectPeerRetirementReason.NETWORK_CHANGED)

    override fun close() = retire(ProductionDirectPeerRetirementReason.CLOSED)

    fun invokeOnCompletion(handler: () -> Unit) {
        actorJob.invokeOnCompletion { handler() }
    }

    suspend fun closeAndJoin() {
        close()
        withContext(NonCancellable) {
            actorJob.join()
            pumpJob.join()
        }
    }

    private fun offer(item: ProductionDirectPeerWork, bytes: Int): Boolean {
        if (retired.get() || bytes < 0) return false
        val reserved = queuedBytes.addAndGet(bytes)
        if (reserved > maxQueueBytes) {
            queuedBytes.addAndGet(-bytes)
            return false
        }
        if (work.trySend(item).isSuccess) return true
        queuedBytes.addAndGet(-bytes)
        return false
    }

    private fun process(item: ProductionDirectPeerWork): Boolean = when (item) {
        is ProductionDirectPeerWork.RelayIngress -> processRelay(item.received)
        is ProductionDirectPeerWork.DirectIngress -> processDirect(item.exactBytes)
        is ProductionDirectPeerWork.Outbound -> processOutbound(item)
        is ProductionDirectPeerWork.Signal -> signalProcessor.process(this, item.envelope)
        is ProductionDirectPeerWork.AppliedCursor -> native.updateAppliedCursor(
            DirectNativeCursorV5(
                native.path,
                DirectNativeDirectionV5.PC_TO_ANDROID,
                item.sessionId.toNetworkBytes(),
                item.sequence,
            ),
        ) is DirectNativeResultV5.Success
        is ProductionDirectPeerWork.DeregisterSession -> deregisterSessionNative(item.sessionId)
        is ProductionDirectPeerWork.SignalingCapabilitiesChanged ->
            signalProcessor.signalingCapabilitiesChanged(this, item.value)
        is ProductionDirectPeerWork.StartCutover -> try {
            signalProcessor.startCutover(this, item.proposal)
        } finally {
            item.proposal.close()
        }
        ProductionDirectPeerWork.Pump -> {
            val nowMillis = clockMillis()
            retryRelayRecovery()
            signalProcessor.tick(this, nowMillis) &&
                native.tickIngress(nowMillis) is DirectNativeResultV5.Success
        }
    }

    private fun deregisterSessionNative(sessionId: UUID): Boolean {
        if (!signalProcessor.deregisterSession(this, sessionId)) return false
        val sessionBytes = sessionId.toNetworkBytes()
        for (direction in listOf(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            DirectNativeDirectionV5.ANDROID_TO_PC,
        )) {
            if (native.deregisterSession(
                    DirectNativeCursorV5(native.path, direction, sessionBytes, 0),
                ) !is DirectNativeResultV5.Success
            ) return false
        }
        removeSessionRelayIngress(sessionId)
        removeSessionRelayRecovery(sessionId)
        return true
    }

    private fun processOutbound(item: ProductionDirectPeerWork.Outbound): Boolean {
        try {
            val sessionId = item.envelope.sessionUuid()
                ?: return sendRelayExact(item.exactBytes)
            val routed = native.routeEnvelope(
                DirectNativeRouteEnvelopeV5(
                    native.path,
                    DirectNativeDirectionV5.ANDROID_TO_PC,
                    DirectRouteEligibility.isEligible(item.envelope),
                    sessionId.toNetworkBytes(),
                    item.envelope.seq,
                    clockMillis(),
                ),
                item.exactBytes,
            )
            if (routed is DirectNativeResultV5.Success) return true
            // Native did not take ownership. Preserve the first serialization
            // on Relay, then retire only this peer actor.
            sendRelayExact(item.exactBytes)
            return false
        } finally {
            item.exactBytes.fill(0)
        }
    }

    private fun processRelay(received: ProductionReceivedEnvelope): Boolean {
        try {
            val envelope = received.envelope
            val sessionId = envelope.sessionUuid() ?: return false
            if (!DirectRouteEligibility.isEligible(envelope)) {
                return multiplexer.deliverArbitratedExact(
                    peerDeviceId,
                    ProductionRelayCarrierOwner(received.generation),
                    received.exactBytes,
                )
            }
            val ownerKey = sessionId to envelope.seq
            if (relayIngress.containsKey(ownerKey)) return false
            relayIngress[ownerKey] = PendingRelayIngress(
                received.generation,
                received.copyRawBytes(),
            )
            val result = native.arbitrateIngress(
                DirectNativeIngressV5(
                    native.path,
                    DirectNativeDirectionV5.PC_TO_ANDROID,
                    DirectNativeIngressSourceV5.RELAY,
                    sessionId.toNetworkBytes(),
                    envelope.seq,
                    clockMillis(),
                    DEFAULT_DUPLICATE_GRACE_MILLIS,
                ),
                received.exactBytes,
            )
            if (result is DirectNativeResultV5.Success) return true
            deliverHeldRelay(ownerKey)
            return false
        } finally {
            received.close()
        }
    }

    private fun processDirect(bytes: ByteArray): Boolean {
        try {
            val envelope = runCatching { Envelope.parseFrom(bytes) }.getOrNull() ?: return false
            val sessionId = envelope.sessionUuid() ?: return false
            if (!DirectRouteEligibility.isEligible(envelope)) return false
            return native.arbitrateIngress(
                DirectNativeIngressV5(
                    native.path,
                    DirectNativeDirectionV5.PC_TO_ANDROID,
                    DirectNativeIngressSourceV5.DIRECT,
                    sessionId.toNetworkBytes(),
                    envelope.seq,
                    clockMillis(),
                    DEFAULT_DUPLICATE_GRACE_MILLIS,
                ),
                bytes,
            ) is DirectNativeResultV5.Success
        } finally {
            bytes.fill(0)
        }
    }

    private fun drainNative(recoveryOnly: Boolean = false): Boolean {
        for (ignored in 0 until MAX_ACTIONS_PER_TURN) {
            val action = when (val result = native.pollRouteAction()) {
                is DirectNativeResultV5.Success -> result.value ?: break
                is DirectNativeResultV5.Failure -> return false
            }
            if (!applyAction(action, recoveryOnly)) return false
        }
        if (recoveryOnly) return true
        for (ignored in 0 until MAX_EVENTS_PER_TURN) {
            val event = when (val result = native.pollEvent()) {
                is DirectNativeResultV5.Success -> result.value ?: break
                is DirectNativeResultV5.Failure -> return false
            }
            if (!applyEvent(event)) return false
        }
        return true
    }

    private fun applyEvent(event: DirectNativeEventV5): Boolean {
        if (event.pathEpoch != pathFence.pathEpoch ||
            !event.attemptId.contentEquals(native.path.attemptId)
        ) return false
        return when (event.kind) {
            DirectNativeEventKindV5.ENVELOPE_AVAILABLE -> {
                for (ignored in 0 until MAX_ENVELOPES_PER_EVENT) {
                    val incoming = when (val result = native.receiveEnvelope()) {
                        is DirectNativeResultV5.Success -> result.value ?: return true
                        is DirectNativeResultV5.Failure -> return false
                    }
                    if (!processDirect(incoming.exactEnvelopeBytes)) return false
                }
                true
            }
            DirectNativeEventKindV5.DIRECT_FAILED,
            DirectNativeEventKindV5.CANCELLED,
            DirectNativeEventKindV5.NETWORK_CHANGED,
            DirectNativeEventKindV5.QUEUE_LIMIT,
            -> false
            DirectNativeEventKindV5.PATH_USED,
            DirectNativeEventKindV5.ENVELOPE_QUEUED,
            DirectNativeEventKindV5.TRANSPORT_RECEIPT,
            DirectNativeEventKindV5.PATH_READY,
            DirectNativeEventKindV5.PING,
            DirectNativeEventKindV5.PONG,
            -> true
            DirectNativeEventKindV5.SIGN_REQUEST -> processSigningRequest()
            DirectNativeEventKindV5.RENEWAL_DUE -> processTicketRenewal()
            DirectNativeEventKindV5.UNKNOWN -> false
        }
    }

    private fun processSigningRequest(): Boolean {
        val request = when (val result = native.receiveSigningPayload()) {
            is DirectNativeResultV5.Success -> result.value ?: return false
            is DirectNativeResultV5.Failure -> return false
        }
        val signature = try {
            signer.sign(request.bytes)
        } catch (_: Exception) {
            request.bytes.fill(0)
            return false
        }
        return try {
            signature.isNotEmpty() &&
                native.submitSignature(request.requestId, signature) is DirectNativeResultV5.Success
        } finally {
            request.bytes.fill(0)
            signature.fill(0)
        }
    }

    /**
     * Ticket renewal due (transport audit #2). The FFI emits the event once
     * per ticket generation, so the actor owns the retry loop: fetch a
     * renewed ticket from the server, apply it through renew_apply, and on
     * acceptance slide the stored proof to the new generation. Failures back
     * off and retry until the path retires; the native hard stop at
     * expiry+grace remains the final fallback.
     */
    private fun processTicketRenewal(): Boolean {
        val renew = renewTicket ?: return true
        val proof = synchronized(currentTicketLock) {
            val snapshot = currentTicket ?: return true
            snapshot.copyOf()
        }
        if (!renewalInFlight.compareAndSet(false, true)) return true
        scope.launch {
            try {
                var backoffMillis = 1_000L
                while (!retired.get()) {
                    val renewed = try {
                        renew(peerDeviceId, pathFence.connectionId, proof)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        null
                    }
                    if (renewed != null &&
                        native.renewApplyTicket(renewed) is DirectNativeResultV5.Success
                    ) {
                        synchronized(currentTicketLock) {
                            currentTicket?.fill(0)
                            currentTicket = renewed.copyOf()
                        }
                        return@launch
                    }
                    if (retired.get()) return@launch
                    delay(backoffMillis)
                    backoffMillis = (backoffMillis * 2).coerceAtMost(8_000L)
                }
            } catch (error: CancellationException) {
                // Path retired or runtime closed; the latch resets below.
            } finally {
                proof.fill(0)
                renewalInFlight.set(false)
            }
        }
        return true
    }

    private fun applyAction(action: DirectNativeRouteActionV5, recoveryOnly: Boolean): Boolean {
        if (!action.path.sameFence(native.path)) return false
        if (!ownsCurrentFence() && !recoveryOnly) return false
        return when (action.kind) {
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.INGRESS_DELIVER -> {
                val sessionId = action.sessionId.toUuid() ?: return false
                val owner = when (action.carrier) {
                    DirectNativeCarrierV5.RELAY -> {
                        val key = sessionId to action.envelopeSequence
                        val pending = relayIngress[key] ?: return false
                        if (!action.payload.contentEquals(pending.exactBytes)) return false
                        val delivered = multiplexer.deliverArbitratedExact(
                            peerDeviceId,
                            ProductionRelayCarrierOwner(pending.generation),
                            pending.exactBytes,
                        )
                        if (delivered) removeHeldRelay(key)
                        action.payload.fill(0)
                        return delivered
                    }
                    DirectNativeCarrierV5.LAN_DIRECT,
                    DirectNativeCarrierV5.NAT_DIRECT,
                    -> {
                        if (recoveryOnly || !ownsCurrentFence()) {
                            action.payload.fill(0)
                            return true
                        }
                        ProductionDirectCarrierOwner(
                            peerDeviceId,
                            pathFence.pathEpoch,
                            native.path.connectionId,
                            native.path.attemptId,
                        )
                    }
                    null -> return false
                }
                try {
                    multiplexer.deliverArbitratedExact(peerDeviceId, owner, action.payload)
                } finally {
                    action.payload.fill(0)
                }
            }
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.RELAY_ENVELOPE ->
                try { sendRelayExact(action.payload) } finally { action.payload.fill(0) }
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.CARRIER -> {
                if (recoveryOnly || !ownsCurrentFence()) return true
                onCarrier(action.direction.toRealtime(), action.carrier ?: return false)
                true
            }
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.SESSION_CARRIER ->
                if (recoveryOnly || !ownsCurrentFence()) {
                    true
                } else {
                    val sessionId = action.sessionId.toUuid() ?: return false
                    onSessionCarrier(
                        sessionId,
                        action.direction.toRealtime(),
                        action.carrier ?: return false,
                    )
                    true
                }
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.DIRECT_ENVELOPE ->
                // Native owns and writes the exact QUIC bytes. This action is
                // deliberately metadata-only, but a predecessor may not alter
                // successor state after its full fence has been replaced.
                recoveryOnly || ownsCurrentFence()
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.ACCEPT,
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.COMMIT,
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.FALLBACK,
            -> if (recoveryOnly || !ownsCurrentFence()) true
            else signalProcessor.processNativeAction(this, action)
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.INGRESS_HOLD ->
                // Only Direct ingress enters the grace hold. Native retains its
                // exact copy; no Relay ownership entry is created here.
                true
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.INGRESS_DROP -> {
                val sessionId = action.sessionId.toUuid() ?: return false
                removeHeldRelay(sessionId to action.envelopeSequence)
                true
            }
            dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5.UNKNOWN -> false
        }
    }

    private fun destroyWork(item: ProductionDirectPeerWork) {
        queuedBytes.addAndGet(-workBytes(item))
        when (item) {
            is ProductionDirectPeerWork.RelayIngress -> try {
                multiplexer.deliverArbitratedExact(
                    peerDeviceId,
                    ProductionRelayCarrierOwner(item.received.generation),
                    item.received.exactBytes,
                )
            } finally {
                item.received.close()
            }
            is ProductionDirectPeerWork.DirectIngress -> item.exactBytes.fill(0)
            is ProductionDirectPeerWork.Outbound -> try {
                sendRelayExact(item.exactBytes)
            } finally {
                item.exactBytes.fill(0)
            }
            is ProductionDirectPeerWork.StartCutover -> item.proposal.close()
            else -> Unit
        }
    }

    private fun workBytes(item: ProductionDirectPeerWork): Int = when (item) {
        is ProductionDirectPeerWork.RelayIngress -> item.received.wireBytes
        is ProductionDirectPeerWork.DirectIngress -> item.exactBytes.size
        is ProductionDirectPeerWork.Outbound -> item.exactBytes.size
        is ProductionDirectPeerWork.Signal -> item.envelope.serializedSize
        is ProductionDirectPeerWork.StartCutover -> item.proposal.wireBytes.size
        else -> 0
    }

    private fun validatePromotedPath(): Boolean {
        if (retired.get() || !ownsCurrentFence()) return false
        val info = when (val result = native.pathInfo()) {
            is DirectNativeResultV5.Success -> result.value
            is DirectNativeResultV5.Failure -> return false
        }
        return info.ready && info.path.sameFence(native.path) &&
            info.pathKind != null && info.directPathBinding.size == 32 &&
            info.directPathBinding.any { it != 0.toByte() }
    }

    private fun requestRetirement(reason: ProductionDirectPeerRetirementReason) {
        if (!retired.compareAndSet(false, true)) return
        retirementReason.compareAndSet(null, reason)
        pumpJob.cancel()
        work.close()
        onRetired(this, reason)
    }

    /**
     * Native owns bytes accepted before a failure. Cancel/network-change first,
     * then drain the fixed emergency action tail before destroying the handle.
     */
    private fun retireNativeAndDrainRecovery() {
        val result = runCatching {
            when (retirementReason.get()) {
                ProductionDirectPeerRetirementReason.NETWORK_CHANGED -> native.networkChanged()
                else -> native.cancel()
            }
        }.getOrNull()
        // Both OK and CANCELLED/failure retire locally. A non-OK result does not
        // authorize skipping recovery actions already committed by native.
        @Suppress("UNUSED_VARIABLE") val ignored = result
        repeat(MAX_FAILURE_DRAIN_ROUNDS) {
            if (!drainNative(recoveryOnly = true)) return@repeat
        }
    }

    private fun drainUnprocessedWork() {
        while (true) {
            val pending = work.tryReceive().getOrNull() ?: break
            queuedBytes.addAndGet(-workBytes(pending))
            if (pending is ProductionDirectPeerWork.Pump) pumpQueued.set(false)
            recoverUnprocessed(pending)
        }
    }

    private fun recoverUnprocessed(item: ProductionDirectPeerWork) {
        when (item) {
            is ProductionDirectPeerWork.RelayIngress -> try {
                multiplexer.deliverArbitratedExact(
                    peerDeviceId,
                    ProductionRelayCarrierOwner(item.received.generation),
                    item.received.exactBytes,
                )
            } finally {
                item.received.close()
            }
            is ProductionDirectPeerWork.Outbound -> try {
                sendRelayExact(item.exactBytes)
            } finally {
                item.exactBytes.fill(0)
            }
            is ProductionDirectPeerWork.DirectIngress -> item.exactBytes.fill(0)
            else -> Unit
        }
    }

    private fun sendRelayExact(exactBytes: ByteArray): Boolean {
        if (!relayBacklogged.get() && relaySender.send(peerDeviceId, exactBytes)) return true
        if (exactBytes.isEmpty() ||
            relayRecoveryBytes > MAX_RELAY_RECOVERY_BYTES - exactBytes.size
        ) return false
        relayRecovery.addLast(exactBytes.copyOf())
        relayRecoveryBytes += exactBytes.size
        relayBacklogged.set(true)
        return true
    }

    private fun retryRelayRecovery() {
        while (relayRecovery.isNotEmpty()) {
            val exact = relayRecovery.first()
            if (!relaySender.send(peerDeviceId, exact)) break
            relayRecovery.removeFirst()
            relayRecoveryBytes -= exact.size
            exact.fill(0)
        }
        relayBacklogged.set(relayRecovery.isNotEmpty())
    }

    private fun deliverHeldRelay(key: Pair<UUID, Long>): Boolean {
        val pending = relayIngress[key] ?: return false
        val delivered = multiplexer.deliverArbitratedExact(
            peerDeviceId,
            ProductionRelayCarrierOwner(pending.generation),
            pending.exactBytes,
        )
        if (delivered) removeHeldRelay(key)
        return delivered
    }

    private fun removeHeldRelay(key: Pair<UUID, Long>) {
        relayIngress.remove(key)?.exactBytes?.fill(0)
    }

    private fun removeSessionRelayIngress(sessionId: UUID) {
        relayIngress.keys.filter { it.first == sessionId }.forEach(::removeHeldRelay)
    }

    private fun removeSessionRelayRecovery(sessionId: UUID) {
        val retained = ArrayDeque<ByteArray>(relayRecovery.size)
        while (relayRecovery.isNotEmpty()) {
            val exact = relayRecovery.removeFirst()
            val belongsToSession = runCatching {
                Envelope.parseFrom(exact).sessionUuid() == sessionId
            }.getOrDefault(false)
            if (belongsToSession) {
                relayRecoveryBytes -= exact.size
                exact.fill(0)
            } else {
                retained.addLast(exact)
            }
        }
        relayRecovery.addAll(retained)
        relayBacklogged.set(relayRecovery.isNotEmpty())
    }

    private fun deliverHeldRelayIngress() {
        relayIngress.keys.toList().forEach(::deliverHeldRelay)
    }

    private companion object {
        const val MAX_ACTIONS_PER_TURN = 260
        const val MAX_EVENTS_PER_TURN = 260
        const val MAX_ENVELOPES_PER_EVENT = 256
        const val MAX_FAILURE_DRAIN_ROUNDS = 2
        const val MAX_RELAY_RECOVERY_BYTES = 16 * 1024 * 1024
        const val DEFAULT_DUPLICATE_GRACE_MILLIS = 500
    }
}

/** Device owner and gateway for independently failing per-PC actors. */
internal class ProductionDirectDataRuntime(
    private val multiplexer: ProductionRealtimeMultiplexer,
    private val realtimeCapabilities: StateFlow<ProductionRealtimeNegotiatedCapabilities>,
    private val signaling: ProductionDirectDataSignalLane,
    private val relaySender: ProductionDirectRelayExactSender,
    private val signalProcessor: ProductionDirectSignalProcessor,
    private val signer: ProductionDirectDataSigner,
    parentScope: CoroutineScope,
    private val enabled: Boolean = PRODUCTION_DIRECT_DATA_PLANE_ENABLED,
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val queueFramesPerPeer: Int = 256,
    private val queueBytesPerPeer: Int = 8 * 1024 * 1024,
    private val pumpIntervalMillis: Long = 10L,
    private val peerRetirementObserver: (UUID, ProductionDirectPeerRetirementReason) -> Unit = { _, _ -> },
    private val sessionCarrierObserver: (
        UUID,
        ProductionDirectPathFence,
        UUID,
        Direction,
        DirectNativeCarrierV5,
    ) -> Unit = { _, _, _, _, _ -> },
) : ProductionRealtimeDirectDataGateway, Closeable {
    private val closed = AtomicBoolean(false)
    private val actorLifecycleLock = Any()
    private val actors = ConcurrentHashMap<ProductionDirectDataPeerActor, Unit>()
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val registry = ProductionDirectPeerActorRegistry<ProductionDirectDataPeerActor>()
    private val gatewayRegistration = multiplexer.installDirectDataGateway(this)

    val carriers: StateFlow<Map<UUID, ProductionDirectPeerCarrier>> = registry.carriers

    init {
        require(queueFramesPerPeer in 1..256)
        require(queueBytesPerPeer in 1..8 * 1024 * 1024)
        require(pumpIntervalMillis > 0)
    }

    fun captureGate(serverRoutingPermitted: Boolean, keyEpoch: Long): ProductionDirectDataGate =
        ProductionDirectDataGate(
            serverRoutingPermitted,
            realtimeCapabilities.value,
            signaling.negotiatedCapabilities.value,
            keyEpoch,
        )

    /**
     * Installs only a path promoted under one complete three-layer gate. Later
     * Relay loss/generation change does not retire it; those generations are
     * admission fences, not owners of an already committed Direct path.
     */
    fun installPromotedPeer(value: ProductionDirectDataPeerInstall): Boolean {
        if (closed.get() || !enabled ||
            !value.gate.admission().permitsNewCutover() ||
            value.gate.relay != realtimeCapabilities.value ||
            value.gate.signaling != signaling.negotiatedCapabilities.value ||
            value.keyEpoch != value.gate.keyEpoch ||
            !value.native.path.sameFence(value.pathFence.toNative())
        ) {
            runCatching(value.native::close)
            return false
        }
        val actor = ProductionDirectDataPeerActor(
            value.peerDeviceId,
            value.pathFence,
            value.keyEpoch,
            value.native,
            multiplexer,
            relaySender,
            signalProcessor,
            signer,
            scope,
            clockMillis,
            queueFramesPerPeer,
            queueBytesPerPeer,
            pumpIntervalMillis,
            ticketWire = value.ticketWire,
            renewTicket = value.renewTicket,
            onCarrier = { direction, carrier ->
                when (carrier) {
                    DirectNativeCarrierV5.LAN_DIRECT,
                    DirectNativeCarrierV5.NAT_DIRECT,
                    -> registry.markDirectionActive(
                        value.peerDeviceId,
                        value.pathFence,
                        direction,
                        if (carrier == DirectNativeCarrierV5.LAN_DIRECT) {
                            ProductionDirectPathKind.LAN
                        } else {
                            ProductionDirectPathKind.NAT
                        },
                    )
                    DirectNativeCarrierV5.RELAY -> registry.markDirectionRelay(
                        value.peerDeviceId,
                        value.pathFence,
                        direction,
                    )
                }
            },
            onSessionCarrier = { sessionId, direction, carrier ->
                sessionCarrierObserver(
                    value.peerDeviceId,
                    value.pathFence,
                    sessionId,
                    direction,
                    carrier,
                )
            },
            onRetired = actorRetired@{ retired, reason ->
                // A stale install never owned the registry fence. Its actor still
                // runs its local native cleanup, but must not re-enter failPeer(),
                // which is reserved for the currently installed actor.
                if (reason == ProductionDirectPeerRetirementReason.STALE_INSTALL) {
                    return@actorRetired
                }
                if (registry.failPeer(retired.peerDeviceId, retired.pathFence, reason)) {
                    peerRetirementObserver(retired.peerDeviceId, reason)
                }
            }
        )
        val installed = synchronized(actorLifecycleLock) {
            actors[actor] = Unit
            actor.invokeOnCompletion { actors.remove(actor) }
            if (closed.get()) {
                false
            } else {
                registry.install(actor)
            }
        }
        if (!installed) {
            actor.retire(
                if (closed.get()) ProductionDirectPeerRetirementReason.CLOSED
                else ProductionDirectPeerRetirementReason.STALE_INSTALL,
            )
            return false
        }
        actor.activate { registry.actorFor(value.peerDeviceId) === actor }
        return true
    }

    override fun offerRelay(peerDeviceId: UUID, received: ProductionReceivedEnvelope): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        if (actor.offerRelay(received)) return true
        registry.failPeer(peerDeviceId, actor.pathFence, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
        return false
    }

    override fun routeEligibleOutbound(
        peerDeviceId: UUID,
        envelope: Envelope,
        exactEnvelopeBytes: ByteArray,
        relayFallback: () -> Boolean,
    ): Boolean = registry.actorFor(peerDeviceId)?.routeOutbound(
        envelope,
        exactEnvelopeBytes,
        relayFallback,
    ) ?: relayFallback()

    fun offerDirect(peerDeviceId: UUID, exactEnvelopeBytes: ByteArray): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        if (actor.offerDirect(exactEnvelopeBytes)) return true
        registry.failPeer(peerDeviceId, actor.pathFence, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
        return false
    }

    fun offerSignal(peerDeviceId: UUID, envelope: TransportSignalEnvelopeV1): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        if (actor.offerSignal(envelope)) return true
        registry.failPeer(peerDeviceId, actor.pathFence, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
        return false
    }

    internal fun hasPeer(peerDeviceId: UUID): Boolean = registry.actorFor(peerDeviceId) != null

    /** Exact promoted-path query used only to route the shared PATH_CLOSE signal. */
    internal fun ownsPromotedPath(
        peerDeviceId: UUID,
        connectionId: UUID,
        pathEpoch: Long,
    ): Boolean = registry.actorFor(peerDeviceId)?.pathFence?.let { fence ->
        fence.connectionId == connectionId && fence.pathEpoch == pathEpoch
    } == true

    /** Retires only the actor still owning this exact peer/connection/epoch tuple. */
    internal fun retirePromotedPath(
        peerDeviceId: UUID,
        connectionId: UUID,
        pathEpoch: Long,
        reason: ProductionDirectPeerRetirementReason,
    ): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        if (actor.pathFence.connectionId != connectionId || actor.pathFence.pathEpoch != pathEpoch) {
            return false
        }
        val retired = registry.failPeer(peerDeviceId, actor.pathFence, reason)
        if (retired) peerRetirementObserver(peerDeviceId, reason)
        return retired
    }

    fun updateAppliedCursor(peerDeviceId: UUID, sessionId: UUID, sequence: Long): Boolean =
        registry.actorFor(peerDeviceId)?.updateAppliedCursor(sessionId, sequence) == true

    fun deregisterSession(peerDeviceId: UUID, sessionId: UUID): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        if (actor.deregisterSession(sessionId)) return true
        if (registry.failPeer(
                peerDeviceId,
                actor.pathFence,
                ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
            )
        ) {
            peerRetirementObserver(peerDeviceId, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
        }
        return false
    }

    fun startCutover(peerDeviceId: UUID, proposal: ProductionDirectCutoverProposal): Boolean {
        val actor = registry.actorFor(peerDeviceId)
        if (actor == null) {
            proposal.close()
            return false
        }
        return actor.startCutover(proposal)
    }

    /** Retires only the currently fenced actor for [peerDeviceId]. */
    fun retirePeer(
        peerDeviceId: UUID,
        reason: ProductionDirectPeerRetirementReason,
    ): Boolean {
        val actor = registry.actorFor(peerDeviceId) ?: return false
        val retired = registry.failPeer(peerDeviceId, actor.pathFence, reason)
        if (retired) peerRetirementObserver(peerDeviceId, reason)
        return retired
    }

    /** Explicit server emergency-off. Ordinary capability generations never call this. */
    fun retireAllForServerRoutingDisabled() {
        registry.carriers.value.keys.toList().forEach { peerDeviceId ->
            retirePeer(peerDeviceId, ProductionDirectPeerRetirementReason.SERVER_ROUTING_DISABLED)
        }
    }

    /** Reconciles signalling without retiring an already committed Direct peer. */
    fun signalingCapabilitiesChanged(value: DirectTransportSignalingNegotiatedCapabilities) {
        registry.carriers.value.keys.toList().forEach { peerDeviceId ->
            val actor = registry.actorFor(peerDeviceId) ?: return@forEach
            if (!actor.signalingCapabilitiesChanged(value) &&
                registry.failPeer(
                    peerDeviceId,
                    actor.pathFence,
                    ProductionDirectPeerRetirementReason.SIGNALING_GENERATION_CHANGED,
                )
            ) {
                peerRetirementObserver(
                    peerDeviceId,
                    ProductionDirectPeerRetirementReason.SIGNALING_GENERATION_CHANGED,
                )
            }
        }
    }

    fun authenticationChanged() =
        registry.retireAll(ProductionDirectPeerRetirementReason.AUTHENTICATION_CHANGED)

    fun networkChanged() = registry.carriers.value.keys.forEach { peer ->
        registry.actorFor(peer)?.networkChanged()
    }

    override fun close() {
        synchronized(actorLifecycleLock) {
            if (!closed.compareAndSet(false, true)) return
            registry.close()
        }
        gatewayRegistration.close()
        scopeJob.cancel()
    }

    suspend fun closeAndJoin() {
        close()
        withContext(NonCancellable) {
            while (true) {
                val remaining = actors.keys.toList()
                if (remaining.isEmpty()) break
                remaining.forEach { it.closeAndJoin() }
            }
        }
    }
}

private fun ProductionDirectPathFence.toNative() = DirectNativePathV5(
    pathEpoch,
    connectionId.toNetworkBytes(),
    attemptId.toNetworkBytes(),
)

private fun UUID.toNetworkBytes(): ByteArray = java.nio.ByteBuffer.allocate(16)
    .putLong(mostSignificantBits)
    .putLong(leastSignificantBits)
    .array()

private fun ByteArray.toUuid(): UUID? = if (size == 16 && any { it != 0.toByte() }) {
    java.nio.ByteBuffer.wrap(this).let { UUID(it.long, it.long) }
} else null

private fun Envelope.sessionUuid(): UUID? = sessionId.toByteArray().toUuid()

private fun DirectNativeDirectionV5.toRealtime(): Direction = when (this) {
    DirectNativeDirectionV5.PC_TO_ANDROID -> Direction.PC_TO_ANDROID
    DirectNativeDirectionV5.ANDROID_TO_PC -> Direction.ANDROID_TO_PC
}

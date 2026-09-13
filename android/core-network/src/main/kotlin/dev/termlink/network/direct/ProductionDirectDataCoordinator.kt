package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DirectTransportCrypto
import dev.termlink.network.direct.nativebridge.DirectNativeCreateConfigV5
import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import dev.termlink.network.direct.nativebridge.DirectNativeHandleV4
import dev.termlink.network.direct.nativebridge.DirectNativePathV5
import dev.termlink.network.direct.nativebridge.DirectNativePromoteRequestV5
import dev.termlink.network.direct.nativebridge.DirectNativeProposalV5
import dev.termlink.network.direct.nativebridge.DirectNativeResultV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionV5
import dev.termlink.network.direct.nativebridge.DirectNativeSessionBarrierV5
import dev.termlink.network.direct.nativebridge.DirectNativeTransitionV5
import dev.termlink.network.direct.nativebridge.JniDirectNetworkCoreV5
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.transport.v1.DirectCutoverAcceptV1
import dev.termlink.protocol.transport.v1.DirectCutoverCommitV1
import dev.termlink.protocol.transport.v1.DirectFallbackV1
import dev.termlink.protocol.transport.v1.DirectPathResultV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.io.Closeable
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private const val PRODUCTION_DIRECT_CUTOVER_TIMEOUT_MILLIS = 10_000L
private val PRODUCTION_DIRECT_NIL_UUID = UUID(0, 0)
private val PRODUCTION_DIRECT_REASON = Regex("[A-Z0-9_]{1,64}")

/** One owned transport-signal socket. Exactly one coordinator may collect [incoming]. */
internal interface ProductionDirectOwnedSignalLane : ProductionDirectDataSignalLane, Closeable {
    val incoming: Flow<TransportSignalEnvelopeV1>
}

/** Publishes exact per-direction session authority after a native commit/fallback. */
internal fun interface ProductionDirectCutoverObserver {
    fun observe(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        direction: DirectNativeDirectionV5,
        activeSessionIds: Set<UUID>,
        committed: Boolean,
    )
}

internal class ClientProductionDirectOwnedSignalLane(
    private val client: DirectTransportSignalingClient,
) : ProductionDirectOwnedSignalLane {
    override val incoming = client.incoming
    override val negotiatedCapabilities = client.negotiatedCapabilities
    override fun send(envelope: TransportSignalEnvelopeV1) = client.send(envelope)
    override fun close() = client.close()
}

/**
 * A consumed V4 nomination may be promoted exactly once. The source owns and
 * closes the V4 wrapper on success, native rejection, and exception.
 */
internal fun interface ProductionDirectV4ReadySource : Closeable {
    fun promote(
        path: DirectNativePathV5,
        nomination: dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4,
    ): DirectNativeResultV5<ProductionDirectDataNative>

    override fun close() = Unit
}

internal class JniProductionDirectV4ReadySource(
    private val v4: DirectNativeHandleV4,
    private val core: JniDirectNetworkCoreV5 = JniDirectNetworkCoreV5(),
) : ProductionDirectV4ReadySource {
    private val consumed = AtomicBoolean(false)

    override fun promote(
        path: DirectNativePathV5,
        nomination: dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4,
    ): DirectNativeResultV5<ProductionDirectDataNative> {
        if (!consumed.compareAndSet(false, true)) return promotionFailure(path)
        var v5: dev.termlink.network.direct.nativebridge.DirectNativeHandleV5? = null
        return try {
            val created = core.create(DirectNativeCreateConfigV5(enabled = true, initialPath = path))
            v5 = (created as? DirectNativeResultV5.Success)?.value ?: return created.asFailure(path)
            when (val promoted = v5.promote(v4, DirectNativePromoteRequestV5(path, nomination))) {
                is DirectNativeResultV5.Success ->
                    DirectNativeResultV5.Success(JniProductionDirectDataNative(v5, path))
                is DirectNativeResultV5.Failure -> {
                    v5.close()
                    promoted
                }
            }
        } catch (_: Throwable) {
            runCatching { v5?.close() }
            promotionFailure(path)
        } finally {
            runCatching(v4::close)
        }
    }

    override fun close() {
        if (consumed.compareAndSet(false, true)) runCatching(v4::close)
    }

    private fun DirectNativeResultV5<*>.asFailure(path: DirectNativePathV5) =
        this as? DirectNativeResultV5.Failure ?: promotionFailure(path)
}

internal class ProductionDirectCutoverPeerRegistration(
    val peerDeviceId: UUID,
    val pathFence: ProductionDirectPathFence,
    val keyEpoch: Long,
    keyId: ByteArray,
    pairwiseKey: ByteArray,
    directPathBinding: ByteArray,
    val signalingGeneration: Int,
    val localSignalSequence: Long,
    val remoteSignalSequence: Long,
    val remoteSignalAcknowledgement: Long,
) : Closeable {
    val keyId = keyId.copyOf()
    val pairwiseKey = pairwiseKey.copyOf()
    val directPathBinding = directPathBinding.copyOf()

    init {
        require(peerDeviceId != PRODUCTION_DIRECT_NIL_UUID && keyEpoch > 0 && signalingGeneration > 0)
        require(this.keyId.size in 1..64)
        require(this.pairwiseKey.size == 32 && this.pairwiseKey.any { it != 0.toByte() })
        require(this.directPathBinding.size == 32 && this.directPathBinding.any { it != 0.toByte() })
        require(localSignalSequence >= 0 && remoteSignalSequence >= 0)
        require(remoteSignalAcknowledgement in 0..localSignalSequence)
    }

    override fun close() {
        keyId.fill(0)
        pairwiseKey.fill(0)
        directPathBinding.fill(0)
    }
}

internal class ProductionDirectV4ReadyHandoff(
    val registration: ProductionDirectCutoverPeerRegistration,
    val nomination: dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4,
    val gate: ProductionDirectDataGate,
    val source: ProductionDirectV4ReadySource,
) : Closeable {
    private val consumed = AtomicBoolean(false)
    private val secretsDestroyed = AtomicBoolean(false)

    internal fun claim(): Boolean = consumed.compareAndSet(false, true)

    internal fun destroyClaimedSecrets() {
        if (secretsDestroyed.compareAndSet(false, true)) nomination.destroy()
    }

    override fun close() {
        if (consumed.compareAndSet(false, true)) {
            registration.close()
            source.close()
        }
        destroyClaimedSecrets()
    }
}

/**
 * Single signalling reader and typed ABI-v5 cutover owner. It owns no Relay
 * reconnect policy and retires only the exact peer actor on any malformed,
 * stale, unauthenticated, or back-pressured signal.
 */
internal class ProductionDirectDataCoordinator(
    private val localDeviceId: UUID,
    private val signaling: ProductionDirectOwnedSignalLane,
    parentScope: CoroutineScope,
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val random: SecureRandom = SecureRandom(),
    private val cutoverObserver: ProductionDirectCutoverObserver =
        ProductionDirectCutoverObserver { _, _, _, _, _ -> Unit },
) : ProductionDirectDataSignalLane, ProductionDirectSignalProcessor, Closeable {
    override val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities> =
        signaling.negotiatedCapabilities

    private val closed = AtomicBoolean(false)
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val runtime = AtomicReference<ProductionDirectDataRuntime?>(null)
    private val reader = AtomicReference<Job?>(null)
    private val peers = ConcurrentHashMap<UUID, PeerState>()
    private val pendingLock = Any()
    private val pendingSignals = LinkedHashMap<UUID, ArrayDeque<TransportSignalEnvelopeV1>>()
    private var pendingSignalFrames = 0
    private var pendingSignalBytes = 0

    init {
        require(localDeviceId != PRODUCTION_DIRECT_NIL_UUID)
    }

    override fun send(envelope: TransportSignalEnvelopeV1): Boolean = signaling.send(envelope)

    /** Installs the only collector of the owned signalling client. */
    fun attachRuntime(value: ProductionDirectDataRuntime): Boolean {
        if (closed.get() || !runtime.compareAndSet(null, value)) return false
        val job = scope.launch {
            try {
                signaling.incoming.collect { envelope ->
                    val peerId = envelope.senderDeviceId.toByteArray().toUuidOrNull()
                    if (peerId == null || envelope.recipientDeviceId.toByteArray().toUuidOrNull() != localDeviceId) {
                        return@collect
                    }
                    val queued = synchronized(pendingLock) {
                        if (value.hasPeer(peerId)) false else queuePendingSignalLocked(peerId, envelope)
                    }
                    if (queued) return@collect
                    if (!value.offerSignal(peerId, envelope)) {
                        value.retirePeer(peerId, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Throwable) {
                // The signalling socket has its own retry owner. Do not touch
                // the Relay or already committed Direct paths here.
            }
        }
        if (!reader.compareAndSet(null, job)) {
            job.cancel()
            runtime.compareAndSet(value, null)
            return false
        }
        return true
    }

    fun registerPeer(value: ProductionDirectCutoverPeerRegistration): Boolean {
        if (closed.get()) {
            value.close()
            return false
        }
        val state = PeerState(value)
        val current = peers.putIfAbsent(value.peerDeviceId, state)
        if (current != null) {
            state.close()
            return false
        }
        return true
    }

    /** Consumes a ready V4 owner once, then installs the promoted V5 actor. */
    fun promoteV4AndInstall(value: ProductionDirectV4ReadyHandoff): Boolean {
        if (!value.claim()) return false
        val registration = value.registration
        if (closed.get()) {
            registration.close()
            value.source.close()
            return false
        }
        val native = try {
            when (val promoted = value.source.promote(registration.pathFence.toNativePath(), value.nomination)) {
                is DirectNativeResultV5.Success -> promoted.value
                is DirectNativeResultV5.Failure -> {
                    registration.close()
                    return false
                }
            }
        } catch (_: Throwable) {
            registration.close()
            return false
        } finally {
            value.source.close()
            value.destroyClaimedSecrets()
        }
        val target = runtime.get()
        if (target == null || !registerPeer(registration)) {
            runCatching(native::close)
            registration.close()
            return false
        }
        val installed = runCatching {
            target.installPromotedPeer(
                ProductionDirectDataPeerInstall(
                    registration.peerDeviceId,
                    registration.pathFence,
                    registration.keyEpoch,
                    native,
                    value.gate,
                ),
            )
        }.getOrElse {
            runCatching(native::close)
            false
        }
        if (!installed) {
            removePeer(registration.peerDeviceId, registration.pathFence)
        } else {
            drainPendingSignals(target, registration.peerDeviceId, registration.pathFence)
        }
        return installed
    }

    override fun process(
        actor: ProductionDirectDataPeerBoundary,
        envelope: TransportSignalEnvelopeV1,
    ): Boolean {
        val state = currentState(actor) ?: return false
        if (!reconcileSignalingCapabilities(actor, state, negotiatedCapabilities.value)) return false
        if (!validSignalFence(actor, state, envelope, inbound = true)) return false
        val payload = decryptCanonical(state, envelope) ?: return false
        val applied = when (envelope.signalType) {
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL ->
                receiveProposal(actor, state, payload)
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_ACCEPT ->
                receiveAccept(actor, state, payload)
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_COMMIT ->
                receiveCommit(actor, state, payload)
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK ->
                receiveFallback(actor, state, payload)
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE ->
                payload.hasPathClose() && validReason(payload.pathClose.reasonCode) && false
            else -> false
        }
        if (applied) {
            state.remoteSequence = envelope.signalSeq
            state.remoteAcknowledgement = envelope.signalAck
        }
        return applied
    }

    override fun startCutover(
        actor: ProductionDirectDataPeerBoundary,
        proposal: ProductionDirectCutoverProposal,
    ): Boolean {
        val state = currentState(actor) ?: return false
        if (!reconcileSignalingCapabilities(actor, state, negotiatedCapabilities.value)) return false
        if (!currentAdmission(state)) return false
        val canonical = ProductionDirectCutoverCodec.tryParseProposal(
            proposal.wireBytes,
            actor.pathFence.connectionId,
            actor.pathFence.pathEpoch,
            Direction.ANDROID_TO_PC,
        ) ?: return false
        if (!canonical.directPathBinding.contentEquals(state.directPathBinding)) return false
        val native = canonical.toNative(actor.native.path, clockMillis())
        return try {
            if (!state.canInstallNegotiatingBarrier(
                    DirectNativeDirectionV5.ANDROID_TO_PC,
                    native.barrierId,
                )
            ) return false
            if (actor.native.startProposal(native) !is DirectNativeResultV5.Success) return false
            state.replaceNegotiatingBarrier(Barrier(
                DirectNativeDirectionV5.ANDROID_TO_PC,
                native.barrierId.copyOf(),
                native.barrierSha256.copyOf(),
                canonical.sessionBarriers.mapTo(linkedSetOf()) { it.sessionId },
            ))
            val payload = TransportSignalPayloadV1.newBuilder()
                .setFormatVersion(FORMAT_VERSION)
                .setCutoverProposal(
                    dev.termlink.protocol.transport.v1.DirectCutoverProposalV1.parseFrom(canonical.wireBytes),
                )
                .build()
            sendPayload(
                actor,
                state,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL,
                payload,
            )
        } finally {
            native.close()
        }
    }

    override fun processNativeAction(
        actor: ProductionDirectDataPeerBoundary,
        action: DirectNativeRouteActionV5,
    ): Boolean {
        val state = currentState(actor) ?: return false
        if (action.kind == DirectNativeRouteActionKindV5.FALLBACK &&
            action.barrierId.all { it == 0.toByte() }
        ) {
            // DirectFallbackV1 has no direction field, so an empty barrier ID
            // can never be put on the wire safely. Represent a native
            // direction-wide fallback with one deterministic committed ID;
            // the receiver expands any committed ID back to the whole
            // direction. A missing representative is a protocol fault and
            // retires only this peer actor.
            val representative = state.committedRepresentative(action.direction)
                ?: return state.wasDirectionFallbackApplied(action.direction)
            state.rememberPendingFallback(action.direction, representative.id, action)
            val removed = state.applyDirectionFallback(action.direction)
            removed.forEach(Barrier::close)
            observeCutover(actor, state, action.direction, committed = false)
            flushPendingFallbacks(actor, state)
            return true
        }
        val fallbackTarget = if (action.kind == DirectNativeRouteActionKindV5.FALLBACK) {
            val targets = state.fallbackTargets(action.direction, action.barrierId)
            if (targets.isEmpty() && state.wasFallbackApplied(action.barrierId)) return true
            targets.singleOrNull() ?: return false
        } else {
            null
        }
        if (fallbackTarget?.committedDirection == true) {
            state.rememberPendingFallback(action.direction, fallbackTarget.barrier.id, action)
            val removed = state.applyFallback(fallbackTarget)
            removed.forEach(Barrier::close)
            if (removed.isNotEmpty()) {
                observeCutover(actor, state, action.direction, committed = false)
            }
            flushPendingFallbacks(actor, state)
            return true
        }
        val type: TransportSignalTypeV1
        val payload: TransportSignalPayloadV1
        when (action.kind) {
            DirectNativeRouteActionKindV5.ACCEPT -> {
                type = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_ACCEPT
                val accepted = action.accepted
                val value = DirectCutoverAcceptV1.newBuilder()
                    .setBarrierId(ByteString.copyFrom(action.barrierId))
                    .setBarrierSha256(ByteString.copyFrom(action.barrierSha256))
                    .setResult(if (accepted) DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED else DirectPathResultV1.DIRECT_PATH_RESULT_V1_REJECTED)
                    .setReasonCode(if (accepted) "" else nativeReason(action.reason))
                    .build()
                payload = TransportSignalPayloadV1.newBuilder().setFormatVersion(FORMAT_VERSION)
                    .setCutoverAccept(value).build()
            }
            DirectNativeRouteActionKindV5.COMMIT -> {
                type = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_COMMIT
                payload = TransportSignalPayloadV1.newBuilder().setFormatVersion(FORMAT_VERSION)
                    .setCutoverCommit(
                        DirectCutoverCommitV1.newBuilder()
                            .setBarrierId(ByteString.copyFrom(action.barrierId))
                            .setBarrierSha256(ByteString.copyFrom(action.barrierSha256)),
                    ).build()
            }
            DirectNativeRouteActionKindV5.FALLBACK -> {
                type = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK
                payload = TransportSignalPayloadV1.newBuilder().setFormatVersion(FORMAT_VERSION)
                    .setFallback(
                        DirectFallbackV1.newBuilder()
                            .setBarrierId(ByteString.copyFrom(action.barrierId))
                            .setReasonCode(nativeReason(action.reason))
                            .setLastReceivedDirectFrameSeq(action.directFrameSequence),
                    ).build()
            }
            else -> return false
        }
        if (!sendPayload(actor, state, type, payload)) return false
        if (action.kind == DirectNativeRouteActionKindV5.COMMIT && action.isEcho) {
            val transition = action.toTransition(actor.native.path, clockMillis())
            if (actor.native.commitEchoSent(transition) !is DirectNativeResultV5.Success) return false
            if (state.commitBarrier(action.direction)) {
                observeCutover(actor, state, action.direction, committed = true)
            }
        } else if (action.kind == DirectNativeRouteActionKindV5.FALLBACK) {
            val removed = state.applyFallback(requireNotNull(fallbackTarget))
            removed.forEach(Barrier::close)
            if (removed.isNotEmpty()) {
                observeCutover(actor, state, action.direction, committed = false)
            }
        }
        return true
    }

    /** Best-effort convergence notice. Local Relay recovery never waits for this socket. */
    private fun flushPendingFallbacks(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
    ) {
        if (!currentAdmission(state)) return
        for (notice in state.pendingFallbacks.values.toList()) {
            val payload = TransportSignalPayloadV1.newBuilder().setFormatVersion(FORMAT_VERSION)
                .setFallback(
                    DirectFallbackV1.newBuilder()
                        .setBarrierId(ByteString.copyFrom(notice.barrierId))
                        .setReasonCode(notice.reasonCode)
                        .setLastReceivedDirectFrameSeq(notice.lastReceivedDirectFrameSequence),
                ).build()
            if (!sendPayload(
                    actor,
                    state,
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK,
                    payload,
                )
            ) return
            state.removePendingFallback(notice.direction)?.close()
        }
    }

    override fun tick(actor: ProductionDirectDataPeerBoundary, nowMillis: Long): Boolean {
        val state = currentState(actor) ?: return false
        for ((direction, barrier) in state.barriers.toMap()) {
            val transition = DirectNativeTransitionV5(
                actor.native.path,
                direction,
                barrier.id,
                barrier.sha256,
                accepted = false,
                isEcho = false,
                nowMillis = nowMillis,
                timeoutMillis = 0,
            )
            if (actor.native.tick(transition) !is DirectNativeResultV5.Success) return false
        }
        return true
    }

    override fun deregisterSession(
        actor: ProductionDirectDataPeerBoundary,
        sessionId: UUID,
    ): Boolean {
        if (sessionId == PRODUCTION_DIRECT_NIL_UUID) return false
        val state = currentState(actor) ?: return false
        state.deregisterSession(sessionId)
        for (direction in listOf(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            DirectNativeDirectionV5.ANDROID_TO_PC,
        )) {
            observeCutover(actor, state, direction, committed = false)
        }
        return true
    }

    override fun signalingCapabilitiesChanged(
        actor: ProductionDirectDataPeerBoundary,
        value: DirectTransportSignalingNegotiatedCapabilities,
    ): Boolean {
        val state = currentState(actor) ?: return false
        return reconcileSignalingCapabilities(actor, state, value)
    }

    override fun retired(actor: ProductionDirectDataPeerBoundary) {
        removePeer(actor.peerDeviceId, actor.pathFence)
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        reader.getAndSet(null)?.cancel()
        runtime.set(null)
        peers.values.forEach(PeerState::close)
        peers.clear()
        synchronized(pendingLock) {
            pendingSignals.clear()
            pendingSignalFrames = 0
            pendingSignalBytes = 0
        }
        signaling.close()
        scopeJob.cancel()
        scope.cancel()
    }

    suspend fun closeAndJoin() {
        val ownedReader = reader.get()
        close()
        withContext(NonCancellable) {
            ownedReader?.cancelAndJoin()
            scopeJob.children.toList().forEach { it.cancelAndJoin() }
        }
    }

    private fun receiveProposal(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        if (!currentAdmission(state) || !payload.hasCutoverProposal()) return false
        val wire = payload.cutoverProposal.toByteArray()
        val proposal = ProductionDirectCutoverCodec.tryParseProposal(
            wire,
            actor.pathFence.connectionId,
            actor.pathFence.pathEpoch,
            Direction.PC_TO_ANDROID,
        ) ?: return false
        if (!proposal.directPathBinding.contentEquals(state.directPathBinding)) return false
        val native = proposal.toNative(actor.native.path, clockMillis())
        return try {
            if (!state.canInstallNegotiatingBarrier(
                    DirectNativeDirectionV5.PC_TO_ANDROID,
                    native.barrierId,
                )
            ) return false
            if (actor.native.receiveProposal(native) !is DirectNativeResultV5.Success) return false
            state.replaceNegotiatingBarrier(Barrier(
                DirectNativeDirectionV5.PC_TO_ANDROID,
                native.barrierId.copyOf(), native.barrierSha256.copyOf(),
                proposal.sessionBarriers.mapTo(linkedSetOf()) { it.sessionId },
            ))
            true
        } finally {
            native.close()
        }
    }

    private fun receiveAccept(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        if (!currentAdmission(state) || !payload.hasCutoverAccept()) return false
        val value = payload.cutoverAccept
        val accepted = value.result == DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED
        if (accepted != value.reasonCode.isEmpty() ||
            !accepted && value.result != DirectPathResultV1.DIRECT_PATH_RESULT_V1_REJECTED ||
            !accepted && !validReason(value.reasonCode)
        ) return false
        val barrier = state.barriers[DirectNativeDirectionV5.ANDROID_TO_PC] ?: return false
        if (!value.barrierId.toByteArray().contentEquals(barrier.id) ||
            !value.barrierSha256.toByteArray().contentEquals(barrier.sha256)
        ) return false
        val applied = actor.native.receiveAccept(
            DirectNativeTransitionV5(
                actor.native.path, DirectNativeDirectionV5.ANDROID_TO_PC,
                barrier.id, barrier.sha256, accepted, false, clockMillis(), 0,
            ),
        ) is DirectNativeResultV5.Success
        if (applied && !accepted) {
            state.removeNegotiatingBarrier(
                DirectNativeDirectionV5.ANDROID_TO_PC,
                barrier.id,
            )?.close()
            observeCutover(
                actor,
                state,
                DirectNativeDirectionV5.ANDROID_TO_PC,
                committed = false,
            )
        }
        return applied
    }

    private fun receiveCommit(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        if (!currentAdmission(state) || !payload.hasCutoverCommit()) return false
        val value = payload.cutoverCommit
        val barrierId = value.barrierId.toByteArray()
        val barrierSha256 = value.barrierSha256.toByteArray()
        val matches = state.barriers.entries.filter { (_, barrier) ->
            barrierId.contentEquals(barrier.id) && barrierSha256.contentEquals(barrier.sha256)
        }
        if (matches.size != 1) return false
        val (direction, barrier) = matches.single()
        val isEcho = direction == DirectNativeDirectionV5.ANDROID_TO_PC
        val applied = actor.native.receiveCommit(
            DirectNativeTransitionV5(
                actor.native.path, direction,
                barrier.id, barrier.sha256, false, isEcho, clockMillis(), 0,
            ),
        ) is DirectNativeResultV5.Success
        if (applied && isEcho && state.commitBarrier(direction)) {
            observeCutover(actor, state, direction, committed = true)
        }
        return applied
    }

    private fun receiveFallback(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        if (!payload.hasFallback()) return false
        val value = payload.fallback
        if (!validReason(value.reasonCode) ||
            value.lastReceivedDirectFrameSeq < 0L ||
            value.lastReceivedDirectFrameSeq == 1L || value.lastReceivedDirectFrameSeq == 2L
        ) return false
        val barrierId = value.barrierId.toByteArray()
        if (barrierId.size != 16 || barrierId.all { it == 0.toByte() }) return false
        val targets = state.fallbackTargets(barrierId)
        if (targets.isEmpty() && state.wasFallbackApplied(barrierId)) return true
        val target = targets.singleOrNull() ?: return false
        val direction = target.barrier.direction
        val barrier = target.barrier
        val applied = actor.native.receiveFallback(
            DirectNativeTransitionV5(
                actor.native.path, direction,
                barrier.id, barrier.sha256, false, false, clockMillis(), 0,
            ),
        ) is DirectNativeResultV5.Success
        if (applied) {
            val removed = state.applyFallback(target)
            removed.forEach(Barrier::close)
            if (removed.isNotEmpty()) observeCutover(actor, state, direction, committed = false)
        }
        return applied
    }

    private fun observeCutover(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        direction: DirectNativeDirectionV5,
        committed: Boolean,
    ) {
        runCatching {
            cutoverObserver.observe(
                actor.peerDeviceId,
                actor.pathFence,
                direction,
                state.committedSessionIds(direction),
                committed,
            )
        }
    }

    private fun reconcileSignalingCapabilities(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        value: DirectTransportSignalingNegotiatedCapabilities,
    ): Boolean {
        val admitted = value.generation > 0 &&
            DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in value.capabilities &&
            DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in value.capabilities
        if (value.generation != state.signalingGeneration || !admitted) {
            val negotiating = state.barriers.values.toList()
            for (barrier in negotiating) {
                if (actor.native.receiveFallback(
                        DirectNativeTransitionV5(
                            actor.native.path,
                            barrier.direction,
                            barrier.id,
                            barrier.sha256,
                            accepted = false,
                            isEcho = false,
                            nowMillis = clockMillis(),
                            timeoutMillis = 0,
                        ),
                    ) !is DirectNativeResultV5.Success
                ) return false
                state.applyFallback(FallbackTarget(barrier, committedDirection = false))
                    .forEach(Barrier::close)
                observeCutover(actor, state, barrier.direction, committed = false)
            }
        }
        if (admitted) {
            state.signalingGeneration = value.generation
            flushPendingFallbacks(actor, state)
        }
        return true
    }

    private fun decryptCanonical(
        state: PeerState,
        envelope: TransportSignalEnvelopeV1,
    ): TransportSignalPayloadV1? {
        val plaintext = runCatching {
            DirectTransportCrypto.decryptSignal(state.pairwiseKey, envelope)
        }.getOrNull() ?: return null
        return try {
            val payload = runCatching { TransportSignalPayloadV1.parseFrom(plaintext) }.getOrNull()
                ?: return null
            val canonical = canonicalPayload(payload, envelope.signalType) ?: return null
            val wire = canonical.toByteArray()
            try {
                canonical.takeIf { plaintext.contentEquals(wire) }
            } finally {
                wire.fill(0)
            }
        } finally {
            plaintext.fill(0)
        }
    }

    private fun canonicalPayload(
        value: TransportSignalPayloadV1,
        type: TransportSignalTypeV1,
    ): TransportSignalPayloadV1? {
        if (value.formatVersion != FORMAT_VERSION) return null
        val builder = TransportSignalPayloadV1.newBuilder().setFormatVersion(FORMAT_VERSION)
        when (type) {
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL ->
                if (value.hasCutoverProposal()) builder.cutoverProposal = value.cutoverProposal else return null
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_ACCEPT ->
                if (value.hasCutoverAccept()) builder.cutoverAccept = value.cutoverAccept else return null
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_COMMIT ->
                if (value.hasCutoverCommit()) builder.cutoverCommit = value.cutoverCommit else return null
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK ->
                if (value.hasFallback()) builder.fallback = value.fallback else return null
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE ->
                if (value.hasPathClose()) builder.pathClose = value.pathClose else return null
            else -> return null
        }
        return builder.build().takeIf { it.signalCase == value.signalCase }
    }

    private fun sendPayload(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        type: TransportSignalTypeV1,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        // FALLBACK is a data-plane control frame too.  Do not hand it to the
        // shared signalling client after its generation/capability lease has
        // gone stale: the client correctly rejects that frame and retires the
        // whole signalling lane, which would be broader than this peer-local
        // cutover failure.
        if (type.number in 6..9 && !currentAdmission(state)) return false
        val plaintext = payload.toByteArray()
        return try {
            val next = state.localSequence.takeIf { it < Long.MAX_VALUE }?.plus(1) ?: return false
            val metadata = TransportSignalEnvelopeV1.newBuilder()
                .setProtocolVersion(FORMAT_VERSION)
                .setSignalType(type)
                .setConnectionId(ByteString.copyFrom(actor.native.path.connectionId))
                .setSenderDeviceId(ByteString.copyFrom(localDeviceId.toNetworkBytes()))
                .setRecipientDeviceId(ByteString.copyFrom(actor.peerDeviceId.toNetworkBytes()))
                .setPathEpoch(actor.pathFence.pathEpoch)
                .setSignalSeq(next)
                .setSignalAck(state.remoteSequence)
                .setSentAtMs(clockMillis().coerceAtLeast(1))
                .setKeyId(ByteString.copyFrom(state.keyId))
                .build()
            val sealed = DirectTransportCrypto.encryptSignal(state.pairwiseKey, metadata, plaintext, random)
            if (!signaling.send(sealed)) return false
            state.localSequence = next
            true
        } finally {
            plaintext.fill(0)
        }
    }

    private fun validSignalFence(
        actor: ProductionDirectDataPeerBoundary,
        state: PeerState,
        envelope: TransportSignalEnvelopeV1,
        inbound: Boolean,
    ): Boolean {
        if (actor.keyEpoch != state.keyEpoch || actor.pathFence != state.pathFence ||
            !envelope.connectionId.toByteArray().contentEquals(actor.native.path.connectionId) ||
            envelope.pathEpoch != actor.pathFence.pathEpoch ||
            !envelope.keyId.toByteArray().contentEquals(state.keyId)
        ) return false
        val sender = envelope.senderDeviceId.toByteArray().toUuidOrNull()
        val recipient = envelope.recipientDeviceId.toByteArray().toUuidOrNull()
        if (inbound && (sender != actor.peerDeviceId || recipient != localDeviceId)) return false
        if (envelope.signalSeq != state.remoteSequence + 1 ||
            envelope.signalAck < state.remoteAcknowledgement ||
            envelope.signalAck > state.localSequence
        ) return false
        return envelope.signalType.number in 6..10 &&
            (envelope.signalType.number !in 6..9 || currentAdmission(state))
    }

    private fun currentAdmission(state: PeerState): Boolean {
        val current = negotiatedCapabilities.value
        return current.generation == state.signalingGeneration && current.generation > 0 &&
            DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in current.capabilities &&
            DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in current.capabilities
    }

    private fun currentState(actor: ProductionDirectDataPeerBoundary): PeerState? =
        peers[actor.peerDeviceId]?.takeIf {
            it.pathFence == actor.pathFence && it.keyEpoch == actor.keyEpoch
        }

    private fun removePeer(peerDeviceId: UUID, fence: ProductionDirectPathFence) {
        val state = peers[peerDeviceId] ?: return
        if (state.pathFence == fence && peers.remove(peerDeviceId, state)) state.close()
    }

    private fun queuePendingSignalLocked(
        peerDeviceId: UUID,
        envelope: TransportSignalEnvelopeV1,
    ): Boolean {
        val bytes = envelope.serializedSize
        if (bytes !in 1..DirectTransportSignalingClient.MAX_SIGNAL_FRAME_BYTES ||
            pendingSignalFrames >= MAX_PENDING_SIGNAL_FRAMES ||
            pendingSignalBytes > MAX_PENDING_SIGNAL_BYTES - bytes
        ) return false
        pendingSignals.getOrPut(peerDeviceId, ::ArrayDeque).addLast(envelope)
        pendingSignalFrames++
        pendingSignalBytes += bytes
        return true
    }

    private fun drainPendingSignals(
        target: ProductionDirectDataRuntime,
        peerDeviceId: UUID,
        fence: ProductionDirectPathFence,
    ) {
        val queued = synchronized(pendingLock) {
            pendingSignals.remove(peerDeviceId).orEmpty().toList().also { values ->
                pendingSignalFrames -= values.size
                pendingSignalBytes -= values.sumOf { it.serializedSize }
            }
        }
        for (envelope in queued) {
            val matchingFence = envelope.pathEpoch == fence.pathEpoch &&
                envelope.connectionId.toByteArray().toUuidOrNull() == fence.connectionId
            if (!matchingFence) continue
            if (!target.offerSignal(peerDeviceId, envelope)) {
                target.retirePeer(peerDeviceId, ProductionDirectPeerRetirementReason.DIRECT_FAILURE)
                return
            }
        }
    }

    private class PeerState(value: ProductionDirectCutoverPeerRegistration) : Closeable {
        val pathFence = value.pathFence
        val keyEpoch = value.keyEpoch
        val keyId = value.keyId.copyOf()
        val pairwiseKey = value.pairwiseKey.copyOf()
        val directPathBinding = value.directPathBinding.copyOf()
        var signalingGeneration = value.signalingGeneration
        var localSequence = value.localSignalSequence
        var remoteSequence = value.remoteSignalSequence
        var remoteAcknowledgement = value.remoteSignalAcknowledgement
        val barriers = LinkedHashMap<DirectNativeDirectionV5, Barrier>()
        val committedBarriers = ArrayList<Barrier>()
        val appliedFallbackBarrierIds = ArrayDeque<ByteArray>()
        val appliedDirectionFallbacks = linkedSetOf<DirectNativeDirectionV5>()
        val pendingFallbacks = LinkedHashMap<DirectNativeDirectionV5, PendingFallback>()
        val deregisteredSessionIds = linkedSetOf<UUID>()

        init {
            value.close()
        }

        override fun close() {
            keyId.fill(0)
            pairwiseKey.fill(0)
            directPathBinding.fill(0)
            barriers.values.forEach(Barrier::close)
            barriers.clear()
            committedBarriers.forEach(Barrier::close)
            committedBarriers.clear()
            appliedFallbackBarrierIds.forEach { it.fill(0) }
            appliedFallbackBarrierIds.clear()
            appliedDirectionFallbacks.clear()
            pendingFallbacks.values.forEach(PendingFallback::close)
            pendingFallbacks.clear()
            deregisteredSessionIds.clear()
        }

        fun commitBarrier(direction: DirectNativeDirectionV5): Boolean {
            val committed = barriers.remove(direction) ?: return false
            committedBarriers += committed
            return true
        }

        fun replaceNegotiatingBarrier(barrier: Barrier) {
            appliedDirectionFallbacks.remove(barrier.direction)
            barriers.put(barrier.direction, barrier)?.close()
        }

        fun deregisterSession(sessionId: UUID) {
            deregisteredSessionIds += sessionId
        }

        fun canInstallNegotiatingBarrier(
            direction: DirectNativeDirectionV5,
            barrierId: ByteArray,
        ): Boolean = barriers.values.none {
            it.direction != direction && barrierId.contentEquals(it.id)
        } && committedBarriers.none {
            barrierId.contentEquals(it.id)
        } && appliedFallbackBarrierIds.none {
            barrierId.contentEquals(it)
        }

        fun removeNegotiatingBarrier(
            direction: DirectNativeDirectionV5,
            barrierId: ByteArray,
        ): Barrier? = barriers[direction]
            ?.takeIf { barrierId.contentEquals(it.id) }
            ?.also { barriers.remove(direction) }

        fun removeBarrier(direction: DirectNativeDirectionV5): List<Barrier> =
            removeBarrier(direction, ByteArray(16))

        fun removeBarrier(direction: DirectNativeDirectionV5, barrierId: ByteArray): List<Barrier> {
            if (barrierId.all { it == 0.toByte() }) {
                return buildList {
                    barriers.remove(direction)?.let(::add)
                    val committed = committedBarriers.filter { it.direction == direction }
                    addAll(committed)
                    committedBarriers.removeAll(committed.toSet())
                }
            }
            val negotiating = barriers[direction]
                ?.takeIf { barrierId.contentEquals(it.id) }
                ?.also { barriers.remove(direction) }
            if (negotiating != null) return listOf(negotiating)
            val index = committedBarriers.indexOfFirst {
                it.direction == direction && barrierId.contentEquals(it.id)
            }
            return if (index >= 0) listOf(committedBarriers.removeAt(index)) else emptyList()
        }

        fun committedRepresentative(direction: DirectNativeDirectionV5): Barrier? =
            committedBarriers.asSequence()
                .filter { it.direction == direction }
                .minWithOrNull { left, right -> compareUnsigned(left.id, right.id) }

        fun fallbackTargets(
            direction: DirectNativeDirectionV5,
            barrierId: ByteArray,
        ): List<FallbackTarget> = buildList {
            barriers[direction]
                ?.takeIf { barrierId.contentEquals(it.id) }
                ?.let { add(FallbackTarget(it, committedDirection = false)) }
            committedBarriers.asSequence()
                .filter { it.direction == direction && barrierId.contentEquals(it.id) }
                .mapTo(this) { FallbackTarget(it, committedDirection = true) }
        }

        fun fallbackTargets(barrierId: ByteArray): List<FallbackTarget> = buildList {
            barriers.values.asSequence()
                .filter { barrierId.contentEquals(it.id) }
                .mapTo(this) { FallbackTarget(it, committedDirection = false) }
            committedBarriers.asSequence()
                .filter { barrierId.contentEquals(it.id) }
                .mapTo(this) { FallbackTarget(it, committedDirection = true) }
        }

        fun applyFallback(target: FallbackTarget): List<Barrier> =
            if (target.committedDirection) {
                applyDirectionFallback(target.barrier.direction)
            } else {
                removeBarrier(target.barrier.direction, target.barrier.id).also(::rememberFallbacks)
            }

        fun applyDirectionFallback(direction: DirectNativeDirectionV5): List<Barrier> =
            removeBarrier(direction).also { removed ->
                rememberFallbacks(removed)
                appliedDirectionFallbacks += direction
            }

        fun wasFallbackApplied(barrierId: ByteArray): Boolean =
            appliedFallbackBarrierIds.any { barrierId.contentEquals(it) }

        fun wasDirectionFallbackApplied(direction: DirectNativeDirectionV5): Boolean =
            direction in appliedDirectionFallbacks

        fun rememberPendingFallback(
            direction: DirectNativeDirectionV5,
            barrierId: ByteArray,
            action: DirectNativeRouteActionV5,
        ) {
            val pending = PendingFallback(
                direction,
                barrierId.copyOf(),
                nativeReason(action.reason),
                action.directFrameSequence,
            )
            pendingFallbacks.put(direction, pending)?.close()
        }

        fun removePendingFallback(direction: DirectNativeDirectionV5): PendingFallback? =
            pendingFallbacks.remove(direction)

        private fun rememberFallbacks(values: List<Barrier>) {
            values.forEach { barrier ->
                if (appliedFallbackBarrierIds.none { barrier.id.contentEquals(it) }) {
                    if (appliedFallbackBarrierIds.size == MAX_APPLIED_FALLBACK_BARRIERS) {
                        appliedFallbackBarrierIds.removeFirst().fill(0)
                    }
                    appliedFallbackBarrierIds.addLast(barrier.id.copyOf())
                }
            }
        }

        fun committedSessionIds(direction: DirectNativeDirectionV5): Set<UUID> =
            committedBarriers.asSequence()
                .filter { it.direction == direction }
                .flatMap { it.sessionIds.asSequence() }
                .filterNot(deregisteredSessionIds::contains)
                .toSet()

    }

    private data class FallbackTarget(
        val barrier: Barrier,
        val committedDirection: Boolean,
    )

    private class Barrier(
        val direction: DirectNativeDirectionV5,
        val id: ByteArray,
        val sha256: ByteArray,
        val sessionIds: Set<UUID>,
    ) : Closeable {
        override fun close() {
            id.fill(0)
            sha256.fill(0)
        }
    }

    private class PendingFallback(
        val direction: DirectNativeDirectionV5,
        val barrierId: ByteArray,
        val reasonCode: String,
        val lastReceivedDirectFrameSequence: Long,
    ) : Closeable {
        override fun close() = barrierId.fill(0)
    }

    private companion object {
        fun compareUnsigned(left: ByteArray, right: ByteArray): Int {
            val common = minOf(left.size, right.size)
            for (index in 0 until common) {
                val comparison = (left[index].toInt() and 0xff)
                    .compareTo(right[index].toInt() and 0xff)
                if (comparison != 0) return comparison
            }
            return left.size.compareTo(right.size)
        }

        const val FORMAT_VERSION = 1
        const val MAX_APPLIED_FALLBACK_BARRIERS = 64
        const val MAX_PENDING_SIGNAL_FRAMES = 32
        const val MAX_PENDING_SIGNAL_BYTES = 512 * 1_024
    }
}

private fun ProductionDirectCutoverProposal.toNative(
    path: DirectNativePathV5,
    nowMillis: Long,
) = DirectNativeProposalV5(
    path,
    if (direction == Direction.PC_TO_ANDROID) DirectNativeDirectionV5.PC_TO_ANDROID
    else DirectNativeDirectionV5.ANDROID_TO_PC,
    barrierId.toNetworkBytes(),
    barrierSha256,
    directPathBinding,
    duplicateGraceMilliseconds,
    nowMillis.coerceAtLeast(0),
    PRODUCTION_DIRECT_CUTOVER_TIMEOUT_MILLIS,
    sessionBarriers.map {
        DirectNativeSessionBarrierV5(it.sessionId.toNetworkBytes(), it.firstDirectSequence)
    },
)

private fun DirectNativeRouteActionV5.toTransition(path: DirectNativePathV5, nowMillis: Long) =
    DirectNativeTransitionV5(
        path, direction, barrierId, barrierSha256, accepted, isEcho,
        nowMillis.coerceAtLeast(0), 0,
    )

private fun ProductionDirectPathFence.toNativePath() = DirectNativePathV5(
    pathEpoch, connectionId.toNetworkBytes(), attemptId.toNetworkBytes(),
)

private fun UUID.toNetworkBytes(): ByteArray = ByteBuffer.allocate(16)
    .putLong(mostSignificantBits).putLong(leastSignificantBits).array()

private fun ByteArray.toUuidOrNull(): UUID? = if (size == 16 && any { it != 0.toByte() }) {
    ByteBuffer.wrap(this).let { UUID(it.long, it.long) }
} else null

private fun validReason(value: String): Boolean =
    PRODUCTION_DIRECT_REASON.matches(value)

private fun nativeReason(reason: Int): String = "DIRECT_NATIVE_REASON_${reason.coerceIn(0, 9)}"

private fun promotionFailure(path: DirectNativePathV5) = DirectNativeResultV5.Failure(
    reason = dev.termlink.network.direct.nativebridge.DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
    pathEpoch = path.epoch,
    attemptId = path.attemptId.copyOf(),
)

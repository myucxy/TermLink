package dev.termlink.network.direct

import dev.termlink.crypto.DeviceIdentity
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.Closeable
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.OkHttpClient

internal enum class ProductionDirectRoutingPermission {
    UNKNOWN,
    FORBIDDEN,
    PERMITTED,
}

internal sealed interface ProductionDirectSignalDispatcherStatus {
    data object IDLE : ProductionDirectSignalDispatcherStatus
    data object RUNNING : ProductionDirectSignalDispatcherStatus
    data class FAULT(val code: DirectSignalingFaultCode) : ProductionDirectSignalDispatcherStatus
    data object CLOSED : ProductionDirectSignalDispatcherStatus
}

internal enum class ProductionDirectSignalLaneKind {
    DISCOVERY,
    DATA,
}

/** Latest bounded-lane rejection. The shared signalling client remains owned and open. */
internal data class ProductionDirectSignalLaneFault(
    val lane: ProductionDirectSignalLaneKind,
    val code: DirectSignalingFaultCode,
    val senderDeviceId: UUID?,
    val connectionId: UUID?,
    val pathEpoch: Long,
    val droppedFrameBytes: Long,
    val totalDroppedFrames: Long,
    val totalDroppedBytes: Long,
)

internal data class ProductionDirectSignalDispatcherDiagnostics(
    val readerActive: Boolean,
    val discoveryLeaseActive: Boolean,
    val dataLeaseActive: Boolean,
    val discoveryQueuedFrames: Int,
    val discoveryQueuedBytes: Long,
    val dataQueuedFrames: Int,
    val dataQueuedBytes: Long,
    val discoveryDroppedFrames: Long,
    val discoveryDroppedBytes: Long,
    val dataDroppedFrames: Long,
    val dataDroppedBytes: Long,
    val closed: Boolean,
)

/**
 * The sole reader of one production Direct signalling client.
 *
 * Discovery and data-cutover consumers receive independent bounded leases. Closing a lease only
 * releases that consumer; only this dispatcher owns and closes the shared client. No Relay owner
 * is reachable from this object graph, so a signalling fault remains Direct-local.
 */
internal class ProductionDirectSignalDispatcher internal constructor(
    private val client: ProductionDirectSignalClientBoundary,
    parentScope: CoroutineScope,
    private val maximumFramesPerLane: Int = MAXIMUM_FRAMES_PER_LANE,
    private val maximumBytesPerLane: Long = MAXIMUM_BYTES_PER_LANE,
) : Closeable {
    private val closed = AtomicBoolean(false)
    private val lifecycleLock = Any()
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val reader = AtomicReference<Job?>(null)
    private val stateObserver = AtomicReference<Job?>(null)
    private val discovery = AtomicReference<DiscoveryLease?>(null)
    private val data = AtomicReference<DataLease?>(null)
    private val promotedPathOwnerResolver =
        AtomicReference<((TransportSignalEnvelopeV1) -> Boolean)?>(null)
    private val laneFaultObserver =
        AtomicReference<((ProductionDirectSignalLaneFault) -> Unit)?>(null)
    private val discoveryDroppedFrames = AtomicLong(0)
    private val discoveryDroppedBytes = AtomicLong(0)
    private val dataDroppedFrames = AtomicLong(0)
    private val dataDroppedBytes = AtomicLong(0)
    private val configLock = Mutex()
    private var clientStarted = false
    private var clientTerminal = false
    private val mutableStatus = MutableStateFlow<ProductionDirectSignalDispatcherStatus>(
        ProductionDirectSignalDispatcherStatus.IDLE,
    )
    private val mutableRoutingPermission = MutableStateFlow(ProductionDirectRoutingPermission.UNKNOWN)
    private val mutableLatestLaneFault = MutableStateFlow<ProductionDirectSignalLaneFault?>(null)

    val status: StateFlow<ProductionDirectSignalDispatcherStatus> = mutableStatus.asStateFlow()
    val routingPermission: StateFlow<ProductionDirectRoutingPermission> =
        mutableRoutingPermission.asStateFlow()
    val latestLaneFault: StateFlow<ProductionDirectSignalLaneFault?> =
        mutableLatestLaneFault.asStateFlow()

    init {
        require(maximumFramesPerLane in 1..MAXIMUM_FRAMES_PER_LANE)
        require(maximumBytesPerLane in 1..MAXIMUM_BYTES_PER_LANE)
    }

    /** Idempotently starts exactly one client socket and one incoming collector. */
    fun start(): Boolean = synchronized(lifecycleLock) {
        if (closed.get()) return@synchronized false
        if (clientTerminal) return@synchronized false
        if (clientStarted) return@synchronized true
        val started = runCatching(client::start).getOrDefault(false)
        if (!started || closed.get()) return@synchronized false
        val readerCandidate = scope.launch(start = CoroutineStart.LAZY) {
            try {
                client.incoming.collect(::dispatch)
                if (!closed.get()) fail(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Throwable) {
                fail(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED)
            }
        }
        val observerCandidate = scope.launch(start = CoroutineStart.LAZY) {
            client.state.collect(::observeClientState)
        }
        reader.set(readerCandidate)
        stateObserver.set(observerCandidate)
        clientStarted = true
        mutableStatus.value = ProductionDirectSignalDispatcherStatus.RUNNING
        observerCandidate.start()
        readerCandidate.start()
        true
    }

    /** At most one discovery attempt may consume types 1-5/11-14 at a time. */
    fun acquireDiscoveryLane(): ProductionDirectShadowSignaling? = synchronized(lifecycleLock) {
        if (closed.get() || clientTerminal) return@synchronized null
        val lease = DiscoveryLease()
        if (!discovery.compareAndSet(null, lease)) {
            lease.dispose()
            return@synchronized null
        }
        lease
    }

    /** At most one v5 coordinator may consume types 6-9 and promoted-path type 10 at a time. */
    fun acquireDataLane(): ProductionDirectOwnedSignalLane? = synchronized(lifecycleLock) {
        if (closed.get() || clientTerminal) return@synchronized null
        val lease = DataLease()
        if (!data.compareAndSet(null, lease)) {
            lease.dispose()
            return@synchronized null
        }
        lease
    }

    /**
     * Installs the device owner's exact promoted-path lookup before the shared reader starts.
     *
     * PATH_CLOSE is the only signal shared by the pre-promotion and promoted protocols. The
     * resolver must return true only when sender, connection id, and path epoch still identify a
     * live promoted actor. A missing, stale, or failed lookup remains owned by discovery; the
     * frame is never broadcast to both lanes.
     */
    fun installPromotedPathOwnerResolver(
        resolver: (TransportSignalEnvelopeV1) -> Boolean,
    ): Boolean = synchronized(lifecycleLock) {
        if (closed.get() || clientStarted || clientTerminal) return@synchronized false
        promotedPathOwnerResolver.compareAndSet(null, resolver)
    }

    /** Installs the synchronous peer-local overflow owner before the reader starts. */
    fun installLaneFaultObserver(
        observer: (ProductionDirectSignalLaneFault) -> Unit,
    ): Boolean = synchronized(lifecycleLock) {
        if (closed.get() || clientStarted || clientTerminal) return@synchronized false
        laneFaultObserver.compareAndSet(null, observer)
    }

    /**
     * Re-reads the server-wide routing gate without acquiring a discovery lease or creating a
     * second signalling client. The device owner uses this for low-frequency emergency-off polls.
     */
    suspend fun refreshRoutingPermission(): ProductionDirectRoutingPermission {
        fetchConfig()
        return routingPermission.value
    }

    fun diagnostics(): ProductionDirectSignalDispatcherDiagnostics {
        val discoveryLease = discovery.get()
        val dataLease = data.get()
        return ProductionDirectSignalDispatcherDiagnostics(
            readerActive = reader.get()?.isActive == true,
            discoveryLeaseActive = discoveryLease != null,
            dataLeaseActive = dataLease != null,
            discoveryQueuedFrames = discoveryLease?.queuedFrames?.get() ?: 0,
            discoveryQueuedBytes = discoveryLease?.queuedBytes?.get() ?: 0,
            dataQueuedFrames = dataLease?.queuedFrames?.get() ?: 0,
            dataQueuedBytes = dataLease?.queuedBytes?.get() ?: 0,
            discoveryDroppedFrames = discoveryDroppedFrames.get(),
            discoveryDroppedBytes = discoveryDroppedBytes.get(),
            dataDroppedFrames = dataDroppedFrames.get(),
            dataDroppedBytes = dataDroppedBytes.get(),
            closed = closed.get(),
        )
    }

    override fun close() {
        shutdown(ProductionDirectSignalDispatcherStatus.CLOSED)
    }

    suspend fun closeAndJoin() {
        val ownedReader = reader.get()
        val ownedStateObserver = stateObserver.get()
        close()
        ownedReader?.cancelAndJoin()
        ownedStateObserver?.cancelAndJoin()
        scopeJob.children.toList().forEach { it.cancelAndJoin() }
    }

    private fun dispatch(envelope: TransportSignalEnvelopeV1) {
        val target = when (envelope.signalType.number) {
            in DISCOVERY_TYPES -> discovery.get()
            in CUTOVER_TYPES -> data.get()
            PATH_CLOSE_TYPE -> if (isPromotedPathOwner(envelope)) data.get() else discovery.get()
            else -> {
                fail(DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED)
                return
            }
        } ?: return
        if (target.offer(envelope) == LaneOfferResult.OVERFLOW) {
            recordLaneDrop(target.kind, envelope)
        }
    }

    private fun isPromotedPathOwner(envelope: TransportSignalEnvelopeV1): Boolean =
        promotedPathOwnerResolver.get()?.let { resolver ->
            runCatching { resolver(envelope) }.getOrDefault(false)
        } ?: false

    private fun recordLaneDrop(
        kind: ProductionDirectSignalLaneKind,
        envelope: TransportSignalEnvelopeV1,
    ) {
        val bytes = envelope.serializedSize.toLong()
        val frameCounter: AtomicLong
        val byteCounter: AtomicLong
        when (kind) {
            ProductionDirectSignalLaneKind.DISCOVERY -> {
                frameCounter = discoveryDroppedFrames
                byteCounter = discoveryDroppedBytes
            }
            ProductionDirectSignalLaneKind.DATA -> {
                frameCounter = dataDroppedFrames
                byteCounter = dataDroppedBytes
            }
        }
        val totalFrames = frameCounter.incrementAndGet()
        val totalBytes = byteCounter.addAndGet(bytes.coerceAtLeast(0))
        val fault = ProductionDirectSignalLaneFault(
            lane = kind,
            code = DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW,
            senderDeviceId = envelope.senderDeviceId.toByteArray().toUuidOrNull(),
            connectionId = envelope.connectionId.toByteArray().toUuidOrNull(),
            pathEpoch = envelope.pathEpoch,
            droppedFrameBytes = bytes.coerceAtLeast(0),
            totalDroppedFrames = totalFrames,
            totalDroppedBytes = totalBytes,
        )
        mutableLatestLaneFault.value = fault
        runCatching { laneFaultObserver.get()?.invoke(fault) }
    }

    private fun observeClientState(value: DirectTransportSignalingState) {
        val fault = when (value) {
            is DirectTransportSignalingState.Fault -> value.fault.takeUnless { value.willRetry }
            DirectTransportSignalingState.Closed -> DirectSignalingFault(DirectSignalingFaultCode.CLOSED)
            DirectTransportSignalingState.Disabled ->
                DirectSignalingFault(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED)
            else -> null
        } ?: return
        synchronized(lifecycleLock) {
            if (closed.get() || clientTerminal) return
            clientTerminal = true
            mutableRoutingPermission.value = ProductionDirectRoutingPermission.UNKNOWN
            mutableStatus.value = ProductionDirectSignalDispatcherStatus.FAULT(fault.code)
        }
    }

    private fun fail(code: DirectSignalingFaultCode) {
        shutdown(ProductionDirectSignalDispatcherStatus.FAULT(code))
    }

    private fun shutdown(terminal: ProductionDirectSignalDispatcherStatus) {
        val resources = synchronized(lifecycleLock) {
            if (!closed.compareAndSet(false, true)) return
            clientStarted = false
            clientTerminal = true
            mutableRoutingPermission.value = ProductionDirectRoutingPermission.UNKNOWN
            mutableStatus.value = terminal
            ShutdownResources(
                discovery.getAndSet(null),
                data.getAndSet(null),
                reader.getAndSet(null),
                stateObserver.getAndSet(null),
            )
        }
        resources.discovery?.dispose()
        resources.data?.dispose()
        resources.reader?.cancel()
        resources.stateObserver?.cancel()
        runCatching(client::close)
        scopeJob.cancel()
        scope.cancel()
    }

    private suspend fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig> =
        configLock.withLock {
            if (!dispatcherUsable()) return@withLock closedResult()
            val result = client.fetchConfig()
            if (!dispatcherUsable()) return@withLock closedResult()
            mutableRoutingPermission.value = when (result) {
                is DirectSignalingCallResult.Success -> when {
                    DirectTransportSignalingClient.hasDirectDataCapabilities(result.value) ->
                        ProductionDirectRoutingPermission.PERMITTED
                    !result.value.directRoutingPermitted -> ProductionDirectRoutingPermission.FORBIDDEN
                    else -> ProductionDirectRoutingPermission.UNKNOWN
                }
                else -> ProductionDirectRoutingPermission.UNKNOWN
            }
            result
        }

    private fun dispatcherUsable(): Boolean =
        !closed.get() && synchronized(lifecycleLock) { !clientTerminal }

    private fun <T> closedResult(): DirectSignalingCallResult<T> = DirectSignalingCallResult.Fault(
        DirectSignalingFault(DirectSignalingFaultCode.CLOSED),
    )

    private abstract inner class LaneLease : Closeable {
        abstract val kind: ProductionDirectSignalLaneKind
        private val laneLock = Any()
        private val leaseClosed = AtomicBoolean(false)
        internal val queuedFrames = AtomicInteger(0)
        internal val queuedBytes = AtomicLong(0)
        private val queue = Channel<QueuedSignal>(
            capacity = maximumFramesPerLane,
            onUndeliveredElement = ::release,
        )

        open val incoming: Flow<TransportSignalEnvelopeV1> = queue.receiveAsFlow().map { queued ->
            release(queued)
            queued.envelope
        }

        fun offer(envelope: TransportSignalEnvelopeV1): LaneOfferResult = synchronized(laneLock) {
            if (leaseClosed.get()) return@synchronized LaneOfferResult.INACTIVE
            val bytes = envelope.serializedSize.toLong()
            if (bytes <= 0L || bytes > maximumBytesPerLane ||
                queuedBytes.get() > maximumBytesPerLane - bytes
            ) return@synchronized LaneOfferResult.OVERFLOW
            val queued = QueuedSignal(envelope, bytes)
            queuedFrames.incrementAndGet()
            queuedBytes.addAndGet(bytes)
            if (queue.trySend(queued).isSuccess) LaneOfferResult.ENQUEUED else {
                release(queued)
                if (leaseClosed.get()) LaneOfferResult.INACTIVE else LaneOfferResult.OVERFLOW
            }
        }

        protected fun isUsable(): Boolean =
            !leaseClosed.get() && !this@ProductionDirectSignalDispatcher.closed.get() &&
                synchronized(lifecycleLock) { !clientTerminal }

        final override fun close() {
            releaseLease(this)
        }

        fun dispose() = synchronized(laneLock) {
            if (!leaseClosed.compareAndSet(false, true)) return@synchronized
            queue.cancel()
        }

        private fun release(value: QueuedSignal) {
            queuedFrames.decrementAndGet()
            queuedBytes.addAndGet(-value.wireBytes)
        }
    }

    private inner class DiscoveryLease : LaneLease(), ProductionDirectShadowSignaling {
        override val kind = ProductionDirectSignalLaneKind.DISCOVERY
        override val incoming: Flow<TransportSignalEnvelopeV1> get() = super.incoming
        override val state: StateFlow<DirectTransportSignalingState> get() = client.state
        override val multipairV2Active: Boolean get() = isUsable() && client.multipairV2Active
        override suspend fun fetchConfig() = if (isUsable()) {
            this@ProductionDirectSignalDispatcher.fetchConfig()
        } else {
            closedResult()
        }
        override fun start() = isUsable() && this@ProductionDirectSignalDispatcher.start()
        override suspend fun issueTicket(
            targetDeviceId: UUID,
            connectionId: UUID,
            config: DirectTransportServerConfig,
        ) = if (isUsable()) {
            client.issueTicket(targetDeviceId, connectionId, config)
        } else {
            closedResult()
        }
        override fun send(envelope: TransportSignalEnvelopeV1) =
            isUsable() && client.send(envelope)
    }

    private inner class DataLease : LaneLease(), ProductionDirectOwnedSignalLane {
        override val kind = ProductionDirectSignalLaneKind.DATA
        override val incoming: Flow<TransportSignalEnvelopeV1> get() = super.incoming
        override val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities>
            get() = client.negotiatedCapabilities
        override fun send(envelope: TransportSignalEnvelopeV1) =
            isUsable() && client.send(envelope)
    }

    private fun releaseLease(value: LaneLease) {
        when (value) {
            is DiscoveryLease -> discovery.compareAndSet(value, null)
            is DataLease -> data.compareAndSet(value, null)
        }
        value.dispose()
    }

    private data class ShutdownResources(
        val discovery: LaneLease?,
        val data: LaneLease?,
        val reader: Job?,
        val stateObserver: Job?,
    )

    internal companion object {
        private val DISCOVERY_TYPES = (1..5).toSet() + (11..14).toSet()
        private val CUTOVER_TYPES = 6..9
        private const val PATH_CLOSE_TYPE = 10
        internal const val MAXIMUM_FRAMES_PER_LANE = 32
        internal const val MAXIMUM_BYTES_PER_LANE = 512L * 1024L

        /** Production construction always advertises v4 multipair; v5 data follows its own gate. */
        fun production(
            rootUrl: String,
            tokenProvider: AccessTokenProvider,
            localDeviceId: UUID,
            identity: DeviceIdentity,
            ticketSignatureVerifier: DirectSignalingTicketSignatureVerifier,
            parentScope: CoroutineScope,
            httpClient: OkHttpClient = OkHttpClient(),
            allowCleartextForLocalTest: Boolean = false,
            directDataEnabled: Boolean = false,
            random: SecureRandom = SecureRandom(),
        ): ProductionDirectSignalDispatcher = ProductionDirectSignalDispatcher(
            ClientBoundary(
                DirectTransportSignalingClient(
                    rootUrl = rootUrl,
                    tokenProvider = tokenProvider,
                    deviceId = localDeviceId,
                    identity = identity,
                    role = "android",
                    ticketSignatureVerifier = ticketSignatureVerifier,
                    feature = DirectTransportSignalingFeature(enabled = true),
                    multipairV2Enabled = true,
                    directDataV1Enabled = directDataEnabled,
                    httpClient = httpClient,
                    parentScope = parentScope,
                    random = random,
                    allowCleartextForLocalTest = allowCleartextForLocalTest,
                ),
            ),
            parentScope,
        )
    }
}

private data class QueuedSignal(
    val envelope: TransportSignalEnvelopeV1,
    val wireBytes: Long,
)

private enum class LaneOfferResult {
    ENQUEUED,
    INACTIVE,
    OVERFLOW,
}

internal interface ProductionDirectSignalClientBoundary : Closeable {
    val incoming: Flow<TransportSignalEnvelopeV1>
    val state: StateFlow<DirectTransportSignalingState>
    val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities>
    val multipairV2Active: Boolean
    suspend fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig>
    fun start(): Boolean
    suspend fun issueTicket(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ): DirectSignalingCallResult<ValidatedDirectPathTicket>
    fun send(envelope: TransportSignalEnvelopeV1): Boolean
}

private class ClientBoundary(
    private val client: DirectTransportSignalingClient,
) : ProductionDirectSignalClientBoundary {
    override val incoming get() = client.incoming
    override val state get() = client.state
    override val negotiatedCapabilities get() = client.negotiatedCapabilities
    override val multipairV2Active get() = client.multipairV2Active
    override suspend fun fetchConfig() = client.fetchConfigCancellable()
    override fun start() = client.startSignaling()
    override suspend fun issueTicket(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ) = client.issueTicketCancellable(targetDeviceId, connectionId, config)
    override fun send(envelope: TransportSignalEnvelopeV1) = client.send(envelope)
    override fun close() = client.close()
}

private fun ByteArray.toUuidOrNull(): UUID? = if (size == 16 && any { it != 0.toByte() }) {
    ByteBuffer.wrap(this).let { UUID(it.long, it.long) }
} else null

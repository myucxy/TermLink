package dev.termlink.network.direct

import android.net.ConnectivityManager
import android.net.Network
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.ProductionRealtimeMultiplexer
import dev.termlink.network.production.ProductionRealtimeSessionClient
import dev.termlink.network.production.ProductionDirectSequenceReservation
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.Closeable
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient

/** Server-authoritative Direct routing policy exposed to the product UI. */
enum class ProductionDirectRoutingPolicy {
    UNKNOWN,
    FAILED,
    UNAVAILABLE,
    AUTO,
    RELAY_ONLY,
}

/** Public, redacted carrier state. It grants no session/control authority. */
enum class ProductionDirectCarrier {
    RELAY,
    NEGOTIATING,
    LAN_DIRECT,
    NAT_DIRECT,
}

/** Activation handle for one already-authorized live session. */
interface ProductionDirectLiveSessionRegistration : Closeable {
    fun activate(): Boolean
}

/**
 * Device-scoped owner of Direct discovery, signalling dispatch, ABI-v5 routing,
 * and peer-local fallback. The shared Relay WebSocket is deliberately only a
 * dependency; this owner cannot close or reconnect it.
 */
class ProductionDirectDeviceCoordinator(
    private val authenticationGeneration: Long,
    rootUrl: String,
    tokenProvider: AccessTokenProvider,
    private val localDeviceId: UUID,
    private val identity: DeviceIdentity,
    private val multiplexer: ProductionRealtimeMultiplexer,
    connectivityManager: ConnectivityManager,
    parentScope: CoroutineScope,
    httpClient: OkHttpClient = OkHttpClient(),
    allowCleartextForLocalTest: Boolean = false,
    private val shadowDiscoveryEnabled: Boolean = false,
    private val dataPlaneEnabled: Boolean = PRODUCTION_DIRECT_DATA_PLANE_ENABLED,
) : Closeable {
    private val featureGates = ProductionDirectFeatureGates(
        shadowDiscoveryEnabled,
        dataPlaneEnabled,
    )
    private val closed = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val serverRoutingPermitted = AtomicBoolean(false)
    private val mutableRoutingPolicy = MutableStateFlow(ProductionDirectRoutingPolicy.UNKNOWN)
    val routingPolicy: StateFlow<ProductionDirectRoutingPolicy> = mutableRoutingPolicy.asStateFlow()
    val networkCheckResults: StateFlow<Map<UUID, ProductionDirectNetworkCheckResult>> by lazy {
        shadowRuntime.networkCheckResults
    }
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val sessions = ConcurrentHashMap<UUID, LiveSession>()
    private val incrementalCutoverRetries = ConcurrentHashMap<UUID, Job>()
    private val promotedPeerLock = Any()
    private val promotedPeers = LinkedHashMap<UUID, ProductionDirectPromotedPeerCutover>()
    private val random = SecureRandom()
    private val dispatcher = ProductionDirectSignalDispatcher.production(
        rootUrl = rootUrl,
        tokenProvider = tokenProvider,
        localDeviceId = localDeviceId,
        identity = identity,
        ticketSignatureVerifier = DirectSignalingTicketSignatureVerifier(::verifyEd25519),
        parentScope = scope,
        httpClient = httpClient,
        allowCleartextForLocalTest = allowCleartextForLocalTest,
        directDataEnabled = featureGates.dataPlaneEnabled,
    )
    private val dataLane = checkNotNull(dispatcher.acquireDataLane())
    private val dataCoordinator = ProductionDirectDataCoordinator(
        localDeviceId,
        dataLane,
        scope,
        cutoverObserver = ProductionDirectCutoverObserver(::observeSessionCutover),
    )
    private lateinit var shadowRuntime: ProductionDirectShadowRuntime
    private lateinit var routingPermissionJob: Job
    private lateinit var routingRefreshJob: Job
    private lateinit var signalingGenerationJob: Job
    private val networkLock = Any()
    private var networkObserved = false
    private var currentNetwork: Network? = null
    private var authorizedPeerEpochs: Map<UUID, Long> = emptyMap()
    /** Exact gate object admitted on [currentNetwork]; identity is the network-change fence. */
    private var networkScopedGate: ProductionDirectDataGate? = null
    private val dataRuntime = ProductionDirectDataRuntime(
        multiplexer = multiplexer,
        realtimeCapabilities = multiplexer.directRealtimeNegotiatedCapabilities,
        signaling = dataCoordinator,
        relaySender = ProductionDirectRelayExactSender(multiplexer::sendRelayExactForPeer),
        signalProcessor = dataCoordinator,
        signer = ProductionDirectDataSigner(identity::sign),
        parentScope = scope,
        enabled = featureGates.dataPlaneEnabled,
        peerRetirementObserver = { peerDeviceId, reason ->
            forgetPromotedPeer(peerDeviceId)
            if (::shadowRuntime.isInitialized && reason !in setOf(
                    ProductionDirectPeerRetirementReason.AUTHENTICATION_CHANGED,
                    ProductionDirectPeerRetirementReason.NETWORK_CHANGED,
                    ProductionDirectPeerRetirementReason.SERVER_ROUTING_DISABLED,
                    ProductionDirectPeerRetirementReason.CLOSED,
                )
            ) {
                shadowRuntime.retryPromotedPeer(authenticationGeneration, peerDeviceId)
            }
        },
        sessionCarrierObserver = ::observeNativeSessionCarrier,
    )
    private val mutableCarriers = MutableStateFlow<Map<UUID, ProductionDirectCarrier>>(emptyMap())
    val carriers: StateFlow<Map<UUID, ProductionDirectCarrier>> = mutableCarriers.asStateFlow()
    private val mutableSessionCarriers = MutableStateFlow<Map<UUID, ProductionDirectCarrier>>(emptyMap())
    /** Exact active-session projection; an unlisted late session remains Relay. */
    val sessionCarriers: StateFlow<Map<UUID, ProductionDirectCarrier>> =
        mutableSessionCarriers.asStateFlow()
    private val carrierJob = scope.launch {
        dataRuntime.carriers.collect(::publishCarrierProjection)
    }

    init {
        require(authenticationGeneration > 0)
        require(localDeviceId != UUID(0, 0))
        check(
            dispatcher.installPromotedPathOwnerResolver { envelope ->
                val sender = envelope.senderDeviceId.toByteArray().toDirectUuidOrNull()
                    ?: return@installPromotedPathOwnerResolver false
                val recipient = envelope.recipientDeviceId.toByteArray().toDirectUuidOrNull()
                    ?: return@installPromotedPathOwnerResolver false
                val connectionId = envelope.connectionId.toByteArray().toDirectUuidOrNull()
                    ?: return@installPromotedPathOwnerResolver false
                recipient == localDeviceId && envelope.pathEpoch > 0 &&
                    dataRuntime.ownsPromotedPath(sender, connectionId, envelope.pathEpoch)
            },
        )
        check(
            dispatcher.installLaneFaultObserver { fault ->
                val peerDeviceId = fault.senderDeviceId ?: return@installLaneFaultObserver
                val connectionId = fault.connectionId ?: return@installLaneFaultObserver
                if (fault.pathEpoch <= 0) return@installLaneFaultObserver
                when (fault.lane) {
                    ProductionDirectSignalLaneKind.DATA -> dataRuntime.retirePromotedPath(
                        peerDeviceId,
                        connectionId,
                        fault.pathEpoch,
                        ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
                    )
                    ProductionDirectSignalLaneKind.DISCOVERY -> if (::shadowRuntime.isInitialized) {
                        shadowRuntime.observeSignalLaneOverflow(
                            authenticationGeneration,
                            peerDeviceId,
                            connectionId,
                            fault.pathEpoch,
                        )
                    } else {
                        Unit
                    }
                }
            },
        )
        check(dataCoordinator.attachRuntime(dataRuntime))
        shadowRuntime = ProductionDirectShadowRuntime(
            enabled = featureGates.shadowDiscoveryEnabled,
            authenticationGeneration = authenticationGeneration,
            localDeviceId = localDeviceId,
            identity = identity,
            relayEvents = multiplexer.directRelayConnectionEvents,
            connectivityManager = connectivityManager,
            parentScope = scope,
            sharedSignalingFactory = {
                checkNotNull(dispatcher.acquireDiscoveryLane()) {
                    "Direct discovery signalling lane is already active"
                }
            },
            productionGateProvider = { permitted, keyEpoch ->
                if (!featureGates.dataPlaneEnabled || !permitted || !serverRoutingPermitted.get()) {
                    null
                } else {
                    synchronized(networkLock) {
                        dataRuntime.captureGate(serverRoutingPermitted = true, keyEpoch = keyEpoch)
                            .also { networkScopedGate = it }
                    }
                }
            },
            productionHandoffConsumer = { handoff ->
                val peerDeviceId = handoff.registration.peerDeviceId
                val pathFence = handoff.registration.pathFence
                val keyEpoch = handoff.registration.keyEpoch
                val binding = handoff.registration.directPathBinding.copyOf()
                installPromotedPeer(handoff, peerDeviceId, pathFence, keyEpoch, binding)
            },
        )
        routingPermissionJob = scope.launch {
            dispatcher.routingPermission.collect { permission ->
                when (permission) {
                    ProductionDirectRoutingPermission.PERMITTED -> {
                        mutableRoutingPolicy.value = ProductionDirectRoutingPolicy.AUTO
                        serverRoutingPermitted.set(true)
                        shadowRuntime.observeProductionRoutingPermitted(authenticationGeneration, true)
                    }
                    ProductionDirectRoutingPermission.FORBIDDEN -> {
                        mutableRoutingPolicy.value = ProductionDirectRoutingPolicy.RELAY_ONLY
                        synchronized(networkLock) {
                            serverRoutingPermitted.set(false)
                            networkScopedGate = null
                        }
                        shadowRuntime.observeProductionRoutingPermitted(authenticationGeneration, false)
                        forgetAllPromotedPeers()
                        dataRuntime.retireAllForServerRoutingDisabled()
                        mutableCarriers.value = emptyMap()
                    }
                    // An unknown server state blocks new promotions without
                    // tearing down an already committed Direct path.
                    ProductionDirectRoutingPermission.UNKNOWN,
                    ProductionDirectRoutingPermission.FAILED,
                    ProductionDirectRoutingPermission.UNAVAILABLE -> {
                        mutableRoutingPolicy.value = when (permission) {
                            ProductionDirectRoutingPermission.FAILED -> ProductionDirectRoutingPolicy.FAILED
                            ProductionDirectRoutingPermission.UNAVAILABLE -> ProductionDirectRoutingPolicy.UNAVAILABLE
                            else -> ProductionDirectRoutingPolicy.UNKNOWN
                        }
                        synchronized(networkLock) {
                            serverRoutingPermitted.set(false)
                            networkScopedGate = null
                        }
                    }
                }
            }
        }
        routingRefreshJob = scope.launch(start = CoroutineStart.LAZY) {
            while (!closed.get()) {
                try {
                    val permission = dispatcher.refreshRoutingPermission()
                    if (permission != ProductionDirectRoutingPermission.FAILED &&
                        dispatcher.status.value == ProductionDirectSignalDispatcherStatus.IDLE && dispatcher.start()) {
                        shadowRuntime.start()
                    }
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (_: Throwable) {
                    mutableRoutingPolicy.value = ProductionDirectRoutingPolicy.FAILED
                    // A failed refresh blocks new promotions;
                    // the Relay and committed Direct peers remain independently owned.
                }
                delay(ROUTING_PERMISSION_REFRESH_MILLIS)
            }
        }
        signalingGenerationJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            dataCoordinator.negotiatedCapabilities.collect { current ->
                dataRuntime.signalingCapabilitiesChanged(current)
                if (current.hasSessionCutoverCapabilities()) {
                    val peers = synchronized(promotedPeerLock) { promotedPeers.keys.toList() }
                    peers.forEach(::startOutboundCutoverWhenReady)
                }
            }
        }
    }

    fun start(): Boolean {
        if (!featureGates.shadowDiscoveryEnabled) {
            mutableRoutingPolicy.value = ProductionDirectRoutingPolicy.UNAVAILABLE
            return false
        }
        if (closed.get() ||
            !started.compareAndSet(false, true)
        ) return false
        routingRefreshJob.start()
        return true
    }

    fun replaceAuthorizedPeers(
        expectedAuthenticationGeneration: Long,
        peers: List<ProductionDirectAuthorizedPeer>,
    ): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || closed.get()) return false
        if (!shadowRuntime.replaceAuthorizedPeers(expectedAuthenticationGeneration, peers)) return false
        val authorizedEpochs = peers.associate { it.deviceId to it.keyEpoch }
        synchronized(networkLock) {
            authorizedPeerEpochs = authorizedEpochs
            networkScopedGate = null
        }
        val retired = synchronized(promotedPeerLock) {
            promotedPeers.values.filter { authorizedEpochs[it.peerDeviceId] != it.keyEpoch }
        }
        retired.forEach { promoted ->
            forgetPromotedPeer(promoted.peerDeviceId, promoted.pathFence)
            dataRuntime.retirePeer(
                promoted.peerDeviceId,
                ProductionDirectPeerRetirementReason.PEER_REMOVED,
            )
        }
        return true
    }

    fun observeNetwork(expectedAuthenticationGeneration: Long, network: Network?): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || closed.get()) return false
        val changed = synchronized(networkLock) {
            if (networkObserved && currentNetwork == network) false else {
                networkObserved = true
                currentNetwork = network
                networkScopedGate = null
                true
            }
        }
        if (changed) {
            forgetAllPromotedPeers()
            dataRuntime.networkChanged()
        }
        return shadowRuntime.observeNetwork(expectedAuthenticationGeneration, network)
    }

    /** Re-runs the existing Direct discovery for one peer without changing the Relay route. */
    fun requestNetworkCheck(peerDeviceId: UUID, kind: ProductionDirectNetworkCheckKind): Boolean {
        if (closed.get() || mutableRoutingPolicy.value != ProductionDirectRoutingPolicy.AUTO) return false
        if (!shadowRuntime.requestNetworkCheck(authenticationGeneration, peerDeviceId, kind)) return false
        dataRuntime.retirePeer(
            peerDeviceId,
            ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
        )
        forgetPromotedPeer(peerDeviceId)
        return true
    }

    fun registerLiveSession(
        peerDeviceId: UUID,
        sessionId: UUID,
        client: ProductionRealtimeSessionClient,
    ): ProductionDirectLiveSessionRegistration {
        require(peerDeviceId != UUID(0, 0) && sessionId != UUID(0, 0))
        check(!closed.get()) { "Direct device coordinator is closed" }
        val value = LiveSession(peerDeviceId, sessionId, client)
        check(sessions.putIfAbsent(sessionId, value) == null) {
            "Direct live session is already registered"
        }
        return value
    }

    fun updateAppliedCursor(peerDeviceId: UUID, sessionId: UUID, sequence: Long): Boolean =
        !closed.get() && sequence >= 0 && sessions[sessionId]?.let { session ->
            session.active.get() && session.peerDeviceId == peerDeviceId &&
                dataRuntime.updateAppliedCursor(peerDeviceId, sessionId, sequence)
        } == true

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        synchronized(networkLock) {
            networkScopedGate = null
            authorizedPeerEpochs = emptyMap()
        }
        sessions.values.forEach(LiveSession::close)
        sessions.clear()
        incrementalCutoverRetries.values.forEach(Job::cancel)
        incrementalCutoverRetries.clear()
        forgetAllPromotedPeers()
        mutableCarriers.value = emptyMap()
        mutableSessionCarriers.value = emptyMap()
        shadowRuntime.close()
        dataRuntime.authenticationChanged()
        dataCoordinator.close()
        dataRuntime.close()
        dispatcher.close()
        carrierJob.cancel()
        routingPermissionJob.cancel()
        routingRefreshJob.cancel()
        signalingGenerationJob.cancel()
        scopeJob.cancel()
        scope.cancel()
    }

    suspend fun closeAndJoin() {
        close()
        withContext(NonCancellable) {
            shadowRuntime.closeAndJoin()
            dataCoordinator.closeAndJoin()
            dataRuntime.closeAndJoin()
            dispatcher.closeAndJoin()
            carrierJob.join()
            routingPermissionJob.join()
            routingRefreshJob.join()
            signalingGenerationJob.join()
        }
    }

    private fun installPromotedPeer(
        handoff: ProductionDirectV4ReadyHandoff,
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        keyEpoch: Long,
        directPathBinding: ByteArray,
    ): Boolean {
        val promoted = ProductionDirectPromotedPeerCutover(
            peerDeviceId,
            pathFence,
            keyEpoch,
            directPathBinding,
        )
        var retired: ProductionDirectPromotedPeerCutover? = null
        val installed = runCatching {
            synchronized(networkLock) {
                if (closed.get() || !serverRoutingPermitted.get() ||
                    networkScopedGate !== handoff.gate ||
                    authorizedPeerEpochs[peerDeviceId] != keyEpoch
                ) return@synchronized false
                synchronized(promotedPeerLock) {
                    if (!dataCoordinator.promoteV4AndInstall(handoff)) return@synchronized false
                    retired = promotedPeers.put(peerDeviceId, promoted)
                    true
                }
            }
        }.getOrDefault(false)
        if (!installed) {
            promoted.close()
            return false
        }
        if (retired != null) {
            incrementalCutoverRetries.remove(peerDeviceId)?.cancel()
            retired?.close()
        }
        publishCarrierProjection()
        if (!dataRuntime.hasPeer(peerDeviceId)) {
            forgetPromotedPeer(peerDeviceId, pathFence)
            return true
        }
        startOutboundCutoverWhenReady(peerDeviceId)
        return true
    }

    private fun startOutboundCutoverWhenReady(peerDeviceId: UUID) {
        scope.launch {
            val selected = sessions.values
                .filter { it.active.get() && it.peerDeviceId == peerDeviceId }
                .sortedBy { it.sessionId.toString() }
            if (selected.isEmpty()) return@launch
            val promoted = synchronized(promotedPeerLock) {
                promotedPeers[peerDeviceId]
            } ?: return@launch
            val selectedById = selected.associateBy(LiveSession::sessionId)
            val claimed = promoted.claimIncrementalCutover(selectedById.keys) ?: return@launch
            publishCarrierProjection()
            val reservations = ArrayList<Pair<LiveSession, ProductionDirectSequenceReservation>>()
            try {
                for (sessionId in claimed.sessionIds) {
                    val session = selectedById[sessionId]
                        ?: throw IllegalStateException("Direct live session changed before cutover reservation")
                    val reservation = session.client.reserveNextEnvelopeSequenceForDirectCutover()
                    reservations += session to reservation
                    check(session.active.get() && sessions[session.sessionId] === session) {
                        "Direct live session changed during cutover reservation"
                    }
                }
                val proposal = ProductionDirectCutoverCodec.createProposal(
                    connectionId = promoted.pathFence.connectionId,
                    pathEpoch = promoted.pathFence.pathEpoch,
                    direction = Direction.ANDROID_TO_PC,
                    barrierId = nonZeroUuid(),
                    directPathBinding = claimed.directPathBinding,
                    sessionBarriers = reservations.map { (session, reservation) ->
                        ProductionDirectSessionCutoverBarrier(session.sessionId, reservation.sequence)
                    },
                    duplicateGraceMilliseconds = DEFAULT_DUPLICATE_GRACE_MILLIS,
                )
                val accepted = synchronized(promotedPeerLock) {
                    promotedPeers[peerDeviceId] === promoted &&
                        dataRuntime.startCutover(peerDeviceId, proposal)
                }
                if (!accepted) {
                    promoted.abortIncrementalCutover(claimed.sessionIds)
                    if (!dataRuntime.hasPeer(peerDeviceId)) {
                        forgetPromotedPeer(peerDeviceId, promoted.pathFence)
                    } else {
                        scheduleIncrementalCutoverRetry(peerDeviceId, promoted)
                    }
                }
            } catch (cancelled: CancellationException) {
                promoted.abortIncrementalCutover(claimed.sessionIds)
                throw cancelled
            } catch (_: Throwable) {
                promoted.abortIncrementalCutover(claimed.sessionIds)
                scheduleIncrementalCutoverRetry(peerDeviceId, promoted)
            } finally {
                reservations.forEach { (_, reservation) -> reservation.releaseForSending() }
                claimed.close()
            }
        }
    }

    private fun scheduleIncrementalCutoverRetry(
        peerDeviceId: UUID,
        expected: ProductionDirectPromotedPeerCutover,
    ) {
        if (closed.get()) return
        lateinit var retry: Job
        retry = scope.launch(start = CoroutineStart.LAZY) {
            try {
                delay(INCREMENTAL_CUTOVER_RETRY_MILLIS)
                val stillCurrent = synchronized(promotedPeerLock) {
                    promotedPeers[peerDeviceId] === expected
                }
                incrementalCutoverRetries.remove(peerDeviceId, retry)
                if (stillCurrent) startOutboundCutoverWhenReady(peerDeviceId)
            } finally {
                incrementalCutoverRetries.remove(peerDeviceId, retry)
            }
        }
        if (incrementalCutoverRetries.putIfAbsent(peerDeviceId, retry) == null) {
            retry.start()
        } else {
            retry.cancel()
        }
    }

    private fun observeSessionCutover(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        direction: DirectNativeDirectionV5,
        activeSessionIds: Set<UUID>,
        committed: Boolean,
    ) {
        val current = synchronized(promotedPeerLock) {
            promotedPeers[peerDeviceId]?.takeIf { it.pathFence == pathFence }?.also {
                it.replaceCommittedSessions(direction, activeSessionIds)
            }
        } ?: return
        publishCarrierProjection()
        if (direction == DirectNativeDirectionV5.ANDROID_TO_PC) {
            if (committed) {
                startOutboundCutoverWhenReady(peerDeviceId)
            } else {
                scheduleIncrementalCutoverRetry(peerDeviceId, current)
            }
        }
    }

    private fun observeNativeSessionCarrier(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence,
        sessionId: UUID,
        direction: Direction,
        carrier: dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5,
    ) {
        val nativeDirection = when (direction) {
            Direction.PC_TO_ANDROID -> DirectNativeDirectionV5.PC_TO_ANDROID
            Direction.ANDROID_TO_PC -> DirectNativeDirectionV5.ANDROID_TO_PC
            else -> return
        }
        val updated = synchronized(promotedPeerLock) {
            promotedPeers[peerDeviceId]
                ?.takeIf { it.pathFence == pathFence }
                ?.also { it.observeSessionCarrier(nativeDirection, sessionId, carrier) }
        } ?: return
        publishCarrierProjection()
        if (carrier == dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.RELAY &&
            nativeDirection == DirectNativeDirectionV5.ANDROID_TO_PC
        ) {
            scheduleIncrementalCutoverRetry(peerDeviceId, updated)
        }
    }

    private fun forgetPromotedPeer(
        peerDeviceId: UUID,
        pathFence: ProductionDirectPathFence? = null,
    ) {
        val retired = synchronized(promotedPeerLock) {
            val current = promotedPeers[peerDeviceId] ?: return@synchronized null
            if (pathFence != null && current.pathFence != pathFence) return@synchronized null
            promotedPeers.remove(peerDeviceId)
        }
        incrementalCutoverRetries.remove(peerDeviceId)?.cancel()
        retired?.close()
        if (retired != null) publishCarrierProjection()
    }

    private fun retirePromotedPeerIfCurrent(promoted: ProductionDirectPromotedPeerCutover) {
        val retired = synchronized(promotedPeerLock) {
            if (promotedPeers[promoted.peerDeviceId] !== promoted) return@synchronized false
            promotedPeers.remove(promoted.peerDeviceId)
            dataRuntime.retirePeer(
                promoted.peerDeviceId,
                ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
            )
            true
        }
        if (retired) promoted.close()
    }

    private fun forgetAllPromotedPeers() {
        val retired = synchronized(promotedPeerLock) {
            promotedPeers.values.toList().also { promotedPeers.clear() }
        }
        incrementalCutoverRetries.values.forEach(Job::cancel)
        incrementalCutoverRetries.clear()
        retired.forEach(ProductionDirectPromotedPeerCutover::close)
        if (retired.isNotEmpty()) publishCarrierProjection()
    }

    private fun publishCarrierProjection(
        peerCarriers: Map<UUID, ProductionDirectPeerCarrier> = dataRuntime.carriers.value,
    ) {
        val publicPeers = peerCarriers.mapValues { (_, value) -> value.toPublicCarrier() }
        val promoted = synchronized(promotedPeerLock) {
            promotedPeers.toMap()
        }
        val activeSessions = sessions.values.filter(LiveSession::isActive)
        mutableCarriers.value = publicPeers
        mutableSessionCarriers.value = activeSessions.associate { session ->
            val peerCarrier = publicPeers[session.peerDeviceId] ?: ProductionDirectCarrier.RELAY
            val route = promoted[session.peerDeviceId]
                ?.sessionProjection(session.sessionId)
                ?: ProductionDirectSessionProjection.RELAY
            val carrier = when (route) {
                ProductionDirectSessionProjection.DIRECT -> when (peerCarrier) {
                    ProductionDirectCarrier.LAN_DIRECT,
                    ProductionDirectCarrier.NAT_DIRECT,
                    -> peerCarrier
                    else -> ProductionDirectCarrier.NEGOTIATING
                }
                ProductionDirectSessionProjection.NEGOTIATING -> ProductionDirectCarrier.NEGOTIATING
                ProductionDirectSessionProjection.RELAY -> ProductionDirectCarrier.RELAY
            }
            session.sessionId to carrier
        }
    }

    private fun nonZeroUuid(): UUID {
        repeat(8) {
            UUID(random.nextLong(), random.nextLong()).takeIf { it != UUID(0, 0) }?.let { return it }
        }
        throw IllegalStateException("Direct barrier identifier source failed")
    }

    private inner class LiveSession(
        val peerDeviceId: UUID,
        val sessionId: UUID,
        val client: ProductionRealtimeSessionClient,
    ) : ProductionDirectLiveSessionRegistration {
        val active = AtomicBoolean(false)
        private val registrationClosed = AtomicBoolean(false)

        override fun activate(): Boolean {
            if (closed.get() || registrationClosed.get() || !active.compareAndSet(false, true)) {
                return false
            }
            publishCarrierProjection()
            startOutboundCutoverWhenReady(peerDeviceId)
            return true
        }

        override fun close() {
            if (!registrationClosed.compareAndSet(false, true)) return
            active.set(false)
            sessions.remove(sessionId, this)
            dataRuntime.deregisterSession(peerDeviceId, sessionId)
            synchronized(promotedPeerLock) {
                promotedPeers[peerDeviceId]?.removeSession(sessionId)
            }
            publishCarrierProjection()
        }

        fun isActive(): Boolean = active.get()
    }

    private companion object {
        const val DEFAULT_DUPLICATE_GRACE_MILLIS = 500
        const val INCREMENTAL_CUTOVER_RETRY_MILLIS = 30_000L
        const val ROUTING_PERMISSION_REFRESH_MILLIS = 30_000L
    }

}

internal data class ProductionDirectFeatureGates(
    val shadowDiscoveryEnabled: Boolean,
    val dataPlaneEnabled: Boolean,
) {
    init {
        require(!dataPlaneEnabled || shadowDiscoveryEnabled) {
            "Direct data requires the discovery owner"
        }
    }
}

internal enum class ProductionDirectSessionProjection {
    RELAY,
    NEGOTIATING,
    DIRECT,
}

/** Caller-owned copy used by one bounded incremental proposal. */
internal class ProductionDirectIncrementalCutoverClaim(
    val sessionIds: Set<UUID>,
    val directPathBinding: ByteArray,
) : Closeable {
    override fun close() = directPathBinding.fill(0)
}

/**
 * Owns one promoted path's reusable binding and exact per-direction session
 * coverage. Only one outbound incremental batch may negotiate at a time.
 */
internal class ProductionDirectPromotedPeerCutover(
    val peerDeviceId: UUID,
    val pathFence: ProductionDirectPathFence,
    val keyEpoch: Long,
    directPathBinding: ByteArray,
) : Closeable {
    private val lock = Any()
    private var directPathBinding: ByteArray? = directPathBinding
    private var negotiatingAndroidToPc: Set<UUID> = emptySet()
    private var committedAndroidToPc: Set<UUID> = emptySet()
    private var committedPcToAndroid: Set<UUID> = emptySet()

    init {
        require(peerDeviceId != UUID(0, 0))
        require(keyEpoch > 0)
        require(directPathBinding.size == 32)
    }

    fun claimIncrementalCutover(
        sessionIds: Set<UUID>,
    ): ProductionDirectIncrementalCutoverClaim? = synchronized(lock) {
        require(sessionIds.isNotEmpty())
        if (negotiatingAndroidToPc.isNotEmpty()) return@synchronized null
        val selected = sessionIds.asSequence()
            .filterNot(committedAndroidToPc::contains)
            .sortedBy(UUID::toString)
            .take(ProductionDirectCutoverCodec.MAXIMUM_SESSION_BARRIERS)
            .toCollection(linkedSetOf())
        if (selected.isEmpty()) return@synchronized null
        val binding = directPathBinding?.copyOf() ?: return@synchronized null
        negotiatingAndroidToPc = selected
        ProductionDirectIncrementalCutoverClaim(selected, binding)
    }

    fun abortIncrementalCutover(sessionIds: Set<UUID>) = synchronized(lock) {
        if (negotiatingAndroidToPc == sessionIds) negotiatingAndroidToPc = emptySet()
    }

    fun replaceCommittedSessions(
        direction: DirectNativeDirectionV5,
        sessionIds: Set<UUID>,
    ) = synchronized(lock) {
        when (direction) {
            DirectNativeDirectionV5.ANDROID_TO_PC -> {
                committedAndroidToPc = sessionIds.toSet()
                negotiatingAndroidToPc = emptySet()
            }
            DirectNativeDirectionV5.PC_TO_ANDROID -> committedPcToAndroid = sessionIds.toSet()
        }
    }

    fun observeSessionCarrier(
        direction: DirectNativeDirectionV5,
        sessionId: UUID,
        carrier: dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5,
    ) = synchronized(lock) {
        val direct = carrier == dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.LAN_DIRECT ||
            carrier == dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5.NAT_DIRECT
        when (direction) {
            DirectNativeDirectionV5.ANDROID_TO_PC -> {
                negotiatingAndroidToPc = negotiatingAndroidToPc - sessionId
                committedAndroidToPc = if (direct) {
                    committedAndroidToPc + sessionId
                } else {
                    committedAndroidToPc - sessionId
                }
            }
            DirectNativeDirectionV5.PC_TO_ANDROID -> committedPcToAndroid = if (direct) {
                committedPcToAndroid + sessionId
            } else {
                committedPcToAndroid - sessionId
            }
        }
    }

    fun removeSession(sessionId: UUID) = synchronized(lock) {
        negotiatingAndroidToPc = negotiatingAndroidToPc - sessionId
        committedAndroidToPc = committedAndroidToPc - sessionId
        committedPcToAndroid = committedPcToAndroid - sessionId
    }

    fun sessionProjection(sessionId: UUID): ProductionDirectSessionProjection = synchronized(lock) {
        when {
            sessionId in committedAndroidToPc && sessionId in committedPcToAndroid ->
                ProductionDirectSessionProjection.DIRECT
            sessionId in negotiatingAndroidToPc || sessionId in committedAndroidToPc ||
                sessionId in committedPcToAndroid -> ProductionDirectSessionProjection.NEGOTIATING
            else -> ProductionDirectSessionProjection.RELAY
        }
    }

    override fun close() = synchronized(lock) {
        directPathBinding?.fill(0)
        directPathBinding = null
        negotiatingAndroidToPc = emptySet()
        committedAndroidToPc = emptySet()
        committedPcToAndroid = emptySet()
    }
}

private fun ProductionDirectPeerCarrier.toPublicCarrier(): ProductionDirectCarrier = when (this) {
    ProductionDirectPeerCarrier.RELAY -> ProductionDirectCarrier.RELAY
    ProductionDirectPeerCarrier.NEGOTIATING_DIRECT -> ProductionDirectCarrier.NEGOTIATING
    ProductionDirectPeerCarrier.LAN_DIRECT -> ProductionDirectCarrier.LAN_DIRECT
    ProductionDirectPeerCarrier.NAT_DIRECT -> ProductionDirectCarrier.NAT_DIRECT
}

private fun DirectTransportSignalingNegotiatedCapabilities.hasSessionCutoverCapabilities(): Boolean =
    DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in capabilities &&
        DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in capabilities

private fun ByteArray.toDirectUuidOrNull(): UUID? =
    if (size == 16 && any { it != 0.toByte() }) {
        java.nio.ByteBuffer.wrap(this).let { UUID(it.long, it.long) }
    } else {
        null
    }

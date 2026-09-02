package dev.termlink.network.direct

import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.DirectTransportCrypto
import dev.termlink.network.direct.nativebridge.DirectNativeCreateConfig
import dev.termlink.network.direct.nativebridge.DirectNativeEvent
import dev.termlink.network.direct.nativebridge.DirectNativeEventKind
import dev.termlink.network.direct.nativebridge.DirectNativeGatherRequest
import dev.termlink.network.direct.nativebridge.DirectNativeHandle
import dev.termlink.network.direct.nativebridge.DirectNativeNominateRequest
import dev.termlink.network.direct.nativebridge.DirectNativeResult
import dev.termlink.network.direct.nativebridge.DirectNativeRole
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddress
import dev.termlink.network.direct.nativebridge.JniDirectNetworkCore
import dev.termlink.network.direct.nativebridge.DirectNativeAppendCheckPairsRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeBeginChecksRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateKindV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidatePairV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateV4
import dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeCreateConfigV4
import dev.termlink.network.direct.nativebridge.DirectNativeEventKindV4
import dev.termlink.network.direct.nativebridge.DirectNativeEventV4
import dev.termlink.network.direct.nativebridge.DirectNativeGatherRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeHandleV4
import dev.termlink.network.direct.nativebridge.DirectNativeResultV4
import dev.termlink.network.direct.nativebridge.DirectNativeRoleV4
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddressV4
import dev.termlink.network.direct.nativebridge.JniDirectNetworkCoreV4
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.ProductionRealtimeSessionConnectionEvent
import dev.termlink.network.production.RealtimeState
import dev.termlink.protocol.transport.v1.DirectCandidateKindV1
import dev.termlink.protocol.transport.v1.DirectCandidatePairV1
import dev.termlink.protocol.transport.v1.DirectCandidateV1
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectNominationV1
import dev.termlink.protocol.transport.v1.DirectCandidateUpdateV1
import dev.termlink.protocol.transport.v1.DirectConnectivityCheckBatchV2
import dev.termlink.protocol.transport.v1.DirectNominationAckV2
import dev.termlink.protocol.transport.v1.DirectNominationResultV2
import dev.termlink.protocol.transport.v1.DirectNominationV2
import dev.termlink.protocol.transport.v1.DirectPathAnswerV1
import dev.termlink.protocol.transport.v1.DirectPathOfferV1
import dev.termlink.protocol.transport.v1.DirectPathResultV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.io.Closeable
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.security.spec.X509EncodedKeySpec
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient

/** Public state is diagnostic only. No state authorizes session or Envelope routing. */
enum class ProductionDirectShadowState {
    DISABLED,
    WAITING_FOR_RELAY,
    WAITING_FOR_NETWORK,
    WAITING_FOR_PEER,
    CONFIGURING,
    GATHERING,
    NEGOTIATING,
    SHADOW_READY_NOT_ROUTED,
    RELAY_ONLY,
    STOPPED,
}

data class ProductionDirectShadowSnapshot(
    val state: ProductionDirectShadowState,
    val reasonCode: String = "",
    val authenticationGeneration: Long,
    val relayGeneration: Int = 0,
    val networkGeneration: Long = 0,
    val pathEpoch: Long = 0,
    val attemptId: UUID? = null,
    val peerDeviceId: UUID? = null,
) {
    /** Shadow mode is deliberately incapable of changing the authoritative Relay route. */
    val directRoutingPermitted: Boolean
        get() = false
}

/**
 * One trusted, online PC authorization copied from server-authoritative device metadata.
 * The caller and runtime own independent copies and must each close their copy.
 */
class ProductionDirectAuthorizedPeer(
    val deviceId: UUID,
    val keyEpoch: Long,
    keyId: String,
    pairwiseKey: ByteArray,
    signingPublicKey: ByteArray,
) : Closeable {
    internal val keyIdBytes = keyId.encodeToByteArray().copyOf()
    internal val pairwiseKey = pairwiseKey.copyOf()
    internal val signingPublicKey = signingPublicKey.copyOf()
    private val closed = AtomicBoolean(false)

    init {
        require(deviceId != NIL_UUID) { "Direct peer device ID is required" }
        require(keyEpoch > 0) { "Direct peer key epoch must be positive" }
        require(keyIdBytes.size in 1..64) { "Direct peer key ID length is invalid" }
        require(this.pairwiseKey.size == 32) { "Direct pairwise key must be 32 bytes" }
        require(this.signingPublicKey.size == 32) { "Direct signing public key must be 32 bytes" }
    }

    internal fun ownedCopy(): ProductionDirectAuthorizedPeer {
        check(!closed.get()) { "Direct peer authorization is closed" }
        return ProductionDirectAuthorizedPeer(
            deviceId,
            keyEpoch,
            keyIdBytes.decodeToString(),
            pairwiseKey,
            signingPublicKey,
        )
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        keyIdBytes.fill(0)
        pairwiseKey.fill(0)
        signingPublicKey.fill(0)
    }
}

internal interface ProductionDirectShadowSignaling : Closeable {
    val incoming: Flow<TransportSignalEnvelopeV1>
    val state: StateFlow<DirectTransportSignalingState>
    val multipairV2Active: Boolean
        get() = false
    fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig>
    fun start(): Boolean
    fun issueTicket(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ): DirectSignalingCallResult<ValidatedDirectPathTicket>
    fun send(envelope: TransportSignalEnvelopeV1): Boolean
}

private class ClientProductionDirectShadowSignaling(
    private val client: DirectTransportSignalingClient,
) : ProductionDirectShadowSignaling {
    override val incoming: Flow<TransportSignalEnvelopeV1> = client.incoming
    override val state: StateFlow<DirectTransportSignalingState> = client.state
    override val multipairV2Active: Boolean get() = client.multipairV2Active
    override fun fetchConfig() = client.fetchConfig()
    override fun start() = client.startSignaling()
    override fun issueTicket(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ) = client.issueTicket(targetDeviceId, connectionId, config)
    override fun send(envelope: TransportSignalEnvelopeV1) = client.send(envelope)
    override fun close() = client.close()
}

internal interface ProductionDirectShadowNative : Closeable {
    val directRoutingPermitted: Boolean
        get() = false
    fun gather(request: DirectNativeGatherRequest): DirectNativeResult<Unit>
    fun markCandidatesExchanged(
        epoch: Long,
        attemptId: ByteArray,
        generation: Long,
        candidates: List<DirectNativeSocketAddress>,
    ): DirectNativeResult<Unit>
    fun nominate(command: ProductionDirectNominationCommand): DirectNativeResult<Unit>
    fun cancel(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit>
    fun networkChanged(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit>
    fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): DirectNativeResult<Unit>
    fun poll(): DirectNativeResult<DirectNativeEvent?>
}

/** Keeps the two directional probe keys explicit across the production/native boundary. */
internal class ProductionDirectNominationCommand(
    val request: DirectNativeNominateRequest,
    localProbeHmacKey: ByteArray,
    peerProbeHmacKey: ByteArray,
) {
    val localProbeHmacKey = localProbeHmacKey.copyOf()
    val peerProbeHmacKey = peerProbeHmacKey.copyOf()

    init {
        require(this.localProbeHmacKey.size == 32 && this.peerProbeHmacKey.size == 32) {
            "Directional Direct probe keys must contain 32 bytes"
        }
    }

    fun destroy() {
        localProbeHmacKey.fill(0)
        peerProbeHmacKey.fill(0)
    }
}

private class JniProductionDirectShadowNative(
    private val handle: DirectNativeHandle,
) : ProductionDirectShadowNative {
    override fun gather(request: DirectNativeGatherRequest) = handle.gather(request)
    override fun markCandidatesExchanged(
        epoch: Long,
        attemptId: ByteArray,
        generation: Long,
        candidates: List<DirectNativeSocketAddress>,
    ) = handle.markCandidatesExchanged(epoch, attemptId, generation, candidates)
    override fun nominate(command: ProductionDirectNominationCommand): DirectNativeResult<Unit> {
        if (!command.request.localSendProbeHmacKey.contentEquals(command.localProbeHmacKey) ||
            !command.request.peerReceiveProbeHmacKey.contentEquals(command.peerProbeHmacKey)
        ) return DirectNativeResult.Failure(DirectNativeEvent.relayOnly(1_002))
        return handle.nominate(command.request)
    }
    override fun cancel(epoch: Long, attemptId: ByteArray) = handle.cancel(epoch, attemptId)
    override fun networkChanged(epoch: Long, attemptId: ByteArray) =
        handle.networkChanged(epoch, attemptId)
    override fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ) = handle.submitSignature(epoch, attemptId, requestId, signature)
    override fun poll() = handle.poll()
    override fun close() = handle.close()
}

/** ABI-v4 surface used only after capability negotiation; it has no Relay or routing handle. */
internal interface ProductionDirectShadowNativeV4 : Closeable {
    val directRoutingPermitted: Boolean
        get() = false
    fun gather(request: DirectNativeGatherRequestV4): DirectNativeResultV4<Unit>
    fun beginChecks(request: DirectNativeBeginChecksRequestV4): DirectNativeResultV4<Unit>
    fun appendCheckPairs(request: DirectNativeAppendCheckPairsRequestV4): DirectNativeResultV4<Unit>
    fun commitNomination(request: DirectNativeCommitNominationRequestV4): DirectNativeResultV4<Unit>
    fun cancel(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<Unit>
    fun networkChanged(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<Unit>
    fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): DirectNativeResultV4<Unit>
    fun poll(): DirectNativeResultV4<DirectNativeEventV4?>
}

private class JniProductionDirectShadowNativeV4(
    private val handle: DirectNativeHandleV4,
) : ProductionDirectShadowNativeV4 {
    override fun gather(request: DirectNativeGatherRequestV4) = handle.gather(request)
    override fun beginChecks(request: DirectNativeBeginChecksRequestV4) = handle.beginChecks(request)
    override fun appendCheckPairs(request: DirectNativeAppendCheckPairsRequestV4) =
        handle.appendCheckPairs(request)
    override fun commitNomination(request: DirectNativeCommitNominationRequestV4) =
        handle.commitNomination(request)
    override fun cancel(epoch: Long, attemptId: ByteArray) = handle.cancel(epoch, attemptId)
    override fun networkChanged(epoch: Long, attemptId: ByteArray) =
        handle.networkChanged(epoch, attemptId)
    override fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ) = handle.submitSignature(epoch, attemptId, requestId, signature)
    override fun poll() = handle.poll()
    override fun close() = handle.close()
}

internal data class CapturedDirectNetwork(
    val token: Any,
    val handle: Long,
    val advertisedAddress: InetAddress,
) {
    init {
        require(handle != 0L) { "Direct Android network handle is required" }
        require(DirectAddressPolicy.isCandidateEndpoint(advertisedAddress, 49_152)) {
            "Direct advertised address is not usable"
        }
    }
}

internal data class PreparedDirectGather(
    val networkHandle: Long,
    val localBind: DirectNativeSocketAddress,
    val advertisedAddress: ByteArray,
    val stunServers: List<DirectNativeSocketAddress>,
)

private fun DirectNativeSocketAddress.toV4() = DirectNativeSocketAddressV4(ipAddress, port)

internal interface ProductionDirectShadowPlatform {
    fun capture(network: Network): CapturedDirectNetwork?
    suspend fun prepare(
        network: CapturedDirectNetwork,
        stunServers: List<String>,
    ): PreparedDirectGather
}

/** Uses only the exact currently validated Android default Network. */
private class AndroidProductionDirectShadowPlatform(
    private val connectivityManager: ConnectivityManager,
) : ProductionDirectShadowPlatform {
    override fun capture(network: Network): CapturedDirectNetwork? {
        if (connectivityManager.activeNetwork != network) return null
        val capabilities = connectivityManager.getNetworkCapabilities(network) ?: return null
        if (!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) ||
            !capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P &&
                !capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED))
        ) return null
        val handle = runCatching { network.networkHandle }.getOrDefault(0L)
        if (handle == 0L) return null
        val address = connectivityManager.getLinkProperties(network)?.linkAddresses
            .orEmpty()
            .map { it.address }
            .filter { DirectAddressPolicy.isCandidateEndpoint(it, 49_152) }
            .sortedWith(
                compareByDescending<InetAddress> { it is Inet4Address }
                    .thenBy { it.hostAddress.orEmpty() },
            )
            .firstOrNull() ?: return null
        return CapturedDirectNetwork(
            token = network,
            handle = handle,
            advertisedAddress = InetAddress.getByAddress(address.address.copyOf()),
        )
    }

    override suspend fun prepare(
        network: CapturedDirectNetwork,
        stunServers: List<String>,
    ): PreparedDirectGather = withContext(Dispatchers.IO) {
        val androidNetwork = network.token as? Network
            ?: throw IllegalStateException("DIRECT_NETWORK_OWNER_INVALID")
        val current = capture(androidNetwork)
            ?: throw IllegalStateException("DIRECT_NETWORK_UNAVAILABLE")
        if (current.handle != network.handle ||
            !current.advertisedAddress.address.contentEquals(network.advertisedAddress.address)
        ) throw IllegalStateException("DIRECT_NETWORK_CHANGED")

        val resolved = ArrayList<DirectNativeSocketAddress>(2)
        for (value in stunServers) {
            if (resolved.size >= 2) break
            val endpoint = parseStunEndpoint(value)
            if (endpoint.second < 1_024) throw IllegalArgumentException("DIRECT_STUN_PORT_INVALID")
            val address = androidNetwork.getAllByName(endpoint.first)
                .asSequence()
                .filter { candidate -> sameFamily(candidate, current.advertisedAddress) }
                .firstOrNull { candidate ->
                    DirectAddressPolicy.isCandidateEndpoint(candidate, endpoint.second)
                } ?: continue
            val native = DirectNativeSocketAddress(address.address, endpoint.second)
            if (native !in resolved) resolved += native
        }
        val wildcard = if (current.advertisedAddress is Inet4Address) ByteArray(4) else ByteArray(16)
        PreparedDirectGather(
            networkHandle = current.handle,
            localBind = DirectNativeSocketAddress(wildcard, 0),
            advertisedAddress = current.advertisedAddress.address.copyOf(),
            stunServers = resolved,
        )
    }

    private fun parseStunEndpoint(value: String): Pair<String, Int> {
        require(value.startsWith("stun:") && value.none(Char::isWhitespace)) {
            "DIRECT_STUN_CONFIG_INVALID"
        }
        val authority = value.removePrefix("stun:")
        val (host, portText) = if (authority.startsWith("[")) {
            val closing = authority.indexOf(']')
            require(closing > 1 && closing + 2 <= authority.lastIndex && authority[closing + 1] == ':') {
                "DIRECT_STUN_CONFIG_INVALID"
            }
            authority.substring(1, closing) to authority.substring(closing + 2)
        } else {
            val separator = authority.lastIndexOf(':')
            require(separator > 0 && ':' !in authority.substring(0, separator)) {
                "DIRECT_STUN_CONFIG_INVALID"
            }
            authority.substring(0, separator) to authority.substring(separator + 1)
        }
        val port = portText.toIntOrNull()
        require(host.length in 1..253 && port != null && port in 1..65_535) {
            "DIRECT_STUN_CONFIG_INVALID"
        }
        return host to port
    }

    private fun sameFamily(left: InetAddress, right: InetAddress): Boolean =
        (left is Inet4Address && right is Inet4Address) ||
            (left is Inet6Address && right is Inet6Address)
}

internal fun interface ProductionDirectShadowDelay {
    suspend fun await(milliseconds: Long)
}

/**
 * Production Direct shadow controller. Its constructor intentionally accepts only a read-only
 * Relay event flow: there is no Relay socket, disconnect, reconnect, session, renderer, history,
 * snapshot, ACK, journal, or logout capability anywhere in this object graph.
 */
class ProductionDirectShadowRuntime internal constructor(
    private val enabled: Boolean,
    private val authenticationGeneration: Long,
    private val localDeviceId: UUID,
    private val identity: DeviceIdentity,
    relayEvents: Flow<ProductionRealtimeSessionConnectionEvent>,
    private val signalingFactory: (nativeV4Available: Boolean) -> ProductionDirectShadowSignaling,
    private val nativeFactory: () -> ProductionDirectShadowNative,
    private val nativeV4Factory: () -> ProductionDirectShadowNativeV4? = { null },
    private val platform: ProductionDirectShadowPlatform,
    parentScope: CoroutineScope,
    private val clockMillis: () -> Long,
    private val random: SecureRandom,
    private val pollDelay: ProductionDirectShadowDelay,
    private val retryDelay: ProductionDirectShadowDelay,
    private val uuidSource: () -> UUID,
) : Closeable {
    constructor(
        enabled: Boolean = false,
        authenticationGeneration: Long,
        rootUrl: String,
        tokenProvider: AccessTokenProvider,
        localDeviceId: UUID,
        identity: DeviceIdentity,
        relayEvents: Flow<ProductionRealtimeSessionConnectionEvent>,
        connectivityManager: ConnectivityManager,
        parentScope: CoroutineScope,
        httpClient: OkHttpClient = OkHttpClient(),
        allowCleartextForLocalTest: Boolean = false,
    ) : this(
        enabled = enabled,
        authenticationGeneration = authenticationGeneration,
        localDeviceId = localDeviceId,
        identity = identity,
        relayEvents = relayEvents,
        signalingFactory = { nativeV4Available ->
            ClientProductionDirectShadowSignaling(
                DirectTransportSignalingClient(
                    rootUrl = rootUrl,
                    tokenProvider = tokenProvider,
                    deviceId = localDeviceId,
                    identity = identity,
                    role = "android",
                    ticketSignatureVerifier = DirectSignalingTicketSignatureVerifier(::verifyEd25519),
                    feature = DirectTransportSignalingFeature(enabled),
                    multipairV2Enabled = nativeV4Available,
                    httpClient = httpClient,
                    parentScope = parentScope,
                    allowCleartextForLocalTest = allowCleartextForLocalTest,
                ),
            )
        },
        nativeFactory = {
            val created = JniDirectNetworkCore().create(DirectNativeCreateConfig(enabled = true))
            val handle = (created as? DirectNativeResult.Success)?.value
                ?: throw IllegalStateException("DIRECT_NATIVE_UNAVAILABLE")
            JniProductionDirectShadowNative(handle)
        },
        nativeV4Factory = {
            val created = JniDirectNetworkCoreV4().create(DirectNativeCreateConfigV4(enabled = true))
            val handle = (created as? DirectNativeResultV4.Success)?.value
            handle?.let(::JniProductionDirectShadowNativeV4)
        },
        platform = AndroidProductionDirectShadowPlatform(connectivityManager),
        parentScope = parentScope,
        clockMillis = System::currentTimeMillis,
        random = SecureRandom(),
        pollDelay = ProductionDirectShadowDelay(::delay),
        retryDelay = ProductionDirectShadowDelay(::delay),
        uuidSource = UUID::randomUUID,
    )

    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val lock = Any()
    private val closed = AtomicBoolean(false)
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private var started = false
    private var relayGeneration = 0
    private var networkGeneration = 0L
    private var network: CapturedDirectNetwork? = null
    private var peerGeneration = 0L
    private var peers: List<ProductionDirectAuthorizedPeer> = emptyList()
    private var active: Attempt? = null
    private var attemptJob: Job? = null
    private var retryJob: Job? = null
    private var retryAttempt = 0
    private val mutableSnapshot = MutableStateFlow(
        ProductionDirectShadowSnapshot(
            state = if (enabled) ProductionDirectShadowState.WAITING_FOR_RELAY
            else ProductionDirectShadowState.DISABLED,
            reasonCode = if (enabled) "" else "FEATURE_DISABLED",
            authenticationGeneration = authenticationGeneration,
        ),
    )

    val snapshot: StateFlow<ProductionDirectShadowSnapshot> = mutableSnapshot.asStateFlow()
    val directRoutingPermitted: Boolean
        get() = false

    private val relayJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
        relayEvents.collect { event ->
            observeRelayConnected(
                authenticationGeneration,
                if (event.state is RealtimeState.Connected) event.connectedGeneration else 0,
            )
        }
    }
    private val coordinator = scope.launch(start = CoroutineStart.UNDISPATCHED) {
        for (ignored in wake) reconcile()
    }

    init {
        require(authenticationGeneration > 0) { "Direct authentication generation must be positive" }
        require(localDeviceId != NIL_UUID) { "Direct local device ID is required" }
    }

    fun start(): Boolean = synchronized(lock) {
        if (!enabled || closed.get() || started) return@synchronized false
        started = true
        wake.trySend(Unit)
        true
    }

    /** Copies only peer authority owned by this exact authentication generation. */
    fun replaceAuthorizedPeers(
        expectedAuthenticationGeneration: Long,
        replacements: List<ProductionDirectAuthorizedPeer>,
    ): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || closed.get()) return false
        val owned = runCatching {
            replacements.sortedBy(ProductionDirectAuthorizedPeer::deviceId).map { it.ownedCopy() }
        }.getOrElse {
            return false
        }
        val retired: List<ProductionDirectAuthorizedPeer>
        val current: Attempt?
        synchronized(lock) {
            if (closed.get()) {
                owned.forEach(ProductionDirectAuthorizedPeer::close)
                return false
            }
            retired = peers
            peers = owned
            peerGeneration = nextGeneration(peerGeneration)
            retryAttempt = 0
            current = active
            publishGateStateLocked()
        }
        retired.forEach(ProductionDirectAuthorizedPeer::close)
        current?.retire("DIRECT_AUTHORIZATION_CHANGED", networkChanged = false)
        attemptJob?.cancel()
        wake.trySend(Unit)
        return true
    }

    /** Captures and owns only the validated current default Android Network. */
    fun observeNetwork(
        expectedAuthenticationGeneration: Long,
        value: Network?,
    ): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || closed.get()) return false
        val captured = value?.let(platform::capture)
        return observeCapturedNetwork(expectedAuthenticationGeneration, captured)
    }

    internal fun observeCapturedNetwork(
        expectedAuthenticationGeneration: Long,
        captured: CapturedDirectNetwork?,
    ): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || closed.get()) return false
        val current: Attempt?
        synchronized(lock) {
            if (sameNetwork(network, captured)) return true
            network = captured
            networkGeneration = nextGeneration(networkGeneration)
            retryAttempt = 0
            current = active
            publishGateStateLocked(
                if (current != null) "DIRECT_NETWORK_CHANGED" else "",
            )
        }
        current?.retire("DIRECT_NETWORK_CHANGED", networkChanged = true)
        attemptJob?.cancel()
        wake.trySend(Unit)
        return true
    }

    internal fun observeRelayConnected(
        expectedAuthenticationGeneration: Long,
        connectedGeneration: Int,
    ): Boolean {
        if (expectedAuthenticationGeneration != authenticationGeneration || connectedGeneration < 0 ||
            closed.get()
        ) return false
        val current: Attempt?
        synchronized(lock) {
            if (relayGeneration == connectedGeneration) return true
            relayGeneration = connectedGeneration
            retryAttempt = 0
            current = active
            publishGateStateLocked(if (connectedGeneration == 0) "RELAY_UNAVAILABLE" else "")
        }
        if (connectedGeneration == 0) {
            current?.retire("RELAY_UNAVAILABLE", networkChanged = false)
            attemptJob?.cancel()
        }
        wake.trySend(Unit)
        return true
    }

    private fun reconcile() {
        val gate = synchronized(lock) {
            if (!started || closed.get() || attemptJob?.isActive == true || retryJob?.isActive == true) {
                return
            }
            captureGateLocked() ?: return
        }
        val job = scope.launch(start = CoroutineStart.LAZY) {
            val retry = try {
                runAttempt(gate)
                false
            } catch (cancelled: CancellationException) {
                false
            } catch (_: Throwable) {
                failCurrent("DIRECT_SHADOW_FAILED")
                true
            } finally { Unit }
            if (retry && !closed.get()) scheduleRetry()
        }
        job.invokeOnCompletion {
            synchronized(lock) {
                if (attemptJob === job) attemptJob = null
            }
        }
        synchronized(lock) {
            if (closed.get() || !gateStillCurrentLocked(gate) || attemptJob?.isActive == true) {
                job.cancel()
            } else {
                attemptJob = job
                job.start()
            }
        }
    }

    private suspend fun runAttempt(gate: Gate) {
        publishIfCurrent(gate, ProductionDirectShadowState.CONFIGURING)
        var availableNativeV4 = runCatching(nativeV4Factory).getOrNull()
        val signaling = try {
            signalingFactory(availableNativeV4 != null)
        } catch (failure: Throwable) {
            runCatching { availableNativeV4?.close() }
            gate.peer.close()
            throw failure
        }
        var peer: ProductionDirectAuthorizedPeer? = gate.peer
        var attempt: Attempt? = null
        try {
            val config = (signaling.fetchConfig() as? DirectSignalingCallResult.Success)?.value
            if (config == null || !validShadowConfig(config)) {
                publishIfCurrent(gate, ProductionDirectShadowState.RELAY_ONLY, "DIRECT_CONFIG_REJECTED")
                scheduleRetry()
                return
            }
            val useMultipairV2 = availableNativeV4 != null && signaling.multipairV2Active &&
                DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY in config.capabilities
            if (!useMultipairV2) {
                runCatching { availableNativeV4?.close() }
                availableNativeV4 = null
            }
            if (!signaling.start()) {
                publishIfCurrent(gate, ProductionDirectShadowState.RELAY_ONLY, "DIRECT_SIGNAL_START_FAILED")
                scheduleRetry()
                return
            }
            ensureGateCurrent(gate)
            val selectedPeer = requireNotNull(peer)
            val connectionId = nonZeroVersion4Uuid()
            val ticketResult = signaling.issueTicket(selectedPeer.deviceId, connectionId, config)
            val issued = (ticketResult as? DirectSignalingCallResult.Success)?.value
            if (issued == null) {
                publishIfCurrent(gate, ProductionDirectShadowState.RELAY_ONLY, "DIRECT_TICKET_REJECTED")
                scheduleRetry()
                return
            }
            val prepared = platform.prepare(gate.network, config.stunServers)
            ensureGateCurrent(gate)
            val attemptId = nonZeroVersion4Uuid()
            val ticketWire = issued.wireBytes.copyOf()
            val created = Attempt(
                gate = gate,
                peer = selectedPeer,
                signaling = signaling,
                connectionId = connectionId,
                attemptId = attemptId,
                ticket = issued.protobuf,
                ticketWire = ticketWire,
                config = config,
                prepared = prepared,
                multipairV2 = useMultipairV2,
            )
            peer = null
            attempt = created
            synchronized(lock) {
                if (!gateStillCurrentLocked(gate) || active != null) throw CancellationException()
                active = created
            }
            if (useMultipairV2) {
                val native = requireNotNull(availableNativeV4)
                availableNativeV4 = null
                created.nativeV4.set(native)
                if (native.directRoutingPermitted) {
                    throw DirectAttemptFailure("DIRECT_ROUTING_FORBIDDEN")
                }
                val gather = DirectNativeGatherRequestV4(
                    epoch = created.ticket.pathEpoch,
                    attemptId = created.attemptBytes,
                    networkHandle = prepared.networkHandle,
                    localBind = prepared.localBind.toV4(),
                    advertisedLocalIp = prepared.advertisedAddress,
                    networkCost = 0,
                    candidateExpiresAtMillis = created.ticket.expiresAtMs,
                    stunServers = prepared.stunServers.map(DirectNativeSocketAddress::toV4),
                    gatherTimeoutMillis = GATHER_TIMEOUT_MILLIS,
                )
                if (native.gather(gather) !is DirectNativeResultV4.Success) {
                    throw DirectAttemptFailure("DIRECT_NATIVE_V4_GATHER_FAILED")
                }
                publishAttempt(created, ProductionDirectShadowState.GATHERING)
                withTimeout(attemptTimeoutMillis(created)) {
                    driveAttemptV4(created, native)
                }
            } else {
                val native = nativeFactory()
                created.native.set(native)
                if (native.directRoutingPermitted) throw DirectAttemptFailure("DIRECT_ROUTING_FORBIDDEN")
                val gather = DirectNativeGatherRequest(
                    epoch = created.ticket.pathEpoch,
                    attemptId = created.attemptBytes,
                    networkHandle = prepared.networkHandle,
                    localBind = prepared.localBind,
                    advertisedLocalIp = prepared.advertisedAddress,
                    stunServers = prepared.stunServers,
                    gatherTimeoutMillis = GATHER_TIMEOUT_MILLIS,
                )
                if (native.gather(gather) !is DirectNativeResult.Success) {
                    throw DirectAttemptFailure("DIRECT_NATIVE_GATHER_FAILED")
                }
                publishAttempt(created, ProductionDirectShadowState.GATHERING)
                withTimeout(attemptTimeoutMillis(created)) {
                    driveAttempt(created, native)
                }
            }
        } catch (_: TimeoutCancellationException) {
            attempt?.retire("DIRECT_ATTEMPT_TIMEOUT", networkChanged = false)
            publishFailureIfCurrent(gate, "DIRECT_ATTEMPT_TIMEOUT")
            scheduleRetry()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: DirectAttemptFailure) {
            attempt?.retire(failure.reason, networkChanged = false)
            publishFailureIfCurrent(gate, failure.reason)
            scheduleRetry()
        } catch (_: Throwable) {
            attempt?.retire("DIRECT_SHADOW_FAILED", networkChanged = false)
            publishFailureIfCurrent(gate, "DIRECT_SHADOW_FAILED")
            scheduleRetry()
        } finally {
            if (attempt == null) {
                runCatching { availableNativeV4?.close() }
                runCatching(signaling::close)
                peer?.close()
            } else if (!attempt.shadowReady.get()) {
                attempt.retire("DIRECT_ATTEMPT_RETIRED", networkChanged = false)
                synchronized(lock) { if (active === attempt) active = null }
            }
        }
    }

    private suspend fun driveAttempt(
        attempt: Attempt,
        native: ProductionDirectShadowNative,
    ) {
        val incoming = Channel<TransportSignalEnvelopeV1>(
            capacity = MAX_INCOMING_SIGNALS,
            onBufferOverflow = BufferOverflow.SUSPEND,
        )
        val pumpFault = AtomicReference<String?>(null)
        val pump = scope.launch {
            try {
                attempt.signaling.incoming.collect { envelope ->
                    if (!incoming.trySend(envelope).isSuccess) {
                        pumpFault.compareAndSet(null, "DIRECT_SIGNAL_QUEUE_OVERFLOW")
                        return@collect
                    }
                }
                pumpFault.compareAndSet(null, "DIRECT_SIGNAL_CLOSED")
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Throwable) {
                pumpFault.compareAndSet(null, "DIRECT_SIGNAL_FAILED")
            }
        }
        try {
            while (scope.isActive && !attempt.retired.get()) {
                ensureAttemptCurrent(attempt)
                pumpFault.get()?.let { throw DirectAttemptFailure(it) }
                if (!attempt.shadowReady.get()) {
                    terminalSignalingReason(attempt.signaling.state.value)?.let {
                        throw DirectAttemptFailure(it)
                    }
                }
                repeat(MAX_NATIVE_EVENTS_PER_POLL) {
                    val result = native.poll()
                    val event = when (result) {
                        is DirectNativeResult.Success -> result.value ?: return@repeat
                        is DirectNativeResult.Failure ->
                            throw DirectAttemptFailure("DIRECT_NATIVE_POLL_FAILED")
                    }
                    processNativeEvent(attempt, native, event)
                }
                if (!attempt.offerSent && attempt.identityReady && attempt.candidatesReady) {
                    sendOffer(attempt, native)
                }
                while (!attempt.shadowReady.get()) {
                    val envelope = incoming.tryReceive().getOrNull() ?: break
                    processIncomingSignal(attempt, native, envelope)
                }
                if (attempt.shadowReady.get()) return
                pollDelay.await(POLL_INTERVAL_MILLIS)
            }
        } finally {
            pump.cancelAndJoin()
            incoming.cancel()
        }
    }

    private suspend fun driveAttemptV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
    ) {
        val incoming = Channel<TransportSignalEnvelopeV1>(
            capacity = MAX_INCOMING_SIGNALS,
            onBufferOverflow = BufferOverflow.SUSPEND,
        )
        val pumpFault = AtomicReference<String?>(null)
        val pump = scope.launch {
            try {
                attempt.signaling.incoming.collect { envelope ->
                    if (!incoming.trySend(envelope).isSuccess) {
                        pumpFault.compareAndSet(null, "DIRECT_SIGNAL_QUEUE_OVERFLOW")
                        return@collect
                    }
                }
                pumpFault.compareAndSet(null, "DIRECT_SIGNAL_CLOSED")
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Throwable) {
                pumpFault.compareAndSet(null, "DIRECT_SIGNAL_FAILED")
            }
        }
        try {
            while (scope.isActive && !attempt.retired.get()) {
                ensureAttemptCurrent(attempt)
                pumpFault.get()?.let { throw DirectAttemptFailure(it) }
                if (!attempt.shadowReady.get()) {
                    terminalSignalingReason(attempt.signaling.state.value)?.let {
                        throw DirectAttemptFailure(it)
                    }
                }
                repeat(MAX_NATIVE_EVENTS_PER_POLL) {
                    val result = native.poll()
                    val event = when (result) {
                        is DirectNativeResultV4.Success -> result.value ?: return@repeat
                        is DirectNativeResultV4.Failure ->
                            throw DirectAttemptFailure("DIRECT_NATIVE_V4_POLL_FAILED")
                    }
                    processNativeEventV4(attempt, native, event)
                }
                if (!attempt.offerSent && attempt.identityReady &&
                    attempt.localCandidatesV4.isNotEmpty()
                ) {
                    sendOfferV4(attempt)
                }
                while (!attempt.shadowReady.get()) {
                    val envelope = incoming.tryReceive().getOrNull() ?: break
                    processIncomingSignalV4(attempt, native, envelope)
                }
                if (attempt.offerSent && attempt.answerReceived) {
                    advanceCheckPlanV4(attempt, native)
                }
                maybeNominateV4(attempt)
                maybeFinishMultipairV4(attempt)
                if (attempt.shadowReady.get()) return
                pollDelay.await(POLL_INTERVAL_MILLIS)
            }
        } finally {
            pump.cancelAndJoin()
            incoming.cancel()
        }
    }

    private fun processNativeEventV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
        event: DirectNativeEventV4,
    ) {
        try {
            if (attempt.lastNativeGeneration == Long.MAX_VALUE ||
                event.generation != attempt.lastNativeGeneration + 1
            ) {
                throw DirectAttemptFailure("DIRECT_NATIVE_V4_EVENT_STALE")
            }
            attempt.lastNativeGeneration = event.generation
            if (event.kind == DirectNativeEventKindV4.IGNORED_STALE) return
            if (event.epoch != attempt.ticket.pathEpoch ||
                !constantTimeEquals(event.attemptId, attempt.attemptBytes)
            ) {
                throw DirectAttemptFailure("DIRECT_NATIVE_V4_EVENT_OWNER_MISMATCH")
            }
            when (event.kind) {
                DirectNativeEventKindV4.IDENTITY_READY -> {
                    if (attempt.identityReady) {
                        throw DirectAttemptFailure("DIRECT_V4_IDENTITY_EVENT_REJECTED")
                    }
                    val certificate = decodeOpaqueV4(event.payload)
                        ?: throw DirectAttemptFailure("DIRECT_V4_IDENTITY_EVENT_REJECTED")
                    attempt.localCertificateSha256 = sha256(certificate)
                    certificate.fill(0)
                    attempt.identityReady = true
                }
                DirectNativeEventKindV4.CANDIDATE_BATCH -> {
                    val batch = decodeCandidateBatchV4(event.payload, attempt)
                        ?: throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_EVENT_REJECTED")
                    acceptLocalCandidateBatchV4(attempt, batch)
                    if (!attempt.offerSent && attempt.identityReady) sendOfferV4(attempt)
                    else if (attempt.offerSent) {
                        sendCandidateUpdateV4(attempt, batch.candidates, batch.complete)
                    }
                }
                DirectNativeEventKindV4.PAIR_VALIDATED -> {
                    val validated = decodePairValidatedV4(event.payload, attempt)
                        ?: throw DirectAttemptFailure("DIRECT_V4_PAIR_EVENT_REJECTED")
                    val submitted = attempt.submittedPairsV4.getOrNull(validated.rank - 1)
                    if (submitted == null || !samePairV4(submitted, validated)) {
                        throw DirectAttemptFailure("DIRECT_V4_PAIR_EVENT_REJECTED")
                    }
                    if (!attempt.validatedPairRanks.add(validated.rank)) {
                        throw DirectAttemptFailure("DIRECT_V4_PAIR_EVENT_DUPLICATE")
                    }
                }
                DirectNativeEventKindV4.CHECKS_COMPLETE -> {
                    if (attempt.checksComplete) {
                        throw DirectAttemptFailure("DIRECT_V4_CHECKS_EVENT_DUPLICATE")
                    }
                    val completed = decodeChecksCompleteV4(event.payload, attempt)
                        ?: throw DirectAttemptFailure("DIRECT_V4_CHECKS_EVENT_REJECTED")
                    if (completed.total != attempt.submittedPairsV4.size ||
                        completed.validated != attempt.validatedPairRanks.size ||
                        completed.exhausted + completed.validated != completed.total
                    ) throw DirectAttemptFailure("DIRECT_V4_CHECKS_EVENT_REJECTED")
                    attempt.checksComplete = true
                }
                DirectNativeEventKindV4.NOMINATION_READY -> {
                    if (attempt.nominationReadyV4) {
                        throw DirectAttemptFailure("DIRECT_V4_NOMINATION_EVENT_DUPLICATE")
                    }
                    val ready = decodeNominationReadyV4(event.payload, attempt)
                        ?: throw DirectAttemptFailure("DIRECT_V4_NOMINATION_EVENT_REJECTED")
                    val winner = attempt.winningPairV4
                    if (!attempt.nominationCommitted || winner == null ||
                        !sameIdentifier(ready.nominationId, attempt.nominationIdV4) ||
                        !samePairV4(ready.pair, winner)
                    ) throw DirectAttemptFailure("DIRECT_V4_NOMINATION_EVENT_REJECTED")
                    attempt.nominationReadyV4 = true
                }
                DirectNativeEventKindV4.SIGN_REQUEST -> {
                    if (!attempt.nominationCommitted || event.requestId <= 0 ||
                        !attempt.signedRequests.add(event.requestId)
                    ) throw DirectAttemptFailure("DIRECT_V4_SIGN_REQUEST_REJECTED")
                    val transcript = decodeOpaqueV4(event.payload)
                        ?: throw DirectAttemptFailure("DIRECT_V4_SIGN_REQUEST_REJECTED")
                    val signature = identity.sign(transcript)
                    transcript.fill(0)
                    try {
                        if (native.submitSignature(
                                event.epoch,
                                attempt.attemptBytes,
                                event.requestId,
                                signature,
                            ) !is DirectNativeResultV4.Success
                        ) throw DirectAttemptFailure("DIRECT_V4_SIGNATURE_REJECTED")
                    } finally {
                        signature.fill(0)
                    }
                }
                DirectNativeEventKindV4.SHADOW_READY -> {
                    val ready = decodeShadowReadyV4(event.payload, attempt)
                        ?: throw DirectAttemptFailure("DIRECT_V4_SHADOW_READY_REJECTED")
                    val winner = attempt.winningPairV4
                    if (!attempt.nominationReadyV4 || winner == null ||
                        !sameIdentifier(ready.nominationId, attempt.nominationIdV4) ||
                        !samePairV4(ready.pair, winner) || ready.binding.all { it == 0.toByte() }
                    ) throw DirectAttemptFailure("DIRECT_V4_SHADOW_READY_REJECTED")
                    attempt.localPathBindingV4 = ready.binding.copyOf()
                    sendNominationResultV4(attempt, ready.binding)
                }
                DirectNativeEventKindV4.DIRECT_FAILED ->
                    throw DirectAttemptFailure("DIRECT_NATIVE_V4_FAILED")
                DirectNativeEventKindV4.STATE_CHANGED -> Unit
                DirectNativeEventKindV4.SYNTHETIC_FAILURE,
                DirectNativeEventKindV4.UNKNOWN,
                -> throw DirectAttemptFailure("DIRECT_NATIVE_V4_EVENT_REJECTED")
                DirectNativeEventKindV4.IGNORED_STALE -> Unit
            }
        } finally {
            event.payload.fill(0)
            event.attemptId.fill(0)
            event.endpoint?.ipAddress?.fill(0)
        }
    }

    private fun sendOfferV4(attempt: Attempt) {
        if (attempt.localCandidatesV4.isEmpty() || attempt.offerSent) {
            throw DirectAttemptFailure("DIRECT_V4_OFFER_STATE_REJECTED")
        }
        val offer = DirectPathOfferV1.newBuilder()
            .setTicket(attempt.ticket)
            .setQuicCertificateSha256(ByteString.copyFrom(attempt.localCertificateSha256))
            .addAllCandidates(attempt.localCandidatesV4.map(::candidateToSignalV4))
            .setGatheringComplete(attempt.localGatheringCompleteV4)
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setPathOffer(offer)
            .build()
        if (!sendEncrypted(attempt, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER, payload)) {
            throw DirectAttemptFailure("DIRECT_V4_OFFER_SEND_FAILED")
        }
        attempt.offerSequence = attempt.localSequence
        attempt.offerSent = true
        publishAttempt(attempt, ProductionDirectShadowState.NEGOTIATING)
    }

    private fun sendCandidateUpdateV4(
        attempt: Attempt,
        additions: List<DirectNativeCandidateV4>,
        complete: Boolean,
    ) {
        if (additions.isEmpty() && !complete) {
            throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_UPDATE_REJECTED")
        }
        val update = DirectCandidateUpdateV1.newBuilder()
            .addAllCandidates(additions.map(::candidateToSignalV4))
            .setGatheringComplete(complete)
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setCandidateUpdate(update)
            .build()
        if (!sendEncrypted(
                attempt,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CANDIDATE_UPDATE,
                payload,
            )
        ) throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_UPDATE_SEND_FAILED")
    }

    private fun processIncomingSignalV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
        envelope: TransportSignalEnvelopeV1,
    ) {
        if (!matchesAttempt(attempt, envelope)) return
        if (envelope.signalSeq != attempt.lastRemoteSequence + 1 ||
            envelope.signalAck < attempt.lastRemoteAck || envelope.signalAck > attempt.localSequence
        ) throw DirectAttemptFailure("DIRECT_V4_SIGNAL_CURSOR_REJECTED")
        val payload = decryptPayloadV4(attempt, envelope)
            ?: throw DirectAttemptFailure("DIRECT_V4_SIGNAL_PAYLOAD_REJECTED")
        attempt.lastRemoteSequence = envelope.signalSeq
        attempt.lastRemoteAck = envelope.signalAck
        when (envelope.signalType) {
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_ANSWER -> {
                if (attempt.answerReceived || !attempt.offerSent || !payload.hasPathAnswer()) {
                    throw DirectAttemptFailure("DIRECT_V4_ANSWER_REJECTED")
                }
                val answer = payload.pathAnswer
                if (!validAnswerV4(attempt, answer)) {
                    throw DirectAttemptFailure("DIRECT_V4_ANSWER_REJECTED")
                }
                attempt.peerCertificateSha256V4 = answer.quicCertificateSha256.toByteArray()
                mergeRemoteCandidatesV4(attempt, answer.candidatesList, answer.gatheringComplete)
                attempt.answerReceived = true
            }
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CANDIDATE_UPDATE -> {
                if (!attempt.answerReceived || attempt.remoteGatheringCompleteV4 ||
                    !payload.hasCandidateUpdate()
                ) throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_UPDATE_REJECTED")
                val update = payload.candidateUpdate
                if (update.candidatesCount == 0 && !update.gatheringComplete) {
                    throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_UPDATE_REJECTED")
                }
                mergeRemoteCandidatesV4(attempt, update.candidatesList, update.gatheringComplete)
            }
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2 -> {
                val ack = payload.nominationAckV2
                acceptNominationAckV4(attempt, native, ack)
            }
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2 -> {
                val result = payload.nominationResultV2
                acceptNominationResultV4(attempt, result)
            }
            else -> throw DirectAttemptFailure("DIRECT_V4_SIGNAL_TYPE_REJECTED")
        }
    }

    private fun decryptPayloadV4(
        attempt: Attempt,
        envelope: TransportSignalEnvelopeV1,
    ): TransportSignalPayloadV1? {
        val plaintext = runCatching {
            DirectTransportCrypto.decryptSignal(attempt.peer.pairwiseKey, envelope)
        }.getOrNull() ?: return null
        return try {
            val payload = runCatching { TransportSignalPayloadV1.parseFrom(plaintext) }.getOrNull()
                ?: return null
            if (payload.formatVersion != PROTOCOL_VERSION ||
                !signalTypeMatchesPayloadV4(envelope.signalType, payload.signalCase)
            ) return null
            val canonical = canonicalPayloadV4(payload).toByteArray()
            try {
                payload.takeIf { plaintext.contentEquals(canonical) }
            } finally {
                canonical.fill(0)
            }
        } finally {
            plaintext.fill(0)
        }
    }

    private fun signalTypeMatchesPayloadV4(
        type: TransportSignalTypeV1,
        signalCase: TransportSignalPayloadV1.SignalCase,
    ): Boolean = when (type) {
        TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_ANSWER ->
            signalCase == TransportSignalPayloadV1.SignalCase.PATH_ANSWER
        TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CANDIDATE_UPDATE ->
            signalCase == TransportSignalPayloadV1.SignalCase.CANDIDATE_UPDATE
        TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2 ->
            signalCase == TransportSignalPayloadV1.SignalCase.NOMINATION_ACK_V2
        TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2 ->
            signalCase == TransportSignalPayloadV1.SignalCase.NOMINATION_RESULT_V2
        else -> false
    }

    private fun canonicalPayloadV4(value: TransportSignalPayloadV1): TransportSignalPayloadV1 {
        val builder = TransportSignalPayloadV1.newBuilder().setFormatVersion(PROTOCOL_VERSION)
        when (value.signalCase) {
            TransportSignalPayloadV1.SignalCase.PATH_ANSWER -> builder.pathAnswer = value.pathAnswer
            TransportSignalPayloadV1.SignalCase.CANDIDATE_UPDATE ->
                builder.candidateUpdate = value.candidateUpdate
            TransportSignalPayloadV1.SignalCase.NOMINATION_ACK_V2 ->
                builder.nominationAckV2 = value.nominationAckV2
            TransportSignalPayloadV1.SignalCase.NOMINATION_RESULT_V2 ->
                builder.nominationResultV2 = value.nominationResultV2
            else -> return TransportSignalPayloadV1.getDefaultInstance()
        }
        return builder.build()
    }

    private fun advanceCheckPlanV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
    ) {
        if (attempt.nominationV2Sent) return
        val ordered = selectCandidatePairs(
            attempt.localCandidatesV4.map(::candidateToSignalV4),
            attempt.remoteCandidatesV4,
        )
        if (ordered.isEmpty()) {
            if (attempt.localGatheringCompleteV4 && attempt.remoteGatheringCompleteV4) {
                throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_PAIR_UNAVAILABLE")
            }
            return
        }
        val existing = attempt.submittedPairsV4
        if (existing.any { submitted -> ordered.none { sameSelectablePairV4(submitted, it) } }) {
            throw DirectAttemptFailure("DIRECT_V4_PAIR_ORDER_CHANGED")
        }
        // Candidate trickle can reveal a higher-preference pair after lower-preference pairs have
        // already entered the native single-socket pump. Keep that submitted prefix immutable and
        // rank only previously unseen pairs in their current deterministic order. Reordering the
        // prefix would either corrupt native rank ownership or require replacing the socket owner.
        val records = ordered
            .filter { candidate -> existing.none { sameSelectablePairV4(it, candidate) } }
            .take((MAX_CANDIDATE_PAIRS - existing.size).coerceAtLeast(0))
            .mapIndexed { index, pair ->
                val local = attempt.localCandidatesV4.singleOrNull {
                    sameIdentifier(it.candidateId, pair.local.wire.candidateId.toByteArray())
                } ?: throw DirectAttemptFailure("DIRECT_V4_LOCAL_CANDIDATE_MISSING")
                DirectNativeCandidatePairV4(
                    rank = existing.size + index + 1,
                    initiator = local,
                    responder = signalToNativeCandidateV4(pair.remote.wire),
                )
            }
        val firstIsSameLan = ordered.first().isSameLanHostPair()
        val collectionComplete = attempt.localGatheringCompleteV4 && attempt.remoteGatheringCompleteV4
        if (!attempt.checksStartedV4 && !firstIsSameLan && !collectionComplete) return

        var offset = 0
        if (!attempt.checksStartedV4) {
            initializeCheckRoundV4(attempt)
            val count = minOf(MAX_SIGNAL_CANDIDATES, records.size)
            val complete = collectionComplete && count == records.size
            submitCheckBatchV4(attempt, native, records.subList(0, count), complete, first = true)
            offset = count
        }
        while (offset < records.size) {
            val end = minOf(offset + MAX_SIGNAL_CANDIDATES, records.size)
            val complete = collectionComplete && end == records.size
            submitCheckBatchV4(attempt, native, records.subList(offset, end), complete, first = false)
            offset = end
        }
        if (collectionComplete && !attempt.checkPlanCompleteV4) {
            submitCheckBatchV4(attempt, native, emptyList(), complete = true, first = false)
        }
    }

    private fun initializeCheckRoundV4(attempt: Attempt) {
        if (attempt.checksStartedV4) return
        attempt.checkRoundIdV4 = randomIdentifier()
        attempt.connectivityNonceV4 = randomIdentifier()
    }

    private fun submitCheckBatchV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
        pairs: List<DirectNativeCandidatePairV4>,
        complete: Boolean,
        first: Boolean,
    ) {
        if (pairs.size > MAX_SIGNAL_CANDIDATES || pairs.isEmpty() && !complete) {
            throw DirectAttemptFailure("DIRECT_V4_PAIR_BATCH_REJECTED")
        }
        val expectedRank = attempt.submittedPairsV4.size + 1
        if (pairs.indices.any { pairs[it].rank != expectedRank + it }) {
            throw DirectAttemptFailure("DIRECT_V4_PAIR_RANK_REJECTED")
        }
        val sequence = attempt.lastPairBatchV4 + 1
        val batch = DirectConnectivityCheckBatchV2.newBuilder()
            .setCheckRoundId(ByteString.copyFrom(attempt.checkRoundIdV4))
            .setConnectivityNonce(ByteString.copyFrom(attempt.connectivityNonceV4))
            .setBatchSeq(sequence)
            .addAllPairs(pairs.map(::pairToSignalV4))
            .setPlanComplete(complete)
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setConnectivityCheckBatchV2(batch)
            .build()
        if (!sendEncrypted(
                attempt,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
                payload,
            )
        ) throw DirectAttemptFailure("DIRECT_V4_CHECK_BATCH_SEND_FAILED")

        val total = attempt.submittedPairsV4.size + pairs.size
        val nativeResult = if (first) {
            val localKey = DirectTransportCrypto.deriveProbeKeyV2(
                attempt.peer.pairwiseKey,
                attempt.connectionId,
                attempt.ticket.pathEpoch,
                attempt.checkRoundIdV4,
                localDeviceId,
                attempt.peer.deviceId,
            )
            val peerKey = DirectTransportCrypto.deriveProbeKeyV2(
                attempt.peer.pairwiseKey,
                attempt.connectionId,
                attempt.ticket.pathEpoch,
                attempt.checkRoundIdV4,
                attempt.peer.deviceId,
                localDeviceId,
            )
            val serverKeyId = attempt.ticket.serverSigningKeyId.toStringUtf8()
            val serverKey = attempt.config.ticketSigningKeys[serverKeyId]
                ?: throw DirectAttemptFailure("DIRECT_TICKET_KEY_MISSING")
            try {
                native.beginChecks(
                    DirectNativeBeginChecksRequestV4(
                        epoch = attempt.ticket.pathEpoch,
                        attemptId = attempt.attemptBytes,
                        connectionId = uuidBytes(attempt.connectionId),
                        localDeviceId = uuidBytes(localDeviceId),
                        peerDeviceId = uuidBytes(attempt.peer.deviceId),
                        networkHandle = attempt.prepared.networkHandle,
                        nowMillis = clockMillis(),
                        timeoutMillis = CHECKS_TIMEOUT_MILLIS,
                        role = DirectNativeRoleV4.INITIATOR,
                        ticket = attempt.ticketWire,
                        pairBatchSequence = sequence,
                        totalPairCount = total,
                        planComplete = complete,
                        serverSigningPublicKey = serverKey.publicKey,
                        ticketId = attempt.ticket.ticketId.toByteArray(),
                        checkRoundId = attempt.checkRoundIdV4,
                        connectivityNonce = attempt.connectivityNonceV4,
                        localSendProbeHmacKey = localKey,
                        peerReceiveProbeHmacKey = peerKey,
                        pairs = pairs,
                    ),
                )
            } finally {
                localKey.fill(0)
                peerKey.fill(0)
            }
        } else {
            native.appendCheckPairs(
                DirectNativeAppendCheckPairsRequestV4(
                    epoch = attempt.ticket.pathEpoch,
                    attemptId = attempt.attemptBytes,
                    checkRoundId = attempt.checkRoundIdV4,
                    pairBatchSequence = sequence,
                    totalPairCount = total,
                    planComplete = complete,
                    pairs = pairs,
                ),
            )
        }
        if (nativeResult !is DirectNativeResultV4.Success) {
            throw DirectAttemptFailure("DIRECT_NATIVE_V4_CHECK_PLAN_FAILED")
        }
        attempt.submittedPairsV4 += pairs
        attempt.lastPairBatchV4 = sequence
        attempt.checksStartedV4 = true
        attempt.checkPlanCompleteV4 = complete
    }

    private fun maybeNominateV4(attempt: Attempt) {
        if (attempt.nominationV2Sent || !attempt.checkPlanCompleteV4 ||
            attempt.validatedPairRanks.isEmpty()
        ) return
        val earliest = attempt.validatedPairRanks.minOrNull() ?: return
        if (earliest != 1 && !attempt.checksComplete) return
        val winner = attempt.submittedPairsV4.getOrNull(earliest - 1)
            ?: throw DirectAttemptFailure("DIRECT_V4_WINNING_PAIR_MISSING")
        val nominationId = randomIdentifier()
        val nomination = DirectNominationV2.newBuilder()
            .setNominationId(ByteString.copyFrom(nominationId))
            .setCheckRoundId(ByteString.copyFrom(attempt.checkRoundIdV4))
            .setWinningPair(pairToSignalV4(winner))
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setNominationV2(nomination)
            .build()
        if (!sendEncrypted(
                attempt,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
                payload,
            )
        ) {
            nominationId.fill(0)
            throw DirectAttemptFailure("DIRECT_V4_NOMINATION_SEND_FAILED")
        }
        attempt.nominationIdV4 = nominationId
        attempt.winningPairV4 = winner
        attempt.nominationV2Sent = true
    }

    private fun acceptNominationAckV4(
        attempt: Attempt,
        native: ProductionDirectShadowNativeV4,
        ack: DirectNominationAckV2,
    ) {
        val winner = attempt.winningPairV4
        if (!attempt.nominationV2Sent || attempt.nominationCommitted || winner == null ||
            !sameIdentifier(ack.nominationId.toByteArray(), attempt.nominationIdV4) ||
            !sameIdentifier(ack.checkRoundId.toByteArray(), attempt.checkRoundIdV4) ||
            !sameSignalPairV4(ack.winningPair, winner) ||
            ack.result != DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED ||
            ack.reasonCode.isNotEmpty()
        ) throw DirectAttemptFailure("DIRECT_V4_NOMINATION_ACK_REJECTED")
        val localProofNonce = randomIdentifier()
        try {
            val request = DirectNativeCommitNominationRequestV4(
                epoch = attempt.ticket.pathEpoch,
                attemptId = attempt.attemptBytes,
                checkRoundId = attempt.checkRoundIdV4,
                nominationId = attempt.nominationIdV4,
                winningPair = winner,
                role = DirectNativeRoleV4.INITIATOR,
                timeoutMillis = NOMINATE_TIMEOUT_MILLIS,
                selectionConfirmed = true,
                peerCertificateSha256 = attempt.peerCertificateSha256V4,
                peerDeviceSigningPublicKey = attempt.peer.signingPublicKey,
                localProofNonce = localProofNonce,
                nowMillis = clockMillis(),
            )
            if (native.commitNomination(request) !is DirectNativeResultV4.Success) {
                throw DirectAttemptFailure("DIRECT_NATIVE_V4_NOMINATION_FAILED")
            }
            attempt.nominationCommitted = true
            attempt.nominated = true
            attempt.ticketWire.fill(0)
        } finally {
            localProofNonce.fill(0)
        }
    }

    private fun sendNominationResultV4(attempt: Attempt, binding: ByteArray) {
        if (attempt.localNominationResultSentV4) {
            throw DirectAttemptFailure("DIRECT_V4_RESULT_DUPLICATE")
        }
        val winner = attempt.winningPairV4
            ?: throw DirectAttemptFailure("DIRECT_V4_WINNING_PAIR_MISSING")
        val result = DirectNominationResultV2.newBuilder()
            .setNominationId(ByteString.copyFrom(attempt.nominationIdV4))
            .setCheckRoundId(ByteString.copyFrom(attempt.checkRoundIdV4))
            .setWinningPair(pairToSignalV4(winner))
            .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED)
            .setReasonCode("")
            .setDirectPathBinding(ByteString.copyFrom(binding))
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setNominationResultV2(result)
            .build()
        if (!sendEncrypted(
                attempt,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
                payload,
            )
        ) throw DirectAttemptFailure("DIRECT_V4_RESULT_SEND_FAILED")
        attempt.localNominationResultSentV4 = true
    }

    private fun acceptNominationResultV4(
        attempt: Attempt,
        result: DirectNominationResultV2,
    ) {
        val winner = attempt.winningPairV4
        if (!attempt.nominationCommitted || winner == null || attempt.remotePathBindingV4.isNotEmpty() ||
            !sameIdentifier(result.nominationId.toByteArray(), attempt.nominationIdV4) ||
            !sameIdentifier(result.checkRoundId.toByteArray(), attempt.checkRoundIdV4) ||
            !sameSignalPairV4(result.winningPair, winner) ||
            result.result != DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED ||
            result.reasonCode.isNotEmpty() || result.directPathBinding.size() != 32 ||
            result.directPathBinding.toByteArray().all { it == 0.toByte() }
        ) throw DirectAttemptFailure("DIRECT_V4_RESULT_REJECTED")
        attempt.remotePathBindingV4 = result.directPathBinding.toByteArray()
    }

    private fun maybeFinishMultipairV4(attempt: Attempt) {
        if (!attempt.localNominationResultSentV4 || attempt.localPathBindingV4.size != 32 ||
            attempt.remotePathBindingV4.size != 32
        ) return
        if (!constantTimeEquals(attempt.localPathBindingV4, attempt.remotePathBindingV4)) {
            throw DirectAttemptFailure("DIRECT_V4_PATH_BINDING_MISMATCH")
        }
        if (!attempt.shadowReady.compareAndSet(false, true)) {
            throw DirectAttemptFailure("DIRECT_V4_SHADOW_READY_DUPLICATE")
        }
        synchronized(lock) { retryAttempt = 0 }
        attempt.releaseNegotiationSecrets()
        runCatching(attempt.signaling::close)
        publishAttempt(attempt, ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED)
    }

    private fun acceptLocalCandidateBatchV4(attempt: Attempt, batch: CandidateBatchV4) {
        if (attempt.localGatheringCompleteV4 || batch.sequence != attempt.lastCandidateBatchV4 + 1 ||
            batch.total != attempt.localCandidatesV4.size + batch.candidates.size ||
            batch.total !in 1..MAX_MULTIPAIR_CANDIDATES ||
            batch.candidates.isEmpty() && !batch.complete
        ) throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_BATCH_REJECTED")
        val combined = attempt.localCandidatesV4 + batch.candidates
        val signal = combined.map(::candidateToSignalV4)
        if (!validCandidatesV4(signal, attempt.ticket.expiresAtMs) ||
            batch.sequence == 1 && batch.candidates.none {
                it.kind == DirectNativeCandidateKindV4.HOST
            }
        ) throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_BATCH_REJECTED")
        attempt.localCandidatesV4 += batch.candidates
        attempt.lastCandidateBatchV4 = batch.sequence
        attempt.localGatheringCompleteV4 = batch.complete
    }

    private fun mergeRemoteCandidatesV4(
        attempt: Attempt,
        additions: List<DirectCandidateV1>,
        complete: Boolean,
    ) {
        if (additions.size > MAX_SIGNAL_CANDIDATES ||
            attempt.remoteCandidatesV4.size + additions.size > MAX_MULTIPAIR_CANDIDATES ||
            attempt.remoteGatheringCompleteV4 || additions.isEmpty() && !complete
        ) throw DirectAttemptFailure("DIRECT_V4_REMOTE_CANDIDATE_REJECTED")
        val priorIds = attempt.remoteCandidatesV4.map { it.candidateId.toByteArray().toList() }.toHashSet()
        if (additions.any { !priorIds.add(it.candidateId.toByteArray().toList()) }) {
            throw DirectAttemptFailure("DIRECT_V4_REMOTE_CANDIDATE_REJECTED")
        }
        val combined = attempt.remoteCandidatesV4 + additions
        if (!validCandidatesV4(combined, attempt.ticket.expiresAtMs)) {
            throw DirectAttemptFailure("DIRECT_V4_REMOTE_CANDIDATE_REJECTED")
        }
        attempt.remoteCandidatesV4 += additions
        attempt.remoteGatheringCompleteV4 = complete
    }

    private fun validAnswerV4(attempt: Attempt, answer: DirectPathAnswerV1): Boolean =
        sameIdentifier(answer.ticketId.toByteArray(), attempt.ticket.ticketId.toByteArray()) &&
            answer.quicCertificateSha256.size() == 32 &&
            answer.quicCertificateSha256.toByteArray().any { it != 0.toByte() } &&
            answer.result == DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED &&
            answer.reasonCode.isEmpty() && answer.candidatesCount in 1..MAX_SIGNAL_CANDIDATES &&
            validCandidatesV4(answer.candidatesList, attempt.ticket.expiresAtMs)

    private fun validCandidatesV4(values: List<DirectCandidateV1>, ticketExpiry: Long): Boolean {
        if (values.size !in 1..MAX_MULTIPAIR_CANDIDATES) return false
        val identifiers = HashSet<List<Byte>>()
        val endpoints = HashSet<Pair<List<Byte>, Int>>()
        val hostIds = HashSet<List<Byte>>()
        for (candidate in values) {
            val identifier = candidate.candidateId.toByteArray()
            val address = runCatching {
                InetAddress.getByAddress(candidate.ipAddress.toByteArray())
            }.getOrNull() ?: return false
            if (identifier.size != CANDIDATE_ID_BYTES || identifier.all { it == 0.toByte() } ||
                candidate.kind !in setOf(
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
                ) || candidate.carrier != DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC ||
                !DirectAddressPolicy.isCandidateEndpoint(address, candidate.port) ||
                candidate.priority !in 1..Int.MAX_VALUE || candidate.networkCost > 999 ||
                candidate.expiresAtMs !in 1L..ticketExpiry ||
                !identifiers.add(identifier.toList()) ||
                !endpoints.add(candidate.ipAddress.toByteArray().toList() to candidate.port)
            ) return false
            if (candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST) {
                if (!candidate.relatedCandidateId.isEmpty) return false
                hostIds += identifier.toList()
            } else if (candidate.relatedCandidateId.size() != CANDIDATE_ID_BYTES) return false
        }
        return values.all { candidate ->
            candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST ||
                candidate.relatedCandidateId.toByteArray().toList() in hostIds
        }
    }

    private fun candidateToSignalV4(value: DirectNativeCandidateV4): DirectCandidateV1 =
        DirectCandidateV1.newBuilder()
            .setCandidateId(ByteString.copyFrom(value.candidateId))
            .setKind(
                when (value.kind) {
                    DirectNativeCandidateKindV4.HOST ->
                        DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST
                    DirectNativeCandidateKindV4.SERVER_REFLEXIVE ->
                        DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE
                },
            )
            .setCarrier(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
            .setIpAddress(ByteString.copyFrom(value.endpoint.ipAddress))
            .setPort(value.endpoint.port)
            .setPriority(value.priority)
            .setNetworkCost(value.networkCost)
            .setExpiresAtMs(value.expiresAtMillis)
            .setRelatedCandidateId(
                if (value.kind == DirectNativeCandidateKindV4.HOST) ByteString.EMPTY
                else ByteString.copyFrom(value.relatedCandidateId),
            )
            .build()

    private fun signalToNativeCandidateV4(value: DirectCandidateV1): DirectNativeCandidateV4 =
        DirectNativeCandidateV4(
            candidateId = value.candidateId.toByteArray(),
            relatedCandidateId = if (value.relatedCandidateId.isEmpty) ByteArray(16)
            else value.relatedCandidateId.toByteArray(),
            endpoint = DirectNativeSocketAddressV4(value.ipAddress.toByteArray(), value.port),
            kind = when (value.kind) {
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST -> DirectNativeCandidateKindV4.HOST
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE ->
                    DirectNativeCandidateKindV4.SERVER_REFLEXIVE
                else -> throw DirectAttemptFailure("DIRECT_V4_CANDIDATE_KIND_REJECTED")
            },
            priority = value.priority,
            networkCost = value.networkCost,
            expiresAtMillis = value.expiresAtMs,
        )

    private fun pairToSignalV4(value: DirectNativeCandidatePairV4): DirectCandidatePairV1 =
        DirectCandidatePairV1.newBuilder()
            .setInitiatorCandidateId(ByteString.copyFrom(value.initiator.candidateId))
            .setResponderCandidateId(ByteString.copyFrom(value.responder.candidateId))
            .build()

    private fun sameSignalPairV4(
        value: DirectCandidatePairV1,
        expected: DirectNativeCandidatePairV4,
    ): Boolean = sameIdentifier(
        value.initiatorCandidateId.toByteArray(), expected.initiator.candidateId,
    ) && sameIdentifier(
        value.responderCandidateId.toByteArray(), expected.responder.candidateId,
    )

    private fun samePairV4(
        left: DirectNativeCandidatePairV4,
        right: DirectNativeCandidatePairV4,
    ): Boolean = left.rank == right.rank && samePairIdentityV4(left, right)

    private fun samePairIdentityV4(
        left: DirectNativeCandidatePairV4,
        right: DirectNativeCandidatePairV4,
    ): Boolean = sameIdentifier(left.initiator.candidateId, right.initiator.candidateId) &&
        sameIdentifier(left.responder.candidateId, right.responder.candidateId)

    private fun sameSelectablePairV4(
        submitted: DirectNativeCandidatePairV4,
        candidate: SelectableCandidatePair,
    ): Boolean = sameIdentifier(
        submitted.initiator.candidateId,
        candidate.local.wire.candidateId.toByteArray(),
    ) && sameIdentifier(
        submitted.responder.candidateId,
        candidate.remote.wire.candidateId.toByteArray(),
    )

    private fun decodeOpaqueV4(payload: ByteArray): ByteArray? = runCatching {
        require(payload.size == V4_OPAQUE_BYTES)
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        require(buffer.getInt(0) == 4 && buffer.getInt(4) == V4_OPAQUE_BYTES)
        val length = buffer.getInt(8)
        require(length in 1..2_048 && buffer.getInt(12) == 0)
        require(payload.rangeIsZero(16 + length, payload.size))
        payload.copyOfRange(16, 16 + length)
    }.getOrNull()

    private fun decodeCandidateBatchV4(
        payload: ByteArray,
        attempt: Attempt,
    ): CandidateBatchV4? = runCatching {
        require(payload.size == V4_CANDIDATE_BATCH_BYTES)
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        require(buffer.getInt(0) == 4 && buffer.getInt(4) == V4_CANDIDATE_BATCH_BYTES)
        require(buffer.getLong(8) == attempt.ticket.pathEpoch)
        require(sameIdentifier(payload.copyOfRange(16, 32), attempt.attemptBytes))
        val sequence = buffer.getInt(32)
        val count = buffer.getInt(36)
        val total = buffer.getInt(40)
        val complete = buffer.getInt(44)
        require(sequence > 0 && count in 0..MAX_SIGNAL_CANDIDATES &&
            total in 0..MAX_MULTIPAIR_CANDIDATES && complete in 0..1)
        val candidates = (0 until count).map { index ->
            decodeCandidateV4(payload, 48 + index * V4_CANDIDATE_BYTES)
                ?: error("candidate")
        }
        require(
            payload.rangeIsZero(
                48 + count * V4_CANDIDATE_BYTES,
                48 + MAX_SIGNAL_CANDIDATES * V4_CANDIDATE_BYTES,
            ),
        )
        require(payload.rangeIsZero(1_072, payload.size))
        CandidateBatchV4(sequence, total, complete == 1, candidates)
    }.getOrNull()

    private fun decodeCandidateV4(payload: ByteArray, offset: Int): DirectNativeCandidateV4? =
        runCatching {
            require(offset >= 0 && offset + V4_CANDIDATE_BYTES <= payload.size)
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
            require(buffer.getInt(offset) == 4 && buffer.getInt(offset + 4) == V4_CANDIDATE_BYTES)
            val candidateId = payload.copyOfRange(offset + 8, offset + 24)
            val related = payload.copyOfRange(offset + 24, offset + 40)
            val socketOffset = offset + 40
            require(buffer.getInt(socketOffset) == 4 && buffer.getInt(socketOffset + 4) == 48)
            val ipLength = buffer.getInt(socketOffset + 24)
            val port = buffer.getInt(socketOffset + 28)
            require(ipLength == 4 || ipLength == 16)
            require(payload.rangeIsZero(socketOffset + 8 + ipLength, socketOffset + 24))
            require(payload.rangeIsZero(socketOffset + 32, socketOffset + 48))
            val endpoint = DirectNativeSocketAddressV4(
                payload.copyOfRange(socketOffset + 8, socketOffset + 8 + ipLength),
                port,
            )
            val kind = DirectNativeCandidateKindV4.from(buffer.getInt(offset + 88))
                ?: error("kind")
            require(buffer.getInt(offset + 92) == 1)
            require(payload.rangeIsZero(offset + 112, offset + 128))
            DirectNativeCandidateV4(
                candidateId = candidateId,
                relatedCandidateId = related,
                endpoint = endpoint,
                kind = kind,
                priority = buffer.getInt(offset + 96),
                networkCost = buffer.getInt(offset + 100),
                expiresAtMillis = buffer.getLong(offset + 104),
            )
        }.getOrNull()

    private fun decodePairV4(payload: ByteArray, offset: Int): DirectNativeCandidatePairV4? =
        runCatching {
            require(offset >= 0 && offset + V4_PAIR_BYTES <= payload.size)
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
            require(buffer.getInt(offset) == 4 && buffer.getInt(offset + 4) == V4_PAIR_BYTES)
            require(buffer.getInt(offset + 12) == 0)
            val pair = DirectNativeCandidatePairV4(
                rank = buffer.getInt(offset + 8),
                initiator = decodeCandidateV4(payload, offset + 16) ?: error("initiator"),
                responder = decodeCandidateV4(payload, offset + 144) ?: error("responder"),
            )
            require(payload.rangeIsZero(offset + 272, offset + 288))
            pair
        }.getOrNull()

    private fun decodePairValidatedV4(
        payload: ByteArray,
        attempt: Attempt,
    ): DirectNativeCandidatePairV4? = runCatching {
        requirePayloadHeaderV4(payload, V4_PAIR_VALIDATED_BYTES, attempt)
        require(sameIdentifier(payload.copyOfRange(32, 48), attempt.checkRoundIdV4))
        val pair = decodePairV4(payload, 48) ?: error("pair")
        require(payload.rangeIsZero(344, payload.size))
        pair
    }.getOrNull()

    private fun decodeChecksCompleteV4(
        payload: ByteArray,
        attempt: Attempt,
    ): ChecksCompleteV4? = runCatching {
        requirePayloadHeaderV4(payload, V4_CHECKS_COMPLETE_BYTES, attempt)
        require(sameIdentifier(payload.copyOfRange(32, 48), attempt.checkRoundIdV4))
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val total = buffer.getInt(48)
        val validated = buffer.getInt(52)
        val exhausted = buffer.getInt(56)
        val result = buffer.getInt(60)
        require(total in 1..MAX_CANDIDATE_PAIRS && validated in 0..total &&
            exhausted in 0..total && result in 0..1 && payload.rangeIsZero(64, payload.size))
        ChecksCompleteV4(total, validated, exhausted)
    }.getOrNull()

    private fun decodeNominationReadyV4(
        payload: ByteArray,
        attempt: Attempt,
    ): NominationReadyV4? = runCatching {
        requirePayloadHeaderV4(payload, V4_NOMINATION_READY_BYTES, attempt)
        require(sameIdentifier(payload.copyOfRange(32, 48), attempt.checkRoundIdV4))
        val nominationId = payload.copyOfRange(48, 64)
        val pair = decodePairV4(payload, 64) ?: error("pair")
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        require(buffer.getInt(352) == DirectNativeRoleV4.INITIATOR.code &&
            buffer.getInt(356) == 0 && payload.rangeIsZero(360, payload.size))
        NominationReadyV4(nominationId, pair)
    }.getOrNull()

    private fun decodeShadowReadyV4(
        payload: ByteArray,
        attempt: Attempt,
    ): ShadowReadyV4? = runCatching {
        requirePayloadHeaderV4(payload, V4_SHADOW_READY_BYTES, attempt)
        require(sameIdentifier(payload.copyOfRange(32, 48), attempt.checkRoundIdV4))
        val nominationId = payload.copyOfRange(48, 64)
        val pair = decodePairV4(payload, 64) ?: error("pair")
        require(payload.rangeIsZero(384, payload.size))
        ShadowReadyV4(nominationId, pair, payload.copyOfRange(352, 384))
    }.getOrNull()

    private fun requirePayloadHeaderV4(payload: ByteArray, size: Int, attempt: Attempt) {
        require(payload.size == size)
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        require(buffer.getInt(0) == 4 && buffer.getInt(4) == size)
        require(buffer.getLong(8) == attempt.ticket.pathEpoch)
        require(sameIdentifier(payload.copyOfRange(16, 32), attempt.attemptBytes))
    }

    private fun ByteArray.rangeIsZero(start: Int, end: Int): Boolean =
        start in 0..end && end <= size && (start until end).all { this[it] == 0.toByte() }

    private fun sameIdentifier(left: ByteArray, right: ByteArray): Boolean =
        left.size == right.size && MessageDigest.isEqual(left, right)

    private fun constantTimeEquals(left: ByteArray, right: ByteArray): Boolean =
        left.size == right.size && MessageDigest.isEqual(left, right)

    private fun processNativeEvent(
        attempt: Attempt,
        native: ProductionDirectShadowNative,
        event: DirectNativeEvent,
    ) {
        try {
            if (event.kind == DirectNativeEventKind.IGNORED_STALE) return
            if (event.epoch != attempt.ticket.pathEpoch ||
                !event.attemptId.contentEquals(attempt.attemptBytes)
            ) return
            if (event.generation <= attempt.lastNativeGeneration) {
                throw DirectAttemptFailure("DIRECT_NATIVE_EVENT_STALE")
            }
            attempt.lastNativeGeneration = event.generation
            when (event.kind) {
                DirectNativeEventKind.IDENTITY_READY -> {
                    if (attempt.identityReady || event.payload.isEmpty()) {
                        throw DirectAttemptFailure("DIRECT_IDENTITY_EVENT_REJECTED")
                    }
                    attempt.localCertificateSha256 = sha256(event.payload)
                    attempt.identityReady = true
                }
                DirectNativeEventKind.CANDIDATES_READY -> {
                    if (attempt.candidatesReady || event.endpoint == null || event.candidates.isEmpty() ||
                        event.candidates.first() != event.endpoint ||
                        event.candidates.any { candidate -> !validEndpoint(candidate) }
                    ) throw DirectAttemptFailure("DIRECT_CANDIDATE_EVENT_REJECTED")
                    attempt.nativeCandidates = event.candidates.map(DirectNativeSocketAddress::ownedCopy)
                    attempt.boundEndpoint = event.endpoint.ownedCopy()
                    attempt.candidatesGeneration = event.generation
                    attempt.localCandidates = encodeCandidates(event.candidates, attempt.ticket.expiresAtMs)
                    attempt.candidatesReady = true
                }
                DirectNativeEventKind.SIGN_REQUEST -> {
                    if (!attempt.nominated || event.requestId <= 0 || event.payload.isEmpty() ||
                        !attempt.signedRequests.add(event.requestId)
                    ) throw DirectAttemptFailure("DIRECT_SIGN_REQUEST_REJECTED")
                    val signature = identity.sign(event.payload)
                    try {
                        if (native.submitSignature(
                                event.epoch,
                                attempt.attemptBytes,
                                event.requestId,
                                signature,
                            ) !is DirectNativeResult.Success
                        ) throw DirectAttemptFailure("DIRECT_SIGNATURE_REJECTED")
                    } finally {
                        signature.fill(0)
                    }
                }
                DirectNativeEventKind.SHADOW_READY -> {
                    if (!attempt.nominated || attempt.shadowReady.get() || event.payload.size != 32 ||
                        event.payload.all { it == 0.toByte() }
                    ) throw DirectAttemptFailure("DIRECT_SHADOW_READY_REJECTED")
                    attempt.shadowReady.set(true)
                    synchronized(lock) { retryAttempt = 0 }
                    attempt.releaseNegotiationSecrets()
                    runCatching(attempt.signaling::close)
                    publishAttempt(attempt, ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED)
                }
                DirectNativeEventKind.DIRECT_FAILED ->
                    throw DirectAttemptFailure("DIRECT_NATIVE_FAILED")
                DirectNativeEventKind.STATE_CHANGED -> Unit
                DirectNativeEventKind.SYNTHETIC_FAILURE,
                DirectNativeEventKind.UNKNOWN,
                -> throw DirectAttemptFailure("DIRECT_NATIVE_EVENT_REJECTED")
                DirectNativeEventKind.IGNORED_STALE -> Unit
            }
        } finally {
            event.payload.fill(0)
            event.attemptId.fill(0)
            event.endpoint?.ipAddress?.fill(0)
            event.candidates.forEach { it.ipAddress.fill(0) }
        }
    }

    private fun sendOffer(attempt: Attempt, native: ProductionDirectShadowNative) {
        val offer = DirectPathOfferV1.newBuilder()
            .setTicket(attempt.ticket)
            .setQuicCertificateSha256(ByteString.copyFrom(attempt.localCertificateSha256))
            .addAllCandidates(attempt.localCandidates)
            .setGatheringComplete(true)
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setPathOffer(offer)
            .build()
        if (!sendEncrypted(attempt, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER, payload)) {
            throw DirectAttemptFailure("DIRECT_OFFER_SEND_FAILED")
        }
        if (native.markCandidatesExchanged(
                attempt.ticket.pathEpoch,
                attempt.attemptBytes,
                attempt.candidatesGeneration,
                attempt.nativeCandidates,
            ) !is DirectNativeResult.Success
        ) throw DirectAttemptFailure("DIRECT_CANDIDATE_EXCHANGE_REJECTED")
        attempt.offerSequence = attempt.localSequence
        attempt.offerSent = true
        publishAttempt(attempt, ProductionDirectShadowState.NEGOTIATING)
    }

    private fun processIncomingSignal(
        attempt: Attempt,
        native: ProductionDirectShadowNative,
        envelope: TransportSignalEnvelopeV1,
    ) {
        if (!matchesAttempt(attempt, envelope)) return
        if (envelope.signalSeq != attempt.lastRemoteSequence + 1 ||
            envelope.signalAck < attempt.lastRemoteAck || envelope.signalAck > attempt.localSequence
        ) throw DirectAttemptFailure("DIRECT_SIGNAL_CURSOR_REJECTED")
        if (attempt.nominated || !attempt.offerSent ||
            envelope.signalType != TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_ANSWER ||
            envelope.signalSeq != 1L || envelope.signalAck != attempt.offerSequence
        ) throw DirectAttemptFailure("DIRECT_ANSWER_REJECTED")
        val answer = decryptAnswer(attempt, envelope)
            ?: throw DirectAttemptFailure("DIRECT_ANSWER_REJECTED")
        attempt.lastRemoteSequence = envelope.signalSeq
        attempt.lastRemoteAck = envelope.signalAck
        nominate(attempt, native, answer)
    }

    private fun nominate(
        attempt: Attempt,
        native: ProductionDirectShadowNative,
        answer: DirectPathAnswerV1,
    ) {
        val selectedPair = selectCandidatePairs(
            attempt.localCandidates,
            answer.candidatesList,
        ).firstOrNull() ?: throw DirectAttemptFailure("DIRECT_CANDIDATE_PAIR_UNAVAILABLE")
        val localCandidate = selectedPair.local.wire
        val remoteCandidate = selectedPair.remote.wire
        val nominationId = randomIdentifier()
        val connectivityNonce = randomIdentifier()
        val nomination = DirectNominationV1.newBuilder()
            .setNominationId(ByteString.copyFrom(nominationId))
            .setPair(
                DirectCandidatePairV1.newBuilder()
                    .setInitiatorCandidateId(localCandidate.candidateId)
                    .setResponderCandidateId(remoteCandidate.candidateId),
            )
            .setConnectivityNonce(ByteString.copyFrom(connectivityNonce))
            .build()
        val payload = TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(PROTOCOL_VERSION)
            .setNomination(nomination)
            .build()
        if (!sendEncrypted(attempt, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION, payload)) {
            nominationId.fill(0)
            connectivityNonce.fill(0)
            throw DirectAttemptFailure("DIRECT_NOMINATION_SEND_FAILED")
        }

        val localProbeKey = DirectTransportCrypto.deriveProbeKey(
            attempt.peer.pairwiseKey,
            attempt.connectionId,
            attempt.ticket.pathEpoch,
            uuidFromBytes(nominationId),
            localDeviceId,
            attempt.peer.deviceId,
        )
        val peerProbeKey = DirectTransportCrypto.deriveProbeKey(
            attempt.peer.pairwiseKey,
            attempt.connectionId,
            attempt.ticket.pathEpoch,
            uuidFromBytes(nominationId),
            attempt.peer.deviceId,
            localDeviceId,
        )
        val localProofNonce = randomIdentifier()
        val serverKeyId = attempt.ticket.serverSigningKeyId.toStringUtf8()
        val serverKey = attempt.config.ticketSigningKeys[serverKeyId]
            ?: throw DirectAttemptFailure("DIRECT_TICKET_KEY_MISSING")
        val request = DirectNativeNominateRequest(
            epoch = attempt.ticket.pathEpoch,
            attemptId = attempt.attemptBytes,
            connectionId = uuidBytes(attempt.connectionId),
            localDeviceId = uuidBytes(localDeviceId),
            peerDeviceId = uuidBytes(attempt.peer.deviceId),
            role = DirectNativeRole.INITIATOR,
            timeoutMillis = NOMINATE_TIMEOUT_MILLIS,
            networkHandle = attempt.prepared.networkHandle,
            localBind = requireNotNull(attempt.boundEndpoint),
            remoteCandidate = DirectNativeSocketAddress(
                remoteCandidate.ipAddress.toByteArray(),
                remoteCandidate.port,
            ),
            ticket = attempt.ticketWire,
            serverSigningPublicKey = serverKey.publicKey,
            peerCertificateSha256 = answer.quicCertificateSha256.toByteArray(),
            peerDeviceSigningPublicKey = attempt.peer.signingPublicKey,
            localSendProbeHmacKey = localProbeKey,
            peerReceiveProbeHmacKey = peerProbeKey,
            ticketId = attempt.ticket.ticketId.toByteArray(),
            nominationId = nominationId,
            initiatorCandidateId = localCandidate.candidateId.toByteArray(),
            responderCandidateId = remoteCandidate.candidateId.toByteArray(),
            localProbeNonce = connectivityNonce,
            peerProbeNonce = connectivityNonce,
            localProofNonce = localProofNonce,
            nowMillis = clockMillis(),
        )
        val command = ProductionDirectNominationCommand(request, localProbeKey, peerProbeKey)
        try {
            if (native.nominate(command) !is DirectNativeResult.Success) {
                throw DirectAttemptFailure("DIRECT_NATIVE_NOMINATION_FAILED")
            }
            attempt.nominated = true
            attempt.ticketWire.fill(0)
        } finally {
            listOf(
                localProbeKey,
                peerProbeKey,
                localProofNonce,
                nominationId,
                connectivityNonce,
                request.localSendProbeHmacKey,
                request.peerReceiveProbeHmacKey,
                request.localProbeNonce,
                request.peerProbeNonce,
                request.localProofNonce,
                request.ticket,
            ).forEach { it.fill(0) }
            command.destroy()
        }
    }

    /**
     * Native v3 consumes its retained socket on the first nomination, so V1 chooses exactly one
     * deterministic pair. Keeping the bounded ordered list here makes that one-shot choice
     * explicit without creating a second Android socket or taking any Relay ownership.
     */
    private fun selectCandidatePairs(
        localCandidates: List<DirectCandidateV1>,
        remoteCandidates: List<DirectCandidateV1>,
    ): List<SelectableCandidatePair> {
        val local = localCandidates.mapNotNull(::selectableCandidate)
        val remote = remoteCandidates.mapNotNull(::selectableCandidate)
        return buildList {
            for (localCandidate in local) {
                for (remoteCandidate in remote) {
                    if (localCandidate.address.address.size == remoteCandidate.address.address.size) {
                        add(SelectableCandidatePair(localCandidate, remoteCandidate))
                    }
                }
            }
        }.filter { pairPreference(it) < UNSAFE_HOST_PAIR_PREFERENCE }
            .sortedWith(Comparator(::compareCandidatePairs))
    }

    private fun selectableCandidate(candidate: DirectCandidateV1): SelectableCandidate? {
        val identifier = candidate.candidateId
        val address = runCatching {
            InetAddress.getByAddress(candidate.ipAddress.toByteArray())
        }.getOrNull() ?: return null
        val kindIsValid = candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST ||
            candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE
        val relationIsValid = when (candidate.kind) {
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST -> candidate.relatedCandidateId.isEmpty
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE ->
                candidate.relatedCandidateId.size() == CANDIDATE_ID_BYTES
            else -> false
        }
        if (identifier.size() != CANDIDATE_ID_BYTES ||
            (0 until identifier.size()).all { identifier.byteAt(it) == 0.toByte() } ||
            !kindIsValid || !relationIsValid ||
            candidate.carrier != DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC ||
            candidate.priority !in 1..Int.MAX_VALUE ||
            !DirectAddressPolicy.isCandidateEndpoint(address, candidate.port)
        ) return null
        return SelectableCandidate(candidate, address)
    }

    private fun compareCandidatePairs(
        left: SelectableCandidatePair,
        right: SelectableCandidatePair,
    ): Int {
        compareValues(pairPreference(left), pairPreference(right)).takeIf { it != 0 }?.let { return it }
        val leftTotalPriority = left.local.wire.priority.toLong() + left.remote.wire.priority.toLong()
        val rightTotalPriority = right.local.wire.priority.toLong() + right.remote.wire.priority.toLong()
        compareValues(rightTotalPriority, leftTotalPriority).takeIf { it != 0 }?.let { return it }
        compareValues(right.local.wire.priority, left.local.wire.priority)
            .takeIf { it != 0 }?.let { return it }
        compareValues(right.remote.wire.priority, left.remote.wire.priority)
            .takeIf { it != 0 }?.let { return it }
        compareCandidateIds(left.local.wire.candidateId, right.local.wire.candidateId)
            .takeIf { it != 0 }?.let { return it }
        return compareCandidateIds(left.remote.wire.candidateId, right.remote.wire.candidateId)
    }

    private fun pairPreference(pair: SelectableCandidatePair): Int = when {
        pair.isSameLanHostPair() -> 0
        pair.hasKinds(
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
        ) -> 1
        pair.hasKinds(
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
        ) -> 2
        pair.hasKinds(
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
            DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
        ) -> 3
        else -> 4
    }

    private fun compareCandidateIds(left: ByteString, right: ByteString): Int {
        for (index in 0 until minOf(left.size(), right.size())) {
            val comparison = compareValues(
                left.byteAt(index).toInt() and 0xff,
                right.byteAt(index).toInt() and 0xff,
            )
            if (comparison != 0) return comparison
        }
        return compareValues(left.size(), right.size())
    }

    private fun sendEncrypted(
        attempt: Attempt,
        type: TransportSignalTypeV1,
        payload: TransportSignalPayloadV1,
    ): Boolean {
        val plaintext = payload.toByteArray()
        return try {
            val metadata = TransportSignalEnvelopeV1.newBuilder()
                .setProtocolVersion(PROTOCOL_VERSION)
                .setSignalType(type)
                .setConnectionId(ByteString.copyFrom(uuidBytes(attempt.connectionId)))
                .setSenderDeviceId(ByteString.copyFrom(uuidBytes(localDeviceId)))
                .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(attempt.peer.deviceId)))
                .setPathEpoch(attempt.ticket.pathEpoch)
                .setSignalSeq(++attempt.localSequence)
                .setSignalAck(attempt.lastRemoteSequence)
                .setSentAtMs(clockMillis())
                .setKeyId(ByteString.copyFrom(attempt.peer.keyIdBytes))
                .build()
            attempt.signaling.send(
                DirectTransportCrypto.encryptSignal(attempt.peer.pairwiseKey, metadata, plaintext),
            )
        } finally {
            plaintext.fill(0)
        }
    }

    private fun decryptAnswer(
        attempt: Attempt,
        envelope: TransportSignalEnvelopeV1,
    ): DirectPathAnswerV1? {
        val plaintext = runCatching {
            DirectTransportCrypto.decryptSignal(attempt.peer.pairwiseKey, envelope)
        }.getOrNull() ?: return null
        return try {
            val payload = runCatching { TransportSignalPayloadV1.parseFrom(plaintext) }.getOrNull()
                ?: return null
            if (payload.formatVersion != PROTOCOL_VERSION ||
                payload.signalCase != TransportSignalPayloadV1.SignalCase.PATH_ANSWER
            ) return null
            val canonical = TransportSignalPayloadV1.newBuilder()
                .setFormatVersion(PROTOCOL_VERSION)
                .setPathAnswer(payload.pathAnswer)
                .build()
                .toByteArray()
            try {
                if (!plaintext.contentEquals(canonical)) return null
            } finally {
                canonical.fill(0)
            }
            payload.pathAnswer.takeIf { answer -> validAnswer(attempt, answer) }
        } finally {
            plaintext.fill(0)
        }
    }

    private fun validAnswer(attempt: Attempt, answer: DirectPathAnswerV1): Boolean =
        answer.ticketId.toByteArray().contentEquals(attempt.ticket.ticketId.toByteArray()) &&
            answer.quicCertificateSha256.size() == 32 &&
            answer.quicCertificateSha256.toByteArray().any { it != 0.toByte() } &&
            answer.result == DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED &&
            answer.gatheringComplete && answer.reasonCode.isEmpty() &&
            validCandidates(answer.candidatesList, attempt.ticket.expiresAtMs)

    private fun matchesAttempt(attempt: Attempt, envelope: TransportSignalEnvelopeV1): Boolean =
        envelope.protocolVersion == PROTOCOL_VERSION &&
            envelope.connectionId.toByteArray().contentEquals(uuidBytes(attempt.connectionId)) &&
            envelope.senderDeviceId.toByteArray().contentEquals(uuidBytes(attempt.peer.deviceId)) &&
            envelope.recipientDeviceId.toByteArray().contentEquals(uuidBytes(localDeviceId)) &&
            envelope.pathEpoch == attempt.ticket.pathEpoch &&
            envelope.keyId.toByteArray().contentEquals(attempt.peer.keyIdBytes)

    private fun validCandidates(values: List<DirectCandidateV1>, ticketExpiry: Long): Boolean {
        if (values.size !in 1..MAX_SIGNAL_CANDIDATES) return false
        val identifiers = HashSet<List<Byte>>()
        val endpoints = HashSet<Pair<List<Byte>, Int>>()
        val hostIds = HashSet<List<Byte>>()
        for (candidate in values) {
            val identifier = candidate.candidateId.toByteArray()
            val address = runCatching { InetAddress.getByAddress(candidate.ipAddress.toByteArray()) }.getOrNull()
                ?: return false
            if (identifier.size != 16 || identifier.all { it == 0.toByte() } ||
                candidate.kind !in setOf(
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
                ) || candidate.carrier != DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC ||
                !DirectAddressPolicy.isCandidateEndpoint(address, candidate.port) ||
                candidate.priority !in 1..Int.MAX_VALUE || candidate.networkCost > 999 ||
                candidate.expiresAtMs !in 1L..ticketExpiry ||
                !identifiers.add(identifier.toList()) ||
                !endpoints.add(candidate.ipAddress.toByteArray().toList() to candidate.port)
            ) return false
            if (candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST) {
                if (!candidate.relatedCandidateId.isEmpty) return false
                hostIds += identifier.toList()
            } else if (candidate.relatedCandidateId.size() != 16) {
                return false
            }
        }
        return values.all { candidate ->
            candidate.kind == DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST ||
                candidate.relatedCandidateId.toByteArray().toList() in hostIds
        }
    }

    private fun encodeCandidates(
        endpoints: List<DirectNativeSocketAddress>,
        expiresAtMillis: Long,
    ): List<DirectCandidateV1> {
        require(endpoints.size in 1..3) { "DIRECT_CANDIDATE_COUNT_INVALID" }
        val hostId = randomIdentifier()
        return endpoints.mapIndexed { index, endpoint ->
            val id = if (index == 0) hostId.copyOf() else randomIdentifier()
            DirectCandidateV1.newBuilder()
                .setCandidateId(ByteString.copyFrom(id))
                .setKind(
                    if (index == 0) DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST
                    else DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
                )
                .setCarrier(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
                .setIpAddress(ByteString.copyFrom(endpoint.ipAddress))
                .setPort(endpoint.port)
                .setPriority(HOST_PRIORITY - index)
                .setRelatedCandidateId(if (index == 0) ByteString.EMPTY else ByteString.copyFrom(hostId))
                .setNetworkCost(0)
                .setExpiresAtMs(expiresAtMillis)
                .build()
                .also { id.fill(0) }
        }.also { hostId.fill(0) }
    }

    private fun validEndpoint(value: DirectNativeSocketAddress): Boolean = runCatching {
        DirectAddressPolicy.isCandidateEndpoint(InetAddress.getByAddress(value.ipAddress), value.port)
    }.getOrDefault(false)

    private fun terminalSignalingReason(state: DirectTransportSignalingState): String? = when (state) {
        is DirectTransportSignalingState.Fault -> if (state.willRetry) null else "DIRECT_SIGNAL_FAILED"
        DirectTransportSignalingState.Closed -> "DIRECT_SIGNAL_CLOSED"
        else -> null
    }

    private fun validShadowConfig(config: DirectTransportServerConfig): Boolean =
        config.protocolVersion == PROTOCOL_VERSION &&
            config.capabilities in setOf(
                setOf(
                    DirectTransportSignalingClient.DIRECT_SIGNALING_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_SHADOW_CAPABILITY,
                ),
                setOf(
                    DirectTransportSignalingClient.DIRECT_SIGNALING_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_SHADOW_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY,
                ),
            ) && !config.directRoutingPermitted && config.ticketSigningKeys.isNotEmpty() &&
            config.stunServers.size <= 2

    private fun attemptTimeoutMillis(attempt: Attempt): Long =
        (attempt.ticket.expiresAtMs - clockMillis()).coerceIn(1L, ATTEMPT_TIMEOUT_MILLIS)

    private fun ensureAttemptCurrent(attempt: Attempt) {
        synchronized(lock) {
            if (closed.get() || active !== attempt || !gateStillCurrentLocked(attempt.gate)) {
                throw CancellationException("Direct attempt owner changed")
            }
        }
    }

    private fun ensureGateCurrent(gate: Gate) {
        synchronized(lock) {
            if (closed.get() || !gateStillCurrentLocked(gate)) {
                throw CancellationException("Direct gate owner changed")
            }
        }
    }

    private fun publishAttempt(attempt: Attempt, state: ProductionDirectShadowState) {
        synchronized(lock) {
            if (active !== attempt || !gateStillCurrentLocked(attempt.gate) || closed.get()) return
            mutableSnapshot.value = snapshotFor(state, "", attempt)
        }
    }

    private fun publishIfCurrent(
        gate: Gate,
        state: ProductionDirectShadowState,
        reason: String = "",
    ) {
        synchronized(lock) {
            if (!gateStillCurrentLocked(gate) || closed.get()) return
            mutableSnapshot.value = ProductionDirectShadowSnapshot(
                state = state,
                reasonCode = reason,
                authenticationGeneration = authenticationGeneration,
                relayGeneration = relayGeneration,
                networkGeneration = networkGeneration,
                peerDeviceId = gate.peer.deviceId,
            )
        }
    }

    private fun publishFailureIfCurrent(gate: Gate, reason: String) {
        synchronized(lock) {
            if (closed.get() || !gateStillCurrentLocked(gate)) return
            mutableSnapshot.value = ProductionDirectShadowSnapshot(
                state = if (relayGeneration > 0) ProductionDirectShadowState.RELAY_ONLY
                else ProductionDirectShadowState.WAITING_FOR_RELAY,
                reasonCode = reason,
                authenticationGeneration = authenticationGeneration,
                relayGeneration = relayGeneration,
                networkGeneration = networkGeneration,
                peerDeviceId = gate.peer.deviceId,
            )
        }
    }

    private fun failCurrent(reason: String) {
        val attempt = synchronized(lock) { active }
        attempt?.retire(reason, networkChanged = false)
        synchronized(lock) {
            if (active === attempt) active = null
            if (!closed.get()) {
                mutableSnapshot.value = snapshotFor(
                    if (relayGeneration > 0) ProductionDirectShadowState.RELAY_ONLY
                    else ProductionDirectShadowState.WAITING_FOR_RELAY,
                    reason,
                    attempt,
                )
            }
        }
    }

    private fun scheduleRetry() {
        synchronized(lock) {
            if (closed.get() || retryJob?.isActive == true || !started ||
                retryAttempt >= MAX_RUNTIME_RETRIES
            ) return
            retryAttempt++
            retryJob = scope.launch {
                retryDelay.await(RETRY_COOLDOWN_MILLIS)
                synchronized(lock) { retryJob = null }
                wake.trySend(Unit)
            }
        }
    }

    private fun captureGateLocked(): Gate? {
        if (relayGeneration <= 0) {
            publishGateStateLocked()
            return null
        }
        val selectedNetwork = network
        if (selectedNetwork == null) {
            publishGateStateLocked()
            return null
        }
        val selectedPeer = peers.firstOrNull()
        if (selectedPeer == null) {
            publishGateStateLocked()
            return null
        }
        return Gate(
            authenticationGeneration = authenticationGeneration,
            relayGeneration = relayGeneration,
            networkGeneration = networkGeneration,
            network = selectedNetwork,
            peerGeneration = peerGeneration,
            peer = selectedPeer.ownedCopy(),
        )
    }

    private fun gateStillCurrentLocked(gate: Gate): Boolean =
        gate.authenticationGeneration == authenticationGeneration &&
            gate.relayGeneration == relayGeneration && relayGeneration > 0 &&
            gate.networkGeneration == networkGeneration && sameNetwork(gate.network, network) &&
            gate.peerGeneration == peerGeneration &&
            peers.any { it.deviceId == gate.peer.deviceId && it.keyEpoch == gate.peer.keyEpoch }

    private fun publishGateStateLocked(reason: String = "") {
        if (!enabled || closed.get()) return
        val state = when {
            relayGeneration <= 0 -> ProductionDirectShadowState.WAITING_FOR_RELAY
            network == null -> ProductionDirectShadowState.WAITING_FOR_NETWORK
            peers.isEmpty() -> ProductionDirectShadowState.WAITING_FOR_PEER
            active == null -> ProductionDirectShadowState.RELAY_ONLY
            else -> mutableSnapshot.value.state
        }
        mutableSnapshot.value = ProductionDirectShadowSnapshot(
            state = state,
            reasonCode = reason,
            authenticationGeneration = authenticationGeneration,
            relayGeneration = relayGeneration,
            networkGeneration = networkGeneration,
            pathEpoch = active?.ticket?.pathEpoch ?: 0,
            attemptId = active?.attemptId,
            peerDeviceId = active?.peer?.deviceId,
        )
    }

    private fun snapshotFor(
        state: ProductionDirectShadowState,
        reason: String,
        attempt: Attempt?,
    ) = ProductionDirectShadowSnapshot(
        state = state,
        reasonCode = reason,
        authenticationGeneration = authenticationGeneration,
        relayGeneration = relayGeneration,
        networkGeneration = networkGeneration,
        pathEpoch = attempt?.ticket?.pathEpoch ?: 0,
        attemptId = attempt?.attemptId,
        peerDeviceId = attempt?.peer?.deviceId,
    )

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        val attempt: Attempt?
        val retiredPeers: List<ProductionDirectAuthorizedPeer>
        synchronized(lock) {
            attempt = active
            active = null
            retiredPeers = peers
            peers = emptyList()
            mutableSnapshot.value = ProductionDirectShadowSnapshot(
                state = ProductionDirectShadowState.STOPPED,
                reasonCode = "STOPPED",
                authenticationGeneration = authenticationGeneration,
            )
        }
        attempt?.retire("STOPPED", networkChanged = false)
        retiredPeers.forEach(ProductionDirectAuthorizedPeer::close)
        attemptJob?.cancel()
        retryJob?.cancel()
        wake.close()
        relayJob.cancel()
        coordinator.cancel()
        scopeJob.cancel()
    }

    suspend fun closeAndJoin() {
        close()
        attemptJob?.join()
        retryJob?.join()
        relayJob.join()
        coordinator.join()
    }

    private inner class Attempt(
        val gate: Gate,
        val peer: ProductionDirectAuthorizedPeer,
        val signaling: ProductionDirectShadowSignaling,
        val connectionId: UUID,
        val attemptId: UUID,
        val ticket: dev.termlink.protocol.transport.v1.DirectPathTicketV1,
        val ticketWire: ByteArray,
        val config: DirectTransportServerConfig,
        val prepared: PreparedDirectGather,
        val multipairV2: Boolean,
    ) {
        val retired = AtomicBoolean(false)
        val shadowReady = AtomicBoolean(false)
        val native = AtomicReference<ProductionDirectShadowNative?>(null)
        val nativeV4 = AtomicReference<ProductionDirectShadowNativeV4?>(null)
        val attemptBytes = uuidBytes(attemptId)
        var identityReady = false
        var candidatesReady = false
        var offerSent = false
        var nominated = false
        var localSequence = 0L
        var lastRemoteSequence = 0L
        var lastRemoteAck = 0L
        var offerSequence = 0L
        var lastNativeGeneration = 0L
        var candidatesGeneration = 0L
        var localCertificateSha256 = ByteArray(0)
        var nativeCandidates: List<DirectNativeSocketAddress> = emptyList()
        var localCandidates: List<DirectCandidateV1> = emptyList()
        var boundEndpoint: DirectNativeSocketAddress? = null
        val signedRequests = HashSet<Long>()
        var lastCandidateBatchV4 = 0
        var localCandidatesV4: List<DirectNativeCandidateV4> = emptyList()
        var localGatheringCompleteV4 = false
        var remoteCandidatesV4: List<DirectCandidateV1> = emptyList()
        var remoteGatheringCompleteV4 = false
        var answerReceived = false
        var peerCertificateSha256V4 = ByteArray(0)
        var checksStartedV4 = false
        var checkPlanCompleteV4 = false
        var lastPairBatchV4 = 0
        var checkRoundIdV4 = ByteArray(0)
        var connectivityNonceV4 = ByteArray(0)
        val submittedPairsV4 = ArrayList<DirectNativeCandidatePairV4>()
        val validatedPairRanks = sortedSetOf<Int>()
        var checksComplete = false
        var nominationV2Sent = false
        var nominationCommitted = false
        var nominationReadyV4 = false
        var nominationIdV4 = ByteArray(0)
        var winningPairV4: DirectNativeCandidatePairV4? = null
        var localNominationResultSentV4 = false
        var localPathBindingV4 = ByteArray(0)
        var remotePathBindingV4 = ByteArray(0)

        fun retire(reason: String, networkChanged: Boolean) {
            if (!retired.compareAndSet(false, true)) return
            val currentNative = native.getAndSet(null)
            if (currentNative != null) {
                runCatching {
                    if (networkChanged) currentNative.networkChanged(ticket.pathEpoch, attemptBytes)
                    else currentNative.cancel(ticket.pathEpoch, attemptBytes)
                }
                runCatching(currentNative::close)
            }
            val currentNativeV4 = nativeV4.getAndSet(null)
            if (currentNativeV4 != null) {
                runCatching {
                    if (networkChanged) currentNativeV4.networkChanged(ticket.pathEpoch, attemptBytes)
                    else currentNativeV4.cancel(ticket.pathEpoch, attemptBytes)
                }
                runCatching(currentNativeV4::close)
            }
            runCatching(signaling::close)
            releaseNegotiationSecrets()
            synchronized(lock) {
                if (active === this) {
                    active = null
                    if (!closed.get()) {
                        mutableSnapshot.value = snapshotFor(
                            if (relayGeneration > 0) ProductionDirectShadowState.RELAY_ONLY
                            else ProductionDirectShadowState.WAITING_FOR_RELAY,
                            reason,
                            this,
                        )
                    }
                }
            }
            destroyIdentifiers()
        }

        fun releaseNegotiationSecrets() {
            peer.close()
            ticketWire.fill(0)
            localCertificateSha256.fill(0)
            peerCertificateSha256V4.fill(0)
            checkRoundIdV4.fill(0)
            connectivityNonceV4.fill(0)
            nominationIdV4.fill(0)
            localPathBindingV4.fill(0)
            remotePathBindingV4.fill(0)
            nativeCandidates.forEach { it.ipAddress.fill(0) }
            localCandidatesV4.forEach {
                it.candidateId.fill(0)
                it.relatedCandidateId.fill(0)
                it.endpoint.ipAddress.fill(0)
            }
            boundEndpoint?.ipAddress?.fill(0)
        }


        fun destroyIdentifiers() {
            attemptBytes.fill(0)
        }
    }

    private data class CandidateBatchV4(
        val sequence: Int,
        val total: Int,
        val complete: Boolean,
        val candidates: List<DirectNativeCandidateV4>,
    )

    private data class ChecksCompleteV4(
        val total: Int,
        val validated: Int,
        val exhausted: Int,
    )

    private data class NominationReadyV4(
        val nominationId: ByteArray,
        val pair: DirectNativeCandidatePairV4,
    )

    private data class ShadowReadyV4(
        val nominationId: ByteArray,
        val pair: DirectNativeCandidatePairV4,
        val binding: ByteArray,
    )

    private data class SelectableCandidate(
        val wire: DirectCandidateV1,
        val address: InetAddress,
    )

    private data class SelectableCandidatePair(
        val local: SelectableCandidate,
        val remote: SelectableCandidate,
    ) {
        fun hasKinds(localKind: DirectCandidateKindV1, remoteKind: DirectCandidateKindV1): Boolean =
            local.wire.kind == localKind && remote.wire.kind == remoteKind

        fun isSameLanHostPair(): Boolean {
            if (!hasKinds(
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                    DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                ) || !DirectAddressPolicy.isLanHostAddress(local.address) ||
                !DirectAddressPolicy.isLanHostAddress(remote.address)
            ) return false
            val localBytes = local.address.address
            val remoteBytes = remote.address.address
            val prefixBytes = when {
                local.address is Inet4Address && remote.address is Inet4Address -> 3
                local.address is Inet6Address && remote.address is Inet6Address -> 8
                else -> return false
            }
            return (0 until prefixBytes).all { localBytes[it] == remoteBytes[it] }
        }
    }

    private data class Gate(
        val authenticationGeneration: Long,
        val relayGeneration: Int,
        val networkGeneration: Long,
        val network: CapturedDirectNetwork,
        val peerGeneration: Long,
        val peer: ProductionDirectAuthorizedPeer,
    )

    private class DirectAttemptFailure(val reason: String) : IllegalStateException(reason)

    private fun nonZeroVersion4Uuid(): UUID {
        repeat(16) {
            val value = uuidSource()
            if (value != NIL_UUID && value.version() == 4) return value
        }
        throw IllegalStateException("DIRECT_UUID_SOURCE_INVALID")
    }

    private fun randomIdentifier(): ByteArray {
        repeat(16) {
            val value = ByteArray(16).also(random::nextBytes)
            if (value.any { it != 0.toByte() }) return value
        }
        throw IllegalStateException("DIRECT_RANDOM_SOURCE_INVALID")
    }

    private fun sameNetwork(left: CapturedDirectNetwork?, right: CapturedDirectNetwork?): Boolean =
        left === right || (left != null && right != null && left.token == right.token &&
            left.handle == right.handle &&
            left.advertisedAddress.address.contentEquals(right.advertisedAddress.address))

    private fun nextGeneration(value: Long): Long {
        check(value < Long.MAX_VALUE) { "Direct generation exhausted" }
        return value + 1
    }

    private companion object {
        const val PROTOCOL_VERSION = 1
        const val GATHER_TIMEOUT_MILLIS = 1_000
        const val UNSAFE_HOST_PAIR_PREFERENCE = 4
        const val NOMINATE_TIMEOUT_MILLIS = 5_000
        const val CHECKS_TIMEOUT_MILLIS = 10_000
        const val ATTEMPT_TIMEOUT_MILLIS = 12_000L
        const val RETRY_COOLDOWN_MILLIS = 30_000L
        const val POLL_INTERVAL_MILLIS = 10L
        const val MAX_NATIVE_EVENTS_PER_POLL = 16
        const val MAX_INCOMING_SIGNALS = 32
        const val MAX_RUNTIME_RETRIES = 3
        const val CANDIDATE_ID_BYTES = 16
        const val MAX_CANDIDATE_PAIRS = 32
        const val MAX_SIGNAL_CANDIDATES = 8
        const val MAX_MULTIPAIR_CANDIDATES = 32
        const val HOST_PRIORITY = 2_000_000_000
        const val V4_CANDIDATE_BYTES = 128
        const val V4_PAIR_BYTES = 288
        const val V4_OPAQUE_BYTES = 2_080
        const val V4_CANDIDATE_BATCH_BYTES = 1_088
        const val V4_PAIR_VALIDATED_BYTES = 360
        const val V4_CHECKS_COMPLETE_BYTES = 80
        const val V4_NOMINATION_READY_BYTES = 376
        const val V4_SHADOW_READY_BYTES = 400
    }
}

private fun verifyEd25519(publicKey: ByteArray, message: ByteArray, signature: ByteArray): Boolean =
    runCatching {
        if (publicKey.size != 32 || signature.size != 64) return@runCatching false
        val encoded = ED25519_SPKI_PREFIX + publicKey
        try {
            val key = KeyFactory.getInstance("Ed25519").generatePublic(X509EncodedKeySpec(encoded))
            Signature.getInstance("Ed25519").run {
                initVerify(key)
                update(message)
                verify(signature)
            }
        } finally {
            encoded.fill(0)
        }
    }.getOrDefault(false)

private fun sha256(value: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(value)

private fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
    .putLong(value.mostSignificantBits)
    .putLong(value.leastSignificantBits)
    .array()

private fun uuidFromBytes(value: ByteArray): UUID {
    require(value.size == 16) { "Direct UUID bytes are invalid" }
    val buffer = ByteBuffer.wrap(value).order(ByteOrder.BIG_ENDIAN)
    return UUID(buffer.long, buffer.long)
}

private val NIL_UUID = UUID(0L, 0L)
private val ED25519_SPKI_PREFIX = byteArrayOf(
    0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00,
)

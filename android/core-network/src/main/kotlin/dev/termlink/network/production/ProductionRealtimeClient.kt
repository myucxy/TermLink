package dev.termlink.network.production

import android.util.Log
import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import java.io.Closeable
import java.net.URI
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString as OkioByteString

sealed interface RealtimeState {
    data object Connecting : RealtimeState
    data object Connected : RealtimeState
    data class Reconnecting(val attempt: Int, val reason: String? = null) : RealtimeState
    data class AuthenticationRejected(val status: Int, val reason: String) : RealtimeState
    data class AuthenticationFailed(val reason: String) : RealtimeState
    data class Failed(val reason: String) : RealtimeState
    data object Closed : RealtimeState
}

data class ProductionReceivePressure(
    val generation: Int,
    val paused: Boolean,
    val bufferedEvents: Int,
    val bufferedWireBytes: Long,
)

data class ProductionRealtimeSocketDiagnostics(
    val generation: Int,
    val connectedGeneration: Int,
    val socketPresent: Boolean,
    val state: String,
    val bufferedEvents: Int,
    val bufferedWireBytes: Long,
    /** Valid application frames whose local admission decision has completed. */
    val observedApplicationFrames: Long = 0,
    /** Observed application frames not admitted to the bounded local receive channel. */
    val droppedApplicationFrames: Long = 0,
)

/**
 * Capabilities actually negotiated for one physical WebSocket generation.
 * An empty set is published before each generation starts and whenever its
 * connection is retired, so a capability from an older socket cannot be used
 * to authorize work on a replacement socket.
 */
data class ProductionRealtimeNegotiatedCapabilities(
    val generation: Int,
    val capabilities: Set<String>,
)

private val REALTIME_CAPABILITY_IDENTIFIER = Regex("[a-z][a-z0-9._-]{0,63}")

internal fun productionRealtimeCapabilities(
    required: Set<String>,
    optionalAdvertised: Set<String> = emptySet(),
): List<String> {
    require((required + optionalAdvertised).all(REALTIME_CAPABILITY_IDENTIFIER::matches)) {
        "Realtime capabilities must use stable protocol identifiers"
    }
    return (listOf(
        "cumulative_ack",
        "e2ee_x25519_chacha20poly1305",
        "flow_control_v1",
        "lease_v1",
        "liveness_probe_v1",
        "bounded_event_replay_v1",
        "terminal_presentation_metadata_v1",
        "terminal_typed_input_v1",
        "host_history_v1",
    ) + required + optionalAdvertised).distinct().sorted()
}

/**
 * Owns one exact wire representation of an Envelope.
 *
 * The byte array is deliberately exposed as a borrowed view: callers must use
 * it before releasing their [ProductionReceivedEnvelope].  Sharing this owner
 * between wrappers keeps a root reservation from clearing bytes still needed
 * by a downstream view.  The last release clears the backing array exactly
 * once.
 */
internal class ProductionRawEnvelopeOwner private constructor(
    private val bytes: ByteArray,
) : AutoCloseable {
    private val references = AtomicInteger(1)
    private val cleared = AtomicBoolean(false)

    fun retain(): ProductionRawEnvelopeOwner {
        while (true) {
            val current = references.get()
            check(current > 0 && !cleared.get()) { "raw Envelope bytes have been released" }
            if (references.compareAndSet(current, current + 1)) return this
        }
    }

    fun borrowedBytes(): ByteArray {
        check(!cleared.get()) { "raw Envelope bytes have been released" }
        return bytes
    }

    fun copyBytes(): ByteArray = borrowedBytes().copyOf()

    override fun close() {
        val remaining = references.decrementAndGet()
        check(remaining >= 0) { "raw Envelope owner released more than once" }
        if (remaining == 0 && cleared.compareAndSet(false, true)) {
            bytes.fill(0)
        }
    }

    companion object {
        fun take(bytes: ByteArray): ProductionRawEnvelopeOwner {
            return ProductionRawEnvelopeOwner(bytes)
        }
    }
}

/**
 * Authenticated transport owner of one exact Envelope serialization.
 *
 * [ProductionRelayCarrierOwner] is fenced by the physical Relay generation. A
 * Direct owner carries the complete native path fence; an epoch on its own is
 * deliberately never sufficient to authorize delivery.
 */
internal sealed interface ProductionEnvelopeCarrierOwner

internal data class ProductionRelayCarrierOwner(
    val generation: Int,
) : ProductionEnvelopeCarrierOwner {
    init {
        require(generation > 0) { "Relay carrier generation must be positive" }
    }
}

internal class ProductionDirectCarrierOwner(
    val peerDeviceId: UUID,
    val pathEpoch: Long,
    connectionId: ByteArray,
    attemptId: ByteArray,
) : ProductionEnvelopeCarrierOwner {
    val connectionId: ByteArray = connectionId.copyOf()
    val attemptId: ByteArray = attemptId.copyOf()

    init {
        require(peerDeviceId != UUID(0, 0)) { "Direct peer device ID is required" }
        require(pathEpoch > 0) { "Direct path epoch must be positive" }
        require(this.connectionId.size == 16 && this.connectionId.any { it != 0.toByte() }) {
            "Direct connection ID must contain 16 non-zero bytes"
        }
        require(this.attemptId.size == 16 && this.attemptId.any { it != 0.toByte() }) {
            "Direct attempt ID must contain 16 non-zero bytes"
        }
    }

    internal fun ownedCopy(): ProductionDirectCarrierOwner = ProductionDirectCarrierOwner(
        peerDeviceId = peerDeviceId,
        pathEpoch = pathEpoch,
        connectionId = connectionId,
        attemptId = attemptId,
    )
}

class ProductionReceivedEnvelope internal constructor(
    val envelope: Envelope,
    val wireBytes: Int,
    val generation: Int,
    private val releaseReservation: (Int) -> Unit,
    rawBytes: ByteArray? = null,
    rawOwner: ProductionRawEnvelopeOwner? = null,
    internal val carrierOwner: ProductionEnvelopeCarrierOwner =
        ProductionRelayCarrierOwner(generation),
) : AutoCloseable {
    init {
        require(rawBytes == null || rawOwner == null) {
            "raw Envelope bytes and owner cannot both be supplied"
        }
    }

    private val ownedRawBytes = rawOwner ?: ProductionRawEnvelopeOwner.take(
        rawBytes ?: envelope.toByteArray(),
    )
    private val released = AtomicBoolean(false)

    /** Borrowed exact bytes received on the wire; do not retain after [close]. */
    val rawBytes: ByteArray
        get() = ownedRawBytes.borrowedBytes()

    /** Alias that makes the no-reserialize contract explicit to transport callers. */
    val exactBytes: ByteArray
        get() = ownedRawBytes.borrowedBytes()

    /** Copies the exact bytes while this reservation is still live. */
    fun copyRawBytes(): ByteArray = ownedRawBytes.copyBytes()

    override fun close() {
        if (released.compareAndSet(false, true)) {
            try {
                releaseReservation(wireBytes)
            } finally {
                ownedRawBytes.close()
            }
        }
    }

    /**
     * Transfers ownership to a close-observed view without copying opaque
     * ciphertext. The observer runs exactly once when downstream processing
     * releases the frame, including channel cancellation paths.
     */
    internal fun withCloseObserver(observer: () -> Unit): ProductionReceivedEnvelope {
        val retainedOwner = ownedRawBytes.retain()
        return try {
            ProductionReceivedEnvelope(
                envelope = envelope,
                wireBytes = wireBytes,
                generation = generation,
                releaseReservation = {
                    try {
                        this.close()
                    } finally {
                        observer()
                    }
                },
                rawOwner = retainedOwner,
                carrierOwner = when (val owner = carrierOwner) {
                    is ProductionRelayCarrierOwner -> owner.copy()
                    is ProductionDirectCarrierOwner -> owner.ownedCopy()
                },
            )
        } catch (error: Throwable) {
            retainedOwner.close()
            throw error
        }
    }
}

/**
 * The production WebSocket transport. It only handles authenticated socket
 * framing and opaque protobuf Envelopes; session keys and payload semantics
 * live in [ProductionRealtimeSessionClient].
 */
internal interface ProductionRealtimeSocket : Closeable {
    val receivedEnvelopes: Flow<ProductionReceivedEnvelope>
    val receivePressure: StateFlow<ProductionReceivePressure>
    val state: SharedFlow<RealtimeState>
    /** Exact capability echo for the current authenticated socket generation. */
    val negotiatedCapabilities: StateFlow<ProductionRealtimeNegotiatedCapabilities>
        get() = EMPTY_REALTIME_NEGOTIATED_CAPABILITIES

    fun connect()
    fun send(envelope: Envelope): Boolean
    /** Sends [exactBytes] without protobuf parse/re-serialization on the wire. */
    fun sendExact(envelope: Envelope, exactBytes: ByteArray): Boolean = send(envelope)
    fun supportsLivenessProbe(expectedGeneration: Int): Boolean = false
    suspend fun probeLiveness(expectedGeneration: Int, timeoutMillis: Long): Boolean = false
    fun disconnect(reason: String)
    fun diagnostics(): ProductionRealtimeSocketDiagnostics = ProductionRealtimeSocketDiagnostics(
        generation = 0,
        connectedGeneration = 0,
        socketPresent = false,
        state = "UNKNOWN",
        bufferedEvents = 0,
        bufferedWireBytes = 0,
    )
}

private val EMPTY_REALTIME_NEGOTIATED_CAPABILITIES = MutableStateFlow(
    ProductionRealtimeNegotiatedCapabilities(0, emptySet()),
)

class ProductionRealtimeClient(
    rootUrl: String,
    private val tokenProvider: AccessTokenProvider,
    private val deviceId: UUID,
    private val identity: DeviceIdentity,
    private val role: String,
    additionalCapabilities: Set<String> = emptySet(),
    httpClient: OkHttpClient = defaultHttpClient,
    allowCleartextForLocalTest: Boolean = false,
    optionalAdvertisedCapabilities: Set<String> = emptySet(),
) : ProductionRealtimeSocket {
    private val websocketUrl = normalizeWebSocket(rootUrl, allowCleartextForLocalTest) + "/v1/realtime"
    // Callers normally share their REST client with realtime. REST defaults
    // are unsafe for a long-lived WebSocket: a finite read timeout can close
    // healthy idle terminals, while no ping leaves a connection half-open
    // after Android changes network. Preserve the caller's dispatcher, pool,
    // TLS and proxy configuration, but always enforce realtime liveness.
    private val httpClient = normalizedWebSocketHttpClient(httpClient)
    private val closed = AtomicBoolean(false)
    private val generation = AtomicInteger(0)
    private val lastIncomingSequences = ConcurrentHashMap<IncomingCursor, java.util.concurrent.atomic.AtomicLong>()
    private val bufferedIncomingEvents = AtomicInteger(0)
    private val bufferedIncomingWireBytes = AtomicLong(0)
    private val observedApplicationFrames = AtomicLong(0)
    private val droppedApplicationFrames = AtomicLong(0)
    private val receivePressurePaused = AtomicBoolean(false)
    private val incomingChannel = Channel<ProductionReceivedEnvelope>(
        capacity = MAX_BUFFERED_EVENTS,
        onUndeliveredElement = { it.close() },
    )
    private val mutableReceivePressure = MutableStateFlow(
        ProductionReceivePressure(0, false, 0, 0),
    )
    private val mutableState = MutableSharedFlow<RealtimeState>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    // `additionalCapabilities` remains the source-compatible required set.
    // Optional capabilities are advertised in Hello but never required in
    // Welcome. Snapshot both inputs so a mutable caller Set cannot alter the
    // protocol contract between reconnects.
    private val requiredWelcomeCapabilities = additionalCapabilities.toSet()
    private val optionalHelloCapabilities = optionalAdvertisedCapabilities.toSet()
    private val advertisedCapabilities = productionRealtimeCapabilities(
        required = requiredWelcomeCapabilities,
        optionalAdvertised = optionalHelloCapabilities,
    )
    private val advertisedCapabilitySet = advertisedCapabilities.toSet()
    private val sendLock = Any()
    @Volatile private var socket: WebSocket? = null
    @Volatile private var connectedGeneration: Int = 0
    private var connectedCapabilities: Set<String> = emptySet()
    private var pendingLivenessProbe: PendingLivenessProbe? = null

    private val mutableNegotiatedCapabilities = MutableStateFlow(
        ProductionRealtimeNegotiatedCapabilities(0, emptySet()),
    )

    private val incomingReceived: Flow<ProductionReceivedEnvelope> = incomingChannel.receiveAsFlow()
    // Compatibility view for transport-level callers that only need the
    // immutable Envelope. A device multiplexer may detach the physical-socket
    // reservation after routing and replace it with an endpoint-local budget;
    // direct consumers still release this reservation after processing.
    val incoming: Flow<Envelope> = incomingReceived.map { received ->
        try { received.envelope } finally { received.close() }
    }
    override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incomingReceived
    override val receivePressure: StateFlow<ProductionReceivePressure> = mutableReceivePressure.asStateFlow()
    override val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()
    override val negotiatedCapabilities: StateFlow<ProductionRealtimeNegotiatedCapabilities> =
        mutableNegotiatedCapabilities.asStateFlow()

    init {
        require(role == "pc" || role == "android") { "role must be pc or android" }
    }

    /** Starts one socket generation. Reconnect policy is owned by the session bridge. */
    override fun connect() {
        check(!closed.get()) { "realtime client is closed" }
        val nextGeneration = generation.incrementAndGet()
        synchronized(sendLock) {
            cancelPendingLivenessProbeLocked()
            socket?.cancel()
            socket = null
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(nextGeneration, emptySet())
        }
        // A new socket can replay from the session ACK. Keep duplicate
        // rejection within one generation, while allowing a durable replay
        // frame that was queued but not consumed before disconnect.
        lastIncomingSequences.clear()
        mutableState.tryEmit(RealtimeState.Connecting)
        val opened = try {
            val target = URI(websocketUrl).rawPath
            val proof = DeviceProof.create(identity, deviceId.toString(), "GET", target, ByteArray(0))
            val request = Request.Builder()
                .url(websocketUrl)
                .header("Authorization", "Bearer ${tokenProvider.accessToken()}")
                .header("X-TermLink-Device", proof.deviceId)
                .header("X-TermLink-Timestamp", proof.timestampMillis.toString())
                .header("X-TermLink-Nonce", proof.nonce)
                .header("X-TermLink-Signature", proof.signature)
                .header("Sec-WebSocket-Protocol", "termlink.realtime.v1")
                .build()
            httpClient.newWebSocket(request, Listener(nextGeneration))
        } catch (error: Exception) {
            if (!closed.get() && generation.get() == nextGeneration) {
                mutableState.tryEmit(
                    RealtimeState.Failed(error.message ?: "realtime connection could not start"),
                )
            }
            return
        }
        synchronized(sendLock) {
            if (!closed.get() && generation.get() == nextGeneration && (socket == null || socket === opened)) {
                socket = opened
            } else {
                opened.cancel()
            }
        }
    }

    /** Sends a validated opaque Envelope on the currently connected socket. */
    override fun send(envelope: Envelope): Boolean {
        val bytes = envelope.toByteArray()
        return try {
            sendExact(envelope, bytes)
        } finally {
            bytes.fill(0)
        }
    }

    /**
     * Sends the caller-provided protobuf bytes verbatim. Parsing only
     * authenticates that the bytes describe [envelope]; the WebSocket payload
     * is built from [exactBytes] itself so unknown fields and field ordering are
     * preserved for Relay fallback.
     */
    override fun sendExact(envelope: Envelope, exactBytes: ByteArray): Boolean {
        validateOutgoingEnvelope(envelope)
        require(exactBytes.isNotEmpty() && exactBytes.size <= MAX_FRAME_BYTES) {
            "exact Envelope bytes must be between 1 and $MAX_FRAME_BYTES bytes"
        }
        val parsed = runCatching { Envelope.parseFrom(exactBytes) }.getOrElse { error ->
            throw IllegalArgumentException("exact Envelope bytes are malformed", error)
        }
        require(parsed == envelope) {
            "exact Envelope bytes do not match the parsed Envelope"
        }
        return synchronized(sendLock) {
            val active = socket
            if (active == null || connectedGeneration != generation.get()) return@synchronized false
            active.send(OkioByteString.of(*exactBytes))
        }
    }

    private fun validateOutgoingEnvelope(envelope: Envelope) {
        require(envelope.protocolVersion == 1) { "unsupported Envelope protocol version" }
        require(envelope.senderDeviceId.toByteArray().contentEquals(uuidBytes(deviceId))) {
            "Envelope sender does not match this device"
        }
        require(envelope.sessionId.size() == 16) { "Envelope session ID must be 16 bytes" }
        require(envelope.recipientDeviceId.isEmpty || envelope.recipientDeviceId.size() == 16) {
            "Envelope recipient must be empty or 16 bytes"
        }
    }

    override fun supportsLivenessProbe(expectedGeneration: Int): Boolean = synchronized(sendLock) {
        !closed.get() && expectedGeneration > 0 && generation.get() == expectedGeneration &&
            connectedGeneration == expectedGeneration && socket != null &&
            LIVENESS_PROBE_CAPABILITY in connectedCapabilities
    }

    /**
     * Proves application-level round-trip liveness on one authenticated socket
     * generation. OkHttp's WebSocket ping interval detects dead transports but
     * does not expose Pong completion, so foreground recovery uses this bounded
     * negotiated control without touching terminal Envelope ordering.
     */
    override suspend fun probeLiveness(expectedGeneration: Int, timeoutMillis: Long): Boolean {
        require(expectedGeneration > 0) { "expected generation must be positive" }
        require(timeoutMillis > 0) { "liveness timeout must be positive" }
        val probe = PendingLivenessProbe(
            generation = expectedGeneration,
            probeId = UUID.randomUUID().toString(),
            completion = CompletableDeferred(),
        )
        val sent = synchronized(sendLock) {
            val active = socket
            if (closed.get() || active == null || generation.get() != expectedGeneration ||
                connectedGeneration != expectedGeneration ||
                LIVENESS_PROBE_CAPABILITY !in connectedCapabilities || pendingLivenessProbe != null
            ) {
                false
            } else {
                pendingLivenessProbe = probe
                val control = buildJsonObject {
                    put("type", "ping")
                    put("probe_id", probe.probeId)
                }
                active.send(Json.encodeToString(JsonObject.serializer(), control)).also { accepted ->
                    if (!accepted && pendingLivenessProbe === probe) pendingLivenessProbe = null
                }
            }
        }
        if (!sent) return false

        val alive = try {
            withTimeoutOrNull(timeoutMillis) { probe.completion.await() } == true &&
                supportsLivenessProbe(expectedGeneration)
        } finally {
            // Endpoint removal, network replacement, and owner shutdown may
            // cancel the supervisor coroutine before its timeout. Never leave
            // that cancelled probe occupying the one-probe generation gate.
            synchronized(sendLock) {
                if (pendingLivenessProbe === probe) pendingLivenessProbe = null
            }
        }
        Log.d(
            REALTIME_LOG_TAG,
            "socket_liveness_probe_result generation=$expectedGeneration alive=$alive",
        )
        return alive
    }

    /** Closes only the current generation, allowing the owner to reconnect. */
    override fun disconnect(reason: String) {
        if (closed.get()) return
        val active = synchronized(sendLock) {
            val retiredGeneration = generation.incrementAndGet()
            cancelPendingLivenessProbeLocked()
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(retiredGeneration, emptySet())
            val current = socket
            socket = null
            current
        }
        active?.close(1002, reason.take(120))
        mutableState.tryEmit(RealtimeState.Failed(reason))
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        synchronized(sendLock) {
            val retiredGeneration = generation.incrementAndGet()
            cancelPendingLivenessProbeLocked()
            socket?.close(1000, "client closed")
            socket = null
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(retiredGeneration, emptySet())
        }
        mutableState.tryEmit(RealtimeState.Closed)
        lastIncomingSequences.clear()
        incomingChannel.cancel()
    }

    override fun diagnostics(): ProductionRealtimeSocketDiagnostics = ProductionRealtimeSocketDiagnostics(
        generation = generation.get(),
        connectedGeneration = connectedGeneration,
        socketPresent = socket != null,
        state = mutableState.replayCache.lastOrNull().diagnosticName(),
        bufferedEvents = bufferedIncomingEvents.get(),
        bufferedWireBytes = bufferedIncomingWireBytes.get(),
        observedApplicationFrames = observedApplicationFrames.get(),
        droppedApplicationFrames = droppedApplicationFrames.get(),
    )

    private inner class Listener(private val listenerGeneration: Int) : WebSocketListener() {
        private var welcomed = false
        private var acceptingBinary = true

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (!isCurrent(webSocket)) return
            val hello = buildJsonObject {
                put("role", role)
                putJsonArray("protocol_versions") { add(kotlinx.serialization.json.JsonPrimitive(1)) }
                putJsonArray("capabilities") {
                    advertisedCapabilities.forEach { add(kotlinx.serialization.json.JsonPrimitive(it)) }
                }
            }
            if (!webSocket.send(Json.encodeToString(JsonObject.serializer(), hello))) {
                webSocket.close(1011, "hello send failed")
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isCurrent(webSocket)) return
            if (text.length > MAX_HELLO_BYTES) {
                acceptingBinary = false
                webSocket.close(1009, "welcome control too large")
                return
            }
            if (!welcomed) {
                val negotiated = validateWelcome(text)
                if (negotiated != null) {
                    val accepted = synchronized(sendLock) {
                        if (closed.get() || socket !== webSocket ||
                            generation.get() != listenerGeneration
                        ) {
                            false
                        } else {
                            welcomed = true
                            connectedGeneration = listenerGeneration
                            connectedCapabilities = negotiated
                            publishNegotiatedCapabilitiesLocked(listenerGeneration, negotiated)
                            true
                        }
                    }
                    if (accepted) mutableState.tryEmit(RealtimeState.Connected)
                    return
                }
            } else if (handleLivenessPong(text)) {
                return
            }
            acceptingBinary = false
            webSocket.close(
                1002,
                if (welcomed) "invalid realtime control" else "invalid welcome control",
            )
        }

        override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
            if (!isCurrent(webSocket)) {
                logBinaryDrop("socket_binary_stale", bytes)
                return
            }
            if (!acceptingBinary) {
                logBinaryDrop("socket_binary_gated", bytes)
                return
            }
            if (!welcomed) {
                acceptingBinary = false
                webSocket.close(1002, "welcome required before binary frames")
                return
            }
            if (bytes.size > MAX_FRAME_BYTES) {
                logBinaryDrop("socket_binary_oversized", bytes)
                return
            }
            val raw = bytes.toByteArray()
            var handedOff = false
            try {
                val envelope = runCatching { Envelope.parseFrom(raw) }.getOrElse {
                    logBinaryDrop("socket_envelope_malformed", bytes)
                    return
                }
                if (!validateIncoming(envelope)) {
                    logEnvelopeAdmission("socket_envelope_metadata_rejected", envelope, bytes.size)
                    return
                }
                if (!reserveIncoming(raw.size, listenerGeneration)) {
                    droppedApplicationFrames.incrementAndGet()
                    // Publish observed last so it is also a completion watermark
                    // for the admission decision of this serial WebSocket callback.
                    observedApplicationFrames.incrementAndGet()
                    logEnvelopeAdmission(
                        decision = "socket_reservation_rejected",
                        envelope = envelope,
                        wireBytes = raw.size,
                    )
                    failReceiveIntegrity(webSocket, "realtime receive reservation overflow")
                    return
                }
                val received = try {
                    ProductionReceivedEnvelope(
                        envelope = envelope,
                        wireBytes = raw.size,
                        generation = listenerGeneration,
                        releaseReservation = ::releaseIncoming,
                        rawBytes = raw,
                    ).also { handedOff = true }
                } catch (error: Throwable) {
                    releaseIncoming(raw.size)
                    throw error
                }
                val admission = try {
                    incomingChannel.trySend(received)
                } catch (error: Throwable) {
                    received.close()
                    throw error
                }
                if (admission.isFailure) {
                    droppedApplicationFrames.incrementAndGet()
                    logEnvelopeAdmission(
                        decision = if (admission.isClosed) {
                            "socket_channel_closed"
                        } else {
                            "socket_channel_full"
                        },
                        envelope = envelope,
                        wireBytes = raw.size,
                    )
                    received.close()
                    if (!admission.isClosed) {
                        failReceiveIntegrity(webSocket, "realtime receive channel overflow")
                    }
                }
                observedApplicationFrames.incrementAndGet()
            } finally {
                // Once handed off, the owner attached to `received` controls
                // clearing. Every parse/rejection/exception path still clears
                // the callback's temporary copy here.
                if (!handedOff) raw.fill(0)
            }
        }

        private fun failReceiveIntegrity(webSocket: WebSocket, reason: String) {
            if (!isCurrent(webSocket) || !acceptingBinary) return
            acceptingBinary = false
            // Selectively dropping authenticated frames would make the current
            // generation look healthy while guaranteeing a later sequence gap.
            // Retire it immediately; durable ACK replay restores every session.
            this@ProductionRealtimeClient.disconnect(reason)
        }

        /**
         * Parse only a bounded rejected frame and expose metadata, never the
         * ciphertext or identifiers. This locates a missing sequence without
         * turning diagnostics into another payload channel.
         */
        private fun logBinaryDrop(decision: String, bytes: OkioByteString) {
            val envelope = if (bytes.size <= MAX_FRAME_BYTES) {
                val raw = bytes.toByteArray()
                try {
                    runCatching { Envelope.parseFrom(raw) }.getOrNull()
                } finally {
                    raw.fill(0)
                }
            } else {
                null
            }
            if (envelope == null) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "$decision generation=$listenerGeneration " +
                        "current_generation=${generation.get()} " +
                        "connected_generation=$connectedGeneration",
                )
                return
            }
            logEnvelopeAdmission(decision, envelope, bytes.size)
        }

        private fun logEnvelopeAdmission(
            decision: String,
            envelope: Envelope,
            wireBytes: Int,
        ) {
            Log.w(
                REALTIME_LOG_TAG,
                "$decision generation=$listenerGeneration " +
                    "current_generation=${generation.get()} " +
                    "connected_generation=$connectedGeneration " +
                    "seq=${envelope.seq} type=${envelope.messageType.name} " +
                    "queue_events=${bufferedIncomingEvents.get()} " +
                    "queue_wire_bytes=${bufferedIncomingWireBytes.get()} " +
                    "wire_bytes=$wireBytes",
            )
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (!isCurrent(webSocket) || closed.get()) {
                response?.close()
                return
            }
            acceptingBinary = false
            synchronized(sendLock) {
                cancelPendingLivenessProbeLocked(listenerGeneration)
                retireNegotiatedCapabilitiesLocked(listenerGeneration)
            }
            val status = response?.code
            val reason = t.message ?: "realtime failed"
            response?.close()
            mutableState.tryEmit(
                if (status == 401 || status == 403) {
                    RealtimeState.AuthenticationRejected(status, reason)
                } else {
                    RealtimeState.Failed(reason)
                },
            )
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (!isCurrent(webSocket) || closed.get()) return
            acceptingBinary = false
            synchronized(sendLock) {
                cancelPendingLivenessProbeLocked(listenerGeneration)
                retireNegotiatedCapabilitiesLocked(listenerGeneration)
            }
            mutableState.tryEmit(RealtimeState.Failed(reason.ifBlank { "socket closed ($code)" }))
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            if (isCurrent(webSocket)) webSocket.close(code, reason)
        }

        /**
         * OkHttp is allowed to invoke a listener on its dispatcher before
         * newWebSocket returns to [connect]. Let that first callback claim the
         * current generation instead of dropping the only failure signal.
         */
        private fun isCurrent(webSocket: WebSocket): Boolean = synchronized(sendLock) {
            if (closed.get() || generation.get() != listenerGeneration) return@synchronized false
            when (val active = socket) {
                null -> {
                    socket = webSocket
                    true
                }
                else -> active === webSocket
            }
        }

        private fun validateWelcome(text: String): Set<String>? = runCatching {
            val value = Json.parseToJsonElement(text) as? JsonObject ?: return@runCatching null
            val type = value["type"] as? JsonPrimitive ?: return@runCatching null
            val protocolVersion = value["protocol_version"] as? JsonPrimitive
                ?: return@runCatching null
            if (!type.isString || type.content != "welcome" ||
                protocolVersion.isString || protocolVersion.content != "1"
            ) return@runCatching null

            val capabilitiesElement = value["capabilities"]
            if (capabilitiesElement == null) {
                // Older Relays did not echo negotiated capabilities. Keep that
                // compatibility only when this client did not explicitly opt
                // into an additional capability.
                return@runCatching if (requiredWelcomeCapabilities.isEmpty()) emptySet() else null
            }
            val capabilities = capabilitiesElement as? JsonArray ?: return@runCatching null
            val serverCapabilities = HashSet<String>(capabilities.size)
            for (element in capabilities) {
                val primitive = element as? JsonPrimitive ?: return@runCatching null
                if (!primitive.isString) return@runCatching null
                val capability = primitive.content
                if (!REALTIME_CAPABILITY_IDENTIFIER.matches(capability) ||
                    !serverCapabilities.add(capability)
                ) return@runCatching null
            }
            if (!serverCapabilities.containsAll(requiredWelcomeCapabilities)) return@runCatching null

            // A Relay may add future valid identifiers. Preserve the existing
            // forward-compatible handshake behavior, but only expose values
            // this client advertised as negotiated capabilities.
            serverCapabilities.filterTo(linkedSetOf()) { it in advertisedCapabilitySet }.toSet()
        }.getOrNull()

        private fun handleLivenessPong(text: String): Boolean = runCatching {
            val value = Json.parseToJsonElement(text) as? JsonObject ?: return@runCatching false
            val type = value["type"] as? JsonPrimitive ?: return@runCatching false
            val probeId = value["probe_id"] as? JsonPrimitive ?: return@runCatching false
            if (!type.isString || type.content != "pong" || !probeId.isString ||
                runCatching { UUID.fromString(probeId.content) }.getOrNull()?.toString() != probeId.content
            ) return@runCatching false

            val completion = synchronized(sendLock) {
                if (generation.get() != listenerGeneration ||
                    LIVENESS_PROBE_CAPABILITY !in connectedCapabilities
                ) return@synchronized null
                val pending = pendingLivenessProbe
                if (pending?.generation == listenerGeneration && pending.probeId == probeId.content) {
                    pendingLivenessProbe = null
                    pending.completion
                } else {
                    null
                }
            }
            // A syntactically valid late Pong is deliberately ignored. It may
            // have raced the bounded timeout and must never poison a live socket.
            completion?.complete(true)
            true
        }.getOrDefault(false)
    }

    private fun cancelPendingLivenessProbeLocked(expectedGeneration: Int? = null) {
        val pending = pendingLivenessProbe ?: return
        if (expectedGeneration != null && pending.generation != expectedGeneration) return
        pendingLivenessProbe = null
        pending.completion.complete(false)
    }

    private fun retireNegotiatedCapabilitiesLocked(expectedGeneration: Int) {
        if (connectedGeneration != expectedGeneration) return
        connectedGeneration = 0
        connectedCapabilities = emptySet()
        publishNegotiatedCapabilitiesLocked(expectedGeneration, emptySet())
    }

    private fun publishNegotiatedCapabilitiesLocked(
        capabilityGeneration: Int,
        capabilities: Set<String>,
    ) {
        mutableNegotiatedCapabilities.value = ProductionRealtimeNegotiatedCapabilities(
            generation = capabilityGeneration,
            capabilities = capabilities.toSet(),
        )
    }

    private data class PendingLivenessProbe(
        val generation: Int,
        val probeId: String,
        val completion: CompletableDeferred<Boolean>,
    )

    private fun reserveIncoming(wireBytes: Int, pressureGeneration: Int): Boolean {
        if (wireBytes <= 0 || wireBytes > MAX_FRAME_BYTES) return false
        val events = bufferedIncomingEvents.incrementAndGet()
        val bytes = bufferedIncomingWireBytes.addAndGet(wireBytes.toLong())
        if (events > MAX_BUFFERED_EVENTS || bytes > HARD_BUFFERED_WIRE_BYTES) {
            bufferedIncomingEvents.decrementAndGet()
            bufferedIncomingWireBytes.addAndGet(-wireBytes.toLong())
            return false
        }
        if (shouldPauseReceive(events, bytes) &&
            receivePressurePaused.compareAndSet(false, true)
        ) {
            publishReceivePressure(pressureGeneration)
        }
        return true
    }

    private fun releaseIncoming(wireBytes: Int) {
        val events = bufferedIncomingEvents.updateAndGet { (it - 1).coerceAtLeast(0) }
        val bytes = bufferedIncomingWireBytes.updateAndGet { (it - wireBytes).coerceAtLeast(0) }
        if (shouldResumeReceive(events, bytes) &&
            receivePressurePaused.compareAndSet(true, false)
        ) {
            publishReceivePressure(generation.get(), events, bytes)
        }
    }

    private fun publishReceivePressure(
        pressureGeneration: Int,
        events: Int = bufferedIncomingEvents.get(),
        bytes: Long = bufferedIncomingWireBytes.get(),
    ) {
        mutableReceivePressure.value = ProductionReceivePressure(
            generation = pressureGeneration,
            paused = receivePressurePaused.get(),
            bufferedEvents = events,
            bufferedWireBytes = bytes,
        )
    }

    private fun validateIncoming(envelope: Envelope): Boolean {
        if (envelope.protocolVersion != 1 || envelope.sessionId.size() != 16 ||
            envelope.senderDeviceId.size() != 16 || envelope.keyId.size() !in 1..128 ||
            envelope.nonce.size() != 12 || envelope.ciphertext.size() < 16 || envelope.seq <= 0L
        ) return false
        if (envelope.senderDeviceId.toByteArray().contentEquals(uuidBytes(deviceId))) return false
        if (envelope.recipientDeviceId.size() > 0 &&
            !envelope.recipientDeviceId.toByteArray().contentEquals(uuidBytes(deviceId))
        ) return false
        val expected = if (role == "android") Direction.PC_TO_ANDROID else Direction.ANDROID_TO_PC
        if (envelope.direction != expected) return false
        val sessionId = uuidFromBytes(envelope.sessionId.toByteArray()) ?: return false
        val senderId = uuidFromBytes(envelope.senderDeviceId.toByteArray()) ?: return false
        val cursor = lastIncomingSequences.computeIfAbsent(IncomingCursor(sessionId, senderId)) {
            java.util.concurrent.atomic.AtomicLong(0)
        }
        while (true) {
            val previous = cursor.get()
            // PC output is replayable from the Android cumulative ACK, including
            // on the current socket after a subscription refresh. Authentication
            // and RemoteTerminalSession provide idempotency. Android input can
            // execute commands and therefore remains strictly monotonic.
            if (envelope.seq <= previous) return role == "android"
            if (cursor.compareAndSet(previous, envelope.seq)) return true
        }
    }

    private data class IncomingCursor(val sessionId: UUID, val senderId: UUID)

    private fun RealtimeState?.diagnosticName(): String = when (this) {
        RealtimeState.Connecting -> "CONNECTING"
        RealtimeState.Connected -> "CONNECTED"
        is RealtimeState.Reconnecting -> "RECONNECTING"
        is RealtimeState.AuthenticationRejected -> "AUTHENTICATION_REJECTED"
        is RealtimeState.AuthenticationFailed -> "AUTHENTICATION_FAILED"
        is RealtimeState.Failed -> "FAILED"
        RealtimeState.Closed -> "CLOSED"
        null -> "IDLE"
    }

    companion object {
        private const val REALTIME_LOG_TAG = "TermLinkRealtime"
        internal const val LIVENESS_PROBE_CAPABILITY = "liveness_probe_v1"
        const val MAX_FRAME_BYTES: Int = 1 shl 20
        const val MAX_BUFFERED_EVENTS: Int = 128
        const val LOW_BUFFERED_EVENTS: Int = 32
        const val HIGH_BUFFERED_EVENTS: Int = 64
        const val OPEN_WINDOW_EVENTS: Int = 32
        const val OPEN_WINDOW_WIRE_BYTES: Long = 3L shl 20
        const val LOW_BUFFERED_WIRE_BYTES: Long = 1L shl 20
        const val HIGH_BUFFERED_WIRE_BYTES: Long = 4L shl 20
        const val HARD_BUFFERED_WIRE_BYTES: Long = 8L shl 20
        private const val MAX_HELLO_BYTES: Int = 16 shl 10

        internal fun shouldPauseReceive(events: Int, wireBytes: Long): Boolean =
            events >= HIGH_BUFFERED_EVENTS || wireBytes >= HIGH_BUFFERED_WIRE_BYTES

        internal fun shouldResumeReceive(events: Int, wireBytes: Long): Boolean =
            events <= LOW_BUFFERED_EVENTS && wireBytes <= LOW_BUFFERED_WIRE_BYTES

        internal val defaultHttpClient: OkHttpClient = OkHttpClient.Builder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .build()

        internal fun normalizedWebSocketHttpClient(source: OkHttpClient): OkHttpClient =
            source.newBuilder()
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .pingInterval(15, TimeUnit.SECONDS)
                .build()

        fun encryptedEnvelope(
            sessionId: UUID,
            senderId: UUID,
            messageType: MessageType,
            direction: CryptoDirection,
            sequence: Long,
            acknowledgement: Long,
            keyId: ByteArray,
            nonce: ByteArray,
            sessionKey: ByteArray,
            plaintext: ByteArray,
            recipientId: UUID? = null,
            nowMillis: Long = System.currentTimeMillis(),
        ): Envelope {
            require(sequence > 0 && acknowledgement >= 0)
            require(keyId.isNotEmpty() && keyId.size <= 128)
            require(nonce.size == 12)
            require(sessionKey.size == 32)
            val builder = Envelope.newBuilder()
                .setProtocolVersion(1)
                .setMessageType(messageType)
                .setSessionId(ByteString.copyFrom(uuidBytes(sessionId)))
                .setSenderDeviceId(ByteString.copyFrom(uuidBytes(senderId)))
                .setDirection(
                    if (direction == CryptoDirection.PC_TO_ANDROID) Direction.PC_TO_ANDROID
                    else Direction.ANDROID_TO_PC,
                )
                .setSeq(sequence)
                .setAck(acknowledgement)
                .setSentAtMs(nowMillis)
                .setKeyId(ByteString.copyFrom(keyId))
                .setNonce(ByteString.copyFrom(nonce))
            if (recipientId != null) builder.setRecipientDeviceId(ByteString.copyFrom(uuidBytes(recipientId)))
            val metadata = builder.build()
            val ciphertext = SessionCrypto.encrypt(sessionKey, nonce, metadata.toByteArray(), plaintext)
            return try {
                metadata.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build()
            } finally {
                ciphertext.fill(0)
            }
        }

        fun decryptEnvelope(envelope: Envelope, sessionKey: ByteArray): ByteArray {
            require(envelope.protocolVersion == 1 && envelope.nonce.size() == 12)
            require(sessionKey.size == 32)
            val metadata = envelope.toBuilder().clearCiphertext().build()
            return SessionCrypto.decrypt(
                sessionKey,
                envelope.nonce.toByteArray(),
                metadata.toByteArray(),
                envelope.ciphertext.toByteArray(),
            )
        }

        internal fun uuidBytes(value: UUID): ByteArray =
            ByteBuffer.allocate(16).putLong(value.mostSignificantBits).putLong(value.leastSignificantBits).array()

        internal fun uuidFromBytes(value: ByteArray): UUID? =
            if (value.size != 16) null else {
                val buffer = ByteBuffer.wrap(value)
                UUID(buffer.long, buffer.long)
            }

        private fun normalizeWebSocket(root: String, allowCleartext: Boolean): String {
            val uri = URI(root.trimEnd('/'))
            require(
                uri.scheme == "https" || uri.scheme == "wss" ||
                    (allowCleartext && (uri.scheme == "http" || uri.scheme == "ws")),
            ) { "production realtime requires HTTPS/WSS" }
            require(!uri.host.isNullOrBlank() && uri.rawQuery == null && uri.rawFragment == null) {
                "realtime root must contain only scheme, host, port, and path"
            }
            val scheme = if (uri.scheme == "https" || uri.scheme == "wss") "wss" else "ws"
            return URI(scheme, uri.userInfo, uri.host, uri.port, uri.path, null, null).toString().trimEnd('/')
        }
    }
}

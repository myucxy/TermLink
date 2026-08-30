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
)

private val REALTIME_CAPABILITY_IDENTIFIER = Regex("[a-z][a-z0-9._-]{0,63}")

internal fun productionRealtimeCapabilities(additional: Set<String>): List<String> {
    require(additional.all(REALTIME_CAPABILITY_IDENTIFIER::matches)) {
        "Realtime capabilities must use stable protocol identifiers"
    }
    return (listOf(
        "cumulative_ack",
        "e2ee_x25519_chacha20poly1305",
        "flow_control_v1",
        "lease_v1",
        "bounded_event_replay_v1",
        "terminal_presentation_metadata_v1",
        "terminal_typed_input_v1",
    ) + additional).distinct().sorted()
}

class ProductionReceivedEnvelope internal constructor(
    val envelope: Envelope,
    val wireBytes: Int,
    val generation: Int,
    private val releaseReservation: (Int) -> Unit,
) : AutoCloseable {
    private val released = AtomicBoolean(false)

    override fun close() {
        if (released.compareAndSet(false, true)) releaseReservation(wireBytes)
    }

    /**
     * Transfers ownership to a close-observed view without copying opaque
     * ciphertext. The observer runs exactly once when downstream processing
     * releases the frame, including channel cancellation paths.
     */
    internal fun withCloseObserver(observer: () -> Unit): ProductionReceivedEnvelope =
        ProductionReceivedEnvelope(envelope, wireBytes, generation) {
            try {
                this.close()
            } finally {
                observer()
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

    fun connect()
    fun send(envelope: Envelope): Boolean
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

class ProductionRealtimeClient(
    rootUrl: String,
    private val tokenProvider: AccessTokenProvider,
    private val deviceId: UUID,
    private val identity: DeviceIdentity,
    private val role: String,
    additionalCapabilities: Set<String> = emptySet(),
    httpClient: OkHttpClient = defaultHttpClient,
    allowCleartextForLocalTest: Boolean = false,
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
    // Snapshot the caller's explicit requirements. A mutable Set changing
    // between reconnects must not silently alter the advertised or required
    // protocol contract for this client instance.
    private val requiredWelcomeCapabilities = additionalCapabilities.toSet()
    private val advertisedCapabilities = productionRealtimeCapabilities(requiredWelcomeCapabilities)
    private val sendLock = Any()
    @Volatile private var socket: WebSocket? = null
    @Volatile private var connectedGeneration: Int = 0

    private val incomingReceived: Flow<ProductionReceivedEnvelope> = incomingChannel.receiveAsFlow()
    // Compatibility view for transport-level callers that only need the
    // immutable Envelope. The session bridge uses receivedEnvelopes so the
    // byte reservation remains held until decrypt/parse/render completes.
    val incoming: Flow<Envelope> = incomingReceived.map { received ->
        try { received.envelope } finally { received.close() }
    }
    override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incomingReceived
    override val receivePressure: StateFlow<ProductionReceivePressure> = mutableReceivePressure.asStateFlow()
    override val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()

    init {
        require(role == "pc" || role == "android") { "role must be pc or android" }
    }

    /** Starts one socket generation. Reconnect policy is owned by the session bridge. */
    override fun connect() {
        check(!closed.get()) { "realtime client is closed" }
        val nextGeneration = generation.incrementAndGet()
        synchronized(sendLock) {
            socket?.cancel()
            socket = null
            connectedGeneration = 0
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
        require(envelope.protocolVersion == 1) { "unsupported Envelope protocol version" }
        require(envelope.senderDeviceId.toByteArray().contentEquals(uuidBytes(deviceId))) {
            "Envelope sender does not match this device"
        }
        require(envelope.sessionId.size() == 16) { "Envelope session ID must be 16 bytes" }
        require(envelope.recipientDeviceId.isEmpty || envelope.recipientDeviceId.size() == 16) {
            "Envelope recipient must be empty or 16 bytes"
        }
        val bytes = envelope.toByteArray()
        return try {
            synchronized(sendLock) {
                val active = socket
                if (active == null || connectedGeneration != generation.get()) return false
                active.send(OkioByteString.of(*bytes))
            }
        } finally {
            bytes.fill(0)
        }
    }

    /** Closes only the current generation, allowing the owner to reconnect. */
    override fun disconnect(reason: String) {
        if (closed.get()) return
        val active = synchronized(sendLock) {
            generation.incrementAndGet()
            connectedGeneration = 0
            val current = socket
            socket = null
            current
        }
        active?.close(1002, reason.take(120))
        mutableState.tryEmit(RealtimeState.Failed(reason))
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        generation.incrementAndGet()
        synchronized(sendLock) {
            socket?.close(1000, "client closed")
            socket = null
            connectedGeneration = 0
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
            val accepted = validateWelcome(text)
            if (accepted && !welcomed) {
                welcomed = true
                connectedGeneration = listenerGeneration
                mutableState.tryEmit(RealtimeState.Connected)
            } else {
                acceptingBinary = false
                webSocket.close(1002, "invalid welcome control")
            }
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
                acceptingBinary = false
                webSocket.close(1009, "frame too large")
                return
            }
            val raw = bytes.toByteArray()
            val envelope = try {
                runCatching { Envelope.parseFrom(raw) }.getOrElse {
                    acceptingBinary = false
                    webSocket.close(1002, "invalid Envelope")
                    return
                }
            } finally {
                raw.fill(0)
            }
            if (!validateIncoming(envelope)) {
                acceptingBinary = false
                webSocket.close(1008, "Envelope metadata rejected")
                return
            }
            if (!reserveIncoming(raw.size, listenerGeneration)) {
                logEnvelopeAdmission(
                    decision = "socket_reservation_rejected",
                    envelope = envelope,
                    wireBytes = raw.size,
                )
                acceptingBinary = false
                webSocket.close(1013, "bounded receive byte budget exceeded")
                return
            }
            val received = ProductionReceivedEnvelope(
                envelope = envelope,
                wireBytes = raw.size,
                generation = listenerGeneration,
                releaseReservation = ::releaseIncoming,
            )
            val admission = incomingChannel.trySend(received)
            if (admission.isFailure) {
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
                // close() is asynchronous. Without this generation-local gate,
                // later callbacks can enter the queue after the first rejected
                // frame as the collector frees capacity, manufacturing a hole
                // that looks like durable History Gap. Stop at one contiguous
                // prefix and let the owner resume from its cumulative ACK.
                acceptingBinary = false
                webSocket.close(1013, "bounded receive queue exceeded")
            }
        }

        /**
         * Parse only a bounded rejected frame and expose metadata, never the
         * ciphertext or identifiers. This locates a missing sequence without
         * turning diagnostics into another payload channel.
         */
        private fun logBinaryDrop(decision: String, bytes: OkioByteString) {
            val envelope = if (bytes.size <= MAX_FRAME_BYTES) {
                runCatching { Envelope.parseFrom(bytes.toByteArray()) }.getOrNull()
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

        private fun validateWelcome(text: String): Boolean = runCatching {
            val value = Json.parseToJsonElement(text) as? JsonObject ?: return@runCatching false
            val type = value["type"] as? JsonPrimitive ?: return@runCatching false
            val protocolVersion = value["protocol_version"] as? JsonPrimitive
                ?: return@runCatching false
            if (!type.isString || type.content != "welcome" ||
                protocolVersion.isString || protocolVersion.content != "1"
            ) return@runCatching false

            val capabilitiesElement = value["capabilities"]
            if (capabilitiesElement == null) {
                // Older Relays did not echo negotiated capabilities. Keep that
                // compatibility only when this client did not explicitly opt
                // into an additional capability.
                return@runCatching requiredWelcomeCapabilities.isEmpty()
            }
            val capabilities = capabilitiesElement as? JsonArray ?: return@runCatching false
            val negotiated = HashSet<String>(capabilities.size)
            for (element in capabilities) {
                val primitive = element as? JsonPrimitive ?: return@runCatching false
                if (!primitive.isString) return@runCatching false
                val capability = primitive.content
                if (!REALTIME_CAPABILITY_IDENTIFIER.matches(capability) ||
                    !negotiated.add(capability)
                ) return@runCatching false
            }
            negotiated.containsAll(requiredWelcomeCapabilities)
        }.getOrDefault(false)
    }

    private fun reserveIncoming(wireBytes: Int, pressureGeneration: Int): Boolean {
        if (wireBytes <= 0 || wireBytes > MAX_FRAME_BYTES) return false
        val events = bufferedIncomingEvents.incrementAndGet()
        val bytes = bufferedIncomingWireBytes.addAndGet(wireBytes.toLong())
        if (events > MAX_BUFFERED_EVENTS || bytes > HARD_BUFFERED_WIRE_BYTES) {
            bufferedIncomingEvents.decrementAndGet()
            bufferedIncomingWireBytes.addAndGet(-wireBytes.toLong())
            return false
        }
        if (bytes >= HIGH_BUFFERED_WIRE_BYTES && receivePressurePaused.compareAndSet(false, true)) {
            publishReceivePressure(pressureGeneration)
        }
        return true
    }

    private fun releaseIncoming(wireBytes: Int) {
        val events = bufferedIncomingEvents.updateAndGet { (it - 1).coerceAtLeast(0) }
        val bytes = bufferedIncomingWireBytes.updateAndGet { (it - wireBytes).coerceAtLeast(0) }
        if (bytes <= LOW_BUFFERED_WIRE_BYTES && receivePressurePaused.compareAndSet(true, false)) {
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
        const val MAX_FRAME_BYTES: Int = 1 shl 20
        const val MAX_BUFFERED_EVENTS: Int = 128
        const val OPEN_WINDOW_EVENTS: Int = 128
        const val OPEN_WINDOW_WIRE_BYTES: Long = 3L shl 20
        const val LOW_BUFFERED_WIRE_BYTES: Long = 1L shl 20
        const val HIGH_BUFFERED_WIRE_BYTES: Long = 4L shl 20
        const val HARD_BUFFERED_WIRE_BYTES: Long = 8L shl 20
        private const val MAX_HELLO_BYTES: Int = 16 shl 10

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

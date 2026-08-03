package dev.termlink.network.production

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
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
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
    data class Failed(val reason: String) : RealtimeState
    data object Closed : RealtimeState
}

/**
 * The production WebSocket transport. It only handles authenticated socket
 * framing and opaque protobuf Envelopes; session keys and payload semantics
 * live in [ProductionRealtimeSessionClient].
 */
class ProductionRealtimeClient(
    rootUrl: String,
    private val tokenProvider: AccessTokenProvider,
    private val deviceId: UUID,
    private val identity: DeviceIdentity,
    private val role: String,
    private val httpClient: OkHttpClient = defaultHttpClient,
    allowCleartextForLocalTest: Boolean = false,
) : Closeable {
    private val websocketUrl = normalizeWebSocket(rootUrl, allowCleartextForLocalTest) + "/v1/realtime"
    private val closed = AtomicBoolean(false)
    private val generation = AtomicInteger(0)
    private val lastIncomingSequences = ConcurrentHashMap<IncomingCursor, java.util.concurrent.atomic.AtomicLong>()
    private val mutableIncoming = MutableSharedFlow<Envelope>(
        extraBufferCapacity = 128,
        onBufferOverflow = BufferOverflow.SUSPEND,
    )
    private val mutableState = MutableSharedFlow<RealtimeState>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    private val sendLock = Any()
    @Volatile private var socket: WebSocket? = null
    @Volatile private var connectedGeneration: Int = 0

    val incoming: SharedFlow<Envelope> = mutableIncoming.asSharedFlow()
    val state: SharedFlow<RealtimeState> = mutableState.asSharedFlow()

    init {
        require(role == "pc" || role == "android") { "role must be pc or android" }
    }

    /** Starts one socket generation. Reconnect policy is owned by the session bridge. */
    fun connect() {
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
        val opened = httpClient.newWebSocket(request, Listener(nextGeneration))
        synchronized(sendLock) {
            if (!closed.get() && generation.get() == nextGeneration) socket = opened else opened.cancel()
        }
    }

    /** Sends a validated opaque Envelope on the currently connected socket. */
    fun send(envelope: Envelope): Boolean {
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
    internal fun disconnect(reason: String) {
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
    }

    private inner class Listener(private val listenerGeneration: Int) : WebSocketListener() {
        private var welcomed = false

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (!isCurrent(webSocket)) return
            val hello = buildJsonObject {
                put("role", role)
                putJsonArray("protocol_versions") { add(kotlinx.serialization.json.JsonPrimitive(1)) }
                putJsonArray("capabilities") {
                    listOf(
                        "cumulative_ack",
                        "e2ee_x25519_chacha20poly1305",
                        "lease_v1",
                        "bounded_event_replay_v1",
                    ).forEach { add(kotlinx.serialization.json.JsonPrimitive(it)) }
                }
            }
            if (!webSocket.send(Json.encodeToString(JsonObject.serializer(), hello))) {
                webSocket.close(1011, "hello send failed")
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isCurrent(webSocket)) return
            if (text.length > MAX_HELLO_BYTES) {
                webSocket.close(1009, "welcome control too large")
                return
            }
            val accepted = runCatching {
                val value = Json.parseToJsonElement(text).jsonObject
                value["type"]?.jsonPrimitive?.content == "welcome" &&
                    value["protocol_version"]?.jsonPrimitive?.content?.toIntOrNull() == 1
            }.getOrDefault(false)
            if (accepted && !welcomed) {
                welcomed = true
                connectedGeneration = listenerGeneration
                mutableState.tryEmit(RealtimeState.Connected)
            } else {
                webSocket.close(1002, "invalid welcome control")
            }
        }

        override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
            if (!isCurrent(webSocket)) return
            if (!welcomed) {
                webSocket.close(1002, "welcome required before binary frames")
                return
            }
            if (bytes.size > MAX_FRAME_BYTES) {
                webSocket.close(1009, "frame too large")
                return
            }
            val raw = bytes.toByteArray()
            val envelope = try {
                runCatching { Envelope.parseFrom(raw) }.getOrElse {
                    webSocket.close(1002, "invalid Envelope")
                    return
                }
            } finally {
                raw.fill(0)
            }
            if (!validateIncoming(envelope)) {
                webSocket.close(1008, "Envelope metadata rejected")
                return
            }
            if (!mutableIncoming.tryEmit(envelope)) {
                webSocket.close(1013, "bounded receive queue exceeded")
            }
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (!isCurrent(webSocket) || closed.get()) return
            mutableState.tryEmit(RealtimeState.Failed(t.message ?: "realtime failed"))
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (!isCurrent(webSocket) || closed.get()) return
            mutableState.tryEmit(RealtimeState.Failed(reason.ifBlank { "socket closed ($code)" }))
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            if (isCurrent(webSocket)) webSocket.close(code, reason)
        }

        private fun isCurrent(webSocket: WebSocket): Boolean =
            !closed.get() && generation.get() == listenerGeneration && socket === webSocket
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
            if (envelope.seq <= previous) return false
            if (cursor.compareAndSet(previous, envelope.seq)) return true
        }
    }

    private data class IncomingCursor(val sessionId: UUID, val senderId: UUID)

    companion object {
        const val MAX_FRAME_BYTES: Int = 1 shl 20
        private const val MAX_HELLO_BYTES: Int = 16 shl 10

        internal val defaultHttpClient: OkHttpClient = OkHttpClient.Builder()
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

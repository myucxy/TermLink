package dev.termlink.network.direct

import com.google.protobuf.ByteString
import com.google.protobuf.InvalidProtocolBufferException
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.io.IOException
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString as OkioByteString
import kotlin.coroutines.resume

/** Capabilities negotiated by one independently reconnectable signalling generation. */
internal data class DirectTransportSignalingNegotiatedCapabilities(
    val generation: Int,
    val capabilities: Set<String>,
)

/**
 * Production direct-transport signalling control plane.
 *
 * This type intentionally has no dependency on ProductionRealtimeClient, its supervisor, terminal
 * ACK storage, or renderer lifecycle. Every fault retires only this optional signalling lane.
 * Encrypted signal payload construction/opening remains at the core-crypto boundary; this client
 * transports and strictly validates the opaque [TransportSignalEnvelopeV1].
 */
internal class DirectTransportSignalingClient(
    rootUrl: String,
    private val tokenProvider: AccessTokenProvider,
    private val deviceId: UUID,
    private val identity: DeviceIdentity,
    private val role: String,
    private val ticketSignatureVerifier: DirectSignalingTicketSignatureVerifier,
    private val feature: DirectTransportSignalingFeature = DirectTransportSignalingFeature(),
    /** Advertise multipair only after the caller has proved that native ABI v4 is usable. */
    private val multipairV2Enabled: Boolean = false,
    /** Advertise production data cutover only when a non-Shadow caller explicitly opts in. */
    private val directDataV1Enabled: Boolean = false,
    httpClient: OkHttpClient = OkHttpClient(),
    parentScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val random: SecureRandom = SecureRandom(),
    private val retryDelay: DirectSignalingRetryDelay = DirectSignalingRetryDelay(::delay),
    private val retryJitter: DirectSignalingRetryJitter = DirectSignalingRetryJitter { base ->
        if (base <= 1) 0 else (random.nextLong() and Long.MAX_VALUE) % base
    },
    private val signalHandshakeTimeoutMillis: Long = SIGNAL_HANDSHAKE_TIMEOUT_MILLIS,
    allowCleartextForLocalTest: Boolean = false,
) : Closeable {
    private val root = normalizeRoot(rootUrl, allowCleartextForLocalTest)
    private val websocketUrl = websocketRoot(root) + SIGNAL_PATH
    private val restClient = httpClient.newBuilder()
        .callTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .connectTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .readTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .writeTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .build()
    private val websocketClient = httpClient.newBuilder()
        .callTimeout(0, TimeUnit.MILLISECONDS)
        .connectTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .writeTimeout(HTTP_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        .pingInterval(SIGNAL_PING_INTERVAL_MILLIS, TimeUnit.MILLISECONDS)
        .build()
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val closed = AtomicBoolean(false)
    private val multipairV2Configured = AtomicBoolean(false)
    private val generation = AtomicInteger(0)
    private val queuedIncomingBytes = AtomicLong(0)
    private val incomingChannel = Channel<QueuedIncomingSignal>(
        capacity = MAX_QUEUED_SIGNAL_FRAMES,
        onUndeliveredElement = { queuedIncomingBytes.addAndGet(-it.wireBytes.toLong()) },
    )
    private val lock = Any()
    private val outgoingQueue = ArrayDeque<ByteArray>()
    private val incomingCursors = LinkedHashMap<SignalCursor, Long>()
    private val outgoingCursors = LinkedHashMap<SignalCursor, Long>()
    private val incomingNonces = LinkedHashSet<SignalNonceKey>()
    private val outgoingNonces = LinkedHashSet<SignalNonceKey>()
    private var queuedOutgoingBytes = 0
    private var socket: WebSocket? = null
    private var connectedGeneration = 0
    private var connectedCapabilities: Set<String> = emptySet()
    private var retryJob: Job? = null
    private var handshakeJob: Job? = null
    private var currentAttempt = 0
    private val mutableState = MutableStateFlow<DirectTransportSignalingState>(
        if (feature.enabled) DirectTransportSignalingState.Idle else DirectTransportSignalingState.Disabled,
    )
    private val mutableNegotiatedCapabilities = MutableStateFlow(
        DirectTransportSignalingNegotiatedCapabilities(0, emptySet()),
    )

    val state: StateFlow<DirectTransportSignalingState> = mutableState.asStateFlow()
    val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities> =
        mutableNegotiatedCapabilities.asStateFlow()
    val multipairV2Active: Boolean
        get() = multipairV2Configured.get()
    val incoming: Flow<TransportSignalEnvelopeV1> = incomingChannel.receiveAsFlow().map { queued ->
        queuedIncomingBytes.addAndGet(-queued.wireBytes.toLong())
        queued.envelope
    }

    init {
        require(deviceId != NIL_UUID) { "direct signalling device ID is required" }
        require(role == "pc" || role == "android") { "direct signalling role must be pc or android" }
        require(signalHandshakeTimeoutMillis > 0) { "signal handshake timeout must be positive" }
    }

    fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig> {
        if (!feature.enabled) return DirectSignalingCallResult.Disabled
        if (closed.get()) return closedFault()
        multipairV2Configured.set(false)
        val response = executeHttp("GET", CONFIG_PATH, ByteArray(0))
            ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
            )
        response.use {
            val failure = httpFailure(it)
            if (failure != null) return DirectSignalingCallResult.Fault(failure)
            val raw = readBoundedBody(it, MAX_CONFIG_RESPONSE_BYTES)
                ?: return DirectSignalingCallResult.Fault(
                    DirectSignalingFault(DirectSignalingFaultCode.RESPONSE_TOO_LARGE),
                )
            val config = runCatching { parseConfig(raw) }.getOrNull()
                ?: return DirectSignalingCallResult.Fault(
                    DirectSignalingFault(DirectSignalingFaultCode.INVALID_CONFIGURATION),
                )
            multipairV2Configured.set(
                multipairV2Enabled && DIRECT_MULTIPAIR_CAPABILITY in config.capabilities,
            )
            return DirectSignalingCallResult.Success(config)
        }
    }

    suspend fun fetchConfigCancellable(): DirectSignalingCallResult<DirectTransportServerConfig> {
        if (!feature.enabled) return DirectSignalingCallResult.Disabled
        if (closed.get()) return closedFault()
        multipairV2Configured.set(false)
        val response = executeHttpBodyCancellable(
            "GET",
            CONFIG_PATH,
            ByteArray(0),
            MAX_CONFIG_RESPONSE_BYTES,
        )
            ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
            )
        val failure = httpFailure(response)
        if (failure != null) return DirectSignalingCallResult.Fault(failure)
        val raw = response.body ?: return DirectSignalingCallResult.Fault(
            DirectSignalingFault(DirectSignalingFaultCode.RESPONSE_TOO_LARGE),
        )
        val config = runCatching { parseConfig(raw) }.getOrNull()
            ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.INVALID_CONFIGURATION),
            )
        multipairV2Configured.set(
            multipairV2Enabled && DIRECT_MULTIPAIR_CAPABILITY in config.capabilities,
        )
        return DirectSignalingCallResult.Success(config)
    }

    fun issueTicket(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ): DirectSignalingCallResult<ValidatedDirectPathTicket> {
        if (!feature.enabled) return DirectSignalingCallResult.Disabled
        if (closed.get()) return closedFault()
        if (targetDeviceId == NIL_UUID || targetDeviceId == deviceId || connectionId.version() != 4 ||
            config.protocolVersion != PROTOCOL_VERSION ||
            !validConfigCapabilities(config.capabilities, config.directRoutingPermitted)
        ) {
            return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
            )
        }
        val jsonBody = buildJsonObject {
            put("target_device_id", targetDeviceId.toString())
            put("connection_id", connectionId.toString())
            putJsonArray("requested_carriers") { add(JsonPrimitive("UDP_QUIC")) }
        }
        val body = JSON.encodeToString(JsonObject.serializer(), jsonBody).encodeToByteArray()
        if (body.size > MAX_TICKET_REQUEST_BYTES) {
            return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
            )
        }
        val response = executeHttp("POST", TICKET_PATH, body)
            ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
            )
        response.use {
            val failure = httpFailure(it)
            if (failure != null) return DirectSignalingCallResult.Fault(failure)
            val raw = readBoundedBody(it, MAX_TICKET_RESPONSE_BYTES)
                ?: return DirectSignalingCallResult.Fault(
                    DirectSignalingFault(DirectSignalingFaultCode.RESPONSE_TOO_LARGE),
                )
            val ticket = runCatching {
                parseAndValidateTicket(raw, targetDeviceId, connectionId, config)
            }.getOrNull() ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
            )
            return DirectSignalingCallResult.Success(ticket)
        }
    }

    suspend fun issueTicketCancellable(
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ): DirectSignalingCallResult<ValidatedDirectPathTicket> {
        if (!feature.enabled) return DirectSignalingCallResult.Disabled
        if (closed.get()) return closedFault()
        if (targetDeviceId == NIL_UUID || targetDeviceId == deviceId || connectionId.version() != 4 ||
            config.protocolVersion != PROTOCOL_VERSION ||
            !validConfigCapabilities(config.capabilities, config.directRoutingPermitted)
        ) {
            return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
            )
        }
        val jsonBody = buildJsonObject {
            put("target_device_id", targetDeviceId.toString())
            put("connection_id", connectionId.toString())
            putJsonArray("requested_carriers") { add(JsonPrimitive("UDP_QUIC")) }
        }
        val body = JSON.encodeToString(JsonObject.serializer(), jsonBody).encodeToByteArray()
        if (body.size > MAX_TICKET_REQUEST_BYTES) {
            return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
            )
        }
        val response = executeHttpBodyCancellable(
            "POST",
            TICKET_PATH,
            body,
            MAX_TICKET_RESPONSE_BYTES,
        )
            ?: return DirectSignalingCallResult.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
            )
        val failure = httpFailure(response)
        if (failure != null) return DirectSignalingCallResult.Fault(failure)
        val raw = response.body ?: return DirectSignalingCallResult.Fault(
            DirectSignalingFault(DirectSignalingFaultCode.RESPONSE_TOO_LARGE),
        )
        val ticket = runCatching {
            parseAndValidateTicket(raw, targetDeviceId, connectionId, config)
        }.getOrNull() ?: return DirectSignalingCallResult.Fault(
            DirectSignalingFault(DirectSignalingFaultCode.TICKET_REJECTED),
        )
        return DirectSignalingCallResult.Success(ticket)
    }

    /** Starts an independently retried signalling socket. Feature-off performs no network I/O. */
    fun startSignaling(): Boolean {
        if (!feature.enabled) {
            mutableState.value = DirectTransportSignalingState.Disabled
            return false
        }
        if (closed.get()) return false
        synchronized(lock) {
            if (socket != null || retryJob?.isActive == true) return false
            currentAttempt = 1
        }
        openSocket(attempt = 1)
        return true
    }

    /**
     * Sends an already pairwise-encrypted signal. A bounded pre-Welcome queue is allowed, but queue
     * overflow retires only signalling and never invokes or references the realtime Relay.
     */
    fun send(envelope: TransportSignalEnvelopeV1): Boolean {
        if (!feature.enabled || closed.get()) return false
        if (envelope.serializedSize !in 1..MAX_SIGNAL_FRAME_BYTES) {
            retireWithFault(DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED, retryable = false)
            return false
        }
        val bytes = envelope.toByteArray()
        val signalType = envelope.signalType.number
        val dataControl = signalType in DIRECT_DATA_SIGNAL_TYPES
        val valid = validateSignal(
            envelope,
            bytes,
            outbound = true,
            directDataAllowed = !dataControl || isDirectDataV1Negotiated(),
        )
        if (!valid || bytes.size > MAX_SIGNAL_FRAME_BYTES) {
            bytes.fill(0)
            retireWithFault(DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED, retryable = false)
            return false
        }
        var overflow = false
        var dataCapabilityRejected = false
        val accepted = synchronized(lock) {
            if (dataControl && !isDirectDataV1NegotiatedLocked(generation.get())) {
                dataCapabilityRejected = true
                return@synchronized false
            }
            val cursor = envelope.cursor()
            val nonce = envelope.nonceKey()
            val previous = outgoingCursors[cursor] ?: 0
            if (!isExactNextSignalSequence(previous, envelope.signalSeq) || nonce in outgoingNonces) {
                return@synchronized false
            }
            val active = socket
            if (active != null && connectedGeneration == generation.get()) {
                active.send(OkioByteString.of(*bytes)).also { sent ->
                    if (sent) {
                        rememberCursor(outgoingCursors, cursor, envelope.signalSeq)
                        rememberNonce(outgoingNonces, nonce)
                    }
                }
            } else if (outgoingQueue.size < MAX_QUEUED_SIGNAL_FRAMES &&
                queuedOutgoingBytes + bytes.size <= MAX_QUEUED_SIGNAL_BYTES
            ) {
                outgoingQueue.addLast(bytes.copyOf())
                queuedOutgoingBytes += bytes.size
                rememberCursor(outgoingCursors, cursor, envelope.signalSeq)
                rememberNonce(outgoingNonces, nonce)
                true
            } else {
                overflow = true
                false
            }
        }
        bytes.fill(0)
        if (dataCapabilityRejected) {
            retireWithFault(DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED, retryable = false)
        } else if (overflow) {
            retireWithFault(DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW, retryable = false)
        } else if (!accepted) {
            retireWithFault(DirectSignalingFaultCode.SIGNAL_REPLAYED, retryable = false)
        }
        return accepted
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        val active = synchronized(lock) {
            val retiredGeneration = generation.incrementAndGet()
            retryJob?.cancel()
            retryJob = null
            handshakeJob?.cancel()
            handshakeJob = null
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(retiredGeneration, emptySet())
            clearOutgoingQueueLocked()
            incomingCursors.clear()
            outgoingCursors.clear()
            incomingNonces.clear()
            outgoingNonces.clear()
            socket.also { socket = null }
        }
        active?.close(1000, "direct signalling closed")
        mutableState.value = DirectTransportSignalingState.Closed
        multipairV2Configured.set(false)
        incomingChannel.cancel()
        scope.cancel()
    }

    private fun createHttpCall(method: String, path: String, body: ByteArray): Call? = runCatching {
        val proof = DirectTransportSignalingProof.createDeviceProof(
            identity = identity,
            deviceId = deviceId.toString(),
            method = method,
            requestTarget = path,
            body = body,
            nowMillis = clockMillis(),
            random = random,
        )
        val builder = Request.Builder()
            .url(root + path)
            .header("Authorization", "Bearer ${tokenProvider.accessToken()}")
            .proofHeaders(proof)
        when (method) {
            "GET" -> builder.get()
            "POST" -> builder.post(body.toRequestBody(JSON_MEDIA_TYPE))
            else -> error("unsupported direct HTTP method")
        }
        restClient.newCall(builder.build())
    }.getOrNull()

    private fun executeHttp(method: String, path: String, body: ByteArray): Response? =
        runCatching { createHttpCall(method, path, body)?.execute() }.getOrNull()

    /**
     * Keeps the exact OkHttp [Call] cancellable until the bounded response body
     * has been consumed. Returning a live [Response] would settle cancellation
     * at headers and allow a stalled body to delay generation retirement.
     */
    private suspend fun executeHttpBodyCancellable(
        method: String,
        path: String,
        body: ByteArray,
        maximumResponseBytes: Int,
    ): CancellableHttpResponse? {
        val call = createHttpCall(method, path, body) ?: return null
        return suspendCancellableCoroutine { continuation ->
            val settled = AtomicBoolean(false)
            continuation.invokeOnCancellation {
                if (settled.compareAndSet(false, true)) call.cancel()
            }
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, error: IOException) {
                    if (settled.compareAndSet(false, true)) continuation.resume(null)
                }

                override fun onResponse(call: Call, response: Response) {
                    val result = response.use { value ->
                        try {
                            CancellableHttpResponse(
                                code = value.code,
                                successful = value.isSuccessful,
                                body = if (value.isSuccessful) {
                                    readBoundedBody(value, maximumResponseBytes)
                                } else {
                                    ByteArray(0)
                                },
                            )
                        } catch (_: IOException) {
                            null
                        }
                    }
                    if (settled.compareAndSet(false, true)) {
                        continuation.resume(result) { _, value, _ -> value?.body?.fill(0) }
                    } else {
                        result?.body?.fill(0)
                    }
                }
            })
        }
    }

    private data class CancellableHttpResponse(
        val code: Int,
        val successful: Boolean,
        val body: ByteArray?,
    )

    private fun openSocket(attempt: Int) {
        val nextGeneration = synchronized(lock) {
            if (closed.get() || !feature.enabled) return
            val next = generation.incrementAndGet()
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(next, emptySet())
            next
        }
        mutableState.value = DirectTransportSignalingState.Connecting(attempt)
        val opened = runCatching {
            val proof = DirectTransportSignalingProof.createSignalProof(
                identity,
                deviceId.toString(),
                clockMillis(),
                random,
            )
            val request = Request.Builder()
                .url(websocketUrl)
                .header("Authorization", "Bearer ${tokenProvider.accessToken()}")
                .header("Sec-WebSocket-Protocol", SIGNAL_SUBPROTOCOL)
                .proofHeaders(proof)
                .build()
            websocketClient.newWebSocket(request, SignalListener(nextGeneration, attempt))
        }.getOrElse {
            handleSocketFault(nextGeneration, attempt, DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED, true)
            return
        }
        synchronized(lock) {
            if (!closed.get() && generation.get() == nextGeneration && (socket == null || socket === opened)) {
                socket = opened
                scheduleHandshakeTimeoutLocked(nextGeneration, attempt)
            } else {
                opened.cancel()
            }
        }
    }

    private inner class SignalListener(
        private val listenerGeneration: Int,
        private val attempt: Int,
    ) : WebSocketListener() {
        private var welcomed = false
        private var acceptingBinary = true

        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (!isCurrent(webSocket)) return
            val hello = buildJsonObject {
                put("type", "CLIENT_HELLO")
                put("role", role)
                putJsonArray("protocol_versions") { add(JsonPrimitive(PROTOCOL_VERSION)) }
                putJsonArray("capabilities") {
                    add(JsonPrimitive(DIRECT_TRANSPORT_CAPABILITY))
                    if (multipairV2Configured.get()) add(JsonPrimitive(DIRECT_MULTIPAIR_CAPABILITY))
                    if (directDataV1Enabled) {
                        add(JsonPrimitive(DIRECT_DATA_CAPABILITY))
                        add(JsonPrimitive(DIRECT_SESSION_CUTOVER_CAPABILITY))
                    }
                }
            }
            if (!webSocket.send(JSON.encodeToString(JsonObject.serializer(), hello))) {
                webSocket.close(1011, "transport hello failed")
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isCurrent(webSocket)) return
            val negotiated = if (!welcomed && text.encodeToByteArray().size <= MAX_HELLO_BYTES) {
                validateWelcome(text)
            } else {
                null
            }
            if (negotiated == null) {
                acceptingBinary = false
                webSocket.close(1002, "invalid transport welcome")
                handleSocketFault(
                    listenerGeneration,
                    attempt,
                    DirectSignalingFaultCode.SIGNAL_PROTOCOL_REJECTED,
                    retryable = false,
                )
                return
            }
            welcomed = true
            val accepted = synchronized(lock) {
                if (closed.get() || socket !== webSocket || generation.get() != listenerGeneration) {
                    false
                } else {
                    connectedGeneration = listenerGeneration
                    connectedCapabilities = negotiated
                    publishNegotiatedCapabilitiesLocked(listenerGeneration, negotiated)
                    currentAttempt = 0
                    handshakeJob?.cancel()
                    handshakeJob = null
                    flushOutgoingQueueLocked(webSocket)
                }
            }
            if (accepted) {
                mutableState.value = DirectTransportSignalingState.Connected(listenerGeneration)
            } else if (isCurrent(webSocket)) {
                handleSocketFault(
                    listenerGeneration,
                    attempt,
                    DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED,
                    retryable = true,
                )
            }
        }

        override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
            if (!isCurrent(webSocket) || !acceptingBinary) return
            if (!welcomed) {
                acceptingBinary = false
                webSocket.close(1002, "transport welcome required")
                handleSocketFault(
                    listenerGeneration,
                    attempt,
                    DirectSignalingFaultCode.SIGNAL_PROTOCOL_REJECTED,
                    retryable = false,
                )
                return
            }
            if (bytes.size <= 0 || bytes.size > MAX_SIGNAL_FRAME_BYTES) {
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED)
                return
            }
            val raw = bytes.toByteArray()
            val envelope = try {
                TransportSignalEnvelopeV1.parseFrom(raw)
            } catch (_: InvalidProtocolBufferException) {
                raw.fill(0)
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED)
                return
            }
            if (!validateSignal(
                    envelope,
                    raw,
                    outbound = false,
                    directDataAllowed = isDirectDataV1Negotiated(listenerGeneration),
                )
            ) {
                raw.fill(0)
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED)
                return
            }
            val cursor = envelope.cursor()
            val nonce = envelope.nonceKey()
            val replayed = synchronized(lock) {
                !isExactNextSignalSequence(incomingCursors[cursor] ?: 0, envelope.signalSeq) ||
                    nonce in incomingNonces
            }
            if (replayed) {
                raw.fill(0)
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_REPLAYED)
                return
            }
            val reserved = queuedIncomingBytes.addAndGet(raw.size.toLong())
            if (reserved > MAX_QUEUED_SIGNAL_BYTES) {
                queuedIncomingBytes.addAndGet(-raw.size.toLong())
                raw.fill(0)
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW)
                return
            }
            val admission = incomingChannel.trySend(QueuedIncomingSignal(envelope, raw.size))
            raw.fill(0)
            if (admission.isFailure) {
                queuedIncomingBytes.addAndGet(-bytes.size.toLong())
                rejectIncoming(webSocket, DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW)
                return
            }
            synchronized(lock) {
                rememberCursor(incomingCursors, cursor, envelope.signalSeq)
                rememberNonce(incomingNonces, nonce)
            }
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (!isCurrent(webSocket)) {
                response?.close()
                return
            }
            acceptingBinary = false
            val status = response?.code
            response?.close()
            val authentication = status == 401 || status == 403
            handleSocketFault(
                listenerGeneration,
                attempt,
                if (authentication) {
                    DirectSignalingFaultCode.AUTHENTICATION_REJECTED
                } else {
                    DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED
                },
                retryable = !authentication,
                httpStatus = status,
            )
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (!isCurrent(webSocket) || closed.get()) return
            acceptingBinary = false
            handleSocketFault(
                listenerGeneration,
                attempt,
                DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED,
                retryable = code !in setOf(1002, 1008, 1009),
            )
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            if (isCurrent(webSocket)) webSocket.close(code, reason.take(MAX_CLOSE_REASON_CHARS))
        }

        private fun rejectIncoming(webSocket: WebSocket, code: DirectSignalingFaultCode) {
            acceptingBinary = false
            webSocket.close(
                if (code == DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW) 1013 else 1008,
                "direct signal rejected",
            )
            handleSocketFault(listenerGeneration, attempt, code, retryable = false)
        }

        private fun isCurrent(webSocket: WebSocket): Boolean = synchronized(lock) {
            if (closed.get() || generation.get() != listenerGeneration) return@synchronized false
            when (val active = socket) {
                null -> {
                    socket = webSocket
                    true
                }
                else -> active === webSocket
            }
        }
    }

    private fun handleSocketFault(
        expectedGeneration: Int,
        attempt: Int,
        code: DirectSignalingFaultCode,
        retryable: Boolean,
        httpStatus: Int? = null,
    ) {
        if (closed.get()) return
        val shouldRetry = retryable && attempt <= MAX_SIGNAL_RETRIES
        val active = synchronized(lock) {
            if (generation.get() != expectedGeneration) return
            val retiredGeneration = generation.incrementAndGet()
            handshakeJob?.cancel()
            handshakeJob = null
            connectedGeneration = 0
            connectedCapabilities = emptySet()
            publishNegotiatedCapabilitiesLocked(retiredGeneration, emptySet())
            clearOutgoingQueueLocked()
            socket.also { socket = null }
        }
        active?.cancel()
        mutableState.value = DirectTransportSignalingState.Fault(
            DirectSignalingFault(code, httpStatus),
            willRetry = shouldRetry,
            attempt = attempt,
        )
        if (shouldRetry) scheduleRetry(attempt + 1) else if (retryable) {
            mutableState.value = DirectTransportSignalingState.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.RETRY_EXHAUSTED, httpStatus),
                willRetry = false,
                attempt = attempt,
            )
        }
    }

    private fun scheduleRetry(nextAttempt: Int) {
        val base = retryBaseMillis(nextAttempt - 1)
        val wait = retryJitter.apply(base).coerceIn(0, base)
        synchronized(lock) {
            retryJob?.cancel()
            retryJob = scope.launch {
                retryDelay.await(wait)
                synchronized(lock) {
                    if (closed.get() || socket != null) return@launch
                    currentAttempt = nextAttempt
                    retryJob = null
                }
                openSocket(nextAttempt)
            }
        }
    }

    private fun scheduleHandshakeTimeoutLocked(expectedGeneration: Int, attempt: Int) {
        handshakeJob?.cancel()
        handshakeJob = scope.launch {
            delay(signalHandshakeTimeoutMillis)
            val timedOut = synchronized(lock) {
                !closed.get() && generation.get() == expectedGeneration &&
                    connectedGeneration != expectedGeneration && socket != null
            }
            if (timedOut) {
                handleSocketFault(
                    expectedGeneration,
                    attempt,
                    DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED,
                    retryable = true,
                )
            }
        }
    }

    private fun retireWithFault(code: DirectSignalingFaultCode, retryable: Boolean) {
        val currentGeneration: Int
        val attempt: Int
        synchronized(lock) {
            currentGeneration = generation.get()
            attempt = currentAttempt.coerceAtLeast(1)
        }
        handleSocketFault(currentGeneration, attempt, code, retryable)
    }

    private fun parseConfig(raw: ByteArray): DirectTransportServerConfig {
        val value = JSON.parseToJsonElement(raw.decodeToString()) as? JsonObject
            ?: error("config must be an object")
        value.requireExactKeys(CONFIG_KEYS)
        val protocol = value.requiredInt("protocol_version")
        require(protocol == PROTOCOL_VERSION)
        val capabilities = value.requiredStringList("capabilities", MAX_CONFIG_CAPABILITIES)
        require(capabilities.distinct().size == capabilities.size)
        require(capabilities.all(CAPABILITY_PATTERN::matches))
        val directRoutingPermitted = value.requiredBoolean("direct_routing_permitted")
        require(validConfigCapabilities(capabilities.toSet(), directRoutingPermitted))
        val keyElements = value["ticket_signing_keys"] as? JsonArray ?: error("keys missing")
        require(keyElements.size in 1..MAX_TICKET_SIGNING_KEYS)
        val keys = LinkedHashMap<String, DirectTicketSigningKey>(keyElements.size)
        keyElements.forEach { element ->
            val key = element as? JsonObject ?: error("key must be an object")
            key.requireExactKeys(TICKET_KEY_KEYS)
            val keyId = key.requiredString("key_id")
            require(key.requiredString("algorithm") == "Ed25519")
            val publicKey = key.requiredString("public_key").decodeBase64UrlStrict()
            val parsed = DirectTicketSigningKey(keyId, publicKey)
            require(keys.put(keyId, parsed) == null)
        }
        val stunServers = value.requiredStringList("stun_servers", MAX_STUN_SERVERS)
        require(stunServers.all(::validStunServer) && stunServers.distinct().size == stunServers.size)
        return DirectTransportServerConfig(
            protocolVersion = protocol,
            capabilities = capabilities.toSet(),
            directRoutingPermitted = directRoutingPermitted,
            ticketSigningKeys = keys,
            stunServers = stunServers,
        )
    }

    private fun parseAndValidateTicket(
        rawResponse: ByteArray,
        targetDeviceId: UUID,
        connectionId: UUID,
        config: DirectTransportServerConfig,
    ): ValidatedDirectPathTicket {
        val response = JSON.parseToJsonElement(rawResponse.decodeToString()) as? JsonObject
            ?: error("ticket response must be object")
        response.requireExactKeys(TICKET_RESPONSE_KEYS)
        require(response.requiredInt("protocol_version") == PROTOCOL_VERSION)
        val wire = response.requiredString("ticket_protobuf").decodeBase64UrlStrict()
        require(wire.size in MIN_TICKET_BYTES..MAX_TICKET_BYTES)
        val ticket = DirectPathTicketV1.parseFrom(wire)
        require(wire.contentEquals(canonicalTicket(ticket).toByteArray())) {
            "ticket protobuf is not canonical"
        }
        require(ticket.formatVersion == PROTOCOL_VERSION)
        require(ticket.ticketId.isNonZeroId() && ticket.accountId.isNonZeroId())
        require(ticket.initiatorDeviceId.matches(deviceId.toBytes()))
        require(ticket.responderDeviceId.matches(targetDeviceId.toBytes()))
        require(ticket.connectionId.matches(connectionId.toBytes()))
        require(ticket.pathEpoch in 1..Long.MAX_VALUE)
        require(ticket.serverNonce.size() == ID_BYTES)
        require(ticket.serverSignature.size() == ED25519_SIGNATURE_BYTES)
        require(ticket.allowedCarriersList == listOf(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC))
        val keyId = ticket.serverSigningKeyId.decodeUtf8Strict()
        val signingKey = config.ticketSigningKeys[keyId] ?: error("ticket key is not pinned")
        val now = clockMillis()
        require(ticket.issuedAtMs > 0 && ticket.expiresAtMs > ticket.issuedAtMs)
        require(ticket.expiresAtMs - ticket.issuedAtMs <= MAX_TICKET_LIFETIME_MILLIS)
        require(ticket.issuedAtMs <= safeAdd(now, MAX_TICKET_CLOCK_SKEW_MILLIS))
        require(now < ticket.expiresAtMs)
        val claims = canonicalTicket(ticket).toBuilder().clearServerSignature().build().toByteArray()
        val signed = TICKET_SIGNATURE_DOMAIN + claims
        require(
            ticketSignatureVerifier.verify(
                signingKey.publicKey.copyOf(),
                signed,
                ticket.serverSignature.toByteArray(),
            ),
        ) { "ticket signature rejected" }
        return ValidatedDirectPathTicket(ticket, wire)
    }

    private fun validateSignal(
        envelope: TransportSignalEnvelopeV1,
        wire: ByteArray,
        outbound: Boolean,
        directDataAllowed: Boolean,
    ): Boolean = runCatching {
        require(wire.size in 1..MAX_SIGNAL_FRAME_BYTES)
        require(wire.contentEquals(canonicalSignal(envelope).toByteArray()))
        require(envelope.protocolVersion == PROTOCOL_VERSION)
        val signalType = envelope.signalType.number
        require(
            signalType in 1..5 || signalType == 10 ||
                directDataAllowed && signalType in DIRECT_DATA_SIGNAL_TYPES ||
                multipairV2Configured.get() && signalType in 11..14,
        )
        require(envelope.connectionId.isNonZeroId())
        require(envelope.senderDeviceId.size() == ID_BYTES)
        require(envelope.recipientDeviceId.size() == ID_BYTES)
        val own = deviceId.toBytes()
        if (outbound) {
            require(envelope.senderDeviceId.matches(own) && !envelope.recipientDeviceId.matches(own))
        } else {
            require(envelope.recipientDeviceId.matches(own) && !envelope.senderDeviceId.matches(own))
        }
        require(envelope.pathEpoch in 1..Long.MAX_VALUE)
        require(envelope.signalSeq in 1..Long.MAX_VALUE)
        require(envelope.signalAck in 0..Long.MAX_VALUE)
        require(envelope.sentAtMs > 0 && withinClockSkew(clockMillis(), envelope.sentAtMs))
        require(envelope.keyId.size() in 1..MAX_KEY_ID_BYTES)
        require(envelope.nonce.size() == SIGNAL_NONCE_BYTES)
        require(envelope.ciphertext.size() in MIN_CIPHERTEXT_BYTES..MAX_SIGNAL_CIPHERTEXT_BYTES)
    }.isSuccess

    private fun validateWelcome(text: String): Set<String>? = runCatching {
        val value = JSON.parseToJsonElement(text) as? JsonObject
            ?: error("welcome must be an object")
        val required = setOf(
            "type",
            "protocol_version",
            "capabilities",
            "heartbeat_interval_ms",
            "max_signal_frame_bytes",
        )
        require(value.keys.containsAll(required))
        require(value.requiredString("type") == "SERVER_WELCOME")
        require(value.requiredInt("protocol_version") == PROTOCOL_VERSION)
        val expectedCapabilities = if (multipairV2Configured.get()) {
            listOf(DIRECT_SHADOW_CAPABILITY, DIRECT_MULTIPAIR_CAPABILITY)
        } else {
            listOf(DIRECT_SHADOW_CAPABILITY)
        }
        val capabilities = value.requiredStringList("capabilities", MAX_CONFIG_CAPABILITIES)
        require(capabilities.distinct().size == capabilities.size)
        val validCapabilities = capabilities == expectedCapabilities ||
            directDataV1Enabled && capabilities == expectedCapabilities + DIRECT_DATA_CAPABILITY ||
            directDataV1Enabled && capabilities == expectedCapabilities + listOf(
                DIRECT_DATA_CAPABILITY,
                DIRECT_SESSION_CUTOVER_CAPABILITY,
            )
        require(validCapabilities)
        require(value.requiredInt("heartbeat_interval_ms") in 1_000..45_000)
        require(value.requiredInt("max_signal_frame_bytes") == MAX_SIGNAL_FRAME_BYTES)
        capabilities.toSet()
    }.getOrNull()

    internal fun isDirectDataV1Negotiated(
        expectedGeneration: Int = generation.get(),
    ): Boolean = synchronized(lock) {
        isDirectDataV1NegotiatedLocked(expectedGeneration)
    }

    private fun isDirectDataV1NegotiatedLocked(expectedGeneration: Int): Boolean =
        !closed.get() && expectedGeneration > 0 && generation.get() == expectedGeneration &&
            connectedGeneration == expectedGeneration && socket != null &&
            DIRECT_DATA_CAPABILITY in connectedCapabilities &&
            DIRECT_SESSION_CUTOVER_CAPABILITY in connectedCapabilities

    private fun publishNegotiatedCapabilitiesLocked(
        capabilityGeneration: Int,
        capabilities: Set<String>,
    ) {
        mutableNegotiatedCapabilities.value = DirectTransportSignalingNegotiatedCapabilities(
            generation = capabilityGeneration,
            capabilities = capabilities.toSet(),
        )
    }

    private fun flushOutgoingQueueLocked(webSocket: WebSocket): Boolean {
        while (outgoingQueue.isNotEmpty()) {
            val bytes = outgoingQueue.removeFirst()
            queuedOutgoingBytes -= bytes.size
            val sent = try {
                webSocket.send(OkioByteString.of(*bytes))
            } finally {
                bytes.fill(0)
            }
            if (!sent) {
                clearOutgoingQueueLocked()
                return false
            }
        }
        return true
    }

    private fun clearOutgoingQueueLocked() {
        outgoingQueue.forEach { it.fill(0) }
        outgoingQueue.clear()
        queuedOutgoingBytes = 0
    }

    private fun rememberCursor(target: LinkedHashMap<SignalCursor, Long>, cursor: SignalCursor, seq: Long) {
        target[cursor] = seq
        while (target.size > MAX_SIGNAL_CURSORS) target.remove(target.keys.first())
    }

    private fun isExactNextSignalSequence(previous: Long, next: Long): Boolean =
        previous in 0 until Long.MAX_VALUE && next == previous + 1

    private fun rememberNonce(target: LinkedHashSet<SignalNonceKey>, nonce: SignalNonceKey) {
        target += nonce
        while (target.size > MAX_SIGNAL_NONCES) target.remove(target.first())
    }

    private fun TransportSignalEnvelopeV1.cursor(): SignalCursor = SignalCursor(
        connectionId.toByteArray().toHexKey(),
        senderDeviceId.toByteArray().toHexKey(),
        pathEpoch,
    )

    private fun TransportSignalEnvelopeV1.nonceKey(): SignalNonceKey = SignalNonceKey(
        connectionId.toByteArray().toHexKey(),
        senderDeviceId.toByteArray().toHexKey(),
        pathEpoch,
        nonce.toByteArray().toHexKey(),
    )

    private fun canonicalSignal(value: TransportSignalEnvelopeV1): TransportSignalEnvelopeV1 =
        TransportSignalEnvelopeV1.newBuilder()
            .setProtocolVersion(value.protocolVersion)
            .setSignalType(value.signalType)
            .setConnectionId(value.connectionId)
            .setSenderDeviceId(value.senderDeviceId)
            .setRecipientDeviceId(value.recipientDeviceId)
            .setPathEpoch(value.pathEpoch)
            .setSignalSeq(value.signalSeq)
            .setSignalAck(value.signalAck)
            .setSentAtMs(value.sentAtMs)
            .setKeyId(value.keyId)
            .setNonce(value.nonce)
            .setCiphertext(value.ciphertext)
            .build()

    private fun canonicalTicket(value: DirectPathTicketV1): DirectPathTicketV1 =
        DirectPathTicketV1.newBuilder()
            .setFormatVersion(value.formatVersion)
            .setTicketId(value.ticketId)
            .setAccountId(value.accountId)
            .setInitiatorDeviceId(value.initiatorDeviceId)
            .setResponderDeviceId(value.responderDeviceId)
            .setConnectionId(value.connectionId)
            .setPathEpoch(value.pathEpoch)
            .setIssuedAtMs(value.issuedAtMs)
            .setExpiresAtMs(value.expiresAtMs)
            .addAllAllowedCarriers(value.allowedCarriersList)
            .setServerNonce(value.serverNonce)
            .setServerSigningKeyId(value.serverSigningKeyId)
            .setServerSignature(value.serverSignature)
            .build()

    private fun Request.Builder.proofHeaders(proof: DirectSignalingProofHeaders): Request.Builder =
        header("X-TermLink-Device", proof.deviceId)
            .header("X-TermLink-Timestamp", proof.timestampMillis.toString())
            .header("X-TermLink-Nonce", proof.nonce)
            .header("X-TermLink-Signature", proof.signature)

    private fun httpFailure(response: Response): DirectSignalingFault? {
        if (response.isSuccessful) return null
        return DirectSignalingFault(
            if (response.code == 401 || response.code == 403) {
                DirectSignalingFaultCode.AUTHENTICATION_REJECTED
            } else {
                DirectSignalingFaultCode.HTTP_UNAVAILABLE
            },
            response.code,
        )
    }

    private fun httpFailure(response: CancellableHttpResponse): DirectSignalingFault? {
        if (response.successful) return null
        return DirectSignalingFault(
            if (response.code == 401 || response.code == 403) {
                DirectSignalingFaultCode.AUTHENTICATION_REJECTED
            } else {
                DirectSignalingFaultCode.HTTP_UNAVAILABLE
            },
            response.code,
        )
    }

    private fun readBoundedBody(response: Response, maximumBytes: Int): ByteArray? {
        val body = response.body ?: return null
        val output = ByteArrayOutputStream(minOf(maximumBytes, 4_096))
        val buffer = ByteArray(4_096)
        body.byteStream().use { input ->
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                if (output.size() + read > maximumBytes) return null
                output.write(buffer, 0, read)
            }
        }
        return output.toByteArray()
    }

    private fun closedFault(): DirectSignalingCallResult.Fault = DirectSignalingCallResult.Fault(
        DirectSignalingFault(DirectSignalingFaultCode.CLOSED),
    )

    private fun retryBaseMillis(retryNumber: Int): Long {
        var result = RETRY_BASE_MILLIS
        repeat((retryNumber - 1).coerceAtLeast(0)) {
            result = (result * 2).coerceAtMost(RETRY_MAX_MILLIS)
        }
        return result
    }

    private fun JsonObject.requireExactKeys(expected: Set<String>) {
        require(keys == expected)
    }

    private fun JsonObject.requiredString(name: String): String {
        val value = this[name] as? JsonPrimitive ?: error("$name missing")
        require(value.isString)
        return value.content
    }

    private fun JsonObject.requiredInt(name: String): Int {
        val value = this[name] as? JsonPrimitive ?: error("$name missing")
        require(!value.isString)
        return value.content.toInt()
    }

    private fun JsonObject.requiredBoolean(name: String): Boolean {
        val value = this[name] as? JsonPrimitive ?: error("$name missing")
        require(!value.isString)
        return value.content.toBooleanStrict()
    }

    private fun JsonObject.requiredStringList(name: String, maximum: Int): List<String> {
        val array = this[name] as? JsonArray ?: error("$name missing")
        require(array.size <= maximum)
        val values = array.map {
            val value = it as? JsonPrimitive ?: error("$name item invalid")
            require(value.isString)
            value.content
        }
        require(values.distinct().size == values.size)
        return values
    }

    private fun String.decodeBase64UrlStrict(): ByteArray {
        require(isNotEmpty() && length <= MAX_BASE64_TEXT_CHARS && '=' !in this)
        val decoded = Base64.getUrlDecoder().decode(this)
        require(DirectTransportSignalingProof.run { decoded.base64Url() } == this)
        return decoded
    }

    private fun ByteString.decodeUtf8Strict(): String {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        val value = decoder.decode(ByteBuffer.wrap(toByteArray())).toString()
        require(value.matches(DirectTicketSigningKey.KEY_ID_PATTERN))
        return value
    }

    private fun ByteString.matches(expected: ByteArray): Boolean =
        size() == expected.size && MessageDigest.isEqual(toByteArray(), expected)

    private fun ByteString.isNonZeroId(): Boolean =
        size() == ID_BYTES && toByteArray().any { it.toInt() != 0 }

    private fun UUID.toBytes(): ByteArray = ByteBuffer.allocate(ID_BYTES).order(ByteOrder.BIG_ENDIAN)
        .putLong(mostSignificantBits)
        .putLong(leastSignificantBits)
        .array()

    private fun ByteArray.toHexKey(): String = joinToString("") { "%02x".format(it) }

    private fun withinClockSkew(now: Long, sentAt: Long): Boolean = try {
        val difference = Math.subtractExact(now, sentAt)
        difference != Long.MIN_VALUE && Math.abs(difference) <= MAX_SIGNAL_CLOCK_SKEW_MILLIS
    } catch (_: ArithmeticException) {
        false
    }

    private fun safeAdd(value: Long, increment: Long): Long = try {
        Math.addExact(value, increment)
    } catch (_: ArithmeticException) {
        Long.MAX_VALUE
    }

    private fun validStunServer(value: String): Boolean {
        if (!value.startsWith("stun:") || value.length > MAX_STUN_TEXT_CHARS || value.any(Char::isWhitespace)) {
            return false
        }
        val authority = value.removePrefix("stun:")
        val split = if (authority.startsWith("[")) {
            val closing = authority.indexOf(']')
            if (closing <= 1 || closing + 1 >= authority.length || authority[closing + 1] != ':') return false
            authority.substring(0, closing + 1) to authority.substring(closing + 2)
        } else {
            val separator = authority.lastIndexOf(':')
            if (separator <= 0 || ':' in authority.substring(0, separator)) return false
            authority.substring(0, separator) to authority.substring(separator + 1)
        }
        val host = split.first
        val portText = split.second
        val port = portText.toIntOrNull() ?: return false
        if (port !in 1..65_535 || port.toString() != portText) return false
        if (host.startsWith("[")) {
            val literal = host.substring(1, host.length - 1)
            if (literal.isEmpty() || '%' in literal || !literal.all { it in "0123456789abcdef:" }) return false
            return runCatching {
                val address = java.net.InetAddress.getByName(literal)
                address.address.size == 16 && !address.isAnyLocalAddress && !address.isMulticastAddress
            }.getOrDefault(false)
        }
        if (host != host.lowercase() || host.length > 253) return false
        return host.split('.').all { label ->
            label.isNotEmpty() && label.length <= 63 && label.first() != '-' && label.last() != '-' &&
                label.all { it in 'a'..'z' || it in '0'..'9' || it == '-' }
        }
    }

    private data class QueuedIncomingSignal(
        val envelope: TransportSignalEnvelopeV1,
        val wireBytes: Int,
    )

    private data class SignalCursor(
        val connectionId: String,
        val senderDeviceId: String,
        val pathEpoch: Long,
    )

    private data class SignalNonceKey(
        val connectionId: String,
        val senderDeviceId: String,
        val pathEpoch: Long,
        val nonce: String,
    )

    internal companion object {
        internal const val CONFIG_PATH = "/v1/transport/config"
        internal const val TICKET_PATH = "/v1/transport/tickets"
        internal const val SIGNAL_PATH = "/v1/transport/signals"
        internal const val SIGNAL_SUBPROTOCOL = "termlink.transport.v1"
        /** Client Hello protocol request; this value never authorizes application-data routing. */
        internal const val DIRECT_TRANSPORT_CAPABILITY = "direct_transport_v1"
        internal const val DIRECT_SIGNALING_CAPABILITY = "direct_signaling_v1"
        internal const val DIRECT_SHADOW_CAPABILITY = "direct_transport_shadow_v1"
        internal const val DIRECT_MULTIPAIR_CAPABILITY = "direct_transport_shadow_multipair_v2"
        internal const val DIRECT_DATA_CAPABILITY = "direct_transport_data_v1"
        internal const val DIRECT_SESSION_CUTOVER_CAPABILITY = "direct_session_cutover_v1"
        internal const val MAX_SIGNAL_FRAME_BYTES = 64 * 1_024
        internal const val MAX_QUEUED_SIGNAL_FRAMES = 32
        internal const val MAX_QUEUED_SIGNAL_BYTES = 512 * 1_024L
        internal const val MAX_HELLO_BYTES = 4 * 1_024
        internal const val MAX_SIGNAL_RETRIES = 3
        private const val PROTOCOL_VERSION = 1
        private const val ID_BYTES = 16
        private const val SIGNAL_NONCE_BYTES = 12
        private const val MIN_CIPHERTEXT_BYTES = 16
        private const val MAX_SIGNAL_CIPHERTEXT_BYTES = MAX_SIGNAL_FRAME_BYTES - 128
        private const val MAX_KEY_ID_BYTES = 64
        private const val MAX_SIGNAL_CLOCK_SKEW_MILLIS = 30_000L
        private const val MAX_TICKET_CLOCK_SKEW_MILLIS = 30_000L
        private const val MAX_TICKET_LIFETIME_MILLIS = 120_000L
        private const val MIN_TICKET_BYTES = 128
        private const val MAX_TICKET_BYTES = 2_048
        private const val ED25519_SIGNATURE_BYTES = 64
        private const val MAX_CONFIG_RESPONSE_BYTES = 16 * 1_024
        private const val MAX_TICKET_REQUEST_BYTES = 4 * 1_024
        private const val MAX_TICKET_RESPONSE_BYTES = 8 * 1_024
        private const val MAX_CONFIG_CAPABILITIES = 16
        private const val MAX_TICKET_SIGNING_KEYS = 4
        private const val MAX_STUN_SERVERS = 2
        private const val MAX_STUN_TEXT_CHARS = 320
        private const val MAX_BASE64_TEXT_CHARS = 4_096
        private const val MAX_SIGNAL_CURSORS = 64
        private const val MAX_SIGNAL_NONCES = 512
        private const val HTTP_TIMEOUT_MILLIS = 10_000L
        private const val SIGNAL_PING_INTERVAL_MILLIS = 15_000L
        private const val SIGNAL_HANDSHAKE_TIMEOUT_MILLIS = 10_000L
        private const val RETRY_BASE_MILLIS = 250L
        private const val RETRY_MAX_MILLIS = 30_000L
        private const val MAX_CLOSE_REASON_CHARS = 120
        private val DIRECT_DATA_SIGNAL_TYPES = 6..9
        private val CAPABILITY_PATTERN = Regex("[a-z][a-z0-9._-]{0,63}")
        private val CONFIG_KEYS = setOf(
            "protocol_version",
            "capabilities",
            "direct_routing_permitted",
            "ticket_signing_keys",
            "stun_servers",
        )
        private val SHADOW_CONFIG_CAPABILITIES = setOf(
            DIRECT_SIGNALING_CAPABILITY,
            DIRECT_SHADOW_CAPABILITY,
        )
        private val TICKET_KEY_KEYS = setOf("key_id", "algorithm", "public_key")
        private val TICKET_RESPONSE_KEYS = setOf("protocol_version", "ticket_protobuf")
        private val TICKET_SIGNATURE_DOMAIN = "TERMLINK-DIRECT-TICKET-V1\n".encodeToByteArray()
        private val JSON = Json { ignoreUnknownKeys = false; explicitNulls = false }
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()
        private val NIL_UUID = UUID(0, 0)

        /**
         * Reports only the server-side data-routing gate. Shadow/v4 callers must still keep their
         * native `directRoutingPermitted` false and must not advertise [DIRECT_DATA_CAPABILITY].
         */
        internal fun hasDirectDataCapabilities(config: DirectTransportServerConfig): Boolean =
            config.protocolVersion == PROTOCOL_VERSION &&
                config.directRoutingPermitted &&
                validConfigCapabilities(config.capabilities, directRoutingPermitted = true) &&
                DIRECT_DATA_CAPABILITY in config.capabilities &&
                DIRECT_SESSION_CUTOVER_CAPABILITY in config.capabilities

        /**
         * The config endpoint is server-wide. It may expose optional multipair and direct-data
         * capabilities to a Shadow client, but no unknown or duplicated capability is accepted.
         * Data permission is valid only when the matching capability is present.
         */
        private fun validConfigCapabilities(
            capabilities: Set<String>,
            directRoutingPermitted: Boolean,
        ): Boolean {
            val hasDirectData = DIRECT_DATA_CAPABILITY in capabilities
            if (directRoutingPermitted != hasDirectData) return false
            val hasSessionCutover = DIRECT_SESSION_CUTOVER_CAPABILITY in capabilities
            if (hasSessionCutover && !hasDirectData) return false
            val shadowCapabilities = capabilities - setOf(
                DIRECT_DATA_CAPABILITY,
                DIRECT_SESSION_CUTOVER_CAPABILITY,
            )
            return shadowCapabilities == SHADOW_CONFIG_CAPABILITIES ||
                shadowCapabilities == SHADOW_CONFIG_CAPABILITIES + DIRECT_MULTIPAIR_CAPABILITY
        }

        private fun normalizeRoot(value: String, allowCleartext: Boolean): String {
            val normalized = value.trimEnd('/')
            val uri = URI(normalized)
            require(uri.scheme == "https" || allowCleartext && uri.scheme == "http")
            require(!uri.host.isNullOrBlank() && uri.rawQuery == null && uri.rawFragment == null)
            require(uri.userInfo == null && (uri.rawPath.isNullOrEmpty() || uri.rawPath == "/"))
            return normalized
        }

        private fun websocketRoot(root: String): String = when {
            root.startsWith("https://") -> "wss://" + root.removePrefix("https://")
            root.startsWith("http://") -> "ws://" + root.removePrefix("http://")
            else -> error("unsupported direct root")
        }
    }
}

package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.InputEventPayload
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.LeasePayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputAckPayload
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.ResizePayload
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.session.EventAcceptance
import dev.termlink.session.RemoteControlLease
import dev.termlink.session.RemoteInput
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionOutbound
import dev.termlink.session.RemoteSessionState
import dev.termlink.session.RemoteTerminalSession
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import java.io.Closeable
import java.security.SecureRandom
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient

data class ProductionRealtimeSessionConfig(
    val rootUrl: String,
    val tokenProvider: AccessTokenProvider,
    val androidDeviceId: UUID,
    val pcDeviceId: UUID,
    val sessionId: UUID,
    val keyEpoch: Long,
    val pcExchangePublicKey: ByteArray,
    val keyId: ByteArray = "epoch-${keyEpoch.toString().padStart(8, '0')}".encodeToByteArray(),
    val noncePrefix: ByteArray = ByteArray(4).also(SecureRandom()::nextBytes),
) {
    init {
        require(keyEpoch >= 0) { "keyEpoch must not be negative" }
        require(pcExchangePublicKey.size == 32) { "PC exchange public key must be 32 bytes" }
        require(pcExchangePublicKey.any { it.toInt() != 0 }) { "PC exchange public key is empty" }
        require(keyId.isNotEmpty() && keyId.size <= 128) { "keyId must be 1..128 bytes" }
        require(noncePrefix.size == 4) { "noncePrefix must be 4 bytes" }
    }
}

sealed interface ProductionTransportState {
    data object Connecting : ProductionTransportState
    data object Connected : ProductionTransportState
    data class Reconnecting(val attempt: Int, val reason: String?) : ProductionTransportState
    data class Failed(val reason: String) : ProductionTransportState
    data object Closed : ProductionTransportState
}

fun interface InputSequenceProvider {
    fun next(): Long
}

private class InMemoryInputSequenceProvider : InputSequenceProvider {
    private val sequence = AtomicLong(0)

    override fun next(): Long = sequence.incrementAndGet()
}

/**
 * E2EE Android session transport. It translates production Envelopes into the
 * renderer-independent [RemoteTerminalSession] contract and owns reconnect,
 * resume, lease, and input correlation state.
 */
class ProductionRealtimeSessionClient(
    engine: TerminalEngine,
    private val config: ProductionRealtimeSessionConfig,
    private val identity: DeviceIdentity,
    parentScope: CoroutineScope,
    private val httpClient: OkHttpClient = ProductionRealtimeClient.defaultHttpClient,
    allowCleartextForLocalTest: Boolean = false,
    private val reconnectDelaysMillis: List<Long> = listOf(250, 500, 1_000, 2_000, 4_000, 8_000, 16_000, 30_000),
    private val inputAcceptanceTimeoutMillis: Long = 5_000,
    private val inputSequenceProvider: InputSequenceProvider = InMemoryInputSequenceProvider(),
    private val subscriberRefreshIntervalMillis: Long = 15_000,
    leaseRenewalIntervalMillis: Long = 10_000,
    disposeEngineOnClose: Boolean = true,
) : RemoteSessionOutbound, Closeable {
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val closed = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val sendMutex = Mutex()
    private val materialLock = Any()
    private val envelopeSequence = AtomicLong(0)
    private val observedLocalInputEpoch = AtomicLong(0)
    private val pendingInputs = ConcurrentHashMap<UUID, CompletableDeferred<Unit>>()
    private val reconnectMutex = Mutex()
    private var reconnectJob: Job? = null
    private var subscriberRefreshJob: Job? = null
    private var reconnectAttempt = 0
    private val mutableTransportState = MutableStateFlow<ProductionTransportState>(ProductionTransportState.Connecting)
    private val pcExchangePublicKey = config.pcExchangePublicKey.copyOf()
    private val keyId = config.keyId.copyOf()
    private val noncePrefix = config.noncePrefix.copyOf()
    private val pairwiseKey = SessionCrypto.derivePairwiseKey(
        privateKey = identity.exchange.privateKey,
        peerPublicKey = pcExchangePublicKey,
        pcDeviceId = config.pcDeviceId,
        androidDeviceId = config.androidDeviceId,
        keyEpoch = config.keyEpoch,
    )
    private val transport = ProductionRealtimeClient(
        rootUrl = config.rootUrl,
        tokenProvider = config.tokenProvider,
        deviceId = config.androidDeviceId,
        identity = identity,
        role = "android",
        httpClient = httpClient,
        allowCleartextForLocalTest = allowCleartextForLocalTest,
    )
    private val api = ProductionApiClient(
        rootUrl = config.rootUrl,
        tokenProvider = config.tokenProvider,
        deviceId = config.androidDeviceId,
        identity = identity,
        httpClient = httpClient,
        allowCleartextForLocalTest = allowCleartextForLocalTest,
    )
    private val incomingJob: Job
    private val transportStateJob: Job
    private lateinit var session: RemoteTerminalSession

    init {
        require(reconnectDelaysMillis.isNotEmpty() && reconnectDelaysMillis.all { it in 0..30_000 }) {
            "reconnect delays must be non-empty and capped at 30 seconds"
        }
        require(inputAcceptanceTimeoutMillis > 0) { "input acceptance timeout must be positive" }
        require(subscriberRefreshIntervalMillis > 0) { "subscriber refresh interval must be positive" }
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = this,
            parentScope = scope,
            leaseRenewalIntervalMillis = leaseRenewalIntervalMillis,
            disposeEngineOnClose = disposeEngineOnClose,
        )
        this.session = session
        incomingJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            transport.incoming.collect { envelope ->
                try {
                    handleIncoming(envelope)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    failTransport(error.message ?: "invalid production Envelope")
                }
            }
        }
        transportStateJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            transport.state.collect(::handleTransportState)
        }
    }

    val remoteSession: RemoteTerminalSession
        get() = session
    val sessionState: StateFlow<RemoteSessionState>
        get() = session.state
    val transportState: StateFlow<ProductionTransportState> = mutableTransportState.asStateFlow()

    fun start() {
        check(!closed.get()) { "production session client is closed" }
        check(started.compareAndSet(false, true)) { "production session client is already started" }
        session.start()
        transport.connect()
    }

    suspend fun requestControl() {
        check(!closed.get()) { "production session client is closed" }
        val lease = withContext(Dispatchers.IO) {
            parseLease(
                api.post(
                    "/v1/sessions/${config.sessionId}/lease",
                    buildJsonObject { },
                ).jsonObject,
            )
        }
        session.grantControl(lease.leaseId, lease.expiresAtEpochMillis, lease.version)
    }

    suspend fun releaseControl() {
        val current = session.state.value as? RemoteSessionState.Controlling ?: return
        withContext(Dispatchers.IO) {
            api.delete("/v1/sessions/${config.sessionId}/lease/${current.leaseId}")
        }
        session.revokeControl("control released")
    }

    override suspend fun resume(lastAckedSequence: Long) {
        require(lastAckedSequence >= 0)
        val payload = SubscribePayload.newBuilder()
            .setResumeFromSeq(lastAckedSequence)
            .setSubscriberExchangePublicKey(ByteString.copyFrom(identity.exchange.publicKey))
            .build()
            .toByteArray()
        sendEncrypted(MessageType.SUBSCRIBE, payload, acknowledgement = lastAckedSequence)
    }

    override suspend fun acknowledge(highestContiguousSequence: Long) {
        require(highestContiguousSequence >= 0)
        val payload = OutputAckPayload.newBuilder()
            .setHighestContiguousSeq(highestContiguousSequence)
            .build()
            .toByteArray()
        sendEncrypted(
            MessageType.OUTPUT_ACK,
            payload,
            acknowledgement = highestContiguousSequence,
        )
    }

    override suspend fun sendInput(input: RemoteInput) {
        require(input.bytes.isNotEmpty()) { "input must not be empty" }
        val controlling = session.state.value as? RemoteSessionState.Controlling
            ?: throw ProductionTransportException("control lease is required")
        val pending = CompletableDeferred<Unit>()
        check(pendingInputs.putIfAbsent(input.inputId, pending) == null) { "input ID is already pending" }
        val inputSeq = inputSequenceProvider.next()
        require(inputSeq > 0) { "input sequence provider returned a non-positive value" }
        val plaintext = InputEventPayload.newBuilder()
            .setInputId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(input.inputId)))
            .setLeaseId(ByteString.copyFrom(leaseIdBytes(controlling.leaseId)))
            .setLeaseVersion(controlling.leaseVersion)
            .setInputSeq(inputSeq)
            .setInput(ByteString.copyFrom(input.bytes))
            .setObservedLocalInputEpoch(observedLocalInputEpoch.get())
            .build()
            .toByteArray()
        try {
            sendEncrypted(MessageType.INPUT_EVENT, plaintext)
            withTimeout(inputAcceptanceTimeoutMillis) { pending.await() }
        } catch (_: TimeoutCancellationException) {
            throw ProductionTransportException("input acceptance timed out")
        } catch (error: CancellationException) {
            throw error
        } finally {
            pendingInputs.remove(input.inputId, pending)
            plaintext.fill(0)
        }
    }

    override suspend fun sendResize(size: TerminalSize) {
        val controlling = session.state.value as? RemoteSessionState.Controlling
            ?: throw ProductionTransportException("control lease is required")
        val payload = ResizePayload.newBuilder()
            .setColumns(size.columns)
            .setRows(size.rows)
            .setLeaseId(ByteString.copyFrom(leaseIdBytes(controlling.leaseId)))
            .setLeaseVersion(controlling.leaseVersion)
            .build()
            .toByteArray()
        try {
            sendEncrypted(MessageType.RESIZE_REQUEST, payload)
        } finally {
            payload.fill(0)
        }
    }

    override suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease =
        withContext(Dispatchers.IO) {
            parseLease(
                api.post(
                    "/v1/sessions/${config.sessionId}/lease/$leaseId/renew",
                    buildJsonObject { },
                ).jsonObject,
            )
        }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        reconnectJob?.cancel()
        subscriberRefreshJob?.cancel()
        pendingInputs.values.forEach { it.completeExceptionally(ProductionTransportException("production session closed")) }
        pendingInputs.clear()
        transport.close()
        api.close()
        incomingJob.cancel()
        transportStateJob.cancel()
        session.close()
        synchronized(materialLock) {
            pairwiseKey.fill(0)
            pcExchangePublicKey.fill(0)
            noncePrefix.fill(0)
            keyId.fill(0)
        }
        scope.cancel()
        mutableTransportState.value = ProductionTransportState.Closed
    }

    private suspend fun sendEncrypted(
        messageType: MessageType,
        plaintext: ByteArray,
        acknowledgement: Long = session.state.value.lastAckedSequence,
    ) {
        sendMutex.withLock {
            check(!closed.get()) { "production session client is closed" }
            val sequence = envelopeSequence.incrementAndGet()
            val (nonce, key, keyIdSnapshot) = synchronized(materialLock) {
                val snapshot = keyId.copyOf()
                Triple(
                    SessionCrypto.nonce(noncePrefix, sequence.toULong()),
                    SessionCrypto.deriveSessionKey(
                        pairwiseKey,
                        config.sessionId,
                        CryptoDirection.ANDROID_TO_PC,
                        snapshot,
                    ),
                    snapshot,
                )
            }
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = config.sessionId,
                    senderId = config.androidDeviceId,
                    messageType = messageType,
                    direction = CryptoDirection.ANDROID_TO_PC,
                    sequence = sequence,
                    acknowledgement = acknowledgement,
                    keyId = keyIdSnapshot,
                    nonce = nonce,
                    sessionKey = key,
                    plaintext = plaintext,
                    recipientId = config.pcDeviceId,
                )
                if (!transport.send(envelope)) throw ProductionTransportException("realtime socket is not connected")
            } finally {
                key.fill(0)
                nonce.fill(0)
                keyIdSnapshot.fill(0)
                plaintext.fill(0)
            }
        }
    }

    private suspend fun handleIncoming(envelope: Envelope) {
        check(!closed.get()) { "production session client is closed" }
        validateEnvelope(envelope)
        val key = synchronized(materialLock) {
            SessionCrypto.deriveSessionKey(
                pairwiseKey,
                config.sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
        }
        val plaintext = try {
            ProductionRealtimeClient.decryptEnvelope(envelope, key)
        } finally {
            key.fill(0)
        }
        try {
            when (envelope.messageType) {
                MessageType.OUTPUT_CHUNK -> {
                    val payload = OutputChunkPayload.parseFrom(plaintext)
                    require(payload.output.size() > 0) { "output chunk is empty" }
                    requireApplied(session.accept(RemoteSessionEvent.Output(envelope.seq, payload.output.toByteArray())))
                }
                MessageType.RESIZE_APPLIED -> {
                    val payload = ResizePayload.parseFrom(plaintext)
                    requireApplied(
                        session.accept(
                            RemoteSessionEvent.Resize(envelope.seq, TerminalSize(payload.columns, payload.rows)),
                        ),
                    )
                }
                MessageType.GAP -> {
                    val payload = GapPayload.parseFrom(plaintext)
                    require(payload.start > 0 && payload.end >= payload.start) { "invalid history gap" }
                    // Older Windows runtime builds used gap_end as the
                    // envelope sequence; the production contract uses the
                    // first sequence after the missing range. Accept either
                    // wire form without weakening contiguous ACK semantics.
                    val eventSequence = when {
                        payload.end == envelope.seq - 1L -> envelope.seq
                        payload.end == envelope.seq -> envelope.seq + 1L
                        else -> error("history gap sequence does not match Envelope.seq")
                    }
                    val acceptance = session.accept(RemoteSessionEvent.Gap(eventSequence, payload.start, payload.end))
                    require(acceptance is EventAcceptance.HistoryGap) { "gap did not enter History Gap" }
                }
                MessageType.SESSION_UPSERT -> {
                    val lifecycle = SessionUpsertPayload.parseFrom(plaintext).lifecycle.uppercase()
                    when (lifecycle) {
                        "RUNNING", "STARTED", "CHANGED" -> requireApplied(
                            session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.RUNNING)),
                        )
                        "INTERRUPTED" -> requireApplied(
                            session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.INTERRUPTED)),
                        )
                        "EXITED", "ENDED" -> requireApplied(
                            session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.ENDED)),
                        )
                        else -> error("unknown session lifecycle")
                    }
                }
                MessageType.SESSION_STARTED -> requireApplied(
                    session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.RUNNING)),
                )
                MessageType.SESSION_INTERRUPTED -> requireApplied(
                    session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.INTERRUPTED)),
                )
                MessageType.SESSION_ENDED -> requireApplied(
                    session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.ENDED)),
                )
                MessageType.INPUT_ACCEPTED, MessageType.INPUT_REJECTED -> {
                    val result = InputResultPayload.parseFrom(plaintext)
                    val inputId = ProductionRealtimeClient.uuidFromBytes(result.inputId.toByteArray())
                        ?: error("InputResult input ID must be 16 bytes")
                    require(
                        (envelope.messageType == MessageType.INPUT_ACCEPTED) == result.accepted,
                    ) { "InputResult type and accepted flag disagree" }
                    val pending = pendingInputs.remove(inputId)
                    try {
                        requireApplied(
                            session.accept(
                                RemoteSessionEvent.InputResult(
                                    sequence = envelope.seq,
                                    inputId = inputId,
                                    accepted = result.accepted,
                                    reason = result.reasonCode.ifBlank { null },
                                ),
                            ),
                        )
                        if (pending != null) {
                            if (envelope.messageType == MessageType.INPUT_ACCEPTED && result.accepted) {
                                pending.complete(Unit)
                            } else {
                                pending.completeExceptionally(
                                    ProductionTransportException(result.reasonCode.ifBlank { "input rejected" }),
                                )
                            }
                        }
                    } catch (error: Throwable) {
                        pending?.completeExceptionally(error)
                        throw error
                    }
                }
                MessageType.LEASE_GRANTED -> {
                    val payload = LeasePayload.parseFrom(plaintext)
                    val leaseId = decodeLeaseId(payload.leaseId.toByteArray())
                    requireApplied(session.accept(RemoteSessionEvent.ControlGranted(envelope.seq)))
                    session.grantControl(leaseId, payload.expiresAtMs, payload.version)
                }
                MessageType.LEASE_DENIED, MessageType.LEASE_REVOKED -> {
                    val payload = LeasePayload.parseFrom(plaintext)
                    if (payload.localInputEpoch > 0) {
                        observedLocalInputEpoch.accumulateAndGet(payload.localInputEpoch, ::maxOf)
                    }
                    requireApplied(
                        session.accept(
                            RemoteSessionEvent.ControlRevoked(
                                sequence = envelope.seq,
                                reason = payload.reasonCode.ifBlank { "control lease denied" },
                            ),
                        ),
                    )
                }
                MessageType.OUTPUT_ACK, MessageType.HEARTBEAT -> Unit
                else -> error("unsupported production message type ${envelope.messageType}")
            }
        } finally {
            plaintext.fill(0)
        }
    }

    private fun validateEnvelope(envelope: Envelope) {
        require(envelope.protocolVersion == 1) { "unsupported Envelope protocol version" }
        require(envelope.sessionId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.sessionId))) {
            "Envelope session mismatch"
        }
        require(envelope.senderDeviceId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.pcDeviceId))) {
            "Envelope sender mismatch"
        }
        require(envelope.recipientDeviceId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.androidDeviceId))) {
            "Envelope recipient mismatch"
        }
        val expectedKeyId = synchronized(materialLock) { keyId.copyOf() }
        try {
            require(envelope.keyId.toByteArray().contentEquals(expectedKeyId)) { "Envelope key ID mismatch" }
        } finally {
            expectedKeyId.fill(0)
        }
        require(envelope.seq > 0 && envelope.nonce.size() == 12 && envelope.ciphertext.size() >= 16) {
            "Envelope cryptographic metadata is invalid"
        }
    }

    private fun parseLease(value: kotlinx.serialization.json.JsonObject): RemoteControlLease {
        val leaseId = value.getValue("lease_id").jsonPrimitive.content
        UUID.fromString(leaseId)
        val expiresAt = Instant.parse(value.getValue("expires_at").jsonPrimitive.content).toEpochMilli()
        val version = value.getValue("version").jsonPrimitive.content.toLong()
        require(version > 0 && expiresAt > System.currentTimeMillis()) { "server returned an invalid control lease" }
        return RemoteControlLease(leaseId, expiresAt, version)
    }

    private suspend fun handleTransportState(state: RealtimeState) {
        if (closed.get()) return
        when (state) {
            RealtimeState.Connecting -> mutableTransportState.value = ProductionTransportState.Connecting
            RealtimeState.Connected -> {
                reconnectJob?.cancel()
                reconnectJob = null
                reconnectAttempt = 0
                mutableTransportState.value = ProductionTransportState.Connected
                if (!session.onConnected()) {
                    transport.close()
                } else {
                    startSubscriberRefresh()
                }
            }
            is RealtimeState.Failed -> {
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                failPendingInputs(state.reason)
                session.onDisconnected(state.reason)
                scheduleReconnect(state.reason)
            }
            is RealtimeState.Reconnecting -> Unit
            RealtimeState.Closed -> {
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                failPendingInputs("production transport closed")
                mutableTransportState.value = ProductionTransportState.Closed
            }
        }
    }

    private fun startSubscriberRefresh() {
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = scope.launch {
            while (true) {
                delay(subscriberRefreshIntervalMillis)
                try {
                    resume(session.state.value.lastAckedSequence)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    failTransport(error.message ?: "subscriber refresh failed")
                    break
                }
            }
        }
    }

    private suspend fun scheduleReconnect(reason: String?) {
        reconnectMutex.withLock {
            if (closed.get() || session.state.value is RemoteSessionState.Exited) return
            if (reconnectJob?.isActive == true) return
            reconnectAttempt++
            mutableTransportState.value = ProductionTransportState.Reconnecting(reconnectAttempt, reason)
            val delayMillis = reconnectDelaysMillis[(reconnectAttempt - 1).coerceAtMost(reconnectDelaysMillis.lastIndex)]
            reconnectJob = scope.launch {
                delay(delayMillis.toLong())
                // Clear the scheduled attempt before starting OkHttp. A fast
                // offline failure can call back from newWebSocket while this
                // coroutine is still active; leaving it installed would make
                // scheduleReconnect discard the next retry.
                reconnectMutex.withLock { reconnectJob = null }
                if (!closed.get()) transport.connect()
            }
        }
    }

    private fun failPendingInputs(reason: String) {
        pendingInputs.values.forEach { it.completeExceptionally(ProductionTransportException(reason)) }
        pendingInputs.clear()
    }

    private fun failTransport(reason: String) {
        if (closed.get()) return
        transport.disconnect(reason)
        mutableTransportState.value = ProductionTransportState.Failed(reason)
        failPendingInputs(reason)
        scope.launch {
            session.onDisconnected(reason)
            scheduleReconnect(reason)
        }
    }

    private fun requireApplied(acceptance: EventAcceptance) {
        require(
            acceptance is EventAcceptance.Applied ||
                acceptance is EventAcceptance.Duplicate ||
                acceptance is EventAcceptance.IgnoredAfterExit,
        ) { "production event was not contiguous: $acceptance" }
    }

    private fun leaseIdBytes(value: String): ByteArray =
        runCatching { ProductionRealtimeClient.uuidBytes(UUID.fromString(value)) }
            .getOrElse { value.encodeToByteArray().also { require(it.isNotEmpty() && it.size <= 128) } }

    private fun decodeLeaseId(bytes: ByteArray): String {
        require(bytes.isNotEmpty() && bytes.size <= 128) { "lease ID is invalid" }
        return ProductionRealtimeClient.uuidFromBytes(bytes)?.toString() ?: bytes.decodeToString()
    }

    private class ProductionTransportException(message: String) : IllegalStateException(message)
}

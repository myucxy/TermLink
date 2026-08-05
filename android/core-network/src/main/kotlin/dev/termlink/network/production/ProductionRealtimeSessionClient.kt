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
import dev.termlink.session.RemoteInputDeliveryUnknownException
import dev.termlink.session.RemoteInputRejectedException
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
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
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
        require(keyEpoch > 0) { "keyEpoch must be positive" }
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
    private val envelopeSequenceProvider: InputSequenceProvider = InMemoryInputSequenceProvider(),
    initialAcknowledgedSequence: Long = 0,
    initialObservedLocalInputEpoch: Long = 0,
    private val acknowledgementObserver: suspend (Long) -> Long = { it },
    private val acknowledgementResetObserver: suspend () -> Unit = {},
    private val authoritativeMetadataRefreshObserver: suspend () -> Unit = {},
    private val metadataRefreshCooldownMillis: Long = 10_000,
    private val previousEpochGraceMillis: Long = 30_000,
    private val subscriberRefreshIntervalMillis: Long = 15_000,
    leaseRenewalIntervalMillis: Long = 10_000,
    disposeEngineOnClose: Boolean = true,
) : RemoteSessionOutbound, Closeable {
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + scopeJob)
    private val cleanupScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val closed = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val metadataRefreshInFlight = AtomicBoolean(false)
    private val historyRecoveryInProgress = AtomicBoolean(false)
    private val nextMetadataRefreshAtMillis = AtomicLong(0)
    private val sendMutex = Mutex()
    private val incomingProcessingMutex = Mutex()
    private val controlArbitrationMutex = Mutex()
    private val materialLock = Any()
    private val observedLocalInputEpoch = AtomicLong(initialObservedLocalInputEpoch)
    private val pendingInputs = ConcurrentHashMap<UUID, CompletableDeferred<Unit>>()
    private val reconnectMutex = Mutex()
    private var reconnectJob: Job? = null
    private var subscriberRefreshJob: Job? = null
    private var previousMaterialRetirementJob: Job? = null
    private var reconnectAttempt = 0
    private var controlGeneration = 0L
    private val mutableTransportState = MutableStateFlow<ProductionTransportState>(ProductionTransportState.Connecting)
    private val noncePrefix = config.noncePrefix.copyOf()
    private var currentMaterial = SessionKeyMaterial(
        epoch = config.keyEpoch,
        exchangePublicKey = config.pcExchangePublicKey.copyOf(),
        keyId = config.keyId.copyOf(),
        pairwiseKey = SessionCrypto.derivePairwiseKey(
            privateKey = identity.exchange.privateKey,
            peerPublicKey = config.pcExchangePublicKey,
            pcDeviceId = config.pcDeviceId,
            androidDeviceId = config.androidDeviceId,
            keyEpoch = config.keyEpoch,
        ),
    )
    private var previousMaterial: SessionKeyMaterial? = null
    private var previousMaterialGeneration = 0L
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
        require(initialObservedLocalInputEpoch >= 0) { "initialObservedLocalInputEpoch must not be negative" }
        require(metadataRefreshCooldownMillis > 0) { "metadata refresh cooldown must be positive" }
        require(previousEpochGraceMillis in 1..60_000) { "previous epoch grace must be 1..60000 milliseconds" }
        require(subscriberRefreshIntervalMillis > 0) { "subscriber refresh interval must be positive" }
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = this,
            parentScope = scope,
            initialAcknowledgedSequence = initialAcknowledgedSequence,
            leaseRenewalIntervalMillis = leaseRenewalIntervalMillis,
            disposeEngineOnClose = disposeEngineOnClose,
        )
        this.session = session
        incomingJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
            transport.incoming.collect { envelope ->
                try {
                    incomingProcessingMutex.withLock { handleIncoming(envelope) }
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    val now = System.currentTimeMillis()
                    if (error is UnknownKeyMaterialException &&
                        now >= nextMetadataRefreshAtMillis.get() &&
                        metadataRefreshInFlight.compareAndSet(false, true)
                    ) {
                        nextMetadataRefreshAtMillis.set(now + metadataRefreshCooldownMillis)
                        scope.launch {
                            try {
                                authoritativeMetadataRefreshObserver()
                            } catch (error: CancellationException) {
                                throw error
                            } catch (_: Exception) {
                                // The transport reconnect loop remains the
                                // fallback; metadata refresh is best effort.
                            } finally {
                                metadataRefreshInFlight.set(false)
                            }
                        }
                    }
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
        val requestGeneration = controlArbitrationMutex.withLock { controlGeneration }
        val lease = withContext(Dispatchers.IO) {
            parseLease(
                api.post(
                    "/v1/sessions/${config.sessionId}/lease",
                    buildJsonObject { },
                ).jsonObject,
            )
        }
        val granted = controlArbitrationMutex.withLock {
            if (controlGeneration != requestGeneration) {
                false
            } else {
                session.grantControl(lease.leaseId, lease.expiresAtEpochMillis, lease.version)
                true
            }
        }
        if (!granted) {
            // A newer local-input/revocation fence won while the REST request
            // was in flight. Release the stale server lease instead of
            // reopening Android input.
            withContext(Dispatchers.IO) {
                runCatching { api.delete("/v1/sessions/${config.sessionId}/lease/${lease.leaseId}") }
            }
            throw ProductionTransportException("control request was superseded by a newer revoke")
        }
    }

    suspend fun releaseControl() {
        val current = controlArbitrationMutex.withLock {
            controlGeneration++
            session.state.value as? RemoteSessionState.Controlling
        } ?: return
        withContext(Dispatchers.IO) {
            api.delete("/v1/sessions/${config.sessionId}/lease/${current.leaseId}")
        }
        controlArbitrationMutex.withLock {
            val active = session.state.value as? RemoteSessionState.Controlling
            if (active?.leaseId == current.leaseId) session.revokeControl("control released")
        }
    }

    /**
     * Replaces an unrecoverable live Gap with a verified raw event prefix.
     * The caller must provide events from sequence 1; ACKs are collapsed to a
     * single cumulative update before a fresh SUBSCRIBE re-establishes the PC
     * subscriber route.
     */
    suspend fun recoverFromHistory(
        events: List<RemoteSessionEvent>,
        recoveredLocalInputEpoch: Long = 0,
        expectedGap: RemoteSessionState.HistoryGap = session.state.value as? RemoteSessionState.HistoryGap
            ?: error("history recovery requires an active History Gap"),
    ): Long {
        check(!closed.get()) { "production session client is closed" }
        require(recoveredLocalInputEpoch >= 0) { "recoveredLocalInputEpoch must not be negative" }
        check(historyRecoveryInProgress.compareAndSet(false, true)) { "history recovery is already running" }
        val refreshJob = subscriberRefreshJob
        return try {
            refreshJob?.cancelAndJoin()
            if (subscriberRefreshJob === refreshJob) subscriberRefreshJob = null
            check(!closed.get()) { "production session client is closed" }
            incomingProcessingMutex.withLock {
                check(!closed.get()) { "production session client is closed" }
                observedLocalInputEpoch.accumulateAndGet(recoveredLocalInputEpoch, ::maxOf)
                session.recoverFromHistory(
                    events,
                    expectedGap,
                    persist = { recovered ->
                        withContext(NonCancellable) {
                            require(persistAcknowledgement(recovered) == recovered) {
                                "durable ACK commit did not reach the recovered cursor"
                            }
                        }
                    },
                    publish = { recovered ->
                        sendAcknowledgement(recovered)
                        resume(recovered)
                    },
                )
            }
        } finally {
            historyRecoveryInProgress.set(false)
            if (!closed.get() && mutableTransportState.value is ProductionTransportState.Connected) {
                startSubscriberRefresh()
            }
        }
    }

    /** Applies only authoritative, monotonic REST key-epoch updates. */
    fun validateKeyMaterialUpdate(
        keyEpoch: Long,
        pcExchangePublicKey: ByteArray,
    ): Boolean {
        require(keyEpoch > 0) { "keyEpoch must be positive" }
        require(pcExchangePublicKey.size == 32 && pcExchangePublicKey.any { it.toInt() != 0 }) {
            "PC exchange public key is invalid"
        }
        if (closed.get()) return false
        return synchronized(materialLock) {
            if (closed.get()) return@synchronized false
            when {
                keyEpoch < currentMaterial.epoch -> error("key epoch must not regress")
                keyEpoch == currentMaterial.epoch -> {
                    require(currentMaterial.exchangePublicKey.contentEquals(pcExchangePublicKey)) {
                        "PC exchange key changed without a key epoch update"
                    }
                    false
                }
                else -> {
                    require(currentMaterial.exchangePublicKey.contentEquals(pcExchangePublicKey)) {
                        "PC exchange key changed without a new device identity"
                    }
                    true
                }
            }
        }
    }

    /** Applies only authoritative, monotonic REST key-epoch updates. */
    suspend fun rotateKeyMaterial(
        keyEpoch: Long,
        pcExchangePublicKey: ByteArray,
    ): Boolean {
        require(keyEpoch > 0) { "keyEpoch must be positive" }
        require(pcExchangePublicKey.size == 32 && pcExchangePublicKey.any { it.toInt() != 0 }) {
            "PC exchange public key is invalid"
        }
        val newKeyId = "epoch-${keyEpoch.toString().padStart(8, '0')}".encodeToByteArray()
        var replacement: SessionKeyMaterial? = null
        try {
            incomingProcessingMutex.withLock {
                sendMutex.withLock {
                    val current = synchronized(materialLock) { currentMaterial }
                    if (keyEpoch == current.epoch) {
                        require(current.exchangePublicKey.contentEquals(pcExchangePublicKey)) {
                            "PC exchange key changed without a key epoch update"
                        }
                        return false
                    }
                    require(keyEpoch > current.epoch) { "key epoch must not regress" }
                    require(current.exchangePublicKey.contentEquals(pcExchangePublicKey)) {
                        "PC exchange key changed without a new device identity"
                    }
                    replacement = SessionKeyMaterial(
                        epoch = keyEpoch,
                        exchangePublicKey = pcExchangePublicKey.copyOf(),
                        keyId = newKeyId.copyOf(),
                        pairwiseKey = SessionCrypto.derivePairwiseKey(
                            privateKey = identity.exchange.privateKey,
                            peerPublicKey = pcExchangePublicKey,
                            pcDeviceId = config.pcDeviceId,
                            androidDeviceId = config.androidDeviceId,
                            keyEpoch = keyEpoch,
                        ),
                    )
                    val retirement = synchronized(materialLock) {
                        check(!closed.get()) { "production session client is closed" }
                        previousMaterial?.destroy()
                        previousMaterial = currentMaterial
                        currentMaterial = requireNotNull(replacement)
                        replacement = null
                        previousMaterialGeneration++
                        requireNotNull(previousMaterial) to previousMaterialGeneration
                    }
                    previousMaterialRetirementJob?.cancel()
                    previousMaterialRetirementJob = scope.launch {
                        delay(previousEpochGraceMillis)
                        synchronized(materialLock) {
                            if (previousMaterial === retirement.first &&
                                previousMaterialGeneration == retirement.second
                            ) {
                                previousMaterial?.destroy()
                                previousMaterial = null
                                previousMaterialGeneration++
                            }
                        }
                    }
                }
                if (mutableTransportState.value is ProductionTransportState.Connected) {
                    resume(session.state.value.lastAckedSequence)
                }
            }
            return true
        } finally {
            replacement?.destroy()
            newKeyId.fill(0)
        }
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
        sendAcknowledgement(highestContiguousSequence)
    }

    override suspend fun persistAcknowledgement(highestContiguousSequence: Long): Long =
        acknowledgementObserver(highestContiguousSequence)

    override suspend fun resetAcknowledgement() = acknowledgementResetObserver()

    private suspend fun sendAcknowledgement(highestContiguousSequence: Long) {
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
        val pending = CompletableDeferred<Unit>()
        try {
            check(pendingInputs.putIfAbsent(input.inputId, pending) == null) { "input ID is already pending" }
            sendEncrypted(MessageType.INPUT_EVENT, plaintext)
            withTimeout(inputAcceptanceTimeoutMillis) { pending.await() }
        } catch (_: TimeoutCancellationException) {
            failTransport("input acceptance timed out")
            throw RemoteInputDeliveryUnknownException("input acceptance timed out")
        } catch (error: RemoteInputRejectedException) {
            throw error
        } catch (error: RemoteInputDeliveryUnknownException) {
            failTransport(error.message ?: "input delivery is unknown")
            throw error
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            val reason = error.message ?: "input transport failed"
            failTransport(reason)
            throw RemoteInputDeliveryUnknownException(reason)
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
        previousMaterialRetirementJob?.cancel()
        pendingInputs.values.forEach {
            it.completeExceptionally(RemoteInputDeliveryUnknownException("production session closed"))
        }
        pendingInputs.clear()
        transport.close()
        api.close()
        incomingJob.cancel()
        transportStateJob.cancel()
        scope.cancel()
        synchronized(materialLock) {
            currentMaterial.destroy()
            previousMaterial?.destroy()
            previousMaterial = null
            previousMaterialGeneration++
            noncePrefix.fill(0)
        }
        if (incomingProcessingMutex.tryLock()) {
            try {
                session.close()
            } finally {
                incomingProcessingMutex.unlock()
                cleanupScope.cancel()
            }
        } else {
            cleanupScope.launch {
                try {
                    incomingProcessingMutex.withLock { session.close() }
                } finally {
                    cleanupScope.cancel()
                }
            }
        }
        mutableTransportState.value = ProductionTransportState.Closed
    }

    private suspend fun sendEncrypted(
        messageType: MessageType,
        plaintext: ByteArray,
        acknowledgement: Long = session.state.value.lastAckedSequence,
    ) {
        try {
            sendMutex.withLock {
                check(!closed.get()) { "production session client is closed" }
                val sequence = envelopeSequenceProvider.next()
                require(sequence > 0) { "envelope sequence provider returned a non-positive value" }
                val (nonce, key, keyIdSnapshot) = synchronized(materialLock) {
                    val snapshot = currentMaterial.keyId.copyOf()
                    Triple(
                        SessionCrypto.nonce(noncePrefix, sequence.toULong()),
                        SessionCrypto.deriveSessionKey(
                            currentMaterial.pairwiseKey,
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
                    if (!transport.send(envelope)) {
                        throw ProductionTransportException("realtime socket is not connected")
                    }
                } finally {
                    key.fill(0)
                    nonce.fill(0)
                    keyIdSnapshot.fill(0)
                }
            }
        } finally {
            plaintext.fill(0)
        }
    }

    private suspend fun handleIncoming(envelope: Envelope) {
        check(!closed.get()) { "production session client is closed" }
        validateEnvelope(envelope)
        val incomingKeyId = envelope.keyId.toByteArray()
        val (key, usedCurrentMaterial) = try {
            synchronized(materialLock) {
                val material = when {
                    currentMaterial.keyId.contentEquals(incomingKeyId) -> currentMaterial
                    previousMaterial?.keyId?.contentEquals(incomingKeyId) == true -> previousMaterial
                    else -> null
                } ?: throw UnknownKeyMaterialException("Envelope key ID mismatch")
                SessionCrypto.deriveSessionKey(
                    material.pairwiseKey,
                    config.sessionId,
                    CryptoDirection.PC_TO_ANDROID,
                    incomingKeyId,
                ) to (material === currentMaterial)
            }
        } finally {
            incomingKeyId.fill(0)
        }
        val plaintext = try {
            ProductionRealtimeClient.decryptEnvelope(envelope, key)
        } finally {
            key.fill(0)
        }
        if (usedCurrentMaterial) {
            // WebSocket delivery is ordered. Once an authenticated frame for
            // the new epoch arrives, all legitimate in-flight frames from the
            // previous epoch have already been observed and the old material
            // must no longer remain usable.
            synchronized(materialLock) {
                previousMaterialRetirementJob?.cancel()
                previousMaterialRetirementJob = null
                previousMaterial?.destroy()
                previousMaterial = null
                previousMaterialGeneration++
            }
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
                    requireApplied(acceptance)
                }
                MessageType.SESSION_UPSERT -> {
                    val payload = SessionUpsertPayload.parseFrom(plaintext)
                    val lifecycle = payload.lifecycle.uppercase()
                    when (lifecycle) {
                        "RUNNING", "STARTED", "CHANGED" -> requireApplied(
                            session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.RUNNING)),
                        )
                        "INTERRUPTED" -> requireApplied(
                            session.accept(RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.INTERRUPTED)),
                        )
                        "EXITED", "ENDED" -> requireApplied(
                            session.accept(
                                RemoteSessionEvent.Lifecycle(
                                    envelope.seq,
                                    RemoteLifecycle.ENDED,
                                    payload.exitCode.takeIf { payload.hasExitCode() },
                                ),
                            ),
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
                MessageType.SESSION_ENDED -> {
                    val payload = SessionUpsertPayload.parseFrom(plaintext)
                    requireApplied(
                        session.accept(
                            RemoteSessionEvent.Lifecycle(
                                envelope.seq,
                                RemoteLifecycle.ENDED,
                                payload.exitCode.takeIf { payload.hasExitCode() },
                            ),
                        ),
                    )
                }
                MessageType.INPUT_ACCEPTED, MessageType.INPUT_REJECTED -> {
                    val result = InputResultPayload.parseFrom(plaintext)
                    val inputId = ProductionRealtimeClient.uuidFromBytes(result.inputId.toByteArray())
                        ?: error("InputResult input ID must be 16 bytes")
                    require(
                        (envelope.messageType == MessageType.INPUT_ACCEPTED) == result.accepted,
                    ) { "InputResult type and accepted flag disagree" }
                    val pending = pendingInputs.remove(inputId)
                    try {
                        val accepted = requireApplied(
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
                            if (!accepted) {
                                pending.completeExceptionally(
                                    RemoteInputDeliveryUnknownException("history gap prevents input confirmation"),
                                )
                            } else if (envelope.messageType == MessageType.INPUT_ACCEPTED && result.accepted) {
                                pending.complete(Unit)
                            } else {
                                pending.completeExceptionally(
                                    RemoteInputRejectedException(result.reasonCode.ifBlank { "input rejected" }),
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
                    controlArbitrationMutex.withLock {
                        val acceptance = session.accept(RemoteSessionEvent.ControlGranted(envelope.seq))
                        requireApplied(acceptance)
                        if (acceptance is EventAcceptance.Applied) {
                            session.grantControl(leaseId, payload.expiresAtMs, payload.version)
                        }
                    }
                }
                MessageType.LEASE_DENIED, MessageType.LEASE_REVOKED -> {
                    val payload = LeasePayload.parseFrom(plaintext)
                    controlArbitrationMutex.withLock {
                        val acceptance = session.accept(
                            RemoteSessionEvent.ControlRevoked(
                                sequence = envelope.seq,
                                reason = payload.reasonCode.ifBlank { "control lease denied" },
                            ),
                        )
                        requireApplied(acceptance)
                        if (acceptance is EventAcceptance.Applied) {
                            controlGeneration++
                            if (payload.localInputEpoch > 0) {
                                observedLocalInputEpoch.accumulateAndGet(payload.localInputEpoch, ::maxOf)
                            }
                        }
                    }
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
        require(envelope.keyId.size() in 1..128) { "Envelope key ID is invalid" }
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
                    failTransport("initial session subscribe failed")
                } else if (session.state.value !is RemoteSessionState.HistoryGap) {
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
        if (closed.get() || historyRecoveryInProgress.get() ||
            session.state.value is RemoteSessionState.HistoryGap
        ) return
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = scope.launch {
            while (true) {
                delay(subscriberRefreshIntervalMillis)
                if (session.state.value is RemoteSessionState.HistoryGap ||
                    session.state.value is RemoteSessionState.Exited
                ) break
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
        pendingInputs.values.forEach { it.completeExceptionally(RemoteInputDeliveryUnknownException(reason)) }
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

    /** Returns true only when post-accept side effects may be committed. */
    private fun requireApplied(acceptance: EventAcceptance): Boolean {
        require(
            acceptance is EventAcceptance.Applied ||
                acceptance is EventAcceptance.Duplicate ||
                acceptance is EventAcceptance.HistoryGap ||
                acceptance is EventAcceptance.IgnoredAfterExit,
        ) { "production event was not contiguous: $acceptance" }
        if (acceptance is EventAcceptance.HistoryGap) {
            subscriberRefreshJob?.cancel()
            subscriberRefreshJob = null
        }
        return acceptance is EventAcceptance.Applied || acceptance is EventAcceptance.Duplicate
    }

    private fun leaseIdBytes(value: String): ByteArray =
        runCatching { ProductionRealtimeClient.uuidBytes(UUID.fromString(value)) }
            .getOrElse { value.encodeToByteArray().also { require(it.isNotEmpty() && it.size <= 128) } }

    private fun decodeLeaseId(bytes: ByteArray): String {
        require(bytes.isNotEmpty() && bytes.size <= 128) { "lease ID is invalid" }
        return ProductionRealtimeClient.uuidFromBytes(bytes)?.toString() ?: bytes.decodeToString()
    }

    private class ProductionTransportException(message: String) : IllegalStateException(message)
    private class UnknownKeyMaterialException(message: String) : IllegalStateException(message)

    private class SessionKeyMaterial(
        val epoch: Long,
        val exchangePublicKey: ByteArray,
        val keyId: ByteArray,
        val pairwiseKey: ByteArray,
    ) {
        fun destroy() {
            exchangePublicKey.fill(0)
            keyId.fill(0)
            pairwiseKey.fill(0)
        }
    }
}

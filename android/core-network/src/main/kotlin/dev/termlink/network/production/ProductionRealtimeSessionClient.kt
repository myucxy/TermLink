package dev.termlink.network.production

import android.util.Log
import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.FlowControlPayload
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.HostHistoryControlActionV1
import dev.termlink.protocol.realtime.v1.HostHistoryControlPayload
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryRequestKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryRequestPayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponsePayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponseStatusV1
import dev.termlink.protocol.realtime.v1.InputEventPayload
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.LeasePayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputAckPayload
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.ResizePayload
import dev.termlink.protocol.realtime.v1.SessionTerminateRequestPayload
import dev.termlink.protocol.realtime.v1.SessionTerminateResponsePayload
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.protocol.realtime.v1.TerminalInputKindV1
import dev.termlink.protocol.realtime.v1.TerminalInputModifiersV1
import dev.termlink.protocol.realtime.v1.TerminalInputV1
import dev.termlink.protocol.realtime.v1.TerminalKeyV1
import dev.termlink.session.EventAcceptance
import dev.termlink.session.DurableAckCommit
import dev.termlink.session.RemoteControlLease
import dev.termlink.session.RemoteControlLeaseLostException
import dev.termlink.session.RemoteInput
import dev.termlink.session.RemoteInputDeliveryUnknownException
import dev.termlink.session.RemoteInputRejectedException
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RendererBootstrapState
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionOutbound
import dev.termlink.session.RemoteSessionOrderedEventObserver
import dev.termlink.session.RemoteSessionState
import dev.termlink.session.RemoteTerminalSession
import dev.termlink.terminal.api.TerminalEngineFailureDiagnostics
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalInput
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.VtTerminalInputEncoder
import java.io.Closeable
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.util.LinkedHashMap
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withContext
import kotlinx.coroutines.yield
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.JsonNull
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
    /** Emergency compatibility rollback; legacy input bytes remain valid. */
    val typedInputEnabled: Boolean = true,
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

/**
 * Optional notification issued only after the renderer cursor is durably
 * committed. Direct uses it to release ingress digest/byte state; failures are
 * peer-local and must never suppress the authoritative Relay OUTPUT_ACK.
 */
fun interface ProductionDirectAppliedCursorObserver {
    fun onDurablyApplied(sessionId: UUID, highestContiguousSequence: Long)
}

/**
 * One already-consumed Envelope sequence held for a cutover proposal.
 * Publishing or cancelling only opens the send gate: either way the next
 * Envelope consumes this sequence, so AEAD nonces are never skipped or reused.
 */
internal class ProductionDirectSequenceReservation internal constructor(
    val sequence: Long,
) {
    private val released = AtomicBoolean(false)
    private val sendGate = CompletableDeferred<Unit>()

    fun releaseForSending() {
        if (released.compareAndSet(false, true)) sendGate.complete(Unit)
    }

    internal suspend fun awaitReleased() = sendGate.await()
}

private class InMemoryInputSequenceProvider : InputSequenceProvider {
    private val sequence = AtomicLong(0)

    override fun next(): Long = sequence.incrementAndGet()
}

private const val TERMINATION_REASON_USER_REQUESTED = "USER_REQUESTED"
private const val HOST_HISTORY_RESPONSE_CHUNK_BYTES = 512 * 1024
private const val MAX_HOST_HISTORY_OBJECT_BYTES = 64 * 1024 * 1024
private const val HOST_HISTORY_REQUEST_TIMEOUT_MILLIS = 30_000L
private const val MAX_SEMANTIC_INPUT_OUTBOX_ENTRIES = 128
private const val MAX_SEMANTIC_INPUT_OUTBOX_BYTES = 8 * 1024 * 1024

/**
 * The identity/fence that makes one semantic input safe to replay. A transport
 * generation is deliberately not part of this fence: a temporary Relay
 * reconnect must not discard an input whose application result is still
 * unknown. Key and control-owner changes are hard fences and clear the entry.
 */
internal data class SemanticInputFence(
    val sessionId: UUID,
    val peerDeviceId: UUID,
    val keyEpoch: Long,
    val controlGeneration: Long,
) {
    init {
        require(keyEpoch > 0) { "semantic input key epoch must be positive" }
        require(controlGeneration >= 0) { "semantic input control generation must not be negative" }
    }
}

internal class SemanticInputOutboxOverflowException(message: String) : IllegalStateException(message)

private class SemanticInputFenceChangedException(message: String) : IllegalStateException(message)

/**
 * Bounded, zeroing storage for semantic INPUT_EVENT envelopes.
 *
 * The entry is removed only after the matching application result has been
 * accepted by the ordered session. Transport success, reconnects, and carrier
 * receipts do not mutate this store. Callers receive copies for replay so the
 * stored bytes remain available until INPUT_ACCEPTED/INPUT_REJECTED or a hard
 * lifecycle/key/owner fence retires them.
 */
internal class SemanticInputOutbox(
    private val maxEntries: Int = MAX_SEMANTIC_INPUT_OUTBOX_ENTRIES,
    private val maxBytes: Int = MAX_SEMANTIC_INPUT_OUTBOX_BYTES,
) : Closeable {
    private data class Entry(
        val inputId: UUID,
        val fence: SemanticInputFence,
        val envelopeSequence: Long,
        val transportGeneration: Int,
        val envelopeBytes: ByteArray,
    ) {
        fun destroy() = envelopeBytes.fill(0)
    }

    private val lock = Any()
    private val entries = LinkedHashMap<UUID, Entry>()
    private var storedBytes = 0

    init {
        require(maxEntries > 0) { "semantic input outbox entry limit must be positive" }
        require(maxBytes > 0) { "semantic input outbox byte limit must be positive" }
    }

    internal val size: Int
        get() = synchronized(lock) { entries.size }

    internal val byteCount: Int
        get() = synchronized(lock) { storedBytes }

    /** Stores the first serialized Envelope bytes; never replaces an existing input ID. */
    internal fun put(
        inputId: UUID,
        fence: SemanticInputFence,
        envelopeSequence: Long,
        transportGeneration: Int,
        exactEnvelopeBytes: ByteArray,
    ) {
        require(envelopeSequence > 0) { "semantic input envelope sequence must be positive" }
        require(transportGeneration >= 0) { "semantic input transport generation must not be negative" }
        require(exactEnvelopeBytes.isNotEmpty()) { "semantic input envelope must not be empty" }
        synchronized(lock) {
            check(!entries.containsKey(inputId)) {
                "semantic input ID is already present in the outbox"
            }
            if (entries.size >= maxEntries) {
                throw SemanticInputOutboxOverflowException(
                    "semantic input outbox entry limit reached",
                )
            }
            if (exactEnvelopeBytes.size > maxBytes - storedBytes) {
                throw SemanticInputOutboxOverflowException(
                    "semantic input outbox byte limit reached",
                )
            }
            val stored = exactEnvelopeBytes.copyOf()
            entries[inputId] = Entry(
                inputId = inputId,
                fence = fence,
                envelopeSequence = envelopeSequence,
                transportGeneration = transportGeneration,
                envelopeBytes = stored,
            )
            storedBytes += stored.size
        }
    }

    /** Returns an exact copy only when the session, peer, key, and owner fence still match. */
    internal fun copyForRelay(inputId: UUID, currentFence: SemanticInputFence): ByteArray? =
        synchronized(lock) {
            val entry = entries[inputId] ?: return@synchronized null
            if (entry.fence != currentFence) return@synchronized null
            entry.envelopeBytes.copyOf()
        }

    /** Returns stable replay identities; transport generations never own them. */
    internal fun pendingInputIds(currentFence: SemanticInputFence): List<UUID> =
        synchronized(lock) {
            entries.values.asSequence()
                .filter { it.fence == currentFence }
                .map(Entry::inputId)
                .toList()
        }

    /** Retires an entry only for the matching session/peer/key/owner fence. */
    internal fun complete(inputId: UUID, currentFence: SemanticInputFence): Boolean =
        synchronized(lock) {
            val entry = entries[inputId] ?: return@synchronized false
            if (entry.fence != currentFence) return@synchronized false
            entries.remove(inputId)
            storedBytes -= entry.envelopeBytes.size
            entry.destroy()
            true
        }

    /** Clears all entries and overwrites every retained Envelope byte. */
    internal fun clear() {
        synchronized(lock) {
            entries.values.forEach(Entry::destroy)
            entries.clear()
            storedBytes = 0
        }
    }

    override fun close() = clear()
}

/** Carrier-neutral semantic replay surface over the first serialized bytes. */
internal interface SemanticInputRelayReplay {
    fun copyPendingInputEnvelopeForRelay(inputId: UUID): ByteArray?

    suspend fun resendPendingInputOverRelay(
        inputId: UUID,
        expectedConnectedGeneration: Int? = null,
    ): Boolean
}

data class ProductionHostHistoryObject(
    val kind: HostHistoryObjectKindV1,
    val objectId: UUID,
    val snapshotSequence: ULong,
    val columns: Int,
    val rows: Int,
    val initialTranscriptBeforeLineId: ULong,
    val requestBeforeLineId: ULong,
    val firstLineId: ULong,
    val lastLineId: ULong,
    val hasMoreBefore: Boolean,
    val nextBeforeLineId: ULong,
    val startSequence: ULong,
    val endSequence: ULong,
    val hasMoreEvents: Boolean,
    val keyId: String,
    val formatName: String,
    val formatVersion: Int,
    val ciphertextSha256: ByteArray,
    val sealedObject: ByteArray,
) {
    fun destroy() {
        ciphertextSha256.fill(0)
        sealedObject.fill(0)
    }
}

class ProductionHostHistoryGapException(message: String) : IllegalStateException(message)
class ProductionHostHistoryNotReadyException(message: String) : IllegalStateException(message)

data class ProductionRealtimeSessionDiagnostics(
    val transportState: String,
    val remoteState: String,
    val transportCollectorActive: Boolean,
    val reconnectActive: Boolean,
    val subscriberReady: Boolean,
    val reconnectAttempt: Int,
    val subscribeSendCount: Long,
    val lastSubscribeCursor: Long,
    val flowControlSendCount: Long,
    val lastReceivedSequence: Long,
    val lastHandledSequence: Long,
    /** Highest sequence accepted by the authoritative session reducer. */
    val contiguousAppliedSequence: Long = 0,
    /** Number of duplicate ordered events rejected by the reducer. */
    val duplicateEventCount: Long = 0,
    /** Number of ordered events rejected after a sequence discontinuity. */
    val historyGapCount: Long = 0,
    /** Highest durable acknowledgement currently exposed by the reducer. */
    val durableAck: Long = 0,
    val multiplexer: ProductionRealtimeMultiplexerDiagnostics,
)

/** Non-sensitive identity of one failed session-processing incarnation. */
data class ProductionRendererFatalFailure(
    val sessionId: UUID,
    val sequence: Long,
    val transportGeneration: Int,
    val messageType: MessageType,
    val failureStage: String,
    val exceptionClass: String,
    val terminalOperation: String?,
    val terminalStatusName: String?,
    val terminalStatusCode: Int?,
)

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
    private val transport: ProductionRealtimeSessionTransport,
    private val httpClient: OkHttpClient = ProductionRealtimeClient.defaultHttpClient,
    allowCleartextForLocalTest: Boolean = false,
    private val reconnectDelaysMillis: List<Long> = listOf(250, 500, 1_000, 2_000, 4_000, 8_000, 16_000, 30_000),
    private val reconnectStabilityMillis: Long = 10_000,
    private val inputAcceptanceTimeoutMillis: Long = 5_000,
    private val inputSequenceProvider: InputSequenceProvider = InMemoryInputSequenceProvider(),
    private val envelopeSequenceProvider: InputSequenceProvider = InMemoryInputSequenceProvider(),
    initialRendererState: RendererBootstrapState = RendererBootstrapState.Empty(),
    initialObservedLocalInputEpoch: Long = 0,
    private val acknowledgementObserver: suspend (Long, Long) -> DurableAckCommit,
    private val acknowledgementResetObserver: suspend (Long) -> DurableAckCommit,
    private val directAppliedCursorObserver: ProductionDirectAppliedCursorObserver =
        ProductionDirectAppliedCursorObserver { _, _ -> },
    private val authoritativeMetadataRefreshObserver: suspend () -> Unit = {},
    private val metadataRefreshCooldownMillis: Long = 10_000,
    private val previousEpochGraceMillis: Long = 30_000,
    private val subscriberRefreshIntervalMillis: Long = 15_000,
    private val terminationReadinessTimeoutMillis: Long = 15_000,
    private val hostHistoryRequestTimeoutMillis: Long = HOST_HISTORY_REQUEST_TIMEOUT_MILLIS,
    private val holdLiveReplayForHostHistory: Boolean = false,
    private val hostHistoryOnlyMode: Boolean = false,
    leaseRenewalIntervalMillis: Long = 10_000,
    disposeEngineOnClose: Boolean = true,
    orderedEventObserver: RemoteSessionOrderedEventObserver? = null,
    orderedEventObserverCapacity: Int = 64,
    private val rendererFatalObserver: (ProductionRendererFatalFailure) -> Unit = {},
    private val sessionDispatcher: CoroutineDispatcher = Dispatchers.Default.limitedParallelism(1),
    private val controlDispatcher: CoroutineDispatcher = Dispatchers.Default.limitedParallelism(1),
) : RemoteSessionOutbound, Closeable, SemanticInputRelayReplay {
    private val scopeJob = SupervisorJob(parentScope.coroutineContext[Job])
    // JNI parsing/rendering and long replay are serialized per session away
    // from Main. NativeTerminalEngine posts view invalidation back to Main.
    private val sessionScope = CoroutineScope(
        parentScope.coroutineContext + scopeJob + sessionDispatcher,
    )
    // Connection and flow-control work gets an independent serial lane so a
    // long native replay cannot starve socket recovery or close propagation.
    private val controlScope = CoroutineScope(
        parentScope.coroutineContext + scopeJob + controlDispatcher,
    )
    private val cleanupScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val closed = AtomicBoolean(false)
    private val closing = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val metadataRefreshInFlight = AtomicBoolean(false)
    private val historyRecoveryInProgress = AtomicBoolean(false)
    private val rendererFatal = AtomicBoolean(false)
    private val nextMetadataRefreshAtMillis = AtomicLong(0)
    private val sendMutex = Mutex()
    private val directSequenceReservationLock = Any()
    private var directSequenceReservation: ProductionDirectSequenceReservation? = null
    private val incomingProcessingMutex = Mutex()
    private val controlRequestMutex = Mutex()
    private val controlArbitrationMutex = Mutex()
    private val materialLock = Any()
    private val observedLocalInputEpoch = AtomicLong(initialObservedLocalInputEpoch)
    private val pendingInputs = ConcurrentHashMap<UUID, CompletableDeferred<Unit>>()
    private val semanticInputOutbox = SemanticInputOutbox()
    private val pendingTerminations = ConcurrentHashMap<UUID, CompletableDeferred<Boolean>>()
    private val pendingHostHistory = ConcurrentHashMap<UUID, HostHistoryAccumulator>()
    private val reconnectMutex = Mutex()
    private var reconnectJob: Job? = null
    private var reconnectStabilityJob: Job? = null
    private val sessionReconcileLock = Any()
    private val sessionReconcileEpoch = AtomicLong(0)
    private var sessionReconcileJob: Job? = null
    private var sessionReconcileProjectionJob: Job? = null
    private var sessionReconcileConnectedGeneration = 0
    private var subscriberRefreshJob: Job? = null
    private var previousMaterialRetirementJob: Job? = null
    private var reconnectAttempt = 0
    @Volatile
    private var controlGeneration = 0L
    private var restoreControlAfterReconnect = false
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
    private val api = ProductionApiClient(
        rootUrl = config.rootUrl,
        tokenProvider = config.tokenProvider,
        deviceId = config.androidDeviceId,
        identity = identity,
        httpClient = httpClient,
        allowCleartextForLocalTest = allowCleartextForLocalTest,
    )
    private val incomingJob: Job
    private var transportStateJob: Job? = null
    private val receivePressureJob: Job
    private lateinit var session: RemoteTerminalSession
    /** Zero means not subscribed; a positive value is the exact socket generation. */
    private val subscriberReady = MutableStateFlow(0)
    private val bootstrapFlowHold = AtomicBoolean(true)
    private val semanticFlowHold = AtomicBoolean(holdLiveReplayForHostHistory)
    private val flowControlEpoch = AtomicLong(0)
    private val subscribeSendCount = AtomicLong(0)
    private val lastSubscribeCursor = AtomicLong(0)
    private val flowControlSendCount = AtomicLong(0)
    private val lastReceivedSequence = AtomicLong(0)
    private val lastHandledSequence = AtomicLong(0)
    private val contiguousAppliedSequence = AtomicLong(0)
    private val duplicateEventCount = AtomicLong(0)
    private val historyGapCount = AtomicLong(0)
    private val rejectedThroughGeneration = AtomicInteger(0)
    private val transportTransitionGeneration = AtomicLong(0)
    /** Guarded by [incomingProcessingMutex]. */
    private var activeIncomingGeneration = 0
    private val subscriptionBootstrapMutex = Mutex()
    private val flowControlMutex = Mutex()
    private var lastFlowControlGeneration = Int.MIN_VALUE
    private var lastFlowControlPaused: Boolean? = null

    private sealed interface HostHistoryRequestResult {
        data class Ready(val value: ProductionHostHistoryObject) : HostHistoryRequestResult
        data object Missing : HostHistoryRequestResult
    }

    private data class HostHistoryChunkAcceptance(
        val objectId: ByteString,
        val chunkIndex: Int,
        val completed: ProductionHostHistoryObject?,
    )

    private class HostHistoryAccumulator {
        val results = Channel<HostHistoryRequestResult>(
            capacity = 1,
            onUndeliveredElement = { result ->
                if (result is HostHistoryRequestResult.Ready) result.value.destroy()
            },
        )
        private var header: ByteArray? = null
        private var metadata: HostHistoryResponsePayload? = null
        private var assembled: ByteArray? = null
        private var received: BooleanArray? = null
        private var receivedCount = 0

        @Synchronized
        fun accept(response: HostHistoryResponsePayload): HostHistoryChunkAcceptance {
            require(response.status == HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK) {
                "Host history response is not successful"
            }
            require(response.objectId.size() == 16 && response.objectSha256.size() == 32) {
                "Host history object identity is invalid"
            }
            require(response.kind != HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_UNSPECIFIED) {
                "Host history object kind is missing"
            }
            require(response.objectBytes in 1..MAX_HOST_HISTORY_OBJECT_BYTES.toLong()) {
                "Host history object size is invalid"
            }
            require(response.chunkCount in 1..128 && response.chunkIndex < response.chunkCount) {
                "Host history chunk index is invalid"
            }
            val expectedChunkCount = (
                (response.objectBytes + HOST_HISTORY_RESPONSE_CHUNK_BYTES - 1) /
                    HOST_HISTORY_RESPONSE_CHUNK_BYTES
                ).toInt()
            require(response.chunkCount == expectedChunkCount) {
                "Host history chunk count does not match object length"
            }
            val offset = response.chunkIndex * HOST_HISTORY_RESPONSE_CHUNK_BYTES
            val expectedChunkBytes = minOf(
                HOST_HISTORY_RESPONSE_CHUNK_BYTES.toLong(),
                response.objectBytes - offset.toLong(),
            ).toInt()
            require(response.objectChunk.size() == expectedChunkBytes) {
                "Host history chunk size is invalid"
            }
            val normalizedMetadata = response.toBuilder()
                .setChunkIndex(0)
                .clearObjectChunk()
                .build()
            val normalizedHeader = normalizedMetadata.toByteArray()
            val existingHeader = header
            if (existingHeader == null) {
                header = normalizedHeader
                metadata = normalizedMetadata
                assembled = ByteArray(response.objectBytes.toInt())
                received = BooleanArray(response.chunkCount)
            } else {
                try {
                    require(MessageDigest.isEqual(existingHeader, normalizedHeader)) {
                        "Host history chunk metadata changed"
                    }
                } finally {
                    normalizedHeader.fill(0)
                }
            }
            val target = requireNotNull(assembled)
            val admitted = requireNotNull(received)
            val index = response.chunkIndex
            if (admitted[index]) {
                var difference = 0
                for (chunkOffset in 0 until expectedChunkBytes) {
                    difference = difference or (
                        target[offset + chunkOffset].toInt() xor
                            response.objectChunk.byteAt(chunkOffset).toInt()
                        )
                }
                require(difference == 0) { "Host history chunk was replayed with different bytes" }
            } else {
                response.objectChunk.asReadOnlyByteBuffer().get(target, offset, expectedChunkBytes)
                admitted[index] = true
                receivedCount++
            }
            if (receivedCount != admitted.size) {
                return HostHistoryChunkAcceptance(response.objectId, index, null)
            }
            val digest = MessageDigest.getInstance("SHA-256").digest(target)
            val expectedDigest = response.objectSha256.toByteArray()
            try {
                require(MessageDigest.isEqual(digest, expectedDigest)) {
                    "Host history object digest mismatch"
                }
            } catch (error: Throwable) {
                target.fill(0)
                throw error
            } finally {
                digest.fill(0)
                expectedDigest.fill(0)
            }
            val value = requireNotNull(metadata)
            val objectId = ProductionRealtimeClient.uuidFromBytes(value.objectId.toByteArray())
                ?: error("Host history object ID is invalid")
            val completed = ProductionHostHistoryObject(
                kind = value.kind,
                objectId = objectId,
                snapshotSequence = value.snapshotSeq.toULong(),
                columns = value.columns,
                rows = value.rows,
                initialTranscriptBeforeLineId = value.initialTranscriptBeforeLineId.toULong(),
                requestBeforeLineId = value.requestBeforeLineId.toULong(),
                firstLineId = value.firstLineId.toULong(),
                lastLineId = value.lastLineId.toULong(),
                hasMoreBefore = value.hasMoreBefore,
                nextBeforeLineId = value.nextBeforeLineId.toULong(),
                startSequence = value.startSeq.toULong(),
                endSequence = value.endSeq.toULong(),
                hasMoreEvents = value.hasMoreEvents,
                keyId = value.keyId,
                formatName = value.formatName,
                formatVersion = value.formatVersion,
                ciphertextSha256 = value.objectSha256.toByteArray(),
                sealedObject = target,
            )
            assembled = null
            clearAssembly()
            return HostHistoryChunkAcceptance(response.objectId, index, completed)
        }

        @Synchronized
        fun deliver(result: HostHistoryRequestResult) {
            if (results.trySend(result).isFailure && result is HostHistoryRequestResult.Ready) {
                result.value.destroy()
            }
        }

        @Synchronized
        fun fail(error: Throwable) {
            clearAssembly()
            results.close(error)
        }

        @Synchronized
        fun destroy() {
            clearAssembly()
            results.cancel()
        }

        private fun clearAssembly() {
            header?.fill(0)
            header = null
            assembled?.fill(0)
            assembled = null
            received = null
            receivedCount = 0
            metadata = null
        }
    }

    init {
        require(reconnectDelaysMillis.isNotEmpty() && reconnectDelaysMillis.all { it in 0..30_000 }) {
            "reconnect delays must be non-empty and capped at 30 seconds"
        }
        require(reconnectStabilityMillis in 0..60_000) {
            "reconnect stability window must be 0..60000 milliseconds"
        }
        require(inputAcceptanceTimeoutMillis > 0) { "input acceptance timeout must be positive" }
        require(initialObservedLocalInputEpoch >= 0) { "initialObservedLocalInputEpoch must not be negative" }
        require(metadataRefreshCooldownMillis > 0) { "metadata refresh cooldown must be positive" }
        require(previousEpochGraceMillis in 1..60_000) { "previous epoch grace must be 1..60000 milliseconds" }
        require(subscriberRefreshIntervalMillis > 0) { "subscriber refresh interval must be positive" }
        require(terminationReadinessTimeoutMillis > 0) { "termination readiness timeout must be positive" }
        require(hostHistoryRequestTimeoutMillis > 0) { "host history request timeout must be positive" }
        require(!hostHistoryOnlyMode || holdLiveReplayForHostHistory) {
            "host history-only mode requires a permanent zero replay window"
        }
        if (hostHistoryOnlyMode) engine.setReadOnly(true)
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = this,
            parentScope = sessionScope,
            initialRendererState = initialRendererState,
            leaseRenewalIntervalMillis = leaseRenewalIntervalMillis,
            disposeEngineOnClose = disposeEngineOnClose,
            orderedEventObserver = orderedEventObserver,
            orderedEventObserverCapacity = orderedEventObserverCapacity,
        )
        this.session = session
        incomingJob = sessionScope.launch {
            transport.receivedEnvelopes.collect { received ->
                try {
                    if (rendererFatal.get()) return@collect
                    val failure: IncomingFailure? = incomingProcessingMutex.withLock {
                        if (rendererFatal.get()) return@withLock null
                        val generation = received.generation
                        val envelope = received.envelope
                        val rejectedGeneration = rejectedThroughGeneration.get()
                        if (generation <= rejectedGeneration || generation < activeIncomingGeneration) {
                            Log.w(
                                REALTIME_LOG_TAG,
                                "session_generation_rejected generation=$generation " +
                                    "active_generation=$activeIncomingGeneration " +
                                    "rejected_through_generation=$rejectedGeneration " +
                                    "seq=${envelope.seq} type=${envelope.messageType.name}",
                            )
                            return@withLock null
                        }
                        if (generation > activeIncomingGeneration) activeIncomingGeneration = generation

                        val context = IncomingProcessingContext(generation, envelope)
                        try {
                            handleIncoming(context)
                            null
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Exception) {
                            rejectedThroughGeneration.accumulateAndGet(generation, ::maxOf)
                            IncomingFailure(context, error)
                        }
                    }
                    if (failure != null) handleIncomingFailure(failure)
                } finally {
                    received.close()
                }
            }
        }
        receivePressureJob = controlScope.launch {
            transport.receivePressure.collect {
                if (subscriberReady.value > 0 &&
                    mutableTransportState.value is ProductionTransportState.Connected
                ) {
                    try {
                        sendFlowControl(force = false)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        handleSessionLocalFailure(
                            error.message ?: "flow control update failed",
                            subscriberReady.value,
                        )
                    }
                }
            }
        }
    }

    val remoteSession: RemoteTerminalSession
        get() = session
    val sessionState: StateFlow<RemoteSessionState>
        get() = session.state
    /** Null until an ordered title event is observed; blank means explicitly cleared. */
    val sessionTitle: StateFlow<String?>
        get() = session.title
    val transportState: StateFlow<ProductionTransportState> = mutableTransportState.asStateFlow()

    fun diagnostics(): ProductionRealtimeSessionDiagnostics = ProductionRealtimeSessionDiagnostics(
        transportState = mutableTransportState.value.diagnosticName(),
        remoteState = session.state.value.diagnosticName(),
        transportCollectorActive = transportStateJob?.isActive == true,
        reconnectActive = reconnectJob?.isActive == true ||
            sessionReconcileJob?.isActive == true ||
            sessionReconcileProjectionJob?.isActive == true,
        subscriberReady = subscriberReady.value > 0,
        reconnectAttempt = reconnectAttempt,
        subscribeSendCount = subscribeSendCount.get(),
        lastSubscribeCursor = lastSubscribeCursor.get(),
        flowControlSendCount = flowControlSendCount.get(),
        lastReceivedSequence = lastReceivedSequence.get(),
        lastHandledSequence = lastHandledSequence.get(),
        contiguousAppliedSequence = contiguousAppliedSequence.get(),
        duplicateEventCount = duplicateEventCount.get(),
        historyGapCount = historyGapCount.get(),
        durableAck = session.state.value.lastAckedSequence,
        multiplexer = transport.diagnostics(),
    )

    fun start() {
        check(!closed.get()) { "production session client is closed" }
        check(started.compareAndSet(false, true)) { "production session client is already started" }
        // History-only endpoints never collect renderer input and never make
        // the temporary RemoteTerminalSession live. They exist solely to
        // carry encrypted Host History and termination request/response frames.
        if (!hostHistoryOnlyMode) session.start()
        transportStateJob = controlScope.launch {
            transport.connectionEvents.collect(::handleTransportState)
        }
        transport.connect()
    }

    suspend fun requestControl() = controlRequestMutex.withLock {
        check(!hostHistoryOnlyMode) { "host history-only client cannot request control" }
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
                val previous = session.state.value as? RemoteSessionState.Controlling
                if (previous == null ||
                    previous.leaseId != lease.leaseId ||
                    previous.leaseVersion != lease.version
                ) {
                    controlGeneration++
                    failPendingInputs("control owner changed")
                    semanticInputOutbox.clear()
                }
                session.grantControl(lease.leaseId, lease.expiresAtEpochMillis, lease.version)
                restoreControlAfterReconnect = true
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
            throw ControlRequestSupersededException()
        }
    }

    /**
     * Requests termination of the authoritative Windows SessionHost. This is
     * independent from both the control lease and permanent REST deletion of
     * an offline session record.
     */
    suspend fun requestTermination(awaitAcknowledgement: Boolean = false): UUID {
        check(started.get()) { "production session client is not started" }
        check(!closed.get()) { "production session client is closed" }
        try {
            withTimeout(terminationReadinessTimeoutMillis) {
                combine(mutableTransportState, subscriberReady) { transportState, readyGeneration ->
                    transportState is ProductionTransportState.Connected && readyGeneration > 0
                }.first { it }
            }
        } catch (_: TimeoutCancellationException) {
            throw ProductionTransportException("session terminate transport was not ready")
        }

        val requestId = UUID.randomUUID()
        val pending = if (awaitAcknowledgement) CompletableDeferred<Boolean>() else null
        if (pending != null) {
            check(pendingTerminations.putIfAbsent(requestId, pending) == null) {
                "Session termination request ID collision"
            }
        }
        val payload = SessionTerminateRequestPayload.newBuilder()
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
            .setReasonCode(TERMINATION_REASON_USER_REQUESTED)
            .build()
            .toByteArray()
        try {
            sendEncrypted(MessageType.SESSION_TERMINATE_REQUEST, payload)
            if (pending == null) return requestId
            val accepted = try {
                withTimeout(terminationReadinessTimeoutMillis) { pending.await() }
            } catch (_: TimeoutCancellationException) {
                throw ProductionTransportException("session termination acknowledgement timed out")
            }
            check(accepted) { "The Windows Agent rejected session termination" }
            return requestId
        } finally {
            if (pending != null) pendingTerminations.remove(requestId, pending)
        }
    }

    suspend fun requestLatestHostSnapshot(): ProductionHostHistoryObject? = requestHostHistory(
        kind = HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_LATEST_SNAPSHOT,
    )

    suspend fun requestHostTranscriptPage(
        snapshotSequence: ULong,
        beforeLineId: ULong,
    ): ProductionHostHistoryObject? {
        require(snapshotSequence in 1uL..Long.MAX_VALUE.toULong()) { "Snapshot sequence is invalid" }
        require(beforeLineId in 1uL..Long.MAX_VALUE.toULong()) { "Transcript cursor is invalid" }
        return requestHostHistory(
            kind = HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_TRANSCRIPT_PAGE,
            snapshotSequence = snapshotSequence,
            beforeLineId = beforeLineId,
        )
    }

    suspend fun requestHostEventRange(
        afterSequence: ULong,
        throughSequence: ULong,
        maxEvents: Int = 512,
    ): ProductionHostHistoryObject? {
        require(afterSequence < Long.MAX_VALUE.toULong()) { "History start is invalid" }
        require(throughSequence > afterSequence && throughSequence <= Long.MAX_VALUE.toULong()) {
            "History range is invalid"
        }
        require(maxEvents in 1..4096) { "History event limit is invalid" }
        return requestHostHistory(
            kind = HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_EVENT_RANGE,
            afterSequence = afterSequence,
            throughSequence = throughSequence,
            maxEvents = maxEvents,
        )
    }

    private suspend fun requestHostHistory(
        kind: HostHistoryRequestKindV1,
        snapshotSequence: ULong = 0u,
        beforeLineId: ULong = 0u,
        afterSequence: ULong = 0u,
        throughSequence: ULong = 0u,
        maxEvents: Int = 0,
    ): ProductionHostHistoryObject? {
        check(started.get()) { "production session client is not started" }
        check(!closed.get()) { "production session client is closed" }
        try {
            withTimeout(terminationReadinessTimeoutMillis) {
                combine(mutableTransportState, subscriberReady) { transportState, readyGeneration ->
                    transportState is ProductionTransportState.Connected && readyGeneration > 0
                }.first { it }
            }
        } catch (_: TimeoutCancellationException) {
            throw ProductionTransportException("host history transport was not ready")
        }

        val requestId = UUID.randomUUID()
        val pending = HostHistoryAccumulator()
        check(pendingHostHistory.putIfAbsent(requestId, pending) == null) {
            "Host history request ID collision"
        }
        val payload = HostHistoryRequestPayload.newBuilder()
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
            .setKind(kind)
            .setSnapshotSeq(snapshotSequence.toLong())
            .setBeforeLineId(beforeLineId.toLong())
            .setAfterSeq(afterSequence.toLong())
            .setThroughSeq(throughSequence.toLong())
            .setMaxEvents(maxEvents)
            .build()
            .toByteArray()
        var requestSent = false
        var requestCompleted = false
        try {
            sendEncrypted(MessageType.HOST_HISTORY_REQUEST, payload)
            requestSent = true
            return try {
                val result = withTimeout(hostHistoryRequestTimeoutMillis) {
                    pending.results.receive()
                }
                requestCompleted = true
                when (result) {
                    is HostHistoryRequestResult.Ready -> result.value
                    HostHistoryRequestResult.Missing -> null
                }
            } catch (_: TimeoutCancellationException) {
                throw ProductionTransportException("host history request timed out")
            }
        } finally {
            if (pendingHostHistory.remove(requestId, pending)) {
                pending.destroy()
                if (requestSent && !requestCompleted && !closed.get()) {
                    withContext(NonCancellable) {
                        runCatching {
                            sendHostHistoryControl(
                                requestId = requestId,
                                action = HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CANCEL,
                            )
                        }
                    }
                }
            }
        }
    }

    private suspend fun sendHostHistoryControl(
        requestId: UUID,
        action: HostHistoryControlActionV1,
        objectId: ByteString = ByteString.EMPTY,
        chunkIndex: Int = 0,
    ) {
        val payload = HostHistoryControlPayload.newBuilder()
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
            .setAction(action)
            .setObjectId(objectId)
            .setChunkIndex(chunkIndex)
            .build()
            .toByteArray()
        sendEncrypted(MessageType.HOST_HISTORY_CONTROL, payload)
    }

    suspend fun releaseControl(reason: String = "control released") {
        val current = controlArbitrationMutex.withLock {
            controlGeneration++
            restoreControlAfterReconnect = false
            failPendingInputs("control lease released")
            semanticInputOutbox.clear()
            session.state.value as? RemoteSessionState.Controlling
        } ?: return
        withContext(Dispatchers.IO) {
            api.delete("/v1/sessions/${config.sessionId}/lease/${current.leaseId}")
        }
        controlArbitrationMutex.withLock {
            val active = session.state.value as? RemoteSessionState.Controlling
            if (active?.leaseId == current.leaseId) session.revokeControl(reason)
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
        // The work belongs to the client lifetime, while await still propagates
        // caller cancellation. Closing the client cancels scopeJob and cannot
        // leave a detached replay mutating a disposed renderer.
        val recovery = sessionScope.async {
            recoverFromHistoryOnSessionLane(events, recoveredLocalInputEpoch, expectedGap)
        }
        return try {
            recovery.await()
        } catch (error: Throwable) {
            recovery.cancel()
            throw error
        }
    }

    private suspend fun recoverFromHistoryOnSessionLane(
        events: List<RemoteSessionEvent>,
        recoveredLocalInputEpoch: Long,
        expectedGap: RemoteSessionState.HistoryGap,
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
                    persist = { expectedGeneration, recovered ->
                        withContext(NonCancellable) {
                            persistAcknowledgement(expectedGeneration, recovered)
                        }
                    },
                    publish = { recovered ->
                        sendAcknowledgement(recovered)
                        val connectedGeneration = transport.currentConnectedGeneration()
                        check(connectedGeneration > 0) { "realtime socket is not connected" }
                        establishSubscriptionHeldThenOpen(
                            lastAckedSequence = recovered,
                            connectedGeneration = connectedGeneration,
                            releaseHistoryHold = true,
                        )
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
                    previousMaterialRetirementJob = controlScope.launch {
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
                    // An input encrypted under the retired key cannot be
                    // safely replayed after an authoritative key fence.
                    failPendingInputs("session key material changed")
                    semanticInputOutbox.clear()
                }
                if (mutableTransportState.value is ProductionTransportState.Connected) {
                    resume(session.state.value.lastAckedSequence)
                    sendFlowControl(force = true)
                }
            }
            return true
        } finally {
            replacement?.destroy()
            newKeyId.fill(0)
        }
    }

    override suspend fun resume(lastAckedSequence: Long) {
        val connectedGeneration = transport.currentConnectedGeneration()
        check(connectedGeneration > 0) { "realtime socket is not connected" }
        resumeForGeneration(lastAckedSequence, connectedGeneration)
    }

    private suspend fun resumeForGeneration(lastAckedSequence: Long, connectedGeneration: Int) {
        require(lastAckedSequence >= 0)
        require(connectedGeneration > 0)
        val payload = SubscribePayload.newBuilder()
            .setResumeFromSeq(lastAckedSequence)
            .setSubscriberExchangePublicKey(ByteString.copyFrom(identity.exchange.publicKey))
            .build()
            .toByteArray()
        sendEncrypted(
            MessageType.SUBSCRIBE,
            payload,
            acknowledgement = lastAckedSequence,
            expectedConnectedGeneration = connectedGeneration,
        )
        lastSubscribeCursor.set(lastAckedSequence)
        subscribeSendCount.incrementAndGet()
    }

    /**
     * SUBSCRIBE installs a paused PC subscriber. Preserve that zero-window
     * invariant until Android has explicitly published its bootstrap hold,
     * then open a window that is safely below every local hard queue limit.
     */
    private suspend fun establishSubscriptionHeldThenOpen(
        lastAckedSequence: Long,
        connectedGeneration: Int,
        releaseHistoryHold: Boolean = false,
    ) = subscriptionBootstrapMutex.withLock {
        bootstrapFlowHold.set(true)
        try {
            resumeForGeneration(lastAckedSequence, connectedGeneration)
            check(transport.currentConnectedGeneration() == connectedGeneration) {
                "realtime socket generation changed during subscriber bootstrap"
            }
            subscriberReady.value = connectedGeneration
            sendFlowControl(force = true)
            check(transport.currentConnectedGeneration() == connectedGeneration) {
                "realtime socket generation changed during subscriber bootstrap"
            }
            if (releaseHistoryHold) semanticFlowHold.set(false)
            bootstrapFlowHold.set(false)
            sendFlowControl(force = true)
            check(transport.currentConnectedGeneration() == connectedGeneration) {
                "realtime socket generation changed while opening subscriber window"
            }
        } catch (error: Throwable) {
            bootstrapFlowHold.set(true)
            subscriberReady.compareAndSet(connectedGeneration, 0)
            throw error
        }
    }

    override suspend fun acknowledge(highestContiguousSequence: Long) {
        require(highestContiguousSequence >= 0)
        val connectedGeneration = transport.currentConnectedGeneration()
        try {
            sendAcknowledgement(highestContiguousSequence, connectedGeneration)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            handleSessionLocalFailure(error.message ?: "acknowledgement send failed", connectedGeneration)
            throw error
        }
    }

    override suspend fun persistAcknowledgement(
        expectedRendererGeneration: Long,
        highestContiguousSequence: Long,
    ): DurableAckCommit {
        val committed = acknowledgementObserver(expectedRendererGeneration, highestContiguousSequence)
        // This callback is deliberately after durable persistence and before
        // RemoteTerminalSession emits OUTPUT_ACK. Direct owns neither action.
        runCatching {
            directAppliedCursorObserver.onDurablyApplied(config.sessionId, committed.sequence)
        }
        return committed
    }

    /**
     * Reserves the exact next outbound Envelope sequence before a cutover
     * proposal is sent. Until [ProductionDirectSequenceReservation.releaseForSending]
     * is called, outbound sends wait without holding [sendMutex].
     */
    internal suspend fun reserveNextEnvelopeSequenceForDirectCutover():
        ProductionDirectSequenceReservation {
        while (true) {
            val existing = synchronized(directSequenceReservationLock) {
                directSequenceReservation
            }
            if (existing != null) return existing
            return sendMutex.withLock {
                synchronized(directSequenceReservationLock) {
                    directSequenceReservation?.let { return@withLock it }
                    val sequence = envelopeSequenceProvider.next()
                    require(sequence > 0) {
                        "envelope sequence provider returned a non-positive value"
                    }
                    ProductionDirectSequenceReservation(sequence).also {
                        directSequenceReservation = it
                    }
                }
            }
        }
    }

    /**
     * One-shot Relay fallback for bytes already serialized by this session.
     * It deliberately bypasses the Direct gateway; semantic retry ownership
     * remains in this client's input outbox until INPUT_ACCEPTED/REJECTED.
     */
    internal fun sendRelayExactFromDirect(exactEnvelopeBytes: ByteArray): Boolean {
        if (closed.get() || closing.get() || exactEnvelopeBytes.isEmpty()) return false
        val envelope = runCatching { Envelope.parseFrom(exactEnvelopeBytes) }.getOrNull()
            ?: return false
        if (envelope.messageType != MessageType.INPUT_EVENT ||
            envelope.direction != dev.termlink.protocol.realtime.v1.Direction.ANDROID_TO_PC ||
            !envelope.sessionId.toByteArray().contentEquals(
                ProductionRealtimeClient.uuidBytes(config.sessionId),
            ) ||
            !envelope.senderDeviceId.toByteArray().contentEquals(
                ProductionRealtimeClient.uuidBytes(config.androidDeviceId),
            ) ||
            !envelope.recipientDeviceId.toByteArray().contentEquals(
                ProductionRealtimeClient.uuidBytes(config.pcDeviceId),
            )
        ) return false
        return transport.sendRelayExact(envelope, exactEnvelopeBytes)
    }

    override suspend fun resetAcknowledgement(expectedRendererGeneration: Long): DurableAckCommit =
        acknowledgementResetObserver(expectedRendererGeneration)

    private suspend fun sendAcknowledgement(
        highestContiguousSequence: Long,
        expectedConnectedGeneration: Int? = null,
    ) {
        val payload = OutputAckPayload.newBuilder()
            .setHighestContiguousSeq(highestContiguousSequence)
            .build()
            .toByteArray()
        sendEncrypted(
            MessageType.OUTPUT_ACK,
            payload,
            acknowledgement = highestContiguousSequence,
            expectedConnectedGeneration = expectedConnectedGeneration,
        )
    }

    override suspend fun sendInput(input: RemoteInput) {
        require(input.bytes.isNotEmpty()) { "input must not be empty" }
        val typedInput = if (config.typedInputEnabled) {
            input.typedInput ?: TerminalInput.RawBytes(input.bytes)
        } else {
            null
        }
        val canonicalBytes = typedInput?.let(VtTerminalInputEncoder::encode) ?: input.bytes.copyOf()
        if (typedInput != null) {
            require(canonicalBytes.contentEquals(input.bytes)) {
                "typed input and compatibility bytes must encode identically"
            }
        }
        val controlling = session.state.value as? RemoteSessionState.Controlling
            ?: throw ProductionTransportException("control lease is required")
        val inputSeq = inputSequenceProvider.next()
        require(inputSeq > 0) { "input sequence provider returned a non-positive value" }
        val inputBuilder = InputEventPayload.newBuilder()
            .setInputId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(input.inputId)))
            .setLeaseId(ByteString.copyFrom(leaseIdBytes(controlling.leaseId)))
            .setLeaseVersion(controlling.leaseVersion)
            .setInputSeq(inputSeq)
            .setInput(ByteString.copyFrom(canonicalBytes))
            .setObservedLocalInputEpoch(observedLocalInputEpoch.get())
        if (typedInput != null) inputBuilder.setTypedInput(typedInput.toWireInput())
        val plaintext = inputBuilder.build()
            .toByteArray()
        val pending = CompletableDeferred<Unit>()
        val connectedGeneration = transport.currentConnectedGeneration()
        try {
            check(pendingInputs.putIfAbsent(input.inputId, pending) == null) { "input ID is already pending" }
            controlArbitrationMutex.withLock {
                val current = session.state.value as? RemoteSessionState.Controlling
                    ?: throw SemanticInputFenceChangedException("control owner is no longer active")
                if (current.leaseId != controlling.leaseId ||
                    current.leaseVersion != controlling.leaseVersion
                ) {
                    throw SemanticInputFenceChangedException("control owner changed while sending input")
                }
                val controlGenerationSnapshot = controlGeneration
                sendEncrypted(
                    MessageType.INPUT_EVENT,
                    plaintext,
                    expectedConnectedGeneration = connectedGeneration,
                    onEnvelopeCreated = { envelope, keyEpoch, exactEnvelopeBytes ->
                        if (controlGeneration != controlGenerationSnapshot) {
                            throw SemanticInputFenceChangedException("control owner changed while sending input")
                        }
                        semanticInputOutbox.put(
                            inputId = input.inputId,
                            fence = SemanticInputFence(
                                sessionId = config.sessionId,
                                peerDeviceId = config.pcDeviceId,
                                keyEpoch = keyEpoch,
                                controlGeneration = controlGenerationSnapshot,
                            ),
                            envelopeSequence = envelope.seq,
                            transportGeneration = connectedGeneration,
                            exactEnvelopeBytes = exactEnvelopeBytes,
                        )
                    },
                )
            }
            withTimeout(inputAcceptanceTimeoutMillis) { pending.await() }
        } catch (_: TimeoutCancellationException) {
            throw RemoteInputDeliveryUnknownException("input acceptance timed out")
        } catch (error: SemanticInputOutboxOverflowException) {
            // A bounded outbox must fail closed without tearing down a healthy
            // Relay connection or pretending the input was delivered.
            throw RemoteInputDeliveryUnknownException(error.message ?: "semantic input outbox is full")
        } catch (error: SemanticInputFenceChangedException) {
            throw RemoteInputDeliveryUnknownException(error.message ?: "control owner changed while sending input")
        } catch (error: RemoteInputRejectedException) {
            throw error
        } catch (error: RemoteInputDeliveryUnknownException) {
            throw error
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            val reason = error.message ?: "input transport failed"
            handleSessionLocalFailure(reason, connectedGeneration)
            throw RemoteInputDeliveryUnknownException(reason)
        } finally {
            pendingInputs.remove(input.inputId, pending)
            plaintext.fill(0)
            canonicalBytes.fill(0)
        }
    }

    /** Number of semantic inputs still awaiting an application result. */
    internal fun semanticInputOutboxSize(): Int = semanticInputOutbox.size

    /** Bytes retained by the semantic input outbox; useful for boundedness diagnostics. */
    internal fun semanticInputOutboxByteCount(): Int = semanticInputOutbox.byteCount

    override fun copyPendingInputEnvelopeForRelay(inputId: UUID): ByteArray? {
        if (closed.get()) return null
        return semanticInputOutbox.copyForRelay(inputId, currentSemanticInputFence())
    }

    override suspend fun resendPendingInputOverRelay(
        inputId: UUID,
        expectedConnectedGeneration: Int?,
    ): Boolean = controlArbitrationMutex.withLock controlLock@{
        if (closed.get() || closing.get()) return@controlLock false
        sendMutex.withLock sendLock@{
            if (closed.get() || closing.get()) return@sendLock false
            // Key rotation is serialized by sendMutex and control-owner changes
            // are serialized by controlArbitrationMutex. Taking the exact copy
            // only after both locks are held prevents a stale input from being
            // replayed between a hard-fence clear and the Relay write.
            val exactEnvelopeBytes = semanticInputOutbox.copyForRelay(
                inputId,
                currentSemanticInputFence(),
            ) ?: return@sendLock false
            try {
                // Parsing validates metadata for the endpoint fence. The exact
                // retained bytes, not protobuf reserialization, are sent on Relay.
                sendExactEnvelopeOverRelayLocked(exactEnvelopeBytes, expectedConnectedGeneration)
            } finally {
                exactEnvelopeBytes.fill(0)
            }
        }
    }

    /** Caller owns both controlArbitrationMutex and sendMutex. */
    private suspend fun sendExactEnvelopeOverRelayLocked(
        exactEnvelopeBytes: ByteArray,
        expectedConnectedGeneration: Int?,
    ): Boolean {
        val envelope = Envelope.parseFrom(exactEnvelopeBytes)
        require(envelope.messageType == MessageType.INPUT_EVENT) {
            "semantic replay Envelope must be INPUT_EVENT"
        }
        require(envelope.sessionId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.sessionId))) {
            "semantic replay Envelope session mismatch"
        }
        require(envelope.senderDeviceId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.androidDeviceId))) {
            "semantic replay Envelope sender mismatch"
        }
        require(envelope.recipientDeviceId.toByteArray().contentEquals(ProductionRealtimeClient.uuidBytes(config.pcDeviceId))) {
            "semantic replay Envelope recipient mismatch"
        }
        if (closed.get() || closing.get()) return false
        return if (expectedConnectedGeneration != null) {
            transport.sendRelayExactForConnectedGeneration(
                envelope,
                exactEnvelopeBytes,
                expectedConnectedGeneration,
            )
        } else {
            transport.sendRelayExact(envelope, exactEnvelopeBytes)
        }
    }

    private fun currentSemanticInputFence(): SemanticInputFence {
        val keyEpoch = synchronized(materialLock) { currentMaterial.epoch }
        return SemanticInputFence(
            sessionId = config.sessionId,
            peerDeviceId = config.pcDeviceId,
            keyEpoch = keyEpoch,
            controlGeneration = controlGeneration,
        )
    }

    private fun TerminalInput.toWireInput(): TerminalInputV1 {
        val builder = TerminalInputV1.newBuilder().setFormatVersion(1)
        when (this) {
            is TerminalInput.Text -> builder
                .setKind(TerminalInputKindV1.TERMINAL_INPUT_KIND_V1_TEXT)
                .setText(value)
                .setModifiers(modifiers.toWireModifiers())
            is TerminalInput.Key -> builder
                .setKind(TerminalInputKindV1.TERMINAL_INPUT_KIND_V1_KEY)
                .setKey(value.toWireKey())
                .setModifiers(modifiers.toWireModifiers())
            is TerminalInput.Paste -> builder
                .setKind(TerminalInputKindV1.TERMINAL_INPUT_KIND_V1_PASTE)
                .setPaste(ByteString.copyFrom(bytes))
            is TerminalInput.RawBytes -> builder
                .setKind(TerminalInputKindV1.TERMINAL_INPUT_KIND_V1_RAW_BYTES)
                .setRawBytes(ByteString.copyFrom(bytes))
        }
        return builder.build()
    }

    private fun dev.termlink.terminal.api.TerminalInputModifiers.toWireModifiers(): TerminalInputModifiersV1 =
        TerminalInputModifiersV1.newBuilder()
            .setControl(control)
            .setAlt(alt)
            .setShift(shift)
            .setMeta(meta)
            .build()

    private fun TerminalKey.toWireKey(): TerminalKeyV1 = when (this) {
        TerminalKey.ENTER -> TerminalKeyV1.TERMINAL_KEY_V1_ENTER
        TerminalKey.TAB -> TerminalKeyV1.TERMINAL_KEY_V1_TAB
        TerminalKey.BACKSPACE -> TerminalKeyV1.TERMINAL_KEY_V1_BACKSPACE
        TerminalKey.ESCAPE -> TerminalKeyV1.TERMINAL_KEY_V1_ESCAPE
        TerminalKey.DELETE -> TerminalKeyV1.TERMINAL_KEY_V1_DELETE
        TerminalKey.INSERT -> TerminalKeyV1.TERMINAL_KEY_V1_INSERT
        TerminalKey.HOME -> TerminalKeyV1.TERMINAL_KEY_V1_HOME
        TerminalKey.END -> TerminalKeyV1.TERMINAL_KEY_V1_END
        TerminalKey.PAGE_UP -> TerminalKeyV1.TERMINAL_KEY_V1_PAGE_UP
        TerminalKey.PAGE_DOWN -> TerminalKeyV1.TERMINAL_KEY_V1_PAGE_DOWN
        TerminalKey.ARROW_UP -> TerminalKeyV1.TERMINAL_KEY_V1_ARROW_UP
        TerminalKey.ARROW_DOWN -> TerminalKeyV1.TERMINAL_KEY_V1_ARROW_DOWN
        TerminalKey.ARROW_LEFT -> TerminalKeyV1.TERMINAL_KEY_V1_ARROW_LEFT
        TerminalKey.ARROW_RIGHT -> TerminalKeyV1.TERMINAL_KEY_V1_ARROW_RIGHT
        TerminalKey.F1 -> TerminalKeyV1.TERMINAL_KEY_V1_F1
        TerminalKey.F2 -> TerminalKeyV1.TERMINAL_KEY_V1_F2
        TerminalKey.F3 -> TerminalKeyV1.TERMINAL_KEY_V1_F3
        TerminalKey.F4 -> TerminalKeyV1.TERMINAL_KEY_V1_F4
        TerminalKey.F5 -> TerminalKeyV1.TERMINAL_KEY_V1_F5
        TerminalKey.F6 -> TerminalKeyV1.TERMINAL_KEY_V1_F6
        TerminalKey.F7 -> TerminalKeyV1.TERMINAL_KEY_V1_F7
        TerminalKey.F8 -> TerminalKeyV1.TERMINAL_KEY_V1_F8
        TerminalKey.F9 -> TerminalKeyV1.TERMINAL_KEY_V1_F9
        TerminalKey.F10 -> TerminalKeyV1.TERMINAL_KEY_V1_F10
        TerminalKey.F11 -> TerminalKeyV1.TERMINAL_KEY_V1_F11
        TerminalKey.F12 -> TerminalKeyV1.TERMINAL_KEY_V1_F12
    }

    override suspend fun sendResize(size: TerminalSize) {
        // Android is a viewer/controller of the Windows PTY. Its viewport can
        // never author a terminal grid, even if a future caller accidentally
        // reaches this legacy outbound interface.
        Unit
    }

    override suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease =
        withContext(Dispatchers.IO) {
            try {
                val response = api.post(
                    "/v1/sessions/${config.sessionId}/lease/$leaseId/renew",
                    buildJsonObject { },
                )
                if (response is JsonNull) {
                    throw RemoteControlLeaseLostException("LEASE_NOT_HELD")
                }
                parseLease(response.jsonObject)
            } catch (error: TermLinkApiException) {
                if (error.code == "CONTROL_NOT_GRANTED" || error.code == "LEASE_NOT_HELD") {
                    throw RemoteControlLeaseLostException(error.code)
                }
                throw error
            }
        }

    /**
     * Removes the PC subscriber route on the existing encrypted stream before
     * unregistering the local endpoint. Product-level suspend teardown should
     * prefer this API; the timeout keeps cancellation and owner shutdown bounded.
     */
    suspend fun closeGracefully() {
        if (closed.get() || !closing.compareAndSet(false, true)) return
        withContext(NonCancellable) {
            var unsubscribeSent = false
            try {
                subscriberReady.value = 0
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                if (started.get() && mutableTransportState.value is ProductionTransportState.Connected) {
                    withTimeout(GRACEFUL_UNSUBSCRIBE_TIMEOUT_MILLIS) {
                        sendEncrypted(
                            messageType = MessageType.UNSUBSCRIBE,
                            plaintext = ByteArray(0),
                            allowWhileClosing = true,
                        )
                    }
                    unsubscribeSent = true
                }
            } catch (_: Exception) {
                // A dead socket cannot acknowledge route teardown. Immediate
                // endpoint close remains the bounded fallback.
            } finally {
                var transportClosed = false
                try {
                    if (unsubscribeSent) {
                        transport.closeAfterUnsubscribe()
                        transportClosed = true
                    }
                } finally {
                    try {
                        closeInternal(transportAlreadyClosed = transportClosed)
                    } finally {
                        // A replacement renderer may reuse the same durable cursor
                        // generation. Do not let an ACK transaction from this retired
                        // incarnation complete after its caller starts that bootstrap.
                        session.awaitAcknowledgementWriterStopped()
                    }
                }
            }
        }
    }

    override fun close() {
        closing.set(true)
        closeInternal(transportAlreadyClosed = false)
    }

    private fun closeInternal(transportAlreadyClosed: Boolean) {
        if (!closed.compareAndSet(false, true)) return
        synchronized(directSequenceReservationLock) {
            directSequenceReservation?.releaseForSending()
            directSequenceReservation = null
        }
        cancelSessionReconcile()
        reconnectJob?.cancel()
        reconnectStabilityJob?.cancel()
        reconnectStabilityJob = null
        subscriberRefreshJob?.cancel()
        previousMaterialRetirementJob?.cancel()
        pendingInputs.values.forEach {
            it.completeExceptionally(RemoteInputDeliveryUnknownException("production session closed"))
        }
        pendingInputs.clear()
        semanticInputOutbox.clear()
        pendingTerminations.values.forEach {
            it.completeExceptionally(
                ProductionTransportException("production session closed during termination request"),
            )
        }
        pendingTerminations.clear()
        pendingHostHistory.entries.forEach { (requestId, pending) ->
            if (pendingHostHistory.remove(requestId, pending)) {
                pending.fail(
                    ProductionTransportException("production session closed during host history request"),
                )
            }
        }
        if (!transportAlreadyClosed) transport.close()
        api.close()
        incomingJob.cancel()
        transportStateJob?.cancel()
        transportStateJob = null
        receivePressureJob.cancel()
        scopeJob.cancel()
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

    private suspend fun sendFlowControl(force: Boolean) {
        flowControlMutex.withLock {
            val readyGeneration = subscriberReady.value
            if (readyGeneration <= 0 ||
                transport.currentConnectedGeneration() != readyGeneration
            ) return
            val pressure = transport.receivePressure.value
            val paused = bootstrapFlowHold.get() || semanticFlowHold.get() || pressure.paused
            if (!force &&
                lastFlowControlGeneration == readyGeneration &&
                lastFlowControlPaused == paused
            ) return
            val acknowledgement = session.state.value.lastAckedSequence
            val epoch = flowControlEpoch.incrementAndGet()
            require(epoch > 0) { "flow control epoch exhausted" }
            val payload = FlowControlPayload.newBuilder()
                .setHighestContiguousSeq(acknowledgement)
                .setMaxUnackedEvents(if (paused) 0 else ProductionRealtimeClient.OPEN_WINDOW_EVENTS)
                .setMaxUnackedWireBytes(if (paused) 0L else ProductionRealtimeClient.OPEN_WINDOW_WIRE_BYTES)
                .setPaused(paused)
                .setReasonCode(
                    when {
                        bootstrapFlowHold.get() -> "ANDROID_SUBSCRIBER_BOOTSTRAP"
                        semanticFlowHold.get() -> "ANDROID_HISTORY_RECOVERY"
                        paused -> "ANDROID_RENDER_QUEUE_HIGH_WATER"
                        else -> "ANDROID_RENDER_QUEUE_AVAILABLE"
                    },
                )
                .setControlEpoch(epoch)
                .build()
                .toByteArray()
            try {
                sendEncrypted(
                    MessageType.FLOW_CONTROL,
                    payload,
                    acknowledgement,
                    expectedConnectedGeneration = readyGeneration,
                )
            } catch (error: ProductionTransportException) {
                if (transport.currentConnectedGeneration() != readyGeneration) {
                    subscriberReady.compareAndSet(readyGeneration, 0)
                    return
                }
                throw error
            }
            flowControlSendCount.incrementAndGet()
            lastFlowControlGeneration = readyGeneration
            lastFlowControlPaused = paused
        }
    }

    private fun setSemanticFlowHold(held: Boolean) {
        if (semanticFlowHold.getAndSet(held) == held) return
        if (subscriberReady.value > 0 &&
            mutableTransportState.value is ProductionTransportState.Connected
        ) {
            controlScope.launch {
                try {
                    sendFlowControl(force = true)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    handleSessionLocalFailure(
                        error.message ?: "semantic flow control update failed",
                        subscriberReady.value,
                    )
                }
            }
        }
    }

    private suspend fun sendEncrypted(
        messageType: MessageType,
        plaintext: ByteArray,
        acknowledgement: Long = 0,
        expectedConnectedGeneration: Int? = null,
        allowWhileClosing: Boolean = false,
        onEnvelopeCreated: ((Envelope, Long, ByteArray) -> Unit)? = null,
    ) {
        try {
            sendMutex.withLock {
                check(!closed.get() && (allowWhileClosing || !closing.get())) {
                    "production session client is closed"
                }
                // A cutover proposal must be visible on the independent
                // signalling lane before the sequence named by its barrier can
                // be used. Waiting while holding the session send mutex keeps
                // later Relay control frames ordered; releasing the reservation
                // itself never takes this mutex.
                val reserved = synchronized(directSequenceReservationLock) {
                    directSequenceReservation
                }
                reserved?.awaitReleased()
                val sequence = synchronized(directSequenceReservationLock) {
                    val current = directSequenceReservation
                    if (current != null) {
                        check(current === reserved) {
                            "Direct sequence reservation changed while awaiting publication"
                        }
                        directSequenceReservation = null
                        current.sequence
                    } else {
                        envelopeSequenceProvider.next()
                    }
                }
                require(sequence > 0) { "envelope sequence provider returned a non-positive value" }
                val (materialSnapshot, keyEpochSnapshot) = synchronized(materialLock) {
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
                    ) to currentMaterial.epoch
                }
                val (nonce, key, keyIdSnapshot) = materialSnapshot
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
                    // Serialize once. The semantic outbox, Direct routing and
                    // Relay fallback all consume this exact byte sequence.
                    val exactEnvelopeBytes = envelope.toByteArray()
                    try {
                        onEnvelopeCreated?.let { callback ->
                            callback(envelope, keyEpochSnapshot, exactEnvelopeBytes)
                        }
                        val sent = if (expectedConnectedGeneration != null) {
                            transport.sendExactForConnectedGeneration(
                                envelope,
                                exactEnvelopeBytes,
                                expectedConnectedGeneration,
                            )
                        } else {
                            transport.sendExact(envelope, exactEnvelopeBytes)
                        }
                        if (!sent) {
                            throw ProductionTransportException("realtime socket is not connected")
                        }
                    } finally {
                        exactEnvelopeBytes.fill(0)
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

    private fun handleIncomingFailure(failure: IncomingFailure) {
        val context = failure.context
        val error = failure.error
        val engineDiagnostics = error as? TerminalEngineFailureDiagnostics
        Log.e(
            REALTIME_LOG_TAG,
            "session_frame_fatal stage=${context.stage.diagnosticName} " +
                "generation=${context.generation} seq=${context.envelope.seq} " +
                "type=${context.envelope.messageType.name} " +
                "exception=${error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS)} " +
                "terminal_operation=${engineDiagnostics?.terminalOperation.orEmpty()} " +
                "terminal_status=${engineDiagnostics?.terminalStatusName.orEmpty()} " +
                "terminal_status_code=${engineDiagnostics?.terminalStatusCode ?: 0}",
        )
        if (rendererFatal.get()) return
        val now = System.currentTimeMillis()
        if (error is UnknownKeyMaterialException &&
            now >= nextMetadataRefreshAtMillis.get() &&
            metadataRefreshInFlight.compareAndSet(false, true)
        ) {
            nextMetadataRefreshAtMillis.set(now + metadataRefreshCooldownMillis)
            controlScope.launch {
                try {
                    authoritativeMetadataRefreshObserver()
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Exception) {
                    // The transport reconnect loop remains the fallback;
                    // metadata refresh is best effort.
                } finally {
                    metadataRefreshInFlight.set(false)
                }
            }
        }
        // A malformed, stale-key, semantic, or renderer frame is scoped to
        // this exact session incarnation. Rebuild it from authenticated
        // history without replacing the device-wide WebSocket used by healthy
        // sibling sessions.
        failSessionIncarnation(context, error)
    }

    private fun failSessionIncarnation(
        context: IncomingProcessingContext,
        error: Exception,
    ) {
        if (!rendererFatal.compareAndSet(false, true)) return
        val reason = "terminal renderer incarnation failed"
        cancelSessionReconcile()
        subscriberReady.value = 0
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = null
        controlScope.launch {
            reconnectMutex.withLock {
                reconnectJob?.cancel()
                reconnectJob = null
                reconnectStabilityJob?.cancel()
                reconnectStabilityJob = null
            }
        }
        failPendingInputs(reason)
        semanticInputOutbox.clear()
        mutableTransportState.value = ProductionTransportState.Failed(reason)
        // This transport is a session-scoped endpoint over a device-wide
        // WebSocket. The product owner retires this endpoint and rebuilds the
        // renderer from authenticated history; a renderer failure must never
        // tear down the physical socket used by healthy sibling sessions.
        controlScope.launch { retireRendererEndpoint() }
        runCatching {
            val diagnostics = error as? TerminalEngineFailureDiagnostics
            rendererFatalObserver(
                ProductionRendererFatalFailure(
                    sessionId = config.sessionId,
                    sequence = context.envelope.seq,
                    transportGeneration = context.generation,
                    messageType = context.envelope.messageType,
                    failureStage = context.stage.diagnosticName,
                    exceptionClass = error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
                    terminalOperation = diagnostics?.terminalOperation?.take(MAX_FAILURE_CLASS_CHARS),
                    terminalStatusName = diagnostics?.terminalStatusName?.take(MAX_FAILURE_CLASS_CHARS),
                    terminalStatusCode = diagnostics?.terminalStatusCode,
                ),
            )
        }.onFailure { observerError ->
            Log.e(
                REALTIME_LOG_TAG,
                "renderer_fatal_observer_failed exception=" +
                    observerError::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
            )
        }
    }

    private suspend fun retireRendererEndpoint() {
        try {
            if (!closed.get() && started.get() &&
                mutableTransportState.value is ProductionTransportState.Connected
            ) {
                withTimeout(GRACEFUL_UNSUBSCRIBE_TIMEOUT_MILLIS) {
                    sendEncrypted(
                        messageType = MessageType.UNSUBSCRIBE,
                        plaintext = ByteArray(0),
                        allowWhileClosing = true,
                    )
                }
            }
        } catch (_: Exception) {
            // The endpoint is already renderer-fenced. A failed UNSUBSCRIBE
            // cannot justify replacing the healthy device-wide socket.
        } finally {
            transport.closeAfterUnsubscribe()
        }
    }

    private inline fun <T> parseIncomingPayload(
        context: IncomingProcessingContext,
        parse: () -> T,
    ): T {
        context.stage = IncomingFailureStage.PARSE
        return parse().also { context.stage = IncomingFailureStage.SEMANTIC }
    }

    private suspend fun handleIncoming(context: IncomingProcessingContext) {
        val envelope = context.envelope
        check(!closed.get()) { "production session client is closed" }
        lastReceivedSequence.accumulateAndGet(envelope.seq, ::maxOf)
        context.stage = IncomingFailureStage.VALIDATE
        validateEnvelope(envelope)
        context.stage = IncomingFailureStage.KEY_LOOKUP
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
        context.stage = IncomingFailureStage.DECRYPT
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
            context.stage = IncomingFailureStage.SEMANTIC
            if (hostHistoryOnlyMode &&
                envelope.messageType != MessageType.HOST_HISTORY_RESPONSE &&
                envelope.messageType != MessageType.SESSION_TERMINATE_RESPONSE
            ) {
                // SUBSCRIBE may race an authoritative lifecycle or old replay
                // already queued by Windows. A history-only zero-window route
                // must never project those frames into its throwaway renderer.
                if (envelope.seq > 0L) lastHandledSequence.accumulateAndGet(envelope.seq, ::maxOf)
                return
            }
            when (envelope.messageType) {
                MessageType.HOST_HISTORY_RESPONSE -> handleHostHistoryResponse(context, plaintext)
                MessageType.SESSION_TERMINATE_RESPONSE -> handleSessionTerminateResponse(context, plaintext)
                MessageType.OUTPUT_CHUNK -> {
                    val payload = parseIncomingPayload(context) { OutputChunkPayload.parseFrom(plaintext) }
                    require(payload.output.size() > 0) { "output chunk is empty" }
                    val sourceEventSequence = payload.sourceEventSeq.takeIf { it > 0 } ?: envelope.seq
                    val presentationRevision = payload.presentationRevision.takeIf { it > 0 }
                        ?: sourceEventSequence
                    require(sourceEventSequence == envelope.seq) {
                        "output source event sequence does not match Envelope.seq"
                    }
                    requireApplied(
                        acceptOrdered(
                            context,
                            envelope,
                            RemoteSessionEvent.Output(
                                sequence = envelope.seq,
                                bytes = payload.output.toByteArray(),
                                sourceEventSequence = sourceEventSequence,
                                presentationRevision = presentationRevision,
                            ),
                        ),
                    )
                }
                MessageType.RESIZE_APPLIED -> {
                    val payload = parseIncomingPayload(context) { ResizePayload.parseFrom(plaintext) }
                    requireApplied(
                        acceptOrdered(
                            context,
                            envelope,
                            RemoteSessionEvent.Resize(envelope.seq, TerminalSize(payload.columns, payload.rows)),
                        ),
                    )
                }
                MessageType.GAP -> {
                    val payload = parseIncomingPayload(context) { GapPayload.parseFrom(plaintext) }
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
                    val acceptance = acceptOrdered(
                        context,
                        envelope,
                        RemoteSessionEvent.Gap(eventSequence, payload.start, payload.end),
                    )
                    requireApplied(acceptance)
                }
                MessageType.SESSION_UPSERT -> {
                    val payload = parseIncomingPayload(context) { SessionUpsertPayload.parseFrom(plaintext) }
                    // One Envelope sequence represents exactly one ordered
                    // event. A title-bearing upsert is therefore applied as a
                    // Title event only; lifecycle transitions continue to use
                    // their dedicated SESSION_* messages or a legacy upsert
                    // without field 9.
                    requireApplied(acceptOrdered(context, envelope, sessionUpsertEvent(envelope.seq, payload)))
                }
                MessageType.SESSION_STARTED -> requireApplied(
                    acceptOrdered(
                        context,
                        envelope,
                        RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.RUNNING),
                    ),
                )
                MessageType.SESSION_INTERRUPTED -> requireApplied(
                    acceptOrdered(
                        context,
                        envelope,
                        RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.INTERRUPTED),
                    ),
                )
                MessageType.SESSION_ENDED -> {
                    val payload = parseIncomingPayload(context) { SessionUpsertPayload.parseFrom(plaintext) }
                    requireApplied(
                        acceptOrdered(
                            context,
                            envelope,
                            RemoteSessionEvent.Lifecycle(
                                envelope.seq,
                                RemoteLifecycle.ENDED,
                                payload.exitCode.takeIf { payload.hasExitCode() },
                            ),
                        ),
                    )
                }
                MessageType.SESSION_REMOVED -> requireApplied(
                    acceptOrdered(
                        context,
                        envelope,
                        RemoteSessionEvent.Lifecycle(envelope.seq, RemoteLifecycle.ENDED),
                    ),
                )
                MessageType.INPUT_ACCEPTED, MessageType.INPUT_REJECTED -> {
                    val result = parseIncomingPayload(context) { InputResultPayload.parseFrom(plaintext) }
                    val inputId = ProductionRealtimeClient.uuidFromBytes(result.inputId.toByteArray())
                        ?: error("InputResult input ID must be 16 bytes")
                    require(
                        (envelope.messageType == MessageType.INPUT_ACCEPTED) == result.accepted,
                    ) { "InputResult type and accepted flag disagree" }
                    val pending = pendingInputs[inputId]
                    try {
                        val accepted = requireApplied(
                            acceptOrdered(
                                context,
                                envelope,
                                RemoteSessionEvent.InputResult(
                                    sequence = envelope.seq,
                                    inputId = inputId,
                                    accepted = result.accepted,
                                    reason = result.reasonCode.ifBlank { null },
                                ),
                            ),
                        )
                        if (!accepted) {
                            if (pending != null) pendingInputs.remove(inputId, pending)
                            pending?.completeExceptionally(
                                RemoteInputDeliveryUnknownException("history gap prevents input confirmation"),
                            )
                        } else {
                            semanticInputOutbox.complete(inputId, currentSemanticInputFence())
                            if (pending != null) pendingInputs.remove(inputId, pending)
                            if (pending != null) {
                                if (envelope.messageType == MessageType.INPUT_ACCEPTED && result.accepted) {
                                    pending.complete(Unit)
                                } else {
                                    pending.completeExceptionally(
                                        RemoteInputRejectedException(result.reasonCode.ifBlank { "input rejected" }),
                                    )
                                }
                            }
                        }
                    } catch (error: Throwable) {
                        if (pending != null) pendingInputs.remove(inputId, pending)
                        pending?.completeExceptionally(error)
                        throw error
                    }
                }
                MessageType.LEASE_GRANTED -> {
                    val payload = parseIncomingPayload(context) { LeasePayload.parseFrom(plaintext) }
                    val leaseId = decodeLeaseId(payload.leaseId.toByteArray())
                    controlArbitrationMutex.withLock {
                        val acceptance = acceptOrdered(
                            context,
                            envelope,
                            RemoteSessionEvent.ControlGranted(envelope.seq),
                        )
                        requireApplied(acceptance)
                        if (acceptance is EventAcceptance.Applied) {
                            val previous = session.state.value as? RemoteSessionState.Controlling
                            if (previous == null ||
                                previous.leaseId != leaseId ||
                                previous.leaseVersion != payload.version
                            ) {
                                controlGeneration++
                                failPendingInputs("control owner changed")
                                semanticInputOutbox.clear()
                            }
                            session.grantControl(leaseId, payload.expiresAtMs, payload.version)
                            restoreControlAfterReconnect = true
                        }
                    }
                }
                MessageType.LEASE_DENIED, MessageType.LEASE_REVOKED -> {
                    val payload = parseIncomingPayload(context) { LeasePayload.parseFrom(plaintext) }
                    controlArbitrationMutex.withLock {
                        val acceptance = acceptOrdered(
                            context,
                            envelope,
                            RemoteSessionEvent.ControlRevoked(
                                sequence = envelope.seq,
                                reason = payload.reasonCode.ifBlank { "control lease denied" },
                            ),
                        )
                        requireApplied(acceptance)
                        if (acceptance is EventAcceptance.Applied) {
                            controlGeneration++
                            restoreControlAfterReconnect = false
                            failPendingInputs("control lease revoked")
                            semanticInputOutbox.clear()
                            if (payload.localInputEpoch > 0) {
                                observedLocalInputEpoch.accumulateAndGet(payload.localInputEpoch, ::maxOf)
                            }
                        }
                    }
                }
                MessageType.HEARTBEAT -> error("PC-to-Android heartbeat is unsupported")
                MessageType.OUTPUT_ACK -> error("PC-to-Android output acknowledgement is unsupported")
                else -> error("unsupported production message type ${envelope.messageType}")
            }
            if (envelope.seq > 0L) lastHandledSequence.accumulateAndGet(envelope.seq, ::maxOf)
            if (session.state.value is RemoteSessionState.Exited) {
                failPendingInputs("remote session ended")
                semanticInputOutbox.clear()
            }
            // A zero-window Host History endpoint remains usable after the
            // authoritative lifecycle has ended. It cannot receive terminal
            // replay or input, but it still needs the subscriber route long
            // enough to fetch the final Snapshot/transcript and explicit-close
            // acknowledgement. Ordinary live clients keep the old teardown.
            if (session.state.value is RemoteSessionState.Exited && !hostHistoryOnlyMode) {
                subscriberReady.value = 0
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                cancelSessionReconcile()
            }
        } finally {
            plaintext.fill(0)
        }
    }

    private suspend fun handleHostHistoryResponse(
        context: IncomingProcessingContext,
        plaintext: ByteArray,
    ) {
        val response = parseIncomingPayload(context) { HostHistoryResponsePayload.parseFrom(plaintext) }
        val requestId = ProductionRealtimeClient.uuidFromBytes(response.requestId.toByteArray())
        if (requestId == null) {
            Log.w(REALTIME_LOG_TAG, "host_history_response_rejected reason=invalid_request_id")
            return
        }
        val pending = pendingHostHistory[requestId] ?: return
        try {
            when (response.status) {
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK -> {
                    val accepted = pending.accept(response)
                    try {
                        sendHostHistoryControl(
                            requestId = requestId,
                            action = HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CHUNK_ACK,
                            objectId = accepted.objectId,
                            chunkIndex = accepted.chunkIndex,
                        )
                    } catch (error: Throwable) {
                        accepted.completed?.destroy()
                        throw error
                    }
                    val completed = accepted.completed ?: return
                    // Keep ownership registered until the requester has taken
                    // the value. If cancellation wins after this delivery,
                    // requestHostHistory removes and cancels the Channel, whose
                    // undelivered callback zeroes the sealed object.
                    pending.deliver(HostHistoryRequestResult.Ready(completed))
                }
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_NOT_READY -> {
                    validateEmptyHostHistoryResponse(response)
                    failHostHistoryRequest(
                        requestId,
                        pending,
                        ProductionHostHistoryNotReadyException(
                            response.errorCode.ifBlank { "Host history is not ready" },
                        ),
                    )
                }
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_NOT_FOUND -> {
                    validateEmptyHostHistoryResponse(response)
                    if (pendingHostHistory.remove(requestId, pending)) {
                        pending.deliver(HostHistoryRequestResult.Missing)
                    }
                }
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_GAP -> {
                    validateEmptyHostHistoryResponse(response)
                    failHostHistoryRequest(
                        requestId,
                        pending,
                        ProductionHostHistoryGapException(
                            response.errorCode.ifBlank { "Host history contains a permanent gap" },
                        ),
                    )
                }
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED -> {
                    validateEmptyHostHistoryResponse(response)
                    failHostHistoryRequest(
                        requestId,
                        pending,
                        IllegalStateException(
                            response.errorCode.ifBlank { "Host history request was rejected" },
                        ),
                    )
                }
                else -> error("Host history response status is invalid")
            }
        } catch (error: Exception) {
            failHostHistoryRequest(requestId, pending, error)
        }
    }

    private fun handleSessionTerminateResponse(
        context: IncomingProcessingContext,
        plaintext: ByteArray,
    ) {
        val response = parseIncomingPayload(context) { SessionTerminateResponsePayload.parseFrom(plaintext) }
        val requestId = ProductionRealtimeClient.uuidFromBytes(response.requestId.toByteArray()) ?: return
        require(response.errorCode.length <= 128) { "Session termination error code is too long" }
        pendingTerminations.remove(requestId)?.let { pending ->
            if (response.accepted) pending.complete(true)
            else pending.completeExceptionally(
                IllegalStateException(response.errorCode.ifBlank { "Session termination was rejected" }),
            )
        }
    }

    private fun validateEmptyHostHistoryResponse(response: HostHistoryResponsePayload) {
        require(response.errorCode.length <= 128) { "Host history error code is too long" }
        require(
            response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_UNSPECIFIED &&
                response.objectId.isEmpty &&
                response.objectSha256.isEmpty &&
                response.objectBytes == 0L &&
                response.chunkIndex == 0 &&
                response.chunkCount == 0 &&
                response.objectChunk.isEmpty,
        ) { "Host history error response contains object data" }
    }

    private fun failHostHistoryRequest(
        requestId: UUID,
        pending: HostHistoryAccumulator,
        error: Throwable,
    ) {
        if (pendingHostHistory.remove(requestId, pending)) {
            pending.fail(error)
        }
    }

    private suspend fun acceptOrdered(
        context: IncomingProcessingContext,
        envelope: Envelope,
        event: RemoteSessionEvent,
    ): EventAcceptance {
        context.stage = IncomingFailureStage.ENGINE_ACCEPT
        val stateBefore = session.state.value
        val acceptance = session.accept(event)
        when (acceptance) {
            is EventAcceptance.Applied ->
                contiguousAppliedSequence.accumulateAndGet(acceptance.sequence, ::maxOf)
            is EventAcceptance.Duplicate -> duplicateEventCount.incrementAndGet()
            is EventAcceptance.HistoryGap -> historyGapCount.incrementAndGet()
            EventAcceptance.IgnoredAfterExit -> Unit
        }
        if (acceptance is EventAcceptance.HistoryGap && stateBefore !is RemoteSessionState.HistoryGap) {
            val output = event as? RemoteSessionEvent.Output
            val cause = when {
                event is RemoteSessionEvent.Gap -> "peer_gap"
                event.sequence != acceptance.expectedSequence -> "sequence_discontinuity"
                output != null && output.sourceEventSequence != output.sequence -> "source_sequence_mismatch"
                output != null -> "presentation_revision_not_advanced"
                else -> "ordered_event_rejected"
            }
            Log.w(
                REALTIME_LOG_TAG,
                "history_gap cause=$cause message_type=${envelope.messageType.name} " +
                    "envelope_seq=${envelope.seq} event_seq=${event.sequence} " +
                    "expected=${acceptance.expectedSequence} received=${acceptance.receivedSequence} " +
                    "ack_before=${stateBefore.lastAckedSequence} " +
                    "source_seq=${output?.sourceEventSequence ?: 0} " +
                    "presentation_revision=${output?.presentationRevision ?: 0} " +
                    "last_received=${lastReceivedSequence.get()} last_handled=${lastHandledSequence.get()}",
            )
        }
        return acceptance
    }

    internal companion object {
        private const val REALTIME_LOG_TAG = "TermLinkRealtime"
        private const val MAX_FAILURE_CLASS_CHARS = 128
        private const val GRACEFUL_UNSUBSCRIBE_TIMEOUT_MILLIS = 1_000L

        internal fun sessionUpsertEvent(
            sequence: Long,
            payload: SessionUpsertPayload,
        ): RemoteSessionEvent {
            require(sequence > 0) { "session event sequence must be positive" }
            if (payload.hasTitle()) return RemoteSessionEvent.Title(sequence, payload.title)
            return when (payload.lifecycle.uppercase()) {
                "RUNNING", "STARTED", "CHANGED" ->
                    RemoteSessionEvent.Lifecycle(sequence, RemoteLifecycle.RUNNING)
                "INTERRUPTED" ->
                    RemoteSessionEvent.Lifecycle(sequence, RemoteLifecycle.INTERRUPTED)
                "EXITED", "ENDED" -> RemoteSessionEvent.Lifecycle(
                    sequence,
                    RemoteLifecycle.ENDED,
                    payload.exitCode.takeIf { payload.hasExitCode() },
                )
                else -> error("unknown session lifecycle")
            }
        }
    }

    private fun validateEnvelope(envelope: Envelope) {
        require(envelope.protocolVersion == 1) { "unsupported Envelope protocol version" }
        require(envelope.direction == dev.termlink.protocol.realtime.v1.Direction.PC_TO_ANDROID) {
            "Envelope direction mismatch"
        }
        require(envelope.ack == 0L) { "PC-to-Android application messages must use ack=0" }
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
        require(envelope.seq > 0L && envelope.nonce.size() == 12 && envelope.ciphertext.size() >= 16) {
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

    private fun ProductionTransportState.diagnosticName(): String = when (this) {
        ProductionTransportState.Connecting -> "CONNECTING"
        ProductionTransportState.Connected -> "CONNECTED"
        is ProductionTransportState.Reconnecting -> "RECONNECTING"
        is ProductionTransportState.Failed -> "FAILED"
        ProductionTransportState.Closed -> "CLOSED"
    }

    private fun RemoteSessionState.diagnosticName(): String = when (this) {
        is RemoteSessionState.Live -> "LIVE"
        is RemoteSessionState.Reconnecting -> "RECONNECTING"
        is RemoteSessionState.ReadOnly -> "READ_ONLY"
        is RemoteSessionState.Controlling -> "CONTROLLING"
        is RemoteSessionState.Exited -> "EXITED"
        is RemoteSessionState.HistoryGap -> "HISTORY_GAP"
    }

    private suspend fun handleTransportState(event: ProductionRealtimeSessionConnectionEvent) {
        if (closed.get() || closing.get()) return
        if (rendererFatal.get()) {
            subscriberReady.value = 0
            return
        }
        // Every authoritative physical-socket transition supersedes any
        // session-local retry, including a queued retry for an older connected
        // generation. The new Connected event performs its own bootstrap.
        cancelSessionReconcile()
        val transition = transportTransitionGeneration.incrementAndGet()
        val state = event.state
        when (state) {
            RealtimeState.Connecting -> {
                // A device-scoped foreground/network reconcile or warm connect
                // already owns this physical generation. Retire any delayed
                // session retry so it cannot start a second socket afterward.
                cancelPendingReconnect()
                cancelReconnectAttemptReset()
                subscriberReady.value = 0
                mutableTransportState.value = ProductionTransportState.Connecting
            }
            RealtimeState.Connected -> {
                val connectedGeneration = event.connectedGeneration
                if (connectedGeneration <= 0 ||
                    transport.currentConnectedGeneration() != connectedGeneration
                ) {
                    // This Connected may already be queued when a device-scoped
                    // lifecycle/network replacement retires its socket. Only the
                    // event for the still-current generation may publish SUBSCRIBE.
                    return
                }
                cancelPendingReconnect()
                mutableTransportState.value = ProductionTransportState.Connected
                resendPendingSemanticInputsOverRelay(connectedGeneration)
                // Wire subscription and its receive window are control-plane
                // work. They must be published for the successor generation
                // even while JNI/replay still occupies the renderer lane. The
                // state cursor advances only after durable ACK persistence.
                val sessionState = session.state.value
                if (sessionState is RemoteSessionState.Exited) return
                if (sessionState !is RemoteSessionState.HistoryGap) {
                    try {
                        establishSubscriptionHeldThenOpen(
                            sessionState.lastAckedSequence,
                            connectedGeneration,
                        )
                        if (!isCurrentTransportTransition(transition, connectedGeneration)) return
                        startSubscriberRefresh()
                        scheduleReconnectAttemptReset()
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        subscriberReady.compareAndSet(connectedGeneration, 0)
                        if (isCurrentTransportTransition(transition, connectedGeneration)) {
                            scheduleSessionReconcile(
                                error.message ?: "initial session subscribe failed",
                                connectedGeneration,
                            )
                        }
                        return
                    }
                }
                scheduleConnectedSessionProjection(transition, connectedGeneration)
            }
            is RealtimeState.Failed -> {
                cancelReconnectAttemptReset()
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                subscriberReady.value = 0
                failPendingInputs(state.reason)
                scheduleReconnect(state.reason)
                notifySessionDisconnected(state.reason)
            }
            is RealtimeState.AuthenticationRejected -> {
                cancelReconnectAttemptReset()
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                subscriberReady.value = 0
                failPendingInputs(state.reason)
                notifySessionDisconnected(state.reason)
                mutableTransportState.value = ProductionTransportState.Reconnecting(0, state.reason)
            }
            is RealtimeState.AuthenticationFailed -> {
                cancelReconnectAttemptReset()
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                subscriberReady.value = 0
                failPendingInputs(state.reason)
                notifySessionDisconnected(state.reason)
                mutableTransportState.value = ProductionTransportState.Failed(state.reason)
            }
            is RealtimeState.Reconnecting -> {
                if (state.attempt == ProductionRealtimeMultiplexer.EXTERNAL_RECONNECT_ATTEMPT) {
                    cancelPendingReconnect()
                    cancelReconnectAttemptReset()
                    subscriberRefreshJob?.cancel()
                    subscriberRefreshJob = null
                    subscriberReady.value = 0
                    failPendingInputs(state.reason ?: "realtime socket is reconnecting")
                    notifySessionDisconnected(state.reason)
                    mutableTransportState.value = ProductionTransportState.Reconnecting(0, state.reason)
                }
            }
            RealtimeState.Closed -> {
                cancelReconnectAttemptReset()
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                subscriberReady.value = 0
                failPendingInputs("production transport closed")
                mutableTransportState.value = ProductionTransportState.Closed
            }
        }
    }

    private fun isCurrentTransportTransition(transition: Long, connectedGeneration: Int): Boolean =
        !closed.get() && !closing.get() && !rendererFatal.get() &&
            transportTransitionGeneration.get() == transition &&
            transport.currentConnectedGeneration() == connectedGeneration

    /**
     * Relay recovery is driven by the semantic outbox, not a retiring Direct
     * actor. A transport send never removes an entry; only its authenticated
     * INPUT_ACCEPTED/INPUT_REJECTED result or a hard authority fence can do so.
     */
    private suspend fun resendPendingSemanticInputsOverRelay(connectedGeneration: Int) {
        for (inputId in semanticInputOutbox.pendingInputIds(currentSemanticInputFence())) {
            if (closed.get() || closing.get() ||
                transport.currentConnectedGeneration() != connectedGeneration
            ) return
            if (!resendPendingInputOverRelay(inputId, connectedGeneration)) return
        }
    }

    private fun scheduleConnectedSessionProjection(transition: Long, connectedGeneration: Int) {
        if (hostHistoryOnlyMode) return
        sessionScope.launch sessionTransition@{
            if (!isCurrentTransportTransition(transition, connectedGeneration)) return@sessionTransition
            try {
                val available = session.onConnected { _ -> Unit }
                if (!available || !isCurrentTransportTransition(transition, connectedGeneration)) {
                    return@sessionTransition
                }
                controlScope.launch controlTransition@{
                    if (!isCurrentTransportTransition(transition, connectedGeneration) ||
                        subscriberReady.value != connectedGeneration
                    ) return@controlTransition
                    restoreDesiredControlIfNeeded()
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "session_connected_projection_failed exception=" +
                        error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
                )
            }
        }
    }

    private fun notifySessionDisconnected(reason: String?) {
        if (hostHistoryOnlyMode) return
        sessionScope.launch {
            try {
                session.onDisconnected(reason)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(
                    REALTIME_LOG_TAG,
                    "session_disconnect_projection_failed exception=" +
                        error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
                )
            }
        }
    }

    private fun startSubscriberRefresh() {
        if (closed.get() || rendererFatal.get() || historyRecoveryInProgress.get() ||
            session.state.value is RemoteSessionState.HistoryGap
        ) return
        val connectedGeneration = subscriberReady.value
        if (connectedGeneration <= 0 ||
            transport.currentConnectedGeneration() != connectedGeneration
        ) return
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = controlScope.launch {
            while (true) {
                delay(subscriberRefreshIntervalMillis)
                if (rendererFatal.get() || session.state.value is RemoteSessionState.HistoryGap ||
                    session.state.value is RemoteSessionState.Exited ||
                    subscriberReady.value != connectedGeneration ||
                    transport.currentConnectedGeneration() != connectedGeneration
                ) break
                try {
                    establishSubscriptionHeldThenOpen(
                        session.state.value.lastAckedSequence,
                        connectedGeneration,
                    )
                    restoreDesiredControlIfNeeded()
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    scheduleSessionReconcile(
                        error.message ?: "subscriber refresh failed",
                        connectedGeneration,
                    )
                    break
                }
            }
        }
    }

    /**
     * Recovers one logical session route without replacing the device-wide
     * WebSocket. Every send is bound to [connectedGeneration], so a successor
     * generation can cancel this work without accepting a stale SUBSCRIBE.
     */
    private fun scheduleSessionReconcile(reason: String, connectedGeneration: Int) {
        if (!isEligibleSessionReconcile(connectedGeneration)) return
        subscriberReady.compareAndSet(connectedGeneration, 0)
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = null
        failPendingInputs(reason)

        synchronized(sessionReconcileLock) {
            if (sessionReconcileConnectedGeneration == connectedGeneration &&
                sessionReconcileJob?.isActive == true
            ) return

            sessionReconcileEpoch.incrementAndGet()
            sessionReconcileJob?.cancel()
            sessionReconcileProjectionJob?.cancel()
            val reconcileEpoch = sessionReconcileEpoch.get()
            val projectionCompletion = CompletableDeferred<Boolean>()
            sessionReconcileConnectedGeneration = connectedGeneration
            mutableTransportState.value = ProductionTransportState.Reconnecting(1, reason)

            // Projection work stays ordered with renderer events. It may be
            // delayed by JNI, but it cannot hold up the control-lane wire retry.
            sessionReconcileProjectionJob = if (hostHistoryOnlyMode) null else sessionScope.launch {
                try {
                    try {
                        session.onDisconnected(reason)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        Log.w(
                            REALTIME_LOG_TAG,
                            "session_reconcile_projection_failed phase=disconnect exception=" +
                                error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
                        )
                    }
                    if (!projectionCompletion.await() ||
                        !isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)
                    ) return@launch
                    try {
                        val available = session.onConnected { _ -> Unit }
                        if (available && isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) {
                            controlScope.launch {
                                if (isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) {
                                    restoreDesiredControlIfNeeded()
                                }
                            }
                        }
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        Log.w(
                            REALTIME_LOG_TAG,
                            "session_reconcile_projection_failed phase=connected exception=" +
                                error::class.java.simpleName.take(MAX_FAILURE_CLASS_CHARS),
                        )
                    }
                } finally {
                    synchronized(sessionReconcileLock) {
                        if (sessionReconcileEpoch.get() == reconcileEpoch) {
                            sessionReconcileProjectionJob = null
                        }
                    }
                }
            }

            sessionReconcileJob = controlScope.launch {
                try {
                    runSessionReconcile(
                        reason = reason,
                        connectedGeneration = connectedGeneration,
                        reconcileEpoch = reconcileEpoch,
                        projectionCompletion = projectionCompletion,
                    )
                } finally {
                    if (!projectionCompletion.isCompleted) projectionCompletion.complete(false)
                    synchronized(sessionReconcileLock) {
                        if (sessionReconcileEpoch.get() == reconcileEpoch) {
                            sessionReconcileJob = null
                        }
                    }
                }
            }
        }
    }

    private suspend fun runSessionReconcile(
        reason: String,
        connectedGeneration: Int,
        reconcileEpoch: Long,
        projectionCompletion: CompletableDeferred<Boolean>,
    ) {
        var attempt = 0
        while (isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) {
            attempt++
            mutableTransportState.value = ProductionTransportState.Reconnecting(attempt, reason)
            val delayMillis = reconnectDelaysMillis[(attempt - 1).coerceAtMost(reconnectDelaysMillis.lastIndex)]
            if (delayMillis == 0L) yield() else delay(delayMillis)
            if (!isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) return
            try {
                establishSubscriptionHeldThenOpen(
                    session.state.value.lastAckedSequence,
                    connectedGeneration,
                )
                if (!isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) return
                mutableTransportState.value = ProductionTransportState.Connected
                startSubscriberRefresh()
                scheduleReconnectAttemptReset()
                projectionCompletion.complete(true)
                return
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                subscriberReady.compareAndSet(connectedGeneration, 0)
                if (!isCurrentSessionReconcile(reconcileEpoch, connectedGeneration)) return
            }
        }
    }

    private fun isEligibleSessionReconcile(connectedGeneration: Int): Boolean =
        connectedGeneration > 0 && !closed.get() && !closing.get() && !rendererFatal.get() &&
            session.state.value !is RemoteSessionState.Exited &&
            session.state.value !is RemoteSessionState.HistoryGap &&
            transport.currentConnectedGeneration() == connectedGeneration

    private fun isCurrentSessionReconcile(reconcileEpoch: Long, connectedGeneration: Int): Boolean =
        sessionReconcileEpoch.get() == reconcileEpoch &&
            isEligibleSessionReconcile(connectedGeneration)

    private fun cancelSessionReconcile() {
        val jobs = synchronized(sessionReconcileLock) {
            sessionReconcileEpoch.incrementAndGet()
            sessionReconcileConnectedGeneration = 0
            val current = sessionReconcileJob to sessionReconcileProjectionJob
            sessionReconcileJob = null
            sessionReconcileProjectionJob = null
            current
        }
        jobs.first?.cancel()
        jobs.second?.cancel()
    }

    private fun handleSessionLocalFailure(reason: String, connectedGeneration: Int) {
        if (connectedGeneration > 0 &&
            transport.currentConnectedGeneration() == connectedGeneration
        ) {
            scheduleSessionReconcile(reason, connectedGeneration)
        } else if (transport.currentConnectedGeneration() <= 0) {
            // The physical transport already disappeared; preserve the existing
            // socket-level reconnect fallback for that distinct failure class.
            failTransport(reason)
        }
    }

    /**
     * A transport generation change invalidates the local lease presentation,
     * but it must not silently discard an explicit user control intent. The
     * REST arbitration generation still fences a concurrent release or remote
     * revoke, while the request mutex prevents duplicate restore POSTs.
     */
    private suspend fun restoreDesiredControlIfNeeded(): Boolean {
        val shouldRestore = controlArbitrationMutex.withLock {
            restoreControlAfterReconnect &&
                session.state.value !is RemoteSessionState.Controlling &&
                session.state.value !is RemoteSessionState.Exited &&
                session.state.value !is RemoteSessionState.HistoryGap
        }
        if (!shouldRestore) return true
        return try {
            requestControl()
            true
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            // Subscription remains usable in read-only mode. The periodic
            // subscriber refresh retries the desired lease without tearing
            // down an otherwise healthy realtime transport.
            false
        }
    }

    private suspend fun scheduleReconnect(reason: String?) {
        reconnectMutex.withLock {
            if (closed.get() || closing.get() || rendererFatal.get() ||
                session.state.value is RemoteSessionState.Exited
            ) return
            if (reconnectJob?.isActive == true) return
            reconnectAttempt++
            mutableTransportState.value = ProductionTransportState.Reconnecting(reconnectAttempt, reason)
            val delayMillis = reconnectDelaysMillis[(reconnectAttempt - 1).coerceAtMost(reconnectDelaysMillis.lastIndex)]
            reconnectJob = controlScope.launch {
                delay(delayMillis.toLong())
                // Clear the scheduled attempt before starting OkHttp. A fast
                // offline failure can call back from newWebSocket while this
                // coroutine is still active; leaving it installed would make
                // scheduleReconnect discard the next retry.
                reconnectMutex.withLock {
                    reconnectJob = null
                    if (!closed.get() && !closing.get() && !rendererFatal.get()) transport.connect()
                }
            }
        }
    }

    private suspend fun cancelPendingReconnect() {
        reconnectMutex.withLock {
            reconnectJob?.cancel()
            reconnectJob = null
        }
    }

    /**
     * A socket is not healthy merely because its WebSocket handshake finished.
     * Preserve the backoff attempt across short-lived reconnects and reset it
     * only after the session subscribed and remained usable for the stability
     * window.
     */
    private suspend fun scheduleReconnectAttemptReset() {
        reconnectMutex.withLock {
            reconnectStabilityJob?.cancel()
            reconnectStabilityJob = controlScope.launch {
                delay(reconnectStabilityMillis)
                reconnectMutex.withLock {
                    reconnectAttempt = 0
                    reconnectStabilityJob = null
                }
            }
        }
    }

    private suspend fun cancelReconnectAttemptReset() {
        reconnectMutex.withLock {
            reconnectStabilityJob?.cancel()
            reconnectStabilityJob = null
        }
    }

    private fun failPendingInputs(reason: String) {
        pendingInputs.values.forEach { it.completeExceptionally(RemoteInputDeliveryUnknownException(reason)) }
        pendingInputs.clear()
    }

    private fun failTransport(reason: String) {
        if (closed.get()) return
        transport.disconnect(reason)
        publishTransportFailure(reason)
    }

    private fun publishTransportFailure(reason: String) {
        if (rendererFatal.get()) return
        mutableTransportState.value = ProductionTransportState.Failed(reason)
        failPendingInputs(reason)
        controlScope.launch { scheduleReconnect(reason) }
        notifySessionDisconnected(reason)
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
            setSemanticFlowHold(true)
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

    private enum class IncomingFailureStage(val diagnosticName: String) {
        VALIDATE("validate"),
        KEY_LOOKUP("key_lookup"),
        DECRYPT("decrypt"),
        PARSE("parse"),
        SEMANTIC("semantic"),
        ENGINE_ACCEPT("engine_accept"),
    }

    private data class IncomingProcessingContext(
        val generation: Int,
        val envelope: Envelope,
        var stage: IncomingFailureStage = IncomingFailureStage.VALIDATE,
    )

    private data class IncomingFailure(
        val context: IncomingProcessingContext,
        val error: Exception,
    )

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

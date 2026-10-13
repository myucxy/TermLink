package dev.termlink.network.production

import android.util.Log
import android.os.SystemClock
import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.DeltaCrypto
import dev.termlink.crypto.BrokerCaptureCrypto
import dev.termlink.crypto.FrozenHistoryCrypto
import dev.termlink.crypto.SessionCrypto
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.model.TerminalBufferLimitException
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.FlowControlPayload
import dev.termlink.protocol.realtime.v1.FrozenHistoryMetadata
import dev.termlink.protocol.realtime.v1.FrozenHistoryRequest
import dev.termlink.protocol.realtime.v1.StateDeltaMetadataV1
import dev.termlink.protocol.realtime.v1.StateDeltaRequestV1
import dev.termlink.protocol.realtime.v1.BrokerCaptureSelectionV1
import dev.termlink.protocol.realtime.v1.BrokerOutputPayload
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
import dev.termlink.protocol.realtime.v1.ResizeResponsePayload
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
import java.util.ArrayDeque
import java.util.LinkedHashMap
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
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
import kotlinx.coroutines.withTimeoutOrNull
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
    /** Product native-core sessions require explicit state acceptance before opening the receive window. */
    val negotiateTerminalState: Boolean = false,
    /** Pins the authenticated host profile to the engine created for this client. */
    val expectedTerminalState: String? = null,
) {
    init {
        require(keyEpoch > 0) { "keyEpoch must be positive" }
        require(pcExchangePublicKey.size == 32) { "PC exchange public key must be 32 bytes" }
        require(pcExchangePublicKey.any { it.toInt() != 0 }) { "PC exchange public key is empty" }
        require(keyId.isNotEmpty() && keyId.size <= 128) { "keyId must be 1..128 bytes" }
        require(noncePrefix.size == 4) { "noncePrefix must be 4 bytes" }
        require(expectedTerminalState == null || expectedTerminalState in ProductionTerminalStateContract.SUPPORTED_STATES) {
            "expected terminal state is unsupported"
        }
        require(expectedTerminalState == null || negotiateTerminalState) { "expected terminal state requires negotiation" }
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
private const val HOST_HISTORY_REQUEST_TIMEOUT_MILLIS = 30_000L
private const val HOST_HISTORY_CANCEL_TIMEOUT_MILLIS = 1_000L
private const val GRAPHICS_HOST_HISTORY_TOTAL_TIMEOUT_MILLIS = 5 * 60_000L
private const val FROZEN_HISTORY_CAPABILITY = "host_history_frozen_v1"
private const val STATE_DELTA_CAPABILITY = "state_delta_v1"
private const val BROKER_CAPTURE_CAPABILITY = "terminal_broker_captures_v1"
private const val BROKER_CAPTURE_FORMAT_NAME = "TERMLINK_BROKER_CAPTURE_V1"
private const val HOST_HISTORY_RELAY_CAPABILITY = "host_history_v1"
private val TERMINAL_SUBSCRIBER_CAPABILITIES = ProductionTerminalStateContract.SUPPORTED_STATES +
    listOf(HOST_HISTORY_RELAY_CAPABILITY, BROKER_CAPTURE_CAPABILITY)
private const val FROZEN_HISTORY_V2_CAPABILITY = "tlhp_v2"
private const val FROZEN_HISTORY_V2_FORMAT_NAME = "TERMLINK_FROZEN_HISTORY_PAGE_V2"
private const val FROZEN_HISTORY_RELEASE_TIMEOUT_MILLIS = 3_000L
private const val HOST_HISTORY_RESPONSE_CHUNK_BYTES = 512 * 1024
private const val MAX_BROKER_PENDING_EVENTS = 32
private const val MAX_BROKER_PENDING_BYTES = 3 * 1024 * 1024
internal fun supportsFrozenHistory(profile: ProductionTerminalProfile, capabilities: List<String>): Boolean =
    profile.isGraphics && capabilities.size <= 16 &&
        capabilities.all { it.length in 1..64 } &&
        FROZEN_HISTORY_CAPABILITY in capabilities && FROZEN_HISTORY_V2_CAPABILITY in capabilities

internal fun supportsStateDelta(profile: ProductionTerminalProfile, capabilities: List<String>): Boolean =
    profile.isGraphics && capabilities.size <= 16 &&
        capabilities.all { it.length in 1..64 } && STATE_DELTA_CAPABILITY in capabilities

internal fun supportsBrokerCapture(profile: ProductionTerminalProfile, capabilities: List<String>): Boolean =
    profile.isGraphics && capabilities.size <= 16 &&
        capabilities.all { it.length in 1..64 } && BROKER_CAPTURE_CAPABILITY in capabilities

internal fun frozenHistoryChunkCount(objectBytes: Long): Int {
    require(objectBytes in 1L..(FrozenHistoryCrypto.MAX_PAGE_V2_BYTES +
        FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES).toLong()) { "Frozen history object size is invalid" }
    return ((objectBytes + HOST_HISTORY_RESPONSE_CHUNK_BYTES - 1) / HOST_HISTORY_RESPONSE_CHUNK_BYTES).toInt()
}

internal fun isCurrentFrozenBrowse(
    request: FrozenHistoryRequestContext,
    browseGeneration: Long,
    keyEpoch: Long,
): Boolean = request.browseGeneration == browseGeneration && request.keyEpoch == keyEpoch
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

class ProductionHostHistoryObject internal constructor(
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
    private var reservation: TerminalBufferBudget.Reservation?,
    val frozenHistory: FrozenHistoryMetadata? = null,
    val stateDelta: StateDeltaMetadataV1? = null,
    val brokerCapture: BrokerCaptureSelectionV1? = null,
) : AutoCloseable {
    private val destroyed = AtomicBoolean(false)
    /** One request's verified receive/send evidence; transport ACK is not a durable renderer commit. */
    @Volatile var transferEvidence: ProductionHostHistoryTransferEvidence? = null
        internal set

    @Synchronized
    fun takeReservation(): TerminalBufferBudget.Reservation {
        check(!destroyed.get()) { "Host history object is destroyed" }
        return requireNotNull(reservation) { "Host history reservation was already transferred" }.also {
            reservation = null
        }
    }

    @Synchronized
    fun destroy() {
        if (!destroyed.compareAndSet(false, true)) return
        ciphertextSha256.fill(0)
        sealedObject.fill(0)
        reservation?.close()
        reservation = null
    }

    override fun close() = destroy()
}

data class ProductionHostHistoryTransferEvidence(
    val requestId: UUID,
    val newChunksAccepted: Int,
    val duplicateChunksAccepted: Int,
    val chunkAcksSent: Int,
)

class ProductionHostHistoryGapException(message: String) : IllegalStateException(message)
class ProductionHostHistoryNotReadyException(message: String) : IllegalStateException(message)

class ProductionFrozenHistoryException(
    val code: String,
    val requiredCells: ULong = 0uL,
    val requiredUtf8Bytes: ULong = 0uL,
    val capture: ProductionFrozenHistoryCapture? = null,
    val availableSnapshotSequence: ULong? = null,
    internal val metadata: FrozenHistoryMetadata? = null,
) : IllegalStateException("Frozen history request failed: $code")

class ProductionStateDeltaNeedsFullSnapshotException : IllegalStateException("NEEDS_FULL_SNAPSHOT")

internal class StateDeltaRequestContext(
    val snapshotSequence: ULong,
    val contextId: ByteString,
    val profile: Int,
    val baseNextSequence: ULong,
    val baseSnapshotBytes: ULong,
    val baseSha256: ByteString,
    val keyEpoch: Long,
    val keyId: String,
) {
    fun validate(response: HostHistoryResponsePayload) {
        require(response.hasStateDelta() && !response.hasFrozenHistory()) { "State Delta metadata is missing" }
        val delta = response.stateDelta
        require(response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_STATE_DELTA &&
            response.snapshotSeq in 1 until Long.MAX_VALUE &&
            (snapshotSequence == 0uL || response.snapshotSeq.toULong() == snapshotSequence) &&
            delta.contextId == contextId && delta.profile == profile &&
            delta.baseNextSourceSequence.toULong() == baseNextSequence &&
            delta.baseSnapshotBytes.toULong() == baseSnapshotBytes &&
            delta.baseSnapshotSha256 == baseSha256 &&
            delta.targetNextSourceSequence.toULong() == response.snapshotSeq.toULong() + 1uL &&
            delta.targetNextSourceSequence.toULong() > baseNextSequence &&
            delta.targetSnapshotBytes in 104L..(128L * 1024 * 1024) &&
            delta.targetSnapshotSha256.size() == 32 &&
            response.keyId == keyId && response.formatName == DeltaCrypto.FORMAT_NAME &&
            response.formatVersion == DeltaCrypto.FORMAT_VERSION && response.errorCode.isEmpty() &&
            response.columns == 0 && response.rows == 0 && response.initialTranscriptBeforeLineId == 0L &&
            response.requestBeforeLineId == 0L && response.firstLineId == 0L && response.lastLineId == 0L &&
            !response.hasMoreBefore && response.nextBeforeLineId == 0L &&
            response.startSeq == 0L && response.endSeq == 0L && !response.hasMoreEvents
        ) { "State Delta response does not match the requested transition" }
    }

    fun validateNeedsFullSnapshot(response: HostHistoryResponsePayload) {
        require(response.status == HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED &&
            response.errorCode == "NEEDS_FULL_SNAPSHOT" && !response.hasStateDelta() &&
            !response.hasFrozenHistory() && response.snapshotSeq in 0 until Long.MAX_VALUE) {
            "State Delta fallback response is invalid"
        }
    }
}

internal class BrokerCaptureRequestContext(
    val selection: BrokerCaptureSelectionV1,
    val keyEpoch: Long,
    val keyId: String,
    val stateContract: String,
) {
    init {
        require(ProductionTerminalProfile.entries.any { it.isGraphics && it.stateContract == stateContract }) {
            "Broker capture terminal state is unsupported"
        }
        require(selection.objectId.size() == 16 && selection.objectId.toByteArray().any { it != 0.toByte() } &&
            selection.sourceEventSeq in 1 until Long.MAX_VALUE &&
            selection.canonicalBytes in 136..BrokerCaptureCrypto.MAXIMUM_CANONICAL_BYTES &&
            selection.canonicalSha256.size() == 32) { "Broker capture selection is invalid" }
    }

    fun validate(response: HostHistoryResponsePayload) {
        require(response.hasBrokerCapture() && !response.hasFrozenHistory() && !response.hasStateDelta() &&
            response.brokerCapture == selection &&
            response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_BROKER_CAPTURE &&
            response.objectId == selection.objectId &&
            response.startSeq == selection.sourceEventSeq && response.endSeq == selection.sourceEventSeq &&
            response.snapshotSeq == 0L && response.columns == 0 && response.rows == 0 &&
            response.initialTranscriptBeforeLineId == 0L && response.requestBeforeLineId == 0L &&
            response.firstLineId == 0L && response.lastLineId == 0L &&
            !response.hasMoreBefore && response.nextBeforeLineId == 0L && !response.hasMoreEvents &&
            response.keyId == keyId && response.formatName == BROKER_CAPTURE_FORMAT_NAME &&
            response.formatVersion == 1 && response.errorCode.isEmpty() &&
            response.objectBytes == selection.canonicalBytes.toLong() +
                BrokerCaptureCrypto.HEADER_BYTES + 16L) {
            "Broker capture response does not match the ordered reference"
        }
    }
}

/** Token and capture identity are valid only for this session and key epoch. */
class ProductionFrozenHistoryCapture internal constructor(
    val token: ByteString,
    val captureId: ULong,
    val revision: ULong,
    val snapshotSequence: ULong,
    val browseGeneration: Long,
    internal val ownerSessionId: UUID,
    val keyEpoch: Long,
    val stateContract: String,
)

/** Encrypted page ownership follows [objectData]; callers must close it after decrypting. */
class ProductionFrozenHistoryPage internal constructor(
    val capture: ProductionFrozenHistoryCapture,
    val objectData: ProductionHostHistoryObject,
    val maxLines: Int,
    val maxCells: Int,
    val maxUtf8Bytes: Int,
) : AutoCloseable {
    override fun close() = objectData.close()
}

internal data class FrozenHistoryRequestContext(
    val kind: HostHistoryRequestKindV1,
    val snapshotSequence: ULong,
    val beforeLineId: ULong,
    val token: ByteString,
    val captureId: ULong,
    val revision: ULong,
    val maxLines: Int,
    val maxCells: Int,
    val maxUtf8Bytes: Int,
    val maxObjectBytes: Int,
    val browseGeneration: Long,
    val keyEpoch: Long,
    val keyId: String,
    val stateContract: String,
) {
    fun validate(response: HostHistoryResponsePayload) {
        require(response.hasFrozenHistory()) { "Frozen history metadata is missing" }
        val frozen = response.frozenHistory
        require(response.snapshotSeq.toULong() == snapshotSequence &&
            response.requestBeforeLineId.toULong() == beforeLineId &&
            frozen.token.size() == 32 && frozen.captureId != 0L &&
            frozen.captureRevision.toULong() == snapshotSequence + 1uL &&
            frozen.snapshotSeq.toULong() == snapshotSequence &&
            frozen.leaseRemainingMs > 0L && frozen.stateContract == stateContract &&
            frozen.requiredCells == 0L && frozen.requiredUtf8Bytes == 0L &&
            (kind == HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN ||
                (frozen.token == token && frozen.captureId.toULong() == captureId &&
                    frozen.captureRevision.toULong() == revision)) &&
            response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_FROZEN_PAGE &&
            response.formatName == FROZEN_HISTORY_V2_FORMAT_NAME && response.formatVersion == 1 &&
            response.keyId == keyId && response.startSeq == 0L && response.endSeq == 0L &&
            !response.hasMoreEvents && response.errorCode.isEmpty()
        ) { "Frozen history response does not match the request" }
    }

    fun validateError(response: HostHistoryResponsePayload): ULong? {
        val availableSnapshotSequence = if (kind == HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN &&
            response.errorCode == "ANCHOR_CHANGED" && response.snapshotSeq > 0L
        ) {
            require(response.snapshotSeq.toULong() > snapshotSequence) {
                "Frozen history available snapshot must advance the requested anchor"
            }
            response.snapshotSeq.toULong()
        } else {
            require(response.snapshotSeq == 0L) { "Frozen history error contains an unexpected snapshot" }
            null
        }
        if (!response.hasFrozenHistory()) {
            require(response.errorCode != "PAGE_BUDGET") { "Frozen history budget metadata is missing" }
            return availableSnapshotSequence
        }
        val frozen = response.frozenHistory
        require(frozen.token.size() == 32 && frozen.captureId != 0L &&
            frozen.captureRevision.toULong() == snapshotSequence + 1uL &&
            frozen.stateContract == stateContract &&
            (kind == HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN ||
            (frozen.token == token && frozen.captureId.toULong() == captureId &&
                frozen.captureRevision.toULong() == revision)) &&
            frozen.snapshotSeq.toULong() == snapshotSequence &&
            (response.errorCode != "PAGE_BUDGET" ||
                frozen.requiredCells > 0L || frozen.requiredUtf8Bytes > 0L)
        ) { "Frozen history budget metadata does not match the request" }
        return availableSnapshotSequence
    }
}

data class ProductionRealtimeSessionDiagnostics(
    val terminalAcceptedGeneration: Int = 0,
    val acceptedTerminalProfile: String? = null,
    val transportState: String,
    val transportFailureReason: String? = null,
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
    /** Valid new Host History chunks accepted by this client; no storage commit implied. */
    val hostHistoryChunkReceivedCount: Long = 0,
    /** Byte-identical repeats of previously accepted chunks. */
    val hostHistoryChunkDuplicateCount: Long = 0,
    /** CHUNK_ACK envelopes accepted by the local encrypted transport send. */
    val hostHistoryChunkAckSentCount: Long = 0,
    val multiplexer: ProductionRealtimeMultiplexerDiagnostics,
)

/** Identity and optional local-only evidence of a failed session-processing incarnation. */
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
    /** Local observer only; excluded from transport logs and telemetry. */
    val terminalProtocolDiagnostic: dev.termlink.terminal.api.TerminalProtocolDiagnostic? = null,
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
    private val graphicsHostHistoryTotalTimeoutMillis: Long = GRAPHICS_HOST_HISTORY_TOTAL_TIMEOUT_MILLIS,
    private val holdLiveReplayForHostHistory: Boolean = false,
    private val hostHistoryOnlyMode: Boolean = false,
    leaseRenewalIntervalMillis: Long = 10_000,
    disposeEngineOnClose: Boolean = true,
    orderedEventObserver: RemoteSessionOrderedEventObserver? = null,
    orderedEventObserverCapacity: Int = 64,
    private val rendererFatalObserver: (ProductionRendererFatalFailure) -> Unit = {},
    private val brokerCaptureResolver: (suspend (seq: Long, referenceV1: ByteArray) -> RemoteSessionEvent.BrokerOutput)? = null,
    private val sessionDispatcher: CoroutineDispatcher = Dispatchers.Default.limitedParallelism(1),
    private val controlDispatcher: CoroutineDispatcher = Dispatchers.Default.limitedParallelism(1),
    private val cleanupDispatcher: CoroutineDispatcher = Dispatchers.Default,
    authenticationRefresher: ((rejectedToken: String) -> String)? = null,
    onAuthenticationRejected: (rejectedToken: String) -> Unit = {},
    private val terminalBufferBudget: TerminalBufferBudget = TerminalBufferBudget.global,
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
    private val cleanupScope = CoroutineScope(SupervisorJob() + cleanupDispatcher)
    private val closed = AtomicBoolean(false)
    private val closing = AtomicBoolean(false)
    private val started = AtomicBoolean(false)
    private val metadataRefreshInFlight = AtomicBoolean(false)
    private val historyRecoveryInProgress = AtomicBoolean(false)
    private val rendererFatal = AtomicBoolean(false)
    private data class PendingTerminalAttachment(val generation: Int, val result: CompletableDeferred<ProductionTerminalProfile>)
    private val pendingTerminalAttachments = ConcurrentHashMap<UUID, PendingTerminalAttachment>()
    private data class HostClockSample(val generation: Int, val keyEpoch: Long, val hostMillis: Long, val elapsedMillis: Long)
    private val hostClockSample = AtomicReference<HostClockSample?>()
    private val terminalAcceptedGeneration = AtomicInteger(0)
    private val acceptedTerminalProfile = AtomicReference<ProductionTerminalProfile?>()
    private val terminalCompatibilityFailure = AtomicReference<TerminalCompatibilityException?>()
    private val nextMetadataRefreshAtMillis = AtomicLong(0)
    private val sendMutex = Mutex()
    private val directSequenceReservationLock = Any()
    private var directSequenceReservation: ProductionDirectSequenceReservation? = null
    private val incomingProcessingMutex = Mutex()
    private val brokerQueue = ArrayDeque<QueuedIncoming>()
    private var brokerPendingCount = 0
    private var brokerPendingBytes = 0
    private var brokerDrainJob: Job? = null
    private var brokerDrainToken = 0L
    private val controlRequestMutex = Mutex()
    private val controlArbitrationMutex = Mutex()
    private val materialLock = Any()
    private val observedLocalInputEpoch = AtomicLong(initialObservedLocalInputEpoch)
    private val pendingInputs = ConcurrentHashMap<UUID, CompletableDeferred<Unit>>()
    private val semanticInputOutbox = SemanticInputOutbox()
    private val pendingTerminations = ConcurrentHashMap<UUID, CompletableDeferred<Boolean>>()
    private val pendingResizes = ConcurrentHashMap<UUID, CompletableDeferred<TerminalSize>>()
    private val resizeInProgress = AtomicBoolean(false)
    private val pendingHostHistory = ConcurrentHashMap<UUID, HostHistoryAccumulator>()
    private val pendingHostHistoryLock = Any()
    private val frozenBrowseGeneration = AtomicLong(0)
    private val frozenSupportedGeneration = AtomicInteger(0)
    private val deltaSupportedGeneration = AtomicInteger(0)
    private val brokerSupportedGeneration = AtomicInteger(0)
    private fun currentFrozenEpoch(): Long = synchronized(materialLock) { currentMaterial.epoch }
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
    private val lastTransportFailureReason = AtomicReference<String?>(null)
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
        authenticationRefresher = authenticationRefresher,
        onAuthenticationRejected = onAuthenticationRejected,
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
    private val hostHistoryChunkReceivedCount = AtomicLong(0)
    private val hostHistoryChunkDuplicateCount = AtomicLong(0)
    private val hostHistoryChunkAckSentCount = AtomicLong(0)
    private val rejectedThroughGeneration = AtomicInteger(0)
    private val transportTransitionGeneration = AtomicLong(0)
    /** Guarded by [incomingProcessingMutex]. */
    private var activeIncomingGeneration = 0
    private val subscriptionBootstrapMutex = Mutex()
    private val flowControlMutex = Mutex()
    private var lastFlowControlGeneration = Int.MIN_VALUE
    private var lastFlowControlPaused: Boolean? = null

    internal sealed interface HostHistoryRequestResult {
        data class Ready(val value: ProductionHostHistoryObject) : HostHistoryRequestResult
        data object Missing : HostHistoryRequestResult
        data object Released : HostHistoryRequestResult
    }

    internal data class HostHistoryChunkAcceptance(
        val objectId: ByteString,
        val chunkIndex: Int,
        val completed: ProductionHostHistoryObject?,
        val newlyAccepted: Boolean,
    )

    internal class HostHistoryAccumulator(
        private val profile: ProductionTerminalProfile,
        private val bufferBudget: TerminalBufferBudget,
        val generation: Int,
        val frozen: FrozenHistoryRequestContext? = null,
        val delta: StateDeltaRequestContext? = null,
        val broker: BrokerCaptureRequestContext? = null,
    ) {
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
        private var newChunksAccepted = 0
        private var duplicateChunksAccepted = 0
        private var chunkAcksSent = 0
        private var accepting = true
        private var reservation: TerminalBufferBudget.Reservation? = null
        private var completedForDelivery: ProductionHostHistoryObject? = null
        private val lastProgressNanos = AtomicLong(System.nanoTime())

        suspend fun awaitResult(idleTimeoutMillis: Long): HostHistoryRequestResult {
            // Only newly admitted chunks move this deadline. Duplicates and invalid
            // metadata cannot keep an incomplete object alive indefinitely.
            while (true) {
                val elapsedMillis = (System.nanoTime() - lastProgressNanos.get()) / 1_000_000
                val remainingMillis = idleTimeoutMillis - elapsedMillis
                if (remainingMillis <= 0) throw ProductionTransportException("host history request timed out")
                withTimeoutOrNull(remainingMillis) { results.receive() }?.let { return it }
            }
        }

        @Synchronized
        fun accept(response: HostHistoryResponsePayload): HostHistoryChunkAcceptance? {
            // A timeout/close can race a response whose request was already looked up.
            // Never allocate again after cancellation or after the final chunk.
            if (!accepting) return null
            require(response.status == HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK) {
                "Host history response is not successful"
            }
            require(response.objectId.size() == 16 && response.objectSha256.size() == 32) {
                "Host history object identity is invalid"
            }
            require(response.kind != HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_UNSPECIFIED) {
                "Host history object kind is missing"
            }
            require(frozen != null || (!response.hasFrozenHistory() &&
                response.kind != HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_FROZEN_PAGE)) {
                "Frozen history page was not requested"
            }
            require(delta != null || (!response.hasStateDelta() &&
                response.kind != HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_STATE_DELTA)) {
                "State Delta was not requested"
            }
            require(broker != null || (!response.hasBrokerCapture() &&
                response.kind != HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_BROKER_CAPTURE)) {
                "Broker capture was not requested"
            }
            val expectedChunkCount = when {
                broker != null -> {
                    broker.validate(response)
                    require(response.objectBytes in (BrokerCaptureCrypto.HEADER_BYTES + 136 + 16).toLong()..
                        BrokerCaptureCrypto.MAXIMUM_OBJECT_BYTES.toLong()) { "Broker capture size is invalid" }
                    ((response.objectBytes + HOST_HISTORY_RESPONSE_CHUNK_BYTES - 1) /
                        HOST_HISTORY_RESPONSE_CHUNK_BYTES).toInt()
                }
                delta != null -> {
                    delta.validate(response)
                    require(response.objectBytes in (DeltaCrypto.HEADER_BYTES + 256 + 16).toLong()..
                        DeltaCrypto.MAXIMUM_OBJECT_BYTES.toLong()) { "State Delta object size is invalid" }
                    ((response.objectBytes + HOST_HISTORY_RESPONSE_CHUNK_BYTES - 1) /
                        HOST_HISTORY_RESPONSE_CHUNK_BYTES).toInt()
                }
                frozen != null -> {
                    frozen.validate(response)
                    if (response.objectBytes > frozen.maxObjectBytes.toLong()) {
                        throw ProductionFrozenHistoryException("PAGE_LIMIT", metadata = response.frozenHistory)
                    }
                    frozenHistoryChunkCount(response.objectBytes)
                }
                else -> profile.hostHistoryChunkCount(response.objectBytes)
            }
            val maximumChunks = when {
                broker != null -> 65
                delta != null -> 16
                frozen != null -> 129
                else -> profile.maxHostHistoryChunks
            }
            require(response.chunkCount in 1..maximumChunks &&
                response.chunkIndex in 0 until response.chunkCount) {
                "Host history chunk index is invalid"
            }
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
                try {
                    reservation = bufferBudget.reserve(response.objectBytes)
                    header = normalizedHeader
                    metadata = normalizedMetadata
                    assembled = ByteArray(response.objectBytes.toInt())
                    received = BooleanArray(response.chunkCount)
                } catch (error: Throwable) {
                    normalizedHeader.fill(0)
                    accepting = false
                    clearAssembly()
                    throw error
                }
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
            val isNewChunk = !admitted[index]
            if (!isNewChunk) {
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
                if (isNewChunk) lastProgressNanos.set(System.nanoTime())
                recordAcceptedChunk(isNewChunk)
                return HostHistoryChunkAcceptance(response.objectId, index, null, isNewChunk)
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
                reservation = requireNotNull(reservation),
                frozenHistory = if (frozen == null) null else value.frozenHistory,
                stateDelta = if (delta == null) null else value.stateDelta,
                brokerCapture = if (broker == null) null else value.brokerCapture,
            )
            reservation = null // Ownership now follows the returned object until destroy().
            assembled = null
            clearAssembly()
            accepting = false
            completedForDelivery = completed
            if (isNewChunk) lastProgressNanos.set(System.nanoTime())
            recordAcceptedChunk(isNewChunk)
            return HostHistoryChunkAcceptance(response.objectId, index, completed, isNewChunk)
        }

        @Synchronized
        fun markChunkAckSent() { chunkAcksSent++ }

        @Synchronized
        fun transferEvidence(requestId: UUID): ProductionHostHistoryTransferEvidence =
            ProductionHostHistoryTransferEvidence(requestId, newChunksAccepted,
                duplicateChunksAccepted, chunkAcksSent)

        private fun recordAcceptedChunk(newlyAccepted: Boolean) {
            if (newlyAccepted) newChunksAccepted++ else duplicateChunksAccepted++
        }

        @Synchronized
        fun deliver(result: HostHistoryRequestResult) {
            accepting = false
            if (results.trySend(result).isFailure && result is HostHistoryRequestResult.Ready) {
                result.value.destroy()
            }
        }

        @Synchronized
        fun acceptRelease(response: HostHistoryResponsePayload): Boolean {
            if (!accepting) return false
            require(frozen?.kind == HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE) {
                "Only frozen release may return empty OK"
            }
            require(response.status == HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK &&
                response.hasFrozenHistory() && response.errorCode.isEmpty()) {
                "Frozen release response is invalid"
            }
            val metadata = response.frozenHistory
            require(metadata.token == frozen.token && metadata.captureId.toULong() == frozen.captureId &&
                metadata.captureRevision.toULong() == frozen.revision &&
                metadata.snapshotSeq.toULong() == frozen.snapshotSequence &&
                frozen.revision == frozen.snapshotSequence + 1uL &&
                metadata.stateContract == frozen.stateContract &&
                response.snapshotSeq == 0L && response.requestBeforeLineId == 0L &&
                metadata.requiredCells == 0L && metadata.requiredUtf8Bytes == 0L) {
                "Frozen release identity does not match the request"
            }
            require(response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_UNSPECIFIED &&
                response.objectId.isEmpty && response.objectSha256.isEmpty && response.objectBytes == 0L &&
                response.chunkIndex == 0 && response.chunkCount == 0 && response.objectChunk.isEmpty &&
                response.formatName.isEmpty() && response.formatVersion == 0) {
                "Frozen release response must be empty"
            }
            accepting = false
            return true
        }

        @Synchronized
        fun takeCompleted(value: ProductionHostHistoryObject): ProductionHostHistoryObject {
            check(completedForDelivery === value) { "host history request is no longer active" }
            completedForDelivery = null
            return value
        }

        @Synchronized
        fun fail(error: Throwable) {
            accepting = false
            clearAssembly()
            completedForDelivery?.destroy()
            completedForDelivery = null
            results.close(error)
            // close preserves queued values; a failed request must relinquish those too.
            val buffered = results.tryReceive().getOrNull()
            if (buffered is HostHistoryRequestResult.Ready) buffered.value.destroy()
        }

        @Synchronized
        fun destroy() {
            accepting = false
            clearAssembly()
            completedForDelivery?.destroy()
            completedForDelivery = null
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
            reservation?.close()
            reservation = null
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
        require(graphicsHostHistoryTotalTimeoutMillis in 1..GRAPHICS_HOST_HISTORY_TOTAL_TIMEOUT_MILLIS) {
            "graphics host history total timeout must be at most five minutes"
        }
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
                        if (generation > activeIncomingGeneration) {
                            clearBrokerQueueLocked()
                            activeIncomingGeneration = generation
                        }

                        val context = IncomingProcessingContext(generation, envelope)
                        try {
                            handleIncoming(context)
                            null
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Exception) {
                            rejectedThroughGeneration.accumulateAndGet(generation, ::maxOf)
                            clearBrokerQueueLocked()
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

    fun diagnostics(): ProductionRealtimeSessionDiagnostics {
        val state = mutableTransportState.value
        if (state is ProductionTransportState.Failed && lastTransportFailureReason.get().isNullOrBlank()) {
            lastTransportFailureReason.set(state.reason.ifBlank { "TRANSPORT_FAILED_WITHOUT_REASON" })
            Log.w("TermLinkRealtime", "transport failed without reason; normalized")
        }
        return ProductionRealtimeSessionDiagnostics(
            terminalAcceptedGeneration = terminalAcceptedGeneration.get(),
            acceptedTerminalProfile = acceptedTerminalProfile.get()?.state,
            transportState = state.diagnosticName(),
            transportFailureReason = lastTransportFailureReason.get(),
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
        hostHistoryChunkReceivedCount = hostHistoryChunkReceivedCount.get(),
        hostHistoryChunkDuplicateCount = hostHistoryChunkDuplicateCount.get(),
        hostHistoryChunkAckSentCount = hostHistoryChunkAckSentCount.get(),
        multiplexer = transport.diagnostics(),
        )
    }

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
        if (started.get()) {
            // Opening a session may request control before the first socket is
            // connected. Wait for that physical generation before asking REST
            // for a lease, while still allowing its first session projection
            // to finish after the REST response.
            try {
                withTimeout(terminationReadinessTimeoutMillis) {
                    mutableTransportState.first { state ->
                        if (closed.get() || state is ProductionTransportState.Closed) {
                            throw ProductionTransportException("production session client is closed")
                        }
                        if (state is ProductionTransportState.Failed) {
                            throw ProductionTransportException(state.reason)
                        }
                        state is ProductionTransportState.Connected && transport.currentConnectedGeneration() > 0
                    }
                }
            } catch (_: TimeoutCancellationException) {
                throw ProductionTransportException("control transport was not ready")
            }
        }
        val requestConnectedGeneration = transport.currentConnectedGeneration()
        val lease = withContext(Dispatchers.IO) {
            parseLease(
                api.post(
                    "/v1/sessions/${config.sessionId}/lease",
                    buildJsonObject { },
                ).jsonObject,
            )
        }
        var releaseSupersededLease = false
        val granted = controlArbitrationMutex.withLock {
            if (requestConnectedGeneration <= 0 ||
                transport.currentConnectedGeneration() != requestConnectedGeneration
            ) {
                false
            } else if (controlGeneration != requestGeneration) {
                releaseSupersededLease = true
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
                if (transport.currentConnectedGeneration() != requestConnectedGeneration) {
                    // grantControl suspends on the session mutex. A socket replacement
                    // during that call must not leave the old lease writable.
                    val active = session.state.value as? RemoteSessionState.Controlling
                    if (active?.leaseId == lease.leaseId && active.leaseVersion == lease.version) {
                        session.revokeControl("control request transport changed")
                    }
                    false
                } else {
                    restoreControlAfterReconnect = true
                    true
                }
            }
        }
        if (!granted) {
            if (releaseSupersededLease) {
                // A local-input/revocation fence may need to release the stale
                // REST lease. Never delete a lease now used by a newer local
                // grant, or one whose physical socket has already changed.
                controlArbitrationMutex.withLock {
                    val active = session.state.value as? RemoteSessionState.Controlling
                    if (transport.currentConnectedGeneration() == requestConnectedGeneration &&
                        active?.leaseId != lease.leaseId
                    ) {
                        withContext(Dispatchers.IO) {
                            runCatching { api.delete("/v1/sessions/${config.sessionId}/lease/${lease.leaseId}") }
                        }
                    }
                }
            }
            if (releaseSupersededLease) throw ControlRequestSupersededException()
            throw java.io.IOException("control request was superseded by a transport generation change")
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
                    if (transportState is ProductionTransportState.Failed) throw terminalCompatibilityFailure.get() ?: ProductionTransportException(transportState.reason)
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

    /** Returns a sealed Delta only; the caller verifies and installs it before advancing live ACK. */
    suspend fun requestHostStateDelta(
        baseNextSourceSequence: ULong,
        baseSnapshotBytes: Int,
        baseSnapshotSha256: ByteArray,
        deltaProfile: Int,
        snapshotSequence: ULong = 0uL,
    ): ProductionHostHistoryObject {
        require(baseNextSourceSequence in 1uL..Long.MAX_VALUE.toULong() &&
            baseSnapshotBytes in 104..(128 * 1024 * 1024) && baseSnapshotSha256.size == 32 &&
            deltaProfile in 1..4 && snapshotSequence < Long.MAX_VALUE.toULong()) {
            "State Delta base checkpoint is invalid"
        }
        val profile = awaitTerminalProfile()
        if (!profile.isGraphics) throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
        val contextBytes = DeltaCrypto.deriveContextId(config.sessionId, config.pcDeviceId,
            profile.stateContract)
        val epoch = currentFrozenEpoch()
        val context = StateDeltaRequestContext(snapshotSequence, ByteString.copyFrom(contextBytes),
            deltaProfile, baseNextSourceSequence, baseSnapshotBytes.toULong(),
            ByteString.copyFrom(baseSnapshotSha256), epoch,
            DeltaCrypto.keyId(config.androidDeviceId, epoch, profile.stateContract))
        contextBytes.fill(0)
        return requireNotNull(requestHostHistory(
            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_STATE_DELTA,
            snapshotSequence = snapshotSequence, delta = context,
        )) { "State Delta response contained no object" }
    }

    /** Fetches only the sealed object named by an already authenticated ordered reference. */
    suspend fun requestHostBrokerCapture(selection: BrokerCaptureSelectionV1): ProductionHostHistoryObject {
        val profile = awaitTerminalProfile()
        if (!profile.isGraphics) throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
        val epoch = currentFrozenEpoch()
        val context = BrokerCaptureRequestContext(selection.toBuilder().build(), epoch,
            BrokerCaptureCrypto.keyId(config.androidDeviceId, epoch, profile.stateContract),
            profile.stateContract)
        return requireNotNull(requestHostHistory(
            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_BROKER_CAPTURE,
            broker = context,
        )) { "Broker capture response contained no object" }
    }

    suspend fun beginFrozenHistory(
        snapshotSequence: ULong,
        maxLines: Int = 128,
        maxCells: Int = 65_536,
        maxUtf8Bytes: Int = 4 * 1024 * 1024,
        maxObjectBytes: Int = FrozenHistoryCrypto.MAX_PAGE_V2_BYTES + FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES,
    ): ProductionFrozenHistoryPage {
        require(snapshotSequence > 0uL && snapshotSequence < Long.MAX_VALUE.toULong()) {
            "Frozen snapshot sequence is invalid"
        }
        validateFrozenBudgets(maxLines, maxCells, maxUtf8Bytes)
        validateFrozenObjectBudget(maxObjectBytes)
        val profile = awaitTerminalProfile()
        if (!profile.isGraphics) throw ProductionFrozenHistoryException("UNSUPPORTED_FORMAT")
        val keyEpoch = currentFrozenEpoch()
        val browseGeneration = synchronized(pendingHostHistoryLock) {
            val next = frozenBrowseGeneration.incrementAndGet()
            pendingHostHistory.entries.forEach { (requestId, pending) ->
                if (pending.frozen != null &&
                    pending.frozen.kind != HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE &&
                    pendingHostHistory.remove(requestId, pending)) {
                    pending.fail(ProductionFrozenHistoryException("STALE_CAPTURE"))
                }
            }
            next
        }
        val context = FrozenHistoryRequestContext(
            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN,
            snapshotSequence, ULong.MAX_VALUE, ByteString.EMPTY, 0uL, 0uL,
            maxLines, maxCells, maxUtf8Bytes, maxObjectBytes, browseGeneration,
            keyEpoch, FrozenHistoryCrypto.keyId(config.androidDeviceId, keyEpoch, profile.stateContract),
            profile.stateContract,
        )
        val value = try {
            requireNotNull(requestHostHistory(context.kind, snapshotSequence = snapshotSequence,
                beforeLineId = ULong.MAX_VALUE, frozen = context)) { "Frozen begin returned no page" }
        } catch (error: ProductionFrozenHistoryException) {
            val metadata = error.metadata
            if (metadata == null) throw error
            val capture = ProductionFrozenHistoryCapture(metadata.token, metadata.captureId.toULong(),
                metadata.captureRevision.toULong(), snapshotSequence, browseGeneration, config.sessionId,
                keyEpoch, profile.stateContract)
            withContext(NonCancellable) {
                withTimeoutOrNull(FROZEN_HISTORY_RELEASE_TIMEOUT_MILLIS) {
                    try { releaseFrozenHistory(capture) } catch (_: Exception) { /* Host lease expires if release fails. */ }
                }
            }
            throw ProductionFrozenHistoryException(error.code, error.requiredCells,
                error.requiredUtf8Bytes, capture, error.availableSnapshotSequence)
        }
        val metadata = requireNotNull(value.frozenHistory)
        val capture = ProductionFrozenHistoryCapture(metadata.token, metadata.captureId.toULong(),
            metadata.captureRevision.toULong(), snapshotSequence, browseGeneration, config.sessionId,
            keyEpoch, profile.stateContract)
        return ProductionFrozenHistoryPage(capture, value, maxLines, maxCells, maxUtf8Bytes)
    }

    suspend fun loadFrozenHistoryPage(
        capture: ProductionFrozenHistoryCapture,
        beforeLineId: ULong,
        maxLines: Int = 128,
        maxCells: Int = 65_536,
        maxUtf8Bytes: Int = 4 * 1024 * 1024,
        maxObjectBytes: Int = FrozenHistoryCrypto.MAX_PAGE_V2_BYTES + FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES,
    ): ProductionFrozenHistoryPage {
        validateFrozenCapture(capture)
        require(beforeLineId != 0uL) { "Frozen page anchor is invalid" }
        validateFrozenBudgets(maxLines, maxCells, maxUtf8Bytes)
        validateFrozenObjectBudget(maxObjectBytes)
        val context = FrozenHistoryRequestContext(
            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_PAGE,
            capture.snapshotSequence, beforeLineId, capture.token, capture.captureId, capture.revision,
            maxLines, maxCells, maxUtf8Bytes, maxObjectBytes, capture.browseGeneration,
            capture.keyEpoch,
            FrozenHistoryCrypto.keyId(config.androidDeviceId, capture.keyEpoch, capture.stateContract),
            capture.stateContract,
        )
        val value = requireNotNull(requestHostHistory(context.kind, snapshotSequence = capture.snapshotSequence,
            beforeLineId = beforeLineId, frozen = context)) { "Frozen page returned no page" }
        return ProductionFrozenHistoryPage(capture, value, maxLines, maxCells, maxUtf8Bytes)
    }

    suspend fun releaseFrozenHistory(capture: ProductionFrozenHistoryCapture) {
        require(capture.ownerSessionId == config.sessionId && capture.keyEpoch == currentFrozenEpoch() &&
            capture.token.size() == 32 && capture.captureId != 0uL &&
            capture.revision == capture.snapshotSequence + 1uL) { "Frozen capture identity is invalid" }
        synchronized(pendingHostHistoryLock) {
            if (frozenBrowseGeneration.compareAndSet(capture.browseGeneration, capture.browseGeneration + 1)) {
                pendingHostHistory.entries.forEach { (requestId, pending) ->
                    if (pending.frozen?.browseGeneration == capture.browseGeneration &&
                        pendingHostHistory.remove(requestId, pending)) {
                        pending.fail(ProductionFrozenHistoryException("STALE_CAPTURE"))
                    }
                }
            }
        }
        val context = FrozenHistoryRequestContext(
            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE,
            capture.snapshotSequence, 0uL, capture.token, capture.captureId, capture.revision,
            0, 0, 0, 0, capture.browseGeneration,
            capture.keyEpoch,
            FrozenHistoryCrypto.keyId(config.androidDeviceId, capture.keyEpoch, capture.stateContract),
            capture.stateContract,
        )
        requestHostHistory(context.kind, snapshotSequence = capture.snapshotSequence, frozen = context)
    }

    private fun validateFrozenCapture(capture: ProductionFrozenHistoryCapture) {
        require(capture.ownerSessionId == config.sessionId && capture.keyEpoch == currentFrozenEpoch() &&
            capture.token.size() == 32 && capture.captureId != 0uL &&
            capture.revision == capture.snapshotSequence + 1uL) { "Frozen capture identity is invalid" }
        if (capture.browseGeneration != frozenBrowseGeneration.get()) {
            throw ProductionFrozenHistoryException("STALE_CAPTURE")
        }
    }

    private fun validateFrozenBudgets(maxLines: Int, maxCells: Int, maxUtf8Bytes: Int) {
        require(maxLines in 1..128 && maxCells in 1..65_536 && maxUtf8Bytes in 1..4 * 1024 * 1024) {
            "Frozen history page budgets are invalid"
        }
    }

    private fun validateFrozenObjectBudget(maxObjectBytes: Int) {
        require(maxObjectBytes in 1..(FrozenHistoryCrypto.MAX_PAGE_V2_BYTES +
            FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES)) { "Frozen history object budget is invalid" }
    }

    /** Waits for the current subscriber's authenticated profile, including history-only bootstraps. */
    fun supportsRemoteFrozenHistory(): Boolean {
        val generation = transport.currentConnectedGeneration()
        return !closed.get() && !closing.get() && generation > 0 && subscriberReady.value == generation &&
            terminalAttachmentAccepted(generation) && frozenSupportedGeneration.get() == generation &&
            acceptedTerminalProfile.get()?.isGraphics == true
    }

    fun supportsRemoteStateDelta(): Boolean {
        val generation = transport.currentConnectedGeneration()
        val relay = transport.negotiatedCapabilities.value
        return !closed.get() && !closing.get() && generation > 0 && subscriberReady.value == generation &&
            terminalAttachmentAccepted(generation) && acceptedTerminalProfile.get()?.isGraphics == true &&
            deltaSupportedGeneration.get() == generation && relay.generation == generation &&
            HOST_HISTORY_RELAY_CAPABILITY in relay.capabilities
    }

    fun supportsRemoteBrokerCapture(): Boolean {
        val generation = transport.currentConnectedGeneration()
        val relay = transport.negotiatedCapabilities.value
        return !closed.get() && !closing.get() && generation > 0 && subscriberReady.value == generation &&
            terminalAttachmentAccepted(generation) && acceptedTerminalProfile.get()?.isGraphics == true &&
            brokerSupportedGeneration.get() == generation && relay.generation == generation &&
            BROKER_CAPTURE_CAPABILITY in relay.capabilities &&
            HOST_HISTORY_RELAY_CAPABILITY in relay.capabilities
    }

    suspend fun awaitTerminalProfile(): ProductionTerminalProfile {
        check(started.get()) { "production session client is not started" }
        check(!closed.get()) { "production session client is closed" }
        // withTimeoutOrNull returns null only for this wait's own deadline.
        // An ancestor timeout must remain cancellation, not become TL-H131.
        val profile = withTimeoutOrNull(terminationReadinessTimeoutMillis) {
            combine(mutableTransportState, subscriberReady) { transportState, readyGeneration ->
                if (transportState is ProductionTransportState.Failed) {
                    throw terminalCompatibilityFailure.get() ?: ProductionTransportException(transportState.reason)
                }
                if (transportState is ProductionTransportState.Closed) throw ProductionTransportException("production session client is closed")
                if (transportState is ProductionTransportState.Connected && readyGeneration > 0 &&
                    transport.currentConnectedGeneration() == readyGeneration && terminalAttachmentAccepted(readyGeneration)
                ) {
                    if (config.negotiateTerminalState) acceptedTerminalProfile.get() else ProductionTerminalProfile.LEGACY
                } else null
            }.first { it != null }!!
        }
        if (profile != null) return profile
        Log.w(REALTIME_LOG_TAG,
            "terminal_profile_wait_timeout connected_generation=${transport.currentConnectedGeneration()} " +
                "ready_generation=${subscriberReady.value} accepted_generation=${terminalAcceptedGeneration.get()} " +
                "transport_state=${mutableTransportState.value.diagnosticName()} " +
                "subscribe_count=${subscribeSendCount.get()} reconcile_active=${sessionReconcileJob?.isActive == true} " +
                "pending_attachments=${pendingTerminalAttachments.size} history_only=$hostHistoryOnlyMode")
        throw if (config.negotiateTerminalState) TerminalCompatibilityException("TERMINAL_NEGOTIATION_TIMEOUT")
            else ProductionTransportException("host history transport was not ready")
    }

    private suspend fun requestHostHistory(
        kind: HostHistoryRequestKindV1,
        snapshotSequence: ULong = 0u,
        beforeLineId: ULong = 0u,
        afterSequence: ULong = 0u,
        throughSequence: ULong = 0u,
        maxEvents: Int = 0,
        frozen: FrozenHistoryRequestContext? = null,
        delta: StateDeltaRequestContext? = null,
        broker: BrokerCaptureRequestContext? = null,
    ): ProductionHostHistoryObject? {
        val profile = awaitTerminalProfile()
        if (frozen != null && (!profile.isGraphics || frozen.stateContract != profile.stateContract)) {
            throw ProductionFrozenHistoryException("UNSUPPORTED_FORMAT")
        }
        if ((delta != null || broker != null) && !profile.isGraphics) {
            throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
        }
        if (broker != null && (broker.stateContract != profile.stateContract ||
            broker.keyId != BrokerCaptureCrypto.keyId(config.androidDeviceId, broker.keyEpoch,
                profile.stateContract))) {
            throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
        }

        val requestId = UUID.randomUUID()
        val generation = transport.currentConnectedGeneration()
        val pending = HostHistoryAccumulator(profile, terminalBufferBudget, generation, frozen, delta, broker)
        synchronized(pendingHostHistoryLock) {
            check(!closed.get() && !closing.get()) { "production session client is closed" }
            if (generation <= 0 || subscriberReady.value != generation ||
                transport.currentConnectedGeneration() != generation || !terminalAttachmentAccepted(generation)
            ) throw ProductionTransportException("host history transport was not ready")
            if (frozen != null) {
                if (frozen.keyEpoch != currentFrozenEpoch()) throw ProductionFrozenHistoryException("STALE_CAPTURE")
                if (frozenSupportedGeneration.get() != generation) throw ProductionFrozenHistoryException("UNSUPPORTED_FORMAT")
                if (frozen.kind != HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE &&
                    !isCurrentFrozenBrowse(frozen, frozenBrowseGeneration.get(), currentFrozenEpoch())) {
                    throw ProductionFrozenHistoryException("STALE_CAPTURE")
                }
            }
            if (delta != null) {
                val relay = transport.negotiatedCapabilities.value
                if (delta.keyEpoch != currentFrozenEpoch() || deltaSupportedGeneration.get() != generation ||
                    relay.generation != generation || HOST_HISTORY_RELAY_CAPABILITY !in relay.capabilities) {
                    throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
                }
                if (pendingHostHistory.values.any { it.delta != null }) {
                    throw IllegalStateException("A State Delta request is already pending")
                }
            }
            if (broker != null) {
                val relay = transport.negotiatedCapabilities.value
                if (broker.keyEpoch != currentFrozenEpoch() || brokerSupportedGeneration.get() != generation ||
                    relay.generation != generation || BROKER_CAPTURE_CAPABILITY !in relay.capabilities ||
                    HOST_HISTORY_RELAY_CAPABILITY !in relay.capabilities) {
                    throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
                }
                if (pendingHostHistory.values.any { it.broker != null }) {
                    throw IllegalStateException("A broker capture request is already pending")
                }
            }
            if (pendingHostHistory.size >= 4) throw TerminalBufferLimitException()
            check(pendingHostHistory.putIfAbsent(requestId, pending) == null) {
                "Host history request ID collision"
            }
        }

        var requestSent = false
        var requestCompleted = false
        try {
            val payload = HostHistoryRequestPayload.newBuilder()
                .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
                .setKind(kind)
                .setSnapshotSeq(snapshotSequence.toLong())
                .setBeforeLineId(beforeLineId.toLong())
                .setAfterSeq(afterSequence.toLong())
                .setThroughSeq(throughSequence.toLong())
                .setMaxEvents(maxEvents)
                .addAllSubscriberCapabilities(
                    (if (config.negotiateTerminalState) TERMINAL_SUBSCRIBER_CAPABILITIES else emptyList()) +
                        (if (frozen != null) listOf(FROZEN_HISTORY_CAPABILITY, FROZEN_HISTORY_V2_CAPABILITY) else emptyList())
                        + (if (delta != null) listOf(STATE_DELTA_CAPABILITY) else emptyList())
                )
                .apply {
                    if (frozen != null) setFrozenHistory(FrozenHistoryRequest.newBuilder()
                        .setToken(frozen.token)
                        .setCaptureId(frozen.captureId.toLong())
                        .setCaptureRevision(frozen.revision.toLong())
                        .setMaxLines(frozen.maxLines)
                        .setMaxCells(frozen.maxCells)
                        .setMaxUtf8Bytes(frozen.maxUtf8Bytes)
                        .setPageFormatVersion(FrozenHistoryCrypto.PAGE_FORMAT_V2))
                    if (delta != null) setStateDelta(StateDeltaRequestV1.newBuilder()
                        .setContextId(delta.contextId)
                        .setProfile(delta.profile)
                        .setBaseNextSourceSequence(delta.baseNextSequence.toLong())
                        .setBaseSnapshotBytes(delta.baseSnapshotBytes.toLong())
                        .setBaseSnapshotSha256(delta.baseSha256))
                    if (broker != null) setBrokerCapture(broker.selection)
                }
                .build()
                .toByteArray()
            sendEncrypted(MessageType.HOST_HISTORY_REQUEST, payload, expectedConnectedGeneration = generation)
            requestSent = true
            return try {
                val result = when {
                    !config.negotiateTerminalState -> withTimeout(hostHistoryRequestTimeoutMillis) { pending.results.receive() }
                    profile.isGraphics -> withTimeout(graphicsHostHistoryTotalTimeoutMillis) {
                        pending.awaitResult(hostHistoryRequestTimeoutMillis)
                    }
                    else -> pending.awaitResult(hostHistoryRequestTimeoutMillis)
                }
                val value = when (result) {
                    is HostHistoryRequestResult.Ready -> pending.takeCompleted(result.value)
                    HostHistoryRequestResult.Missing -> null
                    HostHistoryRequestResult.Released -> null
                }
                if (frozen != null && frozen.kind != HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE &&
                    !isCurrentFrozenBrowse(frozen, frozenBrowseGeneration.get(), currentFrozenEpoch())) {
                    value?.destroy()
                    throw ProductionFrozenHistoryException("STALE_CAPTURE")
                }
                if (delta != null && (delta.keyEpoch != currentFrozenEpoch() ||
                    deltaSupportedGeneration.get() != generation || transport.currentConnectedGeneration() != generation)) {
                    value?.destroy()
                    throw ProductionTransportException("State Delta request generation changed")
                }
                if (broker != null && (broker.keyEpoch != currentFrozenEpoch() ||
                    brokerSupportedGeneration.get() != generation || transport.currentConnectedGeneration() != generation)) {
                    value?.destroy()
                    throw ProductionTransportException("Broker capture request generation changed")
                }
                requestCompleted = true
                value
            } catch (_: TimeoutCancellationException) {
                throw ProductionTransportException("host history request timed out")
            }
        } finally {
            pendingHostHistory.remove(requestId, pending)
            pending.destroy()
            // A response failure may already have removed the accumulator. The
            // requester still owns cancelling its host task, once, on this generation.
            if (requestSent && !requestCompleted && !closed.get() &&
                transport.currentConnectedGeneration() == generation
            ) {
                withContext(NonCancellable) {
                    withTimeoutOrNull(HOST_HISTORY_CANCEL_TIMEOUT_MILLIS) {
                        runCatching {
                            sendHostHistoryControl(
                                requestId = requestId,
                                action = HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CANCEL,
                                expectedConnectedGeneration = generation,
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
        expectedConnectedGeneration: Int? = null,
    ) {
        val payload = HostHistoryControlPayload.newBuilder()
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
            .setAction(action)
            .setObjectId(objectId)
            .setChunkIndex(chunkIndex)
            .build()
            .toByteArray()
        sendEncrypted(MessageType.HOST_HISTORY_CONTROL, payload, expectedConnectedGeneration = expectedConnectedGeneration)
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
            val recovered = incomingProcessingMutex.withLock {
                check(!closed.get()) { "production session client is closed" }
                clearBrokerQueueLocked()
                observedLocalInputEpoch.accumulateAndGet(recoveredLocalInputEpoch, ::maxOf)
                session.recoverFromHistory(
                    events,
                    expectedGap,
                    persist = { expectedGeneration, recovered ->
                        withContext(NonCancellable) {
                            persistAcknowledgement(expectedGeneration, recovered)
                        }
                    },
                    publish = { recovered -> sendAcknowledgement(recovered) },
                )
            }
            val connectedGeneration = transport.currentConnectedGeneration()
            check(connectedGeneration > 0) { "realtime socket is not connected" }
            establishSubscriptionHeldThenOpen(recovered, connectedGeneration, releaseHistoryHold = true)
            recovered
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
                    clearBrokerQueueLocked()
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
                    frozenBrowseGeneration.incrementAndGet()
                    deltaSupportedGeneration.set(0)
                    brokerSupportedGeneration.set(0)
                    failPendingHostHistory(ProductionFrozenHistoryException("STALE_CAPTURE"))
                }
            }
            if (mutableTransportState.value is ProductionTransportState.Connected) {
                resume(session.state.value.lastAckedSequence)
                sendFlowControl(force = true)
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
        val requestId = UUID.randomUUID()
        val accepted = CompletableDeferred<ProductionTerminalProfile>()
        if (config.negotiateTerminalState) {
            try {
                ProductionTerminalStateContract.requireRelay(transport.negotiatedCapabilities.value, connectedGeneration)
            } catch (error: TerminalCompatibilityException) {
                failTerminalCompatibility(error)
                throw error
            }
            pendingTerminalAttachments[requestId] = PendingTerminalAttachment(connectedGeneration, accepted)
        }
        val payload = SubscribePayload.newBuilder()
            .setResumeFromSeq(lastAckedSequence)
            .setSubscriberExchangePublicKey(ByteString.copyFrom(identity.exchange.publicKey))
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(requestId)))
            .addAllSubscriberCapabilities(if (config.negotiateTerminalState) TERMINAL_SUBSCRIBER_CAPABILITIES else emptyList())
            .build()
            .toByteArray()
        try {
            sendEncrypted(
                MessageType.SUBSCRIBE,
                payload,
                acknowledgement = lastAckedSequence,
                expectedConnectedGeneration = connectedGeneration,
            )
            lastSubscribeCursor.set(lastAckedSequence)
            subscribeSendCount.incrementAndGet()
            if (config.negotiateTerminalState) {
                val confirmed = withTimeoutOrNull(terminationReadinessTimeoutMillis) { accepted.await(); true } ?: false
                check(transport.currentConnectedGeneration() == connectedGeneration) { "Terminal attachment generation changed" }
                if (!confirmed) throw TerminalCompatibilityException("TERMINAL_NEGOTIATION_TIMEOUT")
            }
        } catch (error: TerminalCompatibilityException) {
            failTerminalCompatibility(error)
            throw error
        } finally {
            pendingTerminalAttachments.remove(requestId)
        }
    }

    private fun failTerminalCompatibility(error: TerminalCompatibilityException) {
        if (!rendererFatal.compareAndSet(false, true)) return
        terminalCompatibilityFailure.set(error)
        clearBrokerQueueAfterFailure()
        cancelSessionReconcile()
        subscriberReady.value = 0
        frozenSupportedGeneration.set(0)
        deltaSupportedGeneration.set(0)
        brokerSupportedGeneration.set(0)
        failPendingHostHistory(error)
        subscriberRefreshJob?.cancel()
        subscriberRefreshJob = null
        failPendingInputs(error.message.orEmpty())
        semanticInputOutbox.clear()
        lastTransportFailureReason.set(error.message.orEmpty())
        mutableTransportState.value = ProductionTransportState.Failed(error.message.orEmpty())
        notifySessionDisconnected(error.message)
        controlScope.launch { retireRendererEndpoint() }
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
        val generation = transport.currentConnectedGeneration()
        if (!terminalAttachmentAccepted(generation)) return false
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
        return transport.sendRelayExactForConnectedGeneration(envelope, exactEnvelopeBytes, generation)
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
        if (!terminalAttachmentAccepted(transport.currentConnectedGeneration())) {
            throw RemoteInputRejectedException("Terminal attachment is not ready; retry after reconnection")
        }
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
        val generation = expectedConnectedGeneration ?: transport.currentConnectedGeneration()
        if (!terminalAttachmentAccepted(generation)) return false
        return if (expectedConnectedGeneration != null || config.negotiateTerminalState) {
            transport.sendRelayExactForConnectedGeneration(
                envelope,
                exactEnvelopeBytes,
                generation,
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

    /** Explicit user action, distinct from legacy automatic viewport notifications. No blind retries. */
    suspend fun requestWindowResize(metrics: dev.termlink.terminal.api.TerminalViewportMetrics?): TerminalSize {
        val controlling = session.state.value as? RemoteSessionState.Controlling
            ?: error("CONTROL_LEASE_REJECTED")
        check(started.get() && !closed.get() && subscriberReady.value > 0) { "SESSION_UNAVAILABLE" }
        metrics?.let {
            require(it.size.columns in 2..1000 && it.size.rows in 2..1000 &&
                it.cellWidthPx.isFinite() && it.cellHeightPx.isFinite() &&
                it.cellWidthPx > 0 && it.cellWidthPx <= 1024 && it.cellHeightPx > 0 && it.cellHeightPx <= 1024)
        }
        val id = UUID.randomUUID()
        val pending = CompletableDeferred<TerminalSize>()
        val builder = ResizePayload.newBuilder()
            .setRequestId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(id)))
            .setLeaseId(ByteString.copyFrom(leaseIdBytes(controlling.leaseId)))
            .setLeaseVersion(controlling.leaseVersion)
            .setObservedLocalInputEpoch(observedLocalInputEpoch.get())
            .setRestore(metrics == null)
            .setColumns(metrics?.size?.columns ?: 0).setRows(metrics?.size?.rows ?: 0)
            .setCellWidthPx(metrics?.cellWidthPx?.toInt()?.coerceAtLeast(1) ?: 0)
            .setCellHeightPx(metrics?.cellHeightPx?.toInt()?.coerceAtLeast(1) ?: 0)
        check(resizeInProgress.compareAndSet(false, true)) { "RESIZE_BUSY" }
        try {
            pendingResizes[id] = pending
            controlArbitrationMutex.withLock {
                check(session.state.value == controlling) { "CONTROL_LEASE_REJECTED" }
                check(started.get() && !closed.get() && subscriberReady.value > 0) { "SESSION_UNAVAILABLE" }
                val generation = transport.currentConnectedGeneration()
                val clock = hostClockSample.get()?.takeIf {
                    it.generation == generation && it.keyEpoch == currentFrozenEpoch()
                }
                // The correlated, authenticated attach reply supplies the PC clock; elapsed time includes device sleep.
                val now = clock?.let { it.hostMillis + (SystemClock.elapsedRealtime() - it.elapsedMillis).coerceAtLeast(0) }
                    ?: System.currentTimeMillis()
                val payload = builder.setExpiresAtMs(now + 10_000).build().toByteArray()
                try {
                    sendEncrypted(MessageType.RESIZE_REQUEST, payload, expectedConnectedGeneration = generation)
                } finally { payload.fill(0) }
            }
            return withTimeout(12_000) { pending.await() }
        } catch (_: TimeoutCancellationException) {
            throw IllegalStateException("RESIZE_TIMEOUT")
        } finally {
            pendingResizes.remove(id)
            resizeInProgress.set(false)
        }
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
        failPendingHostHistory(ProductionTransportException("production session closed during host history request"))
        frozenSupportedGeneration.set(0)
        deltaSupportedGeneration.set(0)
        brokerSupportedGeneration.set(0)
        frozenBrowseGeneration.incrementAndGet()
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
                clearBrokerQueueLocked()
                session.close()
            } finally {
                incomingProcessingMutex.unlock()
                cleanupScope.cancel()
            }
        } else {
            cleanupScope.launch {
                try {
                    incomingProcessingMutex.withLock {
                        clearBrokerQueueLocked()
                        session.close()
                    }
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
                val sendGeneration = expectedConnectedGeneration ?: transport.currentConnectedGeneration()
                if ((messageType == MessageType.INPUT_EVENT || messageType == MessageType.RESIZE_REQUEST) &&
                    !terminalAttachmentAccepted(sendGeneration)
                ) throw RemoteInputRejectedException("Terminal attachment is not ready; retry after reconnection")
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
        if (error is TerminalCompatibilityException) {
            failTerminalCompatibility(error)
            return
        }
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
        clearBrokerQueueAfterFailure()
        cancelSessionReconcile()
        subscriberReady.value = 0
        failPendingHostHistory(ProductionTransportException(reason))
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
        lastTransportFailureReason.set(reason)
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
                    terminalProtocolDiagnostic = diagnostics?.terminalProtocolDiagnostic,
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
            if (!closed.get() && started.get() && subscribeSendCount.get() > 0 &&
                transport.currentConnectedGeneration() > 0
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

    private fun terminalAttachmentAccepted(generation: Int): Boolean =
        !config.negotiateTerminalState ||
            (generation > 0 && terminalAcceptedGeneration.get() == generation && !rendererFatal.get())

    private suspend fun handleIncoming(context: IncomingProcessingContext) {
        val envelope = context.envelope
        check(!closed.get()) { "production session client is closed" }
        lastReceivedSequence.accumulateAndGet(envelope.seq, ::maxOf)
        context.stage = IncomingFailureStage.VALIDATE
        validateEnvelope(envelope)
        context.stage = IncomingFailureStage.KEY_LOOKUP
        val incomingKeyId = envelope.keyId.toByteArray()
        var keyEpoch = 0L
        val (key, usedCurrentMaterial) = try {
            synchronized(materialLock) {
                val material = when {
                    currentMaterial.keyId.contentEquals(incomingKeyId) -> currentMaterial
                    previousMaterial?.keyId?.contentEquals(incomingKeyId) == true -> previousMaterial
                    else -> null
                } ?: throw UnknownKeyMaterialException("Envelope key ID mismatch")
                keyEpoch = material.epoch
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
        var queued = false
        try {
            queued = queueBehindBrokerLocked(context, plaintext, usedCurrentMaterial, keyEpoch)
            if (!queued) handleAuthenticatedIncoming(context, plaintext, usedCurrentMaterial)
        } finally {
            if (!queued) plaintext.fill(0)
        }
    }

    /** Called only under incomingProcessingMutex; correlated control replies must bypass this queue. */
    private fun queueBehindBrokerLocked(
        context: IncomingProcessingContext,
        plaintext: ByteArray,
        usedCurrentMaterial: Boolean,
        keyEpoch: Long,
    ): Boolean {
        val type = context.envelope.messageType
        if (brokerDrainJob == null && type != MessageType.BROKER_OUTPUT) return false
        if (hostHistoryOnlyMode || type == MessageType.TERMINAL_ATTACH_RESULT ||
            type == MessageType.HOST_HISTORY_RESPONSE || type == MessageType.RESIZE_RESPONSE ||
            type == MessageType.SESSION_TERMINATE_RESPONSE) return false
        if (config.negotiateTerminalState && !terminalAttachmentAccepted(context.generation)) return false
        if (type == MessageType.BROKER_OUTPUT) {
            check(brokerCaptureResolver != null && supportsRemoteBrokerCapture() &&
                keyEpoch == currentFrozenEpoch()) { "Broker Output is not negotiated for this session" }
        }
        check(brokerPendingCount < MAX_BROKER_PENDING_EVENTS &&
            plaintext.size <= MAX_BROKER_PENDING_BYTES - brokerPendingBytes) {
            "Broker Output receive queue overflow"
        }
        val reservation = terminalBufferBudget.reserve(maxOf(plaintext.size, 1).toLong())
        try {
            brokerQueue.addLast(QueuedIncoming(context, plaintext, usedCurrentMaterial, keyEpoch,
                session.currentRendererGeneration, reservation))
            brokerPendingCount++
            brokerPendingBytes += plaintext.size
            if (brokerDrainJob == null) {
                val token = ++brokerDrainToken
                brokerDrainJob = sessionScope.launch { drainBrokerQueue(token) }
            }
        } catch (error: Throwable) {
            reservation.close()
            throw error
        }
        return true
    }

    /** Invalidates queued plaintext without touching an in-flight resolver's owned frame. */
    private fun clearBrokerQueueLocked() {
        brokerDrainToken++
        brokerDrainJob?.cancel()
        brokerDrainJob = null
        while (brokerQueue.isNotEmpty()) {
            val item = brokerQueue.removeFirst()
            brokerPendingCount--
            brokerPendingBytes -= item.plaintext.size
            item.close()
        }
    }

    private fun clearBrokerQueueAfterFailure() {
        if (incomingProcessingMutex.tryLock()) {
            try { clearBrokerQueueLocked() } finally { incomingProcessingMutex.unlock() }
        } else {
            cleanupScope.launch { incomingProcessingMutex.withLock { clearBrokerQueueLocked() } }
        }
    }

    private fun clearBrokerQueueForFailedGeneration(generation: Int, transition: Long) {
        if (generation <= 0) return
        val clearIfStillFailed = {
            if (transportTransitionGeneration.get() == transition &&
                activeIncomingGeneration == generation && subscriberReady.value != generation
            ) clearBrokerQueueLocked()
        }
        if (incomingProcessingMutex.tryLock()) {
            try { clearIfStillFailed() } finally { incomingProcessingMutex.unlock() }
        } else {
            cleanupScope.launch { incomingProcessingMutex.withLock { clearIfStillFailed() } }
        }
    }

    private fun clearBrokerQueueForTransportTransition(
        transition: Long,
        state: RealtimeState,
        connectedGeneration: Int,
    ) {
        val clearIfRetired = {
            if (transportTransitionGeneration.get() == transition &&
                (state != RealtimeState.Connected || connectedGeneration != activeIncomingGeneration)
            ) clearBrokerQueueLocked()
        }
        if (incomingProcessingMutex.tryLock()) {
            try { clearIfRetired() } finally { incomingProcessingMutex.unlock() }
        } else {
            cleanupScope.launch { incomingProcessingMutex.withLock { clearIfRetired() } }
        }
    }

    private fun queuedFrameIsCurrent(item: QueuedIncoming, token: Long): Boolean =
        token == brokerDrainToken && !closed.get() && !closing.get() && !rendererFatal.get() &&
            item.context.generation == activeIncomingGeneration &&
            transport.currentConnectedGeneration() == item.context.generation &&
            subscriberReady.value == item.context.generation &&
            item.keyEpoch == currentFrozenEpoch() &&
            session.currentRendererGeneration == item.rendererGeneration &&
            terminalAttachmentAccepted(item.context.generation)

    private suspend fun drainBrokerQueue(token: Long) {
        while (true) {
            val item = incomingProcessingMutex.withLock {
                if (token != brokerDrainToken) return@withLock null
                if (brokerQueue.isEmpty()) {
                    brokerDrainJob = null
                    return@withLock null
                }
                brokerQueue.removeFirst()
            } ?: return
            var resolved: RemoteSessionEvent.BrokerOutput? = null
            try {
                if (item.context.envelope.messageType == MessageType.BROKER_OUTPUT) {
                    val reference = parseIncomingPayload(item.context) {
                        BrokerOutputPayload.parseFrom(item.plaintext).referenceV1.toByteArray()
                    }
                    try {
                        resolved = requireNotNull(brokerCaptureResolver)(item.context.envelope.seq, reference)
                    } finally {
                        reference.fill(0)
                    }
                }
                incomingProcessingMutex.withLock {
                    if (!queuedFrameIsCurrent(item, token)) {
                        if (token == brokerDrainToken) clearBrokerQueueLocked()
                        return@withLock
                    }
                    val event = resolved
                    if (event != null) {
                        require(event.sequence == item.context.envelope.seq) {
                            "Broker Output resolver returned another sequence"
                        }
                        item.context.stage = IncomingFailureStage.ENGINE_ACCEPT
                        requireApplied(acceptOrdered(item.context, item.context.envelope, event,
                            item.rendererGeneration))
                        lastHandledSequence.accumulateAndGet(event.sequence, ::maxOf)
                    } else {
                        handleAuthenticatedIncoming(item.context, item.plaintext, item.usedCurrentMaterial)
                    }
                }
            } catch (error: Exception) {
                val failure = withContext(NonCancellable) {
                    incomingProcessingMutex.withLock {
                        if (token != brokerDrainToken || closed.get()) return@withLock null
                        rejectedThroughGeneration.accumulateAndGet(item.context.generation, ::maxOf)
                        clearBrokerQueueLocked()
                        IncomingFailure(item.context, error)
                    }
                }
                if (failure != null) handleIncomingFailure(failure)
                return
            } finally {
                resolved?.close()
                item.close()
                withContext(NonCancellable) {
                    incomingProcessingMutex.withLock {
                        brokerPendingCount--
                        brokerPendingBytes -= item.plaintext.size
                    }
                }
            }
        }
    }

    private suspend fun handleAuthenticatedIncoming(
        context: IncomingProcessingContext,
        plaintext: ByteArray,
        usedCurrentMaterial: Boolean,
    ) {
        val envelope = context.envelope
        context.stage = IncomingFailureStage.SEMANTIC
            if (config.negotiateTerminalState && terminalAcceptedGeneration.get() != context.generation &&
                envelope.messageType !in setOf(MessageType.TERMINAL_ATTACH_RESULT, MessageType.HOST_HISTORY_RESPONSE,
                    MessageType.SESSION_TERMINATE_RESPONSE)
            ) {
                // The PC may retry retained ciphertext before it sees our new
                // SUBSCRIBE. Never parse/apply/ACK it until attachment is confirmed;
                // the unchanged durable cursor asks for the same data again.
                return
            }
            if (hostHistoryOnlyMode &&
                envelope.messageType != MessageType.TERMINAL_ATTACH_RESULT &&
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
                MessageType.TERMINAL_ATTACH_RESULT -> {
                    val result = parseIncomingPayload(context) {
                        dev.termlink.protocol.realtime.v1.TerminalAttachResultPayload.parseFrom(plaintext)
                    }
                    val requestId = ProductionRealtimeClient.uuidFromBytes(result.requestId.toByteArray()) ?: return
                    val pending = pendingTerminalAttachments[requestId] ?: return
                    if (!usedCurrentMaterial || pending.generation != context.generation ||
                        transport.currentConnectedGeneration() != context.generation) return
                    if (!pendingTerminalAttachments.remove(requestId, pending)) return
                    try {
                        val profile = ProductionTerminalStateContract.requireAccepted(result)
                        if (config.expectedTerminalState != null && profile.state != config.expectedTerminalState) {
                            throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
                        }
                        acceptedTerminalProfile.compareAndSet(null, profile)
                        if (acceptedTerminalProfile.get() != profile) throw TerminalCompatibilityException("TERMINAL_STATE_MISMATCH")
                        frozenSupportedGeneration.set(if (supportsFrozenHistory(profile, result.hostCapabilitiesList))
                            context.generation else 0)
                        deltaSupportedGeneration.set(if (supportsStateDelta(profile, result.hostCapabilitiesList))
                            context.generation else 0)
                        brokerSupportedGeneration.set(if (supportsBrokerCapture(profile, result.hostCapabilitiesList))
                            context.generation else 0)
                        terminalAcceptedGeneration.set(context.generation)
                        if (envelope.sentAtMs in 1..(Long.MAX_VALUE - 15_000)) {
                            hostClockSample.set(HostClockSample(context.generation, currentFrozenEpoch(),
                                envelope.sentAtMs, SystemClock.elapsedRealtime()))
                        }
                        pending.result.complete(profile)
                    } catch (error: TerminalCompatibilityException) {
                        failTerminalCompatibility(error)
                        pending.result.completeExceptionally(error)
                    }
                    return // Correlated result never advances terminal sequence/ACK.
                }
                MessageType.RESIZE_RESPONSE -> {
                    val response = parseIncomingPayload(context) { ResizeResponsePayload.parseFrom(plaintext) }
                    val id = ProductionRealtimeClient.uuidFromBytes(response.requestId.toByteArray())
                    require(response.errorCode.length <= 128) { "Resize error code is too long" }
                    if (id != null) pendingResizes[id]?.let { pending ->
                        if (response.errorCode.isNotEmpty()) pending.completeExceptionally(IllegalStateException(response.errorCode))
                        else {
                            require(response.columns in 2..1000 && response.rows in 2..1000) { "Invalid resize result" }
                            pending.complete(TerminalSize(response.columns, response.rows))
                        }
                    }
                    return // Control results do not advance terminal sequence/ACK cursors.
                }
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
                MessageType.HOST_RESIZE_RESULT -> {
                    val result = parseIncomingPayload(context) {
                        dev.termlink.terminal.api.TerminalHostResizeResultV1.decode(
                            dev.termlink.protocol.realtime.v1.HostResizeResultPayload.parseFrom(plaintext).resultV1.toByteArray(),
                        )
                    }
                    requireApplied(acceptOrdered(context, envelope, RemoteSessionEvent.HostResizeResult(envelope.seq, result)))
                }
                MessageType.HOST_THEME -> {
                    val theme = parseIncomingPayload(context) {
                        dev.termlink.terminal.api.TerminalHostThemeV1.decode(
                            dev.termlink.protocol.realtime.v1.HostThemePayload.parseFrom(plaintext).themeV1.toByteArray(),
                        )
                    }
                    requireApplied(acceptOrdered(context, envelope, RemoteSessionEvent.HostTheme(envelope.seq, theme)))
                }
                MessageType.HOST_GEOMETRY -> {
                    val geometry = parseIncomingPayload(context) {
                        dev.termlink.terminal.api.TerminalHostGeometryV1.decode(
                            dev.termlink.protocol.realtime.v1.HostGeometryPayload.parseFrom(plaintext).geometryV1.toByteArray(),
                        )
                    }
                    requireApplied(acceptOrdered(context, envelope, RemoteSessionEvent.HostGeometry(envelope.seq, geometry)))
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
                                dev.termlink.terminal.api.TerminalProtocolDiagnostic.forHostExit(
                                    payload.exitCode.takeIf { payload.hasExitCode() },
                                    payload.terminalProtocolDiagnostic.takeIf { payload.hasTerminalProtocolDiagnostic() },
                                ),
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
                MessageType.BROKER_OUTPUT -> error("Broker Output was not resolved before ordered dispatch")
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
        if (pending.generation != context.generation) return
        if (pending.delta != null && pending.delta.keyEpoch != currentFrozenEpoch()) return
        if (pending.broker != null && pending.broker.keyEpoch != currentFrozenEpoch()) return
        if (pending.frozen != null && pending.frozen.kind != HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE &&
            !isCurrentFrozenBrowse(pending.frozen, frozenBrowseGeneration.get(), currentFrozenEpoch())) return
        try {
            when (response.status) {
                HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK -> {
                    if (pending.frozen?.kind == HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_RELEASE) {
                        if (pending.acceptRelease(response)) pending.deliver(HostHistoryRequestResult.Released)
                        return
                    }
                    val accepted = pending.accept(response) ?: return
                    if (accepted.newlyAccepted) hostHistoryChunkReceivedCount.incrementAndGet()
                    else hostHistoryChunkDuplicateCount.incrementAndGet()
                    try {
                        sendHostHistoryControl(
                            requestId = requestId,
                            action = HostHistoryControlActionV1.HOST_HISTORY_CONTROL_ACTION_V1_CHUNK_ACK,
                            objectId = accepted.objectId,
                            chunkIndex = accepted.chunkIndex,
                            expectedConnectedGeneration = context.generation,
                        )
                        hostHistoryChunkAckSentCount.incrementAndGet()
                        pending.markChunkAckSent()
                    } catch (error: Throwable) {
                        accepted.completed?.destroy()
                        throw error
                    }
                    val completed = accepted.completed ?: return
                    completed.transferEvidence = pending.transferEvidence(requestId)
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
                    if (pending.delta != null) {
                        failHostHistoryRequest(requestId, pending,
                            IllegalStateException("State Delta request was unexpectedly not found"))
                        return
                    }
                    if (pending.broker != null) {
                        failHostHistoryRequest(requestId, pending,
                            IllegalStateException("Broker capture is missing"))
                        return
                    }
                    if (pending.frozen != null) {
                        failHostHistoryRequest(requestId, pending, ProductionFrozenHistoryException("STALE_CAPTURE"))
                        return
                    }
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
                    validateEmptyHostHistoryResponse(response,
                        allowSnapshotSequence = pending.frozen?.kind ==
                            HostHistoryRequestKindV1.HOST_HISTORY_REQUEST_KIND_V1_FROZEN_BEGIN &&
                            response.errorCode == "ANCHOR_CHANGED" ||
                            pending.delta != null && response.errorCode == "NEEDS_FULL_SNAPSHOT")
                    pending.broker?.let {
                        require(!response.hasBrokerCapture() && !response.hasStateDelta() &&
                            !response.hasFrozenHistory() && response.snapshotSeq == 0L) {
                            "Broker capture rejection contains unexpected metadata"
                        }
                        failHostHistoryRequest(requestId, pending,
                            IllegalStateException(response.errorCode.ifBlank { "Broker capture request was rejected" }))
                        return
                    }
                    pending.delta?.let { delta ->
                        if (response.errorCode == "NEEDS_FULL_SNAPSHOT") {
                            delta.validateNeedsFullSnapshot(response)
                            failHostHistoryRequest(requestId, pending,
                                ProductionStateDeltaNeedsFullSnapshotException())
                        } else {
                            require(!response.hasStateDelta() && !response.hasFrozenHistory() &&
                                response.snapshotSeq == 0L) { "State Delta rejection contains unexpected metadata" }
                            failHostHistoryRequest(requestId, pending,
                                IllegalStateException(response.errorCode.ifBlank { "State Delta request was rejected" }))
                        }
                        return
                    }
                    pending.frozen?.let { frozen ->
                        val availableSnapshotSequence = frozen.validateError(response)
                        val metadata = response.frozenHistory
                        failHostHistoryRequest(requestId, pending, ProductionFrozenHistoryException(
                            response.errorCode.ifBlank { "UNSUPPORTED_FORMAT" },
                            if (response.hasFrozenHistory()) metadata.requiredCells.toULong() else 0uL,
                            if (response.hasFrozenHistory()) metadata.requiredUtf8Bytes.toULong() else 0uL,
                            availableSnapshotSequence = availableSnapshotSequence,
                            metadata = if (response.hasFrozenHistory()) metadata else null,
                        ))
                        return
                    }
                    failHostHistoryRequest(
                        requestId,
                        pending,
                        if (response.errorCode.startsWith("TERMINAL_")) TerminalCompatibilityException(response.errorCode)
                        else IllegalStateException(response.errorCode.ifBlank { "Host history request was rejected" }),
                    )
                }
                else -> error("Host history response status is invalid")
            }
        } catch (error: Throwable) {
            failHostHistoryRequest(requestId, pending, error)
            if (error !is Exception) throw error
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

    private fun validateEmptyHostHistoryResponse(
        response: HostHistoryResponsePayload,
        allowSnapshotSequence: Boolean = false,
    ) {
        require(response.errorCode.length <= 128) { "Host history error code is too long" }
        require(
            response.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_UNSPECIFIED &&
                (allowSnapshotSequence || response.snapshotSeq == 0L) &&
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

    private fun failPendingHostHistory(error: Throwable, exceptGeneration: Int? = null) {
        synchronized(pendingHostHistoryLock) {
            pendingHostHistory.entries.forEach { (requestId, pending) ->
                if (pending.generation != exceptGeneration && pendingHostHistory.remove(requestId, pending)) {
                    pending.fail(error)
                }
            }
        }
    }

    private suspend fun acceptOrdered(
        context: IncomingProcessingContext,
        envelope: Envelope,
        event: RemoteSessionEvent,
        expectedRendererGeneration: Long? = null,
    ): EventAcceptance {
        context.stage = IncomingFailureStage.ENGINE_ACCEPT
        val stateBefore = session.state.value
        val acceptance = if (expectedRendererGeneration == null) session.accept(event)
            else session.accept(event, expectedRendererGeneration)
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
                    dev.termlink.terminal.api.TerminalProtocolDiagnostic.forHostExit(
                        payload.exitCode.takeIf { payload.hasExitCode() },
                        payload.terminalProtocolDiagnostic.takeIf { payload.hasTerminalProtocolDiagnostic() },
                    ),
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
        if (state != RealtimeState.Connected) subscriberReady.value = 0
        clearBrokerQueueForTransportTransition(transition, state, event.connectedGeneration)
        if (state != RealtimeState.Connected) {
            failPendingHostHistory(ProductionTransportException("host history transport disconnected"))
            frozenSupportedGeneration.set(0)
            deltaSupportedGeneration.set(0)
            brokerSupportedGeneration.set(0)
        }
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
                failPendingHostHistory(ProductionTransportException("host history transport generation changed"), connectedGeneration)
                cancelPendingReconnect()
                mutableTransportState.value = ProductionTransportState.Connected
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
                        resendPendingSemanticInputsOverRelay(connectedGeneration)
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
                lastTransportFailureReason.set(state.reason)
                cancelReconnectAttemptReset()
                subscriberRefreshJob?.cancel()
                subscriberRefreshJob = null
                subscriberReady.value = 0
                failPendingInputs(state.reason)
                scheduleReconnect(state.reason, transition)
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
                lastTransportFailureReason.set(state.reason)
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
        Log.w(REALTIME_LOG_TAG, "session_reconcile_scheduled reason=$reason generation=$connectedGeneration renderer_fatal=${rendererFatal.get()} accepted_generation=${terminalAcceptedGeneration.get()} pending_attachments=${pendingTerminalAttachments.size}")
        if (!isEligibleSessionReconcile(connectedGeneration)) return
        subscriberReady.compareAndSet(connectedGeneration, 0)
        clearBrokerQueueForFailedGeneration(connectedGeneration, transportTransitionGeneration.get())
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
            } catch (error: Exception) {
                Log.w(REALTIME_LOG_TAG, "session_reconcile_attempt_failed attempt=$attempt exception=${error::class.java.simpleName} message=${error.message}")
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
            failTransport(reason, connectedGeneration)
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

    private suspend fun scheduleReconnect(reason: String?, expectedTransition: Long) {
        reconnectMutex.withLock {
            if (closed.get() || closing.get() || rendererFatal.get() ||
                session.state.value is RemoteSessionState.Exited ||
                transportTransitionGeneration.get() != expectedTransition ||
                transport.currentConnectedGeneration() > 0
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
                    if (!closed.get() && !closing.get() && !rendererFatal.get() &&
                        transportTransitionGeneration.get() == expectedTransition &&
                        transport.currentConnectedGeneration() <= 0
                    ) transport.connect()
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
        pendingResizes.values.forEach { it.completeExceptionally(IllegalStateException("RESIZE_INTERRUPTED")) }
        pendingResizes.clear()
        pendingInputs.values.forEach { it.completeExceptionally(RemoteInputDeliveryUnknownException(reason)) }
        pendingInputs.clear()
    }

    private fun failTransport(reason: String, failedGeneration: Int) {
        if (closed.get()) return
        val transition = transportTransitionGeneration.get()
        // The socket was already lost when this fallback was selected. A successor
        // may connect before we reach this callback; never disconnect that socket.
        if (transport.currentConnectedGeneration() > 0) return
        publishTransportFailure(reason, failedGeneration, transition)
    }

    private fun publishTransportFailure(reason: String, failedGeneration: Int, transition: Long) {
        lastTransportFailureReason.set(reason)
        if (rendererFatal.get() || transportTransitionGeneration.get() != transition ||
            transport.currentConnectedGeneration() > 0) return
        subscriberReady.compareAndSet(failedGeneration, 0)
        clearBrokerQueueForFailedGeneration(failedGeneration, transition)
        failPendingHostHistory(ProductionTransportException("host history transport disconnected"))
        if (transportTransitionGeneration.get() != transition ||
            transport.currentConnectedGeneration() > 0) return
        mutableTransportState.value = ProductionTransportState.Failed(reason)
        failPendingInputs(reason)
        controlScope.launch { scheduleReconnect(reason, transition) }
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

    private class QueuedIncoming(
        val context: IncomingProcessingContext,
        val plaintext: ByteArray,
        val usedCurrentMaterial: Boolean,
        val keyEpoch: Long,
        val rendererGeneration: Long,
        private val reservation: TerminalBufferBudget.Reservation,
    ) : AutoCloseable {
        override fun close() {
            plaintext.fill(0)
            reservation.close()
        }
    }

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

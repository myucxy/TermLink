package dev.termlink.history

import androidx.room.withTransaction
import dev.termlink.database.AckCursorEntity
import dev.termlink.database.BrokerCaptureEntity
import dev.termlink.database.BrokerCaptureReadMetadata
import dev.termlink.database.DeviceEntity
import dev.termlink.database.EventChunkEntity
import dev.termlink.database.HistoryPreferenceEntity
import dev.termlink.database.PcEntity
import dev.termlink.database.RendererCheckpointEntity
import dev.termlink.database.SessionEntity
import dev.termlink.database.SnapshotEntity
import dev.termlink.database.SnapshotReadMetadata
import dev.termlink.database.TermLinkDatabase
import dev.termlink.database.TranscriptPageEntity
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.isPcAgent
import dev.termlink.model.HistoryObject
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.model.TerminalBufferBudget
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

/** A decoded bounded event. The payload is transient and is never a Room value. */
data class HistoryReplayEvent(
    val sequence: ULong,
    val payload: ByteArray,
) {
    init {
        require(sequence > 0u) { "event sequence must be positive" }
    }

    fun copyPayload(): HistoryReplayEvent = copy(payload = payload.copyOf())

    override fun equals(other: Any?): Boolean =
        other is HistoryReplayEvent && sequence == other.sequence && payload.contentEquals(other.payload)

    override fun hashCode(): Int = 31 * sequence.hashCode() + payload.contentHashCode()
}

/** Durable cumulative ACK plus the renderer generation it is safe to resume. */
data class AckCursorState(
    val sequence: ULong,
    val rendererGeneration: Long,
) {
    init {
        require(rendererGeneration > 0) { "renderer generation must be positive" }
    }
}

/** Cached encrypted snapshot plus the durable renderer generation it anchors. */
class RendererCheckpointState(
    val snapshot: EncryptedHistoryObject,
    val snapshotRows: Int,
    val snapshotColumns: Int,
    val snapshotSequence: ULong,
    /**
     * The renderer may advertise this cursor only after restoring the snapshot
     * and replaying every ordered event through this sequence.
     */
    val durableAcknowledgement: ULong,
    val rendererGeneration: Long,
) : AutoCloseable {
    init {
        require(snapshot.metadata.kind == HistoryObject.Kind.SNAPSHOT) { "renderer checkpoint must reference a snapshot" }
        require(snapshot.metadata.snapshotSequence == snapshotSequence) { "renderer checkpoint sequence mismatch" }
        require(durableAcknowledgement >= snapshotSequence) { "renderer ACK precedes its checkpoint" }
        require(rendererGeneration > 0) { "renderer generation must be positive" }
    }

    override fun close() = snapshot.destroy()
}

/** Owns its ciphertext and keeps snapshot memory admitted until [destroy] or [close]. */
class EncryptedHistoryObject internal constructor(
    metadata: HistoryObject,
    ciphertext: ByteArray,
    private val bufferBudget: TerminalBufferBudget,
    reservation: TerminalBufferBudget.Reservation?,
) : AutoCloseable {
    constructor(
        metadata: HistoryObject,
        ciphertext: ByteArray,
        bufferBudget: TerminalBufferBudget = TerminalBufferBudget.global,
    ) : this(metadata, ciphertext, bufferBudget, null)

    val metadata: HistoryObject
    val ciphertext: ByteArray
    private var reservation = reservation
    private var destroyed = false

    init {
        var ownedBytes: ByteArray? = null
        try {
            require(metadata.sizeBytes == ciphertext.size.toLong()) {
                "ciphertext size does not match history metadata"
            }
            require(metadata.ciphertextSha256.size == HistoryRepository.SHA256_BYTES) {
                "history ciphertext hash must be SHA-256"
            }
            this.metadata = metadata.copy(ciphertextSha256 = metadata.ciphertextSha256.copyOf())
            if (metadata.kind == HistoryObject.Kind.SNAPSHOT && this.reservation == null) {
                this.reservation = bufferBudget.reserve(ciphertext.size.toLong())
            }
            ownedBytes = ciphertext.copyOf()
            require(MessageDigest.isEqual(metadata.ciphertextSha256, HistoryRepository.sha256(ownedBytes))) {
                "history ciphertext hash mismatch"
            }
            this.ciphertext = ownedBytes
        } catch (error: Exception) {
            ownedBytes?.fill(0)
            this.reservation?.close()
            throw error
        }
    }

    @Synchronized
    fun copyCiphertext(): EncryptedHistoryObject {
        check(!destroyed) { "history object has been destroyed" }
        return EncryptedHistoryObject(metadata, ciphertext, bufferBudget)
    }

    @Synchronized
    fun destroy() {
        if (destroyed) return
        destroyed = true
        ciphertext.fill(0)
        reservation?.close()
        reservation = null
    }

    override fun close() = destroy()
}

data class EncryptedPreference(
    val scope: String,
    val key: String,
    val keyId: String,
    val ciphertextSha256: ByteArray,
    val ciphertext: ByteArray,
) {
    init {
        require(ciphertextSha256.size == HistoryRepository.SHA256_BYTES) { "preference hash must be SHA-256" }
        require(ciphertextSha256.contentEquals(HistoryRepository.sha256(ciphertext))) {
            "preference ciphertext hash mismatch"
        }
    }

    fun copyCiphertext(): EncryptedPreference = copy(ciphertext = ciphertext.copyOf())
}

/** Authenticated transcript selector metadata; row plaintext is never persisted here. */
data class TranscriptPageMetadata(
    val id: UUID,
    val sessionId: UUID,
    val snapshotSequence: ULong,
    val requestBeforeLineId: ULong,
    val firstLineId: ULong,
    val lastLineId: ULong,
    val objectKey: String,
    val keyId: String,
    val ciphertextSha256: ByteArray,
    val sizeBytes: Long,
    val formatName: String,
    val formatVersion: Int,
) {
    init {
        require(snapshotSequence > 0u) { "transcript snapshot anchor must be positive" }
        require(firstLineId > 0u && lastLineId >= firstLineId) { "transcript line range is invalid" }
        require(lastLineId < requestBeforeLineId) { "transcript line range is not before its exclusive cursor" }
        require(ciphertextSha256.size == HistoryRepository.SHA256_BYTES) { "transcript hash must be SHA-256" }
        require(sizeBytes in 1..HistoryRepository.MAX_TERMINAL_TRANSCRIPT_BYTES) {
            "transcript ciphertext size is out of bounds"
        }
        require(formatName == HistoryRepository.TERMINAL_TRANSCRIPT_FORMAT && formatVersion == 1) {
            "transcript format is unsupported"
        }
    }

    fun copyDigest(): TranscriptPageMetadata = copy(ciphertextSha256 = ciphertextSha256.copyOf())
}

/** A cached transcript object remains ciphertext until the product explicitly opens a page. */
data class EncryptedTranscriptPage(
    val metadata: TranscriptPageMetadata,
    val ciphertext: ByteArray,
) {
    init {
        require(metadata.sizeBytes == ciphertext.size.toLong()) { "transcript ciphertext size mismatch" }
        require(MessageDigest.isEqual(metadata.ciphertextSha256, HistoryRepository.sha256(ciphertext))) {
            "transcript ciphertext hash mismatch"
        }
    }

    fun copyCiphertext(): EncryptedTranscriptPage = copy(
        metadata = metadata.copyDigest(),
        ciphertext = ciphertext.copyOf(),
    )
}

data class HistorySearchHit(
    val objectId: UUID,
    val sequence: ULong?,
    val preview: String,
)

/** Ciphertext cache cardinality only; no object metadata or content leaves this diagnostic. */
data class HistoryObjectCounts(
    val chunks: Int,
    val snapshots: Int,
    val transcripts: Int,
)

class HistoryIntegrityException(message: String) : IllegalStateException(message)

class HistoryGapException(
    val expectedSequence: ULong,
    val receivedSequence: ULong,
) : IllegalStateException("history gap: expected $expectedSequence, received $receivedSequence")

/** Non-sensitive index metadata for one authenticated TLBCSEA1 capture. */
data class BrokerCaptureMetadata(
    val sessionId: UUID,
    val captureId: UUID,
    val keyEpoch: Long,
    val sourceSequence: ULong,
    val canonicalBytes: Int,
    val canonicalSha256: ByteArray,
    val keyId: String,
    val sizeBytes: Long,
    val ciphertextSha256: ByteArray,
    val createdAtEpochMillis: Long,
) {
    init {
        require(keyEpoch > 0) { "broker capture key epoch must be positive" }
        require(sourceSequence > 0uL && sourceSequence < Long.MAX_VALUE.toULong()) {
            "broker capture source sequence is invalid"
        }
        require(canonicalBytes.toLong() in HistoryRepository.BROKER_CAPTURE_MINIMUM_CANONICAL_BYTES..
            HistoryRepository.MAX_BROKER_CAPTURE_CANONICAL_BYTES) {
            "broker capture canonical length is invalid"
        }
        require(canonicalSha256.size == HistoryRepository.SHA256_BYTES) {
            "broker capture canonical hash must be SHA-256"
        }
        require(keyId.isNotBlank()) { "broker capture key id is required" }
        require(sizeBytes == canonicalBytes.toLong() + HistoryRepository.BROKER_CAPTURE_SEALED_OVERHEAD_BYTES) {
            "broker capture sealed size is invalid"
        }
        require(ciphertextSha256.size == HistoryRepository.SHA256_BYTES) {
            "broker capture ciphertext hash must be SHA-256"
        }
    }
}

/** Owns a sealed capture and its bounded heap admission until [close]. */
class EncryptedBrokerCapture internal constructor(
    metadata: BrokerCaptureMetadata,
    ciphertext: ByteArray,
    private val bufferBudget: TerminalBufferBudget,
    reservation: TerminalBufferBudget.Reservation,
) : AutoCloseable {
    val metadata: BrokerCaptureMetadata = metadata.copy(
        canonicalSha256 = metadata.canonicalSha256.copyOf(),
        ciphertextSha256 = metadata.ciphertextSha256.copyOf(),
    )
    val ciphertext: ByteArray = ciphertext
    private var reservation: TerminalBufferBudget.Reservation? = reservation
    private var destroyed = false

    init {
        require(metadata.sizeBytes == ciphertext.size.toLong()) {
            "broker capture ciphertext size does not match metadata"
        }
    }

    @Synchronized
    fun copyCiphertext(): EncryptedBrokerCapture {
        check(!destroyed) { "broker capture has been destroyed" }
        val nextReservation = bufferBudget.reserve(ciphertext.size.toLong())
        return try {
            EncryptedBrokerCapture(metadata, ciphertext.copyOf(), bufferBudget, nextReservation)
        } catch (error: Throwable) {
            nextReservation.close()
            throw error
        }
    }

    @Synchronized
    fun destroy() {
        if (destroyed) return
        destroyed = true
        ciphertext.fill(0)
        reservation?.close()
        reservation = null
    }

    /** Transfers this object's admission permit to a later owner. */
    @Synchronized
    fun takeReservation(): TerminalBufferBudget.Reservation {
        check(!destroyed) { "broker capture has been destroyed" }
        return checkNotNull(reservation) { "broker capture reservation was already transferred" }
            .also { reservation = null }
    }

    override fun close() = destroy()
}

/**
 * Persistence boundary for local history. The repository accepts ciphertext, never
 * plaintext terminal bytes. Decryption is an explicit callback used only by replay
 * and search, so no plaintext can accidentally enter a DAO or a database transaction.
 */
class HistoryRepository(
    private val database: TermLinkDatabase,
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val bufferBudget: TerminalBufferBudget = TerminalBufferBudget.global,
) {
    private val devices get() = database.devices()
    private val pcs get() = database.pcs()
    private val sessions get() = database.sessions()
    private val chunks get() = database.eventChunks()
    private val snapshots get() = database.snapshots()
    private val transcriptPages get() = database.transcriptPages()
    private val acks get() = database.ackCursors()
    private val rendererCheckpoints get() = database.rendererCheckpoints()
    private val preferences get() = database.preferences()
    private val brokerCaptures get() = database.brokerCaptures()

    suspend fun upsertDevice(summary: DeviceSummary) {
        requireValidDeviceKey(summary)
        database.withTransaction {
            val stored = devices.get(summary.id.requireUuidString())
            if (stored?.exchangePublicKey?.isNotEmpty() == true) {
                require(summary.exchangePublicKey.contentEquals(stored.exchangePublicKey)) {
                    "device exchange key changed without a new device identity"
                }
            }
            devices.upsert(deviceEntity(summary))
        }
    }

    suspend fun upsertPc(summary: PcSummary) {
        require(summary.keyEpoch > 0) { "key epoch must be positive" }
        database.withTransaction {
            val stored = pcs.get(summary.id.requireUuidString())
            if (stored != null) {
                val incomingDeviceId = summary.deviceId.requireUuidString()
                if (stored.deviceId == incomingDeviceId) {
                    require(summary.keyEpoch >= stored.keyEpoch) { "key epoch must not regress" }
                } else {
                    require(summary.keyEpoch > stored.keyEpoch) {
                        "replacement PC identity must advance the key epoch"
                    }
                }
            }
            pcs.upsert(pcEntity(summary))
        }
    }

    /**
     * Validates and atomically persists the authoritative PC epoch together
     * with the exchange public key it authenticates. A live client may rotate
     * only after this transaction commits.
     */
    suspend fun persistAuthoritativeKeyMetadata(
        deviceSummaries: List<DeviceSummary>,
        pcSummaries: List<PcSummary>,
    ) {
        require(deviceSummaries.map(DeviceSummary::id).toSet().size == deviceSummaries.size) {
            "authoritative device metadata contains duplicate IDs"
        }
        require(pcSummaries.map(PcSummary::id).toSet().size == pcSummaries.size) {
            "authoritative PC metadata contains duplicate IDs"
        }
        val nextDevices = deviceSummaries.associateBy(DeviceSummary::id)
        deviceSummaries.forEach(::requireValidDeviceKey)
        pcSummaries.forEach { summary ->
            require(summary.keyEpoch > 0) { "key epoch must be positive" }
            val nextDevice = requireNotNull(nextDevices[summary.deviceId]) {
                "authoritative PC metadata is missing its device key"
            }
            require(nextDevice.type.isPcAgent()) { "PC metadata references a non-PC device" }
        }
        database.withTransaction {
            deviceSummaries.forEach { summary ->
                val storedDevice = devices.get(summary.id.requireUuidString()) ?: return@forEach
                // Database v1 -> v2 used an empty, unusable sentinel. Permit
                // exactly one authenticated initialization; a real device key
                // is immutable for the lifetime of its device ID.
                if (storedDevice.exchangePublicKey.isNotEmpty()) {
                    require(summary.exchangePublicKey.contentEquals(storedDevice.exchangePublicKey)) {
                        "device exchange key changed without a new device identity"
                    }
                }
            }
            pcSummaries.forEach { summary ->
                val storedPc = pcs.get(summary.id.requireUuidString()) ?: return@forEach
                val incomingDeviceId = summary.deviceId.requireUuidString()
                if (storedPc.deviceId == incomingDeviceId) {
                    require(summary.keyEpoch >= storedPc.keyEpoch) { "key epoch must not regress" }
                } else {
                    // The Server keeps one monotonically increasing PC epoch
                    // across device-key replacement. Requiring a strict
                    // advance makes a delayed pre-rebind catalog unable to
                    // restore the revoked device binding.
                    require(summary.keyEpoch > storedPc.keyEpoch) {
                        "replacement PC identity must advance the key epoch"
                    }
                }
            }
            devices.upsertAll(deviceSummaries.map(::deviceEntity))
            pcs.upsertAll(pcSummaries.map(::pcEntity))
        }
    }

    suspend fun upsertSession(summary: SessionSummary) {
        require(summary.columns in 1..65535) { "columns must be between 1 and 65535" }
        require(summary.rows in 1..65535) { "rows must be between 1 and 65535" }
        require(summary.lastOutputSequence <= Long.MAX_VALUE.toULong()) {
            "last output sequence is too large for Room"
        }
        val incoming = SessionEntity(
                id = summary.id.requireUuidString(),
                pcId = summary.pcId.requireUuidString(),
                shellType = summary.shellType,
                encryptedTitle = summary.encryptedTitle?.copyOf(),
                state = summary.state.name,
                columns = summary.columns,
                rows = summary.rows,
                lastOutputSequence = summary.lastOutputSequence.toLong(),
                createdAtEpochMillis = summary.createdAt.toEpochMilli(),
                endedAtEpochMillis = summary.endedAt?.toEpochMilli(),
            )
        database.withTransaction {
            val stored = sessions.get(incoming.id)
            if (stored == null) {
                sessions.upsert(incoming)
            } else {
                require(stored.pcId == incoming.pcId) { "session PC identity must not change" }
                require(stored.shellType == incoming.shellType) { "session shell type must not change" }
                val storedExited = stored.state == SessionState.EXITED.name
                val incomingExited = incoming.state == SessionState.EXITED.name
                sessions.upsert(
                    incoming.copy(
                        state = if (storedExited) stored.state else incoming.state,
                        lastOutputSequence = maxOf(stored.lastOutputSequence, incoming.lastOutputSequence),
                        createdAtEpochMillis = stored.createdAtEpochMillis,
                        endedAtEpochMillis = when {
                            storedExited -> stored.endedAtEpochMillis ?: incoming.endedAtEpochMillis
                            incomingExited -> incoming.endedAtEpochMillis ?: stored.endedAtEpochMillis
                            else -> incoming.endedAtEpochMillis
                        },
                    ),
                )
            }
        }
    }

    suspend fun devices(): List<DeviceSummary> = devices.list().map { entity ->
        DeviceSummary(
            id = entity.id.toUuid(),
            type = entity.type.toEnumOrThrow<DeviceType>("device type"),
            name = entity.name,
            keyId = entity.keyId,
            exchangePublicKey = entity.exchangePublicKey.copyOf(),
            trustState = entity.trustState.toEnumOrThrow<DeviceTrustState>("trust state"),
            createdAt = Instant.ofEpochMilli(entity.createdAtEpochMillis),
        )
    }

    suspend fun pcs(): List<PcSummary> = pcs.list().map { entity ->
        PcSummary(
            id = entity.id.toUuid(),
            deviceId = entity.deviceId.toUuid(),
            name = entity.name,
            agentVersion = entity.agentVersion,
            keyEpoch = entity.keyEpoch,
            remoteRoutingEnabled = entity.remoteRoutingEnabled,
            online = entity.online,
            lastSeenAt = entity.lastSeenAtEpochMillis?.let(Instant::ofEpochMilli),
            platform = entity.platform,
            architecture = entity.architecture,
            platformVersion = entity.platformVersion,
            capabilities = entity.capabilitiesCsv.split(',').filter(String::isNotBlank).toSet(),
        )
    }

    suspend fun sessions(pcId: UUID? = null): List<SessionSummary> =
        (pcId?.let { sessions.listForPc(it.requireUuidString()) } ?: sessions.list()).map { entity ->
            SessionSummary(
                id = entity.id.toUuid(),
                pcId = entity.pcId.toUuid(),
                shellType = entity.shellType,
                encryptedTitle = entity.encryptedTitle?.copyOf(),
                state = entity.state.toEnumOrThrow<SessionState>("session state"),
                columns = entity.columns,
                rows = entity.rows,
                lastOutputSequence = entity.lastOutputSequence.toULongChecked("last output sequence"),
                createdAt = Instant.ofEpochMilli(entity.createdAtEpochMillis),
                endedAt = entity.endedAtEpochMillis?.let(Instant::ofEpochMilli),
            )
        }

    suspend fun session(sessionId: UUID): SessionSummary? = sessions.get(sessionId.requireUuidString())?.let { entity ->
        SessionSummary(
            id = entity.id.toUuid(),
            pcId = entity.pcId.toUuid(),
            shellType = entity.shellType,
            encryptedTitle = entity.encryptedTitle?.copyOf(),
            state = entity.state.toEnumOrThrow<SessionState>("session state"),
            columns = entity.columns,
            rows = entity.rows,
            lastOutputSequence = entity.lastOutputSequence.toULongChecked("last output sequence"),
            createdAt = Instant.ofEpochMilli(entity.createdAtEpochMillis),
            endedAt = entity.endedAtEpochMillis?.let(Instant::ofEpochMilli),
        )
    }

    /** Stores an already encrypted event chunk and computes its integrity hash. */
    suspend fun putEventChunk(
        sessionId: UUID,
        startSequence: ULong,
        endSequence: ULong,
        objectKey: String,
        keyId: String,
        ciphertext: ByteArray,
        id: UUID = UUID.randomUUID(),
        expiresAt: Instant? = null,
        ciphertextSha256: ByteArray? = null,
    ): UUID {
        val entity = eventChunkEntity(
            id = id,
            sessionId = sessionId,
            startSequence = startSequence,
            endSequence = endSequence,
            objectKey = objectKey,
            keyId = keyId,
            ciphertext = ciphertext,
            expiresAt = expiresAt,
        )
        ciphertextSha256?.let { supplied ->
            if (!MessageDigest.isEqual(supplied, entity.ciphertextSha256)) {
                throw HistoryIntegrityException("event chunk ciphertext hash mismatch before persistence")
            }
        }
        database.withTransaction {
            val existing = eventChunkIdentityMatches(entity)
            if (existing.isNotEmpty()) {
                existing.forEach { requireSameEventChunk(it, entity) }
            } else if (chunks.insertIfAbsent(entity) == -1L) {
                val raced = eventChunkIdentityMatches(entity)
                check(raced.isNotEmpty()) { "event chunk identity conflict could not be resolved" }
                raced.forEach { requireSameEventChunk(it, entity) }
            }
        }
        return id
    }

    /** Compatibility alias for callers which call encrypted chunks history objects. */
    suspend fun putChunk(
        sessionId: UUID,
        startSequence: ULong,
        endSequence: ULong,
        objectKey: String,
        keyId: String,
        ciphertext: ByteArray,
        id: UUID = UUID.randomUUID(),
        expiresAt: Instant? = null,
        ciphertextSha256: ByteArray? = null,
    ): UUID = putEventChunk(
        sessionId,
        startSequence,
        endSequence,
        objectKey,
        keyId,
        ciphertext,
        id,
        expiresAt,
        ciphertextSha256,
    )

    suspend fun putSnapshot(
        sessionId: UUID,
        snapshotSequence: ULong,
        rows: Int,
        columns: Int,
        objectKey: String,
        keyId: String,
        formatName: String = BOUNDED_EVENT_REPLAY_FORMAT,
        formatVersion: Int = 1,
        ciphertext: ByteArray,
        id: UUID = UUID.randomUUID(),
        expiresAt: Instant? = null,
        ciphertextSha256: ByteArray? = null,
    ): UUID {
        require(rows in 1..65535) { "rows must be between 1 and 65535" }
        require(columns in 1..65535) { "columns must be between 1 and 65535" }
        require(formatName.isNotBlank()) { "snapshot format name is required" }
        require(formatVersion > 0) { "snapshot format version must be positive" }
        val sequence = snapshotSequence.toRoomSequence("snapshot sequence")
        require(ciphertext.isNotEmpty()) { "snapshot ciphertext must not be empty" }
        knownSnapshotMaximumBytes(formatName, formatVersion)?.let { maximum ->
            require(ciphertext.size.toLong() <= maximum) { "snapshot ciphertext size is out of bounds" }
        }
        val reservation = bufferBudget.reserve(ciphertext.size.toLong())
        var bytes: ByteArray? = null
        try {
            val ownedBytes = ciphertext.copyOf()
            bytes = ownedBytes
            val digest = sha256(ownedBytes)
            ciphertextSha256?.let { supplied ->
                if (!MessageDigest.isEqual(supplied, digest)) {
                    throw HistoryIntegrityException("snapshot ciphertext hash mismatch before persistence")
                }
            }
            val entity = SnapshotEntity(
                id = id.requireUuidString(),
                sessionId = sessionId.requireUuidString(),
                snapshotSequence = sequence,
                rows = rows,
                columns = columns,
                objectKey = objectKey.requireNonBlank("snapshot object key"),
                keyId = keyId.requireNonBlank("snapshot key id"),
                ciphertextSha256 = digest,
                sizeBytes = ownedBytes.size.toLong(),
                ciphertext = ownedBytes,
                formatName = formatName,
                formatVersion = formatVersion,
                createdAtEpochMillis = clockMillis(),
                expiresAtEpochMillis = expiresAt?.toEpochMilli(),
            )
            database.withTransaction {
                val existingMetadata = snapshots.readMetadata(entity.id)
                if (existingMetadata == null) {
                    check(snapshots.insertIfAbsent(entity) != -1L) {
                        "snapshot identity conflicts with an existing immutable object"
                    }
                } else {
                    requireSnapshotReadSize(existingMetadata)
                    bufferBudget.reserve(existingMetadata.ciphertextBytes).use {
                        val existing = requireNotNull(snapshots.get(entity.id))
                        try {
                            requireSameSnapshot(existing, entity)
                        } finally {
                            existing.ciphertext.fill(0)
                        }
                    }
                }
            }
            return id
        } finally {
            bytes?.fill(0)
            reservation.close()
        }
    }

    /**
     * Stores one immutable TLBCSEA1 capture.  The database only receives the
     * sealed container; the caller remains responsible for opening its PC and
     * recipient identities with BrokerCaptureCrypto.
     *
     * A repeated key is idempotent: an existing sealed object wins, even when a
     * retry supplied a fresh nonce.  The unique source index rejects a second
     * capture UUID for the same ordered terminal event and key epoch.
     */
    suspend fun putBrokerCapture(
        sessionId: UUID,
        captureId: UUID,
        sourceSequence: ULong,
        keyEpoch: Long,
        canonicalBytes: Int,
        canonicalSha256: ByteArray,
        keyId: String,
        ciphertext: ByteArray,
        ciphertextSha256: ByteArray? = null,
        admittedBy: TerminalBufferBudget.Reservation? = null,
    ): EncryptedBrokerCapture {
        val expected = brokerCaptureArguments(
            sessionId,
            captureId,
            sourceSequence,
            keyEpoch,
            canonicalBytes,
            canonicalSha256,
            keyId,
            ciphertext,
        )
        val digest = sha256(ciphertext)
        ciphertextSha256?.let { supplied ->
            if (!MessageDigest.isEqual(supplied, digest)) {
                throw HistoryIntegrityException("broker capture ciphertext hash mismatch before persistence")
            }
        }

        // Admission precedes the owned copy and is released before reading an
        // already cached retry, so a duplicate cannot transiently double-budget.
        val reservation = admittedBy?.transferTo(bufferBudget, ciphertext.size.toLong())
            ?: bufferBudget.reserve(ciphertext.size.toLong())
        var persistedBytes: ByteArray? = null
        var transferred = false
        try {
            val owned = ciphertext.copyOf()
            persistedBytes = owned
            val entity = BrokerCaptureEntity(
                sessionId = sessionId.requireUuidString(),
                captureId = captureId.requireUuidString(),
                keyEpoch = keyEpoch,
                sourceSequence = sourceSequence.toLong(),
                canonicalBytes = canonicalBytes,
                canonicalSha256 = canonicalSha256.copyOf(),
                keyId = keyId,
                sizeBytes = ciphertext.size.toLong(),
                ciphertextSha256 = digest,
                ciphertext = owned,
                createdAtEpochMillis = clockMillis(),
            )
            val existing = database.withTransaction {
                val safeSessionId = entity.sessionId
                val source = brokerCaptures.readMetadataForSource(safeSessionId, entity.sourceSequence, keyEpoch)
                if (source != null) {
                    require(source.captureId == entity.captureId) {
                        "broker capture source sequence is already bound to another capture"
                    }
                }
                val cached = brokerCaptures.readMetadata(safeSessionId, entity.captureId, keyEpoch)
                if (cached == null) {
                    check(brokerCaptures.insertIfAbsent(entity) != -1L) {
                        "broker capture identity conflict could not be resolved"
                    }
                    null
                } else {
                    requireSameBrokerCapture(cached, expected)
                    cached
                }
            }
            if (existing != null) {
                persistedBytes?.fill(0)
                persistedBytes = null
                reservation.close()
                transferred = true
                return requireNotNull(readBrokerCapture(existing))
            }
            // Room has bound the SQLite value; keep only the returned owned copy.
            val returned = EncryptedBrokerCapture(
                metadata = expected.copy(ciphertextSha256 = digest.copyOf(), createdAtEpochMillis = entity.createdAtEpochMillis),
                ciphertext = ciphertext.copyOf(),
                bufferBudget = bufferBudget,
                reservation = reservation,
            )
            transferred = true
            return returned
        } finally {
            persistedBytes?.fill(0)
            if (!transferred) reservation.close()
        }
    }

    /** Loads one exact session/capture/epoch object without materializing a Room BLOB. */
    suspend fun getBrokerCapture(
        sessionId: UUID,
        captureId: UUID,
        keyEpoch: Long,
    ): EncryptedBrokerCapture? {
        require(keyEpoch > 0) { "broker capture key epoch must be positive" }
        val metadata = brokerCaptures.readMetadata(
            sessionId.requireUuidString(),
            captureId.requireUuidString(),
            keyEpoch,
        ) ?: return null
        return readBrokerCapture(metadata)
    }

    private suspend fun readBrokerCapture(metadata: BrokerCaptureReadMetadata): EncryptedBrokerCapture {
        val expected = try {
            brokerCaptureMetadata(metadata)
        } catch (error: IllegalArgumentException) {
            throw HistoryIntegrityException("broker capture metadata is invalid: ${error.message}")
        }
        if (metadata.ciphertextBytes != metadata.sizeBytes) {
            throw HistoryIntegrityException("broker capture ciphertext length metadata is inconsistent")
        }
        if (metadata.ciphertextBytes !in 1L..MAX_BROKER_CAPTURE_OBJECT_BYTES ||
            metadata.ciphertextBytes > Int.MAX_VALUE
        ) {
            throw HistoryIntegrityException("broker capture ciphertext size is out of bounds")
        }
        val reservation = bufferBudget.reserve(metadata.ciphertextBytes)
        var bytes: ByteArray? = null
        var transferred = false
        try {
            bytes = ByteArray(metadata.ciphertextBytes.toInt())
            val ownedBytes = requireNotNull(bytes)
            var offset = 0
            while (offset < ownedBytes.size) {
                val length = minOf(BROKER_CAPTURE_CHUNK_BYTES, ownedBytes.size - offset)
                val chunk = brokerCaptures.readCiphertextChunk(
                    metadata.sessionId,
                    metadata.captureId,
                    metadata.keyEpoch,
                    offset,
                    length,
                ) ?: throw HistoryIntegrityException("broker capture disappeared during read")
                try {
                    require(chunk.size == length) { "broker capture chunk length is inconsistent" }
                    chunk.copyInto(ownedBytes, offset)
                } finally {
                    chunk.fill(0)
                }
                offset += length
            }
            if (!MessageDigest.isEqual(sha256(ownedBytes), metadata.ciphertextSha256)) {
                throw HistoryIntegrityException("broker capture ciphertext hash mismatch")
            }
            try {
                validateBrokerCaptureEnvelope(
                    ownedBytes,
                    UUID.fromString(metadata.sessionId),
                    UUID.fromString(metadata.captureId),
                    metadata.sourceSequence.toULongChecked("broker capture source sequence"),
                    metadata.keyEpoch,
                    metadata.canonicalBytes,
                    metadata.canonicalSha256,
                )
            } catch (error: IllegalArgumentException) {
                throw HistoryIntegrityException("broker capture sealed header is invalid: ${error.message}")
            }
            val result = EncryptedBrokerCapture(expected, ownedBytes, bufferBudget, reservation)
            transferred = true
            return result
        } finally {
            if (!transferred) {
                bytes?.fill(0)
                reservation.close()
            }
        }
    }

    suspend fun listHistory(sessionId: UUID): List<EncryptedHistoryObject> = readHistoryTransaction { retained ->
        val safeSessionId = sessionId.requireUuidString()
        val snapshotMetadata = snapshots.listReadMetadata(safeSessionId)
        for (metadata in snapshotMetadata) retained += readSnapshot(metadata)
        for (id in chunks.listIdsForSession(safeSessionId)) {
            val row = requireNotNull(chunks.get(id))
            try {
                retained += chunkObject(row)
            } finally {
                row.ciphertext.fill(0)
            }
        }
        retained.sortedWith(
            compareBy<EncryptedHistoryObject> {
                it.metadata.startSequence ?: it.metadata.snapshotSequence ?: 0u
            }.thenBy { it.metadata.id.toString() },
        )
    }

    suspend fun latestSnapshot(sessionId: UUID, atOrBefore: ULong): EncryptedHistoryObject? {
        val sequence = atOrBefore.toRoomSequence("snapshot lookup sequence")
        return readHistoryTransaction { retained ->
            snapshots.latestReadMetadata(sessionId.requireUuidString(), sequence)?.let {
                readSnapshot(it).also(retained::add)
            }
        }
    }

    suspend fun getHistoryObject(objectId: UUID): EncryptedHistoryObject? = readHistoryTransaction { retained ->
        chunks.get(objectId.requireUuidString())?.let { row ->
            try {
                chunkObject(row).also(retained::add)
            } finally {
                row.ciphertext.fill(0)
            }
        } ?: snapshots.readMetadata(objectId.requireUuidString())?.let {
            readSnapshot(it).also(retained::add)
        }
    }

    // Failed reads (including transaction commit/cancellation) must not leave any caller-owned permits.
    private suspend fun <T> readHistoryTransaction(block: suspend (MutableList<EncryptedHistoryObject>) -> T): T {
        val retained = mutableListOf<EncryptedHistoryObject>()
        var completed = false
        try {
            val result = database.withTransaction { block(retained) }
            completed = true
            return result
        } finally {
            if (!completed) retained.forEach(EncryptedHistoryObject::destroy)
        }
    }

    /**
     * Loads exactly one encrypted chunk containing the next replay cursor.
     * This deliberately never materializes the session catalog, so callers can
     * resume from a Snapshot inside a Chunk and still replay an arbitrary
     * number of later chunks with a one-chunk memory working set.
     */
    suspend fun eventChunkContaining(sessionId: UUID, sequence: ULong): EncryptedHistoryObject? {
        val cursor = sequence.toPositiveRoomSequence("event chunk replay cursor")
        val rows = chunks.listContaining(sessionId.requireUuidString(), cursor)
        try {
            require(rows.size <= 1) { "overlapping event chunks contain sequence $sequence" }
            return rows.singleOrNull()?.let { row ->
                verifyHash(row.ciphertext, row.ciphertextSha256, "event chunk ${row.id}", row.sizeBytes)
                chunkObject(row)
            }
        } finally {
            rows.forEach { it.ciphertext.fill(0) }
        }
    }

    suspend fun putTranscriptPage(
        sessionId: UUID,
        snapshotSequence: ULong,
        requestBeforeLineId: ULong,
        firstLineId: ULong,
        lastLineId: ULong,
        objectKey: String,
        keyId: String,
        ciphertext: ByteArray,
        id: UUID = UUID.randomUUID(),
        expiresAt: Instant? = null,
        ciphertextSha256: ByteArray? = null,
    ): UUID {
        val anchor = snapshotSequence.toPositiveRoomSequence("transcript snapshot anchor")
        val before = requestBeforeLineId.toPositiveRoomSequence("transcript request cursor")
        val first = firstLineId.toPositiveRoomSequence("transcript first line ID")
        val last = lastLineId.toPositiveRoomSequence("transcript last line ID")
        require(last in first until before) { "transcript line range is not before its exclusive cursor" }
        val bytes = ciphertext.copyOf()
        require(bytes.isNotEmpty() && bytes.size.toLong() <= MAX_TERMINAL_TRANSCRIPT_BYTES) {
            "transcript ciphertext size is out of bounds"
        }
        val digest = sha256(bytes)
        ciphertextSha256?.let { supplied ->
            if (!MessageDigest.isEqual(supplied, digest)) {
                throw HistoryIntegrityException("transcript ciphertext hash mismatch before persistence")
            }
        }
        val entity = TranscriptPageEntity(
            id = id.requireUuidString(),
            sessionId = sessionId.requireUuidString(),
            snapshotSequence = anchor,
            requestBeforeLineId = before,
            firstLineId = first,
            lastLineId = last,
            objectKey = objectKey.requireNonBlank("transcript object key"),
            keyId = keyId.requireNonBlank("transcript key id"),
            ciphertextSha256 = digest,
            sizeBytes = bytes.size.toLong(),
            ciphertext = bytes,
            formatName = TERMINAL_TRANSCRIPT_FORMAT,
            formatVersion = 1,
            createdAtEpochMillis = clockMillis(),
            expiresAtEpochMillis = expiresAt?.toEpochMilli(),
        )
        database.withTransaction {
            val existing = transcriptPages.get(entity.id)
                ?: transcriptPages.getForCursor(entity.sessionId, anchor, before)
                ?: transcriptPages.getByObjectKey(entity.objectKey)
            if (existing != null) {
                requireSameTranscriptPage(existing, entity)
            } else if (transcriptPages.insertIfAbsent(entity) == -1L) {
                val raced = transcriptPages.get(entity.id)
                    ?: transcriptPages.getForCursor(entity.sessionId, anchor, before)
                    ?: transcriptPages.getByObjectKey(entity.objectKey)
                    ?: error("transcript identity conflict could not be resolved")
                requireSameTranscriptPage(raced, entity)
            }
        }
        return id
    }

    suspend fun transcriptPageForCursor(
        sessionId: UUID,
        snapshotSequence: ULong,
        requestBeforeLineId: ULong,
    ): EncryptedTranscriptPage? = transcriptPages.getForCursor(
        sessionId.requireUuidString(),
        snapshotSequence.toPositiveRoomSequence("transcript snapshot anchor"),
        requestBeforeLineId.toPositiveRoomSequence("transcript request cursor"),
    )?.let { transcriptPage(it).copyCiphertext() }

    suspend fun transcriptPages(sessionId: UUID, snapshotSequence: ULong): List<EncryptedTranscriptPage> =
        transcriptPages.listForAnchor(
            sessionId.requireUuidString(),
            snapshotSequence.toPositiveRoomSequence("transcript snapshot anchor"),
        ).map { transcriptPage(it).copyCiphertext() }

    suspend fun historyObjectCounts(sessionId: UUID): HistoryObjectCounts {
        val id = sessionId.requireUuidString()
        return HistoryObjectCounts(
            chunks = chunks.countForSession(id),
            snapshots = snapshots.countForSession(id),
            transcripts = transcriptPages.countForSession(id),
        )
    }

    suspend fun initializeAckCursor(sessionId: UUID, direction: String): AckCursorState {
        val safeSessionId = sessionId.requireUuidString()
        val safeDirection = direction.requireNonBlank("ACK direction")
        return acks.getOrCreate(
            AckCursorEntity(
                sessionId = safeSessionId,
                direction = safeDirection,
                rendererGeneration = 1,
                highestContiguousSequence = 0,
                updatedAtEpochMillis = clockMillis(),
            ),
        ).toAckCursorState()
    }

    /** Stores a cumulative ACK only for the renderer generation that applied it. */
    suspend fun putAckCursor(
        sessionId: UUID,
        direction: String,
        rendererGeneration: Long,
        sequence: ULong,
    ): ULong {
        require(rendererGeneration > 0) { "renderer generation must be positive" }
        val safeDirection = direction.requireNonBlank("ACK direction")
        val requested = sequence.toRoomSequence("ACK sequence")
        val safeSessionId = sessionId.requireUuidString()
        val updatedAt = clockMillis()
        return database.withTransaction {
            check(
                acks.advance(
                    AckCursorEntity(
                        sessionId = safeSessionId,
                        direction = safeDirection,
                        rendererGeneration = rendererGeneration,
                        highestContiguousSequence = requested,
                        updatedAtEpochMillis = updatedAt,
                    ),
                ),
            ) { "ACK renderer generation is stale" }
            val persisted = requireNotNull(acks.get(safeSessionId, safeDirection)) {
                "ACK cursor was not persisted"
            }
            check(persisted.rendererGeneration == rendererGeneration) {
                "ACK renderer generation changed during commit"
            }
            // Keep the last verified snapshot binding after the live ACK advances.
            // A restart may use it only after verifying and replaying the contiguous
            // suffix through this durable cursor; ACKs are cumulative and never regress.
            persisted.highestContiguousSequence.toULongChecked("ACK sequence")
        }
    }

    suspend fun ackCursorState(sessionId: UUID, direction: String): AckCursorState? =
        acks.get(sessionId.requireUuidString(), direction.requireNonBlank("ACK direction"))
            ?.toAckCursorState()

    suspend fun ackCursors(sessionId: UUID): Map<String, ULong> =
        acks.listForSession(sessionId.requireUuidString()).associate {
            it.direction to it.highestContiguousSequence.toULongChecked("ACK sequence")
        }

    /**
     * Atomically fences the old renderer and binds ACK [snapshotSequence] to
     * one exact cached encrypted snapshot. Legacy callers must provide a
     * snapshot at or after the durable ACK; Host History uses the explicit
     * [bindHostRendererCheckpoint] operation below when it has rendered a
     * suffix from an older snapshot.
     */
    suspend fun bindRendererCheckpoint(
        sessionId: UUID,
        direction: String,
        expectedRendererGeneration: Long,
        snapshotId: UUID,
        snapshotSequence: ULong,
        ciphertextSha256: ByteArray,
    ): RendererCheckpointState = bindRendererCheckpointInternal(
        sessionId = sessionId,
        direction = direction,
        expectedRendererGeneration = expectedRendererGeneration,
        snapshotId = snapshotId,
        snapshotSequence = snapshotSequence,
        ciphertextSha256 = ciphertextSha256,
        renderedThroughSequence = snapshotSequence,
    )

    /**
     * Binds a Host Snapshot anchor [snapshotSequence] after its renderer has
     * applied the contiguous suffix through [renderedThroughSequence]. The
     * operation is intentionally separate from the legacy bind API: it allows
     * an older immutable anchor, but can never regress the existing durable
     * ACK and fences the old renderer in the same Room transaction.
     */
    suspend fun bindHostRendererCheckpoint(
        sessionId: UUID,
        direction: String,
        expectedRendererGeneration: Long,
        snapshotId: UUID,
        snapshotSequence: ULong,
        renderedThroughSequence: ULong,
        ciphertextSha256: ByteArray,
    ): RendererCheckpointState {
        require(renderedThroughSequence > 0u) { "rendered cursor must be positive" }
        require(snapshotSequence <= renderedThroughSequence) {
            "renderer cursor must reach the snapshot anchor"
        }
        return bindRendererCheckpointInternal(
            sessionId = sessionId,
            direction = direction,
            expectedRendererGeneration = expectedRendererGeneration,
            snapshotId = snapshotId,
            snapshotSequence = snapshotSequence,
            ciphertextSha256 = ciphertextSha256,
            renderedThroughSequence = renderedThroughSequence,
        )
    }

    private suspend fun bindRendererCheckpointInternal(
        sessionId: UUID,
        direction: String,
        expectedRendererGeneration: Long,
        snapshotId: UUID,
        snapshotSequence: ULong,
        ciphertextSha256: ByteArray,
        renderedThroughSequence: ULong,
    ): RendererCheckpointState {
        require(expectedRendererGeneration > 0) { "renderer generation must be positive" }
        require(expectedRendererGeneration < Long.MAX_VALUE) { "renderer generation is exhausted" }
        require(snapshotSequence > 0u) { "snapshot sequence must be positive" }
        require(ciphertextSha256.size == SHA256_BYTES) { "snapshot ciphertext hash must be SHA-256" }
        val safeSessionId = sessionId.requireUuidString()
        val safeDirection = direction.requireNonBlank("ACK direction")
        val safeSnapshotId = snapshotId.requireUuidString()
        val sequence = snapshotSequence.toRoomSequence("snapshot sequence")
        val rendererCursor = renderedThroughSequence.toRoomSequence("rendered cursor")
        val expectedDigest = ciphertextSha256.copyOf()
        return readHistoryTransaction { retained ->
            val metadata = requireNotNull(snapshots.readMetadata(safeSnapshotId)) {
                "renderer checkpoint snapshot is not cached"
            }
            require(metadata.sessionId == safeSessionId) { "renderer checkpoint snapshot belongs to another session" }
            require(metadata.snapshotSequence == sequence) { "renderer checkpoint snapshot sequence mismatch" }
            val maximumSnapshotBytes = requireNotNull(
                knownSnapshotMaximumBytes(metadata.formatName, metadata.formatVersion),
            ) {
                "renderer checkpoint uses an unsupported snapshot format"
            }
            require(metadata.sizeBytes == metadata.ciphertextBytes &&
                metadata.sizeBytes in 1..maximumSnapshotBytes &&
                metadata.ciphertextBytes in 1..maximumSnapshotBytes
            ) {
                "renderer checkpoint snapshot size is out of bounds"
            }
            require(metadata.rows in 1..MAX_TERMINAL_SNAPSHOT_ROWS) {
                "renderer checkpoint snapshot rows are out of bounds"
            }
            require(metadata.columns in 1..MAX_TERMINAL_SNAPSHOT_COLUMNS) {
                "renderer checkpoint snapshot columns are out of bounds"
            }
            val snapshot = readSnapshot(metadata).also(retained::add)
            require(MessageDigest.isEqual(snapshot.metadata.ciphertextSha256, expectedDigest)) {
                "renderer checkpoint snapshot hash mismatch"
            }

            val cursor = requireNotNull(acks.get(safeSessionId, safeDirection)) {
                "ACK cursor must be initialized before binding a renderer checkpoint"
            }
            require(cursor.rendererGeneration == expectedRendererGeneration) {
                "ACK renderer generation is stale"
            }
            require(rendererCursor >= cursor.highestContiguousSequence) {
                "renderer checkpoint cannot regress the durable ACK cursor"
            }
            val updatedAt = clockMillis()
            check(
                acks.installRendererCheckpoint(
                    safeSessionId,
                    safeDirection,
                    expectedRendererGeneration,
                    rendererCursor,
                    updatedAt,
                ) == 1,
            ) { "ACK renderer generation changed during checkpoint binding" }
            val nextGeneration = expectedRendererGeneration + 1
            rendererCheckpoints.upsert(
                RendererCheckpointEntity(
                    sessionId = safeSessionId,
                    direction = safeDirection,
                    rendererGeneration = nextGeneration,
                    snapshotId = safeSnapshotId,
                    snapshotSequence = sequence,
                    ciphertextSha256 = expectedDigest,
                    updatedAtEpochMillis = updatedAt,
                ),
            )
            checkpointState(snapshot, metadata, sequence, nextGeneration, rendererCursor)
        }
    }

    /** Returns null when the cursor has no snapshot binding; inconsistent bindings fail closed. */
    suspend fun rendererCheckpointState(sessionId: UUID, direction: String): RendererCheckpointState? {
        val safeSessionId = sessionId.requireUuidString()
        val safeDirection = direction.requireNonBlank("ACK direction")
        return readHistoryTransaction { retained ->
            val checkpoint = rendererCheckpoints.get(safeSessionId, safeDirection) ?: return@readHistoryTransaction null
            val cursor = acks.get(safeSessionId, safeDirection)
                ?: throw HistoryIntegrityException("renderer checkpoint has no ACK cursor")
            if (cursor.rendererGeneration != checkpoint.rendererGeneration ||
                cursor.highestContiguousSequence < checkpoint.snapshotSequence
            ) {
                throw HistoryIntegrityException("renderer checkpoint does not match its ACK generation")
            }
            val metadata = snapshots.readMetadata(checkpoint.snapshotId)
                ?: throw HistoryIntegrityException("renderer checkpoint snapshot is missing")
            if (metadata.sessionId != safeSessionId ||
                metadata.snapshotSequence != checkpoint.snapshotSequence
            ) {
                throw HistoryIntegrityException("renderer checkpoint snapshot identity mismatch")
            }
            val maximumSnapshotBytes = try {
                knownSnapshotMaximumBytes(metadata.formatName, metadata.formatVersion)
            } catch (_: IllegalArgumentException) {
                throw HistoryIntegrityException("renderer checkpoint uses an unsupported snapshot format")
            } ?: throw HistoryIntegrityException("renderer checkpoint uses an unsupported snapshot format")
            if (metadata.sizeBytes !in 1..maximumSnapshotBytes ||
                metadata.ciphertextBytes !in 1..maximumSnapshotBytes ||
                metadata.rows !in 1..MAX_TERMINAL_SNAPSHOT_ROWS ||
                metadata.columns !in 1..MAX_TERMINAL_SNAPSHOT_COLUMNS
            ) {
                throw HistoryIntegrityException("renderer checkpoint snapshot is out of bounds")
            }
            val snapshot = readSnapshot(metadata).also(retained::add)
            if (!MessageDigest.isEqual(snapshot.metadata.ciphertextSha256, checkpoint.ciphertextSha256)) {
                throw HistoryIntegrityException("renderer checkpoint snapshot identity mismatch")
            }
            checkpointState(
                snapshot,
                metadata,
                checkpoint.snapshotSequence,
                checkpoint.rendererGeneration,
                cursor.highestContiguousSequence,
            )
        }
    }

    /** Resets one cursor and permanently fences writers from its old renderer. */
    suspend fun resetAckCursor(
        sessionId: UUID,
        direction: String,
        expectedRendererGeneration: Long,
    ): AckCursorState {
        require(expectedRendererGeneration > 0) { "renderer generation must be positive" }
        require(expectedRendererGeneration < Long.MAX_VALUE) { "renderer generation is exhausted" }
        val safeSessionId = sessionId.requireUuidString()
        val safeDirection = direction.requireNonBlank("ACK direction")
        return database.withTransaction {
            val reset = acks.reset(
                safeSessionId,
                safeDirection,
                expectedRendererGeneration,
                clockMillis(),
            )
            rendererCheckpoints.delete(safeSessionId, safeDirection)
            reset.toAckCursorState()
        }
    }

    suspend fun putPreference(
        scope: String,
        key: String,
        keyId: String,
        ciphertext: ByteArray,
        ciphertextSha256: ByteArray? = null,
    ) {
        val bytes = ciphertext.copyOf()
        require(scope.isNotBlank()) { "preference scope is required" }
        require(key.isNotBlank()) { "preference key is required" }
        require(keyId.isNotBlank()) { "preference key id is required" }
        require(bytes.isNotEmpty()) { "preference ciphertext must not be empty" }
        val digest = sha256(bytes)
        ciphertextSha256?.let { supplied ->
            if (!MessageDigest.isEqual(supplied, digest)) {
                throw HistoryIntegrityException("preference ciphertext hash mismatch before persistence")
            }
        }
        preferences.upsert(
            HistoryPreferenceEntity(
                scope = scope,
                preferenceKey = key,
                keyId = keyId,
                valueCiphertext = bytes,
                valueCiphertextSha256 = digest,
                updatedAtEpochMillis = clockMillis(),
            ),
        )
    }

    suspend fun preference(scope: String, key: String): EncryptedPreference? =
        preferences.get(scope, key)?.let { entity ->
            verifyHash(entity.valueCiphertext, entity.valueCiphertextSha256, "preference ${entity.preferenceKey}")
            EncryptedPreference(
                scope = entity.scope,
                key = entity.preferenceKey,
                keyId = entity.keyId,
                ciphertextSha256 = entity.valueCiphertextSha256.copyOf(),
                ciphertext = entity.valueCiphertext.copyOf(),
            )
        }

    suspend fun preferences(scope: String): List<EncryptedPreference> =
        preferences.list(scope).map { entity ->
            verifyHash(entity.valueCiphertext, entity.valueCiphertextSha256, "preference ${entity.preferenceKey}")
            EncryptedPreference(
                scope = entity.scope,
                key = entity.preferenceKey,
                keyId = entity.keyId,
                ciphertextSha256 = entity.valueCiphertextSha256.copyOf(),
                ciphertext = entity.valueCiphertext.copyOf(),
            )
        }

    suspend fun deletePreference(scope: String, key: String) {
        preferences.delete(scope, key)
    }

    suspend fun clearPreferences(scope: String) {
        preferences.deleteScope(scope)
    }

    /**
     * Replays a bounded, contiguous event range. Snapshots are deliberately not decoded
     * here: callers can fetch [latestSnapshot] and apply their engine-specific format,
     * then call this method with the snapshot sequence as the lower bound.
     */
    suspend fun replay(
        sessionId: UUID,
        fromSequence: ULong = 0u,
        throughSequence: ULong? = null,
        decrypt: suspend (metadata: HistoryObject, ciphertext: ByteArray) -> List<HistoryReplayEvent>,
    ): List<HistoryReplayEvent> {
        require(fromSequence <= Long.MAX_VALUE.toULong()) { "replay start is too large for Room" }
        if (throughSequence != null) {
            require(throughSequence >= fromSequence) { "replay range is inverted" }
            require(throughSequence <= Long.MAX_VALUE.toULong()) { "replay end is too large for Room" }
        }
        if (fromSequence == ULong.MAX_VALUE) return emptyList()
        val after = fromSequence.toLong()
        val through = throughSequence?.toLong()
        val rows = chunks.listForReplay(sessionId.requireUuidString(), after, through)
        val expectedFirst = fromSequence + 1u
        if (rows.isEmpty()) {
            if (throughSequence != null && expectedFirst <= throughSequence) {
                throw HistoryGapException(expectedFirst, expectedFirst)
            }
            return emptyList()
        }

        var expected = expectedFirst
        var firstChunk = true
        val result = ArrayList<HistoryReplayEvent>()
        for (row in rows) {
            val metadata = chunkMetadata(row)
            verifyHash(row.ciphertext, row.ciphertextSha256, "event chunk ${row.id}", row.sizeBytes)
            val start = row.startSequence.toULongChecked("event chunk start")
            val end = row.endSequence.toULongChecked("event chunk end")
            if (start == 0uL || end < start) {
                throw HistoryIntegrityException("invalid event chunk range ${row.startSequence}..${row.endSequence}")
            }
            val overlapsRequestedPrefix = firstChunk && start <= fromSequence && end >= expected
            if (start < expected && !overlapsRequestedPrefix) {
                throw HistoryIntegrityException("overlapping event chunk ${row.id}: starts at $start, expected $expected")
            }
            if (start > expected) throw HistoryGapException(expected, start)

            val decoded = try {
                decrypt(metadata, row.ciphertext.copyOf())
            } catch (error: HistoryIntegrityException) {
                throw error
            } catch (error: Exception) {
                throw HistoryIntegrityException("unable to decrypt event chunk ${row.id}: ${error.message ?: "failure"}")
            }
            try {
                validateDecodedChunk(decoded, start, end, row.id)
                decoded.forEach { event ->
                    if (throughSequence == null || event.sequence <= throughSequence) {
                        if (event.sequence > fromSequence) result += event.copyPayload()
                    }
                }
            } finally {
                decoded.forEach { it.payload.fill(0) }
            }
            expected = if (end == ULong.MAX_VALUE) ULong.MAX_VALUE else end + 1u
            firstChunk = false
            if (throughSequence != null && end >= throughSequence) break
        }
        if (throughSequence != null && expected <= throughSequence) {
            throw HistoryGapException(expected, expected)
        }
        return result
    }

    /**
     * Searches decrypted event bytes in memory. Query text, decoded bytes, and previews
     * are local values only; this method has no DAO writes and creates no plaintext index.
     */
    suspend fun search(
        sessionId: UUID,
        query: String,
        decrypt: suspend (metadata: HistoryObject, ciphertext: ByteArray) -> ByteArray,
        limit: Int = DEFAULT_SEARCH_LIMIT,
    ): List<HistorySearchHit> {
        require(limit > 0) { "search limit must be positive" }
        if (query.isEmpty()) return emptyList()
        val rows = chunks.listForSession(sessionId.requireUuidString())
        val result = ArrayList<HistorySearchHit>()
        val foldedQuery = query.lowercase()
        for (row in rows) {
            val metadata = chunkMetadata(row)
            verifyHash(row.ciphertext, row.ciphertextSha256, "event chunk ${row.id}", row.sizeBytes)
            val plaintext = try {
                decrypt(metadata, row.ciphertext.copyOf())
            } catch (error: HistoryIntegrityException) {
                throw error
            } catch (error: Exception) {
                throw HistoryIntegrityException("unable to decrypt event chunk ${row.id}: ${error.message ?: "failure"}")
            }
            try {
                val text = plaintext.toString(Charsets.UTF_8)
                if (text.lowercase().contains(foldedQuery)) {
                    result += HistorySearchHit(
                        objectId = row.id.toUuid(),
                        sequence = row.startSequence.toULongChecked("event chunk start"),
                        preview = text.take(PREVIEW_LIMIT),
                    )
                    if (result.size >= limit) break
                }
            } finally {
                plaintext.fill(0)
            }
        }
        return result
    }

    suspend fun deleteSession(sessionId: UUID) {
        val id = sessionId.requireUuidString()
        database.withTransaction {
            brokerCaptures.deleteForSession(id)
            chunks.deleteForSession(id)
            transcriptPages.deleteForSession(id)
            rendererCheckpoints.deleteForSession(id)
            snapshots.deleteForSession(id)
            acks.deleteForSession(id)
            sessions.delete(id)
            preferences.deleteScope(id)
            preferences.deleteScope(sessionPreferenceScope(sessionId))
        }
    }

    suspend fun clearHistory(sessionId: UUID) {
        val id = sessionId.requireUuidString()
        database.withTransaction {
            brokerCaptures.deleteForSession(id)
            chunks.deleteForSession(id)
            transcriptPages.deleteForSession(id)
            rendererCheckpoints.deleteForSession(id)
            snapshots.deleteForSession(id)
            acks.deleteForSession(id)
        }
    }

    /** Clears summaries, encrypted blobs, ACKs, and encrypted preferences on sign-out. */
    suspend fun clearAll() {
        database.withTransaction {
            brokerCaptures.clear()
            chunks.clear()
            transcriptPages.clear()
            rendererCheckpoints.clear()
            snapshots.clear()
            acks.clear()
            preferences.clear()
            sessions.clear()
            pcs.clear()
            devices.clear()
        }
    }

    private fun deviceEntity(summary: DeviceSummary) = DeviceEntity(
        id = summary.id.requireUuidString(),
        type = summary.type.name,
        name = summary.name,
        keyId = summary.keyId,
        exchangePublicKey = summary.exchangePublicKey.copyOf(),
        trustState = summary.trustState.name,
        createdAtEpochMillis = summary.createdAt.toEpochMilli(),
    )

    private fun requireValidDeviceKey(summary: DeviceSummary) {
        require(summary.exchangePublicKey.size == 32 && summary.exchangePublicKey.any { it.toInt() != 0 }) {
            "device exchange public key is invalid"
        }
        require(summary.keyId.isNotBlank()) { "device key ID is required" }
    }

    private fun pcEntity(summary: PcSummary) = PcEntity(
        id = summary.id.requireUuidString(),
        deviceId = summary.deviceId.requireUuidString(),
        name = summary.name,
        agentVersion = summary.agentVersion,
        keyEpoch = summary.keyEpoch,
        remoteRoutingEnabled = summary.remoteRoutingEnabled,
        online = summary.online,
        lastSeenAtEpochMillis = summary.lastSeenAt?.toEpochMilli(),
        platform = summary.platform,
        architecture = summary.architecture,
        platformVersion = summary.platformVersion,
        capabilitiesCsv = summary.capabilities.sorted().joinToString(","),
    )

    private fun eventChunkEntity(
        id: UUID,
        sessionId: UUID,
        startSequence: ULong,
        endSequence: ULong,
        objectKey: String,
        keyId: String,
        ciphertext: ByteArray,
        expiresAt: Instant?,
    ): EventChunkEntity {
        require(startSequence > 0u) { "event chunk start sequence must be positive" }
        require(endSequence >= startSequence) { "event chunk sequence range is inverted" }
        val start = startSequence.toRoomSequence("event chunk start")
        val end = endSequence.toRoomSequence("event chunk end")
        val bytes = ciphertext.copyOf()
        require(bytes.isNotEmpty()) { "event chunk ciphertext must not be empty" }
        return EventChunkEntity(
            id = id.requireUuidString(),
            sessionId = sessionId.requireUuidString(),
            startSequence = start,
            endSequence = end,
            objectKey = objectKey.requireNonBlank("event chunk object key"),
            keyId = keyId.requireNonBlank("event chunk key id"),
            ciphertextSha256 = sha256(bytes),
            sizeBytes = bytes.size.toLong(),
            ciphertext = bytes,
            createdAtEpochMillis = clockMillis(),
            expiresAtEpochMillis = expiresAt?.toEpochMilli(),
        )
    }

    private fun brokerCaptureArguments(
        sessionId: UUID,
        captureId: UUID,
        sourceSequence: ULong,
        keyEpoch: Long,
        canonicalBytes: Int,
        canonicalSha256: ByteArray,
        keyId: String,
        ciphertext: ByteArray,
    ): BrokerCaptureMetadata {
        require(sessionId != ZERO_UUID && captureId != ZERO_UUID) {
            "broker capture UUID is invalid"
        }
        require(keyEpoch > 0) { "broker capture key epoch must be positive" }
        require(sourceSequence > 0uL && sourceSequence < Long.MAX_VALUE.toULong()) {
            "broker capture source sequence is invalid"
        }
        require(canonicalSha256.size == SHA256_BYTES) {
            "broker capture canonical hash must be SHA-256"
        }
        require(keyId.isNotBlank()) { "broker capture key id is required" }
        validateBrokerCaptureEnvelope(
            ciphertext,
            sessionId,
            captureId,
            sourceSequence,
            keyEpoch,
            canonicalBytes,
            canonicalSha256,
        )
        return BrokerCaptureMetadata(
            sessionId = sessionId,
            captureId = captureId,
            keyEpoch = keyEpoch,
            sourceSequence = sourceSequence,
            canonicalBytes = canonicalBytes,
            canonicalSha256 = canonicalSha256.copyOf(),
            keyId = keyId,
            sizeBytes = ciphertext.size.toLong(),
            ciphertextSha256 = sha256(ciphertext),
            createdAtEpochMillis = 0,
        )
    }

    private fun brokerCaptureMetadata(entity: BrokerCaptureReadMetadata): BrokerCaptureMetadata =
        BrokerCaptureMetadata(
            sessionId = entity.sessionId.toUuid(),
            captureId = entity.captureId.toUuid(),
            keyEpoch = entity.keyEpoch,
            sourceSequence = entity.sourceSequence.toULongChecked("broker capture source sequence"),
            canonicalBytes = entity.canonicalBytes,
            canonicalSha256 = entity.canonicalSha256.copyOf(),
            keyId = entity.keyId,
            sizeBytes = entity.sizeBytes,
            ciphertextSha256 = entity.ciphertextSha256.copyOf(),
            createdAtEpochMillis = entity.createdAtEpochMillis,
        )

    private fun requireSameBrokerCapture(
        stored: BrokerCaptureReadMetadata,
        expected: BrokerCaptureMetadata,
    ) {
        require(stored.sessionId == expected.sessionId.toString() &&
            stored.captureId == expected.captureId.toString() &&
            stored.keyEpoch == expected.keyEpoch &&
            stored.sourceSequence == expected.sourceSequence.toRoomSequence("broker capture source sequence") &&
            stored.canonicalBytes == expected.canonicalBytes &&
            MessageDigest.isEqual(stored.canonicalSha256, expected.canonicalSha256) &&
            stored.keyId == expected.keyId &&
            stored.sizeBytes == expected.sizeBytes &&
            stored.ciphertextBytes == expected.sizeBytes &&
            stored.ciphertextSha256.size == SHA256_BYTES
        ) { "broker capture identity is already bound to different immutable content" }
    }

    private fun validateBrokerCaptureEnvelope(
        sealed: ByteArray,
        sessionId: UUID,
        captureId: UUID,
        sourceSequence: ULong,
        keyEpoch: Long,
        canonicalBytes: Int,
        canonicalSha256: ByteArray,
    ) {
        require(canonicalBytes in BROKER_CAPTURE_MINIMUM_CANONICAL_BYTES..MAX_BROKER_CAPTURE_CANONICAL_BYTES) {
            "broker capture canonical length is invalid"
        }
        require(canonicalSha256.size == SHA256_BYTES) {
            "broker capture canonical hash must be SHA-256"
        }
        val expectedSize = canonicalBytes.toLong() + BROKER_CAPTURE_SEALED_OVERHEAD_BYTES
        require(sealed.size.toLong() == expectedSize && sealed.size <= MAX_BROKER_CAPTURE_OBJECT_BYTES) {
            "broker capture sealed size is invalid"
        }
        require(sealed.size >= BROKER_CAPTURE_HEADER_BYTES) {
            "broker capture sealed header is truncated"
        }
        val view = ByteBuffer.wrap(sealed).order(ByteOrder.BIG_ENDIAN)
        require(sealed.matchesBytes(0, BROKER_CAPTURE_MAGIC) &&
            view.getShort(8).toInt() == 1 &&
            view.getShort(10).toInt() == BROKER_CAPTURE_HEADER_BYTES &&
            sealed.matchesBytes(12, sessionId.uuidBytes()) &&
            sealed.matchesBytes(60, captureId.uuidBytes()) &&
            view.getLong(76).toULong() == sourceSequence &&
            view.getLong(84) == keyEpoch &&
            view.getInt(92) == canonicalBytes &&
            MessageDigest.isEqual(sealed.copyOfRange(96, 128), canonicalSha256) &&
            (sealed.matchesBytes(140, BROKER_CAPTURE_STATE_CONTRACT) ||
                sealed.matchesBytes(140, BROKER_CAPTURE_UNICODE_STATE_CONTRACT)) &&
            (162 until BROKER_CAPTURE_HEADER_BYTES).all { sealed[it] == 0.toByte() }
        ) { "broker capture sealed header identity is invalid" }
    }

    private fun ByteArray.matchesBytes(offset: Int, expected: ByteArray): Boolean =
        offset >= 0 && offset + expected.size <= size && expected.indices.all { this[offset + it] == expected[it] }

    private fun UUID.uuidBytes(): ByteArray = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        .putLong(mostSignificantBits).putLong(leastSignificantBits).array()

    private fun chunkObject(entity: EventChunkEntity): EncryptedHistoryObject =
        EncryptedHistoryObject(chunkMetadata(entity), entity.ciphertext, bufferBudget)

    private fun requireSnapshotReadSize(metadata: SnapshotReadMetadata) {
        val maximumSnapshotBytes = try {
            knownSnapshotMaximumBytes(metadata.formatName, metadata.formatVersion)
        } catch (_: IllegalArgumentException) {
            throw HistoryIntegrityException("unsupported snapshot format in local history")
        }
        if (metadata.sizeBytes <= 0 || metadata.sizeBytes != metadata.ciphertextBytes ||
            (maximumSnapshotBytes != null && metadata.sizeBytes > maximumSnapshotBytes)
        ) {
            throw HistoryIntegrityException("snapshot ciphertext size is out of bounds")
        }
    }

    /** Must be called inside the metadata query's transaction. */
    private suspend fun readSnapshot(metadata: SnapshotReadMetadata): EncryptedHistoryObject {
        requireSnapshotReadSize(metadata)
        val reservation = bufferBudget.reserve(metadata.ciphertextBytes)
        var bytes: ByteArray? = null
        var transferred = false
        try {
            val entity = snapshots.get(metadata.id)
                ?: throw HistoryIntegrityException("snapshot disappeared during read")
            bytes = entity.ciphertext
            verifyHash(entity.ciphertext, entity.ciphertextSha256, "snapshot ${entity.id}", entity.sizeBytes)
            val result = EncryptedHistoryObject(
                metadata = HistoryObject(
                    id = entity.id.toUuid(),
                    kind = HistoryObject.Kind.SNAPSHOT,
                    objectKey = entity.objectKey,
                    keyId = entity.keyId,
                    ciphertextSha256 = entity.ciphertextSha256.copyOf(),
                    sizeBytes = entity.sizeBytes,
                    startSequence = null,
                    endSequence = null,
                    snapshotSequence = entity.snapshotSequence.toULongChecked("snapshot sequence"),
                    formatName = entity.formatName,
                    formatVersion = entity.formatVersion,
                ),
                ciphertext = entity.ciphertext,
                bufferBudget = bufferBudget,
                reservation = reservation,
            )
            transferred = true
            return result
        } finally {
            bytes?.fill(0)
            if (!transferred) reservation.close()
        }
    }

    private fun knownSnapshotMaximumBytes(formatName: String, formatVersion: Int): Long? = when (formatName) {
        TERMINAL_SNAPSHOT_FORMAT -> {
            require(formatVersion == 1) { "unsupported terminal snapshot format version" }
            MAX_TERMINAL_SNAPSHOT_BYTES
        }
        TERMINAL_CORE_SNAPSHOT_FORMAT -> {
            require(formatVersion == 2) { "unsupported terminal-core snapshot format version" }
            MAX_TERMINAL_CORE_SNAPSHOT_BYTES
        }
        TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT -> {
            require(formatVersion == 1) { "unsupported terminal-core graphics snapshot format version" }
            MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_BYTES
        }
        // Generic history storage remains compatible; renderer binding rejects these formats.
        else -> null
    }

    private fun transcriptPage(entity: TranscriptPageEntity): EncryptedTranscriptPage =
        EncryptedTranscriptPage(
            metadata = TranscriptPageMetadata(
                id = entity.id.toUuid(),
                sessionId = entity.sessionId.toUuid(),
                snapshotSequence = entity.snapshotSequence.toULongChecked("transcript snapshot anchor"),
                requestBeforeLineId = entity.requestBeforeLineId.toULongChecked("transcript request cursor"),
                firstLineId = entity.firstLineId.toULongChecked("transcript first line ID"),
                lastLineId = entity.lastLineId.toULongChecked("transcript last line ID"),
                objectKey = entity.objectKey,
                keyId = entity.keyId,
                ciphertextSha256 = entity.ciphertextSha256.copyOf(),
                sizeBytes = entity.sizeBytes,
                formatName = entity.formatName,
                formatVersion = entity.formatVersion,
            ),
            ciphertext = entity.ciphertext.copyOf(),
        )

    private fun checkpointState(
        snapshot: EncryptedHistoryObject,
        metadata: SnapshotReadMetadata,
        snapshotSequence: Long,
        rendererGeneration: Long,
        acknowledgement: Long,
    ): RendererCheckpointState = RendererCheckpointState(
        snapshot = snapshot,
        snapshotRows = metadata.rows,
        snapshotColumns = metadata.columns,
        snapshotSequence = snapshotSequence.toULongChecked("snapshot sequence"),
        durableAcknowledgement = acknowledgement.toULongChecked("ACK sequence"),
        rendererGeneration = rendererGeneration,
    )

    /** Returns every row addressed by one of the candidate's immutable identities. */
    private suspend fun eventChunkIdentityMatches(candidate: EventChunkEntity): List<EventChunkEntity> =
        listOfNotNull(
            chunks.get(candidate.id),
            chunks.getByObjectKey(candidate.objectKey),
            chunks.getForRange(candidate.sessionId, candidate.startSequence, candidate.endSequence),
        ).distinctBy(EventChunkEntity::id)

    private fun requireSameEventChunk(existing: EventChunkEntity, candidate: EventChunkEntity) {
        // The sealed object bytes contain the authenticated recipient and nonce;
        // compare the entire container in constant time. As with snapshots and
        // transcript pages, createdAtEpochMillis and expiresAtEpochMillis are
        // local cache policy metadata rather than immutable object identity.
        require(existing.id == candidate.id &&
            existing.sessionId == candidate.sessionId &&
            existing.startSequence == candidate.startSequence &&
            existing.endSequence == candidate.endSequence &&
            existing.objectKey == candidate.objectKey &&
            existing.keyId == candidate.keyId &&
            MessageDigest.isEqual(existing.ciphertextSha256, candidate.ciphertextSha256) &&
            existing.sizeBytes == candidate.sizeBytes &&
            MessageDigest.isEqual(existing.ciphertext, candidate.ciphertext)
        ) { "event chunk identity is already bound to different immutable content" }
    }

    private fun requireSameSnapshot(existing: SnapshotEntity, candidate: SnapshotEntity) {
        require(existing.sessionId == candidate.sessionId &&
            existing.snapshotSequence == candidate.snapshotSequence &&
            existing.rows == candidate.rows &&
            existing.columns == candidate.columns &&
            existing.objectKey == candidate.objectKey &&
            existing.keyId == candidate.keyId &&
            MessageDigest.isEqual(existing.ciphertextSha256, candidate.ciphertextSha256) &&
            existing.sizeBytes == candidate.sizeBytes &&
            MessageDigest.isEqual(existing.ciphertext, candidate.ciphertext) &&
            existing.formatName == candidate.formatName &&
            existing.formatVersion == candidate.formatVersion
        ) { "snapshot ID is already bound to different immutable content" }
    }

    private fun requireSameTranscriptPage(existing: TranscriptPageEntity, candidate: TranscriptPageEntity) {
        require(existing.id == candidate.id &&
            existing.sessionId == candidate.sessionId &&
            existing.snapshotSequence == candidate.snapshotSequence &&
            existing.requestBeforeLineId == candidate.requestBeforeLineId &&
            existing.firstLineId == candidate.firstLineId &&
            existing.lastLineId == candidate.lastLineId &&
            existing.objectKey == candidate.objectKey &&
            existing.keyId == candidate.keyId &&
            MessageDigest.isEqual(existing.ciphertextSha256, candidate.ciphertextSha256) &&
            existing.sizeBytes == candidate.sizeBytes &&
            MessageDigest.isEqual(existing.ciphertext, candidate.ciphertext) &&
            existing.formatName == candidate.formatName &&
            existing.formatVersion == candidate.formatVersion
        ) { "transcript identity is already bound to different immutable content" }
    }

    private fun chunkMetadata(entity: EventChunkEntity): HistoryObject = HistoryObject(
        id = entity.id.toUuid(),
        kind = HistoryObject.Kind.CHUNK,
        objectKey = entity.objectKey,
        keyId = entity.keyId,
        ciphertextSha256 = entity.ciphertextSha256.copyOf(),
        sizeBytes = entity.sizeBytes,
        startSequence = entity.startSequence.toULongChecked("event chunk start"),
        endSequence = entity.endSequence.toULongChecked("event chunk end"),
        snapshotSequence = null,
        formatName = null,
        formatVersion = null,
    )

    private fun validateDecodedChunk(
        events: List<HistoryReplayEvent>,
        start: ULong,
        end: ULong,
        objectId: String,
    ) {
        val expectedCount = end - start + 1u
        require(expectedCount <= Int.MAX_VALUE.toULong()) {
            "event chunk $objectId is too large to decode in memory"
        }
        if (events.size.toULong() != expectedCount) {
            throw HistoryIntegrityException(
                "event chunk $objectId decoded ${events.size} events for range $start..$end",
            )
        }
        events.forEachIndexed { index, event ->
            val expected = start + index.toULong()
            if (event.sequence != expected) {
                throw HistoryIntegrityException(
                    "event chunk $objectId decoded sequence ${event.sequence}, expected $expected",
                )
            }
        }
    }

    private fun verifyHash(ciphertext: ByteArray, expected: ByteArray, label: String, expectedSize: Long? = null) {
        if (expectedSize != null && expectedSize != ciphertext.size.toLong()) {
            throw HistoryIntegrityException("ciphertext size verification failed for $label")
        }
        if (expected.size != SHA256_BYTES || !MessageDigest.isEqual(sha256(ciphertext), expected)) {
            throw HistoryIntegrityException("SHA-256 verification failed for $label")
        }
        if (ciphertext.isEmpty()) throw HistoryIntegrityException("empty ciphertext for $label")
    }

    private fun UUID.requireUuidString(): String = toString()

    private fun String.toUuid(): UUID = try {
        UUID.fromString(this)
    } catch (error: IllegalArgumentException) {
        throw HistoryIntegrityException("invalid UUID in local history: $this")
    }

    private fun String.requireNonBlank(label: String): String {
        require(isNotBlank()) { "$label is required" }
        return this
    }

    private fun ULong.toRoomSequence(label: String): Long {
        require(this <= Long.MAX_VALUE.toULong()) { "$label is too large for Room" }
        return toLong()
    }

    private fun ULong.toPositiveRoomSequence(label: String): Long {
        require(this > 0u) { "$label must be positive" }
        return toRoomSequence(label)
    }

    private fun Long.toULongChecked(label: String): ULong {
        require(this >= 0) { "$label must not be negative" }
        return toULong()
    }

    private fun AckCursorEntity.toAckCursorState(): AckCursorState = AckCursorState(
        sequence = highestContiguousSequence.toULongChecked("ACK sequence"),
        rendererGeneration = rendererGeneration.also {
            require(it > 0) { "renderer generation must be positive" }
        },
    )

    private inline fun <reified T : Enum<T>> String.toEnumOrThrow(label: String): T = try {
        enumValueOf<T>(this)
    } catch (error: IllegalArgumentException) {
        throw HistoryIntegrityException("unknown $label: $this")
    }

    companion object {
        const val BOUNDED_EVENT_REPLAY_FORMAT = "BOUNDED_EVENT_REPLAY"
        const val TERMINAL_SNAPSHOT_FORMAT = "TERMLINK_TERMINAL_SNAPSHOT"
        const val TERMINAL_CORE_SNAPSHOT_FORMAT = "TERMLINK_TERMINAL_CORE_SNAPSHOT"
        const val TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT = "TERMLINK_TERMINAL_CORE_GRAPHICS_SNAPSHOT"
        const val TERMINAL_TRANSCRIPT_FORMAT = "TERMLINK_TERMINAL_TRANSCRIPT"
        const val MAX_TERMINAL_SNAPSHOT_BYTES = 2L * 1024 * 1024
        const val MAX_TERMINAL_CORE_SNAPSHOT_BYTES = 64L * 1024 * 1024
        // Encrypted container budget; native plaintext admission is enforced by JNI.
        const val MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_BYTES = 128L * 1024 * 1024 + 4096
        const val MAX_TERMINAL_TRANSCRIPT_BYTES = 512L * 1024
        const val MAX_TERMINAL_SNAPSHOT_ROWS = 256
        const val MAX_TERMINAL_SNAPSHOT_COLUMNS = 512
        const val BROKER_CAPTURE_HEADER_BYTES = 192
        const val BROKER_CAPTURE_SEALED_OVERHEAD_BYTES = BROKER_CAPTURE_HEADER_BYTES + 16L
        const val BROKER_CAPTURE_MINIMUM_CANONICAL_BYTES = 136L
        const val MAX_BROKER_CAPTURE_CANONICAL_BYTES = 32L * 1024 * 1024
        const val MAX_BROKER_CAPTURE_OBJECT_BYTES =
            MAX_BROKER_CAPTURE_CANONICAL_BYTES + BROKER_CAPTURE_SEALED_OVERHEAD_BYTES
        const val BROKER_CAPTURE_CHUNK_BYTES = 512 * 1024
        const val SESSION_PREFERENCE_SCOPE = "session"
        const val SHA256_BYTES = 32
        const val DEFAULT_SEARCH_LIMIT = 100
        const val PREVIEW_LIMIT = 256

        fun sessionPreferenceScope(sessionId: UUID): String = "$SESSION_PREFERENCE_SCOPE:$sessionId"

        fun sha256(input: ByteArray): ByteArray =
            MessageDigest.getInstance("SHA-256").digest(input)

        internal val ZERO_UUID = UUID(0, 0)
        internal val BROKER_CAPTURE_MAGIC = "TLBCSEA1".encodeToByteArray()
        internal val BROKER_CAPTURE_STATE_CONTRACT = "terminal_state_2_22_v1".encodeToByteArray()
        internal val BROKER_CAPTURE_UNICODE_STATE_CONTRACT = "terminal_state_2_22_v2".encodeToByteArray()
    }
}

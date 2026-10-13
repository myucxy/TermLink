package dev.termlink.database

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Room rows intentionally contain identifiers, bounded metadata, and ciphertext only.
 * UUIDs are stored as canonical strings because Room has no built-in UUID affinity.
 */
@Entity(
    tableName = "devices",
    indices = [Index(value = ["trustState"]), Index(value = ["createdAtEpochMillis"])]
)
data class DeviceEntity(
    @PrimaryKey val id: String,
    val type: String,
    val name: String,
    val keyId: String,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val exchangePublicKey: ByteArray,
    val trustState: String,
    val createdAtEpochMillis: Long,
)

@Entity(
    tableName = "pcs",
    indices = [Index(value = ["deviceId"]), Index(value = ["online", "lastSeenAtEpochMillis"])]
)
data class PcEntity(
    @PrimaryKey val id: String,
    val deviceId: String,
    val name: String,
    val agentVersion: String,
    val keyEpoch: Long,
    val remoteRoutingEnabled: Boolean,
    val online: Boolean,
    val lastSeenAtEpochMillis: Long?,
    val platform: String = "windows",
    val architecture: String = "unknown",
    val platformVersion: String = "legacy",
    val capabilitiesCsv: String = "",
)

@Entity(
    tableName = "sessions",
    indices = [
        Index(value = ["pcId", "createdAtEpochMillis"]),
        Index(value = ["state"]),
    ]
)
data class SessionEntity(
    @PrimaryKey val id: String,
    val pcId: String,
    val shellType: String,
    /** An encrypted title; plaintext titles must never be passed to this row. */
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val encryptedTitle: ByteArray?,
    val state: String,
    val columns: Int,
    val rows: Int,
    val lastOutputSequence: Long,
    val createdAtEpochMillis: Long,
    val endedAtEpochMillis: Long?,
)

@Entity(
    tableName = "event_chunks",
    indices = [
        Index(value = ["sessionId", "startSequence", "endSequence"], unique = true),
        Index(value = ["sessionId", "endSequence"]),
        Index(value = ["objectKey"], unique = true),
    ]
)
data class EventChunkEntity(
    @PrimaryKey val id: String,
    val sessionId: String,
    val startSequence: Long,
    val endSequence: Long,
    val objectKey: String,
    val keyId: String,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertextSha256: ByteArray,
    val sizeBytes: Long,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertext: ByteArray,
    val createdAtEpochMillis: Long,
    val expiresAtEpochMillis: Long?,
)

@Entity(
    tableName = "snapshots",
    indices = [
        Index(value = ["sessionId", "snapshotSequence", "formatName", "formatVersion"], unique = true),
        Index(value = ["sessionId", "snapshotSequence"]),
        Index(value = ["objectKey"], unique = true),
    ]
)
data class SnapshotEntity(
    @PrimaryKey val id: String,
    val sessionId: String,
    val snapshotSequence: Long,
    val rows: Int,
    val columns: Int,
    val objectKey: String,
    val keyId: String,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertextSha256: ByteArray,
    val sizeBytes: Long,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertext: ByteArray,
    val formatName: String,
    val formatVersion: Int,
    val createdAtEpochMillis: Long,
    val expiresAtEpochMillis: Long?,
)

/**
 * One encrypted, read-only transcript page selected for a fixed snapshot anchor.
 * [requestBeforeLineId] records the exclusive cursor which produced this page so
 * cached paging cannot silently skip a gap or drift to a newer live ACK.
 */
@Entity(
    tableName = "transcript_pages",
    indices = [
        Index(
            value = ["sessionId", "snapshotSequence", "requestBeforeLineId"],
            unique = true,
        ),
        Index(value = ["sessionId", "snapshotSequence", "firstLineId", "lastLineId"]),
        Index(value = ["objectKey"], unique = true),
    ],
)
data class TranscriptPageEntity(
    @PrimaryKey val id: String,
    val sessionId: String,
    val snapshotSequence: Long,
    val requestBeforeLineId: Long,
    val firstLineId: Long,
    val lastLineId: Long,
    val objectKey: String,
    val keyId: String,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertextSha256: ByteArray,
    val sizeBytes: Long,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertext: ByteArray,
    val formatName: String,
    val formatVersion: Int,
    val createdAtEpochMillis: Long,
    val expiresAtEpochMillis: Long?,
)

@Entity(
    tableName = "ack_cursors",
    primaryKeys = ["sessionId", "direction"],
    indices = [Index(value = ["sessionId", "highestContiguousSequence"])]
)
data class AckCursorEntity(
    val sessionId: String,
    /** Direction is metadata, e.g. PC_TO_ANDROID or ANDROID_TO_PC. */
    val direction: String,
    /**
     * Identifies the renderer state this cursor belongs to. Reset increments
     * the generation instead of deleting the row so delayed writers from an
     * older renderer cannot recreate a stale high cursor.
     */
    val rendererGeneration: Long,
    val highestContiguousSequence: Long,
    val updatedAtEpochMillis: Long,
)

/**
 * Binds a durable ACK generation to the exact encrypted snapshot from which
 * that renderer must be reconstructed before the cursor may be advertised.
 */
@Entity(
    tableName = "renderer_checkpoints",
    primaryKeys = ["sessionId", "direction"],
    indices = [Index(value = ["snapshotId"])],
    foreignKeys = [
        ForeignKey(
            entity = SnapshotEntity::class,
            parentColumns = ["id"],
            childColumns = ["snapshotId"],
        ),
        ForeignKey(
            entity = AckCursorEntity::class,
            parentColumns = ["sessionId", "direction"],
            childColumns = ["sessionId", "direction"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
)
data class RendererCheckpointEntity(
    val sessionId: String,
    val direction: String,
    val rendererGeneration: Long,
    val snapshotId: String,
    val snapshotSequence: Long,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertextSha256: ByteArray,
    val updatedAtEpochMillis: Long,
)

@Entity(
    tableName = "history_preferences",
    primaryKeys = ["scope", "preferenceKey"],
    indices = [Index(value = ["scope"])]
)
data class HistoryPreferenceEntity(
    val scope: String,
    val preferenceKey: String,
    val keyId: String,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val valueCiphertext: ByteArray,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val valueCiphertextSha256: ByteArray,
    val updatedAtEpochMillis: Long,
)

/**
 * A sealed Kitty capture kept separately from terminal history objects.
 *
 * The key epoch is part of the identity so a rotated recipient key never
 * overwrites a cache entry that was authenticated under an older key.  The
 * source-sequence index additionally prevents two capture UUIDs from claiming
 * one ordered terminal event.
 */
@Entity(
    tableName = "broker_captures",
    primaryKeys = ["sessionId", "captureId", "keyEpoch"],
    indices = [
        Index(value = ["sessionId", "sourceSequence", "keyEpoch"], unique = true),
        Index(value = ["sessionId", "sourceSequence"]),
    ],
)
data class BrokerCaptureEntity(
    val sessionId: String,
    val captureId: String,
    val keyEpoch: Long,
    val sourceSequence: Long,
    val canonicalBytes: Int,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val canonicalSha256: ByteArray,
    val keyId: String,
    val sizeBytes: Long,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertextSha256: ByteArray,
    @ColumnInfo(typeAffinity = ColumnInfo.BLOB) val ciphertext: ByteArray,
    val createdAtEpochMillis: Long,
)

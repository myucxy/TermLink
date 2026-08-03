package dev.termlink.database

import androidx.room.ColumnInfo
import androidx.room.Entity
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

@Entity(
    tableName = "ack_cursors",
    primaryKeys = ["sessionId", "direction"],
    indices = [Index(value = ["sessionId", "highestContiguousSequence"])]
)
data class AckCursorEntity(
    val sessionId: String,
    /** Direction is metadata, e.g. PC_TO_ANDROID or ANDROID_TO_PC. */
    val direction: String,
    val highestContiguousSequence: Long,
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

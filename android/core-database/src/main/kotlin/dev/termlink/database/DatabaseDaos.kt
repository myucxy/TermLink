package dev.termlink.database

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

@Dao
interface DeviceDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: DeviceEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(entities: List<DeviceEntity>)

    @Query("SELECT * FROM devices ORDER BY createdAtEpochMillis DESC, id ASC")
    suspend fun list(): List<DeviceEntity>

    @Query("SELECT * FROM devices WHERE id = :id LIMIT 1")
    suspend fun get(id: String): DeviceEntity?

    @Query("DELETE FROM devices WHERE id = :id")
    suspend fun delete(id: String)

    @Query("DELETE FROM devices")
    suspend fun clear()
}

@Dao
interface PcDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: PcEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(entities: List<PcEntity>)

    @Query("SELECT * FROM pcs ORDER BY online DESC, name COLLATE NOCASE ASC, id ASC")
    suspend fun list(): List<PcEntity>

    @Query("SELECT * FROM pcs WHERE id = :id LIMIT 1")
    suspend fun get(id: String): PcEntity?

    @Query("DELETE FROM pcs WHERE id = :id")
    suspend fun delete(id: String)

    @Query("DELETE FROM pcs")
    suspend fun clear()
}

@Dao
interface SessionDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: SessionEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(entities: List<SessionEntity>)

    @Query("SELECT * FROM sessions WHERE pcId = :pcId ORDER BY createdAtEpochMillis DESC, id ASC")
    suspend fun listForPc(pcId: String): List<SessionEntity>

    @Query("SELECT * FROM sessions ORDER BY createdAtEpochMillis DESC, id ASC")
    suspend fun list(): List<SessionEntity>

    @Query("SELECT * FROM sessions WHERE id = :id LIMIT 1")
    suspend fun get(id: String): SessionEntity?

    @Query("DELETE FROM sessions WHERE id = :id")
    suspend fun delete(id: String)

    @Query("DELETE FROM sessions")
    suspend fun clear()
}

@Dao
interface EventChunkDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: EventChunkEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(entities: List<EventChunkEntity>)

    /** Returns all chunks which could contribute a sequence after [afterSequence]. */
    @Query(
        "SELECT * FROM event_chunks " +
            "WHERE sessionId = :sessionId AND endSequence > :afterSequence " +
            "AND (:throughSequence IS NULL OR startSequence <= :throughSequence) " +
            "ORDER BY startSequence ASC, endSequence ASC, id ASC",
    )
    suspend fun listForReplay(
        sessionId: String,
        afterSequence: Long,
        throughSequence: Long?,
    ): List<EventChunkEntity>

    @Query("SELECT * FROM event_chunks WHERE sessionId = :sessionId ORDER BY startSequence ASC, endSequence ASC, id ASC")
    suspend fun listForSession(sessionId: String): List<EventChunkEntity>

    @Query("SELECT * FROM event_chunks WHERE id = :id LIMIT 1")
    suspend fun get(id: String): EventChunkEntity?

    @Query("DELETE FROM event_chunks WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM event_chunks")
    suspend fun clear()
}

@Dao
interface SnapshotDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: SnapshotEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(entities: List<SnapshotEntity>)

    @Query(
        "SELECT * FROM snapshots WHERE sessionId = :sessionId " +
            "AND snapshotSequence <= :atOrBeforeSequence " +
            "ORDER BY snapshotSequence DESC, createdAtEpochMillis DESC, id ASC LIMIT 1",
    )
    suspend fun latestAtOrBefore(sessionId: String, atOrBeforeSequence: Long): SnapshotEntity?

    @Query("SELECT * FROM snapshots WHERE sessionId = :sessionId ORDER BY snapshotSequence ASC, id ASC")
    suspend fun listForSession(sessionId: String): List<SnapshotEntity>

    @Query("SELECT * FROM snapshots WHERE id = :id LIMIT 1")
    suspend fun get(id: String): SnapshotEntity?

    @Query("DELETE FROM snapshots WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM snapshots")
    suspend fun clear()
}

@Dao
interface AckCursorDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: AckCursorEntity)

    @Query("SELECT * FROM ack_cursors WHERE sessionId = :sessionId ORDER BY direction ASC")
    suspend fun listForSession(sessionId: String): List<AckCursorEntity>

    @Query("SELECT * FROM ack_cursors WHERE sessionId = :sessionId AND direction = :direction LIMIT 1")
    suspend fun get(sessionId: String, direction: String): AckCursorEntity?

    @Query("DELETE FROM ack_cursors WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM ack_cursors")
    suspend fun clear()
}

@Dao
interface HistoryPreferenceDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: HistoryPreferenceEntity)

    @Query("SELECT * FROM history_preferences WHERE scope = :scope ORDER BY preferenceKey ASC")
    suspend fun list(scope: String): List<HistoryPreferenceEntity>

    @Query("SELECT * FROM history_preferences WHERE scope = :scope AND preferenceKey = :preferenceKey LIMIT 1")
    suspend fun get(scope: String, preferenceKey: String): HistoryPreferenceEntity?

    @Query("DELETE FROM history_preferences WHERE scope = :scope AND preferenceKey = :preferenceKey")
    suspend fun delete(scope: String, preferenceKey: String)

    @Query("DELETE FROM history_preferences WHERE scope = :scope")
    suspend fun deleteScope(scope: String)

    @Query("DELETE FROM history_preferences")
    suspend fun clear()
}

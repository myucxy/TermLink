package dev.termlink.database

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction

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
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(entity: EventChunkEntity): Long

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertAllIfAbsent(entities: List<EventChunkEntity>): List<Long>

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

    /**
     * Returns the immutable chunk(s) beginning at one exact replay cursor.
     * The repository rejects more than one result as an overlapping catalog;
     * limiting this query keeps arbitrary-length replay memory independent of
     * the total number of cached chunks.
     */
    @Query(
        "SELECT * FROM event_chunks WHERE sessionId = :sessionId " +
            "AND startSequence = :startSequence " +
            "ORDER BY endSequence ASC, id ASC LIMIT 2",
    )
    suspend fun listStartingAt(sessionId: String, startSequence: Long): List<EventChunkEntity>

    @Query("SELECT * FROM event_chunks WHERE sessionId = :sessionId ORDER BY startSequence ASC, endSequence ASC, id ASC")
    suspend fun listForSession(sessionId: String): List<EventChunkEntity>

    @Query("SELECT * FROM event_chunks WHERE id = :id LIMIT 1")
    suspend fun get(id: String): EventChunkEntity?

    @Query("SELECT * FROM event_chunks WHERE objectKey = :objectKey LIMIT 1")
    suspend fun getByObjectKey(objectKey: String): EventChunkEntity?

    @Query(
        "SELECT * FROM event_chunks WHERE sessionId = :sessionId " +
            "AND startSequence = :startSequence AND endSequence = :endSequence LIMIT 1",
    )
    suspend fun getForRange(
        sessionId: String,
        startSequence: Long,
        endSequence: Long,
    ): EventChunkEntity?

    @Query("SELECT COUNT(*) FROM event_chunks WHERE sessionId = :sessionId")
    suspend fun countForSession(sessionId: String): Int

    @Query("DELETE FROM event_chunks WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM event_chunks")
    suspend fun clear()
}

@Dao
interface SnapshotDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(entity: SnapshotEntity): Long

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

    @Query("SELECT COUNT(*) FROM snapshots WHERE sessionId = :sessionId")
    suspend fun countForSession(sessionId: String): Int

    @Query("DELETE FROM snapshots WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM snapshots")
    suspend fun clear()
}

@Dao
interface TranscriptPageDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(entity: TranscriptPageEntity): Long

    @Query(
        "SELECT * FROM transcript_pages WHERE sessionId = :sessionId " +
            "AND snapshotSequence = :snapshotSequence AND requestBeforeLineId = :requestBeforeLineId LIMIT 1",
    )
    suspend fun getForCursor(
        sessionId: String,
        snapshotSequence: Long,
        requestBeforeLineId: Long,
    ): TranscriptPageEntity?

    @Query("SELECT * FROM transcript_pages WHERE id = :id LIMIT 1")
    suspend fun get(id: String): TranscriptPageEntity?

    @Query("SELECT * FROM transcript_pages WHERE objectKey = :objectKey LIMIT 1")
    suspend fun getByObjectKey(objectKey: String): TranscriptPageEntity?

    @Query(
        "SELECT * FROM transcript_pages WHERE sessionId = :sessionId AND snapshotSequence = :snapshotSequence " +
            "ORDER BY firstLineId ASC, lastLineId ASC, id ASC",
    )
    suspend fun listForAnchor(sessionId: String, snapshotSequence: Long): List<TranscriptPageEntity>

    @Query("SELECT COUNT(*) FROM transcript_pages WHERE sessionId = :sessionId")
    suspend fun countForSession(sessionId: String): Int

    @Query("DELETE FROM transcript_pages WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM transcript_pages")
    suspend fun clear()
}

@Dao
interface AckCursorDao {
    @Query(
        """UPDATE ack_cursors
           SET highestContiguousSequence = CASE
                 WHEN :sequence > highestContiguousSequence THEN :sequence
                 ELSE highestContiguousSequence
               END,
               updatedAtEpochMillis = CASE
                 WHEN :sequence > highestContiguousSequence THEN :updatedAtEpochMillis
                 ELSE updatedAtEpochMillis
               END
           WHERE sessionId = :sessionId AND direction = :direction
             AND rendererGeneration = :rendererGeneration""",
    )
    suspend fun advanceExisting(
        sessionId: String,
        direction: String,
        rendererGeneration: Long,
        sequence: Long,
        updatedAtEpochMillis: Long,
    ): Int

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(entity: AckCursorEntity): Long

    /** API-26-compatible monotonic upsert; stale concurrent writes cannot regress the cursor. */
    @Transaction
    suspend fun advance(entity: AckCursorEntity): Boolean {
        if (advanceExisting(
                entity.sessionId,
                entity.direction,
                entity.rendererGeneration,
                entity.highestContiguousSequence,
                entity.updatedAtEpochMillis,
            ) == 1
        ) return true
        if (insertIfAbsent(entity) != -1L) return true
        return advanceExisting(
                entity.sessionId,
                entity.direction,
                entity.rendererGeneration,
                entity.highestContiguousSequence,
                entity.updatedAtEpochMillis,
            ) == 1
    }

    @Transaction
    suspend fun getOrCreate(entity: AckCursorEntity): AckCursorEntity {
        get(entity.sessionId, entity.direction)?.let { return it }
        insertIfAbsent(entity)
        return requireNotNull(get(entity.sessionId, entity.direction)) {
            "ACK cursor initialization failed"
        }
    }

    @Query(
        """UPDATE ack_cursors
           SET rendererGeneration = rendererGeneration + 1,
               highestContiguousSequence = 0,
               updatedAtEpochMillis = :updatedAtEpochMillis
           WHERE sessionId = :sessionId AND direction = :direction
             AND rendererGeneration = :expectedGeneration""",
    )
    suspend fun resetExisting(
        sessionId: String,
        direction: String,
        expectedGeneration: Long,
        updatedAtEpochMillis: Long,
    ): Int

    @Query(
        """UPDATE ack_cursors
           SET rendererGeneration = rendererGeneration + 1,
               highestContiguousSequence = :sequence,
               updatedAtEpochMillis = :updatedAtEpochMillis
           WHERE sessionId = :sessionId AND direction = :direction
             AND rendererGeneration = :expectedGeneration
             AND highestContiguousSequence <= :sequence""",
    )
    suspend fun installRendererCheckpoint(
        sessionId: String,
        direction: String,
        expectedGeneration: Long,
        sequence: Long,
        updatedAtEpochMillis: Long,
    ): Int

    /** Atomically fences all writers from the previous renderer generation. */
    @Transaction
    suspend fun reset(
        sessionId: String,
        direction: String,
        expectedGeneration: Long,
        updatedAtEpochMillis: Long,
    ): AckCursorEntity {
        check(resetExisting(sessionId, direction, expectedGeneration, updatedAtEpochMillis) == 1) {
            "ACK renderer generation is stale"
        }
        return requireNotNull(get(sessionId, direction)) { "ACK cursor disappeared during reset" }
    }

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
interface RendererCheckpointDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entity: RendererCheckpointEntity)

    @Query("SELECT * FROM renderer_checkpoints WHERE sessionId = :sessionId AND direction = :direction LIMIT 1")
    suspend fun get(sessionId: String, direction: String): RendererCheckpointEntity?

    @Query("DELETE FROM renderer_checkpoints WHERE sessionId = :sessionId AND direction = :direction")
    suspend fun delete(sessionId: String, direction: String)

    @Query("DELETE FROM renderer_checkpoints WHERE sessionId = :sessionId")
    suspend fun deleteForSession(sessionId: String)

    @Query("DELETE FROM renderer_checkpoints")
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

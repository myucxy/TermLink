package dev.termlink.product

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.history.HistoryRepository
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class ProductionRoomCacheTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var historyRepository: HistoryRepository
    private val loadedCheckpoints = mutableListOf<AutoCloseable>()

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        historyRepository = HistoryRepository(database, clockMillis = { 5_000 })
    }

    @After
    fun tearDown() {
        loadedCheckpoints.forEach(AutoCloseable::close)
        loadedCheckpoints.clear()
        database.close()
    }

    @Test
    fun productCacheClearDeletesCheckpointBeforeItsSnapshotAndAckParents() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val snapshotId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val direction = "PC_TO_ANDROID"
        val ciphertext = byteArrayOf(1, 3, 3, 7)
        val initial = historyRepository.initializeAckCursor(sessionId, direction)
        historyRepository.putSnapshot(
            sessionId = sessionId,
            snapshotSequence = 40u,
            rows = 24,
            columns = 80,
            objectKey = "snapshots/$sessionId/40",
            keyId = "snapshot-v1:recipient:00000001",
            formatName = "TERMLINK_TERMINAL_SNAPSHOT",
            ciphertext = ciphertext,
            id = snapshotId,
        )
        historyRepository.bindRendererCheckpoint(
            sessionId = sessionId,
            direction = direction,
            expectedRendererGeneration = initial.rendererGeneration,
            snapshotId = snapshotId,
            snapshotSequence = 40u,
            ciphertextSha256 = HistoryRepository.sha256(ciphertext),
        ).close()

        clearProductRoomCache(historyRepository)

        assertNull(database.rendererCheckpoints().get(sessionId.toString(), direction))
        assertNull(database.snapshots().get(snapshotId.toString()))
        assertNull(database.ackCursors().get(sessionId.toString(), direction))
    }

    @Test
    fun newerRemoteV2SnapshotRebindsTheBcf7DurableAckAndFencesGenerationTwo() = runBlocking {
        val sessionId = UUID.fromString("bcf7a19f-1d31-57a1-bc5a-f469aad5f48c")
        val localSnapshotId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val remoteSnapshotId = UUID.fromString("20314253-6475-8697-a8b9-cadbeced0f10")
        val direction = "PC_TO_ANDROID"
        val localCiphertext = byteArrayOf(1, 7)
        val remoteCiphertext = byteArrayOf(16, 29)
        val initial = historyRepository.initializeAckCursor(sessionId, direction)
        historyRepository.putSnapshot(
            sessionId = sessionId,
            snapshotSequence = 7uL,
            rows = 24,
            columns = 80,
            objectKey = "snapshots/$sessionId/7",
            keyId = "terminal-core-snapshot-v2:recipient:00000001",
            formatName = HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT,
            formatVersion = 2,
            ciphertext = localCiphertext,
            id = localSnapshotId,
        )
        val localBound = historyRepository.bindRendererCheckpoint(
            sessionId = sessionId,
            direction = direction,
            expectedRendererGeneration = initial.rendererGeneration,
            snapshotId = localSnapshotId,
            snapshotSequence = 7uL,
            ciphertextSha256 = HistoryRepository.sha256(localCiphertext),
        ).also(loadedCheckpoints::add)
        historyRepository.putAckCursor(
            sessionId = sessionId,
            direction = direction,
            rendererGeneration = localBound.rendererGeneration,
            sequence = 1629uL,
        )
        historyRepository.putSnapshot(
            sessionId = sessionId,
            snapshotSequence = 1629uL,
            rows = 24,
            columns = 80,
            objectKey = "snapshots/$sessionId/1629",
            keyId = "terminal-core-snapshot-v2:recipient:00000001",
            formatName = HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT,
            formatVersion = 2,
            ciphertext = remoteCiphertext,
            id = remoteSnapshotId,
        )

        val remoteBound = historyRepository.bindRendererCheckpoint(
            sessionId = sessionId,
            direction = direction,
            expectedRendererGeneration = localBound.rendererGeneration,
            snapshotId = remoteSnapshotId,
            snapshotSequence = 1629uL,
            ciphertextSha256 = HistoryRepository.sha256(remoteCiphertext),
        ).also(loadedCheckpoints::add)

        assertEquals(2L, localBound.rendererGeneration)
        assertEquals(3L, remoteBound.rendererGeneration)
        assertEquals(1629uL, remoteBound.snapshotSequence)
        assertEquals(1629uL, remoteBound.durableAcknowledgement)
        assertEquals(
            1629uL,
            historyRepository.ackCursorState(sessionId, direction)?.sequence,
        )
        assertEquals(
            remoteSnapshotId,
            historyRepository.rendererCheckpointState(sessionId, direction)?.use { it.snapshot.metadata.id },
        )
    }
}

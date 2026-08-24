package dev.termlink.product

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.history.HistoryRepository
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
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
        )

        clearProductRoomCache(historyRepository)

        assertNull(database.rendererCheckpoints().get(sessionId.toString(), direction))
        assertNull(database.snapshots().get(snapshotId.toString()))
        assertNull(database.ackCursors().get(sessionId.toString(), direction))
    }
}

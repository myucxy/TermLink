package dev.termlink.history

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class RendererCheckpointRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = HistoryRepository(database, clockMillis = { 5_000 })
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun snapshotBindingAtomicallyAdvancesAckAndFencesOldRenderer() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val snapshotId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val direction = "PC_TO_ANDROID"
        val ciphertext = byteArrayOf(1, 3, 3, 7)
        val digest = HistoryRepository.sha256(ciphertext)
        val initial = repository.initializeAckCursor(sessionId, direction)
        repository.putSnapshot(
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

        val hashFailure = runCatching {
            repository.bindRendererCheckpoint(
                sessionId,
                direction,
                initial.rendererGeneration,
                snapshotId,
                40u,
                ByteArray(32),
            )
        }.exceptionOrNull()
        assertTrue(hashFailure is IllegalArgumentException)
        assertEquals(initial, repository.ackCursorState(sessionId, direction))
        assertNull(repository.rendererCheckpointState(sessionId, direction))

        val bound = repository.bindRendererCheckpoint(
            sessionId,
            direction,
            initial.rendererGeneration,
            snapshotId,
            40u,
            digest,
        )
        assertEquals(2L, bound.rendererGeneration)
        assertEquals(40uL, bound.snapshotSequence)
        assertEquals(40uL, bound.durableAcknowledgement)
        assertEquals(24, bound.snapshotRows)
        assertEquals(80, bound.snapshotColumns)
        assertArrayEquals(ciphertext, bound.snapshot.ciphertext)

        val replacementFailure = runCatching {
            repository.putSnapshot(
                sessionId = sessionId,
                snapshotSequence = 40u,
                rows = 24,
                columns = 80,
                objectKey = "snapshots/$sessionId/40",
                keyId = "snapshot-v1:recipient:00000001",
                formatName = "TERMLINK_TERMINAL_SNAPSHOT",
                ciphertext = byteArrayOf(4, 2),
                id = snapshotId,
            )
        }.exceptionOrNull()
        assertTrue(replacementFailure is IllegalArgumentException)
        assertArrayEquals(
            ciphertext,
            repository.rendererCheckpointState(sessionId, direction)?.snapshot?.ciphertext,
        )

        val secondDirection = "ANDROID_TO_PC"
        val secondInitial = repository.initializeAckCursor(sessionId, secondDirection)
        val secondBound = repository.bindRendererCheckpoint(
            sessionId,
            secondDirection,
            secondInitial.rendererGeneration,
            snapshotId,
            40u,
            digest,
        )
        assertEquals(2L, secondBound.rendererGeneration)

        val staleWriterFailure = runCatching {
            repository.putAckCursor(sessionId, direction, initial.rendererGeneration, 99u)
        }.exceptionOrNull()
        assertTrue(staleWriterFailure is IllegalStateException)
        assertEquals(45uL, repository.putAckCursor(sessionId, direction, bound.rendererGeneration, 45u))
        val advanced = requireNotNull(repository.rendererCheckpointState(sessionId, direction))
        assertEquals(40uL, advanced.snapshotSequence)
        assertEquals(45uL, advanced.durableAcknowledgement)
        assertEquals(45uL, repository.ackCursorState(sessionId, direction)?.sequence)

        val invalidReanchor = runCatching {
            repository.bindRendererCheckpoint(
                sessionId,
                direction,
                bound.rendererGeneration,
                snapshotId,
                40u,
                ByteArray(32),
            )
        }.exceptionOrNull()
        assertTrue(invalidReanchor is IllegalArgumentException)
        assertEquals(45uL, repository.ackCursorState(sessionId, direction)?.sequence)

        val regressionFailure = runCatching {
            repository.bindRendererCheckpoint(
                sessionId,
                direction,
                bound.rendererGeneration,
                snapshotId,
                40u,
                digest,
            )
        }.exceptionOrNull()
        assertTrue(regressionFailure is IllegalArgumentException)
        assertEquals(
            AckCursorState(sequence = 45u, rendererGeneration = bound.rendererGeneration),
            repository.ackCursorState(sessionId, direction),
        )
        assertEquals(40uL, repository.rendererCheckpointState(sessionId, direction)?.snapshotSequence)
        assertEquals(46uL, repository.putAckCursor(sessionId, direction, bound.rendererGeneration, 46u))
    }

    @Test
    fun rendererResetDeletesCheckpointInTheSameTransaction() = runBlocking {
        val sessionId = UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        val snapshotId = UUID.fromString("bbbbbbbb-cccc-dddd-eeee-ffffffffffff")
        val direction = "PC_TO_ANDROID"
        val ciphertext = byteArrayOf(9, 8, 7)
        val initial = repository.initializeAckCursor(sessionId, direction)
        repository.putSnapshot(
            sessionId = sessionId,
            snapshotSequence = 12u,
            rows = 36,
            columns = 120,
            objectKey = "snapshots/$sessionId/12",
            keyId = "snapshot-v1:recipient:00000001",
            formatName = "TERMLINK_TERMINAL_SNAPSHOT",
            ciphertext = ciphertext,
            id = snapshotId,
        )
        val bound = repository.bindRendererCheckpoint(
            sessionId,
            direction,
            initial.rendererGeneration,
            snapshotId,
            12u,
            HistoryRepository.sha256(ciphertext),
        )

        val reset = repository.resetAckCursor(sessionId, direction, bound.rendererGeneration)

        assertEquals(3L, reset.rendererGeneration)
        assertEquals(0uL, reset.sequence)
        assertNull(repository.rendererCheckpointState(sessionId, direction))
    }
}

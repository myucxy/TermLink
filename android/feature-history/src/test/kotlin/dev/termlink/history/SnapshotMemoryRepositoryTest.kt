package dev.termlink.history

import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.model.TerminalBufferLimitException
import java.util.Collections
import java.util.UUID
import java.util.concurrent.Executor
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import kotlin.test.assertFailsWith

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class SnapshotMemoryRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository
    private var usedBytes = 0L
    private val budget = TerminalBufferBudget { HeapState(192 * MIB, usedBytes) }
    private val queries = Collections.synchronizedList(mutableListOf<String>())
    private val opened = mutableListOf<AutoCloseable>()
    private val sessionId = UUID.randomUUID()
    private val ciphertext = byteArrayOf(1, 2, 3, 4)
    private val direction = "PC_TO_ANDROID"
    private val objectCost = MIB + 8 * ciphertext.size

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(), TermLinkDatabase::class.java,
        ).allowMainThreadQueries().setQueryCallback(
            object : RoomDatabase.QueryCallback {
                override fun onQuery(sqlQuery: String, bindArgs: List<Any?>) {
                    queries += sqlQuery
                }
            },
            Executor { it.run() },
        ).build()
        repository = HistoryRepository(database, bufferBudget = budget)
    }

    @After
    fun tearDown() {
        opened.forEach(AutoCloseable::close)
        database.close()
        assertEquals("every snapshot reservation must be returned", 0L, budget.reservedBytes)
    }

    @Test
    fun everySnapshotReadRejectsBeforeSelectingBlob(): Unit = runBlocking {
        val id = put(40u)
        val initial = repository.initializeAckCursor(sessionId, direction)
        repository.bindRendererCheckpoint(
            sessionId, direction, initial.rendererGeneration, id, 40u, HistoryRepository.sha256(ciphertext),
        ).use { assertEquals(objectCost, budget.reservedBytes) }
        usedBytes = 160 * MIB

        val reads: List<suspend () -> Any?> = listOf(
            { repository.getHistoryObject(id) },
            { repository.latestSnapshot(sessionId, 40u) },
            { repository.listHistory(sessionId) },
            { repository.rendererCheckpointState(sessionId, direction) },
            { repository.bindRendererCheckpoint(sessionId, direction, 2, id, 40u, HistoryRepository.sha256(ciphertext)) },
            { repository.bindHostRendererCheckpoint(sessionId, direction, 2, id, 40u, 40u, HistoryRepository.sha256(ciphertext)) },
        )
        for (read in reads) {
            queries.clear()
            assertEquals("TERMINAL_HISTORY_MEMORY_LIMIT", assertFailsWith<TerminalBufferLimitException> { read() }.code)
            assertTrue(queries.any { it.contains("LENGTH(ciphertext)") })
            assertEquals(0, blobReads())
            assertEquals(0L, budget.reservedBytes)
        }
        assertEquals(2L, repository.ackCursorState(sessionId, direction)?.rendererGeneration)
    }

    @Test
    fun retainedObjectsAndCopiesShareOneBudgetUntilDestroyed(): Unit = runBlocking {
        val id = put(40u)
        usedBytes = 160 * MIB - 2 * objectCost
        val original = requireNotNull(repository.getHistoryObject(id)).track()
        val copy = original.copyCiphertext().track()
        assertEquals(2 * objectCost, budget.reservedBytes)
        assertFailsWith<TerminalBufferLimitException> { original.copyCiphertext() }
        queries.clear()
        assertFailsWith<TerminalBufferLimitException> { repository.getHistoryObject(id) }
        assertEquals(0, blobReads())
        original.destroy()
        original.close()
        assertEquals(objectCost, budget.reservedBytes)
        assertArrayEquals(ByteArray(ciphertext.size), original.ciphertext)
        assertArrayEquals(ciphertext, copy.ciphertext)
        assertFailsWith<IllegalStateException> { original.copyCiphertext() }
        requireNotNull(repository.getHistoryObject(id)).use { assertArrayEquals(ciphertext, it.ciphertext) }
        copy.close()
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun listFailureReturnsEarlierReservationsAndDoesNotReadNextBlob(): Unit = runBlocking {
        put(40u)
        put(41u)
        usedBytes = 160 * MIB - objectCost
        queries.clear()
        assertFailsWith<TerminalBufferLimitException> { repository.listHistory(sessionId) }
        assertEquals(1, blobReads())
        assertEquals(0L, budget.reservedBytes)
        usedBytes = 0
        repository.listHistory(sessionId).onEach { opened += it }.also {
            assertEquals(2, it.size)
            assertEquals(2 * objectCost, budget.reservedBytes)
        }
    }

    @Test
    fun corruptReadAndFailedBindReleaseTheirReservations(): Unit = runBlocking {
        val id = put(40u)
        val initial = repository.initializeAckCursor(sessionId, direction)
        assertFailsWith<IllegalArgumentException> {
            repository.bindRendererCheckpoint(sessionId, direction, initial.rendererGeneration, id, 40u, ByteArray(32))
        }
        assertEquals(0L, budget.reservedBytes)
        assertEquals(initial, repository.ackCursorState(sessionId, direction))
        database.openHelper.writableDatabase.execSQL(
            "UPDATE snapshots SET ciphertext = X'09090909' WHERE id = ?", arrayOf(id.toString()),
        )
        assertFailsWith<HistoryIntegrityException> { repository.getHistoryObject(id) }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun checkpointWriteFailureRollsBackAckAndReleasesSnapshot(): Unit = runBlocking {
        val id = put(40u)
        val initial = repository.initializeAckCursor(sessionId, direction)
        database.openHelper.writableDatabase.execSQL(
            "CREATE TRIGGER reject_checkpoint BEFORE INSERT ON renderer_checkpoints " +
                "BEGIN SELECT RAISE(ABORT, 'checkpoint write rejected'); END",
        )
        assertFailsWith<Exception> {
            repository.bindRendererCheckpoint(
                sessionId, direction, initial.rendererGeneration, id, 40u, HistoryRepository.sha256(ciphertext),
            )
        }
        assertEquals(initial, repository.ackCursorState(sessionId, direction))
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun metadataAndActualBlobLengthMustAgreeBeforeRead(): Unit = runBlocking {
        val id = put(40u)
        database.openHelper.writableDatabase.execSQL(
            "UPDATE snapshots SET sizeBytes = ? WHERE id = ?", arrayOf(128 * MIB, id.toString()),
        )
        queries.clear()
        assertFailsWith<HistoryIntegrityException> { repository.getHistoryObject(id) }
        assertEquals(0, blobReads())
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun persistenceRespectsExistingNetworkPermitAndReleasesOnFailure(): Unit = runBlocking {
        usedBytes = 160 * MIB - objectCost
        budget.reserve(ciphertext.size.toLong()).use {
            assertFailsWith<TerminalBufferLimitException> { put(40u) }
            assertEquals(objectCost, budget.reservedBytes)
        }
        assertEquals(0, database.snapshots().countForSession(sessionId.toString()))
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), ciphertext)
        assertFailsWith<HistoryIntegrityException> { put(40u, digest = ByteArray(32)) }
        assertEquals(0L, budget.reservedBytes)
        val id = put(40u)
        // Comparing an existing immutable object needs a second admission before its BLOB read.
        queries.clear()
        assertFailsWith<TerminalBufferLimitException> { put(40u, id = id) }
        assertEquals(0, blobReads())
        assertEquals(0L, budget.reservedBytes)
        usedBytes = 0
        assertEquals(id, put(40u, id = id))
        assertArrayEquals(ciphertext, requireNotNull(database.snapshots().get(id.toString())).ciphertext)
    }

    @Test
    fun returnedObjectOwnsItsInputAndFailedCopyReturnsNewPermit(): Unit = runBlocking {
        val id = put(40u)
        val stored = requireNotNull(repository.getHistoryObject(id)).track()
        val source = stored.ciphertext.copyOf()
        val objectFromCaller = EncryptedHistoryObject(stored.metadata, source, budget).track()
        source.fill(7)
        assertArrayEquals(ciphertext, objectFromCaller.ciphertext)
        stored.ciphertext.fill(9)
        assertFailsWith<IllegalArgumentException> { stored.copyCiphertext() }
        assertEquals(2 * objectCost, budget.reservedBytes)
        objectFromCaller.destroy()
        assertFalse(source.all { it == 0.toByte() })
    }

    private fun blobReads(): Int = queries.count { it.startsWith("SELECT * FROM snapshots") }

    private fun <T : AutoCloseable> T.track(): T = also { opened += it }

    private suspend fun put(sequence: ULong, id: UUID = UUID.randomUUID(), digest: ByteArray? = null): UUID =
        repository.putSnapshot(
            sessionId = sessionId, snapshotSequence = sequence, rows = 24, columns = 80,
            objectKey = "snapshots/$id", keyId = "snapshot-key",
            formatName = HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, formatVersion = 1,
            ciphertext = ciphertext, id = id, ciphertextSha256 = digest,
        )

    private companion object {
        const val MIB = 1024L * 1024
    }
}

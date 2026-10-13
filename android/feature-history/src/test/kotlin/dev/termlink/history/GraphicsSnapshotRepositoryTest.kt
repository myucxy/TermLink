package dev.termlink.history

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
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
import kotlin.test.assertFailsWith

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class GraphicsSnapshotRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository
    private val bufferBudget = TerminalBufferBudget { HeapState(192L * 1024 * 1024, 0) }
    private val opened = mutableListOf<AutoCloseable>()
    private val sessionId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
    private val ciphertext = byteArrayOf(1, 2, 3, 4)
    private val direction = "PC_TO_ANDROID"

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = HistoryRepository(database, clockMillis = { 5_000 }, bufferBudget = bufferBudget)
    }

    @After
    fun tearDown() {
        opened.forEach(AutoCloseable::close)
        database.close()
        assertEquals(0L, bufferBudget.reservedBytes)
    }

    private fun <T : AutoCloseable> T.track(): T = also { opened += it }

    @Test
    fun graphicsSnapshotBindsAndReloadsWithItsExactFormat(): Unit = runBlocking {
        val initial = repository.initializeAckCursor(sessionId, direction)
        val snapshotId = put(HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, 1)

        val bound = repository.bindRendererCheckpoint(
            sessionId, direction, initial.rendererGeneration, snapshotId, 40u,
            HistoryRepository.sha256(ciphertext),
        ).track()

        assertEquals(2L, bound.rendererGeneration)
        assertEquals(40uL, bound.durableAcknowledgement)
        val restored = requireNotNull(repository.rendererCheckpointState(sessionId, direction)?.track())
        assertEquals(HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, restored.snapshot.metadata.formatName)
        assertEquals(1, restored.snapshot.metadata.formatVersion)
        assertArrayEquals(ciphertext, restored.snapshot.ciphertext)
        assertEquals(24, restored.snapshotRows)
        assertEquals(80, restored.snapshotColumns)

        assertEquals(45uL, repository.putAckCursor(sessionId, direction, bound.rendererGeneration, 45u))
        val hostBound = repository.bindHostRendererCheckpoint(
            sessionId, direction, bound.rendererGeneration, snapshotId, 40u, 45u,
            HistoryRepository.sha256(ciphertext),
        ).track()
        assertEquals(3L, hostBound.rendererGeneration)
        assertEquals(45uL, hostBound.durableAcknowledgement)
        assertEquals(40uL, hostBound.snapshotSequence)
        assertEquals(45uL, repository.rendererCheckpointState(sessionId, direction)?.track()?.durableAcknowledgement)
    }

    @Test
    fun oversizedGraphicsInputIsRejectedBeforeCopyingOrPersistence(): Unit = runBlocking {
        val maximum = HistoryRepository.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_BYTES
        assertEquals(128L * 1024 * 1024 + 4096, maximum)
        val oversized = ByteArray(maximum.toInt() + 1)
        // Reflection keeps JVM-only management APIs out of the Android compile classpath.
        val allocations = Class.forName("java.lang.management.ManagementFactory")
            .getMethod("getThreadMXBean").invoke(null)
        val allocationApi = Class.forName("com.sun.management.ThreadMXBean")
        assertTrue("the test JVM must provide thread allocation accounting", allocationApi.isInstance(allocations))
        assertTrue(allocationApi.getMethod("isThreadAllocatedMemorySupported").invoke(allocations) as Boolean)
        allocationApi.getMethod("setThreadAllocatedMemoryEnabled", java.lang.Boolean.TYPE).invoke(allocations, true)
        val allocatedBytes = allocationApi.getMethod("getThreadAllocatedBytes", java.lang.Long.TYPE)
        val threadId = Thread.currentThread().id
        val before = allocatedBytes.invoke(allocations, threadId) as Long
        assertTrue("thread allocation accounting must be enabled", before >= 0)

        val error = assertFailsWith<IllegalArgumentException> {
            put(HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, 1, oversized)
        }

        val allocated = (allocatedBytes.invoke(allocations, threadId) as Long) - before
        assertEquals("snapshot ciphertext size is out of bounds", error.message)
        assertTrue("oversized input allocated $allocated bytes before rejection", allocated in 0 until maximum / 2)
        assertEquals(0, database.snapshots().countForSession(sessionId.toString()))
    }

    @Test
    fun oldFormatsKeepTheirSmallerStorageBudgets(): Unit = runBlocking {
        assertEquals(64L * 1024 * 1024, HistoryRepository.MAX_TERMINAL_CORE_SNAPSHOT_BYTES)
        for ((format, version, maximum) in listOf(
            Triple(HistoryRepository.TERMINAL_SNAPSHOT_FORMAT, 1, HistoryRepository.MAX_TERMINAL_SNAPSHOT_BYTES),
            Triple(HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT, 2, HistoryRepository.MAX_TERMINAL_CORE_SNAPSHOT_BYTES),
        )) {
            val oversized = ByteArray(maximum.toInt() + 1)
            assertEquals("snapshot ciphertext size is out of bounds",
                assertFailsWith<IllegalArgumentException> { put(format, version, oversized) }.message)
        }
        assertEquals(0, database.snapshots().countForSession(sessionId.toString()))
    }

    @Test
    fun knownFormatNamesRejectWrongVersionsBeforePersistence(): Unit = runBlocking {
        for ((format, wrongVersion) in listOf(
            HistoryRepository.TERMINAL_SNAPSHOT_FORMAT to 2,
            HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT to 1,
            HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT to 2,
            HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT to 3,
        )) {
            assertFailsWith<IllegalArgumentException> { put(format, wrongVersion) }
        }
        assertEquals(0, database.snapshots().countForSession(sessionId.toString()))
    }

    @Test
    fun genericSnapshotsRemainReadableButCannotBindRendererCheckpoints(): Unit = runBlocking {
        val initial = repository.initializeAckCursor(sessionId, direction)
        for ((format, version) in listOf(
            HistoryRepository.BOUNDED_EVENT_REPLAY_FORMAT to 1,
            "CUSTOM_SNAPSHOT" to 7,
            "${HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT}_UNKNOWN" to 1,
        )) {
            val snapshotId = put(format, version)
            val stored = requireNotNull(repository.getHistoryObject(snapshotId)?.track())
            assertArrayEquals(ciphertext, stored.ciphertext)
            assertEquals(format, stored.metadata.formatName)
            assertEquals(version, stored.metadata.formatVersion)
            assertFailsWith<IllegalArgumentException> {
                repository.bindRendererCheckpoint(
                    sessionId, direction, initial.rendererGeneration, snapshotId, 40u,
                    HistoryRepository.sha256(ciphertext),
                ).track()
            }
        }
        assertEquals(initial, repository.ackCursorState(sessionId, direction))
        assertNull(repository.rendererCheckpointState(sessionId, direction)?.track())
        assertEquals(3, repository.listHistory(sessionId).onEach { opened += it }.size)
    }

    @Test
    fun bindingAndReloadRejectPersistedFormatBudgetAndDimensionCorruption(): Unit = runBlocking {
        val initial = repository.initializeAckCursor(sessionId, direction)
        val snapshotId = put(HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, 1)
        val bound = repository.bindRendererCheckpoint(
            sessionId, direction, initial.rendererGeneration, snapshotId, 40u,
            HistoryRepository.sha256(ciphertext),
        ).track()
        val valid = arrayOf<Any>(
            HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, 1, ciphertext.size, 24, 80,
        )
        for (mutated in listOf(
            valid.copyOf().apply { this[1] = 2 },
            valid.copyOf().apply { this[0] = "UNKNOWN_SNAPSHOT" },
            valid.copyOf().apply { this[2] = HistoryRepository.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_BYTES + 1 },
            valid.copyOf().apply {
                this[0] = HistoryRepository.TERMINAL_CORE_SNAPSHOT_FORMAT
                this[1] = 2
                this[2] = HistoryRepository.MAX_TERMINAL_CORE_SNAPSHOT_BYTES + 1
            },
            valid.copyOf().apply {
                this[0] = HistoryRepository.TERMINAL_SNAPSHOT_FORMAT
                this[2] = HistoryRepository.MAX_TERMINAL_SNAPSHOT_BYTES + 1
            },
            valid.copyOf().apply { this[3] = HistoryRepository.MAX_TERMINAL_SNAPSHOT_ROWS + 1 },
            valid.copyOf().apply { this[4] = HistoryRepository.MAX_TERMINAL_SNAPSHOT_COLUMNS + 1 },
        )) {
            database.openHelper.writableDatabase.execSQL(
                "UPDATE snapshots SET formatName = ?, formatVersion = ?, sizeBytes = ?, rows = ?, columns = ? WHERE id = ?",
                mutated + snapshotId.toString(),
            )
            assertFailsWith<HistoryIntegrityException> {
                repository.rendererCheckpointState(sessionId, direction)?.track()
            }
            assertFailsWith<IllegalArgumentException> {
                repository.bindRendererCheckpoint(
                    sessionId, direction, bound.rendererGeneration, snapshotId, 40u,
                    HistoryRepository.sha256(ciphertext),
                ).track()
            }
            assertEquals(bound.rendererGeneration, repository.ackCursorState(sessionId, direction)?.rendererGeneration)
        }
    }

    @Test
    fun genericReadsRejectCorruptKnownFormats(): Unit = runBlocking {
        val snapshotId = put(HistoryRepository.TERMINAL_CORE_GRAPHICS_SNAPSHOT_FORMAT, 1)
        database.openHelper.writableDatabase.execSQL(
            "UPDATE snapshots SET formatVersion = 2 WHERE id = ?", arrayOf(snapshotId.toString()),
        )
        assertFailsWith<HistoryIntegrityException> { repository.getHistoryObject(snapshotId)?.track() }
        assertFailsWith<HistoryIntegrityException> { repository.latestSnapshot(sessionId, 40u)?.track() }
        assertFailsWith<HistoryIntegrityException> { repository.listHistory(sessionId).onEach { opened += it } }
    }

    private suspend fun put(format: String, version: Int, bytes: ByteArray = ciphertext): UUID {
        val id = UUID.randomUUID()
        return repository.putSnapshot(
            sessionId = sessionId,
            snapshotSequence = 40u,
            rows = 24,
            columns = 80,
            objectKey = "snapshots/$id",
            keyId = "snapshot-key",
            formatName = format,
            formatVersion = version,
            ciphertext = bytes,
            id = id,
        )
    }
}

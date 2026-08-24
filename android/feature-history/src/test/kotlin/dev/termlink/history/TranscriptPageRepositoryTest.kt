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
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class TranscriptPageRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = HistoryRepository(database, clockMillis = { 9_000 })
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun encryptedPageIsCachedByFixedAnchorAndExclusiveCursorUsingCopies() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val pageId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val ciphertext = byteArrayOf(1, 2, 3, 4)

        repository.putTranscriptPage(
            sessionId = sessionId,
            snapshotSequence = 80u,
            requestBeforeLineId = 41u,
            firstLineId = 21u,
            lastLineId = 40u,
            objectKey = "transcript/$pageId",
            keyId = "transcript-v1:00112233445566778899aabbccddeeff:00000001",
            ciphertext = ciphertext,
            id = pageId,
        )
        ciphertext[0] = 99

        val cached = requireNotNull(repository.transcriptPageForCursor(sessionId, 80u, 41u))
        assertEquals(pageId, cached.metadata.id)
        assertEquals(21uL, cached.metadata.firstLineId)
        assertEquals(40uL, cached.metadata.lastLineId)
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), cached.ciphertext)
        cached.ciphertext[1] = 88
        assertArrayEquals(
            byteArrayOf(1, 2, 3, 4),
            repository.transcriptPageForCursor(sessionId, 80u, 41u)?.ciphertext,
        )
        assertEquals(HistoryObjectCounts(chunks = 0, snapshots = 0, transcripts = 1), repository.historyObjectCounts(sessionId))
    }

    @Test
    fun transcriptObjectAndCursorIdentitiesAreImmutable() = runBlocking {
        val sessionId = UUID.randomUUID()
        val pageId = UUID.randomUUID()
        repository.putTranscriptPage(
            sessionId, 90u, 51u, 31u, 50u, "transcript/$pageId", "key-1", byteArrayOf(7), pageId,
        )

        assertThrows(IllegalArgumentException::class.java) {
            runBlocking {
                repository.putTranscriptPage(
                    sessionId, 90u, 51u, 31u, 50u, "transcript/$pageId", "key-1", byteArrayOf(8), pageId,
                )
            }
        }
        assertThrows(IllegalArgumentException::class.java) {
            runBlocking {
                repository.putTranscriptPage(
                    sessionId, 90u, 51u, 21u, 30u, "transcript/${UUID.randomUUID()}", "key-1",
                    byteArrayOf(9), UUID.randomUUID(),
                )
            }
        }
        assertArrayEquals(
            byteArrayOf(7),
            repository.transcriptPageForCursor(sessionId, 90u, 51u)?.ciphertext,
        )
    }

    @Test
    fun sessionAndGlobalCleanupRemoveTranscriptCiphertext() = runBlocking {
        val first = UUID.randomUUID()
        val second = UUID.randomUUID()
        suspend fun put(sessionId: UUID, marker: Byte) {
            repository.putTranscriptPage(
                sessionId, 10u, 20u, 1u, 19u, "transcript/${UUID.randomUUID()}", "key-1",
                byteArrayOf(marker), UUID.randomUUID(),
            )
        }
        put(first, 1)
        put(second, 2)

        repository.deleteSession(first)
        assertNull(repository.transcriptPageForCursor(first, 10u, 20u))
        assertEquals(1, repository.transcriptPages(second, 10u).size)

        repository.clearAll()
        assertNull(repository.transcriptPageForCursor(second, 10u, 20u))
    }
}

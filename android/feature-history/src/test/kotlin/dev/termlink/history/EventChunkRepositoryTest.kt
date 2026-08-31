package dev.termlink.history

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.EventChunkEntity
import dev.termlink.database.TermLinkDatabase
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class EventChunkRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository
    private var nowMillis = 5_000L

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = HistoryRepository(database, clockMillis = { nowMillis })
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun identicalEncryptedObjectIsIdempotentAndPreservesFirstCacheRow() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val chunkId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val expiresAt = Instant.ofEpochMilli(50_000)
        val ciphertext = byteArrayOf(1, 2, 3, 4)

        repository.putEventChunk(
            sessionId, 11u, 14u, "chunks/$chunkId", "history-v1:recipient:00000001",
            ciphertext, chunkId, expiresAt,
        )
        ciphertext[0] = 99
        nowMillis = 9_000

        assertEquals(
            chunkId,
            repository.putEventChunk(
                sessionId, 11u, 14u, "chunks/$chunkId", "history-v1:recipient:00000001",
                byteArrayOf(1, 2, 3, 4), chunkId, Instant.ofEpochMilli(90_000),
                HistoryRepository.sha256(byteArrayOf(1, 2, 3, 4)),
            ),
        )

        val stored = requireNotNull(database.eventChunks().get(chunkId.toString()))
        assertEquals(1, database.eventChunks().countForSession(sessionId.toString()))
        assertEquals(5_000L, stored.createdAtEpochMillis)
        assertEquals(50_000L, stored.expiresAtEpochMillis)
        assertArrayEquals(byteArrayOf(1, 2, 3, 4), stored.ciphertext)
    }

    @Test
    fun chunkIdCannotBeRebound() = runBlocking {
        val original = putOriginal()

        assertImmutableConflict {
            repository.putEventChunk(
                original.sessionId, original.startSequence, original.endSequence,
                original.objectKey, original.keyId,
                byteArrayOf(9, 8, 7), original.id,
            )
        }

        assertOriginalUnchanged(original)
    }

    @Test
    fun chunkObjectKeyCannotBeRebound() = runBlocking {
        val original = putOriginal()
        val otherSession = UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")

        assertImmutableConflict {
            repository.putEventChunk(
                otherSession, 31u, 32u, original.objectKey, "history-v1:other:00000002",
                byteArrayOf(6, 5, 4), UUID.fromString("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
            )
        }

        assertOriginalUnchanged(original)
    }

    @Test
    fun chunkSessionRangeCannotBeRebound() = runBlocking {
        val original = putOriginal()

        assertImmutableConflict {
            repository.putEventChunk(
                original.sessionId, original.startSequence, original.endSequence,
                "chunks/other-range", "history-v1:other:00000002", byteArrayOf(3, 2, 1),
                UUID.fromString("cccccccc-1111-2222-3333-dddddddddddd"),
            )
        }

        assertOriginalUnchanged(original)
    }

    @Test
    fun replayLookupFindsTheChunkContainingASnapshotSuffixBoundary() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        repository.putEventChunk(
            sessionId,
            1u,
            8u,
            "chunks/one-through-eight",
            "history-v1:recipient:00000001",
            byteArrayOf(1, 2, 3),
            UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f"),
        )

        val chunk = requireNotNull(repository.eventChunkContaining(sessionId, 8u))

        assertEquals(1uL, chunk.metadata.startSequence)
        assertEquals(8uL, chunk.metadata.endSequence)
    }

    @Test
    fun replayLookupRejectsOverlappingChunksAtItsCursor() = runBlocking {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        repository.putEventChunk(
            sessionId,
            1u,
            8u,
            "chunks/first",
            "history-v1:recipient:00000001",
            byteArrayOf(1),
            UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f"),
        )
        repository.putEventChunk(
            sessionId,
            8u,
            9u,
            "chunks/second",
            "history-v1:recipient:00000001",
            byteArrayOf(2),
            UUID.fromString("20314253-6475-8697-a8b9-cadbeced0f10"),
        )

        val failure = runCatching { repository.eventChunkContaining(sessionId, 8u) }.exceptionOrNull()

        assertTrue(failure is IllegalArgumentException)
    }

    private suspend fun putOriginal(): OriginalChunk {
        val original = OriginalChunk(
            id = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f"),
            sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"),
            startSequence = 11u,
            endSequence = 14u,
            objectKey = "chunks/original",
            keyId = "history-v1:recipient:00000001",
            ciphertext = byteArrayOf(1, 2, 3, 4),
            createdAtEpochMillis = nowMillis,
            expiresAtEpochMillis = 50_000,
        )
        repository.putEventChunk(
            original.sessionId,
            original.startSequence,
            original.endSequence,
            original.objectKey,
            original.keyId,
            original.ciphertext,
            original.id,
            Instant.ofEpochMilli(original.expiresAtEpochMillis),
        )
        return original
    }

    private suspend fun assertImmutableConflict(block: suspend () -> Unit) {
        val failure = runCatching { block() }.exceptionOrNull()
        assertTrue(failure is IllegalArgumentException)
    }

    private suspend fun assertOriginalUnchanged(original: OriginalChunk) {
        val rows = database.eventChunks().listForSession(original.sessionId.toString())
        assertEquals(1, rows.size)
        assertChunkEquals(original, rows.single())
    }

    private fun assertChunkEquals(expected: OriginalChunk, actual: EventChunkEntity) {
        assertEquals(expected.id.toString(), actual.id)
        assertEquals(expected.sessionId.toString(), actual.sessionId)
        assertEquals(expected.startSequence.toLong(), actual.startSequence)
        assertEquals(expected.endSequence.toLong(), actual.endSequence)
        assertEquals(expected.objectKey, actual.objectKey)
        assertEquals(expected.keyId, actual.keyId)
        assertEquals(expected.ciphertext.size.toLong(), actual.sizeBytes)
        assertArrayEquals(expected.ciphertext, actual.ciphertext)
        assertArrayEquals(HistoryRepository.sha256(expected.ciphertext), actual.ciphertextSha256)
        assertEquals(expected.createdAtEpochMillis, actual.createdAtEpochMillis)
        assertEquals(expected.expiresAtEpochMillis, actual.expiresAtEpochMillis)
    }

    private data class OriginalChunk(
        val id: UUID,
        val sessionId: UUID,
        val startSequence: ULong,
        val endSequence: ULong,
        val objectKey: String,
        val keyId: String,
        val ciphertext: ByteArray,
        val createdAtEpochMillis: Long,
        val expiresAtEpochMillis: Long,
    )
}

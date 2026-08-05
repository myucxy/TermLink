package dev.termlink.database

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import java.util.concurrent.Executors
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AckCursorDaoTest {
    private lateinit var database: TermLinkDatabase

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            TermLinkDatabase::class.java,
        ).allowMainThreadQueries().build()
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun concurrentInsertAndStaleUpdatesNeverRegressHighestCursor() = runTest {
        val dao = database.ackCursors()
        val executor = Executors.newFixedThreadPool(4)
        val dispatcher = executor.asCoroutineDispatcher()
        try {
            repeat(32) { round ->
                database.clearAllTables()
                val start = CompletableDeferred<Unit>()
                listOf(10L, 5L, 9L, 1L).mapIndexed { index, sequence ->
                    async(dispatcher) {
                        start.await()
                        dao.advance(
                            AckCursorEntity(
                                sessionId = "00000000-0000-0000-0000-000000000001",
                                direction = "PC_TO_ANDROID",
                                rendererGeneration = 1,
                                highestContiguousSequence = sequence,
                                updatedAtEpochMillis = round * 10L + index,
                            ),
                        )
                    }
                }.also { writes ->
                    start.complete(Unit)
                    writes.awaitAll()
                }

                assertEquals(
                    10L,
                    dao.get("00000000-0000-0000-0000-000000000001", "PC_TO_ANDROID")
                        ?.highestContiguousSequence,
                )
            }
        } finally {
            dispatcher.close()
            executor.shutdownNow()
        }
    }

    @Test
    fun resetFencesDelayedWritersFromThePreviousRendererGeneration() = runTest {
        val dao = database.ackCursors()
        val sessionId = "00000000-0000-0000-0000-000000000002"
        val direction = "PC_TO_ANDROID"
        val initial = dao.getOrCreate(
            AckCursorEntity(sessionId, direction, 1, 0, 1),
        )
        assertEquals(1L, initial.rendererGeneration)

        assertEquals(
            true,
            dao.advance(AckCursorEntity(sessionId, direction, 1, 9, 2)),
        )
        val reset = dao.reset(sessionId, direction, expectedGeneration = 1, updatedAtEpochMillis = 3)
        assertEquals(2L, reset.rendererGeneration)
        assertEquals(0L, reset.highestContiguousSequence)

        assertEquals(
            false,
            dao.advance(AckCursorEntity(sessionId, direction, 1, 99, 4)),
        )
        assertEquals(
            true,
            dao.advance(AckCursorEntity(sessionId, direction, 2, 5, 5)),
        )
        assertEquals(5L, dao.get(sessionId, direction)?.highestContiguousSequence)
        assertEquals(2L, dao.get(sessionId, direction)?.rendererGeneration)
    }
}

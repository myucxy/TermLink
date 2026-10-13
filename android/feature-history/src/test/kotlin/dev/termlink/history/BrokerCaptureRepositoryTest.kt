package dev.termlink.history

import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.test.core.app.ApplicationProvider
import dev.termlink.database.TermLinkDatabase
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.model.TerminalBufferLimitException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.Executor
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
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
class BrokerCaptureRepositoryTest {
    private lateinit var database: TermLinkDatabase
    private lateinit var repository: HistoryRepository
    private lateinit var queries: MutableList<String>
    private val budget = TerminalBufferBudget { HeapState(192L * MIB, 0) }
    private val sessionId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
    private val captureId = UUID.fromString("11234567-89ab-cdef-0123-456789abcdef")
    private val otherCaptureId = UUID.fromString("21234567-89ab-cdef-0123-456789abcdef")
    private val keyEpoch = 7L
    private val sourceSequence = 17uL
    private val keyId = "broker_capture_v1:0123456789abcdef0123456789abcdef:7"

    @Before
    fun setUp() {
        queries = mutableListOf()
        database = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(), TermLinkDatabase::class.java,
        ).allowMainThreadQueries().setQueryCallback(
            object : RoomDatabase.QueryCallback {
                override fun onQuery(sqlQuery: String, bindArgs: List<Any?>) {
                    synchronized(queries) { queries += sqlQuery }
                }
            },
            Executor { it.run() },
        ).build()
        repository = HistoryRepository(database, clockMillis = { 5_000 }, bufferBudget = budget)
    }

    @After
    fun tearDown() {
        database.close()
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun roundTripUsesExactIdentityAndDoesNotReadWholeBlobFromRoom(): Unit = runBlocking {
        val fixture = sealedCapture()
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed, fixture.ciphertextSha256,
        ).use { stored ->
            assertEquals(fixture.sealed.size.toLong(), stored.metadata.sizeBytes)
            assertArrayEquals(fixture.sealed, stored.ciphertext)
            assertEquals(HistoryRepository.BROKER_CAPTURE_SEALED_OVERHEAD_BYTES,
                stored.metadata.sizeBytes - stored.metadata.canonicalBytes)
        }
        queries.clear()
        repository.getBrokerCapture(sessionId, captureId, keyEpoch).use { loaded ->
            assertNotNull(loaded)
            assertArrayEquals(fixture.sealed, requireNotNull(loaded).ciphertext)
        }
        assertTrue(queries.any { it.contains("SUBSTR(ciphertext") })
        assertFalse(queries.any { it.contains("SELECT * FROM broker_captures") })
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun unicodeCaptureHeaderIsPreservedAndUnknownContractIsRejected(): Unit = runBlocking {
        val fixture = sealedCapture(stateContract = "terminal_state_2_22_v2")
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed, fixture.ciphertextSha256,
        ).use { assertArrayEquals(fixture.sealed, it.ciphertext) }
        repository.getBrokerCapture(sessionId, captureId, keyEpoch).use {
            assertArrayEquals(fixture.sealed, requireNotNull(it).ciphertext)
        }
        val unknown = sealedCapture(stateContract = "terminal_state_2_22_v3")
        assertFailsWith<IllegalArgumentException> {
            repository.putBrokerCapture(
                sessionId, captureId, sourceSequence, keyEpoch, unknown.canonical.size,
                unknown.canonicalSha256, keyId, unknown.sealed, unknown.ciphertextSha256,
            )
        }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun multiMegabyteCaptureIsReadInBoundedSlices(): Unit = runBlocking {
        val fixture = sealedCapture(canonicalSize = 2 * MIB.toInt() + 1_024)
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed,
        ).use { assertEquals(fixture.sealed.size, it.ciphertext.size) }
        queries.clear()
        repository.getBrokerCapture(sessionId, captureId, keyEpoch).use { loaded ->
            assertArrayEquals(fixture.sealed, requireNotNull(loaded).ciphertext)
        }
        val chunks = queries.count { it.contains("SUBSTR(ciphertext") }
        assertTrue("expected multiple bounded reads, got $chunks", chunks > 4)
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun retriesDoNotReplaceExistingNonceAndSourceCollisionIsRejected(): Unit = runBlocking {
        val first = sealedCapture()
        val secondNonce = first.sealed.copyOf().also { it[128] = (it[128].toInt() xor 1).toByte() }
        val secondDigest = sha256(secondNonce)
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, first.canonical.size,
            first.canonicalSha256, keyId, first.sealed,
        ).close()
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, first.canonical.size,
            first.canonicalSha256, keyId, secondNonce, secondDigest,
        ).use { returned -> assertArrayEquals(first.sealed, returned.ciphertext) }
        assertFailsWith<IllegalArgumentException> {
            runBlocking {
                repository.putBrokerCapture(
                    sessionId, otherCaptureId, sourceSequence, keyEpoch, first.canonical.size,
                    first.canonicalSha256, keyId, first.sealed,
                )
            }
        }
        assertEquals(1, database.openHelper.writableDatabase.query(
            "SELECT COUNT(*) FROM broker_captures",
        ).use { cursor -> check(cursor.moveToFirst()); cursor.getInt(0) })
    }

    @Test
    fun putTransfersOnlySameLiveSufficientPermitAndDuplicateReleasesTemporaryCopy(): Unit = runBlocking {
        val fixture = sealedCapture()
        val foreign = TerminalBufferBudget { HeapState(192L * MIB, 0) }
        val foreignPermit = foreign.reserve(fixture.sealed.size.toLong())
        assertFailsWith<IllegalArgumentException> {
            repository.putBrokerCapture(
                sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
                fixture.canonicalSha256, keyId, fixture.sealed, admittedBy = foreignPermit,
            )
        }
        foreignPermit.close()

        val closed = budget.reserve(fixture.sealed.size.toLong())
        closed.close()
        assertFailsWith<IllegalStateException> {
            repository.putBrokerCapture(
                sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
                fixture.canonicalSha256, keyId, fixture.sealed, admittedBy = closed,
            )
        }

        val moved = budget.reserve(1)
        assertFailsWith<TerminalBufferLimitException> {
            repository.putBrokerCapture(
                sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
                fixture.canonicalSha256, keyId, fixture.sealed, admittedBy = moved,
            )
        }
        moved.close()
        assertEquals(0L, budget.reservedBytes)

        val original = budget.reserve(fixture.sealed.size.toLong())
        val stored = repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed, admittedBy = original,
        )
        original.close()
        assertTrue(budget.reservedBytes > 0)
        stored.close()
        assertEquals(0L, budget.reservedBytes)

        val retryPermit = budget.reserve(fixture.sealed.size.toLong())
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed, admittedBy = retryPermit,
        ).use { retry ->
            retryPermit.close()
            assertArrayEquals(fixture.sealed, retry.ciphertext)
            assertTrue(budget.reservedBytes > 0)
        }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun malformedHeaderAndCorruptStoredBytesAreRejectedBeforeReturningMemory(): Unit = runBlocking {
        val fixture = sealedCapture()
        val malformed = fixture.sealed.copyOf().also { it[0] = 'X'.code.toByte() }
        assertFailsWith<IllegalArgumentException> {
            repository.putBrokerCapture(
                sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
                fixture.canonicalSha256, keyId, malformed,
            )
        }
        assertEquals(0L, budget.reservedBytes)
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed,
        ).close()
        database.openHelper.writableDatabase.execSQL(
            "UPDATE broker_captures SET ciphertext = X'01020304' WHERE sessionId = ?",
            arrayOf(sessionId.toString()),
        )
        assertFailsWith<HistoryIntegrityException> {
            repository.getBrokerCapture(sessionId, captureId, keyEpoch)
        }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun clearHistoryRemovesCapturesButLeavesOtherTablesUntouched(): Unit = runBlocking {
        val fixture = sealedCapture()
        repository.putBrokerCapture(
            sessionId, captureId, sourceSequence, keyEpoch, fixture.canonical.size,
            fixture.canonicalSha256, keyId, fixture.sealed,
        ).close()
        assertNotNull(repository.getBrokerCapture(sessionId, captureId, keyEpoch)?.also { it.close() })
        repository.clearHistory(sessionId)
        assertNull(repository.getBrokerCapture(sessionId, captureId, keyEpoch))
        assertEquals(0L, budget.reservedBytes)
    }

    private data class Fixture(
        val canonical: ByteArray,
        val canonicalSha256: ByteArray,
        val sealed: ByteArray,
        val ciphertextSha256: ByteArray,
    )

    private fun sealedCapture(canonicalSize: Int = 136, stateContract: String = "terminal_state_2_22_v1"): Fixture {
        val canonical = ByteArray(canonicalSize)
        val canonicalView = ByteBuffer.wrap(canonical).order(ByteOrder.LITTLE_ENDIAN)
        "TLCAPV1\u0000".encodeToByteArray().copyInto(canonical)
        canonicalView.putInt(8, 32)
        canonicalView.putInt(12, 1)
        canonicalView.putLong(16, sourceSequence.toLong())
        canonicalView.putInt(24, canonical.size)
        canonicalView.putInt(28, 0)
        val canonicalSha = sha256(canonical)
        val sealed = ByteArray(canonical.size + HistoryRepository.BROKER_CAPTURE_SEALED_OVERHEAD_BYTES.toInt())
        val view = ByteBuffer.wrap(sealed).order(ByteOrder.BIG_ENDIAN)
        "TLBCSEA1".encodeToByteArray().copyInto(sealed)
        view.putShort(8, 1)
        view.putShort(10, HistoryRepository.BROKER_CAPTURE_HEADER_BYTES.toShort())
        sessionId.uuidBytes().copyInto(sealed, 12)
        UUID.fromString("31234567-89ab-cdef-0123-456789abcdef").uuidBytes().copyInto(sealed, 28)
        UUID.fromString("41234567-89ab-cdef-0123-456789abcdef").uuidBytes().copyInto(sealed, 44)
        captureId.uuidBytes().copyInto(sealed, 60)
        view.putLong(76, sourceSequence.toLong())
        view.putLong(84, keyEpoch)
        view.putInt(92, canonical.size)
        canonicalSha.copyInto(sealed, 96)
        ByteArray(12) { it.toByte() }.copyInto(sealed, 128)
        stateContract.encodeToByteArray().copyInto(sealed, 140)
        canonical.copyInto(sealed, HistoryRepository.BROKER_CAPTURE_HEADER_BYTES)
        return Fixture(canonical, canonicalSha, sealed, sha256(sealed))
    }

    private fun UUID.uuidBytes(): ByteArray = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        .putLong(mostSignificantBits).putLong(leastSignificantBits).array()

    private fun sha256(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)

    private companion object {
        const val MIB = 1024L * 1024
    }
}

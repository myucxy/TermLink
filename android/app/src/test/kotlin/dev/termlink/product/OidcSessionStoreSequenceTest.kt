package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class OidcSessionStoreSequenceTest {
    private lateinit var context: Context

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        context.deleteSharedPreferences(PREFERENCES_NAME)
    }

    @After
    fun tearDown() {
        context.deleteSharedPreferences(PREFERENCES_NAME)
    }

    @Test
    fun envelopeSequenceReservationsRemainDisjointAcrossStoreInstances() {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val first = OidcSessionStore(context).reserveEnvelopeSequenceRange(
            sessionId = sessionId,
            count = 4,
            clockMillis = 1_000,
        )
        val second = OidcSessionStore(context).reserveEnvelopeSequenceRange(
            sessionId = sessionId,
            count = 4,
            clockMillis = 1_000,
        )

        assertEquals(4, first.count())
        assertEquals(4, second.count())
        assertEquals(first.last + 1, second.first)
        assertTrue(first.last < second.first)
    }

    @Test
    fun inputAndEnvelopeSequencesUseIndependentPersistentNamespaces() {
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val store = OidcSessionStore(context)
        val input = store.reserveInputSequenceRange(sessionId, count = 2, clockMillis = 2_000)
        val envelope = store.reserveEnvelopeSequenceRange(sessionId, count = 2, clockMillis = 2_000)
        val nextInput = OidcSessionStore(context).reserveInputSequenceRange(
            sessionId,
            count = 2,
            clockMillis = 2_000,
        )
        val nextEnvelope = OidcSessionStore(context).reserveEnvelopeSequenceRange(
            sessionId,
            count = 2,
            clockMillis = 2_000,
        )

        assertEquals(input, envelope)
        assertEquals(input.last + 1, nextInput.first)
        assertEquals(envelope.last + 1, nextEnvelope.first)
    }

    @Test
    fun concurrentStoreInstancesCannotReserveOverlappingEnvelopeRanges() {
        val sessionId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val workers = 32
        val ready = CountDownLatch(workers)
        val start = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(workers)
        try {
            val reservations = (0 until workers).map {
                executor.submit<LongRange> {
                    val store = OidcSessionStore(context)
                    ready.countDown()
                    check(start.await(5, TimeUnit.SECONDS))
                    store.reserveEnvelopeSequenceRange(sessionId, count = 2, clockMillis = 3_000)
                }
            }
            assertTrue(ready.await(5, TimeUnit.SECONDS))
            start.countDown()
            val ranges = reservations.map { it.get(10, TimeUnit.SECONDS) }.sortedBy(LongRange::first)

            assertEquals(workers, ranges.size)
            assertTrue(ranges.zipWithNext().all { (previous, current) -> previous.last < current.first })
            assertEquals(workers * 2, ranges.flatMap { it.toList() }.distinct().size)
        } finally {
            start.countDown()
            executor.shutdownNow()
        }
    }

    private companion object {
        const val PREFERENCES_NAME = "termlink-product-session"
    }
}

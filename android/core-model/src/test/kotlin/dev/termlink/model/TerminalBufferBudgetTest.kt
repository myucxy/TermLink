package dev.termlink.model

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.test.assertFailsWith

class TerminalBufferBudgetTest {
    private val mib = 1024L * 1024

    @Test
    fun deviceWith192MiBHeapAdmitsSafeObjectsAndRejectsLargeSnapshot() {
        val budget = TerminalBufferBudget { HeapState(192 * mib, 32 * mib) }
        val reservation = budget.reserve(8 * mib)
        assertEquals(65 * mib, budget.reservedBytes)
        assertFailsWith<TerminalBufferLimitException> { budget.reserve(4 * mib) }
        assertEquals(65 * mib, budget.reservedBytes)
        reservation.close()
        assertEquals(0L, budget.reservedBytes)
        assertFailsWith<TerminalBufferLimitException> { budget.reserve(128 * mib) }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun preserves32MiBHeapHeadroomAndCountsExistingReservations() {
        var heap = HeapState(192 * mib, 142 * mib)
        val budget = TerminalBufferBudget { heap }
        val first = budget.reserve(2 * mib)
        assertEquals(17 * mib, budget.reservedBytes)
        assertFailsWith<TerminalBufferLimitException> { budget.reserve(1) }
        assertEquals(17 * mib, budget.reservedBytes)
        first.close()
        heap = HeapState(192 * mib, 161 * mib)
        assertFailsWith<TerminalBufferLimitException> { budget.reserve(1) }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun enforces512MiBCapAndChecksLongOverflowWithoutChangingReservations() {
        val budget = TerminalBufferBudget { HeapState(Long.MAX_VALUE, 0) }
        val first = budget.reserve(63 * mib) // 505 MiB including overhead.
        assertEquals(505 * mib, budget.reservedBytes)
        for (size in listOf(0L, -1L, Long.MAX_VALUE, Long.MAX_VALUE / 8, mib)) {
            val failure = assertFailsWith<TerminalBufferLimitException> { budget.reserve(size) }
            assertEquals("TERMINAL_HISTORY_MEMORY_LIMIT", failure.code)
            assertEquals(failure.code, failure.message)
            assertEquals(505 * mib, budget.reservedBytes)
        }
        first.close()
        first.close()
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun malformedHeapProbesFailClosed() {
        for (heap in listOf(HeapState(0, 0), HeapState(-1, 0), HeapState(192 * mib, -1), HeapState(mib, 2 * mib))) {
            val budget = TerminalBufferBudget { heap }
            assertFailsWith<TerminalBufferLimitException> { budget.reserve(1) }
            assertEquals(0L, budget.reservedBytes)
        }
    }

    @Test
    fun concurrentReservationsNeverExceedTotalBudgetAndReleaseExactlyOnce() {
        val budget = TerminalBufferBudget { HeapState(192 * mib, 0) }
        val executor = Executors.newFixedThreadPool(12)
        val start = CountDownLatch(1)
        try {
            val attempts = (0 until 12).map {
                executor.submit<TerminalBufferBudget.Reservation?> {
                    check(start.await(2, TimeUnit.SECONDS))
                    try { budget.reserve(2 * mib) } catch (_: TerminalBufferLimitException) { null }
                }
            }
            start.countDown()
            val reservations = attempts.mapNotNull { it.get(2, TimeUnit.SECONDS) }
            assertEquals(5, reservations.size) // 5 * 17 MiB fits the 96 MiB ceiling.
            assertEquals(85 * mib, budget.reservedBytes)
            val releases = (0 until 24).map { executor.submit { reservations.forEach { it.close() } } }
            releases.forEach { it.get(2, TimeUnit.SECONDS) }
            assertEquals(0L, budget.reservedBytes)
        } finally {
            executor.shutdownNow()
            assertTrue(executor.awaitTermination(2, TimeUnit.SECONDS))
        }
    }

    @Test
    fun manySmallObjectsHaveNoIndependentCountLimit() {
        val budget = TerminalBufferBudget { HeapState(192 * mib, 0) }
        val reservations = (0 until 9).map { budget.reserve(1) }
        assertEquals(9 * (mib + 8), budget.reservedBytes)
        reservations.forEach { it.close() }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun transferCreatesOneNewOwnerAndOldCloseDoesNotReleaseIt() {
        val budget = TerminalBufferBudget { HeapState(192 * mib, 0) }
        val original = budget.reserve(2 * mib)
        val moved = original.transferTo(budget, 2 * mib)
        assertEquals(17 * mib, budget.reservedBytes)
        original.close()
        original.close()
        assertEquals(17 * mib, budget.reservedBytes)
        moved.close()
        moved.close()
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun transferRejectsClosedSecondUseInsufficientCapacityAndForeignBudget() {
        val budget = TerminalBufferBudget { HeapState(192 * mib, 0) }
        val foreign = TerminalBufferBudget { HeapState(192 * mib, 0) }
        val original = budget.reserve(2 * mib)
        assertFailsWith<TerminalBufferLimitException> { original.transferTo(budget, 3 * mib) }
        assertFailsWith<IllegalArgumentException> { original.transferTo(foreign, 1) }
        assertEquals(17 * mib, budget.reservedBytes)
        val moved = original.transferTo(budget, 1)
        assertFailsWith<IllegalStateException> { original.transferTo(budget, 1) }
        original.close()
        assertEquals(17 * mib, budget.reservedBytes)
        moved.close()
        assertEquals(0L, budget.reservedBytes)

        val closed = budget.reserve(1)
        closed.close()
        assertFailsWith<IllegalStateException> { closed.transferTo(budget, 1) }
        assertEquals(0L, budget.reservedBytes)
    }
}

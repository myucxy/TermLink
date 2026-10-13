package dev.termlink.model

import java.util.concurrent.atomic.AtomicInteger

data class HeapState(val maxBytes: Long, val usedBytes: Long)

/** A process-wide reservation for the simultaneous encrypted, decoded and native history copies. */
class TerminalBufferBudget(
    private val heapState: () -> HeapState = {
        val runtime = Runtime.getRuntime()
        HeapState(runtime.maxMemory(), runtime.totalMemory() - runtime.freeMemory())
    },
) {
    companion object {
        val global = TerminalBufferBudget()
        private const val MIB = 1024L * 1024
        private const val MAX_RESERVED_BYTES = 512 * MIB
        private const val HEAP_HEADROOM_BYTES = 32 * MIB
        private const val OBJECT_OVERHEAD_BYTES = MIB
    }

    private var reserved = 0L
    val reservedBytes: Long
        @Synchronized get() = reserved

    @Synchronized
    fun reserve(objectBytes: Long): Reservation {
        val cost = admissionCost(objectBytes)
        val heap = heapState()
        if (heap.maxBytes <= 0 || heap.usedBytes !in 0..heap.maxBytes) throw TerminalBufferLimitException()
        val ceiling = minOf(heap.maxBytes / 2, MAX_RESERVED_BYTES)
        val available = heap.maxBytes - heap.usedBytes - HEAP_HEADROOM_BYTES
        if (cost > ceiling - reserved || cost > available - reserved) throw TerminalBufferLimitException()
        // Construct before recording ownership so even a failed allocation cannot leak the count.
        val reservation = Reservation(this, cost)
        reserved += cost // The checked ceilings above keep the sum within 512 MiB.
        return reservation
    }

    @Synchronized
    private fun release(cost: Long) {
        reserved -= cost
    }

    private fun admissionCost(objectBytes: Long): Long {
        if (objectBytes <= 0) throw TerminalBufferLimitException()
        return try {
            Math.addExact(Math.multiplyExact(objectBytes, 8L), OBJECT_OVERHEAD_BYTES)
        } catch (_: ArithmeticException) {
            throw TerminalBufferLimitException()
        }
    }

    /** A single-owner permit; transfer preserves the one accounted allocation. */
    class Reservation internal constructor(
        private val budget: TerminalBufferBudget,
        private val cost: Long,
    ) : AutoCloseable {
        private companion object {
            const val ACTIVE = 0
            const val CLOSED = 1
            const val TRANSFERRED = 2
        }

        private val state = AtomicInteger(ACTIVE)

        /**
         * Transfers this permit exactly once to the same budget.  The returned
         * owner carries the existing accounting; closing this handle thereafter
         * is deliberately a no-op.
         */
        fun transferTo(target: TerminalBufferBudget, objectBytes: Long): Reservation {
            require(target === budget) { "buffer reservation belongs to another budget" }
            synchronized(this) {
                when (state.get()) {
                    CLOSED -> throw IllegalStateException("buffer reservation is already closed")
                    TRANSFERRED -> throw IllegalStateException("buffer reservation was already transferred")
                    ACTIVE -> Unit
                    else -> error("unknown buffer reservation state")
                }
                val requiredCost = budget.admissionCost(objectBytes)
                if (cost < requiredCost) throw TerminalBufferLimitException()
                val next = Reservation(budget, cost)
                check(state.compareAndSet(ACTIVE, TRANSFERRED)) {
                    "buffer reservation changed during transfer"
                }
                return next
            }
        }

        override fun close() {
            if (state.compareAndSet(ACTIVE, CLOSED)) budget.release(cost)
        }
    }
}

/** Fixed, content-free reason safe to carry through product error reporting. */
class TerminalBufferLimitException : IllegalStateException(CODE) {
    val code: String get() = CODE

    companion object {
        const val CODE = "TERMINAL_HISTORY_MEMORY_LIMIT"
    }
}

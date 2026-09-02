package dev.termlink.product

import java.io.Closeable
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ProductRepositoryViewModelTest {
    @Test
    fun staleActivityDetachCannotRemoveConfigurationSuccessor() {
        val registry = LatestAttachmentRegistry<String>()
        val retired = registry.attach("retired")
        val successor = registry.attach("successor")

        retired.close()
        assertEquals("successor", registry.current())

        successor.close()
        assertNull(registry.current())
    }

    @Test
    fun realOwnerClearClosesRepositoryExactlyOnce() {
        val closes = AtomicInteger(0)
        val lifetime = ProductRepositoryLifetime(Closeable { closes.incrementAndGet() })

        lifetime.close()
        lifetime.close()

        assertEquals(1, closes.get())
    }

    @Test
    fun foregroundGenerationSurvivesSuccessiveActivityOwners() {
        val clock = ProductForegroundGenerationClock()

        val retiredActivityGeneration = clock.next()
        val successorActivityGeneration = clock.next()

        assertEquals(1L, retiredActivityGeneration)
        assertEquals(2L, successorActivityGeneration)
    }

    @Test
    fun foregroundGenerationFailsClosedAtExhaustion() {
        val clock = ProductForegroundGenerationClock(Long.MAX_VALUE)

        assertThrows(IllegalStateException::class.java) { clock.next() }
    }
}

package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class HomeGreetingTest {
    @Test
    fun greetingTracksMorningAfternoonAndEveningBoundaries() {
        assertEquals(HomeGreetingPeriod.EVENING, homeGreetingPeriod(0))
        assertEquals(HomeGreetingPeriod.MORNING, homeGreetingPeriod(5))
        assertEquals(HomeGreetingPeriod.MORNING, homeGreetingPeriod(11))
        assertEquals(HomeGreetingPeriod.AFTERNOON, homeGreetingPeriod(12))
        assertEquals(HomeGreetingPeriod.AFTERNOON, homeGreetingPeriod(17))
        assertEquals(HomeGreetingPeriod.EVENING, homeGreetingPeriod(18))
        assertEquals(HomeGreetingPeriod.EVENING, homeGreetingPeriod(23))
    }

    @Test
    fun invalidHourIsRejected() {
        assertThrows(IllegalArgumentException::class.java) { homeGreetingPeriod(-1) }
        assertThrows(IllegalArgumentException::class.java) { homeGreetingPeriod(24) }
    }
}

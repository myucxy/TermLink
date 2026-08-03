package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class ProductStateTest {
    @Test
    fun unavailableRepositoryStartsSignedOutAndUnconfigured() {
        val repository = UnavailableProductRepository()

        assertFalse(repository.snapshot.value.signedIn)
        assertFalse(repository.snapshot.value.configured)
        assertEquals(emptyList<Any>(), repository.snapshot.value.devices)
    }

    @Test
    fun formatterDoesNotInventAnEntitlementForUnknownLimit() {
        assertEquals("2 / unavailable", ProductFormatter.quotaLabel(2, 0))
        assertEquals("2 / 5", ProductFormatter.quotaLabel(2, 5))
        assertEquals("Pending", ProductFormatter.trustLabel(DeviceTrustState.PENDING))
    }
}

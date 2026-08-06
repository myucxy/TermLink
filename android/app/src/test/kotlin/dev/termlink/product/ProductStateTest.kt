package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcSummary
import java.time.Instant
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
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

    @Test
    fun phonePrimaryNavigationKeepsNestedRoutesUnderTheirParent() {
        assertEquals(ProductPrimaryDestination.DEVICES, ProductDestination.SESSIONS.primaryDestination())
        assertEquals(ProductPrimaryDestination.ACTIVITY, ProductDestination.HISTORY.primaryDestination())
        assertEquals(ProductPrimaryDestination.SETTINGS, ProductDestination.MEMBERSHIP.primaryDestination())
        assertEquals(ProductDestination.SETTINGS, ProductPrimaryDestination.SETTINGS.destination())
    }

    @Test
    fun removedProductSurfacesAreNotVisibleButRemainRepresentable() {
        assertTrue(ProductDestination.DEVICES.isVisibleInProductShell)
        assertTrue(ProductDestination.ACCOUNT.isVisibleInProductShell)
        assertFalse(ProductDestination.MEMBERSHIP.isVisibleInProductShell)
        assertFalse(ProductDestination.TRUSTED_DEVICES.isVisibleInProductShell)
        assertEquals("https://termlink.kget.cc:8910", PRODUCT_PUBLIC_SERVER_URL)
    }

    @Test
    fun themePreferenceDefaultsToSystemForUnknownStoredValue() {
        assertEquals(ProductThemePreference.SYSTEM, ProductThemePreference.fromWireValue(null))
        assertEquals(ProductThemePreference.SYSTEM, ProductThemePreference.fromWireValue("unexpected"))
        assertEquals(ProductThemePreference.DARK, ProductThemePreference.fromWireValue("dark"))
    }

    @Test
    fun pendingDeviceDoesNotOfferSelfApprovalWithoutManageablePc() {
        val waiting = ProductSnapshot(accountLabel = "Approval pending")
        assertFalse(waiting.canApprovePendingDevice())

        val manageablePc = PcSummary(
            id = UUID.randomUUID(),
            deviceId = UUID.randomUUID(),
            name = "Office",
            agentVersion = "test",
            remoteRoutingEnabled = true,
            online = true,
            lastSeenAt = Instant.EPOCH,
        )
        assertFalse(waiting.copy(pcs = listOf(manageablePc)).canApprovePendingDevice())
        assertEquals(true, ProductSnapshot(pcs = listOf(manageablePc)).canApprovePendingDevice())
    }

    @Test
    fun connectionStateDoesNotTreatConfiguredOrSignedInAsConnected() {
        assertFalse(ProductSnapshot(configured = true).isConnected())
        assertFalse(ProductSnapshot(signedIn = true).isConnected())
        assertTrue(ProductSnapshot(connectionState = ProductConnectionState.CONNECTED).isConnected())
    }

    @Test
    fun localActivityIsBoundedAndMarkedDeviceLocal() {
        var snapshot = ProductSnapshot()
        repeat(MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES + 7) { index ->
            snapshot = snapshot.withLocalActivity(
                ActivityEntry(
                    id = index.toString(),
                    occurredAt = Instant.EPOCH.plusSeconds(index.toLong()),
                    title = "event-$index",
                    detail = "local",
                ),
            )
        }

        assertEquals(MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES, snapshot.activity.size)
        assertEquals("event-${MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES + 6}", snapshot.activity.first().title)
        assertTrue(snapshot.activity.all { it.source == ActivitySource.DEVICE_LOCAL })
    }
}

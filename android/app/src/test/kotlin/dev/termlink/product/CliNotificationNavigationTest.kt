package dev.termlink.product

import android.app.PendingIntent
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import java.time.Instant
import java.util.UUID
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class CliNotificationNavigationTest {
    private val pcId = UUID.randomUUID()
    private val deviceId = UUID.randomUUID()
    private val first = UUID.randomUUID()
    private val second = UUID.randomUUID()
    private val account = "issuer:subject"

    @Test fun separateSessionsHaveSeparateNotificationIntentsAndOpenOnlyTheirOwnTab() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val firstTicket = UUID.randomUUID().toString()
        val secondTicket = UUID.randomUUID().toString()
        val firstIntent = cliNotificationIntent(context, firstTicket)
        val secondIntent = cliNotificationIntent(context, secondTicket)
        assertEquals(firstTicket, cliNotificationTicket(firstIntent))
        assertEquals(secondTicket, cliNotificationTicket(secondIntent))
        assertNull(cliNotificationTicket(firstIntent.setAction("different")))
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val firstPending = PendingIntent.getActivity(context, 0, cliNotificationIntent(context, firstTicket), flags)
        val secondPending = PendingIntent.getActivity(context, 0, secondIntent, flags)
        try { assertNotEquals(firstPending, secondPending) }
        finally { firstPending.cancel(); secondPending.cancel() }
        assertEquals(first, cliNotificationTarget(snapshot(), ticket(first), account))
        assertEquals(second, cliNotificationTarget(snapshot(), ticket(second), account))
    }

    @Test fun wrongAccountLogoutAndStaleCatalogCannotNavigate() {
        val active = snapshot()
        assertNull(cliNotificationTarget(active, ticket(first), "other-account"))
        assertNull(cliNotificationTarget(active, ticket(first), null))
        assertNull(cliNotificationTarget(active.copy(signedIn = false,
            connectionState = ProductConnectionState.SIGNED_OUT), ticket(first), account))
        assertNull(cliNotificationTarget(active.copy(sessions = listOf(session(second))), ticket(first), account))
        assertNull(cliNotificationTarget(active.copy(pcs = active.pcs.map { it.copy(online = false) }), ticket(first), account))
        assertNull(cliNotificationTarget(active, ticket(first).put("route", "ai"), account))
    }

    @Test fun coldStartWaitsForWindowsCatalogThenOpensOnlyTheConfirmedSession() {
        val loading = snapshot().copy(sessions = emptyList(), pcSessionCatalogObservations = emptyMap())
        assertTrue(cliNotificationCatalogPending(loading))
        assertNull(cliNotificationTarget(loading, ticket(first), account))

        val loaded = snapshot()
        assertFalse(cliNotificationCatalogPending(loaded))
        assertEquals(first, cliNotificationTarget(loaded, ticket(first), account))
        assertNull(cliNotificationTarget(loaded.copy(sessions = listOf(session(second))), ticket(first), account))
        assertTrue(cliNotificationCatalogPending(loaded.copy(pcSessionCatalogErrors = mapOf(pcId to "TIMEOUT"))))
    }

    @Test fun expiredCurrentAccountTicketCanReleaseFullCapacityWithoutTouchingAnotherAccount() {
        val now = 1000L
        val entries = (0 until 254).associate { "valid-$it" to
            "cli:$account:${JSONObject().put("expiresAt", now + 1)}" }.toMutableMap<String, Any>()
        entries["expired"] = "cli:$account:${JSONObject().put("expiresAt", now)}"
        entries["other-account"] = "cli:other:${JSONObject().put("expiresAt", now + 1)}"
        assertEquals(256, entries.size)
        val expired = expiredCliTicketKeys(entries, account, now) { scope, sealed ->
            require(sealed.startsWith("$scope:"))
            sealed.removePrefix("$scope:")
        }
        assertEquals(listOf("expired"), expired)
        expired.forEach { entries.remove(it) }
        assertEquals(255, entries.size)
        assertTrue("other-account" in entries)
    }

    private fun snapshot() = ProductSnapshot(signedIn = true,
        connectionState = ProductConnectionState.CONNECTED,
        pcs = listOf(PcSummary(pcId, deviceId, "PC", "test", remoteRoutingEnabled = true,
            online = true, lastSeenAt = Instant.EPOCH)),
        devices = listOf(DeviceSummary(deviceId, DeviceType.WINDOWS_PC, "PC", "key", ByteArray(32),
            trustState = DeviceTrustState.TRUSTED, createdAt = Instant.EPOCH)),
        pcSessionCatalogObservations = mapOf(pcId to SessionCatalogObservation(deviceId, 1,
            UUID.randomUUID(), 1)),
        lastSyncedAt = Instant.now(),
        sessions = listOf(session(first), session(second)))

    private fun session(id: UUID) = SessionSummary(id, pcId, "pwsh", null, SessionState.RUNNING,
        80, 24, 0uL, Instant.EPOCH, null, online = true)

    private fun ticket(id: UUID) = JSONObject().put("route", "cli")
        .put("accountScope", account).put("sessionId", id.toString())
}

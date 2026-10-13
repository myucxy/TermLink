package dev.termlink.product

import android.app.PendingIntent
import android.content.Context
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
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
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Exercises the production Android KeyStore, ticket storage and trusted-catalog route on device. */
@RunWith(AndroidJUnit4::class)
class CliNotificationNavigationInstrumentedTest {
    private val context: Context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test fun sealedTicketRoutesOnlyItsOwnConfirmedSessionAndIsOpaqueToIntentAndPreferences() {
        val account = "instrumentation:${UUID.randomUUID()}"
        val first = UUID.randomUUID()
        val second = UUID.randomUUID()
        val pc = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "PC", "test",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now())
        val device = DeviceSummary(pc.deviceId, DeviceType.WINDOWS_PC, "PC", "test", ByteArray(32),
            trustState = DeviceTrustState.TRUSTED, createdAt = Instant.now())
        val snapshot = ProductSnapshot(signedIn = true, connectionState = ProductConnectionState.CONNECTED,
            devices = listOf(device), pcs = listOf(pc),
            pcSessionCatalogObservations = mapOf(pc.id to SessionCatalogObservation(pc.deviceId,
                pc.keyEpoch, UUID.randomUUID(), System.currentTimeMillis())),
            sessions = listOf(session(first, pc.id), session(second, pc.id)), lastSyncedAt = Instant.now())
        val store = CliNotificationTickets(context)
        val firstTicket = store.save(account, body(account, first))
        val secondTicket = store.save(account, body(account, second))
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val firstIntent = cliNotificationIntent(context, firstTicket)
        val secondIntent = cliNotificationIntent(context, secondTicket)
        val firstPending = PendingIntent.getActivity(context, 0, firstIntent, flags)
        val secondPending = PendingIntent.getActivity(context, 0, secondIntent, flags)
        try {
            assertNotEquals(firstPending, secondPending)
            assertEquals(firstTicket, cliNotificationTicket(firstIntent))
            assertEquals(secondTicket, cliNotificationTicket(secondIntent))
            val persisted = context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE)
                .getString(firstTicket, null)!!
            assertFalse(persisted.contains(first.toString()))
            assertFalse(persisted.contains(account))
            assertFalse(firstIntent.toUri(0).contains(first.toString()))
            assertFalse(secondIntent.toUri(0).contains(second.toString()))
            assertEquals(first, cliNotificationTarget(snapshot, store.read(account, firstTicket)!!, account))
            assertEquals(second, cliNotificationTarget(snapshot, store.read(account, secondTicket)!!, account))
            assertNull(store.read("wrong:$account", firstTicket))
            assertNull(cliNotificationTarget(snapshot.copy(pcSessionCatalogObservations = emptyMap()),
                store.read(account, firstTicket)!!, account))
        } finally {
            firstPending.cancel(); secondPending.cancel()
            store.remove(firstTicket); store.remove(secondTicket)
        }
    }

    @Test fun expiredTicketIsRejectedAndPurgedBeforeNextNoticeWithoutDeletingOtherAccounts() {
        val account = "instrumentation:${UUID.randomUUID()}"
        val other = "other:${UUID.randomUUID()}"
        val store = CliNotificationTickets(context)
        val expired = store.save(account, body(account, UUID.randomUUID(), System.currentTimeMillis() - 1))
        val otherTicket = store.save(other, body(other, UUID.randomUUID()))
        var fresh: String? = null
        try {
            assertNull(store.read(account, expired))
            fresh = store.save(account, body(account, UUID.randomUUID()))
            val prefs = context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE)
            assertFalse(prefs.contains(expired))
            assertTrue(prefs.contains(otherTicket))
            assertNull(store.read(account, otherTicket))
            assertNull(cliNotificationTicket(cliNotificationIntent(context, expired).setAction("old-action")))
        } finally {
            store.remove(expired); store.remove(otherTicket)
            fresh?.let(store::remove)
        }
    }

    private fun session(id: UUID, pcId: UUID) = SessionSummary(id, pcId, "pwsh", null,
        SessionState.RUNNING, 80, 24, 0uL, Instant.now(), null, online = true)

    private fun body(account: String, sessionId: UUID, expiresAt: Long = System.currentTimeMillis() + 60_000) =
        JSONObject().put("route", "cli").put("accountScope", account)
            .put("sessionId", sessionId.toString()).put("expiresAt", expiresAt)
}

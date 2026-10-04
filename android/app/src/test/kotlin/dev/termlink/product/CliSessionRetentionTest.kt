package dev.termlink.product

import dev.termlink.model.*
import dev.termlink.session.RemoteSessionState
import java.time.Instant
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class CliSessionRetentionTest {
    private val pcId = UUID.randomUUID()
    private val deviceId = UUID.randomUUID()
    private val pc = PcSummary(pcId, deviceId, "PC", "1", remoteRoutingEnabled = true, online = true, lastSeenAt = null)
    private val device = DeviceSummary(deviceId, DeviceType.WINDOWS_PC, "PC", "key", ByteArray(32),
        trustState = DeviceTrustState.TRUSTED, createdAt = Instant.EPOCH)
    private val live = SessionSummary(UUID.randomUUID(), pcId, "pwsh", null, SessionState.RUNNING,
        80, 24, 0u, Instant.EPOCH, null, true)
    private val ended = live.copy(id = UUID.randomUUID(), state = SessionState.EXITED, online = false)
    private val interrupted = ended.copy(id = UUID.randomUUID(), state = SessionState.INTERRUPTED)
    private val missing = UUID.randomUUID().toString()
    private val opened = listOf(live.id.toString(), ended.id.toString(), interrupted.id.toString(), missing)
    private val confirmed = ProductSnapshot(signedIn = true, connectionState = ProductConnectionState.CONNECTED,
        pcs = listOf(pc), devices = listOf(device), sessions = listOf(live, ended, interrupted),
        pcSessionCatalogObservations = mapOf(pcId to SessionCatalogObservation(deviceId, pc.keyEpoch, UUID.randomUUID(), 1)))

    @Test fun removesOnlyConfirmedInactiveAndMissingTabsWithoutChangingWindowsDirectory() {
        assertEquals(listOf(live.id.toString()), retainedCliSessionTabs(confirmed, opened))
        assertEquals(listOf(live), visibleDeviceCliSessions(confirmed, pc))
        assertEquals(3, confirmed.sessions.size)
        val possiblyAlive = confirmed.copy(sessions = listOf(live.copy(online = false), ended.copy(online = true), interrupted.copy(online = true)))
        assertEquals(opened.dropLast(1), retainedCliSessionTabs(possiblyAlive, opened))
        assertEquals(possiblyAlive.sessions, visibleDeviceCliSessions(possiblyAlive, pc))
    }

    @Test fun temporaryUnavailabilityAndBootstrapNeverDeleteSavedTabs() {
        val uncertain = listOf(
            confirmed.copy(signedIn = false),
            confirmed.copy(connectionState = ProductConnectionState.CONNECTING),
            confirmed.copy(pcSessionCatalogErrors = mapOf(pcId to "TIMEOUT")),
            confirmed.copy(pcSessionCatalogErrors = mapOf(pcId to "UNAVAILABLE"), sessions = emptyList()),
            confirmed.copy(pcs = listOf(pc.copy(online = false)), sessions = emptyList()),
            confirmed.copy(pcs = listOf(pc.copy(remoteRoutingEnabled = false))),
            confirmed.copy(pcSessionCatalogObservations = emptyMap()),
            confirmed.copy(pcs = listOf(pc.copy(keyEpoch = pc.keyEpoch + 1))),
            confirmed.copy(devices = listOf(device.copy(trustState = DeviceTrustState.REVOKED))),
            confirmed.copy(pcs = emptyList(), sessions = emptyList()),
        )
        uncertain.forEach { assertEquals(opened, retainedCliSessionTabs(it, opened)) }
        uncertain.forEach { snapshot ->
            val devicePc = snapshot.pcs.firstOrNull() ?: pc
            assertEquals(snapshot.sessions, visibleDeviceCliSessions(snapshot, devicePc))
        }
        val reconnecting = confirmed.copy(sessions = listOf(live), sessionStates = mapOf(live.id to RemoteSessionState.Reconnecting(1, "network unavailable")))
        assertEquals(listOf(live.id.toString()), retainedCliSessionTabs(reconnecting, listOf(live.id.toString())))
    }

    @Test fun oneOfflinePcDoesNotBlockConfirmedEndedTabsButUnknownOwnersWait() {
        val offlinePc = pc.copy(id = UUID.randomUUID(), online = false)
        val offlineSession = ended.copy(id = UUID.randomUUID(), pcId = offlinePc.id)
        val partial = confirmed.copy(pcs = listOf(pc, offlinePc), sessions = confirmed.sessions + offlineSession)
        assertEquals(listOf(live.id.toString(), missing), retainedCliSessionTabs(partial, opened))
        assertEquals(listOf(live), visibleDeviceCliSessions(partial, pc))
        assertEquals(listOf(offlineSession), visibleDeviceCliSessions(partial, offlinePc))
    }
}

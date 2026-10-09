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
        val reconnecting = confirmed.copy(sessions = listOf(live), sessionStates = mapOf(live.id to RemoteSessionState.Reconnecting(1, "network unavailable")))
        assertEquals(listOf(live.id.toString()), retainedCliSessionTabs(reconnecting, listOf(live.id.toString())))
    }

    @Test fun endedDeviceRowsStayHiddenThroughoutRefreshFailureAndRecovery() {
        val refreshing = confirmed.copy(connectionState = ProductConnectionState.CONNECTING)
        val timedOut = confirmed.copy(pcSessionCatalogErrors = mapOf(pcId to "TIMEOUT"))
        val failed = timedOut.copy(connectionState = ProductConnectionState.ERROR, connectionError = "network unavailable")
        val recovered = confirmed.copy(pcSessionCatalogObservations = mapOf(pcId to
            confirmed.pcSessionCatalogObservations.getValue(pcId).copy(observedAtMillis = 2)))

        listOf(confirmed, refreshing, timedOut, failed, recovered).forEach { snapshot ->
            assertEquals(listOf(live), visibleDeviceCliSessions(snapshot, pc))
            assertEquals(listOf(live, ended, interrupted), snapshot.sessions)
        }
        listOf(refreshing, timedOut, failed).forEach { snapshot ->
            assertFalse(snapshot.hasConfirmedSessionCatalog(pc))
            assertEquals(opened, retainedCliSessionTabs(snapshot, opened))
        }
        assertEquals(listOf(live.id.toString()), retainedCliSessionTabs(recovered, opened))
        assertTrue(visibleDeviceCliSessions(refreshing.copy(sessions = listOf(ended, interrupted)), pc).isEmpty())
    }

    @Test fun deviceRowsRequireAHostObservationForTheCurrentTrustedIdentity() {
        val unverified = listOf(
            confirmed.copy(signedIn = false),
            confirmed.copy(pcSessionCatalogObservations = emptyMap()),
            confirmed.copy(pcs = listOf(pc.copy(deviceId = UUID.randomUUID()))),
            confirmed.copy(pcs = listOf(pc.copy(keyEpoch = pc.keyEpoch + 1))),
            confirmed.copy(pcs = listOf(pc.copy(online = false))),
            confirmed.copy(pcs = listOf(pc.copy(remoteRoutingEnabled = false))),
            confirmed.copy(devices = listOf(device.copy(trustState = DeviceTrustState.REVOKED))),
            confirmed.copy(devices = emptyList()),
        )
        unverified.forEach { snapshot ->
            assertTrue(visibleDeviceCliSessions(snapshot, snapshot.pcs.single()).isEmpty())
            assertEquals(opened, retainedCliSessionTabs(snapshot, opened))
        }
    }

    @Test fun refreshKeepsUncertainLiveRowsAndFreshRunningObservationCanRestoreARow() {
        val uncertainLive = live.copy(online = false)
        val aliveInterrupted = interrupted.copy(online = true)
        val refreshing = confirmed.copy(connectionState = ProductConnectionState.CONNECTING,
            sessions = listOf(uncertainLive, aliveInterrupted, ended),
            sessionStates = mapOf(live.id to RemoteSessionState.Reconnecting(1, "network unavailable")))
        assertEquals(listOf(uncertainLive, aliveInterrupted), visibleDeviceCliSessions(refreshing, pc))
        val resumed = interrupted.copy(state = SessionState.RUNNING, online = true)
        assertEquals(listOf(live, resumed), visibleDeviceCliSessions(confirmed.copy(sessions = listOf(live, resumed, ended)), pc))
        assertEquals(listOf(live), visibleDeviceCliSessions(confirmed.copy(sessions = listOf(live)), pc))
    }

    @Test fun oneOfflinePcDoesNotBlockConfirmedEndedTabsButUnknownOwnersWait() {
        val offlinePc = pc.copy(id = UUID.randomUUID(), online = false)
        val offlineSession = ended.copy(id = UUID.randomUUID(), pcId = offlinePc.id)
        val partial = confirmed.copy(pcs = listOf(pc, offlinePc), sessions = confirmed.sessions + offlineSession)
        assertEquals(listOf(live.id.toString(), missing), retainedCliSessionTabs(partial, opened))
        assertEquals(listOf(live), visibleDeviceCliSessions(partial, pc))
        assertTrue(visibleDeviceCliSessions(partial, offlinePc).isEmpty())
    }
}

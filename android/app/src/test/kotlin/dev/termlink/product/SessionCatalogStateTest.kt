package dev.termlink.product

import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import java.time.Instant
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class SessionCatalogStateTest {
    private val pcId = UUID.randomUUID()
    private val deviceId = UUID.randomUUID()
    private val pc = PcSummary(id = pcId, deviceId = deviceId, name = "PC", agentVersion = "1",
        remoteRoutingEnabled = true, online = true, lastSeenAt = null)
    private val session = SessionSummary(UUID.randomUUID(), pcId, "pwsh", null, SessionState.RUNNING,
        80, 24, 0u, Instant.EPOCH, null, true)

    @Test fun displayUsesHostAndTransportAuthorityWithoutChangingLifecycle() {
        assertEquals(SessionDisplayState.RUNNING_CONNECTED, resolveSessionDisplayState(session, pc))
        for (host in listOf(null, pc.copy(online = false), pc.copy(remoteRoutingEnabled = false))) {
            assertEquals(SessionDisplayState.HOST_OFFLINE, resolveSessionDisplayState(session, host,
                remoteState = RemoteSessionState.Controlling(1, "lease", Long.MAX_VALUE)))
        }
        for (error in listOf("TIMEOUT", "UNAVAILABLE")) {
            assertEquals(SessionDisplayState.RUNNING_UNCONFIRMED, resolveSessionDisplayState(session, pc, error))
        }
        assertEquals(SessionDisplayState.RUNNING_UNCONFIRMED, resolveSessionDisplayState(session.copy(online = false), pc))
        assertEquals(SessionDisplayState.RECONNECTING, resolveSessionDisplayState(session, pc,
            remoteState = RemoteSessionState.Reconnecting(1, "network unavailable")))
        assertEquals(SessionDisplayState.HISTORY_GAP, resolveSessionDisplayState(session, pc,
            remoteState = RemoteSessionState.HistoryGap(1, 2, 4)))
        assertEquals(SessionDisplayState.EXITED, resolveSessionDisplayState(session, pc,
            remoteState = RemoteSessionState.Exited(1, 0)))
        assertEquals(SessionDisplayState.INTERRUPTED, resolveSessionDisplayState(session.copy(state = SessionState.INTERRUPTED), pc))
        assertEquals(SessionDisplayState.INTERRUPTED, resolveSessionDisplayState(session.copy(state = SessionState.INTERRUPTED), pc,
            remoteState = RemoteSessionState.Exited(1, null))) // Read-only history has no live controller.
        assertEquals(SessionDisplayState.EXITED, resolveSessionDisplayState(session.copy(state = SessionState.EXITED), pc))
        assertEquals(SessionState.RUNNING, session.state)
    }

    @Test fun catalogFencesOldResponsesAndRetiredInstancesWithoutComparingDifferentClocks() {
        val first = SessionCatalogObservation(deviceId, 1, UUID.randomUUID(), 100)
        assertEquals(first, advanceSessionCatalogObservation(null, first))
        assertEquals(first, advanceSessionCatalogObservation(first, first)) // Compatible with old Windows millisecond ties.
        assertThrows(IllegalArgumentException::class.java) {
            advanceSessionCatalogObservation(first, first.copy(observedAtMillis = 99))
        }
        val restarted = advanceSessionCatalogObservation(first,
            first.copy(hostInstanceId = UUID.randomUUID(), observedAtMillis = 10))
        assertEquals(setOf(first.hostInstanceId), restarted.retiredHostInstances)
        assertThrows(IllegalArgumentException::class.java) { advanceSessionCatalogObservation(restarted, first) }
        assertEquals(setOf(pcId), changedSessionCatalogPcIds(mapOf(pcId to first), mapOf(pcId to restarted)))
        assertTrue(changedSessionCatalogPcIds(mapOf(pcId to first), mapOf(pcId to first.copy(observedAtMillis = 101))).isEmpty())
        val rotated = first.copy(deviceId = UUID.randomUUID(), keyEpoch = 2, observedAtMillis = 1)
        assertEquals(rotated, advanceSessionCatalogObservation(restarted, rotated))
        assertThrows(IllegalArgumentException::class.java) {
            advanceSessionCatalogObservation(null, first.copy(hostInstanceId = UUID(0, 0)))
        }
    }
}

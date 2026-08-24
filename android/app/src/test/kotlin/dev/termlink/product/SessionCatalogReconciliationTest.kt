package dev.termlink.product

import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalSize
import java.time.Instant
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionCatalogReconciliationTest {
    private val pcId = UUID.fromString("10000000-0000-0000-0000-000000000000")
    private val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
    private val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")

    @Test
    fun newSessionAppearsWithoutChangingGlobalConnectionState() {
        val current = ProductSnapshot(
            signedIn = true,
            connectionState = ProductConnectionState.ERROR,
            connectionError = "existing banner",
            pcs = listOf(pc(online = false)),
            sessions = listOf(session(sessionA)),
        )

        val reconciled = reconcileSessionCatalog(
            current = current,
            pcs = listOf(pc(online = true)),
            sessions = listOf(session(sessionA), session(sessionB)),
            deletedSessionIds = emptySet(),
        )

        assertEquals(listOf(sessionA, sessionB), reconciled.sessions.map(SessionSummary::id))
        assertTrue(reconciled.pcs.single().online)
        assertEquals(ProductConnectionState.ERROR, reconciled.connectionState)
        assertEquals("existing banner", reconciled.connectionError)
    }

    @Test
    fun removedSessionDisappearsWithEveryUuidScopedRuntimeOverlay() {
        val current = ProductSnapshot(
            sessions = listOf(session(sessionA), session(sessionB)),
            sessionStates = mapOf(
                sessionA to RemoteSessionState.Live(1),
                sessionB to RemoteSessionState.Live(2),
            ),
            sessionTitles = mapOf(sessionA to "A title", sessionB to "B title"),
            liveAcknowledgements = mapOf(sessionA to 1u, sessionB to 2u),
        )

        val reconciled = reconcileSessionCatalog(
            current,
            pcs = listOf(pc(online = false)),
            sessions = listOf(session(sessionB)),
            deletedSessionIds = emptySet(),
        )

        assertEquals(listOf(sessionB), reconciled.sessions.map(SessionSummary::id))
        assertFalse(sessionA in reconciled.sessionStates)
        assertFalse(sessionA in reconciled.sessionTitles)
        assertFalse(sessionA in reconciled.liveAcknowledgements)
        assertEquals("B title", reconciled.sessionTitles[sessionB])
    }

    @Test
    fun encryptedDirectoryTitlesReplaceClearAndFailClosedPerSession() {
        val encryptedA = session(sessionA).copy(encryptedTitle = byteArrayOf(1))
        val encryptedB = session(sessionB).copy(encryptedTitle = byteArrayOf(2))

        val replaced = reconcileSessionDirectoryTitles(
            current = mapOf(sessionA to "stale A", sessionB to "stale B"),
            sessions = listOf(encryptedA, encryptedB),
            decrypted = mapOf(sessionA to "最新标题", sessionB to ""),
        )
        assertEquals("最新标题", replaced[sessionA])
        assertFalse(sessionB in replaced)

        val failedClosed = reconcileSessionDirectoryTitles(
            current = mapOf(sessionA to "must disappear"),
            sessions = listOf(encryptedA),
            decrypted = emptyMap(),
        )
        assertFalse(sessionA in failedClosed)
    }

    @Test
    fun realtimeTitleWinsOverDirectoryReplacementAndDecryptFailure() {
        val encryptedA = session(sessionA).copy(encryptedTitle = byteArrayOf(1))

        val replaced = reconcileSessionDirectoryTitles(
            current = mapOf(sessionA to "old realtime"),
            sessions = listOf(encryptedA),
            decrypted = mapOf(sessionA to "directory title"),
            realtime = mapOf(sessionA to "new realtime"),
        )
        assertEquals("new realtime", replaced[sessionA])

        val failedDirectoryDecrypt = reconcileSessionDirectoryTitles(
            current = replaced,
            sessions = listOf(encryptedA),
            decrypted = emptyMap(),
            realtime = mapOf(sessionA to "new realtime"),
        )
        assertEquals("new realtime", failedDirectoryDecrypt[sessionA])
    }

    @Test
    fun explicitBlankRealtimeTitleClearsFallbackAndRemovedSessionCannotLeakRealtimeTitle() {
        val encryptedA = session(sessionA).copy(encryptedTitle = byteArrayOf(1))

        val reconciled = reconcileSessionDirectoryTitles(
            current = mapOf(sessionA to "old", sessionB to "removed"),
            sessions = listOf(encryptedA),
            decrypted = mapOf(sessionA to "directory fallback"),
            realtime = mapOf(sessionA to "", sessionB to "must not return"),
        )

        assertTrue(reconciled.isEmpty())
    }

    @Test
    fun unpublishedRealtimeTitleUsesFallbackButExplicitBlankDoesNot() {
        assertEquals(null, liveSessionTitleOverride(null, ""))
        assertEquals("renderer fallback", liveSessionTitleOverride(null, "renderer fallback"))
        assertEquals("published", liveSessionTitleOverride("published", "renderer fallback"))
        assertEquals("", liveSessionTitleOverride("", "renderer fallback"))
    }

    @Test
    fun legacySessionWithoutDirectoryEnvelopeRetainsRealtimeTitle() {
        val retained = reconcileSessionDirectoryTitles(
            current = mapOf(sessionA to "realtime title", sessionB to "removed"),
            sessions = listOf(session(sessionA)),
            decrypted = emptyMap(),
        )

        assertEquals(mapOf(sessionA to "realtime title"), retained)
    }

    @Test
    fun locallyDeletedUuidCannotBeResurrectedByCatalog() {
        val reconciled = reconcileSessionCatalog(
            current = ProductSnapshot(),
            pcs = listOf(pc(online = true)),
            sessions = listOf(session(sessionA), session(sessionB)),
            deletedSessionIds = setOf(sessionA),
        )

        assertEquals(listOf(sessionB), reconciled.sessions.map(SessionSummary::id))
    }

    @Test
    fun liveGridLifecycleAndTitlesStayStrictlyUuidScoped() {
        val a = projectLiveSessionSummary(
            session(sessionA, state = SessionState.INTERRUPTED),
            RemoteSessionState.Controlling(7, "lease", Long.MAX_VALUE),
            TerminalSize(132, 43),
        )
        val b = projectLiveSessionSummary(
            session(sessionB, state = SessionState.RUNNING),
            RemoteSessionState.Reconnecting(8, "remote session interrupted"),
            TerminalSize(91, 28),
        )
        val titles = mapOf(sessionA to "alpha", sessionB to "beta")

        assertEquals(SessionState.RUNNING, a.state)
        assertTrue(a.online)
        assertEquals(132, a.columns)
        assertEquals(43, a.rows)
        assertEquals(SessionState.INTERRUPTED, b.state)
        // An explicit interruption makes the opened renderer read-only, but
        // retaining online=true here keeps its detail surface visible until
        // the authoritative catalog reports EXITED or deletion.
        assertTrue(b.online)
        assertEquals(91, b.columns)
        assertEquals(28, b.rows)
        assertEquals("alpha", displaySessionTitle(a, titles[a.id]))
        assertEquals("beta", displaySessionTitle(b, titles[b.id]))
        assertEquals("pwsh", displaySessionTitle(a, "  "))
    }

    @Test
    fun genericTransportReconnectDoesNotRewriteAuthoritativeLifecycle() {
        val interrupted = session(sessionA, state = SessionState.INTERRUPTED)

        val projected = projectLiveSessionSummary(
            interrupted,
            RemoteSessionState.Reconnecting(5, "network unavailable"),
            TerminalSize(80, 24),
        )

        assertEquals(SessionState.INTERRUPTED, projected.state)
        assertTrue(projected.online)
    }

    @Test
    fun transientOfflineCatalogEntryRetainsOpenedRendererButExitedEntryDoesNot() {
        val offlineRunning = session(sessionA, state = SessionState.RUNNING).copy(online = false)
        val exited = session(sessionB, state = SessionState.EXITED).copy(online = false)

        assertEquals(
            setOf(sessionA),
            retainedLiveSessionIds(listOf(offlineRunning, exited), setOf(pcId)),
        )
        assertTrue(retainedLiveSessionIds(listOf(offlineRunning), emptySet()).isEmpty())
    }

    @Test
    fun keyEpochIsMonotonicOnlyWithinOneWindowsDeviceIdentity() {
        val oldDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val newDeviceId = UUID.fromString("30000000-0000-0000-0000-000000000000")
        val storedPc = pc(online = true).copy(deviceId = oldDeviceId, keyEpoch = 9)
        val storedDevice = device(oldDeviceId, 1)

        // A replacement Windows device has its own epoch namespace and key.
        validateAuthoritativePcKeyTransition(
            storedPc,
            storedDevice,
            storedPc.copy(deviceId = newDeviceId, keyEpoch = 1),
            device(newDeviceId, 2),
        )

        assertThrows(IllegalArgumentException::class.java) {
            validateAuthoritativePcKeyTransition(
                storedPc,
                storedDevice,
                storedPc.copy(keyEpoch = 8),
                storedDevice,
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            validateAuthoritativePcKeyTransition(
                storedPc,
                storedDevice,
                storedPc.copy(keyEpoch = 10),
                device(oldDeviceId, 3),
            )
        }
    }

    @Test
    fun replacementWindowsDeviceRetiresOnlyThatPcsLiveSessions() {
        val oldDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val newDeviceId = UUID.fromString("30000000-0000-0000-0000-000000000000")
        val otherPcId = UUID.fromString("40000000-0000-0000-0000-000000000000")
        val otherDeviceId = UUID.fromString("50000000-0000-0000-0000-000000000000")
        val stored = listOf(
            pc(online = true).copy(deviceId = oldDeviceId),
            pc(online = true).copy(id = otherPcId, deviceId = otherDeviceId),
        )
        val incoming = listOf(
            stored[0].copy(deviceId = newDeviceId, keyEpoch = 1),
            stored[1].copy(keyEpoch = 2),
        )
        val otherSession = session(sessionB).copy(pcId = otherPcId)

        assertEquals(
            setOf(sessionA),
            changedPcDeviceIdentitySessionIds(
                stored,
                incoming,
                listOf(session(sessionA), otherSession),
            ),
        )
    }

    private fun pc(online: Boolean) = PcSummary(
        id = pcId,
        deviceId = UUID.fromString("20000000-0000-0000-0000-000000000000"),
        name = "PC",
        agentVersion = "1",
        remoteRoutingEnabled = true,
        online = online,
        lastSeenAt = null,
    )

    private fun device(id: UUID, keyByte: Int) = DeviceSummary(
        id = id,
        type = DeviceType.WINDOWS_PC,
        name = "PC device",
        keyId = "device-$id",
        exchangePublicKey = ByteArray(32) { keyByte.toByte() },
        trustState = DeviceTrustState.TRUSTED,
        createdAt = Instant.EPOCH,
    )

    private fun session(
        id: UUID,
        state: SessionState = SessionState.RUNNING,
    ) = SessionSummary(
        id = id,
        pcId = pcId,
        shellType = "pwsh",
        encryptedTitle = null,
        state = state,
        columns = 80,
        rows = 24,
        lastOutputSequence = 0u,
        createdAt = Instant.EPOCH,
        endedAt = null,
        online = true,
    )
}

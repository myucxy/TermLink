package dev.termlink.product

import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalSize
import java.io.IOException
import java.net.SocketTimeoutException
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
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
    fun catalogTimeoutKeepsOnlyTheSameAuthorizedPcAndFreshReplyRemovesMissingRows() {
        val host = pc(true)
        val trusted = device(host.deviceId, 1)
        val old = ProductSnapshot(pcs = listOf(host), devices = listOf(trusted),
            sessions = listOf(session(sessionA), session(sessionB).copy(state = SessionState.EXITED, online = false)),
            sessionStates = mapOf(sessionA to RemoteSessionState.Controlling(7, "lease", Long.MAX_VALUE)))
        val timeout = mapOf(pcId to "TIMEOUT")
        val retained = retainTimedOutCatalogSessions(old, listOf(trusted), listOf(host), emptyList(), timeout, emptySet())
        assertEquals(old.sessions, retained)
        assertEquals(setOf(sessionA), retainedLiveSessionIds(retained, setOf(pcId) - blockingCatalogPcIds(timeout)))
        assertEquals(old.sessionStates, reconcileSessionCatalog(old, listOf(host), retained, emptySet()).sessionStates)
        assertEquals(listOf(sessionB), retainTimedOutCatalogSessions(old, listOf(trusted), listOf(host), emptyList(), timeout, setOf(sessionA)).map { it.id })
        for (error in listOf("OFFLINE", "UNAVAILABLE", "UPGRADE_REQUIRED")) {
            assertTrue(retainTimedOutCatalogSessions(old, listOf(trusted), listOf(host), emptyList(), mapOf(pcId to error), emptySet()).isEmpty())
            assertEquals(setOf(pcId), blockingCatalogPcIds(mapOf(pcId to error)))
        }
        for (changed in listOf(host.copy(online = false), host.copy(remoteRoutingEnabled = false),
            host.copy(keyEpoch = host.keyEpoch + 1), host.copy(deviceId = UUID.randomUUID()))) {
            assertTrue(retainTimedOutCatalogSessions(old, listOf(trusted), listOf(changed), emptyList(), timeout, emptySet()).isEmpty())
        }
        assertTrue(retainTimedOutCatalogSessions(old, emptyList(), listOf(host), emptyList(), timeout, emptySet()).isEmpty())
        assertTrue(retainTimedOutCatalogSessions(old, listOf(trusted.copy(trustState = DeviceTrustState.REVOKED)),
            listOf(host), emptyList(), timeout, emptySet()).isEmpty())
        assertTrue(retainTimedOutCatalogSessions(old, listOf(device(host.deviceId, 2)), listOf(host), emptyList(), timeout, emptySet()).isEmpty())
        assertTrue(retainTimedOutCatalogSessions(old, listOf(trusted), emptyList(), emptyList(), timeout, emptySet()).isEmpty())
        val fresh = listOf(session(sessionA).copy(state = SessionState.EXITED, online = false))
        assertEquals(fresh, retainTimedOutCatalogSessions(old, listOf(trusted), listOf(host), fresh, emptyMap(), emptySet()))
        assertTrue(retainTimedOutCatalogSessions(old, listOf(trusted), listOf(host), emptyList(), emptyMap(), emptySet()).isEmpty())
    }

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
    fun historyGapAutoReopensOnlyAfterAnOnlineRunningTailAdvances() {
        val previous = ProductSnapshot(
            sessions = listOf(session(sessionA, lastOutputSequence = 4u)),
            sessionOpenFailures = mapOf(
                sessionA to SessionOpenFailure(SessionOpenFailureCode.HISTORY_GAP),
            ),
        )

        assertEquals(
            setOf(sessionA),
            historyGapSessionsReadyForAutoReopen(
                previous,
                listOf(session(sessionA, lastOutputSequence = 5u)),
            ),
        )
        assertTrue(historyGapSessionsReadyForAutoReopen(
            previous,
            listOf(session(sessionA, lastOutputSequence = 4u)),
        ).isEmpty())
        assertTrue(historyGapSessionsReadyForAutoReopen(
            previous,
            listOf(session(sessionA, state = SessionState.INTERRUPTED, lastOutputSequence = 5u)),
        ).isEmpty())
        assertTrue(historyGapSessionsReadyForAutoReopen(
            previous.copy(sessionOpenFailures = emptyMap()),
            listOf(session(sessionA, lastOutputSequence = 5u)),
        ).isEmpty())
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

        assertEquals(SessionState.INTERRUPTED, a.state)
        assertTrue(a.online)
        assertEquals(132, a.columns)
        assertEquals(43, a.rows)
        assertEquals(SessionState.INTERRUPTED, b.state)
        assertFalse(b.online)
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
    fun offlineCatalogCannotRetainLiveOrHistoryRenderers() {
        val offlineRunning = session(sessionA, state = SessionState.RUNNING).copy(online = false)
        val exited = session(sessionB, state = SessionState.EXITED).copy(online = false)

        assertEquals(
            emptySet<UUID>(),
            retainedLiveSessionIds(listOf(offlineRunning, exited), setOf(pcId)),
        )
        assertTrue(retainedLiveSessionIds(listOf(offlineRunning), emptySet()).isEmpty())

        val hostHistoryPc = pc(online = false).copy(capabilities = setOf(PcCapabilityIds.HOST_HISTORY_V1))
        assertEquals(
            emptySet<UUID>(),
            retainedReadOnlySessionIds(listOf(offlineRunning, exited), listOf(hostHistoryPc), setOf(pcId)),
        )
        assertTrue(
            retainedReadOnlySessionIds(
                listOf(exited),
                listOf(hostHistoryPc.copy(capabilities = emptySet())),
                setOf(pcId),
            ).isEmpty(),
        )
    }

    @Test
    fun keyEpochIsMonotonicOnlyWithinOneWindowsDeviceIdentity() {
        val oldDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val newDeviceId = UUID.fromString("30000000-0000-0000-0000-000000000000")
        val storedPc = pc(online = true).copy(deviceId = oldDeviceId, keyEpoch = 9)
        val storedDevice = device(oldDeviceId, 1)

        // A replacement Windows device can introduce a new key only while the
        // PC-wide epoch advances.
        validateAuthoritativePcKeyTransition(
            storedPc,
            storedDevice,
            storedPc.copy(deviceId = newDeviceId, keyEpoch = 10),
            device(newDeviceId, 2),
        )

        assertThrows(IllegalArgumentException::class.java) {
            validateAuthoritativePcKeyTransition(
                storedPc,
                storedDevice,
                storedPc.copy(deviceId = newDeviceId, keyEpoch = 9),
                device(newDeviceId, 2),
            )
        }
        assertThrows(IllegalArgumentException::class.java) {
            validateAuthoritativePcKeyTransition(
                storedPc,
                storedDevice,
                storedPc.copy(deviceId = newDeviceId, keyEpoch = 10),
                null,
            )
        }
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

    @Test
    fun transportLevelCatalogPollFailureDoesNotAuthorizeSessionTeardown() {
        // r425/r426 regression: an airplane-mode relay outage failed the
        // 2-second foreground session-catalog poll. Its catch treated that as
        // authoritative "nothing exists", destroyed every open live session
        // and the frozen capture, and the resulting surface unregister left
        // the E2E inspect hook timing out through the whole reconnect stage.
        // A failure that never reached the server carries no authority about
        // which sessions exist and must not re-authorize that teardown;
        // answered requests and local data failures still do.
        assertTrue(isTransportCatalogPollFailure(IOException("connect: network is unreachable")))
        assertTrue(isTransportCatalogPollFailure(SocketTimeoutException("catalog poll timed out")))
        assertTrue(isTransportCatalogPollFailure(NativeOidcException(
            NativeOidcFailure.NETWORK, cause = IOException("no route to host"))))
        assertFalse(isTransportCatalogPollFailure(TermLinkApiException(401, "UNAUTHENTICATED", null)))
        for (status in listOf(408, 429, 500, 502, 503, 504)) {
            assertTrue(isTransportCatalogPollFailure(TermLinkApiException(status, "HTTP_$status", null)))
        }
        for (status in listOf(400, 401, 403, 404, 409, 426)) {
            assertFalse(isTransportCatalogPollFailure(TermLinkApiException(status, "HTTP_$status", null)))
        }
        assertTrue(isTransportCatalogPollFailure(dev.termlink.network.production.HostDeviceConnectionChangedException()))
        assertFalse(isTransportCatalogPollFailure(
            NativeOidcException(NativeOidcFailure.TOKEN_REQUEST_FAILED)))
        assertFalse(isTransportCatalogPollFailure(
            IllegalStateException("Authentication lifecycle changed")))
    }

    @Test
    fun requestDeadlineIsRecoverableWhileOwnerCancellationIsNot() = runBlocking {
        val deadline = runCatching { withTimeout(1) { awaitCancellation() } }.exceptionOrNull()!!
        assertTrue(currentCoroutineContext().isActive)
        assertTrue(isTransportCatalogPollFailure(deadline))
        assertFalse(isTransportCatalogPollFailure(CancellationException("account runtime stopped")))
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
        lastOutputSequence: ULong = 0u,
    ) = SessionSummary(
        id = id,
        pcId = pcId,
        shellType = "pwsh",
        encryptedTitle = null,
        state = state,
        columns = 80,
        rows = 24,
        lastOutputSequence = lastOutputSequence,
        createdAt = Instant.EPOCH,
        endedAt = null,
        online = true,
    )
}

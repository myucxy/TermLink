package dev.termlink.product

import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductUiLogicTest {
    @Test
    fun devicePageShowsOnlyServerOnlineWindowsPcs() {
        val onlineDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000001")
        val offlineDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000002")
        val androidDeviceId = UUID.fromString("20000000-0000-0000-0000-000000000004")
        val online = pc(
            id = UUID.fromString("30000000-0000-0000-0000-000000000001"),
            deviceId = onlineDeviceId,
            online = true,
        )
        val offline = pc(
            id = UUID.fromString("30000000-0000-0000-0000-000000000002"),
            deviceId = offlineDeviceId,
            online = false,
        )
        val onlineAndroid = pc(
            id = UUID.fromString("30000000-0000-0000-0000-000000000004"),
            deviceId = androidDeviceId,
            online = true,
        )
        val snapshot = ProductSnapshot(
            devices = listOf(
                device(onlineDeviceId),
                device(offlineDeviceId),
                device(androidDeviceId, DeviceType.ANDROID),
            ),
            pcs = listOf(offline, onlineAndroid, online),
        )

        assertEquals(listOf(online.id), visibleOnlineWindowsPcs(snapshot).map(PcSummary::id))
        assertTrue(visibleOnlineWindowsPcs(snapshot.copy(pcs = listOf(offline))).isEmpty())
    }

    @Test
    fun devicePageIncludesGenericPcAgentsAndStillExcludesAndroidRoutes() {
        val deviceId = UUID.fromString("20000000-0000-0000-0000-000000000005")
        val pcId = UUID.fromString("30000000-0000-0000-0000-000000000005")
        val snapshot = ProductSnapshot(
            devices = listOf(device(deviceId, DeviceType.PC_AGENT)),
            pcs = listOf(pc(pcId, deviceId, online = true).copy(platform = "linux", architecture = "arm64")),
        )

        assertEquals(listOf(pcId), visibleOnlineWindowsPcs(snapshot).map(PcSummary::id))
    }

    @Test
    fun devicePageProjectionRespondsToPcStatusUpdatesForTheSameUuid() {
        val deviceId = UUID.fromString("20000000-0000-0000-0000-000000000003")
        val pcId = UUID.fromString("30000000-0000-0000-0000-000000000003")
        val offlineSnapshot = ProductSnapshot(
            devices = listOf(device(deviceId)),
            pcs = listOf(pc(pcId, deviceId, online = false)),
        )

        assertTrue(visibleOnlineWindowsPcs(offlineSnapshot).isEmpty())
        val onlineSnapshot = offlineSnapshot.copy(pcs = listOf(pc(pcId, deviceId, online = true)))
        assertEquals(listOf(pcId), visibleOnlineWindowsPcs(onlineSnapshot).map(PcSummary::id))
        assertTrue(
            visibleOnlineWindowsPcs(
                onlineSnapshot.copy(pcs = listOf(pc(pcId, deviceId, online = false))),
            ).isEmpty(),
        )
    }

    @Test
    fun deviceSessionActivityUsesOnlyTheSessionOnlineFlag() {
        val pcOnlineSessionOffline = isDeviceSessionActive(sessionOnline = false)
        val pcOfflineSessionOnline = isDeviceSessionActive(sessionOnline = true)

        assertFalse(pcOnlineSessionOffline)
        assertTrue(pcOfflineSessionOnline)
        assertTrue(isDeviceSessionActive(sessionOnline = true))
        assertFalse(isDeviceSessionActive(sessionOnline = false))
    }

    @Test
    fun sessionTabsOpenOnlyForOnlineNonExitedCatalogEntries() {
        val id = UUID.fromString("00000000-0000-0000-0000-000000000031")
        val online = session(id, SessionState.RUNNING, Instant.EPOCH, online = true)

        assertTrue(isSessionTabOpenable(online))
        assertFalse(isSessionTabOpenable(online.copy(online = false)))
        assertFalse(isSessionTabOpenable(online.copy(state = SessionState.EXITED, online = true)))
        assertFalse(isSessionTabOpenable(null))
    }

    @Test
    fun onlineCloseAndOfflineRecordDeletionAreDistinctActions() {
        assertEquals(
            DeviceSessionDestructiveAction.CLOSE_ONLINE,
            deviceSessionDestructiveAction(sessionOnline = true),
        )
        assertEquals(
            DeviceSessionDestructiveAction.DELETE_OFFLINE_RECORD,
            deviceSessionDestructiveAction(sessionOnline = false),
        )
    }

    @Test
    fun mobileSessionsRequireExplicitSelectionInsteadOfSwipePaging() {
        assertFalse(MOBILE_SESSION_SWIPE_ENABLED)
        assertEquals("mobile-session-selector", MOBILE_SESSION_SELECTOR_TEST_TAG)
        assertFalse(ANDROID_VIEW_DRIVEN_TERMINAL_RESIZE_ENABLED)
    }

    @Test
    fun selectedRendererSurvivesTransientCatalogOfflineAndDisplaysReconnecting() {
        val selected = session(
            UUID.fromString("00000000-0000-0000-0000-000000000021"),
            SessionState.RUNNING,
            Instant.EPOCH,
            online = false,
        )
        val otherOffline = session(
            UUID.fromString("00000000-0000-0000-0000-000000000022"),
            SessionState.INTERRUPTED,
            Instant.EPOCH,
            online = false,
        )
        val online = session(
            UUID.fromString("00000000-0000-0000-0000-000000000023"),
            SessionState.RUNNING,
            Instant.EPOCH,
            online = true,
        )
        val previousState = RemoteSessionState.Live(lastAckedSequence = 73)

        val visible = liveSessionPageSessions(
            sessions = listOf(otherOffline, selected, online),
            selectedId = selected.id,
            sessionStates = mapOf(selected.id to previousState),
            selectedHasEngine = true,
        )

        assertEquals(listOf(selected.id, online.id), visible.map(SessionSummary::id))
        val displayed = selected.resolveDisplayedRemoteState(previousState)
        assertTrue(displayed is RemoteSessionState.Reconnecting)
        assertEquals(73L, displayed?.lastAckedSequence)
        val reconnecting = RemoteSessionState.Reconnecting(74, "network unavailable")
        assertEquals(reconnecting, selected.resolveDisplayedRemoteState(reconnecting))
    }

    @Test
    fun selectedEngineAloneRetainsDetailButExplicitExitOrDeletionRemovesIt() {
        val selected = session(
            UUID.fromString("00000000-0000-0000-0000-000000000024"),
            SessionState.INTERRUPTED,
            Instant.EPOCH,
            online = false,
        )
        assertEquals(
            listOf(selected),
            liveSessionPageSessions(
                sessions = listOf(selected),
                selectedId = selected.id,
                sessionStates = emptyMap(),
                selectedHasEngine = true,
            ),
        )

        val realtimeExited = RemoteSessionState.Exited(lastAckedSequence = 81, exitCode = 0)
        assertTrue(
            liveSessionPageSessions(
                sessions = listOf(selected),
                selectedId = selected.id,
                sessionStates = mapOf(selected.id to realtimeExited),
                selectedHasEngine = true,
            ).isEmpty(),
        )
        assertTrue(
            liveSessionPageSessions(
                sessions = listOf(selected.copy(state = SessionState.EXITED, online = true)),
                selectedId = selected.id,
                sessionStates = emptyMap(),
                selectedHasEngine = true,
            ).isEmpty(),
        )
        assertTrue(
            liveSessionPageSessions(
                sessions = emptyList(),
                selectedId = selected.id,
                sessionStates = mapOf(selected.id to RemoteSessionState.Reconnecting(81)),
                selectedHasEngine = true,
            ).isEmpty(),
        )
    }

    @Test
    fun mobileSelectorTracksTheSelectedUuidAcrossListPositions() {
        val first = session(UUID.fromString("00000000-0000-0000-0000-000000000011"), SessionState.RUNNING, Instant.EPOCH)
        val selected = session(UUID.fromString("00000000-0000-0000-0000-000000000012"), SessionState.RUNNING, Instant.EPOCH)
        val third = session(UUID.fromString("00000000-0000-0000-0000-000000000013"), SessionState.RUNNING, Instant.EPOCH)

        assertEquals(1, mobileSessionSelectionIndex(listOf(first, selected, third), selected.id))
        assertEquals(0, mobileSessionSelectionIndex(listOf(selected, third, first), selected.id))
        assertEquals("mobile-session-close:${selected.id}", mobileSessionCloseTestTag(selected.id))
    }

    @Test
    fun imeAvoidanceHasExactlyOneBottomInsetOwner() {
        val scaffoldBottom = 273
        val imeSamples = listOf(0, scaffoldBottom / 2, scaffoldBottom, scaffoldBottom + 1, 883)

        imeSamples.forEach { imeBottom ->
            val avoidance = sessionImeAvoidance(imeBottom, scaffoldBottom)
            assertEquals(scaffoldBottom, avoidance.scaffoldBottom)
            assertEquals((imeBottom - scaffoldBottom).coerceAtLeast(0), avoidance.contentBottom)
            assertEquals(maxOf(scaffoldBottom, imeBottom), avoidance.scaffoldBottom + avoidance.contentBottom)
        }
    }

    @Test
    fun acceptedActionBarInputResumesLiveAndClosesAnActiveOverlay() {
        assertEquals(
            TerminalInputResumeEffect(scrollToLatest = true, closeDetachedOverlay = true),
            terminalInputResumeEffect(accepted = true, detachedOverlayActive = true),
        )
        assertEquals(
            TerminalInputResumeEffect(scrollToLatest = false, closeDetachedOverlay = false),
            terminalInputResumeEffect(accepted = false, detachedOverlayActive = true),
        )
    }

    @Test
    fun acceptedImeInputClosesAnActiveOverlayAndRejectedImePreservesIt() {
        assertEquals(
            TerminalInputResumeEffect(scrollToLatest = true, closeDetachedOverlay = true),
            terminalInputResumeEffect(accepted = true, detachedOverlayActive = true),
        )
        assertEquals(
            TerminalInputResumeEffect(scrollToLatest = false, closeDetachedOverlay = false),
            terminalInputResumeEffect(accepted = false, detachedOverlayActive = true),
        )
        assertEquals(
            TerminalInputResumeEffect(scrollToLatest = true, closeDetachedOverlay = false),
            terminalInputResumeEffect(accepted = true, detachedOverlayActive = false),
        )
    }

    @Test
    fun confirmedAndContextPasteShareAcceptedOnlyResumeSemantics() {
        val acceptedConfirmedPaste = terminalInputResumeEffect(accepted = true, detachedOverlayActive = true)
        val acceptedContextPaste = terminalInputResumeEffect(accepted = true, detachedOverlayActive = true)
        val rejectedConfirmedPaste = terminalInputResumeEffect(accepted = false, detachedOverlayActive = true)
        val rejectedContextPaste = terminalInputResumeEffect(accepted = false, detachedOverlayActive = true)

        assertEquals(TerminalInputResumeEffect(true, true), acceptedConfirmedPaste)
        assertEquals(acceptedConfirmedPaste, acceptedContextPaste)
        assertEquals(TerminalInputResumeEffect(false, false), rejectedConfirmedPaste)
        assertEquals(rejectedConfirmedPaste, rejectedContextPaste)
    }

    @Test
    fun quickInputAndKeyShortcutsResumeOnlyWhenAccepted() {
        val accepted = terminalInputResumeEffect(accepted = true, detachedOverlayActive = true)
        val rejected = terminalInputResumeEffect(accepted = false, detachedOverlayActive = true)

        assertEquals(TerminalInputResumeEffect(true, true), accepted)
        assertEquals(TerminalInputResumeEffect(false, false), rejected)
    }

    @Test
    fun confirmedPasteRegistryRunsOnlyAfterSuccessfulSubmission() = runBlocking {
        val sessionId = UUID.fromString("12345678-1234-5678-9abc-def012345678")
        val registry = TerminalInputResumeRegistry()
        var resumes = 0
        val registration = registry.register(sessionId) { resumes += 1 }

        dispatchAcceptedTerminalInput(
            operation = {},
            onAccepted = { registry.resume(sessionId) },
        )
        assertEquals(1, resumes)

        runCatching {
            dispatchAcceptedTerminalInput(
                operation = { error("rejected") },
                onAccepted = { registry.resume(sessionId) },
            )
        }
        assertEquals(1, resumes)

        registration.close()
        assertFalse(registry.resume(sessionId))
    }

    @Test
    fun landscapeOrImeUsesTheCompactSessionSurface() {
        assertTrue(useWideSessionLayout(widthDp = 900, heightDp = 700, imeVisible = false))
        assertFalse(useWideSessionLayout(widthDp = 900, heightDp = 500, imeVisible = false))
        assertFalse(useWideSessionLayout(widthDp = 900, heightDp = 700, imeVisible = true))
        assertFalse(useWideSessionLayout(widthDp = 699, heightDp = 900, imeVisible = false))
    }

    @Test
    fun terminalAndHistoryTagsAreStable() {
        val sessionId = UUID.fromString("12345678-1234-5678-9abc-def012345678")

        assertEquals("terminal:$sessionId", terminalTestTag(sessionId))
        assertEquals("terminal-dock:$sessionId", terminalDockTestTag(sessionId))
        assertEquals("terminal-command:paste", terminalCommandTestTag("paste"))
        assertEquals("history-overlay:$sessionId", transcriptOverlayTestTag(sessionId))
        assertEquals("history-viewport:$sessionId", transcriptViewportTestTag(sessionId))
        assertEquals("history-loading:$sessionId", transcriptLoadingTestTag(sessionId))
        assertEquals("history-retry:$sessionId", transcriptRetryTestTag(sessionId))
        assertEquals("history-return-live:$sessionId", transcriptReturnLiveTestTag(sessionId))
    }

    @Test
    fun transcriptOverlayTracksRequestAndRepositoryState() {
        val idle = transcriptState()

        assertFalse(shouldShowTranscriptOverlay(overlayRequested = false, state = null))
        assertFalse(shouldShowTranscriptOverlay(overlayRequested = false, state = idle))
        assertTrue(shouldShowTranscriptOverlay(overlayRequested = true, state = null))
        assertTrue(shouldShowTranscriptOverlay(overlayRequested = false, state = idle.copy(loading = true)))
        assertTrue(shouldShowTranscriptOverlay(overlayRequested = false, state = idle.copy(error = "unavailable")))
        assertTrue(
            shouldShowTranscriptOverlay(
                overlayRequested = false,
                state = idle.copy(pages = listOf(transcriptPage())),
            ),
        )
    }

    @Test
    fun completedEmptyTranscriptDismissesOnlyTheBlankOverlay() {
        val idle = transcriptState()
        val complete = idle.copy(nextBeforeLineId = null, hasMoreBefore = false)

        assertTrue(shouldDismissEmptyTranscriptOverlay(complete))
        assertFalse(shouldDismissEmptyTranscriptOverlay(null))
        assertFalse(shouldDismissEmptyTranscriptOverlay(idle))
        assertFalse(shouldDismissEmptyTranscriptOverlay(complete.copy(loading = true)))
        assertFalse(shouldDismissEmptyTranscriptOverlay(complete.copy(error = "unavailable")))
        assertFalse(
            shouldDismissEmptyTranscriptOverlay(
                complete.copy(pages = listOf(transcriptPage())),
            ),
        )
    }

    @Test
    fun transcriptLoadingGuardAllowsOnlyOneOutstandingRequest() {
        val ready = transcriptState()

        assertFalse(canRequestOlderTranscript(state = null, localLoadingGuard = false))
        assertTrue(canRequestOlderTranscript(state = ready, localLoadingGuard = false))
        assertFalse(canRequestOlderTranscript(state = ready, localLoadingGuard = true))
        assertFalse(canRequestOlderTranscript(state = ready.copy(loading = true), localLoadingGuard = false))
        assertFalse(
            canRequestOlderTranscript(
                state = ready.copy(nextBeforeLineId = null, hasMoreBefore = false),
                localLoadingGuard = false,
            ),
        )
    }

    @Test
    fun rendererFailureAtTheOldestPageRetriesOnlyTheCurrentWindow() {
        val oldestPage = transcriptState().copy(
            pages = listOf(transcriptPage().copy(hasMoreBefore = false, nextBeforeLineId = 0)),
            nextBeforeLineId = null,
            hasMoreBefore = false,
        )
        var rendererRetries = 0
        var networkLoads = 0
        val action = transcriptRetryAction(oldestPage, rendererFailed = true, localLoadingGuard = false)

        dispatchTranscriptRetry(
            action,
            reapplyCurrentWindow = { rendererRetries += 1 },
            loadOlderPage = { networkLoads += 1 },
        )

        assertEquals(TranscriptRetryAction.REAPPLY_CURRENT_WINDOW, action)
        assertEquals(1, rendererRetries)
        assertEquals(0, networkLoads)
        assertFalse(canRequestOlderTranscript(oldestPage, localLoadingGuard = false, rendererFailed = true))
    }

    @Test
    fun rendererFailureWithAnOlderCursorStillRetriesOnlyTheCurrentWindow() {
        val hasOlderPage = transcriptState().copy(pages = listOf(transcriptPage()))
        var rendererRetries = 0
        var networkLoads = 0
        val action = transcriptRetryAction(hasOlderPage, rendererFailed = true, localLoadingGuard = false)

        dispatchTranscriptRetry(
            action,
            reapplyCurrentWindow = { rendererRetries += 1 },
            loadOlderPage = { networkLoads += 1 },
        )

        assertEquals(TranscriptRetryAction.REAPPLY_CURRENT_WINDOW, action)
        assertEquals(1, rendererRetries)
        assertEquals(0, networkLoads)
        assertFalse(canRequestOlderTranscript(hasOlderPage, localLoadingGuard = false, rendererFailed = true))
        assertEquals(
            TranscriptRetryAction.LOAD_OLDER_PAGE,
            transcriptRetryAction(hasOlderPage, rendererFailed = false, localLoadingGuard = false),
        )
    }

    @Test
    fun newestSessionSelectionRejectsExistingAndExitedSessions() {
        val existing = session(
            id = UUID.fromString("00000000-0000-0000-0000-000000000001"),
            state = SessionState.RUNNING,
            createdAt = Instant.parse("2026-08-22T00:00:03Z"),
        )
        val exited = session(
            id = UUID.fromString("00000000-0000-0000-0000-000000000002"),
            state = SessionState.EXITED,
            createdAt = Instant.parse("2026-08-22T00:00:04Z"),
        )
        val olderNew = session(
            id = UUID.fromString("00000000-0000-0000-0000-000000000003"),
            state = SessionState.RUNNING,
            createdAt = Instant.parse("2026-08-22T00:00:01Z"),
        )
        val newestNew = session(
            id = UUID.fromString("00000000-0000-0000-0000-000000000004"),
            state = SessionState.INTERRUPTED,
            createdAt = Instant.parse("2026-08-22T00:00:02Z"),
        )

        assertEquals(
            newestNew,
            newestNewNonExitedSession(
                sessions = listOf(existing, exited, olderNew, newestNew),
                existingSessionIds = setOf(existing.id),
            ),
        )
        assertEquals(null, newestNewNonExitedSession(listOf(existing, exited), setOf(existing.id)))
    }

    private fun transcriptState() = TranscriptViewportState(
        sessionId = UUID.fromString("12345678-1234-5678-9abc-def012345678"),
        snapshotAnchorSequence = 80u,
        initialBeforeLineId = 61u,
    )

    private fun transcriptPage() = TerminalTranscriptPageV1(
        snapshotAnchorSequence = 80,
        firstLineId = 41,
        lastLineId = 60,
        hasMoreBefore = true,
        nextBeforeLineId = 41,
        columns = 1,
        rows = emptyList(),
        styleTable = emptyList(),
        palette = TerminalPaletteSnapshotV1(emptyList(), 0, 0, 0),
    )

    private fun device(id: UUID, type: DeviceType = DeviceType.WINDOWS_PC) = DeviceSummary(
        id = id,
        type = type,
        name = "PC device",
        keyId = "device-key-$id",
        exchangePublicKey = ByteArray(32),
        trustState = DeviceTrustState.TRUSTED,
        createdAt = Instant.EPOCH,
    )

    private fun pc(id: UUID, deviceId: UUID, online: Boolean) = PcSummary(
        id = id,
        deviceId = deviceId,
        name = "PC",
        agentVersion = "1",
        remoteRoutingEnabled = true,
        online = online,
        lastSeenAt = if (online) Instant.EPOCH else Instant.parse("2999-01-01T00:00:00Z"),
    )

    private fun session(
        id: UUID,
        state: SessionState,
        createdAt: Instant,
        online: Boolean = state != SessionState.EXITED,
    ) = SessionSummary(
        id = id,
        pcId = UUID.fromString("10000000-0000-0000-0000-000000000000"),
        shellType = "pwsh",
        encryptedTitle = null,
        state = state,
        columns = 120,
        rows = 30,
        lastOutputSequence = 0u,
        createdAt = createdAt,
        endedAt = createdAt.takeIf { state == SessionState.EXITED },
        online = online,
    )
}

package dev.termlink.product

import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.terminal.api.TerminalProtocolDiagnostic
import java.time.Instant
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionDiagnosticRetentionTest {
    private val pcId = UUID.randomUUID()
    private val hostDiagnostic = requireNotNull(
        TerminalProtocolDiagnostic.forHostExit(
            125,
            "TL-DIAG/1\ncategory=unsupported\nreason=private mode\nfamily=CSI\n" +
                "sequence=<ESC>[?9999h\nhex=1B 5B 3F 39 39 39 39 68\nstart=1:2\nend=2:5\n" +
                "observed_bytes=8\nhidden_bytes=0\ntruncated_bytes=0\n",
        ),
    )

    @Test
    fun retainsOnlyOpenedCatalogRowsWithValidatedProtocolEvidence() {
        val retained = UUID.randomUUID()
        val noDiagnostic = UUID.randomUUID()
        val notOpened = UUID.randomUUID()
        val removedFromCatalog = UUID.randomUUID()
        val snapshot = ProductSnapshot(
            sessions = listOf(
                session(retained, SessionState.EXITED, online = false),
                session(noDiagnostic, SessionState.EXITED, online = false),
                session(notOpened, SessionState.EXITED, online = false),
            ),
            sessionOpenFailures = mapOf(
                retained to SessionOpenFailure(
                    SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY,
                    protocolDiagnostic = hostDiagnostic,
                    protocolDiagnosticSource = SessionProtocolDiagnosticSource.WINDOWS_HOST,
                ),
                noDiagnostic to SessionOpenFailure(SessionOpenFailureCode.SESSION_ENDED),
                notOpened to SessionOpenFailure(
                    SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY,
                    protocolDiagnostic = hostDiagnostic,
                ),
                removedFromCatalog to SessionOpenFailure(
                    SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY,
                    protocolDiagnostic = hostDiagnostic,
                ),
            ),
        )

        assertEquals(
            setOf(retained),
            retainedProtocolDiagnosticSessionIds(
                snapshot,
                listOf(retained.toString(), noDiagnostic.toString(), "not-a-uuid"),
            ),
        )
        assertEquals(
            setOf(retained, notOpened),
            catalogProtocolDiagnosticSessionIds(snapshot),
        )
    }

    @Test
    fun anEndedSessionWithoutProtocolEvidenceIsNotRetainedForAnErrorCard() {
        val sessionId = UUID.randomUUID()
        val snapshot = ProductSnapshot(
            sessions = listOf(session(sessionId, SessionState.EXITED, online = false)),
            sessionOpenFailures = mapOf(
                sessionId to SessionOpenFailure(SessionOpenFailureCode.SESSION_ENDED),
            ),
        )

        assertTrue(retainedProtocolDiagnosticSessionIds(snapshot, listOf(sessionId.toString())).isEmpty())
    }

    @Test
    fun endedDiagnosticDoesNotAutoReopenOrRenderAStaleEngine() {
        assertTrue(
            !shouldAutoReopenSession(
                sessionState = SessionState.EXITED,
                hasEngine = false,
                hasProtocolDiagnostic = true,
            ),
        )
        assertTrue(
            shouldAutoReopenSession(
                sessionState = SessionState.EXITED,
                hasEngine = false,
                hasProtocolDiagnostic = false,
            ),
        )
        assertTrue(
            shouldAutoReopenSession(
                sessionState = SessionState.INTERRUPTED,
                hasEngine = false,
                hasProtocolDiagnostic = false,
            ),
        )
    }

    private fun session(id: UUID, state: SessionState, online: Boolean): SessionSummary = SessionSummary(
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
        online = online,
    )
}

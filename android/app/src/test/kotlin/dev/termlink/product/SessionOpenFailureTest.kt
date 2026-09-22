package dev.termlink.product

import android.database.sqlite.SQLiteFullException
import dev.termlink.network.production.TermLinkApiException
import java.io.IOException
import java.net.SocketTimeoutException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionOpenFailureTest {
    @Test
    fun tailFirstFailureCodeRetainsOnlyTheStableSessionTaxonomy() {
        assertEquals(
            "SESSION_OPEN_TL_H119",
            tailFirstSessionOpenFailureCode(
                SessionOpenFailure(
                    SessionOpenFailureCode.LOCAL_STORAGE_FULL,
                    httpStatus = 507,
                    diagnosticCode = "SAFE_DIAGNOSTIC",
                ),
            ),
        )
    }

    @Test
    fun stableCodesAreUniqueAndSafeForUserReports() {
        val codes = SessionOpenFailureCode.entries.map(SessionOpenFailureCode::stableCode)

        assertEquals(codes.size, codes.toSet().size)
        assertTrue(codes.all { it.matches(Regex("TL-[A-Z][0-9]{3}")) })
        assertThrows(IllegalArgumentException::class.java) {
            SessionOpenFailure(SessionOpenFailureCode.HISTORY_NOT_READY, diagnosticCode = "terminal text")
        }
        assertEquals(
            "CSI_PRIVATE_MODE_UNSUPPORTED",
            SessionOpenFailure(
                SessionOpenFailureCode.HISTORY_NOT_READY,
                diagnosticCode = "CSI_PRIVATE_MODE_UNSUPPORTED",
            ).diagnosticCode,
        )
    }

    @Test
    fun serverAndNetworkFailuresRetainActionableCategories() {
        assertClassified(
            SessionOpenFailureCode.AUTHENTICATION_EXPIRED,
            classifySessionOpenException(
                TermLinkApiException(401, "TOKEN_EXPIRED", null),
                SessionFailureStage.REALTIME,
            ),
        )
        val forbidden = classifySessionOpenException(
            TermLinkApiException(403, "DEVICE_FORBIDDEN", null),
            SessionFailureStage.REALTIME,
        )
        assertClassified(SessionOpenFailureCode.ACCESS_DENIED, forbidden)
        assertEquals(403, forbidden.failure.httpStatus)
        assertClassified(
            SessionOpenFailureCode.CONTROL_LEASE_HELD,
            classifySessionOpenException(
                TermLinkApiException(409, "LEASE_HELD", null),
                SessionFailureStage.CONTROL,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.SERVICE_TIMEOUT,
            classifySessionOpenException(SocketTimeoutException(), SessionFailureStage.SNAPSHOT_METADATA),
        )
        assertClassified(
            SessionOpenFailureCode.SNAPSHOT_DOWNLOAD,
            classifySessionOpenException(IOException("private URL omitted"), SessionFailureStage.SNAPSHOT_DOWNLOAD),
        )
    }

    @Test
    fun snapshotFailuresSeparateIntegrityKeysFormatRendererAndInstall() {
        assertClassified(
            SessionOpenFailureCode.SNAPSHOT_INTEGRITY,
            classifySessionOpenException(
                IllegalArgumentException("History download integrity check failed"),
                SessionFailureStage.SNAPSHOT_DOWNLOAD,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.SNAPSHOT_RECIPIENT,
            classifySessionOpenException(
                IllegalArgumentException("Snapshot key ID is not for this device"),
                SessionFailureStage.SNAPSHOT_PREPARE,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.SNAPSHOT_FORMAT,
            classifySessionOpenException(
                IllegalArgumentException("Snapshot format is unsupported"),
                SessionFailureStage.SNAPSHOT_PREPARE,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.TERMINAL_RENDERER,
            classifySessionOpenException(
                IllegalArgumentException("primary cell style has unsupported effects"),
                SessionFailureStage.SNAPSHOT_PREPARE,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.SNAPSHOT_INSTALL,
            classifySessionOpenException(
                IllegalStateException("install failed"),
                SessionFailureStage.SNAPSHOT_INSTALL,
            ),
        )
        assertFalse(SessionOpenFailureCode.SNAPSHOT_FORMAT.retryable)
        assertTrue(SessionOpenFailureCode.SNAPSHOT_INSTALL.retryable)
    }

    @Test
    fun alreadyClassifiedFailureIsNotDowngradedByOuterStage() {
        val original = sessionOpenException(SessionOpenFailureCode.TERMINAL_RENDERER)

        assertTrue(classifySessionOpenException(original, SessionFailureStage.CONTROL) === original)
    }

    @Test
    fun historyFailuresRemainSeparateFromSnapshotAndRealtimeFailures() {
        assertClassified(
            SessionOpenFailureCode.HISTORY_NOT_READY,
            classifySessionOpenException(
                TermLinkApiException(503, "HISTORY_UNAVAILABLE", null),
                SessionFailureStage.HISTORY_METADATA,
            ),
        )
        assertClassified(
            SessionOpenFailureCode.HISTORY_REPLAY,
            classifySessionOpenException(
                IllegalStateException("history replay failed"),
                SessionFailureStage.HISTORY_REPLAY,
            ),
        )
        assertTrue(SessionOpenFailureCode.HISTORY_NOT_READY.retryable)
        assertTrue(SessionOpenFailureCode.HISTORY_GAP.retryable)
        assertFalse(SessionOpenFailureCode.entries.any { it.stableCode == "TL-H115" })
    }

    @Test
    fun localDatabaseFullHasItsOwnLocalizedActionableFailure() {
        val error = classifySessionOpenException(
            IllegalStateException("room write failed", SQLiteFullException("database or disk is full")),
            SessionFailureStage.HISTORY_REPLAY,
        )

        assertClassified(SessionOpenFailureCode.LOCAL_STORAGE_FULL, error)
        assertTrue(error.failure.code.retryable)
    }

    private fun assertClassified(expected: SessionOpenFailureCode, error: SessionOpenException) {
        assertEquals(expected, error.failure.code)
        assertEquals(expected.stableCode, error.message)
    }
}

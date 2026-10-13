package dev.termlink.product

import android.database.sqlite.SQLiteFullException
import dev.termlink.harness.R
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
    fun protocolDiagnosticSourceDefaultsToAndroidAndMapsHostContextSeparately() {
        val defaultFailure = SessionOpenFailure(SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY)
        assertEquals(
            SessionProtocolDiagnosticSource.ANDROID_MIRROR,
            defaultFailure.protocolDiagnosticSource,
        )
        assertEquals(
            R.string.terminal_protocol_mirror_context,
            sessionProtocolDiagnosticContextResource(defaultFailure.protocolDiagnosticSource),
        )

        val hostFailure = SessionOpenFailure(
            code = SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY,
            protocolDiagnosticSource = SessionProtocolDiagnosticSource.WINDOWS_HOST,
        )
        assertEquals(SessionProtocolDiagnosticSource.WINDOWS_HOST, hostFailure.protocolDiagnosticSource)
        assertEquals(
            R.string.terminal_protocol_host_context,
            sessionProtocolDiagnosticContextResource(hostFailure.protocolDiagnosticSource),
        )
    }

    @Test
    fun negotiationFailuresDistinguishUpgradeFromTimeoutAndKeepSafeDetails() {
        listOf(
            "TERMINAL_RELAY_UPGRADE_REQUIRED" to SessionOpenFailureCode.TERMINAL_PROTOCOL_SERVER,
            "TERMINAL_HOST_UPGRADE_REQUIRED" to SessionOpenFailureCode.TERMINAL_PROTOCOL_HOST,
            "TERMINAL_CLIENT_UPGRADE_REQUIRED" to SessionOpenFailureCode.TERMINAL_PROTOCOL_CLIENT,
            "TERMINAL_STATE_UNSUPPORTED" to SessionOpenFailureCode.TERMINAL_PROTOCOL_CLIENT,
            "TERMINAL_STATE_MISMATCH" to SessionOpenFailureCode.TERMINAL_STATE_MISMATCH,
            "TERMINAL_NEGOTIATION_TIMEOUT" to SessionOpenFailureCode.TERMINAL_NEGOTIATION_TIMEOUT,
        ).forEach { (diagnostic, code) ->
            val failure = classifySessionOpenException(IllegalStateException("wrapper",
                dev.termlink.network.production.TerminalCompatibilityException(diagnostic)), SessionFailureStage.SNAPSHOT_PREPARE).failure
            assertEquals(code, failure.code)
            assertEquals(diagnostic, failure.diagnosticCode)
            assertEquals(diagnostic == "TERMINAL_NEGOTIATION_TIMEOUT", failure.code.retryable)
        }
        val failure = classifySessionOpenException(dev.termlink.network.production.TerminalCompatibilityException(
            "private path or payload"), SessionFailureStage.REALTIME).failure
        assertEquals(SessionOpenFailureCode.TERMINAL_NEGOTIATION_INVALID, failure.code)
        assertEquals(null, failure.diagnosticCode)
    }

    @Test
    fun checkpointProfileMismatchIsDistinctFromRendererCrashAndUnknownSnapshotFormat() {
        listOf(
            "installCheckpoint.profile" to SessionOpenFailureCode.TERMINAL_STATE_MISMATCH,
            "importCheckpoint" to SessionOpenFailureCode.SNAPSHOT_FORMAT,
        ).forEach { (operation, expected) ->
            val error = dev.termlink.terminal.nativecore.NativeTerminalException(
                operation, dev.termlink.terminal.nativecore.NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
            )
            val failure = classifySessionOpenException(error, SessionFailureStage.SNAPSHOT_INSTALL).failure
            assertEquals(expected, failure.code)
            assertFalse(failure.code.retryable)
        }
    }

    @Test
    fun memoryAdmissionFailureIsNeverReportedAsNetworkOrDiskFailure() {
        val budget = dev.termlink.model.TerminalBufferBudget {
            dev.termlink.model.HeapState(192L * 1024 * 1024, 190L * 1024 * 1024)
        }
        val limit = requireNotNull(runCatching { budget.reserve(1) }.exceptionOrNull())
        assertEquals(SessionOpenFailureCode.TERMINAL_HISTORY_MEMORY_LIMIT,
            sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT, limit).failure.code)
        SessionFailureStage.entries.forEach { stage ->
            val failure = classifySessionOpenException(IllegalStateException("wrapper", limit), stage).failure
            assertEquals(SessionOpenFailureCode.TERMINAL_HISTORY_MEMORY_LIMIT, failure.code)
            assertEquals("TERMINAL_HISTORY_MEMORY_LIMIT", failure.diagnosticCode)
            assertTrue(failure.code.retryable)
        }
    }

    @Test
    fun unsupportedOutputRetainsSafeProtocolEvidenceInsteadOfNetworkFailure() {
        val text = "TL-DIAG/1\ncategory=unsupported\nreason=private mode\nfamily=CSI\n" +
            "sequence=<ESC>[?9999h\nhex=1B 5B 3F 39 39 39 39 68\nstart=1:2\nend=2:5\n" +
            "observed_bytes=8\nhidden_bytes=0\ntruncated_bytes=0\n"
        val error = dev.termlink.terminal.nativecore.NativeTerminalException(
            "applyBatch", dev.termlink.terminal.nativecore.NativeTerminalStatus.UNSUPPORTED, diagnostic = text,
        )
        val failure = classifySessionOpenException(error, SessionFailureStage.HISTORY_REPLAY).failure
        assertEquals(SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY, failure.code)
        assertEquals("<ESC>[?9999h", failure.protocolDiagnostic?.sequence)
        assertEquals(text, failure.protocolDiagnostic?.text)
        assertFalse(error.toString().contains("9999"))
        assertFalse(failure.toString().contains("9999"))
        listOf("malformed" to SessionOpenFailureCode.MALFORMED_TERMINAL_SEQUENCE,
            "limit_exceeded" to SessionOpenFailureCode.TERMINAL_RESOURCE_LIMIT).forEach { (category, code) ->
            val categorized = dev.termlink.terminal.nativecore.NativeTerminalException(
                "applyBatch", dev.termlink.terminal.nativecore.NativeTerminalStatus.UNSUPPORTED,
                diagnostic = text.replace("category=unsupported", "category=$category"),
            )
            assertEquals(code, classifySessionOpenException(categorized, SessionFailureStage.HISTORY_REPLAY).failure.code)
        }
    }

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

    @Test
    fun oldWindowsSessionHostIsNotReportedAsAndroidOrServerFailure() {
        val error = classifySessionOpenException(dev.termlink.network.production.TerminalCompatibilityException("TERMINAL_LOCAL_PROTOCOL_MISMATCH"), SessionFailureStage.REALTIME)
        assertClassified(SessionOpenFailureCode.TERMINAL_LOCAL_PROTOCOL, error)
        assertEquals("TERMINAL_LOCAL_PROTOCOL_MISMATCH", error.failure.diagnosticCode)
        assertFalse(error.failure.code.retryable)
    }

    @Test
    fun preservedHistoryFailuresHaveDistinctNonRetryableCodes() {
        listOf(
            "TERMINAL_HISTORY_PROFILE_MISSING" to SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_MISSING,
            "TERMINAL_HISTORY_PROFILE_INVALID" to SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_INVALID,
            "TERMINAL_HISTORY_PROFILE_MISMATCH" to SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_MISMATCH,
        ).forEach { (diagnostic, category) ->
            val error = classifySessionOpenException(
                dev.termlink.network.production.TerminalCompatibilityException(diagnostic), SessionFailureStage.REALTIME,
            )
            assertClassified(category, error)
            assertEquals(diagnostic, error.failure.diagnosticCode)
            assertFalse(error.failure.code.retryable)
        }
    }
}

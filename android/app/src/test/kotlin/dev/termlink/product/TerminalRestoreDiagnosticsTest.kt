package dev.termlink.product

import dev.termlink.network.production.ProductionHostHistoryTransferEvidence
import dev.termlink.history.HistoryRepository
import java.security.MessageDigest
import java.util.UUID
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TerminalRestoreDiagnosticsTest {
    @Test
    fun latestAttemptResetsEvidenceAndIgnoresLateUpdates() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val first = ledger.begin(session)
        val baseHash = ByteArray(32) { it.toByte() }
        val requestId = UUID.randomUUID()
        val brokerRequestId = UUID.randomUUID()
        ledger.deltaRequested(session, first, 41, baseHash)
        ledger.deltaReceived(session, first, 49, ByteArray(32) { 0xff.toByte() }, 512 * 1024 + 1,
            ProductionHostHistoryTransferEvidence(requestId, 2, 1, 3))
        ledger.brokerResolved(session, first)
        ledger.brokerTransferred(session, first,
            ProductionHostHistoryTransferEvidence(brokerRequestId, 1, 0, 1))

        val observed = ledger.snapshot(session)!!
        assertEquals(1, observed.deltaRequestCount)
        assertEquals(41L, observed.baseSnapshotSequence)
        assertEquals("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
            observed.baseSha256)
        assertEquals(49L, observed.targetSnapshotSequence)
        assertEquals("ff".repeat(32), observed.targetSha256)
        assertEquals(2, observed.deltaExpectedChunkCount)
        assertEquals(requestId.toString(), observed.deltaRequestId)
        assertEquals(2, observed.deltaChunksAccepted)
        assertEquals(1, observed.deltaDuplicateChunks)
        assertEquals(3, observed.deltaChunkAcksSent)
        assertEquals(1, observed.brokerResolvedCount)
        assertEquals(brokerRequestId.toString(), observed.brokerRequestId)
        assertEquals(1, observed.brokerChunksAccepted)
        assertEquals(1, observed.brokerChunkAcksSent)
        assertEquals(0, observed.deltaInstallCount)
        assertNull(observed.deltaFallbackReason)

        val second = ledger.begin(session)
        ledger.deltaInstalled(session, first)
        ledger.fallback(session, first, "FAILED")
        ledger.brokerResolved(session, first)
        ledger.brokerTransferred(session, first,
            ProductionHostHistoryTransferEvidence(UUID.randomUUID(), 7, 0, 7))
        assertEquals(first + 1, second)
        assertEquals(ProductRestoreDiagnostics(second), ledger.snapshot(session))
        ledger.deltaRequested(session, second, 50, baseHash)
        ledger.fallback(session, second, "NEEDS_FULL_SNAPSHOT")
        assertEquals("NEEDS_FULL_SNAPSHOT", ledger.snapshot(session)?.deltaFallbackReason)
        assertEquals(0, ledger.snapshot(session)?.deltaInstallCount)
        ledger.deltaInstalled(session, second)
        assertEquals(1, ledger.snapshot(session)?.deltaInstallCount)
        ledger.remove(session)
        assertNull(ledger.snapshot(session))
    }

    @Test
    fun successfulFullSnapshotStoresAuthenticatedCheckpointDigestAndSequence() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val attempt = ledger.begin(session)
        val checkpoint = "authenticated-native-checkpoint".encodeToByteArray()
        val digest = HistoryRepository.sha256(checkpoint)

        ledger.fullSnapshotInstalled(session, attempt, 77, digest)

        val observed = ledger.snapshot(session)!!
        assertEquals(1, observed.fullSnapshotInstallCount)
        assertEquals(77L, observed.fullSnapshotSequence)
        assertEquals(hex(digest), observed.fullSnapshotSha256)
        assertEquals(64, observed.fullSnapshotSha256!!.length)
        assertEquals(observed.fullSnapshotSha256, observed.fullSnapshotSha256.lowercase())
    }

    @Test
    fun invalidOrLateFullSnapshotEvidenceIsIgnored() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val first = ledger.begin(session)
        ledger.fullSnapshotInstalled(session, first, 0, ByteArray(32))
        ledger.fullSnapshotInstalled(session, first, 4, ByteArray(31))
        assertNull(ledger.snapshot(session)!!.fullSnapshotSequence)
        assertNull(ledger.snapshot(session)!!.fullSnapshotSha256)
        assertEquals(0, ledger.snapshot(session)!!.fullSnapshotInstallCount)

        val second = ledger.begin(session)
        ledger.fullSnapshotInstalled(session, first, 5, ByteArray(32))
        assertEquals(ProductRestoreDiagnostics(second), ledger.snapshot(session))
    }

    @Test
    fun fullSnapshotEvidenceIsFencedAndDigestDoesNotAliasCallerArray() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val attempt = ledger.begin(session)
        val digest = MessageDigest.getInstance("SHA-256").digest(ByteArray(16) { it.toByte() })
        val expected = hex(digest)

        ledger.fullSnapshotInstalled(session, attempt, 9, digest)
        digest.fill(0)

        val observed = ledger.snapshot(session)!!
        assertEquals(expected, observed.fullSnapshotSha256)
        assertEquals(1, observed.fullSnapshotInstallCount)
        assertEquals(9L, observed.fullSnapshotSequence)
    }

    @Test
    fun deltaEvidenceDoesNotPopulateFullSnapshotFields() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val attempt = ledger.begin(session)
        ledger.deltaRequested(session, attempt, 10, ByteArray(32))
        ledger.deltaReceived(session, attempt, 12, ByteArray(32), 1,
            ProductionHostHistoryTransferEvidence(UUID.randomUUID(), 1, 0, 1))
        ledger.deltaInstalled(session, attempt)

        val observed = ledger.snapshot(session)!!
        assertEquals(1, observed.deltaInstallCount)
        assertEquals(0, observed.fullSnapshotInstallCount)
        assertNull(observed.fullSnapshotSequence)
        assertNull(observed.fullSnapshotSha256)
    }

    private fun hex(value: ByteArray): String = buildString(value.size * 2) {
        val digits = "0123456789abcdef"
        value.forEach { byte ->
            val unsigned = byte.toInt() and 0xff
            append(digits[unsigned ushr 4])
            append(digits[unsigned and 0x0f])
        }
    }

    @Test
    fun concurrentBrokerResolutionsRemainVisible() {
        val ledger = ProductRestoreDiagnosticsLedger()
        val session = UUID.randomUUID()
        val attempt = ledger.begin(session)
        val workers = List(8) { thread { repeat(100) { ledger.brokerResolved(session, attempt) } } }
        workers.forEach { it.join() }
        assertEquals(800, ledger.snapshot(session)?.brokerResolvedCount)
    }
}

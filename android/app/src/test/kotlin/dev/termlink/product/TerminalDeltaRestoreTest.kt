package dev.termlink.product

import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.model.TerminalBufferLimitException
import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.network.production.ProductionStateDeltaNeedsFullSnapshotException
import dev.termlink.network.production.ProductionTerminalProfile
import dev.termlink.protocol.realtime.v1.StateDeltaMetadataV1
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalWidthProfile
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalDeltaRestoreTest {
    @Test
    fun deltaRejectsWidthThatDiffersFromNegotiatedGraphicsProfile() {
        val sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        for ((profile, width) in listOf(
            ProductionTerminalProfile.GRAPHICS to TerminalWidthProfile.UNICODE_16,
            ProductionTerminalProfile.GRAPHICS_UNICODE16 to TerminalWidthProfile.LEGACY_V1,
            ProductionTerminalProfile.LEGACY to TerminalWidthProfile.LEGACY_V1,
        )) {
            val failure = assertThrows(IllegalArgumentException::class.java) {
                applyGraphicsStateDelta(
                    pairwiseKey = ByteArray(0), sealedDelta = ByteArray(0),
                    metadata = StateDeltaMetadataV1.getDefaultInstance(), baseCheckpoint = ByteArray(0),
                    baseSnapshotSequence = 1uL, baseObservedLocalInputEpoch = 0uL,
                    sessionId = sessionId, pcDeviceId = sessionId, recipientDeviceId = sessionId,
                    keyEpoch = 1, terminalProfile = profile, widthProfile = width,
                    outputProfile = TerminalOutputProfile.UTF8,
                )
            }
            assertTrue(failure.message.orEmpty().contains("width policy"))
        }
    }

    @Test
    fun currentGraphicsSnapshotVersionIsRequiredAsDeltaBase() {
        val snapshot = ByteArray(104)
        "TLSNP2S\u0000".encodeToByteArray().copyInto(snapshot)
        snapshot[8] = 2
        snapshot[10] = 22
        snapshot[12] = 22
        snapshot[14] = 104.toByte()
        assertTrue(isCurrentGraphicsDeltaBase(snapshot))
        snapshot[12] = 21
        assertFalse(isCurrentGraphicsDeltaBase(snapshot))
    }

    @Test
    fun tinyDeltaCannotBypassLargeTargetMemoryAdmission() {
        val targetBytes = 64L * 1024 * 1024
        assertEquals(targetBytes, graphicsDeltaReservationBytes(512, 1_024, targetBytes))
        val budget = TerminalBufferBudget { HeapState(256L * 1024 * 1024, 0) }
        assertThrows(TerminalBufferLimitException::class.java) {
            budget.reserve(graphicsDeltaReservationBytes(512, 1_024, targetBytes))
        }
        assertEquals(0L, budget.reservedBytes)
    }

    @Test
    fun unavailableDeltaKeepsLocalCheckpointPath() = runBlocking {
        val decision = restorePreferredGraphicsDelta(available = false) {
            error("Delta request must not run")
        }
        assertNull(decision.restored)
        assertFalse(decision.requireFullSnapshot)
    }

    @Test
    fun explicitFullSnapshotAndRetiredBaseBypassLocalCheckpoint() = runBlocking {
        for (failure in listOf(
            ProductionStateDeltaNeedsFullSnapshotException(),
            RetiredGraphicsDeltaBaseException(),
        )) {
            val decision = restorePreferredGraphicsDelta<String>(available = true) {
                throw failure
            }
            assertNull(decision.restored)
            assertTrue(decision.requireFullSnapshot)
        }
    }

    @Test
    fun invalidLocalCheckpointFallsBackToOneAuthenticatedFullSnapshot() = runBlocking {
        val decision = restorePreferredGraphicsDelta<String>(available = true) {
            throw sessionOpenException(SessionOpenFailureCode.LOCAL_CHECKPOINT)
        }
        assertNull(decision.restored)
        assertTrue(decision.requireFullSnapshot)
    }

    @Test
    fun unrelatedSessionFailureDoesNotGetHiddenByDeltaFallback() {
        val failure = sessionOpenException(SessionOpenFailureCode.SNAPSHOT_INSTALL)
        val thrown = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                restorePreferredGraphicsDelta<String>(available = true) { throw failure }
            }
        }
        assertTrue(thrown === failure)
    }

    @Test
    fun oldGraphicsBaseOnUnicode16AttachRequiresRemoteFullSnapshot() = runBlocking {
        val recipient = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val oldKey = TerminalSnapshotCrypto.terminalCoreGraphicsSnapshotKeyId(recipient, 7)
        val decision = restorePreferredGraphicsDelta(available = true) {
            if (terminalCoreSnapshotKeyEpoch(ProductionTerminalProfile.GRAPHICS_UNICODE16,
                    recipient, oldKey) == null) throw RetiredGraphicsDeltaBaseException()
            "stale local checkpoint"
        }
        assertNull(decision.restored)
        assertTrue(decision.requireFullSnapshot)
    }

    @Test
    fun cancelledDeltaRestoreNeverStartsFallback() {
        assertThrows(CancellationException::class.java) {
            runBlocking {
                restorePreferredGraphicsDelta<String>(available = true) {
                    throw CancellationException("session was closed")
                }
            }
        }
    }

    @Test
    fun otherDeltaFailuresRemainFailClosed() {
        val failure = IllegalStateException("authenticated target hash changed")
        val thrown = assertThrows(IllegalStateException::class.java) {
            runBlocking {
                restorePreferredGraphicsDelta<String>(available = true) { throw failure }
            }
        }
        assertTrue(thrown === failure)
    }
}

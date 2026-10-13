package dev.termlink.product

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.Assert.assertThrows

class FrozenHistoryBrowseTest {
    @Test fun newerHostSnapshotCanBeRetriedTwiceWithoutReusingOldAnchor() {
        assertEquals(12uL, nextFrozenSnapshotSequence(10uL, 12uL, 0))
        assertEquals(13uL, nextFrozenSnapshotSequence(12uL, 13uL, 1))
        assertNull(nextFrozenSnapshotSequence(13uL, 14uL, 2))
        assertNull(nextFrozenSnapshotSequence(10uL, 10uL, 0))
        assertNull(nextFrozenSnapshotSequence(10uL, null, 0))
    }

    @Test fun cursorUsesIdentityRatherThanNumericOrder() {
        assertEquals(91uL, validateFrozenPageCursor(
            requestedAnchor = 7uL,
            captureRevision = 43uL,
            pageRevision = 43,
            pageAnchor = 7,
            hasMoreBefore = true,
            nextBeforeLineId = 91,
            firstLineId = 91uL,
            lastLineId = 4uL,
            lineIds = listOf(91, 4),
            previouslyLoadedLineIds = setOf(7),
        ))
    }

    @Test fun staleRevisionAnchorOverlapAndCursorLoopAreRejected() {
        fun check(revision: Long = 43, anchor: Long = 7, next: Long = 91,
            previous: Set<Long> = emptySet()) = validateFrozenPageCursor(
            7uL, 43uL, revision, anchor, true, next, 91uL, 4uL,
            listOf(91, 4), previous,
        )
        assertThrows(IllegalArgumentException::class.java) { check(revision = 44) }
        assertThrows(IllegalArgumentException::class.java) { check(anchor = 8) }
        assertThrows(IllegalArgumentException::class.java) { check(previous = setOf(4)) }
        assertThrows(IllegalArgumentException::class.java) { check(next = 7) }
        assertThrows(IllegalArgumentException::class.java) {
            validateFrozenPageCursor(7uL, 43uL, 43, 7, true, 91,
                91uL, 4uL, listOf(91, 4), emptySet(), setOf(91uL))
        }
    }

    @Test fun terminalPageAndSessionRemovalClearDetachedState() {
        assertNull(validateFrozenPageCursor(7uL, 43uL, 43, 7, false,
            91, 91uL, 4uL, listOf(91, 4), emptySet()))
        assertNull(validateFrozenPageCursor(ULong.MAX_VALUE, 43uL, 43, -1,
            false, -1, 0uL, 0uL, emptyList(), emptySet()))
        val sessionId = UUID.randomUUID()
        val state = FrozenHistoryViewportState(sessionId, 42uL, 1, captureId = 3uL,
            revision = 43uL, nextBeforeLineId = 91uL)
        val snapshot = ProductSnapshot(frozenHistoryViewports = mapOf(sessionId to state),
            liveAcknowledgements = mapOf(sessionId to 99uL))
        assertFalse(snapshot.withoutSession(sessionId).frozenHistoryViewports.containsKey(sessionId))
        assertFalse(snapshot.withoutSession(sessionId).liveAcknowledgements.containsKey(sessionId))
    }

    @Test fun cloudMenuOffersFrozenBrowseOnlyForAuthenticatedGraphicsSession() {
        assertFalse(shouldOfferFrozenHistory(false, true, true, true)) // Windows is offline.
        assertFalse(shouldOfferFrozenHistory(true, false, true, true))
        assertFalse(shouldOfferFrozenHistory(true, true, false, true))
        assertFalse(shouldOfferFrozenHistory(true, true, true, false)) // Authenticated old Host.
        assertEquals(true, shouldOfferFrozenHistory(true, true, true, true))
        assertEquals(true, shouldOfferFrozenHistory(true, true, true, null)) // Ended session checks Host on entry.
    }
}

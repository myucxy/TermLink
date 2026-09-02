package dev.termlink.product

import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionLifecycleIsolationTest {
    @Test
    fun longLifecycleWorkSerializesOneSessionButDoesNotBlockAnotherAndLanesAreReclaimed() = runBlocking {
        val lanes = SessionLifecycleLanes<String>()
        val firstEntered = CompletableDeferred<Unit>()
        val releaseFirst = CompletableDeferred<Unit>()
        val sameAttempted = CompletableDeferred<Unit>()
        val sameEntered = CompletableDeferred<Unit>()
        val otherEntered = CompletableDeferred<Unit>()
        val releaseOther = CompletableDeferred<Unit>()

        val first = async(Dispatchers.Default) {
            lanes.withLock("session-a") {
                firstEntered.complete(Unit)
                releaseFirst.await()
            }
        }
        firstEntered.await()

        val same = async(Dispatchers.Default) {
            sameAttempted.complete(Unit)
            lanes.withLock("session-a") { sameEntered.complete(Unit) }
        }
        sameAttempted.await()
        val other = async(Dispatchers.Default) {
            lanes.withLock("session-b") {
                otherEntered.complete(Unit)
                releaseOther.await()
            }
        }

        withTimeout(1_000) { otherEntered.await() }
        withTimeout(1_000) {
            while (lanes.activeReferenceCount("session-a") != 2) yield()
        }
        repeat(8) { yield() }
        assertFalse("the same session must remain serialized", sameEntered.isCompleted)
        assertEquals("only A and B lanes may be retained", 2, lanes.activeLaneCount())

        releaseOther.complete(Unit)
        releaseFirst.complete(Unit)
        withTimeout(1_000) { sameEntered.await() }
        awaitAll(first, same, other)
        assertEquals("idle keyed lanes must be reclaimed", 0, lanes.activeLaneCount())

        lanes.withLock("session-a") { }
        assertEquals("a reused key must also release its new lane", 0, lanes.activeLaneCount())
    }

    @Test
    fun cancelledWaiterReleasesItsReferenceWithoutSplittingTheHeldLane() = runBlocking {
        val lanes = SessionLifecycleLanes<String>()
        val holderEntered = CompletableDeferred<Unit>()
        val releaseHolder = CompletableDeferred<Unit>()
        val holder = async(Dispatchers.Default) {
            lanes.withLock("session-a") {
                holderEntered.complete(Unit)
                releaseHolder.await()
            }
        }
        holderEntered.await()

        val waiter = async(Dispatchers.Default) {
            lanes.withLock("session-a") { error("cancelled waiter entered") }
        }
        withTimeout(1_000) {
            while (lanes.activeReferenceCount("session-a") != 2) yield()
        }
        waiter.cancelAndJoin()
        assertEquals(1, lanes.activeReferenceCount("session-a"))
        assertEquals(1, lanes.activeLaneCount())

        releaseHolder.complete(Unit)
        holder.await()
        assertEquals(0, lanes.activeLaneCount())
    }

    @Test
    fun staleIncarnationCannotRetireSuccessorAndReplacementWaitsForCloseAndJoin() = runBlocking {
        class Incarnation(val name: String)

        val retired = Incarnation("retired")
        val successor = Incarnation("successor")
        val current = AtomicReference<Incarnation?>(successor)
        val staleEvents = mutableListOf<String>()

        val staleAccepted = retireExactIncarnationAndThen(
            expected = retired,
            removeExact = { expected ->
                current.compareAndSet(expected, null)
            },
            closeAndJoin = { staleEvents += "close" },
            afterRetired = { staleEvents += "replace" },
        )

        assertFalse(staleAccepted)
        assertSame(successor, current.get())
        assertTrue(staleEvents.isEmpty())

        current.set(retired)
        val closeEntered = CompletableDeferred<Unit>()
        val releaseClose = CompletableDeferred<Unit>()
        val orderedEvents = mutableListOf<String>()
        val replacement = async(Dispatchers.Default) {
            retireExactIncarnationAndThen(
                expected = retired,
                removeExact = { expected ->
                    current.compareAndSet(expected, null).also { removed ->
                        if (removed) {
                            orderedEvents += "removed"
                        }
                    }
                },
                closeAndJoin = {
                    orderedEvents += "close-start"
                    closeEntered.complete(Unit)
                    releaseClose.await()
                    orderedEvents += "close-finished"
                },
                afterRetired = {
                    orderedEvents += "successor-installed"
                    current.set(successor)
                },
            )
        }

        closeEntered.await()
        assertEquals(listOf("removed", "close-start"), orderedEvents)
        assertEquals(null, current.get())
        releaseClose.complete(Unit)
        assertTrue(replacement.await())
        assertEquals(
            listOf("removed", "close-start", "close-finished", "successor-installed"),
            orderedEvents,
        )
        assertSame(successor, current.get())
    }

    @Test
    fun rendererPoisonLineageIsBoundToTheExactRecoveryIncarnation() {
        assertEquals(
            RendererFatalIncarnationDecision.STALE,
            rendererFatalIncarnationDecision(
                expectedIsCurrent = false,
                inheritedFatalSequence = 41,
                failureSequence = 41,
            ),
        )
        assertEquals(
            RendererFatalIncarnationDecision.RECOVER,
            rendererFatalIncarnationDecision(
                expectedIsCurrent = true,
                inheritedFatalSequence = null,
                failureSequence = 41,
            ),
        )
        assertEquals(
            RendererFatalIncarnationDecision.FAIL_CLOSED,
            rendererFatalIncarnationDecision(
                expectedIsCurrent = true,
                inheritedFatalSequence = 41,
                failureSequence = 41,
            ),
        )
        assertEquals(
            RendererFatalIncarnationDecision.RECOVER,
            rendererFatalIncarnationDecision(
                expectedIsCurrent = true,
                inheritedFatalSequence = 41,
                failureSequence = 42,
            ),
        )
    }
}

package dev.termlink.product

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AuthenticationLifecycleIsolationTest {
    @Test
    fun restoredAuthenticationFenceStartsWithPositiveGeneration() {
        val fence = AuthenticationLifecycleFence(initialGeneration = 1L)

        assertEquals(1L, fence.capture())
    }

    @Test
    fun staleFailureCannotInvalidateSuccessorAuthentication() {
        val fence = AuthenticationLifecycleFence()
        val retired = fence.capture()
        val successor = fence.invalidate()
        var cleared = false

        val accepted = fence.invalidateIfCurrent(retired) { cleared = true }

        assertFalse(accepted)
        assertFalse(cleared)
        assertTrue(fence.isCurrent(successor))
    }

    @Test
    fun exactFailureInvalidatesOnceAndRunsCleanupBeforeSuccessorStarts() {
        val fence = AuthenticationLifecycleFence()
        val current = fence.capture()
        val events = mutableListOf<String>()

        val accepted = fence.invalidateIfCurrent(current, predicate = { true }) {
            events += "cleanup"
        }
        val successor = fence.invalidate().also { events += "successor" }

        assertTrue(accepted)
        assertTrue(fence.isCurrent(successor))
        assertTrue(events == listOf("cleanup", "successor"))
    }

    @Test
    fun failedOwnerPredicateDoesNotAdvanceAuthenticationGeneration() {
        val fence = AuthenticationLifecycleFence()
        val current = fence.capture()

        val accepted = fence.invalidateIfCurrent(current, predicate = { false }) {
            error("stale owner cleanup must not run")
        }

        assertFalse(accepted)
        assertTrue(fence.isCurrent(current))
    }

    @Test
    fun staleAsyncCommitCannotOverwriteSuccessor() {
        val fence = AuthenticationLifecycleFence()
        val retired = fence.capture()
        val successor = fence.invalidate()
        var committed = false

        val accepted = fence.runIfCurrent(retired) { committed = true }

        assertFalse(accepted)
        assertFalse(committed)
        assertTrue(fence.isCurrent(successor))
    }

    @Test
    fun oldRuntimeOwnerCannotClaimSameGenerationSuccessor() {
        val generation = 7L
        val oldOwner = Any()
        val successorOwner = Any()

        assertFalse(
            authenticationRuntimeClaimMatches(
                currentOwner = successorOwner,
                currentOwnerGeneration = generation,
                expectedOwner = oldOwner,
                expectedGeneration = generation,
            ),
        )
        assertTrue(
            authenticationRuntimeClaimMatches(
                currentOwner = successorOwner,
                currentOwnerGeneration = generation,
                expectedOwner = successorOwner,
                expectedGeneration = generation,
            ),
        )
        assertFalse(
            authenticationRuntimeClaimMatches(
                currentOwner = successorOwner,
                currentOwnerGeneration = generation + 1,
                expectedOwner = successorOwner,
                expectedGeneration = generation,
            ),
        )
        assertTrue(
            authenticationRuntimeClaimMatches(
                currentOwner = null,
                currentOwnerGeneration = null,
                expectedOwner = null,
                expectedGeneration = generation,
            ),
        )
    }

    @Test
    fun currentOwnerRejectionIsClaimedExactlyOnceUnderConcurrency() {
        val fence = AuthenticationLifecycleFence()
        val generation = fence.capture()
        val owner = Any()
        val start = CountDownLatch(1)
        val ready = CountDownLatch(2)
        val accepted = AtomicInteger(0)
        val cleanups = AtomicInteger(0)
        val executor = Executors.newFixedThreadPool(2)
        try {
            val futures = List(2) {
                executor.submit {
                    ready.countDown()
                    assertTrue(start.await(5, TimeUnit.SECONDS))
                    if (fence.invalidateIfCurrent(
                            generation,
                            predicate = {
                                authenticationRuntimeClaimMatches(owner, generation, owner, generation)
                            },
                        ) {
                            cleanups.incrementAndGet()
                        }
                    ) {
                        accepted.incrementAndGet()
                    }
                }
            }
            assertTrue(ready.await(5, TimeUnit.SECONDS))
            start.countDown()
            futures.forEach { it.get(5, TimeUnit.SECONDS) }

            assertEquals(1, accepted.get())
            assertEquals(1, cleanups.get())
            assertFalse(fence.isCurrent(generation))
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun reloginTransitionWinsBeforeReleasedStaleInvalidation() {
        val fence = AuthenticationLifecycleFence()
        val oldGeneration = fence.capture()
        val state = AtomicReference("old")
        val callbackReady = CountDownLatch(1)
        val releaseCallback = CountDownLatch(1)
        val executor = Executors.newSingleThreadExecutor()
        try {
            val staleCallback = executor.submit<Boolean> {
                callbackReady.countDown()
                assertTrue(releaseCallback.await(5, TimeUnit.SECONDS))
                fence.invalidateIfCurrent(oldGeneration) { state.set("cleared") }
            }
            assertTrue(callbackReady.await(5, TimeUnit.SECONDS))
            val successorGeneration = fence.invalidate { state.set("successor") }
            releaseCallback.countDown()

            assertFalse(staleCallback.get(5, TimeUnit.SECONDS))
            assertEquals("successor", state.get())
            assertTrue(fence.isCurrent(successorGeneration))
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun generationAndAuthenticationStateAreCapturedAtomically() {
        val fence = AuthenticationLifecycleFence()
        val state = AtomicReference("old")
        val captureEntered = CountDownLatch(1)
        val releaseCapture = CountDownLatch(1)
        val transitionStarted = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(2)
        try {
            val captured = executor.submit<Pair<Long, String>> {
                fence.capture { generation ->
                    captureEntered.countDown()
                    assertTrue(releaseCapture.await(5, TimeUnit.SECONDS))
                    generation to requireNotNull(state.get())
                }
            }
            assertTrue(captureEntered.await(5, TimeUnit.SECONDS))
            val successor = executor.submit<Long> {
                transitionStarted.countDown()
                fence.invalidate { state.set("successor") }
            }
            assertTrue(transitionStarted.await(5, TimeUnit.SECONDS))
            assertFalse(successor.isDone)
            releaseCapture.countDown()

            val capturedPair = captured.get(5, TimeUnit.SECONDS)
            val successorGeneration = successor.get(5, TimeUnit.SECONDS)
            assertEquals(0L to "old", capturedPair)
            assertEquals("successor", state.get())
            assertTrue(fence.isCurrent(successorGeneration))
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun oldSessionInstallationCannotCrossAuthenticationOwnerOrIdentity() {
        val fence = AuthenticationLifecycleFence()
        val retiredGeneration = fence.capture()
        val retiredOwner = Any()
        val retiredIdentity = Any()
        val currentOwner = AtomicReference(retiredOwner)
        val currentIdentity = AtomicReference(retiredIdentity)
        val successorOwner = Any()
        val successorIdentity = Any()

        val successorGeneration = fence.invalidate {
            currentOwner.set(successorOwner)
            currentIdentity.set(successorIdentity)
        }
        var installed = false
        val accepted = fence.runIfCurrent(
            retiredGeneration,
            predicate = {
                currentOwner.get() === retiredOwner && currentIdentity.get() === retiredIdentity
            },
        ) {
            installed = true
        }

        assertFalse(accepted)
        assertFalse(installed)
        assertTrue(fence.isCurrent(successorGeneration))
        assertTrue(currentOwner.get() === successorOwner)
        assertTrue(currentIdentity.get() === successorIdentity)
    }

    @Test
    fun successorWaitsForRetirementRegisteredAfterItsInitialEmptyCheck() = runBlocking {
        val barrier = AuthenticationRetirementBarrier()

        // Models beginAuthenticationTransition's first quiescence check.
        barrier.awaitQuiescence()
        val retirement = barrier.register()
        val successorInstalled = CompletableDeferred<Unit>()
        val successor = async(Dispatchers.Default) {
            barrier.awaitQuiescence()
            successorInstalled.complete(Unit)
        }

        repeat(8) { yield() }
        assertFalse(successorInstalled.isCompleted)
        barrier.complete(retirement)
        withTimeout(1_000) { successor.await() }
        assertTrue(successorInstalled.isCompleted)
    }

    @Test
    fun retirementClaimsPreserveRegistrationOrder() = runBlocking {
        val barrier = AuthenticationRetirementBarrier()
        val first = barrier.register()
        val second = barrier.register()
        val releaseFirst = CompletableDeferred<Unit>()
        val events = mutableListOf<String>()

        val firstCleanup = async(Dispatchers.Default) {
            first.predecessor?.await()
            releaseFirst.await()
            synchronized(events) { events += "first" }
            barrier.complete(first)
        }
        val secondCleanup = async(Dispatchers.Default) {
            second.predecessor?.await()
            synchronized(events) { events += "second" }
            barrier.complete(second)
        }

        repeat(8) { yield() }
        assertTrue(synchronized(events) { events.isEmpty() })
        releaseFirst.complete(Unit)
        withTimeout(1_000) {
            firstCleanup.await()
            secondCleanup.await()
        }
        assertEquals(listOf("first", "second"), synchronized(events) { events.toList() })
        barrier.awaitQuiescence()
    }
}

package dev.termlink.product

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenRotationCancellationTest {
    @Test
    fun cancelledOwnerSavesRotatedTokenBeforeTheNextRefreshEnters() = runBlocking {
        withTimeout(5_000) {
            val consumed = CompletableDeferred<Unit>()
            val response = CompletableDeferred<Unit>()
            var persistedRefreshToken = "refresh-0"
            val requests = mutableListOf<String>()
            val coordinator = TokenRefreshCoordinator { _ ->
                finishTokenRotation {
                    requests += persistedRefreshToken
                    val next = requests.size
                    withContext(Dispatchers.IO) {
                        if (next == 1) {
                            consumed.complete(Unit)
                            response.await()
                        }
                    }
                    persistedRefreshToken = "refresh-$next"
                    "access-$next"
                }
            }

            val owner = async { coordinator.freshAccessToken(force = true) }
            try {
                consumed.await()
                owner.cancel()
                val waiter = async(start = CoroutineStart.UNDISPATCHED) {
                    coordinator.freshAccessToken(force = true)
                }
                assertFalse(owner.isCompleted)
                assertEquals(listOf("refresh-0"), requests)
                response.complete(Unit)
                owner.join()

                assertTrue(owner.isCancelled)
                assertEquals("access-2", waiter.await())
                assertEquals(listOf("refresh-0", "refresh-1"), requests)
                assertEquals("refresh-2", persistedRefreshToken)
            } finally {
                response.complete(Unit)
                owner.cancelAndJoin()
            }
        }
    }

    @Test
    fun waiterCanCancelWhileTheOwnerCompletesRotation() = runBlocking {
        withTimeout(5_000) {
            val consumed = CompletableDeferred<Unit>()
            val response = CompletableDeferred<Unit>()
            var requests = 0
            val coordinator = TokenRefreshCoordinator { _ ->
                finishTokenRotation {
                    requests++
                    consumed.complete(Unit)
                    response.await()
                    "access"
                }
            }
            val owner = async { coordinator.freshAccessToken() }
            try {
                consumed.await()
                val waiter = async(start = CoroutineStart.UNDISPATCHED) { coordinator.freshAccessToken() }
                waiter.cancelAndJoin()
                assertEquals(1, requests)
                response.complete(Unit)
                assertEquals("access", owner.await())
            } finally {
                response.complete(Unit)
                owner.cancelAndJoin()
            }
        }
    }

    @Test
    fun cancelledRotationCannotOverwriteSignOutOrASuccessorAccount() = runBlocking {
        withTimeout(5_000) {
            for (successor in listOf(null, "other-account-refresh")) {
                val fence = AuthenticationLifecycleFence(initialGeneration = 1)
                val captured = fence.capture()
                val consumed = CompletableDeferred<Unit>()
                val response = CompletableDeferred<Unit>()
                var persistedRefreshToken: String? = "old-account-refresh"
                var committed: Boolean? = null
                val coordinator = TokenRefreshCoordinator { _ ->
                    finishTokenRotation {
                        withContext(Dispatchers.IO) {
                            consumed.complete(Unit)
                            response.await()
                        }
                        committed = fence.runIfCurrent(captured) {
                            persistedRefreshToken = "rotated-old-account-refresh"
                        }
                        "old-account-access"
                    }
                }
                val owner = async { coordinator.freshAccessToken() }
                try {
                    consumed.await()
                    fence.invalidate { persistedRefreshToken = successor }
                    owner.cancel()
                    response.complete(Unit)
                    owner.join()

                    assertEquals(false, committed)
                    assertEquals(successor, persistedRefreshToken)
                } finally {
                    response.complete(Unit)
                    owner.cancelAndJoin()
                }
            }
        }
    }

    @Test
    fun cancellationBeforeExchangeDoesNotConsumeARefreshToken() = runBlocking {
        var exchanged = false
        val caller = launch(start = CoroutineStart.UNDISPATCHED) {
            currentCoroutineContext()[Job]!!.cancel()
            finishTokenRotation { exchanged = true }
        }
        caller.join()

        assertTrue(caller.isCancelled)
        assertFalse(exchanged)
    }

    @Test
    fun callerTimeoutStillSavesTheConsumedTokenResponse() = runBlocking {
        withTimeout(5_000) {
            val consumed = CompletableDeferred<Job>()
            val response = CompletableDeferred<Unit>()
            var persisted = false
            val caller = async {
                withTimeoutOrNull(250) {
                    val deadline = currentCoroutineContext()[Job]!!
                    val coordinator = TokenRefreshCoordinator { _ ->
                        finishTokenRotation {
                            consumed.complete(deadline)
                            response.await()
                            persisted = true
                            "access"
                        }
                    }
                    coordinator.freshAccessToken()
                }
            }
            try {
                val deadline = consumed.await()
                while (!deadline.isCancelled) yield()
                assertFalse(caller.isCompleted)
                response.complete(Unit)

                assertEquals(null, caller.await())
                assertTrue(persisted)
            } finally {
                response.complete(Unit)
                caller.cancelAndJoin()
            }
        }
    }
}

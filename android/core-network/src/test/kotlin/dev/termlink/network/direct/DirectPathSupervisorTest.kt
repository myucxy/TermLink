package dev.termlink.network.direct

import java.io.IOException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DirectPathSupervisorTest {
    @Test
    fun featureIsDisabledByDefaultAndNeverAllocatesUdp() = runTest {
        var opens = 0
        val supervisor = DirectPathSupervisor(
            parentScope = this,
            endpointFactory = DirectDatagramEndpointFactory {
                opens++
                FakeDirectDatagramEndpoint()
            },
        )
        try {
            assertFalse(supervisor.startEpoch(1) { error("must not run") })
            assertEquals(0, opens)
            assertEquals(
                DirectPathState.RelayOnly(DirectFallbackReason.FEATURE_DISABLED),
                supervisor.currentState(),
            )
        } finally {
            supervisor.close()
        }
    }

    @Test
    fun operationFailureFallsBackWithoutAnyRelayDependencyOrDisconnect() = runTest {
        val endpoint = FakeDirectDatagramEndpoint()
        val supervisor = enabledSupervisor(this, endpoint)

        assertTrue(
            supervisor.startEpoch(8) { epoch ->
                assertTrue(epoch.probing())
                throw IOException("malicious probe")
            },
        )
        advanceUntilIdle()

        assertEquals(
            DirectPathState.RelayOnly(DirectFallbackReason.PROBE_FAILED, retiredEpoch = 8),
            supervisor.currentState(),
        )
        assertEquals(1, endpoint.closeCount)
        // The supervisor constructor/API has no Relay socket, disconnect callback, or durable ACK handle.
        supervisor.close()
    }

    @Test
    fun networkChangeSynchronouslyFencesEpochCancelsWorkAndClosesSocket() = runTest {
        val endpoint = FakeDirectDatagramEndpoint()
        val entered = CompletableDeferred<Unit>()
        val never = CompletableDeferred<Unit>()
        val supervisor = enabledSupervisor(this, endpoint)
        supervisor.startEpoch(9) {
            entered.complete(Unit)
            never.await()
        }
        advanceUntilIdle()
        assertTrue(entered.isCompleted)

        supervisor.onNetworkChanged()

        assertEquals(
            DirectPathState.RelayOnly(DirectFallbackReason.NETWORK_CHANGED, retiredEpoch = 9),
            supervisor.currentState(),
        )
        assertTrue(endpoint.isClosed)
        advanceUntilIdle()
        assertEquals(
            DirectPathState.RelayOnly(DirectFallbackReason.NETWORK_CHANGED, retiredEpoch = 9),
            supervisor.currentState(),
        )
        supervisor.close()
    }

    @Test
    fun oneEpochResourceReplacesAndRetiresThePreviousEpoch() = runTest {
        val first = FakeDirectDatagramEndpoint(localPort = 45_001)
        val second = FakeDirectDatagramEndpoint(localPort = 45_002)
        val sockets = ArrayDeque(listOf(first, second))
        val never = CompletableDeferred<Unit>()
        val supervisor = DirectPathSupervisor(
            parentScope = this,
            feature = DirectTransportFeature(enabled = true),
            endpointFactory = DirectDatagramEndpointFactory { sockets.removeFirst() },
        )
        supervisor.startEpoch(1) { never.await() }
        advanceUntilIdle()

        supervisor.startEpoch(2) { epoch ->
            assertTrue(epoch.probing())
            assertTrue(epoch.probeVerifiedNotDirect())
        }
        advanceUntilIdle()

        assertEquals(1, first.closeCount)
        assertEquals(0, second.closeCount)
        assertEquals(DirectPathState.ProbeVerifiedNotDirect(2, second.localPort), supervisor.currentState())
        supervisor.close()
        assertEquals(1, second.closeCount)
    }

    @Test
    fun incompleteOrIllegalTransitionCannotClaimDirectReadiness() = runTest {
        val endpoint = FakeDirectDatagramEndpoint()
        val supervisor = enabledSupervisor(this, endpoint)

        supervisor.startEpoch(3) { epoch ->
            assertFalse(epoch.probeVerifiedNotDirect())
        }
        advanceUntilIdle()

        assertEquals(
            DirectPathState.RelayOnly(DirectFallbackReason.ATTEMPT_INCOMPLETE, retiredEpoch = 3),
            supervisor.currentState(),
        )
        supervisor.close()
    }

    @Test
    fun socketAllocationFailureAlsoRetiresAnExistingEpoch() = runTest {
        val first = FakeDirectDatagramEndpoint()
        var opens = 0
        val never = CompletableDeferred<Unit>()
        val supervisor = DirectPathSupervisor(
            parentScope = this,
            feature = DirectTransportFeature(enabled = true),
            endpointFactory = DirectDatagramEndpointFactory {
                if (opens++ == 0) first else throw IOException("no UDP resources")
            },
        )
        supervisor.startEpoch(4) { never.await() }
        advanceUntilIdle()

        assertFalse(supervisor.startEpoch(5) { })

        assertEquals(1, first.closeCount)
        assertEquals(
            DirectPathState.RelayOnly(DirectFallbackReason.GATHERING_FAILED, retiredEpoch = 4),
            supervisor.currentState(),
        )
        supervisor.close()
    }

    private fun enabledSupervisor(
        scope: kotlinx.coroutines.CoroutineScope,
        endpoint: FakeDirectDatagramEndpoint,
    ): DirectPathSupervisor = DirectPathSupervisor(
        parentScope = scope,
        feature = DirectTransportFeature(enabled = true),
        endpointFactory = DirectDatagramEndpointFactory { endpoint },
    )
}

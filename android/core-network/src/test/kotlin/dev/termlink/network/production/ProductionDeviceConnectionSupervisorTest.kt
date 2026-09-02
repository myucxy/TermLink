package dev.termlink.network.production

import dev.termlink.protocol.realtime.v1.Envelope
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionDeviceConnectionSupervisorTest {
    @Test
    fun stalledAuthenticationRefreshTimesOutWithoutSigningOutAndCanRetry(): Unit = runTest {
        val socket = SupervisorFakeSocket()
        val refreshStarted = CompletableDeferred<Unit>()
        val secondRefreshStarted = CompletableDeferred<Unit>()
        val refreshCount = AtomicInteger(0)
        val failures = mutableListOf<Throwable>()
        val supervisor = ProductionDeviceConnectionSupervisor(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                if (refreshCount.incrementAndGet() == 1) refreshStarted.complete(Unit)
                else secondRefreshStarted.complete(Unit)
                awaitCancellation()
            },
            authenticationFailureObserver = failures::add,
            hasEndpointDemand = { true },
            onGenerationBoundary = {},
            retryJitter = ProductionRealtimeRetryJitter { 0 },
            authenticationRefreshTimeoutMillis = 1_000,
        )
        val transientState = CompletableDeferred<RealtimeState.Failed>()
        val stateJob = backgroundScope.launch {
            val state = supervisor.state.first { it is RealtimeState.Failed }
            transientState.complete(state as RealtimeState.Failed)
        }

        supervisor.connect()
        socket.emit(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()

        assertTrue(refreshStarted.isCompleted)
        assertEquals("AUTHENTICATING", supervisor.phaseName())
        advanceTimeBy(999)
        runCurrent()
        assertFalse(transientState.isCompleted)

        advanceTimeBy(1)
        runCurrent()

        val state = transientState.await()
        assertEquals("realtime authentication refresh timed out", state.reason)
        assertEquals("CONNECTING", supervisor.phaseName())
        assertEquals(2, socket.connectCount.get())
        assertEquals(emptyList<Throwable>(), failures)

        socket.emit(RealtimeState.AuthenticationRejected(401, "still expired"))
        runCurrent()
        assertTrue(secondRefreshStarted.isCompleted)
        assertEquals(2, refreshCount.get())
        assertEquals("AUTHENTICATING", supervisor.phaseName())

        stateJob.cancel()
        supervisor.close()
    }

    @Test
    fun transientAuthenticationRefreshExceptionRetriesWithoutSignOutObserver(): Unit = runTest {
        val socket = SupervisorFakeSocket()
        val refreshCount = AtomicInteger(0)
        val failures = mutableListOf<Throwable>()
        val supervisor = ProductionDeviceConnectionSupervisor(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                refreshCount.incrementAndGet()
                throw IllegalStateException("temporary refresh transport failure")
            },
            authenticationFailureObserver = failures::add,
            hasEndpointDemand = { true },
            onGenerationBoundary = {},
            retryJitter = ProductionRealtimeRetryJitter { 0 },
        )

        supervisor.connect()
        socket.emit(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()

        assertEquals(1, refreshCount.get())
        assertEquals(emptyList<Throwable>(), failures)
        assertEquals(2, socket.connectCount.get())
        assertEquals("CONNECTING", supervisor.phaseName())
        supervisor.close()
    }

    @Test
    fun closeCancelsStalledAuthenticationRefreshWithoutReportingSignOutFailure(): Unit = runTest {
        val socket = SupervisorFakeSocket()
        val refreshStarted = CompletableDeferred<Unit>()
        val refreshCancelled = CompletableDeferred<Unit>()
        val failures = mutableListOf<Throwable>()
        val supervisor = ProductionDeviceConnectionSupervisor(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                refreshStarted.complete(Unit)
                try {
                    awaitCancellation()
                } finally {
                    refreshCancelled.complete(Unit)
                }
            },
            authenticationFailureObserver = failures::add,
            hasEndpointDemand = { true },
            onGenerationBoundary = {},
            authenticationRefreshTimeoutMillis = 1_000,
        )

        supervisor.connect()
        socket.emit(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()
        assertTrue(refreshStarted.isCompleted)

        supervisor.close()
        runCurrent()

        assertTrue(refreshCancelled.isCompleted)
        assertEquals(emptyList<Throwable>(), failures)
        assertEquals("CLOSED", supervisor.phaseName())
        assertEquals(1, socket.closeCount.get())
    }

    private class SupervisorFakeSocket : ProductionRealtimeSocket {
        private val mutableState = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = emptyFlow()
        override val receivePressure: StateFlow<ProductionReceivePressure> = MutableStateFlow(
            ProductionReceivePressure(0, false, 0, 0),
        )
        override val state: SharedFlow<RealtimeState> = mutableState
        val connectCount = AtomicInteger(0)
        val closeCount = AtomicInteger(0)
        private var generation = 0
        private var connectedGeneration = 0

        override fun connect() {
            connectCount.incrementAndGet()
            generation++
        }

        override fun send(envelope: Envelope): Boolean = connectedGeneration > 0

        override fun disconnect(reason: String) {
            connectedGeneration = 0
        }

        override fun diagnostics(): ProductionRealtimeSocketDiagnostics =
            ProductionRealtimeSocketDiagnostics(
                generation = generation,
                connectedGeneration = connectedGeneration,
                socketPresent = generation > 0,
                state = if (connectedGeneration > 0) "CONNECTED" else "CONNECTING",
                bufferedEvents = 0,
                bufferedWireBytes = 0,
            )

        override fun close() {
            closeCount.incrementAndGet()
            connectedGeneration = 0
        }

        fun emit(state: RealtimeState) {
            if (state is RealtimeState.Connected) connectedGeneration = generation
            check(mutableState.tryEmit(state))
        }
    }
}

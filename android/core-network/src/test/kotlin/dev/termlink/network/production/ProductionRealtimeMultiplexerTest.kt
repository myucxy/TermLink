package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.session.DurableAckCommit
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString as OkioByteString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionRealtimeMultiplexerTest {
    @Test
    fun oidcRejectionRefreshesOnceAtDeviceBoundaryAndReconnectsWithLatestToken(): Unit = runBlocking {
        val androidId = UUID.fromString("10000000-0000-0000-0000-000000000000")
        val identity = DeviceIdentity.generate()
        val token = AtomicReference("token-1")
        val refreshCount = AtomicInteger(0)
        val serverSocket = AtomicReference<WebSocket>()
        val serverSocketClosed = CountDownLatch(1)
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(401))
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: okhttp3.Response) {
                        serverSocket.set(webSocket)
                    }

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }

                    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                        serverSocketClosed.countDown()
                    }
                },
            ),
        )
        server.start()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val multiplexer = ProductionRealtimeMultiplexer(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider(token::get),
            deviceId = androidId,
            identity = identity,
            parentScope = scope,
            allowCleartextForLocalTest = true,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                refreshCount.incrementAndGet()
                token.set("token-2")
            },
        )
        val endpoint = multiplexer.register(UUID.randomUUID())
        try {
            val connected = async { endpoint.state.first { it == RealtimeState.Connected } }
            endpoint.connect()
            withTimeout(5_000) { connected.await() }

            assertEquals(1, refreshCount.get())
            assertEquals("Bearer token-1", server.takeRequest().getHeader("Authorization"))
            assertEquals("Bearer token-2", server.takeRequest().getHeader("Authorization"))
        } finally {
            multiplexer.close()
            if (serverSocket.getAndSet(null)?.close(1000, "test complete") == true) {
                serverSocketClosed.await(2, TimeUnit.SECONDS)
            }
            scope.cancel()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun failedForcedRefreshPublishesTerminalAuthenticationFailureWithoutRetryLoop(): Unit = runTest {
        val socket = FakeSocket()
        val refreshCount = AtomicInteger(0)
        val failureCount = AtomicInteger(0)
        val multiplexer = ProductionRealtimeMultiplexer.forTest(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                refreshCount.incrementAndGet()
                throw IllegalStateException("refresh rejected")
            },
            authenticationFailureObserver = { failureCount.incrementAndGet() },
        )
        val endpoint = multiplexer.register(UUID.randomUUID())
        val failed = async { endpoint.state.first { it is RealtimeState.AuthenticationFailed } }

        endpoint.connect()
        socket.emitState(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()

        assertTrue(failed.await() is RealtimeState.AuthenticationFailed)
        assertEquals(1, refreshCount.get())
        assertEquals(1, failureCount.get())
        assertEquals(1, socket.connectCount)

        endpoint.connect()
        multiplexer.onNetworkAvailable()
        runCurrent()
        assertEquals(1, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun secondOidcRejectionAfterForcedRefreshIsTerminal(): Unit = runTest {
        val socket = FakeSocket()
        val refreshCount = AtomicInteger(0)
        val multiplexer = ProductionRealtimeMultiplexer.forTest(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher { refreshCount.incrementAndGet() },
        )
        val endpoint = multiplexer.register(UUID.randomUUID())
        val failed = async { endpoint.state.first { it is RealtimeState.AuthenticationFailed } }

        endpoint.connect()
        socket.emitState(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()
        assertEquals(2, socket.connectCount)

        socket.emitState(RealtimeState.AuthenticationRejected(403, "still rejected"))
        runCurrent()
        assertTrue(failed.await() is RealtimeState.AuthenticationFailed)
        assertEquals(1, refreshCount.get())
        assertEquals(2, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun terminalAuthenticationMultiplexerCanBeRetiredAndReplacementConnects(): Unit = runTest {
        val failedSocket = FakeSocket()
        val failedMultiplexer = ProductionRealtimeMultiplexer.forTest(
            socket = failedSocket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                throw IllegalStateException("refresh rejected")
            },
        )
        val failedEndpoint = failedMultiplexer.register(UUID.randomUUID())
        val authenticationFailed = async {
            failedEndpoint.state.first { it is RealtimeState.AuthenticationFailed }
        }
        failedEndpoint.connect()
        failedSocket.emitState(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()
        assertTrue(authenticationFailed.await() is RealtimeState.AuthenticationFailed)
        failedMultiplexer.close()

        val replacementSocket = FakeSocket()
        val replacement = ProductionRealtimeMultiplexer.forTest(replacementSocket, backgroundScope)
        val replacementEndpoint = replacement.register(UUID.randomUUID())
        replacementEndpoint.connect()

        assertEquals(1, failedSocket.closeCount)
        assertEquals(1, replacementSocket.connectCount)
        replacement.close()
    }

    @Test
    fun onePhysicalWebSocketCarriesInterleavedAAndBSessions(): Unit = runBlocking {
        val androidId = UUID.fromString("10000000-0000-0000-0000-000000000000")
        val pcId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val identity = DeviceIdentity.generate()
        val server = MockWebServer()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val serverWebSocket = AtomicReference<WebSocket>()
        val serverWebSocketClosed = CountDownLatch(1)
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: okhttp3.Response) {
                        serverWebSocket.set(webSocket)
                    }

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        listOf(
                            incomingEnvelope(sessionA, pcId, androidId, 1),
                            incomingEnvelope(sessionB, pcId, androidId, 11),
                            incomingEnvelope(sessionA, pcId, androidId, 2),
                            incomingEnvelope(sessionB, pcId, androidId, 12),
                        ).forEach { envelope ->
                            check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
                        }
                    }

                    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                        serverWebSocketClosed.countDown()
                    }
                },
            ),
        )
        server.start()
        val multiplexer = ProductionRealtimeMultiplexer(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "token" },
            deviceId = androidId,
            identity = identity,
            parentScope = scope,
            allowCleartextForLocalTest = true,
        )
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)
        try {
            val receivedA = async {
                endpointA.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
            }
            val receivedB = async {
                endpointB.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
            }
            endpointA.connect()
            endpointB.connect()

            assertEquals(listOf(1L, 2L), withTimeout(5_000) { receivedA.await() })
            assertEquals(listOf(11L, 12L), withTimeout(5_000) { receivedB.await() })
            assertEquals(1, server.requestCount)
        } finally {
            if (serverWebSocket.getAndSet(null)?.close(1000, "test complete") == true) {
                serverWebSocketClosed.await(2, TimeUnit.SECONDS)
            }
            multiplexer.close()
            scope.cancel()
            identity.destroy()
            server.close()
        }
    }

    @Test
    fun oneDeviceSocketRoutesInterleavedSessionsWithoutCrossDelivery(): Unit = runTest {
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)

        val receivedA = async { endpointA.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList() }
        val receivedB = async { endpointB.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList() }

        endpointA.connect()
        endpointB.connect()
        assertEquals(1, socket.connectCount)

        socket.deliver(socket.received(sessionA, 1))
        socket.deliver(socket.received(sessionB, 11))
        socket.deliver(socket.received(sessionA, 2))
        socket.deliver(socket.received(sessionB, 12))
        runCurrent()

        assertEquals(listOf(1L, 2L), receivedA.await())
        assertEquals(listOf(11L, 12L), receivedB.await())
        assertEquals(4, socket.releasedReservations.get())

        endpointA.close()
        assertEquals(0, socket.closeCount)
        socket.deliver(socket.received(sessionA, 3))
        runCurrent()
        assertEquals(5, socket.releasedReservations.get())
        assertEquals(1, socket.disconnectCount)

        endpointB.close()
        assertEquals(0, socket.closeCount)
        multiplexer.close()
        assertEquals(1, socket.closeCount)
    }

    @Test
    fun endpointOverflowClosesSocketAndRejectsRemainderOfGeneration(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)

        endpoint.connect()
        repeat(ProductionRealtimeClient.MAX_BUFFERED_EVENTS + 2) { index ->
            socket.deliver(socket.received(sessionId, index.toLong() + 1, generation = 1))
        }
        runCurrent()

        assertEquals(1, socket.disconnectCount)
        assertEquals(2, socket.releasedReservations.get())
        assertEquals(1L, endpoint.diagnostics().routeDecisions.overflow)
        assertEquals(1L, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        val admitted = async {
            endpoint.receivedEnvelopes
                .take(ProductionRealtimeClient.MAX_BUFFERED_EVENTS)
                .map { it.use { item -> item.envelope.seq } }
                .toList()
        }
        runCurrent()
        assertEquals(
            (1L..ProductionRealtimeClient.MAX_BUFFERED_EVENTS.toLong()).toList(),
            admitted.await(),
        )

        val nextGeneration = async {
            endpoint.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 129, generation = 2))
        runCurrent()
        assertEquals(listOf(129L), nextGeneration.await())
        assertEquals(ProductionRealtimeClient.MAX_BUFFERED_EVENTS + 3, socket.releasedReservations.get())

        multiplexer.close()
    }

    @Test
    fun routeDecisionsExposeNoTargetClosedAndRejectedGenerationWithoutSessionIdentifiers(): Unit = runTest {
        assertEquals(
            ProductionRealtimeRouteDecision.NO_ENDPOINT,
            classifyProductionRealtimeRoute(endpointPresent = false),
        )
        assertEquals(
            ProductionRealtimeRouteDecision.ENDPOINT_CLOSED,
            classifyProductionRealtimeRoute(endpointPresent = true, endpointClosed = true),
        )
        assertEquals(
            ProductionRealtimeRouteDecision.REJECTED_GENERATION,
            classifyProductionRealtimeRoute(endpointPresent = true, generationRejected = true),
        )
        assertEquals(
            ProductionRealtimeRouteDecision.OVERFLOW,
            classifyProductionRealtimeRoute(endpointPresent = true, overflow = true),
        )

        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)

        socket.deliver(socket.received(sessionId, 306, generation = 1))
        runCurrent()
        val endpoint = multiplexer.register(sessionId)
        socket.deliver(socket.received(sessionId, 307, generation = 1))
        runCurrent()

        val admitted = async {
            endpoint.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 2))
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()
        assertEquals(listOf(306L, 307L), admitted.await())
        assertEquals(1L, endpoint.diagnostics().routeDecisions.noEndpoint)
        assertEquals(1L, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(1, socket.disconnectCount)
        assertEquals(4, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closedEndpointFenceSurvivesReplacementIncarnationUntilNewSocketGeneration(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)
        oldEndpoint.close()

        socket.deliver(socket.received(sessionId, 306, generation = 1))
        runCurrent()
        val replacement = multiplexer.register(sessionId)
        socket.deliver(socket.received(sessionId, 307, generation = 1))
        runCurrent()

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 2))
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(1L, replacement.diagnostics().routeDecisions.endpointClosed)
        assertEquals(1L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(1, socket.disconnectCount)
        assertEquals(4, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closingBufferedEndpointFencesGenerationBeforeReplacementBecomesVisible(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)

        socket.deliver(socket.received(sessionId, 306, generation = 2))
        runCurrent()
        assertEquals(0, socket.releasedReservations.get())

        // The queued 306 is cancelled. Its generation must be fenced before
        // register() can expose the replacement endpoint.
        oldEndpoint.close()
        assertEquals(1, socket.releasedReservations.get())
        assertEquals(1, socket.disconnectCount)

        val replacement = multiplexer.register(sessionId)
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 3))
        socket.deliver(socket.received(sessionId, 307, generation = 3))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(1L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(4, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closingEndpointFencesFrameAlreadyCollectedButNotYetReleased(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)
        val collected = CompletableDeferred<ProductionReceivedEnvelope>()
        val allowRelease = CompletableDeferred<Unit>()
        val collector = backgroundScope.launch {
            oldEndpoint.receivedEnvelopes.take(1).collect { received ->
                collected.complete(received)
                allowRelease.await()
                received.close()
            }
        }

        socket.deliver(socket.received(sessionId, 306, generation = 2))
        val inFlight = withTimeout(2_000) { collected.await() }
        assertEquals(306L, inFlight.envelope.seq)
        assertEquals(0, socket.releasedReservations.get())

        // The channel is empty, but the frame remains outstanding until the
        // SessionClient finally block calls close(). Closing the endpoint must
        // therefore still fence generation 2 and disconnect the socket.
        oldEndpoint.close()
        assertEquals(1, socket.disconnectCount)
        assertEquals(0, socket.releasedReservations.get())

        val replacement = multiplexer.register(sessionId)
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()
        allowRelease.complete(Unit)
        collector.join()

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 3))
        socket.deliver(socket.received(sessionId, 307, generation = 3))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(1L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(4, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun highestObservedGenerationIsSharedAcrossEndpointsAndIncarnations(): Unit = runTest {
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val oldEndpointB = multiplexer.register(sessionB)
        oldEndpointB.close()
        val replacementB = multiplexer.register(sessionB)

        val receivedA = async {
            endpointA.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionA, 306, generation = 2))
        runCurrent()
        assertEquals(listOf(306L), receivedA.await())

        // A frame from an older socket generation cannot regain admission by
        // targeting a different endpoint or a freshly registered incarnation.
        socket.deliver(socket.received(sessionB, 305, generation = 1))
        runCurrent()
        val receivedB = async {
            replacementB.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionB, 306, generation = 2))
        runCurrent()

        assertEquals(listOf(306L), receivedB.await())
        assertEquals(1L, replacementB.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(3, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closingEndpointOrOwnerReleasesEveryBufferedReservation(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
        val endpointB = multiplexer.register(UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

        repeat(3) { index ->
            socket.deliver(
                socket.received(
                    UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    index.toLong() + 1,
                    generation = 1,
                ),
            )
        }
        repeat(4) { index ->
            socket.deliver(
                socket.received(
                    UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    index.toLong() + 11,
                    generation = 1,
                ),
            )
        }
        runCurrent()
        assertEquals(0, socket.releasedReservations.get())

        endpointA.close()
        assertEquals(3, socket.releasedReservations.get())
        endpointB.diagnostics()
        multiplexer.close()
        assertEquals(7, socket.releasedReservations.get())
    }

    @Test
    fun networkAvailableForcesOneReconnectFromEveryActiveSocketPhase(): Unit = runTest {
        val phases = listOf(
            RealtimeState.Connecting,
            RealtimeState.Connected,
            RealtimeState.Failed("offline"),
        )

        phases.forEachIndexed { index, phase ->
            val socket = FakeSocket()
            val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
            val endpoint = multiplexer.register(UUID(index.toLong() + 1, index.toLong() + 11))

            endpoint.connect()
            socket.emitState(phase)
            runCurrent()

            multiplexer.onNetworkAvailable()
            runCurrent()

            assertEquals("phase=$phase", 1, socket.disconnectCount)
            assertEquals("phase=$phase", 2, socket.connectCount)
            multiplexer.close()
        }
    }

    @Test
    fun networkAvailableWithMultipleEndpointsRebuildsOnlyOnePhysicalSocket(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
        val endpointB = multiplexer.register(UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

        endpointA.connect()
        endpointB.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        assertEquals(1, socket.connectCount)

        multiplexer.onNetworkAvailable()
        endpointA.connect()
        endpointB.connect()
        runCurrent()

        assertEquals(1, socket.disconnectCount)
        assertEquals(2, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun networkAvailableAfterCloseDoesNotRevivePhysicalSocket(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.fromString("cccccccc-cccc-cccc-cccc-cccccccccccc"))

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        multiplexer.close()

        multiplexer.onNetworkAvailable()
        runCurrent()

        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.closeCount)
    }

    @Test
    fun productionSessionSendResizeCannotEmitAResizeRequest(): Unit = runTest {
        val androidId = UUID.fromString("10000000-0000-0000-0000-000000000000")
        val pcId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val sessionId = UUID.fromString("30000000-0000-0000-0000-000000000000")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val client = ProductionRealtimeSessionClient(
            engine = NoOpEngine(),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 1,
                pcExchangePublicKey = pcIdentity.exchange.publicKey,
            ),
            identity = androidIdentity,
            parentScope = backgroundScope,
            transport = multiplexer.register(sessionId),
            acknowledgementObserver = { generation, sequence -> DurableAckCommit(sequence, generation) },
            acknowledgementResetObserver = { generation -> DurableAckCommit(0, generation + 1) },
        )

        client.sendResize(TerminalSize(40, 12))

        assertEquals(emptyList<Envelope>(), socket.sentEnvelopes)
        client.close()
        multiplexer.close()
        androidIdentity.destroy()
        pcIdentity.destroy()
    }

    private fun FakeSocket.deliver(received: ProductionReceivedEnvelope) {
        check(incoming.trySend(received).isSuccess)
    }

    private fun FakeSocket.received(
        sessionId: UUID,
        sequence: Long,
        generation: Int = 1,
    ): ProductionReceivedEnvelope =
        ProductionReceivedEnvelope(
            envelope = Envelope.newBuilder()
                .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
                .setSeq(sequence)
                .build(),
            wireBytes = 64,
            generation = generation,
            releaseReservation = { releasedReservations.incrementAndGet() },
        )

    private fun incomingEnvelope(
        sessionId: UUID,
        senderId: UUID,
        recipientId: UUID,
        sequence: Long,
    ): Envelope = Envelope.newBuilder()
        .setProtocolVersion(1)
        .setMessageType(MessageType.OUTPUT_CHUNK)
        .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
        .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(senderId)))
        .setRecipientDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(recipientId)))
        .setDirection(Direction.PC_TO_ANDROID)
        .setSeq(sequence)
        .setKeyId(ByteString.copyFromUtf8("epoch-00000001"))
        .setNonce(ByteString.copyFrom(ByteArray(12) { sequence.toByte() }))
        .setCiphertext(ByteString.copyFrom(ByteArray(16) { 1 }))
        .build()

    private class FakeSocket : ProductionRealtimeSocket {
        val incoming = Channel<ProductionReceivedEnvelope>(Channel.UNLIMITED)
        val releasedReservations = AtomicInteger(0)
        private val mutableState = MutableSharedFlow<RealtimeState>(extraBufferCapacity = 8)
        private val mutablePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        var connectCount = 0
        var disconnectCount = 0
        var closeCount = 0
        val sentEnvelopes = CopyOnWriteArrayList<Envelope>()

        override val receivedEnvelopes = incoming.receiveAsFlow()
        override val receivePressure: StateFlow<ProductionReceivePressure> = mutablePressure
        override val state: SharedFlow<RealtimeState> = mutableState

        override fun connect() {
            connectCount++
            mutableState.tryEmit(RealtimeState.Connecting)
        }

        override fun send(envelope: Envelope): Boolean {
            sentEnvelopes += envelope
            return true
        }

        override fun disconnect(reason: String) {
            disconnectCount++
            mutableState.tryEmit(RealtimeState.Failed(reason))
        }

        override fun close() {
            closeCount++
            incoming.close()
        }

        fun emitState(state: RealtimeState) {
            check(mutableState.tryEmit(state))
        }
    }

    private class NoOpEngine : TerminalEngine {
        override val inputEvents = emptyFlow<ByteArray>()
        override val sizeEvents = emptyFlow<TerminalSize>()

        override suspend fun feed(seq: Long, bytes: ByteBuffer) = Unit
        override fun resize(columns: Int, rows: Int) = Unit
        override fun applyTheme(theme: TerminalThemeV1) = Unit
        override fun setReadOnly(readOnly: Boolean) = Unit
        override fun clear() = Unit
        override fun dispose() = Unit
    }
}

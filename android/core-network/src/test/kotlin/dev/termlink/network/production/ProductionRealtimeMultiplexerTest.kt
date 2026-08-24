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
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.async
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
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionRealtimeMultiplexerTest {
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

        endpointB.close()
        assertEquals(0, socket.closeCount)
        multiplexer.close()
        assertEquals(1, socket.closeCount)
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

    private fun FakeSocket.received(sessionId: UUID, sequence: Long): ProductionReceivedEnvelope =
        ProductionReceivedEnvelope(
            envelope = Envelope.newBuilder()
                .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
                .setSeq(sequence)
                .build(),
            wireBytes = 64,
            generation = 1,
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

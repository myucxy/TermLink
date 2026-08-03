package dev.termlink.network.testrelay

import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

class Phase1TestSessionClientTest {
    private lateinit var server: MockWebServer
    private lateinit var clientScope: CoroutineScope

    @Before
    fun setUp() {
        server = MockWebServer()
        clientScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }

    @After
    fun tearDown() {
        clientScope.cancel()
        server.close()
    }

    @Test
    fun codecRoundTripsBinaryPayloadAndRejectsLanReplayMagic() {
        val expected = Phase1TestFrame(
            type = Phase1FrameType.OUTPUT,
            sequence = 42,
            acknowledgement = 17,
            payload = byteArrayOf(0, 1, 2, -1),
        )
        val actual = Phase1TestFrameCodec.decode(Phase1TestFrameCodec.encode(expected))

        assertEquals(expected.type, actual.type)
        assertEquals(expected.sequence, actual.sequence)
        assertEquals(expected.acknowledgement, actual.acknowledgement)
        assertArrayEquals(expected.payload, actual.payload)

        val wrongMagic = Phase1TestFrameCodec.encode(expected).also { it[0] = 1 }
        assertFailsWith<IllegalArgumentException> { Phase1TestFrameCodec.decode(wrongMagic) }
    }

    @Test
    fun feedsThenAcknowledgesAndWaitsForInputAccepted() = runBlocking {
        val eventLog = CopyOnWriteArrayList<String>()
        val receivedInputs = CopyOnWriteArrayList<ByteArray>()
        val serverListener = object : ClosingWebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                val frame = Phase1TestFrameCodec.decode(bytes.toByteArray())
                when (frame.type) {
                    Phase1FrameType.RESUME -> {
                        eventLog += "resume:${frame.acknowledgement}"
                        send(webSocket, control(Phase1FrameType.WELCOME, "smoke-powershell-v1".encodeToByteArray()))
                        send(webSocket, lifecycle(1, Phase1Lifecycle.RUNNING))
                        send(webSocket, event(Phase1FrameType.RESIZE, 2, uint16Pair(100, 30)))
                        send(webSocket, event(Phase1FrameType.OUTPUT, 3, "phase1-output".encodeToByteArray()))
                    }
                    Phase1FrameType.CUMULATIVE_ACK -> eventLog += "ack:${frame.acknowledgement}"
                    Phase1FrameType.LEASE_REQUEST -> {
                        val leaseId = "lease-test"
                        val payload = ByteBuffer.allocate(8 + leaseId.length)
                            .order(ByteOrder.BIG_ENDIAN)
                            .putLong(System.currentTimeMillis() + 30_000)
                            .put(leaseId.encodeToByteArray())
                            .array()
                        send(webSocket, control(Phase1FrameType.LEASE_GRANTED, payload))
                    }
                    Phase1FrameType.INPUT -> {
                        receivedInputs += frame.payload.copyOfRange(16, frame.payload.size)
                        send(
                            webSocket,
                            control(Phase1FrameType.INPUT_ACCEPTED, frame.payload.copyOfRange(0, 16)),
                        )
                    }
                    Phase1FrameType.LEASE_RELEASE -> send(
                        webSocket,
                        control(Phase1FrameType.LEASE_REVOKED, "released".encodeToByteArray()),
                    )
                }
            }
        }
        server.enqueue(MockResponse().withWebSocketUpgrade(serverListener))
        server.start()

        val engine = RecordingEngine { sequence ->
            delay(20)
            eventLog += "feed:$sequence"
        }
        val client = newClient(engine)
        client.start()

        val readOnly = withTimeout(5_000) {
            client.sessionState.first {
                it is RemoteSessionState.ReadOnly && it.lastAckedSequence == 3L
            }
        }
        assertIs<RemoteSessionState.ReadOnly>(readOnly)
        withTimeout(5_000) {
            while (eventLog.indexOf("ack:3") < 0) delay(1)
        }
        assertTrue(eventLog.indexOf("feed:3") < eventLog.indexOf("ack:3"))
        assertEquals(listOf(TerminalSize(100, 30)), engine.resizes)

        client.requestControl()
        withTimeout(5_000) { client.sessionState.first { it is RemoteSessionState.Controlling } }
        engine.emitInput("whoami\r".encodeToByteArray())
        val accepted = withTimeout(5_000) { client.acceptedInputs.first { it.count == 1 } }
        assertTrue(accepted.lastInputId != null)
        assertArrayEquals("whoami\r".encodeToByteArray(), receivedInputs.single())

        client.releaseControl()
        withTimeout(5_000) { client.sessionState.first { it is RemoteSessionState.ReadOnly } }
        client.close()
    }

    @Test
    fun reconnectSendsHighestAppliedSequenceAndContinuesWithoutDuplicateFeed() = runBlocking {
        val resumes = CopyOnWriteArrayList<Long>()
        val firstListener = object : ClosingWebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                val frame = Phase1TestFrameCodec.decode(bytes.toByteArray())
                when (frame.type) {
                    Phase1FrameType.RESUME -> {
                        resumes += frame.acknowledgement
                        send(webSocket, control(Phase1FrameType.WELCOME, "smoke-powershell-v1".encodeToByteArray()))
                        send(webSocket, lifecycle(1, Phase1Lifecycle.RUNNING))
                        send(webSocket, event(Phase1FrameType.OUTPUT, 2, byteArrayOf(2)))
                    }
                    Phase1FrameType.CUMULATIVE_ACK -> if (frame.acknowledgement == 2L) {
                        webSocket.close(1012, "test disconnect")
                    }
                }
            }
        }
        val secondListener = object : ClosingWebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                val frame = Phase1TestFrameCodec.decode(bytes.toByteArray())
                if (frame.type == Phase1FrameType.RESUME) {
                    resumes += frame.acknowledgement
                    send(webSocket, control(Phase1FrameType.WELCOME, "smoke-powershell-v1".encodeToByteArray()))
                    send(webSocket, event(Phase1FrameType.OUTPUT, 3, byteArrayOf(3)))
                }
            }
        }
        server.enqueue(MockResponse().withWebSocketUpgrade(firstListener))
        server.enqueue(MockResponse().withWebSocketUpgrade(secondListener))
        server.start()

        val engine = RecordingEngine()
        val client = newClient(engine, reconnectDelays = listOf(0))
        client.start()
        withTimeout(5_000) {
            client.sessionState.first { it.lastAckedSequence == 3L }
        }

        assertEquals(listOf(0L, 2L), resumes)
        assertEquals(listOf(2L, 3L), engine.feeds.map { it.first })
        client.close()
    }

    @Test
    fun explicitGapKeepsLastAckAndBindsHistoryGapState() = runBlocking {
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : ClosingWebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                        val frame = Phase1TestFrameCodec.decode(bytes.toByteArray())
                        if (frame.type == Phase1FrameType.RESUME) {
                            send(webSocket, control(Phase1FrameType.WELCOME, "smoke-powershell-v1".encodeToByteArray()))
                            send(webSocket, lifecycle(1, Phase1Lifecycle.RUNNING))
                            send(webSocket, event(Phase1FrameType.RESIZE, 2, uint16Pair(80, 25)))
                            send(webSocket, event(Phase1FrameType.OUTPUT, 3, byteArrayOf(3)))
                            send(webSocket, event(Phase1FrameType.OUTPUT, 4, byteArrayOf(4)))
                            val gap = ByteBuffer.allocate(16)
                                .order(ByteOrder.BIG_ENDIAN)
                                .putLong(5)
                                .putLong(6)
                                .array()
                            send(webSocket, event(Phase1FrameType.GAP, 7, gap))
                        }
                    }
                },
            ),
        )
        server.start()
        val engine = RecordingEngine()
        val client = newClient(engine)
        client.start()

        val gap = withTimeout(5_000) {
            client.sessionState.first { it is RemoteSessionState.HistoryGap }
        }
        assertEquals(4L, gap.lastAckedSequence)
        assertEquals(5L, assertIs<RemoteSessionState.HistoryGap>(gap).expectedSequence)
        assertTrue(engine.readOnlyState)
        client.close()
    }

    @Test
    fun validatesRootUrlAndDoesNotReuseLanReplayPath() {
        val config = Phase1TestSessionConfig(
            rootAddress = "ws://192.0.2.10:8080/",
            token = "test-token",
            scenario = Phase1TestScenario.HISTORY_GAP,
        )
        assertEquals(
            "ws://192.0.2.10:8080/v1/test/session/smoke-powershell-v1?scenario=history-gap",
            config.webSocketUrl(),
        )
        assertFailsWith<IllegalArgumentException> {
            config.copy(rootAddress = "ws://192.0.2.10:8080/v1/test/replay").webSocketUrl()
        }
    }

    private fun newClient(
        engine: TerminalEngine,
        reconnectDelays: List<Long> = listOf(10),
    ): Phase1TestSessionClient = Phase1TestSessionClient(
        engine = engine,
        config = Phase1TestSessionConfig(
            rootAddress = server.url("/").toString().replaceFirst("http", "ws"),
            token = "phase1-test-token",
            scenario = Phase1TestScenario.STEADY,
        ),
        parentScope = clientScope,
        httpClient = OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).build(),
        reconnectDelaysMillis = reconnectDelays,
    )

    private class RecordingEngine(
        private val onFeed: suspend (Long) -> Unit = {},
    ) : TerminalEngine {
        private val mutableInputs = MutableSharedFlow<ByteArray>(extraBufferCapacity = 8)
        private val mutableSizes = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 8)
        override val inputEvents: Flow<ByteArray> = mutableInputs
        override val sizeEvents: Flow<TerminalSize> = mutableSizes
        val feeds = CopyOnWriteArrayList<Pair<Long, ByteArray>>()
        val resizes = CopyOnWriteArrayList<TerminalSize>()
        @Volatile var readOnlyState = true

        suspend fun emitInput(bytes: ByteArray) {
            mutableInputs.emit(bytes)
        }

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            val copy = ByteArray(bytes.remaining()).also(bytes::get)
            onFeed(seq)
            feeds += seq to copy
        }

        override fun resize(columns: Int, rows: Int) {
            resizes += TerminalSize(columns, rows)
        }

        override fun applyTheme(theme: TerminalThemeV1) = Unit

        override fun setReadOnly(readOnly: Boolean) {
            readOnlyState = readOnly
        }

        override fun clear() {
            feeds.clear()
        }

        override fun dispose() = Unit
    }

    private open class ClosingWebSocketListener : WebSocketListener() {
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }
    }

    private companion object {
        fun send(webSocket: WebSocket, frame: Phase1TestFrame) {
            check(webSocket.send(ByteString.of(*Phase1TestFrameCodec.encode(frame))))
        }

        fun control(type: Int, payload: ByteArray = byteArrayOf()) = Phase1TestFrame(
            type = type,
            payload = payload,
        )

        fun event(type: Int, sequence: Long, payload: ByteArray) = Phase1TestFrame(
            type = type,
            sequence = sequence,
            payload = payload,
        )

        fun lifecycle(sequence: Long, state: Int): Phase1TestFrame = event(
            Phase1FrameType.LIFECYCLE,
            sequence,
            byteArrayOf(state.toByte(), 0, 0, 0, 0),
        )
    }
}

package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString as OkioByteString
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import kotlin.test.assertIs

class ProductionRealtimeSessionClientTest {
    private lateinit var server: MockWebServer
    private lateinit var scope: CoroutineScope

    @Before
    fun setUp() {
        server = MockWebServer()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }

    @After
    fun tearDown() {
        scope.cancel()
        server.close()
    }

    @Test
    fun authenticatesHelloDecryptsOutputAndResumesAcrossHistoricalInputResult(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val pcNoncePrefix = byteArrayOf(1, 2, 3, 4)
        val resumes = CopyOnWriteArrayList<Long>()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val listeners = arrayOfNulls<WebSocketListener>(2)

        fun listener(index: Int) = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                check(text.contains("\"role\":\"android\""))
                check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
            }

            override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                val envelope = Envelope.parseFrom(bytes.toByteArray())
                val pairwise = SessionCrypto.derivePairwiseKey(
                    pcIdentity.exchange.privateKey,
                    androidIdentity.exchange.publicKey,
                    pcId,
                    androidId,
                    7,
                )
                val key = SessionCrypto.deriveSessionKey(
                    pairwise,
                    sessionId,
                    CryptoDirection.ANDROID_TO_PC,
                    envelope.keyId.toByteArray(),
                )
                val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, key)
                try {
                    if (envelope.messageType == MessageType.SUBSCRIBE) {
                        val subscribe = SubscribePayload.parseFrom(plaintext)
                        resumes += subscribe.resumeFromSeq
                        if (index == 1) {
                            val historicalResult = InputResultPayload.newBuilder()
                                .setInputId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(UUID.randomUUID())))
                                .setAccepted(true)
                                .build()
                                .toByteArray()
                            val resultEnvelope = ProductionRealtimeClient.encryptedEnvelope(
                                sessionId = sessionId,
                                senderId = pcId,
                                messageType = MessageType.INPUT_ACCEPTED,
                                direction = CryptoDirection.PC_TO_ANDROID,
                                sequence = 2,
                                acknowledgement = 0,
                                keyId = keyId,
                                nonce = SessionCrypto.nonce(pcNoncePrefix, 2uL),
                                sessionKey = SessionCrypto.deriveSessionKey(
                                    SessionCrypto.derivePairwiseKey(
                                        pcIdentity.exchange.privateKey,
                                        androidIdentity.exchange.publicKey,
                                        pcId,
                                        androidId,
                                        7,
                                    ),
                                    sessionId,
                                    CryptoDirection.PC_TO_ANDROID,
                                    keyId,
                                ),
                                plaintext = historicalResult,
                                recipientId = androidId,
                                nowMillis = 1_800_000_000_122,
                            )
                            check(webSocket.send(OkioByteString.of(*resultEnvelope.toByteArray())))
                        }
                        val output = OutputChunkPayload.newBuilder()
                            .setOutput(ByteString.copyFrom(if (index == 0) byteArrayOf(7) else byteArrayOf(8)))
                            .build()
                            .toByteArray()
                        val outputEnvelope = ProductionRealtimeClient.encryptedEnvelope(
                            sessionId = sessionId,
                            senderId = pcId,
                            messageType = MessageType.OUTPUT_CHUNK,
                            direction = CryptoDirection.PC_TO_ANDROID,
                            sequence = if (index == 0) 1 else 3,
                            acknowledgement = 0,
                            keyId = keyId,
                            nonce = SessionCrypto.nonce(pcNoncePrefix, (if (index == 0) 1 else 3).toULong()),
                            sessionKey = SessionCrypto.deriveSessionKey(
                                SessionCrypto.derivePairwiseKey(
                                    pcIdentity.exchange.privateKey,
                                    androidIdentity.exchange.publicKey,
                                    pcId,
                                    androidId,
                                    7,
                                ),
                                sessionId,
                                CryptoDirection.PC_TO_ANDROID,
                                keyId,
                            ),
                            plaintext = output,
                            recipientId = androidId,
                            nowMillis = 1_800_000_000_123,
                        )
                        check(webSocket.send(OkioByteString.of(*outputEnvelope.toByteArray())))
                        if (index == 0) webSocket.close(1012, "test reconnect")
                    } else if (envelope.messageType == MessageType.OUTPUT_ACK) {
                        // The ACK is authenticated by the transport and proves
                        // that a decrypted output was applied before reconnect.
                        assertTrue(envelope.ack >= 1L)
                    }
                } finally {
                    plaintext.fill(0)
                    key.fill(0)
                    pairwise.fill(0)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }
        }.also { listeners[index] = it }

        server.enqueue(MockResponse().withWebSocketUpgrade(listener(0)))
        server.enqueue(MockResponse().withWebSocketUpgrade(listener(1)))
        server.start()

        val engine = RecordingEngine(outputs)
        val client = ProductionRealtimeSessionClient(
            engine = engine,
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
                noncePrefix = byteArrayOf(9, 8, 7, 6),
            ),
            identity = androidIdentity,
            parentScope = scope,
            reconnectDelaysMillis = listOf(0),
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(10_000) { client.sessionState.first { it.lastAckedSequence == 3L } }
            assertEquals(listOf(0L, 1L), resumes)
            assertEquals(2, outputs.size)
            assertArrayEquals(byteArrayOf(7), outputs[0])
            assertArrayEquals(byteArrayOf(8), outputs[1])
            assertIs<RemoteSessionState.Live>(client.sessionState.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun periodicallyRefreshesSubscribeWithCurrentAckAndRestoresOutputRoute(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val pcNoncePrefix = byteArrayOf(1, 2, 3, 4)
        val resumes = CopyOnWriteArrayList<Long>()
        val outputs = CopyOnWriteArrayList<ByteArray>()

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                        val envelope = Envelope.parseFrom(bytes.toByteArray())
                        if (envelope.messageType != MessageType.SUBSCRIBE) return
                        val pairwise = SessionCrypto.derivePairwiseKey(
                            pcIdentity.exchange.privateKey,
                            androidIdentity.exchange.publicKey,
                            pcId,
                            androidId,
                            7,
                        )
                        val key = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, key)
                        try {
                            val resume = SubscribePayload.parseFrom(plaintext).resumeFromSeq
                            resumes += resume
                            if (resumes.size <= 2) {
                                val sequence = resumes.size.toLong()
                                val output = OutputChunkPayload.newBuilder()
                                    .setOutput(ByteString.copyFrom(byteArrayOf((6 + sequence).toByte())))
                                    .build()
                                    .toByteArray()
                                val outputKey = SessionCrypto.deriveSessionKey(
                                    pairwise,
                                    sessionId,
                                    CryptoDirection.PC_TO_ANDROID,
                                    keyId,
                                )
                                try {
                                    val outputEnvelope = ProductionRealtimeClient.encryptedEnvelope(
                                        sessionId = sessionId,
                                        senderId = pcId,
                                        messageType = MessageType.OUTPUT_CHUNK,
                                        direction = CryptoDirection.PC_TO_ANDROID,
                                        sequence = sequence,
                                        acknowledgement = 0,
                                        keyId = keyId,
                                        nonce = SessionCrypto.nonce(pcNoncePrefix, sequence.toULong()),
                                        sessionKey = outputKey,
                                        plaintext = output,
                                        recipientId = androidId,
                                        nowMillis = 1_800_000_000_123 + sequence,
                                    )
                                    check(webSocket.send(OkioByteString.of(*outputEnvelope.toByteArray())))
                                } finally {
                                    output.fill(0)
                                    outputKey.fill(0)
                                }
                            }
                        } finally {
                            plaintext.fill(0)
                            key.fill(0)
                            pairwise.fill(0)
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()

        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(outputs),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
                noncePrefix = byteArrayOf(9, 8, 7, 6),
            ),
            identity = androidIdentity,
            parentScope = scope,
            subscriberRefreshIntervalMillis = 25,
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 2L } }
            assertEquals(listOf(0L, 1L), resumes.take(2))
            assertEquals(2, outputs.size)
            assertArrayEquals(byteArrayOf(7), outputs[0])
            assertArrayEquals(byteArrayOf(8), outputs[1])
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    private class RecordingEngine(private val outputs: MutableList<ByteArray>) : TerminalEngine {
        private val inputs = MutableSharedFlow<ByteArray>(extraBufferCapacity = 8)
        private val sizes = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 8)
        override val inputEvents: Flow<ByteArray> = inputs
        override val sizeEvents: Flow<TerminalSize> = sizes

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            val value = ByteArray(bytes.remaining()).also(bytes::get)
            outputs += value
        }

        override fun resize(columns: Int, rows: Int) = Unit
        override fun applyTheme(theme: TerminalThemeV1) = Unit
        override fun setReadOnly(readOnly: Boolean) = Unit
        override fun clear() = Unit
        override fun dispose() = Unit
    }
}

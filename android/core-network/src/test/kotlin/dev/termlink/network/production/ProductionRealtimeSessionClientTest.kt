package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.LeasePayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputAckPayload
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
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
import kotlin.test.assertFailsWith
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
        val persistedAcknowledgements = CopyOnWriteArrayList<Long>()
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
                        if (index == 0) {
                            webSocket.close(1012, "test reconnect")
                        } else {
                            val ended = SessionUpsertPayload.newBuilder()
                                .setLifecycle("ENDED")
                                .setExitCode(17)
                                .build()
                                .toByteArray()
                            val endedEnvelope = ProductionRealtimeClient.encryptedEnvelope(
                                sessionId = sessionId,
                                senderId = pcId,
                                messageType = MessageType.SESSION_ENDED,
                                direction = CryptoDirection.PC_TO_ANDROID,
                                sequence = 4,
                                acknowledgement = 0,
                                keyId = keyId,
                                nonce = SessionCrypto.nonce(pcNoncePrefix, 4uL),
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
                                plaintext = ended,
                                recipientId = androidId,
                                nowMillis = 1_800_000_000_124,
                            )
                            check(webSocket.send(OkioByteString.of(*endedEnvelope.toByteArray())))
                        }
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
            reconnectDelaysMillis = listOf(25),
            acknowledgementObserver = { sequence ->
                persistedAcknowledgements += sequence
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(10_000) { client.sessionState.first { it.lastAckedSequence == 4L } }
            assertEquals(listOf(0L, 1L), resumes)
            assertCumulativeCursor(persistedAcknowledgements, expectedFinal = 4L)
            assertEquals(2, outputs.size)
            assertArrayEquals(byteArrayOf(7), outputs[0])
            assertArrayEquals(byteArrayOf(8), outputs[1])
            val exited = assertIs<RemoteSessionState.Exited>(client.sessionState.value)
            assertEquals(17, exited.exitCode)
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

    @Test
    fun recoversHistoryGapWithOneCumulativeAckAndFreshSubscribe(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val resumes = CopyOnWriteArrayList<Long>()
        val durableAcks = CopyOnWriteArrayList<Long>()
        val wireAcks = CopyOnWriteArrayList<Long>()
        val outputs = CopyOnWriteArrayList<ByteArray>()

        fun sendPcEnvelope(webSocket: WebSocket, type: MessageType, sequence: Long, payload: ByteArray) {
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                7,
            )
            val sessionKey = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = type,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), sequence.toULong()),
                    sessionKey = sessionKey,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                sessionKey.fill(0)
                pairwise.fill(0)
            }
        }

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
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
                        val sessionKey = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
                        try {
                            when (envelope.messageType) {
                                MessageType.SUBSCRIBE -> {
                                    val resume = SubscribePayload.parseFrom(plaintext).resumeFromSeq
                                    resumes += resume
                                    if (resumes.size == 1) {
                                        val output = OutputChunkPayload.newBuilder()
                                            .setOutput(ByteString.copyFrom(byteArrayOf(1)))
                                            .build()
                                            .toByteArray()
                                        val gap = GapPayload.newBuilder()
                                            .setStart(2)
                                            .setEnd(3)
                                            .setReasonCode("bounded_history")
                                            .build()
                                            .toByteArray()
                                        try {
                                            sendPcEnvelope(webSocket, MessageType.OUTPUT_CHUNK, 1, output)
                                            sendPcEnvelope(webSocket, MessageType.GAP, 4, gap)
                                        } finally {
                                            output.fill(0)
                                            gap.fill(0)
                                        }
                                    }
                                }
                                MessageType.OUTPUT_ACK -> {
                                    val ack = OutputAckPayload.parseFrom(plaintext).highestContiguousSeq
                                    assertEquals(ack, envelope.ack)
                                    wireAcks += ack
                                }
                                else -> Unit
                            }
                        } finally {
                            plaintext.fill(0)
                            sessionKey.fill(0)
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
            ),
            identity = androidIdentity,
            parentScope = scope,
            acknowledgementObserver = { sequence ->
                durableAcks += sequence
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        val replay = listOf(
            RemoteSessionEvent.Output(1, byteArrayOf(1)),
            RemoteSessionEvent.Output(2, byteArrayOf(2)),
            RemoteSessionEvent.Output(3, byteArrayOf(3)),
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.HistoryGap } }
            assertEquals(3L, client.recoverFromHistory(replay, recoveredLocalInputEpoch = 9))
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 3L } }
            withTimeout(2_000) {
                while (resumes.lastOrNull() != 3L || wireAcks.lastOrNull() != 3L) kotlinx.coroutines.yield()
            }
            assertEquals(listOf(0L, 3L), resumes.take(2))
            assertCumulativeCursor(durableAcks, expectedFinal = 3L)
            assertCumulativeCursor(wireAcks, expectedFinal = 3L)
            assertEquals(listOf(1, 2, 3), outputs.map { it.single().toInt() })
        } finally {
            replay.forEach { (it as RemoteSessionEvent.Output).bytes.fill(0) }
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun recoverFromHistoryFeedFailureResetsRendererWithoutAckAndCanRetry(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val resumes = CopyOnWriteArrayList<Long>()
        val durableAcks = CopyOnWriteArrayList<Long>()
        val wireAcks = CopyOnWriteArrayList<Long>()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val engine = RecordingEngine(outputs, failOnFeedSequence = 2L)

        fun sendPcEnvelope(webSocket: WebSocket, type: MessageType, sequence: Long, payload: ByteArray) {
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                7,
            )
            val sessionKey = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = type,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), sequence.toULong()),
                    sessionKey = sessionKey,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                sessionKey.fill(0)
                pairwise.fill(0)
            }
        }

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
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
                        val sessionKey = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
                        try {
                            when (envelope.messageType) {
                                MessageType.SUBSCRIBE -> {
                                    resumes += SubscribePayload.parseFrom(plaintext).resumeFromSeq
                                    if (resumes.size == 1) {
                                        val gap = GapPayload.newBuilder()
                                            .setStart(1)
                                            .setEnd(3)
                                            .setReasonCode("bounded_history")
                                            .build()
                                            .toByteArray()
                                        try {
                                            sendPcEnvelope(webSocket, MessageType.GAP, 4, gap)
                                        } finally {
                                            gap.fill(0)
                                        }
                                    }
                                }
                                MessageType.OUTPUT_ACK -> {
                                    val ack = OutputAckPayload.parseFrom(plaintext).highestContiguousSeq
                                    assertEquals(ack, envelope.ack)
                                    wireAcks += ack
                                }
                                else -> Unit
                            }
                        } finally {
                            plaintext.fill(0)
                            sessionKey.fill(0)
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
            ),
            identity = androidIdentity,
            parentScope = scope,
            acknowledgementObserver = { sequence ->
                durableAcks += sequence
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        val replay = listOf(
            RemoteSessionEvent.Output(1, byteArrayOf(1)),
            RemoteSessionEvent.Output(2, byteArrayOf(2)),
            RemoteSessionEvent.Output(3, byteArrayOf(3)),
        )
        try {
            client.start()
            val initialGap = withTimeout(2_000) {
                client.sessionState.first { it is RemoteSessionState.HistoryGap }
            }
            val expectedGap = assertIs<RemoteSessionState.HistoryGap>(initialGap)
            val durableBeforeFailure = durableAcks.toList()
            val wireBeforeFailure = wireAcks.toList()

            assertFailsWith<IllegalStateException> {
                client.recoverFromHistory(
                    replay,
                    recoveredLocalInputEpoch = 9,
                    expectedGap = expectedGap,
                )
            }

            val failedState = assertIs<RemoteSessionState.HistoryGap>(client.sessionState.value)
            assertEquals(0L, failedState.lastAckedSequence)
            assertEquals(1L, failedState.expectedSequence)
            assertEquals(4L, failedState.receivedSequence)
            assertTrue(engine.isReadOnly)
            assertTrue(outputs.isEmpty())
            assertEquals(durableBeforeFailure, durableAcks.toList())
            assertEquals(wireBeforeFailure, wireAcks.toList())

            assertEquals(
                3L,
                client.recoverFromHistory(
                    replay,
                    recoveredLocalInputEpoch = 9,
                    expectedGap = failedState,
                ),
            )
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 3L } }
            withTimeout(2_000) {
                while (resumes.lastOrNull() != 3L || wireAcks.lastOrNull() != 3L) {
                    kotlinx.coroutines.yield()
                }
            }
            assertEquals(listOf(0L, 3L), resumes.take(2))
            assertCumulativeCursor(durableAcks, expectedFinal = 3L)
            assertCumulativeCursor(wireAcks, expectedFinal = 3L)
            assertEquals(listOf(1, 2, 3), outputs.map { it.single().toInt() })
        } finally {
            replay.forEach { (it as RemoteSessionEvent.Output).bytes.fill(0) }
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun rotatesToAuthoritativeEpochAcceptsInFlightPreviousThenRetiresIt(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val resumes = CopyOnWriteArrayList<Pair<Long, Long>>()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val metadataRefreshes = CopyOnWriteArrayList<Unit>()

        fun sendOutput(webSocket: WebSocket, epoch: Long, sequence: Long, value: Byte) {
            val keyId = "epoch-${epoch.toString().padStart(8, '0')}".encodeToByteArray()
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                epoch,
            )
            val key = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            val payload = OutputChunkPayload.newBuilder()
                .setOutput(ByteString.copyFrom(byteArrayOf(value)))
                .build()
                .toByteArray()
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = MessageType.OUTPUT_CHUNK,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, epoch.toByte()), sequence.toULong()),
                    sessionKey = key,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                payload.fill(0)
                key.fill(0)
                pairwise.fill(0)
                keyId.fill(0)
            }
        }

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                        val envelope = Envelope.parseFrom(bytes.toByteArray())
                        if (envelope.messageType != MessageType.SUBSCRIBE) return
                        val epoch = when (envelope.keyId.toStringUtf8()) {
                            "epoch-00000007" -> 7L
                            "epoch-00000008" -> 8L
                            else -> error("unexpected key epoch")
                        }
                        val pairwise = SessionCrypto.derivePairwiseKey(
                            pcIdentity.exchange.privateKey,
                            androidIdentity.exchange.publicKey,
                            pcId,
                            androidId,
                            epoch,
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
                            resumes += epoch to resume
                            if (epoch == 7L) {
                                sendOutput(webSocket, 7, 1, 1)
                            } else {
                                sendOutput(webSocket, 7, 2, 2)
                                sendOutput(webSocket, 8, 3, 3)
                                sendOutput(webSocket, 7, 4, 4)
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
                keyId = "epoch-00000007".encodeToByteArray(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            authoritativeMetadataRefreshObserver = { metadataRefreshes += Unit },
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 1L } }
            assertTrue(client.rotateKeyMaterial(8, pcIdentity.exchange.publicKey.copyOf()))
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 3L } }
            withTimeout(2_000) {
                client.transportState.first {
                    it is ProductionTransportState.Failed || it is ProductionTransportState.Reconnecting
                }
            }
            withTimeout(2_000) { while (metadataRefreshes.isEmpty()) kotlinx.coroutines.yield() }
            assertEquals(listOf(7L to 0L, 8L to 1L), resumes.take(2))
            assertEquals(listOf(1, 2, 3), outputs.map { it.single().toInt() })
            assertEquals(1, metadataRefreshes.size)
            assertEquals(false, client.rotateKeyMaterial(8, pcIdentity.exchange.publicKey.copyOf()))
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun retiresPreviousEpochAfterGraceWithoutCurrentEpochTraffic(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val resumes = CopyOnWriteArrayList<Pair<Long, Long>>()
        val metadataRefreshes = CopyOnWriteArrayList<Unit>()
        val connectedSocket = CompletableDeferred<WebSocket>()

        fun sendOutput(webSocket: WebSocket, epoch: Long, sequence: Long, value: Byte) {
            val keyId = "epoch-${epoch.toString().padStart(8, '0')}".encodeToByteArray()
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                epoch,
            )
            val key = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            val payload = OutputChunkPayload.newBuilder()
                .setOutput(ByteString.copyFrom(byteArrayOf(value)))
                .build()
                .toByteArray()
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = MessageType.OUTPUT_CHUNK,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(4, 3, 2, epoch.toByte()), sequence.toULong()),
                    sessionKey = key,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                payload.fill(0)
                key.fill(0)
                pairwise.fill(0)
                keyId.fill(0)
            }
        }

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                        val envelope = Envelope.parseFrom(bytes.toByteArray())
                        if (envelope.messageType != MessageType.SUBSCRIBE) return
                        val epoch = when (envelope.keyId.toStringUtf8()) {
                            "epoch-00000007" -> 7L
                            "epoch-00000008" -> 8L
                            else -> error("unexpected key epoch")
                        }
                        val pairwise = SessionCrypto.derivePairwiseKey(
                            pcIdentity.exchange.privateKey,
                            androidIdentity.exchange.publicKey,
                            pcId,
                            androidId,
                            epoch,
                        )
                        val key = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, key)
                        try {
                            resumes += epoch to SubscribePayload.parseFrom(plaintext).resumeFromSeq
                            connectedSocket.complete(webSocket)
                            if (epoch == 7L && outputs.isEmpty()) sendOutput(webSocket, 7, 1, 1)
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
            ),
            identity = androidIdentity,
            parentScope = scope,
            authoritativeMetadataRefreshObserver = { metadataRefreshes += Unit },
            previousEpochGraceMillis = 40,
            reconnectDelaysMillis = listOf(1_000),
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 1L } }
            assertTrue(client.rotateKeyMaterial(8, pcIdentity.exchange.publicKey.copyOf()))
            withTimeout(2_000) { while (resumes.none { it.first == 8L }) kotlinx.coroutines.yield() }
            delay(120)
            sendOutput(connectedSocket.await(), 7, 2, 2)
            withTimeout(2_000) {
                client.transportState.first {
                    it is ProductionTransportState.Failed || it is ProductionTransportState.Reconnecting
                }
            }
            withTimeout(2_000) { while (metadataRefreshes.isEmpty()) kotlinx.coroutines.yield() }
            assertEquals(listOf(1), outputs.map { it.single().toInt() })
            assertEquals(1, metadataRefreshes.size)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun rejectsRegressedEpochAndSameEpochKeySubstitution(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val substitutedPcIdentity = DeviceIdentity.generate()
        server.start()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            allowCleartextForLocalTest = true,
        )
        try {
            assertFailsWith<IllegalStateException> {
                client.validateKeyMaterialUpdate(6, pcIdentity.exchange.publicKey.copyOf())
            }
            assertFailsWith<IllegalArgumentException> {
                client.validateKeyMaterialUpdate(7, substitutedPcIdentity.exchange.publicKey.copyOf())
            }
            assertFailsWith<IllegalArgumentException> {
                client.rotateKeyMaterial(6, pcIdentity.exchange.publicKey.copyOf())
            }
            assertFailsWith<IllegalArgumentException> {
                client.rotateKeyMaterial(7, substitutedPcIdentity.exchange.publicKey.copyOf())
            }
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
            substitutedPcIdentity.destroy()
        }
    }

    @Test
    fun closeDoesNotBlockSingleThreadDispatcherDuringSuspendedAck(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val ackStarted = CompletableDeferred<Unit>()
        val releaseAck = CompletableDeferred<Unit>()
        val disposed = CompletableDeferred<Unit>()
        val executor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "TermLink-close-regression").apply { isDaemon = true }
        }
        val dispatcher = executor.asCoroutineDispatcher()
        val singleScope = CoroutineScope(SupervisorJob() + dispatcher)

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                        val subscribe = Envelope.parseFrom(bytes.toByteArray())
                        if (subscribe.messageType != MessageType.SUBSCRIBE) return
                        val pairwise = SessionCrypto.derivePairwiseKey(
                            pcIdentity.exchange.privateKey,
                            androidIdentity.exchange.publicKey,
                            pcId,
                            androidId,
                            7,
                        )
                        val sessionKey = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.PC_TO_ANDROID,
                            keyId,
                        )
                        val payload = OutputChunkPayload.newBuilder()
                            .setOutput(ByteString.copyFrom(byteArrayOf(1)))
                            .build()
                            .toByteArray()
                        try {
                            val envelope = ProductionRealtimeClient.encryptedEnvelope(
                                sessionId = sessionId,
                                senderId = pcId,
                                messageType = MessageType.OUTPUT_CHUNK,
                                direction = CryptoDirection.PC_TO_ANDROID,
                                sequence = 1,
                                acknowledgement = 0,
                                keyId = keyId,
                                nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), 1uL),
                                sessionKey = sessionKey,
                                plaintext = payload,
                                recipientId = androidId,
                            )
                            check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
                        } finally {
                            payload.fill(0)
                            sessionKey.fill(0)
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
            engine = RecordingEngine(CopyOnWriteArrayList(), onDispose = { disposed.complete(Unit) }),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = singleScope,
            acknowledgementObserver = { sequence ->
                ackStarted.complete(Unit)
                releaseAck.await()
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) { ackStarted.await() }
            withTimeout(2_000) { withContext(dispatcher) { client.close() } }
            withTimeout(2_000) { disposed.await() }
            assertIs<ProductionTransportState.Closed>(client.transportState.value)
        } finally {
            releaseAck.complete(Unit)
            client.close()
            singleScope.cancel()
            dispatcher.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun keepsRetryingWhenOfflineConnectionsFailBackToBack(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()

        server.enqueue(MockResponse().setResponseCode(503).setHeadersDelay(50, TimeUnit.MILLISECONDS))
        server.enqueue(MockResponse().setResponseCode(503).setHeadersDelay(50, TimeUnit.MILLISECONDS))
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()

        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            reconnectDelaysMillis = listOf(0),
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(5_000) {
                client.transportState.first { it is ProductionTransportState.Connected }
            }
            assertEquals(3, server.requestCount)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun lateRestLeaseCannotOverrideARealtimeLocalInputRevoke(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val leaseId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val connectedSocket = CompletableDeferred<WebSocket>()
        // Keep the deliberately failing REST request under a supervisor so the
        // expected stale-lease exception cannot cancel this runBlocking test
        // before we assert the compensating DELETE and read-only state.
        val requestScope = CoroutineScope(SupervisorJob(coroutineContext[kotlinx.coroutines.Job]) + Dispatchers.Default)

        val realtime = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
            }

            override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                val envelope = Envelope.parseFrom(bytes.toByteArray())
                if (envelope.messageType == MessageType.SUBSCRIBE) {
                    connectedSocket.complete(webSocket)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }
        }
        server.enqueue(MockResponse().withWebSocketUpgrade(realtime))
        server.enqueue(
            MockResponse()
                .setBodyDelay(2, TimeUnit.SECONDS)
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """{"lease_id":"$leaseId","expires_at":"2099-01-01T00:00:00Z","version":1}""",
                ),
        )
        server.enqueue(MockResponse().setResponseCode(204))
        server.start()

        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            val webSocket = withTimeout(2_000) { connectedSocket.await() }
            val upgradeRequest = withContext(Dispatchers.IO) {
                requireNotNull(server.takeRequest(2, TimeUnit.SECONDS))
            }
            assertEquals("/v1/realtime", upgradeRequest.path)

            val request = requestScope.async { client.requestControl() }
            val leaseRequest = withContext(Dispatchers.IO) {
                requireNotNull(server.takeRequest(2, TimeUnit.SECONDS))
            }
            assertEquals("POST", leaseRequest.method)
            assertEquals("/v1/sessions/$sessionId/lease", leaseRequest.path)

            val revokePayload = LeasePayload.newBuilder()
                .setLeaseId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(leaseId)))
                .setVersion(1)
                .setReasonCode("LOCAL_INPUT_WON")
                .setLocalInputEpoch(9)
                .build()
                .toByteArray()
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                7,
            )
            val sessionKey = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            try {
                val revoke = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = MessageType.LEASE_REVOKED,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = 1,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(7, 7, 7, 7), 1uL),
                    sessionKey = sessionKey,
                    plaintext = revokePayload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*revoke.toByteArray())))
            } finally {
                revokePayload.fill(0)
                sessionKey.fill(0)
                pairwise.fill(0)
            }

            withTimeout(2_000) {
                client.sessionState.first {
                    it is RemoteSessionState.ReadOnly && it.reason == "LOCAL_INPUT_WON"
                }
            }
            assertFailsWith<IllegalStateException> { request.await() }
            val staleLeaseRelease = withContext(Dispatchers.IO) {
                requireNotNull(server.takeRequest(2, TimeUnit.SECONDS))
            }
            assertEquals("DELETE", staleLeaseRelease.method)
            assertEquals("/v1/sessions/$sessionId/lease/$leaseId", staleLeaseRelease.path)
            assertIs<RemoteSessionState.ReadOnly>(client.sessionState.value)
        } finally {
            requestScope.cancel()
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun doesNotRegrantControlWhenOldLeaseGrantIsReplayedAfterRevoke(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val durableAcks = CopyOnWriteArrayList<Long>()
        val wireAcks = CopyOnWriteArrayList<Long>()
        val leaseId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val leaseExpiresAt = 1_900_000_000_000L

        fun sendLease(
            webSocket: WebSocket,
            sequence: Long,
            type: MessageType,
            payload: ByteArray,
        ) {
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                7,
            )
            val sessionKey = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = type,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), sequence.toULong()),
                    sessionKey = sessionKey,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                sessionKey.fill(0)
                pairwise.fill(0)
            }
        }

        val grant = LeasePayload.newBuilder()
            .setLeaseId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(leaseId)))
            .setVersion(1)
            .setExpiresAtMs(leaseExpiresAt)
            .build()
            .toByteArray()
        val revoke = LeasePayload.newBuilder()
            .setLeaseId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(leaseId)))
            .setVersion(1)
            .setReasonCode("revoked")
            .build()
            .toByteArray()

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
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
                        val sessionKey = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
                        try {
                            when (envelope.messageType) {
                                MessageType.SUBSCRIBE -> {
                                    sendLease(webSocket, 1, MessageType.LEASE_GRANTED, grant)
                                    sendLease(webSocket, 2, MessageType.LEASE_REVOKED, revoke)
                                    // This is an authenticated replay of the old grant, not a new
                                    // event. The client must leave the session read-only.
                                    sendLease(webSocket, 1, MessageType.LEASE_GRANTED, grant)
                                }
                                MessageType.OUTPUT_ACK -> {
                                    val ack = OutputAckPayload.parseFrom(plaintext).highestContiguousSeq
                                    assertEquals(ack, envelope.ack)
                                    wireAcks += ack
                                }
                                else -> Unit
                            }
                        } finally {
                            plaintext.fill(0)
                            sessionKey.fill(0)
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
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = server.url("/").toString(),
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            acknowledgementObserver = { sequence ->
                durableAcks += sequence
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) {
                client.sessionState.first {
                    it is RemoteSessionState.ReadOnly && it.lastAckedSequence == 2L
                }
            }
            val finalState = assertIs<RemoteSessionState.ReadOnly>(client.sessionState.value)
            assertEquals("revoked", finalState.reason)
            withTimeout(2_000) {
                while (durableAcks.lastOrNull() != 2L || wireAcks.lastOrNull() != 2L) {
                    kotlinx.coroutines.yield()
                }
            }
            assertCumulativeCursor(durableAcks, expectedFinal = 2L)
            assertCumulativeCursor(wireAcks, expectedFinal = 2L)
        } finally {
            client.close()
            grant.fill(0)
            revoke.fill(0)
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun recoversOverlappingEnvelopeGapAndAcknowledgesReplayedMarker(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val resumes = CopyOnWriteArrayList<Long>()
        val durableAcks = CopyOnWriteArrayList<Long>()
        val wireAcks = CopyOnWriteArrayList<Long>()
        val outputs = CopyOnWriteArrayList<ByteArray>()

        fun sendPcEnvelope(webSocket: WebSocket, type: MessageType, sequence: Long, payload: ByteArray) {
            val pairwise = SessionCrypto.derivePairwiseKey(
                pcIdentity.exchange.privateKey,
                androidIdentity.exchange.publicKey,
                pcId,
                androidId,
                7,
            )
            val sessionKey = SessionCrypto.deriveSessionKey(
                pairwise,
                sessionId,
                CryptoDirection.PC_TO_ANDROID,
                keyId,
            )
            try {
                val envelope = ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = sessionId,
                    senderId = pcId,
                    messageType = type,
                    direction = CryptoDirection.PC_TO_ANDROID,
                    sequence = sequence,
                    acknowledgement = 0,
                    keyId = keyId,
                    nonce = SessionCrypto.nonce(byteArrayOf(5, 6, 7, 8), sequence.toULong()),
                    sessionKey = sessionKey,
                    plaintext = payload,
                    recipientId = androidId,
                )
                check(webSocket.send(OkioByteString.of(*envelope.toByteArray())))
            } finally {
                sessionKey.fill(0)
                pairwise.fill(0)
            }
        }

        val outputOne = OutputChunkPayload.newBuilder()
            .setOutput(ByteString.copyFrom(byteArrayOf(1)))
            .build()
            .toByteArray()
        val outputTwo = OutputChunkPayload.newBuilder()
            .setOutput(ByteString.copyFrom(byteArrayOf(2)))
            .build()
            .toByteArray()
        val gap = GapPayload.newBuilder()
            // Sequence 2 has already reached the renderer, so start 2 is an
            // overlap. Only suffix sequence 3 is actually missing.
            .setStart(2)
            .setEnd(3)
            .setReasonCode("bounded_history")
            .build()
            .toByteArray()

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
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
                        val sessionKey = SessionCrypto.deriveSessionKey(
                            pairwise,
                            sessionId,
                            CryptoDirection.ANDROID_TO_PC,
                            envelope.keyId.toByteArray(),
                        )
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
                        try {
                            when (envelope.messageType) {
                                MessageType.SUBSCRIBE -> {
                                    resumes += SubscribePayload.parseFrom(plaintext).resumeFromSeq
                                    if (resumes.size == 1) {
                                        sendPcEnvelope(webSocket, MessageType.OUTPUT_CHUNK, 1, outputOne)
                                        sendPcEnvelope(webSocket, MessageType.OUTPUT_CHUNK, 2, outputTwo)
                                        sendPcEnvelope(webSocket, MessageType.GAP, 4, gap)
                                    } else {
                                        // History recovery publishes resume(3); replaying the
                                        // original marker must now be an Applied no-op and ACK 4.
                                        sendPcEnvelope(webSocket, MessageType.GAP, 4, gap)
                                    }
                                }
                                MessageType.OUTPUT_ACK -> {
                                    val ack = OutputAckPayload.parseFrom(plaintext).highestContiguousSeq
                                    assertEquals(ack, envelope.ack)
                                    wireAcks += ack
                                }
                                else -> Unit
                            }
                        } finally {
                            plaintext.fill(0)
                            sessionKey.fill(0)
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
            ),
            identity = androidIdentity,
            parentScope = scope,
            acknowledgementObserver = { sequence ->
                durableAcks += sequence
                sequence
            },
            allowCleartextForLocalTest = true,
        )
        val replay = listOf(
            RemoteSessionEvent.Output(1, byteArrayOf(1)),
            RemoteSessionEvent.Output(2, byteArrayOf(2)),
            RemoteSessionEvent.Output(3, byteArrayOf(3)),
        )
        try {
            client.start()
            val overlap = withTimeout(2_000) {
                client.sessionState.first { it is RemoteSessionState.HistoryGap }
            }
            val gapState = assertIs<RemoteSessionState.HistoryGap>(overlap)
            assertEquals(3L, gapState.expectedSequence)
            assertEquals(4L, gapState.receivedSequence)
            assertEquals(3L, client.recoverFromHistory(replay, recoveredLocalInputEpoch = 9))
            withTimeout(2_000) {
                client.sessionState.first { it.lastAckedSequence == 4L }
            }
            withTimeout(2_000) {
                while (resumes.lastOrNull() != 3L || wireAcks.lastOrNull() != 4L) {
                    kotlinx.coroutines.yield()
                }
            }
            assertEquals(listOf(0L, 3L), resumes.take(2))
            assertEquals(listOf(1, 2, 3), outputs.map { it.single().toInt() })
            assertCumulativeCursor(durableAcks, expectedFinal = 4L)
            assertCumulativeCursor(wireAcks, expectedFinal = 4L)
        } finally {
            replay.forEach { (it as RemoteSessionEvent.Output).bytes.fill(0) }
            outputOne.fill(0)
            outputTwo.fill(0)
            gap.fill(0)
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    private fun assertCumulativeCursor(values: List<Long>, expectedFinal: Long) {
        val snapshot = values.toList()
        assertTrue(snapshot.isNotEmpty())
        assertEquals(expectedFinal, snapshot.last())
        assertTrue(snapshot.zipWithNext().all { (previous, current) -> previous < current })
    }

    private class RecordingEngine(
        private val outputs: MutableList<ByteArray>,
        private val onDispose: () -> Unit = {},
        failOnFeedSequence: Long? = null,
    ) : TerminalEngine {
        private val inputs = MutableSharedFlow<ByteArray>(extraBufferCapacity = 8)
        private val sizes = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 8)
        @Volatile
        private var pendingFeedFailureSequence: Long? = failOnFeedSequence
        @Volatile
        private var readOnly = true
        override val inputEvents: Flow<ByteArray> = inputs
        override val sizeEvents: Flow<TerminalSize> = sizes

        val isReadOnly: Boolean
            get() = readOnly

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            if (pendingFeedFailureSequence == seq) {
                pendingFeedFailureSequence = null
                throw IllegalStateException("deterministic feed failure at sequence $seq")
            }
            val value = ByteArray(bytes.remaining()).also(bytes::get)
            outputs += value
        }

        override fun resize(columns: Int, rows: Int) = Unit
        override fun applyTheme(theme: TerminalThemeV1) = Unit
        override fun setReadOnly(readOnly: Boolean) {
            this.readOnly = readOnly
        }
        override fun clear() = outputs.clear()
        override fun dispose() = onDispose()
    }
}
